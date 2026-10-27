import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { CalendarDay, CalendarEntry } from '../../api/calendar'
import { installMockFetch, type MockFetch, type MockRoute } from '../../test/mockFetch'
import { createTestQueryClient, renderWithProviders } from '../../test/utils'
import { DateDetailDialog } from './DateDetailDialog'
import { HistoricalCorrectionDialog } from './HistoricalCorrectionDialog'

const DATE = '2026-10-05'

function historicalEntry(overrides: Partial<CalendarEntry> = {}): CalendarEntry {
  return {
    id: 7,
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
    created_at: '2026-10-05T00:00:00+00:00',
    updated_at: '2026-10-05T00:00:00+00:00',
    ...overrides,
  }
}

function historicalDay(
  entry: CalendarEntry | null,
  overrides: Partial<CalendarDay> = {},
): CalendarDay {
  return {
    date: DATE,
    is_today: false,
    is_historical: true,
    can_edit: false,
    entry,
    repeat_warning: null,
    ...overrides,
  }
}

/** Mocks the reads and the correction write; overrides win over the defaults. */
function flowMocks(overrides: MockRoute[] = []): MockFetch {
  return installMockFetch([
    {
      path: `/calendar/${DATE}/repeat-check`,
      method: 'GET',
      body: { is_repeat: false, window_start: '', window_end: '', occurrences: [] },
    },
    ...overrides,
    { path: '/recipes?', body: { items: [], total: 0, limit: 5, offset: 0 } },
    { path: `/calendar/${DATE}`, method: 'GET', body: historicalDay(historicalEntry()) },
    {
      path: `/calendar/${DATE}/correction`,
      method: 'POST',
      body: historicalDay(historicalEntry()),
    },
  ])
}

function renderCorrection(overrides: Partial<Parameters<typeof HistoricalCorrectionDialog>[0]>) {
  return renderWithProviders(
    <HistoricalCorrectionDialog
      date={DATE}
      onClose={() => {}}
      onCorrected={() => {}}
      {...overrides}
    />,
  )
}

/** Steps to the confirmation dialog, fills it, and reveals the confirm button. */
async function completeConfirmation({
  reason = 'The wrong meal was recorded',
  confirmation = DATE,
  acknowledge = true,
}: {
  reason?: string
  confirmation?: string
  acknowledge?: boolean
} = {}): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: 'Continue' }))
  fireEvent.change(await screen.findByLabelText('Reason'), { target: { value: reason } })
  if (acknowledge) {
    fireEvent.click(screen.getByRole('checkbox'))
  }
  fireEvent.change(screen.getByLabelText('Type the date to confirm'), {
    target: { value: confirmation },
  })
}

