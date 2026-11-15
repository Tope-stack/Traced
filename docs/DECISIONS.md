# Decisions log

Agent-maintained. Newest entries at the bottom.

## Pre-phase 0 — PLAN.md corrections from emitter source

Source checked: `benx421/traced` @ `8f7918c`, `emitter/internal/emitter/emitter.go` and `emitter/cmd/emitter/main.go`.

Changes to PLAN.md §1 (facts only; no §3 design change was needed):

1. **Shuffle granularity.** PLAN said "~30% of batches are shuffled". The code shuffles each *trace's* span slice with p=`OUT_OF_ORDER_PROB` (default 0.3) inside `generateTrace`; traces in a batch keep generation order. The consequence (store must not assume ordering) is unchanged.
2. **Batch size unit.** PLAN said "batches of ≤10 traces". `BATCH_SIZE` (default 10) is a span threshold: the worker appends whole traces while `len(batch) < BatchSize`, so a batch is 10–13 spans, about 3 traces. Validation's 1–500 span limit is never approached by the emitter.
3. **Rate unit.** PLAN said "5 req/s". `RATE_PER_WORKER` drives the sleep between *trace generations*, so it is 5 traces/s/worker; requests are ~1/3 of that. The ~100 traces/s estimate stands. Added that the default `DURATION` is 60 s (~6k traces), and the 180k–300k figure applies only to a sustained 30-min run.
4. **Child timestamps.** Added: child start = root start + [0, root duration] ms, so children never start before the root; child end may exceed root end by up to 10 ms. Implication: don't validate child-within-parent bounds, and an orphan's `FirstSeenNs` is ≥ its root's start.
5. §8 span-per-trace estimate corrected from ~4 to ~3.5 (1 root + 1–4 children).

Confirmed unchanged: retries send a newly generated batch (failed batch is discarded); verifier uses `limit=min(sent,1000)`, requires `total >= expected`, and checks span counts for 10% of expected traces found in the returned page.

Housekeeping: moved `PLAN.md` → `docs/PLAN.md` and `traced-builder.md` → `.claude/agents/traced-builder.md`; copied `spec/openapi.yaml` from the harness.

## Phase 0 — plan

Files to create:
- `global.json` (SDK 10.0.x, `rollForward: latestFeature`), `Directory.Build.props` (net10.0, nullable, implicit usings, LangVersion latest, TreatWarningsAsErrors), `.editorconfig`, `.gitignore`
- `Traced.slnx` with `src/Traced.Domain`, `src/Traced.Storage`, `src/Traced.Api`, `tests/Traced.Storage.Tests`, `tests/Traced.Api.Tests` (Domain.Tests and tools/LoadGen deferred)
- References: Api -> Storage -> Domain; Storage.Tests -> Storage; Api.Tests -> Api
- `src/Traced.Api/Program.cs` (composition only, binds `http://0.0.0.0:8080`), `src/Traced.Api/Endpoints/HealthEndpoints.cs` (`MapGroup` extension), `src/Traced.Api/Contracts/StatusResponse.cs` + `TracedJsonContext.cs` (source-gen, SnakeCaseLower)

Tests: `tests/Traced.Api.Tests/HealthEndpointTests.cs` — `GET /health` returns 200, `application/json`, body exactly `{"status":"ok"}` (spec `StatusResponse` example).

## Phase 0 — done

Built: `global.json` (SDK 10.0.400, `rollForward: latestFeature`), `Directory.Build.props` (net10.0, LangVersion latest, nullable, implicit usings, TreatWarningsAsErrors, EnforceCodeStyleInBuild, AnalysisLevel latest), `.editorconfig`, `.gitignore`, `Traced.slnx`, the five projects and references from §2, `GET /health` (`Endpoints/HealthEndpoints.cs`, `MapGroup` extension) returning `200 {"status":"ok"}` per spec `StatusResponse`, a source-gen `TracedJsonContext` (SnakeCaseLower, omit nulls) registered ahead of reflection, and one `WebApplicationFactory` test.

