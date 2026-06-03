using DsEap.Equipment;
using DsEap.Events.Models;
using DsEap.Events.Publishers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DsEap.Tests;

// E4 §7.2 — CONTROL_CMD 6종 핸들러 동작 검증
public sealed class ControlCommandHandlerTests
{
    private sealed record PublishedCall(string Kind, string EquipmentId, string? Detail);

    private sealed class FakePublisher : EventPublisher
    {
        public readonly List<PublishedCall> Calls = new();

        public override Task PublishStatusAsync(VirtualEquipment eq, CancellationToken ct)
        {
            Calls.Add(new("STATUS", eq.EquipmentId, eq.State.ToWire()));
            return Task.CompletedTask;
        }

        public override Task PublishLotEndAsync(VirtualEquipment eq, string lotStatus, CancellationToken ct)
        {
            Calls.Add(new("LOT_END", eq.EquipmentId, lotStatus));
            return Task.CompletedTask;
        }

        public override Task ClearAlarmRetainedAsync(VirtualEquipment eq, CancellationToken ct)
        {
            Calls.Add(new("ALARM_CLEAR", eq.EquipmentId, null));
            return Task.CompletedTask;
        }

        public override Task PublishHeartbeatAsync(VirtualEquipment eq, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishInspectionAsync(VirtualEquipment eq, InspectionResultPayload p, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishRecipeChangedAsync(VirtualEquipment eq, string a, string b, string c, string d, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishHwAlarmAsync(VirtualEquipment eq, HwAlarmPayload p, CancellationToken ct) => Task.CompletedTask;
        public override Task PublishOracleAsync(VirtualEquipment eq, OracleAnalysisPayload p, CancellationToken ct) => Task.CompletedTask;
    }

    private static (ControlCommandHandler handler, FakePublisher pub, EquipmentManager mgr) Build()
    {
        var pub = new FakePublisher();
        var tracker = new AlarmTracker(NullLogger<AlarmTracker>.Instance);
        var mgr = new EquipmentManager(
            Microsoft.Extensions.Options.Options.Create(new Configuration.EapSettings()),
            pub,
            new HeartbeatLoop(pub, new Configuration.TimingSettings(), NullLogger<HeartbeatLoop>.Instance),
            new StatusLoop(pub, tracker, new Configuration.TimingSettings(), NullLogger<StatusLoop>.Instance),
            new InspectionLoop(pub, new MockData.MockDataLoader(
                Path.Combine(Path.GetTempPath(), $"eap-{Guid.NewGuid():N}"),
                NullLogger<MockData.MockDataLoader>.Instance), new Configuration.TimingSettings(),
                new Configuration.GeometricJitterSettings(),
                NullLogger<InspectionLoop>.Instance),
            new MockData.MockDataLoader(
                Path.Combine(Path.GetTempPath(), $"eap-{Guid.NewGuid():N}"),
                NullLogger<MockData.MockDataLoader>.Instance),
            NullLogger<EquipmentManager>.Instance);
        var handler = new ControlCommandHandler(mgr, pub, tracker, NullLogger<ControlCommandHandler>.Instance);
        return (handler, pub, mgr);
    }

    [Fact]
    public async Task EmergencyStop_transitions_to_stop_not_idle()
    {
        var (handler, pub, mgr) = Build();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", 100);
        mgr.Register(eq);

        await handler.HandleAsync("DS-VIS-001", new ControlCmdPayload
        {
            Command = "EMERGENCY_STOP", IssuedBy = "MOBILE_APP"
        }, CancellationToken.None);

        // EMERGENCY_STOP → STOP 상태 유지 (IDLE 아님)
        Assert.Equal(EquipmentState.Stop, eq.State);
        Assert.Contains(pub.Calls, c => c.Kind == "LOT_END" && c.Detail == "ABORTED");
        Assert.Contains(pub.Calls, c => c.Kind == "STATUS" && c.Detail == "STOP");
    }

    [Fact]
    public async Task LotAbort_transitions_to_idle_not_stop()
    {
        var (handler, pub, mgr) = Build();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", 100);
        eq.RecordInspection(pass: true);
        mgr.Register(eq);

        await handler.HandleAsync("DS-VIS-001", new ControlCmdPayload
        {
            Command = "LOT_ABORT", IssuedBy = "MES_SERVER"
        }, CancellationToken.None);

        // LOT_ABORT → IDLE 복귀 (STOP 아님)
        Assert.Equal(EquipmentState.Idle, eq.State);
        Assert.Contains(pub.Calls, c => c.Kind == "LOT_END" && c.Detail == "ABORTED");
        Assert.Contains(pub.Calls, c => c.Kind == "STATUS" && c.Detail == "IDLE");
    }

    [Fact]
    public async Task LotAbort_ignored_when_not_running()
    {
        var (handler, pub, mgr) = Build();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        mgr.Register(eq);

        await handler.HandleAsync("DS-VIS-001", new ControlCmdPayload
        {
            Command = "LOT_ABORT", IssuedBy = "MES_SERVER"
        }, CancellationToken.None);

        Assert.Equal(EquipmentState.Idle, eq.State);
        Assert.Empty(pub.Calls); // IDLE 상태에서 LOT_ABORT → 아무 동작 없음
    }

    [Fact]
    public async Task AlarmClear_clears_retained_alarm()
    {
        var (handler, pub, mgr) = Build();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        mgr.Register(eq);

        await handler.HandleAsync("DS-VIS-001", new ControlCmdPayload
        {
            Command = "ALARM_CLEAR", IssuedBy = "MES_SERVER"
        }, CancellationToken.None);

        Assert.Single(pub.Calls);
        Assert.Equal("ALARM_CLEAR", pub.Calls[0].Kind);
    }

    [Fact]
    public async Task StatusQuery_publishes_immediate_status()
    {
        var (handler, pub, mgr) = Build();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        mgr.Register(eq);

        await handler.HandleAsync("DS-VIS-001", new ControlCmdPayload
        {
            Command = "STATUS_QUERY", IssuedBy = "MOBILE_APP"
        }, CancellationToken.None);

        Assert.Single(pub.Calls);
        Assert.Equal("STATUS", pub.Calls[0].Kind);
    }

    [Fact]
    public async Task AlarmAck_with_burst_id_clears_alarm()
    {
        var (handler, pub, mgr) = Build();
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        mgr.Register(eq);

        await handler.HandleAsync("DS-VIS-001", new ControlCmdPayload
        {
            Command = "ALARM_ACK",
            IssuedBy = "MOBILE_APP",
            TargetBurstId = "8d9e1f2a-aggex-4abc-b100-000000000001"
        }, CancellationToken.None);

        Assert.Single(pub.Calls);
        Assert.Equal("ALARM_CLEAR", pub.Calls[0].Kind);
    }

    [Fact]
    public async Task Unknown_equipment_is_ignored()
    {
        var (handler, pub, _) = Build();

        await handler.HandleAsync("DS-VIS-999", new ControlCmdPayload
        {
            Command = "STATUS_QUERY", IssuedBy = "MOBILE_APP"
        }, CancellationToken.None);

        Assert.Empty(pub.Calls);
    }
}
