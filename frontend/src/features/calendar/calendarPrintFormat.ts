import {
  CALENDAR_ENTRY_LABELS,
  ICON_ENTRY_TYPES,
  type CalendarEntry,
  type CalendarEntryType,
} from '../../api/calendar'
import type { PrintMonth } from './printRange'

/**
 * Pure content helpers for the printed calendar.
 * The printed cell carries only the day number and a single line of text: the
 * recipe name or the special-event text. Meal photos, lock icons, notes, diner
 * count, and restaurant/location text are never printed, so this module returns
 * exactly the text an icon key the print view may render — nothing else.
 */

/** Month/year heading, e.g. "October 2026" (the household name is never printed). */
export function printMonthHeading(month: PrintMonth): string {
  return month.label
}

/**
 * Text to print for a day's entry.
 * A recipe prints the recipe name (`display_text`).
 * Holiday / misc / host-family-dinner / go-to-family-dinner print their
 * special-event text (`display_text`, i.e. the custom label or recipe name).
 * Eat out and fend for yourself print their fixed type label as text. Their
 * `display_text` may contain a restaurant or location, which must not print.
 * Anything else (vacation) prints its type label when `display_text` is empty.
 */
export function printEntryText(entry: CalendarEntry | null): string | null {
  if (entry === null) {
    return null
  }
  if (entry.entry_type === 'eat_out' || entry.entry_type === 'fend_for_yourself') {
    return CALENDAR_ENTRY_LABELS[entry.entry_type]
  }
  return entry.display_text.length > 0
    ? entry.display_text
    : CALENDAR_ENTRY_LABELS[entry.entry_type]
}

/**
 * The curated special-event icon to print, or `null`. Icons are only carried by
 * the four icon-bearing types; any other type is
 * ignored even if it somehow has an `icon_key`.
 */
export function printIconKey(entry: CalendarEntry | null): string | null {
  if (entry === null || entry.icon_key === null) {
    return null
  }
  return ICON_ENTRY_TYPES.includes(entry.entry_type as CalendarEntryType) ? entry.icon_key : null
}
