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

    // eap-spec §7.2 RECIPE_LOAD — MES 전용. 지정 레시피 로드 → RECIPE_CHANGED 발행
    private Task HandleRecipeLoad(VirtualEquipment eq, ControlCmdPayload cmd, CancellationToken ct)
    {
        // cmd.Reason에 "recipe_id:version" 형식이 들어올 수 있으나, 현재 Mock 미존재이므로 로그만 출력
        _log.LogInformation("RECIPE_LOAD: eq={Eq} reason={Reason} (MES 전용)", eq.EquipmentId, cmd.Reason);
        return Task.CompletedTask;
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
