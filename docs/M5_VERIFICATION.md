# M5 Verification — Calendar Core

Status: **complete** (2026-10-05, tag `m5-complete`)
Runbook: `docs/M5_PLAN.md` (locked decisions, schema, contract, §4 battery)
Tracking issue: #65 (M5-01…M5-14: #66–#79)

M5 adds the `calendar_entries` and `calendar_entry_history` tables and the
`household_settings.historical_boundary_date` column (migration
`M5CalendarCore`). No other schema changes.

## How to run the battery

```bash
export DOTNET_ROOT=/home/tadbit/.dotnet FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend && dotnet build -c Release && dotnet test -c Release --no-build
dotnet format --verify-no-changes
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
```

Frontend Vitest/Playwright workers time out on the 9p share `/mnt/y`; run them
from a local mirror of `frontend/` under `/tmp/opencode/` (sync changed files
first):

```bash
npx prettier --check . && npm run lint && npm run typecheck && npx vitest run && npm run build
npx playwright test
```

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Timezone / historical (midnight rollover, DST spring/fall, offset changes, client-tz independence) | `HouseholdClockTests` |
| Frozen boundary across a timezone change (east and west; history stays read-only) | `CalendarEndpointsIntegrationTests.Frozen_boundary_*`, `HouseholdClockTests` |
| Historical immutability (PUT/DELETE/lock/unlock rejected on past for Admin/viewer) | `CalendarEndpointsIntegrationTests` |
| Permission matrix (`calendar.view`/`calendar.edit`, none, unauthenticated, single-user) | `CalendarEndpointsIntegrationTests.Permission_matrix_*` |
| Entry type validation, `include_groceries`, `blocks_automation`, misc label, recipe required, diner range, icon catalog | `CalendarEndpointsIntegrationTests` |
| Lock (blank, past, manual-edit-retains-lock, delete removes) | `CalendarEndpointsIntegrationTests` |
| Snapshots (rename refresh future, freeze past; rename never touches frozen history) | `CalendarEndpointsIntegrationTests` |
| Repeat warning (7 back / 14 forward boundaries, non-blocking) | `CalendarEndpointsIntegrationTests.Repeat_check_*`, Vitest + Playwright |
| Range/horizon caps (366 days, ±5 years) and date parsing | `CalendarEndpointsIntegrationTests.Range_reads_*` |
| No-write reads; one history row per write; atomic entry+history | `CalendarEndpointsIntegrationTests.Delete_removes_*` and history counts |
| UI (ARIA grid keyboard nav, focus, read-only historical, mobile agenda, print, week start) | `CalendarPage.test.tsx`, `calendarDates.test.ts`, `e2e/calendar.spec.ts` |

## Evidence at completion

- Backend tests: **1642 passed**.
- Frontend Vitest: **238 passed**.
- Playwright (hermetic): **37 passed / 2 pre-existing stack skips**.
- Backend gates: `dotnet format --verify-no-changes` clean, Release build clean,
  `ef migrations has-pending-model-changes` → none.
- Independent review: the M5 review found no Blocker; its Major/Minor findings
  (frozen-snapshot refresh on rename, household week-start alignment, recipe
  picker card fields, upsert atomicity, repeat warning for special types) were
  fixed on-branch with regression tests (`A_recipe_rename_never_touches_frozen_history_*`,
  `starts_the_week_on_the_household-configured_day`).

## Known limitations (deferred, per docs/M5_PLAN.md §6)

- Randomizer, reroll, undo, batch planning, grocery aggregation, live sync,
  statistics/history browser, audit viewer, and historical correction are M6–M12.
- Single-date write concurrency is last-write-wins; M7 adds batch concurrency
  protection. A concurrent create race on the same date is not specially handled.
- Delete history rows store a null `calendar_entry_id` (they survive the hard
  delete); M6 undo correlates by `meal_date`/`before_json`.
- Serving scaling by diner count is V2; V1 stores diner count as metadata only.

## Live deployment verification (Milestone completion gate)

Recorded retroactively after the M4/M5 milestone-completion gate was added
(`AGENTS.md`). Rebuilt and redeployed via `docker-admin` using
`scripts/deploy.sh up` (`docker compose up -d --build` on the NAS).

- Image `fate-and-fridge:0.1.0`: before `sha256:6d155f76…` (created 2026-10-03,
  pre-M4/M5) → after `sha256:20ba2a53…` (created 2026-10-06T18:00:10-05:00).
- Migrations applied on startup: `Applying migration '20261006022730_M5CalendarCore'`
  then `Database migrations applied`.
- Health: `{"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`.
- Live route proof: `GET /api/v1/calendar/2026-11-15` returns `200` with a real
  day document (route present, not 404); the served bundle contains
  `Calendar Core` and `m5-complete`.
- Live `/progress`: **"6 of 15 milestones complete (40%)"**, with M5 — Calendar
  Core shown Complete.
- The household is in **single-user** mode (`authenticated: true`, system actor
  with all permissions), so requests without a session are authorized by design.
