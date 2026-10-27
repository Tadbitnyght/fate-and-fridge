import { useAuth } from '../auth/AuthContext'
import { useAdminAccess } from '../settings/access'

export interface RecipeAccess {
  /** May create/edit recipes and manage images (the `recipes.edit` key). */
  canEdit: boolean
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: every recipe write is enforced server-side
 * (docs/SECURITY.md §4, docs/API.md §23). This hook must never be the sole
 * gate for an action.
 */
export function useRecipeAccess(): RecipeAccess {
  const { permissions } = useAuth()
  const { isAdmin, isSingleUser } = useAdminAccess()
  return {
    canEdit: isSingleUser || isAdmin || permissions.includes('recipes.edit'),
    isAdmin,
    isSingleUser,
  }
}
