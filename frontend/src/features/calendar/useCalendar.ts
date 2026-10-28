import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  applyBatchReroll,
  checkCalendarRepeat,
  correctCalendarDay,
  deleteCalendarDay,
  getCalendarDay,
  getCalendarRange,
  lockCalendarDay,
  previewBatchReroll,
  putCalendarDay,
  rerollCalendarDay,
  undoBatchOperation,
  undoCalendarOperation,
  unlockCalendarDay,
  type BatchRerollMode,
  type CalendarCorrectionRequest,
  type CalendarWriteRequest,
} from '../../api/calendar'
import { auditQueryKey } from '../settings/useAuditLog'
import { statisticsQueryKey } from '../statistics/useStatistics'

/** Query key prefix for every calendar read (invalidation target). */
export const calendarQueryKey = ['calendar'] as const

export function useCalendarRange(start: string, end: string) {
  return useQuery({
    queryKey: [...calendarQueryKey, 'range', start, end],
    queryFn: () => getCalendarRange(start, end),
  })
}

export function useCalendarDay(date: string | null) {
  return useQuery({
    queryKey: [...calendarQueryKey, 'day', date],
    queryFn: () => getCalendarDay(date as string),
    enabled: date !== null,
  })
}

export function useCalendarRepeatCheck(date: string, recipeId: number | null) {
  return useQuery({
    queryKey: [...calendarQueryKey, 'repeat', date, recipeId],
    queryFn: () => checkCalendarRepeat(date, recipeId as number),
    enabled: recipeId !== null,
  })
}

function useInvalidateCalendar() {
  const queryClient = useQueryClient()
  return async () => {
    await queryClient.invalidateQueries({ queryKey: calendarQueryKey })
  }
}

export function useSaveCalendarDay() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: ({ date, request }: { date: string; request: CalendarWriteRequest }) =>
      putCalendarDay(date, request),
    onSuccess: invalidate,
  })
}

export function useDeleteCalendarDay() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: (date: string) => deleteCalendarDay(date),
    onSuccess: invalidate,
  })
}

export function useSetCalendarLock() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: ({ date, locked }: { date: string; locked: boolean }) =>
      locked ? lockCalendarDay(date) : unlockCalendarDay(date),
    onSuccess: invalidate,
  })
}

/**
 * Admin-only correction of one past date (docs/API.md §17). On success
 * the calendar, statistics (meal history/overview), and audit queries are all
 * invalidated locally, so the corrected row updates immediately even before the
 * coarse `calendar.updated` live-sync event arrives.
 */
export function useCorrectCalendarEntry() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ date, request }: { date: string; request: CalendarCorrectionRequest }) =>
      correctCalendarDay(date, request),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: calendarQueryKey }),
        queryClient.invalidateQueries({ queryKey: statisticsQueryKey }),
        queryClient.invalidateQueries({ queryKey: auditQueryKey }),
      ])
    },
  })
}

/** Rerolls a single date with a random Normal recipe (M6). */
export function useRerollCalendarDay() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: (date: string) => rerollCalendarDay(date),
    onSuccess: invalidate,
  })
}

/** Undoes a reroll operation, restoring the prior day state (M6). */
export function useUndoCalendarOperation() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: (operationId: string) => undoCalendarOperation(operationId),
    onSuccess: invalidate,
  })
}

/**
 * Builds a batch reroll preview draft (M7). Preview persists no calendar state
 * so it does not invalidate any query; Try Another simply replaces the draft.
 */
export function usePreviewBatchReroll() {
  return useMutation({
    mutationFn: ({ startDate, mode }: { startDate: string; mode: BatchRerollMode }) =>
      previewBatchReroll({ startDate, mode }),
  })
}

/** Applies a batch reroll draft and refreshes the calendar (M7). */
export function useApplyBatchReroll() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: (token: string) => applyBatchReroll(token),
    onSuccess: invalidate,
  })
}

/** Reverts an entire batch apply and refreshes the calendar (M7). */
export function useUndoBatchOperation() {
  const invalidate = useInvalidateCalendar()
  return useMutation({
    mutationFn: (batchOperationId: string) => undoBatchOperation(batchOperationId),
    onSuccess: invalidate,
  })
}
