# Fate & Fridge — Milestone 7 Plan (Batch Planning)

Status: **approved, ready to execute**
Approved: 2026-10-08 (product owner answered the full M7 decision set)
Purpose: authoritative M7 runbook. A fresh session should execute M7 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
database, architecture, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. M7 delivers batch planning over a date range: a **preview** (7-day,
   rest-of-month, and Fill Empty), **Try Another**, an **atomic Apply**, a
   **batch Undo**, lock and vacation handling, and batch concurrency protection.
   It reuses the M6 randomization engine, eligibility, and the ten randomizer
   settings — **no new settings**.
2. Execution mirrors M5/M6: `docs/M7_PLAN.md` is the runbook; one branch from
   current `main`, per-issue commits, gates, an independent reviewer, fixes on
   the same branch, fast-forward merge, issue closure, and push. The final issue
   sets the manifest M7 status and cuts tag `m7-complete`.
3. **Scope of a reroll batch.** 7-day and rest-of-month reroll change **blank
   dates and existing plain `recipe` entries only**. They **preserve** special
   events (`holiday`, `host_family_dinner`, `misc`, `go_to_family_dinner`),
   `eat_out`, `fend_for_yourself`, `vacation`, and **locked** dates, showing each
   as unchanged in the preview. Fill Empty fills **blank dates only**.
4. **Ranges.** 7-day = selected date + next 6 days. Rest-of-month = selected date
   → last day of that calendar month. Fill Empty = selected date → last day of
   that calendar month. A range whose start is before the household today is
   rejected with `CALENDAR_DATE_IN_PAST` (no silent clamping). Ranges stay within
   the ±5-year horizon.
5. **Vacation.** A vacation date inside the range is skipped (shown unchanged);
   the batch never aborts because of it.
6. **Sequential generation.** Within one preview the engine generates in
   ascending date order; recipes chosen earlier in the same preview count as
   occurrences/neighbors for later dates, alongside existing, locked, and
   preserved entries and future meals outside the range. Only `status = Normal`
   recipes are candidates; an empty pool fails `RANDOMIZER_NO_CANDIDATES`.
7. **Preview is exact.** Apply commits exactly the recipes shown in the preview;
   Try Another generates a fresh proposal with fresh randomness (no limit,
   discarding the previous one). The preview is held server-side as a
   **persisted draft** with an opaque token and a **15-minute TTL**.
8. **Apply is atomic and conflict-safe.** Apply takes the draft token, re-checks
   every target date, and fails `BATCH_CONFLICT` (409) if any target date changed
   since the preview (conflict scope = target dates only). It writes all changed
   dates in one transaction. A missing/expired/consumed draft fails
   `BATCH_DRAFT_EXPIRED` (410).
9. **Batch operation.** Each changed date writes exactly one
   `calendar_entry_history` row; the rows of one Apply share a
   `batch_operation_id` (new nullable, indexed column) while keeping their own
   per-row `operation_id`. `metadata_json` records the per-date score breakdown
   with `kind` = `batch_reroll` or `fill_empty` plus the mode.
10. **Batch undo.** Undo reverts the whole Apply (all changed dates) while every
    affected date is still in the state the Apply left; if any date changed since,
    the undo fails `UNDO_CONFLICT` (409); a missing/already-undone batch fails
    `UNDO_EXPIRED` (410). Undo writes one history row per restored date sharing a
    fresh `batch_operation_id` with `change_reason` `undo:<batch_operation_id>`.
    Undo of a now-historical date fails `CALENDAR_DATE_IN_PAST` (mirrors M6).
11. **Replaced entry metadata** resets exactly like single-date reroll:
    `is_locked = false`, `diner_count = null`, `notes = null`,
    `include_groceries = true`, cleared restaurant/location/custom/icon fields,
    and the snapshot refreshed from the chosen recipe.
12. **Concurrency.** Apply serializes per target date with the same
    `pg_advisory_xact_lock` used by M6 (acquired in ascending date order); a
    concurrent Apply/undo loses cleanly (`BATCH_CONFLICT`/`UNDO_CONFLICT`), never
    a 500. Preview takes no locks and persists no calendar state.
