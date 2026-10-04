import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { ImportParseResponse } from '../../api/importRecipes'
import type { Recipe } from '../../api/recipes'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

const singleCandidate: ImportParseResponse = {
  source: {
    url: 'https://example.com/lasagna',
    site: 'example.com',
    page_title: null,
    author: 'Jane Cook',
    fetched_at: '2026-10-03T00:00:00+00:00',
    parse_method: 'json_ld',
  },
  source_url_match: null,
  capabilities: { can_create_reference_data: false },
  recipes: [
    {
      index: 0,
      name: 'Lasagna',
      description: 'A cozy bake',
      author: 'Jane Cook',
      source_url: 'https://example.com/lasagna',
      source_site: 'example.com',
      prep_minutes: 20,
      cook_minutes: 45,
      notes: null,
      image_source_url: 'https://example.com/lasagna.jpg',
      name_matches: [],
      ingredients: [
        {
          raw: '1 1/2 cups flour, sifted',
          quantity: 1.5,
          name: 'Flour',
          preparation_note: 'sifted',
          matched_ingredient_id: 12,
          match_kind: 'exact',
          is_unknown: false,
          suggestions: [],
          unit: {
            raw: 'cups',
            matched_unit_id: 3,
            matched_symbol: 'cup',
            is_unknown: false,
            suggested_dimension: null,
            suggestions: [],
          },
        },
        {
          raw: 'a pinch of salt',
          quantity: null,
          name: 'salt',
          preparation_note: null,
          matched_ingredient_id: null,
          match_kind: null,
          is_unknown: true,
          suggestions: [{ id: 12, name: 'Salt' }],
          unit: {
            raw: '',
            matched_unit_id: null,
            matched_symbol: null,
            is_unknown: false,
            suggested_dimension: null,
            suggestions: [],
          },
        },
      ],
      instruction_sections: [{ title: null, steps: ['Boil the noodles.'] }],
      proteins: [],
      tags: [],
      warnings: ['ingredient_unknown'],
    },
  ],
}

const multiCandidate: ImportParseResponse = {
  ...singleCandidate,
  recipes: [
    { ...singleCandidate.recipes[0], index: 0, name: 'Lasagna' },
    { ...singleCandidate.recipes[0], index: 1, name: 'Lasagna Bolognese' },
  ],
}

const createdRecipe: Recipe = {
  id: 1,
  name: 'Lasagna',
  description: 'A cozy bake',
  source_site: 'example.com',
  source_url: 'https://example.com/lasagna',
  author: 'Jane Cook',
  prep_minutes: 20,
  cook_minutes: 45,
  notes: null,
  status: 'normal',
  is_favorite: false,
  is_vegan: false,
  is_vegetarian: false,
  created_at: '2026-10-03T00:00:00+00:00',
  updated_at: '2026-10-03T00:00:00+00:00',
  image: null,
  ingredient_groups: [],
  ingredients: [],
  instruction_sections: [],
  proteins: [],
  tags: [],
  statistics: { last_eaten: null, next_planned: null, times_eaten: null },
}

const flour = {
  id: 12,
  canonical_name: 'Flour',
  department_id: null,
  is_active: true,
  created_at: '2026-10-01T12:00:00Z',
  updated_at: '2026-10-01T12:00:00Z',
  aliases: [],
}

const salt = {
  id: 14,
  canonical_name: 'Salt',
  department_id: null,
  is_active: true,
  created_at: '2026-10-01T12:00:00Z',
  updated_at: '2026-10-01T12:00:00Z',
  aliases: [],
}

/** The salted draft's unknown "salt" line (index 1) suggests Salt (id 14). */
const saltedCandidate: ImportParseResponse = {
  ...singleCandidate,
  recipes: [
    {
      ...singleCandidate.recipes[0],
      ingredients: [
        singleCandidate.recipes[0].ingredients[0],
        {
          ...singleCandidate.recipes[0].ingredients[1],
          suggestions: [{ id: 14, name: 'Salt' }],
        },
      ],
    },
  ],
}

/** Same draft but the actor holds `reference_data.edit`, so inline create is on. */
const createCapableCandidate: ImportParseResponse = {
  ...saltedCandidate,
  capabilities: { can_create_reference_data: true },
}

const cup = {
  id: 3,
  symbol: 'cup',
  display_name: 'cup',
  dimension: 'volume',
  base_factor: 1,
  is_custom: false,
  is_active: true,
  aliases: [],
}

function baseMocks(routes: MockRoute[], session: unknown = adminSession): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: session },
    { path: '/health/db', body: { status: 'ok', database: 'connected' } },
    {
      path: '/health',
      body: { status: 'ok', app: 'Fate & Fridge', version: '0.1.0', environment: 'test' },
    },
    { path: '/ingredients?', body: [flour, salt] },
    { path: '/units?', body: [cup] },
    { path: '/proteins?', body: [] },
    { path: '/tags?', body: [] },
    { path: '/departments?', body: [] },
    ...routes,
  ])
}

