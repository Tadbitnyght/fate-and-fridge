import type { CalendarDay } from '../../api/calendar'

export interface RerollAvailability {
  disabled: boolean
  /** Human-readable reason, shown as a keyboard/AT-reachable explanation. */
  reason: string | null
}

/**
 * UI-side reroll availability (M6-10). Mirrors docs/M6_PLAN.md §1.11 and
 * docs/API.md "Reroll (M6)": historical dates, locked entries, vacation days,
 * and actors without `calendar.edit` cannot reroll. This is presentation only —
 * the server re-checks every rule (docs/M5_PLAN.md §1.9).
 */
export function rerollAvailability(day: CalendarDay | null, canEdit: boolean): RerollAvailability {
  if (!canEdit) {
    return {
      disabled: true,
      reason: 'You need the calendar.edit permission to reroll meals.',
    }
  }
  if (!day) {
    return { disabled: true, reason: 'Loading this date…' }
  }
  if (day.is_historical || !day.can_edit) {
    return { disabled: true, reason: 'Past dates cannot be rerolled.' }
  }
  if (day.entry?.entry_type === 'vacation') {
    return { disabled: true, reason: 'Vacation days cannot be rerolled.' }
  }
  if (day.entry?.is_locked) {
    return { disabled: true, reason: 'Unlock this date before rerolling it.' }
  }
  return { disabled: false, reason: null }
}
