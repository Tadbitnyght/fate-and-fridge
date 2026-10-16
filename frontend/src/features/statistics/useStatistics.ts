import { useQuery } from '@tanstack/react-query'

import { getStatisticsOverview, type StatisticsOverview } from '../../api/statistics'

/**
 * Query-key prefix for every statistics read. M11-05's meal-history browser
 * adds siblings under it, so live-sync invalidation can target the whole
 * prefix.
 */
export const statisticsQueryKey = ['statistics'] as const

export const statisticsOverviewQueryKey = [...statisticsQueryKey, 'overview'] as const

/** The household overview (docs/API.md §14); `recipes.view` gates the read. */
export function useStatisticsOverview(enabled = true) {
  return useQuery<StatisticsOverview>({
    queryKey: statisticsOverviewQueryKey,
    queryFn: getStatisticsOverview,
    retry: false,
    enabled,
  })
}
