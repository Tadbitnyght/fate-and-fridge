import { apiRequest } from './client'

/**
 * Typed client for the M13 update-check contract (docs/API.md §25,
 * docs/M13_PLAN.md §1.13–§1.14).
 *
 * The status read is available to every authenticated actor because the About
 * section and the dashboard notice are visible to members too. The forced check
 * and the enabled preference stay Admin-only on the existing `admin` role: the
 * server is the sole authority, so this module never assumes a caller may write.
 * JSON property names are snake_case.
 */

/**
 * The cached update status. `latest_version` and `release_url` are `null` until
 * a check has found a published release, `last_error` carries a short
 * non-identifying failure note, and `update_available` is already
 * `enabled && latest > current` on the server.
 */
export interface UpdateStatus {
  /** The running build's SemVer version. */
  current_version: string
  /** SemVer of the newest published release, with the leading `v` stripped. */
  latest_version: string | null
  update_available: boolean
  /** ISO-8601 instant of the last check, or `null` when none has run. */
  last_checked_at: string | null
  /** A short failure note from the last check; never a stack trace. */
  last_error: string | null
  /** The `update_check_enabled` setup preference. */
  enabled: boolean
  /** The public release page, an absolute `https` URL, or `null`. */
  release_url: string | null
}

/**
 * The cached status (any authenticated actor). Never triggers an outbound
 * request: the check itself runs on startup or through {@link checkForUpdates}.
 */
export function getUpdates(signal?: AbortSignal): Promise<UpdateStatus> {
  return apiRequest<UpdateStatus>('/updates', { signal })
}

/**
 * Forces one check even inside the 24-hour cache (Admin only). A feed failure
 * is still a `200` with the recorded `last_error`, not an HTTP error; a member
 * receives `403 FORBIDDEN`.
 */
export function checkForUpdates(signal?: AbortSignal): Promise<UpdateStatus> {
  return apiRequest<UpdateStatus>('/admin/updates/check', { method: 'POST', signal })
}

/**
 * Persists the `update_check_enabled` preference (Admin only) and audits it as
 * `settings.update_check_changed`. The response is the fresh status.
 */
export function setUpdateChecksEnabled(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<UpdateStatus> {
  return apiRequest<UpdateStatus>('/admin/updates', {
    method: 'PATCH',
    body: { enabled },
    signal,
  })
}
