using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using BwbAlarmWatcher2.Alarms;
using BwbAlarmWatcher2.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BwbAlarmWatcher2.Tests;

public class BwbAlarmApiClientTests
{
    private static readonly FrozenSet<string> ClosedSubkinds =
        new[] { "CLOSED" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Filters that let everything through, for tests not concerned with filtering.</summary>
    private static AlarmFilterOptions PassAllFilters
        => new() { UseManualAlarms = true, MessageBlockedParts = [], MessageAllowedParts = [] };

    private static IReadOnlyCollection<ActiveAlarm> Extract(string json, AlarmFilterOptions? filters = null)
    {
        var payload = JsonSerializer.Deserialize(json, AlarmApiJsonContext.Default.GraphQlAlarmsResponse);
        Assert.NotNull(payload);
        return BwbAlarmApiClient.ExtractActiveAlarms(payload, ClosedSubkinds, filters ?? PassAllFilters, NullLogger.Instance);
    }

    [Fact]
    public void ExtractActiveAlarms_MixedSubkinds_ReturnsNonClosedAlarms()
    {
        const string json = """
        {
          "data": {
            "alarms": {
              "hasNextPage": false,
              "results": [
                { "id": "1", "extid": "R 1.5 260704 100", "subkind": "NEW", "message": "Einsatz A" },
                { "id": "2", "extid": "R 1.5 260704 101", "subkind": "UPDATE", "message": "Einsatz B" },
                { "id": "3", "extid": "R 1.5 260704 102", "subkind": "CLOSED", "message": "Einsatz C" }
              ]
            }
          }
        }
        """;

        var alarms = Extract(json);

        Assert.Equal(["R 1.5 260704 100", "R 1.5 260704 101"], alarms.Select(a => a.Id));
    }

    [Fact]
    public void ExtractActiveAlarms_ClosedSubkindDifferentCase_IsFilteredOut()
    {
        const string json = """
        { "data": { "alarms": { "results": [ { "id": "1", "extid": "X", "subkind": "closed", "message": "m" } ] } } }
        """;

        Assert.Empty(Extract(json));
    }

    [Fact]
    public void ExtractActiveAlarms_MissingSubkind_IsTreatedAsActive()
    {
        const string json = """
        { "data": { "alarms": { "results": [ { "id": "1", "extid": "A1", "message": "m" } ] } } }
        """;

        Assert.Equal(["A1"], Extract(json).Select(a => a.Id));
    }

    [Fact]
    public void ExtractActiveAlarms_MissingExtid_FallsBackToId()
    {
        const string json = """
        { "data": { "alarms": { "results": [ { "id": "42", "subkind": "NEW", "message": "m" } ] } } }
        """;

        Assert.Equal(["42"], Extract(json).Select(a => a.Id));
    }

    [Fact]
    public void ExtractActiveAlarms_MissingExtidAndId_IsSkipped()
    {
        const string json = """
        { "data": { "alarms": { "results": [ { "subkind": "NEW", "message": "m" } ] } } }
        """;

        Assert.Empty(Extract(json));
    }

    [Fact]
    public void ExtractActiveAlarms_EmptyOrMissingData_ReturnsEmpty()
    {
        Assert.Empty(Extract("""{ "data": { "alarms": { "results": [] } } }"""));
        Assert.Empty(Extract("""{ "data": { "alarms": {} } }"""));
        Assert.Empty(Extract("""{ "data": {} }"""));
        Assert.Empty(Extract("{}"));
    }

    // --- Filter rules ported from v1 AlarmService.FilterAlarms ---

    [Fact]
    public void ExtractActiveAlarms_NullMessage_IsSkipped()
    {
        const string json = """
        { "data": { "alarms": { "results": [ { "id": "1", "extid": "A1", "subkind": "NEW" } ] } } }
        """;

        Assert.Empty(Extract(json));
    }

    [Fact]
    public void ExtractActiveAlarms_BlockedMessagePart_IsSkipped()
    {
        const string json = """
        {
          "data": { "alarms": { "results": [
            { "id": "1", "extid": "A1", "message": "ECH Probealarm" },
            { "id": "2", "extid": "A2", "message": "Vermisste Person" }
          ] } }
        }
        """;
        var filters = new AlarmFilterOptions { MessageBlockedParts = ["ECH"], MessageAllowedParts = [] };

        Assert.Equal(["A2"], Extract(json, filters).Select(a => a.Id));
    }

    [Fact]
    public void ExtractActiveAlarms_AllowedPartsConfigured_RequiresMatch()
    {
        const string json = """
        {
          "data": { "alarms": { "results": [
            { "id": "1", "extid": "A1", "message": "Bergrettung Kempten" },
            { "id": "2", "extid": "A2", "message": "Sonstiger Einsatz" }
          ] } }
        }
        """;
        var filters = new AlarmFilterOptions { MessageBlockedParts = [], MessageAllowedParts = ["Kempten"] };

        Assert.Equal(["A1"], Extract(json, filters).Select(a => a.Id));
    }

    [Fact]
    public void ExtractActiveAlarms_ManualAlarmsDisabled_SkipsExtidWithM()
    {
        const string json = """
        {
          "data": { "alarms": { "results": [
            { "id": "1", "extid": "R 1.5 M 230203", "message": "Manuell" },
            { "id": "2", "message": "Ohne extid" },
            { "id": "3", "extid": "R 1.5 230204", "message": "Alamos" }
          ] } }
        }
        """;
        var filters = new AlarmFilterOptions { UseManualAlarms = false, MessageBlockedParts = [], MessageAllowedParts = [] };

        Assert.Equal(["R 1.5 230204"], Extract(json, filters).Select(a => a.Id));
    }

    [Fact]
    public void ExtractActiveAlarms_ManualAlarmsEnabled_KeepsExtidWithM()
    {
        const string json = """
        { "data": { "alarms": { "results": [ { "id": "1", "extid": "R 1.5 M 230203", "message": "Manuell" } ] } } }
        """;

        Assert.Equal(["R 1.5 M 230203"], Extract(json).Select(a => a.Id));
    }

    // --- Query construction and transport ---

    [Fact]
    public void BuildAlarmsQuery_MatchesV1WireShapePlusSubkind()
    {
        var query = BwbAlarmApiClient.BuildAlarmsQuery("2026-06-05T07:42:16.928Z", 100);

        Assert.Contains("query getAlarms", query);
        Assert.Contains("createdAfter: \"2026-06-05T07:42:16.928Z\"", query);
        Assert.Contains("limit: 100", query);
        Assert.Contains("hasNextPage", query);
        Assert.Contains("results { id extid subkind message }", query);
    }

    [Fact]
    public async Task GetActiveAlarmsAsync_HappyPath_PostsQueryAndReturnsAlarms()
    {
        const string body = """
        { "data": { "alarms": { "hasNextPage": false, "results": [ { "id": "1", "extid": "A1", "subkind": "NEW", "message": "Einsatz" } ] } } }
        """;
        var handler = new RecordingHandler(Respond(HttpStatusCode.OK, body));
        var sut = CreateClient(handler);

        var alarms = await sut.GetActiveAlarmsAsync(CancellationToken.None);

        Assert.Equal(["A1"], alarms.Select(a => a.Id));
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://example.org/graphql", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("\"operationName\":\"getAlarms\"", handler.RequestBody);
        // FakeTimeProvider starts at 2000-01-01T00:00Z; lookback is 1 h.
        Assert.Contains("1999-12-31T23:00:00.000Z", handler.RequestBody);
    }

    [Fact]
    public async Task GetActiveAlarmsAsync_GraphQlErrors_Throws()
    {
        const string body = """
        { "errors": [ { "message": "not authorized" } ] }
        """;
        var sut = CreateClient(new RecordingHandler(Respond(HttpStatusCode.OK, body)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetActiveAlarmsAsync(CancellationToken.None));
        Assert.Contains("not authorized", ex.Message);
    }

    [Fact]
    public async Task GetActiveAlarmsAsync_HttpError_Throws()
    {
        var sut = CreateClient(new RecordingHandler(Respond(HttpStatusCode.Forbidden, "{}")));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GetActiveAlarmsAsync(CancellationToken.None));
    }

    private static BwbAlarmApiClient CreateClient(RecordingHandler handler)
        => new(
            new HttpClient(handler),
            Options.Create(new ApiOptions { AuthToken = "test-token", GraphQlUrl = "https://example.org/graphql" }),
            Options.Create(PassAllFilters),
            new FakeTimeProvider(),
            NullLogger<BwbAlarmApiClient>.Instance);

    private static HttpResponseMessage Respond(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
