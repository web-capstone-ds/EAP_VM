using DsEap.Configuration;
using DsEap.Equipment;
using DsEap.Events.Publishers;
using DsEap.MockData;
using DsEap.Mqtt;
using DsEap.Scenarios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

builder.Services.Configure<EapSettings>(builder.Configuration.GetSection("Eap"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EapSettings>>().Value.Timing);

builder.Services.AddSingleton<MqttClientManager>();
builder.Services.AddSingleton<AlarmTracker>();
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddSingleton<HeartbeatLoop>();
builder.Services.AddSingleton<StatusLoop>();
builder.Services.AddSingleton<InspectionLoop>();
builder.Services.AddSingleton<MockDataLoader>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<EapSettings>>().Value;
    var log  = sp.GetRequiredService<ILogger<MockDataLoader>>();
    return new MockDataLoader(opts.Paths.MockDataDir, log);
});
builder.Services.AddSingleton<EquipmentManager>();
builder.Services.AddSingleton<ScenarioRunner>();
builder.Services.AddSingleton<ControlCommandHandler>();
builder.Services.AddSingleton<MqttSubscriber>();
builder.Services.AddHostedService<EapHostedService>();

using var host = builder.Build();
await host.RunAsync();

internal sealed class EapHostedService : IHostedService
{
    private readonly MqttClientManager _mqtt;
    private readonly MqttSubscriber _subscriber;
    private readonly EquipmentManager _equipment;
    private readonly ScenarioRunner _scenarioRunner;
    private readonly EapSettings _settings;
    private readonly ILogger<EapHostedService> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenSource? _cts;
    private Task? _runnerTask;

    public EapHostedService(
        MqttClientManager mqtt,
        MqttSubscriber subscriber,
        EquipmentManager equipment,
        ScenarioRunner scenarioRunner,
        IOptions<EapSettings> settings,
        ILogger<EapHostedService> log,
        IHostApplicationLifetime lifetime)
    {
        _mqtt = mqtt;
        _subscriber = subscriber;
        _equipment = equipment;
        _scenarioRunner = scenarioRunner;
        _settings = settings.Value;
        _log = log;
        _lifetime = lifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _log.LogInformation("DS Virtual EAP starting (RunMode={Mode})", _settings.RunMode);

        await _mqtt.StartAsync(_cts.Token);

        _runnerTask = Task.Run(async () =>
        {
            try
            {
                // 첫 MQTT 연결 완료를 기다린 후 실행 (간단히 2초 지연)
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);

                if (string.Equals(_settings.RunMode, "GoldenPath", StringComparison.OrdinalIgnoreCase))
                    await _equipment.RunGoldenPathAsync(_cts.Token);
                else if (string.Equals(_settings.RunMode, "Scenario", StringComparison.OrdinalIgnoreCase))
                    await _scenarioRunner.RunAsync(_cts.Token);
                else
                    _log.LogWarning("Unknown RunMode={Mode}", _settings.RunMode);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogError(ex, "Runner failed"); }
        }, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("DS Virtual EAP shutting down");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(_settings.Timing.ShutdownTimeoutMs));
            await _equipment.GracefulShutdownAsync(timeout.Token);
            await _mqtt.DisconnectGracefulAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Shutdown failure");
        }
        finally
        {
            await _mqtt.DisposeAsync();
            _cts?.Cancel();
            _cts?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
