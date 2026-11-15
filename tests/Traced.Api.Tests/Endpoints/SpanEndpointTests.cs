using System.Net;
using System.Text;

namespace Traced.Api.Tests.Endpoints;

public sealed class SpanEndpointTests : ApiTestBase
{
    [Fact]
    public async Task Spec_example_batch_is_accepted_with_202_status_ok()
    {
        using var response = await PostSpans(SpecIngestExample);

        await AssertJson(response, HttpStatusCode.Accepted, """{"status":"ok"}""");
    }

    [Fact]
    public async Task Json_content_type_with_charset_is_accepted()
    {
        using var content = new StringContent(SpecIngestExample, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var response = await Client.PostAsync(new Uri("/spans", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    public static TheoryData<string, string, string> InvalidBodies()
    {
        var t = "550e8400-e29b-41d4-a716-446655440000";
        var s = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";
        const long start = 1_713_484_800_100_000_000L;
        string With(string field, string json) =>
            Batch(Span(t, s, start)).Replace($"\"{field}\":", $"\"{field}_was\":", StringComparison.Ordinal).Replace("}]}", $",\"{field}\":{json}}}]}}", StringComparison.Ordinal);

        return new TheoryData<string, string, string>
        {
            { "truncated JSON", "{\"spans\":[", "invalid request body: malformed JSON at $.spans[0]" },
            { "empty body", "", "invalid request body: malformed JSON" },
            { "not an object", "[]", "invalid request body: malformed JSON" },
            { "null body", "null", "invalid request body: body must be a JSON object" },
            { "missing spans", "{}", "invalid request body: missing required field spans" },
            { "empty spans", "{\"spans\":[]}", "invalid request body: spans must contain at least 1 span" },
            { "501 spans", Batch(Enumerable.Range(0, 501).Select(_ => Span(t, Guid.NewGuid().ToString(), start)).ToArray()), "invalid request body: spans must contain at most 500 spans" },
            { "missing span_id", Batch(Span(t, s, start).Replace($"\"span_id\":\"{s}\",", "", StringComparison.Ordinal)), "invalid request body: spans[0]: missing required field span_id" },
            { "bad trace_id", Batch(Span("nope", s, start)), "invalid request body: spans[0]: trace_id must be a valid UUID" },
            { "bad parent_span_id", Batch(Span(t, s, start, parent: "nope")), "invalid request body: spans[0]: parent_span_id must be a valid UUID" },
            { "service too long", Batch(Span(t, s, start, service: new string('a', 257))), "invalid request body: spans[0]: service must be at most 256 characters" },
            { "operation too long", Batch(Span(t, s, start, operation: new string('a', 257))), "invalid request body: spans[0]: operation must be at most 256 characters" },
            { "end before start", Batch(Span(t, s, start, durationNs: -1)), "invalid request body: spans[0]: end_time must not be before start_time" },
            { "bad status", Batch(Span(t, s, start, status: "OK")), "invalid request body: spans[0]: status must be one of: ok, error" },
            { "51 tags", With("tags", "{" + string.Join(',', Enumerable.Range(0, 51).Select(i => $"\"k{i}\":\"v\"")) + "}"), "invalid request body: spans[0]: tags must have at most 50 entries" },
            { "tag value too long", With("tags", $"{{\"k\":\"{new string('v', 1025)}\"}}"), "invalid request body: spans[0]: tags.k must be at most 1024 characters" },
            { "string timestamp", With("start_time", "\"1713484800100000000\""), "invalid request body: malformed JSON at $.spans[0].start_time" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_body_returns_400_with_error_message(string rule, string body, string expectedError)
    {
        _ = rule;
        using var response = await PostSpans(body);

        await AssertJson(response, HttpStatusCode.BadRequest, $$"""{"error":"{{expectedError}}"}""");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Non_json_content_type_returns_400(string contentType)
    {
        using var response = await PostSpans(SpecIngestExample, contentType);

        await AssertJson(response, HttpStatusCode.BadRequest, """{"error":"invalid request body: Content-Type must be application/json"}""");
    }

    [Fact]
    public async Task Missing_content_type_returns_400()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(SpecIngestExample));

        using var response = await Client.PostAsync(new Uri("/spans", UriKind.Relative), content);

        await AssertJson(response, HttpStatusCode.BadRequest, """{"error":"invalid request body: Content-Type must be application/json"}""");
    }

    [Fact]
    public async Task One_bad_span_rejects_the_whole_batch_and_stores_nothing()
    {
        var traceId = NewId();
        var good = Span(traceId, NewId(), NowNs - 1_000_000);
        var bad = Span(traceId, NewId(), NowNs, status: "maybe");

        using var response = await PostSpans(Batch(good, bad));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var list = await Get("/traces");
        await AssertJson(list, HttpStatusCode.OK, """{"traces":[],"total":0}""");
        using var detail = await Get($"/traces/{traceId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task Stale_spans_are_accepted_with_202_but_not_stored()
    {
        var traceId = NewId();
        var stale = NowNs - (31L * 60 * 1_000_000_000);

        using var response = await PostSpans(Batch(Span(traceId, NewId(), stale)));

        await AssertJson(response, HttpStatusCode.Accepted, """{"status":"ok"}""");
        using var list = await Get("/traces");
        await AssertJson(list, HttpStatusCode.OK, """{"traces":[],"total":0}""");
        using var detail = await Get($"/traces/{traceId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }
}
