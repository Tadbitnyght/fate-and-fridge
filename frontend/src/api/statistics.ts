import { apiRequest } from './client'

/**
 * Typed client for the M11 statistics contract (docs/API.md §14).
 *
 * Reads require `recipes.view` (`calendar.view` for meal history); the
 * single-user system actor and Admins hold both. The server enforces all of
 * that — these types only describe the wire shapes. JSON property names are
 * snake_case, and every date/datetime is household-local (`HouseholdClock`),
 * never a client-side reinterpretation.
 */

/**
 * A ranked recipe's lifecycle badge. `deleted` is a tombstone: the live recipe
 * is gone (M12 hard delete), so `recipe_id` is null and the stored snapshot
 * name carries the history.
 */
export type StatisticsRecipeStatus = 'normal' | 'manual_only' | 'retired' | 'deleted'

export const STATISTICS_RECIPE_STATUS_LABELS: Record<StatisticsRecipeStatus, string> = {
  normal: 'Normal',
  manual_only: 'Manual only',
  retired: 'Retired',
  deleted: 'Deleted',
}

/** The seven special-event entry types (`recipe` is excluded). */
export type SpecialEventType =
  | 'eat_out'
  | 'host_family_dinner'
  | 'go_to_family_dinner'
  | 'holiday'
  | 'misc'
  | 'fend_for_yourself'
  | 'vacation'

/** Canonical display order for the special-event counts (docs/M11_PLAN.md §1.13). */
export const SPECIAL_EVENT_TYPES: SpecialEventType[] = [
  'eat_out',
  'host_family_dinner',
  'go_to_family_dinner',
  'holiday',
  'misc',
  'fend_for_yourself',
  'vacation',
]

export const SPECIAL_EVENT_LABELS: Record<SpecialEventType, string> = {
  eat_out: 'Eat out',
  host_family_dinner: 'Host family dinner',
  go_to_family_dinner: 'Go to family dinner',
  holiday: 'Holiday',
  misc: 'Misc',
  fend_for_yourself: 'Fend for yourself',
  vacation: 'Vacation',
}

/** One household-local calendar window (label plus inclusive start/end dates). */
export interface StatisticsWindow {
  label: string
  start: string
  end: string
}

export interface StatisticsWindows {
  /** Household-local today (`YYYY-MM-DD`). */
  today: string
  /** The household timezone the windows were computed in. */
  timezone: string
  month: StatisticsWindow
  year: StatisticsWindow
}

export interface RankedRecipe {
  /** Null only for a tombstone (`status: 'deleted'`). */
  recipe_id: number | null
  name: string
  status: StatisticsRecipeStatus
  times_eaten: number
  /** Household-local `YYYY-MM-DD`. */
  last_eaten: string
}

export interface ProteinCount {
  protein_id: number
  name: string
  count: number
}

export interface ProteinDistribution {
  proteins: ProteinCount[]
  /** Explicit figure for recipe meals with no primary protein (never dropped). */
  no_primary_protein: number
}

export interface SpecialEventCount {
  entry_type: SpecialEventType
  count: number
}

/** `GET /api/v1/statistics/overview` (docs/API.md §14). */
export interface StatisticsOverview {
  windows: StatisticsWindows
  most_eaten: RankedRecipe[]
  least_recently_eaten: RankedRecipe[]
  never_eaten: number
  meal_count_month: number
  meal_count_year: number
  protein_distribution: ProteinDistribution
  special_event_counts: SpecialEventCount[]
}

export function getStatisticsOverview(): Promise<StatisticsOverview> {
  return apiRequest<StatisticsOverview>('/statistics/overview')
}
