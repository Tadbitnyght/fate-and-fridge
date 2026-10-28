import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  addIngredientAlias,
  addUnitAlias,
  createDepartment,
  createIngredient,
  createProtein,
  createTag,
  createUnit,
  deleteDepartment,
  deleteIngredient,
  deleteProtein,
  deleteTag,
  deleteUnit,
  listDepartments,
  listIngredients,
  listProteins,
  listTags,
  listUnits,
  mergeIngredients,
  removeIngredientAlias,
  removeUnitAlias,
  reorderDepartments,
  retireDepartment,
  retireIngredient,
  retireProtein,
  retireUnit,
  updateDepartment,
  updateIngredient,
  updateProtein,
  updateTag,
  updateUnit,
  type CreateIngredientRequest,
  type CreateProteinRequest,
  type CreateTagRequest,
  type CreateUnitRequest,
  type Department,
  type DepartmentListParams,
  type Ingredient,
  type IngredientMergeRequest,
  type Protein,
  type ReferenceListParams,
  type Tag,
  type Unit,
  type UpdateDepartmentRequest,
  type UpdateIngredientRequest,
  type UpdateProteinRequest,
  type UpdateTagRequest,
  type UpdateUnitRequest,
} from '../../api/referenceData'

/**
 * Query-key prefix for every departments variant. Stages B/C add sibling
 * prefixes (units, ingredients, proteins, tags) to this file.
 */
export const departmentsQueryKey = ['reference-data', 'departments'] as const

/**
 * Departments are always fetched with retired rows included so the up/down
 * reorder permutation can be complete (docs/API.md §22); the tab filters the
 * display list locally. `limit` is the server maximum.
 */
export function useDepartments(search: string) {
  const params: DepartmentListParams = {
    search: search.length > 0 ? search : undefined,
    include_retired: true,
    limit: 100,
  }
  return useQuery<Department[]>({
    queryKey: [...departmentsQueryKey, params],
    queryFn: () => listDepartments(params),
    retry: false,
  })
}

function useRefreshDepartments() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: departmentsQueryKey })
  }
}

export function useCreateDepartment() {
  const refresh = useRefreshDepartments()
  return useMutation({
    mutationFn: (name: string) => createDepartment(name),
    onSuccess: refresh,
  })
}

export function useUpdateDepartment() {
  const refresh = useRefreshDepartments()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: UpdateDepartmentRequest }) =>
      updateDepartment(id, request),
    onSuccess: refresh,
  })
}

export function useRetireDepartment() {
  const refresh = useRefreshDepartments()
  return useMutation({
    mutationFn: (id: number) => retireDepartment(id),
    onSuccess: refresh,
  })
}

export function useDeleteDepartment() {
  const refresh = useRefreshDepartments()
  return useMutation({
    mutationFn: (id: number) => deleteDepartment(id),
    onSuccess: refresh,
  })
}

export function useReorderDepartments() {
  const refresh = useRefreshDepartments()
  return useMutation({
    mutationFn: (ids: number[]) => reorderDepartments(ids),
    onSuccess: refresh,
  })
}

/* units ---------- */

/** Query-key prefix for every units variant. */
export const unitsQueryKey = ['reference-data', 'units'] as const

export function useUnits(search: string, includeRetired: boolean) {
  const params: ReferenceListParams = {
    search: search.length > 0 ? search : undefined,
    include_retired: includeRetired,
    limit: 100,
  }
  return useQuery<Unit[]>({
    queryKey: [...unitsQueryKey, params],
    queryFn: () => listUnits(params),
    retry: false,
  })
}

function useRefreshUnits() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: unitsQueryKey })
  }
}

export function useCreateUnit() {
  const refresh = useRefreshUnits()
  return useMutation({
    mutationFn: (request: CreateUnitRequest) => createUnit(request),
    onSuccess: refresh,
  })
}

export function useUpdateUnit() {
  const refresh = useRefreshUnits()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: UpdateUnitRequest }) =>
      updateUnit(id, request),
    onSuccess: refresh,
  })
}

export function useRetireUnit() {
  const refresh = useRefreshUnits()
  return useMutation({
    mutationFn: (id: number) => retireUnit(id),
    onSuccess: refresh,
  })
}

export function useDeleteUnit() {
  const refresh = useRefreshUnits()
  return useMutation({
    mutationFn: (id: number) => deleteUnit(id),
    onSuccess: refresh,
  })
}

export function useAddUnitAlias() {
  const refresh = useRefreshUnits()
  return useMutation({
    mutationFn: ({ id, alias }: { id: number; alias: string }) => addUnitAlias(id, alias),
    onSuccess: refresh,
  })
}

