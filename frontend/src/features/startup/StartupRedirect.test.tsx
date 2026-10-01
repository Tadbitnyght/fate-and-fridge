import { screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch } from '../../test/mockFetch'
import { adminSession, setupCompleteStatus } from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'

function householdSettings(startupPage: 'calendar' | 'dashboard') {
  return {
    household_name: 'The Smiths',
    timezone: 'UTC',
    week_starts_on: 0,
    default_diner_count: 4,
    startup_page: startupPage,
  }
}

function installMocks(startupPage: 'calendar' | 'dashboard') {
  return installMockFetch([
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: adminSession },
    { path: '/settings/household', body: householdSettings(startupPage) },
  ])
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('startup page routing', () => {
  it('redirects / to /calendar when the household prefers the calendar', async () => {
    installMocks('calendar')

    renderWithProviders(<App />, { route: '/' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Calendar' })).toHaveAttribute('aria-current', 'page')
  })

  it('redirects / to /dashboard when the household prefers the dashboard', async () => {
    installMocks('dashboard')

    renderWithProviders(<App />, { route: '/' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Dashboard' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveAttribute('aria-current', 'page')
  })

  it('falls back to the calendar when the settings cannot be read', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/settings/household',
        status: 500,
        body: { error: { code: 'INTERNAL_ERROR', message: 'boom', details: {} } },
      },
    ])

    renderWithProviders(<App />, { route: '/' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
  })

  it('shows a loading state while the startup page is unknown', async () => {
    installMocks('calendar')

    renderWithProviders(<App />, { route: '/' })

    // First paint: the settings query is still pending, so a loading status is
    // shown before the redirect resolves.
    expect(screen.getByRole('status')).toBeInTheDocument()
    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
  })

  it('applies to single-user households as well', async () => {
    installMockFetch([
      {
        path: '/setup/status',
        body: { setup_completed: true, auth_mode: 'single_user', recovery_key_present: true },
      },
      {
        path: '/auth/session',
        body: {
          authenticated: true,
          auth_mode: 'single_user',
          user: null,
          permissions: ['calendar.view'],
          must_change_password: false,
        },
      },
      { path: '/settings/household', body: householdSettings('dashboard') },
    ])

    renderWithProviders(<App />, { route: '/' })

    await waitFor(() =>
      expect(screen.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeInTheDocument(),
    )
  })
})
