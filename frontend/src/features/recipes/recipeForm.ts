import type {
  Recipe,
  RecipeIngredient,
  RecipeIngredientWrite,
  RecipeStatus,
  RecipeWriteRequest,
} from '../../api/recipes'
import type { FieldErrors } from '../../utils/fieldErrors'

/**
 * Editable form model for a recipe (M3-10). Ingredient/unit ids are kept as
 * strings so the selects have an empty "choose" option; numbers are parsed when
 * the request is built. Reference names are retained so a retired reference
 * that is already on the recipe stays displayable and selectable.
 */

export interface IngredientFormRow {
  key: string
  ingredientId: string
  ingredientName: string
  ingredientActive: boolean
  quantity: string
  unitId: string
  unitLabel: string
  unitActive: boolean
  preparationNote: string
  isOptional: boolean
}

export interface IngredientGroupFormRow {
  key: string
  title: string
  ingredients: IngredientFormRow[]
}

export interface InstructionSectionFormRow {
  key: string
  title: string
  steps: string[]
}

export interface RecipeFormState {
  name: string
  description: string
  sourceSite: string
  sourceUrl: string
  author: string
  prepMinutes: string
  cookMinutes: string
  notes: string
  status: RecipeStatus
  isFavorite: boolean
  isVegan: boolean
  isVegetarian: boolean
  ingredients: IngredientFormRow[]
  groups: IngredientGroupFormRow[]
  sections: InstructionSectionFormRow[]
  proteinIds: number[]
  tagIds: number[]
}

let keyCounter = 0

export function newFormKey(prefix: string): string {
  keyCounter += 1
  return `${prefix}-${keyCounter}`
}

export function emptyIngredientRow(): IngredientFormRow {
  return {
    key: newFormKey('ingredient'),
    ingredientId: '',
    ingredientName: '',
    ingredientActive: true,
    quantity: '',
    unitId: '',
    unitLabel: '',
    unitActive: true,
    preparationNote: '',
    isOptional: false,
  }
}

export function emptyIngredientGroup(): IngredientGroupFormRow {
  return { key: newFormKey('group'), title: '', ingredients: [emptyIngredientRow()] }
}

export function emptyInstructionSection(): InstructionSectionFormRow {
  return { key: newFormKey('section'), title: '', steps: [''] }
}

export function emptyRecipeFormState(): RecipeFormState {
  return {
    name: '',
    description: '',
    sourceSite: '',
    sourceUrl: '',
    author: '',
    prepMinutes: '',
    cookMinutes: '',
    notes: '',
    status: 'normal',
    isFavorite: false,
    isVegan: false,
    isVegetarian: false,
    ingredients: [],
    groups: [],
    sections: [],
    proteinIds: [],
    tagIds: [],
  }
}

function textOrEmpty(value: string | null | undefined): string {
  return value ?? ''
}

function minutesToText(value: number | null): string {
  return value === null ? '' : String(value)
}

function quantityToText(value: number | null): string {
  return value === null ? '' : String(value)
}

function ingredientToRow(ingredient: RecipeIngredient): IngredientFormRow {
  return {
    key: newFormKey('ingredient'),
    ingredientId: String(ingredient.ingredient.id),
    ingredientName: ingredient.ingredient.name,
    ingredientActive: ingredient.ingredient.is_active,
    quantity: quantityToText(ingredient.quantity),
    unitId: ingredient.unit === null ? '' : String(ingredient.unit.id),
    unitLabel: ingredient.unit === null ? '' : ingredient.unit.symbol,
    unitActive: ingredient.unit === null ? true : ingredient.unit.is_active,
    preparationNote: textOrEmpty(ingredient.preparation_note),
    isOptional: ingredient.is_optional,
  }
}

export function fromRecipe(recipe: Recipe): RecipeFormState {
  return {
    name: recipe.name,
    description: textOrEmpty(recipe.description),
    sourceSite: textOrEmpty(recipe.source_site),
    sourceUrl: textOrEmpty(recipe.source_url),
    author: textOrEmpty(recipe.author),
    prepMinutes: minutesToText(recipe.prep_minutes),
    cookMinutes: minutesToText(recipe.cook_minutes),
    notes: textOrEmpty(recipe.notes),
    status: recipe.status,
    isFavorite: recipe.is_favorite,
    isVegan: recipe.is_vegan,
    isVegetarian: recipe.is_vegetarian,
    ingredients: recipe.ingredients.map(ingredientToRow),
    groups: recipe.ingredient_groups.map((group) => ({
      key: newFormKey('group'),
      title: textOrEmpty(group.title),
      ingredients: group.ingredients.map(ingredientToRow),
    })),
    sections: recipe.instruction_sections.map((section) => ({
      key: newFormKey('section'),
      title: textOrEmpty(section.title),
      steps: section.steps.map((step) => step.text),
    })),
    proteinIds: recipe.proteins.map((protein) => protein.id),
    tagIds: recipe.tags.map((tag) => tag.id),
  }
}

function blankToNull(value: string): string | null {
  const trimmed = value.trim()
  return trimmed.length === 0 ? null : trimmed
}

