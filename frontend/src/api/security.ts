import { apiRequest } from './client'
import type { Session } from './auth'

/** `GET /settings/security` payload (docs/API.md §21); Admin-only. */
export interface SecuritySettings {
  ip_allowlist_enabled: boolean
  ip_allowlist_ranges: string[]
  trusted_proxies: string[]
  /** `0` = keep audit entries forever; otherwise the prune window, 7–3650 days. */
  audit_retention_days: number
}

/** Partial PATCH body; `confirmed: true` is required when enabling the allowlist. */
export interface SecuritySettingsPatch {
  ip_allowlist_enabled?: boolean
  ip_allowlist_ranges?: string[]
  trusted_proxies?: string[]
  audit_retention_days?: number
  confirmed?: boolean
}

export function fetchSecuritySettings(): Promise<SecuritySettings> {
  return apiRequest<SecuritySettings>('/settings/security')
}

export function updateSecuritySettings(patch: SecuritySettingsPatch): Promise<SecuritySettings> {
  return apiRequest<SecuritySettings>('/settings/security', { method: 'PATCH', body: patch })
}

/**
 * Single-user → multiuser conversion (`POST /settings/auth-mode`). On success
 * the response is the signed-in session payload (cookie + CSRF pair included);
 * downgrade and already-multiuser conflicts use their own error codes.
 */
export function switchToMultiuser(admin: { username: string; password: string }): Promise<Session> {
  return apiRequest<Session>('/settings/auth-mode', {
    method: 'POST',
    body: { auth_mode: 'multiuser', admin },
  })
}
