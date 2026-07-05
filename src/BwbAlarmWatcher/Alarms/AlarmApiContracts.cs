using System.Text.Json.Serialization;

namespace BwbAlarmWatcher.Alarms;

// GraphQL-over-HTTP contracts. The shape mirrors v1's generated Bwb.GraphQL.Client types
// (PageOfAlarm { hasNextPage, results }, Alarm { id, extid, subkind, message }), but only the
// fields the monitor needs are selected/deserialized; unknown response fields are ignored.

public sealed class GraphQlRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("operationName")]
    public string? OperationName { get; set; }
}

public sealed class GraphQlAlarmsResponse
{
    [JsonPropertyName("data")]
    public AlarmsQueryData? Data { get; set; }

    [JsonPropertyName("errors")]
    public List<GraphQlError>? Errors { get; set; }
}

public sealed class GraphQlError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class AlarmsQueryData
{
    [JsonPropertyName("alarms")]
    public PageOfAlarms? Alarms { get; set; }
}

public sealed class PageOfAlarms
{
    [JsonPropertyName("hasNextPage")]
    public bool? HasNextPage { get; set; }

    [JsonPropertyName("results")]
    public List<AlarmResult>? Results { get; set; }
}

public sealed class AlarmResult
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("extid")]
    public string? Extid { get; set; }

    [JsonPropertyName("subkind")]
    public string? Subkind { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GraphQlRequest))]
[JsonSerializable(typeof(GraphQlAlarmsResponse))]
internal sealed partial class AlarmApiJsonContext : JsonSerializerContext;
