import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Recipe } from '../../api/recipes'
import type { Ingredient, Unit } from '../../api/referenceData'
import { adminSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { createTestQueryClient, renderWithProviders } from '../../test/utils'

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

const historyDetail: Recipe = {
  ...detail,
  status: 'retired',
  statistics: { last_eaten: '2026-09-30', next_planned: '2026-10-20', times_eaten: 4 },
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
    { path: '/ingredients?', body: [chicken] },
    { path: '/units?', body: [grams] },
    { path: '/proteins?', body: [{ id: 5, name: 'Chicken', is_active: true }] },
    { path: '/tags?', body: [] },
    { path: '/recipes?', method: 'GET', body: { items: [], total: 0, limit: 24, offset: 0 } },
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

    expect(await screen.findByRole('heading', { level: 2, name: 'Add recipe' })).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Chicken Dinner' } })

    fireEvent.click(screen.getByRole('button', { name: 'Add ingredient' }))
    fireEvent.change(screen.getByLabelText('Ingredient'), { target: { value: '5' } })
    fireEvent.change(screen.getByLabelText('Quantity'), { target: { value: '1.5' } })
    fireEvent.click(screen.getByLabelText('Optional ingredient'))

    fireEvent.click(screen.getByRole('button', { name: 'Add section' }))
    fireEvent.change(screen.getByLabelText('Step 1'), { target: { value: 'Roast the chicken.' } })

    fireEvent.click(screen.getByLabelText('Chicken'))

    fireEvent.click(screen.getByRole('button', { name: 'Add recipe' }))

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

    // BUG-005: a successful create returns to the recipes list with a notice.
    expect(await screen.findByRole('heading', { level: 2, name: 'Recipes' })).toBeVisible()
    expect(await screen.findByText('Recipe added.')).toBeVisible()
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

    // BUG-005: a successful update returns to the recipes list with a notice.
    expect(await screen.findByRole('heading', { level: 2, name: 'Recipes' })).toBeVisible()
    expect(await screen.findByText('Recipe saved.')).toBeVisible()
  })

  it('shows the read-only history metrics on the detail/editor, including for a retired recipe', async () => {
    baseMocks([{ path: '/recipes/1', method: 'GET', body: historyDetail }])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    expect(await screen.findByLabelText('Name')).toHaveValue('Chicken Dinner')
    expect(screen.getByLabelText('Randomization status')).toHaveValue('retired')

    const history = screen.getByText('History').closest('fieldset')
    expect(history).not.toBeNull()
    const scope = within(history as HTMLElement)
    expect(scope.getByText('Last eaten')).toBeInTheDocument()
    expect(scope.getByText('09-30-26')).toBeInTheDocument()
    expect(scope.getByText('Next planned')).toBeInTheDocument()
    expect(scope.getByText('10-20-26')).toBeInTheDocument()
    expect(scope.getByText('Times eaten')).toBeInTheDocument()
    expect(scope.getByText('4')).toBeInTheDocument()
  })

  it('falls back to the no-history label when the recipe has no metrics', async () => {
    baseMocks([{ path: '/recipes/1', method: 'GET', body: detail }])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    expect(await screen.findByLabelText('Name')).toHaveValue('Chicken Dinner')

    const history = screen.getByText('History').closest('fieldset')
    expect(history).not.toBeNull()
    expect(within(history as HTMLElement).getAllByText('No history yet')).toHaveLength(3)
  })

  it('reports validation problems before contacting the server', async () => {
    baseMocks([])

    renderWithProviders(<App />, { route: '/recipes/new' })
    await screen.findByRole('heading', { level: 2, name: 'Add recipe' })

    fireEvent.click(screen.getByRole('button', { name: 'Add recipe' }))

    expect(await screen.findByText('Fix these problems before saving')).toBeInTheDocument()
    expect(screen.getAllByText('Enter a recipe name.').length).toBeGreaterThan(0)
  })
})

/* Hard delete (Admin-only danger zone) ---------- */

