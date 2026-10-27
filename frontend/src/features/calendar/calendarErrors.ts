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

/** Maps a failed batch preview (M7) to a user-facing, non-blocking message. */
export function describeBatchPreviewError(caught: unknown): string {
  if (isApiError(caught)) {
    switch (caught.code) {
      case ErrorCodes.CalendarDateInPast:
        return 'The range starts in the past. Choose today or a later date.'
      case ErrorCodes.RandomizerNoCandidates:
        return 'No Normal recipes are available to reroll. Add a Normal recipe first.'
      case ErrorCodes.ValidationFailed:
        return 'This range is not valid. Choose a different mode or start date.'
      case ErrorCodes.Forbidden:
        return 'You do not have permission to reroll meals.'
      default:
        return caught.message
    }
  }
  return 'Could not build a preview. Try again.'
}

/** Maps a failed batch apply (M7) to a user-facing, non-blocking message. */
export function describeBatchApplyError(caught: unknown): string {
  if (isApiError(caught)) {
    switch (caught.code) {
      case ErrorCodes.BatchConflict:
        return 'A date in this range changed since the preview. Try another.'
      case ErrorCodes.BatchDraftExpired:
        return 'This preview expired or was already applied. Try another.'
      case ErrorCodes.CalendarDateInPast:
        return 'Part of this range is now in the past and cannot be changed.'
      case ErrorCodes.RandomizerNoCandidates:
        return 'No Normal recipes are available to reroll. Add a Normal recipe first.'
      case ErrorCodes.Forbidden:
        return 'You do not have permission to reroll meals.'
      default:
        return caught.message
    }
  }
  return 'Could not apply the preview. Try again.'
}

/** Maps a failed batch undo (M7) to a user-facing, non-blocking message. */
export function describeBatchUndoError(caught: unknown): string {
  if (isApiError(caught)) {
    switch (caught.code) {
      case ErrorCodes.UndoConflict:
        return 'A date changed after the batch reroll, so it cannot be undone.'
      case ErrorCodes.UndoExpired:
        return 'This batch can no longer be undone.'
      case ErrorCodes.CalendarDateInPast:
        return 'Part of this batch is now historical, so it cannot be undone.'
      case ErrorCodes.Forbidden:
        return 'You do not have permission to undo.'
      default:
        return caught.message
    }
  }
  return 'Undo failed. Try again.'
}
