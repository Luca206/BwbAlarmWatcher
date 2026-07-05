namespace BwbAlarmWatcher.Alarms;

/// <summary>An alarm that is currently active. Only the identity is kept (data minimisation requirement).</summary>
public sealed record ActiveAlarm(string Id);

public interface IAlarmSource
{
    /// <summary>
    /// Fetches the alarms that are currently active. Throws on transport or protocol errors;
    /// the caller decides how to keep the control loop alive.
    /// </summary>
    Task<IReadOnlyCollection<ActiveAlarm>> GetActiveAlarmsAsync(CancellationToken cancellationToken);
}
