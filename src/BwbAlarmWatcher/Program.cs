using System.Net;
using BwbAlarmWatcher;
using BwbAlarmWatcher.Alarms;
using BwbAlarmWatcher.Configuration;
using BwbAlarmWatcher.Tv;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Type=notify readiness plus systemd watchdog pings when WatchdogSec is set in the unit.
builder.Services.AddSystemd();

// IL2026/IL3050 on Bind are false positives here: EnableConfigurationBindingGenerator replaces
// these calls with source-generated, AOT-safe binding (verified: trimmed publish emits no warnings).
#pragma warning disable IL2026, IL3050
builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WorkerOptions>, WorkerOptionsValidator>();

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();

builder.Services.AddOptions<TvOptions>()
    .Bind(builder.Configuration.GetSection(TvOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TvOptions>, TvOptionsValidator>();

builder.Services.AddOptions<AlarmFilterOptions>()
    .Bind(builder.Configuration.GetSection(AlarmFilterOptions.SectionName))
    .ValidateOnStart();
#pragma warning restore IL2026, IL3050

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITvStatusSensor, PingTvStatusSensor>();
builder.Services.AddSingleton<ITvPowerActor, CecTvPowerActor>();

builder.Services.AddHttpClient<IAlarmSource, BwbAlarmApiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // The client instance lives as long as the worker; rotate pooled connections so DNS changes are picked up.
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        AutomaticDecompression = DecompressionMethods.All,
    })
    .AddStandardResilienceHandler(resilience =>
    {
        // Budget stays below the 15 s polling interval: 5 s attempt + one retry.
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        resilience.Retry.MaxRetryAttempts = 1;
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(14);
    });

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
