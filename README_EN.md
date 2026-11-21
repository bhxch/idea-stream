# Idea Manager

A personal idea notebook for a single user and a single instance. The browser and the agents share one REST API;
the application only listens on the loopback address, and everything exposed externally goes through an nginx
reverse proxy (TLS and access control both live in the nginx layer).

- **Backend**: .NET 10 + SQLite + Native AOT — hand-written SQL, no ORM, no reflection-based serialization, no template engine.
- **Frontend**: Vue 3 + TypeScript + Vite, the build output drops straight into the backend's `wwwroot/`, served same-origin by the backend.
- **Agent access**: REST API + SSE (a long-lived connection the server pushes events over one way); the interface
  contract is the OpenAPI 3.1 document committed to the repository;
  the agent skill's manual is **distributed by the server**, so it is always the same version as the interface.

[中文](README.md)

This document is written in English.

**Find what you need**

| What you want to do | Where to look |
|---|---|
| Get the environment right first | [Prerequisites](#prerequisites) |
| Get it running first | [Quick Start](#quick-start) |
| Learn what the system can do | [Capability Overview](#capability-overview) |
| Work on the backend | [Building](#building), [Configuration](#configuration), [Testing](#testing) |
| Work on the frontend | [`web/README.md`](web/README.md), [`web/e2e/README.md`](web/e2e/README.md) |
| Use the API / write an agent | [OpenAPI and API Usage](#openapi-and-api-usage), [Agent Skill](#agent-skill) |
| Deploy it | [Deployment](#deployment) |
| Look up a symptom | [Troubleshooting](#troubleshooting) |
| Pass the gates before committing | [CI and Gates](#ci-and-gates) |
| Know where the traps are | [Known Limitations and Gotchas](#known-limitations-and-gotchas) |
| Know why it is built this way | [Design Trade-offs](#design-trade-offs) |

---

## Prerequisites

Get this table in place before you go any further — with the wrong version the build blows up already at the
`restore` stage, and the error message does not point at the real cause.

| Dependency | Version | Notes |
|---|---|---|
| .NET SDK | **10.0.113** | Pinned by the `global.json` at the repo root (`rollForward: latestPatch` — patch releases inside the 10.0.1xx band may roll forward, other major/feature bands are refused). Install this version yourself: any other version fails at the `restore` stage (NU1903 / NU1101), and the error message there does not point at the real cause. CI pins the same version with `actions/setup-dotnet` (`.github/workflows/ci.yml`). The csproj explicitly bumps ILCompiler to 10.0.14 to avoid the advisory (GHSA-p3jp-9v83-76w9) hit by the 10.0.13 built into this SDK; `TreatWarningsAsErrors` turns NU1903 into an error → restore fails outright. Any conclusion about a different SDK version has to be re-verified |
| Node / npm | Node **24** | Same as CI |
| Python | **3.9+** | The fetcher uses only the standard library, no third-party dependencies |
| clang + zlib1g-dev | Any recent version | The build dependencies for publishing Native AOT on Linux (`apt-get install -y clang zlib1g-dev`) |

Check it with one line once it is installed:

```bash
dotnet --version   # it has to print 10.0.113
```

---

## Quick Start

Five steps from zero to working. **Every step says which directory to run it in** — copy them as they are.

```bash
# ① Build the frontend (the output lands in server/IdeaApp/wwwroot/) — repo root
cd web && npm ci && npm run build && cd ..
```

```bash
# ② Publish the Native AOT artifact — repo root
#    Check first: dotnet --version has to print 10.0.113 (see "Prerequisites")
dotnet publish server/IdeaApp/IdeaApp.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

```bash
# ③ Start the server — repo root (the directory ② produces; it does not exist before the publish)
PUBLISH=server/IdeaApp/bin/Release/net10.0/linux-x64/publish
cd "$PUBLISH" && ./IdeaApp
```

```bash
# ④ Install the agent skill — repo root (installs to ~/.zcode/skills/idea-manager by default)
bash skills/install-agent-skill.sh
```

```bash
# ⑤ Get the skill working locally first — repo root
#    A local instance has no basic auth, so IDEA_USER / IDEA_PASS are not needed
export IDEA_BASE_URL=http://127.0.0.1:5080
python3 ~/.zcode/skills/idea-manager/scripts/fetch.py
```

Once the server is up: the web UI is at <http://127.0.0.1:5080>, the API lives under `/api/**`, and the contract is
at `/openapi/v1.json`. The first start creates the database and the tables automatically — there is no migration step.

> ⚠️ **Step ① cannot be skipped**: `server/IdeaApp/wwwroot/` is excluded by `.gitignore`, so the repository does not
> contain it. Publish without building the frontend and you get an **empty wwwroot** — the app starts as usual,
> `/api/*` works as usual, the page is blank, and **nothing reports an error** (the static file middleware simply
> cannot find the files).
> After publishing, run `ls server/IdeaApp/bin/Release/net10.0/linux-x64/publish/wwwroot`
> and confirm that `index.html` and `assets/` are both there.

> ⚠️ **Step ② depends on .NET SDK 10.0.113 and clang**: with a different SDK version, `restore` can fail outright
> (see [Known Limitations and Gotchas](#known-limitations-and-gotchas)). Publishing Native AOT on Linux additionally
> needs the build dependencies `clang` and `zlib1g-dev`.

> ⚠️ **After step ④ you still have to `export IDEA_BASE_URL`**, otherwise the fetcher fails right away with
> `missing_config` (`IDEA_BASE_URL` is one of its required settings). That is what step ⑤ is for: **get the whole
> chain working locally first**, and only then deal with the nginx side (domain + certificate + basic auth) — those
> three obstacles have nothing to do with the local chain.

---

## Capability Overview

### Backend Capabilities

A single SQLite database, hand-written SQL, one REST surface plus one SSE stream on the server. Grouped by resource
below; every entry states "what it does + how to call it". The per-endpoint parameters and schemas are in
[OpenAPI and API Usage](#openapi-and-api-usage) and in the manual's `references/api.md`.

**Ideas**

| What it does | How to call it |
|---|---|
| List ideas (filter by project / status / tags / keyword, paginated) | `GET /api/ideas?projectId=&status=all\|open\|done&tags=a,b&tagMode=or\|and&q=&limit=&offset=` |
| Record an idea (optionally with tags) | `POST /api/ideas`, body `{"projectId":"","content":"…","tags":["a"]}` → 201 |
| Change the body (send only the fields you want to change) | `PATCH /api/ideas/{id}`, body `{"content":"new body"}` |
| Replace the tag set (**omitting `tags` clears it**) | `PUT /api/ideas/{id}/tags`, body `{"tags":["coffee"]}` |
| Mark done / not done (`done` is required by the contract; **the server does not validate it** — omitting it means false = not done) | `POST /api/ideas/{id}/complete`, body `{"done":true}` |
| Delete an idea (**soft delete**, it goes to the trash) | `DELETE /api/ideas/{id}` → 204 |
| Restore an idea from the trash | `POST /api/ideas/{id}/restore` |
| Bulk mark done (`done` is not validated either — omitting it means "not done") | `POST /api/ideas/bulk/complete`, body `{"ids":[…],"done":true}` |
| Bulk add tags (appends to the union; only `replace: true` makes it a **whole-set replacement**) | `POST /api/ideas/bulk/tags`, body `{"ids":[…],"tags":["a"]}` |
| Bulk delete (**soft delete**) | `POST /api/ideas/bulk/delete`, body `{"ids":[…]}` → 200 + array |
| Merge several ideas into one (**the source ideas are hard-deleted**) | `POST /api/ideas/merge`, body `{"ids":[…, …],"content":"optional"}` |
| Search the whole database (ideas / projects / tags, each paginated) | `GET /api/search?q=keyword&limit=&offset=&includeTrash=` |

**Projects and settings**

| What it does | How to call it |
|---|---|
| List projects | `GET /api/projects` |
| Create a project | `POST /api/projects`, body `{"name":"coffee"}` |
| Rename a project (`name` is **required**) | `PATCH /api/projects/{id}` |
| Delete a project (**its not-yet-deleted ideas go to the trash with it**) | `DELETE /api/projects/{id}` → 204 |
| Restore a project from the trash (the ideas deleted together with it are restored too) | `POST /api/projects/{id}/restore` |
| Reorder the projects (the ones you leave out come last) | `POST /api/projects/reorder`, body `{"ids":["id1","id2"]}` |
| Read the settings | `GET /api/settings` → `{"project.sort_mode":"recent"}` |
| Change a setting (**only `project.sort_mode` is writable**) | `PUT /api/settings/{key}` (the key is currently only `project.sort_mode`), body `{"value":"recent\|manual"}` |
| Tag dictionary (with usage counts) | `GET /api/tags` |
| Tag completion suggestions | `GET /api/tags/suggest?projectId=&prefix=` |

**Trash**

| What it does | How to call it |
|---|---|
| Trash contents (deleted projects are containers; the top level holds individually deleted ideas) | `GET /api/trash` |
| Automatic hard delete once the retention period expires | Configured only in `Trash:RetentionDays` (default 30 days) — neither the page nor the API can change it; a change takes effect after a restart, and **there is no manual purge entry point**. But `merge` hard-deletes the source ideas for you (they do not go to the trash, see below) |

**Real-time events (SSE)**

| What it does | How to call it |
|---|---|
| Subscribe to the full event stream | `GET /api/events` (`Accept: text/event-stream`) |
| Catch up after a disconnect | Send the `Last-Event-ID` header when reconnecting (or `?lastEventId=`) |
| Tell the client to reload when catch-up is impossible | The server sends `event: resync` + `data: {}` (a control frame, no id) |

There are 8 event types in total: `idea.created` / `idea.updated` / `idea.deleted` / `idea.restored` / `idea.merged` /
`project.created` / `project.updated` / `project.deleted`.

**Webhooks (events pushed out)**

| What it does | How to call it |
|---|---|
| CRUD over webhooks (target URL, method, headers, body template, subscribed events, scope, secret) | `GET/POST /api/webhooks`, `GET/PATCH/DELETE /api/webhooks/{id}` |
| Telegram template preset | `GET /api/webhooks/templates/telegram` |
| Delivery records | `GET /api/webhooks/{id}/deliveries` |
| Manually retry one delivery | `POST /api/webhooks/{id}/deliveries/{deliveryId}/retry` |

When creating one: `scope` can only be `global` or `project` (`global` cannot carry a `projectId`, `project` must carry
the id of an existing project); **`bodyTemplate` is required and must be a JSON object** (placeholders may only appear
inside string values); global webhooks are **disabled by default at creation time**, project-scoped ones are enabled by
default. Delivery is asynchronous: on failure it retries with a 1s / 5s / 30s backoff, and the total attempt count
includes the first try (4 attempts by default).

**Operations**

| What it does | How to call it |
|---|---|
| Health check | `GET /api/health` → `{"status":"ok"}` |
| Fetch the interface contract | `GET /openapi/v1.json` |
| Fetch the agent manual | `GET /skill/idea-manager/version`, `GET /skill/idea-manager/{path}` |

### Frontend Pages and Interactions

Vue 3 + TypeScript + Vite, no UI framework, no state library (module-level `ref` singletons, not Pinia).
See [`web/README.md`](web/README.md) for the details, and [`web/e2e/COVERAGE.md`](web/e2e/COVERAGE.md) for the
feature × test-case mapping.

**Route table** (history mode; unknown addresses fall back to the home page in the frontend)

| Path | Page | What it does |
|---|---|---|
| `/` | HomePage | Record ideas, edit, mark done, delete, add tags, filter, bulk operations, merge, live SSE sync |
| `/projects` | ProjectsPage | Create / rename / delete projects, change the sort mode (recent activity / manual order), enter project-scoped webhooks |
| `/trash` | TrashPage | Browse the trash (containers can be expanded), restore projects or ideas |
| `/webhooks` | WebhooksPage | Create / edit / enable / disable global webhooks, subscribe to events, delivery records and retries |
| `/projects/:id/webhooks` | ProjectWebhooksPage | Project-scoped webhooks, the same but limited to that project |

**Home page layout** (two breakpoint sets — 1024px and 768px — coexist)

| Width | Layout |
|---|---|
| ≥1024px | A 220px project sidebar on the left + an idea stream that fills the right side (two interleaved columns); the top bar / list / composer are three separate bands, and only the list scrolls |
| <1024px | The sidebar collapses into a project dropdown in the top bar, the filter row scrolls away with the content, and the top bar gains a 🔍 button that scrolls the list back to the top and focuses the search box in the filter row |
| <768px | Single-column list |

**Home page interactions**

| What it does | How to do it |
|---|---|
| Record an idea | Enter submits from the composer at the bottom, Shift+Enter inserts a newline; **it clears the input only on success and keeps the draft on failure; after submitting, the focus goes back to the input either way** |
| Change the body | Click the body to enter edit mode → Ctrl+Enter saves, Esc cancels, losing focus saves automatically; an unchanged body sends no request |
| Done / delete | Click the ○/● in the card's meta row to toggle done; 🗑 delete asks for confirmation first, and the idea lands in the trash |
| Edit tags | Click 🏷 at the bottom right of the card → Enter / space / comma adds a tag, the arrow keys pick a candidate, × removes one; every change is persisted immediately |
| Filter | The filter row at the top: a three-state status, a keyword, multi-select tags + an AND/OR switch; **filtering happens on the server**, one request returns the final result |
| Bulk operations | Tick the checkboxes on the left of the cards → the action bar: mark done / unmark done / add tags (union) / merge / delete / clear selection |
| Merge | "Merge" in the bulk bar → a dialog previews each body, joined with blank lines by default and editable; on confirmation the source entries are hard-deleted and a new entry is created |
| Deep link to an entry (the URL carries the id, so it lands on that one entry) | `/?idea=<idea id>` scrolls the home page to that entry and flashes a highlight |
| Scroll back through history | Scroll up: 400px from the top automatically preloads an older page, and after the prepend the viewport stays pinned in place; **the bottom is "now"** |
| Live sync | The page subscribes to SSE automatically; on a disconnect it reconnects with exponential backoff (2s → 30s); on `resync` it reloads the whole list |
| New-content notice | While pinned to the bottom it follows along; when you have scrolled up it does not steal the scroll and a "↓ N new items" button appears |

The other pages: the project management page can create / rename / delete (with confirmation), the trash page can
restore, and the webhook page manages both the global and the project-scoped set. Every failure turns into a toast in
the bottom-right corner (gone after 4 seconds).

### Agent Skill Integration

The agent-side manual is **distributed by the server** — when the server changes the API, the manual the agent fetches
changes with it, **with no need to reinstall the skill** (not a single file of the thin shell has to change).

The three-step chain:

```
install the thin shell (2 files) → fetch the manual (fetch.py) → call the API per the manual (curl)
```

- **Thin shell**: `skills/idea-manager-client/` — one pointer, `SKILL.md`, plus one fetcher, `scripts/fetch.py`.
- **Content source**: `skills/idea-manager/` — the index page plus five detail files under `references/`, distributed
  together with the artifact.
- **Distribution endpoints**: `GET /skill/idea-manager/version` (the manifest: version + file list + a sha256 for each
  file), `GET /skill/idea-manager/{path}` (the body of the file).

The full steps for installing the thin shell, fetching the manual and calling the API are in
[Agent Skill](#agent-skill).

---

## Repository Layout

```
IdeaApp.sln                 The solution
Directory.Build.props       Global build properties (TreatWarningsAsErrors and the like)
verify.sh                   One command for all eight gates (see "CI and Gates")
global.json                 Pins the .NET SDK at 10.0.113 (rollForward=latestPatch)
openapi/idea-v1.json        A snapshot of the OpenAPI 3.1 contract (committed to the repo)
docs/
  nginx/idea.conf           The nginx reverse-proxy configuration
  superpowers/
    specs/                  Four design specs (see "Design Trade-offs")
    plans/                  Implementation plans
    reviews/                Interface review conclusions
.github/workflows/ci.yml    GitHub Actions (6 jobs)
server/
  IdeaApp/                  The backend application (.NET 10, Native AOT)
    Domain/                 The domain model, MonotonicGuid (UUIDv7), the tokenizer
    Application/            The use-case service layer (projects / ideas / tags / trash / webhook / delivery / search)
    Infrastructure/         SQL, the event bus, SSE, webhook delivery, the OpenAPI export, schema.sql
    Contracts/              DTOs, request bodies, the JSON source-generation context (types precompiled for AOT, so no runtime reflection), the list of error codes
    Endpoints/              The endpoint mapping per resource (MapXxx extension methods)
    Program.cs              The composition root: registration, endpoint mapping, static files and the SPA fallback
    appsettings.json        The default configuration
    wwwroot/                Where the frontend build output lands (excluded by .gitignore, not in the repo)
  IdeaApp.Tests/            Integration tests (WebApplicationFactory + a real SQLite)
  scripts/aot-smoke.sh      The AOT artifact smoke script (including the cross-stack check)
tools/ExportOpenApi/        The small tool that exports the OpenAPI document
web/                        The frontend (Vue 3 + TypeScript + Vite)
  src/pages/                Route-level pages
  src/components/           Reusable components
  src/composables/          Reusable composables
  src/stores/               Global state
  src/api/                  The layer that calls the backend (REST + SSE)
  src/lib/                  Pure helper functions
  src/styles/               Global styles
  e2e/                      The Playwright end-to-end cases and their harness
  playwright.config.ts      The Playwright configuration (port, baseURL, webServer)
skills/
  idea-manager/             The skill content source (index page + references/, distributed by the server; at publish time the csproj copies it into the artifact's skill/ directory by an explicit file list — the runtime no longer reads the repository)
  idea-manager-client/      The thin shell installed on the agent side (pointer + fetcher)
  install-agent-skill.sh    Installs the thin shell into the agent's skills directory
```

---

## Building

### Frontend

```bash
cd web
npm ci                 # first time
npm run build          # the output goes to server/IdeaApp/wwwroot/
```

The remaining scripts (all of them run in the `web/` directory):

| Command | What it does |
|---|---|
| `npm run dev` | The Vite dev server (`/api` is proxied to `http://127.0.0.1:5080`, **the backend has to be running first**) |
| `npm run preview` | Preview the build output with Vite's static server |
| `npm run typecheck` | `vue-tsc --noEmit`, runs **two** tsconfigs: the app and e2e |
| `npm test` | Vitest (jsdom environment) |
| `npm run e2e` | Playwright end-to-end (`playwright test`; the server is started by the `webServer` in `playwright.config.ts` and torn down afterwards) |
| `npm run e2e:report` | Open the Playwright report |

The frontend uses history mode (`/`, `/projects`, `/trash`, `/webhooks`, `/projects/:id/webhooks` are all real paths),
and the backend falls back to unmatched GETs with `MapFallbackToFile("index.html")` — those few lines of wiring live
in `server/IdeaApp/Program.cs`: `UseStaticFiles` + `MapFallbackToFile`, plus **one provider override**
(`WebRootFileProvider`) — `wwwroot` is excluded by gitignore and therefore does not exist in a fresh clone, so
creating the directory alone is not enough: you have to point at an explicit `PhysicalFileProvider`, otherwise the
static file middleware ends up with a provider that cannot find any file.

> ⚠️ **The SPA fallback is a catch-all and catches every GET that matched no endpoint** — so `/api/**` has to be
> taken by an **earlier** fallback (`app.Map("/api/{**rest}", …)`, the literal segment `api` in the route wins over the
> catch-all template), otherwise a mistyped path turns into **200 + HTML**: this API is meant to be read by agents
> (the contract is in `openapi/idea-v1.json`), and an agent that only looks at the status code would read
> "mistyped path" as success.

### Backend

```bash
# Development (repo root)
dotnet build IdeaApp.sln

# Tests (repo root)
dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj

# Native AOT publish (self-contained single file) — repo root
dotnet publish server/IdeaApp/IdeaApp.csproj -c Release -r linux-x64 \
  -p:PublishAot=true -p:InvariantGlobalization=true
```

The output lands in `server/IdeaApp/bin/Release/net10.0/linux-x64/publish/`:

| File | Has to be copied along? | Notes |
|---|---|---|
| `IdeaApp` | Yes | The executable (native ELF) |
| `appsettings.json` | Yes | Configuration (default Urls / database path / log level) — **missing it falls back to the default port and the default database path** |
| `libe_sqlite3.so` | Yes | The native SQLite library (dynamically linked) — **missing it makes SQLite fail to load** |
| `wwwroot/` | Yes | The frontend build output — **missing it leaves a blank page and reports nothing** |
| `skill/idea-manager/` | Only if you want the agent skill | The distributed copy of the skill — **missing it makes the manual endpoint answer 404 `skill_not_distributed`** |
| `IdeaApp.dbg` | No | Debug symbols |

> ⚠️ **You must `cd web && npm run build` before publishing** — `wwwroot/` is excluded by gitignore, so the
> repository does not contain it. Forget this step and you publish an **empty wwwroot**: the app starts as usual,
> `/api/*` works as usual, but the page is blank and **nothing reports an error**.

> ⚠️ **The RID has to be given explicitly** (`-r linux-x64`): a publish without a RID fails to restore (NU1101)
> under the csproj's `RuntimeFrameworkVersion` condition.

AOT has pitfalls of its own: JSON always goes through a source-generated context (`IdeaJsonContext`). Every new DTO
has to be registered in `IdeaJsonContext` —
**omitting the registration throws `NoMetadataForType` already during development**
(the source-generated context is assigned directly to the `TypeInfoResolver`, not plugged into the resolver
chain), so the test phase catches it. But AOT has another class of degradation (reflection, trimming) that only
a real artifact exposes, so after changing an endpoint run the AOT smoke test:

```bash
bash server/scripts/aot-smoke.sh
```

It publishes a real AOT artifact, exercises the endpoints, and then has the repository's **real fetcher** face it once
(cross-stack check: sha256 verified file by file). A missing python3 is a **failure**, not a skip.

---

## Running

### Development

Two things: **the backend first, then the frontend**.

```bash
# ① Backend — repo root
IDEA_Database__Path="$PWD/server/IdeaApp/data/idea.db" \
  dotnet run --project server/IdeaApp
```

```bash
# ② Frontend — web/ directory
cd web
npm run dev
```

The backend listens on `http://127.0.0.1:5080` by default (the `Urls` entry in `appsettings.json`), the frontend dev
server runs on `http://127.0.0.1:5173`, and `/api` is forwarded by the Vite proxy to `http://127.0.0.1:5080` —
**without the backend running, every data request from the frontend fails**.

### Production (AOT artifact)

```bash
PUBLISH=server/IdeaApp/bin/Release/net10.0/linux-x64/publish
cd "$PUBLISH"

IDEA_Urls=http://127.0.0.1:5080 \
IDEA_Trash__RetentionDays=30 \
IDEA_PublicBaseUrl=https://ideas.example.com \
./IdeaApp
```

**The first start creates the database and the tables** and writes the built-in inbox (`inbox`); there is no separate
migration step. The database lives at `data/idea.db` by default — a relative path is resolved against the
**application directory** (the directory the executable lives in), **not the current working directory** (see
[Which directory is the application directory](#which-directory-is-the-application-directory) below).

The app serves the frontend pages at `/` as well, and frontend routes such as `/projects`, `/trash`, `/webhooks` are
covered by `MapFallbackToFile("index.html")`, so a deep link answers 200 instead of 404.

`/api/**` does not go through that fallback: an unmatched API path answers **404 + ProblemDetails**
(`code=api_not_found`), not 200 + HTML — this API is meant to be read by agents.

### Configuration

The configuration lives in `server/IdeaApp/appsettings.json` and is overridden with environment variables:
**the prefix is `IDEA_`, a double underscore means one nesting level**
(`IDEA_Trash__RetentionDays` corresponds to `Trash:RetentionDays`).

Environment variables without the prefix work as well (`Urls=`, `Trash__RetentionDays=` still take effect); both sets
coexist, and **the ones with the `IDEA_` prefix win** (the provider registered later wins).

> ⚠️ **The `IDEA_` prefix has a higher priority than the command-line arguments** — the **opposite** of the usual
> ASP.NET Core convention (normally the command-line arguments beat the environment variables). The reason is that
> the `IDEA_` providers are registered **later**, and configuration sources are resolved in registration order, so the
> later one wins.
> Measured: `IDEA_Urls=http://127.0.0.1:5192 ./IdeaApp --urls http://127.0.0.1:5193`
> listens on **5192** (the `IDEA_` one), 5193 is not reachable.

| Key | Default | Notes |
|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | The Kestrel listen address (loopback only, everything external goes through nginx) |
| `Database:Path` | `data/idea.db` | The SQLite file path. **A relative path is resolved against the application directory, independent of the current working directory** |
| `Trash:RetentionDays` | `30` | How many days the trash is kept. **Neither the page nor the API can change it**; it is the only switch that makes the contents of the trash disappear once the period is over, a value ≤0 is treated as 1 day and logged as a warning. There is another, immediately irreversible path: `merge` hard-deletes the source ideas without putting them into the trash |
| `PublicBaseUrl` | empty | Used to build `{{idea.url}}`, e.g. `https://ideas.example.com` |
| `Api:MaxIdeasPerResponse` | `2000` | The cap on the number of hits **when no `limit` is given**; going over it reports 400 `too_many_ideas`. When `limit` is given the cap is **1000** (hard-coded in the service layer), and `offset` has to be given together with `limit` |
| `Webhook:TimeoutSeconds` | `10` | The timeout of a single delivery |
| `Webhook:MaxAttempts` | `4` | The maximum number of delivery attempts (**including the first one**) → the first attempt plus 3 retries |
| `Webhook:MaxDeliveryRecords` | `500` | How many delivery records are kept per webhook |
| `Sse:BufferSize` | `500` | How many events the server buffers (for `Last-Event-ID` catch-up). **In memory only, a restart makes catch-up impossible** |
| `Sse:HeartbeatSeconds` | `25` | The SSE heartbeat interval (sends a `: ping` comment frame so an intermediary proxy does not cut the idle connection) |
| `Skill:Path` | `skill/idea-manager` | The root of the distributed copy of the skill. **A relative path is resolved against the application directory, independent of the current working directory** (the same rule as `Database:Path`) |

There is one more switch that only recognizes environment variables: `IDEA_EXPORT_OPENAPI=<path>` — when it is set, the
application writes the OpenAPI document to that file after startup (see
[OpenAPI and API Usage](#openapi-and-api-usage)).

#### Which directory is the application directory

The relative paths of `Database:Path` and `Skill:Path` are anchored to the **application directory**, that is the
directory the executable lives in (`AppContext.BaseDirectory` in the code):

| How it is run | Application directory | Where `data/idea.db` lands |
|---|---|---|
| AOT single-file publish (`/opt/idea/IdeaApp`) | `/opt/idea/` | `/opt/idea/data/idea.db` |
| `dotnet run` (development) | `server/IdeaApp/bin/Debug/net10.0/` | `server/IdeaApp/bin/Debug/net10.0/data/idea.db` |

**It must not be resolved against the current working directory**: under systemd without `WorkingDirectory` set the
CWD is `/`, and the database file would land in `/data/idea.db` — unpredictable, possibly without the permission,
and it may clobber unrelated data. If you want the database file inside the project directory during development,
give an absolute path explicitly (see [Running · Development](#development)).

**If you follow the [Quick Start](#quick-start), the database lands inside the publish directory** —
`server/IdeaApp/bin/Release/net10.0/linux-x64/publish/data/idea.db` — and `bin/` is in `.gitignore`
(line 2 of `.gitignore`), so a single `git clean -xdf` wipes it together with the WAL. If you want a stable
location, point `IDEA_Database__Path` outside the publish directory (for example `/var/lib/idea/idea.db`).

### Data and Storage

- **Database**: a SQLite database with hand-written SQL, running in **WAL mode** — while the application runs, the
  data is spread over **three files** at once (see [Data and Backup](#data-and-backup) below). The schema is embedded
  as a resource (`server/IdeaApp/Infrastructure/schema.sql`); the first start creates the database and the tables and
  writes the built-in inbox `inbox`.
- **Full-text index**: written in the **same transaction** as the business data, so it is strongly consistent. An old
  database is backfilled in one go at startup based on the `search_meta` version —
  **a failed backfill makes the startup fail**: when the index and the data disagree, search returns
  **wrong results**, and that is far more dangerous than not starting at all.
- **id**: a self-implemented RFC 9562 UUIDv7 (32 lowercase hex digits without hyphens). .NET's built-in
  `Guid.CreateVersion7()` is not monotonic within the same millisecond, so the counter method was implemented by hand.
- **There is no "title"**: an idea is just a piece of text. The table has no such column, the interface has no such
  field, and the templates have no `{{idea.title}}`. An old client that still sends `title` is **silently ignored**
  by System.Text.Json (no error).
- **Ordering**: lists are always `updated_at ASC, id ASC` (the oldest on top); entries inserted at the same instant are
  ordered by id.

### Data and Backup

**There are three files on disk, not one** — the database runs in WAL mode (`PRAGMA journal_mode=WAL`), so while
the application runs the data is in `idea.db`, `idea.db-wal` and `idea.db-shm` at the same time (`.gitignore`
covers those three suffixes as well). **Copying `idea.db` while the application is running loses the writes that
are still in the WAL and not checkpointed yet** — the copy looks successful and the data is short.

There are two ways to back it up:

| How | What you do | The price |
|---|---|---|
| Stop the application and copy everything (the safest) | Stop the process → copy `idea.db`, `idea.db-wal` and `idea.db-shm` **together** | The application is down |
| Back up without stopping, via sqlite3 | Run this in the application directory: `sqlite3 data/idea.db ".backup '/backup/idea-$(date +%F).db'"` | The sqlite3 command line has to be installed |

Restoring (the order matters):

1. **Stop the application**;
2. overwrite `idea.db` with the backup;
3. **delete the leftover `idea.db-wal` and `idea.db-shm`** — left in place, SQLite replays the old WAL onto the
   database you just restored;
4. start the application.

> ⚠️ **The hard delete when the trash expires is irreversible** — once the retention period is over, the background
> job deletes the rows, and there is no intermediate state anything could rescue. **A backup is the only safety
> net** (the hard delete of the source ideas by `merge` is the same: it does not even go through the trash).

---

## Deployment

The application only listens on the loopback address; everything exposed externally goes through nginx (TLS and
access control live in the nginx layer — the application itself does no authentication and no Origin/CSRF checking).

```bash
cp docs/nginx/idea.conf /etc/nginx/conf.d/idea.conf
# Adjust server_name, the certificate paths and the htpasswd path for your own domain (there is a list below)
htpasswd -c /etc/nginx/.htpasswd ideauser
nginx -t && systemctl reload nginx
```

> ⚠️ **The `http2 on;` in the configuration needs nginx ≥ 1.25.1** — older nginx versions fail already at the
> `nginx -t` stage, so copying the configuration as it is leaves you stuck.

There are **three places to change** for your own domain in `docs/nginx/idea.conf` (everything else is copied as it
is):

| Setting | The default in the file | Change it to |
|---|---|---|
| `server_name` | `ideas.example.com` | your domain |
| `ssl_certificate` / `ssl_certificate_key` | `/etc/letsencrypt/live/ideas.example.com/fullchain.pem` / `…/privkey.pem` | your own certificate paths (both of them) |
| `auth_basic_user_file` | `/etc/nginx/.htpasswd` | your own htpasswd path — it has to be the one the `htpasswd -c` command above writes |

Two more things to watch in `docs/nginx/idea.conf`:

- **SSE (`/api/events`) has its own block**: `proxy_buffering off` + `X-Accel-Buffering: no`, otherwise the live
  pushes are held back by nginx instead of being sent.
- **The request body limit `client_max_body_size 4m`** (4 MiB = 4 194 304 bytes, nginx's `m` is 1024-based), which is
  hit before Kestrel's own default limit (30 000 000 bytes) — requests going through nginx never reach Kestrel.
  When it is hit, the 413 comes from **nginx itself**, and the response body is structured JSON (ProblemDetails
  shape) but **deliberately carries no `code`** (connecting to Kestrel directly gives 413 with an empty body).
  **Changing this number means changing `references/errors.md` in the manual as well** — both describe the same thing.

The published artifact carries the distributed copy of the skill — that is where the agent's manual comes from, so
copy it to **`skill/idea-manager/` under the application directory** when deploying (that is the configuration item
`Skill:Path`; the application directory is the directory the executable lives in). Check it afterwards:

```bash
# run this in the application directory (the one the executable lives in)
ls skill/idea-manager/SKILL.md   # without it the manual endpoint answers 404 skill_not_distributed
```

Without it, the endpoint that serves the manual answers 404 `skill_not_distributed`
(where the copy lives is decided by `Skill:Path`);
**a wrong path gives two other codes**: a misspelled skill name → `skill_endpoint_not_found`, a misspelled
file name → `skill_file_not_found` — different directions to debug.

The application side has `UseForwardedHeaders()`: `X-Forwarded-Proto` / `X-Forwarded-For` / `X-Forwarded-Host` are
trusted, **but only from a proxy on the loopback address**. It works out of the box when nginx and the application run
on the same machine; when nginx runs on another machine you have to add `KnownProxies` in the code, otherwise these
headers have no effect (the application thinks it is still on http). Note that .NET 10's `ForwardedHeadersOptions`
defaults to `ForwardedHeaders.None` — unless you explicitly name the headers to handle, the middleware is effectively
not attached.

### The first visit

Open `https://<your domain>` in the browser and nginx pops up its **basic auth dialog** — log in with the account
you created with `htpasswd -c` (**the application itself knows no accounts**: the authentication is entirely in the
nginx layer, the application receives requests that already passed nginx).

Agents use the same account: the environment variables `IDEA_USER` / `IDEA_PASS` (see
[Connection settings](#connection-settings)). Against a local instance (`http://127.0.0.1:5080`) neither of them is
needed.

### Upgrading

```bash
# ① Back up the database first (see "Data and Backup")
# ② Stop the service
systemctl stop idea            # or just kill the process
# ③ Replace the publish directory — leave data/ and a changed appsettings.json alone
# ④ Start the service again
systemctl start idea
```

- **Replace the publish directory only** (the executable + `wwwroot/` + `skill/` + `libe_sqlite3.so`).
- **Keep `data/`** — the database lives in there, and publishing again does not overwrite it (`data/` is not part of
  the publish output).
- **Keep `appsettings.json`** — it **is** part of the publish output, so overwriting it resets a configuration you
  changed back to the defaults; merge that one file by hand instead.
- If you changed the content source in `skills/idea-manager/`, you also have to publish again and replace the
  artifact, otherwise the agent's next fetch still gets the old manual (see [Agent Skill](#agent-skill)).

---

## Troubleshooting

Look it up by symptom, not by module — symptom → cause → what to do:

| Symptom | Cause | What to do |
|---|---|---|
| **The page is blank and nothing reports an error** | `wwwroot/` was not built or not copied into the artifact | Build the frontend (`cd web && npm run build`) → publish again → have a look with `ls <application directory>/wwwroot/index.html` |
| **It does not start: the port is taken / the bind fails** | The default port 5080 is already in use | Change the port: `IDEA_Urls=http://127.0.0.1:5190 ./IdeaApp` (the `IDEA_` prefix beats the command-line arguments) |
| **Fetching the manual answers 404 `skill_not_distributed`** | The distributed copy was not copied, the path is not configured, or the copy is missing its entry file `SKILL.md` | `ls skill/idea-manager/SKILL.md` in the application directory; check `Skill:Path` |
| **Fetching the manual answers 404 `skill_endpoint_not_found`** | The skill name is misspelled (the path is not under the `skill/idea-manager/` prefix) | Check the path prefix |
| **Fetching the manual answers 404 `skill_file_not_found`** | The file name is misspelled, or the file is over the 4 MiB limit | Check the file name and its size |
| **413 from nginx** | The request body is over `client_max_body_size 4m` (4 MiB) | Change the setting and change the manual's `references/errors.md` with it |
| **The fetcher exits with code 3** | The server is temporarily unreachable and the previous version of the manual from the local cache is used | Exit code 3 is the exclusive "degraded" signal: retry once, or tell the user that the server is temporarily unreachable |
| **The fetcher exits with code 1 and `missing_config`** | `IDEA_BASE_URL` was not exported (it is required) | `export IDEA_BASE_URL=http://127.0.0.1:5080` (a local instance) or your domain |
| **The data in the database looks wrong / is incomplete** | The database runs in WAL mode — check the three files first | `ls <application directory>/data/idea.db*`; **do not copy `idea.db` alone** (see [Data and Backup](#data-and-backup)) |

---

## CI and Gates

### `verify.sh` — eight gates in one command

At the repo root:

```bash
bash verify.sh
```

The script cds to its own directory by itself. **All eight steps run to completion** before it summarizes; it is not
fail-fast, and if any step fails the overall exit code is non-zero.

| Step | What it runs |
|---|---|
| 1 | `node tools/check-readme.mjs` — the **mechanical documentation gate**: the links, commands, API paths, configuration defaults, gate step count and SDK version in the two READMEs must not drift away from the repository (well under a second) |
| 2 | `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj` — the server (including the skill distribution endpoints, the contract snapshot, the documentation drift guard) |
| 3 | `python3 -m unittest discover -s skills/idea-manager-client/tests` — the unit tests of the fetcher |
| 4 | `bash server/scripts/aot-smoke.sh` — a real AOT artifact + a **cross-stack** check (real artifact ↔ real fetcher) |
| 5 | `bash skills/idea-manager-client/scripts/run-tests.sh` — the test script of the fetcher (with one smoke test against a real HTTP server) |
| 6 | `npm --prefix web run typecheck` — the frontend type check (two tsconfigs: app + e2e) |
| 7 | `npm --prefix web test` — the frontend unit tests (vitest) |
| 8 | `bash web/e2e/run-e2e.sh` — the end-to-end cases (Playwright) |

> The documentation gate comes **first**: it is the cheapest one, and documentation is the most common thing to
> change — drift should become visible right away instead of after ten minutes of e2e.

> ⚠️ **A missing prerequisite is a "failure", not a "skip"**: the script first checks that python3, node and npm are
> on the PATH and that `web/node_modules` exists; if any of them is missing it exits 1 right away.
> Skipping is the same as not running — a gate that did not run is more dangerous than no gate at all (it looks green).
> If the frontend dependencies are missing, run `cd web && npm ci` first.

### GitHub Actions

`.github/workflows/ci.yml` triggers on push and pull_request, split into **6 independent jobs**:

| Job | What it runs |
|---|---|
| docs | `node tools/check-readme.mjs` — the mechanical documentation gate |
| backend | `dotnet test` |
| frontend | `npm ci` → `typecheck` → `vitest` → `build` |
| e2e | install Playwright's chromium → warm up the backend build → `npm run e2e` |
| client | the python unit tests of the fetcher + the test script of the fetcher |
| aot | install `clang`/`zlib1g-dev` → build the frontend → `bash server/scripts/aot-smoke.sh` |

The versions are pinned: .NET SDK **10.0.113** (pinned in one place — `global.json` and CI agree), Node **24** (the
same as locally). Older runs of the same ref are cancelled automatically; `permissions: contents: read`, so CI does
not write back to the repository.

> ⚠️ **CI does not run `verify.sh`** — it maps those commands **one by one** onto the 6 jobs, so a failing backend
> does not have to wait for the frontend to finish, and each required check can turn red on its own.
> If you change the steps in `verify.sh`, change CI in step with it, and vice versa.

---

## OpenAPI and API Usage

### Where the contract lives

- **The contract file**: [`openapi/idea-v1.json`](openapi/idea-v1.json) (OpenAPI 3.1, committed to the repository).
  It is a snapshot of a build artifact: the paths, parameters, request and response body schemas, and status codes are
  all in it. The file has no `servers` section — the address is a runtime matter, the public address is decided by
  nginx, and the contract only covers the interface itself.
- **The runtime document**: after the application starts, `/openapi/v1.json` serves content identical to the contract
  file — this is the copy the agents get (the manual points at it).
- **There is no second contract in the distributed copy of the skill**: to get the contract, fetch
  `/openapi/v1.json`.

**Re-export** (rerun this after changing an endpoint, a DTO or a status code):

```bash
# repo root
dotnet run --project tools/ExportOpenApi            # writes openapi/idea-v1.json
dotnet run --project tools/ExportOpenApi -- <path>    # writes to the given path
```

The tool starts the **application itself** in-process (`WebApplicationFactory`), so the exported document necessarily
matches the endpoints the application actually exposes. By default it writes the **file tracked in the repository**,
which has to be committed along with the change.

**The other export path**: export from a real application process; the result is identical:

```bash
IDEA_EXPORT_OPENAPI=/tmp/idea-v1.json ./IdeaApp
```

### Reading errors

The 4xx / 5xx of **business errors** are always RFC 9457 ProblemDetails (content type `application/problem+json`)
with a machine-readable extension field `code`:

```json
{ "type": "about:blank", "title": "…", "status": 400,
  "detail": "…", "code": "invalid_pagination" }
```

- **Branch on `code`, do not match the text of `detail`** (detail is for humans and it changes).
- The description of each error response lists the `code`s that **that endpoint** can return; the description of
  `components.schemas.ProblemDetails` lists the full set of `code` values.
- The authoritative list is in `server/IdeaApp/Contracts/ApiErrorCodes.cs`, and the guard test checks the codes that
  are actually thrown in the source (a missing registration fails the test).
- The business error codes come in four kinds: 400 (the request is invalid), 404 (the thing does not exist),
  415 (the media type is wrong), 503 (the service cannot take it right now), plus 500 `internal_error`.

> ⚠️ **Two kinds of exception carry no `code`; do not branch on `code` to read them**:
>
> | Case | Response |
> |---|---|
> | An illegal `bool` parameter in the query string (e.g. `?includeTrash=xyz`) | 400, **an empty body and no `code`** — the binding failure happens at the routing layer and bypasses the single error-handling exit point, which only handles `BadHttpRequestException` |
> | 413 request body too large | Status code 413, **no `code`** (through nginx it is structured JSON but deliberately carries no `code`; connecting to Kestrel directly it is an empty body) |
>
> Branch on the **status code** for these two. That is exactly what the manual's `references/errors.md` says.

### How to call

Agents call the API directly with curl, with no wrapper in between:

```bash
# GET: a list
curl -sS --fail-with-body "$IDEA_BASE_URL/api/ideas?status=open" \
  -u "$IDEA_USER:$IDEA_PASS"

# POST: the request body goes through a file (safest for special characters)
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @body.json

# PATCH: send only the fields you want to change (do not send a single key you are not changing)
curl -sS --fail-with-body -X PATCH "$IDEA_BASE_URL/api/ideas/$ID" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @patch.json
```

- Use `--fail-with-body`, **do not add `-f`** (`-f` swallows the error body).
- Keep the `-u` in the template **only when there are credentials**; add `-H "X-Api-Key: $IDEA_TOKEN"` when
  `IDEA_TOKEN` has a value.
- **Do not put the credentials in the URL** — nginx writes the userinfo into the access log verbatim.
- A request with a body needs `Content-Type: application/json` (without it you get 415).
- **Always send `done` yourself** — the contract marks it required and the server does not validate it: leaving it
  out means `false` (= not done), and **nothing reports an error**. When you send `complete` / `bulk/complete`, write
  `"done":true` or `"done":false` explicitly.
- **Do not build the request body by concatenating strings in the shell** — quotes, newlines and backslashes get eaten
  by the shell. Write it into a file, or feed it to curl with a quoted heredoc (the quotes in `<<'JSON'` cannot be
  omitted, otherwise the `$` inside the body is expanded):

```bash
curl -sS --fail-with-body -X POST "$IDEA_BASE_URL/api/ideas" \
  -u "$IDEA_USER:$IDEA_PASS" -H 'Content-Type: application/json' \
  --data-binary @- <<'JSON'
{"content":"body text"}
JSON
```

Once it works you see the response body: a list is a **bare array** (`[{...}]`), a single resource is an object, and
`DELETE` is 204 with no response body.

**The places where data is easiest to lose**:

1. **Replace vs. union for tags**: `PUT /api/ideas/{id}/tags` is a **whole-set replacement** (omitting `tags` clears
   it), while `POST /api/ideas/bulk/tags` **appends to the union** by default (only `replace: true` replaces). Using
   the wrong one loses tags.
2. **`POST /api/ideas/merge` hard-deletes the source ideas** (they do not go to the trash) — **a wrong merge cannot
   be undone**, so copy the bodies out before you merge. The bulk delete (`POST /api/ideas/bulk/delete`) is the soft
   delete.
3. **A webhook's url and headers are masked by default** (the url keeps only `scheme://host[:port]`, and the path
   segments and the header values are shown as `***`). To get the original values you have to add `?unmask=true`
   explicitly, and **only `GET /api/webhooks` and `GET /api/webhooks/{id}` honour it** (the write endpoints always
   mask, adding it does nothing). When you change other fields, **do not write the masked values back** — leave those
   two keys out entirely.
4. **A bulk completion without `done`** — the server does not validate that field, so leaving it out means `false` =
   **not done**: a whole batch of completed ideas is turned back into unfinished ones, and **nothing reports an
   error**.

**The trap in pagination**: the `offset` of `/api/ideas` counts from the **newest** end (that is how you page back
through history), while the `offset` of `/api/search` counts from the **most relevant** end — the two have opposite
semantics, do not mix them up. The `limit` of `/api/ideas` is capped at 1000; the `limit` of `/api/search` is capped at
100 and defaults to 20. The `q` of `/api/search` is **required** (an empty string → 400 `invalid_search_query`) —
**the contract marks it as required as well** (`required: true`, aligned explicitly by
`OpenApiQueryParameterTransformer`: the endpoint binds it as nullable, and the framework would mark it optional
because of that).

### Endpoint list

The complete contract is on the server at `/openapi/v1.json`; the index page of the manual
[`skills/idea-manager/SKILL.md`](skills/idea-manager/SKILL.md) has the same "capability → method + path" table
(**33 business endpoints**). The table below adds **2 skill distribution endpoints and the `/skill/**` fallback**
on top of it (**36 rows** in total), and is kept in sync with it.

**How the three numbers relate**: 33 business endpoints in the index page of the manual → 36 rows in the table below
(2 skill distribution endpoints and 1 fallback more) → the contract `openapi/idea-v1.json` holds **29 paths / 36
operations** (there are fewer paths than operations because one path carries several methods — for example
`/api/webhooks/{id}` has GET / PATCH / DELETE). **`/openapi/v1.json` is the authority**.

| Capability | Method + path |
|---|---|
| Health check | GET /api/health |
| List ideas (filterable by project / status / tags / keyword, paginated) | GET /api/ideas |
| Search the whole database (ideas / projects / tags, each paginated) | GET /api/search |
| Record an idea | POST /api/ideas |
| Change an idea (send only the fields you want to change) | PATCH /api/ideas/{id} |
| Delete an idea (goes to the trash) | DELETE /api/ideas/{id} |
| Replace the tag set (**omitting `tags` clears it**) | PUT /api/ideas/{id}/tags |
| Mark done / not done (`done` is required by the contract; **the server does not validate it** — omitting it means false = not done) | POST /api/ideas/{id}/complete |
| Restore an idea from the trash | POST /api/ideas/{id}/restore |
| Bulk mark done / not done (`done` is not validated either — omitting it means false = not done) | POST /api/ideas/bulk/complete |
| Bulk add tags (appends to the union; `replace: true` is the **whole-set replacement**) | POST /api/ideas/bulk/tags |
| Bulk delete ideas (goes to the trash) | POST /api/ideas/bulk/delete |
| Merge several ideas into one | POST /api/ideas/merge |
| List projects | GET /api/projects |
| Create a project | POST /api/projects |
| Rename a project (`name` is **required**) | PATCH /api/projects/{id} |
| Delete a project (its ideas go to the trash with it) | DELETE /api/projects/{id} |
| Restore a project from the trash | POST /api/projects/{id}/restore |
| Reorder the projects | POST /api/projects/reorder |
| Read the settings (a key/value dictionary) | GET /api/settings |
| Change a setting (currently only `project.sort_mode`) | PUT /api/settings/{key} |
| Tag dictionary (with usage counts) | GET /api/tags |
| Tag completion suggestions | GET /api/tags/suggest |
| Event stream (SSE) | GET /api/events |
| Trash contents | GET /api/trash |
| List webhooks | GET /api/webhooks |
| Create a webhook | POST /api/webhooks |
| Telegram webhook template preset | GET /api/webhooks/templates/telegram |
| Read one webhook | GET /api/webhooks/{id} |
| Change a webhook | PATCH /api/webhooks/{id} |
| Delete a webhook | DELETE /api/webhooks/{id} |
| The delivery records of a webhook | GET /api/webhooks/{id}/deliveries |
| Manually retry one delivery | POST /api/webhooks/{id}/deliveries/{deliveryId}/retry |
| Skill manifest (version + file list + a sha256 for each file) | GET /skill/idea-manager/version |
| Fetch the body of a skill file | GET /skill/idea-manager/{path} |
| The `/skill/**` fallback (a mistyped path answers 404, not 200 + HTML) | GET /skill/{rest} |

---

## Agent Skill

The **authoritative content of the agent skill lives on the server**: the application ships `skills/idea-manager/` (the
index page + `references/`) along with the artifact, and the manual the agent gets is the **same version** as the
server — when the server changes the API, the manual in the agent's hands changes with it:
**no need to reinstall the skill** (not a single file of the thin shell has to change), and every fetch
re-syncs once more.

> ⚠️ **The prerequisite is that the artifact has been republished**: after changing the content source in
> `skills/idea-manager/`, you still have to publish again and replace the deployed artifact, otherwise the agent's
> next fetch still gets the old manual.
> The content source is copied into the artifact by the csproj, which lists the files explicitly at publish time (it
> does not copy the whole directory, to avoid shipping the leftovers from the development side).

### Delivery shape

| Directory | What it is | Who uses it |
|---|---|---|
| `skills/idea-manager/` | **Content source**: the index page `SKILL.md` + `references/` (each file handles one thing, the routing table is in the index page) | The server distributes it along with the artifact; you edit it in the repository |
| `skills/idea-manager-client/` | **Thin shell**: `SKILL.md` (the pointer) + `scripts/fetch.py` (the fetcher) | Installed into the agent's skills directory |

On the server side there are read-only endpoints: `/skill/idea-manager/version` gives the manifest (version + file
list + a sha256 for each file), `/skill/idea-manager/{path}` gives the content of the file. **A mistyped path**
answers 404 and does not fall through to the SPA fallback as 200 + HTML.

But a request that **escapes the `/skill/` prefix with `..`** is the exception: the server normalizes the path per
RFC 3986, `/skill/idea-manager/../../etc/passwd` is normalized to `/etc/passwd` → it escapes the `/skill/` prefix →
it lands on the **frontend fallback** → **200 + HTML** (no file is read). What the three shapes (mistyped path / `..`
traversal / encoded traversal) each produce is in the manual's `references/errors.md`.

### Installation

**The thin shell is installed from this repository** — the server only distributes the body of the manual; the two
thin-shell files (the pointer and the fetcher) are not in the distributed copy: without this repository at hand you
cannot install it.
(The prerequisites are that the server is running and that the published artifact carries the distributed copy — see
[Deployment](#deployment).)

What gets installed is a **copy**, not a symlink — a symlink to `skills/idea-manager/` (the content source) would
install the content source as if it were the thin shell, and the agent would end up holding a manual that is out of
sync with the server:

```bash
bash skills/install-agent-skill.sh                       # installs to ~/.zcode/skills/idea-manager
bash skills/install-agent-skill.sh ~/.claude/skills/idea-manager
```

The script is idempotent (doing the same thing twice has the same result as doing it once): **the known files left
behind by the old skill** are removed from the target directory
(`scripts/idea.py`, `scripts/run-tests.sh`, `references/openapi.json`, `tests/test_cli.py`, the `.pyc` files in
`__pycache__`), and the directory is only removed **once it is empty** (`rmdir`) — if your own files are still in
there they are kept, and entries it does not recognize only produce a warning and are not deleted. The convention of
the thin shell is "that directory holds exactly two files" — leaving anything else there means the agent might read
it.

After the installation the agent has to **rescan its local skill directory** before it notices it; if the skill is
still invisible in the current session, try a new session (different agent runtimes refresh the local directory at
different times).

After the installation the agent reads the thin shell's `SKILL.md` (the pointer) — it runs `scripts/fetch.py` itself
to fetch the manual; nobody has to remind it to.

### Connection settings

Environment variables, not the repository (the command line does not accept passwords):

| Variable | Required | Notes |
|---|---|---|
| `IDEA_BASE_URL` | Yes | The address of the instance, e.g. `https://ideas.example.com` (with the scheme) |
| `IDEA_USER` | No | The nginx basic auth user name; sent only when the password is set as well |
| `IDEA_PASS` | No | The nginx basic auth password |
| `IDEA_TOKEN` | No | Optional. Set it when nginx additionally validates an `X-Api-Key` header |
| `IDEA_SKILL_CACHE_DIR` | No | The cache root of the fetcher (default `${XDG_CACHE_HOME:-~/.cache}/idea-skill`) |

**A local instance**: when you run one locally (`./IdeaApp`, `http://127.0.0.1:5080` by default),
`export IDEA_BASE_URL=http://127.0.0.1:5080` is enough — **it has no basic auth, so `IDEA_USER` / `IDEA_PASS` are
not needed**.

Set them once and every later command can use them (`export` only affects the current shell):

```bash
export IDEA_BASE_URL=https://ideas.example.com
export IDEA_USER=ideauser
export IDEA_PASS='<password>'
# export IDEA_TOKEN='<token>'    # only needed when nginx also validates X-Api-Key
```

**These variables have to be present in the environment where the agent runs its commands** — the ones you `export` in
another terminal are invisible to the agent. Run `export` before starting the agent, or put them into the agent's
startup configuration (**never put a password into the repository**).

### Fetching the manual

The fetcher is a pure standard-library script, **Python 3.9+** is enough (not a single third-party dependency). The
examples below assume the variables above have already been exported:

```bash
SKILL=~/.zcode/skills/idea-manager      # wherever you installed it (the same target directory as above)

python3 "$SKILL/scripts/fetch.py"                        # index page: which capabilities there are, how to call them, where the details live
python3 "$SKILL/scripts/fetch.py" references/errors.md   # fetch one of the detail files using the routing table from the index page
python3 "$SKILL/scripts/fetch.py" /openapi/v1.json       # the complete interface contract
```

The fetcher downloads every file in the manifest and verifies each sha256; only when they all pass does it switch to
the new version — if it fails halfway the previous version is kept, so you never end up with a mix where the index
page is new but the detail files are old. The cache keeps two versions (`~/.cache/idea-skill/<version>/`), and a
cache hit still recomputes the hashes.

When the server is temporarily unreachable but a **sound** cache is present locally, it prints one `degraded_cache:`
line on stderr and exits with **exit code 3** — what is on stdout then is the **previous** version of the manual (not
the server's current version). **Exit code 3 is exclusive**: it means only "this time you got the previous version",
which keeps it distinguishable from every other failure. An agent that sees it should **retry once**, or tell the
user "the server is temporarily unreachable" — a manual from the previous version makes the agent operate under the
old semantics, and that is exactly what this redesign set out to eliminate.

When the fetch **fails hard** (not the degraded case above), it prints **one** line `code: detail` on stderr and
exits with a non-zero code (no traceback). What each code means is in the manual's `references/errors.md`; the thin
shell's `SKILL.md` also carries a condensed troubleshooting list (missing configuration, wrong credentials, nothing
distributed, content mismatch, …).

Self-check (if it goes through, the address is right, the credentials are right and the server distributes the skill
content) — this block is self-contained and can be copied over as it is:

```bash
# The path has to match the actual install location (change this line if you installed to ~/.claude/skills/idea-manager)
SKILL=~/.zcode/skills/idea-manager
IDEA_BASE_URL=https://ideas.example.com \
IDEA_USER=ideauser IDEA_PASS='<password>' \
python3 "$SKILL/scripts/fetch.py"
```

**Running the self-check without credentials fails immediately with `auth_error`** (exit code 1): the standard
deployment of this project goes through nginx basic auth, and only an instance without basic auth can be used with
empty credentials — for example a local instance (`IDEA_BASE_URL=http://127.0.0.1:5080`, with the `IDEA_USER` /
`IDEA_PASS` lines left out).

### Calling the API

Call the API directly with curl, following the manual, with no wrapper in between — the templates and the pitfalls
are in [How to call](#how-to-call). The complete contract is on the server at `/openapi/v1.json` (served live).
The `openapi/idea-v1.json` at the repo root is a snapshot of the same thing, but that only concerns the repository
itself: the distributed copy contains **no second contract** — the copy the agent holds is served live by the server
at runtime.

The index page of the manual [`skills/idea-manager/SKILL.md`](skills/idea-manager/SKILL.md) has the capability list,
the curl templates, the easiest mistakes to make, and a routing table pointing at the files under `references/`:

| What the agent wants to do | Which file to fetch |
|---|---|
| How to write the request body, how to handle special characters, how to authenticate | `references/calling.md` |
| You got a `code` and have to decide how to handle it | `references/errors.md` |
| Configure a webhook, subscribe to events, the masking of url and headers | `references/webhooks.md` |
| The full parameters and schema of an endpoint | `references/api.md` |
| A complete example you can copy | `references/scenarios.md` |

---

## Testing

The same eight steps `verify.sh` runs, one row each (see [CI and Gates](#ci-and-gates)). `bash verify.sh` at the
repo root runs all of them at once. Run them individually:

| Command | Directory | What it covers |
|---|---|---|
| `node tools/check-readme.mjs` | repo root | The mechanical documentation gate (links / commands / API paths / configuration defaults / step count / SDK version in the READMEs) |
| `dotnet test server/IdeaApp.Tests/IdeaApp.Tests.csproj` | repo root | The server integration/unit tests (including the skill distribution endpoints, the contract snapshot, the documentation drift guard) |
| `python3 -m unittest discover -s skills/idea-manager-client/tests` | repo root | The unit tests of the fetcher (manifest verification, degraded mode, pipes, redirects, …) |
| `bash skills/idea-manager-client/scripts/run-tests.sh` | repo root | The unit tests of the fetcher **+ one smoke test against a real HTTP server** |
| `bash server/scripts/aot-smoke.sh` | repo root | A real AOT artifact hitting the endpoints + a **cross-stack** check (real artifact ↔ real fetcher) |
| `npm --prefix web run typecheck` | repo root | The frontend type check (two tsconfigs: app + e2e) |
| `npm --prefix web test` | repo root | The frontend unit tests (Vitest, jsdom) |
| `bash web/e2e/run-e2e.sh` | repo root | The end-to-end cases (Playwright) |

### End-to-end (Playwright)

The end-to-end harness lives in `web/e2e/`, it is described in [`web/e2e/README.md`](web/e2e/README.md), and the
feature × test-case mapping is in [`web/e2e/COVERAGE.md`](web/e2e/COVERAGE.md).

```bash
# Full run (starts the server, stops it afterwards)
bash web/e2e/run-e2e.sh

# Full run + a clean database (use it when debugging "the data of the last round affected this round")
bash web/e2e/run-e2e.sh --fresh

# Force a rebuild (frontend + backend) and restart the server
bash web/e2e/run-e2e.sh --rebuild

# Keep the server running afterwards (for the next round or for manual debugging)
bash web/e2e/run-e2e.sh --keep

# A single file / a single case: the arguments are passed through to playwright test as they are
# (the test titles are in Chinese, so the --grep pattern is Chinese too)
bash web/e2e/run-e2e.sh e2e/smoke.spec.ts
bash web/e2e/run-e2e.sh --grep "回收站"

# Pick the port (by default it picks a free one itself)
E2E_PORT=5398 bash web/e2e/run-e2e.sh
```

**One round = one port + one database**: the port starts at 5300 (`E2E_PORT` can specify it; if it is not given, a
free one is picked), and the database is at `web/e2e/.tmp/run-<port>/e2e.db`, completely separate from the development
database and from the one the smoke script uses. Playwright's workers are fixed at 1 (they share one database), running
them in parallel would reset each other.

> ⚠️ **The frontend artifact `server/IdeaApp/wwwroot/` is shared, not one copy per round** — running two rounds
> concurrently means the rebuild of the second round replaces the pages of the first one.

---

## Known Limitations and Gotchas

### API semantics

- **No manual purge**: deletion has exactly one path — a soft delete into the trash, and a background job hard-deletes
  it once the retention period expires (every hour, and once at startup).
  **The trash retention period is the only switch that makes the contents of the trash disappear once the period is
  over**; neither the page nor the API has an entry point for it, and changing it means changing the configuration
  and restarting. A value ≤0 is treated as 1 day and logged as a warning. The cleanup worker is not registered in the
  Testing environment.
  **But there is another, immediately irreversible path**: `merge` hard-deletes the source ideas without putting
  them into the trash (see below) — there is no manual entry point, but merge deletes for you.
- **Restoring a project also restores the ideas "deleted together with it"** (decided by `deleted_at` being equal);
  the ones deleted individually earlier do not come back.
- **Every write on an idea refreshes `updated_at`** (the same goes for renaming, deleting and restoring a project) —
  editing an old idea moves it one slot down, so the entries below
  it shift up one slot as well (in the two-column layout the cards in the lower half swap sides). This is an
  **approved trade-off**, do not fix it as a bug.
  **The one exception is reordering the projects**: it only writes `sort_order` and **deliberately does not refresh**
  `updated_at` (`updated_at` is the fallback of the recent sorting, and refreshing it would make a manual reorder
  change the sorting result).
- **Merge and bulk delete are not the same thing**: `merge` hard-deletes the source ideas (they do not go to the
  trash), `bulk/delete` is a soft delete. Merge **does not skip** ids that do not exist (one missing id makes the whole
  call 404 and nothing is changed), it cannot span projects, and fewer than two ideas reports 400
  `invalid_merge_selection`.
- **Bulk delete is idempotent**: ids that are already in the trash count as "handled" and do not reset the retention
  period.
- **Bulk marking done refreshes `updated_at` on every entry** — the processed entries move to the bottom of the list.
- **`done` is required by the contract, the server does not validate it**: `POST /api/ideas/{id}/complete` and
  `POST /api/ideas/bulk/complete` without `done` means `done` = false = **not done**, and the server **does not
  report an error**. **The caller has to make sure the field is there.**
- **The built-in inbox `inbox` cannot be deleted** (400 `inbox_not_deletable`), but it can be renamed.
- **Only `project.sort_mode` is writable**, every other settings key reports 400 `setting_key_not_writable`.
- **`PATCH` sends only the fields you want to change**: `null` means "no change"; but an empty string / an empty array
  often means "really clear it" (giving a webhook `headers` of `{}` clears the request headers) — an empty string for
  an idea's `content` or a project's `name` is 400, and a project's `name` is also **required**.
- **The search semantics are broader than the old frontend substring match**: Chinese matches adjacent tokens
  (punctuation and whitespace do not block the way), English has to match **whole words** (searching `hel` does not
  find `hello`), and multiple words are combined with **AND**. Searching 「咖啡水温」 hits 「咖啡，水温」 but not
  「咖啡的水温」.
- **The `matches` highlight ranges can duplicate or overlap** (searching "哈哈" in "哈哈哈哈"), the offset unit is the
  UTF-16 code unit (aligned with the frontend's `String.slice`), and the caller has to merge them itself.
- **SSE always pushes everything**, and there is **no filtering parameter at all**. The event id is a monotonic UUIDv7,
  **not a consecutive sequence number** — a client cannot detect a silent gap in between.
- **`resync` is a control frame**, not a business event, and it cannot be subscribed to by a webhook; it carries no
  id, so a client cannot use it to continue a catch-up.
- **If the project an event belongs to is in the trash, the whole event is not delivered** (both global and
  project-scoped webhooks skip it) — the only exception is `project.deleted`, which is delivered as usual, otherwise
  deleting a project once would flood out N+1 notifications.
- **A manual retry resends the "already rendered payload"**, it does not re-render with the current template (the
  delivery record table does not store the full event data, so the input for a re-render does not exist).
- **A webhook's secret only goes in, never out** (the response only has a `hasSecret` boolean); the secret has three
  states: not sent = unchanged, `""` = cleared, non-empty = replaced.

### Data and consistency

- **A failed full-text index backfill = a failed startup**: when the index and the business data disagree, search
  gives **wrong results**, and that is far more dangerous than not starting at all. The price is that a failed index
  write also takes the business write down with it (same transaction).
- **A wrong column count in the FTS table of an old database is silent**: a number of `bm25` (the ranking algorithm
  that scores the relevance of a keyword) weights that does not
  match the columns **does not report an error**, it just quietly skews the relevance. The acceptance test has to
  assert the column count of `PRAGMA table_info(idea_fts)` directly.
- **A hard delete has to delete the index row as well**: the `idea` table has no explicit integer primary key, so
  rowids get reused; the trash cleanup on expiry is **raw SQL that bypasses the repository layer**, which is where it
  is easiest to miss. An orphan index row holds on to the rowid, and the new rows land in the wrong place.
- **Events are delivered after the transaction commits** (the channel cannot be rolled back, delivering inside a
  transaction produces ghost events). The channel holds 1000 items; when it is full it
  **drops the new events and logs them**, and it never blocks the write path.

### Frontend

- **The idea list is not virtualized**: `content-visibility` changes the card height and fights the viewport
  compensation used when paging back through history.
- **The filter conditions do not go into the URL**.
  **The relative timestamps are hand-written and do not depend on ICU**.
  **The drag-and-drop reordering only works with a mouse gesture**.
  **The composer at the bottom does not warn you when you leave with a draft** — these four are deviations that
  were explicitly accepted during the interface review
  (see `docs/superpowers/reviews/2026-11-20-web-design-guidelines.md`).
- **The card number is a "position", not an identity**: scroll up by one batch and the numbers all shift by +P.
- **Paging back through history has to add an even number of entries**: it requests 101 (the extra one is a probe), and
  on a full page it drops the **oldest** one, so the net gain is 100 — dropping the newest one instead would leave a
  hole in the middle of the already loaded set.
- **AND/OR is a per-device preference**, stored in localStorage (the browser's own key/value store), not shared
  across devices.
- **The filter condition changes and the SSE events use two separate debounce timers** (250ms each; debounce =
  only the last of a series of triggers counts) — sharing one
  would let them cancel each other out.
- **The incremental entries pushed over SSE are judged approximately by the frontend** as to whether to show them, and
  then a debounced 250ms refetch of the server results overwrites it — with English text individual entries can
  "appear and then be taken back".

### Deployment and operations

- **The application layer does no authentication and no Origin/CSRF checking** — the browser and the agents share the
  basic auth of nginx. The API must **always** sit behind access control.
- **The `IDEA_` prefix beats the command-line arguments** (the opposite of the usual ASP.NET Core convention), see
  [Configuration](#configuration).
- **`wwwroot/` is gitignored**: a fresh clone does not have it; publishing without building the frontend leaves a blank
  page and **reports nothing**.
- **The published artifact has to be copied completely**: missing `appsettings.json` or `libe_sqlite3.so` leads to the
  default configuration / SQLite failing to load.
- **AOT's JSON source-generated context**: every new DTO has to be registered in `IdeaJsonContext`, and
  **omitting the registration throws `NoMetadataForType` already during development**; but the reflection /
  trimming kind of degradation only a real artifact exposes, and both have to be run (`dotnet test` cannot
  detect AOT degradation).
- **Changing the .NET SDK version means re-verifying the ILCompiler part**: the csproj explicitly bumps ILCompiler to
  10.0.14 to avoid the advisory hit by the 10.0.13 built into SDK 10.0.113; `TreatWarningsAsErrors` turns NU1903 into
  an error → restore fails outright.
- **Changing the request body limit of nginx means changing the manual as well**: the 413 has no `code`, and the
  manual's `references/errors.md` says the same thing.
- **A missing entry `SKILL.md` in the copy pointed at by `Skill:Path`** shares **the same 404 code** as "the path
  is not configured" and "the directory does not exist" (`skill_not_distributed`), but the debugging direction
  differs: a misspelled skill name → `skill_endpoint_not_found`, a misspelled file name → `skill_file_not_found`.

### Deliberately out of scope

No multi-user collaboration / accounts / sharing, no LLM, no kanban board / rich text / attachments, no native mobile
app, no multi-instance or horizontal scaling.

> **The "kanban" in the project name is a historical leftover** — this project is a message stream, not a board.
> Do not write the documentation as if it were a board, and do not file requirements as if it were one.

---

## Design Trade-offs

The full derivation of the design and the trade-offs is in the four specs under `docs/superpowers/specs/`:

| Spec | What it settles |
|---|---|
| [`2026-11-11-idea-manager-design.md`](docs/superpowers/specs/2026-11-11-idea-manager-design.md) | The main design: data model, endpoints, deployment shape, non-goals and anti-goals |
| [`2026-11-13-skill-distribution-design.md`](docs/superpowers/specs/2026-11-13-skill-distribution-design.md) | Distributing the skill with the service: why the command layer was removed and why the manual lives on the server |
| [`2026-11-15-backend-full-text-search-design.md`](docs/superpowers/specs/2026-11-15-backend-full-text-search-design.md) | Full-text search: **overturns one non-goal of the main spec** ("no server-side full-text search") — the tokenizer, the index, the transaction boundary |
| [`2026-11-17-idea-manager-ui-layout-redesign.md`](docs/superpowers/specs/2026-11-17-idea-manager-ui-layout-redesign.md) | The frontend layout rework; **the concept of a "title" was removed as a whole** |

The conclusions of the interface review (12 issues fixed, 12 deviations explicitly accepted) are in
[`docs/superpowers/reviews/2026-11-20-web-design-guidelines.md`](docs/superpowers/reviews/2026-11-20-web-design-guidelines.md).

A few trade-offs that run through the whole stack:

- **The AOT constraint runs through the whole stack**: no ORM, no reflection-based DI, no template engine — hand-written
  SQL, a hand-written Mustache-style renderer, JSON through a source-generated context.
- **The application layer does no authentication**: all access control lives in the nginx layer, and the application
  only listens on the loopback address.
- **The authoritative content of the skill lives on the server**: reimplementing the contract in the command layer was
  the biggest surface for drift — so the manual is distributed by the server and the agent side only installs a thin
  shell.
