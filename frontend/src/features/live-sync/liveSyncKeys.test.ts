import type { QueryClient } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'

import { calendarQueryKey } from '../calendar/useCalendar'
import {
  groceriesListQueryKey,
  groceriesQueryKey,
  groceriesStaplesQueryKey,
} from '../groceries/useGroceries'
import { recipesQueryKey } from '../recipes/useRecipes'
import {
  dashboardQueryKey,
  invalidateAllLiveSyncQueries,
  invalidateForEvent,
  liveSyncEventType,
  liveSyncInvalidationKeys,
  operationsQueryKey,
  queryKeysForEvent,
  referenceDataQueryKey,
  settingsQueryKey,
  type LiveSyncEvent,
} from './liveSyncKeys'

function createSpyQueryClient(): {
  queryClient: QueryClient
  invalidateQueries: ReturnType<typeof vi.fn>
} {
  const invalidateQueries = vi.fn().mockResolvedValue(undefined)
  const queryClient = { invalidateQueries } as unknown as QueryClient
  return { queryClient, invalidateQueries }
}

function invalidatedKeys(invalidateQueries: ReturnType<typeof vi.fn>): unknown[] {
  return invalidateQueries.mock.calls.map(
    ([options]) => (options as { queryKey: unknown }).queryKey,
  )
}

function event(type: string): LiveSyncEvent {
  return { type, entity: null, id: null, operation_id: null, actor_user_id: 3, timestamp: null }
}

describe('liveSyncKeys mapping', () => {
  it('maps calendar.updated to calendar, operations, and dashboard', () => {
    expect(queryKeysForEvent(liveSyncEventType.calendarUpdated)).toEqual([
      calendarQueryKey,
      operationsQueryKey,
      dashboardQueryKey,
    ])
  })

  it('maps calendar.lock_changed to calendar and operations only', () => {
    expect(queryKeysForEvent(liveSyncEventType.calendarLockChanged)).toEqual([
      calendarQueryKey,
      operationsQueryKey,
    ])
  })

  it('maps groceries.updated to the list and staples (never preferences)', () => {
    expect(queryKeysForEvent(liveSyncEventType.groceriesUpdated)).toEqual([
      groceriesListQueryKey,
      groceriesStaplesQueryKey,
    ])
    expect(queryKeysForEvent(liveSyncEventType.groceriesUpdated)).not.toContain(groceriesQueryKey)
  })

  it('maps recipe.updated to recipes, groceries, and calendar', () => {
    expect(queryKeysForEvent(liveSyncEventType.recipeUpdated)).toEqual([
      recipesQueryKey,
      groceriesQueryKey,
      calendarQueryKey,
    ])
  })

  it('maps settings.updated to settings, reference-data, groceries, and recipes', () => {
    expect(queryKeysForEvent(liveSyncEventType.settingsUpdated)).toEqual([
      settingsQueryKey,
      referenceDataQueryKey,
      groceriesQueryKey,
      recipesQueryKey,
    ])
  })

  it('maps session.revoked to no query keys (the provider signs out)', () => {
    expect(queryKeysForEvent(liveSyncEventType.sessionRevoked)).toEqual([])
  })

  it('treats an unknown event type as a no-op', () => {
    expect(queryKeysForEvent('not.a.real.event')).toEqual([])
  })
})

describe('invalidateForEvent', () => {
  it('invalidates exactly the documented keys and returns them', async () => {
    const { queryClient, invalidateQueries } = createSpyQueryClient()

    const keys = await invalidateForEvent(queryClient, event('groceries.updated'))

    expect(invalidateQueries).toHaveBeenCalledTimes(2)
    expect(invalidatedKeys(invalidateQueries)).toEqual([
      groceriesListQueryKey,
      groceriesStaplesQueryKey,
    ])
    expect(keys).toEqual([groceriesListQueryKey, groceriesStaplesQueryKey])
  })

  it('does not touch the query client for session.revoked', async () => {
    const { queryClient, invalidateQueries } = createSpyQueryClient()

    const keys = await invalidateForEvent(queryClient, event('session.revoked'))

    expect(invalidateQueries).not.toHaveBeenCalled()
    expect(keys).toEqual([])
  })

  it('does not touch the query client for an unknown event', async () => {
    const { queryClient, invalidateQueries } = createSpyQueryClient()

    await invalidateForEvent(queryClient, event('mystery.event'))

    expect(invalidateQueries).not.toHaveBeenCalled()
  })
})

describe('invalidateAllLiveSyncQueries', () => {
  it('runs the full pass over every distinct live-sync key once', async () => {
    const { queryClient, invalidateQueries } = createSpyQueryClient()

    const keys = await invalidateAllLiveSyncQueries(queryClient)

    // calendar, operations, dashboard, groceries/list, groceries/staples,
    // recipes, groceries, settings, reference-data.
    expect(new Set(liveSyncInvalidationKeys.map((key) => JSON.stringify(key))).size).toBe(9)
    expect(keys).toEqual(liveSyncInvalidationKeys)
    expect(invalidateQueries).toHaveBeenCalledTimes(9)
    expect(new Set(invalidatedKeys(invalidateQueries).map((key) => JSON.stringify(key))).size).toBe(
      9,
    )
  })
})
