import { apiRequest, apiUpload } from './client'
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

/** The typed acknowledgement for a clean-install restore (docs/API.md §21). */
export const SETUP_RESTORE_CONFIRMATION = 'RESTORE'

/** The 2 GB archive cap enforced by `POST /setup/restore` (docs/API.md §21). */
export const MAX_SETUP_RESTORE_BYTES = 2 * 1024 * 1024 * 1024

/**
 * The `202 Accepted` body of a clean-install restore (docs/API.md §21). The
 * restore itself continues in the background and restarts the app; there is no
 * `backup_id` before setup because no backup catalogue exists yet.
 */
export interface SetupRestoreResponse {
  restore_id: string
  filename: string
  safety_backup_filename: string
}

export function fetchSetupStatus(signal?: AbortSignal): Promise<SetupStatus> {
  return apiRequest<SetupStatus>('/setup/status', { signal })
}

/**
 * Starts a clean-install restore (docs/API.md §21). The archive plus the pasted
 * recovery key are the only credential before setup exists; the key is adopted
 * by the device after validation, never stored in the browser. The request is
 * multipart, so the browser sets its own boundary — the recovery key never
 * appears in a URL or a JSON body.
 */
export function restoreSetupBackup(
  file: File,
  recoveryKey: string,
  confirmation: string,
  signal?: AbortSignal,
): Promise<SetupRestoreResponse> {
  const form = new FormData()
  form.append('file', file)
  form.append('recovery_key', recoveryKey)
  form.append('confirmation', confirmation)

  return apiUpload<SetupRestoreResponse>('/setup/restore', form, { method: 'POST', signal })
}

/**
 * Single-shot setup completion. The recovery key is returned exactly once and
 * must be shown before the operator enters the app; repeats fail with
 * `409 SETUP_ALREADY_COMPLETED`.
 */
export function completeSetup(request: CompleteSetupRequest): Promise<CompleteSetupResponse> {
  return apiRequest<CompleteSetupResponse>('/setup/complete', { method: 'POST', body: request })
}
