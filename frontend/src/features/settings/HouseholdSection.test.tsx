import { fireEvent, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch, type MockFetch } from '../../test/mockFetch'
import {
  adminSession,
  householdSettings,
  memberSession,
  setupCompleteStatus,
} from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'

function installHouseholdMocks(options: { session: unknown }) {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: options.session },
    { path: '/settings/household', body: householdSettings },
    {
      path: '/settings/household',
      method: 'PATCH',
      body: { ...householdSettings, household_name: 'Updated Household', default_diner_count: 6 },
    },
  ])
}

function patchCalls(mock: MockFetch): Array<Record<string, unknown>> {
  return mock.fetchMock.mock.calls
    .filter(
      ([url, init]) =>
        String(url).endsWith('/settings/household') &&
        (init as RequestInit | undefined)?.method === 'PATCH',
    )
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>)
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('household settings section', () => {
  it('loads the settings and saves the edited fields', async () => {
    const mock = installHouseholdMocks({ session: adminSession })

    renderWithProviders(<App />, { route: '/settings' })

    const nameInput = await screen.findByLabelText('Household name')
    expect(nameInput).toHaveValue('The Smiths')

    fireEvent.change(nameInput, { target: { value: 'Updated Household' } })
    fireEvent.change(screen.getByLabelText('Default diners'), { target: { value: '6' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save household settings' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Household settings saved.')
    expect(patchCalls(mock)).toEqual([
      {
        household_name: 'Updated Household',
        timezone: 'America/Chicago',
        week_starts_on: 0,
        default_diner_count: 6,
        startup_page: 'calendar',
        randomizer_repeat_lookback_days: 7,
        randomizer_repeat_lookahead_days: 14,
        randomizer_repeat_multiplier: 0.1,
        randomizer_special_day_multiplier: 0.02,
        randomizer_protein_multiplier: 0.7,
        randomizer_protein_neighbor_distance: 1,
        randomizer_favorite_multiplier: 1.1,
        randomizer_spacing_near_days: 3,
        randomizer_spacing_far_days: 8,
        randomizer_spacing_search_days: 90,
      },
    ])
  })

  it('sends edited randomizer settings in the PATCH body', async () => {
    const mock = installHouseholdMocks({ session: adminSession })

    renderWithProviders(<App />, { route: '/settings' })

    const lookback = await screen.findByLabelText('Repeat lookback (days)')
    expect(lookback).toHaveValue(7)
    fireEvent.change(lookback, { target: { value: '10' } })
    fireEvent.change(screen.getByLabelText('Favorite multiplier'), { target: { value: '1.25' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save household settings' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Household settings saved.')
    expect(patchCalls(mock)).toEqual([
      {
        household_name: 'The Smiths',
        timezone: 'America/Chicago',
        week_starts_on: 0,
        default_diner_count: 4,
        startup_page: 'calendar',
        randomizer_repeat_lookback_days: 10,
        randomizer_repeat_lookahead_days: 14,
        randomizer_repeat_multiplier: 0.1,
        randomizer_special_day_multiplier: 0.02,
        randomizer_protein_multiplier: 0.7,
        randomizer_protein_neighbor_distance: 1,
        randomizer_favorite_multiplier: 1.25,
        randomizer_spacing_near_days: 3,
        randomizer_spacing_far_days: 8,
        randomizer_spacing_search_days: 90,
      },
    ])
  })

  it('blocks out-of-range and out-of-order randomizer values before sending', async () => {
    const mock = installHouseholdMocks({ session: adminSession })

    renderWithProviders(<App />, { route: '/settings' })

    const lookback = await screen.findByLabelText('Repeat lookback (days)')
    fireEvent.change(lookback, { target: { value: '99' } })
    fireEvent.change(screen.getByLabelText('Spacing near (days)'), { target: { value: '20' } })
    fireEvent.change(screen.getByLabelText('Spacing far (days)'), { target: { value: '10' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save household settings' }))

    expect(
      (
        await screen.findAllByText(
          'Repeat lookback (days) must be a whole number between 0 and 60.',
        )
      ).length,
    ).toBeGreaterThan(0)
    expect(
      (
        await screen.findAllByText(
          'Spacing far days must be greater than or equal to spacing near days.',
        )
      ).length,
    ).toBeGreaterThan(0)
    expect(patchCalls(mock)).toHaveLength(0)
  })

  it('blocks invalid values before sending and maps server field errors', async () => {
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/settings/household', body: householdSettings },
      {
        path: '/settings/household',
        method: 'PATCH',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request body failed validation.',
            details: {
              field_errors: { household_name: ['household_name must be a valid name.'] },
            },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings' })

    const nameInput = await screen.findByLabelText('Household name')
    fireEvent.change(nameInput, { target: { value: '   ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save household settings' }))

    expect((await screen.findAllByText('Enter a household name.')).length).toBeGreaterThan(0)
    expect(patchCalls(mock)).toHaveLength(0)

    fireEvent.change(nameInput, { target: { value: 'Server Rejected Name' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save household settings' }))

    expect(
      (await screen.findAllByText('household_name must be a valid name.')).length,
    ).toBeGreaterThan(0)
  })

  it('shows the values read-only to members', async () => {
    installHouseholdMocks({ session: memberSession })

    renderWithProviders(<App />, { route: '/settings' })

    const nameInput = await screen.findByLabelText('Household name')
    expect(nameInput).toBeDisabled()
    expect(
      screen.queryByRole('button', { name: 'Save household settings' }),
    ).not.toBeInTheDocument()
    expect(screen.getByText(/only admins can change household settings/i)).toBeInTheDocument()
  })
})
