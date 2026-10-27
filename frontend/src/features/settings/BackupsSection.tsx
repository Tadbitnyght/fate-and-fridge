import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type JSX } from 'react'
import { useNavigate } from 'react-router-dom'

import {
  BACKUP_DELETE_CONFIRMATION,
  BACKUP_RESTORE_CONFIRMATION,
  MAX_BACKUP_RESTORE_BYTES,
  downloadBackup,
  type BackupKind,
  type BackupRow,
  type BackupStatus,
  type RestoreAccepted,
} from '../../api/backups'
import { fetchHealth, isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import { EXPORT_RECOVERY_KEY_CONFIRMATION, exportRecoveryKey } from '../../api/recoveryKey'
import {
  DangerousActionDialog,
  type DangerousActionValues,
} from '../../components/DangerousActionDialog'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { formatTimestamp } from '../../lib/dateFormat'
import { saveDownloadedFile } from '../../lib/download'
import { sessionQueryKey } from '../auth/session'
import { useAdminAccess } from './access'
import { AdminOnlyNotice } from './AdminOnlyNotice'
import { useBackups, useCreateBackup, useDeleteBackup, useRestoreBackup } from './useBackups'

const KIND_LABELS: Record<BackupKind, string> = {
  manual: 'Manual',
  safety: 'Safety',
}

const STATUS_LABELS: Record<BackupStatus, string> = {
  pending: 'Pending',
  completed: 'Completed',
  failed: 'Failed',
  restored: 'Restored',
  deleted: 'Deleted',
}

/** Backups that still have a usable archive file on the server. */
const RESTORABLE: ReadonlySet<BackupStatus> = new Set<BackupStatus>(['completed', 'restored'])

const DEFAULT_POLL_INTERVAL_MS = 2000

type PollState = 'waiting' | 'unreachable'

/**
 * Human-readable byte count in binary units (`1 MB` for 1048576). Kept local to
 * this section because it is the only surface that renders archive sizes.
 */
function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return '—'
  }
  if (bytes < 1024) {
    return `${bytes} B`
  }
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value >= 10 ? Math.round(value) : Math.round(value * 10) / 10} ${units[unit]}`
}

/** First 12 checksum characters plus an ellipsis, or an em dash when empty. */
function truncateChecksum(checksum: string): string {
  if (checksum.length === 0) {
    return '—'
  }
  return checksum.length <= 16 ? checksum : `${checksum.slice(0, 12)}…`
}

/** A short filename for dialogs and action labels. */
function displayFilename(row: BackupRow): string {
  return row.filename
}

/** Readable create failures (docs/API.md §18). Never branch on the message. */
function createFailureMessage(error: unknown): string {
  if (!isApiError(error)) {
    return 'The backup request could not reach the server. Check the connection and try again.'
  }
  switch (error.code) {
    case ErrorCodes.BackupClientTooOld:
      return 'The server’s database tools are older than its database, so backups are refused. Update the app image and try again.'
    case ErrorCodes.BackupKeyMissing:
      return 'The server has no usable recovery key, so a backup cannot be encrypted. Restore the recovery key file on the server first.'
    case ErrorCodes.BackupInProgress:
      return 'Another backup or restore is already running. Wait for it to finish and try again.'
    case ErrorCodes.BackupFailed:
      return 'The backup failed before it completed. No archive was created; the server recorded the error.'
    case ErrorCodes.ReasonRequired:
      return 'Enter a reason for the backup.'
    case ErrorCodes.ReasonTooLong:
      return 'The reason is too long. Keep it under 500 characters.'
    case ErrorCodes.ReasonInvalid:
    case ErrorCodes.ValidationFailed:
      return error.message.length > 0 ? error.message : 'Check the reason and try again.'
    default:
      return error.message.length > 0
        ? error.message
        : 'The backup could not be created. Check the connection and try again.'
  }
}

/** Readable delete failures (docs/API.md §18). */
function deleteFailureMessage(error: unknown): string {
  if (!isApiError(error)) {
    return 'The delete request could not reach the server. Check the connection and try again.'
  }
  switch (error.code) {
    case ErrorCodes.BackupNotFound:
      return 'That backup no longer exists. The list has been refreshed.'
    case ErrorCodes.BackupInProgress:
      return 'This backup is still being written, so it cannot be deleted yet. Wait for it to finish.'
    case ErrorCodes.BackupFailed:
      return 'The server could not remove the archive file, so the backup was left unchanged.'
    case ErrorCodes.ReasonRequired:
      return 'Enter a reason for deleting this backup.'
    case ErrorCodes.ConfirmationMismatch:
      return `Type ${BACKUP_DELETE_CONFIRMATION} exactly to confirm the deletion.`
    case ErrorCodes.ValidationFailed:
      return error.message.length > 0 ? error.message : 'Check the reason and try again.'
    default:
      return error.message.length > 0
        ? error.message
        : 'The backup could not be deleted. Check the connection and try again.'
  }
}

/** Readable recovery-key export failures (docs/API.md §18). */
function recoveryKeyFailureMessage(error: unknown): string {
  if (!isApiError(error)) {
    return 'The download request could not reach the server. Check the connection and try again.'
  }
  switch (error.code) {
    case ErrorCodes.BackupKeyMissing:
      return 'The server has no usable recovery key file, so there is nothing to download. Complete setup or restore the key file on the server first.'
    case ErrorCodes.ReasonRequired:
      return 'Enter a reason for exporting the recovery key.'
    case ErrorCodes.ConfirmationMismatch:
      return `Type ${EXPORT_RECOVERY_KEY_CONFIRMATION} exactly to confirm the export.`
    case ErrorCodes.ReasonTooLong:
      return 'The reason is too long. Keep it under 500 characters.'
    case ErrorCodes.ReasonInvalid:
    case ErrorCodes.ValidationFailed:
      return error.message.length > 0 ? error.message : 'Check the reason and try again.'
    default:
      return error.message.length > 0
        ? error.message
        : 'The recovery key could not be downloaded. Check the connection and try again.'
  }
}

/** Readable restore failures (docs/API.md §18). */
function restoreFailureMessage(error: unknown): string {
  if (!isApiError(error)) {
    return 'The restore request could not reach the server. Check the connection and try again.'
  }
  switch (error.code) {
    case ErrorCodes.BackupKeyMismatch:
      return 'The recovery key on this server does not match this backup, so it cannot be decrypted. Restore the matching recovery key file first.'
    case ErrorCodes.BackupIntegrityFailed:
      return 'The archive is damaged or incomplete: its checksum or contents do not match. Use a fresh copy of the file.'
    case ErrorCodes.BackupFormatUnsupported:
      return 'That file is not a supported Fate & Fridge backup, or it contains an unsafe path.'
    case ErrorCodes.BackupSchemaTooNew:
      return 'This backup was made by a newer version of Fate & Fridge. Update the app before restoring it.'
    case ErrorCodes.BackupFileMissing:
      return 'The archive file is missing from the server’s backups directory. Upload a copy of the file instead.'
    case ErrorCodes.BackupNotFound:
      return 'That backup no longer exists. Refresh the list and choose another archive.'
    case ErrorCodes.RestoreInProgress:
      return 'Another backup or restore is already running. Wait for it to finish and try again.'
    case ErrorCodes.RestoreFailed:
      return 'The restore could not be started. The server log has the details; the household data was not changed.'
    case ErrorCodes.BackupKeyMissing:
      return 'The server has no usable recovery key, so the archive cannot be decrypted. Restore the recovery key file first.'
    case ErrorCodes.ConfirmationMismatch:
      return `Type ${BACKUP_RESTORE_CONFIRMATION} exactly to confirm the restore.`
    case ErrorCodes.ValidationFailed:
      return 'Check the archive, the reason, and the confirmation, then try again.'
    default:
      return error.message.length > 0
        ? error.message
        : 'The restore could not be started. Check the connection and try again.'
  }
}

type RestoreChoice = { kind: 'stored'; row: BackupRow } | { kind: 'upload' }

/**
 * Admin-only Backups section (M13-06, docs/API.md §18, docs/M13_PLAN.md §1.8,
 * §1.9, §4).
 *
 * The list is the server's `backup_metadata` rows, newest first. Creating is a
 * long synchronous request; downloading saves the encrypted archive (useless
 * without the recovery key); deleting marks the row `deleted`; and restoring
 * either a stored archive or an upload validates on the server first, answers
 * `202`, and then restarts the app. Every destructive action goes through the
 * shared dangerous-action dialog, and the server independently enforces the
 * reason and the typed confirmation.
 */
export function BackupsSection({
  pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
}: {
  pollIntervalMs?: number
} = {}): JSX.Element {
  const { isAdmin } = useAdminAccess()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const backupsQuery = useBackups(isAdmin)
  const createMutation = useCreateBackup()
  const deleteMutation = useDeleteBackup()
  const restoreMutation = useRestoreBackup()

  const [createOpen, setCreateOpen] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [created, setCreated] = useState<BackupRow | null>(null)

  const [deleteTarget, setDeleteTarget] = useState<BackupRow | null>(null)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const [uploadFile, setUploadFile] = useState<File | null>(null)
  const [uploadError, setUploadError] = useState<string | null>(null)
  const [restoreChoice, setRestoreChoice] = useState<RestoreChoice | null>(null)
  const [restoreError, setRestoreError] = useState<string | null>(null)

  const [downloadingId, setDownloadingId] = useState<number | null>(null)
  const [downloadError, setDownloadError] = useState<string | null>(null)

  const [recoveryKeyOpen, setRecoveryKeyOpen] = useState(false)
  const [recoveryKeyError, setRecoveryKeyError] = useState<string | null>(null)
  const [recoveryKeyBusy, setRecoveryKeyBusy] = useState(false)

  const [restoring, setRestoring] = useState<RestoreAccepted | null>(null)
  const [pollState, setPollState] = useState<PollState>('waiting')

  // Poll the public health endpoint while the restore restarts the app.
  // Connection failures are expected (the container goes away) and 503
  // RESTORE_IN_PROGRESS is the maintenance window; the app is only announced as
  // back once health actually answers, then the client re-enters the app.
  useEffect(() => {
    if (restoring === null) {
      return
    }

    let cancelled = false
    let timer: number | undefined

    const poll = async () => {
      let healthy: boolean
      try {
        await fetchHealth()
        healthy = true
      } catch {
        healthy = false
      }
      if (cancelled) {
        return
      }

      if (healthy) {
        try {
          await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
        } catch {
          // A revoked session is expected after a restore; the routing guard
          // takes over from here.
        }
        if (!cancelled) {
          navigate('/', { replace: true })
        }
        return
      }

      setPollState('unreachable')
      timer = window.setTimeout(() => {
        void poll()
      }, pollIntervalMs)
    }

    timer = window.setTimeout(() => {
      void poll()
    }, pollIntervalMs)

    return () => {
      cancelled = true
      if (timer !== undefined) {
        window.clearTimeout(timer)
      }
    }
  }, [navigate, pollIntervalMs, queryClient, restoring])

  if (restoring !== null) {
    const statusMessage =
      pollState === 'unreachable'
        ? 'The app is restarting. Waiting for it to come back — this page reconnects automatically.'
        : 'Restoring your household and restarting the app. This page reconnects automatically.'

    return (
      <div className="restore-progress" role="status" aria-live="polite" aria-busy="true">
        <h3 className="restore-progress__title">Restoring from backup</h3>
        <p className="restore-progress__message">{statusMessage}</p>

        <dl className="restore-progress__facts">
          <div>
            <dt>Archive</dt>
            <dd>{restoring.filename}</dd>
          </div>
          <div>
            <dt>Safety backup</dt>
            <dd>{restoring.safety_backup_filename}</dd>
          </div>
          <div>
            <dt>Restore id</dt>
            <dd>{restoring.restore_id}</dd>
          </div>
        </dl>

        <p className="page-note">
          Everyone is signed out by the restore, including this browser. If the app does not come
          back, check the server log and restart the container.
        </p>
      </div>
    )
  }

  if (!isAdmin) {
    return (
      <section className="settings-section" aria-labelledby="backups-heading">
        <h3 id="backups-heading">Backups</h3>
        <AdminOnlyNotice feature="Backup and restore" />
      </section>
    )
  }

  async function handleCreate(values: DangerousActionValues): Promise<void> {
    setCreateError(null)
    setCreated(null)
    try {
      const row = await createMutation.mutateAsync(values.reason)
      setCreated(row)
      setCreateOpen(false)
    } catch (error) {
      setCreateError(createFailureMessage(error))
    }
  }

  async function handleDelete(values: DangerousActionValues): Promise<void> {
    if (deleteTarget === null) {
      return
    }
    setDeleteError(null)
    try {
      await deleteMutation.mutateAsync({
        id: deleteTarget.id,
        action: { reason: values.reason, confirmation: values.confirmation ?? '' },
      })
      setDeleteTarget(null)
    } catch (error) {
      setDeleteError(deleteFailureMessage(error))
    }
  }

  async function handleRestore(values: DangerousActionValues): Promise<void> {
    if (restoreChoice === null) {
      return
    }
    const source =
      restoreChoice.kind === 'stored'
        ? { backupId: restoreChoice.row.id }
        : uploadFile === null
          ? null
          : { file: uploadFile }
    if (source === null) {
      return
    }

    setRestoreError(null)
    try {
      const accepted = await restoreMutation.mutateAsync({
        source,
        action: { reason: values.reason, confirmation: values.confirmation ?? '' },
      })
      setRestoreChoice(null)
      setUploadFile(null)
      setPollState('waiting')
      setRestoring(accepted)
    } catch (error) {
      setRestoreError(restoreFailureMessage(error))
    }
  }

  async function handleDownload(row: BackupRow): Promise<void> {
    setDownloadError(null)
    setDownloadingId(row.id)
    try {
      saveDownloadedFile(await downloadBackup(row.id, row.filename))
    } catch (error) {
      setDownloadError(
        isApiError(error)
          ? error.message
          : 'The download failed. Check the connection and try again.',
      )
    } finally {
      setDownloadingId(null)
    }
  }

  async function handleExportRecoveryKey(values: DangerousActionValues): Promise<void> {
    setRecoveryKeyError(null)
    setRecoveryKeyBusy(true)
    try {
      saveDownloadedFile(
        await exportRecoveryKey({
          reason: values.reason,
          confirmation: values.confirmation ?? '',
        }),
      )
      setRecoveryKeyOpen(false)
    } catch (error) {
      setRecoveryKeyError(recoveryKeyFailureMessage(error))
    } finally {
      setRecoveryKeyBusy(false)
    }
  }

  function handleChooseUpload(file: File | null): void {
    setUploadError(null)
    setUploadFile(file)
    if (file !== null && file.size > MAX_BACKUP_RESTORE_BYTES) {
      setUploadError('The backup archive must be 2 GB or smaller.')
    }
  }

  function openUploadRestore(): void {
    setUploadError(null)
    if (uploadFile === null) {
      setUploadError('Choose a .ffbackup archive to restore.')
      return
    }
    if (uploadFile.size > MAX_BACKUP_RESTORE_BYTES) {
      setUploadError('The backup archive must be 2 GB or smaller.')
      return
    }
    setRestoreError(null)
    setRestoreChoice({ kind: 'upload' })
  }

  function openStoredRestore(row: BackupRow): void {
    setRestoreError(null)
    setRestoreChoice({ kind: 'stored', row })
  }

  const rows = backupsQuery.data ?? []

  return (
    <section className="settings-section" aria-labelledby="backups-heading">
      <div className="settings-section__header">
        <h3 id="backups-heading">Backups</h3>
        <button
          type="button"
          className="button--primary"
          onClick={() => {
            setCreateError(null)
            setCreateOpen(true)
          }}
        >
          Create backup
        </button>
      </div>

      <p className="page-note">
        Encrypted household archives stored on this server, newest first. A backup contains the
        database, recipe images, and encryption keys — never the recovery key, so an archive cannot
        be opened without it.
      </p>

      <div className="settings-subsection backups-recovery-key">
        <h4 id="recovery-key-heading">Recovery key</h4>
        <p className="page-note">
          The recovery key is needed to restore a backup. Store it offline in a safe place: an
          archive cannot be opened without it. It is never shown inline on this page and is never
          stored inside a backup.
        </p>
        <div className="settings-actions">
          <button
            type="button"
            onClick={() => {
              setRecoveryKeyError(null)
              setRecoveryKeyOpen(true)
            }}
          >
            Download recovery key
          </button>
        </div>
        {!recoveryKeyOpen && recoveryKeyError !== null && (
          <p className="form__error" role="alert">
            {recoveryKeyError}
          </p>
        )}
      </div>

      {created !== null && (
        <div className="form__status backups-created" role="status">
          <span>Backup created: {created.filename}.</span>
          <button
            type="button"
            aria-label={`Download ${created.filename}`}
            onClick={() => void handleDownload(created)}
            disabled={downloadingId !== null}
          >
            {downloadingId === created.id ? 'Downloading…' : 'Download backup'}
          </button>
        </div>
      )}

      {downloadError !== null && (
        <p className="form__error" role="alert">
          {downloadError}
        </p>
      )}

      {backupsQuery.isPending && <LoadingScreen label="Loading backups…" />}

      {backupsQuery.isError && (
        <ErrorScreen
          title="Cannot load backups"
          error={backupsQuery.error}
          onRetry={() => void backupsQuery.refetch()}
        />
      )}

      {backupsQuery.isSuccess && rows.length === 0 && (
        <p className="stats-empty">No backups yet. Create one before an upgrade or a big change.</p>
      )}

      {rows.length > 0 && (
        <div className="table-wrap">
          <table className="data-table backups-table">
            <caption className="sr-only">Backups, newest first</caption>
            <thead>
              <tr>
                <th scope="col">Created</th>
                <th scope="col">Kind</th>
                <th scope="col">Status</th>
                <th scope="col">App version</th>
                <th scope="col">Schema version</th>
                <th scope="col">Size</th>
                <th scope="col">Checksum</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const restorable = RESTORABLE.has(row.status)
                const deletable = row.status !== 'pending' && row.status !== 'deleted'
                return (
                  <tr key={row.id}>
                    <th scope="row">
                      <time dateTime={row.created_at}>{formatTimestamp(row.created_at)}</time>
                      <span className="backups-table__filename">{row.filename}</span>
                    </th>
                    <td>{KIND_LABELS[row.kind]}</td>
                    <td>
                      <span className={`tag backup-status--${row.status}`}>
                        {STATUS_LABELS[row.status]}
                      </span>
                      {row.error !== null && row.error.length > 0 && (
                        <span className="backups-table__error">{row.error}</span>
                      )}
                    </td>
                    <td>{row.app_version}</td>
                    <td className="backups-table__schema">{row.schema_version}</td>
                    <td className="backups-table__size">{formatBytes(row.size_bytes)}</td>
                    <td>
                      {row.checksum.length === 0 ? (
                        '—'
                      ) : (
                        <details className="backup-checksum">
                          <summary>{truncateChecksum(row.checksum)}</summary>
                          <code>{row.checksum}</code>
                        </details>
                      )}
                    </td>
                    <td className="data-table__actions">
                      <button
                        type="button"
                        aria-label={`Download ${displayFilename(row)}`}
                        disabled={!restorable || downloadingId !== null}
                        onClick={() => void handleDownload(row)}
                      >
                        {downloadingId === row.id ? 'Downloading…' : 'Download'}
                      </button>
                      <button
                        type="button"
                        aria-label={`Restore ${displayFilename(row)}`}
                        disabled={!restorable}
                        onClick={() => openStoredRestore(row)}
                      >
                        Restore
                      </button>
                      <button
                        type="button"
                        aria-label={`Delete ${displayFilename(row)}`}
                        disabled={!deletable}
                        onClick={() => {
                          setDeleteError(null)
                          setDeleteTarget(row)
                        }}
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      <div className="settings-subsection backups-restore">
        <h4>Restore from a file</h4>
        <p className="page-note">
          Restore an archive from another device or an earlier installation. Restores replace this
          household&apos;s data, restart the app, and sign everyone out. The archive may be up to 2
          GB.
        </p>
        <div className="form__field">
          <label htmlFor="backup-upload">Backup archive</label>
          <input
            id="backup-upload"
            type="file"
            accept=".ffbackup,application/octet-stream"
            aria-invalid={uploadError !== null ? true : undefined}
            aria-describedby={uploadError !== null ? 'backup-upload-error' : undefined}
            onChange={(event) => handleChooseUpload(event.target.files?.[0] ?? null)}
          />
          {uploadError !== null && (
            <p className="form__error" id="backup-upload-error" role="alert">
              {uploadError}
            </p>
          )}
        </div>
        <div className="settings-actions">
          <button type="button" onClick={openUploadRestore}>
            Restore uploaded archive
          </button>
        </div>
      </div>

      {recoveryKeyOpen && (
        <DangerousActionDialog
          title="Download recovery key"
          description="Download this server’s recovery key file so you can keep a copy offline."
          warnings={[
            'Anyone with this key and a backup archive can read the household data.',
            'Store the downloaded file offline, for example in a password manager or a safe.',
            'The key is never shown on this page: the download is the only copy the app provides.',
          ]}
          reasonLabel="Reason"
          acknowledgementLabel="I understand this key must be stored securely offline."
          confirmation={{
            label: `Type ${EXPORT_RECOVERY_KEY_CONFIRMATION} to confirm`,
            expected: EXPORT_RECOVERY_KEY_CONFIRMATION,
            hint: 'The confirmation is case-sensitive.',
          }}
          confirmLabel="Download recovery key"
          busy={recoveryKeyBusy}
          error={recoveryKeyBusy ? null : recoveryKeyError}
          onConfirm={(values) => void handleExportRecoveryKey(values)}
          onClose={() => {
            if (!recoveryKeyBusy) {
              setRecoveryKeyOpen(false)
            }
          }}
        />
      )}

      {createOpen && (
        <DangerousActionDialog
          title="Create backup"
          description="Create one encrypted household archive on this server."
          warnings={[
            'A backup can take a while for a large household. Keep this page open until it finishes.',
            'The archive is encrypted with this server’s recovery key.',
            'A backup is useless without that recovery key: it is never stored inside the archive.',
          ]}
          reasonLabel="Reason"
          acknowledgementLabel="I understand this backup cannot be opened without the recovery key."
          confirmLabel="Create backup"
          busy={createMutation.isPending}
          error={createError}
          onConfirm={(values) => void handleCreate(values)}
          onClose={() => {
            if (!createMutation.isPending) {
              setCreateOpen(false)
            }
          }}
        />
      )}

      {deleteTarget !== null && (
        <DangerousActionDialog
          title={`Delete ${deleteTarget.filename}?`}
          description="Permanently delete this encrypted archive from the server."
          warnings={[
            'The archive file is deleted and the row stays in the list as deleted.',
            'This cannot be undone from the app. Any copy you already downloaded is not affected.',
          ]}
          reasonLabel="Reason"
          acknowledgementLabel="I understand this backup file will be deleted."
          confirmation={{
            label: `Type ${BACKUP_DELETE_CONFIRMATION} to confirm`,
            expected: BACKUP_DELETE_CONFIRMATION,
            hint: 'The confirmation is case-sensitive.',
          }}
          confirmLabel="Delete backup"
          busy={deleteMutation.isPending}
          error={deleteError}
          onConfirm={(values) => void handleDelete(values)}
          onClose={() => {
            if (!deleteMutation.isPending) {
              setDeleteTarget(null)
            }
          }}
        />
      )}

      {restoreChoice !== null && (
        <DangerousActionDialog
          title="Restore from backup"
          description={
            restoreChoice.kind === 'stored'
              ? `Replace this household’s data with ${restoreChoice.row.filename}.`
              : `Replace this household’s data with the uploaded archive${
                  uploadFile === null ? '' : ` ${uploadFile.name}`
                }.`
          }
          warnings={[
            'The household database, recipe images, and encryption keys are replaced with the archive’s contents.',
            'The app will restart when the restore finishes, and every session is signed out.',
            'This cannot be undone in the live app. A safety backup is created first, so keep that archive and the recovery key.',
          ]}
          reasonLabel="Reason"
          acknowledgementLabel="I understand this household’s data will be replaced and the app will restart."
          confirmation={{
            label: `Type ${BACKUP_RESTORE_CONFIRMATION} to confirm`,
            expected: BACKUP_RESTORE_CONFIRMATION,
            hint: 'The confirmation is case-sensitive.',
          }}
          confirmLabel="Restore backup"
          busy={restoreMutation.isPending}
          error={restoreError}
          onConfirm={(values) => void handleRestore(values)}
          onClose={() => {
            if (!restoreMutation.isPending) {
              setRestoreChoice(null)
            }
          }}
        />
      )}
    </section>
  )
}
