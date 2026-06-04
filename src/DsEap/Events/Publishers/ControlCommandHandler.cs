using DsEap.Equipment;
using DsEap.Events.Models;
using Microsoft.Extensions.Logging;

namespace DsEap.Events.Publishers;

// CONTROL_CMD 6종 핸들러 — 모바일/MES에서 수신한 명령을 장비 상태에 반영
public sealed class ControlCommandHandler
{
    private readonly EquipmentManager _equipmentManager;
    private readonly EventPublisher _publisher;
    private readonly AlarmTracker _alarmTracker;
    private readonly ILogger<ControlCommandHandler> _log;

    public ControlCommandHandler(
        EquipmentManager equipmentManager,
        EventPublisher publisher,
        AlarmTracker alarmTracker,
        ILogger<ControlCommandHandler> log)
    {
        _equipmentManager = equipmentManager;
        _publisher = publisher;
        _alarmTracker = alarmTracker;
        _log = log;
    }

    public async Task HandleAsync(string equipmentIdFromTopic, ControlCmdPayload cmd, CancellationToken ct)
    {
        // 페이로드의 equipment_id가 있으면 우선, 없으면 토픽에서 추출된 값 사용
        var equipmentId = string.IsNullOrEmpty(cmd.EquipmentId) ? equipmentIdFromTopic : cmd.EquipmentId;
        var eq = _equipmentManager.Find(equipmentId);
        if (eq is null)
        {
            _log.LogWarning("CONTROL_CMD for unknown equipment '{Eq}' — dropped", equipmentId);
            return;
        }

        _log.LogInformation("CONTROL_CMD {Cmd} received: eq={Eq} issuer={Issuer} reason={Reason}",
            cmd.Command, equipmentId, cmd.IssuedBy, cmd.Reason);

        switch (cmd.Command)
        {
            case "EMERGENCY_STOP":
                await HandleEmergencyStop(eq, ct);
                break;

            case "STATUS_QUERY":
                // 즉시 STATUS_UPDATE 1회 발행
                await _publisher.PublishStatusAsync(eq, ct);
                break;

            case "ALARM_ACK":
                await HandleAlarmAck(eq, cmd, ct);
                break;

            case "ALARM_CLEAR":
                await HandleAlarmClear(eq, ct);
                break;

            case "RECIPE_LOAD":
                await HandleRecipeLoad(eq, cmd, ct);
                break;

            case "LOT_ABORT":
                await HandleLotAbort(eq, ct);
                break;

            default:
                _log.LogWarning("Unknown CONTROL_CMD command: {Cmd}", cmd.Command);
                break;
        }
    }

    private async Task HandleEmergencyStop(VirtualEquipment eq, CancellationToken ct)
    {
        // ① LOT_END(ABORTED) ② STATUS(STOP) — IDLE로 복귀하지 않음 (eap-spec §7.2 EMERGENCY_STOP)
        if (eq.State == EquipmentState.Run)
        {
            await _publisher.PublishLotEndAsync(eq, "ABORTED", ct);
            eq.FinalizeLot();
        }
        eq.TransitionToStop();
        await _publisher.PublishStatusAsync(eq, ct);
    }

    private async Task HandleAlarmAck(VirtualEquipment eq, ControlCmdPayload cmd, CancellationToken ct)
    {
        // 빈 페이로드 + Retain=true → Broker의 retained alarm 메시지 clear (§6.6)
        // burst_id가 지정된 경우도 동일 토픽에 clear 발행 (현재 구현은 장비당 1 alarm 토픽이므로 동등)
        await _publisher.ClearAlarmRetainedAsync(eq, ct);
        _alarmTracker.ClearAlarm(eq.EquipmentId);
        if (!string.IsNullOrEmpty(cmd.TargetBurstId))
            _log.LogInformation("ALARM_ACK burst cleared: burst_id={BurstId}", cmd.TargetBurstId);
        else
            _log.LogInformation("ALARM_ACK single alarm cleared for {Eq}", eq.EquipmentId);
    }

    // eap-spec §7.2 ALARM_CLEAR — MES 전용. 알람 해제 + 복구 시도
    private async Task HandleAlarmClear(VirtualEquipment eq, CancellationToken ct)
    {
        _log.LogInformation("ALARM_CLEAR: clearing alarm and attempting recovery for {Eq}", eq.EquipmentId);
        await _publisher.ClearAlarmRetainedAsync(eq, ct);
        _alarmTracker.ClearAlarm(eq.EquipmentId);
    }

