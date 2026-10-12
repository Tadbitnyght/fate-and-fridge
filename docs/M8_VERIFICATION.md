# M8 Verification — Groceries

Status: **pre-deploy gates green** (2026-10-12, branch `feature/m8-groceries`,
HEAD `3478082`). Tracking issue: #106 (M8-01…M8-10 are #107–#116). The milestone
is marked `complete` in the progress manifest; the `m8-complete` tag and the
live-deployment verification are performed by the Lead/docker-admin after the
redeploy (see **Live deployment verification** below).

M8 delivers the **live grocery projection**: a read-only aggregate of the
planned recipe ingredients in the active range plus manual items, grouped by
department, with per-meal attribution, household-shared check state, reusable
staples, per-user preferences, and a print view. It is not a persisted calculated
list.

## Schema, entities, and routes

`M8Groceries` (`20261010050705_M8Groceries`) is the **only** M8 migration. It
creates four tables:

- `grocery_staples`
- `manual_grocery_items`
- `grocery_check_state`
- `user_grocery_preferences`

(`grocery_departments` is **not** created by M8 — that table and its
`GroceryDepartment` entity predate M8 from `M2ReferenceDataBaseline`. M8 adds the
`department_id` associations on staples/manual items pointing at it.) M8 model
types: `GroceryDepartment` (M2), `GroceryStaple`, `ManualGroceryItem`,
`GroceryCheckState`, `UserGroceryPreference`.

Routes (`src/FateAndFridge.Api/Endpoints/GroceryEndpoints.cs`, group
`/api/v1/groceries`):

| Route(s) | Permission |
|----------|------------|
| `GET /groceries` | `groceries.view` |
| `POST /groceries/manual`, `PATCH /groceries/manual/{id}`, `DELETE /groceries/manual/{id}` | `groceries.edit` |
| `GET /groceries/preferences`, `PATCH /groceries/preferences` | `groceries.view` |
| `GET /groceries/staples` | `groceries.view` |
| `POST /groceries/staples`, `PATCH /groceries/staples/{id}`, `POST /groceries/staples/{id}/add-to-list` | `groceries.edit` |
| `POST /groceries/check`, `POST /groceries/uncheck` | `groceries.edit` |

`groceries.edit` implies `groceries.view`; the single-user system actor has full
access. Contracts are recorded in `docs/API.md` §13 and `docs/DATABASE.md` §9.

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet format --verify-no-changes
dotnet build -c Release FateAndFridge.slnx
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
# Full suite (rebuild first; run the binary directly — do not use `dotnet test`):
$ARTIFACTS
# Or the M8 battery only:
$ARTIFACTS \
  -class "FateAndFridge.Api.Tests.M8GroceryMatrixIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8GroceryListIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8ManualItemAndPreferenceIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8StapleAndCheckStateIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8GroceriesSchemaIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8GroceriesSchemaModelTests" \
  -class "FateAndFridge.Api.Tests.GroceryAggregationServiceTests" \
  -class "FateAndFridge.Api.Tests.GroceryItemIdentityTests"
