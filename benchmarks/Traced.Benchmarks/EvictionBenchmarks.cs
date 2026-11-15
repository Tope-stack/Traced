using BenchmarkDotNet.Attributes;
using Traced.Domain;
using Traced.Storage;

namespace Traced.Benchmarks;

/// <summary>
/// One eviction tick over an expired backlog (phase 3 open question: lock hold time after a long pause).
/// With 1 shard the whole tick is one lock hold; with 32 shards each hold is roughly tick / 32.
/// </summary>
[SimpleJob(warmupCount: 2, iterationCount: 10, invocationCount: 1)]
[MemoryDiagnoser]
public class EvictionBenchmarks
{
    private ShardedTraceStore _store = null!;

    [Params(10_000, 100_000, 300_000)]
    public int Backlog { get; set; }

    [Params(1, 32)]
    public int Shards { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _store = Workload.Filled(Backlog, shardCount: Shards);
        GC.Collect();
    }

    [Benchmark]
    public int EvictWholeBacklog() => _store.EvictOlderThan(long.MaxValue);
}

/// <summary>Steady-state tick at 300k in window: ~1 s of expired traces at 10x load (~1,000 traces) or none.</summary>
[SimpleJob(warmupCount: 2, iterationCount: 10, invocationCount: 1)]
public class EvictionTickBenchmarks
{
    private ShardedTraceStore _store = null!;
    private long _cutoff;

    [Params(0, 1_000)]
    public int Expired { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        _store = Workload.Filled(300_000);
        var random = new Random(7);
        foreach (var batch in Workload.Batches(random, Expired, Workload.NowNs - (29 * Workload.Minute), Workload.NowNs - (28 * Workload.Minute)))
        {
            _store.Ingest(batch);
        }

        _cutoff = Workload.NowNs - (26 * Workload.Minute); // the 300k live traces start at now-25 min or later
        GC.Collect();
    }

    [Benchmark]
    public int Tick() => _store.EvictOlderThan(_cutoff);
}
