using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher.Configuration;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>How often the Bergwacht Bayern API is polled. 10-30 s is the recommended range for a rescue station.</summary>
    [Range(5, 300)]
    public int PollingIntervalInSec { get; set; } = 15;

    /// <summary>How long the TV stays on after the service switched it on (FA-5). A new alarm extends this deadline.</summary>
    [Range(60, 86400)]
    public int AutoOffAfterSec { get; set; } = 1800;
}

[OptionsValidator]
public sealed partial class WorkerOptionsValidator : IValidateOptions<WorkerOptions>;
