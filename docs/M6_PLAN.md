# Fate & Fridge — Milestone 6 Plan (Randomization Engine)

Status: **approved, ready to execute**
Approved: 2026-10-06 (product owner answered the open M6 decision set)
Purpose: authoritative M6 runbook. A fresh session should execute M6 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
database, architecture, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. M6 delivers the randomization engine: candidate eligibility, the exact-repeat
   windows, the special-day reduced contribution, the direct-neighbor protein
   penalty, the spacing preference, favorite weighting, secure weighted
   selection, the single-date **Reroll**, operation metadata, temporary **Undo**,
   and exhaustive randomizer tests. Batch previews (7-day, rest-of-month, Try
   Another, Apply, Fill Empty) are M7.
2. Execution mirrors M5: `docs/M6_PLAN.md` is the runbook; one branch from
   current `main`, per-issue commits, gates, an independent reviewer, fixes on
   the same branch, fast-forward merge, issue closure, and push. The final issue
   sets the manifest M6 status and cuts tag `m6-complete`.
3. **Scoring knobs are household settings.** M6 adds a `Randomizer` settings group
   to `household_settings`, exposed through `GET/PATCH /settings/household` and the
   Household settings UI (docs/API.md §21), with validated ranges. Defaults:

   | Setting | Default | Range |
   |---------|---------|-------|
   | `randomizer_repeat_lookback_days` | 7 | 0–60 |
   | `randomizer_repeat_lookahead_days` | 14 | 0–60 |
   | `randomizer_repeat_multiplier` | 0.10 | 0.01–1.00 |
   | `randomizer_special_day_multiplier` | 0.02 | 0.00–1.00 |
   | `randomizer_protein_multiplier` | 0.70 | 0.10–1.00 (tuned band 0.6–0.8) |
   | `randomizer_protein_neighbor_distance` | 1 | 1–7 |
   | `randomizer_favorite_multiplier` | 1.10 | 1.00–2.00 |
   | `randomizer_spacing_near_days` | 3 | 1–30 |
   | `randomizer_spacing_far_days` | 8 | ≥ near, ≤ 60 |
   | `randomizer_spacing_search_days` | 90 | 7–365 |

   The spacing step multipliers are fixed constants (≤ near → **0.5**,
   near < d < far → **0.75**, ≥ far → **1.0**).
4. **Eligibility.** Only `status = Normal` recipes are candidates. `ManualOnly`
   and `Retired` are never auto-selected. If no Normal recipe exists the reroll
   fails with `RANDOMIZER_NO_CANDIDATES`.
5. **Exact-repeat window.** For candidate date D, count same-recipe occurrences
   in `[D − lookback, D + lookahead]` (default 7 back / 14 forward, inclusive)
   over historical, today, and future meals. A normal occurrence multiplies the
   candidate weight by `randomizer_repeat_multiplier` (0.10). A special-day
   occurrence (holiday/host_family_dinner/misc carrying the recipe) contributes
   `randomizer_special_day_multiplier` (0.02) instead.
6. **Protein adjacency.** Use any direct neighbor (distance
   `randomizer_protein_neighbor_distance`, default 1) that carries a recipe,
   including special events. If the candidate shares any primary protein with a
   neighbor, multiply the weight once by `randomizer_protein_multiplier` (0.70,
   tuned within 0.6–0.8); proteins do not stack.
7. **Spacing.** Search `randomizer_spacing_search_days` (±90) for the nearest
   same-recipe occurrence in either direction and multiply by the step factor
   (≤ near → 0.5, near < d < far → 0.75, ≥ far → 1.0). A recipe with no nearby
   occurrence uses 1.0.
8. **Favorites.** Multiply by `randomizer_favorite_multiplier` (1.10) as a weak
   tie-breaker, subordinate to the repeat, protein, and spacing factors.
