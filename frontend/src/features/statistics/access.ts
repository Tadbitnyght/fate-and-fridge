import { useAuth } from '../auth/AuthContext'
import { useAdminAccess } from '../settings/access'

export interface StatisticsAccess {
  /** May read the overview (`recipes.view`, or `recipes.edit` via edit-implies-view). */
  canViewOverview: boolean
  /** May read the meal-history browser (`calendar.view`, or `calendar.edit`; M11-05). */
  canViewHistory: boolean
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: the statistics endpoints enforce `recipes.view` /
 * `calendar.view` server-side (docs/M11_PLAN.md §1.7, §1.30). This hook must
 * never be the sole gate for a read.
 *
 * The gates mirror the server's edit-implies-view rule
 * (`PermissionEvaluator.ForMember`): a member holding `recipes.edit` (or
 * `calendar.edit`) is treated as holding the matching `.view` key, so the
 * content is hidden only when the member holds neither key.
 */
export function useStatisticsAccess(): StatisticsAccess {
  const { permissions } = useAuth()
  const { isAdmin, isSingleUser } = useAdminAccess()
  const canViewOverview =
    isSingleUser ||
    isAdmin ||
    permissions.includes('recipes.view') ||
    permissions.includes('recipes.edit')
  return {
    canViewOverview,
    canViewHistory:
      isSingleUser ||
      isAdmin ||
      permissions.includes('calendar.view') ||
      permissions.includes('calendar.edit'),
    isAdmin,
    isSingleUser,
  }
}
