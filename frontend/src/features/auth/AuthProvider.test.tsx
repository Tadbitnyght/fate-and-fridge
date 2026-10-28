import { useQuery } from '@tanstack/react-query'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { getCsrfToken, setCsrfToken } from '../../api/client'
import type { GroceryPreferences } from '../../api/groceries'
import type { SetupStatus } from '../../api/setup'
import { installMockFetch, type MockResponse } from '../../test/mockFetch'
import { createTestQueryClient, renderWithProviders } from '../../test/utils'
import { groceriesListQueryKey, groceriesPreferencesQueryKey } from '../groceries/useGroceries'
import { recipesQueryKey } from '../recipes/useRecipes'
import { setupStatusQueryKey, useSetupStatus } from '../setup/useSetupStatus'
import { statisticsOverviewQueryKey } from '../statistics/useStatistics'
import { AuthProvider } from './AuthProvider'
import { useAuth } from './AuthContext'
import { sessionQueryKey } from './session'

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

const jonesSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 2, username: 'jones', role: 'member' },
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
  const {
    status,
    session,
    user,
    permissions,
    error,
    login,
    logout,
    changePassword,
    refetchSession,
  } = useAuth()
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
      <button type="button" onClick={() => void refetchSession().catch(() => undefined)}>
        refresh session
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

/**
 * User-scoped reads that used to survive logout in the shared `QueryClient`
 * (BUG-007). The grocery range is deliberately distinctive so a stale render is
 * unmistakable.
 */
const smithPreferences = {
  hide_checked: true,
  range_preset: 'next_3_days' as const,
  range_start: '2026-10-01',
  range_days: 3,
}

function seedUserCache(queryClient: ReturnType<typeof createTestQueryClient>): void {
  queryClient.setQueryData(groceriesPreferencesQueryKey, smithPreferences)
  queryClient.setQueryData(groceriesListQueryKey, {
    range: { preset: 'next_3_days', start: '2026-10-01', end: '2026-10-03', day_count: 3 },
    departments: [],
  })
  queryClient.setQueryData([...recipesQueryKey, 'list', { query: '' }], { items: [], total: 0 })
  queryClient.setQueryData(statisticsOverviewQueryKey, { meal_count_month: 12 })
}

/**
 * Renders the cached grocery range and deliberately never resolves its own
 * refetch, so whatever it displays can only have come from the shared cache —
 * exactly what the next user would see before a real refetch could land.
 */
function GroceriesProbe() {
  const preferences = useQuery({
    queryKey: groceriesPreferencesQueryKey,
    // Never resolves: the rendered value can only come from the shared cache.
    queryFn: () => new Promise<GroceryPreferences>(() => undefined),
    retry: false,
  })
  return (
    <span data-testid="groceries">
      {preferences.data === undefined
        ? 'none'
        : `${preferences.data.range_start}:${preferences.data.range_days}`}
    </span>
  )
}

/** A protected page: mounted only while signed in, like the router guard. */
function ProtectedGroceriesProbe() {
  const { status } = useAuth()
  if (status !== 'authenticated' && status !== 'single_user') {
    return null
  }
  return <GroceriesProbe />
}

const setupStatus: SetupStatus = {
  setup_completed: true,
  auth_mode: 'multiuser',
  recovery_key_present: true,
}

/**
 * Mirrors `RequireSession`: the setup-status query gates every route and stays
 * mounted for the whole authenticated lifetime. The router renders either the
 * shell, the loading screen, or the `<Navigate to="/login">` for a lost session.
 */
