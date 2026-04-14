namespace DsEap.Equipment;

// API 명세서 §3 — equipment_status 3가지
public enum EquipmentState
{
    Idle,
    Run,
    Stop,
}

public static class EquipmentStateExtensions
{
    public static string ToWire(this EquipmentState s) => s switch
    {
        EquipmentState.Idle => "IDLE",
        EquipmentState.Run  => "RUN",
        EquipmentState.Stop => "STOP",
        _ => "IDLE",
    };
}
