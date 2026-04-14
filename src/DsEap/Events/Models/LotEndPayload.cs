namespace DsEap.Events.Models;

// API 명세서 §5 LOT_END — QoS 2, Retain=true
public sealed class LotEndPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.LotEnd;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string EquipmentStatus { get; set; } = EquipmentStatuses.Idle;
    public string LotId { get; set; } = "";
    public string LotStatus { get; set; } = "COMPLETED"; // COMPLETED | ABORTED
    public int TotalUnits { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public double YieldPct { get; set; }
    public long LotDurationSec { get; set; }
}
