using System.Collections.Concurrent;
using DsEap.Equipment;
using Microsoft.Extensions.Logging;

namespace DsEap.Events.Publishers;

// eap-spec §4.5 자동 ACK 3종 트리거
//  1) auto_recovery_attempted=true 알람 복구 성공 → 즉시 clear
//  2) 동일 hw_error_code에 대해 정상(RUN/IDLE) STATUS 6회 연속 → clear
//  3) 새 RECIPE_CHANGED 발생 → 이전 레시피의 VISION_SCORE_ERR 등 clear
public sealed class AlarmTracker
{
    public sealed record ActiveAlarm(string HwErrorCode, bool AutoRecoveryAttempted, DateTime OpenedUtc);

    private readonly ILogger<AlarmTracker> _log;
    private readonly ConcurrentDictionary<string, ActiveAlarm> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _normalStatusStreak = new(StringComparer.OrdinalIgnoreCase);

    public const int AutoAckStreakThreshold = 6;

    public AlarmTracker(ILogger<AlarmTracker> log) { _log = log; }

    public void RegisterAlarm(string equipmentId, string hwErrorCode, bool autoRecoveryAttempted)
    {
        _active[equipmentId] = new ActiveAlarm(hwErrorCode, autoRecoveryAttempted, DateTime.UtcNow);
        _normalStatusStreak[equipmentId] = 0;
        _log.LogDebug("Alarm tracked: {Eq} {Code} auto_recovery={Auto}",
            equipmentId, hwErrorCode, autoRecoveryAttempted);

        // Trigger 1: 즉시 복구 시도가 성공 표시된 알람은 곧바로 clear 대상으로 간주 가능
        if (autoRecoveryAttempted)
        {
            _log.LogInformation("Auto-ACK trigger [auto_recovery]: {Eq} {Code}", equipmentId, hwErrorCode);
        }
    }

    public bool TryGet(string equipmentId, out ActiveAlarm alarm) =>
        _active.TryGetValue(equipmentId, out alarm!);

    public void ClearAlarm(string equipmentId)
    {
        _active.TryRemove(equipmentId, out _);
        _normalStatusStreak[equipmentId] = 0;
    }

    // StatusLoop 이후 호출 — 정상 상태 STATUS 연속 카운트 추적 (Trigger 2)
    public bool ShouldAutoAckOnStatus(string equipmentId, EquipmentState state)
    {
        if (!_active.ContainsKey(equipmentId)) return false;
        if (state == EquipmentState.Stop)
        {
            _normalStatusStreak[equipmentId] = 0;
            return false;
        }

        var streak = _normalStatusStreak.AddOrUpdate(equipmentId, 1, (_, v) => v + 1);
        if (streak >= AutoAckStreakThreshold)
        {
            _log.LogInformation("Auto-ACK trigger [status_streak]: {Eq} streak={Streak}", equipmentId, streak);
            return true;
        }
        return false;
    }

    // EventPublisher.PublishRecipeChangedAsync 이후 호출 (Trigger 3)
    public bool ShouldAutoAckOnRecipeChange(string equipmentId)
    {
        if (!_active.TryGetValue(equipmentId, out var alarm)) return false;
        _log.LogInformation("Auto-ACK trigger [recipe_change]: {Eq} {Code}", equipmentId, alarm.HwErrorCode);
        return true;
    }
}
