# Fate & Fridge — Milestone 8 Plan (Groceries)

Status: **approved, ready to execute**
Approved: 2026-10-09 (product owner answered the full M8 decision set)
Purpose: authoritative M8 runbook. A fresh session should execute M8 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
database, architecture, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. M8 delivers the **live grocery projection**: planned recipe ingredients in
   the active range + manual items, grouped by department, with recipe
   attribution, checkbox persistence, hide-checked, reusable staples, and a
   printable list. It is **not** a persisted calculated list.
2. Execution mirrors M5/M6/M7: `docs/M8_PLAN.md` is the runbook; one branch from
   current `main`, per-issue commits, gates, an independent reviewer, fixes on
   the same branch, fast-forward merge, issue closure, and push. The final issue
   sets the manifest M8 status and cuts tag `m8-complete`.
3. **Contributing meals.** Only calendar entries whose derived
   `include_groceries` is true contribute recipe-derived groceries (recipe
   entries, plus `host_family_dinner`/`holiday`/`misc` carrying a recipe).
   `eat_out`, `fend_for_yourself`, `go_to_family_dinner`, and vacation do not.
4. **Aggregation.** Key on the canonical `ingredient_id` (aliases resolve to it,
   M2) + **unit dimension** (or `count`). Compatible quantities in the same
   dimension combine via the existing `UnitConversionService`; incompatible
   dimensions stay separate (1 onion vs ½ cup chopped onion). **Never guess
   package-size conversions** (V2). Unitless and same-unit counts combine as a
   count; different count units stay separate. Preparation notes are **not** part
   of identity (dropped when combining).
5. **Display unit.** A combined total is shown in the **largest compatible unit
   that appears in the range** (1 lb + 8 oz → 1.5 lb). Quantities render as the
   nearest **common fraction when exact** (½, ⅓, ¾), otherwise up to two
   decimals; a quantity-less line renders without a quantity.
6. **Attribution.** Each recipe-derived item lists **one row per contributing
   meal** (recipe name + weekday/short date, e.g. "Tacos — Tue 10/6").
7. **Ranges.** Presets: Next 3 Days, Next 7 Days, Next 14 Days, Rest of Month,
   Custom (start + day count). The **server resolves** the preset to a concrete
   range; the **last-used range is remembered per user**. Ranges are
   future/today only — a past start is rejected (`VALIDATION_FAILED`); the range
   is capped at 366 days. Default on first open: **Next 7 Days**.
8. **Manual items.** Range-scoped: a manual item belongs to the active range and
   is hidden when the range changes; **Save for future** promotes it to a
   staple. A manual item is free text with optional quantity/unit and an optional
   canonical ingredient; the department defaults to Other or comes from the
   linked ingredient. Manual/staple lines stay **separate** from recipe-derived
   lines and are visually distinct **without relying on color alone**.
9. **Staples.** A household-wide reusable catalog (name, optional ingredient,
   department, optional default quantity/unit). Staples are **not auto-added**;
   the user taps "add to list" to create a manual item for the current range.
10. **Check state.** **Household-shared**: one shopping list, any member's check
    is visible to all. Identity = **canonical ingredient + dimension** (or
    `count`) for recipe-derived items, and the manual item's own id for manual
    items — quantity-independent, so checked state survives quantity changes and
    range expansion "where reasonable". A checked item is dimmed, struck
    through, and moved below unchecked items **within its department**.
11. **Hide Checked** is a **per-user** preference, persisted.
12. **Ordering.** Departments follow the configured `sort_order`; items with no
    department go to **Other last**. Within a department: **unchecked items
    first, alphabetical by name, then checked items alphabetically** (the spec's
    check-state rule takes precedence over the chosen alphabetical primary sort;
    recorded here as the resolution).
13. **Permissions.** `groceries.view` for reads; `groceries.edit` for manual
    items, staples, and check/uncheck. Single-user mode acts with full
    permissions. Authorization is server-side only.
14. **Printing.** M8 ships a **print-optimized grocery view**: grouped by
    department in configured order, a checkbox before each item, recipe/date
    attribution, manual items included, and a **"include checked items"** option
    (checked excluded by default). M10 polishes cross-app print CSS.
15. **No new infrastructure.** Grocery math lives in service/domain code, not in
    React or route handlers. No pantry/inventory, package intelligence, or
    serving scaling (V2 — see `docs/FUTURE_FEATURES.md`).
16. **Verification.** The final issue mirrors the M4–M7 sweep: exhaustive grocery
    tests, permission/timezone/identity coverage, an independent review, docs,
    the manifest M8 `complete` with evidence, tag `m8-complete`, and closing the
    tracking issue.

