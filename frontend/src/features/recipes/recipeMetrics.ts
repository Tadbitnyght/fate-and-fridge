import { formatHouseholdDate } from '../../lib/dateFormat'

/**
 * Formatting for the per-recipe history metrics shared by the recipe browser
 * (photo cards and the compact list) and the recipe detail/editor.
 * Values arrive on the recipes list items and the detail document's
 * `statistics` object. Each is `null` when the recipe has no such history —
 * never a fake zero — and the dates are household-local `yyyy-MM-dd` strings
 * rendered through the shared formatter: never `Date`/`toLocale*`, which would
 * shift the day across a UTC boundary or no-op under invariant globalization.
 */
export interface RecipeHistoryMetrics {
  last_eaten: string | null
  next_planned: string | null
  times_eaten: number | null
}

/** Fallback shown whenever a metric has no history (the established convention). */
export const NO_HISTORY_LABEL = 'No history yet'

/** A household-local metric date, or the shared no-history fallback. */
export function historyDateLabel(value: string | null): string {
  return value === null ? NO_HISTORY_LABEL : formatHouseholdDate(value)
}

/** An eaten-count metric, or the shared no-history fallback. */
export function historyCountLabel(value: number | null): string {
  return value === null ? NO_HISTORY_LABEL : String(value)
}
