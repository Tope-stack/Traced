using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

/// <summary>Light smoke test only; the full stress test is phase 5.</summary>
public sealed class ConcurrencySmokeTests
{
    [Fact]
    public async Task Concurrent_writers_and_readers_lose_nothing()
    {
        const int writers = 8;
        const int tracesPerWriter = 500;
        var (_, store) = NewStore(shardCount: 8);
        var spanCounts = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
        using var stop = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var result = store.Query(new TraceQuery(Limit: 50));
                Assert.True(result.Traces.Count <= 50);
                foreach (var summary in result.Traces)
                {
                    Assert.True(store.TryGetTrace(summary.TraceId, out var spans));
                    Assert.True(spans.Count >= 1);
                }
            }
        })).ToArray();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var random = new Random(w);
            for (var t = 0; t < tracesPerWriter; t++)
            {
                var root = Root(Guid.NewGuid(), startNs: NowNs - (random.Next(0, 1000) * Ms));
                var spans = new List<Span> { root };
                var children = random.Next(1, 5);
                for (var c = 0; c < children; c++)
                {
                    spans.Add(Child(root, offsetNs: random.Next(0, 150) * Ms));
                }

                spanCounts[root.TraceId] = spans.Count;
                var shuffled = spans.OrderBy(_ => random.Next()).ToArray();
                var split = random.Next(0, shuffled.Length + 1);
                store.Ingest(shuffled[..split]);
                store.Ingest(shuffled[split..]);
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        var all = store.Query(new TraceQuery(Limit: TraceQuery.MaxLimit));
        Assert.Equal(writers * tracesPerWriter, all.Total);
        foreach (var (traceId, expected) in spanCounts)
        {
            Assert.Equal(expected, store.Get(traceId).Count);
        }
    }
}
