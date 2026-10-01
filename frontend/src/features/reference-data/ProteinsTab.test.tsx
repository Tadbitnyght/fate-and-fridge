import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Protein } from '../../api/referenceData'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

function protein(overrides: Partial<Protein> & Pick<Protein, 'id' | 'name'>): Protein {
  return {
    is_active: true,
    ...overrides,
  }
}

const chicken = protein({ id: 10, name: 'Chicken' })
const tofu = protein({ id: 11, name: 'Tofu' })
const duck = protein({ id: 12, name: 'Duck', is_active: false })

/** Member with `reference_data.edit` but no Admin role: no hard deletes. */
const editorSession = {
  ...memberSession,
  permissions: [...memberSession.permissions, 'reference_data.edit'],
}

function installAdminMocks(routes: MockRoute[]): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    ...routes,
  ])
}

/** Recorded requests whose URL contains `path`, mirroring the sibling tests. */
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

describe('proteins tab', () => {
  it('lists proteins with status and reveals retired rows through the request flag', async () => {
    const mock = installAdminMocks([{ path: '/proteins', body: [chicken, tofu, duck] }])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    const table = await screen.findByRole('table', { name: 'Proteins' })

    const chickenRow = within(table).getByRole('rowheader', { name: 'Chicken' }).closest('tr')
    expect(chickenRow).not.toBeNull()
    expect(within(chickenRow as HTMLElement).getByText('Active')).toBeInTheDocument()

    expect(within(table).queryByRole('rowheader', { name: 'Duck' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Include retired'))

    const duckRow = (await screen.findByRole('rowheader', { name: 'Duck' })).closest('tr')
    expect(duckRow).not.toBeNull()
    expect(within(duckRow as HTMLElement).getByText('Retired')).toBeInTheDocument()

    const listCalls = mock.fetchMock.mock.calls.filter(([url]) => String(url).includes('/proteins'))
    expect(String(listCalls[listCalls.length - 1]?.[0])).toContain('include_retired=true')

    fireEvent.change(screen.getByLabelText('Search proteins'), { target: { value: 'tof' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      const calls = mock.fetchMock.mock.calls.filter(([url]) => String(url).includes('/proteins'))
      expect(String(calls[calls.length - 1]?.[0])).toContain('search=tof')
    })
  })

  it('creates a protein with the expected payload', async () => {
    const mock = installAdminMocks([
      { path: '/proteins', body: [chicken] },
      { path: '/proteins', method: 'POST', status: 201, body: protein({ id: 20, name: 'Beef' }) },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add protein' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add protein' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Beef' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create protein' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/proteins')).toContainEqual({
      method: 'POST',
      body: { name: 'Beef' },
    })
  })

  it('keeps the create dialog open and explains PROTEIN_EXISTS', async () => {
    installAdminMocks([
      { path: '/proteins', body: [chicken] },
      {
        path: '/proteins',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'PROTEIN_EXISTS',
            message: 'A protein with this name already exists.',
            details: { existing_id: 10 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add protein' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add protein' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'chicken' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create protein' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'A protein with this name already exists (existing protein #10).',
    )
    expect(screen.getByRole('dialog', { name: 'Add protein' })).toBeInTheDocument()
  })

  it('renames a protein through PATCH with the new name', async () => {
    const mock = installAdminMocks([
      { path: '/proteins/10', method: 'PATCH', body: protein({ id: 10, name: 'Chicken breast' }) },
      { path: '/proteins', body: [chicken] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    fireEvent.click(await screen.findByRole('button', { name: 'Rename Chicken' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename Chicken' })
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Chicken')

    fireEvent.change(within(dialog).getByLabelText('Name'), {
      target: { value: 'Chicken breast' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/proteins/10')).toContainEqual({
      method: 'PATCH',
      body: { name: 'Chicken breast' },
    })
  })

  it('retires an active protein through the retire endpoint', async () => {
    const mock = installAdminMocks([
      {
        path: '/proteins/10/retire',
        method: 'POST',
        body: protein({ id: 10, name: 'Chicken', is_active: false }),
      },
      { path: '/proteins', body: [chicken, duck] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    fireEvent.click(await screen.findByRole('button', { name: 'Retire Chicken' }))
    await waitFor(() =>
      expect(requestsFor(mock, '/proteins/10/retire')).toContainEqual({ method: 'POST' }),
    )
  })

  it('enables a retired protein through PATCH is_active and never offers re-retire', async () => {
    const mock = installAdminMocks([
      { path: '/proteins/12', method: 'PATCH', body: protein({ id: 12, name: 'Duck' }) },
      { path: '/proteins', body: [chicken, duck] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    expect(await screen.findByRole('table', { name: 'Proteins' })).toBeInTheDocument()
    fireEvent.click(screen.getByLabelText('Include retired'))
    const duckRow = (await screen.findByRole('rowheader', { name: 'Duck' })).closest('tr')
    expect(duckRow).not.toBeNull()
    expect(
      within(duckRow as HTMLElement).queryByRole('button', { name: 'Retire Duck' }),
    ).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Enable Duck' }))
    await waitFor(() =>
      expect(requestsFor(mock, '/proteins/12')).toContainEqual({
        method: 'PATCH',
        body: { is_active: true },
      }),
    )

    // Active rows offer Retire, never Enable.
    expect(screen.queryByRole('button', { name: 'Enable Chicken' })).not.toBeInTheDocument()
  })

  it('keeps the delete dialog open with the PROTEIN_IN_USE usage count', async () => {
    installAdminMocks([
      {
        path: '/proteins/10',
        method: 'DELETE',
        status: 409,
        body: {
          error: {
            code: 'PROTEIN_IN_USE',
            message: 'The protein is still referenced by 2 recipe(s).',
            details: { usage_count: 2 },
          },
        },
      },
      { path: '/proteins', body: [chicken] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Chicken' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Delete protein' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      '2 recipes use this protein. Retire it instead, or update those recipes first.',
    )
    expect(screen.getByRole('dialog', { name: 'Delete Chicken?' })).toBeInTheDocument()
  })

  it('shows rename and retire to a non-Admin editor but no delete', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: editorSession },
      { path: '/proteins', body: [chicken] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    expect(await screen.findByRole('button', { name: 'Rename Chicken' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Retire Chicken' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Chicken' })).not.toBeInTheDocument()
  })

  it('hides every write control from a non-editor member', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/proteins', body: [chicken] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/proteins' })

    expect(await screen.findByRole('table', { name: 'Proteins' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add protein' })).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Rename Chicken' })).not.toBeInTheDocument()
  })
})
