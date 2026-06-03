using DsEap.Configuration;
using DsEap.Equipment;
using DsEap.Events.Models;
using DsEap.Events.Publishers;
using DsEap.MockData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DsEap.Tests;

// Task E7 — Graceful Shutdown 시퀀스 검증 (§1.2.5)
// RUN 장비는 LOT_END(ABORTED) + STATUS(IDLE) 발행, IDLE/STOP 장비는 LOT_END 생략.
public sealed class GracefulShutdownTests
{
    private sealed record PublishedCall(string Kind, string EquipmentId, string? LotStatus);

    private sealed class FakeEventPublisher : EventPublisher
    {
        public readonly List<PublishedCall> Calls = new();

        public override Task PublishStatusAsync(VirtualEquipment eq, CancellationToken ct)
        {
            Calls.Add(new PublishedCall("STATUS", eq.EquipmentId, eq.State.ToWire()));
            return Task.CompletedTask;
        }

        public override Task PublishLotEndAsync(VirtualEquipment eq, string lotStatus, CancellationToken ct)
        {
            Calls.Add(new PublishedCall("LOT_END", eq.EquipmentId, lotStatus));
            return Task.CompletedTask;
        }

        // Shutdown 경로에서 호출되지 않아야 함
        public override Task PublishHeartbeatAsync(VirtualEquipment eq, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishInspectionAsync(VirtualEquipment eq, InspectionResultPayload payload, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishRecipeChangedAsync(VirtualEquipment eq, string prevId, string prevVer, string newId, string newVer, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishHwAlarmAsync(VirtualEquipment eq, HwAlarmPayload payload, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishOracleAsync(VirtualEquipment eq, OracleAnalysisPayload payload, CancellationToken ct) => Task.CompletedTask;
        public override Task ClearAlarmRetainedAsync(VirtualEquipment eq, CancellationToken ct) => Task.CompletedTask;
    }

    private static (EquipmentManager mgr, FakeEventPublisher pub) BuildManager()
    {
        var settings = new EapSettings();
        var opts = Options.Create(settings);
        var pub = new FakeEventPublisher();
        var hb = new HeartbeatLoop(pub, settings.Timing, NullLogger<HeartbeatLoop>.Instance);
        var st = new StatusLoop(pub, new AlarmTracker(NullLogger<AlarmTracker>.Instance), settings.Timing, NullLogger<StatusLoop>.Instance);
        var mocks = new MockDataLoader(Path.Combine(Path.GetTempPath(), $"eap-mocks-{Guid.NewGuid():N}"), NullLogger<MockDataLoader>.Instance);
        var insp = new InspectionLoop(pub, mocks, settings.Timing, settings.GeometricJitter, NullLogger<InspectionLoop>.Instance);
        var mgr = new EquipmentManager(opts, pub, hb, st, insp, mocks, NullLogger<EquipmentManager>.Instance);
        return (mgr, pub);
    }

    [Fact]
    public async Task Empty_manager_is_noop()
    {
        var (mgr, pub) = BuildManager();
        await mgr.GracefulShutdownAsync(CancellationToken.None);
        Assert.Empty(pub.Calls);
    }

    [Fact]
    public async Task Run_equipment_publishes_lot_end_aborted_then_status_idle()
    {
        var (mgr, pub) = BuildManager();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-TEST-001", expectedTotalUnits: 100);
        eq.RecordInspection(pass: true);
        mgr.Register(eq);

        await mgr.GracefulShutdownAsync(CancellationToken.None);

        Assert.Equal(2, pub.Calls.Count);
        Assert.Equal("LOT_END", pub.Calls[0].Kind);
        Assert.Equal("ABORTED", pub.Calls[0].LotStatus);
        Assert.Equal("DS-VIS-001", pub.Calls[0].EquipmentId);

        Assert.Equal("STATUS", pub.Calls[1].Kind);
        Assert.Equal(EquipmentStatuses.Idle, pub.Calls[1].LotStatus);
        Assert.Equal(EquipmentState.Idle, eq.State); // FinalizeLot이 IDLE로 복귀
    }

    [Fact]
    public async Task Idle_equipment_skips_lot_end()
    {
        var (mgr, pub) = BuildManager();
        var eq = new VirtualEquipment("DS-VIS-003", "ATC_1X1", "v1.0", "ENG-KIM");
        // IDLE 상태 — 초기값 유지
        mgr.Register(eq);

        await mgr.GracefulShutdownAsync(CancellationToken.None);

        Assert.Empty(pub.Calls); // IDLE은 어떤 것도 발행하지 않음
    }

    [Fact]
    public async Task Stop_equipment_skips_lot_end()
    {
        var (mgr, pub) = BuildManager();
        var eq = new VirtualEquipment("DS-VIS-004", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.TransitionToStop();
        mgr.Register(eq);

        await mgr.GracefulShutdownAsync(CancellationToken.None);

        Assert.Empty(pub.Calls);
    }

    [Fact]
    public async Task Multiple_equipments_processed_independently()
    {
        var (mgr, pub) = BuildManager();

        var run = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        run.StartLot("LOT-A", 100);
        mgr.Register(run);

        var idle = new VirtualEquipment("DS-VIS-003", "ATC_1X1", "v1.0", "ENG-KIM");
        mgr.Register(idle);

        var stop = new VirtualEquipment("DS-VIS-004", "Carsem_3X3", "v1.0", "ENG-KIM");
        stop.TransitionToStop();
        mgr.Register(stop);

        await mgr.GracefulShutdownAsync(CancellationToken.None);

        // RUN 장비만 LOT_END + STATUS 2건 발행
        Assert.Equal(2, pub.Calls.Count);
        Assert.All(pub.Calls, c => Assert.Equal("DS-VIS-001", c.EquipmentId));
        Assert.Contains(pub.Calls, c => c.Kind == "LOT_END" && c.LotStatus == "ABORTED");
        Assert.Contains(pub.Calls, c => c.Kind == "STATUS");
    }
}
