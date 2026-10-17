# M11 Test Matrix — Statistics and History

Status: **coverage map for M11-07** (2026-10-17, branch `main`). Feeds
`docs/M11_VERIFICATION.md` at M11-08. Source of truth for the battery is
`docs/M11_PLAN.md` §4 plus the locked decisions §1 (esp. §1.3, §1.4, §1.5,
§1.12, §1.13, §1.19, §1.20, §1.22–§1.26, §1.28, §1.29); the shipped contracts
are `docs/API.md` §14 (statistics) and §23 (the recipes list).

This file records, for every §4 battery area, the primary automated test(s) that
cover it. It is the "audit result plus added gaps" map: the per-issue suites
(M11-01…M11-06) were checked first and only the missing bullets were added in:

- `backend/tests/FateAndFridge.Api.Tests/M11StatisticsBatteryIntegrationTests.cs`
  **(new M11-07)** — occurrence/eaten rules, historical integrity, the
  household-local window boundaries, the rankings, the protein distribution;
- `backend/tests/FateAndFridge.Api.Tests/M11MealHistoryIntegrationTests.cs`
  **(extended M11-07)** — the complete month-paging walk and the wire shape;
- `backend/tests/FateAndFridge.Api.Tests/M11StatisticsIntegrationTests.cs`
  **(extended M11-07)** — the recipes-list gate and edit-implies-view;
- `backend/tests/FateAndFridge.Api.Tests/PermissionKeysTests.cs`
  **(extended M11-07)** — no eighth permission key;
- `backend/tests/FateAndFridge.Api.Tests/M11StatisticsPerformanceTests.cs`
  **(new M11-07)** — the TESTING §12 baseline.

## How to run the matrix

Backend gates (run the test binary directly with `-class` filters — `dotnet
test` misreports the MTP runner):

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet format --verify-no-changes
dotnet build -c Release FateAndFridge.slnx
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
BIN=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
$BIN -class "*M11StatisticsBattery*" -class "*M11StatisticsIntegration*" \
     -class "*M11MealHistory*" -class "*MealOccurrenceRules*" \
     -class "*HouseholdDateLabels*" -class "*PermissionKeysTests*"
