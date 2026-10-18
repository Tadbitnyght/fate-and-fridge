# M11 Verification — Statistics and History

Status: **complete** (live deployment verified 2026-10-18)
Runbook: `docs/M11_PLAN.md` (approved 2026-10-14; execution details locked
2026-10-15). Coverage map: `docs/M11_TEST_MATRIX.md`.
Tracking: #140; M11-01…M11-08: #141–#148.

This document records how M11 was verified: the independent review verdicts, the
gates that were run, the measured performance numbers, the defects the review
found and how they were fixed, and (finally) the live rebuild/redeploy evidence
required by the milestone completion gate.

---

## What shipped

Three new reads, all aggregate queries over `calendar_entries` (+ recipe/protein
joins) with **no cache, no statistics table, no background job, no new
infrastructure**:

- `GET /api/v1/statistics/overview` (`recipes.view`) — household-local window
  labels, `most_eaten` and `least_recently_eaten` (top 10 each, deterministic
  tie-breaks, status badges, snapshot tombstones), `never_eaten`,
  `meal_count_month`, `meal_count_year`, `protein_distribution`
  (+ `no_primary_protein`), and `special_event_counts` (the seven types).
- `GET /api/v1/statistics/recipes/{id}` (`recipes.view`) — `last_eaten`,
  `next_planned`, `times_eaten`; `404 NOT_FOUND` when no live recipe has that id.
- `GET /api/v1/statistics/history` (`calendar.view`) — one month per page, newest
  first, strictly past days that have an entry, server-side AND-combined filters
  (`entry_type`, `recipe` id **or** stored snapshot name, `protein`), a
  366-day-bounded household-local range, and `newer`/`older` month cursors.

Frontend: the `/statistics` overview page (ranked lists, month/year cards, CSS
protein bars, event counts), a read-only month-grouped meal-history browser with
filters and paging, and **Last Eaten / Next Planned / Times Eaten** on the
existing recipe browser cards, compact list, and recipe detail/editor. No new
page, no new nav item, no new dependency (no chart library).

**No schema change, no migration, and no new permission key.**

---

## No schema, entity, API, or auth change

- `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`
  → "No changes have been made to the model since the last migration."
- No new entity; the only new files are services, endpoints, tests, and frontend
  modules.
- `PermissionKeys.All` still has exactly seven keys; `PermissionKeysTests`
  asserts that the statistics/history reads introduce no eighth key.
- No route beyond the three `docs/API.md` §14 routes; `Program.cs` only registers
  `StatisticsService`/`MealHistoryService` and maps the statistics group.

---

## Product rules that the verification pinned down

Four ambiguities in the plan were locked as concrete contracts during execution
(each recorded in `docs/M11_PLAN.md`):

1. **Multi-protein distribution** (§1.12, 2026-10-16): an occurrence whose recipe
   lists several proteins counts toward **each** protein's bar, so the bars may
   sum to more than the occurrence count; `no_primary_protein` covers an
   occurrence whose recipe has no protein rows or whose live recipe is gone.
2. **Per-recipe null semantics** (§1.6, 2026-10-16): each metric is `null` when
   the recipe has no such history — never a fake `0`; write responses return the
   `statistics` object with null metrics.
3. **History wire shape** (§1.26, 2026-10-16): `days` is a **sibling** of `month`
   (`{ range, month{key,label,start,end}, days[], paging }`), matching the shipped
   server response and `docs/API.md` §14.
4. **`never_eaten`** (§1.19, 2026-10-17) counts **all live recipes of any status**
   with no eaten occurrence.

---

## Independent review

Two independent, read-only verification passes were run (the milestone's own
reviewer role), each required to execute the suites itself rather than trust the
implementer's reports.

### Backend review — verdict: **PASS**, no blocker, no major

Verified with evidence: the `meal_count_month` fix and its regression tests
(including a reasoned proof that the new tests fail against the old predicate);
occurrence eligibility against the M5 write-path derivation of
`include_groceries`; retired/tombstone/rename integrity; household-local windows
with no UTC shortcut (`UtcNow`/`DateTime.Now`/`FromDateTime` absent from every
M11 file) and non-UTC boundary tests; the history contract (366-day cap,
`VALIDATION_FAILED` cases, paging walk, `days` sibling) with `docs/API.md`
§14/§23 checked field-by-field against the C# response records via Roslyn; the
permission matrix and the absence of an eighth key; the performance class; and
scope (no migration, no new entity/key/route).

