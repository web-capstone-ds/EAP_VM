using DsEap.Equipment;
using DsEap.Events.Publishers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DsEap.Tests;

public sealed class AlarmTrackerTests
{
    [Fact]
    public void Register_and_clear_roundtrip()
    {
        var t = new AlarmTracker(NullLogger<AlarmTracker>.Instance);
        t.RegisterAlarm("DS-VIS-001", "CAM_TIMEOUT_ERR", autoRecoveryAttempted: false);
        Assert.True(t.TryGet("DS-VIS-001", out var a));
        Assert.Equal("CAM_TIMEOUT_ERR", a.HwErrorCode);

        t.ClearAlarm("DS-VIS-001");
        Assert.False(t.TryGet("DS-VIS-001", out _));
    }

    [Fact]
    public void Status_streak_auto_acks_after_6_consecutive_normal()
    {
        var t = new AlarmTracker(NullLogger<AlarmTracker>.Instance);
        t.RegisterAlarm("DS-VIS-001", "LIGHT_PWR_LOW", autoRecoveryAttempted: false);

        for (int i = 1; i <= 5; i++)
            Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));

        // 6번째
        Assert.True(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
    }

    [Fact]
    public void Stop_state_resets_streak()
    {
        var t = new AlarmTracker(NullLogger<AlarmTracker>.Instance);
        t.RegisterAlarm("DS-VIS-001", "CAM_TIMEOUT_ERR", autoRecoveryAttempted: false);

        for (int i = 0; i < 5; i++) t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run);
        // STOP 삽입 → streak 리셋
        t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Stop);
        // 다시 5회 필요
        Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
        Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
        Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
        Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
        Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
        Assert.True(t.ShouldAutoAckOnStatus("DS-VIS-001", EquipmentState.Run));
    }

    [Fact]
    public void Recipe_change_triggers_auto_ack_when_alarm_active()
    {
        var t = new AlarmTracker(NullLogger<AlarmTracker>.Instance);
        Assert.False(t.ShouldAutoAckOnRecipeChange("DS-VIS-001")); // 알람 없음

        t.RegisterAlarm("DS-VIS-001", "VISION_SCORE_ERR", autoRecoveryAttempted: false);
        Assert.True(t.ShouldAutoAckOnRecipeChange("DS-VIS-001"));
    }

    [Fact]
    public void Unknown_equipment_never_auto_acks()
    {
        var t = new AlarmTracker(NullLogger<AlarmTracker>.Instance);
        Assert.False(t.ShouldAutoAckOnStatus("DS-VIS-999", EquipmentState.Run));
        Assert.False(t.ShouldAutoAckOnRecipeChange("DS-VIS-999"));
    }
}
