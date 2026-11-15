using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Traced.Domain;

namespace Traced.Storage;

/// <summary>
/// In-memory <see cref="ITraceStore"/> split into power-of-two shards, each guarded by a plain lock.
/// See docs/PLAN.md §3. Every read clamps to <c>now - window</c>, so results are correct whether or not
/// eviction has run.
/// </summary>
public sealed class ShardedTraceStore : ITraceStore
{
    private static readonly Guid MinGuid = Guid.Empty;
    private static readonly Guid MaxGuid = Guid.AllBitsSet;

    private readonly TimeProvider _time;
    private readonly WindowOptions _options;
    private readonly Shard[] _shards;
    private readonly int _shardMask;

    public ShardedTraceStore(TimeProvider time, WindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ShardCount <= 0 || !BitOperations.IsPow2(options.ShardCount))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ShardCount, "ShardCount must be a positive power of two.");
        }

        if (options.WindowMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WindowMinutes, "WindowMinutes must be positive.");
        }

        _time = time;
        _options = options;
        _shards = new Shard[options.ShardCount];
        for (var i = 0; i < _shards.Length; i++)
        {
            _shards[i] = new Shard();
        }

        _shardMask = options.ShardCount - 1;
    }

    public void Ingest(IReadOnlyList<Span> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var cutoff = CurrentCutoff();

        // Group by shard first so each shard lock is taken at most once per batch.
        var buckets = new List<Span>?[_shards.Length];
        foreach (var span in spans)
        {
            if (span.StartNs < cutoff)
            {
                continue; // stale: dropped silently
            }

            (buckets[ShardIndex(span.TraceId)] ??= []).Add(span);
        }

        for (var i = 0; i < buckets.Length; i++)
        {
            if (buckets[i] is not { } bucket)
            {
                continue;
            }

            var shard = _shards[i];
            lock (shard.Gate)
            {
                foreach (var span in bucket)
                {
                    shard.Apply(span);
                }
            }
        }
    }

    public TraceQueryResult Query(TraceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cutoff = CurrentCutoff();
        var lo = Math.Max(query.AfterNs ?? long.MinValue, cutoff);                  // inclusive
        var hi = query.BeforeNs is { } before ? before - 1 : long.MaxValue;         // before is exclusive
        if (lo > hi)
        {
            return new TraceQueryResult(0, []); // GetViewBetween throws when lower > upper
        }

        var lower = (lo, MinGuid);
        var upper = (hi, MaxGuid);

        var total = 0;
        var candidates = new List<TraceSummary>();
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                var view = shard.Rooted.GetViewBetween(lower, upper);
                total += view.Count; // O(matches) for a view; see PLAN §3.3 / §8
                var taken = 0;
                foreach (var (_, traceId) in view.Reverse())
                {
                    if (taken++ == query.Limit)
                    {
                        break;
                    }

                    // Built inside the lock: TraceEntry is mutable and must not escape.
                    candidates.Add(shard.Traces[traceId].ToSummary(traceId));
                }
            }
        }

        // Each shard contributed its own top `limit`, so the global top `limit` is among them.
        candidates.Sort(NewestFirst);
        if (candidates.Count > query.Limit)
        {
            candidates.RemoveRange(query.Limit, candidates.Count - query.Limit);
        }

        return new TraceQueryResult(total, candidates);
    }

    public bool TryGetTrace(Guid traceId, [NotNullWhen(true)] out IReadOnlyList<Span>? spans)
    {
        var cutoff = CurrentCutoff();
        var shard = _shards[ShardIndex(traceId)];

        Span[] copy;
        lock (shard.Gate)
        {
            if (!shard.Traces.TryGetValue(traceId, out var entry) || entry.Root is not { } root || root.StartNs < cutoff)
            {
                spans = null;
                return false; // unknown, orphaned, or outside the window
            }

            copy = entry.CopySpans();
        }

        // Sort outside the lock; the copy is ours.
        Array.Sort(copy, OldestFirst);
        spans = copy;
        return true;
    }

    public int EvictOlderThan(long cutoffNs)
    {
        var removed = 0;
        foreach (var shard in _shards)
        {
            // One shard at a time: ingest and queries on other shards proceed meanwhile.
            lock (shard.Gate)
            {
                removed += shard.EvictOlderThan(cutoffNs);
            }
        }

        return removed;
    }

    private long CurrentCutoff() => _options.CutoffNs(_time.GetUtcNowUnixNanoseconds());

    private int ShardIndex(Guid traceId) => traceId.GetHashCode() & _shardMask;

    // Matches the reverse of the Rooted index's (StartNs, TraceId) ascending order.
    private static int NewestFirst(TraceSummary a, TraceSummary b)
    {
        var byStart = b.StartNs.CompareTo(a.StartNs);
        return byStart != 0 ? byStart : b.TraceId.CompareTo(a.TraceId);
    }

    private static int OldestFirst(Span a, Span b)
    {
        var byStart = a.StartNs.CompareTo(b.StartNs);
        return byStart != 0 ? byStart : a.SpanId.CompareTo(b.SpanId);
    }

    private sealed class Shard
    {
        public readonly object Gate = new();
        public readonly Dictionary<Guid, TraceEntry> Traces = [];
        public readonly SortedSet<(long StartNs, Guid TraceId)> Rooted = [];
        public readonly SortedSet<(long FirstSeenNs, Guid TraceId)> Orphans = [];

        /// <summary>Caller holds <see cref="Gate"/>. Pops from the front of each index; both are ordered oldest first.</summary>
        public int EvictOlderThan(long cutoffNs)
        {
            var removed = 0;
            while (Rooted.Count > 0 && Rooted.Min.StartNs < cutoffNs)
            {
                var oldest = Rooted.Min;
                Rooted.Remove(oldest);
                Traces.Remove(oldest.TraceId);
                removed++;
            }

            while (Orphans.Count > 0 && Orphans.Min.FirstSeenNs < cutoffNs)
            {
                var oldest = Orphans.Min;
                Orphans.Remove(oldest);
                Traces.Remove(oldest.TraceId);
                removed++;
            }

            return removed;
        }

        /// <summary>Caller holds <see cref="Gate"/>.</summary>
        public void Apply(Span span)
        {
            var traceId = span.TraceId;
            var isNew = !Traces.TryGetValue(traceId, out var entry);
            if (isNew)
            {
                entry = new TraceEntry(span.StartNs);
                Traces.Add(traceId, entry);
            }

            var previousFirstSeen = entry!.FirstSeenNs;
            if (!entry.TryAdd(span))
            {
                return; // duplicate span_id
            }

            if (entry.Root is not null)
            {
                return; // already rooted; a second parentless span is stored but not promoted
            }

            // An existing entry without a root is, by construction, in the orphan index.
            if (span.IsRoot)
            {
                if (!isNew)
                {
                    Orphans.Remove((previousFirstSeen, traceId));
                }

                entry.SetRoot(span);
                Rooted.Add((span.StartNs, traceId));
            }
            else if (isNew)
            {
                Orphans.Add((entry.FirstSeenNs, traceId));
            }
            else if (entry.FirstSeenNs != previousFirstSeen)
            {
                Orphans.Remove((previousFirstSeen, traceId));
                Orphans.Add((entry.FirstSeenNs, traceId));
            }
        }
    }
}
