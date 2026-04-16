namespace DsEap.Events.Models;

// API 명세서 §6 HW_ALARM — QoS 2, Retain=true, burst_id (§6.6.2)
public sealed class HwAlarmPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.HwAlarm;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string EquipmentStatus { get; set; } = EquipmentStatuses.Stop;
    public string AlarmLevel { get; set; } = "WARNING"; // CRITICAL | WARNING
    public string HwErrorCode { get; set; } = "";
    public string HwErrorSource { get; set; } = "";
    public string HwErrorDetail { get; set; } = "";
    public ExceptionDetail? ExceptionDetail { get; set; }
    public bool AutoRecoveryAttempted { get; set; }
    public bool RequiresManualIntervention { get; set; }
    public string? BurstId { get; set; }
    public int? BurstCount { get; set; } // burst_id 그룹 내 누적 알람 횟수 (API §6.1)
}

public sealed class ExceptionDetail
{
    public string Module { get; set; } = "";
    public string ExceptionType { get; set; } = "";
    public string StackTraceHash { get; set; } = "";
}
