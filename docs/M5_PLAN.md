# Fate & Fridge — Milestone 5 Plan (Calendar Core)

Status: **approved, ready to execute**
Approved: 2026-10-05 (product owner answered the open M5 decision set)
Purpose: authoritative M5 runbook. A fresh session should execute M5 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
database, architecture, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. M5 delivers the calendar core: the calendar entry model and migration, a
   month calendar (desktop) and agenda (mobile), a date-detail editor, the eight
   meal/special-event types, a curated event-icon set, diner metadata,
   lock/unlock, manual recipe selection, server-enforced historical
   immutability, arbitrary future navigation, and a print skeleton. Randomizing,
   batch planning, groceries, live sync, statistics, and full printing are later
   milestones.
2. Execution mirrors M4: `docs/M5_PLAN.md` is the runbook; one issue at a time,
   each on a branch from current `main`, implementer work, gates, an independent
   reviewer, fixes on the same branch, fast-forward merge, issue closure, and
   push. The final issue sets the manifest M5 status and cuts tag `m5-complete`.
3. **Schema.** `calendar_entries` follows `docs/DATABASE.md §8` exactly (one row
   per `meal_date`, unique; nullable type-specific fields; snapshot fields), with
   one addition: a nullable `location_text` for Go To Family Dinner
   host/location. `calendar_entry_history` is created in M5 (used by M6 undo and
   M12 corrections). `household_settings` gains a nullable
   `historical_boundary_date` (see §1.7). One EF migration.
4. **Entry types** are stored as the snake_case strings from
   `docs/DATABASE.md §8`: `recipe`, `eat_out`, `fend_for_yourself`,
   `host_family_dinner`, `go_to_family_dinner`, `holiday`, `misc`, `vacation`.
   Per-type field rules and `include_groceries` derivation are in §2.2.
5. **Snapshots.** Every write stores `snapshot_recipe_name` and
   `snapshot_display_text`. Non-historical (today/future) entries refresh their
   snapshot when the referenced recipe is renamed; historical entries are frozen
   and keep the name captured while they were still editable. When a recipe is
   retired or deleted the display persists from the snapshot; `recipe_id` is
   null-safe (`ON DELETE SET NULL` in M12 is anticipated).
6. **Groceries.** `include_groceries` is derived on write from the entry type and
   any attached recipe (§2.2); it is not user-editable in M5. Grocery
   aggregation itself is M8.
7. **Dates, timezone, and the frozen boundary.** Calendar dates are household
   local dates (`YYYY-MM-DD`). "Today" is computed from the household timezone
   with the injected `TimeProvider`. Past = strictly before today; today and the
   future are editable. When the household timezone is changed,
   `historical_boundary_date` is advanced to the last date that was historical
   under the old timezone, so a date that was already historical can never become
   editable again. Effective rule:
   `is_historical(d) = d < today || d <= historical_boundary_date`. Freezing wins
   over the "today is editable" rule for the rare date that a westward timezone
   change turns into the current local day; the boundary is applied dynamically
   on every read/write and needs no data migration beyond the column.
8. **Historical immutability is server-enforced.** Normal calendar routes reject
   every write to a past date for every actor, including Admin
   (`CALENDAR_DATE_IN_PAST`). Admin may only change history through the M12
   historical-correction API, which lands later. Reads of past dates are
   allowed.
9. **Permissions.** `GET` routes require `calendar.view`; `PUT`/`DELETE`/lock
   routes require `calendar.edit`. Single-user mode acts with full permissions
   without a session. Authorization is server-side only.
10. **Lock rules.** A blank date cannot be locked (`CALENDAR_BLANK_CANNOT_LOCK`).
    A past date cannot be locked or unlocked (`CALENDAR_DATE_IN_PAST`). A
    manually edited locked entry is allowed and keeps its lock — locks restrict
    automatic/random mutation only. Deleting a locked future/today entry is
    allowed and removes the entry (and its lock). `CALENDAR_ENTRY_LOCKED` is
    reserved for M6/M7 automatic mutation and is not returned by M5 routes.
