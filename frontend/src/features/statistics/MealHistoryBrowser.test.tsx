import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { MealHistoryResponse } from '../../api/statistics'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

const proteins = [
  { id: 2, name: 'Chicken', is_active: true },
  { id: 3, name: 'Beef', is_active: true },
  { id: 4, name: 'Lamb', is_active: false },
]

function sampleHistory(overrides: Partial<MealHistoryResponse> = {}): MealHistoryResponse {
  return {
    range: { start: '2025-11-01', end: '2026-10-16', timezone: 'America/Chicago' },
    month: { key: '2026-10', label: 'October 2026', start: '2026-10-01', end: '2026-10-31' },
    days: [
      { date: '2026-10-15', entry_type: 'recipe', recipe_id: 5, name: 'Tacos', protein: 'Chicken' },
      {
        date: '2026-10-14',
        entry_type: 'eat_out',
        recipe_id: null,
        name: 'Sushi place',
        protein: null,
      },
      {
        date: '2026-10-05',
        entry_type: 'recipe',
        recipe_id: null,
        name: "Grandma's Stew",
        protein: null,
      },
    ],
    paging: { newer: null, older: '2026-09' },
    ...overrides,
  }
}

const september = sampleHistory({
  month: { key: '2026-09', label: 'September 2026', start: '2026-09-01', end: '2026-09-30' },
  days: [
    { date: '2026-09-20', entry_type: 'holiday', recipe_id: 7, name: 'Lasagna', protein: 'Beef' },
  ],
  paging: { newer: '2026-10', older: null },
})

function historyRequests(mock: MockFetch): string[] {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).includes('/statistics/history'))
    .map(([url]) => String(url))
}

