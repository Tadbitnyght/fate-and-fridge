import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { AuditLogItem } from '../../api/audit'
import {
  adminSession,
  adminUserRecord,
  memberSession,
  memberUserRecord,
  setupCompleteStatus,
} from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

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
    users?: unknown
    session?: unknown
  } = {},
): MockFetch {
  const mock = installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session ?? adminSession },
    { path: '/users', body: options.users ?? [adminUserRecord, memberUserRecord] },
    {
      path: '/admin/audit',
      body: { items: [auditItem, historyItem], next_cursor: null },
      ...options.audit,
    },
  ])
  renderWithProviders(<App />, { route: '/settings/audit' })
  return mock
}

function auditRequestUrls(mock: MockFetch): URL[] {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).includes('/admin/audit'))
    .map(([url]) => new URL(String(url), 'http://localhost'))
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
})
