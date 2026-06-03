using System.Text.Json;
using System.Text.Json.Nodes;
using DsEap.Configuration;
using DsEap.Events.Models;

namespace DsEap.MockData;

// Mock 페이로드에 equipment_id/lot_id/timestamp/message_id 등을 현재 런타임 값으로 치환.
// _ prefix 메타 필드는 DTO 역직렬화 시 자연스럽게 제거 (§1.3).
public static class MockPayloadTransformer
{
    public static InspectionResultPayload OverrideInspection(
        InspectionResultPayload src,
        string equipmentId,
        string lotId,
        string stripId,
        string unitId,
        string recipeId,
        string recipeVersion,
        string operatorId,
        string equipmentStatus)
    {
        src.MessageId = Guid.NewGuid().ToString();
        src.Timestamp = EventJson.NowIsoUtc();
        src.EquipmentId = equipmentId;
        src.EquipmentStatus = equipmentStatus;
        src.LotId = lotId;
        src.StripId = stripId;
        src.UnitId = unitId;
        src.RecipeId = recipeId;
        src.RecipeVersion = recipeVersion;
        src.OperatorId = operatorId;
        return src;
    }

    // 모든 side_result AxisResult의 ErrorType을 주어진 값으로 덮어쓴다.
    // 예: CAM_TIMEOUT_ERR 트리거(ET=30) 사전 3회 발행 시 사용 (eap-spec §9.2).
    public static InspectionResultPayload OverrideSideErrorType(InspectionResultPayload src, int errorType)
    {
        foreach (var side in src.InspectionDetail.SideResult)
        {
            side.ErrorType = errorType;
            side.InspectionResult = 0; // FAIL
        }
        src.OverallResult = "FAIL";
        src.FailReasonCode = $"ET={errorType}";
        return src;
    }

    // Cpk 검증용: geometric 수치 필드에 정규분포 지터를 더해 LOT 내 측정 산포(σ>0)를 만든다.
    // Mock 파일 원본은 불변(매 takt 새 DTO 역직렬화). PASS/FAIL·ErrorType·inspection_detail에는 손대지 않는다.
    public static void ApplyGeometricJitter(InspectionResultPayload src, GeometricJitterSettings cfg, Random rng)
    {
        if (!cfg.Enabled) return;
        if (src.Geometric is not JsonElement geo || geo.ValueKind != JsonValueKind.Object) return;

        var node = JsonNode.Parse(geo.GetRawText())?.AsObject();
        if (node is null) return;

        JitterField(node, "dimension_w_mm", cfg.DimensionSigmaMm, rng);
        JitterField(node, "dimension_l_mm", cfg.DimensionSigmaMm, rng);
        JitterField(node, "dimension_h_mm", cfg.DimensionSigmaMm, rng);
        JitterField(node, "kerf_width_um", cfg.KerfSigmaUm, rng);

        src.Geometric = JsonSerializer.SerializeToElement(node);
    }

    private static void JitterField(JsonObject obj, string key, double sigma, Random rng)
    {
        if (sigma <= 0) return;
        if (!obj.TryGetPropertyValue(key, out var n) || n is null) return;
        if (n.GetValueKind() != JsonValueKind.Number) return;

        double noisy = n.GetValue<double>() + NextGaussian(rng) * sigma;
        obj[key] = Math.Round(noisy, 4);
    }

    // Box-Muller 표준정규 표본
    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public static OracleAnalysisPayload OverrideOracle(
        OracleAnalysisPayload src,
        string equipmentId,
        string lotId,
        string recipeId)
    {
        src.MessageId = Guid.NewGuid().ToString();
        src.Timestamp = EventJson.NowIsoUtc();
        src.EquipmentId = equipmentId;
        src.LotId = lotId;
        src.RecipeId = recipeId;
        return src;
    }
}
