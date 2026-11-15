using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

/// <summary>Window behaviour with no eviction ever running: correctness comes from clamping alone.</summary>
public sealed class WindowTests
{
    private const long Window = 30 * Minute;
    private const long Cutoff = NowNs - Window;

    [Fact]
    public void Span_starting_before_cutoff_is_dropped_on_ingest()
    {
        var (_, store) = NewStore();
        var stale = Root(Guid.NewGuid(), startNs: Cutoff - 1);

        store.Ingest([stale]);

        Assert.Equal(0, store.Query(new TraceQuery()).Total);
        Assert.False(store.TryGetTrace(stale.TraceId, out _));
    }

    [Fact]
    public void Span_starting_exactly_at_cutoff_is_kept()
    {
        var (_, store) = NewStore();
        var edge = Root(Guid.NewGuid(), startNs: Cutoff);

        store.Ingest([edge]);

        Assert.Equal(edge.StartNs, store.Single().StartNs);
        Assert.Single(store.Get(edge.TraceId));
    }

    [Fact]
    public void Stale_child_is_dropped_while_fresh_spans_in_the_same_batch_are_kept()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: Cutoff + Ms);
        var staleChild = Child(root, offsetNs: -2 * Ms);
        var freshChild = Child(root);

        store.Ingest([staleChild, root, freshChild]);

        Assert.Equal(2, store.Single().SpanCount);
        Assert.DoesNotContain(staleChild, store.Get(root.TraceId));
    }

    [Fact]
    public void Stale_root_is_dropped_so_its_fresh_children_stay_hidden()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: Cutoff - Ms);

        store.Ingest([root, Child(root, offsetNs: 5 * Ms)]);

        Assert.Equal(0, store.Query(new TraceQuery()).Total);
        Assert.False(store.TryGetTrace(root.TraceId, out _));
    }

    [Fact]
    public void Query_and_get_hide_a_trace_that_aged_out_without_eviction()
    {
        var (time, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: NowNs);
        store.Ingest([root, Child(root)]);

        time.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1)); // cutoff is now root start + 100 ns

        Assert.Equal(0, store.Query(new TraceQuery()).Total);
        Assert.Empty(store.Query(new TraceQuery()).Traces);
        Assert.False(store.TryGetTrace(root.TraceId, out _));
    }

    [Fact]
    public void Trace_whose_root_is_exactly_at_the_advanced_cutoff_is_still_visible()
    {
        var (time, store) = NewStore();
        var root = Root(Guid.NewGuid(), startNs: NowNs);
        store.Ingest([root]);

        time.Advance(TimeSpan.FromMinutes(30)); // cutoff == root start

        Assert.Equal(1, store.Query(new TraceQuery()).Total);
        Assert.True(store.TryGetTrace(root.TraceId, out _));
    }

    [Fact]
    public void Total_counts_only_in_window_traces_as_time_moves()
    {
        var (time, store) = NewStore();
        for (var i = 0; i < 10; i++)
        {
            store.Ingest([Root(Guid.NewGuid(), startNs: NowNs + (i * Minute))]);
        }

        time.Advance(TimeSpan.FromMinutes(34)); // cutoff = NowNs + 4 min: traces 4..9 remain

        var result = store.Query(new TraceQuery(Limit: 3));
        Assert.Equal(6, result.Total);
        Assert.Equal([NowNs + (9 * Minute), NowNs + (8 * Minute), NowNs + (7 * Minute)], result.Traces.Select(t => t.StartNs));
    }

    [Fact]
    public void After_below_cutoff_is_clamped_to_the_window()
    {
        var (time, store) = NewStore();
        store.Ingest([Root(Guid.NewGuid(), startNs: NowNs)]);
        store.Ingest([Root(Guid.NewGuid(), startNs: NowNs + (10 * Minute))]);

        time.Advance(TimeSpan.FromMinutes(35));

        Assert.Equal(1, store.Query(new TraceQuery(AfterNs: 0)).Total);
    }

    [Fact]
    public void Window_minutes_option_is_honoured()
    {
        var (_, store) = NewStore(windowMinutes: 1);

        store.Ingest([Root(Guid.NewGuid(), startNs: NowNs - Minute - 1)]);
        store.Ingest([Root(Guid.NewGuid(), startNs: NowNs - Minute)]);

        Assert.Equal(1, store.Query(new TraceQuery()).Total);
    }
}
