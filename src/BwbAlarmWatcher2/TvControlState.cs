namespace BwbAlarmWatcher2;

/// <summary>
/// Tracks whether this service switched the TV on. Only then may it switch the TV off again;
/// a manually switched-on TV must never be touched (FA-6). The flag is deliberately evaluated
/// only when a new alarm arrives — re-probing the ping sensor every cycle would see the TV the
/// service just switched on and misclassify it as manually on (the v1 "TurnedOnByService" lesson).
/// </summary>
public sealed class TvControlState
{
    public bool TurnedOnByService { get; private set; }

    public DateTimeOffset? AutoOffAt { get; private set; }

    public void MarkTurnedOnByService(DateTimeOffset now, TimeSpan autoOffAfter)
    {
        TurnedOnByService = true;
        AutoOffAt = now + autoOffAfter;
    }

    /// <summary>A further alarm during an active window pushes the deadline out so the TV does not go dark mid-operation.</summary>
    public void ExtendAutoOff(DateTimeOffset now, TimeSpan autoOffAfter)
    {
        if (TurnedOnByService)
        {
            AutoOffAt = now + autoOffAfter;
        }
    }

    public bool IsAutoOffDue(DateTimeOffset now)
        => TurnedOnByService && AutoOffAt is { } autoOffAt && now >= autoOffAt;

    public void Reset()
    {
        TurnedOnByService = false;
        AutoOffAt = null;
    }
}
