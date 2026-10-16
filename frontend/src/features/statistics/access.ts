import { useAuth } from '../auth/AuthContext'
import { useAdminAccess } from '../settings/access'

export interface StatisticsAccess {
  /** May read the overview (`recipes.view`). */
  canViewOverview: boolean
  /** May read the meal-history browser (`calendar.view`; M11-05). */
  canViewHistory: boolean
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: the statistics endpoints enforce `recipes.view` /
 * `calendar.view` server-side (docs/M11_PLAN.md §1.7, §1.30). This hook must
 * never be the sole gate for a read.
 */
export function useStatisticsAccess(): StatisticsAccess {
  const { permissions } = useAuth()
  const { isAdmin, isSingleUser } = useAdminAccess()
  const canViewOverview = isSingleUser || isAdmin || permissions.includes('recipes.view')
  return {
    canViewOverview,
    canViewHistory: isSingleUser || isAdmin || permissions.includes('calendar.view'),
    isAdmin,
    isSingleUser,
  }
}
