using System.Text.Json;

namespace DsEap.Events.Models;

// API 명세서 §9 ORACLE_ANALYSIS — QoS 2, Retain=true, equipment_status 제외
// yield_status / threshold_proposal은 판정별로 키가 달라지므로 JsonElement로 투명 보존
public sealed class OracleAnalysisPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.OracleAnalysis;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string LotId { get; set; } = "";
    public string RecipeId { get; set; } = "";
    public string Judgment { get; set; } = "NORMAL"; // NORMAL | WARNING | DANGER
    public JsonElement? YieldStatus { get; set; }
    public double? IsolationForestScore { get; set; }
    public string AiComment { get; set; } = "";
    public JsonElement? ThresholdProposal { get; set; }
}