function correctionBody(mock: MockFetch): Record<string, unknown> | undefined {
  const call = mock.fetchMock.mock.calls.find(
    ([url, init]) =>
      String(url).includes(`/calendar/${DATE}/correction`) &&
      (init as RequestInit | undefined)?.method === 'POST',
  )
  return call
    ? (JSON.parse(String((call[1] as RequestInit).body)) as Record<string, unknown>)
    : undefined
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('historical correction affordance', () => {
  function renderDetail(isAdmin: boolean, day: CalendarDay, canEdit = false) {
    flowMocks([{ path: `/calendar/${DATE}`, method: 'GET', body: day }])
    return renderWithProviders(
      <DateDetailDialog
        date={DATE}
        canEdit={canEdit}
        isAdmin={isAdmin}
        onClose={() => {}}
        onRerolled={() => {}}
      />,
    )
  }

  it('appears for an Admin on a historical date and opens the flow', async () => {
    renderDetail(true, historicalDay(historicalEntry()))

    const affordance = await screen.findByRole('button', { name: 'Correct this date' })
    expect(affordance).toBeInTheDocument()

    fireEvent.click(affordance)
    expect(await screen.findByRole('dialog', { name: /Correct 10-05-26/i })).toBeInTheDocument()
  })

  it('is hidden for a non-Admin', async () => {
    renderDetail(false, historicalDay(historicalEntry()))
    await screen.findByRole('dialog', { name: /read-only/i })

    expect(screen.queryByRole('button', { name: 'Correct this date' })).not.toBeInTheDocument()
  })

  it('is hidden for a non-historical date', async () => {
    renderDetail(
      true,
      historicalDay(historicalEntry(), { is_historical: false, is_today: true, can_edit: true }),
      true,
    )
    // A non-historical date renders the ordinary editor, not the read-only view.
    await screen.findByRole('button', { name: 'Save' })

    expect(screen.queryByRole('button', { name: 'Correct this date' })).not.toBeInTheDocument()
  })
})

describe('historical correction flow', () => {
  it('posts the exact contract body and reports success', async () => {
    const mock = flowMocks()
    const onCorrected = vi.fn()
    const queryClient = createTestQueryClient()
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
    renderWithProviders(
      <HistoricalCorrectionDialog date={DATE} onClose={() => {}} onCorrected={onCorrected} />,
      { queryClient },
    )

    await completeConfirmation()
    fireEvent.click(screen.getByRole('button', { name: 'Correct this date' }))

    await waitFor(() => expect(mock.callsFor(`/calendar/${DATE}/correction`)).toBe(1))
    expect(correctionBody(mock)).toEqual({
      entry_type: 'recipe',
      recipe_id: 5,
      diner_count: null,
      restaurant_name: null,
      location_text: null,
      custom_label: null,
      notes: null,
      icon_key: null,
      reason: 'The wrong meal was recorded',
      confirmation: DATE,
      lock_acknowledged: false,
    })

    await waitFor(() => expect(onCorrected).toHaveBeenCalledTimes(1))

    // Success invalidates the calendar, statistics, and audit query families so
    // the views refresh before the live-sync event arrives.
    await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(3))
    const keys = invalidate.mock.calls.map(
      ([filters]) => (filters as { queryKey: unknown[] }).queryKey,
    )
    expect(keys).toEqual(expect.arrayContaining([['calendar'], ['statistics'], ['audit']]))
  })

  it('keeps submit disabled until the typed date matches exactly', async () => {
    flowMocks()
    renderCorrection({})

    await completeConfirmation({ confirmation: '2026-10-06' })

    const confirm = screen.getByRole('button', { name: 'Correct this date' })
    expect(confirm).toBeDisabled()
    expect(screen.getByText('Type "2026-10-05" exactly to confirm.')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Type the date to confirm'), {
      target: { value: DATE },
    })
    expect(confirm).toBeEnabled()
  })

  it('requires the reason and the acknowledgement', async () => {
    flowMocks()
    renderCorrection({})

    fireEvent.click(await screen.findByRole('button', { name: 'Continue' }))

    // No reason, no acknowledgement, no confirmation yet.
    expect(screen.getByRole('button', { name: 'Correct this date' })).toBeDisabled()
  })

  it('warns about a locked date and sends lock_acknowledged true', async () => {
    const mock = flowMocks([
      {
        path: `/calendar/${DATE}`,
        method: 'GET',
        body: historicalDay(historicalEntry({ is_locked: true })),
      },
    ])
    renderCorrection({})

    await screen.findByRole('button', { name: 'Continue' })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(
      await screen.findByText(
        'This date is locked. The correction leaves it locked, and past dates still cannot be edited from the calendar.',
      ),
    ).toBeVisible()

    fireEvent.change(screen.getByLabelText('Reason'), {
      target: { value: 'Correct the lock record' },
    })
    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.change(screen.getByLabelText('Type the date to confirm'), {
      target: { value: DATE },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Correct this date' }))

    await waitFor(() => expect(mock.callsFor(`/calendar/${DATE}/correction`)).toBe(1))
    expect(correctionBody(mock)).toMatchObject({ lock_acknowledged: true })
  })

  it('surfaces CALENDAR_DATE_LOCKED readably', async () => {
    flowMocks([
      {
        path: `/calendar/${DATE}/correction`,
        method: 'POST',
        status: 409,
        body: { error: { code: 'CALENDAR_DATE_LOCKED', message: 'locked', details: {} } },
      },
    ])
    renderCorrection({})

    await completeConfirmation()
    fireEvent.click(screen.getByRole('button', { name: 'Correct this date' }))

    expect(await screen.findByText(/This date is locked\. Acknowledge the lock/i)).toBeVisible()
  })

  it('surfaces CONFIRMATION_MISMATCH readably', async () => {
    flowMocks([
      {
        path: `/calendar/${DATE}/correction`,
        method: 'POST',
        status: 400,
        body: {
          error: {
            code: 'CONFIRMATION_MISMATCH',
            message: 'mismatch',
            details: {
              field_errors: { confirmation: ['confirmation does not match the required value.'] },
            },
          },
        },
      },
    ])
    renderCorrection({})

    await completeConfirmation()
    fireEvent.click(screen.getByRole('button', { name: 'Correct this date' }))

    expect(await screen.findByText(/typed confirmation did not match/i)).toBeVisible()
  })

  it('surfaces VALIDATION_FAILED field errors readably', async () => {
    flowMocks([
      {
        path: `/calendar/${DATE}/correction`,
        method: 'POST',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request failed validation.',
            details: { field_errors: { recipe_id: ['Choose a recipe for this date.'] } },
          },
        },
      },
    ])
    renderCorrection({})

    await completeConfirmation()
    fireEvent.click(screen.getByRole('button', { name: 'Correct this date' }))

    expect(await screen.findByText('Choose a recipe for this date.')).toBeVisible()
  })
})
