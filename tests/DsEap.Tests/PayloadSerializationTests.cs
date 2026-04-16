using System.Text.Json;
using DsEap.Events.Models;
using Xunit;

namespace DsEap.Tests;

// E2 §5.3 — JSON 직렬화 세부 검증
public sealed class PayloadSerializationTests
{
    [Fact]
    public void Heartbeat_excludes_equipment_status()
    {
        var hb = new HeartbeatPayload
        {
            Timestamp = EventJson.NowIsoUtc(),
            EquipmentId = "DS-VIS-001",
        };
        var json = JsonSerializer.Serialize(hb, EventJson.Options);
        Assert.DoesNotContain("equipment_status", json);
        Assert.Contains("\"event_type\":\"HEARTBEAT\"", json);
    }

    [Fact]
    public void StatusUpdate_includes_progress_fields_when_set()
    {
        var su = new StatusUpdatePayload
        {
            EquipmentId = "DS-VIS-001",
            EquipmentStatus = "RUN",
            Timestamp = EventJson.NowIsoUtc(),
            CurrentUnitCount = 1247,
            ExpectedTotalUnits = 2792,
            CurrentYieldPct = 95.8,
        };
        var json = JsonSerializer.Serialize(su, EventJson.Options);
        Assert.Contains("\"current_unit_count\":1247", json);
        Assert.Contains("\"expected_total_units\":2792", json);
        Assert.Contains("\"current_yield_pct\":95.8", json);
    }

    [Fact]
    public void StatusUpdate_excludes_null_progress_fields()
    {
        var su = new StatusUpdatePayload
        {
            EquipmentId = "DS-VIS-001",
            EquipmentStatus = "IDLE",
            Timestamp = EventJson.NowIsoUtc(),
            CurrentUnitCount = null,
            ExpectedTotalUnits = null,
            CurrentYieldPct = null,
        };
        var json = JsonSerializer.Serialize(su, EventJson.Options);
        Assert.DoesNotContain("current_unit_count", json);
        Assert.DoesNotContain("expected_total_units", json);
        Assert.DoesNotContain("current_yield_pct", json);
    }

    [Fact]
    public void HwAlarm_burst_count_excluded_when_null()
    {
        var alarm = new HwAlarmPayload
        {
            EquipmentId = "DS-VIS-001",
            Timestamp = EventJson.NowIsoUtc(),
            HwErrorCode = "CAM_TIMEOUT_ERR",
            BurstId = null,
            BurstCount = null,
        };
        var json = JsonSerializer.Serialize(alarm, EventJson.Options);
        Assert.DoesNotContain("burst_id", json);
        Assert.DoesNotContain("burst_count", json);
    }

    [Fact]
    public void HwAlarm_burst_count_included_when_set()
    {
        var alarm = new HwAlarmPayload
        {
            EquipmentId = "DS-VIS-001",
            Timestamp = EventJson.NowIsoUtc(),
            HwErrorCode = "VISION_SCORE_ERR",
            BurstId = "8d9e1f2a-aggex-4abc-b100-000000000001",
            BurstCount = 41,
        };
        var json = JsonSerializer.Serialize(alarm, EventJson.Options);
        Assert.Contains("\"burst_id\":", json);
        Assert.Contains("\"burst_count\":41", json);
    }

    [Fact]
    public void LotEnd_fail_count_is_zero_not_null_on_pass()
    {
        var lot = new LotEndPayload
        {
            EquipmentId = "DS-VIS-001",
            Timestamp = EventJson.NowIsoUtc(),
            LotId = "LOT-TEST",
            LotStatus = "COMPLETED",
            TotalUnits = 2792,
            PassCount = 2687,
            FailCount = 0,
            YieldPct = 96.2,
        };
        var json = JsonSerializer.Serialize(lot, EventJson.Options);
        Assert.Contains("\"fail_count\":0", json); // 0이지 null이 아님
    }

    [Fact]
    public void Timestamp_format_is_iso8601_with_milliseconds()
    {
        var ts = EventJson.NowIsoUtc();
        // yyyy-MM-ddTHH:mm:ss.fffZ 형식 검증
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", ts);
    }

    [Fact]
    public void MessageId_is_valid_uuid_v4()
    {
        var hb = new HeartbeatPayload();
        Assert.True(Guid.TryParse(hb.MessageId, out _));
    }

    [Fact]
    public void ControlCmd_excludes_equipment_status()
    {
        var cmd = new ControlCmdPayload
        {
            Command = "STATUS_QUERY",
            IssuedBy = "MOBILE_APP",
            Timestamp = EventJson.NowIsoUtc(),
        };
        var json = JsonSerializer.Serialize(cmd, EventJson.Options);
        Assert.DoesNotContain("equipment_status", json);
    }

    [Fact]
    public void OracleAnalysis_excludes_equipment_status()
    {
        var oracle = new OracleAnalysisPayload
        {
            EquipmentId = "DS-VIS-001",
            Timestamp = EventJson.NowIsoUtc(),
            LotId = "LOT-1",
            RecipeId = "Carsem_3X3",
            Judgment = "NORMAL",
        };
        var json = JsonSerializer.Serialize(oracle, EventJson.Options);
        Assert.DoesNotContain("equipment_status", json);
    }
}
