# Deploying Traced to Render's free tier

Traced runs on Render as **one Docker web service**, defined in [`render.yaml`](../../render.yaml) and built from [`src/Traced.Api/Dockerfile`](../../src/Traced.Api/Dockerfile). There is no database: all traces are held in memory.

## What to expect from the free tier

Check Render's current terms; this is how it worked when this guide was written.

- **Sleep empties the store.** The service sleeps after 15 minutes without traffic and the next request wakes it, which takes about a minute. Every sleep, restart or redeploy starts with an empty store, because nothing is persisted.
- **512 MB of memory.** A default 60-second emitter run (~14k traces) used 58 MiB in the container locally. A 30-minute window under sustained default load would hold ~420k traces at ~1.3 KB each, which is more than the free instance has. Use the free tier for demos and short emitter runs; for sustained load, pick a larger plan or lower `WINDOW_MINUTES`.
- **One instance.** The store is in-process, so the service must not be scaled to more than one instance: each would hold a different subset of the traces.

## 1. Create the service from the Blueprint

1. Sign in to Render and connect your GitHub account, giving it access to the Traced repository.
2. Go to **New → Blueprint**, choose the repository and branch `main`. Render reads `render.yaml` and shows one web service, **traced** (free, Docker, Frankfurt).
3. Click **Apply**. The first build takes a few minutes. Render then calls `/health` until it returns 200.

If the name `traced` is taken, Render gives the service a URL with a suffix; use whatever URL the dashboard shows.

## 2. Try it

```bash
curl -s https://<your-service>.onrender.com/health
curl -s 'https://<your-service>.onrender.com/traces?limit=20'
```

The `POST /spans` example in the [README](../../README.md#endpoints) works the same way with the Render URL in place of `localhost:8080`.

### The dashboard

The harness dashboard runs in your browser, so it can point at the deployed API. CORS allows any origin.

```bash
cd ../traced-harness && docker compose up -d
```

Open http://localhost:8081, enter `https://<your-service>.onrender.com`, and click **Connect**.

### The emitter and verifier

Set the target in `../traced-harness/.env`:

```bash
TARGET_URL=https://<your-service>.onrender.com
VERIFY=true
```

Then run `docker compose --profile tools run --rm emitter`. Wake the service first with any request, such as `/health`. Otherwise the first batches fail while it starts. That doesn't break verification, because the emitter discards a failed batch and only counts traces from batches that were accepted, but it wastes part of the run. Keep to the default load on the free tier (see above).

## Updating

`autoDeployTrigger: checksPass` means a push to `main` deploys only after its GitHub checks pass. Those checks are [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml):

- **build-and-test:** `dotnet build -warnaserror` and the full `dotnet test`, including the stress test, in Release.
- **docker:** builds the same image Render builds and smoke-tests `/health` and `/traces` in the running container.

A pull request runs the same checks but never deploys. You can also deploy by hand with **Manual Deploy** in the Render dashboard.

## Environment variables

| Variable | Value in `render.yaml` | Purpose |
|---|---|---|
| `PORT` | `8080` | Tells Render which port the container listens on. |
| `WINDOW_MINUTES` | `30` | Rolling window. Must match the emitter's `WINDOW_MINUTES` for the verifier's counts to line up. |

`ASPNETCORE_URLS` is not set, so the server uses its default `http://0.0.0.0:8080`.
