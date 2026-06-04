using DsEap.Configuration;
using DsEap.Equipment;
using DsEap.Events.Models;
using DsEap.Events.Publishers;
using DsEap.MockData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DsEap.Tests;

public sealed class InspectionLoopTests
{
    private sealed class CountingPublisher : EventPublisher
    {
        public int InspectionCount { get; private set; }

        public override Task PublishInspectionAsync(VirtualEquipment eq, InspectionResultPayload payload, CancellationToken ct)
        {
            InspectionCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunLotAsync_stops_when_equipment_leaves_run_state()
    {
        var publisher = new CountingPublisher();
        var loop = new InspectionLoop(
            publisher,
            new MockDataLoader(LocateMockDir(), NullLogger<MockDataLoader>.Instance),
            new TimingSettings { TaktTimeMs = 20, PassRatio = 1.0 },
            new GeometricJitterSettings(),
            NullLogger<InspectionLoop>.Instance);
        var eq = new VirtualEquipment("DS-VIS-001", "Carsem_3X3", "v1.0", "ENG-KIM");
        eq.StartLot("LOT-1", expectedTotalUnits: 100);

        var runTask = loop.RunLotAsync(eq, maxUnits: 100, CancellationToken.None);
        while (eq.CurrentUnitCount == 0)
        {
            await Task.Delay(5);
        }

        var countAtStop = eq.CurrentUnitCount;
        eq.TransitionToStop();

        var completed = await Task.WhenAny(runTask, Task.Delay(500));

        Assert.Same(runTask, completed);
        Assert.Equal(EquipmentState.Stop, eq.State);
        Assert.Equal(countAtStop, eq.CurrentUnitCount);
        Assert.True(publisher.InspectionCount <= countAtStop + 1);
    }

    private static string LocateMockDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DS-Document", "EAP_mock_data");
            if (Directory.Exists(candidate)) return candidate;
            var lowerCaseCandidate = Path.Combine(dir.FullName, "ds-document", "EAP_mock_data");
            if (Directory.Exists(lowerCaseCandidate)) return lowerCaseCandidate;
        }
        throw new DirectoryNotFoundException("EAP_mock_data directory not found walking up from " + AppContext.BaseDirectory);
    }
}
