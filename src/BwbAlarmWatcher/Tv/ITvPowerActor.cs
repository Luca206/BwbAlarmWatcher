namespace BwbAlarmWatcher.Tv;

public interface ITvPowerActor
{
    /// <summary>Switches the TV on. Returns false on failure instead of throwing (except on cancellation).</summary>
    Task<bool> TurnOnAsync(CancellationToken cancellationToken);

    /// <summary>Puts the TV into standby. Returns false on failure instead of throwing (except on cancellation).</summary>
    Task<bool> TurnOffAsync(CancellationToken cancellationToken);
}
