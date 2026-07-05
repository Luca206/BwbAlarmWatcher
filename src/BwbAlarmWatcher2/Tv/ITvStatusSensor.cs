namespace BwbAlarmWatcher2.Tv;

public interface ITvStatusSensor
{
    /// <summary>
    /// True if the TV answers in the WLAN, meaning it is switched on. Never throws (except on
    /// cancellation); sensor failures are reported as "not reachable" so the caller fails safe
    /// towards switching the TV on.
    /// </summary>
    Task<bool> IsTvReachableAsync(CancellationToken cancellationToken);
}
