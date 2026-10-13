import { act, fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AuthProvider } from '../auth/AuthProvider'
import { useAuth } from '../auth/AuthContext'
import { installMockFetch, type MockResponse } from '../../test/mockFetch'
import { createTestQueryClient, renderWithProviders } from '../../test/utils'
import { LiveSyncProvider } from './LiveSyncProvider'
import { retryDelayForAttempt } from './liveSyncConnection'
import { useLiveSync } from './LiveSyncContext'

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

/**
 * The SignalR client is replaced with a recording fake so the provider's
 * wiring (URL/transport, handlers, teardown) is testable without a socket.
 */
const signalr = vi.hoisted(() => {
  interface FakeConnection {
    handlers: Record<string, (...args: unknown[]) => void>
    on: ReturnType<typeof vi.fn>
    off: ReturnType<typeof vi.fn>
    start: ReturnType<typeof vi.fn>
    stop: ReturnType<typeof vi.fn>
    onreconnecting: ReturnType<typeof vi.fn>
    onreconnected: ReturnType<typeof vi.fn>
    onclose: ReturnType<typeof vi.fn>
  }

  const state = {
    withUrlCalls: [] as unknown[][],
    connections: [] as FakeConnection[],
    startImpl: (async () => {}) as () => Promise<void>,
  }

  class HubConnectionBuilder {
    withUrl(url: string, options: unknown) {
      state.withUrlCalls.push([url, options])
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      const handlers: Record<string, (...args: unknown[]) => void> = {}
      const connection: FakeConnection = {
        handlers,
        on: vi.fn((name: string, handler: (...args: unknown[]) => void) => {
          handlers[name] = handler
        }),
        off: vi.fn(),
        start: vi.fn(() => state.startImpl()),
        stop: vi.fn(async () => {}),
        onreconnecting: vi.fn((handler: (...args: unknown[]) => void) => {
          handlers['reconnecting'] = handler
        }),
        onreconnected: vi.fn((handler: (...args: unknown[]) => void) => {
          handlers['reconnected'] = handler
        }),
        onclose: vi.fn((handler: (...args: unknown[]) => void) => {
          handlers['close'] = handler
        }),
      }
      state.connections.push(connection)
      return connection
    }
  }

  return {
    state,
    HubConnectionBuilder,
    HttpTransportType: { WebSockets: 1 },
    LogLevel: { Warning: 3 },
  }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: signalr.HubConnectionBuilder,
  HttpTransportType: signalr.HttpTransportType,
  LogLevel: signalr.LogLevel,
}))

function Probe() {
  const { status } = useLiveSync()
  const { status: sessionStatus, logout } = useAuth()
  return (
    <div>
      <span data-testid="live-status" data-state={status}>
        {status}
      </span>
      <span data-testid="session-status">{sessionStatus}</span>
      <button type="button" onClick={() => void logout().catch(() => undefined)}>
        logout
      </button>
    </div>
  )
}

function renderApp() {
  const queryClient = createTestQueryClient()
  const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries')
  const result = renderWithProviders(
    <AuthProvider>
      <LiveSyncProvider>
        <Probe />
      </LiveSyncProvider>
    </AuthProvider>,
    { queryClient },
  )
  return { ...result, queryClient, invalidateQueries }
}

