import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'

/** Maps a failed reroll (M6) to a user-facing, non-blocking message. */
export function describeRerollError(caught: unknown): string {
  if (isApiError(caught)) {
    switch (caught.code) {
      case ErrorCodes.CalendarDateInPast:
        return 'Past dates cannot be rerolled.'
      case ErrorCodes.CalendarEntryLocked:
        return 'This date is locked. Unlock it, then reroll.'
      case ErrorCodes.RandomizerNotApplicable:
        return 'Vacation days cannot be rerolled.'
      case ErrorCodes.RandomizerNoCandidates:
        return 'No Normal recipes are available to reroll. Add a Normal recipe first.'
      case ErrorCodes.Forbidden:
        return 'You do not have permission to reroll meals.'
      default:
        return caught.message
    }
  }
  return 'Reroll failed. Try again.'
}

/** Maps a failed undo (M6) to a user-facing, non-blocking message. */
export function describeUndoError(caught: unknown): string {
  if (isApiError(caught)) {
    switch (caught.code) {
      case ErrorCodes.UndoConflict:
        return 'This date changed after the reroll, so it cannot be undone.'
      case ErrorCodes.UndoExpired:
        return 'This reroll can no longer be undone.'
      case ErrorCodes.Forbidden:
        return 'You do not have permission to undo.'
      default:
        return caught.message
    }
  }
  return 'Undo failed. Try again.'
}
