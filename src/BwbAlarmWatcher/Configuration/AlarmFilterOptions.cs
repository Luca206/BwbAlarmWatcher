namespace BwbAlarmWatcher.Configuration;

/// <summary>
/// Filter rules deciding which fetched alarms activate the TV. Ported unchanged from v1
/// (section name and keys included, e.g. <c>Alarm__MessageBlockedParts__0</c>), where they
/// proved necessary in production to suppress test alarms.
/// </summary>
public sealed class AlarmFilterOptions
{
    public const string SectionName = "Alarm";

    /// <summary>Whether manual alarms (extid containing 'M') activate the TV.</summary>
    public bool UseManualAlarms { get; set; } = true;

    /// <summary>An alarm whose message contains any of these parts is ignored. Blocked wins over allowed.</summary>
    public string[] MessageBlockedParts { get; set; } = ["ECH"];

    /// <summary>When non-empty, an alarm message must contain at least one of these parts.</summary>
    public string[] MessageAllowedParts { get; set; } = [];
}