/** A member who may edit recipes but is not an Admin. */
const memberEditorSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 2, username: 'sam', role: 'member' },
  permissions: ['recipes.view', 'recipes.edit'],
  must_change_password: false,
}

const deleteResult = {
  recipe_id: 1,
  removed_planned_entries: 2,
  tombstoned_entries: 1,
  removed_images: 1,
}

const DELETE_TRIGGER = 'Delete this recipe permanently'
const DELETE_CONFIRM = 'Delete permanently'

function deleteBody(mock: MockFetch): Record<string, unknown> | undefined {
  const call = mock.fetchMock.mock.calls.find(
    ([url, init]) =>
      String(url).includes('/recipes/1') &&
      ((init as RequestInit | undefined)?.method ?? 'GET').toUpperCase() === 'DELETE',
  )
  return call
    ? (JSON.parse(String((call[1] as RequestInit).body)) as Record<string, unknown>)
    : undefined
}

async function openDeleteDialog(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole('button', { name: DELETE_TRIGGER }))
  return screen.findByRole('dialog', { name: 'Delete recipe permanently' })
}

function fillDeleteDialog(
  dialog: HTMLElement,
  {
    reason = 'Duplicate recipe created by mistake',
    confirmation = 'Chicken Dinner',
    acknowledge = true,
  }: { reason?: string; confirmation?: string; acknowledge?: boolean } = {},
): void {
  fireEvent.change(within(dialog).getByLabelText('Reason'), { target: { value: reason } })
  if (acknowledge) {
    fireEvent.click(within(dialog).getByRole('checkbox'))
  }
  fireEvent.change(within(dialog).getByLabelText('Type the recipe name to confirm'), {
    target: { value: confirmation },
  })
}

