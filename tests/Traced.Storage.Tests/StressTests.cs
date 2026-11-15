using System.Collections.Concurrent;
using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

/// <summary>
/// PLAN §6 phase 5 stress test. Run on its own with:
/// <c>dotnet test tests/Traced.Storage.Tests --filter Category=Stress</c>
/// </summary>
[Trait("Category", "Stress")]
public sealed class StressTests
{
    private const int Writers = 32;
    private const int TracesPerWriter = 5_000;
    private const int Readers = 8;
    private const int BatchSpans = 10;

    // Kept traces start in [now - 10 min, now]; doomed traces in [now - 20 min, now - 15 min].
    // The ingest window is 30 min, so both are accepted; the evictor's cutoff (now - 12 min) removes only doomed ones.
    private const long KeptFrom = NowNs - (10 * Minute);
    private const long EvictCutoff = NowNs - (12 * Minute);

    private sealed record Expected(Guid TraceId, long RootStartNs, int SpanCount, bool HasError, bool Doomed);

    [Fact]
    public async Task Writers_readers_and_eviction_together_produce_exact_results_and_no_torn_reads()
    {
        var (_, store) = NewStore(shardCount: 32, windowMinutes: 30);
        var random = new Random(20261115);

        // 1. Generate every trace, then scatter its (shuffled) spans across random writers.
        var expected = new Dictionary<Guid, Expected>(Writers * TracesPerWriter);
        var perWriter = Enumerable.Range(0, Writers).Select(_ => new List<Span>()).ToArray();
        for (var i = 0; i < Writers * TracesPerWriter; i++)
        {
            var doomed = i % 10 == 0;
            var start = doomed
                ? NowNs - (20 * Minute) + random.NextInt64(5 * Minute)
                : KeptFrom + random.NextInt64(10 * Minute);
            var durationMs = 50 + random.Next(451);
            var root = Root(Guid.NewGuid(), startNs: start, durationNs: durationMs * Ms, status: RandomStatus(random));
            var spans = new List<Span> { root };
            var children = 1 + random.Next(4);
            for (var c = 0; c < children; c++)
            {
                spans.Add(Child(root, offsetNs: random.Next(durationMs + 1) * Ms, status: RandomStatus(random)));
            }

            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(spans));
            foreach (var span in spans)
            {
                perWriter[random.Next(Writers)].Add(span);
            }

            expected[root.TraceId] = new Expected(root.TraceId, start, spans.Count, spans.Any(s => s.Status == SpanStatus.Error), doomed);
        }

        foreach (var list in perWriter)
        {
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        }

        var keptCount = expected.Values.Count(e => !e.Doomed);
        var keptIds = expected.Values.Where(e => !e.Doomed).Select(e => e.TraceId).ToArray();
        using var stop = new CancellationTokenSource();
        var readerQueries = 0L;

        // 2. Readers: watch for torn or regressing views while writes and eviction happen.
        var readers = Enumerable.Range(0, Readers).Select(r => Task.Run(() =>
        {
            var rng = new Random(r);
            var lastSpanCount = new Dictionary<Guid, int>();
            var lastKeptTotal = 0;
            while (!stop.IsCancellationRequested)
            {
                // Kept range: nothing in it is ever evicted, so its total may only grow.
                var kept = store.Query(new TraceQuery(Limit: 1 + rng.Next(TraceQuery.MaxLimit), AfterNs: KeptFrom));
                Assert.InRange(kept.Total, lastKeptTotal, keptCount);
                lastKeptTotal = kept.Total;
                CheckSummaries(kept.Traces, lastSpanCount);

                // Random sub-range over everything, including doomed traces.
                var lo = NowNs - (20 * Minute) + rng.NextInt64(20 * Minute);
                var hi = lo + 1 + rng.NextInt64(5 * Minute);
                var ranged = store.Query(new TraceQuery(Limit: 50, AfterNs: lo, BeforeNs: hi));
                Assert.InRange(ranged.Total, ranged.Traces.Count, expected.Count);
                Assert.All(ranged.Traces, t => Assert.InRange(t.StartNs, lo, hi - 1));
                CheckSummaries(ranged.Traces, lastSpanCount);

                // Detail reads: kept traces that were listed must stay gettable and consistent.
                foreach (var summary in kept.Traces.Take(5))
                {
                    Assert.True(store.TryGetTrace(summary.TraceId, out var spans));
                    CheckDetail(summary, spans);
                }

                var probe = keptIds[rng.Next(keptIds.Length)];
                if (store.TryGetTrace(probe, out var probeSpans))
                {
                    Assert.InRange(probeSpans.Count, 1, expected[probe].SpanCount);
                }

                Interlocked.Increment(ref readerQueries);
            }
        })).ToArray();

