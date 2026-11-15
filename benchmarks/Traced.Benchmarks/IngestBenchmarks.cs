using BenchmarkDotNet.Attributes;
using Traced.Domain;
using Traced.Storage;

namespace Traced.Benchmarks;

/// <summary>Per-batch ingest cost for emitter-shaped batches (~3 traces, ~10–13 spans).</summary>
[SimpleJob(warmupCount: 3, iterationCount: 15, invocationCount: 1)]
[MemoryDiagnoser]
public class IngestBenchmarks
{
    private const int BatchesPerThread = 3_000;
    private const int Threads = 12;

    private List<Span[]>[] _perThread = [];
    private ShardedTraceStore _store = null!;

    /// <summary>Rooted traces already in the store before the measured batches arrive.</summary>
    [Params(0, 180_000)]
    public int Preloaded { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _perThread = Enumerable.Range(0, Threads)
            .Select(t => Workload.Batches(new Random(100 + t), BatchesPerThread * 3, Workload.NowNs - (5 * Workload.Minute), Workload.NowNs).Take(BatchesPerThread).ToList())
            .ToArray();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _store = Preloaded == 0 ? Workload.NewStore() : Workload.Filled(Preloaded);
        GC.Collect();
    }

    [Benchmark(OperationsPerInvoke = BatchesPerThread)]
    public void Ingest_1Thread()
    {
        foreach (var batch in _perThread[0])
        {
            _store.Ingest(batch);
        }
    }

    [Benchmark(OperationsPerInvoke = BatchesPerThread * Threads)]
    public void Ingest_12Threads()
    {
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            foreach (var batch in _perThread[t])
            {
                _store.Ingest(batch);
            }
        });
    }
}
