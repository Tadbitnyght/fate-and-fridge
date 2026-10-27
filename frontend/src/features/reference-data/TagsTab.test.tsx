import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { Tag } from '../../api/referenceData'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

function tag(overrides: Partial<Tag> & Pick<Tag, 'id' | 'name'>): Tag {
  return { ...overrides }
}

const weeknight = tag({ id: 10, name: 'Weeknight' })
const holiday = tag({ id: 11, name: 'Holiday' })

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

describe('tags tab', () => {
  it('lists tags, has no status/retire affordances, and sends the search param', async () => {
    const mock = installAdminMocks([{ path: '/tags', body: [weeknight, holiday] }])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    const table = await screen.findByRole('table', { name: 'Tags' })
    expect(within(table).getByRole('rowheader', { name: 'Weeknight' })).toBeInTheDocument()
    expect(within(table).getByRole('rowheader', { name: 'Holiday' })).toBeInTheDocument()

    // The M2 tags contract has no retire flag: no status column, no include-retired
    // filter, and no retire/enable actions.
    expect(within(table).queryByRole('columnheader', { name: 'Status' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Include retired')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Retire Weeknight' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Enable Weeknight' })).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Search tags'), { target: { value: 'week' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    await waitFor(() => {
      const calls = mock.fetchMock.mock.calls.filter(([url]) => String(url).includes('/tags'))
      expect(String(calls[calls.length - 1]?.[0])).toContain('search=week')
    })
  })

  it('creates a tag with the expected payload', async () => {
    const mock = installAdminMocks([
      { path: '/tags', body: [weeknight] },
      { path: '/tags', method: 'POST', status: 201, body: tag({ id: 20, name: 'Holiday' }) },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add tag' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add tag' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Holiday' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add tag' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/tags')).toContainEqual({ method: 'POST', body: { name: 'Holiday' } })
  })

  it('keeps the create dialog open and explains TAG_EXISTS', async () => {
    installAdminMocks([
      { path: '/tags', body: [weeknight] },
      {
        path: '/tags',
        method: 'POST',
        status: 409,
        body: {
          error: {
            code: 'TAG_EXISTS',
            message: 'A tag with this name already exists.',
            details: { existing_id: 10 },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add tag' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add tag' })
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'weeknight' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add tag' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'A tag with this name already exists (existing tag #10).',
    )
    expect(screen.getByRole('dialog', { name: 'Add tag' })).toBeInTheDocument()
  })

  it('renames a tag through PATCH with the new name', async () => {
    const mock = installAdminMocks([
      { path: '/tags/10', method: 'PATCH', body: tag({ id: 10, name: 'Weeknight dinner' }) },
      { path: '/tags', body: [weeknight] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    fireEvent.click(await screen.findByRole('button', { name: 'Rename Weeknight' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename Weeknight' })
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Weeknight')

    fireEvent.change(within(dialog).getByLabelText('Name'), {
      target: { value: 'Weeknight dinner' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/tags/10')).toContainEqual({
      method: 'PATCH',
      body: { name: 'Weeknight dinner' },
    })
  })

  it('keeps the delete dialog open with the TAG_IN_USE usage count', async () => {
    installAdminMocks([
      {
        path: '/tags/10',
        method: 'DELETE',
        status: 409,
        body: {
          error: {
            code: 'TAG_IN_USE',
            message: 'The tag is still referenced by 2 recipe(s).',
            details: { usage_count: 2 },
          },
        },
      },
      { path: '/tags', body: [weeknight] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Weeknight' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Delete tag' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      '2 recipes use this tag. Remove the tag from those recipes first, then delete it.',
    )
    expect(screen.getByRole('dialog', { name: 'Delete Weeknight?' })).toBeInTheDocument()
  })

  it('shows rename to a non-Admin editor but no delete', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: editorSession },
      { path: '/tags', body: [weeknight] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    expect(await screen.findByRole('button', { name: 'Rename Weeknight' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Weeknight' })).not.toBeInTheDocument()
  })

  it('hides every write control from a non-editor member', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/tags', body: [weeknight] },
    ])

    renderWithProviders(<App />, { route: '/settings/reference-data/tags' })

    expect(await screen.findByRole('table', { name: 'Tags' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add tag' })).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Actions' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Rename Weeknight' })).not.toBeInTheDocument()
  })
})