export function useRemoveUnitAlias() {
  const refresh = useRefreshUnits()
  return useMutation({
    mutationFn: ({ id, aliasId }: { id: number; aliasId: number }) => removeUnitAlias(id, aliasId),
    onSuccess: refresh,
  })
}

/* ingredients ---------- */

/** Query-key prefix for every ingredient-list variant. */
export const ingredientsQueryKey = ['reference-data', 'ingredients'] as const

export function useIngredients(search: string, includeRetired: boolean) {
  const params: ReferenceListParams = {
    search: search.length > 0 ? search : undefined,
    include_retired: includeRetired,
    limit: 100,
  }
  return useQuery<Ingredient[]>({
    queryKey: [...ingredientsQueryKey, params],
    queryFn: () => listIngredients(params),
    retry: false,
  })
}

function useRefreshIngredients() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: ingredientsQueryKey })
  }
}

export function useCreateIngredient() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: (request: CreateIngredientRequest) => createIngredient(request),
    onSuccess: refresh,
  })
}

export function useUpdateIngredient() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: UpdateIngredientRequest }) =>
      updateIngredient(id, request),
    onSuccess: refresh,
  })
}

export function useRetireIngredient() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: (id: number) => retireIngredient(id),
    onSuccess: refresh,
  })
}

export function useDeleteIngredient() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: (id: number) => deleteIngredient(id),
    onSuccess: refresh,
  })
}

export function useAddIngredientAlias() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: ({ id, alias }: { id: number; alias: string }) => addIngredientAlias(id, alias),
    onSuccess: refresh,
  })
}

export function useRemoveIngredientAlias() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: ({ id, aliasId }: { id: number; aliasId: number }) =>
      removeIngredientAlias(id, aliasId),
    onSuccess: refresh,
  })
}

/**
 * Merging retires the source and reassigns its references, so every ingredient
 * list variant — the tab and the merge dialog's pickers, which share the key
 * prefix — must refetch.
 */
export function useMergeIngredients() {
  const refresh = useRefreshIngredients()
  return useMutation({
    mutationFn: (request: IngredientMergeRequest) => mergeIngredients(request),
    onSuccess: refresh,
  })
}

/* proteins ---------- */

/** Query-key prefix for every protein-list variant. */
export const proteinsQueryKey = ['reference-data', 'proteins'] as const

export function useProteins(search: string, includeRetired: boolean) {
  const params: ReferenceListParams = {
    search: search.length > 0 ? search : undefined,
    include_retired: includeRetired,
    limit: 100,
  }
  return useQuery<Protein[]>({
    queryKey: [...proteinsQueryKey, params],
    queryFn: () => listProteins(params),
    retry: false,
  })
}

function useRefreshProteins() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: proteinsQueryKey })
  }
}

export function useCreateProtein() {
  const refresh = useRefreshProteins()
  return useMutation({
    mutationFn: (request: CreateProteinRequest) => createProtein(request),
    onSuccess: refresh,
  })
}

export function useUpdateProtein() {
  const refresh = useRefreshProteins()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: UpdateProteinRequest }) =>
      updateProtein(id, request),
    onSuccess: refresh,
  })
}

export function useRetireProtein() {
  const refresh = useRefreshProteins()
  return useMutation({
    mutationFn: (id: number) => retireProtein(id),
    onSuccess: refresh,
  })
}

export function useDeleteProtein() {
  const refresh = useRefreshProteins()
  return useMutation({
    mutationFn: (id: number) => deleteProtein(id),
    onSuccess: refresh,
  })
}

/* tags ---------- */

/**
 * Query-key prefix for every tag-list variant. Tags have no retirement or
 * `is_active` flag in the M2 contract, so there is no retired filter here.
 */
export const tagsQueryKey = ['reference-data', 'tags'] as const

export function useTags(search: string) {
  const params: ReferenceListParams = {
    search: search.length > 0 ? search : undefined,
    limit: 100,
  }
  return useQuery<Tag[]>({
    queryKey: [...tagsQueryKey, params],
    queryFn: () => listTags(params),
    retry: false,
  })
}

function useRefreshTags() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: tagsQueryKey })
  }
}

export function useCreateTag() {
  const refresh = useRefreshTags()
  return useMutation({
    mutationFn: (request: CreateTagRequest) => createTag(request),
    onSuccess: refresh,
  })
}

export function useUpdateTag() {
  const refresh = useRefreshTags()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: UpdateTagRequest }) =>
      updateTag(id, request),
    onSuccess: refresh,
  })
}

export function useDeleteTag() {
  const refresh = useRefreshTags()
  return useMutation({
    mutationFn: (id: number) => deleteTag(id),
    onSuccess: refresh,
  })
}
