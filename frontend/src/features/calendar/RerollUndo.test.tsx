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

const OPERATION_ID = 'op-1-2-3'

function rerollResponse(): CalendarDay & { operation: { id: string; undo_expires_at: null } } {
  return {
    ...dayFor({ ...recipeEntry, display_text: 'Tacos' }),
    operation: { id: OPERATION_ID, undo_expires_at: null },
  }
}

function baseMocks(routes: MockRoute[] = [], session: unknown = adminSession): MockFetch {
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

async function openTodayDialog(): Promise<void> {
  const grid = await screen.findByRole('grid')
  fireEvent.click(within(grid).getByRole('gridcell', { name: /today/i }))
  await screen.findByRole('button', { name: 'Reroll' })
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('reroll and undo', () => {
  it('rerolls from the date dialog and shows the Undo banner', async () => {
    const mock = baseMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      { path: `/calendar/${todayIso}/reroll`, method: 'POST', body: rerollResponse() },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openTodayDialog()

    fireEvent.click(screen.getByRole('button', { name: 'Reroll' }))

    await waitFor(() => expect(mock.callsFor(`/calendar/${todayIso}/reroll`)).toBe(1))
    expect(await screen.findByText('Meal rerolled.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Undo' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeVisible()
  })

  it('rerolls from the month-grid context menu', async () => {
    const mock = baseMocks([
      { path: `/calendar/${todayIso}/reroll`, method: 'POST', body: rerollResponse() },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    const grid = await screen.findByRole('grid')
    const cell = within(grid).getByRole('gridcell', { name: /today/i })

    fireEvent.contextMenu(cell)
    fireEvent.click(await screen.findByRole('menuitem', { name: /Reroll meal/i }))

    await waitFor(() => expect(mock.callsFor(`/calendar/${todayIso}/reroll`)).toBe(1))
    expect(await screen.findByText('Meal rerolled.')).toBeVisible()
  })

  it('undoes a reroll with the operation id and refetches the calendar', async () => {
    const mock = baseMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      { path: `/calendar/${todayIso}/reroll`, method: 'POST', body: rerollResponse() },
      { path: `/operations/${OPERATION_ID}/undo`, method: 'POST', body: dayFor(recipeEntry) },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openTodayDialog()
    fireEvent.click(screen.getByRole('button', { name: 'Reroll' }))
    const undoButton = await screen.findByRole('button', { name: 'Undo' })
    const rangeCallsBefore = mock.callsFor('/calendar?')

    fireEvent.click(undoButton)

    await waitFor(() => expect(mock.callsFor(`/operations/${OPERATION_ID}/undo`)).toBe(1))
    await waitFor(() => expect(screen.queryByText('Meal rerolled.')).not.toBeInTheDocument())
    await waitFor(() => expect(mock.callsFor('/calendar?')).toBeGreaterThan(rangeCallsBefore))
  })

  it('dismisses the Undo banner without undoing', async () => {
    const mock = baseMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      { path: `/calendar/${todayIso}/reroll`, method: 'POST', body: rerollResponse() },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openTodayDialog()
    fireEvent.click(screen.getByRole('button', { name: 'Reroll' }))
    const dismiss = await screen.findByRole('button', { name: 'Dismiss' })

    fireEvent.click(dismiss)

    await waitFor(() => expect(screen.queryByText('Meal rerolled.')).not.toBeInTheDocument())
    expect(mock.callsFor(`/operations/${OPERATION_ID}/undo`)).toBe(0)
  })

  it('surfaces a failed reroll without crashing', async () => {
    baseMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      {
        path: `/calendar/${todayIso}/reroll`,
        method: 'POST',
        status: 422,
        body: {
          error: { code: 'RANDOMIZER_NO_CANDIDATES', message: 'none', details: {} },
        },
      },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openTodayDialog()

    fireEvent.click(screen.getByRole('button', { name: 'Reroll' }))

    expect(await screen.findByText(/No Normal recipes are available to reroll/i)).toBeVisible()
    expect(screen.queryByText('Meal rerolled.')).not.toBeInTheDocument()
  })

  it('reports a failed undo and clears the banner', async () => {
    baseMocks([
      { path: `/calendar/${todayIso}`, method: 'GET', body: dayFor(recipeEntry) },
      { path: `/calendar/${todayIso}/reroll`, method: 'POST', body: rerollResponse() },
      {
        path: `/operations/${OPERATION_ID}/undo`,
        method: 'POST',
        status: 409,
        body: { error: { code: 'UNDO_CONFLICT', message: 'changed', details: {} } },
      },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openTodayDialog()
    fireEvent.click(screen.getByRole('button', { name: 'Reroll' }))
    const undoButton = await screen.findByRole('button', { name: 'Undo' })

    fireEvent.click(undoButton)

    expect(await screen.findByText(/changed after the reroll/i)).toBeVisible()
    await waitFor(() => expect(screen.queryByText('Meal rerolled.')).not.toBeInTheDocument())
  })
})

describe('reroll availability in the date dialog', () => {
  function dialogMocks(day: CalendarDay): MockFetch {
    return installMockFetch([
      { path: '/recipes?', body: { items: [], total: 0, limit: 5, offset: 0 } },
      {
        path: '/repeat-check',
        method: 'GET',
        body: { is_repeat: false, window_start: '', window_end: '', occurrences: [] },
      },
      { path: `/calendar/${todayIso}`, method: 'GET', body: day },
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

  it('disables reroll with an explanation on historical dates', async () => {
    dialogMocks(dayFor(recipeEntry, { is_historical: true, can_edit: false, is_today: false }))
    renderDialog()

    const button = await screen.findByRole('button', { name: 'Reroll' })
    expect(button).toBeDisabled()
    expect(screen.getByText('Past dates cannot be rerolled.')).toBeVisible()
  })

  it('disables reroll with an explanation on locked entries', async () => {
    dialogMocks(dayFor({ ...recipeEntry, is_locked: true }))
    renderDialog()

    const button = await screen.findByRole('button', { name: 'Reroll' })
    expect(button).toBeDisabled()
    expect(screen.getByText('Unlock this date before rerolling it.')).toBeVisible()
  })

  it('disables reroll with an explanation on vacation days', async () => {
    dialogMocks(dayFor({ ...recipeEntry, entry_type: 'vacation', blocks_automation: true }))
    renderDialog()

    const button = await screen.findByRole('button', { name: 'Reroll' })
    expect(button).toBeDisabled()
    expect(screen.getByText('Vacation days cannot be rerolled.')).toBeVisible()
  })

  it('disables reroll without calendar.edit and explains why', async () => {
    dialogMocks(dayFor(recipeEntry))
    renderDialog(false)

    const button = await screen.findByRole('button', { name: 'Reroll' })
    expect(button).toBeDisabled()
    expect(screen.getByText(/calendar\.edit permission to reroll/i)).toBeVisible()
  })

  it('enables reroll for an editable, unlocked recipe date', async () => {
    dialogMocks(dayFor(recipeEntry))
    renderDialog()

    expect(await screen.findByRole('button', { name: 'Reroll' })).toBeEnabled()
  })
})
