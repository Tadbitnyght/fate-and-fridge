import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  checkCalendarRepeat,
  deleteCalendarDay,
  getCalendarDay,
  getCalendarRange,
  lockCalendarDay,
  putCalendarDay,
  rerollCalendarDay,
  undoCalendarOperation,
  unlockCalendarDay,
  type CalendarWriteRequest,
} from '../../api/calendar'

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
