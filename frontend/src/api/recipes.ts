import { apiRequest, apiUpload } from './client'

/**
 * Typed client for the M3 recipe contract (docs/API.md §23).
 *
 * Reads require `recipes.view`; writes require `recipes.edit`; the single-user
 * system actor and Admins hold both. The server enforces all of that — these
 * functions only describe the wire shapes. JSON property names are snake_case.
 */

export type RecipeStatus = 'normal' | 'manual_only' | 'retired'

export type RecipeSort =
  'recently_added' | 'name' | 'favorites' | 'recently_eaten' | 'least_recently_eaten' | 'most_eaten'

export const RECIPE_STATUS_LABELS: Record<RecipeStatus, string> = {
  normal: 'Normal',
  manual_only: 'Manual only',
  retired: 'Retired',
}

export const RECIPE_SORT_LABELS: Record<RecipeSort, string> = {
  recently_added: 'Recently added',
  name: 'Name',
  favorites: 'Favorites',
  recently_eaten: 'Recently eaten',
  least_recently_eaten: 'Least recently eaten',
  most_eaten: 'Most eaten',
}

export const RECIPE_STATUSES: RecipeStatus[] = ['normal', 'manual_only', 'retired']

export const RECIPE_SORTS: RecipeSort[] = [
  'recently_added',
  'name',
  'favorites',
  'recently_eaten',
  'least_recently_eaten',
  'most_eaten',
]

/* ---------- list ---------- */

export interface RecipeListParams {
  /** Normalized-name substring (server-side). */
  search?: string
  status?: RecipeStatus
  favorite?: boolean
  vegan?: boolean
  vegetarian?: boolean
  protein_id?: number
  tag_id?: number
  sort?: RecipeSort
  /** Defaults to 24 server-side; clamped to 100. */
  limit?: number
  offset?: number
}

export interface RecipeImage {
  id: number
  alt_text: string | null
  source_url: string | null
  created_at: string
  /** Relative bytes URL, e.g. `/api/v1/recipes/1/image`. */
  url: string
}

export interface RecipeListItem {
  id: number
  name: string
  status: RecipeStatus
  is_favorite: boolean
  is_vegan: boolean
  is_vegetarian: boolean
  image: RecipeImage | null
  /** Latest eaten occurrence (household-local `yyyy-MM-dd`), or null (M11). */
  last_eaten: string | null
  /** Nearest today-or-future occurrence (household-local `yyyy-MM-dd`), or null. */
  next_planned: string | null
  /** Historical eaten occurrence count, or null when the recipe has none. */
  times_eaten: number | null
  created_at: string
  updated_at: string
}

export interface RecipeListResponse {
  items: RecipeListItem[]
  total: number
  limit: number
  offset: number
}

function listQuery(params: RecipeListParams): string {
  const query = new URLSearchParams()
  if (params.search !== undefined && params.search.length > 0) {
    query.set('search', params.search)
  }
  if (params.status !== undefined) {
    query.set('status', params.status)
  }
  if (params.favorite !== undefined) {
    query.set('favorite', String(params.favorite))
  }
  if (params.vegan !== undefined) {
    query.set('vegan', String(params.vegan))
  }
  if (params.vegetarian !== undefined) {
    query.set('vegetarian', String(params.vegetarian))
  }
  if (params.protein_id !== undefined) {
    query.set('protein_id', String(params.protein_id))
  }
  if (params.tag_id !== undefined) {
    query.set('tag_id', String(params.tag_id))
  }
  if (params.sort !== undefined) {
    query.set('sort', params.sort)
  }
  if (params.limit !== undefined) {
    query.set('limit', String(params.limit))
  }
  if (params.offset !== undefined) {
    query.set('offset', String(params.offset))
  }
  const text = query.toString()
  return text.length > 0 ? `?${text}` : ''
}

export function listRecipes(params: RecipeListParams = {}): Promise<RecipeListResponse> {
  return apiRequest<RecipeListResponse>(`/recipes${listQuery(params)}`)
}

/* ---------- detail ---------- */

export interface RecipeIngredientReference {
  id: number
  name: string
  is_active: boolean
}

