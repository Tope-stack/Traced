using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

/// <summary>
/// Calls <see cref="ITraceStore.EvictOlderThan"/> directly. The clamping-only window behaviour
/// (query/get hide stale traces with no eviction, ingest drops stale spans) is in <see cref="WindowTests"/>.
/// </summary>
public sealed class EvictionTests
{
    [Fact]
    public void Root_exactly_at_cutoff_is_kept_and_one_nanosecond_earlier_is_evicted()
    {
        var (_, store) = NewStore();
        var kept = Root(Guid.NewGuid(), startNs: NowNs);
        var evicted = Root(Guid.NewGuid(), startNs: NowNs - 1);
        store.Ingest([kept, evicted]);

        var removed = store.EvictOlderThan(NowNs);

        Assert.Equal(1, removed);
        Assert.Equal(kept.TraceId, store.Single().TraceId);
        Assert.False(store.TryGetTrace(evicted.TraceId, out _));
    }

    [Fact]
    public void Eviction_removes_every_rooted_trace_below_the_cutoff_across_shards()
    {
        var (_, store) = NewStore(shardCount: 8);
        for (var i = 0; i < 100; i++)
        {
            store.Ingest([Root(Guid.NewGuid(), startNs: NowNs - (i * Ms))]); // 0..99 ms back
        }

        var removed = store.EvictOlderThan(NowNs - (49 * Ms)); // keeps 0..49

        Assert.Equal(50, removed);
        var result = store.Query(new TraceQuery(Limit: 1000));
        Assert.Equal(50, result.Total);
        Assert.All(result.Traces, t => Assert.True(t.StartNs >= NowNs - (49 * Ms)));
    }

    [Fact]
    public void Orphan_whose_first_span_is_below_the_cutoff_is_evicted()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: NowNs - (10 * Ms));
        store.Ingest([Child(root, offsetNs: 0), Child(root, offsetNs: 5 * Ms)]); // orphan, first seen root start

        var removed = store.EvictOlderThan(NowNs - (5 * Ms));

        Assert.Equal(1, removed);
        store.Ingest([root]); // root arrives after its orphaned children were evicted
        Assert.Equal(1, store.Single().SpanCount);
    }

    [Fact]
    public void Orphan_at_or_above_the_cutoff_is_kept()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: NowNs);
        store.Ingest([Child(root, offsetNs: 0)]);

        Assert.Equal(0, store.EvictOlderThan(NowNs));

        store.Ingest([root]);
        Assert.Equal(2, store.Single().SpanCount);
    }

    [Fact]
    public void Orphan_whose_first_seen_moved_earlier_is_evicted_by_its_new_earliest_start()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: NowNs);
        store.Ingest([Child(root, offsetNs: 50 * Ms)]); // orphan keyed at +50 ms
        store.Ingest([Child(root, offsetNs: 10 * Ms)]); // re-keyed to +10 ms

        // A stale +50 ms key would survive a +20 ms cutoff.
        Assert.Equal(1, store.EvictOlderThan(NowNs + (20 * Ms)));

        store.Ingest([root with { StartNs = NowNs + (30 * Ms), EndNs = NowNs + (60 * Ms) }]);
        Assert.Equal(1, store.Single().SpanCount);
    }

    [Fact]
    public void Orphan_that_gained_a_root_is_evicted_by_root_start_not_first_seen()
    {
        var (_, store) = NewStore();
        var traceId = Guid.NewGuid();
        var earlyChild = new Span(traceId, Guid.NewGuid(), Guid.NewGuid(), "s", "o", NowNs, NowNs + Ms, SpanStatus.Ok, new Dictionary<string, string>());
        var root = Root(traceId, startNs: NowNs + (10 * Ms));
        store.Ingest([earlyChild]); // orphan keyed at NowNs
        store.Ingest([root]);       // rooted at +10 ms; the orphan key must be gone

        Assert.Equal(0, store.EvictOlderThan(NowNs + (5 * Ms)));

        // A leftover orphan key would have removed the entry while Rooted still referenced it.
        Assert.Equal(2, store.Single().SpanCount);
        Assert.Equal(2, store.Get(traceId).Count);
        Assert.Equal(1, store.EvictOlderThan(NowNs + (11 * Ms)));
        Assert.Equal(0, store.Query(new TraceQuery()).Total);
    }

    [Fact]
    public void Late_child_of_an_evicted_trace_starts_a_fresh_orphan_and_does_not_resurrect_it()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: NowNs - (100 * Ms), durationNs: 200 * Ms);
        store.Ingest([root, Child(root)]);
        store.EvictOlderThan(NowNs);

        store.Ingest([Child(root, offsetNs: 150 * Ms)]); // starts at +50 ms: inside the window

        Assert.Equal(0, store.Query(new TraceQuery()).Total);
        Assert.False(store.TryGetTrace(root.TraceId, out _));
        Assert.Equal(1, store.EvictOlderThan(NowNs + (51 * Ms))); // it was indexed as an orphan
    }

    [Fact]
    public void In_window_traces_keep_their_summary_and_spans_after_eviction()
    {
        var (_, store) = NewStore();
        var old = Root(Guid.NewGuid(), startNs: NowNs - Minute);
        var root = Root(Guid.NewGuid(), startNs: NowNs);
        var child = Child(root, status: SpanStatus.Error);
        store.Ingest([old, Child(old), root, child]);
        var summaryBefore = store.Query(new TraceQuery()).Traces[0];
        var spansBefore = store.Get(root.TraceId);

        store.EvictOlderThan(NowNs);

        Assert.Equal(summaryBefore, store.Single());
        Assert.Equal(spansBefore, store.Get(root.TraceId));
    }

    [Fact]
    public void Evicting_twice_is_a_no_op_the_second_time()
    {
        var (_, store) = NewStore();
        store.Ingest([Root(Guid.NewGuid(), startNs: NowNs - Ms)]);

        Assert.Equal(1, store.EvictOlderThan(NowNs));
        Assert.Equal(0, store.EvictOlderThan(NowNs));
    }

    [Fact]
    public void Evicting_an_empty_store_returns_zero()
    {
        var (_, store) = NewStore();

        Assert.Equal(0, store.EvictOlderThan(long.MaxValue));
    }

    [Fact]
    public void Evicted_trace_id_can_be_reused_by_a_new_in_window_trace()
    {
        var (_, store) = NewStore();
        var traceId = Guid.NewGuid();
        store.Ingest([Root(traceId, startNs: NowNs - Ms, service: "old")]);
        store.EvictOlderThan(NowNs);

        store.Ingest([Root(traceId, startNs: NowNs, service: "new")]);

        Assert.Equal("new", store.Single().RootService);
    }
}
