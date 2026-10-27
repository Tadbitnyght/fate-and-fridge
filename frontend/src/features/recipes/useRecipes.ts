import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  createRecipe,
  deleteRecipeImage,
  getRecipe,
  hardDeleteRecipe,
  listRecipes,
  updateRecipe,
  updateRecipeImageAltText,
  uploadRecipeImage,
  type Recipe,
  type RecipeHardDeleteRequest,
  type RecipeHardDeleteResult,
  type RecipeListParams,
  type RecipeListResponse,
  type RecipeWriteRequest,
} from '../../api/recipes'
import { calendarQueryKey } from '../calendar/useCalendar'
import { auditQueryKey } from '../settings/useAuditLog'
import { statisticsQueryKey } from '../statistics/useStatistics'

/**
 * Query-key prefix for every recipe query. Image mutations and recipe writes
 * invalidate the whole prefix so browser lists and detail views refetch.
 */
export const recipesQueryKey = ['recipes'] as const

export function useRecipeList(params: RecipeListParams) {
  return useQuery<RecipeListResponse>({
    queryKey: [...recipesQueryKey, 'list', params],
    queryFn: () => listRecipes(params),
    retry: false,
  })
}

export function useRecipe(id: number | undefined) {
  return useQuery<Recipe>({
    queryKey: [...recipesQueryKey, 'detail', id],
    queryFn: () => getRecipe(id as number),
    enabled: id !== undefined,
    retry: false,
  })
}

function useRefreshRecipes() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: recipesQueryKey })
  }
}

export function useCreateRecipe() {
  const refresh = useRefreshRecipes()
  return useMutation({
    mutationFn: (request: RecipeWriteRequest) => createRecipe(request),
    onSuccess: refresh,
  })
}

export function useUpdateRecipe() {
  const refresh = useRefreshRecipes()
  return useMutation({
    mutationFn: ({ id, request }: { id: number; request: RecipeWriteRequest }) =>
      updateRecipe(id, request),
    onSuccess: refresh,
  })
}

export function useUploadRecipeImage() {
  const refresh = useRefreshRecipes()
  return useMutation({
    mutationFn: ({ id, file, altText }: { id: number; file: File; altText?: string }) =>
      uploadRecipeImage(id, file, altText),
    onSuccess: refresh,
  })
}

export function useUpdateRecipeImageAltText() {
  const refresh = useRefreshRecipes()
  return useMutation({
    mutationFn: ({ id, altText }: { id: number; altText: string | null }) =>
      updateRecipeImageAltText(id, altText),
    onSuccess: refresh,
  })
}

export function useDeleteRecipeImage() {
  const refresh = useRefreshRecipes()
  return useMutation({
    mutationFn: (id: number) => deleteRecipeImage(id),
    onSuccess: refresh,
  })
}

/**
 * Irreversible Admin-only hard delete (M12-09). A delete removes today/future
 * planned entries and tombstones past ones, so the recipe lists, the calendar,
 * the statistics/meal-history views, and the audit log all change at once.
 * (Live sync also emits `recipe.updated` and `calendar.updated`.)
 */
export function useHardDeleteRecipe() {
  const queryClient = useQueryClient()
  return useMutation<
    RecipeHardDeleteResult,
    unknown,
    { id: number; request: RecipeHardDeleteRequest }
  >({
    mutationFn: ({ id, request }) => hardDeleteRecipe(id, request),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: recipesQueryKey }),
        queryClient.invalidateQueries({ queryKey: calendarQueryKey }),
        queryClient.invalidateQueries({ queryKey: statisticsQueryKey }),
        queryClient.invalidateQueries({ queryKey: auditQueryKey }),
      ])
    },
  })
}
