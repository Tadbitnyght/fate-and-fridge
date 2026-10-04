import type { ImportReference, ImportRecipeDraft } from '../../api/importRecipes'
import {
  newFormKey,
  type IngredientFormRow,
  type InstructionSectionFormRow,
  type RecipeFormState,
} from '../recipes/recipeForm'

/**
 * Maps a parsed import draft (docs/API.md §15) into the editable
 * {@link RecipeFormState} used by the shared recipe form controls, so the
 * review screen can reuse RecipeFormPage's ingredients/instructions/proteins
 * fieldset components and the same create request builder.
 *
 * Only values the parser produced are carried over — nothing is invented. A
 * line whose quantity stayed raw (ranges, word quantities) renders with an
 * empty quantity field; the raw text is preserved in the preparation note when
 * the parser did not split one out.
 */

function ingredientRow(ingredient: ImportRecipeDraft['ingredients'][number]): IngredientFormRow {
  const matchedId = ingredient.matched_ingredient_id
  const quantityText = ingredient.quantity === null ? '' : String(ingredient.quantity)
  const unitId = ingredient.unit.matched_unit_id
  const preparationNote =
    ingredient.preparation_note ??
    // Preserve unparsed raw quantity text so the user can see what was dropped
    // instead of silently losing it.
    (ingredient.quantity === null && ingredient.raw.length > 0
      ? extractRawQuantityText(ingredient.raw, ingredient.name)
      : '')

  return {
    key: newFormKey('ingredient'),
    ingredientId: matchedId === null ? '' : String(matchedId),
    ingredientName: matchedId === null ? '' : ingredient.name,
    ingredientActive: true,
    quantity: quantityText,
    unitId: unitId === null ? '' : String(unitId),
    unitLabel: unitId === null ? '' : (ingredient.unit.matched_symbol ?? ingredient.unit.raw),
    unitActive: true,
    preparationNote,
    isOptional: false,
  }
}

/**
 * When the parser left `quantity` null it kept the original token in `raw`.
 * Show only a *numeric-looking* token (ranges, multipliers, mixed numbers the
 * parser could not resolve) in the preparation note so the reviewer can turn
 * it into a number. Word quantities ("a pinch", "some") are intentionally not
 * surfaced as a prep note — they belong to the raw line, not the note.
 */
function extractRawQuantityText(raw: string, name: string): string {
  const trimmed = raw.trim()
  if (trimmed.length === 0) {
    return ''
  }
  if (name.length === 0) {
    return ''
  }
  // The raw line normally starts with the quantity token then the name; take
  // the prefix before the matched name when it is a recognizable short token.
  const index = trimmed.toLowerCase().indexOf(name.toLowerCase())
  if (index <= 0 || index > 24) {
    return ''
  }
  const prefix = trimmed.slice(0, index).trim()
  // Only numeric-ish prefixes are useful as an editable quantity hint.
  return /[\d/]/.test(prefix) ? prefix : ''
}

/**
 * Per-row import-resolution metadata (M4-09). Keyed by the {@link IngredientFormRow.key}
 * created by {@link fromImportDraft}, so it survives adding, removing, and
 * reordering rows. A line is only surfaced as "needs an ingredient" while its
 * row has no `ingredientId`; mapping the row clears it naturally.
 */
export interface UnknownIngredientLine {
  /** The original parsed ingredient line, shown verbatim to the reviewer. */
  raw: string
  /** Parsed canonical-name text, offered as the default create name. */
  name: string
  /** Draft suggestions (already bounded by the server) for this line. */
  suggestions: ImportReference[]
}

/**
 * Builds the unknown-line map for a freshly seeded draft. `rows` must be the
 * rows returned by {@link fromImportDraft} for the same draft, in order.
 */
export function seedUnknownLines(
  draft: ImportRecipeDraft,
  rows: IngredientFormRow[],
): Record<string, UnknownIngredientLine> {
  const unknown: Record<string, UnknownIngredientLine> = {}
  draft.ingredients.forEach((ingredient, index) => {
    const row = rows[index]
    if (row === undefined || !ingredient.is_unknown) {
      return
    }
    unknown[row.key] = {
      raw: ingredient.raw,
      name: ingredient.name,
      suggestions: ingredient.suggestions,
    }
  })
  return unknown
}

export function fromImportDraft(draft: ImportRecipeDraft): RecipeFormState {
  const sections: InstructionSectionFormRow[] = draft.instruction_sections.map((section) => ({
    key: newFormKey('section'),
    title: section.title ?? '',
    steps: section.steps.length > 0 ? section.steps : [''],
  }))

  return {
    name: draft.name,
    description: draft.description ?? '',
    sourceSite: draft.source_site ?? '',
    sourceUrl: draft.source_url ?? '',
    author: draft.author ?? '',
    prepMinutes: draft.prep_minutes === null ? '' : String(draft.prep_minutes),
    cookMinutes: draft.cook_minutes === null ? '' : String(draft.cook_minutes),
    notes: draft.notes ?? '',
    status: 'normal',
    isFavorite: false,
    isVegan: false,
    isVegetarian: false,
    ingredients: draft.ingredients.map(ingredientRow),
    groups: [],
    sections,
    proteinIds: draft.proteins.map((protein) => protein.id),
    tagIds: draft.tags.map((tag) => tag.id),
  }
}

/** A friendly, non-leaking message for an import failure. */
export function describeImportError(code: string, retryAfterSeconds: number | null): string {
  switch (code) {
    case 'IMPORT_URL_INVALID':
      return 'That does not look like a valid recipe link. Paste a full http:// or https:// address.'
    case 'IMPORT_URL_BLOCKED':
      return 'That address cannot be imported for security reasons. Try a different link.'
    case 'IMPORT_FETCH_FAILED':
      return 'The page could not be reached. Check the link and try again.'
    case 'IMPORT_UNSUPPORTED_CONTENT':
      return 'That link returned content this importer cannot read. Try a different page.'
    case 'IMPORT_TOO_LARGE':
      return 'That page is too large to import. Try a different link.'
    case 'IMPORT_NO_RECIPE':
      return 'No recipe could be found on that page. Try a different link.'
    case 'IMPORT_RATE_LIMITED': {
      const suffix =
        retryAfterSeconds !== null && retryAfterSeconds > 0
          ? ` Try again in about ${formatSeconds(retryAfterSeconds)}.`
          : ' Try again later.'
      return `You have imported too many recipes recently.${suffix}`
    }
    default:
      return 'Import failed. Check the server and try again.'
  }
}

function formatSeconds(seconds: number): string {
  if (seconds < 60) {
    return `${seconds} second${seconds === 1 ? '' : 's'}`
  }
  const minutes = Math.ceil(seconds / 60)
  return `${minutes} minute${minutes === 1 ? '' : 's'}`
}
