import { useAuth } from '../auth/AuthContext'

export interface AdminAccess {
  /** Admin in multiuser mode, or the single-user system actor. */
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: the server enforces authorization on every route
 * (docs/SECURITY.md §4). This hook must never be the sole gate for an action.
 */
export function useAdminAccess(): AdminAccess {
  const { status, user } = useAuth()
  const isSingleUser = status === 'single_user'
  return {
    isAdmin: isSingleUser || (status === 'authenticated' && user?.role === 'admin'),
    isSingleUser,
  }
}
