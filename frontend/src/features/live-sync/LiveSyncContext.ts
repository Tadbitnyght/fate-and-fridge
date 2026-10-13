import { createContext, useContext } from 'react'

/** Observable connection state exposed through `useLiveSync()`. */
export type LiveSyncStatus = 'connected' | 'connecting' | 'reconnecting' | 'disconnected'

export interface LiveSyncContextValue {
  status: LiveSyncStatus
}

export const LiveSyncContext = createContext<LiveSyncContextValue | null>(null)

export function useLiveSync(): LiveSyncContextValue {
  const value = useContext(LiveSyncContext)
  if (value === null) {
    throw new Error('useLiveSync must be used inside a LiveSyncProvider')
  }
  return value
}
