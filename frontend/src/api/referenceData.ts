import { apiRequest } from './client'

/**
 * Typed client for the M2 reference-data contract (docs/API.md §22).
 * Reads require any authenticated actor; writes require `reference_data.edit`
 * and hard deletes are Admin-only. The server enforces all of that — these
 * functions only describe the wire shapes.
 */

/** Query shared by every reference-data list endpoint (docs/API.md §22). */
export interface ReferenceListParams {
  search?: string
  limit?: number
  include_retired?: boolean
}

function listQuery(params: ReferenceListParams): string {
  const query = new URLSearchParams()
  if (params.search !== undefined && params.search.length > 0) {
    query.set('search', params.search)
  }
  if (params.limit !== undefined) {
    query.set('limit', String(params.limit))
  }
  if (params.include_retired !== undefined) {
    query.set('include_retired', String(params.include_retired))
  }
  const text = query.toString()
  return text.length > 0 ? `?${text}` : ''
}

/* departments ---------- */

export interface Department {
  id: number
  name: string
  sort_order: number
  is_active: boolean
  created_at: string
  updated_at: string
}

export interface DepartmentListParams {
  search?: string
  /** Request up to 100 rows (the server clamps higher values). */
  limit?: number
  include_retired?: boolean
}

export interface UpdateDepartmentRequest {
  name?: string
  is_active?: boolean
}

export function listDepartments(params: DepartmentListParams = {}): Promise<Department[]> {
  return apiRequest<Department[]>(`/departments${listQuery(params)}`)
}

export function createDepartment(name: string): Promise<Department> {
  return apiRequest<Department>('/departments', { method: 'POST', body: { name } })
}

export function updateDepartment(
  id: number,
  request: UpdateDepartmentRequest,
): Promise<Department> {
  return apiRequest<Department>(`/departments/${id}`, { method: 'PATCH', body: request })
}

export function retireDepartment(id: number): Promise<Department> {
  return apiRequest<Department>(`/departments/${id}/retire`, { method: 'POST' })
}

export function deleteDepartment(id: number): Promise<void> {
  return apiRequest<void>(`/departments/${id}`, { method: 'DELETE' })
}

/** `ids` must be a complete permutation of every department id, retired included. */
export function reorderDepartments(ids: number[]): Promise<Department[]> {
  return apiRequest<Department[]>('/departments/order', { method: 'PUT', body: { ids } })
}

/* units ---------- */

export type UnitDimension = 'mass' | 'volume' | 'count' | 'nonconvertible'

export interface UnitAlias {
  id: number
  alias: string
}

export interface Unit {
  id: number
  symbol: string
  display_name: string
  dimension: UnitDimension
  base_factor: number | null
  is_custom: boolean
  is_active: boolean
  aliases: UnitAlias[]
}

export interface CreateUnitRequest {
  symbol: string
  display_name: string
  dimension: UnitDimension
  /** Required for mass/volume, forbidden otherwise. */
  base_factor?: number
}

export interface UpdateUnitRequest {
  symbol?: string
  display_name?: string
  base_factor?: number
}

export function listUnits(params: ReferenceListParams = {}): Promise<Unit[]> {
  return apiRequest<Unit[]>(`/units${listQuery(params)}`)
}

export function createUnit(request: CreateUnitRequest): Promise<Unit> {
  return apiRequest<Unit>('/units', { method: 'POST', body: request })
}

export function updateUnit(id: number, request: UpdateUnitRequest): Promise<Unit> {
  return apiRequest<Unit>(`/units/${id}`, { method: 'PATCH', body: request })
}

export function retireUnit(id: number): Promise<Unit> {
  return apiRequest<Unit>(`/units/${id}/retire`, { method: 'POST' })
}

export function deleteUnit(id: number): Promise<void> {
  return apiRequest<void>(`/units/${id}`, { method: 'DELETE' })
}

export function addUnitAlias(id: number, alias: string): Promise<UnitAlias> {
  return apiRequest<UnitAlias>(`/units/${id}/aliases`, { method: 'POST', body: { alias } })
}

export function removeUnitAlias(id: number, aliasId: number): Promise<void> {
  return apiRequest<void>(`/units/${id}/aliases/${aliasId}`, { method: 'DELETE' })
}

