using System.Text.Json;
using System.Text.Json.Nodes;
using DsEap.Events.Models;
using Xunit;

namespace DsEap.Tests;

// E2 §5.4 검증: Mock 01~27을 DTO로 역직렬화 → 재직렬화 후 원본과 논리적으로 동등한지 확인
// 정규화 규칙:
//   1) `_` prefix 키는 Mock 메타 필드이므로 발행 페이로드에서 제거 (§1.3)
//   2) 값이 null인 속성은 DTO의 WhenWritingNull 정책상 직렬화 결과에서 사라지므로 비교 대상에서 제외
public sealed class MockRoundtripTests
{
    private static readonly string MockDir = LocateMockDir();

    public static IEnumerable<object[]> AllMocks()
    {
        foreach (var path in Directory.EnumerateFiles(MockDir, "*.json").OrderBy(p => p))
            yield return new object[] { Path.GetFileName(path) };
    }

    [Theory]
    [MemberData(nameof(AllMocks))]
    public void Mock_roundtrip_equivalence(string fileName)
    {
        var path = Path.Combine(MockDir, fileName);
        var originalJson = File.ReadAllText(path);
        var original = JsonNode.Parse(originalJson)!.AsObject();

        var eventType = original["event_type"]!.GetValue<string>();
        byte[] reserialized = eventType switch
        {
            EventTypes.Heartbeat        => Roundtrip<HeartbeatPayload>(originalJson),
            EventTypes.StatusUpdate     => Roundtrip<StatusUpdatePayload>(originalJson),
            EventTypes.InspectionResult => Roundtrip<InspectionResultPayload>(originalJson),
            EventTypes.LotEnd           => Roundtrip<LotEndPayload>(originalJson),
            EventTypes.HwAlarm          => Roundtrip<HwAlarmPayload>(originalJson),
            EventTypes.RecipeChanged    => Roundtrip<RecipeChangedPayload>(originalJson),
            EventTypes.ControlCmd       => Roundtrip<ControlCmdPayload>(originalJson),
            EventTypes.OracleAnalysis   => Roundtrip<OracleAnalysisPayload>(originalJson),
            _ => throw new InvalidOperationException($"Unknown event_type: {eventType}")
        };

        var reparsed = JsonNode.Parse(reserialized)!.AsObject();

        Normalize(original);
        Normalize(reparsed);

        Assert.Equal(Canonical(original), Canonical(reparsed));
    }

    [Fact]
    public void Heartbeat_has_no_equipment_status()
    {
        var hb = new HeartbeatPayload
        {
            Timestamp = "2026-01-22T16:17:10.921Z",
            EquipmentId = "DS-VIS-001",
        };
        var json = JsonSerializer.Serialize(hb, EventJson.Options);
        Assert.DoesNotContain("equipment_status", json);
        var obj = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(4, obj.Count);
    }

    [Fact]
    public void Inspection_detail_uses_PascalCase()
    {
        var payload = JsonSerializer.Deserialize<InspectionResultPayload>(
            File.ReadAllText(Path.Combine(MockDir, "04_inspection_pass.json")), EventJson.Options)!;
        var json = JsonSerializer.Serialize(payload, EventJson.Options);
        Assert.Contains("\"ZAxisNum\"", json);
        Assert.Contains("\"XOffset\"", json);
        Assert.DoesNotContain("\"z_axis_num\"", json);
        Assert.DoesNotContain("\"x_offset\"", json);
    }

    [Fact]
    public void Top_level_uses_snake_case()
    {
        var payload = JsonSerializer.Deserialize<StatusUpdatePayload>(
            File.ReadAllText(Path.Combine(MockDir, "02_status_run.json")), EventJson.Options)!;
        var json = JsonSerializer.Serialize(payload, EventJson.Options);
        Assert.Contains("\"equipment_status\"", json);
        Assert.Contains("\"current_unit_count\"", json);
        Assert.DoesNotContain("\"equipmentStatus\"", json);
    }

    [Fact]
    public void Underscore_prefix_fields_are_dropped_on_reserialize()
    {
        var payload = JsonSerializer.Deserialize<HeartbeatPayload>(
            File.ReadAllText(Path.Combine(MockDir, "01_heartbeat.json")), EventJson.Options)!;
        var json = JsonSerializer.Serialize(payload, EventJson.Options);
        Assert.DoesNotContain("_source", json);
        Assert.DoesNotContain("_note", json);
    }

    private static byte[] Roundtrip<T>(string json)
    {
        var dto = JsonSerializer.Deserialize<T>(json, EventJson.Options)!;
        return JsonSerializer.SerializeToUtf8Bytes(dto, EventJson.Options);
    }

    // _ prefix 키 삭제 + null 값 속성 삭제 (재귀)
    private static void Normalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var toRemove = new List<string>();
            foreach (var kv in obj)
            {
                if (kv.Key.StartsWith("_") || kv.Value is null || kv.Value is JsonValue v && v.ToJsonString() == "null")
                    toRemove.Add(kv.Key);
            }
            foreach (var k in toRemove) obj.Remove(k);
            foreach (var kv in obj) Normalize(kv.Value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) Normalize(item);
        }
    }

    // 키 정렬된 canonical JSON 문자열 (순서 독립 비교)
    private static string Canonical(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                JsonNode? child = kv.Value is null ? null : JsonNode.Parse(kv.Value.ToJsonString());
                sorted[kv.Key] = child is null ? null : JsonNode.Parse(Canonical(child));
            }
            return sorted.ToJsonString();
        }
        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
                result.Add(item is null ? null : JsonNode.Parse(Canonical(item)));
            return result.ToJsonString();
        }
        return node.ToJsonString();
    }

    private static string LocateMockDir()
    {
        // tests/DsEap.Tests/bin/Debug/net8.0 → ../../../../../../DS-Document/EAP_mock_data
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DS-Document", "EAP_mock_data");
            if (Directory.Exists(candidate)) return candidate;
            var candidate2 = Path.Combine(dir.FullName, "ds-document", "EAP_mock_data");
            if (Directory.Exists(candidate2)) return candidate2;
        }
        throw new DirectoryNotFoundException("EAP_mock_data directory not found walking up from " + baseDir);
    }
}
