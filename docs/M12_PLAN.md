# Fate & Fridge — Milestone 12 Plan (Administration)

Status: **approved, ready to execute**
Approved: 2026-10-18 (product owner answered the full M12 decision set)
Purpose: authoritative M12 runbook. A fresh session should execute M12 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
architecture, database, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. **Permissions — no new keys.** Audit read, historical correction, and hard
   recipe delete are **Admin-only** (the existing `admin` role; the single-user
   system actor is an Admin). The seven permission keys are **unchanged — no
   eighth key** (`docs/SECURITY.md` §4). Enforcement is server-side; the UI hides
   what the caller cannot do. Rerolls are excluded from audit_events (**see §1.4**).
2. **Audit store.** `audit_events` (created in M2, with `created_at`,
   `actor_user_id`, and `(entity_type, entity_id)` indexes already in place) is
   the audit log. Rows are append-only. Event and entity types stay open strings;
   no schema change is planned.
3. **Audit coverage in M12.** Start writing audit rows for:
   - **calendar meal changes** — create/update/delete entries and lock/unlock;
   - **recipe lifecycle** — create/update/retire, image add/replace/remove, and
     hard delete;
   - **users + permissions** — create/rename/role/enable/disable/reset-password
     and permission-set changes;
   - **settings** — household and security changes, including the
     single→multiuser switch;
   - **groceries** — manual item adds/edits and staple create/retire;
   - **logins** — successful login, failed login, and logout (mirrored from the
     auth path so the audit stream covers `docs/PRODUCT_SPEC.md` §43);
   - **historical corrections**;
   - **backup/restore stub types** — defined now, emitted by M13.
   Reference-data events already ship from M2. **Ordinary page views are never
   logged**, and no secret is ever logged (`docs/SECURITY.md` §17).
4. **Rerolls → entry history, not audit_events.** `docs/PRODUCT_SPEC.md` §43 lists
   rerolls in the audit log, but rerolls already have durable provenance in
   `calendar_entry_history` (with their score metadata, `docs/M6_PLAN.md` §2.3 and
   `docs/M7_PLAN.md` §2.3). M12 therefore keeps rerolls **out of `audit_events`**
   and instead surfaces them in the viewer from the entry history. This is the
   accepted interpretation of §43.
5. **Audit viewer sources.** The viewer is a single **newest-first merged
   timeline** of:
   - **all** `audit_events` rows (`source = "audit"`), and
   - `calendar_entry_history` rows **that carry reroll/batch metadata**
     (`metadata_json.kind` ∈ `reroll` | `batch_reroll` | `fill_empty` | `undo`,
     `source = "entry_history"`) — these are the reroll records.
   Ordinary entry edits are **not** duplicated from the history table (they
   already appear as calendar audit rows). Each item carries its source
   discriminator, so the UI can label a reroll row distinctly.
6. **Audit viewer capabilities.** Filters (event type, entity type, actor, date
   range, source), newest-first paging, expandable before/after detail, and the
   reason / IP address / metadata fields. Read-only: no row editing and no
   per-row deletion.
7. **Export.** Export the **current filtered view** as **CSV and JSON**
   (Admin-only download). CSV has a stable header row, correct quoting
   (commas/quotes/newlines), and neutralises spreadsheet formula injection;
   both formats are capped (over the cap → `400 VALIDATION_FAILED`).
8. **Retention.** A configurable **`audit_retention_days`** setting (validated
   range, default e.g. 365; `0`/blank meaning "keep forever") plus an
   **Admin-only, explicitly confirmed "prune audit rows older than the window"
   action** that itself writes an `audit.pruned` row. No automatic/background
   pruning, no scheduled job, no new infrastructure. Only age-based pruning —
   never a per-row delete.
