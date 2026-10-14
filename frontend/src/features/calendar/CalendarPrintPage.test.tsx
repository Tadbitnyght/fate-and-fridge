import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Link } from 'react-router-dom'

import { App } from '../../App'
import type { CalendarDay, CalendarEntry, CalendarRange } from '../../api/calendar'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
import { buildMonthGrid } from './calendarDates'

function entry(overrides: Partial<CalendarEntry>): CalendarEntry {
  return {
    id: 1,
    entry_type: 'recipe',
    recipe: { id: 5, name: 'Lasagna', image_url: null, status: 'normal' },
    display_text: 'Lasagna',
    is_locked: false,
    diner_count: null,
    effective_diner_count: 4,
    restaurant_name: null,
    location_text: null,
    custom_label: null,
    notes: null,
    icon_key: null,
    include_groceries: true,
    blocks_automation: false,
    created_at: '2026-11-01T00:00:00+00:00',
    updated_at: '2026-11-01T00:00:00+00:00',
    ...overrides,
  }
}

/**
 * October 2026 entries exercising every printed content rule, plus the excluded
 * data (photo, lock, notes, diner count, restaurant, location).
 */
const OCTOBER_ENTRIES: Record<string, CalendarEntry> = {
  '2026-10-05': entry({
    entry_type: 'recipe',
    display_text: 'Lasagna',
    recipe: { id: 5, name: 'Lasagna', image_url: '/meal.jpg', status: 'normal' },
  }),
  '2026-10-12': entry({
    entry_type: 'holiday',
    display_text: 'Thanksgiving',
    recipe: null,
    icon_key: 'turkey',
    include_groceries: false,
  }),
  '2026-10-20': entry({
    entry_type: 'eat_out',
    recipe: null,
    display_text: 'Pizza Place',
    restaurant_name: 'Pizza Place',
    location_text: 'Downtown',
    notes: 'Reservation at 7',
    diner_count: 6,
    is_locked: true,
    include_groceries: false,
  }),
  '2026-10-25': entry({
    entry_type: 'fend_for_yourself',
    recipe: null,
    display_text: 'Fend for yourself',
    include_groceries: false,
  }),
}

function dayFor(date: string, value: CalendarEntry | null): CalendarDay {
  return {
    date,
    is_today: date === '2026-10-14',
    is_historical: false,
    can_edit: true,
    entry: value,
    repeat_warning: null,
  }
}

function calendarRange(start: string, end: string): CalendarRange {
  return {
    start,
    end,
    today: '2026-10-14',
    week_starts_on: 0,
    entries: Object.entries(OCTOBER_ENTRIES)
      .filter(([date]) => date >= start && date <= end)
      .map(([date, value]) => dayFor(date, value)),
  }
}

function printMocks(routes: MockRoute[] = [], session: unknown = adminSession): MockFetch {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: session },
    { path: '/health/db', body: { status: 'ok', database: 'connected' } },
    { path: '/health', body: { status: 'ok', app: 'Fate & Fridge', version: '0.1.0' } },
    {
      path: '/settings/household',
      body: {
        household_name: 'Home',
        timezone: 'America/Chicago',
        week_starts_on: 0,
        default_diner_count: 4,
        startup_page: 'calendar',
      },
    },
    { path: '/recipes?', body: { items: [], total: 0, limit: 5, offset: 0 } },
    {
      path: '/calendar?',
      respond: (_init, _count, url) => {
        const parsed = new URL(url, 'http://localhost')
        return {
          body: calendarRange(
            parsed.searchParams.get('start') ?? '',
            parsed.searchParams.get('end') ?? '',
          ),
        }
      },
    },
    ...routes,
  ])
}

