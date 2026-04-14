using System.Text.Json;
using DsEap.Configuration;
using DsEap.Equipment;
using DsEap.Events.Models;
using DsEap.Events.Publishers;
using DsEap.MockData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DsEap.Scenarios;

// multi_equipment_4x.json 기반 N:1 다설비 동시 시뮬레이션
public sealed class ScenarioRunner
{
    private readonly EapSettings _settings;
    private readonly EquipmentManager _equipmentManager;
    private readonly EventPublisher _publisher;
    private readonly HeartbeatLoop _heartbeat;
    private readonly StatusLoop _status;
    private readonly InspectionLoop _inspection;
    private readonly MockDataLoader _mocks;
    private readonly ILogger<ScenarioRunner> _log;
    private readonly List<Task> _tasks = new();

    public ScenarioRunner(
        IOptions<EapSettings> settings,
        EquipmentManager equipmentManager,
        EventPublisher publisher,
        HeartbeatLoop heartbeat,
        StatusLoop status,
        InspectionLoop inspection,
        MockDataLoader mocks,
        ILogger<ScenarioRunner> log)
    {
        _settings = settings.Value;
        _equipmentManager = equipmentManager;
        _publisher = publisher;
        _heartbeat = heartbeat;
        _status = status;
        _inspection = inspection;
        _mocks = mocks;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var scenarioPath = ResolveScenarioPath(_settings.Paths.ScenarioFile);
        if (!File.Exists(scenarioPath))
        {
            _log.LogError("Scenario file not found: {Path}", scenarioPath);
            return;
        }

        var raw = await File.ReadAllTextAsync(scenarioPath, ct);
        var cfg = JsonSerializer.Deserialize<ScenarioConfig>(raw, EventJson.Options)
            ?? throw new InvalidOperationException("Scenario parse failed");

        _log.LogInformation("Scenario '{Id}' — {Count} equipments, duration={Duration}s",
            cfg.ScenarioId, cfg.Equipments.Count, cfg.DurationSec);

        foreach (var def in cfg.Equipments)
        {
            var (initialRecipe, initialVer) = InitialRecipeFor(def.Scenario);
            var eq = new VirtualEquipment(def.EquipmentId, initialRecipe, initialVer, "ENG-KIM");
            _equipmentManager.Register(eq);

            _tasks.Add(Task.Run(() => _heartbeat.RunAsync(eq, ct), CancellationToken.None));
            _tasks.Add(Task.Run(() => _status.RunAsync(eq, ct), CancellationToken.None));
            _tasks.Add(Task.Run(() => DriveEquipmentAsync(eq, def, ct), CancellationToken.None));
        }

        _log.LogInformation("Scenario loops launched: {Count} equipment drivers + HB/Status",
            cfg.Equipments.Count);

        // 시나리오 자체는 루프 내부 지속. 호출자(Host)가 shutdown 토큰으로 중단.
        await Task.CompletedTask;
    }

    private async Task DriveEquipmentAsync(VirtualEquipment eq, EquipmentScenario def, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            switch (def.Scenario)
            {
                case "RUN_NORMAL":    await DriveRunNormal(eq, ct); break;
                case "RUN_DEGRADED":  await DriveRunDegraded(eq, ct); break;
                case "IDLE":          await DriveIdle(eq, ct); break;
                case "STOP_CRITICAL": await DriveStopCritical(eq, ct); break;
                default:
                    _log.LogWarning("Unknown scenario {Scenario} for {Eq}", def.Scenario, eq.EquipmentId);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scenario driver failed for {Eq}", eq.EquipmentId);
        }
    }

    private async Task DriveRunNormal(VirtualEquipment eq, CancellationToken ct)
    {
        // 시나리오 시작 시 이미 Carsem_3X3 장전 상태 — 추가 RECIPE_CHANGED 발행 불필요.
        while (!ct.IsCancellationRequested)
        {
            var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1, 1000):D3}";
            eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
            await _publisher.PublishStatusAsync(eq, ct);

            await _inspection.RunLotAsync(eq, _settings.Timing.GoldenPathMaxUnits, ct);
            if (ct.IsCancellationRequested) return;

            await _publisher.PublishLotEndAsync(eq, "COMPLETED", ct);
            eq.FinalizeLot();
            await _publisher.PublishStatusAsync(eq, ct);

