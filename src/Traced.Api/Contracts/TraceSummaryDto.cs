namespace Traced.Api.Contracts;

/// <summary>Outbound spec <c>TraceSummary</c>.</summary>
public sealed record TraceSummaryDto(
    string TraceId,
    string RootService,
    string RootOperation,
    int SpanCount,
    long DurationMs,
    long StartTime,
    string Status);
