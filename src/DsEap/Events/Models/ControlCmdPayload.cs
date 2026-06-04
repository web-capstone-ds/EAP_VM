namespace DsEap.Events.Models;

using System.Text.Json;

// API 명세서 §8 CONTROL_CMD — 구독 전용, equipment_status 제외
public sealed class ControlCmdPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.ControlCmd;
    public string Timestamp { get; set; } = "";
    public string? EquipmentId { get; set; } // Mock 21/22는 미포함, 26/27은 포함
    public string Command { get; set; } = ""; // START/EMERGENCY_STOP/STATUS_QUERY/ALARM_ACK/ALARM_CLEAR/RECIPE_LOAD/LOT_ABORT
    public string IssuedBy { get; set; } = ""; // MOBILE_APP | MES
    public string? Reason { get; set; }
    public string? TargetLotId { get; set; }
    public string? TargetBurstId { get; set; } // ALARM_ACK burst 그룹 ACK (§6.6.2)
    public Dictionary<string, JsonElement>? Payload { get; set; }
}
