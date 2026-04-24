using System.Diagnostics;

namespace DsEap.Equipment;

// 단일 가상 장비의 런타임 상태 — 상태 머신 + LOT 진행 카운터
public sealed class VirtualEquipment
{
    private readonly Stopwatch _uptimeSw = Stopwatch.StartNew();
    private readonly object _gate = new();

    public string EquipmentId { get; }

    public EquipmentState State { get; private set; } = EquipmentState.Idle;
    public string? LotId { get; private set; }
    public string RecipeId { get; private set; } = "";
    public string RecipeVersion { get; private set; } = "";
    public string OperatorId { get; private set; } = "";

    public int CurrentUnitCount { get; private set; }
    public int ExpectedTotalUnits { get; private set; }
    public int PassCount { get; private set; }
    public int FailCount { get; private set; }

    public DateTime? LotStartUtc { get; private set; }

    public VirtualEquipment(string equipmentId, string recipeId, string recipeVersion, string operatorId)
    {
        EquipmentId = equipmentId;
        RecipeId = recipeId;
        RecipeVersion = recipeVersion;
        OperatorId = operatorId;
    }

    public long UptimeSec => _uptimeSw.Elapsed.Ticks / TimeSpan.TicksPerSecond;

    public double CurrentYieldPct
    {
        get
        {
            lock (_gate)
            {
                return CurrentUnitCount == 0 ? 0.0 : Math.Round(PassCount * 100.0 / CurrentUnitCount, 1);
            }
        }
    }

    public void ChangeRecipe(string newRecipeId, string newRecipeVersion)
    {
        lock (_gate)
        {
            RecipeId = newRecipeId;
            RecipeVersion = newRecipeVersion;
        }
    }

    // API §3 STATUS_UPDATE.lot_id = "현재(또는 마지막) Lot ID" — IDLE 시나리오용 직전 LOT 시드
    public void SeedPriorLot(string lotId)
    {
        lock (_gate) LotId = lotId;
    }

    public void StartLot(string lotId, int expectedTotalUnits)
    {
        lock (_gate)
        {
            LotId = lotId;
            ExpectedTotalUnits = expectedTotalUnits;
            CurrentUnitCount = 0;
            PassCount = 0;
            FailCount = 0;
            LotStartUtc = DateTime.UtcNow;
            State = EquipmentState.Run;
        }
    }

    public void RecordInspection(bool pass)
    {
        lock (_gate)
        {
            CurrentUnitCount++;
            if (pass) PassCount++; else FailCount++;
        }
    }

    public (int totalUnits, int passCount, int failCount, double yieldPct, long durationSec) FinalizeLot()
    {
        lock (_gate)
        {
            var durationSec = LotStartUtc.HasValue
                ? (long)Math.Max(0, (DateTime.UtcNow - LotStartUtc.Value).TotalSeconds)
                : 0;
            var yieldPct = CurrentUnitCount == 0
                ? 0.0
                : Math.Round(PassCount * 100.0 / CurrentUnitCount, 1);
            var total = CurrentUnitCount;
            var pass = PassCount;
            var fail = FailCount;
            State = EquipmentState.Idle;
            return (total, pass, fail, yieldPct, durationSec);
        }
    }

    public void TransitionToStop()
    {
        lock (_gate) State = EquipmentState.Stop;
    }

    public (int Strip, int Unit) CurrentStripAndUnit()
    {
        lock (_gate)
        {
            // 8슬롯/Strip 가정 (Carsem_3X3: 349 Strip × 8)
            var unit = CurrentUnitCount + 1;
            var strip = ((unit - 1) / 8) + 1;
            return (strip, unit);
        }
    }
}
