# Traced: project overview

*An in-memory span-ingestion server in .NET 10. This document explains what it is, what it does, and how it works, for engineers who are new to the project.*

---

## 1. What Traced is

Distributed tracing follows one request as it moves through several services. Each service records a **span** for the work it did: which service, which operation, when it started and ended, whether it failed. All the spans for one request share a **trace ID**, and together they form a **trace**. A tracing dashboard shows those traces so an engineer can see, for example, that checkout called inventory, inventory called payment, and payment timed out.

Traced is the **collector** in that picture. It:

- **accepts spans over HTTP**, in batches, from many senders at once;
- **assembles them into traces**, even when a trace's spans arrive out of order or in different batches;
- **keeps a rolling time window** of recent traces (30 minutes by default) and forgets anything older;
- **serves the traces** to a dashboard: a newest-first list with summaries, and the full span list for any one trace.

Everything is held in memory. There is no database, and a restart starts empty.

### Where it comes from

Traced implements the contract in [`spec/openapi.yaml`](../spec/openapi.yaml), taken unchanged from the [benx421/traced](https://github.com/benx421/traced) exercise. That repository also provides the test harness this project is verified against:

- an **emitter** that sends realistic span traffic from many concurrent workers, shuffling some traces' spans;
- a **verifier** that checks afterwards that every trace sent is listed and has the right number of spans;
- a **dashboard** that polls the API every 3 seconds and plots the traces.

## 2. Core concepts

| Term | Meaning in Traced |
|---|---|
| **Span** | One unit of work: `trace_id`, `span_id`, optional `parent_span_id`, `service`, `operation`, `start_time` and `end_time` (Unix nanoseconds), `status` (`ok` or `error`), and up to 50 string `tags`. |
| **Root span** | The span with no `parent_span_id`. It defines the trace's start time, duration, and service and operation name in summaries. |
| **Trace** | All spans sharing a `trace_id`. It appears in listings only once its root has arrived. |
| **Orphan** | A trace whose children have arrived but whose root hasn't yet. It is stored, but hidden from listings and from get-trace until the root arrives. |
| **Window** | Only traces whose root started within the last `WINDOW_MINUTES` (default 30) are visible. Older ones are hidden immediately and deleted by a background job. |
| **Trace status** | `error` if *any* span in the trace has status `error`, otherwise `ok`. |

Timestamps are 64-bit Unix nanoseconds everywhere: on the wire, in memory, and in comparisons. They are never converted to date types.

## 3. The API

| Method and path | Success | Errors |
|---|---|---|
| `POST /spans` | `202 {"status":"ok"}` | `400 {"error":"..."}` if any span is invalid; the whole batch is then rejected and nothing is stored. `413` if the body is over 32 MiB. |
| `GET /traces?limit=&after=&before=` | `200 {"traces":[...],"total":N}` | `400` for a bad or repeated parameter, or `after >= before` |
| `GET /traces/{trace_id}` | `200 {"trace_id":"...","spans":[...]}` | `400` for an invalid UUID; `404` if unknown, root-less, or outside the window |
| `GET /health` | `200 {"status":"ok"}` | |

Details worth knowing:

- **Batches** hold 1 to 500 spans. Spans older than the window are accepted with a 202 and silently dropped.
- **`GET /traces`** lists only traces with a root, newest root first. `limit` defaults to 20 (maximum 1000). `after` is inclusive and `before` exclusive, both in Unix nanoseconds. `total` counts every matching trace, not just the returned page.
- **A summary** holds `trace_id`, `root_service`, `root_operation`, `span_count` (including children that arrived before the root), `duration_ms` (the root's duration, whole milliseconds), `start_time` and `status`.
- **`GET /traces/{id}`** returns every span received for the trace, ordered by `start_time`. Root spans omit `parent_span_id`.
- **Every error** is a JSON body of the form `{"error":"<message>"}`, including malformed JSON and unexpected server errors.
- **CORS** allows any origin for `GET` and `POST` with a `Content-Type` header, so a browser dashboard on another port or host can call it.

A minimal exchange:

```text
POST /spans
{"spans":[
  {"trace_id":"550e8400-...","span_id":"6ba7b811-...","parent_span_id":"6ba7b810-...",
   "service":"inventory","operation":"reserve_stock","start_time":...,"end_time":...,"status":"ok"},
  {"trace_id":"550e8400-...","span_id":"6ba7b810-...",
   "service":"checkout","operation":"place_order","start_time":...,"end_time":...,"status":"ok"}
]}
→ 202 {"status":"ok"}

GET /traces?limit=20
→ 200 {"traces":[{"trace_id":"550e8400-...","root_service":"checkout","root_operation":"place_order",
                  "span_count":2,"duration_ms":150,"start_time":...,"status":"ok"}],"total":1}
```

The child arrived first in that batch. Order doesn't matter: the trace is the same either way.

## 4. Architecture

### Projects and layering

```text
Traced.Api  ──►  Traced.Storage  ──►  Traced.Domain
(HTTP)           (in-memory store,     (types and the ITraceStore
                  eviction job)          interface; no ASP.NET, no JSON)
```

| Project | Contents |
|---|---|
| **Traced.Domain** | `Span`, `SpanStatus`, `TraceSummary`, `TraceQuery`, the `ITraceStore` interface, `WindowOptions`, and time helpers. It references nothing web-related. |
| **Traced.Storage** | `ShardedTraceStore` (implements `ITraceStore`), `TraceEntry` (per-trace state), and `EvictionService` (the background cleanup job). |
| **Traced.Api** | `Program.cs` (wiring only), `Endpoints/` (one file per route group), `Contracts/` (wire-format types and a compile-time JSON serializer), `Validation/` (request checks), `Mapping/` (domain to wire format). |

Endpoints depend only on the `ITraceStore` interface, and wire-format types never reach the domain.

### A request, end to end

```text
POST /spans
  1. Read the body (≤ 32 MiB).
  2. Validate and parse every span. Any failure → 400, nothing stored.
  3. Store.Ingest(spans)
       - drop spans older than now − window
       - group the rest by shard
       - for each shard touched: take its lock once, apply its spans
  4. 202 {"status":"ok"}

GET /traces
  1. Parse limit / after / before. Any failure → 400.
  2. Store.Query(...)
       - lower bound = max(after, now − window); upper bound = before
       - for each shard, under its lock: count matches, copy its newest `limit` as summaries
       - merge the per-shard results, newest first, keep `limit`
  3. 200 {"traces":[...], "total":N}
```

## 5. How storage works

### Shards

The store is split into 32 **shards**. A trace always lives in the same shard, chosen from its trace ID. Each shard has its own lock and its own data, so writers working on different traces rarely wait for each other, and there is no global lock.

Each shard holds:

| Structure | Purpose |
|---|---|
| `Dictionary<Guid, TraceEntry>` | Every trace in the shard, rooted or orphaned. |
| `SortedSet<(StartNs, TraceId)>` of rooted traces | Ordered by root start time. Used for range queries and for eviction, oldest first. |
| `SortedSet<(FirstSeenNs, TraceId)>` of orphans | Ordered by earliest span start. Lets orphans that never get a root be evicted too. |

A `TraceEntry` holds the root (once it arrives), all spans in arrival order, the set of span IDs already seen, an error flag, and the earliest span start. It is only ever read or changed while its shard's lock is held.

### Ingest

1. **Drop stale spans** (start time before `now − window`).
2. **Group by shard,** so each shard's lock is taken once per request rather than once per span.
3. **For each span, under the lock:**
   - skip it if its `span_id` is already in the trace (duplicates count once);
   - append it and update the error flag;
   - if it has no parent and the trace has no root yet, it becomes the root: the trace moves from the orphan index to the rooted index;
   - otherwise, if the trace still has no root, update its position in the orphan index.

A second parentless span for an already-rooted trace is stored as a span but doesn't replace the root.

### Query and get-trace

- **Queries clamp themselves to the window.** Results are correct whether or not the cleanup job has run yet.
- **Summaries are built inside the shard lock** and returned as immutable values, so callers never see a trace mid-update.
- **Get-trace** copies the trace's spans under the lock and sorts them by start time after releasing it.

### Eviction

`EvictionService` runs every second on the injected clock. Each run computes `cutoff = now − window` and, one shard at a time:

- removes rooted traces from the front of the rooted index while their root started before the cutoff;
- removes orphans from the front of the orphan index while their earliest span started before the cutoff.

If a run throws, the error is logged and the next run goes ahead. A child that arrives after its trace was evicted starts a new orphan; the old trace is never brought back.

### Time

All "now" values come from an injected `TimeProvider`. Production uses the system clock and tests use a fake clock they can move forward, which makes the window and eviction behaviour testable to the nanosecond.

## 6. Validation and errors

Validators are plain functions that return a success-or-error result. They don't throw, and the project uses no validation library. They enforce the contract:

- **Body:** a JSON object with a `spans` array of 1 to 500 items.
- **IDs:** `trace_id`, `span_id` and `parent_span_id` (if present) are canonical UUIDs (8-4-4-4-12, either case).
- **Names:** `service` and `operation` are at most 256 characters.
- **Times:** `start_time` and `end_time` are present 64-bit integers, and `end_time` is not before `start_time`.
- **Status:** `status` is `ok` or `error`.
- **Tags:** at most 50, each value a string of at most 1,024 characters.
- **Query:** `limit` is an integer from 1 to 1000; `after` and `before` are non-negative integers with `after < before`; no parameter may be repeated.

The first problem found is reported, with its position, for example `invalid request body: spans[3]: missing required field span_id`.

## 7. Configuration

| Setting | Default | Effect |
|---|---|---|
| `ASPNETCORE_URLS` | unset → `http://0.0.0.0:8080` | Listening address. It binds all interfaces so the Dockerized emitter can reach a server on the host. |
| `WINDOW_MINUTES` | `30` | Rolling window. Must be positive. It should match the emitter's setting. |

Internal defaults are 32 shards and a 1-second eviction interval. The API uses workstation garbage collection, chosen after measuring memory (section 9).

## 8. Testing

**267 automated tests** run with `dotnet test`:

| Project | Tests | Covers |
|---|---|---|
| Traced.Domain.Tests | 5 | Time and window helpers |
| Traced.Storage.Tests | 75 | Out-of-order assembly, dedupe, error status, root-less traces, ordering and tie-breaks, limit and total, window bounds, eviction (including the exact cutoff nanosecond), the eviction job, concurrency, and the stress test |
| Traced.Api.Tests | 187 | Every validation rule, response shapes, and in-process HTTP tests asserting exact status codes and JSON bodies from the OpenAPI examples, CORS preflight and error handling |

The tests were written before the code in each phase.

**The stress test** sends 160,000 traces from 32 concurrent writers, with each span going to a random writer and batch, while 8 readers query and the eviction job runs. It checks that every trace ends with exactly the right span count, and that no reader ever saw a trace's span count go backwards. To check that the test catches real bugs, the shard lock was removed from ingest once; the test failed within a second. It passed 10 runs in a row.

**CI** ([`.github/workflows/ci.yml`](../.github/workflows/ci.yml)) builds with warnings treated as errors, runs all tests including the stress test, and builds and smoke-tests the Docker image on every pull request and push to `main`.

## 9. Verification and performance

### Against the real harness

The emitter and verifier from the harness were run against the server on this machine (Apple M4 Pro, 12 cores, 24 GB, .NET 10.0.11, Release build):

| | Default load (20 workers, 60 s) | 10× workers (200 workers, 60 s) |
|---|---|---|
| Traces / spans sent | 14,039 / 49,377 | 143,353 / 501,497 |
| Verifier | `all checks passed`, 0 missing, 0 span mismatches | `all checks passed`, 0 missing, 0 span mismatches |
| Server memory, peak | 101 MB | 286 MB |
| `GET /traces?limit=1000`, p50 / p99 | 2.8 / 9.0 ms | 9.6 / 17.0 ms |
| `POST /spans`, p50 / p99 | 0.19 / 0.87 ms | 0.06 / 0.47 ms |

The Docker image also passed a default emitter run (13,795 traces) using 58 MiB.

### Benchmarks at larger sizes

| Measure | Result |
|---|---|
| Store query, limit 1000, 300k traces | 20 ms |
| HTTP `/traces?limit=1000` under load, 300k traces | p50 17.8 ms, p99 20.5 ms |
| Single trace lookup | ~60 ns |
| HTTP ingest, 20 clients | ~40,800 requests/s (~467,000 spans/s), p99 0.84 ms |
| Evicting a 300k-trace backlog | 32.8 ms in total, ~1 ms per shard lock |
| Memory per stored trace | ~1.3 KB (380 MB of heap at 300k traces) |

Full numbers, with the command that produced each, are in [`MEASUREMENTS.md`](MEASUREMENTS.md).

## 10. Running and deploying

**Locally:**

```bash
dotnet build -warnaserror
dotnet test
dotnet run -c Release --project src/Traced.Api   # http://localhost:8080
```

**With the harness:** clone `benx421/traced` next to this repo, set `TARGET_URL=http://host.docker.internal:8080` and `VERIFY=true` in its `.env`, then use `docker compose up -d` for the dashboard on http://localhost:8081 and `docker compose --profile tools run --rm emitter` for a verified load run. The [README](../README.md) has the details.

**In Docker:**

```bash
docker build -f src/Traced.Api/Dockerfile -t traced-api .
docker run --rm -p 8080:8080 traced-api
```

**On Render:** [`render.yaml`](../render.yaml) is a Blueprint for one free Docker web service. Render deploys `main` after the CI checks pass. On the free tier the service sleeps after 15 minutes idle and comes back empty. See [`deploy/render.md`](deploy/render.md).

## 11. Limits and known risks

- **No persistence.** A restart, redeploy or sleep loses every trace in the window.
- **Single instance.** The store lives in the process, so the service can't be scaled out as-is.
- **Sustained 10× load is unmeasured.** A full 30-minute window at 200 workers would hold about 4.3 million traces, roughly 5.6 GB at the measured size per trace. The 60-second runs and benchmarks went up to 300,000.
- **Unfiltered listing cost grows with the window.** Counting `total` walks the matching index entries, which is most of the 20 ms at 300k traces.
- **Contract edge cases.** The first root wins if a trace has two parentless spans. Spans with future timestamps are accepted and kept until they age out.

These, and every other design decision with its reasoning, are tracked in [`DECISIONS.md`](DECISIONS.md) under "Open questions and known risks".

## 12. Where to go next

| Document | What's in it |
|---|---|
| [`README.md`](../README.md) | Commands for building, testing, running, the harness and benchmarks |
| [`PLAN.md`](PLAN.md) | The original design and the phase-by-phase build plan |
| [`DECISIONS.md`](DECISIONS.md) | Every decision and deviation, by phase, plus open questions |
| [`MEASUREMENTS.md`](MEASUREMENTS.md) | Raw stress, benchmark and end-to-end numbers with commands |
| [`spec/openapi.yaml`](../spec/openapi.yaml) | The API contract |
| [`deploy/render.md`](deploy/render.md) | Deploying to Render |

A suggested reading order for the code: `Traced.Domain/ITraceStore.cs`, then `Traced.Storage/ShardedTraceStore.cs` and `TraceEntry.cs`, then `Traced.Api/Endpoints/`, then `tests/Traced.Storage.Tests/StressTests.cs`.
