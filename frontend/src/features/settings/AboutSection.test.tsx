import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { createResponse, installMockFetch, type MockRoute } from '../../test/mockFetch'
import { adminSession, memberSession, setupCompleteStatus } from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'
import { formatTimestamp } from '../../lib/dateFormat'
import { AboutSection } from './AboutSection'

const about = {
  app_version: '1.0.0',
  schema_version: '20261021023131_M13Backups',
}

const checkedAt = '2026-10-20T12:00:00Z'

function updateStatus(overrides: Record<string, unknown> = {}) {
  return {
    current_version: '1.0.0',
    latest_version: '1.0.0',
    update_available: false,
    last_checked_at: checkedAt,
    last_error: null,
    enabled: true,
    release_url: 'https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v1.0.0',
    ...overrides,
  }
}

/** The base routes every About render needs, plus one `/updates` answer. */
function baseRoutes(updates: MockRoute, session: unknown = memberSession): MockRoute[] {
  return [
    { path: '/setup/status', body: setupCompleteStatus },
    { path: '/auth/session', body: session },
    { path: '/about', body: about },
    // Specific Admin routes first: `/updates` is a substring of both.
    { path: '/admin/updates/check', method: 'POST', body: updateStatus() },
    { path: '/admin/updates', method: 'PATCH', body: updateStatus({ enabled: false }) },
    updates,
  ]
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('About section (M13-08 / M13-09)', () => {
  it('shows the app and schema versions plus the live update status to a member', async () => {
    installMockFetch(baseRoutes({ path: '/updates', body: updateStatus() }))

    renderWithProviders(<App />, { route: '/settings/about' })

    expect(await screen.findByRole('heading', { name: 'About' })).toBeInTheDocument()
    expect(screen.getByTestId('about-app-version')).toHaveTextContent('1.0.0')
    expect(screen.getByTestId('about-schema-version')).toHaveTextContent(
      '20261021023131_M13Backups',
    )

    const status = screen.getByTestId('about-update-status')
    expect(within(status).getByRole('heading', { name: 'Update status' })).toBeVisible()
    expect(within(status).getByTestId('about-current-version')).toHaveTextContent('1.0.0')
    expect(within(status).getByTestId('about-latest-version')).toHaveTextContent('1.0.0')
    expect(within(status).getByTestId('about-update-state')).toHaveTextContent('Up to date')
    expect(within(status).getByTestId('about-last-checked')).toHaveTextContent(
      formatTimestamp(checkedAt),
    )

    // No placeholder text remains, and the member is not gated out.
    expect(screen.queryByText(/not implemented yet/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/limited to admins/i)).not.toBeInTheDocument()

    // A member still gets the status read-only controls the server authorizes.
    expect(within(status).getByRole('button', { name: 'Check now' })).toBeEnabled()
    expect(within(status).queryByLabelText('Enable update checks')).not.toBeInTheDocument()
    expect(within(status).getByText(/never updates itself/i)).toBeVisible()
  })

  it('reports an available update with its version and no colour-only meaning', async () => {
    installMockFetch(
      baseRoutes({
        path: '/updates',
        body: updateStatus({
          latest_version: '0.2.0',
          update_available: true,
          release_url: 'https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.2.0',
        }),
      }),
    )

    renderWithProviders(<App />, { route: '/settings/about' })

    const status = await screen.findByTestId('about-update-status')
    expect(within(status).getByTestId('about-update-state')).toHaveTextContent('Update available')
    expect(within(status).getByTestId('about-latest-version')).toHaveTextContent('0.2.0')
  })

  it('shows a failed check quietly and keeps the versions readable', async () => {
    installMockFetch(
      baseRoutes({
        path: '/updates',
        body: updateStatus({
          update_available: true,
          latest_version: '0.2.0',
          last_error: 'The release feed returned HTTP 503.',
        }),
      }),
    )

    renderWithProviders(<App />, { route: '/settings/about' })

    const status = await screen.findByTestId('about-update-status')
    expect(within(status).getByTestId('about-update-error')).toHaveTextContent(
      'The release feed returned HTTP 503.',
    )
    // A failed check is a note, never a page error.
    expect(screen.getByRole('heading', { name: 'About' })).toBeInTheDocument()
    expect(
      screen.queryByRole('heading', { name: 'Cannot load version information' }),
    ).not.toBeInTheDocument()
  })

  it('forces a check from Check now and shows the refreshed status', async () => {
    let checked = 0
    installMockFetch(
      baseRoutes({
        path: '/updates',
        respond: () => ({
          body: updateStatus(
            checked === 0
              ? { last_checked_at: null, latest_version: null }
              : { update_available: true, latest_version: '0.2.0' },
          ),
        }),
      }).map((route) =>
        route.method === 'POST'
          ? {
              ...route,
              respond: () => {
                checked = 1
                return {
                  body: updateStatus({ update_available: true, latest_version: '0.2.0' }),
                }
              },
            }
          : route,
      ),
    )

    renderWithProviders(<App />, { route: '/settings/about' })

    const status = await screen.findByTestId('about-update-status')
    expect(within(status).getByTestId('about-last-checked')).toHaveTextContent('Never checked')

    fireEvent.click(within(status).getByRole('button', { name: 'Check now' }))

    expect(await screen.findByText('Checked with the release feed.')).toBeVisible()
    await waitFor(() =>
      expect(screen.getByTestId('about-update-state')).toHaveTextContent('Update available'),
    )
    expect(screen.getByTestId('about-latest-version')).toHaveTextContent('0.2.0')
  })

  it('does not claim success when a 200 check recorded a feed error', async () => {
    // The server answers 200 for a feed failure: the note is the only truth.
    const failed = updateStatus({ last_error: 'The release feed returned HTTP 503.' })
    let fetches = 0
    let posts = 0

    installMockFetch(
      baseRoutes({
        path: '/updates',
        respond: () => {
          fetches += 1
          return { body: failed }
        },
      }).map((route) =>
        route.method === 'POST'
          ? {
              ...route,
              respond: () => {
                posts += 1
                return { body: failed }
              },
            }
          : route,
      ),
    )

    renderWithProviders(<App />, { route: '/settings/about' })

    const status = await screen.findByTestId('about-update-status')
    fireEvent.click(within(status).getByRole('button', { name: 'Check now' }))

    await waitFor(() => expect(posts).toBe(1))
    // The mutation settles only after the invalidated status refetch resolves.
    await waitFor(() => expect(fetches).toBeGreaterThan(1))
    await waitFor(() =>
      expect(within(status).getByRole('button', { name: 'Check now' })).toBeEnabled(),
    )

    expect(within(status).getByTestId('about-update-error')).toHaveTextContent('HTTP 503')
    expect(screen.queryByText('Checked with the release feed.')).not.toBeInTheDocument()
  })

  it('shows the server’s 403 readably when a member forces a check', async () => {
    installMockFetch(
      baseRoutes({ path: '/updates', body: updateStatus() }).map((route) =>
        route.method === 'POST'
          ? {
              ...route,
              status: 403,
              body: {
                error: {
                  code: 'FORBIDDEN',
                  message: 'This action requires the Admin role.',
                  details: {},
                },
              },
            }
          : route,
      ),
    )

    renderWithProviders(<App />, { route: '/settings/about' })

    const status = await screen.findByTestId('about-update-status')
    fireEvent.click(within(status).getByRole('button', { name: 'Check now' }))

    expect(await screen.findByText(/only an admin can force an update check/i)).toBeInTheDocument()
    // The status itself stays readable; only the action failed.
    expect(within(status).getByTestId('about-update-state')).toHaveTextContent('Up to date')
  })

  it('lets an Admin turn update checks off through the PATCH', async () => {
    const state = { enabled: true }
    let patchedBody: unknown = null

    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/about', body: about },
      { path: '/admin/updates/check', method: 'POST', body: updateStatus() },
      {
        path: '/admin/updates',
        method: 'PATCH',
        respond: (init) => {
          patchedBody = JSON.parse(String(init.body))
          state.enabled = false
          return { body: updateStatus({ enabled: false }) }
        },
      },
      {
        path: '/updates',
        respond: () => ({ body: updateStatus({ enabled: state.enabled }) }),
      },
    ])

    renderWithProviders(<App />, { route: '/settings/about' })

    const toggle = await screen.findByLabelText('Enable update checks')
    expect(toggle).toBeChecked()

    fireEvent.click(toggle)

    await waitFor(() => expect(toggle).not.toBeChecked())
    expect(patchedBody).toEqual({ enabled: false })
    const disabledNote = screen.getByTestId('about-updates-disabled')
    expect(disabledNote).toHaveTextContent(/update checks are turned off/i)
    expect(disabledNote).toHaveAttribute('id', 'about-updates-disabled')
    // Disabled means the server makes no outbound call, so Check now is inert.
    const checkButton = screen.getByRole('button', { name: 'Check now' })
    expect(checkButton).toBeDisabled()
    // The button's `aria-describedby` must resolve to the note that exists.
    expect(checkButton).toHaveAttribute('aria-describedby', 'about-updates-disabled')
    expect(document.getElementById(checkButton.getAttribute('aria-describedby')!)).toBe(
      disabledNote,
    )
  })

  it('renders a quiet loading line for the update status while the versions load', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: memberSession },
      { path: '/about', body: about },
    ])
    // Replace the `/updates` answer with a request that never settles.
    const fetchMock = vi.mocked(fetch)
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/updates')) {
        return new Promise<Response>(() => undefined)
      }
      if (url.includes('/about')) {
        return createResponse(200, about)
      }
      return createResponse(200, memberSession)
    })

    renderWithProviders(<AboutSection />)

    expect(await screen.findByTestId('about-update-loading')).toHaveTextContent(
      'Loading update status…',
    )
    expect(screen.getByTestId('about-app-version')).toHaveTextContent('1.0.0')
  })

  it('keeps the versions readable when the update status request fails', async () => {
    installMockFetch(
      baseRoutes({
        path: '/updates',
        status: 500,
        body: {
          error: {
            code: 'INTERNAL_ERROR',
            message: 'The update status could not be read.',
            details: {},
          },
        },
      }),
    )

    renderWithProviders(<App />, { route: '/settings/about' })

    expect(await screen.findByTestId('about-update-unavailable')).toHaveTextContent(
      /update status is unavailable/i,
    )
    expect(screen.getByTestId('about-app-version')).toHaveTextContent('1.0.0')
    expect(screen.getByRole('button', { name: 'Reload update status' })).toBeInTheDocument()
  })

  it('lists the About entry for a member in the settings navigation', async () => {
    installMockFetch(baseRoutes({ path: '/updates', body: updateStatus() }))

    renderWithProviders(<App />, { route: '/settings/about' })

    const nav = await screen.findByRole('navigation', { name: 'Settings sections' })
    const aboutLink = within(nav).getByRole('link', { name: 'About' })
    expect(aboutLink).toHaveAttribute('href', '/settings/about')
    expect(aboutLink).toHaveAttribute('aria-current', 'page')

    // Still no Admin-only entries for a member.
    expect(within(nav).queryByRole('link', { name: 'Backups' })).not.toBeInTheDocument()
  })

  it('shows the version in the shell footer and links it to About', async () => {
    installMockFetch(baseRoutes({ path: '/updates', body: updateStatus() }))

    renderWithProviders(<App />, { route: '/settings/account' })

    const versionLink = await screen.findByRole('link', { name: 'Version 1.0.0' })
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
      { path: '/updates', body: updateStatus() },
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
