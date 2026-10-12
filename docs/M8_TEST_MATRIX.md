# M8 Test Matrix — Groceries

Status: **coverage map for M8-09** (2026-10-12, branch `feature/m8-groceries`).
Feeds `docs/M8_VERIFICATION.md` at M8-10. Source of truth for the battery is
`docs/M8_PLAN.md` §4 plus the locked decisions §1.1–§1.16.

This file records, for every §4 battery area, the primary automated test(s) that
cover it — backend first, then frontend. It is the "audit result plus added
gaps" map: existing per-issue suites were checked first and only the missing
bullets were added in
`backend/tests/FateAndFridge.Api.Tests/M8GroceryMatrixIntegrationTests.cs` and
`frontend/e2e/groceries.spec.ts` (marked **(new M8-09)** below).

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet build -c Release FateAndFridge.slnx
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
$ARTIFACTS \
  -class "FateAndFridge.Api.Tests.M8GroceryMatrixIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8GroceryListIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8ManualItemAndPreferenceIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8StapleAndCheckStateIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8GroceriesSchemaIntegrationTests" \
  -class "FateAndFridge.Api.Tests.M8GroceriesSchemaModelTests" \
  -class "FateAndFridge.Api.Tests.GroceryAggregationServiceTests" \
  -class "FateAndFridge.Api.Tests.GroceryItemIdentityTests"
