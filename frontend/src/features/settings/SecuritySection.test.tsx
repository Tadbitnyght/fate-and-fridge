import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch, type MockFetch } from '../../test/mockFetch'
import {
  adminSession,
  securitySettings,
  setupCompleteStatus,
  singleUserSession,
  singleUserSetupStatus,
} from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'

function patchCalls(mock: MockFetch): Array<Record<string, unknown>> {
  return mock.fetchMock.mock.calls
    .filter(
      ([url, init]) =>
        String(url).endsWith('/settings/security') &&
        (init as RequestInit | undefined)?.method === 'PATCH',
    )
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>)
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('security settings section', () => {
  it('requires the lockout confirmation before enabling the allowlist', async () => {
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/settings/security', body: securitySettings },
      {
        path: '/settings/security',
        method: 'PATCH',
        body: {
          ip_allowlist_enabled: true,
          ip_allowlist_ranges: ['192.168.1.0/24'],
          trusted_proxies: [],
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/security' })

    fireEvent.click(await screen.findByLabelText(/enable the ip allowlist/i))
    expect(screen.getByText(/lock out your household/i)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Allowed addresses and ranges'), {
      target: { value: '192.168.1.0/24' },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Save security settings' }))
    expect(
      (await screen.findAllByText(/confirm that you understand the lockout risk/i)).length,
    ).toBeGreaterThan(0)
    expect(patchCalls(mock)).toHaveLength(0)

    fireEvent.click(screen.getByLabelText(/I understand that a misconfigured allowlist/i))
    fireEvent.click(screen.getByRole('button', { name: 'Save security settings' }))

    expect(await screen.findByText('Security settings saved.')).toBeInTheDocument()
    expect(patchCalls(mock)).toEqual([
      {
        ip_allowlist_enabled: true,
        ip_allowlist_ranges: ['192.168.1.0/24'],
        trusted_proxies: [],
        confirmed: true,
      },
    ])
  })

  it('rejects invalid ranges client-side', async () => {
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/settings/security', body: securitySettings },
    ])

    renderWithProviders(<App />, { route: '/settings/security' })

    fireEvent.click(await screen.findByLabelText(/enable the ip allowlist/i))
    fireEvent.change(screen.getByLabelText('Allowed addresses and ranges'), {
      target: { value: '010.0.0.1' },
    })
    fireEvent.click(screen.getByLabelText(/I understand that a misconfigured allowlist/i))
    fireEvent.click(screen.getByRole('button', { name: 'Save security settings' }))

    expect((await screen.findAllByText(/Line 1/)).length).toBeGreaterThan(0)
    expect(patchCalls(mock)).toHaveLength(0)
  })

  it('maps server field errors back to the ranges editor', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/settings/security', body: securitySettings },
      {
        path: '/settings/security',
        method: 'PATCH',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request body failed validation.',
            details: {
              field_errors: {
                ip_allowlist_ranges: [
                  "The caller's address must be covered by the allowlist ranges to avoid locking out this session.",
                ],
              },
            },
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/security' })

    fireEvent.click(await screen.findByLabelText(/enable the ip allowlist/i))
    fireEvent.change(screen.getByLabelText('Allowed addresses and ranges'), {
      target: { value: '203.0.113.0/24' },
    })
    fireEvent.click(screen.getByLabelText(/I understand that a misconfigured allowlist/i))
    fireEvent.click(screen.getByRole('button', { name: 'Save security settings' }))

    expect(
      (await screen.findAllByText(/must be covered by the allowlist ranges/i)).length,
    ).toBeGreaterThan(0)
  })

  it('clears errors while the form is edited', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/settings/security', body: securitySettings },
    ])

    renderWithProviders(<App />, { route: '/settings/security' })

    fireEvent.click(await screen.findByLabelText(/enable the ip allowlist/i))
    fireEvent.click(screen.getByRole('button', { name: 'Save security settings' }))

    expect(
      (await screen.findAllByText(/confirm that you understand the lockout risk/i)).length,
    ).toBeGreaterThan(0)
    fireEvent.click(screen.getByLabelText(/I understand that a misconfigured allowlist/i))
    expect(
      screen.queryByText(/confirm that you understand the lockout risk/i),
    ).not.toBeInTheDocument()

    const rangesInput = screen.getByLabelText('Allowed addresses and ranges')
    fireEvent.change(rangesInput, { target: { value: '010.0.0.1' } })
    expect(await screen.findByText(/Line 1/)).toBeInTheDocument()

    fireEvent.change(rangesInput, { target: { value: '192.168.1.0/24' } })
    expect(screen.queryByText(/Line 1/)).not.toBeInTheDocument()
  })

  it('switches a single-user household to multiuser and seeds the session', async () => {
    let switched = false
    const mock = installMockFetch([
      {
        path: '/setup/status',
        respond: () => ({
          status: 200,
          body: switched ? setupCompleteStatus : singleUserSetupStatus,
        }),
      },
      {
        path: '/auth/session',
        respond: () => ({ status: 200, body: switched ? adminSession : singleUserSession }),
      },
      { path: '/settings/security', body: securitySettings },
      {
        path: '/settings/auth-mode',
        method: 'POST',
        respond: () => {
          switched = true
          return { status: 200, body: adminSession }
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/security' })

    expect(await screen.findByRole('heading', { name: 'Switch to multiuser' })).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Admin username'), { target: { value: 'root' } })
    fireEvent.change(screen.getByLabelText('Admin password'), {
      target: { value: 'correct horse battery staple' },
    })
    fireEvent.change(screen.getByLabelText('Confirm admin password'), {
      target: { value: 'correct horse battery staple' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Switch to multiuser' }))

    // The returned session is seeded: the shell now knows the Admin.
    expect(await screen.findByText('root')).toBeInTheDocument()

    const authModeCalls = mock.fetchMock.mock.calls.filter(([url]) =>
      String(url).endsWith('/settings/auth-mode'),
    )
    expect(authModeCalls).toHaveLength(1)
    expect(JSON.parse(String((authModeCalls[0][1] as RequestInit).body))).toEqual({
      auth_mode: 'multiuser',
      admin: { username: 'root', password: 'correct horse battery staple' },
    })
    await waitFor(() =>
      expect(screen.getByText(/downgrading to single-user is not supported/i)).toBeInTheDocument(),
    )
  })
})
