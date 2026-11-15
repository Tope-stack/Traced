using Microsoft.Extensions.Time.Testing;
using Traced.Domain;

namespace Traced.Storage.Tests;

internal static class TestSpans
{
    public const long Ms = 1_000_000L;
    public const long Minute = 60L * 1_000_000_000L;

    // 2024-04-19T00:00:00Z, the spec example's root start_time.
    public static readonly DateTimeOffset Epoch = new(2024, 4, 19, 0, 0, 0, TimeSpan.Zero);
    public const long NowNs = 1_713_484_800_000_000_000L;

    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    public static (FakeTimeProvider Time, ShardedTraceStore Store) NewStore(int shardCount = 32, int windowMinutes = 30)
    {
        var time = new FakeTimeProvider(Epoch);
        var store = new ShardedTraceStore(time, new WindowOptions { ShardCount = shardCount, WindowMinutes = windowMinutes });
        return (time, store);
    }

    public static Span Root(
        Guid traceId,
        long startNs = NowNs,
        long durationNs = 150 * Ms,
        string service = "checkout",
        string operation = "place_order",
        SpanStatus status = SpanStatus.Ok) =>
        new(traceId, Guid.NewGuid(), null, service, operation, startNs, startNs + durationNs, status, NoTags);

    public static Span Child(Span parent, long offsetNs = 10 * Ms, SpanStatus status = SpanStatus.Ok) =>
        new(parent.TraceId, Guid.NewGuid(), parent.SpanId, "inventory", "reserve_stock",
            parent.StartNs + offsetNs, parent.StartNs + offsetNs + (5 * Ms), status, NoTags);

    public static TraceSummary Single(this ITraceStore store)
    {
        var result = store.Query(new TraceQuery());
        Assert.Equal(1, result.Total);
        return Assert.Single(result.Traces);
    }

    public static IReadOnlyList<Span> Get(this ITraceStore store, Guid traceId)
    {
        Assert.True(store.TryGetTrace(traceId, out var spans));
        return spans;
    }
}