dotnet format --verify-no-changes
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
```

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers): `npm run lint && npm run
format:check && npm run typecheck && npm test`, then `npm run build`, then
`npx playwright test e2e/groceries.spec.ts`.

The M8 integration fixtures (all in the `M8 grocery list` collection) share one
Testcontainers/PostgreSQL container, inject a deterministic `TestTimeProvider`,
and set the household timezone to `America/Chicago` so household-local "today"
is distinct from UTC.

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Aggregation — same canonical ingredient + compatible units combine (1 lb + 8 oz → 1.5 lb; tbsp + cup) | `GroceryAggregationServiceTests.Compatible_mass_units_combine_into_the_largest_present_unit`, `GroceryAggregationServiceTests.Compatible_volume_units_combine_into_the_largest_present_unit`; `M8GroceryMatrixIntegrationTests.Mass_units_combine_regardless_of_the_order_they_appear` **(new M8-09)**, `M8GroceryMatrixIntegrationTests.Volume_tablespoons_and_teaspoons_combine_into_tablespoons` **(new M8-09)**; `M8GroceryListIntegrationTests.Compatible_mass_units_combine_into_the_largest_present_unit` |
| Aggregation — incompatible dimensions stay separate | `GroceryAggregationServiceTests.Incompatible_dimensions_stay_separate`, `Mass_and_volume_of_one_ingredient_stay_separate`, `Different_nonconvertible_units_stay_separate`; `M8GroceryListIntegrationTests.Incompatible_dimensions_stay_separate` |
| Aggregation — count/unitless combine by the same unit; different count units stay separate | `GroceryAggregationServiceTests.Unitless_quantities_combine_by_count`, `Same_count_unit_combines_by_count`, `Different_count_units_stay_separate`, `Unitless_counts_stay_separate_from_an_explicit_count_unit` |
| Aggregation — aliases resolve to the canonical item | `M8GroceryListIntegrationTests.Alias_resolves_to_canonical_through_a_merge_redirect`; `GroceryAggregationServiceTests.Lines_sharing_a_canonical_ingredient_combine_across_display_names`, `Different_canonical_ingredients_with_the_same_name_stay_separate`; `GroceryItemIdentityTests.Different_ingredients_never_collapse` |
| Aggregation — preparation notes are not part of identity | Structural: `GroceryIngredientInput` deliberately has **no** preparation-note field (doc comment in `Services/GroceryAggregationService.cs`), so a note can never enter identity or math. Covered by that type contract, not a runtime test. |
| Scope — only `include_groceries` entries contribute; eat-out/fend/go-to/vacation contribute nothing; specials with a recipe do | `M8GroceryMatrixIntegrationTests.Entry_type_scope_matrix` **(new M8-09)** (all eight `CalendarEntryType`s, including specials that do *not* opt in); `M8GroceryListIntegrationTests.Only_include_groceries_entries_contribute`, `Non_grocery_entry_types_contribute_nothing`, `Special_entries_with_a_recipe_contribute`, `Retired_recipe_still_contributes_via_its_ingredient_rows` |
| Display — largest-present unit | `GroceryAggregationServiceTests.The_largest_unit_present_in_the_bucket_is_selected`, `A_single_present_unit_is_used_as_is`, `Three_unit_mass_bucket_uses_the_largest_unit_in_any_position` |
| Display — exact fractions (½ ⅓ ¾) else two decimals | `GroceryAggregationServiceTests.Exact_quantities_render_as_common_fractions`, `Non_fraction_quantities_render_with_up_to_two_decimals`; `M8GroceryMatrixIntegrationTests.Exact_cooking_fractions_render_as_fractions` **(new M8-09)** (⅓, ⅔, ¾, ¼, ⅛, ⅜, ⅝, ⅞) |
| Display — quantity-less lines render without a quantity | `GroceryAggregationServiceTests.Quantity_less_unitless_lines_render_without_a_quantity`, `Quantity_less_nonconvertible_lines_render_without_a_quantity`; `M8GroceryListIntegrationTests.Quantity_less_recipe_line_keeps_its_display_unit`, `Manual_quantity_is_formatted_and_quantity_less_lines_render_empty` |
| Display — attribution lists one row per contributing meal | `GroceryAggregationServiceTests.Attribution_is_one_row_per_contributing_meal`, `Nonconvertible_lines_merge_their_sources_without_quantities`; `M8GroceryListIntegrationTests.Each_contributing_meal_produces_one_attribution_row` |
| Ranges — each preset resolves correctly (3/7/14) | `M8GroceryListIntegrationTests.Preset_resolves_to_its_fixed_length` |
| Ranges — rest of month (incl. the last-day-of-month single-day case) | `M8GroceryListIntegrationTests.Rest_of_month_ends_on_the_month_boundary`; `M8GroceryMatrixIntegrationTests.Rest_of_month_on_the_last_day_is_a_single_day` **(new M8-09)** |
| Ranges — custom start + days incl. a month boundary | `M8GroceryListIntegrationTests.Custom_range_uses_start_and_day_count`, `Next_seven_days_crosses_a_month_boundary`; `M8GroceryMatrixIntegrationTests.Custom_range_crosses_a_month_boundary` **(new M8-09)** |
| Ranges — past start rejected; >366 days rejected | `M8GroceryListIntegrationTests.Past_start_is_rejected`, `Range_larger_than_366_days_is_rejected`, `Days_equal_to_366_is_allowed_while_367_is_rejected`, `Preset_cannot_be_combined_with_start_or_days`, `Unknown_preset_is_rejected`, `Days_without_start_is_rejected` |
| Ranges — last-used range remembered per user; default Next 7 Days | `M8GroceryListIntegrationTests.Remembered_preference_is_used_when_no_query_is_supplied`, `No_query_defaults_to_next_seven_days`, `Read_does_not_create_or_update_the_preference`; `M8GroceryMatrixIntegrationTests.Remembered_range_is_per_user` **(new M8-09)**; `M8ManualItemAndPreferenceIntegrationTests.Multiuser_preferences_patch_round_trips_through_one_row` |
| Manual/staples — range-scoped visibility (exact `(range_start, range_days)`) | `M8GroceryListIntegrationTests.Manual_items_show_only_for_the_exact_active_range`; `M8ManualItemAndPreferenceIntegrationTests.Manual_item_is_visible_only_for_its_exact_range` |
| Manual/staples — free text + optional ingredient; department default | `M8ManualItemAndPreferenceIntegrationTests.Manual_create_happy_path_returns_201_and_the_item`, `Manual_department_defaults_from_the_linked_ingredient`, `Manual_create_redirects_a_merged_ingredient_to_the_canonical`; `M8GroceryListIntegrationTests.Manual_department_defaults_to_the_linked_ingredient_and_other_is_last` |
| Manual/staples — Save for future creates (idempotently) a staple | `M8ManualItemAndPreferenceIntegrationTests.Save_for_future_creates_one_staple_and_is_idempotent`; `M8StapleAndCheckStateIntegrationTests.Staple_add_to_list_creates_a_range_scoped_manual_item_without_mutating_the_staple` |
| Manual/staples — add-to-list creates a range manual item | `M8StapleAndCheckStateIntegrationTests.Staple_add_to_list_creates_a_range_scoped_manual_item_without_mutating_the_staple`, `Staple_add_to_list_records_the_acting_user`, `Staple_add_to_list_accepts_the_366_day_boundary` |
| Manual/staples — staples are household-wide | `M8GroceryMatrixIntegrationTests.Staples_are_household_wide_across_users` **(new M8-09)**; `M8StapleAndCheckStateIntegrationTests.Staples_list_defaults_to_active_and_include_inactive_reveals_retired` |
| Manual/staples — manual lines stay separate/distinct from recipe buckets | `M8GroceryMatrixIntegrationTests.Manual_line_stays_separate_from_a_same_named_recipe_bucket` **(new M8-09)**; `M8GroceryListIntegrationTests.Manual_quantity_is_formatted_and_quantity_less_lines_render_empty` (asserts `source_type`/`manual_item_id`/empty `sources`) |
| Check state — persists by ingredient+dimension across quantity changes | `M8StapleAndCheckStateIntegrationTests.Checked_state_survives_a_quantity_change_for_the_same_identity`; `GroceryItemIdentityTests.Mass_identity_is_stable_across_units`, `Volume_identity_is_stable_across_units`, `Count_identity_is_unit_specific` |
| Check state — persists across range expansion | `M8GroceryMatrixIntegrationTests.Checked_state_survives_a_range_expansion` **(new M8-09)** |
| Check state — different dimensions do not collapse | `M8StapleAndCheckStateIntegrationTests.Different_dimensions_do_not_collapse_into_one_checkbox`; `GroceryItemIdentityTests.Dimensions_never_collapse` |
| Check state — household-shared across users; checked moves below unchecked within a department | `M8StapleAndCheckStateIntegrationTests.Check_state_is_household_shared_across_users`, `Checked_items_sort_below_unchecked_within_a_department`, `Check_then_uncheck_round_trip_and_idempotency`, `Check_concurrent_first_writes_leave_one_checked_row`; `M8GroceryListIntegrationTests.Items_sort_unchecked_first_alphabetically_then_checked_last` |
| Check state — hide-checked is per-user and persists | `M8GroceryMatrixIntegrationTests.Hide_checked_is_per_user_and_persists` **(new M8-09)**; `M8ManualItemAndPreferenceIntegrationTests.Preferences_patch_persists_hide_checked_and_drives_the_list_range`, `Preferences_hide_checked_update_does_not_revalidate_a_stale_range` |
| Permissions — `groceries.view`/`groceries.edit` matrix on every route; member without edit → 403; single-user full access | `M8GroceryListIntegrationTests.Permission_matrix_gates_the_read`, `Single_user_system_actor_reads_without_a_session`; `M8ManualItemAndPreferenceIntegrationTests.Permission_matrix_gates_manual_items_and_preferences`, `Single_user_preferences_persist_in_application_settings`; `M8StapleAndCheckStateIntegrationTests.Permission_matrix_gates_staples_and_check_state`, `Single_user_system_actor_has_full_access_to_staples_and_check_state`; `M8GroceryMatrixIntegrationTests.Anonymous_manual_patch_and_delete_return_unauthorized` **(new M8-09)** (the previously un-covered manual PATCH/DELETE anon routes) |
| Timezone — range/manual validation uses the household-local today | `M8GroceryMatrixIntegrationTests.List_range_validation_uses_the_household_local_today` **(new M8-09)**, `Manual_range_validation_uses_the_household_local_today` **(new M8-09)**; `M8ManualItemAndPreferenceIntegrationTests.Preferences_custom_range_uses_the_household_local_today` |
| UI — mobile tap targets, sticky headers | `frontend/e2e/groceries.spec.ts` → "groceries: mobile layout keeps department headers sticky and tap targets ≥44px" **(new M8-09)** |
| UI — checkbox UX, hide-checked, manual add/edit, staples | `frontend/src/features/groceries/GroceriesPage.test.tsx` ("checks an item, persists it, and moves it below the unchecked items", "toggles hide-checked and persists it via preferences", "adds a manual item and validates the required name", "edits an existing manual item", "disables every write for a member without groceries.edit"); `frontend/src/features/groceries/StaplesPanel.test.tsx`; `frontend/e2e/groceries.spec.ts` ("checking an item strikes it through and moves it below unchecked", "adds a staple to the list as a manual item") |
| Print — department order, checkbox, attribution, manual items, checked excluded by default with an include option | `frontend/src/features/groceries/GroceryPrintPage.test.tsx` ("renders the range heading, departments in order with Other last, and attribution", "draws a decorative, screen-reader-hidden checkbox before each item", "includes manual items marked with the icon+text Manual badge", "excludes checked items by default and reveals them via the include toggle"); `frontend/e2e/groceries.spec.ts` ("groceries print: departments, attribution, manual badge, and the include-checked toggle", "groceries print: print media hides app chrome and screen-only controls") |

## Audit result

All §4 bullets were already covered by the per-issue M8 suites except the
**(new M8-09)** entries above. The gaps closed were: service-level exact
fractions beyond halves/quarters, mass order independence, the tbsp/tsp volume
merge, the last-day-of-month single-day range, a custom range crossing a month
boundary, the per-user remembered range with two users, the full eight-entry-type
scope matrix (including specials that do not opt in), a manual line staying
separate from a same-named recipe bucket, household-wide staples across two
users, check state surviving a range expansion, per-user hide-checked, the
manual PATCH/DELETE anonymous permission routes, household-local range validation
on the list and manual create paths, and the frontend mobile sticky-header/tap-
target checks.

No §4 battery bullet failed, so no product defect was fixed in M8-09. The one
structural note is aggregation's preparation-note exclusion, which is enforced
by the absence of a note field on `GroceryIngredientInput` rather than by a
runtime assertion.