/* ingredients ---------- */

export interface IngredientAlias {
  id: number
  alias: string
}

export interface Ingredient {
  id: number
  canonical_name: string
  department_id: number | null
  is_active: boolean
  created_at: string
  updated_at: string
  aliases: IngredientAlias[]
}

export interface CreateIngredientRequest {
  canonical_name: string
  department_id?: number | null
  aliases?: string[]
}

export interface UpdateIngredientRequest {
  canonical_name?: string
  department_id?: number | null
}

export interface IngredientMergeRequest {
  source_id: number
  target_id: number
}

export interface IngredientMergeResult {
  source_id: number
  target_id: number
  skipped_aliases: string[]
  reassigned_reference_count: number
}

export function listIngredients(params: ReferenceListParams = {}): Promise<Ingredient[]> {
  return apiRequest<Ingredient[]>(`/ingredients${listQuery(params)}`)
}

export function createIngredient(request: CreateIngredientRequest): Promise<Ingredient> {
  return apiRequest<Ingredient>('/ingredients', { method: 'POST', body: request })
}

export function updateIngredient(
  id: number,
  request: UpdateIngredientRequest,
): Promise<Ingredient> {
  return apiRequest<Ingredient>(`/ingredients/${id}`, { method: 'PATCH', body: request })
}

export function retireIngredient(id: number): Promise<Ingredient> {
  return apiRequest<Ingredient>(`/ingredients/${id}/retire`, { method: 'POST' })
}

export function deleteIngredient(id: number): Promise<void> {
  return apiRequest<void>(`/ingredients/${id}`, { method: 'DELETE' })
}

export function addIngredientAlias(id: number, alias: string): Promise<IngredientAlias> {
  return apiRequest<IngredientAlias>(`/ingredients/${id}/aliases`, {
    method: 'POST',
    body: { alias },
  })
}

export function removeIngredientAlias(id: number, aliasId: number): Promise<void> {
  return apiRequest<void>(`/ingredients/${id}/aliases/${aliasId}`, { method: 'DELETE' })
}

export function mergeIngredients(request: IngredientMergeRequest): Promise<IngredientMergeResult> {
  return apiRequest<IngredientMergeResult>('/ingredients/merge', { method: 'POST', body: request })
}

/* proteins ---------- */

export interface Protein {
  id: number
  name: string
  is_active: boolean
}

export interface CreateProteinRequest {
  name: string
}

export interface UpdateProteinRequest {
  name?: string
  is_active?: boolean
}

export function listProteins(params: ReferenceListParams = {}): Promise<Protein[]> {
  return apiRequest<Protein[]>(`/proteins${listQuery(params)}`)
}

export function createProtein(request: CreateProteinRequest): Promise<Protein> {
  return apiRequest<Protein>('/proteins', { method: 'POST', body: request })
}

export function updateProtein(id: number, request: UpdateProteinRequest): Promise<Protein> {
  return apiRequest<Protein>(`/proteins/${id}`, { method: 'PATCH', body: request })
}

export function retireProtein(id: number): Promise<Protein> {
  return apiRequest<Protein>(`/proteins/${id}/retire`, { method: 'POST' })
}

export function deleteProtein(id: number): Promise<void> {
  return apiRequest<void>(`/proteins/${id}`, { method: 'DELETE' })
}

/* tags ---------- */

export interface Tag {
  id: number
  name: string
}

export interface CreateTagRequest {
  name: string
}

export interface UpdateTagRequest {
  name: string
}

export function listTags(params: ReferenceListParams = {}): Promise<Tag[]> {
  return apiRequest<Tag[]>(`/tags${listQuery(params)}`)
}

export function createTag(request: CreateTagRequest): Promise<Tag> {
  return apiRequest<Tag>('/tags', { method: 'POST', body: request })
}

export function updateTag(id: number, request: UpdateTagRequest): Promise<Tag> {
  return apiRequest<Tag>(`/tags/${id}`, { method: 'PATCH', body: request })
}

export function deleteTag(id: number): Promise<void> {
  return apiRequest<void>(`/tags/${id}`, { method: 'DELETE' })
}
