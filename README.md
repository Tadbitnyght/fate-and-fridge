# Fate & Fridge

**Fate & Fridge** is a self-hosted household dinner planner. It answers the
household's three dinner questions — what's for dinner, what's coming up, and
what do we need from the store — and keeps the answers in one place on your own
network.

Codename: *Operation Dinner Plate*.

One installation serves one household. Dinner planning, recipes, and groceries
are shared by everyone in the household; the data stays in a PostgreSQL database
next to the application, and the core features keep working without internet
access.

> Fate & Fridge should feel like a simple household meal planner hiding a
> sophisticated database — not a sophisticated database pretending to be a meal
> planner.

## Features

### Dinner calendar

- A month calendar on desktop and a Month/Agenda view on mobile, with a day
  detail editor for notes, diner count, and the meal itself.
- Eight entry types: recipe, eat out, fend for yourself, host family dinner, go
  to family dinner, holiday, misc, and vacation.
- Lock any date to protect it from automatic changes; the randomizer never
  touches locked dates.
- Past dates are read-only in normal use. Changing one requires the Admin
  historical-correction workflow (reason, typed confirmation, audit entry).
- Multi-device live updates: a change on one device refreshes the others.

### "What's for dinner?" randomizer

- Reroll a single date, or reroll a range (next 7 days, rest of month, or fill
  empty days) with a per-day preview, "try another", and undo.
- Only recipes marked `Normal` are chosen. Exact repeats are heavily suppressed
  inside a 7-day-back / 14-day-forward window, sharing a primary protein with a
  neighbouring day is penalised, greater spacing from other appearances is
  preferred, and favourites act only as a weak tie-breaker.
- Selection uses cryptographically secure randomness, and every reroll stores
  its score breakdown so a result can be explained.
- Undo stays available until the date is next modified.

### Recipes

- Photo-card grid and compact list with server-side search, filters (status,
  favourite, vegan, vegetarian, protein, tag), sorting, and paging.
- Full recipe editor: status (normal, manual-only, retired), favourites,
  photos with alt text, ordered ingredient groups with precise quantities and
  optional units, preparation notes, instruction sections and steps, proteins,
  and tags.
- Import from a URL: schema.org Recipe data first, best-effort HTML parsing as a
  fallback, and an editable review draft before anything is saved. Unknown
  ingredients and units are resolved during review, similar names warn about
  duplicates, and the fetcher is hardened against unsafe network targets.
- Recipes are referenced by, never silently changed by, the calendar: past meals
  keep their stored text when a recipe is renamed, retired, or deleted.

### Groceries

- A live list built from the planned meals in the selected range, plus manual
  items and reusable staples.
- Range presets (next 3, 7, or 14 days, rest of month, or a custom range) with
  the last-used range remembered per user.
- Quantities combine only when the units are compatible; incompatible
  measurements stay on separate lines, and package sizes are never guessed.
- Grouped by store department in your configured order, with per-meal
  attribution, shared check state, and a print view.

### Statistics and history

- Most-eaten and least-recently-eaten recipes, meal counts for the current month
  and year, a primary-protein distribution, and counts for every special-event
  type.
- A read-only meal-history browser with type, recipe, and protein filters.
- Last eaten / next planned / times eaten on every recipe card.

### Printing

- A calendar print view (landscape, one month per page, any number of months)
  and a grocery list print view (portrait, department order, paper checkboxes).

### Backups, restore, and updates

- Manual encrypted backups (`.ffbackup`): database dump, recipe images, and
  application keys sealed with AES-256-GCM under a key derived from your setup
  recovery key. The recovery key is never stored inside the archive.
- Restore validates the archive before touching anything, takes a mandatory
  safety backup, runs in a maintenance window, and revokes all sessions. A clean
  install can be restored from an archive plus the recovery key.
- The About panel shows the application and schema versions, and a
  privacy-preserving update check tells you when a newer release exists. There
  is no telemetry and no self-update.

### Household and permissions

- Two setup modes: **single user** (no login, for a trusted private network) or
  **multiuser** (Admin plus Members, with logins).
- Seven server-enforced permission keys — calendar view/edit, groceries
  view/edit, recipes view/edit, and reference-data edit. Users, permissions,
  security settings, the audit log, historical correction, and backups are
  Admin-only, and the last Admin cannot be removed.
- Password hashing with Argon2id, database-backed sessions, CSRF protection,
  login lockout and rate limiting, an optional IP allowlist, and trusted-proxy
  aware forwarded headers.
- An audit log with filters, CSV/JSON export, and a configurable retention
  window.

## Quick start

Requirements: Docker Engine with the Compose plugin (Docker Compose v2), and a
host directory on persistent storage for the household data.

```bash
git clone <repository-url> fate-and-fridge
cd fate-and-fridge

cp .env.example .env      # then edit the values (see Configuration)
docker compose up -d --build
```

Before the first start, set at least:

- `POSTGRES_PASSWORD` — generate one with `openssl rand -hex 24`. Keep it
  URL-safe (letters, digits, `-`, `_`) so it embeds cleanly in the connection
  string Compose builds.
- `HOST_DATA_DIR` — an absolute path on persistent storage (the default is
  `./data` next to `compose.yaml`).
- `TZ` — the household's IANA timezone, used for calendar-day semantics.

Then open `http://<host>:50080/` (or your `HOST_PORT`). The first page is the
setup wizard: choose single-user or multiuser, enter the household details,
optionally restrict access by IP, and — for multiuser — create the first Admin.
Setup ends with a **recovery key** that is shown once. Store it somewhere safe:
it is the only way to open encrypted backups and to restore an installation, and
it is deliberately not kept in the database.

