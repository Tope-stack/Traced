# Measurements

Raw numbers only. Interpretation is in `docs/DECISIONS.md` (Phase 5 — done).

## Environment

| | |
|---|---|
| Machine | Apple M4 Pro, 12 cores (8 performance + 4 efficiency), 24 GB RAM |
| OS | macOS 15.3.1 (24D70), Darwin 24.3.0 |
| .NET | SDK 10.0.400, runtime Microsoft.NETCore.App 10.0.11, Arm64 RyuJIT |
| Build | Release for all benchmarks and the HTTP server; the stress test runs under `dotnet test` (Debug) |
| GC | Server GC for the benchmark process and the API server (ASP.NET default), unless a row says workstation |
| BenchmarkDotNet | 0.15.8 |
| Date | 2026-11-15 |

The workload is emitter-shaped (`benchmarks/Traced.Benchmarks/Workload.cs`, following `emitter/internal/emitter/emitter.go`):
- each trace is 1 root + 1–4 children;
- root duration is 50–500 ms;
- each span has a 5% chance of `error`;
- tags are empty;
- each trace's spans are shuffled with p = 0.3;
- batches add whole traces until they hold ≥ 10 spans.

Service and operation strings are fresh instances per span, as after JSON deserialization. Store: 32 shards, 30 min window.

## 1. Stress test (phase 5, PLAN §6)

`StressTests` setup:
- 32 writers × 5,000 traces = 160,000 traces, ~3.5 spans each.
- Every span is scattered to a random writer, in batches of 10.
- 8 concurrent readers and 1 concurrent evictor.
- 16,000 "doomed" traces are evicted during the run.

Command (each run):

```
dotnet test tests/Traced.Storage.Tests --no-build --filter Category=Stress
```

| Run | Result | Duration |
|---|---|---|
| 1 | Passed 1/1 | 14 s |
| 2 | Passed 1/1 | 14 s |
| 3 | Passed 1/1 | 14 s |
| 4 | Passed 1/1 | 10 s |
| 5 | Passed 1/1 | 14 s |
| 6 | Passed 1/1 | 14 s |
| 7 | Passed 1/1 | 14 s |
| 8 | Passed 1/1 | 13 s |
| 9 | Passed 1/1 | 12 s |
| 10 | Passed 1/1 | 14 s |

Mutation check: with the shard lock removed from `Ingest`, the test failed in 311 ms (`IndexOutOfRangeException`). The file was restored from git afterwards.

## 2. Store-level benchmarks (BenchmarkDotNet)

Command:

```
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- --filter '*' --artifacts <dir>
```

### 2.1 Ingest, per emitter-shaped batch (~3 traces, ~10–13 spans)

Job: WarmupCount=3, IterationCount=15, InvocationCount=1. A fresh store is built per iteration. Each invocation ingests 3,000 batches per thread. Times are per batch (`OperationsPerInvoke`). `Preloaded` is the number of rooted traces already in the store.

| Method | Preloaded | Mean | Error | StdDev | Allocated/batch |
|---|---|---:|---:|---:|---:|
| Ingest_1Thread | 0 | 3,051.2 ns | 460.11 ns | 430.39 ns | 2.54 KB |
| Ingest_12Threads | 0 | 772.6 ns | 354.17 ns | 331.29 ns | 2.45 KB |
| Ingest_1Thread | 180,000 | 1,966.1 ns | 67.27 ns | 59.63 ns | 2.21 KB |
| Ingest_12Threads | 180,000 | 401.8 ns | 23.16 ns | 20.53 ns | 2.76 KB |

`Ingest_12Threads` is wall time for 12 × 3,000 batches divided by 36,000, i.e. aggregate throughput.

### 2.2 Query (`ITraceStore.Query`)

Job: WarmupCount=3, IterationCount=15. `Traces` rooted traces are spread over the last 25 minutes. `Unfiltered` = no after/before (what the dashboard sends: `limit=1000`). `Last5Minutes` = `after = now − 5 min`, `before = now` (~1/5 of the traces in range).

