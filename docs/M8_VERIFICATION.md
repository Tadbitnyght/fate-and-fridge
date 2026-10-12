# M8 Verification — Groceries

Status: **complete** (2026-10-12, tag `m8-complete`). Tracking issue: #106
(M8-01…M8-10 are #107–#116). The milestone is marked `complete` in the progress
manifest and the live deployment was rebuilt, redeployed, and verified (see
**Live deployment verification** below).

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

Recorded on the M8-10 commit chain (branch `feature/m8-groceries`, HEAD
`3478082`). The milestone was marked `complete` in `80bc722`, which is the
deployed/verified commit; the `m8-complete` annotated tag points at the final
verification commit after the live checks below.

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

Verified on 2026-10-12 after rebuilding and redeploying via `docker-admin`
using `scripts/deploy.sh up` (`docker compose up -d --build` on the NAS).

- **Deploy-host checkout:** `/mnt/Data/calendarplanner` at `80bc722` on `main`,
  working tree clean. `/mnt/y` is the 9p SMB export of the same NAS repository
  (identical tree object `2443a859…`), so there is one checkout. The NAS git
  needed a one-shot `-c safe.directory=…`; no persistent config was written.
- **Image `fate-and-fridge:0.1.0`:** `sha256:2a114cc61c447709318c652ac1753ece672819679832ae45a5d30faf7b08f65c`,
  created `2026-10-12T13:02:59Z`. A repeat `scripts/deploy.sh up` at 13:15Z was a
  full cache hit with no container recreation (idempotent), so the running image
  is the 13:02:59Z build of this commit; the served bundle independently proves
  it carries the M8-10 manifest change (below).
- **Migrations applied:** `Applying migration '20261010050705_M8Groceries'.` then
  `Database migrations applied`; `__EFMigrationsHistory`'s last row is
  `20261010050705_M8Groceries`. All four M8 tables exist
  (`grocery_staples`, `manual_grocery_items`, `grocery_check_state`,
  `user_grocery_preferences`).
- **Containers:** `fate-and-fridge-app-1` `Up (healthy)` (`restarts=0`),
  `fate-and-fridge-postgres-1` `Up (healthy)`.
- **Health:** `GET /api/v1/health` → `200 {"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`;
  `GET /api/v1/health/db` → `200 {"status":"ok","database":"connected"}`.
- **Live route proof:** `GET /api/v1/groceries` → `200` with the full projection
  (`{"range":{"preset":"next_7_days","start":"2026-10-12","end":"2026-10-18","day_count":7},…}`;
  the household is single-user, so the system actor is authorized). An unknown
  route returns `404` as the baseline.
- **Frontend bundle:** `/assets/index-lov0dLqD.js` (hash changed from M7's
  `index-azB5w2ge.js`). It contains the M8 UI strings (`Add grocery item`,
  `Include checked items`, `Add to list`, `next_14_days`,
  `GROCERY_RANGE_TOO_LARGE`) and the M8 completion date `2026-10-12`.
- **Live browser verification** (read-only Playwright, no mutations):
  - `/progress` renders `9 of 15 milestones complete (60%).` with the
    **M8 — Groceries** card showing **Complete** / `Completed 2026-10-12` and
    M9–M14 as Planned.
  - `/groceries` renders the real aggregation UI (range presets with Next 7 days
    active, the range readout `Oct 12 – Oct 18 · 7 days`, 26 items across the 8
    configured departments in order, per-item attribution such as
    `Chicken Enchiladas — Fri 10/16`, item checkboxes, the hide-checked control,
    and the Print entry point) — not a placeholder or error state, with 0
    console errors.
  - `/groceries/print` renders `Grocery list: Oct 12 – Oct 18`, 26 `aria-hidden`
    paper checkboxes, departments in order, and the "Include checked items"
    control; under print-media emulation the header, nav, footer, and
    screen-only controls are hidden while the list stays visible.
  - Mobile 375×667 keeps the department headers sticky (`position: sticky`,
    pinned on scroll) with ≥44px grocery controls.
  - Screenshots: `.playwright-mcp/m8-progress-desktop.png`,
    `m8-groceries-desktop.png`, `m8-groceries-print-desktop.png`,
    `m8-groceries-mobile.png`.
- **Second redeploy (tag metadata):** after `7537c78` added the
  `tag: 'm8-complete'` / `commit: '80bc722'` manifest metadata (docs-only + the
  manifest), the frontend layer was rebuilt (a real `RUN npm run build`, not a
  cache hit) to image
  `sha256:a773cd21318f…9c01b47` (created `2026-10-12T13:45:52Z`), replacing the
  app container; postgres was not recreated. `GET /api/v1/health` → `200` and
  `GET /api/v1/groceries` → `200`. The served bundle is now
  `/assets/index-AIN_3mTH.js` (previously `index-lov0dLqD.js`) and contains the
  string `m8-complete`, so the live `/progress` page carries the M8 tag metadata.
- **PostgreSQL** still publishes no host port (`PortBindings {}` for the postgres
  container); only the app is exposed on `50080`.
- **Non-destructive:** no volumes, datasets, backups, or containers were deleted;
  the only mutating command was the sanctioned `up`, and no M8 mutating endpoint
  was called during verification.

Observations (non-blocking): `/favicon.ico` returns `404` (pre-existing and
cosmetic); one live grocery line reads `Onion {owder` (a stored recipe-ingredient
label, almost certainly user/seeded data rather than an M8 rendering defect —
recorded as `BUG-008` for a data/code check).