Checks: `dotnet build -warnaserror` 0 warnings/0 errors; `dotnet test` 1 passed (Api.Tests), Storage.Tests has no tests yet ("No test is available", exit 0).

Deviations from PLAN.md:
- **`Traced.slnx`, not `Traced.sln`.** `dotnet new sln` on SDK 10 defaults to `.slnx`; it is XML, diffs and merges cleanly, and `dotnet build/test` and current IDEs read it. No tooling here needs the legacy format.
- **Default bind is conditional.** `Program.cs` calls `UseUrls("http://0.0.0.0:8080")` only when no `urls` config is set, so `ASPNETCORE_URLS`/`--urls` still override (PLAN §4 asks for an env override). Deleted the template's `launchSettings.json` so `dotnet run` doesn't silently bind a random localhost port.
- Removed the template's `coverlet.collector` (not on the allowed package list). `Microsoft.AspNetCore.Mvc.Testing` pinned to 10.0.11 (11.0.0 targets net11 only).
- Domain.Tests and tools/LoadGen not created (optional / phase 6).

Environment issue: port 8080 on this machine is held by an unrelated Docker container (`innpay_api`). The server's default 8080 bind fails with "address already in use" and `curl localhost:8080/health` reaches that container (404). Verified instead with `ASPNETCORE_URLS=http://0.0.0.0:18080`: 200, `{"status":"ok"}`. That container was left running.

Open questions:
- Tope: stop `innpay_api` (or remap it) before phase 6, since the emitter targets `:8080`.

### Worth understanding
- `src/Traced.Api/Program.cs:7` — `ASPNETCORE_URLS` reaches config as the `urls` key; calling `UseUrls` unconditionally would override the env var, so the default is applied only when it is absent.
- `src/Traced.Api/Contracts/TracedJsonContext.cs:5` and `Program.cs:13` — the source-generated context produces serializers at compile time (no reflection, trimming-safe); inserting it at index 0 of `TypeInfoResolverChain` makes it win over the reflection fallback.
- `src/Traced.Api/Endpoints/HealthEndpoints.cs:9-11` — `MapGroup("/health")` + `MapGet("")` yields the exact route `/health`; `MapGet("/")` would give `/health/`. Groups are where later phases hang shared metadata (tags, CORS) without touching `Program.cs`.

## Phase 1 — plan

Domain (`src/Traced.Domain`): `Span.cs`, `SpanStatus.cs`, `TraceSummary.cs`, `TraceQuery.cs`, `TraceQueryResult.cs` (Total + page; `Query` needs both), `ITraceStore.cs` (interface only), `WindowOptions.cs`, `TimeExtensions.cs`.

Api (`src/Traced.Api`):
- `Contracts/`: `IngestRequest.cs` (inbound, all fields nullable so "missing" is detectable), `SpanDto.cs`, `TraceSummaryDto.cs`, `TraceListResponse.cs`, `TraceDetailResponse.cs`, `ErrorResponse.cs`; register all in `TracedJsonContext`.
- `Validation/`: `Result.cs`, `IngestRequestValidator.cs` (`Parse(bytes)` and `Validate(dto)` -> `Result<IReadOnlyList<Span>>`), `TraceQueryParser.cs` (`Parse(limit, after, before)` -> `Result<TraceQuery>`), `TraceIdParser` (path UUID).
- `Mapping/`: `ContractMapping.cs` (domain -> DTO).

Tests (written first):
- `tests/Traced.Api.Tests/Validation/IngestRequestValidatorTests.cs`: one test (or theory row) per §5 body and span rule, boundary values on both sides, whole-batch rejection.
- `tests/Traced.Api.Tests/Validation/TraceQueryParserTests.cs`: limit default/range/format, after/before format/range, after >= before.
- `tests/Traced.Api.Tests/Mapping/ContractMappingTests.cs`: exact JSON shapes vs spec examples (snake_case, nulls omitted, lowercase UUIDs).
- New `tests/Traced.Domain.Tests` (PLAN marks it optional): `TimeExtensions` with `FakeTimeProvider`, `WindowOptions` defaults.

