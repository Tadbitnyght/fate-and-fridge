import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type { CalendarDay, CalendarEntry, CalendarRange } from '../../api/calendar'
import { adminSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
import { isoDate } from './calendarDates'
import { DateDetailDialog } from './DateDetailDialog'

const todayIso = isoDate(new Date())

const recipeEntry: CalendarEntry = {
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
}

function dayFor(entry: CalendarEntry | null, overrides: Partial<CalendarDay> = {}): CalendarDay {
  return {
    date: todayIso,
    is_today: true,
    is_historical: false,
    can_edit: true,
    entry,
    repeat_warning: null,
    ...overrides,
  }
}

const range: CalendarRange = {
  start: todayIso,
  end: todayIso,
  today: todayIso,
  week_starts_on: 0,
  entries: [dayFor(recipeEntry)],
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
    { path: '/calendar?', body: range },
    ...routes,
  ])
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('calendar page', () => {
  it('renders the month grid with today highlighted and the entry text', async () => {
    baseMocks([])
    renderWithProviders(<App />, { route: '/calendar' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    const grid = await screen.findByRole('grid')
    expect(within(grid).getByText('Lasagna')).toBeInTheDocument()
    const todayCell = within(grid).getByRole('gridcell', { name: /today/i })
    expect(todayCell).toHaveAttribute('aria-selected', 'false')
  })

  it('switches to the agenda view', async () => {
    baseMocks([])
    renderWithProviders(<App />, { route: '/calendar' })
    await screen.findByRole('grid')

    fireEvent.click(screen.getByRole('button', { name: 'Agenda' }))

    const agenda = screen.getByRole('list', { name: 'Agenda' })
    expect(within(agenda).getByText('Lasagna')).toBeVisible()
  })

  it('starts the week on the household-configured day', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/health/db', body: { status: 'ok', database: 'connected' } },
      { path: '/health', body: { status: 'ok' } },
      {
        path: '/settings/household',
        body: {
          household_name: 'Home',
          timezone: 'America/Chicago',
          week_starts_on: 1,
          default_diner_count: 4,
          startup_page: 'calendar',
        },
      },
      { path: '/recipes?', body: { items: [], total: 0, limit: 5, offset: 0 } },
      { path: '/calendar?', body: range },
    ])
    renderWithProviders(<App />, { route: '/calendar' })

    const grid = await screen.findByRole('grid')
    const headers = within(grid)
      .getAllByRole('columnheader')
      .map((header) => header.textContent)
    expect(headers[0]).toBe('Mon')
    expect(headers[6]).toBe('Sun')
  })

  it('opens the print view from the toolbar (M10-06)', async () => {
    const printSpy = vi.spyOn(window, 'print').mockImplementation(() => {})
    baseMocks([])
    renderWithProviders(<App />, { route: '/calendar' })
    await screen.findByRole('heading', { level: 2, name: 'Calendar' })

    fireEvent.click(screen.getByRole('button', { name: 'Print' }))

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Print calendar' }),
    ).toBeInTheDocument()
    // The toolbar now navigates to the dedicated print view rather than printing
    // the interactive month grid.
    expect(printSpy).not.toHaveBeenCalled()
  })

  it('hides the calendar from members without calendar.view', async () => {
    baseMocks([], {
      ...adminSession,
      user: { id: 2, username: 'member', role: 'member' },
      permissions: [],
    })
    renderWithProviders(<App />, { route: '/calendar' })

    expect(await screen.findByText(/do not have permission to view the calendar/i)).toBeVisible()
  })
})

