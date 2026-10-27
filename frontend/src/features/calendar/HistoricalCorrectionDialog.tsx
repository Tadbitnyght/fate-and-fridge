import { useState } from 'react'

import type { CalendarCorrectionRequest, CalendarDay } from '../../api/calendar'
import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import {
  DangerousActionDialog,
  MAX_DANGEROUS_ACTION_REASON_LENGTH,
  type DangerousActionValues,
} from '../../components/DangerousActionDialog'
import { Modal } from '../../components/Modal'
import { formatHouseholdDate } from '../../lib/dateFormat'
import { extractServerFieldErrors, firstFieldError } from '../../utils/fieldErrors'
import { CalendarEntryFields } from './CalendarEntryFields'
import {
  calendarEntryFieldsFromEntry,
  toCalendarWriteRequest,
  type CalendarEntryFieldsValue,
} from './calendarEntryFields'
import { fullDateLabel } from './calendarDates'
import { useCalendarDay, useCorrectCalendarEntry } from './useCalendar'

/**
 * M12-07 reusable historical-correction flow (docs/M12_PLAN.md §1.9, §1.11,
 * §1.12, docs/API.md §17).
 *
 * Step 1 edits the same per-type entry fields as the ordinary editor (via
 * {@link CalendarEntryFields}); step 2 collects the dangerous-action reason and
 * the typed date confirmation in the shared {@link DangerousActionDialog}. The
 * server remains the authority: it re-validates the reason and confirmation,
 * the past-only rule, and the lock acknowledgement. A correction never changes
 * the lock state, so the dialog only sends `lock_acknowledged: true` for a
 * locked date (see docs/M12_PLAN.md §1.12).
 */
export function HistoricalCorrectionDialog({
  date,
  onClose,
  onCorrected,
}: {
  date: string
  onClose: () => void
  onCorrected: (day: CalendarDay) => void
}) {
  const dayQuery = useCalendarDay(date)
  const correction = useCorrectCalendarEntry()

  const [fields, setFields] = useState<CalendarEntryFieldsValue | null>(null)
  const [seededKey, setSeededKey] = useState<string | null>(null)
  const [step, setStep] = useState<'fields' | 'confirm'>('fields')
  const [fieldError, setFieldError] = useState<string | null>(null)
  const [confirmError, setConfirmError] = useState<string | null>(null)

  const day = dayQuery.data

  // Seed from the loaded day once (React's derive-from-props pattern, matching
  // DateDetailDialog), so a correction starts from the stored entry.
  const seedKey = day ? `${day.date}:${day.entry?.updated_at ?? 'none'}` : null
  if (day && seedKey !== null && seedKey !== seededKey) {
    setSeededKey(seedKey)
    setFields(calendarEntryFieldsFromEntry(day.entry))
    setFieldError(null)
  }

  const isLocked = day?.entry?.is_locked ?? false

  async function handleConfirm(values: DangerousActionValues): Promise<void> {
    if (fields === null) {
      return
    }
    setConfirmError(null)
    const request: CalendarCorrectionRequest = {
      ...toCalendarWriteRequest(fields),
      reason: values.reason,
      confirmation: values.confirmation ?? '',
      lock_acknowledged: isLocked,
    }
    try {
      const corrected = await correction.mutateAsync({ date, request })
      onCorrected(corrected)
    } catch (caught) {
      const message = describeCorrectionError(caught)
      setConfirmError(message)
      if (isEntryFieldError(caught)) {
        // The fields are the likely culprit; show the message on step 1 too so
        // it is still visible if the caller goes back to edit them.
        setFieldError(message)
      }
    }
  }

  if (step === 'fields') {
    return (
      <Modal title={`Correct ${fullDateLabel(date)}`} onClose={onClose}>
        {dayQuery.isLoading && <p className="page-note">Loading…</p>}
        {dayQuery.isError && (
          <p className="form__error" role="alert">
            Could not load this date.
          </p>
        )}

        {fields !== null && (
          <form
            className="form calendar-detail"
            noValidate
            onSubmit={(event) => {
              event.preventDefault()
              setConfirmError(null)
              setStep('confirm')
            }}
          >
            {fieldError !== null && (
              <p className="form__error" role="alert">
                {fieldError}
              </p>
            )}

            <p className="page-note">
              Correcting a past date rewrites stored history. The change is recorded in the audit
              log with your reason and cannot be undone here.
            </p>

            <CalendarEntryFields
              value={fields}
              idPrefix="correction"
              onChange={(patch) =>
                setFields((current) => (current === null ? current : { ...current, ...patch }))
              }
            />

            <div className="modal__actions calendar-detail__actions">
              <button type="button" onClick={onClose}>
                Cancel
              </button>
              <button type="submit" className="button--primary">
                Continue
              </button>
            </div>
          </form>
        )}
      </Modal>
    )
  }

  const warnings = [
    `This rewrites the stored entry for ${formatHouseholdDate(date)}; the previous values are kept only in the audit log and entry history.`,
    'The correction is permanently recorded in the audit log with your account, the date, and the reason below.',
  ]
  if (isLocked) {
    warnings.push(
      'This date is locked. The correction leaves it locked, and past dates still cannot be edited from the calendar.',
    )
  }

  return (
    <DangerousActionDialog
      title="Confirm historical correction"
      description={`Correct the stored entry for ${formatHouseholdDate(date)}.`}
      warnings={warnings}
      reasonLabel="Reason"
      acknowledgementLabel={
        isLocked
          ? 'I understand this date is locked and this correction rewrites audited historical data.'
          : 'I understand this correction rewrites audited historical data.'
      }
      confirmation={{
        label: 'Type the date to confirm',
        expected: date,
        hint: 'Include the dashes, exactly as shown.',
      }}
      confirmLabel="Correct this date"
      busy={correction.isPending}
      error={confirmError}
      onConfirm={(values) => void handleConfirm(values)}
      onClose={() => {
        if (!correction.isPending) {
          setStep('fields')
        }
      }}
    />
  )
}

