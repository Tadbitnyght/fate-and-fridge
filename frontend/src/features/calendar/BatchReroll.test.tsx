import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import type {
  BatchApplyResponse,
  BatchPreviewResponse,
  BatchUndoResponse,
  CalendarDay,
  CalendarEntry,
  CalendarRange,
} from '../../api/calendar'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
import { addDaysIso, isoDate } from './calendarDates'

const todayIso = isoDate(new Date())
const rangeEnd = addDaysIso(todayIso, 6)

const BATCH_OPERATION_ID = 'batch-0000-0000-0000-000000000001'

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

function previewResponse(overrides: Partial<BatchPreviewResponse> = {}): BatchPreviewResponse {
  return {
    draft: {
      token: 'draft-1',
      mode: 'seven_days',
      start_date: todayIso,
      end_date: rangeEnd,
      expires_at: '2026-11-01T18:00:00+00:00',
    },
    days: [
      {
        date: todayIso,
        status: 'proposed',
        unchanged_reason: null,
        current: null,
        proposed: { entry_type: 'recipe', recipe_id: 5, display_text: 'Lasagna' },
      },
      {
        date: rangeEnd,
        status: 'unchanged',
        unchanged_reason: 'locked',
        current: { ...recipeEntry, id: 2, display_text: 'Locked Stew' },
        proposed: null,
      },
    ],
    ...overrides,
  }
}

function applyResponse(): BatchApplyResponse {
  return {
    operation: { id: BATCH_OPERATION_ID, undo_expires_at: null },
    days: [],
    changed_count: 1,
  }
}

