using System.Text.Json;
using Traced.Api.Contracts;
using Traced.Api.Mapping;
using Traced.Domain;

namespace Traced.Api.Tests.Mapping;

public sealed class ContractMappingTests
{
    private static readonly Guid TraceId = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    private static readonly Guid RootSpanId = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
    private static readonly Guid ChildSpanId = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    private static readonly Span Root = new(
        TraceId, RootSpanId, ParentSpanId: null, "checkout", "place_order",
        StartNs: 1713484800000000000L, EndNs: 1713484800150000000L, SpanStatus.Ok,
        new Dictionary<string, string> { ["order_id"] = "ord_123" });

    private static readonly Span Child = new(
        TraceId, ChildSpanId, RootSpanId, "inventory", "reserve_stock",
        StartNs: 1713484800020000000L, EndNs: 1713484800100000000L, SpanStatus.Error,
        new Dictionary<string, string>());

    [Fact]
    public void Trace_list_matches_spec_example_shape()
    {
        var result = new TraceQueryResult(
            Total: 1,
            Traces: [new TraceSummary(TraceId, "checkout", "place_order", SpanCount: 4, DurationMs: 150, StartNs: 1713484800000000000L, SpanStatus.Ok)]);

        var json = JsonSerializer.Serialize(result.ToResponse(), TracedJsonContext.Default.TraceListResponse);

        Assert.Equal(
            """{"traces":[{"trace_id":"550e8400-e29b-41d4-a716-446655440000","root_service":"checkout","root_operation":"place_order","span_count":4,"duration_ms":150,"start_time":1713484800000000000,"status":"ok"}],"total":1}""",
            json);
    }

    [Fact]
    public void Error_summary_serialises_status_error()
    {
        var summary = new TraceSummary(TraceId, "s", "o", 1, 0, 0, SpanStatus.Error);

        var json = JsonSerializer.Serialize(summary.ToDto(), TracedJsonContext.Default.TraceSummaryDto);

        Assert.Contains("\"status\":\"error\"", json);
    }

    [Fact]
    public void Trace_detail_omits_parent_on_root_and_empty_tags_on_child()
    {
        var json = JsonSerializer.Serialize(
            ContractMapping.ToDetailResponse(TraceId, [Root, Child]),
            TracedJsonContext.Default.TraceDetailResponse);

        Assert.Equal(
            """{"trace_id":"550e8400-e29b-41d4-a716-446655440000","spans":[""" +
            """{"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"checkout","operation":"place_order","start_time":1713484800000000000,"end_time":1713484800150000000,"status":"ok","tags":{"order_id":"ord_123"}},""" +
            """{"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b811-9dad-11d1-80b4-00c04fd430c8","parent_span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"inventory","operation":"reserve_stock","start_time":1713484800020000000,"end_time":1713484800100000000,"status":"error"}""" +
            "]}",
            json);
    }

    [Fact]
    public void Uuids_serialise_lowercase()
    {
        var upper = Guid.Parse("550E8400-E29B-41D4-A716-446655440000");

        var json = JsonSerializer.Serialize(
            ContractMapping.ToDetailResponse(upper, []),
            TracedJsonContext.Default.TraceDetailResponse);

        Assert.Equal("""{"trace_id":"550e8400-e29b-41d4-a716-446655440000","spans":[]}""", json);
    }

    [Fact]
    public void Error_response_has_single_error_field()
    {
        var json = JsonSerializer.Serialize(new ErrorResponse("trace not found"), TracedJsonContext.Default.ErrorResponse);

        Assert.Equal("""{"error":"trace not found"}""", json);
    }

    [Fact]
    public void Status_response_is_ok()
    {
        Assert.Equal("""{"status":"ok"}""", JsonSerializer.Serialize(StatusResponse.Ok, TracedJsonContext.Default.StatusResponse));
    }
}