/** Renders the app on the History tab with the meal-log endpoint stubbed. */
function renderHistory(
  options: { history?: MockRoute; session?: typeof adminSession } = {},
): MockFetch {
  const mock = installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session ?? adminSession },
    { path: '/proteins?', body: proteins },
    { path: '/statistics/history', body: sampleHistory(), ...options.history },
  ])
  renderWithProviders(<App />, { route: '/statistics?tab=history' })
  return mock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('meal-history browser', () => {
  it('renders the month label, applied range, and its days newest-first', async () => {
    renderHistory()

    const month = await screen.findByRole('region', { name: 'October 2026' })

    // Title, label, and the full calendar-month bounds come from the server.
    expect(within(month).getByRole('heading', { level: 3 })).toHaveTextContent('October 2026')
    expect(within(month).getByText(/10-01-26/).textContent).toContain('10-31-26')
    const rangeNote = within(month).getByText(/Showing 11-01-25/)
    expect(rangeNote.textContent).toContain('America/Chicago')

    expect(
      within(month)
        .getAllByRole('listitem')
        .map((row) => row.textContent),
    ).toEqual([
      '10-15-26RecipeTacosChicken',
      '10-14-26Eat outSushi placeNo primary protein',
      "10-05-26RecipeGrandma's StewNo primary protein",
    ])

    // A live recipe links to the recipe; a tombstone keeps its snapshot name.
    expect(within(month).getByRole('link', { name: 'Tacos' })).toHaveAttribute(
      'href',
      '/recipes/5/edit',
    )
    expect(within(month).queryByRole('link', { name: "Grandma's Stew" })).not.toBeInTheDocument()
    expect(within(month).getByText("Grandma's Stew")).toBeInTheDocument()
  })

  it('starts on the API default page and pages with the newer/older cursors', async () => {
    const mock = renderHistory({
      history: {
        path: '/statistics/history',
        respond: (_init, _count, url) => ({
          body: url.includes('month=2026-09') ? september : sampleHistory(),
        }),
      },
    })

    await screen.findByRole('region', { name: 'October 2026' })

    // First load sends no `month`: the server picks the newest month in range.
    expect(historyRequests(mock)).toHaveLength(1)
    expect(historyRequests(mock)[0]).not.toContain('month=')

    // At the newer end the control is disabled; the older cursor is offered.
    expect(screen.getByRole('button', { name: 'Newer month' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Older month: Sep 2026' }))

    expect(await screen.findByRole('region', { name: 'September 2026' })).toBeInTheDocument()
    expect(historyRequests(mock).at(-1)).toContain('month=2026-09')
    // At the older end the control is disabled; now the newer cursor is offered.
    expect(screen.getByRole('button', { name: 'Older month' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Newer month: Oct 2026' })).toBeEnabled()
  })

  it('sends the entry-type, protein, and recipe filters server-side', async () => {
    const mock = renderHistory()

    await screen.findByRole('region', { name: 'October 2026' })

    fireEvent.change(screen.getByLabelText('Entry type'), { target: { value: 'eat_out' } })
    await waitFor(() => expect(historyRequests(mock).at(-1)).toContain('entry_type=eat_out'))

    // Retired proteins stay filterable, because historical meals reference them.
    expect(screen.getByRole('option', { name: 'Lamb (retired)' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Primary protein'), { target: { value: '2' } })
    await waitFor(() => expect(historyRequests(mock).at(-1)).toContain('protein=2'))

    // The text field accepts a recipe id or the stored snapshot name, on submit.
    const snapshotName = "Grandma's Stew"
    fireEvent.change(screen.getByLabelText('Recipe'), { target: { value: snapshotName } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply filters' }))
    await waitFor(() =>
      expect(historyRequests(mock).at(-1)).toContain(
        new URLSearchParams({ recipe: snapshotName }).toString(),
      ),
    )
  })

  it('returns to the default page when a filter changes', async () => {
    const mock = renderHistory({
      history: {
        path: '/statistics/history',
        respond: (_init, _count, url) => ({
          body: url.includes('month=2026-09') ? september : sampleHistory(),
        }),
      },
    })

    await screen.findByRole('region', { name: 'October 2026' })
    fireEvent.click(screen.getByRole('button', { name: 'Older month: Sep 2026' }))
    await screen.findByRole('region', { name: 'September 2026' })
    expect(historyRequests(mock).at(-1)).toContain('month=2026-09')

    fireEvent.change(screen.getByLabelText('Entry type'), { target: { value: 'holiday' } })

    await waitFor(() => expect(historyRequests(mock).at(-1)).toContain('entry_type=holiday'))
    expect(historyRequests(mock).at(-1)).not.toContain('month=')
  })

  it('shows a per-month empty state when the month has no logged meals', async () => {
    renderHistory({ history: { path: '/statistics/history', body: sampleHistory({ days: [] }) } })

    const month = await screen.findByRole('region', { name: 'October 2026' })
    expect(within(month).getByText('No meals logged in this month.')).toBeInTheDocument()
    expect(within(month).queryAllByRole('listitem')).toHaveLength(0)
  })

  it('shows an error state with a retry that refetches the month', async () => {
    const mock = renderHistory({
      history: {
        path: '/statistics/history',
        status: 500,
        body: {
          error: { code: 'INTERNAL_ERROR', message: 'History exploded.', details: {} },
        },
      },
    })

    expect(
      await screen.findByRole('heading', { name: 'Cannot load meal history' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('History exploded.')

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(mock.callsFor('/statistics/history')).toBe(2))
  })

  it('offers no edit or delete affordance anywhere', async () => {
    renderHistory()

    await screen.findByRole('region', { name: 'October 2026' })

    // The panel holds exactly the filter submit and the two paging controls —
    // no row-level action of any kind.
    const panel = screen.getByRole('tabpanel')
    expect(
      within(panel)
        .getAllByRole('button')
        .map((button) => button.textContent),
    ).toEqual(['Apply filters', 'Newer', 'Older'])
    expect(screen.queryByRole('button', { name: /edit|delete|remove/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /edit|delete|remove/i })).not.toBeInTheDocument()

    // The only product link within the log is the live recipe's own card link.
    expect(
      screen
        .getAllByRole('link')
        .map((link) => link.getAttribute('href'))
        .filter((href) => href?.startsWith('/recipes/')),
    ).toEqual(['/recipes/5/edit'])
  })

  it('renders a live recipe name as plain text for a view-only member (#148)', async () => {
    renderHistory({ session: { ...memberSession, permissions: ['calendar.view', 'recipes.view'] } })

    const month = await screen.findByRole('region', { name: 'October 2026' })

    expect(within(month).getByText('Tacos')).toBeInTheDocument()
    expect(within(month).queryByRole('link', { name: 'Tacos' })).not.toBeInTheDocument()
    // No recipe edit-page link exists anywhere in the rendered output.
    expect(
      screen
        .queryAllByRole('link')
        .filter((link) => /^\/recipes\/\d+\/edit$/.test(link.getAttribute('href') ?? '')),
    ).toHaveLength(0)
  })

  it('links a live recipe name for a member with recipes.edit (#148)', async () => {
    renderHistory({ session: { ...memberSession, permissions: ['calendar.view', 'recipes.edit'] } })

    const month = await screen.findByRole('region', { name: 'October 2026' })
    expect(within(month).getByRole('link', { name: 'Tacos' })).toHaveAttribute(
      'href',
      '/recipes/5/edit',
    )
  })
})
