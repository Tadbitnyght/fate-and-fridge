import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { AuditLogItem } from '../../api/audit'
import { saveDownloadedFile } from '../../lib/download'
import {
  adminSession,
  adminUserRecord,
  memberSession,
  memberUserRecord,
  securitySettings,
  setupCompleteStatus,
} from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

vi.mock('../../lib/download', () => ({ saveDownloadedFile: vi.fn() }))

const auditItem: AuditLogItem = {
  id: 42,
  source: 'audit',
  recorded_at: '2026-11-15T18:00:00+00:00',
  actor_user_id: 1,
  actor_username: 'root',
  event_type: 'calendar.updated',
  entity_type: 'calendar_entry',
  entity_id: '7',
  summary: 'Calendar entry changed',
  reason: 'Swapped dinner',
  ip_address: '203.0.113.11',
  before: { entry_type: 'eat_out' },
  after: { entry_type: 'recipe' },
  metadata: { meal_date: '2026-11-15' },
}

const historyItem: AuditLogItem = {
  id: 9,
  source: 'entry_history',
  recorded_at: '2026-11-14T12:30:00+00:00',
  actor_user_id: 1,
  actor_username: 'root',
  event_type: 'calendar.reroll',
  entity_type: 'calendar_entry',
  entity_id: '5',
  summary: 'Rerolled the week',
  reason: null,
  ip_address: null,
  before: null,
  after: { days: ['2026-11-20'] },
  metadata: { kind: 'reroll' },
}

/** Renders the app on the Audit log section with the API stubbed. */
function renderAudit(
  options: {
    audit?: Partial<MockRoute>
    export?: Partial<MockRoute>
    prune?: Partial<MockRoute>
    security?: unknown
    users?: unknown
    session?: unknown
  } = {},
): MockFetch {
  const mock = installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session ?? adminSession },
    { path: '/users', body: options.users ?? [adminUserRecord, memberUserRecord] },
    { path: '/settings/security', body: options.security ?? securitySettings },
    // Export is matched before the viewer because its path contains
    // `/admin/audit`; the viewer route would otherwise answer it.
    options.export
      ? { path: '/admin/audit/export', ...options.export }
      : {
          path: '/admin/audit/export',
          status: 200,
          raw: 'id,source\r\n',
          headers: {
            'Content-Type': 'text/csv',
            'Content-Disposition': 'attachment; filename=audit-log.csv',
          },
        },
    {
      path: '/admin/audit',
      body: { items: [auditItem, historyItem], next_cursor: null },
      ...options.audit,
    },
    {
      path: '/admin/audit/prune',
      method: 'POST',
      body: { deleted_count: 3, cutoff: '2026-11-08T18:00:00+00:00', retention_days: 365 },
      ...options.prune,
    },
  ])
  renderWithProviders(<App />, { route: '/settings/audit' })
  return mock
}

function auditRequestUrls(mock: MockFetch): URL[] {
  return mock.fetchMock.mock.calls
    .filter(([url]) => {
      const value = String(url)
      return (
        value.includes('/admin/audit') &&
        !value.includes('/admin/audit/export') &&
        !value.includes('/admin/audit/prune')
      )
    })
    .map(([url]) => new URL(String(url), 'http://localhost'))
}

function exportRequestUrls(mock: MockFetch): URL[] {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).includes('/admin/audit/export'))
    .map(([url]) => new URL(String(url), 'http://localhost'))
}

function pruneBodies(mock: MockFetch): Array<Record<string, unknown>> {
  return mock.fetchMock.mock.calls
    .filter(
      ([url, init]) =>
        String(url).endsWith('/admin/audit/prune') &&
        (init as RequestInit | undefined)?.method === 'POST',
    )
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>)
}

