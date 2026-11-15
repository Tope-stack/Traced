using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Traced.Benchmarks;

/// <summary>
/// `http <baseUrl> <traces> <workers>`: timed loop against a running server.
/// 1) preload <traces> traces as fast as <workers> clients can POST emitter-shaped batches (current timestamps);
/// 2) GET /traces latency idle; 3) GET /traces latency while 20 clients POST at ~10x the emitter's default rate.
/// </summary>
internal static class HttpLoad
{
    private const int Samples = 200;

    public static async Task Run(Uri baseUrl, int traces, int workers)
    {
        using var http = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
        Console.WriteLine($"target {baseUrl}  traces {traces}  workers {workers}");

        // 1. Preload.
        var perWorker = traces / workers;
        var postLatencies = new List<double>[workers];
        var spansSent = 0L;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(async w =>
        {
            var random = new Random(w);
            var latencies = postLatencies[w] = [];
            var sent = 0;
            while (sent < perWorker)
            {
                var (json, traceCount, spanCount) = BatchJson(random);
                var t0 = Stopwatch.GetTimestamp();
                using var response = await http.PostAsync(new Uri("/spans", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"));
                latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                if ((int)response.StatusCode != 202)
                {
                    throw new InvalidOperationException($"POST /spans returned {(int)response.StatusCode}");
                }

                sent += traceCount;
                Interlocked.Add(ref spansSent, spanCount);
            }
        }));
        sw.Stop();
        var allPosts = postLatencies.SelectMany(l => l).ToList();
        Print("preload_requests", allPosts.Count);
        Print("preload_spans", spansSent);
        Print("preload_seconds", sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
        Print("preload_req_per_s", (allPosts.Count / sw.Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture));
        Print("preload_spans_per_s", (spansSent / sw.Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture));
        PrintPercentiles("post_spans_ms", allPosts);
        var totalLine = await http.GetStringAsync(new Uri("/traces?limit=1", UriKind.Relative));
        Print("traces_total_after_preload", totalLine[(totalLine.LastIndexOf(':') + 1)..].TrimEnd('}'));

        // 2. Idle GET latency.
        foreach (var path in Paths())
        {
            PrintPercentiles($"idle GET {path}", await TimeGets(http, path));
        }

        // 3. GET latency under ~10x emitter load (20 clients × ~16.7 req/s ≈ 333 req/s ≈ 1,000 traces/s).
        using var stop = new CancellationTokenSource();
        var loadRequests = 0L;
        var load = Enumerable.Range(0, 20).Select(async w =>
        {
            var random = new Random(1000 + w);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(60));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    var (json, _, _) = BatchJson(random);
                    using var response = await http.PostAsync(new Uri("/spans", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), stop.Token);
                    Interlocked.Increment(ref loadRequests);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }).ToArray();

        await Task.Delay(1000);
        var loadSw = Stopwatch.StartNew();
        var startRequests = Interlocked.Read(ref loadRequests);
        foreach (var path in Paths())
        {
            PrintPercentiles($"loaded GET {path}", await TimeGets(http, path));
        }

        var achieved = (Interlocked.Read(ref loadRequests) - startRequests) / loadSw.Elapsed.TotalSeconds;
        await stop.CancelAsync();
        await Task.WhenAll(load);
        Print("background_post_req_per_s", achieved.ToString("F0", CultureInfo.InvariantCulture));
    }

    private static string[] Paths()
    {
        var now = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100;
        var fiveMinAgo = now - (5 * Workload.Minute);
        return
        [
            "/traces",
            "/traces?limit=1000",
            $"/traces?limit=1000&after={fiveMinAgo}&before={now}",
        ];
    }

    private static async Task<List<double>> TimeGets(HttpClient http, string path)
    {
        var latencies = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            using var response = await http.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseContentRead);
            _ = await response.Content.ReadAsByteArrayAsync();
            latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GET {path} returned {(int)response.StatusCode}");
            }
        }

        return latencies;
    }

    private static (string Json, int Traces, int Spans) BatchJson(Random random)
    {
        var nowNs = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks * 100;
        var sb = new StringBuilder("{\"spans\":[");
        var spans = 0;
        var traces = 0;
        while (spans < 10)
        {
            foreach (var s in Workload.Trace(random, nowNs))
            {
                if (spans++ > 0)
                {
                    sb.Append(',');
                }

                sb.Append(CultureInfo.InvariantCulture, $"{{\"trace_id\":\"{s.TraceId}\",\"span_id\":\"{s.SpanId}\",");
                if (s.ParentSpanId is { } parent)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"\"parent_span_id\":\"{parent}\",");
                }

                sb.Append(CultureInfo.InvariantCulture, $"\"service\":\"{s.Service}\",\"operation\":\"{s.Operation}\",\"start_time\":{s.StartNs},\"end_time\":{s.EndNs},\"status\":\"{(s.Status == Traced.Domain.SpanStatus.Error ? "error" : "ok")}\",\"tags\":{{}}}}");
            }

            traces++;
        }

        sb.Append("]}");
        return (sb.ToString(), traces, spans);
    }

    private static void PrintPercentiles(string name, List<double> samples)
    {
        samples.Sort();
        string P(double q) => samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(q * samples.Count) - 1)].ToString("F2", CultureInfo.InvariantCulture);
        Console.WriteLine($"{name,-60} n={samples.Count} p50={P(0.50)} p90={P(0.90)} p99={P(0.99)} max={samples[^1].ToString("F2", CultureInfo.InvariantCulture)} ms");
    }

    private static void Print(string name, object value) => Console.WriteLine($"{name,-36} {value}");
}
