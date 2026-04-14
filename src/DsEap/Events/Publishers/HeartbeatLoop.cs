using DsEap.Configuration;
using DsEap.Equipment;
using Microsoft.Extensions.Logging;

namespace DsEap.Events.Publishers;

// 3초 주기 HEARTBEAT — 장비 상태 무관 항상 발행 (§1.4)
public sealed class HeartbeatLoop
{
    private readonly EventPublisher _publisher;
    private readonly TimingSettings _timing;
    private readonly ILogger<HeartbeatLoop> _log;

    public HeartbeatLoop(EventPublisher publisher, TimingSettings timing, ILogger<HeartbeatLoop> log)
    {
        _publisher = publisher;
        _timing = timing;
        _log = log;
    }

    public async Task RunAsync(VirtualEquipment eq, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_timing.HeartbeatIntervalMs));
        try
        {
            // 즉시 첫 Heartbeat 1회
            await _publisher.PublishHeartbeatAsync(eq, ct);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _publisher.PublishHeartbeatAsync(eq, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Heartbeat loop failed for {Eq}", eq.EquipmentId);
        }
    }
}
