namespace Traced.Api.Contracts;

/// <summary>Spec <c>TraceDetailResponse</c>.</summary>
public sealed record TraceDetailResponse(string TraceId, IReadOnlyList<SpanDto> Spans);