11. **Writes.** `PUT /calendar/{date}` is a full-replace upsert: an omitted body
    field is null/default. `DELETE` hard-deletes a today/future entry after
    writing a history row. Concurrency is last-write-wins in M5; M7 adds batch
    protection. Calendar changes write `calendar_entry_history`; `audit_events`
    for calendar changes stay in M12 per `docs/DATABASE.md §10`.
12. **API shapes.** `GET /calendar/{date}` returns `200` with `entry: null` for a
    blank date. Range reads are capped at 366 days; all date arguments are within
    ±5 years of today (`VALIDATION_FAILED` outside those bounds). New stable
    error codes: `CALENDAR_DATE_IN_PAST`, `CALENDAR_BLANK_CANNOT_LOCK`,
    `CALENDAR_ENTRY_LOCKED` (reserved), `CALENDAR_RECIPE_REQUIRED`,
    `CALENDAR_MISC_LABEL_REQUIRED`, plus existing `VALIDATION_FAILED`/`FORBIDDEN`.
13. **Manual recipe selection.** The recipe picker reuses the M3 recipe search
    with a result card (image, name, last eaten, times eaten). An exact-recipe
    repeat inside the 7-back/14-forward window is a server-computed,
    non-blocking `repeat_warning`; the user may override. Same-protein proximity
    is never warned in M5.
14. **Icons.** A curated, checked-in inline-SVG set with fixed keys; only the
    special types (`holiday`, `misc`, `host_family_dinner`,
    `go_to_family_dinner`) may carry one. Backend validates `icon_key` against the
    catalog. Keys: `turkey`, `cake`, `heart`, `star`, `pumpkin`, `gift`,
    `snowflake`, `flag`, `fireworks`, `clover`, `leaf`, `egg`.
15. **Diner metadata.** `diner_count` is nullable (1–99); null means "use
    `household_settings.default_diner_count`". Reads expose both the stored value
    and the effective count. V1 never scales recipe quantities by diner count.
16. **Vacation.** Vacation entries store the type and expose
    `blocks_automation = true` so M6/M7 can honor them; M5 does not randomize.
17. **Text.** Calendar text fields reuse the M3 sanitization (`ImportTextSanitizer`
    rules / `RecipeService` limits): trim, collapse whitespace, strip
    control/format characters, decode entities, reject markup. Limits: name 200,
    long text 20,000, short note 300.
18. **UI.** Desktop month grid (`docs/PRODUCT_SPEC.md §6`), mobile Month/Agenda
    toggle (agenda defaults to the visible month), date detail in a modal/side
    panel, snapshot display text truncated to two lines per cell, today
    highlighted, past dimmed and read-only. The month grid is an ARIA grid with
    roving-tabindex arrow-key navigation. Print skeleton = print stylesheet +
    Print button for the visible month (full printing is M10).
19. **No new infrastructure.** No Redis, no SignalR hub (M9), no background
    workers. Data stays in PostgreSQL; nothing critical is written to the app
    container filesystem.
20. **M5 writes no audit rows and performs no recipe mutation.** Recipe rename/
    retire/delete snapshot refreshes only touch `calendar_entries` columns for
    non-historical dates.

---

## 2. M5 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Domain/CalendarEntry.cs`, `Domain/CalendarEntryHistory.cs`,
  `Domain/CalendarEntryType.cs` (enum + snake_case converter).
- `Data/Configurations/CalendarEntryConfiguration.cs`,
  `.../CalendarEntryHistoryConfiguration.cs`; add
  `HouseholdSettings.HistoricalBoundaryDate`.
- `Services/HouseholdClock.cs` (or an extension on the settings service) —
  computes household-local today, `is_historical`, and applies the frozen
  boundary; shared by calendar reads and writes.
- `Services/CalendarService.cs` — read/write orchestration, per-type validation,
  snapshots, history rows, repeat warning, snapshot refresh on recipe rename.
- `Endpoints/CalendarEndpoints.cs` — route wiring and permission checks.
- `Services/RecipeService.cs` — call `CalendarService.RefreshSnapshotsForRecipe`
  on rename (retire/delete in M12).
