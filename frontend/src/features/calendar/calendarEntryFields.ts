import type { CalendarEntry, CalendarEntryType, CalendarWriteRequest } from '../../api/calendar'

/**
 * The editable meal-defining fields of a calendar entry. This is the single
 * source of the per-type field set shared by the ordinary editor
 * (`DateDetailDialog`) and the M12 historical correction flow, so the two can
 * never drift apart (docs/M12_PLAN.md §1.9).
 */
export interface CalendarEntryFieldsValue {
  entryType: CalendarEntryType
  recipeId: number | null
  recipeName: string | null
  recipeSearch: string
  dinerCount: string
  restaurantName: string
  locationText: string
  customLabel: string
  notes: string
  iconKey: string
}

/** A blank recipe-type entry, used before a day loads or for a new date. */
export const EMPTY_CALENDAR_ENTRY_FIELDS: CalendarEntryFieldsValue = {
  entryType: 'recipe',
  recipeId: null,
  recipeName: null,
  recipeSearch: '',
  dinerCount: '',
  restaurantName: '',
  locationText: '',
  customLabel: '',
  notes: '',
  iconKey: '',
}

/** Seeds the shared fields from a loaded day entry (or a blank date). */
export function calendarEntryFieldsFromEntry(
  entry: CalendarEntry | null | undefined,
): CalendarEntryFieldsValue {
  return {
    entryType: entry?.entry_type ?? 'recipe',
    recipeId: entry?.recipe?.id ?? null,
    recipeName: entry?.recipe?.name ?? null,
    recipeSearch: '',
    dinerCount: entry?.diner_count != null ? String(entry.diner_count) : '',
    restaurantName: entry?.restaurant_name ?? '',
    locationText: entry?.location_text ?? '',
    customLabel: entry?.custom_label ?? '',
    notes: entry?.notes ?? '',
    iconKey: entry?.icon_key ?? '',
  }
}

/**
 * Builds the full-replace write body exactly like the ordinary calendar
 * editor: a blank optional field is sent as `null`, never omitted, so the
 * correction and the normal upsert share one normalization (docs/API.md §17).
 */
export function toCalendarWriteRequest(value: CalendarEntryFieldsValue): CalendarWriteRequest {
  return {
    entry_type: value.entryType,
    recipe_id: value.recipeId,
    diner_count: value.dinerCount.trim() === '' ? null : Number(value.dinerCount),
    restaurant_name: value.restaurantName.trim() === '' ? null : value.restaurantName,
    location_text: value.locationText.trim() === '' ? null : value.locationText,
    custom_label: value.customLabel.trim() === '' ? null : value.customLabel,
    notes: value.notes.trim() === '' ? null : value.notes,
    icon_key: value.iconKey === '' ? null : value.iconKey,
  }
}
