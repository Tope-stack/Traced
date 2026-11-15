namespace Traced.Api.Contracts;

/// <summary>Spec <c>TraceListResponse</c>.</summary>
public sealed record TraceListResponse(IReadOnlyList<TraceSummaryDto> Traces, int Total);
