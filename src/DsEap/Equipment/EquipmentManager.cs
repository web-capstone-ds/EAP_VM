using DsEap.Configuration;
using DsEap.Events.Models;
using DsEap.Events.Publishers;
using DsEap.MockData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DsEap.Equipment;

// 단일 장비 Golden Path 오케스트레이션 (§6.2)
// IDLE → RECIPE_CHANGED → STATUS(RUN) → INSPECTION × N → LOT_END(COMPLETED) → STATUS(IDLE) → ORACLE
public sealed class EquipmentManager
{
    private readonly EapSettings _settings;
    private readonly EventPublisher _publisher;
    private readonly HeartbeatLoop _heartbeat;
    private readonly StatusLoop _status;
    private readonly InspectionLoop _inspection;
    private readonly MockDataLoader _mocks;
    private readonly ILogger<EquipmentManager> _log;

    private readonly Dictionary<string, VirtualEquipment> _equipments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _bgTasks = new();
    private VirtualEquipment? _equipment;
    private Task? _heartbeatTask;
    private Task? _statusTask;
    private CancellationTokenSource? _loopCts;

    public VirtualEquipment? Current => _equipment;

    public VirtualEquipment? Find(string equipmentId) =>
        _equipments.TryGetValue(equipmentId, out var eq) ? eq : null;

    public IReadOnlyCollection<VirtualEquipment> All => _equipments.Values;

    public void Register(VirtualEquipment eq) => _equipments[eq.EquipmentId] = eq;

    public EquipmentManager(
        IOptions<EapSettings> settings,
        EventPublisher publisher,
        HeartbeatLoop heartbeat,
        StatusLoop status,
        InspectionLoop inspection,
        MockDataLoader mocks,
        ILogger<EquipmentManager> log)
    {
        _settings = settings.Value;
        _publisher = publisher;
        _heartbeat = heartbeat;
        _status = status;
        _inspection = inspection;
        _mocks = mocks;
        _log = log;
    }

    public async Task RunGoldenPathAsync(CancellationToken ct)
    {
        var cfg = _settings.GoldenPath;
        var eq = new VirtualEquipment(cfg.EquipmentId, cfg.RecipeId, cfg.RecipeVersion, cfg.OperatorId);
        _equipment = eq;
        Register(eq);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loopCt = _loopCts.Token;

        // Heartbeat + Status 타이머 시작 — 장비 전체 수명 동안 유지
        _heartbeatTask = Task.Run(() => _heartbeat.RunAsync(eq, loopCt), CancellationToken.None);
        _statusTask    = Task.Run(() => _status.RunAsync(eq, loopCt), CancellationToken.None);

        _log.LogInformation("Golden Path start: {Eq} recipe={Recipe}", eq.EquipmentId, cfg.RecipeId);

        // Step 1. RECIPE_CHANGED (ATC_1X1 → Carsem_3X3 가정)
        await Task.Delay(TimeSpan.FromSeconds(1), loopCt);
        await _publisher.PublishRecipeChangedAsync(eq, "ATC_1X1", "v1.0", cfg.RecipeId, cfg.RecipeVersion, loopCt);

        // Step 2. LOT 시작 → STATUS(RUN)
        var lotId = $"LOT-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1, 1000):D3}";
        eq.StartLot(lotId, _settings.Timing.ExpectedTotalUnits);
        await _publisher.PublishStatusAsync(eq, loopCt);
        _log.LogInformation("LOT start: {Lot} target={Target}", lotId, _settings.Timing.ExpectedTotalUnits);

        // Step 3. INSPECTION_RESULT × N (GoldenPathMaxUnits로 빠른 시뮬레이션)
        var maxUnits = _settings.Timing.GoldenPathMaxUnits;
        await _inspection.RunLotAsync(eq, maxUnits, loopCt);

        if (loopCt.IsCancellationRequested) return;

        // Step 4. LOT_END(COMPLETED)
        await _publisher.PublishLotEndAsync(eq, "COMPLETED", loopCt);
        var (total, pass, fail, yieldPct, _) = eq.FinalizeLot();
        _log.LogInformation("LOT end: {Lot} total={Total} pass={Pass} fail={Fail} yield={Yield}%",
            lotId, total, pass, fail, yieldPct);

        // Step 5. STATUS(IDLE)
        await _publisher.PublishStatusAsync(eq, loopCt);

        // Step 6. ORACLE_ANALYSIS (Mock 23, 비동기 지연 발행)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), loopCt);
                var oracle = _mocks.Get<OracleAnalysisPayload>("23_oracle_normal");
                MockPayloadTransformer.OverrideOracle(oracle, eq.EquipmentId, lotId, eq.RecipeId);
                await _publisher.PublishOracleAsync(eq, oracle, loopCt);
                _log.LogInformation("ORACLE_ANALYSIS published for {Lot}", lotId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Oracle publish failed"); }
        }, CancellationToken.None);
    }

    // §1.2.5 Graceful Shutdown 시퀀스
    //   ① equipment_status == RUN인 모든 장비: LOT_END(ABORTED) → STATUS(IDLE)
    //   ② Heartbeat/Inspection 타이머 중지
    //   ③ MqttClient.DisconnectAsync() 호출 (EapHostedService가 담당)
    //   ④ Will 메시지 발동 안 함 (Broker가 정상 DISCONNECT 수신)
    public async Task GracefulShutdownAsync(CancellationToken ct)
    {
        if (_equipments.Count == 0) return;

        _log.LogInformation("Graceful shutdown: processing {Count} equipment(s)", _equipments.Count);

        foreach (var eq in _equipments.Values)
        {
            try
            {
                if (eq.State == EquipmentState.Run)
                {
                    _log.LogInformation("  {Eq}: LOT_END(ABORTED) → STATUS(IDLE)", eq.EquipmentId);
                    await _publisher.PublishLotEndAsync(eq, "ABORTED", ct);
                    eq.FinalizeLot();
                    await _publisher.PublishStatusAsync(eq, ct);
                }
                else
                {
                    _log.LogInformation("  {Eq}: already {State}, skip LOT_END", eq.EquipmentId, eq.State);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Graceful shutdown publish failed for {Eq}", eq.EquipmentId);
            }
        }

        try { _loopCts?.Cancel(); } catch { }
        if (_heartbeatTask is not null) { try { await _heartbeatTask; } catch { } }
        if (_statusTask    is not null) { try { await _statusTask; } catch { } }
        _loopCts?.Dispose();
    }
}