describe('recipe hard delete', () => {
  it('is absent for a member who can edit recipes', async () => {
    baseMocks([{ path: '/recipes/1', method: 'GET', body: detail }], memberEditorSession)

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    expect(await screen.findByLabelText('Name')).toHaveValue('Chicken Dinner')
    expect(screen.queryByRole('button', { name: DELETE_TRIGGER })).not.toBeInTheDocument()
    expect(screen.queryByText('Danger zone')).not.toBeInTheDocument()
  })

  it('is absent on the create route for an Admin', async () => {
    baseMocks([])

    renderWithProviders(<App />, { route: '/recipes/new' })
    await screen.findByRole('heading', { level: 2, name: 'Add recipe' })

    expect(screen.queryByRole('button', { name: DELETE_TRIGGER })).not.toBeInTheDocument()
    expect(screen.queryByText('Danger zone')).not.toBeInTheDocument()
  })

  it('appears for an Admin on a saved recipe and opens the shared dialog', async () => {
    baseMocks([{ path: '/recipes/1', method: 'GET', body: detail }])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    const trigger = await screen.findByRole('button', { name: DELETE_TRIGGER })
    expect(trigger).toBeInTheDocument()

    fireEvent.click(trigger)
    const dialog = await screen.findByRole('dialog', { name: 'Delete recipe permanently' })
    expect(dialog).toBeVisible()
    // The irreversibility and tombstone warnings are stated.
    expect(within(dialog).getByText(/irreversible\. There is no undo/i)).toBeVisible()
    expect(within(dialog).getByText(/photo is deleted/i)).toBeVisible()
    expect(within(dialog).getByText(/today and in the future are removed/i)).toBeVisible()
    expect(within(dialog).getByText(/kept as read-only history/i)).toBeVisible()
  })

  it('keeps Delete disabled until the reason, acknowledgement, and exact name are given', async () => {
    baseMocks([{ path: '/recipes/1', method: 'GET', body: detail }])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    const dialog = await openDeleteDialog()
    const confirm = within(dialog).getByRole('button', { name: DELETE_CONFIRM })
    expect(confirm).toBeDisabled()

    // Reason and acknowledgement, but a case near-miss on the name.
    fillDeleteDialog(dialog, { confirmation: 'chicken dinner' })
    expect(confirm).toBeDisabled()
    expect(within(dialog).getByText('Type "Chicken Dinner" exactly to confirm.')).toBeVisible()

    fireEvent.change(within(dialog).getByLabelText('Type the recipe name to confirm'), {
      target: { value: 'Chicken Dinner' },
    })
    expect(confirm).toBeEnabled()
  })

  it('posts the reason and the exact name to DELETE /recipes/{id}', async () => {
    const mock = baseMocks([
      { path: '/recipes/1', method: 'GET', body: detail },
      { path: '/recipes/1', method: 'DELETE', body: deleteResult },
    ])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    const dialog = await openDeleteDialog()
    fillDeleteDialog(dialog, { reason: 'Duplicate recipe created by mistake' })
    fireEvent.click(within(dialog).getByRole('button', { name: DELETE_CONFIRM }))

    await waitFor(() => expect(deleteBody(mock)).toBeDefined())
    expect(deleteBody(mock)).toEqual({
      reason: 'Duplicate recipe created by mistake',
      confirmation: 'Chicken Dinner',
    })
  })

  it('reports the counts and returns to /recipes on success', async () => {
    baseMocks([
      { path: '/recipes/1', method: 'GET', body: detail },
      { path: '/recipes/1', method: 'DELETE', body: deleteResult },
    ])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    const dialog = await openDeleteDialog()
    fillDeleteDialog(dialog)
    fireEvent.click(within(dialog).getByRole('button', { name: DELETE_CONFIRM }))

    expect(await screen.findByRole('heading', { level: 2, name: 'Recipes' })).toBeVisible()
    expect(
      await screen.findByText(/Removed 2 planned meals; kept 1 past meal as read-only history/),
    ).toBeVisible()
  })

  it.each([
    {
      name: 'CONFIRMATION_MISMATCH',
      status: 400,
      error: { code: 'CONFIRMATION_MISMATCH', message: 'mismatch', details: {} },
      expected: /typed confirmation did not match/i,
    },
    {
      name: 'REASON_REQUIRED',
      status: 400,
      error: {
        code: 'REASON_REQUIRED',
        message: 'reason required',
        details: { field_errors: { reason: ['Enter a reason.'] } },
      },
      expected: 'Enter a reason.',
    },
    {
      name: 'NOT_FOUND',
      status: 404,
      error: { code: 'NOT_FOUND', message: 'missing', details: {} },
      expected: /no longer exists/i,
    },
  ])('surfaces $name readably and keeps the dialog open', async ({ status, error, expected }) => {
    baseMocks([
      { path: '/recipes/1', method: 'GET', body: detail },
      { path: '/recipes/1', method: 'DELETE', status, body: { error } },
    ])

    renderWithProviders(<App />, { route: '/recipes/1/edit' })

    const dialog = await openDeleteDialog()
    fillDeleteDialog(dialog)
    fireEvent.click(within(dialog).getByRole('button', { name: DELETE_CONFIRM }))

    expect(await screen.findByText(expected)).toBeVisible()
    expect(screen.getByRole('dialog', { name: 'Delete recipe permanently' })).toBeVisible()
  })

  it('invalidates the recipes, calendar, statistics, and audit query families', async () => {
    baseMocks([
      { path: '/recipes/1', method: 'GET', body: detail },
      { path: '/recipes/1', method: 'DELETE', body: deleteResult },
    ])
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    renderWithProviders(<App />, { route: '/recipes/1/edit', queryClient })

    const dialog = await openDeleteDialog()
    fillDeleteDialog(dialog)
    fireEvent.click(within(dialog).getByRole('button', { name: DELETE_CONFIRM }))

    await waitFor(() => {
      const keys = invalidate.mock.calls.map(
        ([filters]) => (filters as { queryKey: unknown[] }).queryKey,
      )
      expect(keys).toEqual(
        expect.arrayContaining([['recipes'], ['calendar'], ['statistics'], ['audit']]),
      )
    })
  })
})
