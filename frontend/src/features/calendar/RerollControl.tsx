import { useId, useState } from 'react'

import type { CalendarDay, CalendarOperation } from '../../api/calendar'
import { describeRerollError } from './calendarErrors'
import { rerollAvailability } from './rerollAvailability'
import { useRerollCalendarDay } from './useCalendar'

/**
 * Reroll action for the date-detail dialog (M6-10). Disabled with an
 * AT/keyboard-reachable explanation on historical, locked, and vacation dates,
 * and without `calendar.edit`. On success the operation is handed up so the
 * page can surface the Undo banner.
 */
export function RerollControl({
  day,
  canEdit,
  onRerolled,
}: {
  day: CalendarDay
  canEdit: boolean
  onRerolled: (operation: CalendarOperation) => void
}) {
  const rerollMutation = useRerollCalendarDay()
  const [error, setError] = useState<string | null>(null)
  const { disabled, reason } = rerollAvailability(day, canEdit)
  const reasonId = useId()

  async function handleReroll(): Promise<void> {
    setError(null)
    try {
      const result = await rerollMutation.mutateAsync(day.date)
      onRerolled(result.operation)
    } catch (caught) {
      setError(describeRerollError(caught))
    }
  }

  return (
    <div className="calendar-reroll">
      <button
        type="button"
        onClick={() => void handleReroll()}
        disabled={disabled || rerollMutation.isPending}
        title={reason ?? undefined}
        aria-describedby={reason ? reasonId : undefined}
      >
        {rerollMutation.isPending ? 'Rerolling…' : 'Reroll'}
      </button>
      {reason && (
        <p id={reasonId} className="form__hint">
          {reason}
        </p>
      )}
      {error !== null && (
        <p className="form__error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
