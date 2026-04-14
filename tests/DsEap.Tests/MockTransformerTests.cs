using System.Text.Json;
using DsEap.Events.Models;
using DsEap.MockData;
using Xunit;

namespace DsEap.Tests;

public sealed class MockTransformerTests
{
    private static string MockPath(string stem) =>
        Path.Combine(LocateMockDir(), stem + ".json");

    [Fact]
    public void OverrideInspection_replaces_identifiers_and_drops_underscore_fields()
    {
        var src = JsonSerializer.Deserialize<InspectionResultPayload>(
            File.ReadAllText(MockPath("04_inspection_pass")), EventJson.Options)!;

        MockPayloadTransformer.OverrideInspection(
            src,
            equipmentId: "DS-VIS-002",
            lotId: "LOT-X",
            stripId: "STRIP-042",
            unitId: "UNIT-0333",
            recipeId: "Carsem_4X6",
            recipeVersion: "v1.0",
            operatorId: "ENG-PARK",
            equipmentStatus: "RUN");

        var json = JsonSerializer.Serialize(src, EventJson.Options);
        Assert.Contains("\"equipment_id\":\"DS-VIS-002\"", json);
        Assert.Contains("\"lot_id\":\"LOT-X\"", json);
        Assert.Contains("\"strip_id\":\"STRIP-042\"", json);
        Assert.Contains("\"unit_id\":\"UNIT-0333\"", json);
        Assert.Contains("\"recipe_id\":\"Carsem_4X6\"", json);
        Assert.DoesNotContain("_metadata", json);
        Assert.DoesNotContain("_source", json);
        // PascalCase 유지 확인
        Assert.Contains("\"ZAxisNum\"", json);
    }

    [Fact]
    public void OverrideOracle_preserves_yield_status_and_replaces_ids()
    {
        var src = JsonSerializer.Deserialize<OracleAnalysisPayload>(
            File.ReadAllText(MockPath("23_oracle_normal")), EventJson.Options)!;

        MockPayloadTransformer.OverrideOracle(src, "DS-VIS-003", "LOT-Y", "Carsem_3X3");

        var json = JsonSerializer.Serialize(src, EventJson.Options);
        Assert.Contains("\"equipment_id\":\"DS-VIS-003\"", json);
        Assert.Contains("\"lot_id\":\"LOT-Y\"", json);
        Assert.Contains("\"judgment\":\"NORMAL\"", json);
        Assert.Contains("dynamic_threshold", json); // yield_status 내부 보존
    }

    private static string LocateMockDir()
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
