using System.Text.Json;
using System.Text.Json.Serialization;

namespace DsEap.Events.Models;

// API 명세서 §4 INSPECTION_RESULT — takt ~1,620ms, QoS 1, Retain=false
public sealed class InspectionResultPayload
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = EventTypes.InspectionResult;
    public string Timestamp { get; set; } = "";
    public string EquipmentId { get; set; } = "";
    public string EquipmentStatus { get; set; } = EquipmentStatuses.Run;
    public string LotId { get; set; } = "";
    public string StripId { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string RecipeId { get; set; } = "";
    public string RecipeVersion { get; set; } = "";
    public string OperatorId { get; set; } = "";
    public string OverallResult { get; set; } = "PASS"; // PASS | FAIL
    public string? FailReasonCode { get; set; }
    public int FailCount { get; set; }
    public int TotalInspectedCount { get; set; }

    // inspection_detail 내부 필드만 PascalCase 유지 (GVisionWpf 원본 컨벤션)
    public InspectionDetail InspectionDetail { get; set; } = new();

    // detail 그룹 — 현장 로그 미수집, 자유 구조 허용
    public JsonElement? Geometric { get; set; }
    public JsonElement? Bga { get; set; }
    public JsonElement? Surface { get; set; }
    public JsonElement? Singulation { get; set; }
    public JsonElement? Process { get; set; }
}

public sealed class InspectionDetail
{
    [JsonPropertyName("prs_result")]
    public List<AxisResult> PrsResult { get; set; } = new();

    [JsonPropertyName("side_result")]
    public List<AxisResult> SideResult { get; set; } = new();
}

// PascalCase 유지 — GVisionWpf 원본 필드명. PropertyNamingPolicy를 무시하고 [JsonPropertyName]으로 고정.
public sealed class AxisResult
{
    [JsonPropertyName("ZAxisNum")]      public int ZAxisNum { get; set; }
    [JsonPropertyName("InspectionResult")] public int InspectionResult { get; set; }
    [JsonPropertyName("ErrorType")]     public int ErrorType { get; set; }
    [JsonPropertyName("XOffset")]       public int XOffset { get; set; }
    [JsonPropertyName("YOffset")]       public int YOffset { get; set; }
    [JsonPropertyName("TOffset")]       public int TOffset { get; set; }
}
