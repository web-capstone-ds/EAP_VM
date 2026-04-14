using System.Text.Json;
using DsEap.Configuration;
using DsEap.Events.Models;
using Xunit;

namespace DsEap.Tests;

public sealed class ScenarioConfigTests
{
    [Fact]
    public void Multi_equipment_4x_scenario_parses()
    {
        var path = Path.Combine(TestPaths.MockDir, "scenarios", "multi_equipment_4x.json");
        var raw = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<ScenarioConfig>(raw, EventJson.Options)!;

        Assert.Equal("MULTI-4X-001", cfg.ScenarioId);
        Assert.Equal(4, cfg.Equipments.Count);

        var byId = cfg.Equipments.ToDictionary(e => e.EquipmentId);
        Assert.Equal("RUN_NORMAL",    byId["DS-VIS-001"].Scenario);
        Assert.Equal("RUN_DEGRADED",  byId["DS-VIS-002"].Scenario);
        Assert.Equal("IDLE",          byId["DS-VIS-003"].Scenario);
        Assert.Equal("STOP_CRITICAL", byId["DS-VIS-004"].Scenario);

        Assert.Contains("19_recipe_changed_new_4x6", byId["DS-VIS-002"].MockSequence);
        Assert.Equal("RED", byId["DS-VIS-004"].TileColorHint);
    }
}
