using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

public sealed class IngestAssemblyTests
{
    public enum Arrival
    {
        RootThenChildren,
        ChildrenThenRootSameBatch,
        ChildrenInBatchARootInBatchB,
    }

    [Theory]
    [InlineData(Arrival.RootThenChildren)]
    [InlineData(Arrival.ChildrenThenRootSameBatch)]
    [InlineData(Arrival.ChildrenInBatchARootInBatchB)]
    public void Arrival_order_does_not_change_summary_or_span_list(Arrival arrival)
    {
        var (_, store) = NewStore();
        var traceId = Guid.NewGuid();
        var root = Root(traceId, durationNs: 150 * Ms);
        var c1 = Child(root, offsetNs: 20 * Ms);
        var c2 = Child(root, offsetNs: 10 * Ms);
        var c3 = Child(root, offsetNs: 30 * Ms);

        switch (arrival)
        {
            case Arrival.RootThenChildren:
                store.Ingest([root, c1, c2, c3]);
                break;
            case Arrival.ChildrenThenRootSameBatch:
                store.Ingest([c1, c2, c3, root]);
                break;
            case Arrival.ChildrenInBatchARootInBatchB:
                store.Ingest([c1, c2, c3]);
                store.Ingest([root]);
                break;
        }

        Assert.Equal(
            new TraceSummary(traceId, "checkout", "place_order", SpanCount: 4, DurationMs: 150, root.StartNs, SpanStatus.Ok),
            store.Single());
        Assert.Equal([root, c2, c1, c3], store.Get(traceId));
    }

    [Fact]
    public void Span_count_includes_children_that_arrived_before_the_root()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        store.Ingest([Child(root), Child(root)]);
        store.Ingest([Child(root)]);

        store.Ingest([root]);

        Assert.Equal(4, store.Single().SpanCount);
    }

    [Fact]
    public void Same_span_twice_in_one_batch_is_counted_once()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        var child = Child(root);

        store.Ingest([root, child, child]);

        Assert.Equal(2, store.Single().SpanCount);
        Assert.Equal(2, store.Get(root.TraceId).Count);
    }

    [Fact]
    public void Same_span_across_batches_is_counted_once()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        var child = Child(root);

        store.Ingest([child]);
        store.Ingest([root, child]);
        store.Ingest([root]);

        Assert.Equal(2, store.Single().SpanCount);
    }

    [Fact]
    public void Duplicate_span_id_keeps_the_first_copy()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        var child = Child(root);
        var resent = child with { Status = SpanStatus.Error };

        store.Ingest([root, child]);
        store.Ingest([resent]);

        Assert.Equal(SpanStatus.Ok, store.Single().Status);
        Assert.Equal(SpanStatus.Ok, store.Get(root.TraceId)[1].Status);
    }

    [Fact]
    public void Error_on_a_leaf_child_makes_the_trace_error()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        var mid = Child(root);
        var leaf = Child(mid, status: SpanStatus.Error);

        store.Ingest([root, mid, leaf]);

        Assert.Equal(SpanStatus.Error, store.Single().Status);
    }

    [Fact]
    public void Error_on_a_child_that_arrived_before_the_root_makes_the_trace_error()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        store.Ingest([Child(root, status: SpanStatus.Error)]);

        store.Ingest([root, Child(root)]);

        Assert.Equal(SpanStatus.Error, store.Single().Status);
    }

    [Fact]
    public void Error_on_the_root_makes_the_trace_error()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), status: SpanStatus.Error);

        store.Ingest([root, Child(root)]);

        Assert.Equal(SpanStatus.Error, store.Single().Status);
    }

    [Fact]
    public void Root_less_trace_is_hidden_from_query_and_get()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());

        store.Ingest([Child(root), Child(root)]);

        var result = store.Query(new TraceQuery());
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Traces);
        Assert.False(store.TryGetTrace(root.TraceId, out _));
    }

    [Fact]
    public void Second_parentless_span_is_stored_but_first_root_wins()
    {
        var (_, store) = NewStore();
        var traceId = Guid.NewGuid();
        var first = Root(traceId, startNs: NowNs - (10 * Ms), durationNs: 100 * Ms, service: "first");
        var second = Root(traceId, startNs: NowNs - (20 * Ms), durationNs: 300 * Ms, service: "second");

        store.Ingest([first]);
        store.Ingest([second]);

        var summary = store.Single();
        Assert.Equal("first", summary.RootService);
        Assert.Equal(first.StartNs, summary.StartNs);
        Assert.Equal(100, summary.DurationMs);
        Assert.Equal(2, summary.SpanCount);
        Assert.Contains(second, store.Get(traceId));
    }

    [Fact]
    public void Second_parentless_span_in_the_same_batch_is_not_root()
    {
        var (_, store) = NewStore();
        var traceId = Guid.NewGuid();
        var first = Root(traceId, service: "first");
        var second = Root(traceId, service: "second");

        store.Ingest([first, second]);

        Assert.Equal("first", store.Single().RootService);
    }

    [Theory]
    [InlineData(150_000_000L, 150L)]
    [InlineData(150_999_999L, 150L)]
    [InlineData(999_999L, 0L)]
    [InlineData(0L, 0L)]
    public void Duration_ms_is_integer_division_of_root_duration(long durationNs, long expectedMs)
    {
        var (_, store) = NewStore();

        store.Ingest([Root(Guid.NewGuid(), durationNs: durationNs)]);

        Assert.Equal(expectedMs, store.Single().DurationMs);
    }

    [Fact]
    public void Root_end_is_used_for_duration_even_when_a_child_ends_later()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid(), durationNs: 100 * Ms);
        var lateChild = Child(root, offsetNs: 99 * Ms); // ends at +104 ms

        store.Ingest([lateChild, root]);

        Assert.Equal(100, store.Single().DurationMs);
    }

    [Fact]
    public void Empty_batch_is_a_no_op()
    {
        var (_, store) = NewStore();

        store.Ingest([]);

        Assert.Equal(0, store.Query(new TraceQuery()).Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-4)]
    [InlineData(31)]
    public void Shard_count_must_be_a_positive_power_of_two(int shardCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewStore(shardCount));
    }
}
