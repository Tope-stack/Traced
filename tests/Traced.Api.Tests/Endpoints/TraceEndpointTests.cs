using System.Net;
using System.Text.Json;

namespace Traced.Api.Tests.Endpoints;

public sealed class TraceEndpointTests : ApiTestBase
{
    private const long Ms = 1_000_000L;

    private static async Task<(int Total, string[] Ids, long[] Starts)> ReadList(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var traces = doc.RootElement.GetProperty("traces").EnumerateArray().ToArray();
        return (
            doc.RootElement.GetProperty("total").GetInt32(),
            traces.Select(t => t.GetProperty("trace_id").GetString()!).ToArray(),
            traces.Select(t => t.GetProperty("start_time").GetInt64()).ToArray());
    }

    [Fact]
    public async Task Empty_store_lists_no_traces()
    {
        using var response = await Get("/traces");

        await AssertJson(response, HttpStatusCode.OK, """{"traces":[],"total":0}""");
    }

    [Fact]
    public async Task Traces_are_newest_first_with_default_limit_20_and_total_of_all()
    {
        for (var i = 0; i < 25; i++)
        {
            await IngestRoot(NowNs - (i * Ms));
        }

        using var response = await Get("/traces");
        var (total, ids, starts) = await ReadList(response);

        Assert.Equal(25, total);
        Assert.Equal(20, ids.Length);
        Assert.Equal(starts.OrderByDescending(s => s), starts);
        Assert.Equal(NowNs, starts[0]);
    }

    [Fact]
    public async Task Limit_after_and_before_filter_the_list()
    {
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await IngestRoot(NowNs - (i * 10 * Ms))); // 0, -10, -20, -30, -40 ms
        }

        using var response = await Get($"/traces?limit=1&after={NowNs - (30 * Ms)}&before={NowNs - (10 * Ms)}");
        var (total, page, _) = await ReadList(response);

        Assert.Equal(2, total); // -20 (inside), -30 (after is inclusive); -10 excluded (before is exclusive)
        Assert.Equal([ids[2]], page);
    }

    [Theory]
    [InlineData("limit=0", "invalid query parameter: limit must be an integer between 1 and 1000")]
    [InlineData("limit=1001", "invalid query parameter: limit must be an integer between 1 and 1000")]
    [InlineData("limit=abc", "invalid query parameter: limit must be an integer between 1 and 1000")]
    [InlineData("limit=", "invalid query parameter: limit must be an integer between 1 and 1000")]
    [InlineData("after=-1", "invalid query parameter: after must be a non-negative integer (Unix nanoseconds)")]
    [InlineData("before=x", "invalid query parameter: before must be a non-negative integer (Unix nanoseconds)")]
    [InlineData("after=100&before=100", "invalid query parameter: after must be less than before")]
    [InlineData("after=101&before=100", "invalid query parameter: after must be less than before")]
    [InlineData("limit=1&limit=2", "invalid query parameter: limit must not be repeated")]
    [InlineData("after=1&after=2", "invalid query parameter: after must not be repeated")]
    [InlineData("before=1&before=2", "invalid query parameter: before must not be repeated")]
    public async Task Invalid_query_returns_400_with_error_message(string query, string expectedError)
    {
        using var response = await Get($"/traces?{query}");

        await AssertJson(response, HttpStatusCode.BadRequest, $$"""{"error":"{{expectedError}}"}""");
    }

    [Fact]
    public async Task Unknown_query_parameters_are_ignored()
    {
        await IngestRoot(NowNs);

        using var response = await Get("/traces?foo=bar&limit=5");

        Assert.Equal(1, (await ReadList(response)).Total);
    }

    [Fact]
    public async Task Trace_status_is_error_when_any_span_errors()
    {
        var traceId = NewId();
        var rootId = NewId();
        using var post = await PostSpans(Batch(
            Span(traceId, NewId(), NowNs - (5 * Ms), parent: rootId, status: "error"),
            Span(traceId, rootId, NowNs - (10 * Ms))));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var response = await Get("/traces");

        Assert.Contains("\"status\":\"error\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Trace_detail_lists_spans_by_start_time_ascending()
    {
        var traceId = NewId();
        var rootId = NewId();
        using var post = await PostSpans(Batch(
            Span(traceId, NewId(), NowNs - (2 * Ms), parent: rootId),
            Span(traceId, NewId(), NowNs - (8 * Ms), parent: rootId),
            Span(traceId, rootId, NowNs - (10 * Ms), durationNs: 9 * Ms)));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var response = await Get($"/traces/{traceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var starts = doc.RootElement.GetProperty("spans").EnumerateArray().Select(s => s.GetProperty("start_time").GetInt64()).ToArray();

        Assert.Equal([NowNs - (10 * Ms), NowNs - (8 * Ms), NowNs - (2 * Ms)], starts);
        Assert.Equal(traceId, doc.RootElement.GetProperty("trace_id").GetString());
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("550e8400e29b41d4a716446655440000")]
    [InlineData("550e8400-e29b-41d4-a716-44665544000z")]
    public async Task Invalid_trace_id_returns_400(string traceId)
    {
        using var response = await Get($"/traces/{traceId}");

        await AssertJson(response, HttpStatusCode.BadRequest, """{"error":"invalid trace_id: must be a valid UUID"}""");
    }

    [Fact]
    public async Task Unknown_trace_returns_404()
    {
        using var response = await Get($"/traces/{NewId()}");

        await AssertJson(response, HttpStatusCode.NotFound, """{"error":"trace not found"}""");
    }

    [Fact]
    public async Task Orphan_trace_without_root_returns_404_and_is_not_listed()
    {
        var traceId = NewId();
        using var post = await PostSpans(Batch(Span(traceId, NewId(), NowNs, parent: NewId())));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        using var detail = await Get($"/traces/{traceId}");
        await AssertJson(detail, HttpStatusCode.NotFound, """{"error":"trace not found"}""");
        using var list = await Get("/traces");
        await AssertJson(list, HttpStatusCode.OK, """{"traces":[],"total":0}""");
    }

    [Fact]
    public async Task Trace_that_aged_out_of_the_window_returns_404_and_is_not_listed()
    {
        var traceId = await IngestRoot(NowNs);

        Factory.Time.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));

        using var detail = await Get($"/traces/{traceId}");
        await AssertJson(detail, HttpStatusCode.NotFound, """{"error":"trace not found"}""");
        using var list = await Get("/traces");
        await AssertJson(list, HttpStatusCode.OK, """{"traces":[],"total":0}""");
    }
}
