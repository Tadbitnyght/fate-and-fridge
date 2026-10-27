import { apiRequest } from './client'
import type { RecipeStatus } from './recipes'

/**
 * Typed client for the M5 calendar contract (docs/M5_PLAN.md §2.4).
 *
 * Reads require `calendar.view`; writes require `calendar.edit`. The server
 * enforces historical immutability, range caps, and permissions — these
 * functions only describe the wire shapes. JSON property names are snake_case.
 */

export type CalendarEntryType =
  | 'recipe'
  | 'eat_out'
  | 'fend_for_yourself'
  | 'host_family_dinner'
  | 'go_to_family_dinner'
  | 'holiday'
  | 'misc'
  | 'vacation'

export const CALENDAR_ENTRY_TYPES: CalendarEntryType[] = [
  'recipe',
  'eat_out',
  'fend_for_yourself',
  'host_family_dinner',
  'go_to_family_dinner',
  'holiday',
  'misc',
  'vacation',
]

export const CALENDAR_ENTRY_LABELS: Record<CalendarEntryType, string> = {
  recipe: 'Recipe',
  eat_out: 'Eat out',
  fend_for_yourself: 'Fend for yourself',
  host_family_dinner: 'Host family dinner',
  go_to_family_dinner: 'Go to family dinner',
  holiday: 'Holiday',
  misc: 'Misc',
  vacation: 'Vacation',
}

/** Special types that may carry a curated icon (docs/M5_PLAN.md §1.14). */
export const ICON_ENTRY_TYPES: CalendarEntryType[] = [
  'holiday',
  'misc',
  'host_family_dinner',
  'go_to_family_dinner',
]

export const CALENDAR_ICONS = [
  'turkey',
  'cake',
  'heart',
  'star',
  'pumpkin',
  'gift',
  'snowflake',
  'flag',
  'fireworks',
  'clover',
  'leaf',
  'egg',
] as const

export type CalendarIconKey = (typeof CALENDAR_ICONS)[number]

export interface CalendarRecipe {
  id: number
  name: string
  image_url: string | null
  status: RecipeStatus
}

export interface CalendarEntry {
  id: number
  entry_type: CalendarEntryType
  recipe: CalendarRecipe | null
  display_text: string
  is_locked: boolean
  diner_count: number | null
  effective_diner_count: number
  restaurant_name: string | null
  location_text: string | null
  custom_label: string | null
  notes: string | null
  icon_key: string | null
  include_groceries: boolean
  blocks_automation: boolean
  created_at: string
  updated_at: string
}

export interface CalendarOccurrence {
  date: string
  entry_type: CalendarEntryType
}

export interface CalendarRepeatWarning {
  recipe_id: number
  window_start: string
  window_end: string
  occurrences: CalendarOccurrence[]
}

export interface CalendarDay {
  date: string
  is_today: boolean
  is_historical: boolean
  can_edit: boolean
  entry: CalendarEntry | null
  repeat_warning: CalendarRepeatWarning | null
}

export interface CalendarRange {
  start: string
  end: string
  today: string
  week_starts_on: number
  entries: CalendarDay[]
}

/** Compact reroll handle returned by the M6 reroll route (docs/API.md "Reroll (M6)"). */
export interface CalendarOperation {
  id: string
  undo_expires_at: string | null
}

/** A reroll response is the M5 day document plus the undo handle. */
export interface CalendarRerollResponse extends CalendarDay {
  operation: CalendarOperation
}

/** Batch reroll range modes (M7, docs/API.md "Batch reroll preview"). */
export type BatchRerollMode = 'seven_days' | 'rest_of_month' | 'fill_empty'

/** Persisted preview draft handle with a 15-minute TTL. */
export interface BatchPreviewDraft {
  token: string
  mode: BatchRerollMode
  start_date: string
  end_date: string
  expires_at: string
}

/** Proposed replacement for one previewed day. */
export interface BatchProposedEntry {
  entry_type: CalendarEntryType
  recipe_id: number
  display_text: string
}

export type BatchPreviewDayStatus = 'proposed' | 'unchanged'

/**
 * Why a day is left unchanged: locked, vacation, a special/non-recipe entry,
 * or an existing plain non-recipe (eat out / fend) entry.
 */
export type BatchUnchangedReason = 'locked' | 'vacation' | 'special' | 'existing_non_recipe'

/** One day in a batch reroll preview diff. */
export interface BatchPreviewDay {
  date: string
  status: BatchPreviewDayStatus
  unchanged_reason: BatchUnchangedReason | null
  current: CalendarEntry | null
  proposed: BatchProposedEntry | null
}