- Migration `M5CalendarCore`.

### 2.2 `calendar_entries`

| Column | Type | Notes |
|--------|------|-------|
| `id` | bigint pk | |
| `meal_date` | date, unique | one entry per date |
| `entry_type` | text | snake_case enum converter |
| `recipe_id` | bigint null | FK `recipes(id)`, `ON DELETE SET NULL` |
| `is_locked` | boolean | default false |
| `diner_count` | int null | 1–99 |
| `restaurant_name` | text null | eat_out |
| `location_text` | text null | go_to_family_dinner |
| `custom_label` | text null | category name |
| `notes` | text null | |
| `icon_key` | text null | curated catalog |
| `include_groceries` | boolean | derived |
| `snapshot_recipe_name` | text null | |
| `snapshot_display_text` | text | required |
| `created_by_user_id` / `updated_by_user_id` | bigint null | FK `users(id)` |
| `created_at` / `updated_at` | timestamptz | UTC |

Per-type rules:

| Type | Required | Optional | `include_groceries` | Icon |
|------|----------|----------|---------------------|------|
| `recipe` | `recipe_id` | notes | `true` | no |
| `eat_out` | — | restaurant_name, notes | `false` | no |
| `fend_for_yourself` | — | notes | `false` | no |
| `host_family_dinner` | — | recipe_id, notes, diner_count | `recipe_id != null` | yes |
| `go_to_family_dinner` | — | location_text, notes | `false` | yes |
| `holiday` | — | custom_label, recipe_id, notes | `recipe_id != null` | yes |
| `misc` | `custom_label` | recipe_id, notes | `recipe_id != null` | yes |
| `vacation` | — | notes | `false` | no |

`snapshot_display_text` precedence: recipe name → `custom_label` →
`restaurant_name` → `location_text` → the human type label
(`Eat Out`, `Fend For Yourself`, `Host Family Dinner`, `Go To Family Dinner`,
`Holiday`, `Vacation`). `blocks_automation = (entry_type == vacation)`.

### 2.3 `calendar_entry_history`

`id`, `calendar_entry_id` (nullable FK, `ON DELETE SET NULL`), `meal_date`
(copied), `operation_id` (uuid null), `before_json` (jsonb null), `after_json`
(jsonb null), `changed_by_user_id` (null), `changed_at`, `change_reason` (null),
`is_historical_correction` (boolean, default false). Indexes on
`calendar_entry_id`, `operation_id`, and `changed_at`.

### 2.4 Routes (recorded in `docs/API.md` before completion)

- `GET /api/v1/calendar?start=YYYY-MM-DD&end=YYYY-MM-DD` — `calendar.view`.
  Returns `{ start, end, today, week_starts_on, entries: [ day ] }` (only
  non-blank days; ascending). Span ≤ 366 days; both ends within ±5 years.
- `GET /api/v1/calendar/{date}` — `calendar.view`. Returns one `day`.
- `GET /api/v1/calendar/{date}/repeat-check?recipe_id={id}` — `calendar.view`.
  Returns `{ is_repeat, window_start, window_end, occurrences: [ { date } ] }`
  over `[date-7, date+14]`.
- `PUT /api/v1/calendar/{date}` — `calendar.edit`. Full-replace upsert.
- `DELETE /api/v1/calendar/{date}` — `calendar.edit`. `204`.
- `POST /api/v1/calendar/{date}/lock` / `.../unlock` — `calendar.edit`.
  Returns the updated `day`.

`day`:
```json
{
  "date": "2026-10-06",
  "is_today": true,
  "is_historical": false,
  "can_edit": true,
  "entry": {
    "id": 12,
    "entry_type": "host_family_dinner",
    "recipe": { "id": 5, "name": "Lasagna", "image_url": null, "status": "normal" },
    "display_text": "Lasagna",
    "is_locked": false,
    "diner_count": null,
    "effective_diner_count": 4,
    "restaurant_name": null,
    "location_text": null,
    "custom_label": null,
    "notes": null,
    "icon_key": "star",
    "include_groceries": true,
    "blocks_automation": false,
    "created_at": "2026-10-05T00:00:00+00:00",
    "updated_at": "2026-10-05T00:00:00+00:00"
  },
  "repeat_warning": {
    "recipe_id": 5,
    "window_start": "2026-09-29",
    "window_end": "2026-10-20",
    "occurrences": [{ "date": "2026-10-11", "entry_type": "recipe" }]
  }
}
```

