using DsEap.Events.Models;
using DsEap.Events.Publishers;
using Microsoft.Extensions.Logging;

namespace DsEap.Mqtt;

// ds/+/control 구독 + CONTROL_CMD 페이로드 파싱 + 핸들러 분기
public sealed class MqttSubscriber
{
    private readonly MqttClientManager _mqtt;
    private readonly ControlCommandHandler _handler;
    private readonly ILogger<MqttSubscriber> _log;
    private bool _subscribed;

    public MqttSubscriber(
        MqttClientManager mqtt,
        ControlCommandHandler handler,
        ILogger<MqttSubscriber> log)
    {
        _mqtt = mqtt;
        _handler = handler;
        _log = log;

        _mqtt.Connected += OnConnectedAsync;
        _mqtt.ApplicationMessageReceived += OnMessageAsync;
    }

    private async Task OnConnectedAsync()
    {
        // 재연결 시 재구독 보장
        _subscribed = false;
        try
        {
            await _mqtt.SubscribeAsync("ds/+/control", CancellationToken.None);
            _subscribed = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Subscribe ds/+/control failed");
        }
    }

    private async Task OnMessageAsync(MQTTnet.Client.MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        if (!IsControlTopic(topic, out var equipmentIdFromTopic)) return;

        try
        {
            var payload = e.ApplicationMessage.PayloadSegment;
            if (payload.Count == 0) return;

            var cmd = EventJson.Deserialize<ControlCmdPayload>(payload.AsSpan());
            if (cmd is null)
            {
                _log.LogWarning("CONTROL_CMD parse returned null on {Topic}", topic);
                return;
            }

            await _handler.HandleAsync(equipmentIdFromTopic, cmd, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CONTROL_CMD handler failed on {Topic}", topic);
        }
    }

    // ds/{equipment_id}/control 형식만 허용
    private static bool IsControlTopic(string topic, out string equipmentId)
    {
        equipmentId = "";
        var parts = topic.Split('/');
        if (parts.Length != 3 || parts[0] != "ds" || parts[2] != "control") return false;
        equipmentId = parts[1];
        return true;
    }

    public bool IsSubscribed => _subscribed;
}
