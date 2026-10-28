import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Department } from '../../api/referenceData'
import { adminSession, setupCompleteStatus } from '../../test/fixtures'
import {
  createResponse,
  installMockFetch,
  type MockFetch,
  type MockRoute,
} from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

function department(overrides: Partial<Department> & Pick<Department, 'id' | 'name'>): Department {
  return {
    sort_order: overrides.id,
    is_active: true,
    created_at: '2026-09-01T10:00:00Z',
    updated_at: '2026-09-01T10:00:00Z',
    ...overrides,
  }
}

const produce = department({ id: 10, name: 'Produce', sort_order: 1 })
const spices = department({ id: 11, name: 'Spices', sort_order: 2, is_active: false })

function installAdminMocks(routes: MockRoute[]): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    ...routes,
  ])
}

/** Recorded requests whose URL contains `path`, mirroring the settings tests. */
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

function reorderBodies(mock: MockFetch): Array<{ ids: number[] }> {
  return requestsFor(mock, '/departments/order')
    .filter((request) => request.method === 'PUT')
    .map((request) => request.body as { ids: number[] })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('departments tab', () => {
  it('lists active departments and reveals retired rows on request', async () => {
    const mock = installAdminMocks([{ path: '/departments', body: [produce, spices] }])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    const table = await screen.findByRole('table', { name: 'Grocery departments' })
    expect(within(table).getByRole('rowheader', { name: 'Produce' })).toBeInTheDocument()
    expect(within(table).getByText('Active')).toBeInTheDocument()
    expect(within(table).queryByText('Spices')).not.toBeInTheDocument()
    expect(within(table).queryByText('Retired')).not.toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Include retired'))

    // Query the document again instead of reusing the captured table node
    // the filter toggle is a state change, and re-querying keeps the assertions
    // honest even if React swaps the table subtree.
    expect(screen.getByRole('rowheader', { name: 'Spices' })).toBeInTheDocument()
    expect(screen.getByText('Retired')).toBeInTheDocument()

    // The query always asks for retired rows so reorder permutations stay complete.
    const listCall = mock.fetchMock.mock.calls.find(([url]) => String(url).includes('/departments'))
    expect(String(listCall?.[0])).toContain('include_retired=true')
    expect(String(listCall?.[0])).toContain('limit=100')
  })

  it('blocks reordering while a search filter is active and restores it on clear', async () => {
    installAdminMocks([
      {
        path: '/departments',
        body: [produce, department({ id: 12, name: 'Pantry', sort_order: 3 })],
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    fireEvent.change(await screen.findByLabelText('Search departments'), {
      target: { value: 'p' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    expect(await screen.findByText('Clear the search to reorder departments.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Move up Pantry' })).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: 'Clear' }))

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Move up Pantry' })).toBeEnabled(),
    )
  })

  it('creates a department from the dialog with a trimmed name', async () => {
    const mock = installAdminMocks([
      { path: '/departments', body: [produce] },
      {
        path: '/departments',
        method: 'POST',
        status: 201,
        body: department({ id: 12, name: 'Bakery', sort_order: 3 }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add department' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add department' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: '  Bakery  ' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add department' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/departments')).toContainEqual({
      method: 'POST',
      body: { name: 'Bakery' },
    })
  })

  it('renames a department and sends only the new name', async () => {
    const mock = installAdminMocks([
      { path: '/departments', body: [produce] },
      {
        path: '/departments/10',
        method: 'PATCH',
        body: department({ id: 10, name: 'Fresh Produce', sort_order: 1 }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    fireEvent.click(await screen.findByRole('button', { name: 'Rename Produce' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename Produce' })
    const nameInput = within(dialog).getByLabelText('Name')
    expect(nameInput).toHaveValue('Produce')

    fireEvent.change(nameInput, { target: { value: 'Fresh Produce' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/departments/10')).toContainEqual({
      method: 'PATCH',
      body: { name: 'Fresh Produce' },
    })
  })

  it('validates locally and surfaces server VALIDATION_FAILED field errors', async () => {
    const mock = installAdminMocks([
      { path: '/departments', body: [produce] },
      {
        path: '/departments',
        method: 'POST',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request body failed validation.',
            details: { field_errors: { name: ['A department named "Bakery" already exists.'] } },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add department' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add department' })
    const nameInput = within(dialog).getByLabelText('Name')

    fireEvent.click(within(dialog).getByRole('button', { name: 'Add department' }))
    expect(await within(dialog).findByText('Enter a department name.')).toBeInTheDocument()
    expect(requestsFor(mock, '/departments')).not.toContainEqual({ method: 'POST' })

    fireEvent.change(nameInput, { target: { value: 'Bakery' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add department' }))

    expect(
      await within(dialog).findByText('A department named "Bakery" already exists.'),
    ).toBeInTheDocument()
    expect(nameInput).toHaveAttribute('aria-invalid', 'true')
    expect(requestsFor(mock, '/departments')).toContainEqual({
      method: 'POST',
      body: { name: 'Bakery' },
    })
    expect(screen.getByRole('dialog', { name: 'Add department' })).toBeInTheDocument()
  })

  it.each([
    {
      count: 1,
      message:
        '1 ingredient uses this department. Retire it instead, or move that ingredient first.',
    },
    {
      count: 3,
      message:
        '3 ingredients use this department. Retire it instead, or move those ingredients first.',
    },
  ])(
    'keeps the delete dialog open when $count ingredient(s) use the department',
    async ({ count, message }) => {
      installAdminMocks([
        { path: '/departments', body: [produce] },
        {
          path: '/departments/10',
          method: 'DELETE',
          status: 409,
          body: {
            error: {
              code: 'DEPARTMENT_IN_USE',
              message: 'This department is still referenced by ingredients.',
              details: { ingredient_count: count },
            },
          },
        },
      ])

      renderWithProviders(<App />, { route: '/settings/reference-data' })

      fireEvent.click(await screen.findByRole('button', { name: 'Delete Produce' }))
      fireEvent.click(await screen.findByRole('button', { name: 'Delete department' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(message)
      expect(screen.getByRole('dialog', { name: 'Delete Produce?' })).toBeInTheDocument()
    },
  )

  it('retires an active department and enables a retired one', async () => {
    const mock = installAdminMocks([
      { path: '/departments', body: [produce, spices] },
      {
        path: '/departments/10/retire',
        method: 'POST',
        body: department({ id: 10, name: 'Produce', sort_order: 1, is_active: false }),
      },
      {
        path: '/departments/11',
        method: 'PATCH',
        body: department({ id: 11, name: 'Spices', sort_order: 2 }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    fireEvent.click(await screen.findByRole('button', { name: 'Retire Produce' }))
    await waitFor(() =>
      expect(requestsFor(mock, '/departments/10/retire')).toContainEqual({ method: 'POST' }),
    )
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Retire Produce' })).toBeEnabled(),
    )

    fireEvent.click(screen.getByLabelText('Include retired'))
    fireEvent.click(screen.getByRole('button', { name: 'Enable Spices' }))

    await waitFor(() =>
      expect(requestsFor(mock, '/departments/11')).toContainEqual({
        method: 'PATCH',
        body: { is_active: true },
      }),
    )
  })

  it('reorders the full permutation and disables moves while pending', async () => {
    const original = [
      department({ id: 10, name: 'Produce', sort_order: 1 }),
      department({ id: 11, name: 'Spices', sort_order: 2, is_active: false }),
      department({ id: 12, name: 'Pantry', sort_order: 3 }),
      department({ id: 13, name: 'Freezer', sort_order: 4 }),
    ]
    const afterFirstMove = [
      department({ id: 12, name: 'Pantry', sort_order: 1 }),
      department({ id: 11, name: 'Spices', sort_order: 2, is_active: false }),
      department({ id: 10, name: 'Produce', sort_order: 3 }),
      department({ id: 13, name: 'Freezer', sort_order: 4 }),
    ]
    const mock = installAdminMocks([
      {
        path: '/departments',
        respond: (_init, callCount) => ({
          status: 200,
          body: callCount === 1 ? original : afterFirstMove,
        }),
      },
      { path: '/departments/order', method: 'PUT', body: afterFirstMove },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data' })

    const movePantryUp = await screen.findByRole('button', { name: 'Move up Pantry' })

    // Defer the first PUT so the pending state is observable; the list has
    // loaded, so the next request is the reorder.
    const orderGate: { resolve: ((response: Response) => void) | null } = { resolve: null }
    mock.fetchMock.mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          orderGate.resolve = resolve
        }),
    )

    fireEvent.click(movePantryUp)
    await waitFor(() => expect(reorderBodies(mock)).toEqual([{ ids: [12, 11, 10, 13] }]))
    expect(screen.getByRole('button', { name: 'Move up Pantry' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move up Freezer' })).toBeDisabled()

    await act(async () => {
      orderGate.resolve?.(createResponse(200, afterFirstMove))
    })
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Move up Produce' })).toBeEnabled(),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Move down Produce' }))

    await waitFor(() =>
      expect(reorderBodies(mock)).toEqual([{ ids: [12, 11, 10, 13] }, { ids: [12, 11, 13, 10] }]),
    )
  })
})
