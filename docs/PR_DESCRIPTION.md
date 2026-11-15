# Traced: span-ingestion server in .NET 10

Draft PR description for the project as a whole (phases 0–7, built and merged phase by phase).

## What this is

This is an in-memory HTTP server for the [benx421/traced](https://github.com/benx421/traced) exercise. It implements `spec/openapi.yaml` (unmodified):

- `POST /spans` accepts a batch of up to 500 spans. One invalid span rejects the whole batch with a 400; stale spans are dropped silently.
- `GET /traces` lists rooted traces in the rolling window, newest root first, with `limit` (default 20, max 1000), `after` (inclusive) and `before` (exclusive), and a `total` counted before the limit.
- `GET /traces/{trace_id}` returns every span of a rooted, in-window trace in ascending start order. An invalid UUID gives 400; an unknown, orphaned or aged-out trace gives 404.
- `GET /health`.

All errors use the spec's `{"error":"..."}` body. CORS allows any origin for GET and POST.

## What was built

| Layer | Contents |
|---|---|
| `Traced.Domain` | `Span`, `TraceSummary`, `TraceQuery`, `TraceQueryResult`, `ITraceStore`, `WindowOptions`, `TimeProvider` → Unix ns. No ASP.NET or JSON references. |
| `Traced.Storage` | `ShardedTraceStore`: 32 power-of-two shards, a plain `lock` per shard, `Dictionary` + `SortedSet` index of rooted traces + a separate orphan index. Each batch is grouped by shard, so each shard lock is taken once per request. Summaries are built inside the lock. Reads clamp to `now − window`, so they don't depend on eviction. `EvictionService`: a `BackgroundService` on a `PeriodicTimer` driven by the injected `TimeProvider`; it catches and logs per tick. |
| `Traced.Api` | `Program.cs` (composition only); `Endpoints/` (`MapGroup` extensions); `Contracts/` (wire DTOs + source-generated `JsonSerializerContext`, `SnakeCaseLower`, nulls omitted); `Validation/` (pure validators returning `Result`, no exceptions for 4xx); `Mapping/`; a JSON error handler; 32 MiB body limit; workstation GC. |
| Tests | 267 xUnit tests: validators (every PLAN §5 rule), mapping shapes, store assembly/query/window/eviction, `EvictionService` with `FakeTimeProvider`, `WebApplicationFactory` endpoint/CORS/error tests, and a concurrency stress test. |
| `benchmarks/Traced.Benchmarks` | A BenchmarkDotNet suite, a memory probe and an HTTP load loop. Not run by `dotnet test`. |

The only NuGet packages are `Microsoft.Extensions.TimeProvider.Testing`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`, and `BenchmarkDotNet` (benchmarks only).

## How it was verified

All numbers are from an Apple M4 Pro (12 cores, 24 GB, macOS 15.3.1) on .NET 10.0.11. Full tables and the commands that produced them are in `docs/MEASUREMENTS.md`.

- **Build and tests.** `dotnet build -warnaserror` is clean (warnings are errors in every project). `dotnet test` passes with 267 tests, and was run twice per phase from phase 5 on.
- **Stress test.** 32 writers × 5,000 traces, spans scattered across writers and batches, 8 readers and a concurrent evictor. It asserts exact totals and span counts and checks that readers never see a torn view. It passed 10/10 consecutive runs. With the ingest lock removed it fails within 311 ms.
- **Real emitter and verifier** (harness @ `8f7918c`, Docker):

  | Run | Emitter sent | Verifier | Server RSS (peak) | `GET /traces?limit=1000` p50 / max | `POST /spans` p50 / p99 (server-side) |
  |---|---|---|---|---|---|
  | Default (20 workers, 60 s) | 14,039 traces / 49,377 spans | `expected=14039 found=14039 missing=0 span_mismatches=0` → `all checks passed` | 101 MB | 2.8 / 9.0 ms | 0.19 / 0.87 ms |
  | `WORKERS=200` (60 s) | 143,353 traces / 501,497 spans | `expected=143353 found=143353 missing=0 span_mismatches=0` → `all checks passed` | 286 MB | 9.6 / 17.0 ms | 0.06 / 0.47 ms |

- **Store benchmarks.**
  - `Query` with limit 1000 and no filter (the dashboard's request) takes 0.47 ms at 6k traces, 13.7 ms at 180k and 20.1 ms at 300k.
  - Ingest takes 0.4–3 µs per emitter batch.
  - `TryGetTrace` takes 50–60 ns.
  - Evicting a 300k backlog takes 32.8 ms in total (≈ 1 ms per shard lock).
  - The store uses ≈ 1.3 KB per trace (380 MB managed heap at 300k).
- **Real server on HTTP.** It sustained 37–41k POST req/s from 20 local clients.

## How to review

Suggested reading order:

1. `README.md`: how to build, run and exercise it.
2. `docs/PLAN.md` §1–§5: the facts and the design the code follows.
3. `src/Traced.Domain/ITraceStore.cs`, then `src/Traced.Storage/ShardedTraceStore.cs` and `TraceEntry.cs`: the concurrency core.
4. `src/Traced.Storage/EvictionService.cs`.
5. `src/Traced.Api/Program.cs`, then `Endpoints/`, `Validation/`, `Contracts/`, `Mapping/`.
6. Tests: `tests/Traced.Storage.Tests/StressTests.cs`, `IngestAssemblyTests.cs`, `WindowTests.cs`, `EvictionTests.cs`; `tests/Traced.Api.Tests/Endpoints/RoundTripTests.cs`, `SpanEndpointTests.cs`, `TraceEndpointTests.cs`.
7. `docs/DECISIONS.md`: start with "Summary of decisions". Each phase's "Worth understanding" list points at specific file:line locations.
8. `docs/MEASUREMENTS.md`: raw numbers.

## Open questions and known risks

These are consolidated in `docs/DECISIONS.md` under "Open questions and known risks". The main ones:

- A sustained 30-minute window at `WORKERS=200` (~4.3M traces) was not measured, by choice.
- The `SortedSet` view `Count` is O(n); a per-shard counter is deferred.
- `limit=1000` allocates 2.7 MB per query.
- There is no persistence.
- The first root wins.
- Future timestamps are accepted.

Design tradeoffs are written up separately by the author in `TRADEOFFS.md`.