Useful commands:

```bash
docker compose ps                       # container status
docker compose logs -f app              # application logs
curl -fsS http://localhost:50080/api/v1/health     # health (and /api/v1/health/db)
docker compose down                     # stop; data stays in HOST_DATA_DIR
```

Database migrations run automatically when the container starts, retrying while
PostgreSQL warms up.

## Configuration

All configuration lives in `.env` next to `compose.yaml`. The file is ignored by
git and must never be committed.

| Key | Default | Purpose |
| --- | --- | --- |
| `TZ` | `UTC` | IANA timezone used for household calendar days. Set it to your household's timezone. |
| `APP_ENV` | `production` | ASP.NET Core environment. Keep `production` for a real deployment. |
| `HOST_PORT` | `50080` | Host port the application is published on. The container always listens on 8080. |
| `HOST_DATA_DIR` | `./data` | Host directory for persistent state: `postgres/`, `config/`, `images/`, `backups/`. Use an absolute path. |
| `POSTGRES_DB` | `fatefridge` | Database name. |
| `POSTGRES_USER` | `fatefridge` | Database user. |
| `POSTGRES_PASSWORD` | — (required) | Database password. Compose refuses to start without it. |
| `LOG_LEVEL` | `info` | Reserved; log levels are currently configured in `appsettings.json`. |

PostgreSQL is not published to the host; the application reaches it over a
private Docker network.

## Data, backups, and updating

Everything persistent lives under `HOST_DATA_DIR` and is bind-mounted into the
application container, so rebuilding or replacing the container never loses data:

```text
HOST_DATA_DIR/
├── postgres/   PostgreSQL data directory
├── config/     application configuration and secrets (recovery key, data-protection keys)
├── images/     recipe images
└── backups/    encrypted backup archives
```

Two layers of protection are worth having:

1. **Host-level snapshots or filesystem backups of `HOST_DATA_DIR`.** On
   TrueNAS SCALE a periodic snapshot task on the dataset is the easy option.
2. **Application backups (Settings → Backups, Admin only).** These are portable,
   encrypted `.ffbackup` archives you can download and keep off the host. They
   are the only backups you can restore from the setup wizard, and the recovery
   key is required to open them. The five most recent safety backups are kept.

Never open or edit PostgreSQL files directly; use
`docker compose exec postgres psql ...` if you need to inspect the database.

**Updating.** Pull the new version of the code and rebuild:

```bash
git pull
docker compose up -d --build
```

The rebuild restarts the stack and applies any pending migrations; stored data
is untouched. Take an application backup before a major upgrade.

## Security and network posture

- Fate & Fridge is designed for a trusted home network. Do not expose it
  directly to the internet: if you need remote access, put it behind a reverse
  proxy that terminates TLS and enforces authentication, and enable the
  application's IP allowlist.
- Only the application port is published. PostgreSQL stays on the private Docker
  network.
- `.env` holds secrets (the database password). Keep it out of version control
  and out of any public backup of the source tree.
- Multiuser mode uses Argon2id-hashed passwords, opaque server-side sessions,
  CSRF tokens, login throttling, and Admin revocation of users and sessions.
  Single-user mode has no login and relies on network trust — use it only on a
  private network.
- The optional IP allowlist and trusted-proxy support are configured in
  Settings → Security.
- Known limitation: the application container currently runs as root, because
  bind-mounted host directories may be owned by other users. See
  [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the deployment notes.

## Project layout

```text
backend/        ASP.NET Core API, EF Core migrations, xUnit tests
frontend/       React + TypeScript + Vite application (TanStack Query, SignalR client)
docs/           product, architecture, API, database, randomizer, security,
                testing, and deployment documentation
scripts/        operational helper scripts
compose.yaml    Docker Compose stack (application + PostgreSQL)
Dockerfile      single application image (API plus the built frontend)
```

The stack is .NET 10 / ASP.NET Core with Entity Framework Core (Npgsql),
PostgreSQL 17, React 19 + TypeScript + Vite, TanStack Query, and SignalR for live
updates, all served from one container.

Working on the code:

```bash
# backend (.NET 10 SDK)
cd backend
dotnet restore
dotnet build
dotnet test
dotnet run --project src/FateAndFridge.Api     # http://localhost:8000

# frontend (Node 24+)
cd frontend
npm install
npm run dev        # Vite dev server on :5173, proxies /api to the backend
npm run test       # Vitest unit tests
npm run test:e2e   # hermetic Playwright tests
```

The backend test suite includes integration tests that start disposable
PostgreSQL containers, so a running Docker daemon is required. See
[docs/TESTING.md](docs/TESTING.md) for the full test strategy.

## Documentation

- [Product specification](docs/PRODUCT_SPEC.md) — behaviour and requirements
- [Architecture](docs/ARCHITECTURE.md) — services, data flow, and boundaries
- [API](docs/API.md) — HTTP endpoints, permissions, and error codes
- [Database](docs/DATABASE.md) — schema, invariants, and migrations
- [Randomizer](docs/RANDOMIZER.md) — selection rules and scoring
- [Security](docs/SECURITY.md) — authentication, authorization, and hardening
- [Testing](docs/TESTING.md) — test layers and quality gates
- [Deployment](docs/DEPLOYMENT.md) — deployment, data layout, and operations
- [Changelog](CHANGELOG.md) — release history

## License

Fate & Fridge is released under the [MIT License](LICENSE).
