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
