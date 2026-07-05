using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher.Configuration;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>GraphQL endpoint. Default is the services environment that v1 ran against in production.</summary>
    [Required, Url]
    public string GraphQlUrl { get; set; } = "https://api.services.bergwacht-bayern.org/graphql";

    /// <summary>Long-lived bearer token with api:alarm:read, restricted to the Kempten unit. Never commit or log it.</summary>
    [Required(AllowEmptyStrings = false)]
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Query window: alarms created within the last N seconds are considered. Guards against
    /// alarms missed during an outage. Up to 30 days are allowed for testing against old data.
    /// </summary>
    [Range(60, 2_592_000)]
    public int LookbackSec { get; set; } = 3600;

    /// <summary>Page size for the alarms query (schema maximum is 250). A warning is logged if the page overflows.</summary>
    [Range(1, 250)]
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Subkind values that mark an alarm as no longer active. Configurable because the documented
    /// semantics (NEW/UPDATE/CLOSED vs. the OPEN example) are not yet verified against the live API.
    /// Alarms without a subkind count as active.
    /// </summary>
    public string[] ClosedSubkinds { get; set; } = ["CLOSED"];
}

[OptionsValidator]
public sealed partial class ApiOptionsValidator : IValidateOptions<ApiOptions>;
