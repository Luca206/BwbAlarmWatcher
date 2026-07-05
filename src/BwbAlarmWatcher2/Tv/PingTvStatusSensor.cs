using System.Net;
using System.Net.NetworkInformation;
using BwbAlarmWatcher2.Configuration;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher2.Tv;

public sealed partial class PingTvStatusSensor(
    IOptions<TvOptions> options,
    TimeProvider timeProvider,
    ILogger<PingTvStatusSensor> logger) : ITvStatusSensor
{
    private TvOptions Options { get; } = options.Value;
    private IPAddress TvIp { get; } = IPAddress.Parse(options.Value.IpAddress);
    private TimeProvider TimeProvider { get; } = timeProvider;
    private ILogger<PingTvStatusSensor> Logger { get; } = logger;

    public async Task<bool> IsTvReachableAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Options.PingAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(
                    TvIp,
                    TimeSpan.FromMilliseconds(Options.PingTimeoutMs),
                    cancellationToken: cancellationToken);

                if (reply.Status == IPStatus.Success)
                {
                    LogTvReachable(Logger, attempt);
                    return true;
                }

                LogPingAttemptFailed(Logger, attempt, Options.PingAttempts, reply.Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogPingAttemptError(Logger, ex, attempt, Options.PingAttempts);
            }

            if (attempt < Options.PingAttempts && Options.PingRetryDelayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Options.PingRetryDelayMs), TimeProvider, cancellationToken);
            }
        }

        LogTvNotReachable(Logger, Options.PingAttempts);
        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "TV reachable in WLAN (attempt {Attempt})")]
    private static partial void LogTvReachable(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ping attempt {Attempt}/{MaxAttempts} failed with status {Status}")]
    private static partial void LogPingAttemptFailed(ILogger logger, int attempt, int maxAttempts, IPStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ping attempt {Attempt}/{MaxAttempts} threw")]
    private static partial void LogPingAttemptError(ILogger logger, Exception exception, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TV not reachable after {Attempts} ping attempts, treating as off")]
    private static partial void LogTvNotReachable(ILogger logger, int attempts);
}
