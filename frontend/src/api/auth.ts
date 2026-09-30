import { ApiError, apiRequest, setCsrfToken } from './client'

export type AuthMode = 'multiuser' | 'single_user'

export type UserRole = 'admin' | 'member'

export interface SessionUser {
  id: number
  username: string
  role: UserRole
}

/** Payload of `GET /auth/session`, `POST /auth/login`, and setup completion. */
export interface Session {
  authenticated: boolean
  auth_mode: AuthMode
  user: SessionUser | null
  permissions: string[]
  must_change_password: boolean
}

export interface LoginRequest {
  username: string
  password: string
}

export interface ChangePasswordRequest {
  current_password: string
  new_password: string
}

/**
 * `GET /auth/session`. A `401 AUTH_REQUIRED` is a normal unauthenticated
 * state, not an error; anything else (for example `409 SETUP_REQUIRED` for a
 * household that has not been configured yet) is surfaced to the caller.
 */
export async function fetchSession(): Promise<Session | null> {
  try {
    return await apiRequest<Session>('/auth/session')
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      return null
    }
    throw error
  }
}

export function login(request: LoginRequest): Promise<Session> {
  return apiRequest<Session>('/auth/login', { method: 'POST', body: request })
}

export async function logout(): Promise<void> {
  await apiRequest<void>('/auth/logout', { method: 'POST' })
  // The session is gone; do not reuse the old antiforgery token.
  setCsrfToken(null)
}

export function changePassword(request: ChangePasswordRequest): Promise<void> {
  return apiRequest<void>('/auth/change-password', { method: 'POST', body: request })
}
