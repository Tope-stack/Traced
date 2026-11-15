using Traced.Domain;
using Traced.Storage;

namespace Traced.Benchmarks;

/// <summary>A clock that never moves, so window clamping doesn't change results mid-run.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Emitter-shaped data (emitter/internal/emitter/emitter.go): 1 root + 1–4 children, root 50–500 ms, 5% error, empty tags.</summary>
internal static class Workload
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    public static readonly long NowNs = (Now - DateTimeOffset.UnixEpoch).Ticks * 100;
    public const long Ms = 1_000_000;
    public const long Minute = 60_000_000_000;

    private static readonly (string Service, string Operation)[] Ops =
    [
        ("api-gateway", "GET /checkout"), ("checkout", "place_order"), ("inventory", "reserve_stock"),
        ("payments", "charge_card"), ("users", "get_profile"), ("shipping", "quote"),
        ("notifications", "send_email"), ("search", "query"),
    ];

    private static readonly IReadOnlyDictionary<string, string> NoTags =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    public static ShardedTraceStore NewStore(int shardCount = 32, int windowMinutes = 30) =>
        new(new FixedTime(Now), new WindowOptions { ShardCount = shardCount, WindowMinutes = windowMinutes });

    /// <summary>One trace, spans in generation order (root first), with fresh string instances like deserialized JSON.</summary>
    public static Span[] Trace(Random random, long rootStartNs)
    {
        var traceId = Guid.NewGuid();
        var durationMs = 50 + random.Next(451);
        var rootOp = Ops[random.Next(Ops.Length)];
        var root = new Span(traceId, Guid.NewGuid(), null, Copy(rootOp.Service), Copy(rootOp.Operation),
            rootStartNs, rootStartNs + (durationMs * Ms), Status(random), NoTags);
        var spans = new Span[2 + random.Next(4)];
        spans[0] = root;
        for (var i = 1; i < spans.Length; i++)
        {
            var op = Ops[random.Next(Ops.Length)];
            var start = rootStartNs + (random.Next(durationMs + 1) * Ms);
            spans[i] = new Span(traceId, Guid.NewGuid(), root.SpanId, Copy(op.Service), Copy(op.Operation),
                start, start + ((10 + random.Next(Math.Max(1, durationMs - 10))) * Ms), Status(random), NoTags);
        }

        if (random.NextDouble() < 0.3)
        {
            random.Shuffle(spans);
        }

        return spans;
    }

    /// <summary>Emitter batching: append whole traces until the batch has ≥ 10 spans.</summary>
    public static List<Span[]> Batches(Random random, int traceCount, long fromNs, long toNs)
    {
        var batches = new List<Span[]>();
        var current = new List<Span>(14);
        for (var i = 0; i < traceCount; i++)
        {
            current.AddRange(Trace(random, fromNs + random.NextInt64(Math.Max(1, toNs - fromNs))));
            if (current.Count >= 10)
            {
                batches.Add([.. current]);
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            batches.Add([.. current]);
        }

        return batches;
    }

    /// <summary>Fills a store with <paramref name="traceCount"/> rooted traces spread over the last 25 minutes.</summary>
    public static ShardedTraceStore Filled(int traceCount, int seed = 1, int shardCount = 32)
    {
        var store = NewStore(shardCount);
        var random = new Random(seed);
        foreach (var batch in Batches(random, traceCount, NowNs - (25 * Minute), NowNs))
        {
            store.Ingest(batch);
        }

        return store;
    }

    private static SpanStatus Status(Random random) => random.Next(100) < 5 ? SpanStatus.Error : SpanStatus.Ok;

    private static string Copy(string s) => new(s.AsSpan());
}
