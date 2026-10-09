# M7 Verification — Batch Planning

Status: **complete** (2026-10-09, tag `m7-complete`). Tracking issue: #94
(M7-01…M7-10 are #95–#104).

M7 adds the `calendar_reroll_drafts` table and the
`calendar_entry_history.batch_operation_id` column (migration
`M7BatchPlanning`), the `BatchRerollService` (preview drafts, sequential engine
pass, atomic apply, batch undo), the preview/apply/batch-undo routes, and the
batch UI (Reroll menu, preview wizard, batch Undo banner). No other schema
changes.

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet build -c Release tests/FateAndFridge.Api.Tests/FateAndFridge.Api.Tests.csproj
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
$ARTIFACTS -class "FateAndFridge.Api.Tests.BatchRerollPreviewIntegrationTests"
$ARTIFACTS -class "FateAndFridge.Api.Tests.BatchApplyIntegrationTests" \
  -class "FateAndFridge.Api.Tests.BatchRerollMatrixIntegrationTests" \
  -class "FateAndFridge.Api.Tests.BatchUndoIntegrationTests"
dotnet format --verify-no-changes --no-restore
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
```

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers): `npm run lint && npm run format:check
&& npm run typecheck && npm run test`, then `npx playwright test`.

Integration tests use Testcontainers/PostgreSQL, an injected `TestTimeProvider`
(deterministic household-local "today"), and the M6 injected `IRandomSource`.
Where a single `Normal` candidate exists the weighted draw must return it, so the
persisted `metadata_json` and the applied entries become stable assertions of the
service-built context.

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Scope (blank + plain `recipe` dates replaced; special/eat-out/fend/locked/vacation preserved and shown unchanged; Fill Empty touches blank dates only) | `BatchRerollPreviewIntegrationTests.Blank_and_plain_recipe_dates_are_proposed`, `Locked_vacation_special_and_non_recipe_dates_are_preserved`; `BatchRerollMatrixIntegrationTests.Fill_empty_proposes_only_blank_dates_and_preserves_existing_entries`, `Fill_empty_on_a_fully_occupied_range_is_a_no_op` |
| Ranges (7-day = start+6; rest-of-month and Fill Empty reach month end; past start → `CALENDAR_DATE_IN_PAST`; horizon/mode validation) | `BatchRerollPreviewIntegrationTests.Seven_days_preview_ends_at_start_plus_six`, `Rest_of_month_preview_ends_at_the_month_end`, `Preview_rejects_a_past_start_date`, `Preview_rejects_an_unknown_mode` |
| Vacation/locks (vacation skipped and never aborts; locked preserved but still counted as occurrence/protein context) | `BatchRerollMatrixIntegrationTests.Vacation_date_is_skipped_without_aborting_the_batch`, `Locked_date_is_unchanged_and_never_written`, `Locked_occurrence_still_counts_as_scoring_context`, `Locked_special_entry_is_preserved` |
| Sequential context (an earlier pick in the same preview suppresses a later repeat; existing/future occurrences still count) | `BatchRerollPreviewIntegrationTests.Sequential_context_counts_an_earlier_pick_in_the_same_preview`; `BatchRerollMatrixIntegrationTests.Locked_occurrence_still_counts_as_scoring_context` |
| Preview (persists no entries/history; Try Another yields a different proposal and a new token; expired/consumed → `BATCH_DRAFT_EXPIRED`; empty pool → `RANDOMIZER_NO_CANDIDATES`) | `BatchRerollPreviewIntegrationTests.Preview_writes_no_calendar_rows_and_creates_one_draft_with_a_fifteen_minute_ttl`, `A_second_preview_supersedes_the_first`, `Preview_fails_when_no_normal_candidate_exists`; `BatchApplyIntegrationTests.Try_another_supersedes_the_first_draft_and_the_superseded_token_is_expired`, `Try_another_can_yield_a_different_proposal`, `Apply_rejects_an_expired_draft`, `Apply_rejects_an_already_applied_draft` |
| Apply (exact previewed recipes; atomicity; one history row per changed date sharing one `batch_operation_id`; `metadata_json` shape; changed target date → `BATCH_CONFLICT`; actor scoping; no audit rows) | `BatchApplyIntegrationTests.Apply_fills_blanks_and_replaces_a_recipe_with_the_previewed_recipes`, `Apply_writes_one_history_row_per_changed_date_sharing_one_batch_operation_id`, `Apply_writes_the_documented_metadata_json`, `Apply_conflict_on_a_later_date_writes_nothing`, `Apply_rejects_a_target_date_that_became_historical_after_the_preview`, `Apply_hides_another_actors_draft`, `Apply_rejects_a_malformed_token`, `Apply_rejects_a_missing_token`, `Apply_writes_no_audit_rows` |
| Concurrency (two concurrent applies → one wins, the other conflict/expired, no 500; apply racing a single-date edit serializes; overlapping drafts don't deadlock) | `BatchRerollMatrixIntegrationTests.Two_concurrent_applies_of_the_same_draft_allow_exactly_one_success`, `Apply_racing_a_single_date_reroll_serializes_without_error`, `Apply_racing_a_single_date_put_never_500s_and_keeps_one_entry`, `Concurrent_applies_of_overlapping_drafts_do_not_deadlock` |
| Undo (reverts every changed date exactly; later change → `UNDO_CONFLICT`; missing/already-undone → `UNDO_EXPIRED`; now-historical → `CALENDAR_DATE_IN_PAST`; one undo row per date; concurrent undo applies once; Fill Empty restores blanks) | `BatchUndoIntegrationTests.Undo_restores_every_changed_date_exactly`, `Undo_writes_one_history_row_per_date_sharing_one_new_batch_operation_id`, `Undo_after_a_later_change_to_one_date_conflicts_and_reverts_nothing`, `Undo_twice_returns_expired`, `Undo_of_an_unknown_batch_returns_expired`, `Undo_of_a_batch_that_is_now_historical_returns_date_in_past`, `Undo_with_a_malformed_id_returns_validation_failed`, `Undo_of_a_fill_empty_batch_restores_blank_dates`, `Concurrent_undoes_of_the_same_batch_apply_once_and_the_loser_is_expired` |
| Permissions (`calendar.edit` on preview/apply/fill/batch-undo; member without it → 403; unauthenticated handling consistent with M6) | `BatchRerollPreviewIntegrationTests.Preview_permission_matrix`; `BatchApplyIntegrationTests.Apply_permission_matrix`; `BatchUndoIntegrationTests.Undo_permission_matrix` |
| Timezone (ranges/context use household-local dates via `HouseholdClock`) | Injected `TestTimeProvider` in every batch integration fixture (`HouseholdClockTests` covers the clock itself); ranges and preserved/unchanged decisions are computed on household-local dates |
| UI (menu availability, wizard diff, Try Another/Apply/Cancel, disabled states, batch Undo banner, mobile behavior) | `frontend/src/features/calendar/BatchReroll.test.tsx`; `frontend/src/features/calendar/RerollUndo.test.tsx`; hermetic Playwright `e2e/calendar.spec.ts` and `e2e/app.spec.ts` |

## Evidence at completion

Recorded on the M7-10 commit (branch `feature/m7-batch-planning`).

| Gate | Result |
|------|--------|
| Backend Release build (`FateAndFridge.Api`, `FateAndFridge.Api.Tests`) | 0 warnings / 0 errors |
| Backend full suite (`FateAndFridge.Api.Tests`) | 1818 passed, 0 failed |
| Frontend lint / format / typecheck (local mirror) | clean |
| Vitest | 266 passed |
| Playwright (hermetic) | 37 passed / 2 pre-existing stack skips |
| `dotnet format --verify-no-changes --no-restore` | clean |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |

Independent review outcome: the backend was approved with one MEDIUM finding
fixed on the same branch in commit `96047a3` (`fix(batch): apply historical
re-check, undo ordering, draft cleanup, docs (#97, #102)`) — Apply re-checks
that a target date has not become historical since the preview, batch undo
orders/cleans up its history rows consistently, and stale drafts are cleaned up.
The batch UI was reviewed and approved.

The M7-10 full-suite gate also caught one stale expectation missed by the
per-area batteries: `M2SchemaBaselineModelTests.All_expected_entities_are_mapped`
still listed 24 model types and did not include the M7 `CalendarRerollDraft`
entity. It was updated in the M7-10 commit; the full suite then passed.

## Live deployment verification

_To be completed by the Lead under the milestone completion gate
(`AGENTS.md` → "Milestone completion gate"): rebuild/redeploy from the completed
commit via `docker-admin` (`scripts/deploy.sh up`), then record the deploy-host
checkout hash, image id/digest, the applied migration, the health response, a
live M7 route proof, and the live `/progress` bundle evidence below. Do not
invent digests or health responses._

- Deploy-host checkout: _pending_
- Image identity: _pending_
- Migrations applied on startup: _pending_ (`M7BatchPlanning` expected)
- Health: _pending_
- Live route proof: _pending_ (e.g. `POST /api/v1/calendar/reroll/preview`
  present, not `404`/`405`)
- Live `/progress`: _pending_ (`m7-complete` and `Batch Planning` shipped;
  summary 8 of 15)
- PostgreSQL remains unpublished (no host port); no volumes/datasets deleted
  during the deploy: _pending_
