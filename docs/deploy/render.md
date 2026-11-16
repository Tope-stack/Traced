# Deploying Traced to Render's free tier

[`render.yaml`](../../render.yaml) defines two free services:

| Service | What it is | URL paths |
|---|---|---|
| **traced** | The API, a Docker web service built from [`src/Traced.Api/Dockerfile`](../../src/Traced.Api/Dockerfile). There is no database: all traces are held in memory. | `/` redirects to `/docs` (API reference), `/openapi.yaml`, plus the API endpoints |
| **traced-dashboard** | The harness dashboard, a static site deployed from [Tope-stack/traced-harness](https://github.com/Tope-stack/traced-harness), an unmodified GitHub fork of [benx421/traced](https://github.com/benx421/traced). Nothing from it is copied into this repo. | `/` (dashboard), `/docs` (the harness's API reference) |

## What to expect from the free tier

Check Render's current terms; this is how it worked when this guide was written.

- **Sleep empties the store.** The service sleeps after 15 minutes without traffic and the next request wakes it, which takes about a minute. Every sleep, restart or redeploy starts with an empty store, because nothing is persisted.
- **512 MB of memory.** A default 60-second emitter run (~14k traces) used 58 MiB in the container locally. A 30-minute window under sustained default load would hold ~420k traces at ~1.3 KB each, which is more than the free instance has. Use the free tier for demos and short emitter runs; for sustained load, pick a larger plan or lower `WINDOW_MINUTES`.
- **One instance.** The store is in-process, so the service must not be scaled to more than one instance: each would hold a different subset of the traces.

## 1. Create the services from the Blueprint

Render only builds from repos your GitHub account can grant it access to, so the dashboard is deployed from a fork:

0. On GitHub, open [benx421/traced](https://github.com/benx421/traced), click **Fork**, set **Repository name** to `traced-harness` (the name `traced` is already this repo), and create the fork. Don't change anything in it.

Then:

1. Sign in to Render and connect your GitHub account, giving it access to the **Traced** and **traced-harness** repositories.
2. Go to **New → Blueprint**, choose the repository and branch `main`. Render reads `render.yaml` and shows **traced** (free, Docker, Frankfurt) and **traced-dashboard** (free static site).
3. Click **Apply**. The first build takes a few minutes. Render then calls `/health` until it returns 200.

If a name is taken, Render gives the service a URL with a suffix (for example `traced-llzo.onrender.com`); use whatever URL the Render dashboard shows.

**Already created the API from an earlier Blueprint?** After forking, give Render access to `traced-harness` (Render → Account Settings → GitHub → Configure), then open the Blueprint and click **Sync** to add `traced-dashboard`. If you'd rather create it by hand: **New → Static Site**, pick `traced-harness`, branch `master`, build command `cp spec/openapi.yaml dashboard/openapi.yaml`, publish directory `dashboard`, and under **Redirects/Rewrites** a rewrite from `/docs` to `/docs/index.html`.

**Harness updates:** the fork doesn't follow benx421/traced by itself. On the fork's GitHub page, click **Sync fork** to pull in upstream changes; the dashboard site then redeploys automatically.

## 2. Try it

Open `https://<your-service>.onrender.com/` in a browser: it redirects to `/docs`, an API reference rendered from `spec/openapi.yaml`, whose "try it" requests go to the same host. From the command line:

```bash
curl -s https://<your-service>.onrender.com/health
curl -s 'https://<your-service>.onrender.com/traces?limit=20'
```

The `POST /spans` example in the [README](../../README.md#endpoints) works the same way with the Render URL in place of `localhost:8080`.

### The dashboard

Open the **traced-dashboard** URL, enter the API's URL (`https://<your-service>.onrender.com`, no trailing slash), and click **Connect**. The dashboard runs in your browser and calls the API directly; CORS allows any origin.

You can also run the dashboard locally against the deployed API: `cd ../traced-harness && docker compose up -d`, then open http://localhost:8081.

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
