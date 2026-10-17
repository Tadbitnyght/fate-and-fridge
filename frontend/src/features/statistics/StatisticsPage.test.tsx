import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { SPECIAL_EVENT_TYPES, type StatisticsOverview } from '../../api/statistics'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockResponse } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
function sampleOverview(overrides: Partial<StatisticsOverview> = {}): StatisticsOverview {
  return {
    windows: {
      today: '2026-11-15',
      timezone: 'America/Chicago',
      month: { label: 'November 2026', start: '2026-11-01', end: '2026-11-30' },
      year: { label: '2026', start: '2026-01-01', end: '2026-12-31' },
    },
    most_eaten: [
      { recipe_id: 5, name: 'Tacos', status: 'normal', times_eaten: 9, last_eaten: '2026-11-01' },
      {
        recipe_id: 7,
        name: 'Lasagna',
        status: 'retired',
        times_eaten: 4,
        last_eaten: '2026-09-20',
      },
    ],
    least_recently_eaten: [
      {
        recipe_id: null,
        name: "Grandma's Stew",
        status: 'deleted',
        times_eaten: 3,
        last_eaten: '2025-12-24',
      },
      {
        recipe_id: 9,
        name: 'Chili',
        status: 'manual_only',
        times_eaten: 1,
        last_eaten: '2026-06-01',
      },
    ],
    never_eaten: 42,
    meal_count_month: 12,
    meal_count_year: 140,
    protein_distribution: {
      proteins: [
        { protein_id: 2, name: 'Chicken', count: 30 },
        { protein_id: 3, name: 'Beef', count: 12 },
      ],
      no_primary_protein: 7,
    },
    special_event_counts: [
      { entry_type: 'eat_out', count: 4 },
      { entry_type: 'host_family_dinner', count: 1 },
      { entry_type: 'go_to_family_dinner', count: 2 },
      { entry_type: 'holiday', count: 1 },
      { entry_type: 'misc', count: 0 },
      { entry_type: 'fend_for_yourself', count: 6 },
      { entry_type: 'vacation', count: 1 },
    ],
    ...overrides,
  }
}

function emptyOverview(): StatisticsOverview {
  return sampleOverview({
    most_eaten: [],
    least_recently_eaten: [],
    never_eaten: 0,
    meal_count_month: 0,
    meal_count_year: 0,
    protein_distribution: { proteins: [], no_primary_protein: 0 },
    special_event_counts: SPECIAL_EVENT_TYPES.map((entry_type) => ({ entry_type, count: 0 })),
  })
}

interface RenderOptions {
  session?: typeof adminSession
  response?: MockResponse
}

