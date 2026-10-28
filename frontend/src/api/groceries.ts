import { apiRequest } from './client'

/**
 * Typed client for the M8 grocery contract. Reads require `groceries.view`; manual items, staples, and check state
 * writes require `groceries.edit`. The server enforces permissions, range
 * resolution, and ordering — these functions only describe the wire shapes.
 * JSON property names are snake_case.
 */

/* ranges ---------- */

export type GroceryRangePreset =
  'next_3_days' | 'next_7_days' | 'next_14_days' | 'rest_of_month' | 'custom'

/** Presets the range selector offers as one-tap choices (custom is separate). */
export const GROCERY_RANGE_PRESETS: ReadonlyArray<{
  value: Exclude<GroceryRangePreset, 'custom'>
  label: string
}> = [
  { value: 'next_3_days', label: 'Next 3 days' },
  { value: 'next_7_days', label: 'Next 7 days' },
  { value: 'next_14_days', label: 'Next 14 days' },
  { value: 'rest_of_month', label: 'Rest of month' },
]

/** The server-resolved concrete range (docs/API.md §13). */
export interface GroceryRange {
  preset: GroceryRangePreset
  start: string
  end: string
  day_count: number
}

/** Optional explicit range query for `GET /groceries`. Omit for the remembered range. */
export interface GroceryRangeQuery {
  preset?: GroceryRangePreset
  start?: string
  days?: number
}

/* list ---------- */

export type GrocerySourceType = 'recipe' | 'manual'

/** One contributing meal for a recipe-derived line. */
export interface GroceryItemSource {
  recipe_id: number
  recipe_name: string
  date: string
}

export interface GroceryItem {
  /** Stable `GroceryItemIdentity` hash the check API keys on. */
  identity: string
  name: string
  /** Empty string for a quantity-less line. */
  quantity_text: string
  quantity: number | null
  unit: string | null
  ingredient_id: number | null
  department_id: number | null
  checked: boolean
  source_type: GrocerySourceType
  manual_item_id: number | null
  sources: GroceryItemSource[]
}

export interface GroceryDepartment {
  department_id: number | null
  name: string
  sort_order: number
  is_other: boolean
  items: GroceryItem[]
}

export interface GroceryList {
  range: GroceryRange
  departments: GroceryDepartment[]
}

/* manual items ---------- */

export interface GroceryManualItem {
  id: number
  name: string
  quantity_numeric: number | null
  unit_id: number | null
  ingredient_id: number | null
  department_id: number | null
  range_start: string
  range_days: number
  save_for_future: boolean
  created_at: string
  updated_at: string
}

export interface GroceryManualItemRequest {
  name: string
  quantity_numeric?: number | null
  unit_id?: number | null
  ingredient_id?: number | null
  department_id?: number | null
  range_start: string
  range_days: number
  save_for_future?: boolean
}

/** Partial update: an explicit `null` clears a nullable field. */
export type GroceryManualItemPatch = Partial<GroceryManualItemRequest>

/* staples ---------- */

export interface GroceryStaple {
  id: number
  name: string
  ingredient_id: number | null
  department_id: number | null
  default_quantity_numeric: number | null
  unit_id: number | null
  is_active: boolean
  created_at: string
  updated_at: string
}

export interface GroceryStapleRequest {
  name: string
  ingredient_id?: number | null
  department_id?: number | null
  default_quantity_numeric?: number | null
  unit_id?: number | null
}

export type GroceryStaplePatch = Partial<GroceryStapleRequest> & { is_active?: boolean }

export interface GroceryStapleList {
  staples: GroceryStaple[]
}

export interface GroceryStapleRangeRequest {
  range_start: string
  range_days: number
}

/* preferences ---------- */

export interface GroceryPreferences {
  hide_checked: boolean
  range_preset: GroceryRangePreset | null
  range_start: string | null
  range_days: number | null
}

export interface GroceryPreferencesPatch {
  hide_checked?: boolean
  range_preset?: GroceryRangePreset | null
  range_start?: string | null
  range_days?: number | null
}

/* check state ---------- */

export interface GroceryCheckResult {
  identity: string
  checked: boolean
  checked_at: string | null
  checked_by_user_id: number | null
}

/* requests ---------- */

function rangeQuery(range: GroceryRangeQuery): string {
  const query = new URLSearchParams()
  if (range.preset !== undefined) {
    query.set('preset', range.preset)
  }
  if (range.start !== undefined) {
    query.set('start', range.start)
  }
  if (range.days !== undefined) {
    query.set('days', String(range.days))
  }
  const text = query.toString()
  return text.length > 0 ? `?${text}` : ''
}

/**
 * The live grocery projection. With no range supplied the server resolves the
 * caller's remembered preference (default Next 7 Days).
 */
export function getGroceryList(range: GroceryRangeQuery = {}): Promise<GroceryList> {
  return apiRequest<GroceryList>(`/groceries${rangeQuery(range)}`)
}

export function getGroceryPreferences(): Promise<GroceryPreferences> {
  return apiRequest<GroceryPreferences>('/groceries/preferences')
}

export function patchGroceryPreferences(
  patch: GroceryPreferencesPatch,
): Promise<GroceryPreferences> {
  return apiRequest<GroceryPreferences>('/groceries/preferences', { method: 'PATCH', body: patch })
}

export function createGroceryManualItem(
  request: GroceryManualItemRequest,
): Promise<GroceryManualItem> {
  return apiRequest<GroceryManualItem>('/groceries/manual', { method: 'POST', body: request })
}

export function updateGroceryManualItem(
  id: number,
  request: GroceryManualItemPatch,
): Promise<GroceryManualItem> {
  return apiRequest<GroceryManualItem>(`/groceries/manual/${id}`, {
    method: 'PATCH',
    body: request,
  })
}

export function deleteGroceryManualItem(id: number): Promise<void> {
  return apiRequest<void>(`/groceries/manual/${id}`, { method: 'DELETE' })
}

export function listGroceryStaples(includeInactive = false): Promise<GroceryStapleList> {
  const query = includeInactive ? '?include_inactive=true' : ''
  return apiRequest<GroceryStapleList>(`/groceries/staples${query}`)
}

export function createGroceryStaple(request: GroceryStapleRequest): Promise<GroceryStaple> {
  return apiRequest<GroceryStaple>('/groceries/staples', { method: 'POST', body: request })
}

export function updateGroceryStaple(
  id: number,
  request: GroceryStaplePatch,
): Promise<GroceryStaple> {
  return apiRequest<GroceryStaple>(`/groceries/staples/${id}`, { method: 'PATCH', body: request })
}

/** Creates a range-scoped manual item from a staple for the active range. */
export function addGroceryStapleToList(
  id: number,
  request: GroceryStapleRangeRequest,
): Promise<GroceryManualItem> {
  return apiRequest<GroceryManualItem>(`/groceries/staples/${id}/add-to-list`, {
    method: 'POST',
    body: request,
  })
}

export function checkGroceryItem(identity: string): Promise<GroceryCheckResult> {
  return apiRequest<GroceryCheckResult>('/groceries/check', { method: 'POST', body: { identity } })
}

export function uncheckGroceryItem(identity: string): Promise<GroceryCheckResult> {
  return apiRequest<GroceryCheckResult>('/groceries/uncheck', {
    method: 'POST',
    body: { identity },
  })
}
