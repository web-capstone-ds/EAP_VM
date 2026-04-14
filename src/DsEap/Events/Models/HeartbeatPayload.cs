namespace DsEap.Events.Models;

// API 명세서 §2 HEARTBEAT — 4필드만, equipment_status 제외
public sealed class HeartbeatPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.Heartbeat;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
}