## Phase 1 — done

Built: Domain types (`Span`, `SpanStatus`, `TraceSummary`, `TraceQuery`, `TraceQueryResult`, `ITraceStore`, `WindowOptions` with `CutoffNs`, `TimeExtensions.GetUtcNowUnixNanoseconds`); wire contracts (`IngestRequest`/`IngestSpan` inbound, `SpanDto`, `TraceSummaryDto`, `TraceListResponse`, `TraceDetailResponse`, `ErrorResponse`) all in the source-gen `TracedJsonContext`; `Validation/` (`Result<T>`, `IngestRequestValidator`, `TraceQueryParser`, `TraceIdParser`); `Mapping/ContractMapping` (domain -> DTO). No endpoints added. Tests: 126 (Api 121, Domain 5), all written before the code and confirmed red (compile failure) first.

Error message shape: spec `Error` example is `"invalid request body: missing required field span_id"`. Ingest errors use `invalid request body: spans[i]: <rule>` (body-level ones omit `spans[i]`), query errors `invalid query parameter: <rule>`, path errors `invalid trace_id: must be a valid UUID`. The first error found is returned.

Deviations / spec-vs-PLAN:
- **Empty `service`/`operation` accepted.** PLAN §5 says non-empty; the spec only has `required` + `maxLength: 256`, no `minLength`. Followed the spec. Empty string still counts as present; JSON `null` counts as missing.
- **UUIDs must be canonical 8-4-4-4-12** (`Guid.TryParseExact(.., "D")`, either case). `Guid.TryParse` would also accept braces, parens and 32-digit forms, which aren't `format: uuid`. Version/variant bits are not checked (the nil UUID passes).
- **Inbound mapping lives in the validator**, not `Mapping/`. Each field is parsed once, and an unvalidated DTO can't be mapped. `Mapping/` is outbound only.
- **Malformed JSON is the one exception converted to a Result** (`IngestRequestValidator.Parse` catches `JsonException`). System.Text.Json has no non-throwing deserialize. Wrong JSON types (number for a string field, string or float for a timestamp, non-string tag value, int64 overflow) fall into this path. The message includes only `ex.Path`, never `ex.Message` (which leaks .NET type names).
- Lengths count UTF-16 code units (`string.Length`), not code points. A 256-char emoji service name would fail early. The spec doesn't define the unit.
- Spans without tags (or with `{}`) get an empty map in Domain and serialise with `tags` omitted. "Absent" and `{}` are not distinguished on the way out.
- `parent_span_id: null` is treated as absent (a root).
- `ITraceStore` signatures fixed: `Ingest` returns void, `Query` returns `TraceQueryResult(Total, Traces)`, `TryGetTrace(Guid, out IReadOnlyList<Span>?)`, `EvictOlderThan` returns the evicted count. Phase 2 may adjust if it needs to.
- Added `tests/Traced.Domain.Tests` (optional in PLAN) with `Microsoft.Extensions.TimeProvider.Testing` 10.0.0.
- No limit was found where spec and PLAN §5 disagree numerically (1–500 spans, 256, 50 tags, 1024, limit 1–1000, after/before ≥ 0, after < before all match).

