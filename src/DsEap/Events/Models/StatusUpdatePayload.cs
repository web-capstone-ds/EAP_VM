namespace DsEap.Events.Models;

// API 명세서 §3 STATUS_UPDATE — Retained=true, 진행률 3필드 (§3.1)
public sealed class StatusUpdatePayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.StatusUpdate;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string EquipmentStatus { get; set; } = EquipmentStatuses.Idle;
    public string? LotId { get; set; }
    public string? RecipeId { get; set; }
    public string? RecipeVersion { get; set; }
    public string? OperatorId { get; set; }
    public long UptimeSec { get; set; }

    // v3.4 진행률 3필드 — RUN 상태에서만 채움, 그 외는 null 직렬화 제외
    public int? CurrentUnitCount { get; set; }
    public int? ExpectedTotalUnits { get; set; }
    public double? CurrentYieldPct { get; set; }
}
