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
                case "DISK_FULL":     await DriveDiskFull(eq, ct); break;
                case "LIGHT_DEGRADE": await DriveLightDegrade(eq, ct); break;
                case "LOT_MISSING":   await DriveLotMissing(eq, ct); break;
                case "EAP_CRASH_BURST": await DriveEapCrashBurst(eq, ct); break;
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
            if (ct.IsCancellationRequested || eq.State != EquipmentState.Run) return;

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
        for (int i = 0; i < 10 && !ct.IsCancellationRequested && eq.State == EquipmentState.Run; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("05_inspection_fail_side_et52");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            if (!eq.TryRecordInspection(pass: false)) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (eq.State != EquipmentState.Run) return;

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
        // 시나리오 의미: "직전 LOT 정상 완료 후 대기". API §3 lot_id 필수 필드 준수 위해 직전 LOT ID 시드.
        eq.SeedPriorLot($"LOT-{DateTime.UtcNow:yyyyMMdd}-PRV{Random.Shared.Next(1, 1000):D3}");
        await _publisher.PublishStatusAsync(eq, ct);
    }

    private async Task DriveStopCritical(VirtualEquipment eq, CancellationToken ct)
    {
        // eap-spec §9.2 "카메라 타임아웃" 시퀀스 — ET=30 연속 3회 → CAM_TIMEOUT_ERR → STOP
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-CAM{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        for (int i = 0; i < 3 && !ct.IsCancellationRequested && eq.State == EquipmentState.Run; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("07_inspection_fail_prs_offset");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            MockPayloadTransformer.OverrideSideErrorType(payload, errorType: 30);
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            if (!eq.TryRecordInspection(pass: false)) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (eq.State != EquipmentState.Run) return;

        // 3회 ET=30 누적 → CAM_TIMEOUT_ERR (CRITICAL): 양산 중이던 LOT를 강제 중단한다.
        // eap-spec §5.1 lot_status=ABORTED — DISK_FULL/EMERGENCY_STOP과 동일하게 RUN 중 중단 시
        // LOT_END(ABORTED)를 먼저 발행한다(검사 unit FAIL과 별개로, 끝난 LOT은 ABORTED로 집계).
        if (eq.State == EquipmentState.Run)
        {
            await _publisher.PublishLotEndAsync(eq, "ABORTED", ct);
            eq.FinalizeLot();
        }
        // CRITICAL 알람으로 STOP 전환 (RED 타일 유지) + CAM_TIMEOUT_ERR (Mock 11) retained CRITICAL
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

    // eap-spec §5.2 디스크 포화 — WRITE_FAIL × 20 → fps 저하 → LOT_END(ABORTED)
    // Mock 12: WRITE_FAIL (CRITICAL, HALCON #3142)
    private async Task DriveDiskFull(VirtualEquipment eq, CancellationToken ct)
    {
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-DSK{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        // 정상 검사 5회 → 디스크 포화 발생
        for (int i = 0; i < 5 && !ct.IsCancellationRequested && eq.State == EquipmentState.Run; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("04_inspection_pass");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            if (!eq.TryRecordInspection(pass: true)) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (eq.State != EquipmentState.Run) return;

        // HW_ALARM: WRITE_FAIL (Mock 12) — CRITICAL → STOP
        eq.TransitionToStop();
        var alarm = _mocks.Get<HwAlarmPayload>("12_alarm_write_image_fail");
        await _publisher.PublishHwAlarmAsync(eq, alarm, ct);
        await _publisher.PublishStatusAsync(eq, ct);

        // LOT_END(ABORTED) — 디스크 포화로 양산 중단
        await _publisher.PublishLotEndAsync(eq, "ABORTED", ct);
        eq.FinalizeLot();
        await _publisher.PublishStatusAsync(eq, ct);
    }

    // eap-spec §5.2 조명 열화 — LIGHT_PWR_LOW → SIDE Pass율 점진 하락
    // Mock 14: LIGHT_PWR_LOW (WARNING, auto_recovery_attempted=true)
    private async Task DriveLightDegrade(VirtualEquipment eq, CancellationToken ct)
    {
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-LIT{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        // 정상 검사 3회
        for (int i = 0; i < 3 && !ct.IsCancellationRequested && eq.State == EquipmentState.Run; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("04_inspection_pass");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            if (!eq.TryRecordInspection(pass: true)) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (eq.State != EquipmentState.Run) return;

        // HW_ALARM: LIGHT_PWR_LOW (Mock 14) — WARNING, auto_recovery=true
        // auto_recovery=true이므로 EventPublisher가 자동 clear 트리거 (§4.5 Trigger 1)
        var alarm = _mocks.Get<HwAlarmPayload>("14_alarm_light_param_err");
        await _publisher.PublishHwAlarmAsync(eq, alarm, ct);

        // 조명 열화 후 FAIL 비율 증가 — ET=12 혼재 검사
        for (int i = 0; i < 5 && !ct.IsCancellationRequested && eq.State == EquipmentState.Run; i++)
        {
            var failPayload = _mocks.Get<InspectionResultPayload>("06_inspection_fail_side_et12");
            var (s, u) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                failPayload, eq.EquipmentId, lotId, $"STRIP-{s:D3}", $"UNIT-{u:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            await _publisher.PublishInspectionAsync(eq, failPayload, ct);
            if (!eq.TryRecordInspection(pass: false)) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (eq.State != EquipmentState.Run) return;

        // ORACLE_ANALYSIS: WARNING
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var oracle = _mocks.Get<OracleAnalysisPayload>("24_oracle_warning");
        MockPayloadTransformer.OverrideOracle(oracle, eq.EquipmentId, lotId, eq.RecipeId);
        await _publisher.PublishOracleAsync(eq, oracle, ct);
    }

    // eap-spec §5.2 LOT_END 누락 — LOT Start 후 24,000s 초과 시뮬레이션
    // Mock 16: VISION_SCORE_ERR (AggregateException, LotController.StartNewLot)
    private async Task DriveLotMissing(VirtualEquipment eq, CancellationToken ct)
    {
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-MIS{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        // 정상 검사 3회 후 LOT_END가 나오지 않는 상태 시뮬레이션
        for (int i = 0; i < 3 && !ct.IsCancellationRequested && eq.State == EquipmentState.Run; i++)
        {
            var payload = _mocks.Get<InspectionResultPayload>("04_inspection_pass");
            var (stripNo, unitNo) = eq.CurrentStripAndUnit();
            MockPayloadTransformer.OverrideInspection(
                payload, eq.EquipmentId, lotId, $"STRIP-{stripNo:D3}", $"UNIT-{unitNo:D4}",
                eq.RecipeId, eq.RecipeVersion, eq.OperatorId, eq.State.ToWire());
            await _publisher.PublishInspectionAsync(eq, payload, ct);
            if (!eq.TryRecordInspection(pass: true)) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(_settings.Timing.TaktTimeMs), ct); }
            catch (OperationCanceledException) { return; }
        }
        if (eq.State != EquipmentState.Run) return;

        // HW_ALARM: VISION_SCORE_ERR (LotController AggregateException) — Mock 16
        // LOT_END 미발행 상태에서 알람 발행
        var alarm = _mocks.Get<HwAlarmPayload>("16_alarm_lot_start_fail");
        await _publisher.PublishHwAlarmAsync(eq, alarm, ct);

        // ORACLE_ANALYSIS: DANGER — LOT_END 누락으로 수율 판정 불가
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var oracle = _mocks.Get<OracleAnalysisPayload>("25_oracle_danger");
        MockPayloadTransformer.OverrideOracle(oracle, eq.EquipmentId, lotId, eq.RecipeId);
        await _publisher.PublishOracleAsync(eq, oracle, ct);
    }

    // eap-spec §5.2 EAP 크래시 — AggregateException burst 41건 → Heartbeat 중단 → Will
    // Mock 16: VISION_SCORE_ERR (burst) + Mock 17: EAP_DISCONNECTED (Will)
    private async Task DriveEapCrashBurst(VirtualEquipment eq, CancellationToken ct)
    {
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-BUR{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, ct);

        // burst_id를 공유하는 연속 알람 발행 (실측: 41건, 시뮬레이션은 5건으로 단축)
        var burstId = Guid.NewGuid().ToString();
        var burstCount = 5;
        for (int i = 1; i <= burstCount && !ct.IsCancellationRequested; i++)
        {
            var alarm = _mocks.Get<HwAlarmPayload>("16_alarm_lot_start_fail");
            alarm.BurstId = burstId;
            alarm.BurstCount = i;
            await _publisher.PublishHwAlarmAsync(eq, alarm, ct);
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); }
            catch (OperationCanceledException) { return; }
        }

        // AggregateException 누적 후 EAP 프로세스 크래시 시뮬레이션
        // Will 메시지는 Broker가 발행하지만, 시뮬레이션에서는 수동으로 발행
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (OperationCanceledException) { return; }
        eq.TransitionToStop();
        var willAlarm = _mocks.Get<HwAlarmPayload>("17_alarm_eap_disconnected");
        await _publisher.PublishHwAlarmAsync(eq, willAlarm, ct);
        _log.LogInformation("EAP crash burst simulated: {Eq} burst_id={BurstId} count={Count}",
            eq.EquipmentId, burstId, burstCount);
    }

    // 시나리오 타입별 초기 recipe 매핑 (eap-spec §5.3.1).
    // multi_equipment_4x.json에는 recipe 필드가 없으므로 scenario_type에서 유도한다.
    private static (string RecipeId, string Version) InitialRecipeFor(string scenarioType) => scenarioType switch
    {
        "RUN_NORMAL"      => ("Carsem_3X3", "v1.0"),
        "RUN_DEGRADED"    => ("Carsem_3X3", "v1.0"), // 이후 Carsem_4X6으로 전환됨
        "IDLE"            => ("ATC_1X1",    "v1.0"),
        "STOP_CRITICAL"   => ("Carsem_3X3", "v1.0"),
        "DISK_FULL"       => ("Carsem_3X3", "v1.0"),
        "LIGHT_DEGRADE"   => ("Carsem_3X3", "v1.0"),
        "LOT_MISSING"     => ("Carsem_3X3", "v1.0"),
        "EAP_CRASH_BURST" => ("Carsem_3X3", "v1.0"),
        _                 => ("ATC_1X1",    "v1.0"),
    };

    private static string ResolveScenarioPath(string configPath)
    {
        // 절대경로 — 그대로 반환
        if (Path.IsPathRooted(configPath) && File.Exists(configPath)) return configPath;

        // 현재 작업 디렉토리 기준 상대경로 체크
        var cwdResolved = Path.GetFullPath(configPath);
        if (File.Exists(cwdResolved)) return cwdResolved;

        // 실행 바이너리 디렉토리 기준 상대경로
        var baseDir = AppContext.BaseDirectory;
        var combined = Path.GetFullPath(Path.Combine(baseDir, configPath));
        if (File.Exists(combined)) return combined;

        // fallback: 시나리오 파일명만 추출해서 DS-Document 디렉토리 탐색
        var fileName = Path.GetFileName(configPath);
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var name in new[] { "DS-Document", "ds-document" })
            {
                var c = Path.Combine(dir.FullName, name, "EAP_mock_data", "scenarios", fileName);
                if (File.Exists(c)) return c;
            }
        }
        return combined;
    }
}
