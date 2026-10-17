# Fate & Fridge — Milestone 11 Plan (Statistics and History)

Status: **approved, ready to execute**
Approved: 2026-10-14 (product owner answered the full M11 decision set)
Started: 2026-10-16 (tracking #140; M11-01…M11-08: #141–#148)
Purpose: authoritative M11 runbook. A fresh session should execute M11 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
architecture, database, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. **Metric set.** M11 ships the **full `docs/PRODUCT_SPEC.md` §41 list** plus the
   **§12 per-recipe metrics**:
   - household overview: **most eaten recipes**, **least recently eaten
     recipes**, **meal count this month**, **meal count this year**, **primary
     protein distribution**, and **special-event counts** (all seven entry
     types);
   - per recipe: **Last Eaten**, **Next Planned**, **Times Eaten**.
2. **Grocery ingredient frequency is NOT in M11.** `docs/FUTURE_FEATURES.md` →
   "Statistics expansion" lists it as **V2**, and that registry is authoritative
   (`AGENTS.md`). The stale `docs/M8_PLAN.md` §6 note that deferred it to M11 is
   **corrected** during M11's docs pass.
3. **"Eaten" is strictly past.** An occurrence counts as eaten only when
   `meal_date < household today` (household timezone). **Next Planned** uses
   today and future dates. Today's dinner is therefore not yet "eaten".
4. **An occurrence is any entry carrying a recipe** — `recipe` entries plus
   `host_family_dinner` / `holiday` / `misc` that carry a recipe (the same rule
   M8 uses for grocery contribution). `eat_out`, `fend_for_yourself`,
   `go_to_family_dinner`, and vacation never contribute to recipe metrics.
5. **Historical integrity.** Meals attributed to a now-**retired** recipe still
   count and appear in rankings **with a status badge**; a meal whose recipe was
   **hard-deleted** still counts, shown by its **stored snapshot name** as a
   **tombstone** entry (M12 owns hard deletion; M11 must not lose the count).
   Nothing in statistics may be derived solely from a live `recipes` row.
6. **Where the §12 metrics appear.** Both on the **recipe browser cards / recipe
   detail** and on the **statistics page**, backed by
   `GET /api/v1/statistics/recipes/{id}`. **Locked 2026-10-16 (M11-02):** each
   metric is `null` when the recipe has no such history (never a fake `0`), so a
   never-eaten recipe returns `times_eaten: null` and a recipe that is only
   planned returns `next_planned` set with `times_eaten: null`. The same values
   flow through the list items and the detail `statistics` object; M11-07 tests
   this as written.
7. **Permissions — no contract change.** Statistics reads require
   **`recipes.view`**; meal-history reads require **`calendar.view`**. The seven
   permission keys are unchanged (no eighth key). Enforcement is server-side
   only; the UI hides what the caller cannot read.
8. **Meal-history browser.** A **reverse-chronological list grouped by month**
   with filters (entry type, recipe, primary protein), using the household
   timezone. Read-only: **no edit affordance** (past-date editing and admin
   correction are M12).
9. **History data source.** The **current `calendar_entries`** rows for past
   dates. Past dates are immutable in normal use, so the current state is what
   was eaten; the change log/audit trail is M12's concern, not M11's.
10. **History range and paging.** Default the **last 12 months**; allow an
    explicit household-local `start`/`end` bounded to **366 days per request**
    (over that → `400 VALIDATION_FAILED`, reusing the M8 range-cap pattern);
    page **by month, newest first**.
11. **Ranking windows.** "Most eaten" and "least recently eaten" are **all-time**
    (Times Eaten is inherently historical). "Meal count this month/year" use the
    **household calendar month/year**. No user-selectable window in V1.
12. **Protein distribution.** Counts **recipe meals that have a primary
    protein**; one bar per protein. Meals with no recipe are **excluded** from the
    distribution (they are reported by the special-event counts instead). Recipe
    meals with no protein are excluded from the distribution and reported as an
    explicit **"no primary protein"** figure in the response (not silently
    dropped). **Locked 2026-10-16 (M11-01 gap resolution):** an occurrence whose
    recipe lists **several** proteins counts toward **each** of those proteins'
    bars (the bars answer "meals featuring this protein"), so the bars may sum to
    more than the occurrence count; `no_primary_protein` covers an occurrence
    whose recipe has no protein rows or whose live recipe is gone (tombstone).
    M11-07 tests this rule as written.
13. **Special-event counts.** One count per entry type — **Eat Out, Host Family
    Dinner, Go To Family Dinner, Holiday, Misc, Fend For Yourself, Vacation** —
    over the **household calendar year**.
14. **Computation.** **On-demand SQL aggregate queries** over `calendar_entries`
    (+ recipe/protein joins), keyed on `meal_date`. **No cache, no statistics
    table, no background job, no new infrastructure.** A **non-destructive index
    migration** may be added only if the §4 performance baseline requires it and
    the Lead approves it.
15. **No charts library.** Numbers, ranked lists, and **CSS bars** for the
    protein distribution. No new frontend dependency; works offline.
16. **API.** Reuse the `docs/API.md` §14 shapes — `GET /api/v1/statistics/overview`,
    `GET /api/v1/statistics/recipes/{id}`, `GET /api/v1/statistics/history?start=&end=`
    — and record the concrete response contracts in §2.2 before completion.
17. **Explicitly out of M11:** the audit-log viewer (`PRODUCT_SPEC` §43, M12),
    admin past-date correction (`PRODUCT_SPEC` §42, M12), **Times Planned**
    (deferred by §12), advanced analytics (average days between repeats, favorite
    usage, cuisine trends, household preference trends — V2), grocery ingredient
    frequency (V2), and any new nav item or dashboard widget beyond the existing
    `/statistics` route.
18. **Verification.** The final issue mirrors the M4–M10 sweep: the §4 battery, an
    independent review, docs, the manifest M11 `complete` with evidence, tag
    `m11-complete`, and closing the tracking issue.

### 1.1 Execution details (locked 2026-10-15)

19. **Ranking size.** `most_eaten` and `least_recently_eaten` each return the
    **top 10**. "Least recently eaten" considers only recipes **eaten at least
    once**; recipes never eaten are reported as a single `never_eaten` **count**
    rather than ranked as infinitely stale.
20. **Grouping identity.** Meals group by **`recipe_id` when present**, else by
    the **normalized stored snapshot name** (a recipe hard-deleted in M12 nulls
    the reference). Implement and test this defensively **now**, before hard
    delete exists, so no history is ever lost.
21. **Deleted-recipe endpoint.** `GET /statistics/recipes/{id}` returns
    **`404 NOT_FOUND`** when no live recipe has that id; household rankings still
    show the deleted recipe's meals via the tombstone row.
22. **History day set.** The history browser is a **meal log**: it lists only days
    that have an entry, with no blank filler rows.
23. **History boundary.** History is **strictly past days**
    (`meal_date < household today`), matching the "eaten" rule.
24. **Meal count.** `meal_count_month` / `meal_count_year` count **every calendar
    entry** in the window regardless of type.
25. **History filters.** **Server-side and AND-combined**: entry type, recipe, and
    primary protein. The recipe filter matches the **recipe id or the stored
    snapshot name**, so deleted recipes stay filterable. Filters narrow within the
    already-bounded range.
26. **History paging.** **One month per page, newest first**; the range defaults to
    the last 12 months, and month navigation pages through it (a first load is a
    single month's query, not twelve). **Locked 2026-10-16 (M11-03):** the request
    selects the page with an optional household-local `month=yyyy-MM` (default =
    the newest month in the range that has at least one entry, falling back to the
    newest month in the range when none has entries). The response carries the
    applied `range`, the single `month` (its `key`, `label`, and full
    calendar-month `start`/`end`), a **sibling** `days` array (newest-first day
    rows — *not* nested inside `month`; recorded 2026-10-16 after M11-03 shipped
    that wire shape), and `paging` cursors (`newer`/`older`, one month per step
    within the range, `null` at the range ends). Because
    `calendar_entries.meal_date` is unique, a day holds exactly one entry, so each
    day row **is** the entry: `{ date, entry_type, recipe_id, name, protein }`,
    where `recipe_id` is null for a tombstone and `name` is the stored snapshot
    name, and `protein` is the entry's primary protein (null when none/not
    applicable). M11-05 builds the browser against this contract; M11-07 tests it.
27. **Per-recipe metrics on lists.** The **recipes list response gains the three
    §12 metrics** (computed in the same aggregate query) so browser cards need no
    per-card fetch; `GET /statistics/recipes/{id}` remains for the detail view.
28. **Performance bar.** On the TESTING §12 baseline (2,000 recipes, 10 years of
    meal history) the overview and a single history month each respond within
    **1 second**, with **no N+1** query pattern.
29. **Index policy.** Add a **non-destructive index migration** only if the
    measured baseline misses the bar, with the Lead's approval.
30. **UI gating.** Hide the Statistics nav item for a user without
    `recipes.view` (and the history tab without `calendar.view`) and show a clear
    "not available" message on a deep link. UI visibility only — the server
    remains the sole authority. **Locked 2026-10-17 (M11-05 review):** the client
    gates mirror the server's **edit-implies-view** rule
    (`PermissionEvaluator.ForMember`), so a member holding `recipes.edit` (or
    `calendar.edit`) is treated as holding the corresponding `.view` key: the nav
    item and the history tab are hidden only when the member holds **neither**
    key. M11-06 applies this to `useStatisticsAccess` and the AppShell nav gate.
31. **Existing recipes UI.** The per-recipe metrics reuse the **existing** recipes
    browser cards and the recipe detail/editor — **no new page and no new nav
    item**.

---

## 2. M11 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Services/StatisticsService.cs` — the aggregate queries for the overview and
  per-recipe metrics (pure reads; scoped over the request `AppDbContext`).
- `Services/MealHistoryService.cs` — the month-grouped history read with the
  bounded range and month paging.
- `Endpoints/StatisticsEndpoints.cs` — the three §14 routes under
  `/api/v1/statistics` with the permission keys from §1.7.
- Shared helpers for the "eaten vs planned" split and the
  occurrence-eligibility rule (§1.3, §1.4) so the overview, per-recipe, and
  history reads cannot drift.
- **No new entity and no migration** unless §1.14's performance exception is
  approved.

### 2.2 Response contracts (recorded in `docs/API.md` §14 before completion)

- `GET /statistics/overview` (`recipes.view`) → household-local window labels,
  `most_eaten` and `least_recently_eaten` (**top 10 each**, ordered by count /
  last-eaten date with a deterministic name tie-break; each entry carries the
  recipe id **or** the tombstone snapshot name, a status badge, times eaten, and
  last eaten), a `never_eaten` count, `meal_count_month`, `meal_count_year`
  (both counting **every entry type**), `protein_distribution` (per protein +
  an explicit `no_primary_protein` figure), and `special_event_counts` (the seven
  types over the household calendar year).
- `GET /statistics/recipes/{id}` (`recipes.view`) → `last_eaten`, `next_planned`,
  `times_eaten` for one recipe, with `404 NOT_FOUND` when no live recipe has that
  id (a hard-deleted recipe's meals still appear in the household rankings).
- **The existing recipes list response gains `last_eaten`, `next_planned`, and
  `times_eaten`** per recipe (§1.27), computed in the same aggregate query, so the
  browser needs no per-card request.
- `GET /statistics/history?start=&end=` (`calendar.view`) → **one month per page,
  newest first**, each month with its household-local label and only its **days
  that have an entry** (strictly past dates; each day carries the date, entry
  type, recipe or snapshot name, and primary protein), plus the applied range and
  the paging cursor. Optional server-side, AND-combined filters: `entry_type`,
  `recipe` (id **or** snapshot name), and `protein`. Errors: `VALIDATION_FAILED`
  for a malformed range or one longer than 366 days.
- Every response uses the shared error envelope and snake_case JSON.

### 2.3 Frontend (`frontend/src/`)

- `features/statistics/StatisticsPage.tsx` (replacing the placeholder at
  `src/pages/StatisticsPage.tsx`) — overview cards, ranked lists, CSS protein
  bars, event counts; loading/error/empty states; permission-gated sections.
- `features/statistics/MealHistoryBrowser.tsx` — month-grouped read-only list
  with filters and month paging.
- `features/statistics/useStatistics.ts` — TanStack Query hooks.
- `features/recipes/` — per-recipe metrics (Last Eaten / Next Planned / Times
  Eaten) on cards/detail, with the retired badge.
- Reuses the shared `Modal`/`Screens`/permission helpers; no new dependency.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M11-01 | Statistics service + overview endpoint | shared "eaten/planned" + occurrence-eligibility helpers, the overview aggregates (rankings, month/year counts, protein distribution, event counts), `GET /statistics/overview` under `recipes.view` | — |
| M11-02 | Per-recipe metrics endpoint | `GET /statistics/recipes/{id}` (Last Eaten / Next Planned / Times Eaten) with retired/deleted snapshot handling | M11-01 |
| M11-03 | Meal-history endpoint | `GET /statistics/history?start=&end=` (month-grouped, filters, 366-day bound, month paging) under `calendar.view` | M11-01 |
| M11-04 | Statistics page UI | overview cards, ranked lists, CSS protein bars, event counts; empty/error states; permission gating | M11-01, M11-02 |
| M11-05 | Meal-history browser UI | month-grouped read-only list with filters and month paging | M11-03 |
| M11-06 | Per-recipe metrics in the recipe browser | Last Eaten / Next Planned / Times Eaten on the existing cards/detail with the retired badge; the recipes **list** response carries the metrics so cards need no per-card fetch | M11-02 |
| M11-07 | Exhaustive statistics/history tests | definitional matrix (today excluded, specials-with-recipe counted, retired/deleted snapshots, timezone month/year boundaries, protein distribution, event counts), permission matrix, history range/paging bounds, and the TESTING §12 performance baseline (2,000 recipes / 10 years) | M11-01…M11-06 |
| M11-08 | M11 verification sweep | independent §4 battery, docs (`API.md` §14, `TESTING.md`, `CHANGELOG.md`, the `M8_PLAN` §6 correction), manifest M11 `complete`, rebuild/redeploy + live gate, tag `m11-complete`, close the tracking issue | M11-01…M11-07 |

Milestone mapping: 1→M11-01/M11-04; 2→M11-02/M11-06; 3→M11-01/M11-04;
4→M11-01/M11-04; 5→M11-03/M11-05; verification→M11-08.

---

## 4. M11 verification battery

- **Occurrence rules:** today's entry is **not** eaten and **is** Next Planned; a
  past entry is eaten and not planned; `host_family_dinner`/`holiday`/`misc`
  carrying a recipe count; `eat_out`/`fend_for_yourself`/`go_to_family_dinner`/
  vacation never count toward recipe metrics.
- **Historical integrity:** a retired recipe's meals still count and carry the
  retired badge; a hard-deleted recipe's meals still count under the stored
  snapshot name (tombstone), and no total silently drops; a renamed recipe's
  history is not double-counted.
- **Windows and timezone:** "this month"/"this year" are the household calendar
  month/year in the household timezone (a UTC-naive implementation must fail a
  boundary test); all-time rankings are unaffected by the window.
- **Rankings:** most eaten ordered by count (ties broken deterministically);
  least recently eaten ordered by last-eaten date; both bounded/sane with an
  empty household.
- **Protein distribution:** only recipe meals with a primary protein appear in the
  bars; `no_primary_protein` is reported explicitly; no-recipe days are excluded
  from the distribution and appear only in the event counts.
- **Event counts:** all seven types counted over the household calendar year,
  including types with a recipe (which additionally count toward recipe metrics).
- **History:** months newest-first with correct labels; filters (type, recipe,
  protein) narrow correctly; a range over 366 days or a malformed range →
  `VALIDATION_FAILED`; paging by month is stable and complete; only past dates
  appear.
- **Permissions:** `recipes.view` on overview/per-recipe, `calendar.view` on
  history; unauthenticated → 401, a member lacking the key → 403, single-user
  system actor and Admin → full access. No new permission key exists.
- **Performance:** on the TESTING §12 baseline (2,000 recipes, 10 years of meal
  history) the overview and a single history month each respond within **1
  second**, with **no N+1** query pattern; the recipes list with metrics stays
  bounded. An index migration is added only if the measured baseline misses this
  bar (§1.29).
- **UI:** statistics page cards/lists/bars render and are accessible
  (labels, no colour-only meaning, keyboard, mobile); the history browser is
  read-only with no edit affordance; per-recipe metrics show on cards/detail with
  the retired badge; empty/error states handled.
- **No schema change** (unless §1.14's approved index exception applies) and **no
  new API beyond `docs/API.md` §14**.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M11 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use the Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the
   **absolute** `.csproj` to `projectPath`
   (`/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj`); `filePath`
   resolves against the workspace root `/mnt/y/backend`. Confirm with
   `roslyn_get_project_info` (`is_msbuild_workspace: true`). Never use
   `String.Normalize` (invariant globalization).
3. Frontend gates: Vitest/Playwright workers time out on the 9p share `/mnt/y`,
   so run them from a local mirror under `/tmp/opencode/frontend` with a **real
   local `node_modules`** (`npm ci`; never symlink it to the 9p share; sync with
   `rsync --exclude node_modules`, copy formatter output back with `cp`). Never
   commit from the mirror.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
   Do not use `dotnet test` (it misreports the MTP runner); run the test binary
   with `-class` filters.
5. The reviewer must independently execute the relevant tests and review the
   occurrence rules, historical integrity, timezone windows, permission matrix,
   history bounds/paging, the performance baseline, and the UI/accessibility.
6. At M11 start, set M11 `in_progress` in
   `frontend/src/features/progress/milestones.ts` (and its feature list) and
   create issues. At M11-10, all gates must pass **and CI must be green** (see
   `BUG-009`/`BUG-010`), then delegate a rebuild/redeploy to `docker-admin`
   (`scripts/deploy.sh up`) and verify the live instance (healthy; the
   statistics and history views reachable with real data; `/progress` shows M11
   complete) before setting M11 `complete` with evidence, updating the progress
   tests and the Playwright progress assertion, `CHANGELOG.md`, and the docs,
   pushing `main`, tagging `m11-complete`, and closing the M11 tracking issue.
   See `AGENTS.md` → "Milestone completion gate".

### 5.1 Execution cadence

M11 is executed in **deliverable-sized sessions, not issue-sized ones**, with a
**checkpoint commit after every verified deliverable** (see `docs/M6_PLAN.md`
§5.1). One session owns one authorable *and* independently verifiable unit; commit
as soon as its gates pass; never leave more than one unverified unit in the
working tree. `implementer` runs at `steps: 80`, `reviewer` 28, `docker-admin`
16. The Lead orchestrates and does not author milestone code.

---

## 6. Explicitly deferred from M11

- **Audit-log viewer** (`PRODUCT_SPEC` §43) and the **audit event model**: M12.
- **Admin correction of past entries** (`PRODUCT_SPEC` §42) and any past-date
  editing: M12.
- **Times Planned** (`PRODUCT_SPEC` §12 explicitly defers it).
- **Grocery ingredient frequency** and the rest of **Statistics expansion**
  (average days between repeats, favorite usage, cuisine trends, household
  preference trends): V2 (`docs/FUTURE_FEATURES.md`).
- **Nutrition / macros**: V2.
- A **charting library** or any new frontend dependency.
- **Caching/materialized statistics**, a background statistics job, or any new
  infrastructure.
- New **nav items / dashboard widgets** beyond the existing `/statistics` route
  and the per-recipe metrics on the recipe browser.
- User-selectable ranking windows (all-time only in V1).
