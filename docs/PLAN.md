# Traced: .NET implementation plan

Source spec: https://github.com/benx421/traced (`spec/openapi.yaml` is the contract).
Target: .NET 10, ASP.NET Core Minimal APIs, in-memory storage, xUnit.

## 1. Facts from the spec and emitter source that drive the design

| Fact | Consequence |
|---|---|
| All spans of a trace are generated together and sent in the **same batch**. The shuffle is **per trace**: each trace's span slice is shuffled with p=0.3 (`OUT_OF_ORDER_PROB`); traces within a batch stay in generation order. | Out-of-order means "child before root *within* a batch", but the store must not assume that. Treat every span independently. |
| Root `start_time` = `time.Now()` when generated (ns). Each trace is 1 root + 1–4 children (avg 3.5 spans). Child `start_time` = root start + [0, root duration] ms, so a child never starts before its root; a child's `end_time` can exceed the root's by up to 10 ms. | Window membership = root `start_time >= now - WINDOW`. An orphan's `FirstSeenNs` is ≥ its root's start, so orphan eviction never outlives the root it waits for. Don't validate child-within-parent bounds. |
| Verifier: `total >= expected` (traces sent within window), then checks `len(spans)` for 10% of the first page (`limit=min(sent,1000)`). | `total` must count every rooted trace in the window; `GET /traces/{id}` must return every span received. Under-counting fails; nothing is penalised for duplicates except span counts. |
| Emitter retries on failure with a *new* batch, not the same one. | Duplicates are unlikely but dedupe by `span_id` anyway (cheap, keeps span counts honest). |
| Dashboard is a browser app on `:8081` calling the API on `:8080`, polling every 3s with `after`/`before`/`limit`. | CORS required. `/traces` must be cheap enough to serve every 3s while ingest runs. |
| Emitter runs in Docker and reaches the host by IP. | Kestrel binds `0.0.0.0:8080`, not localhost. |
| Default load: 20 workers × 5 **traces**/s each (Poisson sleep between trace generations, not between requests). `BATCH_SIZE=10` is a **span** threshold: whole traces are appended until the batch has ≥10 spans, so a batch is 10–13 spans / ~3 traces. A non-surging worker has a 5% chance per trace of starting a 2–6 s surge at 4x, so it surges after ~20 traces (~4 s), then emits ~80 traces in the surge: ~12.5 traces/s/worker on average, not 5. Default `DURATION` is 60 s. | Measured in phase 6: ~234 traces/s and ~72 req/s at default (~14k traces per 60 s run); ~2,390 traces/s and ~730 req/s at `WORKERS=200` (~143k per run). Sustained for 30 min that is ~420k traces in the window at default and ~4.3M at 10x. Phase 5 benchmarked only up to 300k. In-memory is fine at default; 10x sustained is unmeasured (see DECISIONS.md, phase 6). |

## 2. Solution layout

```
Traced.sln
src/
  Traced.Domain/            # no ASP.NET dependency
    Span.cs                 # record: TraceId(Guid), SpanId(Guid), ParentSpanId(Guid?), Service, Operation, StartNs, EndNs, Status, Tags
    SpanStatus.cs           # enum Ok, Error
    TraceSummary.cs         # record returned by queries
    TraceQuery.cs           # Limit, AfterNs?, BeforeNs?
    ITraceStore.cs          # Ingest(IReadOnlyList<Span>), Query(TraceQuery), TryGetTrace(Guid), EvictOlderThan(long cutoffNs)
    WindowOptions.cs        # WindowMinutes (default 30), EvictionInterval (default 1s), ShardCount (default 32)
    TimeExtensions.cs       # TimeProvider -> unix ns
  Traced.Storage/
    ShardedTraceStore.cs    # ITraceStore implementation
    TraceEntry.cs           # per-trace mutable state (only touched under its shard lock)
    EvictionService.cs      # BackgroundService + PeriodicTimer
  Traced.Api/
    Program.cs              # composition root only
    Contracts/              # wire DTOs (snake_case) + JsonSerializerContext (source-gen)
    Validation/             # IngestRequestValidator, TraceQueryParser -> Result<T, string>
    Endpoints/              # HealthEndpoints, SpanEndpoints, TraceEndpoints (MapGroup extension methods)
    Mapping/                # DTO <-> domain
tests/
  Traced.Domain.Tests/ (optional, small)
  Traced.Storage.Tests/     # assembly, eviction, query, concurrency
  Traced.Api.Tests/         # WebApplicationFactory contract tests
tools/
  Traced.LoadGen/           # optional: C# port of the emitter + verifier for runs without Docker
docs/
  PLAN.md, DECISIONS.md (agent-maintained log), MEASUREMENTS.md
TRADEOFFS.md                # written by Tope, NOT the agent
```

