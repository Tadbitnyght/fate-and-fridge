import { apiDownload, apiRequest, apiUpload, type DownloadedFile } from './client'

/**
 * Typed client for the M13 admin backup contract.
 * Every route is under `/api/v1/admin` and is Admin-only on the existing
 * `admin` role (and the single-user system actor): no new permission key is
 * introduced. This module only describes the wire shapes; the server is the
 * sole authority on authorization and on the destructive-action guards. JSON
 * property names are snake_case.
 */

/** Archives are either created by an Admin or by a restore (`safety`). */
export type BackupKind = 'manual' | 'safety'

/** Lifecycle of one `backup_metadata` row (docs/API.md §18). */
export type BackupStatus = 'pending' | 'completed' | 'failed' | 'restored' | 'deleted'

/**
 * One backup row as the API returns it, newest first in the list. `checksum` is
 * the lowercase SHA-256 of the **encrypted** archive (empty until a create
 * completes) and `error` is the recorded failure detail for a `failed` row.
 */
export interface BackupRow {
  id: number
  filename: string
  kind: BackupKind
  status: BackupStatus
  app_version: string
  schema_version: string
  /** ISO-8601 instant; render through `formatTimestamp`. */
  created_at: string
  size_bytes: number
  checksum: string
  error: string | null
}

/** The exact typed confirmation for deleting one archive. */
export const BACKUP_DELETE_CONFIRMATION = 'DELETE'

/** The exact typed confirmation for restoring one archive. */
export const BACKUP_RESTORE_CONFIRMATION = 'RESTORE'

/**
 * The archive size cap the restore route enforces (`RestoreService.MaximumUploadBytes`
 * docs/API.md §18). Checked client-side only to give a clear message early.
 */
export const MAX_BACKUP_RESTORE_BYTES = 2 * 1024 * 1024 * 1024

/** Body of `POST /admin/backups`; the reason is written to the audit row. */
export interface CreateBackupRequest {
  reason: string
}

/** Body of `DELETE /admin/backups/{id}` (typed `DELETE` confirmation). */
export interface DeleteBackupRequest {
  reason: string
  confirmation: string
}

/** Either a stored archive (by row id) or an uploaded `.ffbackup` file. */
export type BackupRestoreSource = { backupId: number } | { file: File }

/** The dangerous-action fields every restore requires. */
export interface RestoreBackupAction {
  reason: string
  confirmation: string
}

/**
 * The `202 Accepted` body of a restore (docs/API.md §18). The request is not the
 * restore: the validation and safety backup happen before the `202`, and the
 * process enters maintenance, restores, then restarts afterwards.
 */
export interface RestoreAccepted {
  restore_id: string
  /** Present when a stored archive was chosen; null for an upload. */
  backup_id: number | null
  /** Report-only file name of the chosen/uploaded archive. */
  filename: string
  safety_backup_filename: string
}

/**
 * Newest-first list of every `backup_metadata` row, including `failed` and
 * `deleted` ones (Admin only).
 */
export function listBackups(signal?: AbortSignal): Promise<BackupRow[]> {
  return apiRequest<BackupRow[]>('/admin/backups', { signal })
}

/**
 * Creates one manual backup. Creation is **synchronous** on the server (dump
 * encrypt, checksum), so a large household means a long request; the caller
 * must allow a generous timeout. Resolves `201` with the already-`completed`
 * row, or rejects with an {@link ApiError} (`BACKUP_CLIENT_TOO_OLD`
 * `BACKUP_KEY_MISSING`, `BACKUP_IN_PROGRESS`, `BACKUP_FAILED`, reason guards).
 */
export function createBackup(reason: string, signal?: AbortSignal): Promise<BackupRow> {
  const body: CreateBackupRequest = { reason }
  return apiRequest<BackupRow>('/admin/backups', { method: 'POST', body, signal })
}

/**
 * Downloads the **encrypted** archive as an attachment (Admin only). The
 * recovery key is never inside the file, so the download is useless without it.
 */
export function downloadBackup(id: number, filename: string): Promise<DownloadedFile> {
  return apiDownload(`/admin/backups/${id}/download`, filename)
}

/** Deletes one archive file and marks its row `deleted` (Admin only). */
export function deleteBackup(
  id: number,
  action: DeleteBackupRequest,
  signal?: AbortSignal,
): Promise<BackupRow> {
  return apiRequest<BackupRow>(`/admin/backups/${id}`, {
    method: 'DELETE',
    body: action,
    signal,
  })
}

/**
 * Starts one restore (Admin only). A stored archive is a JSON
 * `{ backup_id }` request; an upload is `multipart/form-data` with the file
 * streamed to the server (2 GB cap). Both shapes carry the reason and the typed
 * `RESTORE` confirmation. A `202` means validation passed and the safety backup
 * exists — the restore itself then runs in the background and restarts the app.
 */
export function restoreBackup(
  source: BackupRestoreSource,
  action: RestoreBackupAction,
  signal?: AbortSignal,
): Promise<RestoreAccepted> {
  if ('file' in source) {
    const form = new FormData()
    form.append('file', source.file)
    form.append('reason', action.reason)
    form.append('confirmation', action.confirmation)
    return apiUpload<RestoreAccepted>('/admin/backups/restore', form, {
      method: 'POST',
      signal,
    })
  }

  return apiRequest<RestoreAccepted>('/admin/backups/restore', {
    method: 'POST',
    body: {
      backup_id: source.backupId,
      reason: action.reason,
      confirmation: action.confirmation,
    },
    signal,
  })
}