function pagedAudit(): Partial<MockRoute> {
  return {
    respond: (_init, _count, url) => {
      const parsed = new URL(url, 'http://localhost')
      return {
        body:
          parsed.searchParams.get('cursor') === 'CURSOR1'
            ? { items: [historyItem], next_cursor: null }
            : { items: [auditItem], next_cursor: 'CURSOR1' },
      }
    },
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('audit log section', () => {
  it('renders audit and entry-history rows with their source labels', async () => {
    renderAudit()

    const list = await screen.findByRole('list', { name: 'Audit entries' })
    const rows = within(list).getAllByRole('listitem')
    expect(rows).toHaveLength(2)

    // The audit_events row.
    const auditRow = rows[0]
    expect(within(auditRow).getByText('Calendar entry updated')).toBeInTheDocument()
    expect(within(auditRow).getByText('Audit')).toBeInTheDocument()
    expect(within(auditRow).getByText('Calendar entry #7')).toBeInTheDocument()
    expect(within(auditRow).getByText('Calendar entry changed')).toBeInTheDocument()
    expect(within(auditRow).getByText('Actor: root')).toBeInTheDocument()
    expect(within(auditRow).getByText('Reason: Swapped dinner')).toBeInTheDocument()
    expect(within(auditRow).getByText('IP 203.0.113.11')).toBeInTheDocument()

    // The reroll row surfaced from the entry history is labelled distinctly.
    const historyRow = rows[1]
    expect(within(historyRow).getByText('Calendar reroll (history)')).toBeInTheDocument()
    expect(within(historyRow).getByText('Calendar history')).toBeInTheDocument()
    expect(within(historyRow).getByText('Reroll record')).toBeInTheDocument()
    expect(within(historyRow).getByText('Rerolled the week')).toBeInTheDocument()
  })

  it('sends filters as query params and resets the cursor on every change', async () => {
    const mock = renderAudit({ audit: pagedAudit() })

    await screen.findByRole('list', { name: 'Audit entries' })

    // Put a cursor in play by loading the second page.
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }))
    await waitFor(() =>
      expect(auditRequestUrls(mock).at(-1)?.searchParams.get('cursor')).toBe('CURSOR1'),
    )

    const changes: Array<[string, string, string, string]> = [
      ['Event type', 'calendar.created', 'event_type', 'calendar.created'],
      ['Entity type', 'recipe', 'entity_type', 'recipe'],
      ['Actor', '2', 'actor_user_id', '2'],
      ['Source', 'entry_history', 'source', 'entry_history'],
      ['From', '2026-11-01', 'from', '2026-11-01'],
      ['To', '2026-11-30', 'to', '2026-11-30'],
    ]

    for (const [label, value, param, expected] of changes) {
      fireEvent.change(screen.getByLabelText(label), { target: { value } })
      await waitFor(() => {
        const url = auditRequestUrls(mock).at(-1)
        expect(url?.searchParams.get(param)).toBe(expected)
        // A filter change always starts from a fresh first page.
        expect(url?.searchParams.get('cursor')).toBeNull()
      })
    }
  })

  it('appends the next page when Load more follows next_cursor', async () => {
    const mock = renderAudit({ audit: pagedAudit() })

    const list = await screen.findByRole('list', { name: 'Audit entries' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(1)
    expect(within(list).getByText('Calendar entry updated')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }))

    await waitFor(() => expect(within(list).getAllByRole('listitem')).toHaveLength(2))
    expect(within(list).getByText('Calendar reroll (history)')).toBeInTheDocument()
    expect(auditRequestUrls(mock).at(-1)?.searchParams.get('cursor')).toBe('CURSOR1')
    // The last page carries no cursor, so the control disappears.
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
  })

  it('expands a row detail with readable before and after JSON', async () => {
    renderAudit()

    await screen.findByRole('list', { name: 'Audit entries' })
    const toggle = screen.getAllByRole('button', { name: 'Details' })[0]
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')

    const panelId = toggle.getAttribute('aria-controls')
    expect(panelId).toBeTruthy()
    const panel = document.getElementById(panelId ?? '')
    expect(panel).not.toBeNull()
    const detail = panel as HTMLElement

    expect(within(detail).getByRole('heading', { name: 'Before' })).toBeInTheDocument()
    expect(within(detail).getByRole('heading', { name: 'After' })).toBeInTheDocument()
    expect(within(detail).getByRole('heading', { name: 'Metadata' })).toBeInTheDocument()
    expect(within(detail).getByText(/eat_out/)).toBeInTheDocument()
    expect(within(detail).getByText(/recipe/)).toBeInTheDocument()
    expect(within(detail).getByText(/meal_date/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Hide details' }))
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(document.getElementById(panelId ?? '')).toBeNull()
  })

  it('shows an empty state when no entries match', async () => {
    renderAudit({ audit: { body: { items: [], next_cursor: null } } })

    expect(await screen.findByText('No audit entries match these filters.')).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Audit entries' })).not.toBeInTheDocument()
  })

  it('shows an error state with a retry that refetches', async () => {
    const mock = renderAudit({
      audit: {
        status: 500,
        body: { error: { code: 'INTERNAL_ERROR', message: 'Audit exploded.', details: {} } },
      },
    })

    expect(
      await screen.findByRole('heading', { name: 'Cannot load the audit log' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Audit exploded.')

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(mock.callsFor('/admin/audit')).toBe(2))
  })

  it('shows the Admin-only notice on a deep link and issues no audit fetch for a member', async () => {
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
    ])

    renderWithProviders(<App />, { route: '/settings/audit' })

    expect(await screen.findByText(/limited to admins/i)).toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Settings sections' })
    expect(within(nav).queryByRole('link', { name: 'Audit log' })).not.toBeInTheDocument()
    // The UI gate is visibility only; it must never call the Admin endpoints.
    expect(mock.callsFor('/admin/audit')).toBe(0)
    expect(mock.callsFor('/users')).toBe(0)
  })

  it('shows the Audit log nav entry to Admins', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
    ])

    renderWithProviders(<App />, { route: '/settings/account' })

    const nav = await screen.findByRole('navigation', { name: 'Settings sections' })
    expect(within(nav).getByRole('link', { name: 'Audit log' })).toHaveAttribute(
      'href',
      '/settings/audit',
    )
  })

  it('exports the current filtered view as CSV and JSON', async () => {
    const mock = renderAudit()

    await screen.findByRole('list', { name: 'Audit entries' })

    // Activate two filters, then export each format.
    fireEvent.change(screen.getByLabelText('Event type'), { target: { value: 'calendar.created' } })
    fireEvent.change(screen.getByLabelText('Source'), { target: { value: 'entry_history' } })
    await waitFor(() =>
      expect(auditRequestUrls(mock).at(-1)?.searchParams.get('source')).toBe('entry_history'),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Export CSV' }))
    await waitFor(() => {
      const url = exportRequestUrls(mock).at(-1)
      expect(url?.searchParams.get('format')).toBe('csv')
      expect(url?.searchParams.get('event_type')).toBe('calendar.created')
      expect(url?.searchParams.get('source')).toBe('entry_history')
      // Paging controls never reach an export: it is always the whole view.
      expect(url?.searchParams.has('limit')).toBe(false)
      expect(url?.searchParams.has('cursor')).toBe(false)
    })
    await waitFor(() =>
      expect(saveDownloadedFile).toHaveBeenCalledWith(
        expect.objectContaining({ filename: 'audit-log.csv' }),
      ),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Export JSON' }))
    await waitFor(() =>
      expect(exportRequestUrls(mock).at(-1)?.searchParams.get('format')).toBe('json'),
    )
  })

  it('surfaces an export failure from the server', async () => {
    renderAudit({
      export: {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The filtered view exceeds the 100000 row export cap.',
            details: { field_errors: { export: ['too many rows'] } },
          },
        },
      },
    })

    await screen.findByRole('list', { name: 'Audit entries' })
    fireEvent.click(screen.getByRole('button', { name: 'Export CSV' }))

    expect(await screen.findByText(/exceeds the 100000 row export cap/i)).toBeInTheDocument()
    expect(saveDownloadedFile).not.toHaveBeenCalled()
  })

  it('prunes only after a reason and the exact PRUNE confirmation', async () => {
    const mock = renderAudit()

    await screen.findByRole('list', { name: 'Audit entries' })
    fireEvent.click(screen.getByRole('button', { name: 'Prune audit log' }))

    const dialog = await screen.findByRole('dialog', { name: 'Prune audit log' })
    // The retention window and permanence are explained before confirming.
    expect(within(dialog).getByText(/365-day retention window/i)).toBeInTheDocument()
    expect(within(dialog).getByText(/Deletion is permanent/i)).toBeInTheDocument()

    const confirm = within(dialog).getByRole('button', { name: 'Prune audit log' })
    expect(confirm).toBeDisabled()

    fireEvent.change(within(dialog).getByLabelText('Reason'), {
      target: { value: 'Retention window has passed' },
    })
    expect(confirm).toBeDisabled()

    fireEvent.click(within(dialog).getByLabelText(/cannot be recovered/i))
    expect(confirm).toBeDisabled()

    fireEvent.change(within(dialog).getByLabelText('Type PRUNE to confirm'), {
      target: { value: 'prune' },
    })
    expect(confirm).toBeDisabled()

    fireEvent.change(within(dialog).getByLabelText('Type PRUNE to confirm'), {
      target: { value: 'PRUNE' },
    })
    expect(confirm).toBeEnabled()

    fireEvent.click(confirm)

    await waitFor(() =>
      expect(pruneBodies(mock)).toEqual([
        { reason: 'Retention window has passed', confirmation: 'PRUNE' },
      ]),
    )
    expect(await screen.findByRole('status')).toHaveTextContent('Pruned 3 audit entries')
    expect(screen.queryByRole('dialog', { name: 'Prune audit log' })).not.toBeInTheDocument()
  })

  it('keeps the prune dialog open and shows a server refusal', async () => {
    renderAudit({
      security: { ...securitySettings, audit_retention_days: 0 },
      prune: {
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'Audit retention is set to keep forever, so there is nothing to prune.',
            details: { field_errors: { audit_retention_days: ['keep forever'] } },
          },
        },
      },
    })

    await screen.findByRole('list', { name: 'Audit entries' })
    fireEvent.click(screen.getByRole('button', { name: 'Prune audit log' }))

    const dialog = await screen.findByRole('dialog', { name: 'Prune audit log' })
    expect(within(dialog).getByText(/keep entries forever/i)).toBeInTheDocument()

    fireEvent.change(within(dialog).getByLabelText('Reason'), { target: { value: 'housekeeping' } })
    fireEvent.click(within(dialog).getByLabelText(/cannot be recovered/i))
    fireEvent.change(within(dialog).getByLabelText('Type PRUNE to confirm'), {
      target: { value: 'PRUNE' },
    })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Prune audit log' }))

    expect(await within(dialog).findByText(/nothing to prune/i)).toBeInTheDocument()
    expect(screen.getByRole('dialog', { name: 'Prune audit log' })).toBeInTheDocument()
  })

  it('hides the export and prune controls from a member', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
    ])

    renderWithProviders(<App />, { route: '/settings/audit' })

    expect(await screen.findByText(/limited to admins/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Export CSV' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Export JSON' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Prune audit log' })).not.toBeInTheDocument()
  })
})
