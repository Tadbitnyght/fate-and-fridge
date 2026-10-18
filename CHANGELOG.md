# Changelog

All notable changes to Fate & Fridge are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Milestone 0 — project skeleton:
  - .NET 10 / ASP.NET Core backend (C# 14, minimal APIs) with `/api/v1/health`
    and `/api/v1/health/db`, an OpenAPI document, and static SPA hosting
  - EF Core (Npgsql, snake_case conventions) with the `InitialCreate` migration;
    migrations apply at startup (`Database__MigrateOnStartup`) or on demand via
    `dotnet FateAndFridge.Api.dll --migrate`
  - React 19 + TypeScript + Vite frontend with a TanStack Query status screen
  - PostgreSQL 17 service, private to the Docker network, persistent volume
  - Single application image (Vite build served by the API) and a Docker
    Compose stack with `.env.example`
  - Test runners: xUnit v3 + FluentAssertions + Testcontainers (backend),
    Vitest + Testing Library (frontend), Playwright (end to end)
  - Enforced linting/formatting/type checks: `dotnet format` and analyzers for
    the backend; ESLint, Prettier, and `tsc` for the frontend
  - CI: backend format/build/EF-model-drift/tests, frontend lint/format/types/
    tests/build, compose validation, and a stack acceptance job that runs
    Docker Compose plus Playwright
- TrueNAS deployment tooling: `scripts/deploy.sh`, dedicated `fatefridge` SSH
  access for the `CPlanner` account, `docs/DEPLOYMENT.md`, and a periodic ZFS
  snapshot task for the `Data/calendarplanner` dataset
- OpenCode MCP configuration: GitHub (PAT), Playwright, Roslyn, and Docker
  pointed at the NAS Docker daemon over SSH

- Milestone 1 — setup and authentication:
  - Database: household settings, application settings, users, per-user
    permissions, sessions, and auth-event tables (EF Core migration
    `M1SchemaBaseline`)
  - Setup: single-shot, race-safe `POST /setup/complete` (household fields,
    first Admin, optional IP allowlist, update-check preference) with a
    one-time recovery key stored at `/data/config/recovery.key` (mode 0600)
  - Auth: Argon2id password hashing (libsodium via Geralt), opaque DB-backed
    sessions (`faf_session`, 30-day idle / 90-day absolute, rotation and
    revocation), CSRF via `X-CSRF-TOKEN`, login lockout (5 failures →
    progressive delay, 10 → 15 min, 5 lockouts/24 h → 24 h), per-IP failure
    limiting, forced password change, and Admin password reset
  - Authorization: seven server-enforced permission keys (edit implies view),
    Admin-only user management with last-admin protection, and single → multiuser
    conversion
  - Security settings: app-wide IP allowlist (health/loopback exempt),
    trusted-proxy-aware forwarded headers, and an Admin security screen
  - Frontend: routing/session shell, setup wizard with recovery-key
    confirmation, admin console (household/users/security), startup-page
    routing, and an accessibility sweep (skip link, focus management, modal
    focus traps)
  - Hardening: request body limits and `application/json` policy, strict
    canonical PHC parsing, unified error envelopes (404/415/500 codes), and an
    expanded verification suite (643 backend tests; 94 Vitest, 11 hermetic
    Playwright, plus a real-stack e2e suite)
- Frontend: `/progress` milestone roadmap page (M0–M14) rendering a checked-in
  manifest with status chips, completion dates, feature lists, test evidence,
  and GitHub issue links; linked from the Dashboard card and the app footer
- Milestone 2 — reference data (2026-10-01): reference-data APIs and startup
  seeder, configurable conversion factors and aliases, ingredient merge with
  audit safety, and the admin management UI
- Milestone 3 — recipes (2026-10-03): the complete recipe REST API and
  management UI — status lifecycle (normal/manual-only/retired), favorites and
  vegan/vegetarian metadata, secure local images with alt text, ordered
  ingredient groups and ingredients (precise nullable decimals, optional
  units, preparation notes, optional flags), ordered instruction sections and
  steps, and protein/tag associations — plus the recipe browser (photo-card
  grid and compact list, server-side search/filters/sorts and `limit`/`offset`
  paging, explicit no-history statistics states). Real reference-data usage
  checks replace the M2 seam: recipe references block ingredient/unit/protein/
  tag hard deletes and ingredient merges reassign recipe ingredients.
- Milestone 4 — URL import (2026-10-05): import a recipe from a pasted URL,
  stateless and draft-only. An SSRF-hardened fetcher (HTTP/HTTPS only, DNS
  revalidated per hop, manual redirects re-validated, 5 s connect / 15 s read
  timeouts, 2 MiB HTML / 5 MiB image ceilings, no credentials or cookies,
  loopback/private/link-local/ULA/CGNAT/multicast/reserved/metadata ranges
  blocked) feeds a schema.org Recipe JSON-LD parser with a static AngleSharp
  HTML fallback (never executes third-party JavaScript). Deterministic
  quantity/fraction/unit normalization and canonical/alias ingredient matching
  produce an editable, multi-candidate review draft; unknown ingredients and
  units are resolved in review (permission-gated inline creation), photos are
  downloaded after review through the same SSRF and M3 image policy, and
  duplicate detection warns on an exact normalized source URL and on similar
  names (with a rename recheck). Import routes require `recipes.edit`
  (`/import/check` requires `recipes.view`), are per-user throttled
  (30 page fetches/hour, 60 image fetches/hour), and never write until the
  reviewed draft is saved through `POST /recipes`.
- Milestone 5 — calendar core (2026-10-05): the `calendar_entries` /
  `calendar_entry_history` model and the month/agenda calendar. A desktop
  ARIA-grid month view and a mobile Month/Agenda toggle, a date-detail editor,
  the eight meal/special-event types with a curated inline-SVG icon set, diner
  metadata (nullable, household default), lock/unlock (`locks restrict automatic
  changes only`), manual recipe selection with a server-computed non-blocking
  repeat warning, and server-enforced historical immutability in the household
  timezone. A persisted historical boundary freezes history across a timezone
  change, recipe renames refresh future/today snapshots while historical
  snapshots stay frozen, and a landscape print skeleton is included.
- Milestone 6 — randomization engine (2026-10-08): the “what’s for dinner?”
  engine. Ten validated `household_settings.randomizer_*` fields (7-back /
  14-forward exact-repeat window, 0.10 repeat and 0.02 special-day
  multipliers, 0.70 direct-neighbour protein penalty over a configurable
  neighbour distance, 1.10 favorite tie-breaker, and spacing near/far/search
  days) are exposed through `GET/PATCH /settings/household` and a Household
  settings “Randomization” fieldset. A secure weighted-selection engine
  (`RandomizerEngine`/`CalendarRandomizer`) draws with
  `System.Security.Cryptography.RandomNumberGenerator` behind an injectable
  `IRandomSource`, counting exact-recipe occurrences across historical, today,
  and future household-local dates, applying a near-zero special-day
  contribution, a once-only shared-primary-protein penalty against
  recipe-bearing neighbours, a nearest-occurrence spacing step, and the
  favorite multiplier to the final
  `base × repeat × protein × spacing × favorite` weight.
  `POST /calendar/{date}/reroll` (`calendar.edit`) replaces any non-locked,
  non-historical, non-vacation entry or fills a blank date and stages one
  `calendar_entry_history` row with a fresh `operation_id` and a server-side
  `metadata_json` score breakdown, returning the compact
  `operation: { id, undo_expires_at }`; `POST /operations/{operation_id}/undo`
  restores the exact pre-reroll entry (or blank) until the date is next
  modified (`UNDO_CONFLICT`/`UNDO_EXPIRED`). The frontend adds the Reroll
  action and a dismissible Undo banner with disabled-state messaging. Schema:
  `household_settings.randomizer_*` and
  `calendar_entry_history.metadata_json` (migrations `M6Randomizer`,
  `M6OperationMetadata`).
- Milestone 7 — batch planning (2026-10-09): reroll a whole range at once.
  `POST /api/v1/calendar/reroll/preview` (`calendar.edit`) builds a persisted
  15-minute draft over 7-day, rest-of-month, or Fill Empty ranges and returns a
  per-date diff (changed dates highlighted; locked, vacation, special, eat-out,
  fend-for-yourself, and non-recipe dates shown unchanged). Generation is
  sequential in ascending date order and draws only `Normal` recipes, with
  earlier picks in the proposal counting as occurrences/neighbours for later
  dates. `POST /api/v1/calendar/reroll/apply` re-checks every target date and
  writes all changes atomically, failing `BATCH_CONFLICT` (409) on a changed
  target and `BATCH_DRAFT_EXPIRED` (410) on a missing/expired/consumed draft;
  each changed date writes one `calendar_entry_history` row sharing a new
  nullable, indexed `batch_operation_id` while keeping its own `operation_id`,
  with a `metadata_json` score breakdown carrying `kind = batch_reroll` or
  `fill_empty` plus `mode`. Apply serializes per target date with the M6
  `pg_advisory_xact_lock`. `POST /api/v1/batch-operations/{id}/undo`
  (`calendar.edit`) reverts every changed date while each is still in the
  applied state (`UNDO_CONFLICT` 409 otherwise; `UNDO_EXPIRED` 410 for a
  missing/already-undone batch), writing one history row per restored date
  sharing a fresh `batch_operation_id` and `metadata_json` with
  `kind = undo` and `undone_batch_operation_id`. The frontend adds a "Reroll…"
  toolbar menu (7-day / Rest of month / Fill empty) with a modal preview wizard
  (diff, Try Another, Apply, Cancel) and reuses the dismissible Undo banner for
  the batch. Schema: `calendar_reroll_drafts` and
  `calendar_entry_history.batch_operation_id` (migration `M7BatchPlanning`).
- Milestone 8 — groceries (2026-10-12): the live grocery projection. `GET
  /api/v1/groceries` (`groceries.view`) resolves a preset (`next_3_days`,
  `next_7_days`, `next_14_days`, `rest_of_month`, or `custom`) or an explicit
  `start` + `days` range against the household-local today (past start and
  invalid `days` → `VALIDATION_FAILED`; a custom range longer than 366 days →
  `GROCERY_RANGE_TOO_LARGE`), remembers the last-used range per user, and
  defaults to Next 7 Days. Only `include_groceries` entries contribute (recipe
  entries plus specials carrying a recipe; eat-out, fend-for-yourself,
  go-to-family-dinner, and vacation contribute nothing). Recipe-derived lines
  aggregate by canonical ingredient (aliases resolve through the M2 merge
  redirect) plus unit dimension: compatible units merge through the existing
  conversion factors into the largest present unit (1 lb + 8 oz → 1.5 lb),
  incompatible dimensions stay separate, unitless/same-unit counts combine by
  count while different count units stay separate, and preparation notes never
  enter identity. Quantities render as the nearest exact common fraction (½, ⅓,
  ¾, …) or up to two decimals, and each line carries one attribution row per
  contributing meal (recipe name + short date). Items are grouped by department
  in configured order (no department → Other last), unchecked first /
  alphabetical then checked. Manual items (`POST/PATCH/DELETE /groceries/manual`,
  `groceries.edit`) are range-scoped free text with an optional quantity/unit and
  canonical ingredient, with the department defaulting from the linked
  ingredient, and an optional Save for future that idempotently promotes a
  staple. Staples are a
  household-wide catalog (`GET/POST/PATCH /groceries/staples`) whose add-to-list
  (`POST /groceries/staples/{id}/add-to-list`) creates a range-scoped manual item
  without mutating the staple. Household-shared check state
  (`POST /groceries/check` and `/groceries/uncheck`, `groceries.edit`) is keyed by
  the stable `GroceryItemIdentity` hash (canonical ingredient + dimension, or the
  manual item id) so it survives quantity changes and range expansion, and a
  checked item moves below unchecked items within its department. Per-user
  preferences (`GET/PATCH /groceries/preferences`, `groceries.view`) store Hide
  Checked and the remembered range (the single-user system actor stores them
  under the reserved `system_grocery_preferences` application setting). The
  mobile-first UI adds a department-grouped list with sticky headers and large
  tap targets, manual-item add/edit, and a staples panel, plus a print-optimized
  view (`/groceries/print`) with department order, a checkbox per item,
  attribution, manual items, and an include-checked-items option (checked
  excluded by default). Reads require `groceries.view` and writes
  `groceries.edit` (edit implies view; new members default to `groceries.view`,
  and the single-user system actor has full access). Schema: `grocery_staples`,
  `manual_grocery_items`, `grocery_check_state`, and `user_grocery_preferences`
  (migration `M8Groceries`).
- Milestone 9 — live sync (2026-10-13): multi-device updates over an
  authenticated SignalR hub at `/hub/live` (mapped before the SPA catch-all; the
  earlier `/ws` suggestion is superseded). The hub is server-to-client
  notifications only — no client-invokable methods, so all writes stay on the
  REST API — and authenticates with the existing opaque `faf_session` cookie in
  multiuser mode (no anonymous socket, no token in the URL) or, in single-user
  mode, as the system actor. Every notification arrives through one client
  method (`event`) carrying a coarse, snake_case envelope (`type`, `entity`,
  `id`, `operation_id`, `actor_user_id`, `timestamp`) with no entity bodies:
  `calendar.updated`, `calendar.lock_changed` (lock toggles only, no double
  invalidation), `groceries.updated` (per-user preferences never broadcast),
  `recipe.updated`, `settings.updated` (household/security settings and
  reference-data writes), and `session.revoked`. Events are emitted only after a
  committed write. Guards: a same-origin `Origin` check, the existing IP
  allowlist on the upgrade, a 32 KiB inbound frame cap, `EnableDetailedErrors =
  false`, and bounded per-session (8) / per-household (32) connections; the hub
  path is CSRF exempt. A valid session is required at connect and re-validated
  per broadcast; logout, admin revocation, user disable, and session expiry emit
  `session.revoked` and abort those sockets (a re-validation read failure fails
  closed). The frontend adds one connection per tab with automatic reconnect and
  backoff, a full query-invalidation pass on every (re)connect (no event log or
  replay), the documented event → query-key mapping, an accessible non-blocking
  `Live`/`Reconnecting…` indicator, and socket teardown on logout. Schema: none —
  live sync is stateless (no table or migration).
- Milestone 10 — printing (2026-10-14): a dedicated, deep-linkable calendar print
  view at `/calendar/print` (the calendar toolbar's Print button navigates to it)
  with screen-only controls (start month + a month count of 1–12, defaulting to
  the month being viewed, carried as `?month=YYYY-MM`), one printable month per
  page in chronological order including year-boundary jobs, and a month/year
  heading on each page. Long names wrap fully (never ellipsis-clipped); a busy
  month auto-shrinks deterministically to a ≈10pt legibility floor; and a month
  that still overflows at the floor continues onto a following page rather than
  shrinking further or clipping. Page size is owned per print view via named
  `@page` rules — `@page calendar-print { size: A4 landscape; margin: 10mm }` and
  `@page grocery-list { size: A4 portrait; margin: 12mm }` — replacing the
  app-wide M5 `@page { size: landscape }` and the global M8
  `@page { margin: 12mm }`, so printing any other page keeps the browser defaults.
  Output is black on white with no background fills and no colour-only meaning,
  legible in greyscale. Printed calendar content is the day number and the
  recipe/special-event text, the curated special-event icons in black, and
  eat-out / fend-for-yourself as text — with no meal photos, lock icons, notes,
  diner count, or restaurant/location. Any month (including past months) is
  printable. M10 also verifies the M8 grocery print view (still portrait,
  black-on-white, department order, paper checkbox, per-meal attribution, the
  manual badge, and checked-excluded-by-default) after the `@page` refactor, and
  adds a Playwright print battery (`emulateMedia({ media: 'print' })` computed
  styles plus `page.pdf({ preferCSSPageSize: true })` page-size/page-count
  assertions). **No new API endpoint and no schema change** — printing is
  client-rendered from the existing reads.

- Milestone 11 — statistics and history (2026-10-18): a household `/statistics`
  page computed on demand from `calendar_entries` — **most eaten** and **least
  recently eaten** recipes (top 10 each, with status badges and snapshot
  tombstones for deleted recipes), meal counts for the household calendar
  **month** and **year**, an offline CSS **primary-protein distribution** with an
  explicit "no primary protein" figure, and counts for all **seven special-event
  types** — plus per-recipe **Last Eaten / Next Planned / Times Eaten** on the
  recipe browser cards, the compact list, and the recipe detail/editor, and a
  **read-only, month-grouped meal-history browser** with server-side
  entry-type/recipe/protein filters and month paging. Three new reads —
  `GET /api/v1/statistics/overview`, `GET /api/v1/statistics/recipes/{id}`, and
  `GET /api/v1/statistics/history` (`recipes.view`; `calendar.view` for history)
  — are aggregate queries over `calendar_entries`: **no cache, no statistics
  table, no background job, no new infrastructure**. "Eaten" is strictly before
  the household today (household timezone), retired recipes keep their history,
  and a hard-deleted recipe's meals survive as a stored-snapshot tombstone rather
  than being lost. The recipes list gained the three metrics (one aggregate query
  for the page) and its `recently_eaten` / `least_recently_eaten` / `most_eaten`
  sorts are now live; the statistics and history reads are invalidated over the
  existing live-sync events. **No schema change, no migration, and no new
  permission key.**

### Changed

- Default published port is now **50080** (`HOST_PORT` in `.env`, default in
  `compose.yaml`, CI, Playwright, and docs). The container-internal port
  remains 8080.
- Backend stack decision: Python/FastAPI replaced by .NET 10 / ASP.NET Core
  (the Python scaffold remains in git history)

### Notes

- Milestone 0 acceptance verified: `docker compose up -d --build` produces a
  reachable, healthy application shell on the household TrueNAS server and in
  CI, with the status page and health API covered by Playwright.
