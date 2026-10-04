import { apiRequest } from './client'
import type { RecipeImage } from './recipes'

/**
 * Typed client for the M4 import contract (docs/API.md §15).
 *
 * Import is **stateless and draft-only**: `POST /import/recipe-url` fetches and
 * parses a page and returns a draft; nothing is written until the reviewed
 * draft is submitted through the normal `POST /recipes` endpoint. These
 * functions only describe the wire shapes — the server enforces `recipes.edit`,
 * SSRF policy, throttling, and image content rules.
 */

export type ImportParseMethod = 'json_ld' | 'html'

export type ImportIngredientMatchKind = 'exact' | 'alias' | null

export type ImportUnitDimension = 'mass' | 'volume' | 'count'

/** A candidate recipe or reference the user can act on. */
export interface ImportReference {
  id: number
  name: string
}

export interface ImportUnitSuggestion {
  id: number
  symbol: string
  display_name: string
}

export interface ImportUnitDraft {
  raw: string
  matched_unit_id: number | null
  matched_symbol: string | null
  is_unknown: boolean
  suggested_dimension: ImportUnitDimension | null
  suggestions: ImportUnitSuggestion[]
}

export interface ImportIngredientDraft {
  raw: string
  quantity: number | null
  name: string
  preparation_note: string | null
  matched_ingredient_id: number | null
  match_kind: ImportIngredientMatchKind
  is_unknown: boolean
  suggestions: ImportReference[]
  unit: ImportUnitDraft
}

export interface ImportInstructionSectionDraft {
  title: string | null
  steps: string[]
}

export interface ImportRecipeDraft {
  index: number
  name: string
  description: string | null
  author: string | null
  source_url: string | null
  source_site: string | null
  prep_minutes: number | null
  cook_minutes: number | null
  notes: string | null
  image_source_url: string | null
  name_matches: ImportReference[]
  ingredients: ImportIngredientDraft[]
  instruction_sections: ImportInstructionSectionDraft[]
  proteins: ImportReference[]
  tags: ImportReference[]
  warnings: string[]
}

export interface ImportSource {
  url: string
  site: string
  page_title: string | null
  author: string | null
  fetched_at: string
  parse_method: ImportParseMethod
}

export interface ImportCapabilities {
  can_create_reference_data: boolean
}

export interface ImportParseResponse {
  source: ImportSource
  source_url_match: ImportReference | null
  capabilities: ImportCapabilities
  /** Always has at least one element or the request fails with `IMPORT_NO_RECIPE`. */
  recipes: ImportRecipeDraft[]
}

export interface ImportCheckRequest {
  source_url?: string
  name?: string
}

export interface ImportCheckResponse {
  source_url_match: ImportReference | null
  name_matches: ImportReference[]
}

/** `POST /import/recipe-url` — requires `recipes.edit`. */
export function parseRecipeUrl(url: string): Promise<ImportParseResponse> {
  return apiRequest<ImportParseResponse>('/import/recipe-url', {
    method: 'POST',
    body: { url },
  })
}

/** `POST /import/check` — requires `recipes.view` (implied by `recipes.edit`). */
export function checkImport(request: ImportCheckRequest): Promise<ImportCheckResponse> {
  return apiRequest<ImportCheckResponse>('/import/check', {
    method: 'POST',
    body: request,
  })
}

/** `POST /recipes/{id}/image/from-url` — requires `recipes.edit`. */
export function uploadRecipeImageFromUrl(id: number, sourceUrl: string): Promise<RecipeImage> {
  return apiRequest<RecipeImage>(`/recipes/${id}/image/from-url`, {
    method: 'POST',
    body: { source_url: sourceUrl },
  })
}