Open questions:
- Repeated query keys (`?limit=1&limit=2`): the parser takes one string. Phase 4 endpoint must decide (proposal: 400).
- Unknown JSON fields are ignored, not rejected (spec doesn't set `additionalProperties: false`).

### Worth understanding
- `src/Traced.Api/Contracts/IngestRequest.cs:9` and `src/Traced.Api/Validation/IngestRequestValidator.cs:82` — the inbound DTO is all-nullable. That lets STJ deserialize anything structurally valid, so "missing field X" becomes a readable validator message rather than a deserializer exception. `required`/non-nullable members would push those 400s into exceptions.
- `src/Traced.Api/Validation/Result.cs:15` — `[MemberNotNullWhen]` tells the nullable flow analysis that `Value` is non-null after `IsSuccess` is true, and `Error` non-null after it is false. Callers get compiler-checked access without `!`.
- `src/Traced.Api/Validation/TraceQueryParser.cs:61` — `NumberStyles.AllowLeadingSign` (not `Integer`, which also allows surrounding whitespace) plus `InvariantCulture` makes `" 5"`, `"1.5"` and `"0x10"` fail. A negative number then parses and gets a range error instead of a format error.

## Decision — repeated query keys (Tope, 2026-11-15)

Repeated query keys on `GET /traces` (e.g. `?limit=1&limit=2`) return **400** with an `{"error":"..."}` body naming the key. Don't pick the first or last value. Implement in `TraceQueryParser` (with tests) as part of phase 4.

## Phase 2 — plan

Files:
- `src/Traced.Storage/TraceEntry.cs`: per-trace mutable state (Root, Spans, SpanIds, HasError, FirstSeenNs). Internal, touched only under its shard lock.
- `src/Traced.Storage/ShardedTraceStore.cs`: `ITraceStore` per PLAN §3.1–3.4. Power-of-two shards, a plain `lock` per shard, `Dictionary` + `SortedSet` rooted index + orphan index, grouping by shard per batch, summaries built inside the lock, get-trace spans copied under the lock and sorted outside it.
- `src/Traced.Domain/ITraceStore.cs`: drop `EvictOlderThan` for now. It returns in phase 3 with its implementation, so phase 2 ships no stub.
- `tests/Traced.Storage.Tests`: add `Microsoft.Extensions.TimeProvider.Testing`.

Tests (first): `tests/Traced.Storage.Tests/` — `TestSpans.cs` (builders), `IngestAssemblyTests.cs` (out-of-order ×3, dedupe, error propagation, root-less hidden, second root, span_count includes early children), `QueryTests.cs` (ordering, tie-break, limit, total, after/before, cross-shard merge against a brute-force model, snapshot immutability), `WindowTests.cs` (stale dropped on ingest, cutoff boundary, query/get hide aged-out traces without eviction), `GetTraceTests.cs` (asc order, copy isolation, 404 cases), `ConcurrencySmokeTests.cs`.

## Phase 2 — done

Built: `TraceEntry` (internal, mutable, shard-owned) and `ShardedTraceStore : ITraceStore` per PLAN §3.1–3.4.
- Ingest: stale spans (`StartNs < cutoff`) are dropped, the rest grouped into per-shard buckets, then each touched shard lock is taken once. Dedupe is by span_id within a trace (first copy wins). `HasError` is OR'd on insert. First parentless span wins as root. The orphan index is re-keyed when an earlier child lowers `FirstSeenNs`.
- Query: bounds are clamped to `[max(after, cutoff), before)`. Each shard contributes `view.Count` and its top `limit` summaries, built under the lock. These are merged with one sort, `(StartNs desc, TraceId desc)`, then cut to `limit`.
- Get: clamped to the window, spans copied under the lock and sorted `(StartNs, SpanId)` ascending outside it.

Tests: 55 new storage tests. The repo total is now 181 (Api 121, Storage 55, Domain 5). They were written first and confirmed red (compile failure). The storage tests ran 5× in a row green.

Deviations:
- **`EvictOlderThan` removed from `ITraceStore` for phase 2.** It returns in phase 3 with its implementation and tests. A `NotImplementedException` stub would be a public method that crashes.
- **Merge is a sort of ≤ shards × limit candidates**, not a k-way merge. At most 32 × 1000 = 32k entries. Simpler, and its cost gets measured in phase 5 before anything cleverer is tried.
- **Tie-break order is defined as `TraceId` descending using `Guid.CompareTo`**, which is the reverse of the `SortedSet` tuple order. `Guid.CompareTo` compares fields unsigned, so `Guid.Empty`/`Guid.AllBitsSet` are true min/max sentinels for the range keys. `Cross_shard_merge_matches_a_brute_force_model` exercises the boundary (roots exactly at `before - 1`).
- Get-trace ties on `StartNs` are ordered by `SpanId` so output is deterministic.
- Dedupe scope is per trace. The same span_id under two trace_ids is stored in both.
- A stale child of a fresh root is dropped (lower `span_count`). Per PLAN §1 a child never starts before its root, so the emitter can't produce this.

Open questions:
- Orphan-index bookkeeping has no observable effect until phase 3's orphan eviction, so it is only covered indirectly here. Phase 3 tests will pin it.
- `view.Count` is O(matches) per shard per query (PLAN §8). Not yet measured.

### Worth understanding
- `src/Traced.Storage/ShardedTraceStore.cs:49-67` — grouping a batch into per-shard buckets before locking means a 13-span batch takes ≤13 (usually ~3) lock acquisitions, each once. Locking per span would let another writer interleave within a trace's spans and multiply the lock traffic.
- `src/Traced.Storage/ShardedTraceStore.cs:97-108` — `GetViewBetween` gives a live range over the tree. `Reverse()` walks it from the newest end, so taking `limit` is O(log n + limit). `view.Count`, however, walks the whole range (O(matches)), which is why `total` is the likely hot spot. The `TraceSummary` is built here, inside the lock, because the `TraceEntry` it reads keeps mutating after the lock is released.
- `src/Traced.Storage/ShardedTraceStore.cs:197` and `:209` — `SortedSet` keys must never change while stored. When a trace's `FirstSeenNs` drops (an earlier child arrives) or it gains a root, the old `(FirstSeenNs, TraceId)` key is removed *using the value captured before the mutation* and re-added. Otherwise the tree would hold a key it can no longer find.

## Phase 3 — plan

Files:
- `src/Traced.Domain/ITraceStore.cs`: restore `int EvictOlderThan(long cutoffNs)`.
- `src/Traced.Storage/ShardedTraceStore.cs`: implement it per §3.5, one shard lock at a time, popping `Rooted.Min` / `Orphans.Min` while below the cutoff and removing from `Traces`.
- `src/Traced.Storage/EvictionService.cs`: `BackgroundService`. `PeriodicTimer(EvictionInterval, TimeProvider)`; each tick computes `cutoff = now - window` and calls the store. Per-tick catch + log; cancellation exits cleanly.
- `src/Traced.Storage/Traced.Storage.csproj`: `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for `BackgroundService`/`ILogger` (shared framework, not a NuGet package; see done entry).
- `src/Traced.Api/Program.cs`: register `TimeProvider.System`, `WindowOptions` (from `WINDOW_MINUTES`), `ShardedTraceStore` as singleton `ITraceStore`, and the hosted `EvictionService`.

Tests (first):
- `tests/Traced.Storage.Tests/EvictionTests.cs`: cutoff boundary, orphan eviction, re-keyed orphan, rooted-from-orphan not evicted via a stale orphan key, late children after eviction, in-window traces untouched, eviction count, idempotence.
- `tests/Traced.Storage.Tests/EvictionServiceTests.cs`: ticks on `Advance` with the right cutoff, no tick before the interval, a throwing tick is logged and the next tick runs, stop on cancellation.
- `tests/Traced.Storage.Tests/ConcurrencySmokeTests.cs`: add eviction running alongside ingest and query.
- `tests/Traced.Api.Tests/CompositionTests.cs`: DI resolves a singleton `ITraceStore`, the hosted service is registered, `WINDOW_MINUTES` is honoured.

## Phase 3 — done

Built:
- `ITraceStore.EvictOlderThan(long cutoffNs)` is back. `ShardedTraceStore` implements it one shard lock at a time, popping `Rooted.Min` while `StartNs < cutoff` and `Orphans.Min` while `FirstSeenNs < cutoff`, removing each from `Traces`. It returns the count.
- `EvictionService : BackgroundService`: `PeriodicTimer(EvictionInterval, TimeProvider)`. Each tick does `cutoff = now - window` → `EvictOlderThan`. Per-tick catch-all logs at Error (source-generated `LoggerMessage`). Cancellation ends the loop and `ExecuteTask` completes successfully.
- `Program.cs` registers `TimeProvider.System`, `WindowOptions` (from `WINDOW_MINUTES`, default 30), `ShardedTraceStore` as singleton `ITraceStore`, and the hosted service. Still no /spans or /traces endpoints.
- `ShardedTraceStore` now also rejects `WindowMinutes <= 0` at construction, so a bad `WINDOW_MINUTES` fails at startup.

Tests: 23 new (Storage 74 with 19 new, Api 125 with 4 new, Domain 5; total 204). They were written first and confirmed red. The storage suite ran 10× in a row green. A boot smoke test on :18080 with `WINDOW_MINUTES=5` gave `/health` 200 and no hosted-service errors; the process was stopped afterwards.

Deviations:
- **`FrameworkReference Microsoft.AspNetCore.App` in Traced.Storage.** PLAN puts `EvictionService` (a `BackgroundService` with `ILogger`) in Storage, which needs the hosting and logging abstractions. The NuGet route (`Microsoft.Extensions.Hosting.Abstractions`) isn't on the allowed list. The shared framework is already on every machine that runs the Api, so it adds no package. Domain stays free of it. Alternative if Tope prefers: move `EvictionService` to `Traced.Api` (Storage would then be pure) or approve the NuGet package.
- **The timer is created in `StartAsync`, not `ExecuteAsync`.** Since .NET 10, `BackgroundService.StartAsync` runs `ExecuteAsync` on the thread pool. A timer created there can come into existence after a `FakeTimeProvider.Advance` (or, in production, a little later than start). That made the first test run fail: 4 service tests timed out waiting for ticks. `PeriodicTimer` latches one tick that fires before `WaitForNextTickAsync` is awaited, so nothing is lost.
- **Late children of an evicted trace start a fresh orphan.** This follows directly from §3: after eviction the trace_id has no entry, so a child ≥ cutoff creates a new orphan entry (`FirstSeenNs` = its start) and is evicted on the orphan path later. It never resurrects the trace, because the root is < cutoff and would be dropped on ingest. Pinned by `Late_child_of_an_evicted_trace_starts_a_fresh_orphan_and_does_not_resurrect_it`.
- A trace_id evicted and later reused by a fresh trace is treated as a new trace.

Open questions:
- Each shard's eviction holds that shard's lock for the whole pop loop. At default load that is ~3 traces/shard/tick. After a long pause (e.g. a laptop sleep) one tick could pop a large backlog under one lock. Phase 5 can measure; a per-tick cap is the simple fix if needed.

### Worth understanding
- `src/Traced.Storage/ShardedTraceStore.cs:194` and `:202` — both indexes are ordered oldest-first, so eviction is "pop `Min` until it's in the window". Each pop is O(log n) and the loop touches only what it removes, never the live traces. That is why the orphan index exists as a separate `SortedSet` instead of being found by scanning `Traces`.
- `src/Traced.Storage/EvictionService.cs:25` — `new PeriodicTimer(interval, timeProvider)` puts timer firing under the injected clock, so `FakeTimeProvider.Advance(1s)` fires a tick deterministically, with no real sleeping. Creating it before `base.StartAsync` matters because of .NET 10's thread-pool `ExecuteAsync` (see deviations).
- `src/Traced.Storage/EvictionService.cs:63` — the catch is inside the loop body, not around the loop. An exception ends only that tick. If the try/catch wrapped the `while`, one bad tick would end eviction for good, and memory would then grow without any error reaching a caller.

## Decision — Storage framework reference (Tope, 2026-11-15)

Keep `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in `Traced.Storage` so `EvictionService` can use `BackgroundService` and `ILogger`. Chosen over adding `Microsoft.Extensions.Hosting.Abstractions` (not on the allowed package list) and over moving `EvictionService` into Traced.Api (would break the PLAN §2 layout). Storage code should still use only `Microsoft.Extensions.*` hosting/logging types from it, not ASP.NET HTTP types. Domain stays framework-free.
