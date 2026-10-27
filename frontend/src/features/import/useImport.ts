import { useMutation, useQueryClient } from '@tanstack/react-query'

import {
  checkImport,
  parseRecipeUrl,
  uploadRecipeImageFromUrl,
  type ImportCheckRequest,
  type ImportCheckResponse,
  type ImportParseResponse,
} from '../../api/importRecipes'
import type { RecipeImage } from '../../api/recipes'
import { recipesQueryKey } from '../recipes/useRecipes'

/**
 * TanStack Query mutations for the M4 import flow (docs/M4_PLAN.md §2.3).
 *
 * Parsing/checking are stateless requests with no cache to invalidate. The
 * image-from-URL mutation invalidates the recipe prefix so a freshly downloaded
 * photo appears in lists and detail views.
 */

export function useParseImportUrl() {
  return useMutation<ImportParseResponse, unknown, string>({
    mutationFn: (url: string) => parseRecipeUrl(url),
  })
}

export function useCheckImport() {
  return useMutation<ImportCheckResponse, unknown, ImportCheckRequest>({
    mutationFn: (request: ImportCheckRequest) => checkImport(request),
  })
}

export function useRecipeImageFromUrl() {
  const queryClient = useQueryClient()
  return useMutation<RecipeImage, unknown, { id: number; sourceUrl: string }>({
    mutationFn: ({ id, sourceUrl }) => uploadRecipeImageFromUrl(id, sourceUrl),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: recipesQueryKey })
    },
  })
}
