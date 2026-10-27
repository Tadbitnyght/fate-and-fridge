import { createContext, useContext } from 'react'

import type { ChangePasswordRequest, LoginRequest, Session, SessionUser } from '../../api/auth'
import type { SessionStatus } from './session'

export interface AuthContextValue {
  status: SessionStatus
  session: Session | null
  user: SessionUser | null
  permissions: string[]
  /** Set when the session query failed (network error, setup gating, …). */
  error: Error | null
  refetchSession: () => Promise<void>
  login: (request: LoginRequest) => Promise<void>
  logout: () => Promise<void>
  changePassword: (request: ChangePasswordRequest) => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext)
  if (value === null) {
    throw new Error('useAuth must be used inside an AuthProvider')
  }
  return value
}