beforeEach(() => {
  signalr.state.withUrlCalls.length = 0
  signalr.state.connections.length = 0
  signalr.state.startImpl = async () => {}
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('LiveSyncProvider', () => {
  it('connects with skipNegotiation over WebSockets at /hub/live', async () => {
    installMockFetch([{ path: '/auth/session', body: multiuserSession }])

    renderApp()

    await waitFor(() => expect(signalr.state.withUrlCalls).toHaveLength(1))
    expect(signalr.state.withUrlCalls[0]).toEqual([
      '/hub/live',
      { skipNegotiation: true, transport: 1 },
    ])
    await waitFor(() =>
      expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'connected'),
    )
  })

  it('connects as the system actor in single-user mode', async () => {
    installMockFetch([{ path: '/auth/session', body: singleUserSession }])

    renderApp()

    await waitFor(() => expect(signalr.state.connections).toHaveLength(1))
    await waitFor(() =>
      expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'connected'),
    )
  })

  it('does not connect while unauthenticated', async () => {
    installMockFetch([{ path: '/auth/session', ...authRequired() }])

    renderApp()

    await waitFor(() =>
      expect(screen.getByTestId('session-status')).toHaveTextContent('unauthenticated'),
    )
    expect(signalr.state.connections).toHaveLength(0)
    expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'disconnected')
  })

  it('runs the full invalidation pass on connect and again on reconnect', async () => {
    installMockFetch([{ path: '/auth/session', body: multiuserSession }])

    const { invalidateQueries } = renderApp()

    await waitFor(() => expect(signalr.state.connections).toHaveLength(1))
    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledTimes(9))

    invalidateQueries.mockClear()
    act(() => signalr.state.connections[0].handlers['reconnected']())

    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledTimes(9))
    expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'connected')
  })

  it('invalidates the mapped keys when an event arrives', async () => {
    installMockFetch([{ path: '/auth/session', body: multiuserSession }])

    const { invalidateQueries } = renderApp()
    await waitFor(() => expect(signalr.state.connections).toHaveLength(1))
    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledTimes(9))

    invalidateQueries.mockClear()
    act(() =>
      signalr.state.connections[0].handlers['event']({
        type: 'groceries.updated',
        entity: 'groceries',
        actor_user_id: 3,
      }),
    )

    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledTimes(2))
    expect(
      invalidateQueries.mock.calls.map(([options]) => (options as { queryKey: unknown }).queryKey),
    ).toEqual([
      ['groceries', 'list'],
      ['groceries', 'staples'],
    ])
  })

  it('surfaces connecting, reconnecting, and connected states', async () => {
    installMockFetch([{ path: '/auth/session', body: multiuserSession }])

    renderApp()

    await waitFor(() =>
      expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'connected'),
    )

    act(() => signalr.state.connections[0].handlers['reconnecting']())
    expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'reconnecting')

    act(() => signalr.state.connections[0].handlers['reconnected']())
    expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'connected')
  })

  it('tears the connection down on logout', async () => {
    let authenticated = true
    installMockFetch([
      {
        path: '/auth/session',
        respond: () => (authenticated ? { body: multiuserSession } : authRequired()),
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

    renderApp()
    await waitFor(() => expect(signalr.state.connections).toHaveLength(1))
    const connection = signalr.state.connections[0]

    fireEvent.click(screen.getByRole('button', { name: 'logout' }))

    await waitFor(() =>
      expect(screen.getByTestId('session-status')).toHaveTextContent('unauthenticated'),
    )
    await waitFor(() => expect(connection.stop).toHaveBeenCalled())
    expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'disconnected')
  })

  it('forces a sign-out and stops reconnecting on session.revoked', async () => {
    let revoked = false
    installMockFetch([
      {
        path: '/auth/session',
        respond: () => (revoked ? authRequired() : { body: multiuserSession }),
      },
    ])

    renderApp()
    await waitFor(() => expect(signalr.state.connections).toHaveLength(1))
    const connection = signalr.state.connections[0]

    revoked = true
    act(() => connection.handlers['event']({ type: 'session.revoked' }))

    await waitFor(() => expect(connection.stop).toHaveBeenCalled())
    await waitFor(() =>
      expect(screen.getByTestId('session-status')).toHaveTextContent('unauthenticated'),
    )
    expect(screen.getByTestId('live-status')).toHaveAttribute('data-state', 'disconnected')
  })

  it('backs off exponentially, capped at 30s', () => {
    expect(retryDelayForAttempt(1)).toBe(1000)
    expect(retryDelayForAttempt(2)).toBe(2000)
    expect(retryDelayForAttempt(3)).toBe(4000)
    expect(retryDelayForAttempt(20)).toBe(30_000)
  })
})
