import { fireEvent, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { createResponse, installMockFetch } from '../../test/mockFetch'
import { memberSession, setupCompleteStatus } from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'
import { AboutSection } from './AboutSection'

const about = {
  app_version: '0.1.0',
  schema_version: '20261021023131_M13Backups',
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('About section (M13-08)', () => {
  it('shows the app and schema versions plus the update-status placeholder to a member', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/about', body: about },
    ])

    renderWithProviders(<App />, { route: '/settings/about' })

    expect(await screen.findByRole('heading', { name: 'About' })).toBeInTheDocument()
    expect(screen.getByTestId('about-app-version')).toHaveTextContent('0.1.0')
    expect(screen.getByTestId('about-schema-version')).toHaveTextContent(
      '20261021023131_M13Backups',
    )

    // The update status is a marked placeholder for M13-09, not a live check.
    const placeholder = screen.getByTestId('about-update-placeholder')
    expect(within(placeholder).getByRole('heading', { name: 'Update status' })).toBeVisible()
    expect(within(placeholder).getByText(/not implemented yet/i)).toBeVisible()

    // No Admin gate: a member reaches the section directly.
    expect(screen.queryByText(/limited to admins/i)).not.toBeInTheDocument()
  })

  it('lists the About entry for a member in the settings navigation', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/about', body: about },
    ])

    renderWithProviders(<App />, { route: '/settings/about' })

    const nav = await screen.findByRole('navigation', { name: 'Settings sections' })
    const aboutLink = within(nav).getByRole('link', { name: 'About' })
    expect(aboutLink).toHaveAttribute('href', '/settings/about')
    expect(aboutLink).toHaveAttribute('aria-current', 'page')

    // Still no Admin-only entries for a member.
    expect(within(nav).queryByRole('link', { name: 'Backups' })).not.toBeInTheDocument()
  })

  it('shows the version in the shell footer and links it to About', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/about', body: about },
    ])

    renderWithProviders(<App />, { route: '/settings/account' })

    const versionLink = await screen.findByRole('link', { name: 'Version 0.1.0' })
    expect(versionLink).toHaveAttribute('href', '/settings/about')

    fireEvent.click(versionLink)
    expect(await screen.findByRole('heading', { name: 'About' })).toBeInTheDocument()
    expect(screen.getByTestId('about-schema-version')).toHaveTextContent(
      '20261021023131_M13Backups',
    )
  })

  it('renders a neutral loading state while the versions are fetched', () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => new Promise<Response>(() => undefined)),
    )

    renderWithProviders(<AboutSection />)

    expect(screen.getByRole('status')).toHaveTextContent('Loading version information…')
  })

  it('shows an error with a retry and no footer version when the request fails', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      {
        path: '/about',
        status: 500,
        body: {
          error: {
            code: 'INTERNAL_ERROR',
            message: 'The server failed to read the version.',
            details: {},
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/about' })

    expect(
      await screen.findByRole('heading', { name: 'Cannot load version information' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
    // A failed version request must not produce a broken footer label.
    expect(screen.queryByRole('link', { name: /^Version / })).not.toBeInTheDocument()
  })

  it('renders nothing for the footer label while the version is still loading', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input)
        if (url.includes('/about')) {
          return new Promise<Response>(() => undefined)
        }
        if (url.includes('/setup/status')) {
          return Promise.resolve(createResponse(200, setupCompleteStatus))
        }
        if (url.includes('/auth/session')) {
          return Promise.resolve(createResponse(200, memberSession))
        }
        return Promise.resolve(createResponse(404, null))
      }),
    )

    renderWithProviders(<App />, { route: '/settings/account' })

    // The app shell is usable; only the version label is withheld.
    expect(await screen.findByRole('heading', { name: 'Account' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /^Version / })).not.toBeInTheDocument()
  })
})
