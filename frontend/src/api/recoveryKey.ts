import { apiDownload, type DownloadedFile } from './client'

/**
 * Typed client for the M13-07 Admin recovery-key export (docs/API.md §18,
 * docs/M13_PLAN.md §1.11).
 *
 * The key is only ever returned as a `text/plain` attachment; it is never a
 * JSON body and the app never renders it inline. The route is Admin-only on the
 * existing `admin` role (and the single-user system actor) and requires the
 * exact typed `EXPORT` confirmation plus a reason under the shared
 * dangerous-action rules. The server is the sole authority.
 */

/** The exact typed confirmation for exporting the recovery key. */
export const EXPORT_RECOVERY_KEY_CONFIRMATION = 'EXPORT'

/** The download file name the server sets in `Content-Disposition`. */
export const RECOVERY_KEY_FILENAME = 'recovery.key'

/** The dangerous-action fields every export requires. */
export interface ExportRecoveryKeyAction {
  reason: string
  confirmation: string
}

/**
 * Downloads the household recovery key through the guarded export route.
 * Resolves with the file to save; rejects with an {@link import('./client').ApiError}
 * (`BACKUP_KEY_MISSING`, `CONFIRMATION_MISMATCH`, reason guards) or a network
 * error. The key text stays inside the blob and is never rendered.
 */
export function exportRecoveryKey(
  action: ExportRecoveryKeyAction,
  signal?: AbortSignal,
): Promise<DownloadedFile> {
  return apiDownload('/admin/recovery-key/export', RECOVERY_KEY_FILENAME, {
    method: 'POST',
    body: action,
    signal,
  })
}
