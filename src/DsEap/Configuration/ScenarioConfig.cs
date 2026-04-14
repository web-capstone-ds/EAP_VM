using System.Text.Json.Serialization;

namespace DsEap.Configuration;

// scenarios/multi_equipment_4x.json 매핑 모델 (eap-spec §5.3.1)
public sealed class ScenarioConfig
{
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; set; } = "";
    [JsonPropertyName("scenario_name")] public string ScenarioName { get; set; } = "";
    [JsonPropertyName("duration_sec")] public int DurationSec { get; set; }
    [JsonPropertyName("concurrent_alarms")] public bool ConcurrentAlarms { get; set; }
    [JsonPropertyName("equipments")] public List<EquipmentScenario> Equipments { get; set; } = new();
}

public sealed class EquipmentScenario
{
    [JsonPropertyName("equipment_id")] public string EquipmentId { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = ""; // RUN_NORMAL / RUN_DEGRADED / IDLE / STOP_CRITICAL
    [JsonPropertyName("scenario_desc")] public string ScenarioDesc { get; set; } = "";
    [JsonPropertyName("mock_sequence")] public List<string> MockSequence { get; set; } = new();
    [JsonPropertyName("tile_color_hint")] public string? TileColorHint { get; set; }
}
