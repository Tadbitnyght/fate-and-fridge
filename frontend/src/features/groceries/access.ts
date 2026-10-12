import { useAuth } from '../auth/AuthContext'
import { useAdminAccess } from '../settings/access'

export interface GroceriesAccess {
  /** May add manual items, manage staples, and check/uncheck (`groceries.edit`). */
  canEdit: boolean
  /** May read the list and write per-user preferences (`groceries.view`). */
  canView: boolean
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: every grocery write is enforced server-side
 * (docs/M8_PLAN.md §1.13). This hook must never be the sole gate for an action.
 */
export function useGroceriesAccess(): GroceriesAccess {
  const { permissions } = useAuth()
  const { isAdmin, isSingleUser } = useAdminAccess()
  const canEdit = isSingleUser || isAdmin || permissions.includes('groceries.edit')
  return {
    canEdit,
    canView: canEdit || permissions.includes('groceries.view'),
    isAdmin,
    isSingleUser,
  }
}
