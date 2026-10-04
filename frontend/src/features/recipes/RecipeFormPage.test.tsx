import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Recipe } from '../../api/recipes'
import type { Ingredient, Unit } from '../../api/referenceData'
import { adminSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

const chicken: Ingredient = {
  id: 5,
  canonical_name: 'Chicken',
  department_id: null,
  is_active: true,
  created_at: '2026-10-01T12:00:00Z',
  updated_at: '2026-10-01T12:00:00Z',
  aliases: [],
}

const grams: Unit = {
  id: 2,
  symbol: 'g',
  display_name: 'gram',
  dimension: 'mass',
  base_factor: 1,
  is_custom: false,
  is_active: true,
  aliases: [],
}

const detail: Recipe = {
  id: 1,
  name: 'Chicken Dinner',
  description: null,
  source_site: null,
  source_url: null,
  author: null,
  prep_minutes: 10,
  cook_minutes: 30,
  notes: null,
  status: 'normal',
  is_favorite: false,
  is_vegan: false,
  is_vegetarian: false,
  created_at: '2026-10-01T12:00:00Z',
  updated_at: '2026-10-01T12:00:00Z',
  image: null,
  ingredient_groups: [],
  ingredients: [
    {
      id: 11,
      ingredient: { id: 5, name: 'Chicken', is_active: true },
      quantity: 1,
      unit: null,
      preparation_note: null,
      is_optional: false,
      sort_order: 0,
    },
  ],
  instruction_sections: [
    {
      id: 21,
      title: 'Prep',
      sort_order: 0,
      steps: [{ id: 31, text: 'Chop', sort_order: 0 }],
    },
  ],
  proteins: [{ id: 5, name: 'Chicken', is_active: true }],
  tags: [],
  statistics: { last_eaten: null, next_planned: null, times_eaten: null },
}

function baseMocks(routes: MockRoute[]): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    { path: '/health/db', body: { status: 'ok', database: 'connected' } },
    {
      path: '/health',
      body: { status: 'ok', app: 'Fate & Fridge', version: '0.1.0', environment: 'test' },
    },
    { path: '/ingredients?', body: [chicken] },
    { path: '/units?', body: [grams] },
    { path: '/proteins?', body: [{ id: 5, name: 'Chicken', is_active: true }] },
    { path: '/tags?', body: [] },
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

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('recipe form', () => {
  it('creates a recipe with ingredients, instructions, and proteins', async () => {
    const mock = baseMocks([
      { path: '/recipes', method: 'POST', status: 201, body: detail },
      { path: '/recipes/1', method: 'GET', body: detail },
    ])

    renderWithProviders(<App />, { route: '/recipes/new' })

    expect(await screen.findByRole('heading', { level: 2, name: 'New recipe' })).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Chicken Dinner' } })

    fireEvent.click(screen.getByRole('button', { name: 'Add ingredient' }))
    fireEvent.change(screen.getByLabelText('Ingredient'), { target: { value: '5' } })
    fireEvent.change(screen.getByLabelText('Quantity'), { target: { value: '1.5' } })
    fireEvent.click(screen.getByLabelText('Optional ingredient'))

    fireEvent.click(screen.getByRole('button', { name: 'Add section' }))
    fireEvent.change(screen.getByLabelText('Step 1'), { target: { value: 'Roast the chicken.' } })

    fireEvent.click(screen.getByLabelText('Chicken'))

    fireEvent.click(screen.getByRole('button', { name: 'Create recipe' }))

    await waitFor(() => {
      expect(requestBodyFor(mock, '/recipes', 'POST')).toBeDefined()
    })

    const body = requestBodyFor(mock, '/recipes', 'POST') as Record<string, unknown>
    expect(body.name).toBe('Chicken Dinner')
    expect(body.ingredients).toEqual([
      {
        ingredient_id: 5,
        quantity: 1.5,
        unit_id: null,
        preparation_note: null,
        is_optional: true,
      },
    ])
    expect(body.instruction_sections).toEqual([{ title: null, steps: ['Roast the chicken.'] }])
    expect(body.protein_ids).toEqual([5])
  })

  it('loads an existing recipe and saves the edited name', async () => {
    const mock = baseMocks([
      { path: '/recipes/1', method: 'GET', body: detail },
      { path: '/recipes/1', method: 'PATCH', body: { ...detail, name: 'Chicken Supper' } },
    ])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    const nameInput = await screen.findByLabelText('Name')
    expect(nameInput).toHaveValue('Chicken Dinner')
    expect(screen.getByLabelText('Step 1')).toHaveValue('Chop')

    fireEvent.change(nameInput, { target: { value: 'Chicken Supper' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    await waitFor(() => {
      expect(requestBodyFor(mock, '/recipes/1', 'PATCH')).toBeDefined()
    })
    const body = requestBodyFor(mock, '/recipes/1', 'PATCH') as Record<string, unknown>
    expect(body.name).toBe('Chicken Supper')
    expect(body.ingredients).toHaveLength(1)
  })

  it('reports validation problems before contacting the server', async () => {
    baseMocks([])

    renderWithProviders(<App />, { route: '/recipes/new' })
    await screen.findByRole('heading', { level: 2, name: 'New recipe' })

    fireEvent.click(screen.getByRole('button', { name: 'Create recipe' }))

    expect(await screen.findByText('Fix these problems before saving')).toBeInTheDocument()
    expect(screen.getAllByText('Enter a recipe name.').length).toBeGreaterThan(0)
  })
})
