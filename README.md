# Traced

Traced is an HTTP span-ingestion server written in .NET 10 (ASP.NET Core Minimal APIs). It accepts span batches, which may arrive out of order. It assembles them into traces in memory, keeps a rolling time window (30 minutes by default), and serves trace summaries and details to a dashboard. The contract is [`spec/openapi.yaml`](spec/openapi.yaml), from [benx421/traced](https://github.com/benx421/traced).

## Prerequisites

- .NET 10 SDK. `global.json` pins 10.0.400 with `rollForward: latestFeature`.
- Docker with Docker Compose. This is only needed for the external harness (emitter, verifier and dashboard).

## Build and test

```bash
dotnet build -warnaserror
dotnet test
```

`dotnet test` includes a stress test (32 writers × 5,000 traces, 8 readers and a concurrent evictor; about 10–15 s). To run it on its own, or to skip it:

```bash
dotnet test tests/Traced.Storage.Tests --filter Category=Stress   # only the stress test
dotnet test --filter "Category!=Stress"                           # everything except it
```

## Run the server

```bash
dotnet run -c Release --project src/Traced.Api
```

- The server listens on `http://0.0.0.0:8080` by default. It binds `0.0.0.0` so the emitter in Docker can reach it.
- `ASPNETCORE_URLS` overrides the address, for example `ASPNETCORE_URLS=http://0.0.0.0:18080 dotnet run -c Release --project src/Traced.Api`.
- `WINDOW_MINUTES` sets the rolling window in minutes (default 30), for example `WINDOW_MINUTES=5 dotnet run -c Release --project src/Traced.Api`. It must match the emitter's `WINDOW_MINUTES`.
- CORS allows any origin for `GET` and `POST` with a `Content-Type` header, so the dashboard on `:8081` can call the API.

## Endpoints

| Method and path | Success | Errors |
|---|---|---|
| `POST /spans` | `202 {"status":"ok"}` | `400 {"error":"..."}`: any invalid span rejects the whole batch. Spans older than the window are dropped silently. |
| `GET /traces?limit=&after=&before=` | `200 {"traces":[...],"total":N}`, rooted traces only, newest root first. `limit` defaults to 20 (max 1000), `after` is inclusive, `before` exclusive (Unix ns). | `400` on a bad or repeated parameter, or `after >= before` |
| `GET /traces/{trace_id}` | `200 {"trace_id":"...","spans":[...]}`, spans by `start_time` ascending | `400` for an invalid UUID; `404` if the trace is unknown, has no root, or is outside the window |
| `GET /health` | `200 {"status":"ok"}` | |

Outside the contract, for people rather than clients: `GET /docs` is an API reference page rendered from the spec, `GET /openapi.yaml` serves the spec unchanged, and `GET /` redirects to `/docs`.

The examples below use timestamps from the current time. A span older than the window would be accepted and then dropped.

```bash
NOW=$(($(date +%s) * 1000000000))
curl -s -i -X POST localhost:8080/spans -H 'Content-Type: application/json' -d '{"spans":[
  {"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"checkout","operation":"place_order","start_time":'$NOW',"end_time":'$((NOW + 150000000))',"status":"ok","tags":{"order_id":"ord_123"}},
  {"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b811-9dad-11d1-80b4-00c04fd430c8","parent_span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"inventory","operation":"reserve_stock","start_time":'$((NOW + 20000000))',"end_time":'$((NOW + 100000000))',"status":"ok"}]}'

curl -s 'localhost:8080/traces?limit=20'

curl -s localhost:8080/traces/550e8400-e29b-41d4-a716-446655440000

curl -s localhost:8080/health
```

## End to end with the real emitter and dashboard

The emitter, verifier and dashboard live in the harness repo. They are not part of this repo.

```bash
git clone https://github.com/benx421/traced ../traced-harness
cd ../traced-harness
```

Create `../traced-harness/.env`:

```bash
TARGET_URL=http://host.docker.internal:8080
VERIFY=true
```

- On macOS and Windows, `host.docker.internal` reaches the host from the container. On Linux, use the host's IP instead: `ip route | awk '/default/ { print $3 }'`.
- `localhost` does not work, because the emitter runs inside Docker.
- Omitted or empty variables use the emitter defaults: 20 workers, 60 s, window 30, out-of-order probability 0.3, batch 10.
- If you start from the harness's `.env.example` instead, note that it sets `VERIFY=false`. Change it to `true`, or remove the line, to get the verifier.

With the server running on 8080:

```bash
docker compose up -d                                             # dashboard at http://localhost:8081 (enter http://localhost:8080, click Connect)
docker compose --profile tools run --rm emitter                  # default load + verifier
docker compose --profile tools run --rm -e WORKERS=200 emitter   # 10x workers
docker compose down                                              # stop the dashboard
```

A passing run ends with `INFO all checks passed`. The harness's `Makefile` wraps the same commands as `make up`, `make emit` and `make down`.

## Benchmarks

`benchmarks/Traced.Benchmarks` contains the BenchmarkDotNet suite plus two timed probes. It is not a test project, so `dotnet test` skips it. Always run it in Release.

```bash
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- --list flat              # list benchmarks
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- --filter '*QueryBenchmarks*'
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- memory 300000            # heap / working set at N traces
dotnet run -c Release --project benchmarks/Traced.Benchmarks -- http http://localhost:8080 300000 20   # against a running server
```

The full suite (`--filter '*'`) takes several minutes. The recorded results and the commands that produced them are in [`docs/MEASUREMENTS.md`](docs/MEASUREMENTS.md).

## Docker and deployment

```bash
docker build -f src/Traced.Api/Dockerfile -t traced-api .
docker run --rm -p 8080:8080 traced-api
```

[`render.yaml`](render.yaml) deploys the same image to Render's free tier, plus the harness dashboard as a static site built from `benx421/traced`; see [`docs/deploy/render.md`](docs/deploy/render.md). [`.github/workflows/ci.yml`](.github/workflows/ci.yml) builds, runs every test, and smoke-tests the image on each pull request and push to `main`, and Render deploys `main` only after those checks pass.

## Repository layout

```
spec/openapi.yaml            API contract (unmodified copy from the harness)
src/Traced.Domain/           Span, TraceSummary, TraceQuery, ITraceStore, WindowOptions, time helpers (no ASP.NET)
src/Traced.Storage/          ShardedTraceStore, TraceEntry, EvictionService (BackgroundService)
src/Traced.Api/              Program.cs (composition), Endpoints/, Contracts/ (wire DTOs + JSON context),
                             Validation/ (pure validators returning Result), Mapping/ (domain -> wire)
tests/Traced.Domain.Tests/   time and window helpers
tests/Traced.Storage.Tests/  assembly, query, window, eviction, concurrency, stress
tests/Traced.Api.Tests/      validators, mapping, WebApplicationFactory endpoint/CORS/error tests
benchmarks/Traced.Benchmarks BenchmarkDotNet suite, memory probe, HTTP load loop
docs/                        OVERVIEW.md (and .pdf), PLAN.md, DECISIONS.md, MEASUREMENTS.md, PR_DESCRIPTION.md,
                             deploy/render.md
render.yaml                  Render Blueprint (one free Docker web service)
.github/workflows/ci.yml     build, test and Docker smoke test
```

## Further reading

- [`docs/OVERVIEW.md`](docs/OVERVIEW.md) ([PDF](docs/OVERVIEW.pdf)): what Traced is and how it works, for engineers new to the project.
- [`docs/PLAN.md`](docs/PLAN.md): design and phase plan.
- [`docs/DECISIONS.md`](docs/DECISIONS.md): every decision and deviation, by phase, plus the open questions and known risks.
- [`docs/MEASUREMENTS.md`](docs/MEASUREMENTS.md): raw benchmark, stress and end-to-end numbers, with commands.
