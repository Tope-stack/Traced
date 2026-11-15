using BenchmarkDotNet.Attributes;
using Traced.Domain;
using Traced.Storage;

namespace Traced.Benchmarks;

/// <summary>GET /traces at the store level: the SortedSet view Count question (PLAN §3.3, §8).</summary>
[SimpleJob(warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private ShardedTraceStore _store = null!;
    private TraceQuery _unfiltered = null!;
    private TraceQuery _last5Min = null!;

    [Params(6_000, 180_000, 300_000)]
    public int Traces { get; set; }

    [Params(20, 1000)]
    public int Limit { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _store = Workload.Filled(Traces);
        _unfiltered = new TraceQuery(Limit);
        _last5Min = new TraceQuery(Limit, AfterNs: Workload.NowNs - (5 * Workload.Minute), BeforeNs: Workload.NowNs);
        if (_store.Query(new TraceQuery(1)).Total != Traces)
        {
            throw new InvalidOperationException("setup did not produce the expected trace count");
        }
    }

    [Benchmark]
    public int Query_Unfiltered() => _store.Query(_unfiltered).Total;

    [Benchmark]
    public int Query_Last5Minutes() => _store.Query(_last5Min).Total;
}

[SimpleJob(warmupCount: 3, iterationCount: 15)]
[MemoryDiagnoser]
public class GetTraceBenchmarks
{
    private ShardedTraceStore _store = null!;
    private Guid[] _ids = [];
    private int _next;

    [Params(6_000, 300_000)]
    public int Traces { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _store = Workload.Filled(Traces);
        _ids = _store.Query(new TraceQuery(TraceQuery.MaxLimit)).Traces.Select(t => t.TraceId).ToArray();
    }

    [Benchmark]
    public int TryGetTrace_Hit()
    {
        var id = _ids[_next++ % _ids.Length];
        return _store.TryGetTrace(id, out var spans) ? spans.Count : 0;
    }

    [Benchmark]
    public bool TryGetTrace_Miss() => _store.TryGetTrace(Guid.NewGuid(), out _);
}
