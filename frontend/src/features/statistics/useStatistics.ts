import { useQuery } from '@tanstack/react-query'

import {
  getMealHistory,
  getStatisticsOverview,
  type MealHistoryParams,
  type MealHistoryResponse,
  type StatisticsOverview,
} from '../../api/statistics'

/**
 * Query-key prefix for every statistics read. M11-05's meal-history browser
 * adds siblings under it, so live-sync invalidation can target the whole
 * prefix.
 */
export const statisticsQueryKey = ['statistics'] as const

export const statisticsOverviewQueryKey = [...statisticsQueryKey, 'overview'] as const

export const mealHistoryQueryKey = [...statisticsQueryKey, 'history'] as const

/** The household overview (docs/API.md §14); `recipes.view` gates the read. */
export function useStatisticsOverview(enabled = true) {
  return useQuery<StatisticsOverview>({
    queryKey: statisticsOverviewQueryKey,
    queryFn: getStatisticsOverview,
    retry: false,
    enabled,
  })
}

/**
 * One month page of the read-only meal log (docs/API.md §14). The params object
 * is part of the key, so each filter/month combination caches separately;
 * `calendar.view` gates the read (docs/M11_PLAN.md §1.7).
 */
export function useMealHistory(params: MealHistoryParams = {}, enabled = true) {
  return useQuery<MealHistoryResponse>({
    queryKey: [...mealHistoryQueryKey, params],
    queryFn: () => getMealHistory(params),
    retry: false,
    enabled,
  })
}
