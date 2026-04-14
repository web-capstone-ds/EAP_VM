namespace DsEap.Events.Models;

// API 명세서 §1 — event_type 상수
public static class EventTypes
{
    public const string Heartbeat       = "HEARTBEAT";
    public const string StatusUpdate    = "STATUS_UPDATE";
    public const string InspectionResult = "INSPECTION_RESULT";
    public const string LotEnd          = "LOT_END";
    public const string HwAlarm         = "HW_ALARM";
    public const string RecipeChanged   = "RECIPE_CHANGED";
    public const string ControlCmd      = "CONTROL_CMD";
    public const string OracleAnalysis  = "ORACLE_ANALYSIS";
}

public static class EquipmentStatuses
{
    public const string Run  = "RUN";
    public const string Idle = "IDLE";
    public const string Stop = "STOP";
}
