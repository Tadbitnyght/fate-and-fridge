import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Department, Ingredient } from '../../api/referenceData'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

function ingredient(
  overrides: Partial<Ingredient> & Pick<Ingredient, 'id' | 'canonical_name'>,
): Ingredient {
  return {
    department_id: null,
    is_active: true,
    created_at: '2026-09-01T10:00:00Z',
    updated_at: '2026-09-01T10:00:00Z',
    aliases: [],
    ...overrides,
  }
}

const produce: Department = {
  id: 10,
  name: 'Produce',
  sort_order: 1,
  is_active: true,
  created_at: '2026-09-01T10:00:00Z',
  updated_at: '2026-09-01T10:00:00Z',
}

const pantry: Department = {
  id: 11,
  name: 'Pantry',
  sort_order: 2,
  is_active: true,
  created_at: '2026-09-01T10:00:00Z',
  updated_at: '2026-09-01T10:00:00Z',
}

const tomato = ingredient({
  id: 10,
  canonical_name: 'Tomato',
  department_id: 10,
  aliases: [{ id: 1, alias: 'tomatoes' }],
})
const cannedTomato = ingredient({
  id: 11,
  canonical_name: 'Tomato (canned)',
  department_id: 11,
})
const flour = ingredient({ id: 12, canonical_name: 'Flour' })
const oldSpice = ingredient({ id: 13, canonical_name: 'Old Spice', is_active: false })

/** Member with `reference_data.edit` but no Admin role: no hard deletes. */
const editorSession = {
  ...memberSession,
  permissions: [...memberSession.permissions, 'reference_data.edit'],
}

function installAdminMocks(routes: MockRoute[]): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    { path: '/departments', body: [produce, pantry] },
    ...routes,
  ])
}