| Method | Traces | Limit | Mean | Error | StdDev | Allocated |
|---|---:|---:|---:|---:|---:|---:|
| Query_Unfiltered | 6,000 | 20 | 76.34 μs | 0.567 μs | 0.502 μs | 81.14 KB |
| Query_Last5Minutes | 6,000 | 20 | 50.56 μs | 0.453 μs | 0.402 μs | 80.3 KB |
| Query_Unfiltered | 6,000 | 1000 | 472.03 μs | 4.001 μs | 3.742 μs | 570.53 KB |
| Query_Last5Minutes | 6,000 | 1000 | 85.07 μs | 0.366 μs | 0.324 μs | 136.12 KB |
| Query_Unfiltered | 180,000 | 20 | 5,522.92 μs | 793.449 μs | 742.192 μs | 88.24 KB |
| Query_Last5Minutes | 180,000 | 20 | 416.67 μs | 6.716 μs | 6.282 μs | 88.24 KB |
| Query_Unfiltered | 180,000 | 1000 | 13,663.30 μs | 860.157 μs | 804.591 μs | 2789.36 KB |
| Query_Last5Minutes | 180,000 | 1000 | 7,327.43 μs | 1,563.013 μs | 1,462.043 μs | 2789.36 KB |
| Query_Unfiltered | 300,000 | 20 | 12,018.62 μs | 425.276 μs | 397.803 μs | 88.74 KB |
| Query_Last5Minutes | 300,000 | 20 | 879.73 μs | 39.051 μs | 36.529 μs | 88.74 KB |
| Query_Unfiltered | 300,000 | 1000 | 20,130.05 μs | 164.214 μs | 153.606 μs | 2789.86 KB |
| Query_Last5Minutes | 300,000 | 1000 | 11,241.98 μs | 2,134.944 μs | 1,997.028 μs | 2789.86 KB |

### 2.3 TryGetTrace

Job: WarmupCount=3, IterationCount=15. `Hit` cycles through 1,000 existing rooted trace ids; `Miss` uses a new random id.

| Method | Traces | Mean | Error | StdDev | Allocated |
|---|---:|---:|---:|---:|---:|
| TryGetTrace_Hit | 6,000 | 49.75 ns | 0.952 ns | 0.891 ns | 52 B |
| TryGetTrace_Miss | 6,000 | 208.15 ns | 1.364 ns | 1.275 ns | 0 B |
| TryGetTrace_Hit | 300,000 | 60.33 ns | 0.532 ns | 0.471 ns | 51 B |
| TryGetTrace_Miss | 300,000 | 232.68 ns | 4.240 ns | 3.759 ns | 0 B |

### 2.4 Eviction

`EvictionBenchmarks`: a single `EvictOlderThan` call removes the whole expired backlog. Job: WarmupCount=2, IterationCount=10, InvocationCount=1, with a fresh store per iteration. With `Shards = 1` the whole call is one lock hold; with 32 shards it is 32 sequential holds.

| Backlog (traces) | Shards | Mean | Error | StdDev | Median |
|---:|---:|---:|---:|---:|---:|
| 10,000 | 1 | 7.699 ms | 0.9348 ms | 0.6183 ms | 7.453 ms |
| 10,000 | 32 | 3.586 ms | 3.4371 ms | 2.2734 ms | 5.238 ms |
| 100,000 | 1 | 13.212 ms | 0.3856 ms | 0.2295 ms | 13.180 ms |
| 100,000 | 32 | 9.564 ms | 0.5298 ms | 0.3505 ms | 9.621 ms |
| 300,000 | 1 | 54.787 ms | 5.2107 ms | 3.1008 ms | 55.837 ms |
| 300,000 | 32 | 32.795 ms | 0.5777 ms | 0.3821 ms | 32.783 ms |

`EvictionTickBenchmarks`: one tick against a store holding 300,000 live traces plus `Expired` expired traces. Same job.

| Expired | Mean | Error | StdDev |
|---:|---:|---:|---:|
| 0 | 24.78 μs | 4.140 μs | 2.738 μs |
| 1,000 | 230.48 μs | 5.316 μs | 3.164 μs |

## 3. Memory: store holding N traces

Command:

```
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- memory 300000
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- memory 180000
DOTNET_gcServer=0 dotnet run -c Release --project benchmarks/Traced.Benchmarks -- memory 300000
```

Values come after two forced compacting full GCs. `GC.GetTotalMemory(true)` and `GCMemoryInfo.HeapSizeBytes` disagree. `HeapSizeBytes` is the one that is stable across GC modes, and both are listed.

