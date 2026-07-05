using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher.Configuration;

public sealed class TvOptions
{
    public const string SectionName = "Tv";

    /// <summary>Static IP of the TV in the station WLAN. Reachable = TV is on (ping sensor).</summary>
    [Required(AllowEmptyStrings = false)]
    public string IpAddress { get; set; } = string.Empty;

    [Range(200, 10000)]
    public int PingTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// One lost WLAN packet must not misclassify a manually switched-on TV as off (that would
    /// arm the auto-off timer against FA-6), so several attempts are made before concluding "off".
    /// </summary>
    [Range(1, 10)]
    public int PingAttempts { get; set; } = 3;

    [Range(0, 5000)]
    public int PingRetryDelayMs { get; set; } = 250;

    [Required(AllowEmptyStrings = false)]
    public string CecClientPath { get; set; } = "cec-client";

    /// <summary>Logical CEC address of the TV; per CEC standard the TV is always 0.</summary>
    [Range(0, 15)]
    public int CecTvAddress { get; set; }

    [Range(1, 120)]
    public int CecCommandTimeoutSec { get; set; } = 20;

    /// <summary>After power-on, send "as" so the TV switches to the Pi's HDMI input (dashboard visible).</summary>
    public bool CecSetActiveSourceAfterOn { get; set; } = true;
}

[OptionsValidator]
public sealed partial class TvOptionsValidator : IValidateOptions<TvOptions>;
