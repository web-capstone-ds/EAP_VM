using System.Text.Json;

namespace DsEap.Mqtt;

// eap-spec §1.2.3 / Mock 17 — 비정상 종료 시 Broker가 자동 발행할 Will 페이로드
public static class WillPayloadFactory
{
    public static byte[] BuildEapDisconnected(string equipmentId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["message_id"] = Guid.NewGuid().ToString(),
            ["event_type"] = "HW_ALARM",
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["equipment_id"] = equipmentId,
            ["equipment_status"] = "STOP",
            ["alarm_level"] = "CRITICAL",
            ["hw_error_code"] = "EAP_DISCONNECTED",
            ["hw_error_source"] = "PROCESS",
            ["hw_error_detail"] = "EAP process terminated unexpectedly.",
            ["exception_detail"] = null,
            ["auto_recovery_attempted"] = false,
            ["requires_manual_intervention"] = true,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }
}
