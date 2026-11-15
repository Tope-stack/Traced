using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

public sealed class QueryTests
{
    private static Guid[] IngestRoots(ITraceStore store, params long[] startOffsetsMs)
    {
        var ids = new Guid[startOffsetsMs.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = Guid.NewGuid();
            store.Ingest([Root(ids[i], startNs: NowNs - (startOffsetsMs[i] * Ms))]);
        }

        return ids;
    }

    private static Guid[] Ids(TraceQueryResult result) => result.Traces.Select(t => t.TraceId).ToArray();

    [Fact]
    public void Traces_are_ordered_by_root_start_descending()
    {
        var (_, store) = NewStore();
        var ids = IngestRoots(store, 30, 10, 50, 20, 40); // offsets back from now

        var result = store.Query(new TraceQuery());

        Assert.Equal([ids[1], ids[3], ids[0], ids[4], ids[2]], Ids(result));
    }

    [Fact]
    public void Equal_start_times_are_ordered_by_trace_id_descending_and_stable_across_queries()
    {
        var (_, store) = NewStore(shardCount: 4);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            store.Ingest([Root(id, startNs: NowNs)]);
        }

        var first = Ids(store.Query(new TraceQuery()));
        var second = Ids(store.Query(new TraceQuery()));

        Assert.Equal(ids.OrderByDescending(id => id).ToArray(), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Limit_is_respected_and_total_ignores_it()
    {
        var (_, store) = NewStore();
        var ids = IngestRoots(store, 1, 2, 3, 4, 5, 6, 7);

        var result = store.Query(new TraceQuery(Limit: 3));

        Assert.Equal(7, result.Total);
        Assert.Equal(ids[..3], Ids(result));
    }

    [Fact]
    public void Limit_larger_than_matches_returns_all()
    {
        var (_, store) = NewStore();
        IngestRoots(store, 1, 2);

        var result = store.Query(new TraceQuery(Limit: 1000));

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Traces.Count);
    }

    [Fact]
    public void After_is_inclusive()
    {
        var (_, store) = NewStore();
        var ids = IngestRoots(store, 10, 20, 30);

        var result = store.Query(new TraceQuery(AfterNs: NowNs - (20 * Ms)));

        Assert.Equal(2, result.Total);
        Assert.Equal([ids[0], ids[1]], Ids(result));
    }

    [Fact]
    public void Before_is_exclusive()
    {
        var (_, store) = NewStore();
        var ids = IngestRoots(store, 10, 20, 30);

        var result = store.Query(new TraceQuery(BeforeNs: NowNs - (20 * Ms)));

        Assert.Equal(1, result.Total);
        Assert.Equal([ids[2]], Ids(result));
    }

    [Fact]
    public void Before_one_nanosecond_after_a_root_includes_it()
    {
        var (_, store) = NewStore();
        var ids = IngestRoots(store, 10);

        var result = store.Query(new TraceQuery(BeforeNs: NowNs - (10 * Ms) + 1));

        Assert.Equal([ids[0]], Ids(result));
    }

    [Fact]
    public void After_and_before_together_select_a_half_open_range_and_total_counts_only_it()
    {
        var (_, store) = NewStore();
        var ids = IngestRoots(store, 10, 20, 30, 40, 50);

        var result = store.Query(new TraceQuery(Limit: 1, AfterNs: NowNs - (40 * Ms), BeforeNs: NowNs - (10 * Ms)));

        Assert.Equal(3, result.Total); // 20, 30, 40
        Assert.Equal([ids[1]], Ids(result));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(null, 0L)]
    [InlineData(long.MaxValue, null)]
    [InlineData(0L, 1L)]
    public void Ranges_matching_nothing_return_empty_without_throwing(long? after, long? before)
    {
        var (_, store) = NewStore();
        IngestRoots(store, 10);

        var result = store.Query(new TraceQuery(AfterNs: after, BeforeNs: before));

        // after=0 with no before matches everything in the window; the rest match nothing.
        Assert.Equal(after == 0 && before is null ? 1 : 0, result.Total);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 7)]
    [InlineData(32, 50)]
    [InlineData(32, 1000)]
    [InlineData(8, 1)]
    public void Cross_shard_merge_matches_a_brute_force_model(int shardCount, int limit)
    {
        var (_, store) = NewStore(shardCount);
        var random = new Random(42);
        var model = new List<(long StartNs, Guid Id)>();
        for (var i = 0; i < 400; i++)
        {
            var id = Guid.NewGuid();
            var start = NowNs - (random.Next(0, 100) * Ms); // many ties on purpose
            model.Add((start, id));
            store.Ingest([Root(id, startNs: start)]);
        }

        var after = NowNs - (80 * Ms);
        var before = NowNs - (5 * Ms);
        var expected = model
            .Where(m => m.StartNs >= after && m.StartNs < before)
            .OrderByDescending(m => m.StartNs).ThenByDescending(m => m.Id)
            .ToList();

        var result = store.Query(new TraceQuery(limit, after, before));

        Assert.Equal(expected.Count, result.Total);
        Assert.Equal(expected.Take(limit).Select(m => m.Id).ToArray(), Ids(result));
    }

    [Fact]
    public void Returned_summary_is_a_snapshot_unaffected_by_later_ingest()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        store.Ingest([root]);

        var before = store.Single();
        store.Ingest([Child(root, status: SpanStatus.Error)]);
        var after = store.Single();

        Assert.Equal(1, before.SpanCount);
        Assert.Equal(SpanStatus.Ok, before.Status);
        Assert.Equal(2, after.SpanCount);
        Assert.Equal(SpanStatus.Error, after.Status);
    }
}
