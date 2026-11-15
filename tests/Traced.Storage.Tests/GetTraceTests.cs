using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

public sealed class GetTraceTests
{
    [Fact]
    public void Spans_are_ordered_by_start_time_ascending_regardless_of_arrival()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        var late = Child(root, offsetNs: 90 * Ms);
        var early = Child(root, offsetNs: 1 * Ms);
        var mid = Child(root, offsetNs: 40 * Ms);

        store.Ingest([late, mid]);
        store.Ingest([root, early]);

        Assert.Equal([root, early, mid, late], store.Get(root.TraceId));
    }

    [Fact]
    public void Unknown_trace_is_not_found()
    {
        var (_, store) = NewStore();
        store.Ingest([Root(Guid.NewGuid())]);

        Assert.False(store.TryGetTrace(Guid.NewGuid(), out var spans));
        Assert.Null(spans);
    }

    [Fact]
    public void Returned_span_list_is_a_copy_unaffected_by_later_ingest()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        store.Ingest([root]);

        var first = store.Get(root.TraceId);
        store.Ingest([Child(root)]);

        Assert.Single(first);
        Assert.Equal(2, store.Get(root.TraceId).Count);
    }

    [Fact]
    public void Trace_becomes_gettable_once_its_root_arrives()
    {
        var (_, store) = NewStore();
        var root = Root(Guid.NewGuid());
        var child = Child(root);

        store.Ingest([child]);
        Assert.False(store.TryGetTrace(root.TraceId, out _));

        store.Ingest([root]);
        Assert.Equal([root, child], store.Get(root.TraceId));
    }
}