| Metric | 300k, server GC | 180k, server GC | 300k, workstation GC |
|---|---:|---:|---:|
| traces | 300,000 | 180,000 | 300,000 |
| avg spans/trace (first 1,000 listed) | 3.44 | 3.45 | 3.44 |
| est. spans | 1,030,500 | 621,000 | 1,030,500 |
| fill time | 901 ms | 533 ms | 1,335 ms |
| `GCMemoryInfo.HeapSizeBytes` | 380.0 MB | 223.0 MB | 380.7 MB |
| `GCMemoryInfo.TotalCommittedBytes` | 482.1 MB | 286.9 MB | 405.3 MB |
| `GC.GetTotalMemory(true)` delta | 717.8 MB | 428.0 MB | 461.8 MB |
| working set, baseline → after | 38.4 → 524.9 MB | 39.0 → 329.3 MB | 35.9 → 446.9 MB |

From `HeapSizeBytes`, 300k traces is ≈ 1,328 bytes/trace (≈ 386 bytes/span).

## 4. HTTP (real server, timed loop)

The server is `src/Traced.Api` in Release, on :18080. Port 8080 is held by an unrelated container. A fresh server process is started per run.

```
dotnet build src/Traced.Api -c Release
ASPNETCORE_URLS=http://0.0.0.0:18080 Logging__LogLevel__Default=Warning dotnet src/Traced.Api/bin/Release/net10.0/Traced.Api.dll
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- http http://localhost:18080 <traces> 20
```

Each run has three phases:
1. **Preload.** 20 clients POST emitter-shaped batches as fast as possible, with timestamps = now.
2. **Idle.** 200 sequential GETs per path.
3. **Loaded.** The same GETs while 20 clients POST at a 60 ms interval each (~333 req/s ≈ 1,000 traces/s, about 10× the emitter default).

Because the preload stamps every trace with "now", the `after/before` (last 5 min) path covers all preloaded traces in these runs. RSS is `ps -o rss=` of the server process.

| Metric | 6k, server GC | 300k, server GC | 300k, workstation GC (`DOTNET_gcServer=0`) |
|---|---|---|---|
| server RSS idle (before) | 83 MB | 83 MB | 82 MB |
| preload requests | 1,844 | 91,738 | 91,738 |
| preload spans | 21,098 | 1,050,058 | 1,050,058 |
| preload time | 0.09 s | 2.25 s | 2.49 s |
| preload req/s | 20,216 | 40,792 | 36,896 |
| preload spans/s | 231,303 | 466,914 | 422,327 |
| POST /spans ms p50 / p90 / p99 / max | 0.51 / 0.65 / 34.99 / 37.39 | 0.34 / 0.49 / 0.84 / 151.58 | 0.33 / 0.53 / 3.92 / 37.41 |
| `total` after preload | 6,020 | 300,027 | 300,027 |
| idle `GET /traces` p50 / p90 / p99 / max ms | 0.77 / 0.82 / 1.08 / 1.39 | 11.38 / 30.04 / 31.04 / 31.81 | 9.78 / 22.49 / 23.00 / 23.73 |
| idle `GET /traces?limit=1000` | 2.66 / 2.93 / 6.22 / 9.52 | 17.41 / 17.72 / 20.14 / 20.40 | 15.10 / 17.60 / 20.09 / 145.62 |
| idle `GET /traces?limit=1000&after&before` (5 min) | 2.73 / 3.02 / 4.22 / 5.35 | 17.61 / 18.45 / 23.14 / 43.45 | — |
| loaded `GET /traces` | 0.91 / 1.25 / 2.22 / 3.85 | 11.35 / 11.67 / 13.65 / 25.41 | 9.77 / 10.14 / 19.90 / 27.08 |
| loaded `GET /traces?limit=1000` | 4.20 / 5.05 / 7.72 / 10.99 | 17.79 / 18.46 / 20.49 / 63.76 | 15.25 / 19.54 / 23.55 / 23.99 |
| loaded `GET /traces?limit=1000&after&before` (5 min) | 0.87 / 1.02 / 2.50 / 3.51 | 17.42 / 18.32 / 23.10 / 35.80 | — |
| background POST req/s achieved | 333 | 335 | — |
| server RSS after run | 434 MB | 1,793 MB | 847 MB (645 MB after 10 s idle) |
| server log error lines | 0 | 0 | — |

`total` exceeds the target slightly because each client sends whole traces until it reaches its share.
