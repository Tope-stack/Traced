namespace Traced.Domain;

/// <summary>Immutable per-trace summary, derived from the root span plus all spans received.</summary>
public sealed record TraceSummary(
    Guid TraceId,
    string RootService,
    string RootOperation,
    int SpanCount,
    long DurationMs,
    long StartNs,
    SpanStatus Status);
