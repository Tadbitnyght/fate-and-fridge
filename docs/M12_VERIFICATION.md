# M12 Verification — Administration

Status: **complete** (live deployment verified 2026-10-19)
Runbook: `docs/M12_PLAN.md` (approved 2026-10-18). Coverage map:
`docs/M12_TEST_MATRIX.md`. Tracking: #149; M12-01…M12-10: #150–#159. The V1.x
date-display change (#160) ships with this deploy.

---

## What shipped

- **Audit coverage** — `audit_events` now records calendar meal changes and
  lock/unlock, recipe lifecycle (incl. the retire transition), users and
  permission changes, household/security settings (incl. the auth-mode switch),
  grocery manual items and staples, successful/failed logins, historical
  corrections, and hard deletes, with backup/restore stub types for M13.
  Reference-data events already shipped from M2. Rerolls deliberately stay in
  `calendar_entry_history` (they carry score metadata there); no page views and
  no secrets are logged.
- **Admin audit log** (`/settings/audit`, admin-only) — a newest-first **merged**
  timeline of `audit_events` and the reroll rows of `calendar_entry_history`
  (kind → event type, `source` discriminator), with event-type/entity-type/
  actor/source/date filters, keyset paging, expandable before/after detail, and
  **CSV/JSON export** of the filtered view (quoting + formula-injection safety,
  100k cap).
- **Retention** — `audit_retention_days` (`0` = keep forever, else `7..3650`,
  default 365) with an Admin-confirmed, age-based **prune** that deletes only
  `audit_events` older than the cutoff and records `audit.pruned`.
- **Historical correction** — `POST /calendar/{date}/correction` (admin-only):
  a single **past** date, full edit, reason + typed-date confirmation, locked
  dates allowed with an explicit acknowledgement (and left locked), atomic
  `calendar_entry_history` (`is_historical_correction = true`) + audit rows, and
  a refreshed frozen snapshot. UI reachable from the date-detail dialog and the
  meal-history browser (admin-only).
- **Hard recipe delete** — `DELETE /recipes/{id}` (admin-only): durable snapshot
  backfill, entries dated today or later removed, **past meals tombstoned**
  (`recipe_id` cleared, snapshots preserved, `include_groceries` still true so
  statistics and meal history keep their counts), image rows **and files**
  removed, `recipe.hard_deleted` audit row with the recipe before-image, no
  broken foreign keys, irreversible.
- **Shared dangerous-action dialog** — one accessible, focus-trapped dialog
  (reason, warnings, acknowledgement, typed confirmation) used by correction,
  hard delete and prune, and reused by M13's restore; the reason and confirmation
  are verified **server-side** by `DangerousActionGuard`.
- **V1.x date display (#160)** — product UI dates render `MM-DD-YY` through one
  shared formatter; the API wire stays ISO.

**No schema change, no migration, no new permission key** — audit read,
correction and hard delete are Admin-only on the existing seven-key model.

---

## Independent review

Two focused, read-only verification passes were run (each required to execute the
suites itself). A single full-milestone pass exceeded its step budget, so it was
split by scope; the split reviewers' open items were closed directly by the Lead
(recorded below).

### Audit half (coverage, viewer, export, retention/prune) — **PASS**

Verified: exactly one audit row per covered write with actor/target/before-after;
**no secret** reaches any audit column (producer inspection plus the persisted-
payload scan); rerolls are absent from `audit_events`; no page views logged; a
failed audit write aborts the change. The merged viewer excludes ordinary edits,
orders by `recorded_at desc, source desc, id desc` with a matching keyset cursor
(no gaps/duplicates), AND-combines filters, resolves the actor by join inside one
bounded query (**no N+1**; `limit=1` and `limit=200` both take one command), and
rejects malformed input. Export quoting and `= + - @ tab CR` neutralisation are
correct, the cap returns `VALIDATION_FAILED` rather than truncating, and JSON
equals the viewer rows. Prune deletes only `audit_events` rows strictly older than
the cutoff, keeps a row exactly at the cutoff, writes `audit.pruned` after the
delete in the same transaction, refuses "keep forever", and enforces the reason +
typed `PRUNE` server-side. All three admin routes are `RequireAdmin()`; the
permission-key count is unchanged; no pending model changes.

Suites run by the reviewer: `*M12Audit*` + `*DangerousActionGuard*` → **91 passed,
0 failed**; `*PermissionMatrix*`/`*PermissionKeys*`/`*AuthEndpoints*`/
`*HouseholdSettings*`/`*SecuritySettings*` → **228 passed, 0 failed**. Plus
`dotnet format` clean, Release build 0/0, `ef migrations has-pending-model-changes`
empty.

**Open item the reviewer could not confirm** — the `audit_retention_days` range
validation. **Closed by the Lead:** `SecuritySettingsService.IsValidAuditRetentionDays`
is `value == 0 || (value >= 7 && value <= 3650)` and `PATCH /settings/security`
rejects anything else with `audit_retention_days` field errors, so a prune can
never observe a value in `1..6`.

### Correction + hard-delete half — **PASS on the reviewed paths**

Verified: the correction is past-only (`CALENDAR_DATE_NOT_PAST`) while ordinary
endpoints still reject past dates; a locked date needs the acknowledgement and
`ApplyFields` never touches `IsLocked`; the reason/typed-date confirmation is
enforced server-side; **no fork/drift** — `ValidateWriteRequestAsync`,
`ApplyFields`, `StageHistoryRow` and friends each have exactly one definition and
are called by both the normal write and the correction; the snapshot is refreshed
at correction time; entry + history + audit rows share one transaction under a
per-date advisory lock. Hard delete preserves the M11 invariants (tombstoned past
entries keep snapshots and `include_groceries`, statistics keep counting, the
tombstone renders `status: deleted`, and a hard-deleted recipe correctly drops out
of `never_eaten`), removes today/future references, backfills snapshots **only
when missing/blank** (a stale snapshot is never overwritten — the M5 freeze rule),
removes image rows and files, writes the `recipe.hard_deleted` before-image, and
leaves no orphans. Both routes are `RequireAdmin()`; no eighth key; no migration.

Suites run by the reviewer: `*M12HistoricalCorrection*` + `*M12RecipeHardDelete*`
→ **19 passed, 0 failed**; regression batch (`*M11Statistics*`, `*M11MealHistory*`,
`*CalendarEndpoints*`, `*RecipeEndpoints*`, `*RecipeDocument*`,
`*RecipeImageEndpoints*`) → **121 passed, 0 failed**.

**Open items closed by the Lead** (the reviewer ran out of budget before reading
the test bodies or exercising concurrency):

- The M12 test bodies do assert the invariants they are named for — confirmed by
  inspecting the test names and contents: `Past_meals_survive_the_hard_delete_with_the_snapshot_and_statistics_intact`,
  `Snapshots_are_backfilled_when_missing_and_a_stale_snapshot_still_renders`,
  `Audit_row_retains_the_before_image_and_counts_and_a_forced_failure_rolls_back`,
  `Locked_past_date_needs_acknowledgement_and_stays_locked`,
  `History_and_audit_commit_atomically_and_a_forced_audit_failure_rolls_back`,
  `A_correction_racing_a_batch_apply_does_not_500`, and the admin-only matrices.
- The M11 tombstone contract (`docs/M11_PLAN.md` §1.5/§1.20) matches the
  implementation: `MealOccurrenceRules.IsRecipeOccurrence` is `IncludeGroceries`
  and `GroupKey` falls back to the normalized snapshot name when `recipe_id` is
  null.
- **Concurrency verdict:** the residual **reroll/batch-apply racing a hard
  delete** is judged an acceptable V1 limitation — the request transaction rolls
  back, so nothing is corrupted or half-deleted, and it needs a deliberate race
  between an Admin deleting a recipe and a planner rerolling that instant. It is
  recorded as **`BUG-011`** (issue #161) with the suggested fix (re-validate
  candidates inside the reroll transaction, or map the FK violation to a
  conflict), rather than silently accepted.

### Regression caught by the full-suite run and fixed

| Severity | Finding | Disposition |
|----------|---------|-------------|
| **major (caught, fixed)** | The M9 completeness guard (`M9LiveSyncEventMatrixTests.Event_matrix_covers_every_mutating_route`) failed because M12 added three mutating routes without deciding their live-sync emission: `POST /calendar/{date}/correction`, `DELETE /recipes/{id}`, `POST /admin/audit/prune`. | Fixed in `c7843a3`: the correction and hard delete are listed as **covered** (both really do publish — verified in `CalendarEndpoints.CorrectionAsync` and `RecipeEndpoints.HardDeleteRecipeAsync`, and by their integration tests' live-sync assertions), and the audit prune is **excluded with a stated reason** (audit-log maintenance; `LiveSyncEventType` has no audit entity, so the viewer refetches). The guard itself was not weakened. The class re-runs green (45 passed). |

### Findings accepted as limitations (not completion-blocking)

| Severity | Finding | Disposition |
|----------|---------|-------------|
| minor | A user PATCH that changes nothing still writes a `user.renamed` row (`before == after`) | Accepted — a real request, and the row records actor/reason. Noted here and in the matrix. |
| minor | `AuditLogQueryService.QueryAsync` returns early on the first invalid filter, so simultaneous `limit`/`cursor` errors are not all reported | Accepted — cosmetic; all invalid inputs are still rejected. |
| nit | Export materializes up to the cap + 1 rows to detect overflow | Accepted — correct; a `COUNT(*)` pre-check is a possible later optimisation. |
| nit | `AtStartOfLocalDay` uses `GetUtcOffset(localWallTime)`; a DST transition exactly at local midnight would use the standard offset | Accepted — no impact for the default `America/Chicago`. |
| note | `AuditService` does not scrub secrets itself; the invariant is per-producer | Accepted, mitigated by the persisted-payload secret scan; revisit if many more emitters are added. |

---

## Gates and evidence at completion

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers) with a real local `node_modules`.

| Gate | Result |
|------|--------|
| `dotnet format FateAndFridge.slnx --verify-no-changes` | clean |
| `dotnet build -c Release FateAndFridge.slnx` | succeeded, 0 warnings / 0 errors |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |
| Backend tests (full Release suite) | **2293 passed / 0 failed / 0 skipped** (the earlier run's single failure — the M9 route-matrix guard — was fixed in `c7843a3`) |
| Vitest | 48 files / 489 tests passed |
| Playwright (hermetic) | 70 passed / 3 gated stack skips |
| `npm run lint` / `format:check` / `typecheck` | clean |

---

## Live deployment verification

Rebuild/redeploy was executed by `docker-admin` against the TrueNAS deploy host
(`fatefridge`, `CPlanner@192.168.0.2`, deploy dir `/mnt/Data/calendarplanner`,
app URL `http://192.168.0.2:50080/`) from the completed commit, via the
documented `scripts/deploy.sh up` (`docker compose up -d --build`).

**Deployed revision.** The deploy checkout was verified to be the completed
commit: `git -C /mnt/Data/calendarplanner rev-parse --short HEAD` → `820a20c`
(clean tree). The fresh build produced the SPA bundle `index-CKAWJziJ.js`
(`index-OIqghAI6.css`), which is exactly the asset the live instance serves.

**Build and image.** The frontend build and the backend publish succeeded; the
stack was rebuilt to image
**`sha256:99a114cd1c7cce5aebb33b75901718ccfdd66c120051f3978076686730e6757f`**
(`fate-and-fridge:0.1.0`). This is a locally built image, so it has **no registry
digest** (`RepoDigests` is empty); the image id, build output, and container id
are the identifying evidence.

**Containers.**

```
NAME                          IMAGE                  STATUS                 PORTS
fate-and-fridge-app-1         fate-and-fridge:0.1.0  Up (healthy)           0.0.0.0:50080->8080/tcp
fate-and-fridge-postgres-1    postgres:17-alpine     Up 5 days (healthy)    5432/tcp  (not published)
```

App container id `5b7559ee00683a0da6def4a6062e94b8975323b0986cc2d02cde707117e33f03`.
PostgreSQL stayed running/healthy and **no volume or dataset was touched**; only
port 50080 is published.

**Migrations.** Startup logged "No migrations were applied. The database is
already up to date.", as expected — M12 added none.

**Health.** `GET /api/v1/health` → `200`
`{"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`;
`GET /api/v1/health/db` → `200` `{"status":"ok","database":"connected"}`.

**M12 live proof (read-only; nothing destructive was performed).**

- `GET /api/v1/admin/audit?limit=5` → **200** with real rows: the newest are
  `source: "entry_history"`, `event_type: "calendar.batch_reroll"` (ids 70→66,
  recorded `2026-10-17T16:04:25Z`, system actor) carrying full before/after JSON
  and the M7 metadata — i.e. the merged viewer really does surface reroll records
  from the entry history. `next_cursor` is present (keyset paging is live).
- `GET /api/v1/admin/audit/export?format=csv` → **200**,
  `Content-Type: text/csv; charset=utf-8`,
  `Content-Disposition: attachment; filename=audit-log.csv; filename*=UTF-8''audit-log.csv`
  (136,920 bytes), header line:
  `id,source,recorded_at,actor_user_id,actor_username,event_type,entity_type,entity_id,summary,reason,ip_address,before,after,metadata`.
- **The new mutating routes are live and their guards refuse unsafe calls**
  (deliberately non-destructive — a real correction/delete/prune was **not**
  performed on live household data):
  - `POST /api/v1/calendar/{past}/correction` with `{}` → **400
    `REASON_REQUIRED`** (`field_errors.reason`); with an empty body → 400
    `VALIDATION_FAILED`.
  - `DELETE /api/v1/recipes/1` with a wrong `confirmation` → **400
    `CONFIRMATION_MISMATCH`** (`field_errors.confirmation`).
  - `POST /api/v1/admin/audit/prune` with a wrong confirmation → 400
    `CONFIRMATION_MISMATCH`; without a reason → 400 `REASON_REQUIRED`. No valid
    `PRUNE` confirmation was ever sent.
  - **Nothing changed:** `GET /api/v1/recipes/1` returned a byte-identical
    document (still `Bacon-Wrapped Hot Dogs`, still 5 recipes), the calendar
    entry for the probed date was untouched, and the row counts were stable
    (`audit_events` 56, `calendar_entry_history` 70, max ids unchanged) with **no**
    `calendar.historical_correction`, `recipe.hard_deleted`, or `audit.pruned` row
    written.
- `/progress` — rendered live in Chromium against the deployed instance: the
  summary reads **`13 of 15 milestones complete (87%).`**, no milestone reports
  "In progress", and the **`M12 — Administration`** card shows the **Complete**
  status chip with **`Completed 2026-10-19`**. The served bundle hash matches the
  fresh build output.

**Runbook:** `git tag m12-complete` (annotated, "M12 — Administration complete
(2026-10-19)") is cut on the commit that records this verification.

---

## CI status after completion

**Post-completion CI incident (found and fixed on `main`).** The first CI run on
the completion commits (run `39234859827`, commit `ebcaf0a`) was **red**: the
backend, format and compose jobs passed, but the frontend job failed
`src/features/progress/futureFeatures.test.ts` —
*"missing manifest entry for section 'Administration & audit expansion'"*. The
guard asserts that **every** `##` section of `docs/FUTURE_FEATURES.md` has a
matching entry (with the same status) in the offline
`frontend/src/features/progress/futureFeatures.ts` manifest, and M12 added two
registry sections (`Administration & audit expansion` → V2 and, from the M12-01
session, `Per-user & device preferences` → V1.x) without their manifest entries.
The stack-acceptance job was consequently skipped.

This was masked locally because the frontend test mirror held only `frontend/`,
so `resolve(process.cwd(), '../docs/FUTURE_FEATURES.md')` did not exist and the
test **skipped with a warning** (the test documents that pitfall). The mirror now
also carries `docs/`, so the guard runs locally from now on.

**Fix:** `1ed2e09` adds both manifest entries (`v2` / `v1x`) with summaries and
provenance. Verified locally in a repo-shaped mirror: `futureFeatures.test.ts`
3/3 with no skip warning, full Vitest 48 files / 489 tests, and
lint/format/typecheck clean.

**Tag/commit note:** the annotated tag `m12-complete` was cut on `ebcaf0a` (the
verification commit, the M9/M10 convention) *before* CI reported, so the tag
points at the commit that carried the pre-fix registry gap. The milestone's
feature content, the manifest's recorded completion commit (`820a20c`) and the
live deployment (image `sha256:99a114cd…`, built from `820a20c`) are unaffected —
the fix only adds offline registry entries that have no runtime behaviour.
Following the `BUG-009`/`BUG-010` precedent, the incident is recorded here rather
than hidden; the tag was left in place.

**Post-completion CI incident 2 (found and fixed on `main`).** After the registry
fix, CI (run `39238281072`, commit `1ed2e09`) passed the frontend, backend and
compose jobs but the **stack-acceptance** job failed one Playwright spec:
`e2e/settings.spec.ts` › *"admin saves the IP allowlist with the lockout
confirmation"* timed out waiting for "Security settings saved.".

Cause: that spec keeps its **own inline** security payload (it does not use
`src/test/fixtures.ts`), and the inline payload predated M12-04's
`audit_retention_days`. With the field absent, the form initialised the retention
input as `"undefined"`, client-side validation rejected it, and the submit was
blocked — so the success message never rendered. This only shows up when the
spec runs against the real contract; the earlier local full suite had passed
because the mirror's copy of that spec predated the change.

**Fix:** `98b3985` adds `audit_retention_days: 365` to that inline payload so it
matches the shipped contract. Verified locally: the spec 4/4, the full hermetic
suite **70 passed / 3 gated stack skips**, Vitest 48 files / 489 tests.

**Process note (adopted):** frontend verification now runs from a **repo-shaped**
mirror (`frontend/` **and** `docs/`), so the registry-parity guard cannot silently
skip, and e2e specs with inline fixtures are treated as contract duplicates that
must be updated with any security/API field change.

**Final CI status:** **GREEN.** Run **39241785391** on **`98b3985`** —
`conclusion: success`, all four jobs green:

- Frontend (lint, types, tests, build): success
- Backend (format, build, migrations, tests): success
- Compose configuration: success
- Docker stack acceptance + Playwright: success

This satisfies the `BUG-009`/`BUG-010` precedent (a green CI run on `main` before
the milestone is declared done).

**Redeploy from the final tree.** Because one of the post-completion fixes is
**user-visible** (the two new `/progress` → *Future & ideas* entries), the stack
was rebuilt/redeployed again from `f7b1b78` and re-verified live:

- image **`sha256:73ef22ebcf8ceb0720938ddae103cbcd88ece7771b1a191921750da297d698ac`**
  (local build, no registry digest), app container
  `5b89ef97da584d82139122301a559e3bef658f3d1fc1258eb4ae566882aeb9be`, healthy;
  PostgreSQL untouched (no published host port), **no migrations applied**.
- `GET /api/v1/health` and `/api/v1/health/db` → 200.
- Served bundle `index-XubSTjSq.js` matches the fresh build output
  (served-vs-container SHA-256 identical).
- `/progress` renders `13 of 15 milestones complete (87%)` with the
  **`M12 — Administration`** card **Complete**, `Completed 2026-10-19`,
  `· tag m12-complete`, `· commit 820a20c`; M13/M14 remain Planned.
- **`/progress` → *Future & ideas*** now lists **"Administration & audit
  expansion" (V2)** and **"Per-user & device preferences" (V1.x)** — the
  user-visible proof that the live app reflects `f7b1b78` and not the earlier
  `820a20c` build.
- `GET /api/v1/admin/audit?limit=1` → 200 with real rows.

**Note on the unauthenticated 200.** The deploy agent flagged that
`GET /api/v1/admin/audit` answered 200 without a session. That is **expected, not
an authorization gap**: the instance is in **single-user mode**
(`GET /api/v1/setup/status` → `auth_mode: "single_user"`), where — by the locked
M1 design (`docs/SECURITY.md` §5) — there is no login and any client reaching the
port acts as the Admin system actor; the network perimeter (trusted LAN + IP
allowlist) is the only protection, and the wizard warns about it. In multiuser
mode `RequireAdmin()` enforces the Admin role and the same routes were audited
401/403 in the integration suite.

**Tag:** `m12-complete` now marks this verification commit (the final tree, which
is code-identical to the deployed `f7b1b78` apart from documentation).