13. **Permissions.** Preview, apply, and fill all require `calendar.edit`.
14. **No extra confirmation** for Apply — Apply is the confirmation. Only the M12
    historical-correction workflow uses the heavy warning flow.
15. **UI.** A **"Reroll…" toolbar menu** (7-day / Rest of month / Fill empty)
    available on desktop and mobile opens a **modal preview wizard** with a diff
    (changed dates highlighted; unchanged/locked/special/vacation muted) and
    **Try Another / Apply / Cancel**. After Apply, the M6 dismissible **Undo
    banner** shows one banner for the batch.
16. **No live-sync broadcast** in M7 (M9 owns it), but the batch operation id is
    emitted for M9. No calendar `audit_events` (M12). Household-timezone dates via
    `HouseholdClock`.
17. **Verification.** The final issue mirrors the M4/M5/M6 sweep: exhaustive batch
    tests, permission/timezone/lock/vacation/conflict/undo coverage, an
    independent review, docs, the manifest M7 `complete` with evidence, tag
    `m7-complete`, and closing the tracking issue.

---

## 2. M7 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Domain/CalendarRerollDraft.cs` — the persisted preview draft.
- `Domain/CalendarEntryHistory.cs` — add nullable `BatchOperationId` (uuid).
- `Services/BatchRerollService.cs` — builds ranges, runs the sequential engine
  pass, creates/supersedes drafts, applies, and undoes batches transactionally.
- `Services/RandomizerEngine.cs` / `RandomizerOptions.cs` — reused unchanged.
- `Endpoints/CalendarEndpoints.cs` — preview route; `Endpoints/OperationEndpoints.cs`
  — batch undo route.
- Migration `M7BatchPlanning` (draft table + history column).

### 2.2 Routes (recorded in `docs/API.md` before completion)

- `POST /api/v1/calendar/reroll/preview` — `calendar.edit`. Body:
  ```json
  { "start_date": "2026-10-15", "mode": "seven_days" }
  ```
  `mode` ∈ `seven_days` | `rest_of_month` | `fill_empty`. Response:
  ```json
  {
    "draft": { "token": "uuid", "mode": "seven_days",
               "start_date": "2026-10-15", "end_date": "2026-10-21",
               "expires_at": "2026-10-15T18:15:00Z" },
    "days": [
      { "date": "2026-10-15", "status": "proposed", "unchanged_reason": null,
        "current": { "…": "entry summary or null" },
        "proposed": { "entry_type": "recipe", "recipe_id": 5, "display_text": "…" } },
      { "date": "2026-10-16", "status": "unchanged", "unchanged_reason": "locked",
        "current": { "…": "…" }, "proposed": null }
    ]
  }
  ```
  `unchanged_reason` ∈ `locked` | `vacation` | `special` | `existing_non_recipe` | null.
  The full score breakdown stays server-side in the draft.
- `POST /api/v1/calendar/reroll/apply` — `calendar.edit`. Body `{ "token": "uuid" }`.
  Response: the batch operation plus the changed day documents:
  ```json
  { "operation": { "id": "uuid", "undo_expires_at": null },
    "days": [ { "…": "day document" } ], "changed_count": 5 }
  ```
- `POST /api/v1/operations/{operation_id}/undo` — unchanged M6 single-reroll route.
- `POST /api/v1/batch-operations/{batch_operation_id}/undo` — `calendar.edit`.
  Response: `{ "operation": { "id": "uuid" }, "days": [ { "…": "day document" } ],
  "reverted_count": 5 }`.
- `POST /api/v1/calendar/fill-empty` from `docs/API.md §8` is superseded by
  `mode = "fill_empty"` on the preview route (documented as such).

Errors: `CALENDAR_DATE_IN_PAST`, `BATCH_CONFLICT` (409),
`BATCH_DRAFT_EXPIRED` (410), `RANDOMIZER_NO_CANDIDATES` (422), `UNDO_CONFLICT`
(409), `UNDO_EXPIRED` (410), `NOT_FOUND`, `FORBIDDEN`, `VALIDATION_FAILED`.

