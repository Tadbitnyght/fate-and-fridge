import { screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../App'
import { installMockFetch } from '../test/mockFetch'
import { adminSession, setupCompleteStatus } from '../test/fixtures'
import { renderWithProviders } from '../test/utils'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('skip link and route focus', () => {
  it('is the first focusable element and targets the main landmark', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/settings/household',
        body: {
          household_name: 'The Smiths',
          timezone: 'UTC',
          week_starts_on: 0,
          default_diner_count: 4,
          startup_page: 'calendar',
        },
      },
    ])

    renderWithProviders(<App />, { route: '/recipes' })

    const skipLink = await screen.findByRole('link', { name: 'Skip to main content' })
    expect(skipLink).toHaveAttribute('href', '#main-content')

    const main = screen.getByRole('main')
    expect(main).toHaveAttribute('id', 'main-content')
    expect(main).toHaveAttribute('tabindex', '-1')

    const focusables = Array.from(
      document.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]',
      ),
    )
    expect(focusables[0]).toBe(skipLink)
  })

  it('renders a skip link on the standalone pages too', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      {
        path: '/auth/session',
        status: 401,
        body: { error: { code: 'AUTH_REQUIRED', message: 'No session', details: {} } },
      },
    ])

    renderWithProviders(<App />, { route: '/login' })

    expect(await screen.findByRole('link', { name: 'Skip to main content' })).toBeInTheDocument()
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main-content')
  })

  it('moves focus to the page heading on route changes', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/settings/household',
        body: {
          household_name: 'The Smiths',
          timezone: 'UTC',
          week_starts_on: 0,
          default_diner_count: 4,
          startup_page: 'calendar',
        },
      },
    ])

    renderWithProviders(<App />, { route: '/recipes' })

    const heading = await screen.findByRole('heading', { level: 2, name: 'Recipes' })
    await waitFor(() => expect(document.activeElement).toBe(heading))
  })
})
