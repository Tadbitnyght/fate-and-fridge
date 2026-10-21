import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import {
  fetchSetupStatus,
  MAX_SETUP_RESTORE_BYTES,
  restoreSetupBackup,
  SETUP_RESTORE_CONFIRMATION,
  type SetupRestoreResponse,
} from '../../api/setup'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { sessionQueryKey } from '../auth/session'
import { setupStatusQueryKey } from './useSetupStatus'

const DEFAULT_POLL_INTERVAL_MS = 2000

type PollState = 'waiting' | 'unreachable' | 'incomplete'

const CONFIRMATION_LABEL = `Type ${SETUP_RESTORE_CONFIRMATION} to confirm`

/** Human-readable restore failures (docs/API.md §4, §21). Never branch on the message. */
function restoreFailureMessage(error: unknown): string {
  if (!isApiError(error)) {
    return 'The restore request could not reach the server. Check that it is reachable and try again.'
  }

  switch (error.code) {
    case ErrorCodes.BackupKeyMismatch:
      return 'The recovery key does not match this backup. Check the key and try again.'
    case ErrorCodes.BackupIntegrityFailed:
      return 'The backup file is damaged or incomplete. Use a fresh copy of the archive.'
    case ErrorCodes.BackupFormatUnsupported:
      return 'That file is not a supported Fate & Fridge backup, or its format is not supported by this version.'
    case ErrorCodes.BackupSchemaTooNew:
      return 'This backup was made by a newer version of Fate & Fridge. Update the app before restoring it.'
    case ErrorCodes.BackupKeyMissing:
      return 'The server could not read a recovery key for this restore. Check the key file on the server and try again.'
    case ErrorCodes.RestoreInProgress:
      return 'Another backup or restore is already running. Wait for it to finish and try again.'
    case ErrorCodes.SetupAlreadyCompleted:
      return 'Setup has already been completed on this server. Restore is only available before setup.'
    case ErrorCodes.ValidationFailed:
      return 'Check the backup archive, recovery key, and confirmation, then try again.'
    default:
      return error.message.length > 0
        ? error.message
        : 'The restore could not be started. Check that the server is reachable and try again.'
  }
}

/**
 * Clean-install restore (M13-05, docs/API.md §21): upload a `.ffbackup`
 * archive, paste the recovery key that belongs to it, and acknowledge the
 * replacement by typing `RESTORE`. The server validates everything, adopts the
 * key, creates a safety backup, and answers `202`; this page then polls
 * `GET /setup/status` while the app restarts and only enters the normal startup
 * flow once the server answers with a completed setup.
 */