9. **Selection.** `weight = base(1.0) × repeat × protein × spacing × favorite`,
   selected by a secure weighted random draw (roulette wheel). Randomness comes
   from `System.Security.Cryptography.RandomNumberGenerator` behind an
   injectable `IRandomSource`; no user-facing predictable seed. Production code
   never derives randomness from the date, recipe count, or ids.
10. **Small pool.** If every eligible candidate is inside the repeat window the
    reroll still returns a weighted candidate; it never fails while at least one
    Normal recipe exists.
11. **Reroll applicability.** `POST /calendar/{date}/reroll` (permission
    `calendar.edit`) replaces **any non-locked, non-historical, non-vacation
    entry** — and fills a blank date — with a randomly selected recipe entry.
    Historical dates fail `CALENDAR_DATE_IN_PAST`, locked dates
    `CALENDAR_ENTRY_LOCKED`, vacation dates `RANDOMIZER_NOT_APPLICABLE`. The
    replacement resets entry metadata (`is_locked = false`, `diner_count = null`,
    `notes = null`, `include_groceries = true`, snapshot from the chosen recipe).
12. **Operation metadata.** Reroll stages one `calendar_entry_history` row with a
    fresh `operation_id` (UUID), `before_json`, `after_json`, and a new
    `metadata_json` (jsonb) recording the score breakdown: candidate count,
    chosen recipe id, each phase multiplier, spacing days, and final weight. The
    API exposes only a compact `operation: { id, undo_expires_at }`; the full
    breakdown stays server-side.
13. **Undo.** `POST /operations/{operation_id}/undo` (permission `calendar.edit`)
    restores the exact pre-reroll state (the previous entry, or blank if the date
    was empty). Undo is offered until the entry is next modified: a later change
    makes it fail `UNDO_CONFLICT`; an operation that is missing or already undone
    fails `UNDO_EXPIRED`. Undo writes its own history row.
14. **No audit rows in M6.** Calendar auditing stays in M12 (consistent with M5);
    M6 writes only `calendar_entry_history`. Reroll/undo do not broadcast (live
    sync is M9) but do emit the operation id that M9 will use.
15. **UI.** A **Reroll** action (desktop compact action on the day cell/context,
    mobile inside the date detail) and a dismissible **Undo** banner after a
    reroll. Reroll is disabled with an explanatory message on historical, locked,
    and vacation dates.
16. **Timezone.** The repeat/protein/spacing windows use household-local dates
    and the `HouseholdClock` (`docs/M5_PLAN.md §1.7`); historical, today, and
    future meals all participate. `last_eaten`/`times_eaten` remain null until
    M11 and are not part of M6 scoring.
17. **No new infrastructure.** Randomization stays in service/domain code; no
    scoring in React, route handlers, or ORM hooks (docs/ARCHITECTURE.md §13).
18. **Verification.** The final issue mirrors the M4/M5 sweep: exhaustive
    randomizer tests, permission/timezone/lock/undo coverage, an independent
    review, docs, the manifest M6 `complete` with evidence, tag `m6-complete`,
    and closing the tracking issue.

---

## 2. M6 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `Domain/HouseholdSettings.cs` — the ten randomizer fields (defaults/ranges per
  §1.3).
- `Domain/CalendarEntryHistory.cs` — add nullable `MetadataJson` (jsonb).
- `Services/RandomizerOptions.cs` — a record projected from household settings.
- `Services/IRandomSource.cs` + `SystemRandomSource` — injectable secure RNG.
- `Services/RandomizerEngine.cs` — pure scoring + weighted selection; returns the
  chosen recipe and a `RandomizationMetadata` breakdown. No I/O.
- `Services/CalendarRandomizer.cs` — builds the context (windows, neighbors),
  calls the engine, and persists the reroll/history/undo transactionally.
- `Services/HouseholdSettingsService.cs` / `HouseholdSettingsPatchValidator.cs` —
  new fields + range validation.
- `Endpoints/CalendarEndpoints.cs` — reroll route; `Endpoints/OperationEndpoints.cs`
  — undo route.
