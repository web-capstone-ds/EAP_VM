using System.Text.Json;
using DsEap.Events.Models;
using Xunit;

namespace DsEap.Tests;

public sealed class ControlCmdTopicTests
{
    [Fact]
    public void Mock_21_emergency_stop_deserializes()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.MockDir, "21_control_emergency_stop.json"));
        var cmd = JsonSerializer.Deserialize<ControlCmdPayload>(json, EventJson.Options)!;
        Assert.Equal("EMERGENCY_STOP", cmd.Command);
        Assert.Equal("MOBILE_APP", cmd.IssuedBy);
        Assert.Equal("LOT-20260127-003", cmd.TargetLotId);
        Assert.Null(cmd.EquipmentId); // Mock 21은 equipment_id 미포함
    }

    [Fact]
    public void Mock_22_status_query_deserializes()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.MockDir, "22_control_status_query.json"));
        var cmd = JsonSerializer.Deserialize<ControlCmdPayload>(json, EventJson.Options)!;
        Assert.Equal("STATUS_QUERY", cmd.Command);
        Assert.Null(cmd.TargetLotId);
    }

    [Fact]
    public void Mock_26_alarm_ack_single_deserializes()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.MockDir, "26_control_alarm_ack.json"));
        var cmd = JsonSerializer.Deserialize<ControlCmdPayload>(json, EventJson.Options)!;
        Assert.Equal("ALARM_ACK", cmd.Command);
        Assert.Null(cmd.TargetBurstId);
        Assert.Equal("DS-VIS-001", cmd.EquipmentId);
    }

    [Fact]
    public void Mock_27_alarm_ack_burst_carries_burst_id()
    {
        var json = File.ReadAllText(Path.Combine(TestPaths.MockDir, "27_control_alarm_ack_burst.json"));
        var cmd = JsonSerializer.Deserialize<ControlCmdPayload>(json, EventJson.Options)!;
        Assert.Equal("ALARM_ACK", cmd.Command);
        Assert.Equal("8d9e1f2a-aggex-4abc-b100-000000000001", cmd.TargetBurstId);
    }
}

internal static class TestPaths
{
    public static string MockDir { get; } = Locate();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var name in new[] { "DS-Document", "ds-document" })
            {
                var c = Path.Combine(dir.FullName, name, "EAP_mock_data");
                if (Directory.Exists(c)) return c;
            }
        }
        throw new DirectoryNotFoundException();
    }
}
