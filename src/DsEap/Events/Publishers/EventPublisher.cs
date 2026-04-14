using DsEap.Equipment;
using DsEap.Events.Models;
using DsEap.Mqtt;
using Microsoft.Extensions.Logging;

namespace DsEap.Events.Publishers;

// 8종 이벤트 발행 공통 래퍼 — 토픽/QoS/Retained 정책은 MqttClientManager에 위임
public class EventPublisher
{
    private readonly MqttClientManager? _mqtt;
    private readonly AlarmTracker? _alarmTracker;
    private readonly ILogger<EventPublisher>? _log;

    public EventPublisher(MqttClientManager mqtt, AlarmTracker alarmTracker, ILogger<EventPublisher> log)
    {
        _mqtt = mqtt;
        _alarmTracker = alarmTracker;
        _log = log;
    }

    // 테스트 전용 — 파생 클래스가 모든 Publish 메서드를 오버라이드하면 내부 필드 접근 없음
    protected EventPublisher() { }

    public virtual Task PublishHeartbeatAsync(VirtualEquipment eq, CancellationToken ct)
    {
        var payload = new HeartbeatPayload
        {
            Timestamp = EventJson.NowIsoUtc(),
            EquipmentId = eq.EquipmentId,
        };
        return _mqtt!.PublishAsync(TopicPolicy.Kind.Heartbeat, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);
    }

    public virtual Task PublishStatusAsync(VirtualEquipment eq, CancellationToken ct)
    {
        var payload = new StatusUpdatePayload
        {
            Timestamp = EventJson.NowIsoUtc(),
            EquipmentId = eq.EquipmentId,
            EquipmentStatus = eq.State.ToWire(),
            LotId = eq.LotId,
            RecipeId = eq.RecipeId,
            RecipeVersion = eq.RecipeVersion,
            OperatorId = eq.OperatorId,
            UptimeSec = eq.UptimeSec,
            CurrentUnitCount = eq.State == EquipmentState.Run || eq.CurrentUnitCount > 0 ? eq.CurrentUnitCount : null,
            ExpectedTotalUnits = eq.ExpectedTotalUnits > 0 ? eq.ExpectedTotalUnits : null,
            CurrentYieldPct = eq.CurrentUnitCount > 0 ? eq.CurrentYieldPct : null,
        };
        return _mqtt!.PublishAsync(TopicPolicy.Kind.Status, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);
    }

    public virtual Task PublishInspectionAsync(VirtualEquipment eq, InspectionResultPayload payload, CancellationToken ct)
    {
        // payload의 IDs는 호출자(MockPayloadTransformer)가 치환
        return _mqtt!.PublishAsync(TopicPolicy.Kind.Result, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);
    }

    public virtual Task PublishLotEndAsync(VirtualEquipment eq, string lotStatus, CancellationToken ct)
    {
        var (total, pass, fail, yieldPct, durationSec) = (
            eq.CurrentUnitCount, eq.PassCount, eq.FailCount, eq.CurrentYieldPct,
            (long)(DateTime.UtcNow - (eq.LotStartUtc ?? DateTime.UtcNow)).TotalSeconds);

        var payload = new LotEndPayload
        {
            Timestamp = EventJson.NowIsoUtc(),
            EquipmentId = eq.EquipmentId,
            EquipmentStatus = EquipmentStatuses.Idle,
            LotId = eq.LotId ?? "",
            LotStatus = lotStatus,
            TotalUnits = total,
            PassCount = pass,
            FailCount = fail,
            YieldPct = yieldPct,
            LotDurationSec = durationSec,
        };
        return _mqtt!.PublishAsync(TopicPolicy.Kind.Lot, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);
    }

    public virtual async Task PublishRecipeChangedAsync(
        VirtualEquipment eq, string prevId, string prevVer, string newId, string newVer, CancellationToken ct)
    {
        var payload = new RecipeChangedPayload
        {
            Timestamp = EventJson.NowIsoUtc(),
            EquipmentId = eq.EquipmentId,
            EquipmentStatus = eq.State.ToWire(),
            PreviousRecipeId = prevId,
            PreviousRecipeVersion = prevVer,
            NewRecipeId = newId,
            NewRecipeVersion = newVer,
            ChangedBy = eq.OperatorId,
        };
        await _mqtt!.PublishAsync(TopicPolicy.Kind.Recipe, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);

        // Trigger 3: 새 레시피 변경 시 이전 레시피에서 누적된 알람 auto-ACK (§4.5)
        if (_alarmTracker!.ShouldAutoAckOnRecipeChange(eq.EquipmentId))
        {
            await ClearAlarmRetainedAsync(eq, ct);
            _alarmTracker.ClearAlarm(eq.EquipmentId);
        }
    }

    public virtual async Task PublishHwAlarmAsync(VirtualEquipment eq, HwAlarmPayload payload, CancellationToken ct)
    {
        payload.Timestamp = EventJson.NowIsoUtc();
        payload.EquipmentId = eq.EquipmentId;
        payload.EquipmentStatus = eq.State.ToWire();
        await _mqtt!.PublishAsync(TopicPolicy.Kind.Alarm, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);

        _alarmTracker!.RegisterAlarm(eq.EquipmentId, payload.HwErrorCode, payload.AutoRecoveryAttempted);

        // Trigger 1: auto_recovery_attempted=true 알람은 발행 직후 자동 clear
        if (payload.AutoRecoveryAttempted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            await ClearAlarmRetainedAsync(eq, ct);
            _alarmTracker!.ClearAlarm(eq.EquipmentId);
        }
    }

    public virtual Task PublishOracleAsync(VirtualEquipment eq, OracleAnalysisPayload payload, CancellationToken ct)
    {
        return _mqtt!.PublishAsync(TopicPolicy.Kind.Oracle, eq.EquipmentId,
            EventJson.SerializeToUtf8(payload), ct);
    }

    // 알람 retained clear (ALARM_ACK 처리) — 빈 페이로드 + Retain=true
    public virtual Task ClearAlarmRetainedAsync(VirtualEquipment eq, CancellationToken ct) =>
        _mqtt!.PublishAsync(TopicPolicy.Kind.Alarm, eq.EquipmentId, Array.Empty<byte>(), ct);
}
