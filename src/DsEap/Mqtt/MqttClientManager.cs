using DsEap.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace DsEap.Mqtt;

// eap-spec §1.2 / §8.2 — MQTT v5.0 연결 · 재연결 · Will 관리
public sealed class MqttClientManager : IAsyncDisposable
{
    private readonly EapSettings _settings;
    private readonly ILogger<MqttClientManager> _log;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _rand = new();
    private CancellationTokenSource? _loopCts;
    private Task? _reconnectLoop;

    public IMqttClient Client => _client;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceived;
    public event Func<Task>? Connected;

    public MqttClientManager(IOptions<EapSettings> settings, ILogger<MqttClientManager> log)
    {
        _settings = settings.Value;
        _log = log;

        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            if (ApplicationMessageReceived is { } handler) await handler(e);
        };
        _client.ConnectedAsync += async _ =>
        {
            _log.LogInformation("MQTT connected to {Host}:{Port}", _settings.Broker.Host, _settings.Broker.Port);
            if (Connected is { } handler) await handler();
        };
        _client.DisconnectedAsync += e =>
        {
            _log.LogWarning("MQTT disconnected: {Reason}", e.Reason);
            return Task.CompletedTask;
        };

        // Will 토픽은 GoldenPath.EquipmentId 기준. 시나리오 모드에서 장비별 Will은
        // 각 VirtualEquipment가 자신의 세션으로 발행하는 형태가 이상적이지만, 본 프로젝트는
        // 단일 MQTT 세션을 공유하므로 대표 장비 ID를 Will에 사용한다 (eap-spec §8.2 주석).
        var willEquipmentId = _settings.GoldenPath.EquipmentId;
        var willPayload = WillPayloadFactory.BuildEapDisconnected(willEquipmentId);

        _options = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithTcpServer(_settings.Broker.Host, _settings.Broker.Port)
            .WithClientId($"ds-eap-{willEquipmentId}-{Guid.NewGuid():N}")
            .WithCleanStart(_settings.Broker.CleanStart)
            .WithSessionExpiryInterval(_settings.Broker.SessionExpirySeconds)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_settings.Broker.KeepAliveSeconds))
            .WithWillTopic(TopicPolicy.Topic(TopicPolicy.Kind.Alarm, willEquipmentId))
            .WithWillPayload(willPayload)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
            .WithWillRetain(true)
            .WithWillContentType("application/json")
            .WithCredentials(_settings.Broker.Username, _settings.Broker.Password)
            .Build();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectLoop = Task.Run(() => RunReconnectLoopAsync(_loopCts.Token), CancellationToken.None);
        await Task.CompletedTask;
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        var steps = _settings.Backoff.StepsSeconds;
        var maxIdx = Math.Max(steps.Length - 1, 0);
        var idx = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    _log.LogInformation("MQTT connecting to {Host}:{Port}...", _settings.Broker.Host, _settings.Broker.Port);
                    await _client.ConnectAsync(_options, ct);
                    idx = 0;
                }
                catch (Exception ex)
                {
                    var baseSec = steps[Math.Min(idx, maxIdx)];
                    var jitter = 1.0 + ((_rand.NextDouble() * 2 - 1) * (_settings.Backoff.JitterPct / 100.0));
                    var delay = TimeSpan.FromSeconds(Math.Max(0.1, baseSec * jitter));
                    _log.LogWarning("MQTT connect failed ({Msg}). Retry in {Delay:F1}s", ex.Message, delay.TotalSeconds);
                    try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
                    if (idx < maxIdx) idx++;
                    continue;
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task PublishAsync(
        TopicPolicy.Kind kind,
        string equipmentId,
        byte[] payload,
        CancellationToken ct)
    {
        var policy = TopicPolicy.Get(kind);
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(TopicPolicy.Topic(kind, equipmentId))
            .WithPayload(payload)
            .WithQualityOfServiceLevel(policy.Qos)
            .WithRetainFlag(policy.Retain)
            .WithContentType("application/json")
            .Build();

        await _gate.WaitAsync(ct);
        try
        {
            if (!_client.IsConnected)
            {
                _log.LogDebug("Publish skipped (not connected): {Topic}", msg.Topic);
                return;
            }
            await _client.PublishAsync(msg, ct);
            _log.LogInformation("PUB {Topic} qos={Qos} retain={Retain} bytes={Len}",
                msg.Topic, (int)policy.Qos, policy.Retain, payload.Length);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Publish failed: {Topic}", msg.Topic);
        }
        finally { _gate.Release(); }
    }

    public async Task SubscribeAsync(string topicFilter, CancellationToken ct)
    {
        if (!_client.IsConnected) return;
        var opts = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topicFilter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce))
            .Build();
        await _client.SubscribeAsync(opts, ct);
        _log.LogInformation("SUB {Topic}", topicFilter);
    }

    public async Task DisconnectGracefulAsync(CancellationToken ct)
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build(), ct);
                _log.LogInformation("MQTT graceful disconnect sent");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Graceful disconnect failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _loopCts?.Cancel(); } catch { }
        if (_reconnectLoop is not null)
        {
            try { await _reconnectLoop; } catch { }
        }
        _loopCts?.Dispose();
        _client.Dispose();
        _gate.Dispose();
    }
}
