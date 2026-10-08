# M6 Verification — Randomization Engine

Status: **complete** (2026-10-08, tag `m6-complete`). Tracking issue: #91
(M6-11 exhaustive randomizer tests); the M6-12 sweep is issue #92.

M6 adds the ten `household_settings` randomizer columns and the
`calendar_entry_history.metadata_json` column (migration `M6Randomizer`), the
`RandomizerEngine`/`CalendarRandomizer` services, and the reroll/undo routes. No
other schema changes.

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet build -c Release tests/FateAndFridge.Api.Tests/FateAndFridge.Api.Tests.csproj
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
$ARTIFACTS -class "FateAndFridge.Api.Tests.RandomizerMatrixIntegrationTests"
$ARTIFACTS -class "FateAndFridge.Api.Tests.RandomizerEndpointsIntegrationTests" \
  -class "FateAndFridge.Api.Tests.UndoEndpointsIntegrationTests" \
  -class "FateAndFridge.Api.Tests.RandomizerEngineTests"
dotnet format --verify-no-changes --no-restore
```

Integration tests use Testcontainers/PostgreSQL. `RandomizerMatrixIntegrationTests`
makes selection deterministic with the single-Normal-candidate technique: when
exactly one recipe has `status = Normal` the weighted draw must return it, so the
persisted `metadata_json` becomes a stable assertion of the service-built context.

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Eligibility (Normal allowed; ManualOnly/Retired never selected; empty pool → `RANDOMIZER_NO_CANDIDATES`) | `RandomizerEndpointsIntegrationTests.Reroll_fails_when_only_non_normal_recipes_exist`; every reroll test proves Normal selection |
| Windows (7 back included / 8 excluded; 14 forward included / 15 excluded; special-day near-zero; historical/today/future count) | `RandomizerEngineTests.Repeat_window_boundaries_are_inclusive`, `A_special_day_occurrence_uses_the_special_day_multiplier`, `Past_and_future_occurrences_count_and_the_candidate_date_is_excluded`; `RandomizerMatrixIntegrationTests.Locked_occurrence_on_the_previous_day_still_counts_for_the_repeat_penalty`, `Special_day_occurrence_uses_the_special_day_multiplier`, `Future_occurrence_affects_scoring_but_is_never_modified` |
| Protein (previous/next match penalized once; multiple overlap once; unrelated not penalized) | `RandomizerEngineTests.A_shared_protein_with_the_previous_day_is_penalized_once`, `A_shared_protein_with_the_next_day_is_penalized_once`, `Multiple_shared_proteins_are_penalized_once_and_reported_sorted`, `Two_matching_neighbors_still_penalize_only_once`, `Unrelated_proteins_are_not_penalized`; `RandomizerMatrixIntegrationTests.Shared_primary_protein_with_a_manual_only_neighbor_is_penalized_once`, `An_unrelated_protein_neighbor_does_not_penalize` |
| Favorites (slightly favored among equals; does not beat a strong repeat penalty) | `RandomizerEngineTests.A_favorite_candidate_uses_the_favorite_multiplier`, `Default_multipliers_compose_to_the_documented_product` |
| Spacing (forced repeat picks larger nearest spacing; near/far buckets) | `RandomizerEngineTests.Spacing_buckets_map_to_the_documented_factors`, `A_forced_repeat_prefers_the_candidate_with_the_larger_nearest_spacing`, `The_nearest_of_several_occurrences_drives_spacing`; `RandomizerMatrixIntegrationTests.Spacing_uses_the_nearest_prior_occurrence` |
| Small pool (every candidate in window still yields a result) | `RandomizerEngineTests.An_all_zero_weight_pool_falls_back_to_a_uniform_successful_draw`; `RandomizerMatrixIntegrationTests.Small_pool_single_candidate_inside_the_repeat_window_still_returns_a_result`, `Small_pool_two_candidates_inside_the_repeat_window_still_returns_one` |
| Randomness (repeated runs not trivially identical; OS RNG via the seam; no predictable seed) | `SystemRandomSourceTests`, `RandomizerEngineTests.Selection_is_deterministic_for_a_given_source_value`; `RandomizerMatrixIntegrationTests.Repeated_rerolls_over_the_same_data_are_not_trivially_identical` |
| Reroll applicability (blank fills; non-locked entry replaceable; historical/locked/vacation failures; replacement resets metadata) | `RandomizerEndpointsIntegrationTests.Reroll_fills_a_blank_date_with_a_recipe_and_returns_the_operation`, `Reroll_replaces_an_existing_entry_and_resets_metadata`, `Reroll_rejects_a_historical_date`, `Reroll_rejects_a_locked_date`, `Reroll_rejects_a_vacation_date`, `Reroll_clears_restaurant_location_custom_label_and_icon`, `Concurrent_rerolls_of_the_same_blank_date_serialize_without_error` |
| Operation/undo (one history row with operation id + `metadata_json`; exact restore incl. `icon_key`; later change → `UNDO_CONFLICT`; missing/already-undone → `UNDO_EXPIRED`; permission matrix; no audit rows) | `RandomizerEndpointsIntegrationTests.Reroll_writes_the_documented_metadata_json`, `Reroll_permission_matrix`; `UndoEndpointsIntegrationTests.Undo_restores_the_exact_prior_entry`, `Undo_of_a_reroll_over_a_blank_date_leaves_the_date_blank`, `Undo_after_a_later_lock_conflicts`, `Undo_after_a_later_upsert_conflicts`, `Undo_twice_returns_expired`, `Undo_of_an_unknown_operation_returns_expired`, `Undo_permission_matrix`, `Undo_writes_one_history_row_and_no_audit_rows`, `Concurrent_undoes_of_the_same_operation_apply_once` |
| Settings (each randomizer field range validates on PATCH; appears in Household settings UI) | `HouseholdSettingsIntegrationTests.Patch_round_trips_every_randomizer_field_at_its_minimum`/`_maximum`, `Patch_rejects_randomizer_fields_just_outside_their_range`, `Patch_rejects_spacing_far_below_near_and_accepts_equal_values`; `HouseholdSettingsPatchValidatorTests`; `frontend/src/features/settings/HouseholdSection.test.tsx` |
| Timezone (windows/neighbors/history use household-local dates via `HouseholdClock`) | `HouseholdClockTests`; `RandomizerMatrixIntegrationTests.Household_local_dates_drive_history_and_windows_across_the_utc_boundary` (Pacific/Kiritimati UTC+14 and Pacific/Midway UTC-11) |
| UI (Reroll disabled states, keyboard/mobile operation, Undo banner dismiss) | `frontend/src/features/calendar/RerollUndo.test.tsx` |

## Notes

- `docs/RANDOMIZER.md §15` "Batch" (preview persistence, Try Another, atomic
  Apply) is M7 and is intentionally not covered here.

## Evidence at completion

Recorded on the M6-12 commit (branch `feature/m6-randomization-engine`).

| Gate | Result |
|------|--------|
| Backend Release build (`FateAndFridge.Api`, `FateAndFridge.Api.Tests`) | 0 warnings / 0 errors |
| Backend full suite (`FateAndFridge.Api.Tests`) | 1771 passed, 0 failed |
| Frontend lint / format / typecheck (local mirror) | clean |
| Vitest | 252 passed |
| Playwright (hermetic) | 37 passed / 2 pre-existing stack skips |
| `dotnet format --verify-no-changes --no-restore` | clean |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |

Independent review outcome: three MEDIUM findings were raised and fixed on the
same branch — (1) Undo must be scoped to reroll operations only, (2) Undo must
honour past-date immutability, and (3) concurrent per-date mutations must
serialize via a per-date advisory lock. The fixes are in commit `b63efe3`
(`fix(randomizer): scope undo to rerolls, enforce past-date immutability,
serialize per-date mutations (#89)`).

## Live deployment verification

Rebuilt and redeployed via `docker-admin` using `scripts/deploy.sh up`
(`docker compose up -d --build` on the NAS) from commit `9b83d05`, then verified
live.

- Deploy-host checkout: `git rev-parse --short HEAD` → `9b83d05`, tree clean.
- Image `fate-and-fridge:0.1.0`: before `sha256:20ba2a53…` → after
  `sha256:500d520d78f4531eeb7fbd7f51325927bdea759ba318162610acd1dae95524fb`
  (created 2026-10-08T05:16:33-05:00). Local-only build, so no registry
  `RepoDigests`; the image id is the identity.
- Migrations applied on startup: `Applying migration '20261007055546_M6Randomizer'`
  then `Applying migration '20261008054900_M6OperationMetadata'`, then
  `Database migrations applied`.
- Health: `GET /api/v1/health` → `200`
  `{"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`;
  the app container reports `healthy`.
- Live route proof (unauthenticated probes, no mutation): `POST /api/v1/calendar/2026-10-15/reroll`
  → `422` and `POST /api/v1/operations/{uuid}/undo` → `410` (both routes present,
  not `404`/`405`; an unknown route returns `404` as a baseline).
- Live `/progress`: the served bundle `/assets/index-DOfsZ8JZ.js` contains
  `m6-complete` and `Randomization Engine`, so the milestone manifest is shipped
  and M6 renders Complete (summary 7 of 15, computed client-side).
- PostgreSQL remains unpublished (no host port); no volumes/datasets were
  deleted during the deploy.