$BIN -class "*M11StatisticsPerformance*" -showLiveOutput
```

Every M11 integration class shares one Testcontainers/PostgreSQL 17 container
via `M11StatisticsFixture` (the `M11 statistics` collection) and resets the
volatile rows per test. M11 adds **no schema**, so
`has-pending-model-changes` is expected to be empty. The frontend half of the
battery (the statistics page, the history browser, and the per-recipe metrics on
the cards) is owned by the M11-04/M11-05/M11-06 suites —
`frontend/src/features/statistics/StatisticsPage.test.tsx`,
`frontend/src/features/statistics/MealHistoryBrowser.test.tsx`,
`frontend/src/features/recipes/RecipesBrowser.test.tsx`, and
`frontend/e2e/statistics.spec.ts`/`recipes.spec.ts`.

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Occurrence — today is not eaten and is Next Planned | `M11StatisticsBatteryIntegrationTests.Today_is_only_next_planned_and_is_never_eaten` (times_eaten null, rankings empty, today still a meal); `M11StatisticsIntegrationTests.Recipe_metrics_endpoint_splits_eaten_and_planned` |
| Occurrence — a past entry is eaten and not planned | `M11StatisticsBatteryIntegrationTests.A_past_meal_is_eaten_and_has_no_next_planned`; `M11StatisticsIntegrationTests.Eaten_is_strictly_past_and_today_is_not_eaten` |
| Occurrence — `host_family_dinner`/`holiday`/`misc` carrying a recipe count | `M11StatisticsBatteryIntegrationTests.Only_recipe_carrying_types_feed_recipe_metrics_but_every_type_is_a_meal`; `M11StatisticsIntegrationTests.Recipe_carrying_specials_count_and_eat_out_fend_and_go_to_do_not` |
| Occurrence — `eat_out`/`fend_for_yourself`/`go_to_family_dinner`/vacation never feed recipe metrics (but appear in `meal_count_*` and event counts) | `M11StatisticsBatteryIntegrationTests.Only_recipe_carrying_types_feed_recipe_metrics_but_every_type_is_a_meal` (adds go-to/vacation, and a recipe id on a non-occurrence type); `M11StatisticsIntegrationTests.Recipe_carrying_specials_count_and_eat_out_fend_and_go_to_do_not`; unit `MealOccurrenceRulesTests.The_occurrence_predicate_is_the_include_groceries_flag` |
| Historical integrity — a retired recipe still counts with its badge | `M11StatisticsIntegrationTests.A_retired_recipe_keeps_its_meals_and_carries_its_badge` |
| Historical integrity — a hard-deleted recipe's meals count as a tombstone and no total drops | `M11StatisticsBatteryIntegrationTests.A_hard_deleted_recipe_keeps_every_meal_and_no_total_drops` (ranked total, `meal_count_year`, and `never_eaten` all survive the delete); `M11StatisticsIntegrationTests.A_snapshot_only_occurrence_counts_as_a_deleted_tombstone`, `.Recipe_metrics_endpoint_is_not_found_for_unknown_and_hard_deleted_recipes` |
| Historical integrity — a renamed recipe is not double-counted | `M11StatisticsBatteryIntegrationTests.A_renamed_recipe_is_counted_once_and_never_double_counted`; `M11StatisticsIntegrationTests.Meals_group_by_recipe_id_or_normalized_snapshot_name` |
| Windows — "this month"/"this year" are the household calendar windows (a UTC-naive implementation fails a boundary test) | `M11StatisticsBatteryIntegrationTests.The_calendar_year_is_the_household_year_not_utc` (Pacific/Auckland: 2025-12-31T11:30Z is household 2026-01-01); `M11StatisticsBatteryIntegrationTests.The_calendar_month_is_the_household_month_not_utc` (2026-02-28T12:00Z is household March) |
| Windows — all-time rankings are unaffected by the window | `M11StatisticsBatteryIntegrationTests.The_calendar_year_is_the_household_year_not_utc`, `.The_calendar_month_is_the_household_month_not_utc` (the out-of-window meal still ranks while `meal_count_*` excludes it); unit `HouseholdClockTests`, `HouseholdDateLabelsTests` |
| Rankings — most eaten ordered by count with deterministic ties | `M11StatisticsIntegrationTests.Most_eaten_returns_the_top_ten_with_a_deterministic_name_tie_break`; `M11StatisticsBatteryIntegrationTests.Both_rankings_are_bounded_to_ten_with_deterministic_ties` |
| Rankings — least recently eaten ordered by last-eaten date, never-eaten excluded | `M11StatisticsIntegrationTests.Least_recently_eaten_is_oldest_first_and_excludes_never_eaten`; `M11StatisticsBatteryIntegrationTests.Both_rankings_are_bounded_to_ten_with_deterministic_ties` (the top-10 bound on this list) |
| Rankings — bounded/sane on an empty household; `never_eaten` count | `M11StatisticsIntegrationTests.Empty_household_reports_household_local_windows_and_zeroed_metrics`; `never_eaten` also asserted in `.Least_recently_eaten_is_oldest_first_and_excludes_never_eaten` and the integrity tests |
| Protein distribution — only recipe occurrences with a primary protein appear; multi-protein counts toward **each** bar (§1.12); `no_primary_protein` explicit; no-recipe days excluded | `M11StatisticsBatteryIntegrationTests.A_multi_protein_occurrence_counts_toward_every_protein_bar` (one occurrence, three bars, sum > occurrence count); `M11StatisticsIntegrationTests.Protein_distribution_counts_recipe_occurrences_and_reports_no_primary_protein` |
| Event counts — all seven types over the household calendar year, including recipe-carrying specials | `M11StatisticsBatteryIntegrationTests.Only_recipe_carrying_types_feed_recipe_metrics_but_every_type_is_a_meal`; `M11StatisticsIntegrationTests.Special_event_counts_cover_all_seven_types_over_the_calendar_year`, `.Meal_counts_use_the_household_month_and_year_and_count_every_type`, `.Recipe_carrying_specials_count_and_eat_out_fend_and_go_to_do_not` |
| History — months newest-first with correct labels | `M11MealHistoryIntegrationTests.Month_selection_pages_one_month_per_step_inside_the_range`, `.Empty_history_defaults_to_the_last_twelve_months_and_the_newest_month` |
| History — filters (entry type, recipe id **or** snapshot name, protein) narrow and AND-combine | `M11MealHistoryIntegrationTests.The_entry_type_filter_is_and_combined_with_the_others`, `.The_protein_filter_matches_the_rows_primary_protein`, `.A_renamed_recipe_keeps_its_meal_and_the_stored_snapshot_stays_filterable`, `.A_filter_that_matches_nothing_returns_an_empty_page_for_the_default_month` |
| History — malformed range / range > 366 days / month outside the range / bad filter values → `400 VALIDATION_FAILED` | `M11MealHistoryIntegrationTests.The_range_is_capped_at_366_days_and_malformed_ranges_are_rejected`, `.A_malformed_or_out_of_range_month_is_rejected`, `.The_entry_type_filter_is_and_combined_with_the_others`, `.The_recipe_and_protein_filters_reject_malformed_values` |
| History — paging by month is stable and complete | `M11MealHistoryIntegrationTests.Walking_the_month_cursors_visits_every_month_in_the_range_exactly_once` (walks `paging.newer`/`older` newest→oldest→newest, every month once, empty months included) |
| History — only strictly-past days appear; entry-only (no filler) | `M11MealHistoryIntegrationTests.Only_strictly_past_days_with_entries_appear_newest_first` |
| History — the shipped wire shape (`days` is a **sibling** of `month`) | `M11MealHistoryIntegrationTests.The_month_page_keeps_days_as_a_sibling_of_month` |
| Permissions — `recipes.view` on overview and per-recipe | `M11StatisticsIntegrationTests.Permission_matrix_gates_the_overview`, `.Permission_matrix_gates_the_per_recipe_metrics` |
| Permissions — `recipes.view` on the recipes list | `M11StatisticsIntegrationTests.Recipes_list_is_gated_by_recipes_view` |
| Permissions — `calendar.view` on history | `M11MealHistoryIntegrationTests.Permission_matrix_gates_history_by_calendar_view` |
| Permissions — 401 unauthenticated, 403 without the key, single-user system actor and Admin full access | the four matrix tests above plus `M11StatisticsIntegrationTests.Single_user_system_actor_reads_without_a_session`, `M11MealHistoryIntegrationTests.Single_user_system_actor_reads_history_without_a_session` |
| Permissions — a member with `calendar.edit`/`recipes.edit` (edit-implies-view) is served | `M11StatisticsIntegrationTests.Edit_implies_view_serves_the_statistics_and_history_reads` |
| Permissions — no eighth permission key exists | `PermissionKeysTests.Statistics_and_history_reads_introduce_no_eighth_permission_key` |
| Performance — 2,000 recipes / 10 years: overview and one history month within 1s, no N+1, the recipes list with metrics bounded | `M11StatisticsPerformanceTests.The_baseline_stays_under_one_second_without_n_plus_one_queries` |
| UI — statistics cards/lists/bars accessible; history browser read-only; per-recipe metrics + retired badge; empty/error states | `frontend/src/features/statistics/StatisticsPage.test.tsx`, `MealHistoryBrowser.test.tsx`, `RecipesBrowser.test.tsx`, `frontend/e2e/statistics.spec.ts`, `recipes.spec.ts` (M11-04/M11-05/M11-06; the live gate is M11-08) |
| No schema change and no new API beyond `docs/API.md` §14 | Structural: M11 adds no migration (`has-pending-model-changes` is empty) and only the three §14 routes; asserted by `M1SchemaBaseline*` plus the migration gate |

## Performance baseline result

`M11StatisticsPerformanceTests` seeds **2,000 recipes**, **20 proteins** (every
tenth recipe lists two), and **ten years of daily meal history** (~3,650
entries), then measures the endpoints on a warm host with a counting
`DbCommandInterceptor`:

| Call | Commands | Latency (max of the runs) |
|------|----------|---------------------------|
| `GET /statistics/overview` | 8 | 101 ms |
| `GET /statistics/history?month=2026-10` | 5 | 43 ms |
| `GET /recipes?limit=100&sort=most_eaten` (with the §12 metrics) | 8 | 100 ms |

The seeding step took 2.2 s; the whole class ran in ~12 s wall clock (including
the shared container start). Both §1.28 bars (1 s) are met with wide margin, the
command counts are a small constant unchanged between calls, and the recipes
list returns `total = 2000` with 100 items and the embedded metrics from one
aggregate query. **No index migration is needed** (§1.29 stays unexercised).

## Audit result

**M11-07 (statistics/history battery) — the per-issue suites were strong; the
§4 gaps were closed, and no product defect was found.**

Existing coverage already proved the eaten/today split
(`M11StatisticsIntegrationTests`), the retired badge, the snapshot tombstone and
the renamed identity, the household windows, the top-10 most-eaten bound, the
least-recently-eaten order, the empty household, the month/year and special-event
counts, the protein distribution, the default history range, the strictly-past
entry-only day set, the day row shape, the filters, the 366-day cap and the
malformed-input rejections, the month step paging, the 401/403/Admin/single-user
matrix, and the history wire shape.

M11-07 added: the **today-only** / **past-only** per-recipe definitions; the
**go-to-family-dinner and vacation** exclusions (with a recipe id attached, and
with every type still counted as a meal); the **hard-delete no-total-drops**
assertion; a focused **renamed-identity** assertion; the **UTC-naive-failing
year and month boundary** tests with a non-UTC household timezone and an injected
clock; the **least-recently-eaten top-10 bound**; the **multi-protein
per-protein** distribution sum; the **complete month-paging walk**; an explicit
assertion of the **`days` sibling** wire shape; the **recipes-list permission**;
the **edit-implies-view** member matrix; the **no-eighth-key** guard; and the
**TESTING §12 performance baseline** with an N+1 command-count proof. No §4
bullet failed, so **no production code changed**.

## Deferred items (not covered here)

- The **real-stack/live** verification of the statistics and history views is the
  M11-08 gate (`AGENTS.md` → "Milestone completion gate"), not a hermetic
  backend assertion.
- **Hard deletion** (M12) is only simulated here via raw SQL, matching the
  existing per-issue tests; the M12 workflow itself is out of M11.
- **Grocery ingredient frequency**, advanced analytics (average days between
  repeats, favorite usage, cuisine trends, household preference trends),
  **Times Planned**, nutrition/macros, and any charting library are deferred
  (`docs/M11_PLAN.md` §6, `docs/FUTURE_FEATURES.md`).
- A **pixel-level accessibility audit** of the statistics/history UI is covered
  by the M11-04/M11-05 component and Playwright suites; the M11-08 sweep
  performs the independent review.