function RouterGateProbe({
  onLogoutResult,
}: {
  onLogoutResult?: (result: 'ok' | 'failed') => void
}) {
  const { status, logout } = useAuth()
  const setup = useSetupStatus()
  const screen =
    setup.isPending || status === 'loading'
      ? 'loading'
      : status === 'unauthenticated'
        ? 'login'
        : 'app'
  return (
    <div>
      <span data-testid="screen">{screen}</span>
      <span data-testid="setup">{setup.isPending ? 'pending' : 'ready'}</span>
      <button
        type="button"
        onClick={() =>
          void logout().then(
            () => onLogoutResult?.('ok'),
            () => onLogoutResult?.('failed'),
          )
        }
      >
        sign out
      </button>
    </div>
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

/**
 * BUG-007: the app-wide `QueryClient` outlives a session, so every
 * user-scoped read must be dropped whenever the identity behind the cache
 * changes. These tests seed the cache the way the groceries, recipes and
 * statistics screens would and then assert that a logout, a 401-driven session
 * loss, or a login as another user leaves no previous-user data behind.
 */
describe('AuthProvider query cache isolation (BUG-007)', () => {
  it('clears every user-scoped cache entry on logout', async () => {
    const queryClient = createTestQueryClient()
    seedUserCache(queryClient)

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

    renderWithProviders(
      <AuthProvider>
        <Probe />
        <ProtectedGroceriesProbe />
      </AuthProvider>,
      { queryClient },
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('groceries')).toHaveTextContent('2026-10-01:3')

    fireEvent.click(screen.getByRole('button', { name: 'logout' }))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))

    // The protected page is gone, and only the explicitly nulled session entry
    // still holds a value: groceries, recipes and statistics are all dropped.
    expect(screen.queryByTestId('groceries')).not.toBeInTheDocument()
    const entriesWithData = queryClient
      .getQueryCache()
      .getAll()
      .filter((query) => query.state.data !== undefined)
    expect(entriesWithData.map((query) => query.queryKey)).toEqual([[...sessionQueryKey]])
    expect(queryClient.getQueryData(sessionQueryKey)).toBeNull()
    expect(queryClient.getQueryData(groceriesPreferencesQueryKey)).toBeUndefined()
    expect(queryClient.getQueryData(groceriesListQueryKey)).toBeUndefined()
    expect(queryClient.getQueryData([...recipesQueryKey, 'list', { query: '' }])).toBeUndefined()
    expect(queryClient.getQueryData(statisticsOverviewQueryKey)).toBeUndefined()
  })

  it('does not render the previous user’s groceries to the next user who logs in', async () => {
    const queryClient = createTestQueryClient()
    seedUserCache(queryClient)

    // The stub represents "the next user": logout revokes the first session and
    // the following login resolves to a different identity.
    let session: unknown = multiuserSession
    installMockFetch([
      {
        path: '/auth/session',
        respond: () => (session === null ? authRequired() : { status: 200, body: session }),
      },
      {
        path: '/auth/logout',
        method: 'POST',
        respond: () => {
          session = null
          return { status: 204 }
        },
      },
      {
        path: '/auth/login',
        method: 'POST',
        respond: () => {
          session = jonesSession
          return { status: 200, body: jonesSession, headers: { 'X-CSRF-TOKEN': 'token-2' } }
        },
      },
    ])

    renderWithProviders(
      <AuthProvider>
        <Probe />
        <ProtectedGroceriesProbe />
      </AuthProvider>,
      { queryClient },
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('groceries')).toHaveTextContent('2026-10-01:3')

    fireEvent.click(screen.getByRole('button', { name: 'logout' }))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))

    fireEvent.click(screen.getByRole('button', { name: 'login' }))
    await waitFor(() => expect(screen.getByTestId('user')).toHaveTextContent('jones'))

    // The grocery page remounts for the new user with nothing cached: it shows
    // its loading state, never the first user's remembered range.
    expect(await screen.findByTestId('groceries')).toHaveTextContent('none')
    expect(screen.queryByText(/2026-10-01:3/)).not.toBeInTheDocument()
    expect(queryClient.getQueryData(groceriesPreferencesQueryKey)).toBeUndefined()
  })

  it('clears the cache when a 401 session refresh loses the session', async () => {
    const queryClient = createTestQueryClient()
    seedUserCache(queryClient)

    let authenticated = true
    installMockFetch([
      {
        path: '/auth/session',
        respond: () => (authenticated ? { status: 200, body: multiuserSession } : authRequired()),
      },
    ])

    renderWithProviders(
      <AuthProvider>
        <Probe />
        <ProtectedGroceriesProbe />
      </AuthProvider>,
      { queryClient },
    )

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('authenticated'))
    expect(screen.getByTestId('groceries')).toHaveTextContent('2026-10-01:3')

    // A revoked/expired session surfaces as a 401 on the next session read; the
    // live-sync teardown and any manual refresh take exactly this path.
    authenticated = false
    fireEvent.click(screen.getByRole('button', { name: 'refresh session' }))

    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('unauthenticated'))
    await waitFor(() =>
      expect(queryClient.getQueryData(groceriesPreferencesQueryKey)).toBeUndefined(),
    )
    expect(screen.queryByTestId('groceries')).not.toBeInTheDocument()
    expect(queryClient.getQueryData(statisticsOverviewQueryKey)).toBeUndefined()
  })

  /**
   * Regression (CI run 40145178694): the sweep must not destroy the router's own
   * prerequisite query. `removeQueries` on a query that still has a mounted
   * observer destroys the cache entry and cancels its fetch without telling the
   * observer, so the gate re-renders against a fresh, permanently pending query
   * `/settings/users` stays on the "Loading Fate & Fridge…" screen instead of
   * redirecting to `/login`.
   */
  it('reaches /login after sign-out while the router gate is mounted', async () => {
    const queryClient = createTestQueryClient()

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
      { path: '/setup/status', body: setupStatus },
    ])

    renderWithProviders(
      <AuthProvider>
        <RouterGateProbe />
      </AuthProvider>,
      { queryClient },
    )

    await waitFor(() => expect(screen.getByTestId('screen')).toHaveTextContent('app'))

    fireEvent.click(screen.getByRole('button', { name: 'sign out' }))

    await waitFor(() => expect(screen.getByTestId('screen')).toHaveTextContent('login'))
    // The household setup status is routing plumbing shared by every identity
    // not user data, so it legitimately survives the sweep.
    expect(screen.getByTestId('setup')).toHaveTextContent('ready')
    expect(queryClient.getQueryData(setupStatusQueryKey)).toEqual(setupStatus)
  })

  /**
   * A sign-out that already succeeded server-side must never be reported as a
   * failure by a follow-up read: an awaited, rejecting session refetch makes
   * `logout` reject ("Sign-out failed…") and leaves the session query in an
   * error state, which the router renders instead of `/login`.
   */
  it('completes sign-out even when a post-logout session re-read fails', async () => {
    const queryClient = createTestQueryClient()
    const logoutResults: string[] = []

    let authenticated = true
    installMockFetch([
      {
        path: '/auth/session',
        respond: () =>
          authenticated
            ? { status: 200, body: multiuserSession }
            : {
                status: 500,
                body: {
                  error: { code: 'INTERNAL_ERROR', message: 'Session read failed.', details: {} },
                },
              },
      },
      {
        path: '/auth/logout',
        method: 'POST',
        respond: () => {
          authenticated = false
          return { status: 204 }
        },
      },
      { path: '/setup/status', body: setupStatus },
    ])

    renderWithProviders(
      <AuthProvider>
        <RouterGateProbe onLogoutResult={(result) => logoutResults.push(result)} />
      </AuthProvider>,
      { queryClient },
    )

    await waitFor(() => expect(screen.getByTestId('screen')).toHaveTextContent('app'))

    fireEvent.click(screen.getByRole('button', { name: 'sign out' }))

    await waitFor(() => expect(logoutResults).toEqual(['ok']))
    await waitFor(() => expect(screen.getByTestId('screen')).toHaveTextContent('login'))
    expect(queryClient.getQueryState(sessionQueryKey)?.error).toBeNull()
  })
})
