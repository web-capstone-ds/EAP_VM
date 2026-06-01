using DsEap.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace DsEap.Mqtt;

// eap-spec §1.2 / §8.2 — 장비별 독립 MQTT 세션 관리
// 각 equipmentId는 자신의 계정(eap_vis_NNN)으로 별도 IMqttClient를 생성해 접속한다.
// ACL: eap_vis_NNN은 ds/DS-VIS-NNN/#만 write 가능 — 계정 분리로 Broker 거부 없음.
public sealed class MqttClientManager : IAsyncDisposable
{
    private readonly EapSettings _settings;
    private readonly ILogger<MqttClientManager> _log;
    private readonly Random _rand = new();

    private sealed record EquipmentSlot(
        IMqttClient Client,
        MqttClientOptions Options,
        SemaphoreSlim Gate);

    private readonly Dictionary<string, EquipmentSlot> _slots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CancellationTokenSource> _loopCtsList = new();
    private readonly List<Task> _reconnectLoops = new();

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceived;
    public event Func<Task>? Connected;

    public MqttClientManager(IOptions<EapSettings> settings, ILogger<MqttClientManager> log)
    {
        _settings = settings.Value;
        _log = log;

        var broker = _settings.Broker;
        var factory = new MqttFactory();

        // PerEquipment가 설정된 경우 장비별 계정, 없으면 기본 Username/Password로 GoldenPath 단일 장비
        var credentials = broker.PerEquipment.Count > 0
            ? broker.PerEquipment
            : new Dictionary<string, EquipmentCredential>(StringComparer.OrdinalIgnoreCase)
              {
                  [_settings.GoldenPath.EquipmentId] = new()
                  {
                      Username = broker.Username,
                      Password = broker.Password,
                  }
              };

        foreach (var (equipmentId, cred) in credentials)
        {
            var client = factory.CreateMqttClient();
            var capturedId = equipmentId;

            client.ApplicationMessageReceivedAsync += async e =>
            {
                if (ApplicationMessageReceived is { } h) await h(e);
            };
            client.ConnectedAsync += async _ =>
            {
                _log.LogInformation("MQTT connected [{Eq}] to {Host}:{Port}",
                    capturedId, broker.Host, broker.Port);
                if (Connected is { } h) await h();
            };
            client.DisconnectedAsync += e =>
            {
                _log.LogWarning("MQTT disconnected [{Eq}]: {Reason}", capturedId, e.Reason);
                return Task.CompletedTask;
            };

            var willPayload = WillPayloadFactory.BuildEapDisconnected(equipmentId);
            var options = new MqttClientOptionsBuilder()
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithTcpServer(broker.Host, broker.Port)
                .WithClientId($"ds-eap-{equipmentId}-{Guid.NewGuid():N}")
                .WithCleanStart(broker.CleanStart)
                .WithSessionExpiryInterval(broker.SessionExpirySeconds)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(broker.KeepAliveSeconds))
                .WithWillTopic(TopicPolicy.Topic(TopicPolicy.Kind.Alarm, equipmentId))
                .WithWillPayload(willPayload)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .WithWillRetain(true)
                .WithWillContentType("application/json")
                .WithCredentials(cred.Username, cred.Password)
                .Build();

            _slots[equipmentId] = new EquipmentSlot(client, options, new SemaphoreSlim(1, 1));
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var (equipmentId, slot) in _slots)
        {
            var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loopCtsList.Add(loopCts);
            _reconnectLoops.Add(Task.Run(
                () => RunReconnectLoopAsync(equipmentId, slot, loopCts.Token),
                CancellationToken.None));
        }
        await Task.CompletedTask;
    }

    private async Task RunReconnectLoopAsync(string equipmentId, EquipmentSlot slot, CancellationToken ct)
    {
        var steps = _settings.Backoff.StepsSeconds;
        var maxIdx = Math.Max(steps.Length - 1, 0);
        var idx = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!slot.Client.IsConnected)
            {
                try
                {
                    _log.LogInformation("MQTT connecting [{Eq}] to {Host}:{Port}...",
                        equipmentId, _settings.Broker.Host, _settings.Broker.Port);
                    await slot.Client.ConnectAsync(slot.Options, ct);
                    idx = 0;
                }
                catch (Exception ex)
                {
                    var baseSec = steps[Math.Min(idx, maxIdx)];
                    var jitter = 1.0 + ((_rand.NextDouble() * 2 - 1) * (_settings.Backoff.JitterPct / 100.0));
                    var delay = TimeSpan.FromSeconds(Math.Max(0.1, baseSec * jitter));
                    _log.LogWarning("MQTT connect failed [{Eq}] ({Msg}). Retry in {Delay:F1}s",
                        equipmentId, ex.Message, delay.TotalSeconds);
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                    if (idx < maxIdx) idx++;
                    continue;
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task PublishAsync(
        TopicPolicy.Kind kind,
        string equipmentId,
        byte[] payload,
        CancellationToken ct)
    {
        // 장비 ID에 맞는 슬롯 선택 — 없으면 첫 번째 슬롯으로 fallback (GoldenPath 단일 계정 모드)
        var slot = _slots.TryGetValue(equipmentId, out var s)
            ? s
            : _slots.Values.First();

        var policy = TopicPolicy.Get(kind);
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(TopicPolicy.Topic(kind, equipmentId))
            .WithPayload(payload)
            .WithQualityOfServiceLevel(policy.Qos)
            .WithRetainFlag(policy.Retain)
            .WithContentType("application/json")
            .Build();

        await slot.Gate.WaitAsync(ct);
        try
        {
            if (!slot.Client.IsConnected)
            {
                _log.LogDebug("Publish skipped (not connected) [{Eq}]: {Topic}", equipmentId, msg.Topic);
                return;
            }
            await slot.Client.PublishAsync(msg, ct);
            _log.LogInformation("PUB {Topic} qos={Qos} retain={Retain} bytes={Len}",
                msg.Topic, (int)policy.Qos, policy.Retain, payload.Length);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Publish failed [{Eq}]: {Topic}", equipmentId, msg.Topic);
        }
        finally { slot.Gate.Release(); }
    }

    // MqttSubscriber가 호출하는 "ds/+/control" 구독을 장비별 전용 토픽으로 변환
    // ACL: 각 계정은 자신의 ds/{eq}/control 만 read 가능 — wildcard 구독 불가
    public async Task SubscribeAsync(string topicFilter, CancellationToken ct)
    {
        foreach (var (equipmentId, slot) in _slots)
        {
            if (!slot.Client.IsConnected) continue;
            var topic = TopicPolicy.Topic(TopicPolicy.Kind.Control, equipmentId);
            var opts = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce))
                .Build();
            await slot.Client.SubscribeAsync(opts, ct);
            _log.LogInformation("SUB [{Eq}] {Topic}", equipmentId, topic);
        }
    }

    public async Task DisconnectGracefulAsync(CancellationToken ct)
    {
        foreach (var (equipmentId, slot) in _slots)
        {
            try
            {
                if (slot.Client.IsConnected)
                {
                    await slot.Client.DisconnectAsync(
                        new MqttClientDisconnectOptionsBuilder()
                            .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                            .Build(), ct);
                    _log.LogInformation("MQTT graceful disconnect sent [{Eq}]", equipmentId);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Graceful disconnect failed [{Eq}]", equipmentId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _loopCtsList) { try { cts.Cancel(); } catch { } }
        foreach (var loop in _reconnectLoops) { try { await loop; } catch { } }
        foreach (var cts in _loopCtsList) { cts.Dispose(); }
        foreach (var slot in _slots.Values)
        {
            slot.Client.Dispose();
            slot.Gate.Dispose();
        }
    }
}
