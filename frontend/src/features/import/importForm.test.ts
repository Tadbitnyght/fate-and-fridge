import { describe, expect, it } from 'vitest'

import type { ImportRecipeDraft } from '../../api/importRecipes'
import { fromImportDraft, seedUnknownLines, validateUnknownUnits } from './importForm'

/**
 * BUG-002: grouped import drafts do not exist. The draft contract
 * (docs/API.md §15) carries `ingredients` only — `ingredient_groups` belongs to
 * `POST /recipes` (§23) — so the nested unknown-unit seeding/validation/
 * rendering branches were removed rather than half-wired. These tests pin the
 * two facts that make the removal safe: an import draft always starts flat, and
 * the flat unknown-unit path is still seeded and validated.
 */
function draftFixture(): ImportRecipeDraft {
  return {
    index: 0,
    name: 'Lasagna',
    description: null,
    author: null,
    source_url: null,
    source_site: null,
    prep_minutes: null,
    cook_minutes: null,
    notes: null,
    image_source_url: null,
    name_matches: [],
    ingredients: [
      {
        raw: '1 pkg flour',
        quantity: 1,
        name: 'Flour',
        preparation_note: null,
        matched_ingredient_id: 12,
        match_kind: 'exact',
        is_unknown: false,
        suggestions: [],
        unit: {
          raw: 'pkg',
          matched_unit_id: null,
          matched_symbol: null,
          is_unknown: true,
          suggested_dimension: 'mass',
          suggestions: [{ id: 20, symbol: 'g', display_name: 'gram' }],
        },
      },
    ],
    instruction_sections: [],
    proteins: [],
    tags: [],
    warnings: [],
  }
}

describe('importForm (BUG-002)', () => {
  it('never builds ingredient groups from an import draft', () => {
    const draft = draftFixture()
    const state = fromImportDraft(draft)

    // If this ever fails, grouped import drafts have arrived: seed their lines
    // in `seedUnknownLines`, validate them in `validateUnknownUnits`, and thread
    // `unknownByRowKey` plus the create controls into the group row list in
    // `IngredientFields` in the same change.
    expect(state.groups).toEqual([])
    expect(Object.keys(seedUnknownLines(draft, state.ingredients))).toEqual(
      state.ingredients.map((row) => row.key),
    )
  })

  it('still blocks an unresolved raw unit on a flat line', () => {
    const draft = draftFixture()
    const state = fromImportDraft(draft)

    const errors = validateUnknownUnits(state, seedUnknownLines(draft, state.ingredients))

    expect(errors).toEqual({
      'ingredients[0].unit_id': expect.stringContaining('pkg'),
    })
  })
})