function undoResponse(): BatchUndoResponse {
  return {
    operation: { id: BATCH_OPERATION_ID, undo_expires_at: null },
    days: [],
    reverted_count: 1,
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

async function openMenu(): Promise<void> {
  await screen.findByRole('grid')
  fireEvent.click(screen.getByRole('button', { name: 'Reroll…' }))
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('batch reroll menu', () => {
  it('opens the preview for 7 days with the household today as start_date', async () => {
    const mock = baseMocks([
      { path: '/calendar/reroll/preview', method: 'POST', body: previewResponse() },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openMenu()

    fireEvent.click(await screen.findByRole('menuitem', { name: '7 days' }))

    await waitFor(() => expect(mock.callsFor('/calendar/reroll/preview')).toBe(1))
    const call = mock.fetchMock.mock.calls.find(([url]) =>
      String(url).includes('/calendar/reroll/preview'),
    )
    expect(JSON.parse(String((call![1] as RequestInit).body))).toMatchObject({
      start_date: todayIso,
      mode: 'seven_days',
    })
    expect(
      await screen.findByRole('dialog', { name: /Reroll preview — 7 days/i }),
    ).toBeInTheDocument()
  })

  it('sends the fill_empty mode when Fill empty is chosen', async () => {
    const mock = baseMocks([
      { path: '/calendar/reroll/preview', method: 'POST', body: previewResponse() },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openMenu()

    fireEvent.click(await screen.findByRole('menuitem', { name: 'Fill empty' }))

    await waitFor(() => expect(mock.callsFor('/calendar/reroll/preview')).toBe(1))
    const call = mock.fetchMock.mock.calls.find(([url]) =>
      String(url).includes('/calendar/reroll/preview'),
    )
    expect(JSON.parse(String((call![1] as RequestInit).body))).toMatchObject({
      mode: 'fill_empty',
    })
  })

  it('closes the menu with Escape', async () => {
    baseMocks([{ path: '/calendar/reroll/preview', method: 'POST', body: previewResponse() }])
    renderWithProviders(<App />, { route: '/calendar' })
    await openMenu()
    expect(await screen.findByRole('menu', { name: 'Batch reroll options' })).toBeInTheDocument()

    fireEvent.keyDown(screen.getByRole('menu', { name: 'Batch reroll options' }), {
      key: 'Escape',
    })

    await waitFor(() =>
      expect(screen.queryByRole('menu', { name: 'Batch reroll options' })).not.toBeInTheDocument(),
    )
  })

  it('hides the Reroll menu from actors without calendar.edit', async () => {
    baseMocks([], memberSession)
    renderWithProviders(<App />, { route: '/calendar' })
    await screen.findByRole('grid')

    expect(screen.queryByRole('button', { name: 'Reroll…' })).not.toBeInTheDocument()
  })
})

describe('batch reroll preview wizard', () => {
  async function openPreview(routes: MockRoute[] = []): Promise<MockFetch> {
    const mock = baseMocks([
      { path: '/calendar/reroll/preview', method: 'POST', body: previewResponse() },
      ...routes,
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openMenu()
    fireEvent.click(await screen.findByRole('menuitem', { name: '7 days' }))
    await screen.findByRole('dialog', { name: /Reroll preview/i })
    return mock
  }

  it('renders proposed dates highlighted and unchanged dates muted with their reason', async () => {
    await openPreview()
    const dialog = screen.getByRole('dialog', { name: /Reroll preview/i })

    const proposed = within(dialog).getByText('Lasagna')
    expect(proposed.closest('li')).toHaveClass('batch-preview__day--changed')

    const locked = within(dialog).getByText('Locked')
    expect(locked.closest('li')).toHaveClass('batch-preview__day--unchanged')
  })

  it('re-previews with a fresh token on Try another', async () => {
    const mock = baseMocks([
      {
        path: '/calendar/reroll/preview',
        method: 'POST',
        respond: (_init, count) => ({
          body:
            count === 1
              ? previewResponse()
              : previewResponse({
                  draft: {
                    token: 'draft-2',
                    mode: 'seven_days',
                    start_date: todayIso,
                    end_date: rangeEnd,
                    expires_at: '2026-11-01T18:15:00+00:00',
                  },
                  days: [
                    {
                      date: todayIso,
                      status: 'proposed',
                      unchanged_reason: null,
                      current: null,
                      proposed: { entry_type: 'recipe', recipe_id: 7, display_text: 'Tacos' },
                    },
                  ],
                }),
        }),
      },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openMenu()
    fireEvent.click(await screen.findByRole('menuitem', { name: '7 days' }))
    const dialog = await screen.findByRole('dialog', { name: /Reroll preview/i })
    await within(dialog).findByText('Lasagna')

    fireEvent.click(within(dialog).getByRole('button', { name: 'Try another' }))

    await waitFor(() => expect(mock.callsFor('/calendar/reroll/preview')).toBe(2))
    expect(await within(dialog).findByText('Tacos')).toBeVisible()
  })

  it('applies the preview token and shows the batch Undo banner', async () => {
    const mock = await openPreview([
      { path: '/calendar/reroll/apply', method: 'POST', body: applyResponse() },
    ])
    const dialog = screen.getByRole('dialog', { name: /Reroll preview/i })

    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply' }))

    await waitFor(() => expect(mock.callsFor('/calendar/reroll/apply')).toBe(1))
    const call = mock.fetchMock.mock.calls.find(([url]) =>
      String(url).includes('/calendar/reroll/apply'),
    )
    expect(JSON.parse(String((call![1] as RequestInit).body))).toMatchObject({ token: 'draft-1' })

    expect(await screen.findByText('Batch reroll applied.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Undo' })).toBeVisible()
  })

  it('calls the batch undo route and refreshes the calendar', async () => {
    const mock = await openPreview([
      { path: '/calendar/reroll/apply', method: 'POST', body: applyResponse() },
      {
        path: `/batch-operations/${BATCH_OPERATION_ID}/undo`,
        method: 'POST',
        body: undoResponse(),
      },
    ])
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))
    const undoButton = await screen.findByRole('button', { name: 'Undo' })
    const rangeCallsBefore = mock.callsFor('/calendar?')

    fireEvent.click(undoButton)

    await waitFor(() =>
      expect(mock.callsFor(`/batch-operations/${BATCH_OPERATION_ID}/undo`)).toBe(1),
    )
    await waitFor(() => expect(screen.queryByText('Batch reroll applied.')).not.toBeInTheDocument())
    await waitFor(() => expect(mock.callsFor('/calendar?')).toBeGreaterThan(rangeCallsBefore))
  })

  it('cancels without applying', async () => {
    const mock = await openPreview([
      { path: '/calendar/reroll/apply', method: 'POST', body: applyResponse() },
    ])

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: /Reroll preview/i })).not.toBeInTheDocument(),
    )
    expect(mock.callsFor('/calendar/reroll/apply')).toBe(0)
  })

  it('reports Nothing to change when every date is unchanged', async () => {
    baseMocks([
      {
        path: '/calendar/reroll/preview',
        method: 'POST',
        body: previewResponse({
          days: [
            {
              date: todayIso,
              status: 'unchanged',
              unchanged_reason: 'special',
              current: recipeEntry,
              proposed: null,
            },
          ],
        }),
      },
    ])
    renderWithProviders(<App />, { route: '/calendar' })
    await openMenu()
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Fill empty' }))

    expect(await screen.findByText('Nothing to change.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Apply' })).toBeEnabled()
  })

  it('surfaces a BATCH_CONFLICT apply error non-blockingly and stays open', async () => {
    await openPreview([
      {
        path: '/calendar/reroll/apply',
        method: 'POST',
        status: 409,
        body: { error: { code: 'BATCH_CONFLICT', message: 'changed', details: {} } },
      },
    ])
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }))

    expect(await screen.findByText(/changed since the preview/i)).toBeVisible()
    expect(screen.getByRole('dialog', { name: /Reroll preview/i })).toBeInTheDocument()
  })
})