```

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers): `npm run lint && npm run format:check
&& npm run typecheck && npm test`, then `npm run build`, then
`npx playwright test`.

M8 integration fixtures share one Testcontainers/PostgreSQL container, inject a
deterministic `TestTimeProvider`, and set the household timezone to
`America/Chicago` so household-local "today" is distinct from UTC.

## §4 battery → coverage

The exhaustive per-area coverage map is **`docs/M8_TEST_MATRIX.md`** (M8-09) —
it lists every §4 battery bullet from `docs/M8_PLAN.md` and its primary
automated test(s), backend first, then frontend, and records the gaps closed in
the M8-09 sweep. Reproduced compactly:

| §4 area | Primary coverage (see `docs/M8_TEST_MATRIX.md` for the full list) |
|---------|--------------------------------------------------------------------|
| Aggregation (compatible combine, incompatible separate, counts, aliases→canonical) | `GroceryAggregationServiceTests`, `GroceryItemIdentityTests`, `M8GroceryListIntegrationTests`, `M8GroceryMatrixIntegrationTests` |
| Scope (only `include_groceries` contributes; specials with a recipe do) | `M8GroceryListIntegrationTests`, `M8GroceryMatrixIntegrationTests.Entry_type_scope_matrix` |
| Display (largest present unit, exact fractions else 2 decimals, quantity-less, attribution) | `GroceryAggregationServiceTests`, `M8GroceryListIntegrationTests`, `M8GroceryMatrixIntegrationTests` |
| Ranges (3/7/14/rest-of-month/custom, past rejected, >366 rejected, remembered range, default Next 7 Days) | `M8GroceryListIntegrationTests`, `M8GroceryMatrixIntegrationTests`, `M8ManualItemAndPreferenceIntegrationTests` |
| Manual/staples (range-scoped, department default, Save for future, add-to-list, household-wide, separate lines) | `M8ManualItemAndPreferenceIntegrationTests`, `M8StapleAndCheckStateIntegrationTests`, `M8GroceryListIntegrationTests`, `M8GroceryMatrixIntegrationTests` |
| Check state (persists across quantity/range, dimensions don't collapse, household-shared, ordering, hide-checked per-user) | `M8StapleAndCheckStateIntegrationTests`, `GroceryItemIdentityTests`, `M8GroceryListIntegrationTests`, `M8GroceryMatrixIntegrationTests`, `M8ManualItemAndPreferenceIntegrationTests` |
| Permissions (`groceries.view`/`groceries.edit` matrix, member 403, single-user full access) | `M8GroceryListIntegrationTests`, `M8ManualItemAndPreferenceIntegrationTests`, `M8StapleAndCheckStateIntegrationTests`, `M8GroceryMatrixIntegrationTests` |
| Timezone (range/manual validation uses household-local today) | `M8GroceryMatrixIntegrationTests`, `M8ManualItemAndPreferenceIntegrationTests` |
| UI (mobile tap targets/sticky headers, checkbox UX, hide-checked, manual add/edit, staples) | `frontend/src/features/groceries/GroceriesPage.test.tsx`, `StaplesPanel.test.tsx`, hermetic `e2e/groceries.spec.ts` |
| Print (department order, checkbox, attribution, manual items, include-checked) | `frontend/src/features/groceries/GroceryPrintPage.test.tsx`, hermetic `e2e/groceries.spec.ts` |

## Evidence at completion

Recorded on the M8-10 commit (branch `feature/m8-groceries`, HEAD `3478082`).

| Gate | Result |
|------|--------|
| `dotnet format --verify-no-changes` | clean (exit 0) |
| Backend Release build (`FateAndFridge.slnx`) | 0 warnings / 0 errors |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |
| Backend full suite (`FateAndFridge.Api.Tests`) | **Total 2029, Errors 0, Failed 0, Skipped 0, Not Run 0** (1122.7 s) |
| Frontend lint / format:check / typecheck (local mirror) | clean |
| Vitest | **315 passed** (34 files) |
| Playwright (hermetic, `npx playwright test`) | **47 passed / 2 pre-existing stack skips** |
| `npm run build` | succeeds (pre-existing >500 kB chunk warning only) |

No stale full-suite expectation failed: `M2SchemaBaselineModelTests.
All_expected_entities_are_mapped` already lists the M8 model types
(`GroceryStaple`, `ManualGroceryItem`, `GroceryCheckState`,
`UserGroceryPreference`, and the M2 `GroceryDepartment`), so no test fix was
needed.

Independent review outcome: the M8 backend and UI were reviewed per issue on the
branch. The branch carried one follow-up aggregation fix in commit `520865d`
(`fix(groceries): handle zero, nonconvertible, and overflow quantities in
aggregation (#108)`) with 147 lines of regression tests covering the zero,
non-convertible, and overflow quantity cases. The M8-09 exhaustive battery
(`docs/M8_TEST_MATRIX.md`) then found **no §4 battery failure and no remaining
product defect**, so no further corrective commit was required. The one
structural note is that preparation-note exclusion from aggregation identity is
enforced by the absence of a note field on `GroceryIngredientInput` rather than
by a runtime assertion.

## Live deployment verification

**Pending** — to be completed by the Lead/docker-admin after the M8 redeploy
(`scripts/deploy.sh up` on the deploy host, applying `M8Groceries`). Expected
content:

- deploy-host checkout commit and clean tree
- `fate-and-fridge:0.1.0` image id before → after
- `Applying migration '…_M8Groceries'` and `Database migrations applied`
- `GET /api/v1/health` → `200 {status: ok}`
- live route proof: at least one `GET /api/v1/groceries` response (401
  `AUTH_REQUIRED` for an anonymous probe is sufficient evidence the route is
  served; an unknown route returns 404 as a baseline)
- served bundle hash containing `Groceries`/the M8 UI strings, and `/progress`
  rendering M8 as Complete (summary 9 of 15, computed client-side)
- PostgreSQL still unpublished; no volumes/datasets deleted