Dependency direction: `Api -> Storage -> Domain`. Endpoints depend on `ITraceStore`, never on `ShardedTraceStore`. Wire DTOs never leak into Domain.

## 3. Storage design

### 3.1 Shards
`ShardedTraceStore` holds `N` shards (power of two, default 32). Shard = `traceId.GetHashCode() & (N-1)`.

Each shard:
```
object Gate                                        // plain lock; critical sections are tiny
Dictionary<Guid, TraceEntry> Traces                // rooted + orphaned
SortedSet<(long StartNs, Guid TraceId)> Rooted     // index of rooted traces by root start
SortedSet<(long FirstSeenNs, Guid TraceId)> Orphans // traces with no root yet, by earliest span start
```

`TraceEntry`:
```
Span? Root
List<Span> Spans          // all spans incl. root, insertion order
HashSet<Guid> SpanIds     // dedupe
bool HasError             // OR of all span statuses, maintained on insert
long FirstSeenNs          // min span start, for orphan eviction
```

Why: writers to different traces almost never contend (1/32 chance); readers lock one shard at a time; no global lock. The SortedSet makes eviction "pop from the front" and range queries `GetViewBetween`.

### 3.2 Ingest (`POST /spans`)
1. Validate the whole batch (§5). Any invalid span → 400, nothing stored.
2. `cutoff = now - window`. Drop spans with `StartNs < cutoff` (silently).
3. Group remaining spans by shard; for each shard take the lock **once** and apply its spans:
   - get-or-create `TraceEntry`; skip if `SpanId` already present
   - append; `HasError |= span.Status == Error`
   - if span has no parent and `Root == null`: set `Root`, remove from `Orphans`, add `(Root.StartNs, TraceId)` to `Rooted`
   - else if `Root == null`: add/update in `Orphans` with `FirstSeenNs`
   - a second parentless span for a rooted trace is stored as a span but ignored as root (first wins; log in DECISIONS.md)
4. Return 202 `{"status":"ok"}`.

Grouping by shard keeps lock acquisitions per request ≤ number of shards touched, not number of spans.

### 3.3 Query (`GET /traces`)
- Effective lower bound = `max(after ?? 0, cutoff)` so results are correct even if eviction lags.
- Upper bound = `before ?? long.MaxValue` (exclusive).
- For each shard under its lock: `view = Rooted.GetViewBetween(lo, hi-1)`; add `view.Count` to total; copy the top `limit` entries from `view.Reverse()` into summaries (build `TraceSummary` inside the lock, since `TraceEntry` is mutable).
- Merge the per-shard lists (each ≤ limit) by `StartNs` desc, `TraceId` as tiebreak, take `limit`.
- `duration_ms = (root.End - root.Start) / 1_000_000`, `span_count = Spans.Count`, `status = HasError ? error : ok`.

Cost: O(shards × limit) copied + O(window) for `Count` on SortedSet views (view Count is O(n) in .NET). Measure it in phase 5; if it hurts, keep a per-shard counter for the unfiltered case.

### 3.4 Get trace (`GET /traces/{id}`)
- Invalid UUID → 400. Not found, no root, or `Root.StartNs < cutoff` → 404.
- Copy spans under lock, sort by `StartNs` asc outside the lock.
- Orphans return 404 (the spec says only traces with a root are listed; be consistent).

### 3.5 Eviction
`EvictionService : BackgroundService` with `PeriodicTimer(EvictionInterval)` driven by injected `TimeProvider`.
Each tick, `cutoff = now - window`, then for each shard (lock one at a time):
- while `Rooted.Min.StartNs < cutoff`: remove from `Rooted` and `Traces`
- while `Orphans.Min.FirstSeenNs < cutoff`: remove from `Orphans` and `Traces`
Exceptions are logged and the loop continues; a crashing eviction loop silently leaks memory.
Expose `EvictOlderThan(cutoff)` on the store so tests call it directly with a `FakeTimeProvider`.

### 3.6 Time
All "now" comes from `TimeProvider` (DI singleton). `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` in tests. Never `DateTime.UtcNow` in src.

