using System.Collections.Frozen;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BwbAlarmWatcher.Configuration;
using Microsoft.Extensions.Options;

namespace BwbAlarmWatcher.Alarms;

/// <summary>
/// Fetches alarms via the GraphQL endpoint. Query shape and filter rules are ported from v1's
/// GraphQlAccessService/AlarmService (operation "getAlarms", alarms(createdAfter:) with
/// PageOfAlarm { hasNextPage, results }, manual-alarm and message-part filters) — but without
/// the GraphQL.Client/query-builder stack: one pooled HttpClient, a precomputed query template
/// and source-generated JSON keep allocations and footprint minimal.
/// </summary>
public sealed partial class BwbAlarmApiClient : IAlarmSource
{
    private const string OperationName = "getAlarms";

    public BwbAlarmApiClient(
        HttpClient httpClient,
        IOptions<ApiOptions> options,
        IOptions<AlarmFilterOptions> filterOptions,
        TimeProvider timeProvider,
        ILogger<BwbAlarmApiClient> logger)
    {
        Options = options.Value;
        Filters = filterOptions.Value;
        TimeProvider = timeProvider;
        Logger = logger;
        ClosedSubkinds = Options.ClosedSubkinds.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        httpClient.BaseAddress = new Uri(Options.GraphQlUrl);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Options.AuthToken);
        HttpClient = httpClient;
    }

    private HttpClient HttpClient { get; }
    private ApiOptions Options { get; }
    private AlarmFilterOptions Filters { get; }
    private TimeProvider TimeProvider { get; }
    private ILogger<BwbAlarmApiClient> Logger { get; }
    private FrozenSet<string> ClosedSubkinds { get; }

    public async Task<IReadOnlyCollection<ActiveAlarm>> GetActiveAlarmsAsync(CancellationToken cancellationToken)
    {
        var since = TimeProvider.GetUtcNow() - TimeSpan.FromSeconds(Options.LookbackSec);
        var sinceIso = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var request = new GraphQlRequest
        {
            Query = BuildAlarmsQuery(sinceIso, Options.Limit),
            OperationName = OperationName,
        };

        using var response = await HttpClient.PostAsJsonAsync(
            requestUri: (string?)null,
            request,
            AlarmApiJsonContext.Default.GraphQlRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(AlarmApiJsonContext.Default.GraphQlAlarmsResponse, cancellationToken)
            ?? throw new InvalidOperationException("GraphQL endpoint returned an empty response body.");

        // GraphQL reports schema/authorisation problems as 200 + errors array.
        if (payload.Errors is { Count: > 0 } errors)
        {
            var messages = string.Join("; ", errors.Select(e => e.Message ?? "unknown error"));
            throw new InvalidOperationException($"GraphQL query failed: {messages}");
        }

        var active = ExtractActiveAlarms(payload, ClosedSubkinds, Filters, Logger);
        LogAlarmsFetched(Logger, payload.Data?.Alarms?.Results?.Count ?? 0, active.Count, Options.LookbackSec);
        return active;
    }

    /// <summary>
    /// Same wire shape v1's generated query builder produced, plus "subkind" (verified against
    /// the schema of the generated client). The date is inlined so no assumption about the
    /// schema's scalar type name is needed; the value is fully service-controlled.
    /// </summary>
    internal static string BuildAlarmsQuery(string createdAfterIso, int limit)
        => $$"""query getAlarms { alarms(createdAfter: "{{createdAfterIso}}", limit: {{limit}}) { hasNextPage results { id extid subkind message } } }""";

    /// <summary>Filter semantics ported from v1's AlarmService.FilterAlarms; subkind handling is new in v2.</summary>
    internal static IReadOnlyList<ActiveAlarm> ExtractActiveAlarms(
        GraphQlAlarmsResponse payload,
        FrozenSet<string> closedSubkinds,
        AlarmFilterOptions filters,
        ILogger logger)
    {
        var page = payload.Data?.Alarms;
        if (page?.Results is not { } results)
        {
            LogNoAlarmData(logger);
            return [];
        }

        if (page.HasNextPage is true)
        {
            LogPageTruncated(logger);
        }

        var active = new List<ActiveAlarm>(results.Count);
        foreach (var alarm in results)
        {
            var id = alarm.Extid ?? alarm.Id;
            if (id is null)
            {
                LogAlarmWithoutId(logger);
                continue;
            }

            if (alarm.Subkind is not null && closedSubkinds.Contains(alarm.Subkind))
            {
                continue;
            }

            // Manual-alarm guard (v1): with UseManualAlarms disabled, only alarms with a
            // non-empty extid that does not contain 'M' pass.
            if (!filters.UseManualAlarms && (string.IsNullOrEmpty(alarm.Extid) || alarm.Extid.Contains('M')))
            {
                continue;
            }

            // v1: alarms without a message cannot be classified and are dropped.
            if (alarm.Message is null)
            {
                continue;
            }

            if (ContainsAny(alarm.Message, filters.MessageBlockedParts))
            {
                continue;
            }

            if (filters.MessageAllowedParts.Length > 0 && !ContainsAny(alarm.Message, filters.MessageAllowedParts))
            {
                continue;
            }

            active.Add(new ActiveAlarm(id));
        }

        return active;
    }

    private static bool ContainsAny(string message, string[] parts)
    {
        foreach (var part in parts)
        {
            if (message.Contains(part, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetched {FetchedCount} alarms ({ActiveCount} active after filtering) within the last {LookbackSec}s")]
    private static partial void LogAlarmsFetched(ILogger logger, int fetchedCount, int activeCount, int lookbackSec);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GraphQL response contained no alarm data")]
    private static partial void LogNoAlarmData(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alarm page overflowed (hasNextPage), consider raising Api:Limit")]
    private static partial void LogPageTruncated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alarm without extid and id in GraphQL response, skipping entry")]
    private static partial void LogAlarmWithoutId(ILogger logger);
}