describe('calendar date detail dialog', () => {
  function dialogMocks(routes: MockRoute[] = []): MockFetch {
    return installMockFetch([
      { path: '/recipes?', body: { items: [], total: 0, limit: 5, offset: 0 } },
      ...routes,
    ])
  }

  function renderDialog(canEdit = true) {
    return renderWithProviders(
      <DateDetailDialog
        date={todayIso}
        canEdit={canEdit}
        onClose={() => {}}
        onRerolled={() => {}}
      />,
    )
  }

  it('seeds the entry and saves a full-replace request', async () => {
    const mock = dialogMocks([
      {
        path: '/repeat-check',
        method: 'GET',
        body: { is_repeat: false, window_start: '', window_end: '', occurrences: [] },
      },
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      { path: `/calendar/${todayIso}`, method: 'PUT', body: dayFor(recipeEntry) },
    ])
    renderDialog()

    const nameInput = await screen.findByLabelText(/Recipe \(required\)/i)
    expect(nameInput).toBeInTheDocument()
    expect(await screen.findByText(/Selected:/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      const call = mock.fetchMock.mock.calls.find(
        ([url, init]) =>
          String(url).includes(`/calendar/${todayIso}`) &&
          (init as RequestInit | undefined)?.method === 'PUT',
      )
      expect(call).toBeDefined()
      const body = JSON.parse(String((call![1] as RequestInit).body))
      expect(body).toMatchObject({ entry_type: 'recipe', recipe_id: 5 })
    })
  })

  it('renders historical dates read-only', async () => {
    dialogMocks([
      {
        path: '/repeat-check',
        method: 'GET',
        body: { is_repeat: false, window_start: '', window_end: '', occurrences: [] },
      },
      {
        path: `/calendar/${todayIso}`,
        method: 'GET',
        body: dayFor(recipeEntry, { is_historical: true, can_edit: false, is_today: false }),
      },
    ])
    renderDialog()

    expect(await screen.findByRole('dialog', { name: /read-only/i })).toBeInTheDocument()
    expect(screen.getByText(/Past dates are read-only/i)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument()
  })

  it('locks an existing entry', async () => {
    const mock = dialogMocks([
      {
        path: '/repeat-check',
        method: 'GET',
        body: { is_repeat: false, window_start: '', window_end: '', occurrences: [] },
      },
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      {
        path: `/calendar/${todayIso}/lock`,
        method: 'POST',
        body: dayFor({ ...recipeEntry, is_locked: true }),
      },
    ])
    renderDialog()

    fireEvent.click(await screen.findByRole('button', { name: 'Lock' }))

    await waitFor(() => expect(mock.callsFor(`/calendar/${todayIso}/lock`)).toBe(1))
  })

  it('creates a blank date as an eat-out entry', async () => {
    const mock = dialogMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(null) },
      {
        path: `/calendar/${todayIso}`,
        method: 'PUT',
        body: dayFor({
          ...recipeEntry,
          entry_type: 'eat_out',
          recipe: null,
          display_text: 'Pizza Place',
          restaurant_name: 'Pizza Place',
          include_groceries: false,
        }),
      },
    ])
    renderDialog()

    const typeSelect = await screen.findByLabelText('Type')
    fireEvent.change(typeSelect, { target: { value: 'eat_out' } })
    fireEvent.change(screen.getByLabelText(/Restaurant/i), { target: { value: 'Pizza Place' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      const call = mock.fetchMock.mock.calls.find(
        ([url, init]) =>
          String(url).includes(`/calendar/${todayIso}`) &&
          (init as RequestInit | undefined)?.method === 'PUT',
      )
      expect(JSON.parse(String((call![1] as RequestInit).body))).toMatchObject({
        entry_type: 'eat_out',
        restaurant_name: 'Pizza Place',
      })
    })
  })

  it('shows the server validation message when a misc label is missing', async () => {
    dialogMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(null) },
      {
        path: `/calendar/${todayIso}`,
        method: 'PUT',
        status: 400,
        body: { error: { code: 'CALENDAR_MISC_LABEL_REQUIRED', message: 'label', details: {} } },
      },
    ])
    renderDialog()

    fireEvent.change(await screen.findByLabelText('Type'), { target: { value: 'misc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Enter a label for this misc entry.')).toBeVisible()
  })

  it('warns about an exact repeat within the window without blocking', async () => {
    dialogMocks([
      {
        path: '/repeat-check',
        method: 'GET',
        body: {
          is_repeat: true,
          window_start: '',
          window_end: '',
          occurrences: [{ date: '2026-11-20', entry_type: 'recipe' }],
        },
      },
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
    ])
    renderDialog()

    expect(await screen.findByText(/repeat window/i)).toBeVisible()
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled()
  })
})