---

## 2. M8 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Domain/GroceryStaple.cs`, `Domain/ManualGroceryItem.cs`,
  `Domain/GroceryCheckState.cs`, `Domain/UserGroceryPreference.cs`.
- `Services/GroceryAggregationService.cs` — pure aggregation over supplied
  recipe ingredients + units + conversion factors; produces item groups,
  quantities, and per-meal attribution.
- `Services/GroceryListService.cs` — resolves the range, loads contributing
  calendar entries and recipe ingredients, calls aggregation, merges manual
  items, attaches household check state, orders by department/checked/alphabetical.
- `Services/GroceryStapleService.cs` — staple CRUD + add-to-list.
- `Services/GroceryItemIdentity.cs` — the stable identity/hashing shared by the
  list and the check API.
- `Endpoints/GroceryEndpoints.cs`.
- Migration `M8Groceries`.

### 2.2 Routes (recorded in `docs/API.md` before completion)

- `GET /api/v1/groceries?preset=next_7_days` or `?start=YYYY-MM-DD&days=N`
  (`groceries.view`) → resolved range + departments with ordered items; each item
  carries its identity, name, quantity/unit text, department, `sources`
  (`recipe` with meal date, or `manual`), and `checked`. Presets:
  `next_3_days` | `next_7_days` | `next_14_days` | `rest_of_month` | `custom`.
- `POST /api/v1/groceries/manual`, `PATCH /groceries/manual/{id}`,
  `DELETE /groceries/manual/{id}` (`groceries.edit`) — free-text or
  ingredient-linked, range-scoped, optional `save_for_future`.
- `GET /api/v1/groceries/staples`, `POST /groceries/staples`,
  `PATCH /groceries/staples/{id}`, `POST /groceries/staples/{id}/add-to-list`
  (`groceries.view` read, `groceries.edit` writes).
- `POST /api/v1/groceries/check` / `POST /api/v1/groceries/uncheck`
  (`groceries.edit`) — body `{ "identity": "<hash>" }`; household-shared.
- `GET/PATCH /api/v1/groceries/preferences` (`groceries.view`) — per-user
  `hide_checked` and last-used range.

Errors: `VALIDATION_FAILED` (range/preset/quantity), `NOT_FOUND`, `FORBIDDEN`,
plus `GROCERY_RANGE_TOO_LARGE` if the custom range exceeds 366 days.

### 2.3 Persistence (docs/DATABASE.md §9)

- `grocery_staples`: `id`, `name`, `ingredient_id?`, `department_id?`,
  `default_quantity_numeric?`, `unit_id?`, `is_active`, timestamps.
- `manual_grocery_items`: `id`, `name`, `ingredient_id?`, `department_id?`,
  `quantity_numeric?`, `unit_id?`, `range_start`, `range_days`, `save_for_future`,
  `created_by_user_id`, timestamps.
- `grocery_check_state`: `id`, `item_identity_hash` (unique), `checked`,
  `checked_by_user_id?`, `checked_at?`, `updated_at` — household-shared.
- `user_grocery_preferences`: `user_id` (unique), `hide_checked`, `range_preset`,
  `range_start?`, `range_days?`, `updated_at`.

### 2.4 Frontend

- `features/groceries/` — a mobile-first list grouped by department with sticky
  headers, large checkboxes/tap targets, hide-checked toggle, manual-item
  add/edit, staples management, and a print view.
- Reuses the shared `Modal`, query hooks (TanStack Query), and permission
  helpers; `groceries.edit` gates writes.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M8-01 | Grocery schema + preferences | domain entities, `M8Groceries` migration, per-user preferences, seed check | — |
| M8-02 | Aggregation service + identity | canonical-ingredient aggregation, compatible-dimension conversion, counts, largest-unit display, fractions, per-meal attribution, `GroceryItemIdentity` | M8-01 |
| M8-03 | Grocery list API | `GET /groceries` with presets/custom range, server resolution, remembered range, department ordering, check state, `include_groceries` scope | M8-02 |
| M8-04 | Manual items API | range-scoped CRUD, free text + optional ingredient/department, `/preferences` hide-checked | M8-01 |
| M8-05 | Staples API | household catalog CRUD + add-to-list (Save for future) | M8-04 |
| M8-06 | Check state API | `check`/`uncheck` by identity, household-shared, stable across range/quantity changes | M8-03 |
| M8-07 | Grocery UI | mobile-first grouped list, large checkboxes, hide-checked, manual add/edit, staples | M8-03…M8-06 |
| M8-08 | Grocery print | print-optimized view: department order, checkbox, attribution, include-checked option | M8-07 |
| M8-09 | Exhaustive grocery tests | merge/conversion/incompatible-separate, alias→canonical, attribution, check identity, permissions, range boundaries | M8-01…M8-08 |
| M8-10 | M8 verification sweep | independent §4 battery, docs, manifest M8 `complete`, `CHANGELOG.md`, tag `m8-complete`, close tracking issue | M8-01…M8-09 |