        void CheckSummaries(IReadOnlyList<TraceSummary> traces, Dictionary<Guid, int> lastSpanCount)
        {
            for (var i = 0; i < traces.Count; i++)
            {
                var t = traces[i];
                var truth = expected[t.TraceId];
                Assert.Equal(truth.RootStartNs, t.StartNs);
                Assert.InRange(t.SpanCount, 1, truth.SpanCount);
                if (t.Status == SpanStatus.Error)
                {
                    Assert.True(truth.HasError);
                }

                if (lastSpanCount.TryGetValue(t.TraceId, out var last))
                {
                    Assert.True(t.SpanCount >= last, $"span_count for {t.TraceId} went {last} -> {t.SpanCount}");
                }

                lastSpanCount[t.TraceId] = t.SpanCount;
                if (i > 0)
                {
                    var prev = traces[i - 1];
                    Assert.True(prev.StartNs > t.StartNs || (prev.StartNs == t.StartNs && prev.TraceId.CompareTo(t.TraceId) > 0));
                }
            }
        }

        void CheckDetail(TraceSummary summary, IReadOnlyList<Span> spans)
        {
            Assert.InRange(spans.Count, summary.SpanCount, expected[summary.TraceId].SpanCount);
            Assert.All(spans, s => Assert.Equal(summary.TraceId, s.TraceId));
            Assert.Equal(spans.Count, spans.Select(s => s.SpanId).Distinct().Count());
            for (var i = 1; i < spans.Count; i++)
            {
                Assert.True(spans[i - 1].StartNs <= spans[i].StartNs);
            }
        }

        // 3. Evictor: runs continuously against the doomed range.
        var evicted = 0L;
        var evictor = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Interlocked.Add(ref evicted, store.EvictOlderThan(EvictCutoff));
            }
        });

        // 4. Writers: emitter-sized batches (~10 spans).
        var writers = perWriter.Select(spans => Task.Run(() =>
        {
            for (var i = 0; i < spans.Count; i += BatchSpans)
            {
                store.Ingest(spans.GetRange(i, Math.Min(BatchSpans, spans.Count - i)));
            }
        })).ToArray();

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await Task.WhenAll(readers.Append(evictor));
        store.EvictOlderThan(EvictCutoff); // doomed spans that arrived after the evictor's last pass

        // 5. Exact final state.
        var all = store.Query(new TraceQuery(Limit: TraceQuery.MaxLimit));
        Assert.Equal(keptCount, all.Total);
        Assert.Equal(keptCount, store.Query(new TraceQuery(AfterNs: KeptFrom)).Total);
        Assert.Equal(0, store.Query(new TraceQuery(BeforeNs: KeptFrom)).Total);
        foreach (var truth in expected.Values)
        {
            if (truth.Doomed)
            {
                Assert.False(store.TryGetTrace(truth.TraceId, out _));
                continue;
            }

            Assert.True(store.TryGetTrace(truth.TraceId, out var spans));
            Assert.Equal(truth.SpanCount, spans.Count);
            Assert.Equal(truth.HasError, spans.Any(s => s.Status == SpanStatus.Error));
        }

        Assert.True(Interlocked.Read(ref readerQueries) > 0, "readers never ran");
        Assert.True(Interlocked.Read(ref evicted) > 0, "evictor never removed anything");
        Assert.Equal(0, store.EvictOlderThan(EvictCutoff)); // nothing left below the cutoff, orphans included
    }

    private static SpanStatus RandomStatus(Random random) => random.Next(100) < 5 ? SpanStatus.Error : SpanStatus.Ok;
}
