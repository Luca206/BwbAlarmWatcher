using System.Diagnostics;
using BwbAlarmWatcher.Configuration;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher.Tv;

/// <summary>
/// Switches the TV via HDMI-CEC using one-shot cec-client invocations. Unlike v1 there is no
/// persistent cec-client session: the power status now comes from the ping sensor, so CEC is only
/// used for the rare on/standby commands and a fresh process per command avoids all long-lived
/// process state (v1 needed auto-restart handling for exactly that).
/// </summary>
public sealed partial class CecTvPowerActor(
    IOptions<TvOptions> options,
    ILogger<CecTvPowerActor> logger) : ITvPowerActor
{
    private TvOptions Options { get; } = options.Value;
    private ILogger<CecTvPowerActor> Logger { get; } = logger;

    public async Task<bool> TurnOnAsync(CancellationToken cancellationToken)
    {
        var success = await SendCecCommandAsync($"on {Options.CecTvAddress}", cancellationToken);
        if (success && Options.CecSetActiveSourceAfterOn)
        {
            // Best effort: even if switching the input fails, the TV is on, which is the critical part.
            await SendCecCommandAsync("as", cancellationToken);
        }

        return success;
    }

    public Task<bool> TurnOffAsync(CancellationToken cancellationToken)
        => SendCecCommandAsync($"standby {Options.CecTvAddress}", cancellationToken);

    internal async Task<bool> SendCecCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Options.CecClientPath,
                ArgumentList = { "-s", "-d", "1" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            // Drain both pipes concurrently so a chatty cec-client can never dead-lock on a full pipe.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Options.CecCommandTimeoutSec));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                LogCecTimeout(Logger, command, Options.CecCommandTimeoutSec);
                return false;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                LogCecNonZeroExit(Logger, command, process.ExitCode, stderr.Trim());
                return false;
            }

            LogCecCommandSent(Logger, command);
            LogCecOutput(Logger, stdout.Trim());
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCecFailed(Logger, ex, command);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CEC command '{Command}' sent")]
    private static partial void LogCecCommandSent(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "cec-client output: {Output}")]
    private static partial void LogCecOutput(ILogger logger, string output);

    [LoggerMessage(Level = LogLevel.Error, Message = "CEC command '{Command}' timed out after {TimeoutSec}s, killed cec-client")]
    private static partial void LogCecTimeout(ILogger logger, string command, int timeoutSec);

    [LoggerMessage(Level = LogLevel.Error, Message = "CEC command '{Command}' exited with code {ExitCode}: {Stderr}")]
    private static partial void LogCecNonZeroExit(ILogger logger, string command, int exitCode, string stderr);

    [LoggerMessage(Level = LogLevel.Error, Message = "CEC command '{Command}' failed")]
    private static partial void LogCecFailed(ILogger logger, Exception exception, string command);
}
