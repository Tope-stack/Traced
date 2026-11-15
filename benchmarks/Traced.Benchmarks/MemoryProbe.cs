using System.Diagnostics;
using System.Globalization;
using Traced.Domain;

namespace Traced.Benchmarks;

/// <summary>`memory [traces]`: managed heap and working set of a store holding N emitter-shaped traces.</summary>
internal static class MemoryProbe
{
    public static void Run(int traces)
    {
        var baselineHeap = FullGcHeap();
        var baselineWs = Process.GetCurrentProcess().WorkingSet64;

        var sw = Stopwatch.StartNew();
        var store = Workload.Filled(traces);
        sw.Stop();

        var heap = FullGcHeap();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var result = store.Query(new TraceQuery(TraceQuery.MaxLimit));
        var spans = 0L;
        foreach (var t in result.Traces)
        {
            spans += t.SpanCount;
        }

        var avgSpans = (double)spans / result.Traces.Count;
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        Print("traces", result.Total);
        Print("avg_spans_per_trace (first 1000)", avgSpans.ToString("F2", CultureInfo.InvariantCulture));
        Print("est_spans", (long)(avgSpans * result.Total));
        Print("fill_time_ms", sw.ElapsedMilliseconds);
        Print("managed_heap_baseline_mb", Mb(baselineHeap));
        Print("managed_heap_after_mb", Mb(heap));
        Print("managed_heap_delta_mb", Mb(heap - baselineHeap));
        Print("bytes_per_trace", (heap - baselineHeap) / result.Total);
        Print("bytes_per_span_est", (long)((heap - baselineHeap) / (avgSpans * result.Total)));
        Print("gc_heap_size_mb", Mb(info.HeapSizeBytes));
        Print("gc_committed_mb", Mb(info.TotalCommittedBytes));
        Print("working_set_baseline_mb", Mb(baselineWs));
        Print("working_set_after_mb", Mb(process.WorkingSet64));
        Print("server_gc", System.Runtime.GCSettings.IsServerGC);
        GC.KeepAlive(store);
    }

    private static long FullGcHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private static void Print(string name, object value) => Console.WriteLine($"{name,-36} {value}");
}