function requestBodyFor(mock: MockFetch, path: string, method: string): unknown {
  const call = mock.fetchMock.mock.calls.find(
    ([url, init]) =>
      String(url).includes(path) &&
      ((init as RequestInit | undefined)?.method ?? 'GET').toUpperCase() === method,
  )
  if (call === undefined) {
    return undefined
  }
  const body = (call[1] as RequestInit | undefined)?.body
  return body === undefined ? undefined : (JSON.parse(String(body)) as unknown)
}

function errorRoute(code: string, status = 422): MockRoute {
  return {
    path: '/import/recipe-url',
    method: 'POST',
    status,
    body: { error: { code, message: code, details: {} } },
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('import page', () => {
  it('validates an empty URL before contacting the server', async () => {
    const mock = baseMocks([])

    renderWithProviders(<App />, { route: '/recipes/import' })
    await screen.findByRole('heading', { level: 2, name: 'Import from URL' })

    fireEvent.click(screen.getByRole('button', { name: 'Import' }))

    expect(await screen.findByText('Paste a recipe link to import.')).toBeInTheDocument()
    expect(mock.callsFor('/import/recipe-url')).toBe(0)
  })

  it('parses a single candidate and pre-fills the review form', async () => {
    baseMocks([{ path: '/import/recipe-url', method: 'POST', body: singleCandidate }])

    renderWithProviders(<App />, { route: '/recipes/import' })
    fireEvent.change(await screen.findByLabelText('Recipe URL'), {
      target: { value: 'https://example.com/lasagna' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Import' }))

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Review imported recipe' }),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveValue('Lasagna')
    expect(screen.getByLabelText('Step 1')).toHaveValue('Boil the noodles.')
    // Parsed quantity hints render the fraction form.
    expect(screen.getByText('Shows as 1 1/2')).toBeInTheDocument()
    // Unknown ingredient line is flagged.
    expect(screen.getByText(/Some ingredient lines could not be matched/i)).toBeInTheDocument()
    // The single candidate picker is hidden.
    expect(screen.queryByText(/This page has 2 recipes/)).not.toBeInTheDocument()
  })

  it('lets the user pick among multiple candidates', async () => {
    baseMocks([{ path: '/import/recipe-url', method: 'POST', body: multiCandidate }])

    renderWithProviders(<App />, { route: '/recipes/import' })
    fireEvent.change(await screen.findByLabelText('Recipe URL'), {
      target: { value: 'https://example.com/many' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Import' }))

    await screen.findByRole('heading', { level: 2, name: 'Review imported recipe' })
    expect(screen.getByText('This page has 2 recipes')).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveValue('Lasagna')

    fireEvent.click(screen.getByRole('radio', { name: 'Lasagna Bolognese' }))
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Lasagna Bolognese'))
  })

  it('saves the reviewed draft through POST /recipes', async () => {
    const mock = baseMocks([
      { path: '/import/recipe-url', method: 'POST', body: singleCandidate },
      { path: '/recipes', method: 'POST', status: 201, body: createdRecipe },
      {
        path: '/recipes/1/image/from-url',
        method: 'POST',
        body: {
          id: 1,
          alt_text: null,
          source_url: null,
          created_at: '',
          url: '/api/v1/recipes/1/image',
        },
      },
      { path: '/recipes/1', method: 'GET', body: createdRecipe },
    ])

    renderWithProviders(<App />, { route: '/recipes/import' })
    fireEvent.change(await screen.findByLabelText('Recipe URL'), {
      target: { value: 'https://example.com/lasagna' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Import' }))
    await screen.findByRole('heading', { level: 2, name: 'Review imported recipe' })

    // Reference data resolves asynchronously; wait until the ingredient pickers
    // have their options before resolving the unknown line.
    await waitFor(() => {
      const first = screen.getAllByLabelText('Ingredient')[0]
      expect(first.querySelectorAll('option').length).toBeGreaterThan(1)
    })

    // Resolve the unknown ingredient line to the existing match.
    const selects = screen.getAllByLabelText('Ingredient')
    fireEvent.change(selects[selects.length - 1], { target: { value: '12' } })

    fireEvent.click(screen.getByRole('button', { name: 'Save recipe' }))

    await waitFor(() => expect(requestBodyFor(mock, '/recipes', 'POST')).toBeDefined(), {
      timeout: 4000,
    })
    const body = requestBodyFor(mock, '/recipes', 'POST') as Record<string, unknown>
    expect(body.name).toBe('Lasagna')
    expect(body.ingredients).toEqual([
      {
        ingredient_id: 12,
        quantity: 1.5,
        unit_id: 3,
        preparation_note: 'sifted',
        is_optional: false,
      },
      {
        ingredient_id: 12,
        quantity: null,
        unit_id: null,
        preparation_note: null,
        is_optional: false,
      },
    ])
    expect(body.instruction_sections).toEqual([{ title: null, steps: ['Boil the noodles.'] }])
  })

  it('shows a friendly message for each import error code', async () => {
    const cases: Array<[string, number, RegExp]> = [
      ['IMPORT_URL_INVALID', 400, /valid recipe link/i],
      ['IMPORT_URL_BLOCKED', 422, /security reasons/i],
      ['IMPORT_FETCH_FAILED', 502, /could not be reached/i],
      ['IMPORT_UNSUPPORTED_CONTENT', 415, /cannot read/i],
      ['IMPORT_TOO_LARGE', 413, /too large/i],
      ['IMPORT_NO_RECIPE', 422, /No recipe could be found/i],
      ['IMPORT_RATE_LIMITED', 429, /too many recipes recently/i],
    ]

    for (const [code, status, pattern] of cases) {
      const mock = baseMocks([errorRoute(code, status)])
      const view = renderWithProviders(<App />, { route: '/recipes/import' })
      fireEvent.change(await screen.findByLabelText('Recipe URL'), {
        target: { value: 'https://example.com/bad' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Import' }))
      expect(await screen.findByText(pattern)).toBeInTheDocument()
      expect(mock.callsFor('/import/recipe-url')).toBe(1)
      view.unmount()
      vi.unstubAllGlobals()
    }
  })

  it('cancels the parse and returns to the empty URL form', async () => {
    const mock = baseMocks([{ path: '/import/recipe-url', method: 'POST', body: singleCandidate }])

    renderWithProviders(<App />, { route: '/recipes/import' })
    fireEvent.change(await screen.findByLabelText('Recipe URL'), {
      target: { value: 'https://example.com/lasagna' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Import' }))
    await screen.findByRole('heading', { level: 2, name: 'Review imported recipe' })

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Import from URL' }),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Recipe URL')).toHaveValue('')
    expect(mock.callsFor('/recipes')).toBe(0)
  })

  it('hides the import flow from view-only members', async () => {
    baseMocks(
      [{ path: '/recipes?', method: 'GET', body: { items: [], total: 0, limit: 24, offset: 0 } }],
      memberSession,
    )

    renderWithProviders(<App />, { route: '/recipes' })
    await screen.findByRole('heading', { level: 2, name: 'Recipes' })

    expect(screen.queryByRole('link', { name: 'Import from URL' })).not.toBeInTheDocument()
  })

  it('shows the import entry point on the recipe browser for editors', async () => {
    baseMocks([
      { path: '/recipes?', method: 'GET', body: { items: [], total: 0, limit: 24, offset: 0 } },
    ])

    renderWithProviders(<App />, { route: '/recipes' })
    await screen.findByRole('heading', { level: 2, name: 'Recipes' })

    expect(screen.getByRole('link', { name: 'Import from URL' })).toHaveAttribute(
      'href',
      '/recipes/import',
    )
  })
})

describe('import unknown ingredient flow', () => {
  async function openReview(candidate: ImportParseResponse, routes: MockRoute[] = []) {
    const mock = baseMocks([
      { path: '/import/recipe-url', method: 'POST', body: candidate },
      ...routes,
    ])
    renderWithProviders(<App />, { route: '/recipes/import' })
    fireEvent.change(await screen.findByLabelText('Recipe URL'), {
      target: { value: 'https://example.com/lasagna' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Import' }))
    await screen.findByRole('heading', { level: 2, name: 'Review imported recipe' })
    // Reference data resolves asynchronously; wait for the picker options.
    await waitFor(() => {
      const first = screen.getAllByLabelText('Ingredient')[0]
      expect(first.querySelectorAll('option').length).toBeGreaterThan(1)
    })
    return mock
  }

  it('surfaces an unknown line with its raw text and suggestions', async () => {
    await openReview(saltedCandidate)

    expect(screen.getByText(/Needs an ingredient before this recipe can be saved/i)).toBeVisible()
    expect(screen.getByText(/a pinch of salt/)).toBeVisible()
    // The draft suggestion renders as a one-tap mapping button.
    expect(screen.getByRole('button', { name: /Use Salt for ingredient 2/i })).toBeVisible()
  })

  it('maps an unknown line to an existing ingredient via the suggestion', async () => {
    const mock = await openReview(saltedCandidate)

    fireEvent.click(screen.getByRole('button', { name: /Use Salt for ingredient 2/i }))

    const selects = screen.getAllByLabelText('Ingredient') as HTMLSelectElement[]
    expect(selects[selects.length - 1].value).toBe('14')
    // Resolving the last unknown clears the blocking state.
    await waitFor(() =>
      expect(
        screen.queryByText(/Needs an ingredient before this recipe can be saved/i),
      ).not.toBeInTheDocument(),
    )
    expect(mock.callsFor('/ingredients')).toBe(0)
  })

  it('maps an unknown line through the ingredient select', async () => {
    await openReview(saltedCandidate)

    const selects = screen.getAllByLabelText('Ingredient') as HTMLSelectElement[]
    fireEvent.change(selects[selects.length - 1], { target: { value: '14' } })

    await waitFor(() =>
      expect(
        screen.queryByText(/Needs an ingredient before this recipe can be saved/i),
      ).not.toBeInTheDocument(),
    )
    expect(selects[selects.length - 1].value).toBe('14')
  })

  it('blocks save while a line is unresolved and reports it per line', async () => {
    const mock = await openReview(saltedCandidate)

    fireEvent.click(screen.getByRole('button', { name: 'Save recipe' }))

    expect(await screen.findByRole('heading', { name: /Fix these problems/i })).toBeVisible()
    expect(screen.getAllByText('Choose an ingredient from the list.').length).toBeGreaterThan(0)
    expect(mock.callsFor('/recipes')).toBe(0)
  })

  it('creates an ingredient inline when permitted, then maps the line', async () => {
    const created = {
      id: 77,
      canonical_name: 'Sea Salt',
      department_id: null,
      is_active: true,
      created_at: '2026-10-01T12:00:00Z',
      updated_at: '2026-10-01T12:00:00Z',
      aliases: [],
    }
    const mock = await openReview(createCapableCandidate, [
      { path: '/ingredients', method: 'POST', status: 201, body: created },
    ])

    // The create affordance is shown and pre-fills the parsed name.
    expect(screen.getByLabelText('New ingredient name')).toHaveValue('salt')

    fireEvent.change(screen.getByLabelText('New ingredient name'), {
      target: { value: 'Sea Salt' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create ingredient' }))

    await waitFor(() => {
      const selects = screen.getAllByLabelText('Ingredient') as HTMLSelectElement[]
      expect(selects[selects.length - 1].value).toBe('77')
    })
    expect(requestBodyFor(mock, '/ingredients', 'POST')).toMatchObject({
      canonical_name: 'Sea Salt',
      department_id: null,
    })
  })

  it('maps a duplicate inline create to the existing ingredient', async () => {
    const mock = await openReview(createCapableCandidate, [
      {
        path: '/ingredients',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'INGREDIENT_EXISTS',
            message: 'Ingredient exists',
            details: { existing_id: 14 },
          },
        },
      },
    ])

    fireEvent.click(screen.getByRole('button', { name: 'Create ingredient' }))

    await waitFor(() => {
      const selects = screen.getAllByLabelText('Ingredient') as HTMLSelectElement[]
      expect(selects[selects.length - 1].value).toBe('14')
    })
    // No error card remains after the duplicate is resolved.
    expect(screen.queryByText(/Creating the ingredient failed/i)).not.toBeInTheDocument()
    expect(mock.callsFor('/ingredients')).toBe(1)
  })

  it('hides inline create and shows ask-an-admin guidance when not permitted', async () => {
    await openReview(saltedCandidate)

    expect(screen.queryByLabelText('New ingredient name')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create ingredient' })).not.toBeInTheDocument()
    expect(screen.getByText(/ask an Admin to add this one/i)).toBeVisible()
    // Mapping to an existing ingredient still works.
    expect(screen.getByRole('button', { name: /Use Salt for ingredient 2/i })).toBeVisible()
  })

  it('saves a fully resolved draft and never sends a free-text ingredient name', async () => {
    const mock = await openReview(saltedCandidate, [
      { path: '/recipes', method: 'POST', status: 201, body: createdRecipe },
      { path: '/recipes/1', method: 'GET', body: createdRecipe },
    ])

    fireEvent.click(screen.getByRole('button', { name: /Use Salt for ingredient 2/i }))
    fireEvent.click(screen.getByRole('button', { name: 'Save recipe' }))

    await waitFor(() => expect(requestBodyFor(mock, '/recipes', 'POST')).toBeDefined(), {
      timeout: 4000,
    })
    const body = requestBodyFor(mock, '/recipes', 'POST') as {
      ingredients: Array<Record<string, unknown>>
    }
    expect(body.ingredients).toHaveLength(2)
    expect(body.ingredients[1]).toMatchObject({ ingredient_id: 14 })
    // Only ids cross the wire — never the parsed free-text name.
    expect(JSON.stringify(body)).not.toContain('a pinch of salt')
  })
})
