using DsEap.Configuration;
using DsEap.Equipment;
using Microsoft.Extensions.Logging;

namespace DsEap.Events.Publishers;

// 6초 주기 STATUS_UPDATE — Retained=true, 진행률 3필드 갱신 (§3.1)
public sealed class StatusLoop
{
    private readonly EventPublisher _publisher;
    private readonly AlarmTracker _alarmTracker;
    private readonly TimingSettings _timing;
    private readonly ILogger<StatusLoop> _log;

    public StatusLoop(
        EventPublisher publisher,
        AlarmTracker alarmTracker,
        TimingSettings timing,
        ILogger<StatusLoop> log)
    {
        _publisher = publisher;
        _alarmTracker = alarmTracker;
        _timing = timing;
        _log = log;
    }

    public async Task RunAsync(VirtualEquipment eq, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_timing.StatusIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _publisher.PublishStatusAsync(eq, ct);

                // Trigger 2: 정상 STATUS 연속 시 auto-ACK
                if (_alarmTracker.ShouldAutoAckOnStatus(eq.EquipmentId, eq.State))
                {
                    await _publisher.ClearAlarmRetainedAsync(eq, ct);
                    _alarmTracker.ClearAlarm(eq.EquipmentId);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Status loop failed for {Eq}", eq.EquipmentId);
        }
    }
}