`PUT` body: `{ "entry_type", "recipe_id"?, "diner_count"?, "restaurant_name"?,
"location_text"?, "custom_label"?, "notes"?, "icon_key"? }`. Omitted = null.

All errors use the shared envelope.

### 2.5 Frontend (`src/features/calendar/`)

- `CalendarPage.tsx` (replaces the placeholder route `/calendar`), `MonthGrid`,
  `AgendaList`, `DateDetailDialog`, `EventIcon`, `icons.tsx`, `useCalendar.ts`,
  `calendarTypes.ts`, `calendarForm.ts`.
- TanStack Query keys per date range; modal reuses `components/Modal`.
- Month grid: ARIA `grid`/`gridcell` with roving tabindex; day cells show two
  lines of `display_text`; today highlighted; past dimmed and non-interactive
  except read-only detail; lock icon on locked entries.
- Print: `@media print` landscape month stylesheet (day number, display text,
  no photos, no lock icons) + a Print button.
- Manual recipe picker reuses the M3 recipe search hook with an image/name/last
  eaten/times eaten card and the server repeat warning.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M5-01 | Calendar schema + model | `calendar_entries`, `calendar_entry_history`, `household_settings.historical_boundary_date`, domain/enums/config, migration, manifest M5 `in_progress`, issues created | — |
| M5-02 | Household clock + frozen boundary | today/historical/`can_edit` computation, timezone-change freeze, DST/timezone matrix tests | M5-01 |
| M5-03 | Calendar read API | `GET /calendar` range + `GET /calendar/{date}`, permissions, range/horizon caps, snapshot display, repeat warning, no-write | M5-02 |
| M5-04 | Calendar write API | `PUT`/`DELETE`, per-type validation, snapshots, history rows, past-date rejection, recipe-rename snapshot refresh | M5-03 |
| M5-05 | Lock/unlock | `POST .../lock`/`unlock`, blank/past rules, manual-edit-retains-lock | M5-04 |
| M5-06 | Manual recipe selection backend | `repeat-check` endpoint, recipe summary fields, 7/14 window | M5-03 |
| M5-07 | Special types + icon catalog | per-type validation, icon key catalog + validation, frontend icon set | M5-04 |
| M5-08 | Diner metadata | nullable count, effective count, validation | M5-04 |
| M5-09 | Month calendar UI | desktop grid, navigation (month/year), ARIA grid keyboard nav, print CSS + button, range caps in the client | M5-03…M5-08 |
| M5-10 | Mobile agenda | Month/Agenda toggle, agenda = visible month | M5-09 |
| M5-11 | Date detail UI | modal/side panel, per-type fields, lock/delete, recipe picker, repeat warning, validation | M5-06, M5-08, M5-09 |
| M5-12 | Historical read-only UX | dimmed cells, read-only detail, no edit affordances; server already enforces | M5-09, M5-11 |
| M5-13 | Calendar print skeleton | landscape print stylesheet verification | M5-09 |
| M5-14 | M5 verification sweep | independent §4 battery, docs, manifest M5 `complete` + evidence, `CHANGELOG.md`, tag `m5-complete`, close tracking issue | M5-01…M5-13 |

Milestone mapping: `docs/MILESTONES.md` M5 items 1→M5-01; 10→M5-02/M5-04;
8→M5-05; 9→M5-06/M5-11; 5,6→M5-07; 7→M5-08; 2,12→M5-09; 3→M5-10; 4→M5-11;
11→M5-12; 13→M5-13; new verification→M5-14.

One issue at a time, in order. Each issue gets a branch from current `main`,
implementer work, gates, independent reviewer, fixes on the same branch,
fast-forward merge, issue closure, and push. No unrelated feature expansion is
permitted.