- Migration `M6Randomizer`.

### 2.2 Routes (recorded in `docs/API.md` before completion)

- `POST /api/v1/calendar/{date}/reroll` — `calendar.edit`, no body. Response:
  the M5 `day` document plus:
  ```json
  { "operation": { "id": "uuid", "undo_expires_at": null } }
  ```
- `POST /api/v1/operations/{operation_id}/undo` — `calendar.edit`. Response: the
  restored `day` document.

Errors: `CALENDAR_DATE_IN_PAST`, `CALENDAR_ENTRY_LOCKED`, `RANDOMIZER_NOT_APPLICABLE`
(vacation), `RANDOMIZER_NO_CANDIDATES`, `UNDO_EXPIRED`, `UNDO_CONFLICT`,
`NOT_FOUND`, `FORBIDDEN`, `VALIDATION_FAILED`.

### 2.3 Operation metadata (`calendar_entry_history.metadata_json`)

```json
{
  "kind": "reroll",
  "candidate_count": 42,
  "chosen_recipe_id": 5,
  "base_weight": 1.0,
  "repeat_multiplier": 0.10,
  "repeat_occurrences": 2,
  "protein_multiplier": 0.70,
  "shared_proteins": ["chicken"],
  "spacing_days": 9,
  "spacing_multiplier": 1.0,
  "favorite_multiplier": 1.10,
  "final_weight": 0.077
}
```

### 2.4 Frontend

- `features/calendar/` — Reroll control on the day cell/context (desktop) and in
  `DateDetailDialog` (mobile); an `UndoBanner` after a successful reroll with a
  dismiss action; query invalidation on reroll/undo.
- `features/settings/HouseholdSection.tsx` — a "Randomization" fieldset for the
  ten settings with the §1.3 ranges and validation.
- Reuses the M5 calendar client/hooks, the shared `Modal`, and the existing
  toast/banner patterns.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M6-01 | Randomizer settings | household_settings fields, API payload/validation, Household settings UI fieldset | — |
| M6-02 | Service contract + eligibility | `RandomizerOptions`, `IRandomSource`, `RandomizerEngine` skeleton, Normal-only eligibility, deterministic tests | M6-01 |
| M6-03 | Exact-repeat windows | 7-back/14-forward counting incl. special-day near-zero contribution | M6-02 |
| M6-04 | Protein adjacency | direct-neighbor shared-protein penalty (any recipe-bearing neighbor, no stacking) | M6-02 |
| M6-05 | Spacing + favorites | step penalty for nearest occurrence and the 1.10 favorite tie-breaker | M6-02 |
| M6-06 | Secure weighted selection | roulette draw over final weights with `IRandomSource`; small-pool behavior | M6-03…M6-05 |
| M6-07 | Operation metadata | `metadata_json` column + breakdown writer; compact operation in the API | M6-06 |
| M6-08 | Reroll endpoint | `POST /calendar/{date}/reroll` with applicability rules, transaction, history row | M6-07 |
| M6-09 | Undo endpoint | `POST /operations/{operation_id}/undo` with exact restore, conflict, expiry | M6-08 |
| M6-10 | Reroll/Undo UI | Reroll action + Undo banner, disabled states | M6-09 |
| M6-11 | Exhaustive randomizer tests | eligibility/window/protein/favorite/forced-repeat/small-pool/randomness matrix | M6-02…M6-10 |
| M6-12 | M6 verification sweep | independent §4 battery, docs, manifest M6 `complete`, `CHANGELOG.md`, tag `m6-complete`, close tracking issue | M6-01…M6-11 |

Milestone mapping: items 1→M6-02; 2→M6-02; 3,4,5→M6-03; 6→M6-04; 7→M6-05; 8→M6-05;
9→M6-06; 10→M6-08; 11→M6-07; 12→M6-09; 13→M6-11; new settings→M6-01; new
verification→M6-12.

---

## 4. M6 verification battery

