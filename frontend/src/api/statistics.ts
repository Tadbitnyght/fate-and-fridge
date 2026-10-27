import type { CalendarEntryType } from './calendar'
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

/* ---------- meal history ---------- */

/**
 * One day of the strictly-past meal log. A day row **is** the entry
 * (`calendar_entries.meal_date` is unique), so there is no blank filler
 * (docs/M11_PLAN.md §1.22).
 */
export interface MealHistoryDay {
  /** Household-local `YYYY-MM-DD`. */
  date: string
  entry_type: CalendarEntryType
  /** Null for a tombstone: the live recipe is gone, so only the name remains. */
  recipe_id: number | null
  /** Live recipe name, else the stored snapshot name, else the entry's display text. */
  name: string
  /** Primary protein name, or null when the entry carries no primary protein. */
  protein: string | null
}

/** The applied household-local range the month was selected inside. */
export interface MealHistoryRange {
  start: string
  end: string
  timezone: string
}

/** The single month page: its key, label, and full calendar-month bounds. */
export interface MealHistoryMonth {
  /** Household-local `YYYY-MM`, matching the `month` request parameter. */
  key: string
  label: string
  start: string
  end: string
}

/** The month cursors: one month per step, both null at the range ends. */
export interface MealHistoryPaging {
  newer: string | null
  older: string | null
}

/**
 * `GET /api/v1/statistics/history` (docs/API.md §14). One month page of the
 * read-only meal log, newest first.
 *
 * The wire shape here matches the shipped server response and the documented
 * contract: `days` is a **sibling** of `month` (docs/API.md §14, the §14
 * example, and `M11MealHistoryIntegrationTests.The_month_page_keeps_days_as_a_sibling_of_month`).
 */
export interface MealHistoryResponse {
  range: MealHistoryRange
  month: MealHistoryMonth
  days: MealHistoryDay[]
  paging: MealHistoryPaging
}

/** Server-side, AND-combined meal-history filters (docs/M11_PLAN.md §1.25). */
export interface MealHistoryParams {
  /** Household-local `YYYY-MM-DD` range bound. */
  start?: string
  /** Household-local `YYYY-MM-DD` range bound. */
  end?: string
  /** Household-local `YYYY-MM` month page; omitted, the server picks the newest. */
  month?: string
  entry_type?: CalendarEntryType
  /** A live recipe id (`5`) or a normalized stored snapshot name (`Grandma's Stew`). */
  recipe?: string
  /** A primary-protein id (the recipes-list `protein_id` shape). */
  protein?: number
}

function historyQuery(params: MealHistoryParams): string {
  const query = new URLSearchParams()
  if (params.start !== undefined && params.start.length > 0) {
    query.set('start', params.start)
  }
  if (params.end !== undefined && params.end.length > 0) {
    query.set('end', params.end)
  }
  if (params.month !== undefined && params.month.length > 0) {
    query.set('month', params.month)
  }
  if (params.entry_type !== undefined) {
    query.set('entry_type', params.entry_type)
  }
  if (params.recipe !== undefined && params.recipe.length > 0) {
    query.set('recipe', params.recipe)
  }
  if (params.protein !== undefined) {
    query.set('protein', String(params.protein))
  }
  const text = query.toString()
  return text.length > 0 ? `?${text}` : ''
}

export function getMealHistory(params: MealHistoryParams = {}): Promise<MealHistoryResponse> {
  return apiRequest<MealHistoryResponse>(`/statistics/history${historyQuery(params)}`)
}
