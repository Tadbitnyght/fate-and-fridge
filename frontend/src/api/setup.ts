import { apiRequest } from './client'
import type { AuthMode, Session } from './auth'

/** Payload of `GET /setup/status` (docs/API.md §21). Public while unconfigured. */
export interface SetupStatus {
  setup_completed: boolean
  auth_mode: AuthMode | null
  recovery_key_present: boolean
}

export type StartupPage = 'calendar' | 'dashboard'

export interface SetupAdmin {
  username: string
  password: string
}

export interface SetupIpAllowlist {
  enabled: boolean
  ranges: string[]
}

/** Body of `POST /setup/complete` (docs/API.md §21). */
export interface CompleteSetupRequest {
  household_name: string
  timezone: string
  week_starts_on: number
  default_diner_count: number
  startup_page: StartupPage
  auth_mode: AuthMode
  /** Required if and only if `auth_mode` is `multiuser`. */
  admin?: SetupAdmin
  ip_allowlist?: SetupIpAllowlist
  update_check_enabled: boolean
}

/** Response of `POST /setup/complete`; `session` is present only in multiuser mode. */
export interface CompleteSetupResponse {
  auth_mode: AuthMode
  recovery_key: string
  session?: Session
}

export function fetchSetupStatus(): Promise<SetupStatus> {
  return apiRequest<SetupStatus>('/setup/status')
}

/**
 * Single-shot setup completion. The recovery key is returned exactly once and
 * must be shown before the operator enters the app; repeats fail with
 * `409 SETUP_ALREADY_COMPLETED`.
 */
export function completeSetup(request: CompleteSetupRequest): Promise<CompleteSetupResponse> {
  return apiRequest<CompleteSetupResponse>('/setup/complete', { method: 'POST', body: request })
}
