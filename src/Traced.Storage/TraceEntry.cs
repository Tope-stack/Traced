using Traced.Domain;

namespace Traced.Storage;

/// <summary>
/// Mutable per-trace state. Owned by one shard and read or written only while holding that shard's lock;
/// never handed out of <see cref="ShardedTraceStore"/>.
/// </summary>
internal sealed class TraceEntry(long firstSeenNs)
{
    private readonly List<Span> _spans = new(capacity: 4);
    private readonly HashSet<Guid> _spanIds = new(capacity: 4);

    /// <summary>The first parentless span received. Null while the trace is an orphan.</summary>
    public Span? Root { get; private set; }

    /// <summary>OR of every stored span's status.</summary>
    public bool HasError { get; private set; }

    /// <summary>Earliest span start seen; keys the orphan index while <see cref="Root"/> is null.</summary>
    public long FirstSeenNs { get; private set; } = firstSeenNs;

    public int SpanCount => _spans.Count;

    /// <summary>Appends <paramref name="span"/> unless its span_id is already present.</summary>
    /// <returns>false for a duplicate (nothing changed).</returns>
    public bool TryAdd(Span span)
    {
        if (!_spanIds.Add(span.SpanId))
        {
            return false;
        }

        _spans.Add(span);
        HasError |= span.Status == SpanStatus.Error;
        FirstSeenNs = Math.Min(FirstSeenNs, span.StartNs);
        return true;
    }

    /// <summary>Promotes <paramref name="span"/> to root. Caller ensures <see cref="Root"/> is null (first root wins).</summary>
    public void SetRoot(Span span) => Root = span;

    public TraceSummary ToSummary(Guid traceId)
    {
        var root = Root ?? throw new InvalidOperationException("Only rooted traces have a summary.");
        return new TraceSummary(
            traceId,
            root.Service,
            root.Operation,
            _spans.Count,
            (root.EndNs - root.StartNs) / 1_000_000,
            root.StartNs,
            HasError ? SpanStatus.Error : SpanStatus.Ok);
    }

    public Span[] CopySpans() => [.. _spans];
}