export function RestoreFromBackupPage({
  pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
}: {
  pollIntervalMs?: number
} = {}) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const [file, setFile] = useState<File | null>(null)
  const [recoveryKey, setRecoveryKey] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [restoring, setRestoring] = useState<SetupRestoreResponse | null>(null)
  const [pollState, setPollState] = useState<PollState>('waiting')

  const headingRef = useRef<HTMLHeadingElement>(null)
  const errorSummaryRef = useRef<HTMLDivElement>(null)
  const [focusRequest, setFocusRequest] = useState<{ target: 'heading' | 'summary' } | null>({
    target: 'heading',
  })

  useEffect(() => {
    if (focusRequest === null) {
      return
    }
    const node = focusRequest.target === 'summary' ? errorSummaryRef.current : headingRef.current
    node?.focus()
  }, [focusRequest])

  const restore = useMutation({
    mutationFn: ({
      archive,
      key,
      typedConfirmation,
    }: {
      archive: File
      key: string
      typedConfirmation: string
    }) => restoreSetupBackup(archive, key, typedConfirmation),
    onSuccess: (response) => {
      // The server owns the key from here; do not keep a copy in the browser.
      setRecoveryKey('')
      setConfirmation('')
      setErrors({})
      setFormError(null)
      setRestoring(response)
      setFocusRequest({ target: 'heading' })
    },
    onError: (error) => {
      const fieldErrors =
        isApiError(error) && error.code === ErrorCodes.ValidationFailed
          ? extractServerFieldErrors(error.details)
          : {}
      const knownFields: FieldErrors = {}
      for (const field of ['file', 'recovery_key', 'confirmation'] as const) {
        if (fieldErrors[field] !== undefined) {
          knownFields[field] = fieldErrors[field]
        }
      }
      setErrors(knownFields)
      setFormError(Object.keys(knownFields).length === 0 ? restoreFailureMessage(error) : null)
      setFocusRequest({ target: 'summary' })
    },
  })

  // Poll the public setup status while the restore restarts the app. Connection
  // failures are expected (the container goes away); 503 RESTORE_IN_PROGRESS is
  // the maintenance window. Success is claimed only on a real status response.
  useEffect(() => {
    if (restoring === null) {
      return
    }

    let cancelled = false
    let timer: number | undefined

    const poll = async () => {
      try {
        const status = await fetchSetupStatus()
        if (cancelled) {
          return
        }
        if (status.setup_completed) {
          queryClient.setQueryData(setupStatusQueryKey, status)
          try {
            await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
          } catch {
            // The routing guard surfaces a session error with its own retry;
            // the restore itself is already complete.
          }
          if (!cancelled) {
            navigate('/', { replace: true })
          }
          return
        }
        setPollState('incomplete')
      } catch {
        if (cancelled) {
          return
        }
        setPollState('unreachable')
      }

      if (!cancelled) {
        timer = window.setTimeout(() => {
          void poll()
        }, pollIntervalMs)
      }
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

  const canSubmit =
    file !== null &&
    recoveryKey.trim().length > 0 &&
    confirmation.trim() === SETUP_RESTORE_CONFIRMATION

  function handleSubmit(event: FormEvent<HTMLFormElement>): void {
    event.preventDefault()
    if (restore.isPending || restoring !== null) {
      return
    }

    const validationErrors: FieldErrors = {}
    if (file === null) {
      validationErrors.file = 'Choose a .ffbackup archive to restore.'
    } else if (file.size > MAX_SETUP_RESTORE_BYTES) {
      validationErrors.file = 'The backup archive must be 2 GB or smaller.'
    }
    if (recoveryKey.trim().length === 0) {
      validationErrors.recovery_key = 'Paste the recovery key for this backup.'
    }
    if (confirmation.trim() !== SETUP_RESTORE_CONFIRMATION) {
      validationErrors.confirmation = `Type ${SETUP_RESTORE_CONFIRMATION} exactly to confirm the restore.`
    }

    if (Object.keys(validationErrors).length > 0 || file === null) {
      setErrors(validationErrors)
      setFormError(null)
      setFocusRequest({ target: 'summary' })
      return
    }

    restore.mutate({
      archive: file,
      key: recoveryKey.trim(),
      typedConfirmation: confirmation.trim(),
    })
  }

  if (restoring !== null) {
    const statusMessage =
      pollState === 'unreachable'
        ? 'The app is restarting. Waiting for it to come back — this page reconnects automatically.'
        : pollState === 'incomplete'
          ? 'The restore is still running on the server. Waiting for it to finish…'
          : 'Restoring your household. The app will restart when the restore finishes, and this page reconnects automatically.'

    return (
      <div className="screen screen--narrow wizard">
        <h1 className="wizard__title" tabIndex={-1} ref={headingRef}>
          Restoring from backup
        </h1>

        <p className="form__notice" role="status" aria-live="polite" aria-busy="true">
          {statusMessage}
        </p>

        <dl className="setup-facts">
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
          The app may take a few minutes to come back. If it does not, check the server log. Once it
          is reachable again, setup is still available if you need it.
        </p>

        <div className="wizard__footer">
          <Link to="/setup">Back to setup</Link>
        </div>
      </div>
    )
  }

  const errorMessages = [...(formError !== null ? [formError] : []), ...Object.values(errors)]
  const hasErrors = errorMessages.length > 0

  return (
    <div className="screen screen--narrow wizard">
      <h1 className="wizard__title" tabIndex={-1} ref={headingRef}>
        Restore from backup
      </h1>

      <p>
        Restore a Fate &amp; Fridge backup from another device or an earlier installation. Use this
        instead of the setup wizard when you already have a household backup.
      </p>

      <div className="notice notice--warning" role="note" aria-labelledby="restore-warning-title">
        <h2 id="restore-warning-title">This replaces this device&apos;s data</h2>
        <ul>
          <li>
            The household database, recipe images, and encryption keys will be replaced with the
            backup&apos;s contents.
          </li>
          <li>The app will restart when the restore finishes. Everyone will be signed out.</li>
          <li>
            You need the recovery key that was shown when this backup was created — without it the
            archive cannot be restored.
          </li>
        </ul>
      </div>

      {hasErrors && (
        <div
          className="wizard__errors"
          role="alert"
          tabIndex={-1}
          ref={errorSummaryRef}
          aria-labelledby="restore-errors-title"
        >
          <h2 id="restore-errors-title">There is a problem</h2>
          <ul>
            {errorMessages.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        </div>
      )}

      <form className="wizard__form" onSubmit={handleSubmit} noValidate>
        <div className="wizard__step">
          <div className="form__field">
            <label htmlFor="restore-file">Backup archive</label>
            <input
              id="restore-file"
              type="file"
              accept=".ffbackup,application/octet-stream"
              aria-invalid={errors.file ? true : undefined}
              aria-describedby={
                errors.file ? 'restore-file-hint restore-file-error' : 'restore-file-hint'
              }
              onChange={(event) => {
                setFile(event.target.files?.[0] ?? null)
                setErrors((current) => {
                  const next = { ...current }
                  delete next.file
                  return next
                })
                setFormError(null)
              }}
            />
            <p className="form__hint" id="restore-file-hint">
              The <code>.ffbackup</code> file created by Fate &amp; Fridge. The archive may be up to
              2 GB.
            </p>
            {errors.file && (
              <p className="form__error" id="restore-file-error">
                {errors.file}
              </p>
            )}
          </div>

          <div className="form__field">
            <label htmlFor="restore-recovery-key">Recovery key</label>
            <input
              id="restore-recovery-key"
              type="password"
              autoComplete="off"
              spellCheck={false}
              value={recoveryKey}
              aria-invalid={errors.recovery_key ? true : undefined}
              aria-describedby={
                errors.recovery_key
                  ? 'restore-recovery-key-hint restore-recovery-key-error'
                  : 'restore-recovery-key-hint'
              }
              onChange={(event) => {
                setRecoveryKey(event.target.value)
                setErrors((current) => {
                  const next = { ...current }
                  delete next.recovery_key
                  return next
                })
                setFormError(null)
              }}
            />
            <p className="form__hint" id="restore-recovery-key-hint">
              Paste the key exactly as it was shown when this backup was created. It is sent to this
              server only and is not saved in this browser.
            </p>
            {errors.recovery_key && (
              <p className="form__error" id="restore-recovery-key-error">
                {errors.recovery_key}
              </p>
            )}
          </div>

          <div className="form__field">
            <label htmlFor="restore-confirmation">{CONFIRMATION_LABEL}</label>
            <input
              id="restore-confirmation"
              type="text"
              autoComplete="off"
              spellCheck={false}
              value={confirmation}
              aria-invalid={errors.confirmation ? true : undefined}
              aria-describedby={
                errors.confirmation
                  ? 'restore-confirmation-hint restore-confirmation-error'
                  : 'restore-confirmation-hint'
              }
              onChange={(event) => {
                setConfirmation(event.target.value)
                setErrors((current) => {
                  const next = { ...current }
                  delete next.confirmation
                  return next
                })
                setFormError(null)
              }}
            />
            <p className="form__hint" id="restore-confirmation-hint">
              Type <strong>{SETUP_RESTORE_CONFIRMATION}</strong> in capitals to acknowledge that
              this device&apos;s data will be replaced.
            </p>
            {errors.confirmation && (
              <p className="form__error" id="restore-confirmation-error">
                {errors.confirmation}
              </p>
            )}
          </div>
        </div>

        <div className="wizard__footer">
          <Link to="/setup">Back to setup</Link>
          <button
            type="submit"
            className="button--primary"
            disabled={!canSubmit || restore.isPending}
          >
            {restore.isPending ? 'Starting restore…' : 'Restore backup'}
          </button>
        </div>
      </form>
    </div>
  )
}