    // eap-spec §7.2 RECIPE_LOAD — MES 전용. 지정 레시피 로드 → RECIPE_CHANGED + 모바일 알림 발행
    private async Task HandleRecipeLoad(VirtualEquipment eq, ControlCmdPayload cmd, CancellationToken ct)
    {
        var (newRecipeId, newRecipeVersion) = ResolveRecipeTarget(cmd);
        if (string.IsNullOrWhiteSpace(newRecipeId))
        {
            _log.LogWarning("RECIPE_LOAD ignored: recipe_id missing. eq={Eq} reason={Reason}", eq.EquipmentId, cmd.Reason);
            return;
        }

        var prevRecipeId = eq.RecipeId;
        var prevRecipeVersion = eq.RecipeVersion;
        newRecipeVersion = string.IsNullOrWhiteSpace(newRecipeVersion) ? "v1.0" : newRecipeVersion.Trim();

        if (string.Equals(prevRecipeId, newRecipeId, StringComparison.Ordinal)
            && string.Equals(prevRecipeVersion, newRecipeVersion, StringComparison.Ordinal))
        {
            _log.LogInformation("RECIPE_LOAD no-op: {Eq} already uses {Recipe} {Version}",
                eq.EquipmentId, newRecipeId, newRecipeVersion);
            await _publisher.PublishStatusAsync(eq, ct);
            return;
        }

        eq.ChangeRecipe(newRecipeId.Trim(), newRecipeVersion);
        _log.LogInformation("RECIPE_LOAD applied: {Eq} {PrevRecipe}/{PrevVersion} -> {NewRecipe}/{NewVersion}",
            eq.EquipmentId, prevRecipeId, prevRecipeVersion, eq.RecipeId, eq.RecipeVersion);

        await _publisher.PublishRecipeChangedAsync(
            eq,
            prevRecipeId,
            prevRecipeVersion,
            eq.RecipeId,
            eq.RecipeVersion,
            ct);

        await PublishRecipeChangedNoticeAsync(eq, cmd, prevRecipeId, prevRecipeVersion, ct);
        await _publisher.PublishStatusAsync(eq, ct);
    }

    private static (string RecipeId, string RecipeVersion) ResolveRecipeTarget(ControlCmdPayload cmd)
    {
        var recipeId = TryPayloadString(cmd, "recipe_id")
            ?? TryPayloadString(cmd, "recipeName")
            ?? TryPayloadString(cmd, "recipe_name")
            ?? ExtractRecipeFromReason(cmd.Reason);

        var version = TryPayloadString(cmd, "recipe_version")
            ?? TryPayloadString(cmd, "recipeVersion")
            ?? "v1.0";

        return (recipeId ?? "", version);
    }

    private static string? TryPayloadString(ControlCmdPayload cmd, string key)
    {
        if (cmd.Payload is null || !cmd.Payload.TryGetValue(key, out var value))
            return null;

        return value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static string? ExtractRecipeFromReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var text = reason.Trim();
        return text.StartsWith("Load ", StringComparison.OrdinalIgnoreCase)
            ? text[5..].Trim()
            : text;
    }

    private async Task PublishRecipeChangedNoticeAsync(
        VirtualEquipment eq,
        ControlCmdPayload cmd,
        string prevRecipeId,
        string prevRecipeVersion,
        CancellationToken ct)
    {
        var alarm = new HwAlarmPayload
        {
            AlarmLevel = "WARNING",
            HwErrorCode = "RECIPE_CHANGED_NOTICE",
            HwErrorSource = "MES_RECIPE_CONTROL",
            HwErrorDetail = $"Recipe changed from {prevRecipeId}/{prevRecipeVersion} to {eq.RecipeId}/{eq.RecipeVersion}",
            AutoRecoveryAttempted = false,
            RequiresManualIntervention = true,
            BurstId = string.IsNullOrWhiteSpace(cmd.MessageId) ? Guid.NewGuid().ToString() : cmd.MessageId,
        };

        await _publisher.PublishHwAlarmAsync(eq, alarm, ct);
    }

    // eap-spec §7.2 LOT_ABORT — MES 전용. LOT_END(ABORTED) → STATUS(IDLE) 전환
    // EMERGENCY_STOP과 달리 장비 정지 아님 — IDLE로 복귀
    private async Task HandleLotAbort(VirtualEquipment eq, CancellationToken ct)
    {
        if (eq.State == EquipmentState.Run)
        {
            _log.LogInformation("LOT_ABORT: {Eq} LOT_END(ABORTED) → STATUS(IDLE)", eq.EquipmentId);
            await _publisher.PublishLotEndAsync(eq, "ABORTED", ct);
            eq.FinalizeLot(); // FinalizeLot이 IDLE로 전환
            await _publisher.PublishStatusAsync(eq, ct);
        }
        else
        {
            _log.LogInformation("LOT_ABORT: {Eq} not in RUN state ({State}), ignoring", eq.EquipmentId, eq.State);
        }
    }
}
