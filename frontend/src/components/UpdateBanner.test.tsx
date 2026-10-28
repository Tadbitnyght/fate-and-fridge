import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { App } from '../App'
import { adminSession, setupCompleteStatus } from '../test/fixtures'
import { installMockFetch } from '../test/mockFetch'
import { createTestQueryClient, renderWithProviders } from '../test/utils'
import { UPDATE_NOTICE_DISMISSED_KEY, safeReleaseUrl } from '../lib/updateNotice'
import { UpdateBanner } from './UpdateBanner'

const releaseUrl = 'https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.2.0'

function updateStatus(overrides: Record<string, unknown> = {}) {
  return {
    current_version: '0.1.0',
    latest_version: '0.2.0',
    update_available: true,
    last_checked_at: '2026-10-20T12:00:00Z',
    last_error: null,
    enabled: true,
    release_url: releaseUrl,
    ...overrides,
  }
}

beforeEach(() => {
  window.localStorage.clear()
})

afterEach(() => {
  vi.unstubAllGlobals()
  window.localStorage.clear()
})

describe('UpdateBanner', () => {
  it('appears above main in the shell when a newer release exists', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/updates', body: updateStatus() },
    ])

    renderWithProviders(<App />, { route: '/settings/account' })

    const banner = await screen.findByTestId('update-banner')
    expect(banner).toHaveAttribute('role', 'status')
    expect(
      within(banner).getByRole('heading', { name: /newer release is available/i }),
    ).toBeVisible()
    expect(within(banner).getByText(/version 0\.2\.0/)).toBeVisible()
    expect(within(banner).getByText(/manual rebuild and redeploy/i)).toBeVisible()
    expect(within(banner).getByText(/never updates itself/i)).toBeVisible()

    const main = screen.getByRole('main')
    expect(banner.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('links to the server-provided release page safely in a new tab', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/updates', body: updateStatus() },
    ])

    renderWithProviders(<App />, { route: '/settings/account' })

    const banner = await screen.findByTestId('update-banner')
    const link = within(banner).getByRole('link', { name: /release notes/i })
    expect(link).toHaveAttribute('href', releaseUrl)
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noreferrer')
  })

  it('does not link an unsafe release_url', async () => {
    expect(safeReleaseUrl(releaseUrl)).toBe(releaseUrl)
    expect(safeReleaseUrl('javascript:alert(1)')).toBeNull()
    expect(safeReleaseUrl('http://example.test/v0.2.0')).toBeNull()
    expect(safeReleaseUrl('/relative')).toBeNull()
    expect(safeReleaseUrl('not a url')).toBeNull()
    expect(safeReleaseUrl(null)).toBeNull()

    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/updates', body: updateStatus({ release_url: 'javascript:alert(1)' }) },
    ])

    renderWithProviders(<App />, { route: '/settings/account' })

    const banner = await screen.findByTestId('update-banner')
    expect(within(banner).queryByRole('link')).not.toBeInTheDocument()
  })

  it('hides for the dismissed version and remembers it per device', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/updates', body: updateStatus() },
    ])

    const first = renderWithProviders(<App />, { route: '/settings/account' })

    const banner = await screen.findByTestId('update-banner')
    fireEvent.click(within(banner).getByRole('button', { name: /dismiss update notice/i }))

    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument()
    expect(window.localStorage.getItem(UPDATE_NOTICE_DISMISSED_KEY)).toBe('0.2.0')

    // A reload on this device stays dismissed.
    first.unmount()
    renderWithProviders(<App />, { route: '/settings/account' })
    expect(await screen.findByRole('heading', { name: 'Account' })).toBeInTheDocument()
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument()
  })

  it('shows again for a newer release than the dismissed one', async () => {
    window.localStorage.setItem(UPDATE_NOTICE_DISMISSED_KEY, '0.2.0')

    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/updates',
        body: updateStatus({
          latest_version: '0.3.0',
          release_url: 'https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.3.0',
        }),
      },
    ])

    renderWithProviders(<UpdateBanner />)

    const banner = await screen.findByTestId('update-banner')
    expect(within(banner).getByText(/version 0\.3\.0/)).toBeVisible()
  })

  it('stays hidden when the app is up to date', async () => {
    installMockFetch([
      {
        path: '/updates',
        body: updateStatus({ latest_version: '0.1.0', update_available: false }),
      },
    ])

    const queryClient = createTestQueryClient()
    renderWithProviders(<UpdateBanner />, { queryClient })

    await waitFor(() => expect(queryClient.getQueryState(['updates'])?.status).toBe('success'))
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument()
  })

  it('stays hidden when the status request fails', async () => {
    installMockFetch([
      {
        path: '/updates',
        status: 500,
        body: { error: { code: 'INTERNAL_ERROR', message: 'Nope', details: {} } },
      },
    ])

    const queryClient = createTestQueryClient()
    renderWithProviders(<UpdateBanner />, { queryClient })

    await waitFor(() => expect(queryClient.getQueryState(['updates'])?.status).toBe('error'))
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument()
  })
})
