import type { QueryClient, QueryKey } from '@tanstack/react-query'

import { calendarQueryKey } from '../calendar/useCalendar'
import {
  groceriesListQueryKey,
  groceriesQueryKey,
  groceriesStaplesQueryKey,
} from '../groceries/useGroceries'
import { recipesQueryKey } from '../recipes/useRecipes'
import { statisticsQueryKey } from '../statistics/useStatistics'

/**
 * Live-sync event → query-key invalidation mapping. The hub envelope is coarse: a client only
 * invalidates/refetches, it never applies an entity body. This module is the
 * single place that knows which queries an event affects, so no component
 * talks to the socket and no view duplicates server state from events.
 * | Event | Invalidated query keys |
 * |-------------------------|----------------------------------------------|
 * | `calendar.updated` | calendar, operations, dashboard, statistics |
 * | `calendar.lock_changed` | calendar, operations |
 * | `groceries.updated` | groceries (list + staples) |
 * | `recipe.updated` | recipes, groceries, calendar, statistics |
 * | `settings.updated` | settings, reference-data, groceries, recipes |
 * | `session.revoked` | force sign-out (handled by the provider) |
 * Statistics (overview + meal history) are on-demand reads derived from
 * calendar entries and recipes, so both events invalidate them.
 */

/** The six stable envelope types. */
export const liveSyncEventType = {
  calendarUpdated: 'calendar.updated',
  calendarLockChanged: 'calendar.lock_changed',
  groceriesUpdated: 'groceries.updated',
  recipeUpdated: 'recipe.updated',
  settingsUpdated: 'settings.updated',
  sessionRevoked: 'session.revoked',
} as const

export type LiveSyncEventType = (typeof liveSyncEventType)[keyof typeof liveSyncEventType]

/**
 * Coarse, stable envelope. Only `type` is required
 * the rest are nullable as applicable.
 */
export interface LiveSyncEvent {
  type: LiveSyncEventType | string
  entity?: string | null
  id?: string | null
  operation_id?: string | null
  actor_user_id?: number | null
  timestamp?: string | null
}

/**
 * Prefixes that are documented invalidation targets but have no query hook yet
 * (`operations`, `dashboard`) plus the settings/reference-data roots. The
 * existing hooks expose `calendar`, `recipes`, and the groceries keys; the
 * `settings`/`reference-data` prefixes are the stable roots of
 * `useHouseholdSettings`/`useSecuritySettings` and `useReferenceData`.
 */
export const operationsQueryKey = ['operations'] as const
export const dashboardQueryKey = ['dashboard'] as const
export const settingsQueryKey = ['settings'] as const
export const referenceDataQueryKey = ['reference-data'] as const

/**
 * The documented event → query-key mapping. `session.revoked` maps to no
 * query: it is a forced sign-out, handled by `LiveSyncProvider`.
 */
export const liveSyncInvalidationMap: Record<LiveSyncEventType, readonly QueryKey[]> = {
  [liveSyncEventType.calendarUpdated]: [
    calendarQueryKey,
    operationsQueryKey,
    dashboardQueryKey,
    statisticsQueryKey,
  ],
  [liveSyncEventType.calendarLockChanged]: [calendarQueryKey, operationsQueryKey],
  [liveSyncEventType.groceriesUpdated]: [groceriesListQueryKey, groceriesStaplesQueryKey],
  [liveSyncEventType.recipeUpdated]: [
    recipesQueryKey,
    groceriesQueryKey,
    calendarQueryKey,
    statisticsQueryKey,
  ],
  [liveSyncEventType.settingsUpdated]: [
    settingsQueryKey,
    referenceDataQueryKey,
    groceriesQueryKey,
    recipesQueryKey,
  ],
  [liveSyncEventType.sessionRevoked]: [],
}

/** The keys an event invalidates (empty for an unknown or revoked event). */
export function queryKeysForEvent(type: string): readonly QueryKey[] {
  return liveSyncInvalidationMap[type as LiveSyncEventType] ?? []
}

/**
 * Invalidates exactly the keys mapped to `event.type` and returns them, so the
 * mapping is unit-testable without a live socket. Unknown types are a no-op.
 */
export async function invalidateForEvent(
  queryClient: QueryClient,
  event: LiveSyncEvent,
): Promise<readonly QueryKey[]> {
  const keys = queryKeysForEvent(event.type)
  await Promise.all(keys.map((queryKey) => queryClient.invalidateQueries({ queryKey })))
  return keys
}

/**
 * Every distinct key across the whole mapping. Used for the full invalidation
 * pass on each (re)connect, so anything missed while
 * offline is reconciled.
 */
export const liveSyncInvalidationKeys: readonly QueryKey[] = [
  ...new Set(
    Object.values(liveSyncInvalidationMap)
      .flat()
      .map((queryKey) => JSON.stringify(queryKey)),
  ),
].map((serialized) => JSON.parse(serialized) as QueryKey)

/** Runs the full live-sync invalidation pass and returns the keys touched. */
export async function invalidateAllLiveSyncQueries(
  queryClient: QueryClient,
): Promise<readonly QueryKey[]> {
  await Promise.all(
    liveSyncInvalidationKeys.map((queryKey) => queryClient.invalidateQueries({ queryKey })),
  )
  return liveSyncInvalidationKeys
}
