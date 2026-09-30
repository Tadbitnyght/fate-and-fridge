import { apiRequest } from './client'
import type { UserRole } from './auth'

/** `GET /users` entry (docs/API.md §6/§21). */
export interface UserSummary {
  id: number
  username: string
  role: UserRole
  is_active: boolean
  must_change_password: boolean
  last_login_at: string | null
  /** Effective permissions (Admins hold every key). */
  permissions: string[]
  /** Present exactly once when the account was created without a password. */
  temporary_password?: string
}

export interface CreateUserRequest {
  username: string
  role?: UserRole
  /** Omit to have the server generate a one-time temporary password. */
  password?: string
}

export interface UpdateUserRequest {
  username?: string
  role?: UserRole
}

export function fetchUsers(): Promise<UserSummary[]> {
  return apiRequest<UserSummary[]>('/users')
}

export function createUser(request: CreateUserRequest): Promise<UserSummary> {
  return apiRequest<UserSummary>('/users', { method: 'POST', body: request })
}

export function updateUser(id: number, request: UpdateUserRequest): Promise<UserSummary> {
  return apiRequest<UserSummary>(`/users/${id}`, { method: 'PATCH', body: request })
}

/** Replaces the member's granted rows; edit implies view (docs/API.md §6). */
export function updateUserPermissions(id: number, permissions: string[]): Promise<UserSummary> {
  return apiRequest<UserSummary>(`/users/${id}/permissions`, {
    method: 'PATCH',
    body: { permissions },
  })
}

export function disableUser(id: number): Promise<UserSummary> {
  return apiRequest<UserSummary>(`/users/${id}/disable`, { method: 'POST' })
}

export function enableUser(id: number): Promise<UserSummary> {
  return apiRequest<UserSummary>(`/users/${id}/enable`, { method: 'POST' })
}

/** Returns the one-time temporary password exactly once. */
export function resetUserPassword(id: number): Promise<{ temporary_password: string }> {
  return apiRequest<{ temporary_password: string }>(`/users/${id}/reset-password`, {
    method: 'POST',
  })
}
