import { useAuth } from '../auth/AuthContext'
import { useAdminAccess } from '../settings/access'

export interface ReferenceDataAccess {
  /** May create/rename/retire/reorder (the `reference_data.edit` key). */
  canEdit: boolean
  /** May hard-delete; deletion is Admin-only (docs/API.md §22). */
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: every reference-data write is enforced server-side
 * (docs/SECURITY.md §4). Reads are open to any authenticated actor.
 */
export function useReferenceDataAccess(): ReferenceDataAccess {
  const { permissions } = useAuth()
  const { isAdmin, isSingleUser } = useAdminAccess()
  return {
    canEdit: isSingleUser || isAdmin || permissions.includes('reference_data.edit'),
    isAdmin,
    isSingleUser,
  }
}
