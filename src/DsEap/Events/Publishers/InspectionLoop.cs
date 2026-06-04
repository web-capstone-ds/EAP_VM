using DsEap.Configuration;
using DsEap.Equipment;
using DsEap.Events.Models;
using DsEap.MockData;
using Microsoft.Extensions.Logging;

namespace DsEap.Events.Publishers;

// takt 1,620ms 주기 INSPECTION_RESULT — PASS 96.2% / FAIL 3.8% (Mock 04/05/06/07/08)
public sealed class InspectionLoop
{
    private readonly EventPublisher _publisher;
    private readonly MockDataLoader _mocks;
    private readonly TimingSettings _timing;
    private readonly GeometricJitterSettings _jitter;
    private readonly ILogger<InspectionLoop> _log;
    private readonly Random _rand = new();

    private static readonly string[] FailMockStems =
    {
        "05_inspection_fail_side_et52",
        "06_inspection_fail_side_et12",
        "07_inspection_fail_prs_offset",
        "08_inspection_fail_side_mixed",
    };

    public InspectionLoop(
        EventPublisher publisher,
        MockDataLoader mocks,
        TimingSettings timing,
        GeometricJitterSettings jitter,
        ILogger<InspectionLoop> log)
    {
        _publisher = publisher;
        _mocks = mocks;
        _timing = timing;
        _jitter = jitter;
        _log = log;
    }

    // maxUnits 단위만큼 INSPECTION_RESULT 발행 후 LOT 완료 카운트 달성 → 호출자에게 반환
    public async Task RunLotAsync(VirtualEquipment eq, int maxUnits, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_timing.TaktTimeMs));
        try
        {
            while (!ct.IsCancellationRequested && eq.State == EquipmentState.Run && eq.CurrentUnitCount < maxUnits)
            {
                var pass = _rand.NextDouble() < _timing.PassRatio;
                var stem = pass ? "04_inspection_pass" : FailMockStems[_rand.Next(FailMockStems.Length)];

                // 매 takt마다 새 DTO를 역직렬화 (Mock 원본 데이터 불변 보장)
                var payload = _mocks.Get<InspectionResultPayload>(stem);
                var (stripNo, unitNo) = eq.CurrentStripAndUnit();
                MockPayloadTransformer.OverrideInspection(
                    payload,
                    equipmentId:  eq.EquipmentId,
                    lotId:        eq.LotId ?? "",
                    stripId:      $"STRIP-{stripNo:D3}",
                    unitId:       $"UNIT-{unitNo:D4}",
                    recipeId:     eq.RecipeId,
                    recipeVersion:eq.RecipeVersion,
                    operatorId:   eq.OperatorId,
                    equipmentStatus: eq.State.ToWire());

                // Cpk 검증용: geometric에 측정 산포 부여 (옵트인, 기본 off)
                MockPayloadTransformer.ApplyGeometricJitter(payload, _jitter, _rand);

                if (eq.State != EquipmentState.Run) return;
                await _publisher.PublishInspectionAsync(eq, payload, ct);
                if (!eq.TryRecordInspection(pass)) return;

                try { await timer.WaitForNextTickAsync(ct); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Inspection loop failed for {Eq}", eq.EquipmentId);
        }
    }
}
