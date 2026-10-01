import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  createDepartment,
  deleteDepartment,
  listDepartments,
  reorderDepartments,
  retireDepartment,
  updateDepartment,
  type Department,
  type DepartmentListParams,
  type UpdateDepartmentRequest,
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
