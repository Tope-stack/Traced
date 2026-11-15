using Traced.Api.Contracts;
using Traced.Domain;

namespace Traced.Api.Mapping;

/// <summary>Domain -> wire. Inbound wire -> domain lives in <c>Validation/IngestRequestValidator</c>.</summary>
public static class ContractMapping
{
    public static TraceListResponse ToResponse(this TraceQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var traces = new TraceSummaryDto[result.Traces.Count];
        for (var i = 0; i < traces.Length; i++)
        {
            traces[i] = result.Traces[i].ToDto();
        }

        return new TraceListResponse(traces, result.Total);
    }

    public static TraceDetailResponse ToDetailResponse(Guid traceId, IReadOnlyList<Span> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        var dtos = new SpanDto[spans.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            dtos[i] = spans[i].ToDto();
        }

        return new TraceDetailResponse(FormatUuid(traceId), dtos);
    }

    public static TraceSummaryDto ToDto(this TraceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new TraceSummaryDto(
            FormatUuid(summary.TraceId),
            summary.RootService,
            summary.RootOperation,
            summary.SpanCount,
            summary.DurationMs,
            summary.StartNs,
            FormatStatus(summary.Status));
    }

    public static SpanDto ToDto(this Span span)
    {
        ArgumentNullException.ThrowIfNull(span);

        return new SpanDto(
            FormatUuid(span.TraceId),
            FormatUuid(span.SpanId),
            span.ParentSpanId is { } parent ? FormatUuid(parent) : null,
            span.Service,
            span.Operation,
            span.StartNs,
            span.EndNs,
            FormatStatus(span.Status),
            span.Tags.Count == 0 ? null : span.Tags);
    }

    // "D" is lowercase 8-4-4-4-12.
    private static string FormatUuid(Guid id) => id.ToString("D");

    private static string FormatStatus(SpanStatus status) => status == SpanStatus.Error ? "error" : "ok";
}