/** Recorded requests whose URL contains `path`, mirroring the units tests. */
function requestsFor(mock: MockFetch, path: string): Array<{ method: string; body?: unknown }> {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).includes(path))
    .map(([, init]) => {
      const request = init as RequestInit | undefined
      const method = (request?.method ?? 'GET').toUpperCase()
      return request?.body === undefined
        ? { method }
        : { method, body: JSON.parse(String(request.body)) as unknown }
    })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('ingredients tab', () => {
  it('lists ingredients with resolved departments and aliases, and reveals retired rows', async () => {
    const mock = installAdminMocks([
      { path: '/ingredients', body: [tomato, cannedTomato, flour, oldSpice] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    const table = await screen.findByRole('table', { name: 'Ingredients' })

    const tomatoRow = within(table).getByRole('rowheader', { name: 'Tomato' }).closest('tr')
    expect(tomatoRow).not.toBeNull()
    const tomatoCells = within(tomatoRow as HTMLElement).getAllByRole('cell')
    expect(tomatoCells[0]).toHaveTextContent('Produce')
    expect(tomatoCells[1]).toHaveTextContent('tomatoes')
    expect(tomatoCells[2]).toHaveTextContent('Active')

    const flourRow = within(table).getByRole('rowheader', { name: 'Flour' }).closest('tr')
    expect(flourRow).not.toBeNull()
    const flourCells = within(flourRow as HTMLElement).getAllByRole('cell')
    expect(flourCells[0]).toHaveTextContent('—')
    expect(flourCells[1]).toHaveTextContent('—')

    expect(within(table).queryByRole('rowheader', { name: 'Old Spice' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Include retired'))

    const retiredRow = (await screen.findByRole('rowheader', { name: 'Old Spice' })).closest('tr')
    expect(retiredRow).not.toBeNull()
    expect(within(retiredRow as HTMLElement).getByText('Retired')).toBeInTheDocument()

    const listCalls = mock.fetchMock.mock.calls.filter(([url]) =>
      String(url).includes('/ingredients'),
    )
    expect(String(listCalls[listCalls.length - 1]?.[0])).toContain('include_retired=true')

    fireEvent.change(screen.getByLabelText('Search ingredients'), { target: { value: 'tom' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      const calls = mock.fetchMock.mock.calls.filter(([url]) =>
        String(url).includes('/ingredients'),
      )
      expect(String(calls[calls.length - 1]?.[0])).toContain('search=tom')
    })
  })

  it('creates an ingredient with a department and comma-separated aliases', async () => {
    const mock = installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients',
        method: 'POST',
        status: 201,
        body: ingredient({ id: 20, canonical_name: 'Basil', department_id: 10 }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add ingredient' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add ingredient' })
    fireEvent.change(within(dialog).getByLabelText('Canonical name'), {
      target: { value: 'Basil' },
    })
    fireEvent.change(within(dialog).getByLabelText('Department'), { target: { value: '10' } })
    fireEvent.change(within(dialog).getByLabelText('Aliases'), {
      target: { value: 'basil, sweet basil' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add ingredient' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/ingredients')).toContainEqual({
      method: 'POST',
      body: { canonical_name: 'Basil', department_id: 10, aliases: ['basil', 'sweet basil'] },
    })
  })

  it('keeps the create dialog open and explains INGREDIENT_EXISTS', async () => {
    installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'INGREDIENT_EXISTS',
            message: 'An ingredient with this name or alias already exists.',
            details: { existing_id: 10 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add ingredient' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add ingredient' })
    fireEvent.change(within(dialog).getByLabelText('Canonical name'), {
      target: { value: 'tomato' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add ingredient' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'An ingredient with this name or alias already exists (existing ingredient #10).',
    )
    expect(screen.getByRole('dialog', { name: 'Add ingredient' })).toBeInTheDocument()
  })

  it('surfaces ALIAS_EXISTS on create with the alias and existing id', async () => {
    installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'ALIAS_EXISTS',
            message: 'The alias is already used by another ingredient.',
            details: { alias: 'tomatoes', existing_id: 10 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add ingredient' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add ingredient' })
    fireEvent.change(within(dialog).getByLabelText('Canonical name'), {
      target: { value: 'Tomato, fresh' },
    })
    fireEvent.change(within(dialog).getByLabelText('Aliases'), {
      target: { value: 'tomatoes' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add ingredient' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'The alias "tomatoes" is already used by another ingredient (existing ingredient #10).',
    )
  })

  it('renames and clears the department through PATCH with an explicit null', async () => {
    const mock = installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients/10',
        method: 'PATCH',
        body: ingredient({ id: 10, canonical_name: 'Tomato, fresh' }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Edit Tomato' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Tomato' })
    expect(within(dialog).getByLabelText('Department')).toHaveValue('10')
    expect(within(dialog).queryByLabelText('Aliases')).not.toBeInTheDocument()

    fireEvent.change(within(dialog).getByLabelText('Canonical name'), {
      target: { value: 'Tomato, fresh' },
    })
    fireEvent.change(within(dialog).getByLabelText('Department'), { target: { value: '' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/ingredients/10')).toContainEqual({
      method: 'PATCH',
      body: { canonical_name: 'Tomato, fresh', department_id: null },
    })
  })

  it('adds and removes aliases with the expected payloads', async () => {
    const mock = installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients/10/aliases',
        method: 'POST',
        status: 201,
        body: { id: 5, alias: 'love apple' },
      },
      { path: '/ingredients/10/aliases/1', method: 'DELETE', status: 204 },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    const input = await screen.findByLabelText('New alias for Tomato')
    fireEvent.change(input, { target: { value: '  love apple  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add alias to Tomato' }))

    await waitFor(() =>
      expect(requestsFor(mock, '/ingredients/10/aliases')).toContainEqual({
        method: 'POST',
        body: { alias: 'love apple' },
      }),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Remove alias tomatoes from Tomato' }))

    await waitFor(() =>
      expect(requestsFor(mock, '/ingredients/10/aliases/1')).toContainEqual({ method: 'DELETE' }),
    )
  })

  it('keeps the alias form open and shows the ALIAS_EXISTS message', async () => {
    installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients/10/aliases',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'ALIAS_EXISTS',
            message: 'The alias is already used by another ingredient.',
            details: { existing_id: 11 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    const input = await screen.findByLabelText('New alias for Tomato')
    fireEvent.change(input, { target: { value: 'tomatoes' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add alias to Tomato' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The alias is already used by another ingredient.',
    )
    expect(input).toHaveValue('tomatoes')
  })

  it('keeps the delete dialog open with the INGREDIENT_IN_USE usage count', async () => {
    installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients/10',
        method: 'DELETE',
        status: 409,
        body: {
          error: {
            code: 'INGREDIENT_IN_USE',
            message: 'The ingredient is still referenced by 2 recipe ingredient(s).',
            details: { usage_count: 2 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Tomato' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Delete ingredient' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      '2 recipe ingredients reference this ingredient. Retire it instead, or update those recipes first.',
    )
    expect(screen.getByRole('dialog', { name: 'Delete Tomato?' })).toBeInTheDocument()
  })

  it('explains the merge-reference reason when deletion is blocked', async () => {
    installAdminMocks([
      { path: '/ingredients', body: [tomato] },
      {
        path: '/ingredients/10',
        method: 'DELETE',
        status: 409,
        body: {
          error: {
            code: 'INGREDIENT_IN_USE',
            message: 'The ingredient is referenced by an ingredient merge record.',
            details: { usage_count: 0, reason: 'merge_reference' },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Tomato' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Delete ingredient' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'kept as the source or target of an ingredient merge',
    )
  })

  it('merges a source into a target and reports the result summary', async () => {
    const mock = installAdminMocks([
      { path: '/ingredients', body: [tomato, cannedTomato, flour] },
      {
        path: '/ingredients/merge',
        method: 'POST',
        body: {
          source_id: 11,
          target_id: 10,
          skipped_aliases: ['canned'],
          reassigned_reference_count: 3,
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Merge ingredients' }))
    const dialog = await screen.findByRole('dialog', { name: 'Merge ingredients' })

    fireEvent.change(within(dialog).getByLabelText('Filter source ingredients'), {
      target: { value: 'can' },
    })
    const sourceSelect = within(dialog).getByLabelText('Source')
    expect(within(sourceSelect).queryByRole('option', { name: 'Tomato' })).not.toBeInTheDocument()
    fireEvent.change(sourceSelect, { target: { value: '11' } })
    fireEvent.change(within(dialog).getByLabelText('Target'), { target: { value: '10' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Merge' }))

    expect(
      await within(dialog).findByText('Merged Tomato (canned) into Tomato.'),
    ).toBeInTheDocument()
    expect(within(dialog).getByText('3 references reassigned.')).toBeInTheDocument()
    expect(
      within(dialog).getByText('Skipped aliases already present on the target: canned.'),
    ).toBeInTheDocument()

    expect(requestsFor(mock, '/ingredients/merge')).toContainEqual({
      method: 'POST',
      body: { source_id: 11, target_id: 10 },
    })
    await waitFor(() => expect(mock.callsFor('/ingredients')).toBeGreaterThanOrEqual(2))
  })

  it('shows VALIDATION_FAILED field errors from merge and keeps the dialog open', async () => {
    installAdminMocks([
      { path: '/ingredients', body: [tomato, cannedTomato] },
      {
        path: '/ingredients/merge',
        method: 'POST',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request body failed validation.',
            details: {
              field_errors: { source_id: ['The source ingredient is already retired.'] },
            },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Merge ingredients' }))
    const dialog = await screen.findByRole('dialog', { name: 'Merge ingredients' })
    fireEvent.change(within(dialog).getByLabelText('Source'), { target: { value: '10' } })
    fireEvent.change(within(dialog).getByLabelText('Target'), { target: { value: '11' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Merge' }))

    expect(
      await within(dialog).findByText('The source ingredient is already retired.'),
    ).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Source')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByRole('dialog', { name: 'Merge ingredients' })).toBeInTheDocument()
  })

  it('hides merge and every write control from a non-editor member', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/departments', body: [produce] },
      { path: '/ingredients', body: [tomato] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    expect(await screen.findByRole('table', { name: 'Ingredients' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Merge ingredients' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add ingredient' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit Tomato' })).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument()
  })

  it('shows merge and edit to a non-Admin editor but no delete', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: editorSession },
      { path: '/departments', body: [produce, pantry] },
      { path: '/ingredients', body: [tomato] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    expect(await screen.findByRole('button', { name: 'Merge ingredients' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit Tomato' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retire Tomato' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Tomato' })).not.toBeInTheDocument()
  })

  it('retires an active ingredient and offers no re-activation for retired rows', async () => {
    const mock = installAdminMocks([
      { path: '/ingredients', body: [tomato, oldSpice] },
      {
        path: '/ingredients/10/retire',
        method: 'POST',
        body: ingredient({ id: 10, canonical_name: 'Tomato', is_active: false }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/ingredients' })

    fireEvent.click(await screen.findByRole('button', { name: 'Retire Tomato' }))
    await waitFor(() =>
      expect(requestsFor(mock, '/ingredients/10/retire')).toContainEqual({ method: 'POST' }),
    )

    fireEvent.click(screen.getByLabelText('Include retired'))
    expect(await screen.findByRole('button', { name: 'Delete Old Spice' })).toBeInTheDocument()

    // The M2 ingredients API has no un-retire endpoint (docs/API.md §22), so
    // retired rows must not offer an Enable action that would fail server-side.
    expect(screen.queryByRole('button', { name: 'Retire Old Spice' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Enable Old Spice' })).not.toBeInTheDocument()
  })
})