9. **Historical correction.** **Single past date, full edit**: an Admin may
   change one past date's **entry type, recipe, notes, and diner count**.
   **Locked 2026-10-18 (M12-06):** the correction is a **full replace** and also
   accepts the normal write's optional type-specific fields (`restaurant_name`,
   `location_text`, `custom_label`, `icon_key`) so every entry type is
   expressible; it never changes the lock state.
   - Requires a **reason** and a confirmation; recorded in `audit_events`
     (`calendar.historical_correction`) **and** as a `calendar_entry_history` row
     with `is_historical_correction = true` and the reason, in one transaction.
   - The corrected entry's frozen snapshot (`snapshot_recipe_name` /
     `snapshot_display_text`) is **refreshed from the recipe at correction time**,
     so history keeps rendering after later renames or deletion.
   - **Locked past dates are allowed** with an explicit "this date is locked"
     warning and an extra acknowledgement; the correction does **not** change the
     lock state, and locked dates are still immutable through the ordinary
     endpoints.
   - Only **past** dates are correctable; today/future keep using the normal
     endpoints (`409 CALENDAR_DATE_NOT_PAST` otherwise). The correction never
     touches dates outside the requested date.
   - Bulk/range correction is **V2** (§6).
10. **Hard recipe delete.** An Admin may hard-delete a recipe **at any time**.
    - Before deleting, every referencing entry gets durable snapshot text.
    - **Entries dated today or later that reference the recipe are removed** so
      nothing is planned against a deleted recipe.
    - **Past entries are tombstoned**: `recipe_id` becomes null while
      `snapshot_recipe_name` / `snapshot_display_text` are preserved and
      `include_groceries` **stays true**, so statistics, meal history, and
      grocery attribution stay coherent (the M11 tombstone guarantee).
    - The recipe row and its image **rows and files** are removed; no broken
      foreign keys.
    - One `audit_events` row (`recipe.hard_deleted`) retains the before-image
      (recipe document), the reason, and the affected counts.
    - **Irreversible** — there is no undo. The shared dangerous-action dialog
      applies.
11. **Dangerous-action framework.** One shared, accessible dialog
    (reason, explicit warnings, acknowledgement checkbox, typed confirmation
    string, final confirm) used by **historical correction, hard recipe delete,
    and audit prune**, and **reused by M13's restore**. The typed confirmation is
    verified **server-side** as well as in the UI. Every use writes an audit row.
    The reason is trimmed and capped at **500 characters**; the typed confirmation
    must match exactly (ordinal, case-sensitive) after trimming. `DangerousActionGuard`
    owns these rules so the endpoints cannot drift.
    **Locked 2026-10-18 (M12-05 review):** the wire field carrying the typed value
    is **`confirmation`** on every dangerous-action endpoint (matching
    `DangerousActionGuard`), and the expected string is per action —
    audit prune types `PRUNE`; historical correction types the date being
    corrected (`yyyy-MM-dd`, e.g. `2026-10-05`); hard recipe delete types the
    **recipe name**. A missing/blank/`null` expected value means that action
    requires no typed confirmation.
12. **UI placement.** The **audit log** is a new Admin-only section of the
    existing `/settings` console — **no new nav item and no new route family for
    navigation**. **Historical correction** is launched from the existing
    meal-history/date view (admin-only affordance); **hard recipe delete** from
    the recipe detail/editor (admin-only affordance). Both use the shared dialog.
    **Locked 2026-10-18 (M12-07):** the correction UI sends
    `lock_acknowledged: true` only for a locked date (otherwise `false`) and always types the date as the confirmation.
13. **Live sync** reuses the existing coarse events — a correction broadcasts
    `calendar.updated` and a hard delete broadcasts `recipe.updated` (+
    `calendar.updated` when planned entries were removed). **No new event type.**
14. **No new entity, no new table, and no migration** unless the merged viewer's
    performance requires an approved index (§4). No cache, no queue, no worker,
    no Redis, no telemetry.
15. **Date display.** Product UI dates render as **MM-DD-YY** (American); the
    **API wire format stays ISO `yyyy-MM-dd`** (stability for stored data and
    clients). This is a V1.x display change tracked separately (§7).

---