function renderPrint(route = '/calendar/print?month=2026-10', session: unknown = adminSession) {
  const mock = printMocks([], session)
  const view = renderWithProviders(<App />, { route })
  return { mock, view }
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('calendar print page', () => {
  it('defaults to the ?month= month and shows the screen-only controls', async () => {
    renderPrint('/calendar/print?month=2026-10')

    expect(
      await screen.findByRole('heading', { level: 3, name: 'October 2026' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 2, name: 'Print calendar' })).toBeInTheDocument()
    expect(screen.getByLabelText('Start month')).toHaveValue('2026-10')
    expect(screen.getByLabelText('Months')).toHaveValue(1)
    expect(screen.getByRole('button', { name: 'Print' })).toBeInTheDocument()

    // Only the selected month prints.
    expect(screen.getAllByRole('heading', { level: 3 })).toHaveLength(1)
  })

  it('renders one heading per month in order across a year boundary', async () => {
    renderPrint('/calendar/print?month=2026-11')
    await screen.findByRole('heading', { level: 3, name: 'November 2026' })

    fireEvent.change(screen.getByLabelText('Months'), { target: { value: '3' } })

    await waitFor(() => {
      expect(screen.getAllByRole('heading', { level: 3 }).map((node) => node.textContent)).toEqual([
        'November 2026',
        'December 2026',
        'January 2027',
      ])
    })
  })

  it('reacts to a ?month= change instead of freezing the first value', async () => {
    printMocks()
    renderWithProviders(
      <>
        <Link to="/calendar/print?month=2026-12">Go to December</Link>
        <App />
      </>,
      { route: '/calendar/print?month=2026-10' },
    )
    await screen.findByRole('heading', { level: 3, name: 'October 2026' })

    fireEvent.click(screen.getByRole('link', { name: 'Go to December' }))

    expect(
      await screen.findByRole('heading', { level: 3, name: 'December 2026' }),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Start month')).toHaveValue('2026-12')
  })

  it('clamps the month count control to 1–12', async () => {
    renderPrint()
    await screen.findByRole('heading', { level: 3, name: 'October 2026' })

    fireEvent.change(screen.getByLabelText('Months'), { target: { value: '99' } })
    expect(screen.getByLabelText('Months')).toHaveValue(12)

    fireEvent.change(screen.getByLabelText('Months'), { target: { value: '0' } })
    expect(screen.getByLabelText('Months')).toHaveValue(1)
  })

  it('prints the day number and the recipe/special-event/eat-out/fend text', async () => {
    const { view } = renderPrint()

    const lasagna = await screen.findByText('Lasagna')
    const lasagnaCell = lasagna.closest('td')
    expect(lasagnaCell).not.toBeNull()
    expect(within(lasagnaCell as HTMLElement).getByText('5')).toBeInTheDocument()

    expect(screen.getByText('Thanksgiving')).toBeInTheDocument()
    expect(screen.getByText('Eat out')).toBeInTheDocument()
    expect(screen.getByText('Fend for yourself')).toBeInTheDocument()

    // One cell per grid day.
    const cells = view.container.querySelectorAll('.calendar-print-month__cell')
    expect(cells).toHaveLength(buildMonthGrid(2026, 9, 0).length)
    expect(cells.length % 7).toBe(0)
  })

  it('prints the curated special-event icon in black', async () => {
    const { view } = renderPrint()
    await screen.findByText('Thanksgiving')

    const icons = view.container.querySelectorAll('.calendar-print-month__icon')
    expect(icons).toHaveLength(1)
    // Inline SVG strokes with `currentColor`, so the icon inherits the black text.
    expect(icons[0].tagName.toLowerCase()).toBe('svg')
    expect(icons[0].getAttribute('stroke')).toBe('currentColor')
    expect(icons[0].getAttribute('aria-hidden')).toBe('true')
  })

  it('excludes meal photos, lock icons, notes, diner count, and restaurant/location text', async () => {
    const { view } = renderPrint()
    await screen.findByText('Lasagna')

    expect(view.container.querySelectorAll('.calendar-print-month img')).toHaveLength(0)
    expect(view.container.querySelector('.calendar-grid__lock')).toBeNull()
    expect(screen.queryByText('🔒')).toBeNull()
    expect(screen.queryByText('Reservation at 7')).toBeNull()
    expect(screen.queryByText(/diner/i)).toBeNull()
    // Eat out prints as text; its restaurant and location never leak.
    expect(screen.queryByText('Pizza Place')).toBeNull()
    expect(screen.queryByText('Downtown')).toBeNull()
  })

  it('calls window.print from the Print button', async () => {
    const printSpy = vi.spyOn(window, 'print').mockImplementation(() => undefined)
    renderPrint()
    await screen.findByText('Lasagna')

    fireEvent.click(screen.getByRole('button', { name: 'Print' }))

    expect(printSpy).toHaveBeenCalledTimes(1)
  })

  it('links back to the calendar', async () => {
    renderPrint()
    await screen.findByRole('heading', { level: 3, name: 'October 2026' })

    expect(screen.getByRole('link', { name: 'Back to calendar' })).toHaveAttribute(
      'href',
      '/calendar',
    )
  })

  it('shows a permission message without calendar.view and issues no calendar read', async () => {
    const { mock } = renderPrint('/calendar/print?month=2026-10', {
      ...memberSession,
      permissions: ['groceries.view'],
    })

    expect(await screen.findByText(/do not have permission to view the calendar/i)).toBeVisible()
    expect(mock.callsFor('/calendar?')).toBe(0)
  })
})