function renderPage(options: RenderOptions = {}): MockFetch {
  const mock = installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session ?? adminSession },
    { path: '/statistics/overview', ...(options.response ?? { body: sampleOverview() }) },
  ])
  renderWithProviders(<App />, { route: '/statistics' })
  return mock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('statistics overview page', () => {
  it('renders the ranked lists with status badges, counts, and household-local dates', async () => {
    renderPage()

    expect(await screen.findByRole('heading', { level: 2, name: 'Statistics' })).toBeInTheDocument()
    expect(await screen.findByText('Eaten 9 times')).toBeInTheDocument()

    // A live recipe links to its recipe card.
    expect(screen.getByRole('link', { name: 'Tacos' })).toHaveAttribute('href', '/recipes/5/edit')
    expect(screen.getByText('Last eaten Nov 1, 2026')).toBeInTheDocument()
    expect(screen.getAllByText('Normal').length).toBeGreaterThan(0)
    expect(screen.getByText('Retired')).toBeInTheDocument()

    // A tombstone keeps its history but has no link.
    expect(screen.getByText("Grandma's Stew")).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: "Grandma's Stew" })).not.toBeInTheDocument()
    expect(screen.getByText('Deleted')).toBeInTheDocument()
    expect(screen.getByText('Last eaten Dec 24, 2025')).toBeInTheDocument()
    expect(screen.getByText('Manual only')).toBeInTheDocument()

    expect(screen.getByText('42 recipes have never been eaten.')).toBeInTheDocument()
  })

  it('renders the meal-count cards using the API window labels', async () => {
    renderPage()

    const monthCard = (await screen.findByText(/Meals this month/)).closest('li')
    expect(monthCard).not.toBeNull()
    expect(within(monthCard as HTMLElement).getByText('12')).toBeInTheDocument()
    expect(within(monthCard as HTMLElement).getByText(/November 2026/)).toBeInTheDocument()

    const yearCard = screen.getByText(/Meals this year/).closest('li')
    expect(yearCard).not.toBeNull()
    expect(within(yearCard as HTMLElement).getByText('140')).toBeInTheDocument()
    expect(within(yearCard as HTMLElement).getByText(/Meals this year/)).toBeInTheDocument()
  })

  it('renders the protein distribution as labelled bars plus an explicit no-protein figure', async () => {
    renderPage()

    const region = await screen.findByRole('region', { name: 'Primary protein distribution' })

    expect(within(region).getByText('Chicken')).toBeInTheDocument()
    expect(within(region).getByText('30 meals')).toBeInTheDocument()
    expect(within(region).getByText('Beef')).toBeInTheDocument()
    expect(within(region).getByText('12 meals')).toBeInTheDocument()
    expect(within(region).getByText('No primary protein')).toBeInTheDocument()
    expect(within(region).getByText('7 meals')).toBeInTheDocument()

    // One row per protein plus the explicit no-primary figure; the bar is
    // decorative so the visible label and value carry all meaning.
    expect(within(region).getAllByRole('listitem')).toHaveLength(3)
    expect(region.querySelector('.stats-bar')).toHaveAttribute('aria-hidden', 'true')

    const fills = region.querySelectorAll<HTMLElement>('.stats-bar__fill')
    expect(Array.from(fills).map((fill) => fill.style.width)).toEqual(['100%', '40%', '23%'])
  })

  it('lists all seven special-event types with their counts', async () => {
    renderPage()

    const region = await screen.findByRole('region', { name: 'Special events' })
    expect(
      within(region)
        .getAllByRole('listitem')
        .map((row) => row.textContent),
    ).toEqual([
      'Eat out4',
      'Host family dinner1',
      'Go to family dinner2',
      'Holiday1',
      'Misc0',
      'Fend for yourself6',
      'Vacation1',
    ])
  })

  it('shows a clear empty state when the household has no history', async () => {
    renderPage({ response: { body: emptyOverview() } })

    expect(await screen.findByText(/No meal history yet/)).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Most eaten' })).not.toBeInTheDocument()
  })

  it('shows an error state with a retry that refetches the overview', async () => {
    const mock = renderPage({
      response: {
        status: 500,
        body: {
          error: {
            code: 'INTERNAL_ERROR',
            message: 'Statistics exploded.',
            details: {},
          },
        },
      },
    })

    expect(
      await screen.findByRole('heading', { name: 'Cannot load statistics' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Statistics exploded.')

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(mock.callsFor('/statistics/overview')).toBe(2))
  })

  it('hides the nav item and shows a permission message without recipes.view', async () => {
    const restrictedMember = { ...memberSession, permissions: ['calendar.view', 'groceries.view'] }
    const mock = renderPage({ session: restrictedMember })

    expect(await screen.findByRole('heading', { level: 2, name: 'Statistics' })).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent(
      /do not have permission to view statistics/i,
    )
    expect(screen.queryByRole('link', { name: 'Statistics' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Calendar' })).toBeInTheDocument()
    expect(mock.callsFor('/statistics/overview')).toBe(0)
  })

  it('shows the nav item and content for a member with recipes.view', async () => {
    renderPage({ session: { ...memberSession, permissions: ['recipes.view'] } })

    expect(await screen.findByRole('link', { name: 'Statistics' })).toBeInTheDocument()
    expect(await screen.findByText('Eaten 9 times')).toBeInTheDocument()
  })
})

/* ---------- M11-05: Overview / History tabs ---------- */

function sampleHistory(): unknown {
  return {
    range: { start: '2025-11-01', end: '2026-10-16', timezone: 'America/Chicago' },
    month: { key: '2026-10', label: 'October 2026', start: '2026-10-01', end: '2026-10-31' },
    days: [
      { date: '2026-10-15', entry_type: 'recipe', recipe_id: 5, name: 'Tacos', protein: 'Chicken' },
    ],
    paging: { newer: null, older: '2026-09' },
  }
}

interface TabRenderOptions {
  session?: typeof adminSession
  route?: string
}

/** Renders the statistics route with both tab endpoints stubbed. */
function renderTabsPage(options: TabRenderOptions = {}): MockFetch {
  const mock = installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session ?? adminSession },
    { path: '/statistics/overview', body: sampleOverview() },
    { path: '/proteins?', body: [{ id: 2, name: 'Chicken', is_active: true }] },
    { path: '/statistics/history', body: sampleHistory() },
  ])
  renderWithProviders(<App />, { route: options.route ?? '/statistics' })
  return mock
}