## 4. Wire contract
- `System.Text.Json` source-generated context, `JsonNamingPolicy.SnakeCaseLower`, `DefaultIgnoreCondition = WhenWritingNull` (omit `parent_span_id` on roots).
- Timestamps are `long` nanoseconds on the wire and in Domain. No `DateTime` conversion.
- UUIDs parsed into `Guid` on ingest, serialised lowercase `D` format.
- Responses: `202 {"status":"ok"}`, `200` lists/details, `400/404 {"error":"..."}`.
- Request body limit: Kestrel default is fine (500 spans × ~60KB worst case tags ≈ 30MB is theoretical; set `MaxRequestBodySize` to 32MB and document).
- CORS: allow any origin, `GET, POST`, `Content-Type` header.
- `ASPNETCORE_URLS=http://0.0.0.0:8080` default; `WINDOW_MINUTES` read from env.

## 5. Validation rules (400 with a specific message)
Body: valid JSON object with `spans` array, 1–500 items.
Per span: `trace_id`, `span_id` valid UUIDs; `parent_span_id` valid UUID if present; `service`, `operation` non-empty, ≤256 chars; `start_time`, `end_time` present, `end_time >= start_time`; `status` ∈ {ok, error}; `tags` ≤50 entries, values ≤1024 chars.
Query: `limit` integer 1–1000 (default 20); `after`, `before` integers ≥0; if both, `after < before`.
Validators are pure functions returning `Result` — no exceptions for control flow, no FluentValidation (unneeded dependency).

## 6. Phases

Each phase ends green (`dotnet build -warnaserror`, `dotnet test`) with a summary and stops for review.

| # | Phase | Done when |
|---|---|---|
| 0 | Scaffold solution, projects, references, `Directory.Build.props` (nullable, warnings as errors, LangVersion latest), `.editorconfig`, `/health` | `curl :8080/health` → 200 |
| 1 | Domain types, wire DTOs, JSON context, mapping, validators + unit tests for every rule in §5 | Validator tests cover each 400 case |
| 2 | `ShardedTraceStore` ingest/query/get + tests: out-of-order assembly, dedupe, error propagation from child, root-less trace hidden, ordering, limit, total, after/before | Store tests green |
| 3 | Eviction (`EvictOlderThan` + `EvictionService`) + tests with `FakeTimeProvider`: boundary (exactly at cutoff kept), orphan eviction, query excludes stale even without eviction, ingest drops stale | Eviction tests green |
| 4 | Endpoints, CORS, error mapping + `WebApplicationFactory` tests asserting exact JSON shapes and status codes from the OpenAPI examples | API tests green |
| 5 | Concurrency: stress test (e.g. 32 writer tasks × 5k traces each, shuffled, split across batches, with 8 concurrent readers) asserting exact totals and span counts; BenchmarkDotNet or a timed loop for ingest and `/traces` latency. Record numbers in `docs/MEASUREMENTS.md` | Stress test green 10 runs in a row; numbers recorded |
| 6 | End to end: run the real emitter (`docker compose --profile tools run --rm emitter`) or `tools/Traced.LoadGen` if Docker is unavailable; default load then 10x (`WORKERS=200`). Record verifier output, memory, p99 | Verifier "all checks passed" at default load |
| 7 | Hand-off: README (run instructions), `docs/DECISIONS.md` complete, `docs/MEASUREMENTS.md` complete, PR description draft. **No TRADEOFFS.md** | Tope reviews and writes TRADEOFFS.md |

## 7. Test inventory (minimum)
- Out-of-order: children then root in one batch; children in batch A, root in batch B; root then children; all produce same summary and span list.
- Error status: only a leaf has `error` → summary `error`.
- Dedupe: same span twice → counted once.
- Window: span with `start < cutoff` dropped on ingest; trace at `cutoff - 1ns` evicted, at `cutoff` kept; orphan evicted; `/traces` and `/traces/{id}` hide stale before eviction runs.
- Query: desc ordering, tie on start time is deterministic, `limit` respected, `total` ignores limit, `after` inclusive, `before` exclusive.
- API: 400 for each validation rule, 404 unknown/evicted/orphan, 400 bad UUID, 202 body shape, CORS preflight.
- Concurrency: stress test in §6 phase 5; eviction running concurrently with ingest and query does not throw or lose in-window traces.

## 8. Known risks / open decisions for Tope
- `SortedSet` view `Count` is O(n). Fine at default load, likely the first hot spot at 10x on `/traces` polling.
- Memory: ~300k traces × ~3.5 spans × object overhead. Measure in phase 6; consider struct spans / interned service+operation strings.
- No persistence: a restart loses the window. Acceptable for this exercise; state it.
- First root wins on duplicate parentless spans.
- Spans with future timestamps are accepted and live until they age out.
