import { useAuth } from '../auth/AuthContext'
import { useAdminAccess } from '../settings/access'

export interface CalendarAccess {
  /** May create/edit entries and lock dates (the `calendar.edit` key). */
  canEdit: boolean
  /** May read the calendar (`calendar.view`, implied by edit server-side). */
  canView: boolean
  isAdmin: boolean
  isSingleUser: boolean
}

/**
 * UI visibility only: every calendar write is enforced server-side
 * (docs/M5_PLAN.md §1.9). This hook must never be the sole gate for an action.
 */
export function useCalendarAccess(): CalendarAccess {
  const { permissions } = useAuth()
  const { isAdmin, isSingleUser } = useAdminAccess()
  const canEdit = isSingleUser || isAdmin || permissions.includes('calendar.edit')
  return {
    canEdit,
    canView: canEdit || permissions.includes('calendar.view'),
    isAdmin,
    isSingleUser,
  }
}