- **Eligibility:** Normal allowed; ManualOnly and Retired never selected; empty
  pool → `RANDOMIZER_NO_CANDIDATES`.
- **Windows:** 7 days back included, 8 excluded; 14 days forward included, 15
  excluded; special-day occurrences contribute near-zero; historical/today/future
  all count.
- **Protein:** previous-day match penalized, next-day match penalized, multiple
  overlap penalized once, unrelated proteins not penalized.
- **Favorites:** a favorite is favored among otherwise-equal candidates and does
  not beat a strong repeat penalty.
- **Spacing:** forced repeat chooses the larger nearest-occurrence spacing; step
  buckets at the near/far boundaries.
- **Small pool:** every candidate in the window still yields a result.
- **Randomness:** repeated runs over the same data are not trivially identical;
  production uses the OS RNG (asserted via the seam); no predictable seed.
- **Reroll applicability:** blank fills; any non-locked entry is replaceable;
  historical → `CALENDAR_DATE_IN_PAST`; locked → `CALENDAR_ENTRY_LOCKED`;
  vacation → `RANDOMIZER_NOT_APPLICABLE`; replacement resets metadata.
- **Operation/undo:** one history row with operation id + `metadata_json`;
  undo restores the exact prior state (entry or blank) and writes history;
  a later change → `UNDO_CONFLICT`; missing/already-undone → `UNDO_EXPIRED`;
  permission matrix on both routes; no audit rows.
- **Settings:** each randomizer field's range validates on PATCH and appears in
  the Household settings UI.
- **Timezone:** the windows/neighbors use household-local dates via
  `HouseholdClock` (DST/midnight cases covered by M5 tests).
- **UI:** Reroll disabled states, keyboard/mobile operation, Undo banner
  dismiss, screenshots for UI changes.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M6 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use Roslyn MCP first for every C# inspection/edit/diagnostic (pass the
   `.csproj` directly; backend workspace root is `/mnt/y/backend`). Never use
   `String.Normalize`; the workstation runs invariant globalization.
3. Frontend gates: Vitest and Playwright workers time out on the 9p share
   `/mnt/y`, so run them from a local mirror of `frontend/` under
   `/tmp/opencode/` (sync changed files before each run; never commit from the
   mirror).
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`.
5. The reviewer must independently execute the relevant tests and review
   eligibility, windows, protein, favorites, spacing, weighted selection,
   locked/historical handling, undo integrity, permission enforcement, and
   UI/accessibility. Scoring tests use the injected `IRandomSource`; production
   randomness is asserted by construction, not by flaky sampling.
6. At M6 start, set M6 `in_progress` in
   `frontend/src/features/progress/milestones.ts` and create issues. At M6-12,
   `dotnet format --verify-no-changes`, Release build, Release tests, and
   `dotnet ef migrations has-pending-model-changes` must pass, then delegate a
   rebuild/redeploy to `docker-admin` (`scripts/deploy.sh up`) and verify the
   live instance (healthy; at least one M6 route reachable; `/progress` shows M6
   complete) before setting M6 `complete` with evidence, updating
   `progressSummary` and the progress tests (including the Playwright progress
   assertion), updating `CHANGELOG.md` and `docs/API.md` (and `docs/DATABASE.md`
   for the new columns), pushing `main`, tagging `m6-complete`, and closing the
   M6 tracking issue. See `AGENTS.md` → "Milestone completion gate".

---

## 6. Explicitly deferred from M6

- Batch previews, Try Another, atomic Apply, rest-of-month, Fill Empty Days,
  vacation blocking in batch, and batch concurrency: M7.
- Live-sync broadcast of reroll/undo events: M9.
- Grocery consumption of rerolled meals: M8.
- Statistics/history (`last_eaten`/`times_eaten`): M11.
- Audit viewer and calendar `audit_events` (incl. rerolls): M12.
- Advanced variety rules (cuisine/pasta caps, seasonal weighting, dietary
  scheduling): V2 backlog.