/** Parses a whole-minute field. Returns `undefined` for invalid input. */
export function parseOptionalMinutes(value: string): number | null | undefined {
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    return null
  }
  if (!/^\d+$/.test(trimmed)) {
    return undefined
  }
  const parsed = Number(trimmed)
  return Number.isSafeInteger(parsed) ? parsed : undefined
}

/** Parses a quantity field. Returns `undefined` for invalid input. */
export function parseQuantity(value: string): number | null | undefined {
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    return null
  }
  const parsed = Number(trimmed)
  return Number.isFinite(parsed) ? parsed : undefined
}

function ingredientWrite(row: IngredientFormRow): RecipeIngredientWrite {
  const parsed = parseQuantity(row.quantity)
  return {
    ingredient_id: Number(row.ingredientId),
    quantity: parsed === undefined ? null : parsed,
    unit_id: row.unitId === '' ? null : Number(row.unitId),
    preparation_note: blankToNull(row.preparationNote),
    is_optional: row.isOptional,
  }
}

/** Builds the create/update body; present collections are full replacements. */
export function buildRecipeRequest(state: RecipeFormState): RecipeWriteRequest {
  return {
    name: state.name.trim(),
    description: blankToNull(state.description),
    source_site: blankToNull(state.sourceSite),
    source_url: blankToNull(state.sourceUrl),
    author: blankToNull(state.author),
    prep_minutes: parseOptionalMinutes(state.prepMinutes) ?? null,
    cook_minutes: parseOptionalMinutes(state.cookMinutes) ?? null,
    notes: blankToNull(state.notes),
    status: state.status,
    is_favorite: state.isFavorite,
    is_vegan: state.isVegan,
    is_vegetarian: state.isVegetarian,
    ingredients: state.ingredients.map(ingredientWrite),
    ingredient_groups: state.groups.map((group) => ({
      title: blankToNull(group.title),
      ingredients: group.ingredients.map(ingredientWrite),
    })),
    instruction_sections: state.sections.map((section) => ({
      title: blankToNull(section.title),
      steps: section.steps.map((step) => step.trim()).filter((step) => step.length > 0),
    })),
    protein_ids: state.proteinIds,
    tag_ids: state.tagIds,
  }
}

const MAX_MINUTES = 1440
const MAX_QUANTITY = 1_000_000
const MAX_QUANTITY_DECIMALS = 14

function isHttpUrl(value: string): boolean {
  try {
    const parsed = new URL(value)
    return parsed.protocol === 'http:' || parsed.protocol === 'https:'
  } catch {
    return false
  }
}

function validateIngredientRow(row: IngredientFormRow, prefix: string, errors: FieldErrors): void {
  if (row.ingredientId === '') {
    errors[`${prefix}.ingredient_id`] = 'Choose an ingredient from the list.'
  }
  const quantity = parseQuantity(row.quantity)
  if (quantity === undefined) {
    errors[`${prefix}.quantity`] = 'Enter a number or leave quantity blank.'
  } else if (quantity !== null && (quantity < 0 || quantity > MAX_QUANTITY)) {
    errors[`${prefix}.quantity`] = `Quantity must be between 0 and ${MAX_QUANTITY}.`
  } else if (quantity !== null && decimalPlaces(row.quantity.trim()) > MAX_QUANTITY_DECIMALS) {
    errors[`${prefix}.quantity`] =
      `Quantity can have at most ${MAX_QUANTITY_DECIMALS} decimal places.`
  }
}

/** Fractional digit count of a plain decimal string; scientific notation is skipped. */
function decimalPlaces(value: string): number {
  if (value.includes('e') || value.includes('E')) {
    return 0
  }
  const dot = value.indexOf('.')
  return dot === -1 ? 0 : value.length - dot - 1
}

/**
 * Client-side validation mirroring the server contract (docs/API.md §23).
 * Keys use the server's index-qualified field paths so server validation
 * responses line up with the same display map.
 */
export function validateRecipeForm(state: RecipeFormState): FieldErrors {
  const errors: FieldErrors = {}

  const name = state.name.trim()
  if (name.length === 0) {
    errors['name'] = 'Enter a recipe name.'
  } else if (name.length > 200) {
    errors['name'] = 'Recipe names are limited to 200 characters.'
  }

  for (const [field, label] of [
    ['prep_minutes', 'Prep time'],
    ['cook_minutes', 'Cook time'],
  ] as const) {
    const value = field === 'prep_minutes' ? state.prepMinutes : state.cookMinutes
    const parsed = parseOptionalMinutes(value)
    if (parsed === undefined) {
      errors[field] = `${label} must be whole minutes (0–${MAX_MINUTES}).`
    } else if (parsed !== null && parsed > MAX_MINUTES) {
      errors[field] = `${label} must be ${MAX_MINUTES} minutes or fewer.`
    }
  }

  if (state.sourceUrl.trim().length > 0 && !isHttpUrl(state.sourceUrl.trim())) {
    errors['source_url'] = 'Enter a full http:// or https:// URL.'
  }

  state.ingredients.forEach((row, index) => {
    validateIngredientRow(row, `ingredients[${index}]`, errors)
  })

  state.groups.forEach((group, groupIndex) => {
    group.ingredients.forEach((row, index) => {
      validateIngredientRow(row, `ingredient_groups[${groupIndex}].ingredients[${index}]`, errors)
    })
  })

  return errors
}

export type { FieldErrors }
