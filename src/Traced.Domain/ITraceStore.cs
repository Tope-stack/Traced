using System.Diagnostics.CodeAnalysis;

namespace Traced.Domain;

public interface ITraceStore
{
    /// <summary>Stores a validated batch. Spans older than the window are dropped silently.</summary>
    void Ingest(IReadOnlyList<Span> spans);

    /// <summary>Rooted traces in the window matching the query, newest root first.</summary>
    TraceQueryResult Query(TraceQuery query);

    /// <summary>All spans of a rooted, in-window trace, ordered by start time ascending.</summary>
    bool TryGetTrace(Guid traceId, [NotNullWhen(true)] out IReadOnlyList<Span>? spans);

    /// <summary>
    /// Removes rooted traces whose root starts before <paramref name="cutoffNs"/> and orphans whose earliest span
    /// starts before it. A trace starting exactly at the cutoff is kept.
    /// </summary>
    /// <returns>The number of traces removed.</returns>
    int EvictOlderThan(long cutoffNs);
}