            var oracle = _mocks.Get<OracleAnalysisPayload>("23_oracle_normal");
            MockPayloadTransformer.OverrideOracle(oracle, eq.EquipmentId, lotId, eq.RecipeId);
            await _publisher.PublishOracleAsync(eq, oracle, ct);

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DriveRunDegraded(VirtualEquipment eq, CancellationToken ct)
    {
        eq.ChangeRecipe("Carsem_4X6", "v1.0");
        await _publisher.PublishRecipeChangedAsync(eq, "Carsem_3X3", "v1.0", "Carsem_4X6", "v1.0", ct);

        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-DEG{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        // SIDE ET=52 fail 폭주 — Mock 05 반복
        for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("05_inspection_fail_side_et52");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            eq.RecordInspection(pass: false);
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }

        // HW_ALARM: SIDE_VISION_FAIL (Mock 15)
        var alarm = _mocks.Get<HwAlarmPayload>("15_alarm_side_vision_fail");
        await _publisher.PublishHwAlarmAsync(eq, alarm, ct);

        // ORACLE_ANALYSIS: WARNING (Mock 24)
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var oracle = _mocks.Get<OracleAnalysisPayload>("24_oracle_warning");
        MockPayloadTransformer.OverrideOracle(oracle, eq.EquipmentId, lotId, eq.RecipeId);
        await _publisher.PublishOracleAsync(eq, oracle, ct);
    }

    private async Task DriveIdle(VirtualEquipment eq, CancellationToken ct)
    {
        // 이미 생성 시 IDLE 상태. STATUS는 StatusLoop가 주기 발행.
        // 최초 1회 즉시 발행으로 retained IDLE 상태 확정.
        await _publisher.PublishStatusAsync(eq, ct);
    }

    private async Task DriveStopCritical(VirtualEquipment eq, CancellationToken ct)
    {
        // eap-spec §9.2 "카메라 타임아웃" 시퀀스 — ET=30 연속 3회 → CAM_TIMEOUT_ERR → STOP
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-CAM{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        for (int i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("07_inspection_fail_prs_offset");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            MockPayloadTransformer.OverrideSideErrorType(payload, errorType: 30);
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            eq.RecordInspection(pass: false);
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }

        // 3회 ET=30 누적 → STOP 전환 + CAM_TIMEOUT_ERR (Mock 11) retained CRITICAL
        eq.TransitionToStop();
        var alarm = _mocks.Get<HwAlarmPayload>("11_alarm_cam_timeout");
        await _publisher.PublishHwAlarmAsync(eq, alarm, ct);
        await _publisher.PublishStatusAsync(eq, ct);

        // EAP_DISCONNECTED 시뮬레이션 — Will 메시지 수동 발행으로 대체 (실제 Will은 프로세스 크래시 시 Broker가 발행)
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (OperationCanceledException) { return; }
        var willAlarm = _mocks.Get<HwAlarmPayload>("17_alarm_eap_disconnected");
        await _publisher.PublishHwAlarmAsync(eq, willAlarm, ct);
    }

    // 시나리오 타입별 초기 recipe 매핑 (eap-spec §5.3.1).
    // multi_equipment_4x.json에는 recipe 필드가 없으므로 scenario_type에서 유도한다.
    private static (string RecipeId, string Version) InitialRecipeFor(string scenarioType) => scenarioType switch
    {
        "RUN_NORMAL"    => ("Carsem_3X3", "v1.0"),
        "RUN_DEGRADED"  => ("Carsem_3X3", "v1.0"), // 이후 Carsem_4X6으로 전환됨
        "IDLE"          => ("ATC_1X1",    "v1.0"),
        "STOP_CRITICAL" => ("Carsem_3X3", "v1.0"),
        _               => ("ATC_1X1",    "v1.0"),
    };

    private static string ResolveScenarioPath(string configPath)
    {
        if (Path.IsPathRooted(configPath) && File.Exists(configPath)) return configPath;

        var baseDir = AppContext.BaseDirectory;
        var combined = Path.GetFullPath(Path.Combine(baseDir, configPath));
        if (File.Exists(combined)) return combined;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var name in new[] { "DS-Document", "ds-document" })
            {
                var c = Path.Combine(dir.FullName, name, "EAP_mock_data", "scenarios", "multi_equipment_4x.json");
                if (File.Exists(c)) return c;
            }
        }
        return combined;
    }
}