export interface RecipeIngredientUnitReference {
  id: number
  symbol: string
  display_name: string
  is_active: boolean
}

export interface RecipeIngredient {
  id: number
  ingredient: RecipeIngredientReference
  quantity: number | null
  unit: RecipeIngredientUnitReference | null
  preparation_note: string | null
  is_optional: boolean
  sort_order: number
}

export interface RecipeIngredientGroup {
  id: number
  title: string | null
  sort_order: number
  ingredients: RecipeIngredient[]
}

export interface RecipeInstructionStep {
  id: number
  text: string
  sort_order: number
}

export interface RecipeInstructionSection {
  id: number
  title: string | null
  sort_order: number
  steps: RecipeInstructionStep[]
}

export interface RecipeProteinReference {
  id: number
  name: string
  is_active: boolean
}

export interface RecipeTagReference {
  id: number
  name: string
}

export interface RecipeStatistics {
  last_eaten: string | null
  next_planned: string | null
  times_eaten: number | null
}

export interface Recipe {
  id: number
  name: string
  description: string | null
  source_site: string | null
  source_url: string | null
  author: string | null
  prep_minutes: number | null
  cook_minutes: number | null
  notes: string | null
  status: RecipeStatus
  is_favorite: boolean
  is_vegan: boolean
  is_vegetarian: boolean
  created_at: string
  updated_at: string
  image: RecipeImage | null
  ingredient_groups: RecipeIngredientGroup[]
  ingredients: RecipeIngredient[]
  instruction_sections: RecipeInstructionSection[]
  proteins: RecipeProteinReference[]
  tags: RecipeTagReference[]
  statistics: RecipeStatistics
}

export function getRecipe(id: number): Promise<Recipe> {
  return apiRequest<Recipe>(`/recipes/${id}`)
}

/* ---------- writes ---------- */

export interface RecipeIngredientWrite {
  ingredient_id: number
  quantity?: number | null
  unit_id?: number | null
  preparation_note?: string | null
  is_optional?: boolean
}

export interface RecipeIngredientGroupWrite {
  title?: string | null
  ingredients: RecipeIngredientWrite[]
}

export interface RecipeInstructionSectionWrite {
  title?: string | null
  steps: string[]
}

/**
 * Create/update body. A present collection is a FULL replacement on PATCH
 * (docs/API.md §23); the form always sends every slice it owns.
 */
export interface RecipeWriteRequest {
  name?: string
  description?: string | null
  source_site?: string | null
  source_url?: string | null
  author?: string | null
  prep_minutes?: number | null
  cook_minutes?: number | null
  notes?: string | null
  status?: RecipeStatus
  is_favorite?: boolean
  is_vegan?: boolean
  is_vegetarian?: boolean
  ingredient_groups?: RecipeIngredientGroupWrite[]
  ingredients?: RecipeIngredientWrite[]
  instruction_sections?: RecipeInstructionSectionWrite[]
  protein_ids?: number[]
  tag_ids?: number[]
}

export function createRecipe(request: RecipeWriteRequest): Promise<Recipe> {
  return apiRequest<Recipe>('/recipes', { method: 'POST', body: request })
}

export function updateRecipe(id: number, request: RecipeWriteRequest): Promise<Recipe> {
  return apiRequest<Recipe>(`/recipes/${id}`, { method: 'PATCH', body: request })
}

/* ---------- image ---------- */

export function uploadRecipeImage(id: number, file: File, altText?: string): Promise<RecipeImage> {
  const formData = new FormData()
  formData.append('file', file)
  if (altText !== undefined && altText.length > 0) {
    formData.append('alt_text', altText)
  }
  return apiUpload<RecipeImage>(`/recipes/${id}/image`, formData, { method: 'PUT' })
}

export function updateRecipeImageAltText(id: number, altText: string | null): Promise<RecipeImage> {
  return apiRequest<RecipeImage>(`/recipes/${id}/image`, {
    method: 'PATCH',
    body: { alt_text: altText },
  })
}

export function deleteRecipeImage(id: number): Promise<void> {
  return apiRequest<void>(`/recipes/${id}/image`, { method: 'DELETE' })
}