describe('statistics tabs', () => {
  it('defaults to the Overview tab and shows the History tab with calendar.view', async () => {
    const mock = renderTabsPage()

    await screen.findByText('Eaten 9 times')

    const overviewTab = screen.getByRole('tab', { name: 'Overview' })
    const historyTab = screen.getByRole('tab', { name: 'History' })
    expect(overviewTab).toHaveAttribute('aria-selected', 'true')
    expect(overviewTab).toHaveAttribute('tabindex', '0')
    expect(historyTab).toHaveAttribute('aria-selected', 'false')
    expect(historyTab).toHaveAttribute('tabindex', '-1')
    expect(mock.callsFor('/statistics/history')).toBe(0)

    fireEvent.click(historyTab)

    expect(await screen.findByRole('region', { name: 'October 2026' })).toBeInTheDocument()
    expect(historyTab).toHaveAttribute('aria-selected', 'true')
    expect(overviewTab).toHaveAttribute('aria-selected', 'false')
    expect(screen.queryByText('Eaten 9 times')).not.toBeInTheDocument()
  })

  it('supports Left/Right/Home/End keyboard navigation with roving focus', async () => {
    renderTabsPage()

    await screen.findByText('Eaten 9 times')

    const overviewTab = screen.getByRole('tab', { name: 'Overview' })
    const historyTab = screen.getByRole('tab', { name: 'History' })

    overviewTab.focus()
    fireEvent.keyDown(overviewTab, { key: 'ArrowRight' })
    expect(historyTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(historyTab)

    fireEvent.keyDown(historyTab, { key: 'ArrowLeft' })
    expect(overviewTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(overviewTab)

    // Arrows wrap around the ends of the tablist.
    fireEvent.keyDown(overviewTab, { key: 'ArrowLeft' })
    expect(historyTab).toHaveAttribute('aria-selected', 'true')

    fireEvent.keyDown(historyTab, { key: 'Home' })
    expect(overviewTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(overviewTab)

    fireEvent.keyDown(overviewTab, { key: 'End' })
    expect(historyTab).toHaveAttribute('aria-selected', 'true')
    expect(document.activeElement).toBe(historyTab)
  })

  it('deep-links the History tab for a member with calendar.view but no recipes.view', async () => {
    const member = { ...memberSession, permissions: ['calendar.view'] }
    const mock = renderTabsPage({ session: member, route: '/statistics?tab=history' })

    const historyTab = await screen.findByRole('tab', { name: 'History' })
    expect(historyTab).toHaveAttribute('aria-selected', 'true')
    expect(historyTab).toHaveAttribute('tabindex', '0')
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'false')

    expect(await screen.findByRole('region', { name: 'October 2026' })).toBeInTheDocument()
    expect(mock.callsFor('/statistics/overview')).toBe(0)
    // The nav item still requires recipes.view, so it stays hidden.
    expect(screen.queryByRole('link', { name: 'Statistics' })).not.toBeInTheDocument()
  })

  it('explains a History deep link when calendar.view is missing', async () => {
    const member = { ...memberSession, permissions: ['recipes.view'] }
    const mock = renderTabsPage({ session: member, route: '/statistics?tab=history' })

    // Waiting for the nav link proves the session (and its permissions) loaded.
    expect(await screen.findByRole('link', { name: 'Statistics' })).toBeInTheDocument()
    expect(await screen.findByRole('alert')).toHaveTextContent(
      /meal history is not available to your account/i,
    )
    expect(screen.queryByRole('tab', { name: 'History' })).not.toBeInTheDocument()
    expect(mock.callsFor('/statistics/history')).toBe(0)
  })
})

/* ---------- M11-06: edit-implies-view gates (docs/M11_PLAN.md §1.30) ---------- */

describe('statistics access gates honour edit-implies-view', () => {
  it('shows the nav item and the overview for a member with only recipes.edit', async () => {
    renderPage({ session: { ...memberSession, permissions: ['recipes.edit'] } })

    expect(await screen.findByRole('link', { name: 'Statistics' })).toBeInTheDocument()
    expect(await screen.findByText('Eaten 9 times')).toBeInTheDocument()
    expect(screen.queryByText(/do not have permission to view statistics/i)).not.toBeInTheDocument()
  })

  it('shows the history tab and content for a member with only calendar.edit', async () => {
    const mock = renderTabsPage({
      session: { ...memberSession, permissions: ['calendar.edit'] },
      route: '/statistics?tab=history',
    })

    expect(await screen.findByRole('region', { name: 'October 2026' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'History' })).toHaveAttribute('aria-selected', 'true')
    expect(
      screen.queryByText(/meal history is not available to your account/i),
    ).not.toBeInTheDocument()
    expect(mock.callsFor('/statistics/overview')).toBe(0)
    // Neither recipes.view nor recipes.edit, so the nav item stays hidden.
    expect(screen.queryByRole('link', { name: 'Statistics' })).not.toBeInTheDocument()
  })
})
