using BwbAlarmWatcher.Alarms;
using BwbAlarmWatcher.Configuration;
using BwbAlarmWatcher.Tv;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher;

public sealed partial class Worker(
    IAlarmSource alarmSource,
    ITvStatusSensor tvStatusSensor,
    ITvPowerActor tvPowerActor,
    TimeProvider timeProvider,
    IOptions<WorkerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    private IAlarmSource AlarmSource { get; } = alarmSource;
    private ITvStatusSensor TvStatusSensor { get; } = tvStatusSensor;
    private ITvPowerActor TvPowerActor { get; } = tvPowerActor;
    private TimeProvider TimeProvider { get; } = timeProvider;
    private ILogger<Worker> Logger { get; } = logger;
    private TimeSpan PollingInterval { get; } = TimeSpan.FromSeconds(options.Value.PollingIntervalInSec);
    private TimeSpan AutoOffAfter { get; } = TimeSpan.FromSeconds(options.Value.AutoOffAfterSec);

    internal TvControlState State { get; } = new();

    /// <summary>Alarms already reacted to; pruned once the API no longer reports them as active.</summary>
    internal HashSet<string> TrackedAlarmIds { get; } = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(Logger, (int)PollingInterval.TotalSeconds, (int)AutoOffAfter.TotalMinutes);

        await TryProcessOneCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(PollingInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TryProcessOneCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    internal async Task TryProcessOneCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProcessOneCycleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCycleFailed(Logger, ex);
        }
    }

    internal async Task ProcessOneCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var activeAlarms = await AlarmSource.GetActiveAlarmsAsync(cancellationToken);
            var newAlarms = MergeActiveAlarms(activeAlarms);
            if (newAlarms.Count > 0)
            {
                await HandleNewAlarmsAsync(newAlarms, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unreachable API must not stop the local auto-off timer below.
            LogPollFailed(Logger, ex);
        }

        await ProcessAutoOffAsync(cancellationToken);
    }

    /// <summary>Prunes alarms the API no longer reports and returns the ones not seen before.</summary>
    private List<ActiveAlarm> MergeActiveAlarms(IReadOnlyCollection<ActiveAlarm> activeAlarms)
    {
        var currentIds = new HashSet<string>(activeAlarms.Count, StringComparer.Ordinal);
        var newAlarms = new List<ActiveAlarm>();
        foreach (var alarm in activeAlarms)
        {
            currentIds.Add(alarm.Id);
            if (!TrackedAlarmIds.Contains(alarm.Id))
            {
                newAlarms.Add(alarm);
            }
        }

        TrackedAlarmIds.RemoveWhere(id => !currentIds.Contains(id));
        return newAlarms;
    }

    private async Task HandleNewAlarmsAsync(List<ActiveAlarm> newAlarms, CancellationToken cancellationToken)
    {
        foreach (var alarm in newAlarms)
        {
            LogNewAlarm(Logger, alarm.Id);
        }

        var now = TimeProvider.GetUtcNow();

        if (State.TurnedOnByService)
        {
            State.ExtendAutoOff(now, AutoOffAfter);
            LogAutoOffExtended(Logger, State.AutoOffAt!.Value);
            TrackAlarms(newAlarms);
            return;
        }

        if (await TvStatusSensor.IsTvReachableAsync(cancellationToken))
        {
            // TV was switched on manually: hands off, no auto-off timer (FA-6).
            LogTvManuallyOn(Logger);
            TrackAlarms(newAlarms);
            return;
        }

        if (await TvPowerActor.TurnOnAsync(cancellationToken))
        {
            State.MarkTurnedOnByService(now, AutoOffAfter);
            TrackAlarms(newAlarms);
            LogTvTurnedOn(Logger, State.AutoOffAt!.Value);
        }
        else
        {
            // Alarms stay untracked so the next cycle retries the power-on.
            LogTurnOnFailedWillRetry(Logger);
        }
    }

    private async Task ProcessAutoOffAsync(CancellationToken cancellationToken)
    {
        if (!State.IsAutoOffDue(TimeProvider.GetUtcNow()))
        {
            return;
        }

        if (await TvPowerActor.TurnOffAsync(cancellationToken))
        {
            State.Reset();
            LogTvTurnedOff(Logger);
        }
        else
        {
            // State stays armed so the next cycle retries the standby command.
            LogTurnOffFailedWillRetry(Logger);
        }
    }

    private void TrackAlarms(List<ActiveAlarm> alarms)
    {
        foreach (var alarm in alarms)
        {
            TrackedAlarmIds.Add(alarm.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Alarm watcher started (polling every {PollingIntervalSec}s, auto-off after {AutoOffMinutes}min)")]
    private static partial void LogStarted(ILogger logger, int pollingIntervalSec, int autoOffMinutes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cycle failed unexpectedly, loop continues")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alarm poll failed, retrying next cycle")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "New alarm detected: {AlarmId}")]
    private static partial void LogNewAlarm(ILogger logger, string alarmId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Further alarm while TV is on by service, auto-off extended to {AutoOffAt:u}")]
    private static partial void LogAutoOffExtended(ILogger logger, DateTimeOffset autoOffAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "TV is already on (manually switched on), leaving it alone")]
    private static partial void LogTvManuallyOn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "TV switched on, auto-off at {AutoOffAt:u}")]
    private static partial void LogTvTurnedOn(ILogger logger, DateTimeOffset autoOffAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switching the TV on failed, will retry next cycle")]
    private static partial void LogTurnOnFailedWillRetry(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Auto-off elapsed, TV switched to standby")]
    private static partial void LogTvTurnedOff(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Switching the TV off failed, will retry next cycle")]
    private static partial void LogTurnOffFailedWillRetry(ILogger logger);
}