/** True when the failure is (or carries) a per-type entry-field problem. */
function isEntryFieldError(caught: unknown): boolean {
  if (!isApiError(caught)) {
    return false
  }
  return (
    caught.code === ErrorCodes.ValidationFailed ||
    caught.code === ErrorCodes.CalendarRecipeRequired ||
    caught.code === ErrorCodes.CalendarMiscLabelRequired
  )
}

/**
 * Turns a correction failure into a readable message. Server codes are the
 * contract (docs/API.md §7, §17); `details.field_errors` is surfaced first
 * because it is the most specific message the server can give.
 */
function describeCorrectionError(caught: unknown): string {
  if (!isApiError(caught)) {
    return 'The correction failed. Try again.'
  }

  const fieldErrors = extractServerFieldErrors(caught.details)
  const fieldMessage = firstFieldError(fieldErrors)

  switch (caught.code) {
    case ErrorCodes.CalendarDateNotPast:
      return 'Only past dates can be corrected. Today and future dates use the normal calendar.'
    case ErrorCodes.CalendarDateLocked:
      return 'This date is locked. Acknowledge the lock and try again.'
    case ErrorCodes.ConfirmationMismatch:
      return 'The typed confirmation did not match. Type the date exactly (yyyy-MM-dd) to confirm.'
    case ErrorCodes.ReasonRequired:
      return fieldMessage ?? 'Enter a reason for the correction.'
    case ErrorCodes.ReasonTooLong:
      return `The reason must be at most ${MAX_DANGEROUS_ACTION_REASON_LENGTH} characters.`
    case ErrorCodes.ReasonInvalid:
      return fieldMessage ?? 'The reason contains characters the server rejects.'
    case ErrorCodes.CalendarRecipeRequired:
      return fieldMessage ?? 'Choose a recipe for this date.'
    case ErrorCodes.CalendarMiscLabelRequired:
      return fieldMessage ?? 'Enter a label for this misc entry.'
    default:
      return fieldMessage ?? caught.message
  }
}
