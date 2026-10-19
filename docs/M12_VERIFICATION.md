# M12 Verification — Administration

Status: **in progress** (live deployment gate pending)
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
| Playwright (hermetic) | 69 passed / 3 gated stack skips |
| `npm run lint` / `format:check` / `typecheck` | clean |

---

## Live deployment verification

_Pending: the rebuild/redeploy is performed by `docker-admin`
(`scripts/deploy.sh up`) from the completed commit, and the audit log, a
historical correction and a hard delete are verified on the running instance
before the milestone is marked complete and tagged._

---

## CI status after completion

_Pending._