---

## 4. M5 verification battery

- **Timezone / historical:** household midnight rollover; DST spring-forward and
  fall-back; UTC offset mismatch; client timezone has no effect; today editable;
  yesterday historical at midnight; the frozen boundary prevents a
  historical→editable flip when the household timezone moves (east and west,
  including a multi-hour/multi-day shift).
- **Historical immutability:** `PUT`/`DELETE`/lock/unlock on a past date rejected
  for Admin, `calendar.edit`, `calendar.view`, and unauthenticated callers;
  `GET` allowed; boundary computed in the household timezone, never server-local.
- **Permission matrix:** every route with Admin, `calendar.edit`,
  `calendar.view` only, no permission, unauthenticated, and single-user.
- **Entry type validation:** each type's required/optional fields; recipe
  required for `recipe`; `custom_label` required for `misc`; `include_groceries`
  derivation; `blocks_automation` for vacation; text limits and sanitization.
- **Lock:** blank cannot lock; past cannot lock/unlock; manual edit retains lock;
  delete removes the entry; unlock works.
- **Snapshots:** recipe rename refreshes non-historical display and freezes
  historical; retire/delete keeps the historical display.
- **Repeat warning:** exact-recipe detection at 7 days back and 14 days forward,
  non-blocking, override allowed, never for same-protein.
- **Limits:** 366-day range cap; ±5-year horizon; invalid dates rejected.
- **Draft integrity / writes:** reads write nothing; writes stage exactly one
  history row; last-write-wins; no audit rows in M5; migration has no pending
  model changes.
- **UI:** ARIA grid keyboard navigation and focus; labels; mobile Month/Agenda;
  read-only past; print CSS; 2-line cell truncation; no HTML/script rendering.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M5 sections of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the
   **absolute** `.csproj` to `projectPath`
   (`/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj`): `projectPath`
   resolves against the MCP server working directory `/mnt/y`, so
   `src/FateAndFridge.Api/...` silently loads an AdhocWorkspace (false CS0234
   storm); `filePath` instead resolves against the workspace root
   `/mnt/y/backend`. Never use
   `String.Normalize`; this workstation runs invariant globalization, so any
   folding/casing logic must be explicit and table-driven.
3. Frontend gates: Vitest and Playwright workers time out on the 9p share
   `/mnt/y`, so run them from a local mirror of `frontend/` under
   `/tmp/opencode/` (copy with `tar`/`rsync`, sync changed files before each run,
   and never commit from the mirror). Format/lint/typecheck/build may run on the
   share.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
5. Reviewer must independently execute the relevant tests and review timezone/
   historical enforcement, permission enforcement, lock semantics, snapshot
   integrity, range/horizon caps, and mobile/accessibility behavior. Tests must
   not depend on the wall clock or the server's local timezone; use an injectable
   `TimeProvider`.
6. At M5 start, set M5 `in_progress` in
   `frontend/src/features/progress/milestones.ts` and create issues. At M5-14, set
   M5 `complete` with evidence, update `progressSummary` and the progress tests
   (including the Playwright progress assertion), update `CHANGELOG.md` and
   `docs/API.md` (and `docs/DATABASE.md` for the new tables/column), push `main`,
   tag `m5-complete`, and close the M5 tracking issue.

---

## 6. Explicitly deferred from M5

- Randomizer, single-date reroll, undo, and operation metadata: M6.
- Batch previews, Try Another, atomic apply, Fill Empty Days, vacation blocking
  enforcement, batch concurrency: M7.
- Grocery aggregation and consumption of `include_groceries`: M8.
- WebSocket live sync and query invalidation: M9.
- Full calendar/grocery printing and browser print tests: M10.
- Statistics and meal-history browser: M11.
- Audit viewer, calendar `audit_events`, historical correction, hard recipe
  deletion, and dangerous-action framework: M12.
- Serving scaling by diner count, package intelligence, dietary scheduling:
  see `docs/FUTURE_FEATURES.md`.
