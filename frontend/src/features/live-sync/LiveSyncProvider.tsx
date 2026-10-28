import type { HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'

import { useAuth } from '../auth/AuthContext'
import { LiveSyncContext, type LiveSyncStatus } from './LiveSyncContext'
import { retryDelayForAttempt, safeBuildLiveSyncConnection } from './liveSyncConnection'
import {
  invalidateAllLiveSyncQueries,
  invalidateForEvent,
  liveSyncEventType,
  type LiveSyncEvent,
} from './liveSyncKeys'

/**
 * One SignalR connection per tab, mounted inside `AuthProvider` so it can use
 * the session and the shared `QueryClient`. It connects only for an
 * authenticated multiuser session or the single-user system actor, runs a full
 * invalidation pass on every (re)connect, forces a sign-out on
 * `session.revoked`, and closes on logout.
 */
export function LiveSyncProvider({ children }: { children: ReactNode }) {
  const { status: sessionStatus, refetchSession } = useAuth()
  const queryClient = useQueryClient()
  const [connectionStatus, setConnectionStatus] = useState<LiveSyncStatus>('disconnected')
  const connectionRef = useRef<HubConnection | null>(null)

  const shouldConnect = sessionStatus === 'authenticated' || sessionStatus === 'single_user'
  // While the session is inactive the socket is down by definition, so the
  // exposed state is derived instead of set from the effect body.
  const status: LiveSyncStatus = shouldConnect ? connectionStatus : 'disconnected'

  useEffect(() => {
    if (!shouldConnect) {
      return
    }

    let stopped = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined
    const connection = safeBuildLiveSyncConnection()
    if (connection === null) {
      return
    }
    connectionRef.current = connection

    const handleEvent = (event: LiveSyncEvent): void => {
      if (event.type === liveSyncEventType.sessionRevoked) {
        // The session is gone: stop reconnecting and force a sign-out. The
        // session query 401s, which flips `shouldConnect` off and tears down.
        stopped = true
        setConnectionStatus('disconnected')
        void connection.stop()
        void refetchSession()
        return
      }
      void invalidateForEvent(queryClient, event)
    }

    connection.on('event', handleEvent)
    connection.onreconnecting(() => {
      if (!stopped) {
        setConnectionStatus('reconnecting')
      }
    })
    connection.onreconnected(() => {
      if (stopped) {
        return
      }
      setConnectionStatus('connected')
      // Missed events are reconciled by a full pass on every (re)connect.
      void invalidateAllLiveSyncQueries(queryClient)
    })
    connection.onclose(() => {
      if (!stopped) {
        setConnectionStatus('disconnected')
      }
    })

    let attempt = 0
    const connect = async (): Promise<void> => {
      if (stopped) {
        return
      }
      try {
        await connection.start()
        if (stopped) {
          await connection.stop()
          return
        }
        setConnectionStatus('connected')
        void invalidateAllLiveSyncQueries(queryClient)
      } catch {
        if (stopped) {
          return
        }
        // Initial-connect failures are not covered by withAutomaticReconnect
        // so retry with the same backoff until the backend is reachable.
        setConnectionStatus('disconnected')
        attempt += 1
        retryTimer = setTimeout(() => void connect(), retryDelayForAttempt(attempt))
      }
    }

    const begin = async (): Promise<void> => {
      setConnectionStatus('connecting')
      await connect()
    }
    void begin()

    return () => {
      stopped = true
      if (retryTimer !== undefined) {
        clearTimeout(retryTimer)
      }
      connection.off('event', handleEvent)
      void connection.stop()
      if (connectionRef.current === connection) {
        connectionRef.current = null
      }
    }
  }, [shouldConnect, queryClient, refetchSession])

  const value = useMemo(() => ({ status }), [status])

  return <LiveSyncContext.Provider value={value}>{children}</LiveSyncContext.Provider>
}