## 2. M12 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Services/AuditService.cs` — **extend** with the M12 event/entity type
  constants and a small staging helper; audit writes already participate in the
  caller's transaction (stage before `SaveChanges`).
- `Services/AuditLogQueryService.cs` — **new**: the merged read model over
  `audit_events` ∪ reroll `calendar_entry_history` rows (one `UNION ALL` query or
  two bounded queries merged deterministically), the filters, newest-first keyset
  paging, the detail projection, the CSV/JSON export projections, and the
  retention prune executor.
- `Services/HistoricalCorrectionService.cs` — **new**: validates the past-date
  correction, refreshes the frozen snapshot, writes the history row and the audit
  row in one transaction, and returns the corrected entry (the caller emits live
  sync).
- `Services/RecipeHardDeleteService.cs` — **new**: snapshot backfill, removal of
  today/future references, tombstoning of past references, image row + file
  removal, the audit row, and the delete — all in one transaction.
- `Endpoints/AdminEndpoints.cs` — **new**: `GET /admin/audit`,
  `GET /admin/audit/export`, `POST /admin/audit/prune`, admin-gated
  (`RequireAdmin()`).
- `Endpoints/CalendarEndpoints.cs` — add `POST /calendar/{date}/correction`
  (Admin-only).
- `Endpoints/RecipeEndpoints.cs` — add `DELETE /recipes/{id}` (Admin-only).
- `Endpoints/SettingsEndpoints.cs` — add `audit_retention_days` to the security
  settings payload + validator.
- Audit emitters added at the existing write paths (calendar, recipes, users,
  permissions, settings, groceries, auth).

### 2.2 Contracts (recorded in `docs/API.md` before completion)

- `GET /api/v1/admin/audit?event_type=&entity_type=&actor_user_id=&source=&from=&to=&limit=&cursor=`
  (Admin) → `{ items, next_cursor }`; each item
  `{ id, source, recorded_at, actor_user_id, actor_username, event_type, entity_type, entity_id, summary, reason, ip_address, before, after, metadata }`,
  newest-first, stable paging.
- `GET /api/v1/admin/audit/export?format=csv|json&<same filters>` (Admin) →
  download; CSV with a fixed header order; capped; `400 VALIDATION_FAILED` over
  the cap or for an unknown format.
- `POST /api/v1/admin/audit/prune` (Admin) → body
  `{ reason, confirmation }` (`confirmation` must be `PRUNE`) →
  `{ deleted_count, cutoff, retention_days }`; writes
  `audit.pruned`; `400` when retention is "keep forever" or the reason/confirmation
  is invalid (`REASON_REQUIRED`/`REASON_TOO_LONG`/`CONFIRMATION_MISMATCH` with
  `details.field_errors`).
- `POST /api/v1/calendar/{date}/correction` (Admin) → body
  `{ entry_type, recipe_id?, notes?, diner_count?, reason, confirmation, lock_acknowledged? }`
  (`confirmation` must be the `yyyy-MM-dd` date being corrected)
  → the corrected entry document; `409 CALENDAR_DATE_NOT_PAST` for a non-past
  date, `409 CALENDAR_DATE_LOCKED` when the date is locked without
  `lock_acknowledged`, `400 VALIDATION_FAILED` otherwise (same per-type field
  rules as the normal calendar writes).
- `DELETE /api/v1/recipes/{id}` (Admin) → body
  `{ reason, confirmation }` (`confirmation` must be the exact recipe name) →
  `{ recipe_id, removed_planned_entries, tombstoned_entries, removed_images }`;
  `404 NOT_FOUND` for an unknown recipe.
