import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  addGroceryStapleToList,
  checkGroceryItem,
  createGroceryManualItem,
  createGroceryStaple,
  deleteGroceryManualItem,
  getGroceryList,
  getGroceryPreferences,
  listGroceryStaples,
  patchGroceryPreferences,
  uncheckGroceryItem,
  updateGroceryManualItem,
  updateGroceryStaple,
  type GroceryManualItemPatch,
  type GroceryManualItemRequest,
  type GroceryPreferences,
  type GroceryPreferencesPatch,
  type GroceryStaplePatch,
  type GroceryStapleRequest,
} from '../../api/groceries'
import { listDepartments, listIngredients, listUnits } from '../../api/referenceData'

/** Root query key for every grocery read (broad invalidation target). */
const groceriesQueryKey = ['groceries'] as const

export const groceriesListQueryKey = [...groceriesQueryKey, 'list'] as const
export const groceriesPreferencesQueryKey = [...groceriesQueryKey, 'preferences'] as const
export const groceriesStaplesQueryKey = [...groceriesQueryKey, 'staples'] as const
export const groceriesDepartmentsQueryKey = [...groceriesQueryKey, 'departments'] as const
export const groceriesUnitsQueryKey = [...groceriesQueryKey, 'units'] as const
export const groceriesIngredientsQueryKey = [...groceriesQueryKey, 'ingredients'] as const

/** The active-range projection; no query means the remembered range applies. */
export function useGroceryList(enabled = true) {
  return useQuery({
    queryKey: groceriesListQueryKey,
    queryFn: () => getGroceryList(),
    retry: false,
    enabled,
  })
}

export function useGroceryPreferences(enabled = true) {
  return useQuery({
    queryKey: groceriesPreferencesQueryKey,
    queryFn: getGroceryPreferences,
    retry: false,
    enabled,
  })
}

export function useGroceryStaples(includeInactive = false, enabled = true) {
  return useQuery({
    queryKey: [...groceriesStaplesQueryKey, includeInactive],
    queryFn: () => listGroceryStaples(includeInactive),
    retry: false,
    enabled,
  })
}

/** Active departments for the manual-item and staple pickers. */
export function useGroceryDepartmentOptions(enabled = true) {
  return useQuery({
    queryKey: groceriesDepartmentsQueryKey,
    queryFn: () => listDepartments({ include_retired: false, limit: 100 }),
    retry: false,
    enabled,
  })
}

/** Active units for the manual-item and staple pickers. */
export function useGroceryUnitOptions(enabled = true) {
  return useQuery({
    queryKey: groceriesUnitsQueryKey,
    queryFn: () => listUnits({ include_retired: false, limit: 100 }),
    retry: false,
    enabled,
  })
}

/** Active ingredients for the manual-item and staple pickers. */
export function useGroceryIngredientOptions(enabled = true) {
  return useQuery({
    queryKey: groceriesIngredientsQueryKey,
    queryFn: () => listIngredients({ include_retired: false, limit: 100 }),
    retry: false,
    enabled,
  })
}

function useRefreshList() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: groceriesListQueryKey })
  }
}

/** Checks or unchecks one line by identity; the list reloads in server order. */
export function useSetGroceryChecked() {
  const refresh = useRefreshList()
  return useMutation({
    mutationFn: ({ identity, checked }: { identity: string; checked: boolean }) =>
      checked ? checkGroceryItem(identity) : uncheckGroceryItem(identity),
    onSuccess: refresh,
  })
}

/**
 * Manual-item writes change the list; `save_for_future` (create or a false→true
 * update) can also promote a staple, so the staples query is refreshed too.
 */
export function useCreateGroceryManualItem() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: GroceryManualItemRequest) => createGroceryManualItem(request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groceriesListQueryKey })
      await queryClient.invalidateQueries({ queryKey: groceriesStaplesQueryKey })
    },
  })
}

export function useUpdateGroceryManualItem() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: GroceryManualItemPatch }) =>
      updateGroceryManualItem(id, request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groceriesListQueryKey })
      await queryClient.invalidateQueries({ queryKey: groceriesStaplesQueryKey })
    },
  })
}

export function useDeleteGroceryManualItem() {
  const refresh = useRefreshList()
  return useMutation({
    mutationFn: (id: number) => deleteGroceryManualItem(id),
    onSuccess: refresh,
  })
}

export function useCreateGroceryStaple() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: GroceryStapleRequest) => createGroceryStaple(request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groceriesStaplesQueryKey })
    },
  })
}

export function useUpdateGroceryStaple() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: GroceryStaplePatch }) =>
      updateGroceryStaple(id, request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: groceriesStaplesQueryKey })
    },
  })
}

/** Creates a manual item for the active range from a staple. */
export function useAddGroceryStapleToList() {
  const refresh = useRefreshList()
  return useMutation({
    mutationFn: ({
      id,
      rangeStart,
      rangeDays,
    }: {
      id: number
      rangeStart: string
      rangeDays: number
    }) => addGroceryStapleToList(id, { range_start: rangeStart, range_days: rangeDays }),
    onSuccess: refresh,
  })
}

/**
 * Preferences are per-user. Changing them refreshes both the preference query
 * and the list, because the remembered range feeds the next list read.
 */
export function usePatchGroceryPreferences() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (patch: GroceryPreferencesPatch) => patchGroceryPreferences(patch),
    onSuccess: async (preferences: GroceryPreferences) => {
      queryClient.setQueryData(groceriesPreferencesQueryKey, preferences)
      await queryClient.invalidateQueries({ queryKey: groceriesPreferencesQueryKey })
      await queryClient.invalidateQueries({ queryKey: groceriesListQueryKey })
    },
  })
}
