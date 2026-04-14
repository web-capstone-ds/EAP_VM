using DsEap.Equipment;
using Xunit;

namespace DsEap.Tests;

public sealed class VirtualEquipmentTests
{
    [Fact]
    public void StartLot_transitions_to_run_and_resets_counters()
    {
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        Assert.Equal(EquipmentState.Idle, eq.State);

        eq.StartLot("LOT-20260413-001", 2792);

        Assert.Equal(EquipmentState.Run, eq.State);
        Assert.Equal("LOT-20260413-001", eq.LotId);
        Assert.Equal(2792, eq.ExpectedTotalUnits);
        Assert.Equal(0, eq.CurrentUnitCount);
        Assert.Equal(0, eq.PassCount);
    }

    [Fact]
    public void RecordInspection_updates_counters_and_yield()
    {
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", 100);

        for (int i = 0; i < 96; i++) eq.RecordInspection(pass: true);
        for (int i = 0; i < 4;  i++) eq.RecordInspection(pass: false);

        Assert.Equal(100, eq.CurrentUnitCount);
        Assert.Equal(96,  eq.PassCount);
        Assert.Equal(4,   eq.FailCount);
        Assert.Equal(96.0, eq.CurrentYieldPct);
    }

    [Fact]
    public void FinalizeLot_returns_to_idle_and_snapshot()
    {
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", 10);
        for (int i = 0; i < 10; i++) eq.RecordInspection(pass: i < 9);

        var (total, pass, fail, yieldPct, _) = eq.FinalizeLot();

        Assert.Equal(EquipmentState.Idle, eq.State);
        Assert.Equal(10, total);
        Assert.Equal(9, pass);
        Assert.Equal(1, fail);
        Assert.Equal(90.0, yieldPct);
    }

    [Fact]
    public void TransitionToStop_sets_stop_state()
    {
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", 10);
        eq.TransitionToStop();
        Assert.Equal(EquipmentState.Stop, eq.State);
    }

    [Fact]
    public void StripAndUnit_derive_from_8slot_per_strip()
    {
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", 2792);
        var (s1, u1) = eq.CurrentStripAndUnit();
        Assert.Equal(1, s1);
        Assert.Equal(1, u1);

        for (int i = 0; i < 8; i++) eq.RecordInspection(pass: true);
        var (s2, u2) = eq.CurrentStripAndUnit();
        Assert.Equal(2, s2);
        Assert.Equal(9, u2);
    }
}