- Errors use the shared envelope; JSON stays snake_case.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M12-01 | Audit coverage sweep | M12 event/entity type constants + emitters at calendar, recipe, user/permission, settings, grocery, and auth write paths, with tests proving actor/target/before/after and that secrets are never logged | — |
| M12-02 | Audit log query service + read API | merged audit+reroll read model, filters, newest-first paging, detail projection, `GET /admin/audit` | M12-01 |
| M12-03 | Audit viewer UI | Admin-only Audit log section in `/settings`: filters, paging, detail, reason/IP/metadata, empty/error states, a11y, mobile | M12-02 |
| M12-04 | Audit export + retention | CSV/JSON export of the filtered view (quoting + formula-injection safety) and the `audit_retention_days` setting with the confirmed prune action (`audit.pruned`) | M12-02, **M12-05** |
| M12-05 | Shared dangerous-action dialog | reusable accessible dialog (reason, warnings, acknowledgement, typed confirmation, final confirm) + server-side confirmation verification seam | — |
| M12-06 | Historical correction backend | `POST /calendar/{date}/correction`, past-only + locked-date acknowledgement, reason, snapshot refresh, history + audit rows in one transaction, live sync | M12-05 |
| M12-07 | Historical correction UI | admin-only entry point from the meal-history/date view using the shared dialog | M12-06 |
| M12-08 | Recipe hard delete backend | snapshot backfill, planned-entry removal, past tombstones (`include_groceries` stays true), image row/file removal, `recipe.hard_deleted` audit row, atomicity, live sync | M12-05, M12-01 |
| M12-09 | Recipe hard delete UI | admin-only affordance on the recipe detail/editor with the shared dialog and an irreversibility warning | M12-08 |
| M12-10 | M12 verification sweep | independent §4 battery, docs (`API.md`, `DATABASE.md`, `SECURITY.md`, `TESTING.md`, `CHANGELOG.md`), manifest M12 `complete`, rebuild/redeploy + live gate, tag `m12-complete`, close the tracking issue | M12-01…M12-09 |

Milestone mapping: 1→M12-01; 2→M12-02/M12-03; 3→M12-06/M12-07; 4→M12-06;
5→M12-08/M12-09; 6→M12-08; 7→M12-05.

---

## 4. M12 verification battery

- **Audit coverage:** each listed write path emits exactly one audit row with
  actor, target, and before/after; a failed audit write fails the operation
  (nothing commits unaudited); ordinary page views are never logged; **no
  plaintext password, reset secret, recovery key, or auth token** appears in any
  audit row.
- **Viewer:** the merged timeline contains audit rows **and** reroll rows from the
  entry history, ordered newest-first with stable paging; each filter (event type,
  entity type, actor, source, date range) narrows correctly and AND-combines;
  ordinary entry edits are not double-reported; before/after detail is accurate;
  the empty state is sane.
- **Export:** CSV and JSON contain exactly the filtered rows; CSV quoting is
  correct for commas, quotes, and newlines, and leading `=`/`+`/`-`/`@` values are
  neutralised; the cap returns `VALIDATION_FAILED`.
- **Retention:** prune removes **only** rows older than the cutoff (boundary
  tested), writes its own audit row, is Admin-only, and requires the confirmation;
  `audit_retention_days` validates its range and "keep forever" refuses to prune.
- **Permissions:** every new endpoint is Admin-only — anonymous → 401, member →
  403, Admin/single-user → allowed; **no eighth permission key exists**; the UI
  afforances are hidden for non-Admins and hiding them is not the authority.
- **Historical correction:** past-only (today/future → `CALENDAR_DATE_NOT_PAST`);
  a locked date needs `lock_acknowledged` and does not change lock state; the
  reason is required; the audit row and the `is_historical_correction` history row
  commit atomically with the change; the snapshot is refreshed so a later rename
  or deletion still renders; statistics/history/meal-history reflect the
  correction; other dates are untouched; live sync fires.
- **Hard delete:** snapshots are backfilled before the delete; **today/future
  references are removed** while **past references are tombstoned** with
  `include_groceries` still true and snapshots preserved; statistics and meal
  history keep the counts (M11 regression); image rows and files are gone; the
  audit row retains the recipe before-image; no broken foreign keys (DB-level
  check); `404` for an unknown recipe; the recipe disappears from lists, the
  randomizer, and grocery projections; the operation is irreversible.
