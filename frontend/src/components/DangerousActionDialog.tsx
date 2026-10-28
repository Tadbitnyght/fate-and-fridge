import { useId, useState } from 'react'

import { Field } from './Field'
import { fieldProps } from './fieldProps'
import { Modal } from './Modal'

/** Mirrors `DangerousActionGuard.MaxReasonLength` on the server. */
export const MAX_DANGEROUS_ACTION_REASON_LENGTH = 500

export interface DangerousActionConfirmation {
  /** Label of the typed-confirmation field. */
  label: string
  /** Exact value the caller must type (compared after trimming). */
  expected: string
  /** Optional extra instruction appended to the built-in hint. */
  hint?: string
}

export interface DangerousActionValues {
  /** The reason, trimmed. */
  reason: string
  /** The typed confirmation, trimmed, or null when no confirmation was asked for. */
  confirmation: string | null
}

/**
 * Shared confirmation dialog for destructive/administrative actions
 * a reason, explicit warnings, an acknowledgement
 * checkbox, and an optional typed confirmation, all of which must be satisfied
 * before Confirm is enabled.
 * The dialog is a convenience, never the authority: every caller's endpoint
 * must also run `DangerousActionGuard` (and enforce the Admin role) so a direct
 * API request without the dialog still fails. It builds on `Modal`, which owns
 * the focus trap, Escape handling, `aria-modal`, and returning focus to the
 * trigger.
 */
export function DangerousActionDialog({
  title,
  description,
  warnings,
  reasonLabel,
  acknowledgementLabel,
  confirmation,
  confirmLabel,
  busy = false,
  error,
  onConfirm,
  onClose,
}: {
  title: string
  description: string
  warnings: string[]
  reasonLabel: string
  acknowledgementLabel: string
  confirmation?: DangerousActionConfirmation
  confirmLabel: string
  busy?: boolean
  error?: string | null
  onConfirm: (values: DangerousActionValues) => void
  onClose: () => void
}) {
  const reasonId = useId()
  const confirmationId = useId()

  const [reason, setReason] = useState('')
  const [acknowledged, setAcknowledged] = useState(false)
  const [typedConfirmation, setTypedConfirmation] = useState('')

  const trimmedReason = reason.trim()
  const reasonTooLong = trimmedReason.length > MAX_DANGEROUS_ACTION_REASON_LENGTH
  const reasonError = reasonTooLong
    ? `Reason must be at most ${MAX_DANGEROUS_ACTION_REASON_LENGTH} characters.`
    : undefined
  const reasonValid = trimmedReason.length > 0 && !reasonTooLong

  const expected = confirmation ? confirmation.expected.trim() : null
  const trimmedConfirmation = typedConfirmation.trim()
  const confirmationMatches = expected === null || trimmedConfirmation === expected
  const confirmationError =
    confirmation && trimmedConfirmation.length > 0 && !confirmationMatches
      ? `Type "${confirmation.expected.trim()}" exactly to confirm.`
      : undefined
  const confirmationHint = confirmation
    ? `Type "${confirmation.expected.trim()}" exactly to confirm.${
        confirmation.hint ? ` ${confirmation.hint}` : ''
      }`
    : undefined

  const canConfirm = !busy && reasonValid && acknowledged && confirmationMatches

  function handleClose(): void {
    // A request is in flight: closing would orphan it, so control is locked
    // until the caller clears `busy` (success closes, failure shows `error`).
    if (!busy) {
      onClose()
    }
  }

  function handleConfirm(): void {
    if (!canConfirm) {
      return
    }

    onConfirm({
      reason: trimmedReason,
      confirmation: expected === null ? null : trimmedConfirmation,
    })
  }

  return (
    <Modal title={title} onClose={handleClose}>
      <div className="modal__body">
        <p>{description}</p>
      </div>

      {warnings.length > 0 && (
        <div className="notice notice--warning">
          <ul>
            {warnings.map((warning, index) => (
              <li key={index}>{warning}</li>
            ))}
          </ul>
        </div>
      )}

      <Field id={reasonId} label={reasonLabel} error={reasonError}>
        <textarea
          {...fieldProps(reasonId, { error: reasonError })}
          value={reason}
          rows={3}
          disabled={busy}
          onChange={(event) => setReason(event.target.value)}
        />
      </Field>

      <label className="form__choice">
        <input
          type="checkbox"
          checked={acknowledged}
          disabled={busy}
          onChange={(event) => setAcknowledged(event.target.checked)}
        />
        <span>{acknowledgementLabel}</span>
      </label>

      {confirmation && (
        <Field
          id={confirmationId}
          label={confirmation.label}
          hint={confirmationHint}
          error={confirmationError}
        >
          <input
            type="text"
            {...fieldProps(confirmationId, { hint: true, error: confirmationError })}
            value={typedConfirmation}
            autoComplete="off"
            spellCheck={false}
            disabled={busy}
            onChange={(event) => setTypedConfirmation(event.target.value)}
          />
        </Field>
      )}

      {error && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}

      <div className="modal__actions">
        <button type="button" onClick={handleClose} disabled={busy}>
          Cancel
        </button>
        <button
          type="button"
          className="button--primary"
          onClick={handleConfirm}
          disabled={!canConfirm}
        >
          {busy ? 'Working…' : confirmLabel}
        </button>
      </div>
    </Modal>
  )
}
