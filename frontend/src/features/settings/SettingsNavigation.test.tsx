import { screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch } from '../../test/mockFetch'
import {
  adminSession,
  householdSettings,
  memberSession,
  securitySettings,
  setupCompleteStatus,
  singleUserSession,
  singleUserSetupStatus,
} from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('settings navigation and visibility', () => {
  it('shows every section to Admins with an active section link', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [] },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    const nav = await screen.findByRole('navigation', { name: 'Settings sections' })
    for (const label of ['Household', 'Users', 'Security', 'Account']) {
      expect(within(nav).getByRole('link', { name: label })).toBeInTheDocument()
    }
    expect(within(nav).getByRole('link', { name: 'Users' })).toHaveAttribute('aria-current', 'page')
  })

  it('hides the Admin sections from members and explains a direct visit', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/settings/household', body: householdSettings },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    const nav = await screen.findByRole('navigation', { name: 'Settings sections' })
    expect(within(nav).getByRole('link', { name: 'Household' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Account' })).toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: 'Security' })).not.toBeInTheDocument()

    expect(screen.getByText(/limited to admins/i)).toBeInTheDocument()
  })

  it('gives the single-user system actor the Admin sections', async () => {
    installMockFetch([
      { path: '/setup/status', body: singleUserSetupStatus },
      { path: '/auth/session', body: singleUserSession },
      { path: '/settings/security', body: securitySettings },
    ])

    renderWithProviders(<App />, { route: '/settings/security' })

    const nav = await screen.findByRole('navigation', { name: 'Settings sections' })
    expect(within(nav).getByRole('link', { name: 'Users' })).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: 'Security' })).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: 'Switch to multiuser' })).toBeInTheDocument()
  })
})