### 2.3 Draft and metadata

`calendar_reroll_drafts`:

| Column | Type | Notes |
|--------|------|-------|
| `id` | bigint PK | |
| `token` | uuid | unique; returned to the client |
| `mode` | text | `seven_days`/`rest_of_month`/`fill_empty` |
| `start_date` / `end_date` | date | inclusive range |
| `actor_user_id` | bigint? | creator |
| `proposal_json` | jsonb | per-date proposal + score breakdown + before-state |
| `created_at` | timestamptz | |
| `expires_at` | timestamptz | `created_at + 15 min` |
| `applied_at` | timestamptz? | set when consumed |

`calendar_entry_history.batch_operation_id` (uuid, nullable, indexed) groups the
rows of one Apply (or one batch undo). `metadata_json` per row:

```json
{
  "kind": "batch_reroll",
  "mode": "seven_days",
  "candidate_count": 42,
  "chosen_recipe_id": 5,
  "base_weight": 1.0, "repeat_multiplier": 0.10, "repeat_occurrences": 2,
  "protein_multiplier": 0.70, "shared_proteins": ["chicken"],
  "spacing_days": 9, "spacing_multiplier": 1.0,
  "favorite_multiplier": 1.10, "final_weight": 0.077
}
```

### 2.4 Frontend

- `api/calendar.ts` — `previewBatchReroll`, `applyBatchReroll`,
  `undoBatchOperation` + types.
- `features/calendar/` — a `RerollMenu` (toolbar, desktop + mobile), a
  `BatchPreviewDialog` modal wizard with diff rendering and Try Another / Apply /
  Cancel, and reuse of `UndoBanner` for the batch operation.
- Query invalidation on apply/undo via the existing `calendarQueryKey`.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M7-01 | Draft schema + preview service/route | `calendar_reroll_drafts` + `M7BatchPlanning` migration, `BatchRerollService` preview, sequential engine pass, `POST /calendar/reroll/preview` (seven_days + rest_of_month), draft TTL | — |
| M7-02 | Try Another | regeneration semantics, draft supersede, no persistence | M7-01 |
| M7-03 | Atomic Apply | `POST /calendar/reroll/apply`, batch operation + `batch_operation_id` + per-date history/metadata, conflict detection, atomic transaction | M7-02 |
| M7-04 | Fill Empty | `mode = fill_empty` (blank-only), same preview/apply flow | M7-03 |
| M7-05 | Lock behavior in batch | locked dates preserved/unchanged and still counted as scoring context; tests | M7-03 |
| M7-06 | Vacation blocking | vacation dates skipped/unchanged; tests | M7-03 |
| M7-07 | Batch concurrency protection | per-date advisory locks, `BATCH_CONFLICT`, concurrent apply/undo tests | M7-03 |
| M7-08 | Batch undo | `POST /batch-operations/{id}/undo`, exact multi-date restore, conflict/expiry, history rows | M7-03 |
| M7-09 | Batch UI | Reroll menu, modal preview wizard with diff, Try Another/Apply/Cancel, batch Undo banner, disabled states | M7-04…M7-08 |
| M7-10 | M7 verification sweep | independent §4 battery, docs, manifest M7 `complete`, `CHANGELOG.md`, tag `m7-complete`, close tracking issue | M7-01…M7-09 |

Milestone mapping: 1→M7-01; 2→M7-02; 3→M7-03; 4→M7-01; 5→M7-04; 6→M7-05;
7→M7-06; 8→M7-07; batch undo→M7-08; UI→M7-09; verification→M7-10.

---

## 4. M7 verification battery

- **Scope:** blank + plain `recipe` dates replaced; special/eat-out/fend/locked/
  vacation preserved and shown unchanged; Fill Empty touches blank dates only.
- **Ranges:** 7-day boundary (start+6), rest-of-month month-end, Fill Empty
  month-end; past start → `CALENDAR_DATE_IN_PAST`; horizon caps.
