import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { getCsrfToken, setCsrfToken } from '../../api/client'
import { installMockFetch, type MockResponse } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'
import { AuthProvider } from './AuthProvider'
import { useAuth } from './AuthContext'

const multiuserSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'smith', role: 'admin' },
  permissions: ['calendar.view', 'calendar.edit'],
  must_change_password: false,
}

const singleUserSession = {
  authenticated: true,
  auth_mode: 'single_user',
  user: null,
  permissions: ['calendar.view'],
  must_change_password: false,
}

function authRequired(): MockResponse {
  return {
    status: 401,
    body: {
      error: { code: 'AUTH_REQUIRED', message: 'A valid session is required.', details: {} },
    },
  }
}

function Probe() {
  const { status, session, user, permissions, error, login, logout, changePassword } = useAuth()
  return (
    <div>
      <span data-testid="status">{status}</span>
      <span data-testid="user">{user?.username ?? 'none'}</span>
      <span data-testid="permissions">{permissions.join(',')}</span>
      <span data-testid="must-change">{session?.must_change_password ? 'yes' : 'no'}</span>
      <span data-testid="error">{error?.message ?? 'none'}</span>
      <button
        type="button"
        onClick={() => void login({ username: 'smith', password: 'secret' }).catch(() => undefined)}
      >
        login
      </button>
      <button type="button" onClick={() => void logout().catch(() => undefined)}>
        logout
      </button>
      <button
        type="button"
        onClick={() =>
          void changePassword({
            current_password: 'old-password',
            new_password: 'new-password',
          }).catch(() => undefined)
        }
      >
        change
      </button>
    </div>
  )
}

function renderAuth() {
  return renderWithProviders(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
  setCsrfToken(null)
})

describe('AuthProvider', () => {
  it('starts loading and resolves an authenticated multiuser session', async () => {
    installMockFetch([{ path: '/auth/session', body: multiuserSession }])

    renderAuth()

    expect(screen.getByTestId('status')).toHaveTextContent('loading')
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('user')).toHaveTextContent('smith')
    expect(screen.getByTestId('permissions')).toHaveTextContent('calendar.view,calendar.edit')
  })

  it('treats a 401 session response as unauthenticated', async () => {
    installMockFetch([
      {
        path: '/auth/session',
        status: 401,
        body: { error: { code: 'AUTH_REQUIRED', message: 'No session', details: {} } },
      },
    ])

    renderAuth()

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))
    expect(screen.getByTestId('user')).toHaveTextContent('none')
    expect(screen.getByTestId('error')).toHaveTextContent('none')
  })

  it('resolves single-user households without a user record', async () => {
    installMockFetch([{ path: '/auth/session', body: singleUserSession }])

    renderAuth()

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('single_user'))
    expect(screen.getByTestId('user')).toHaveTextContent('none')
  })

  it('surfaces non-401 session failures', async () => {
    installMockFetch([
      {
        path: '/auth/session',
        status: 409,
        body: {
          error: { code: 'SETUP_REQUIRED', message: 'Setup is not complete.', details: {} },
        },
      },
    ])

    renderAuth()

    await waitFor(() =>
      expect(screen.getByTestId('error')).toHaveTextContent('Setup is not complete.'),
    )
  })

  it('refreshes the session after a successful login', async () => {
    let authenticated = false
    const mock = installMockFetch([
      {
        path: '/auth/session',
        respond: () => (authenticated ? { status: 200, body: multiuserSession } : authRequired()),
      },
      {
        path: '/auth/login',
        method: 'POST',
        respond: () => {
          authenticated = true
          return { status: 200, body: multiuserSession, headers: { 'X-CSRF-TOKEN': 'token-1' } }
        },
      },
    ])

    renderAuth()
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))

    fireEvent.click(screen.getByRole('button', { name: 'login' }))

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('user')).toHaveTextContent('smith')
    expect(mock.callsFor('/auth/session')).toBeGreaterThanOrEqual(2)
  })

  it('refreshes the session after logout', async () => {
    let authenticated = true
    installMockFetch([
      {
        path: '/auth/session',
        respond: () => (authenticated ? { status: 200, body: multiuserSession } : authRequired()),
      },
      {
        path: '/auth/logout',
        method: 'POST',
        respond: () => {
          authenticated = false
          return { status: 204 }
        },
      },
    ])

    renderAuth()
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))

    setCsrfToken('token-1')
    fireEvent.click(screen.getByRole('button', { name: 'logout' }))

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))
    expect(getCsrfToken()).toBeNull()
  })

  it('refreshes the session after a password change', async () => {
    let mustChange = true
    installMockFetch([
      {
        path: '/auth/session',
        respond: () => ({
          status: 200,
          body: { ...multiuserSession, must_change_password: mustChange },
        }),
      },
      {
        path: '/auth/change-password',
        method: 'POST',
        respond: () => {
          mustChange = false
          return { status: 204 }
        },
      },
    ])

    renderAuth()
    await waitFor(() => expect(screen.getByTestId('must-change')).toHaveTextContent('yes'))

    fireEvent.click(screen.getByRole('button', { name: 'change' }))

    await waitFor(() => expect(screen.getByTestId('must-change')).toHaveTextContent('no'))
  })
})
