import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from './App'
import type { MockResponse } from './test/mockFetch'
import { installMockFetch } from './test/mockFetch'
import { renderWithProviders } from './test/utils'

const setupIncomplete = { setup_completed: false, auth_mode: null, recovery_key_present: false }
const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

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

function sessionRequired(): MockResponse {
  return {
    status: 401,
    body: {
      error: { code: 'AUTH_REQUIRED', message: 'A valid session is required.', details: {} },
    },
  }
}

function sessionBeforeSetup(): MockResponse {
  return {
    status: 409,
    body: { error: { code: 'SETUP_REQUIRED', message: 'Setup is not complete.', details: {} } },
  }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('App routing and guards', () => {
  it('sends visitors to the setup wizard while setup is incomplete', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupIncomplete },
      { path: '/auth/session', ...sessionBeforeSetup() },
    ])

    renderWithProviders(<App />, { route: '/' })

    expect(
      await screen.findByRole('heading', { level: 1, name: /welcome to fate & fridge/i }),
    ).toBeInTheDocument()
    expect(screen.getByText('Installation mode')).toBeInTheDocument()
    expect(screen.getByLabelText(/single user/i)).toBeInTheDocument()
    expect(screen.getByLabelText(/multiuser/i)).toBeInTheDocument()
  })

  it('redirects unauthenticated multiuser visitors to the login page', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', ...sessionRequired() },
    ])

    renderWithProviders(<App />, { route: '/' })

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByLabelText('Username')).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
  })

  it('renders the shell with navigation for an authenticated session', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', body: multiuserSession },
      { path: '/health/db', body: { status: 'ok', database: 'connected' } },
      {
        path: '/health',
        body: { status: 'ok', app: 'Fate & Fridge', version: '1.0.0', environment: 'test' },
      },
    ])

    renderWithProviders(<App />, { route: '/' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    const nav = screen.getByRole('navigation', { name: 'Primary' })
    for (const label of [
      'Calendar',
      'Dashboard',
      'Recipes',
      'Groceries',
      'Statistics',
      'Settings',
    ]) {
      expect(screen.getByRole('link', { name: label })).toBeInTheDocument()
    }
    expect(nav).toBeInTheDocument()
    expect(screen.getByText('smith')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /sign out/i })).toBeInTheDocument()
  })

  it('marks the active navigation link for the current route', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', body: multiuserSession },
    ])

    renderWithProviders(<App />, { route: '/recipes' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Recipes' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Recipes' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Calendar' })).not.toHaveAttribute('aria-current')
  })

  it('forces a password change when the session requires it', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', body: { ...multiuserSession, must_change_password: true } },
    ])

    renderWithProviders(<App />, { route: '/' })

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Change password' }),
    ).toBeInTheDocument()
    expect(screen.getByText(/you must set a new password before continuing/i)).toBeInTheDocument()
    expect(screen.getByLabelText('Current password')).toBeInTheDocument()
    expect(screen.getByLabelText('New password')).toBeInTheDocument()
  })

  it('lets single-user households straight into the shell', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', body: singleUserSession },
    ])

    renderWithProviders(<App />, { route: '/' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /sign out/i })).not.toBeInTheDocument()
  })

  it('redirects unknown protected paths to the calendar', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', body: multiuserSession },
    ])

    renderWithProviders(<App />, { route: '/does-not-exist' })

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
  })

  it('shows a generic message when login fails with invalid credentials', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      { path: '/auth/session', ...sessionRequired() },
      {
        path: '/auth/login',
        method: 'POST',
        status: 401,
        body: {
          error: {
            code: 'INVALID_CREDENTIALS',
            message: 'Invalid username or password.',
            details: {},
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/login' })

    fireEvent.change(await screen.findByLabelText('Username'), { target: { value: 'smith' } })
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong-password' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The username or password is incorrect.',
    )
  })

  it('opens the shell after a successful login', async () => {
    let authenticated = false
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      {
        path: '/auth/session',
        respond: () =>
          authenticated ? { status: 200, body: multiuserSession } : sessionRequired(),
      },
      {
        path: '/auth/login',
        method: 'POST',
        respond: () => {
          authenticated = true
          return {
            status: 200,
            body: multiuserSession,
            headers: { 'X-CSRF-TOKEN': 'token-1' },
          }
        },
      },
    ])

    renderWithProviders(<App />, { route: '/login' })

    fireEvent.change(await screen.findByLabelText('Username'), { target: { value: 'smith' } })
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByText('smith')).toBeInTheDocument())
  })

  it('returns to the protected route after login from a deep link', async () => {
    let authenticated = false
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      {
        path: '/auth/session',
        respond: () =>
          authenticated ? { status: 200, body: multiuserSession } : sessionRequired(),
      },
      {
        path: '/auth/login',
        method: 'POST',
        respond: () => {
          authenticated = true
          return {
            status: 200,
            body: multiuserSession,
            headers: { 'X-CSRF-TOKEN': 'token-1' },
          }
        },
      },
    ])

    renderWithProviders(<App />, { route: '/recipes' })

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
    await signIn()

    expect(await screen.findByRole('heading', { level: 2, name: 'Recipes' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Recipes' })).toHaveAttribute('aria-current', 'page')
  })

  it.each(['//evil.com', 'https://evil.com', '/\\evil.com', 'javascript:alert(1)'])(
    'falls back to the calendar for a hostile deep link (%s)',
    async (hostile) => {
      let authenticated = false
      installMockFetch([
        { path: '/setup/status', body: setupComplete },
        {
          path: '/auth/session',
          respond: () =>
            authenticated ? { status: 200, body: multiuserSession } : sessionRequired(),
        },
        {
          path: '/auth/login',
          method: 'POST',
          respond: () => {
            authenticated = true
            return {
              status: 200,
              body: multiuserSession,
              headers: { 'X-CSRF-TOKEN': 'token-1' },
            }
          },
        },
      ])

      renderWithProviders(<App />, { route: '/login', routeState: { from: hostile } })

      await signIn()

      expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    },
  )

  it('returns to the intended page after a forced password change', async () => {
    let mustChangePassword = true
    installMockFetch([
      { path: '/setup/status', body: setupComplete },
      {
        path: '/auth/session',
        respond: () => ({
          status: 200,
          body: { ...multiuserSession, must_change_password: mustChangePassword },
        }),
      },
      {
        path: '/auth/change-password',
        method: 'POST',
        respond: () => {
          mustChangePassword = false
          return { status: 204 }
        },
      },
    ])

    renderWithProviders(<App />, { route: '/groceries' })

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Change password' }),
    ).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Current password'), {
      target: { value: 'old-password' },
    })
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'new-password' } })
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByRole('heading', { level: 2, name: 'Groceries' })).toBeInTheDocument()
  })
})

async function signIn(): Promise<void> {
  fireEvent.change(await screen.findByLabelText('Username'), { target: { value: 'smith' } })
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } })
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }))
}