### Frontend review — verdict: **PASS after one major fix**

Verified: contract consumption matches the shipped wire shapes exactly; no fake
zeros; no client date math (`Date`/`toLocale*` absent, so it is correct under the
workstation's invariant globalization); read-only history with no edit/delete
affordance; UI gates mirror edit-implies-view and are never treated as the
authority; server-side filters; cursor paging disabled at the range ends;
colour-independent protein bars; keyboard-operable tabs; 390 px with no
horizontal overflow; live-sync invalidation wired for `calendar.updated` and
`recipe.updated`; no new dependency.

---

## Defects found by the review and fixed

| # | Severity | Defect | Fix | Commit |
|---|----------|--------|-----|--------|
| 1 | **blocker** | `meal_count_month` counted every entry from the first of the month **through December 31**, so later-in-the-year (including planned future) meals inflated it. | Bounded to the inclusive `[monthStart, monthEnd]` household window, with regression tests for the mid-month and year-boundary cases. | `8e5ce08` |
| 2 | major | A query disabled for lack of `recipes.view` still has `isPending === true` in TanStack Query v5, so a member without the permission saw the "not available" alert **and** a permanent "Loading statistics…" spinner. | Render the loading state with `isLoading` (false for a disabled query) and assert the spinner is absent in the permission case. | `523f691` |
| 3 | minor | A `?tab=history` deep link without `calendar.view` left the Overview tab `aria-selected` with an `aria-controls` pointing at a panel that was not rendered (dangling ARIA reference). | The tablist is no longer rendered in that case; a standalone "not available" alert is shown, and every rendered tab's `aria-controls` is asserted to resolve. | `523f691` |
| 4 | minor | An event type omitted from the response rendered as a **fake `0`**. | An absent type renders an explicit absence marker. | `523f691` |
| 5 | minor | The date formatter validated the month but not the day, so `2026-02-30` rendered as "Feb 30, 2026". | Month-aware day bounds (Gregorian leap rule), falling back to the raw string; covered by a new `statisticsFormat.test.ts`. | `523f691` |
| 6 | minor | Statistics/history recipe links sent `recipes.view`-only members to `/recipes/{id}/edit`, which shows a permission-denied page. | The name renders as plain text unless the caller may edit; `useStatisticsAccess.canEditRecipes` mirrors edit-implies-view. | `2eba4cf` |
| 7 | nit | A stale comment claimed the documented §14 history shape was wrong. | Comment corrected to the shipped-and-documented shape. | `2eba4cf` |

Two further review items were accepted as **documented known limitations** rather
than fixed, because each is pre-existing behaviour outside M11's contract:

- **Inactive-tab ARIA:** with two tabs rendered, only the active `tabpanel`
  exists, so the inactive tab's `aria-controls` has no element to point at. This
  matches the existing progress-page tab pattern (single-active-panel rendering);
  rendering both panels would change accepted behaviour and existing assertions.
- **Numeric recipe names in the history filter:** a recipe whose name is purely
  numeric cannot be selected by name, because digits are parsed as an id. The
  contract is documented as "id **or** snapshot name", so this is inherent.

---

## Gates and evidence at completion

Run on the completed commit (see the commit recorded in the manifest). Frontend
gates run from a local mirror under `/tmp/opencode/frontend` (the 9p share times
out Vitest/Playwright workers) with a real local `node_modules`.

| Gate | Result |
|------|--------|
| `dotnet format --verify-no-changes` | clean |
| `dotnet build -c Release FateAndFridge.slnx` | succeeded, 0 warnings / 0 errors |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |
| Backend tests (full Release suite) | 2183 passed / 0 failed / 0 skipped |
| Vitest | 44 files / 420 tests passed |
| Playwright (hermetic) | 67 passed / 3 gated stack skips |
| `npm run lint` / `format:check` / `typecheck` | clean |

### Performance baseline (`docs/TESTING.md` §12)

Measured by `M11StatisticsPerformanceTests` on a seeded baseline of 2,000 recipes
and 10 years of daily meal history (~3,650 entries): overview **8 commands /
111 ms**, one history month **5 commands / 44 ms**, recipes list with embedded
metrics **8 commands / 95 ms**. The 1-second bar is met with wide margin and the
command counts are a small constant that does not grow with the data (no N+1), so
the §1.29 index exception was **not** needed and no migration was added.

---

## Live deployment verification

Rebuild/redeploy was executed by `docker-admin` against the TrueNAS deploy host
(`fatefridge`, `CPlanner@192.168.0.2`, deploy dir `/mnt/Data/calendarplanner`,
app URL `http://192.168.0.2:50080/`) from the completed commit — the documented
`scripts/deploy.sh up` (`docker compose up -d --build`).

**Deployed revision.** The deploy checkout was verified to be the completed
commit: `git -C /mnt/Data/calendarplanner rev-parse --short HEAD` → `141168a`
(clean tree). The NAS build produced frontend bundle `index-BaF50CYt.js`, which
is the asset the live instance serves.

**Build and image.** `dotnet publish` succeeded (17.3 s) and the frontend
`tsc -b && vite build` succeeded (238 modules). The stack was rebuilt to image
**`sha256:e04aaf3b29ffaa5c71c3fec53bd3d5c123ad1a2d9bec58b04ac06262fe8a8aa4`**
(`fate-and-fridge:0.1.0`, created `2026-10-18T08:13:14Z`). This is a locally built
image, so it has **no registry digest** (`RepoDigests` is empty); the image id,
build timestamp, and container id are the identifying evidence.

**Containers.**

```
NAME                          IMAGE                   STATUS                    PORTS
fate-and-fridge-app-1         fate-and-fridge:0.1.0   Up (healthy)              0.0.0.0:50080->8080/tcp
fate-and-fridge-postgres-1    postgres:17-alpine      Up 4 days (healthy)       5432/tcp  (not published)
```

App container id `302ed32d9aa10a56874059bd77d4b89dc15a62bb7c1d6c79d66f862fd1c11b4c`.
PostgreSQL remained running/healthy and **no volume or dataset was touched**;
only port 50080 is published.

**Migrations.** Startup logged "No migrations were applied. The database is
already up to date.", as expected — M11 added none.

**Health.**

- `GET /api/v1/health` → `200`
  `{"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`
- `GET /api/v1/health/db` → `200` `{"status":"ok","database":"connected"}`

**Live M11 proof** (single-user/configured instance; real household data):

- `GET /api/v1/statistics/overview` → `200` — household-local windows
  (`today 2026-10-18`, `America/Chicago`, `October 2026`), `most_eaten` with
  `Chicken Enchiladas` eaten 3× (last `2026-10-16`), `meal_count_month` 23,
  `meal_count_year` 23, `protein_distribution` `{Beef 3, Chicken 3, Pork 1}` with
  `no_primary_protein` 1, and the seven `special_event_counts`
  (`eat_out` 1, `host_family_dinner` 2, `go_to_family_dinner` 2, …).
- `GET /api/v1/statistics/history?month=2026-10` → `200` — default range
  `2025-11-01 … 2026-10-18` (the last 12 months), `month` metadata with
  **`days` as a sibling**, newest-first days ending at `2026-10-17` (strictly
  past — today is `2026-10-18`), a `host_family_dinner` carrying a recipe with
  protein `Beef`, and a recipe with no protein (`protein: null`).
- `GET /api/v1/statistics/recipes/2` → `200`
  `{"last_eaten":"2026-10-16","next_planned":"2026-10-18","times_eaten":3}` —
  today's occurrence is **planned, not eaten**, as specified.
- `/progress` — rendered live in Chromium against the deployed instance: the
  summary reads `12 of 15 milestones complete (80%).`, and the M11 card shows
  `M11 — Statistics and History`, `Complete`, `Completed 2026-10-18`. The served
  bundle hash matches the fresh build output.

**Runbook:** `git tag m11-complete` (annotated, "M11 — Statistics and History
complete (2026-10-18)") is cut on the commit that records this verification.

---

## CI status after completion

_Pending the push of the completion commits to `main`; the M9/M10 precedent
(`BUG-009`/`BUG-010`) required a green CI run on `main` before the milestone was
declared done, and the same check is recorded there._