export interface BatchPreviewResponse {
  draft: BatchPreviewDraft
  days: BatchPreviewDay[]
}

export interface BatchApplyResponse {
  operation: CalendarOperation
  days: CalendarDay[]
  changed_count: number
}

export interface BatchUndoResponse {
  operation: CalendarOperation
  days: CalendarDay[]
  reverted_count: number
}

export interface CalendarRepeatCheck {
  is_repeat: boolean
  window_start: string
  window_end: string
  occurrences: CalendarOccurrence[]
}

export interface CalendarWriteRequest {
  entry_type: CalendarEntryType
  recipe_id?: number | null
  diner_count?: number | null
  restaurant_name?: string | null
  location_text?: string | null
  custom_label?: string | null
  notes?: string | null
  icon_key?: string | null
}

/**
 * The M12 historical-correction body (docs/API.md §17): the ordinary
 * full-replace write fields plus the dangerous-action guard fields. `reason` is
 * required (≤ 500 characters) and `confirmation` must equal the corrected date
 * as `yyyy-MM-dd`. `lock_acknowledged` is only meaningful for a locked date.
 */
export interface CalendarCorrectionRequest extends CalendarWriteRequest {
  reason: string
  confirmation: string
  lock_acknowledged?: boolean
}

export function getCalendarRange(start: string, end: string): Promise<CalendarRange> {
  return apiRequest<CalendarRange>(`/calendar?start=${start}&end=${end}`)
}

export function getCalendarDay(date: string): Promise<CalendarDay> {
  return apiRequest<CalendarDay>(`/calendar/${date}`)
}

export function checkCalendarRepeat(date: string, recipeId: number): Promise<CalendarRepeatCheck> {
  return apiRequest<CalendarRepeatCheck>(`/calendar/${date}/repeat-check?recipe_id=${recipeId}`)
}

export function putCalendarDay(date: string, request: CalendarWriteRequest): Promise<CalendarDay> {
  return apiRequest<CalendarDay>(`/calendar/${date}`, { method: 'PUT', body: request })
}

/**
 * Admin-only correction of one past date (M12, docs/API.md §17). The server
 * enforces past-only, the lock acknowledgement, and the reason/typed-date
 * confirmation; the response is the corrected M5 day document.
 */
export function correctCalendarDay(
  date: string,
  request: CalendarCorrectionRequest,
): Promise<CalendarDay> {
  return apiRequest<CalendarDay>(`/calendar/${date}/correction`, { method: 'POST', body: request })
}

export function deleteCalendarDay(date: string): Promise<void> {
  return apiRequest<void>(`/calendar/${date}`, { method: 'DELETE' })
}

export function lockCalendarDay(date: string): Promise<CalendarDay> {
  return apiRequest<CalendarDay>(`/calendar/${date}/lock`, { method: 'POST' })
}

export function unlockCalendarDay(date: string): Promise<CalendarDay> {
  return apiRequest<CalendarDay>(`/calendar/${date}/unlock`, { method: 'POST' })
}

/** Replaces a date with a randomly selected Normal recipe (M6, `calendar.edit`). */
export function rerollCalendarDay(date: string): Promise<CalendarRerollResponse> {
  return apiRequest<CalendarRerollResponse>(`/calendar/${date}/reroll`, { method: 'POST' })
}

/** Restores the pre-reroll state of an operation (M6, `calendar.edit`). */
export function undoCalendarOperation(operationId: string): Promise<CalendarDay> {
  return apiRequest<CalendarDay>(`/operations/${operationId}/undo`, { method: 'POST' })
}

/** Builds a batch reroll preview draft (M7, `calendar.edit`). */
export function previewBatchReroll({
  startDate,
  mode,
}: {
  startDate: string
  mode: BatchRerollMode
}): Promise<BatchPreviewResponse> {
  return apiRequest<BatchPreviewResponse>('/calendar/reroll/preview', {
    method: 'POST',
    body: { start_date: startDate, mode },
  })
}

/** Atomically applies a preview draft by token (M7, `calendar.edit`). */
export function applyBatchReroll(token: string): Promise<BatchApplyResponse> {
  return apiRequest<BatchApplyResponse>('/calendar/reroll/apply', {
    method: 'POST',
    body: { token },
  })
}

/** Reverts an entire batch apply (M7, `calendar.edit`). */
export function undoBatchOperation(batchOperationId: string): Promise<BatchUndoResponse> {
  return apiRequest<BatchUndoResponse>(`/batch-operations/${batchOperationId}/undo`, {
    method: 'POST',
  })
}