Milestone mapping: 1→M8-02; 2→M8-02; 3→M8-02; 4→M8-03; 5→M8-03; 6→M8-04;
7/8→M8-05; 9→M8-03/M8-07; 10→M8-06; 11→M8-06/M8-07; 12→M8-07; 13→M8-08;
verification→M8-10.

---

## 4. M8 verification battery

- **Aggregation:** same canonical ingredient + compatible units combine
  (1 lb + 8 oz → 1.5 lb; tbsp + cup); incompatible dimensions stay separate;
  count/unitless combine by same unit; aliases resolve to the canonical item.
- **Scope:** only `include_groceries` entries contribute; eat-out/fend/go-to/
  vacation contribute nothing; specials with a recipe do.
- **Display:** largest-present unit; exact fractions else 2 decimals; quantity-less
  lines; attribution lists one row per meal.
- **Ranges:** each preset resolves correctly (3/7/14, month end, custom start +
  days, incl. month boundary); past start rejected; >366 days rejected; last-used
  range remembered per user; default Next 7 Days.
- **Manual/staples:** range-scoped visibility; free-text + optional ingredient;
  department default; Save for future creates a staple; add-to-list creates a
  range manual item; household-wide staples; manual lines stay separate/distinct.
- **Check state:** persists by ingredient+dimension across quantity changes and
  range expansion; different dimensions do not collapse; household-shared across
  users; checked moved below unchecked within a department; hide-checked is
  per-user and persists.
- **Permissions:** `groceries.view`/`groceries.edit` matrix on every route;
  member without edit → 403; single-user full access.
- **UI:** mobile tap targets, sticky headers, checkbox UX, hide-checked, manual
  add/edit, staples, print view with include-checked; screenshots for UI changes.
- **Print:** department order, checkbox, attribution, manual items, checked
  excluded by default with an include option.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M8 section of `docs/MILESTONES.md`, and the
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
   local `node_modules`** (`npm ci`; never symlink it to the 9p share). Never
   commit from the mirror.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
   Do not use `dotnet test` (it misreports the MTP runner); run the test binary
   with `-class` filters.
5. The reviewer must independently execute the relevant tests and review
   aggregation/identity, range resolution, scope, check-state integrity, manual/
   staples, permissions, ordering, printing, and mobile/accessibility.
6. At M8 start, set M8 `in_progress` in
   `frontend/src/features/progress/milestones.ts` and create issues. At M8-10,
   all gates must pass, then delegate a rebuild/redeploy to `docker-admin`
   (`scripts/deploy.sh up`) and verify the live instance (healthy; at least one
   M8 route reachable; `/progress` shows M8 complete) before setting M8
   `complete` with evidence, updating the progress tests and the Playwright
   progress assertion, `CHANGELOG.md`, `docs/API.md`, and `docs/DATABASE.md`,
   pushing `main`, tagging `m8-complete`, and closing the M8 tracking issue.
   See `AGENTS.md` → "Milestone completion gate".

### 5.1 Execution cadence

M8 is executed in **deliverable-sized sessions, not issue-sized ones**, with a
**checkpoint commit after every verified deliverable** (see `docs/M6_PLAN.md`
§5.1). One session owns one authorable *and* independently verifiable unit;
commit as soon as its gates pass; never leave more than one unverified unit in
the working tree. `implementer` runs at `steps: 80`, `reviewer` 28,
`docker-admin` 16. The Lead orchestrates and does not author milestone code.

---

## 6. Explicitly deferred from M8

- Live-sync broadcast of grocery/check changes: M9.
- Cross-app print CSS polish and calendar printing: M10.
- Statistics over grocery ingredient frequency: **not in M11** — deferred to V2
  (`docs/FUTURE_FEATURES.md` → "Statistics expansion"). M11 ships the
  `PRODUCT_SPEC` §41/§12 household statistics and meal history without
  grocery-ingredient frequency (`docs/M11_PLAN.md` §1.2).
- Pantry/inventory, package intelligence, serving scaling, dietary scheduling,
  external API credentials: see `docs/FUTURE_FEATURES.md`.
