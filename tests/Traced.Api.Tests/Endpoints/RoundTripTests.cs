using System.Net;

namespace Traced.Api.Tests.Endpoints;

/// <summary>POST the spec's ingest example, then read it back in the spec's response shapes.</summary>
public sealed class RoundTripTests : ApiTestBase
{
    [Fact]
    public async Task Spec_example_round_trips_through_list_and_detail()
    {
        using var post = await PostSpans(SpecIngestExample);
        await AssertJson(post, HttpStatusCode.Accepted, """{"status":"ok"}""");

        using var list = await Get("/traces");
        await AssertJson(list, HttpStatusCode.OK,
            """{"traces":[{"trace_id":"550e8400-e29b-41d4-a716-446655440000","root_service":"checkout","root_operation":"place_order","span_count":2,"duration_ms":150,"start_time":1713484800000000000,"status":"ok"}],"total":1}""");

        using var detail = await Get($"/traces/{SpecTraceId}");
        await AssertJson(detail, HttpStatusCode.OK,
            """{"trace_id":"550e8400-e29b-41d4-a716-446655440000","spans":[""" +
            """{"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"checkout","operation":"place_order","start_time":1713484800000000000,"end_time":1713484800150000000,"status":"ok","tags":{"order_id":"ord_123"}},""" +
            """{"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b811-9dad-11d1-80b4-00c04fd430c8","parent_span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"inventory","operation":"reserve_stock","start_time":1713484800020000000,"end_time":1713484800100000000,"status":"ok"}""" +
            "]}");
    }

    [Fact]
    public async Task Uppercase_trace_id_in_path_finds_the_trace_and_echoes_lowercase()
    {
        using var post = await PostSpans(SpecIngestExample);

        using var detail = await Get($"/traces/{SpecTraceId.ToUpperInvariant()}");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.StartsWith($$"""{"trace_id":"{{SpecTraceId}}",""", await detail.Content.ReadAsStringAsync());
    }
}