- **Dangerous-action dialog:** keyboard-operable with a focus trap, labelled,
  Escape-dismissible, and the typed confirmation is enforced **server-side**
  (a direct API call without it fails).
- **Concurrency:** prune racing a viewer read; a correction racing a batch apply;
  a hard delete racing a reroll — no 500s, no partial writes.
- **Performance:** the merged viewer, a filtered page, and an export stay
  responsive on a `docs/TESTING.md` §12-sized dataset (10 years of meals plus a
  large audit log); no N+1. An index migration is added only if this bar is
  missed and the Lead approves.
- **UI:** the settings section is mobile-usable and accessible; export downloads;
  error/empty states are handled; the date display is MM-DD-YY.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M12 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Roslyn MCP first for all C# work: pass the **absolute**
   `/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj` as
   `projectPath`; `filePath` resolves against `/mnt/y/backend`. Confirm
   `is_msbuild_workspace: true`. Never use `String.Normalize` (invariant
   globalization).
3. Frontend gates run from a local mirror under `/tmp/opencode/frontend` with a
   real local `node_modules` (`rsync --exclude node_modules`; never commit from
   the mirror).
4. Backend gates: `dotnet format FateAndFridge.slnx --verify-no-changes`, Release
   build, Release tests via the test binary with `-class` filters (never
   `dotnet test`), and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
5. The reviewer independently executes the battery, reviews the audit coverage
   for secret leakage, the four invariants (past-only correction, tombstone
   integrity, append-only audit, Admin-only enforcement), export safety, and the
   UI/accessibility.
6. At M12 start, set M12 `in_progress` in
   `frontend/src/features/progress/milestones.ts` and create issues. At M12-10,
   all gates must pass **and CI must be green**, then delegate a rebuild/redeploy
   to `docker-admin` (`scripts/deploy.sh up`) and verify the live instance
   (healthy; the audit log, a historical correction, and a hard delete reachable
   with real data; `/progress` shows M12 complete) before setting M12 `complete`
   with evidence, updating the progress tests and the Playwright assertion,
   `CHANGELOG.md`, and the docs, pushing `main`, tagging `m12-complete`, and
   closing the M12 tracking issue. See `AGENTS.md` → "Milestone completion gate".

### 5.1 Execution cadence

M12 is executed in **deliverable-sized sessions, not issue-sized ones**, with a
**checkpoint commit after every verified deliverable** (see `docs/M6_PLAN.md`
§5.1). One session owns one authorable *and* independently verifiable unit;
commit as soon as its gates pass; never leave more than one unverified unit in
the working tree.

---

## 6. Explicitly deferred from M12 (recorded in `docs/FUTURE_FEATURES.md`)

- **Bulk / date-range historical correction** (V2).
- **Per-capability permission keys** (`audit.view`, `history.correct`,
  `recipes.delete`) so Admin capabilities can be delegated to members (V2).
- **Automatic or scheduled audit pruning and scheduled exports** (V2).
- **Tamper-evident audit storage** (hash chaining/signing) (V2).
- **A dedicated `/admin` navigation area** (V2).
- **Changing the API wire date format** to a localised form (rejected — ISO is
  kept for stored data and clients).
- Backup/restore workflows, restore validation, and the recovery-key export:
  **M13** (M12 only defines the stub event types).

---

## 7. Tracked V1.x display change (outside M12's review surface)

**Date display format → MM-DD-YY (implemented, #160).** Product UI dates
(calendar, statistics overview, meal history, per-recipe Last Eaten / Next
Planned, print output, settings last-login, and lockout timestamps) render as
**MM-DD-YY** through the shared `frontend/src/lib/dateFormat.ts`. Month labels,
weekday labels, and calendar grid day numbers stay readable. The API wire format
stays ISO `yyyy-MM-dd` and no stored value changes. Delivered as its own V1.x
issue with the same gates; no API, schema, or dependency change.