- **Vacation/locks:** vacation skipped and never aborts; locked preserved but still
  counted as occurrence/protein context.
- **Sequential context:** an earlier pick in the same preview suppresses a repeat
  later in the same preview; existing/future occurrences still count.
- **Preview:** persists no entries and no history; Try Another yields a different
  proposal and a new token; expired/consumed draft → `BATCH_DRAFT_EXPIRED`.
- **Apply:** exact previewed recipes; atomic (a mid-apply failure rolls back all
  dates); one history row per changed date sharing one `batch_operation_id`;
  `metadata_json` shape; a changed target date → `BATCH_CONFLICT`.
- **Concurrency:** two concurrent applies → one wins, the other
  conflict/expired, no 500; apply racing a single-date edit serializes.
- **Undo:** reverts every changed date exactly; later change → `UNDO_CONFLICT`;
  missing/already-undone → `UNDO_EXPIRED`; now-historical → `CALENDAR_DATE_IN_PAST`;
  one undo row per date.
- **Permissions:** `calendar.edit` on preview/apply/fill/batch-undo; member
  without it → 403; unauthenticated handling consistent with M6.
- **Timezone:** ranges/context use household-local dates via `HouseholdClock`.
- **UI:** menu availability, wizard diff, Try Another/Apply/Cancel, disabled
  states, batch Undo banner, mobile behavior; screenshots for UI changes.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M7 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use the Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the
   **absolute** `.csproj` to `projectPath`
   (`/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj`): `projectPath`
   resolves against the MCP server working directory `/mnt/y`; `filePath`
   resolves against the workspace root `/mnt/y/backend`. Confirm with
   `roslyn_get_project_info` (`is_msbuild_workspace: true`). Never use
   `String.Normalize` (invariant globalization).
3. Frontend gates: Vitest/Playwright workers time out on the 9p share `/mnt/y`,
   so run them from a local mirror under `/tmp/opencode/frontend` (rsync changed
   files before each run; never commit from the mirror).
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
5. The reviewer must independently execute the relevant tests and review scope,
   sequential generation, draft TTL/conflict, atomicity, concurrency, undo
   integrity, permissions, and UI/accessibility. Scoring tests use the injected
   `IRandomSource`; production randomness is asserted by construction.
6. At M7 start, set M7 `in_progress` in
   `frontend/src/features/progress/milestones.ts` and create issues. At M7-10,
   all gates must pass, then delegate a rebuild/redeploy to `docker-admin`
   (`scripts/deploy.sh up`) and verify the live instance (healthy; at least one
   M7 route reachable; `/progress` shows M7 complete) before setting M7
   `complete` with evidence, updating the progress tests and the Playwright
   progress assertion, `CHANGELOG.md`, `docs/API.md`, and `docs/DATABASE.md`,
   pushing `main`, tagging `m7-complete`, and closing the M7 tracking issue.
   See `AGENTS.md` → "Milestone completion gate".

### 5.1 Execution cadence

M7 is executed in **deliverable-sized sessions, not issue-sized ones**, with a
**checkpoint commit after every verified deliverable** (see `docs/M6_PLAN.md`
§5.1). One session owns one authorable *and* independently verifiable unit; commit
as soon as its gates pass; never leave more than one unverified unit in the
working tree. `implementer` runs at `steps: 80`, `reviewer` 28, `docker-admin`
16. The Lead orchestrates and does not author milestone code.

---

## 6. Explicitly deferred from M7

- Live-sync broadcast of preview/apply/undo events: M9.
- Grocery consumption of batch-rerolled meals: M8.
- Statistics/history (`last_eaten`/`times_eaten`): M11.
- Audit viewer and calendar `audit_events` (incl. batch rerolls): M12.
- Redo, undo retention beyond "until a date is next modified", and proposal
  history/navigation: see `docs/FUTURE_FEATURES.md`.
- Advanced variety rules (cuisine/pasta caps, seasonal weighting, dietary
  scheduling): see `docs/FUTURE_FEATURES.md`.
