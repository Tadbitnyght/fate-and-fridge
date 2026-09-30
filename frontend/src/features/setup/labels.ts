import type { AuthMode } from '../../api/auth'
import type { StartupPage } from '../../api/setup'

export const WEEK_START_LABELS = [
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
]

export const STARTUP_PAGE_LABELS: Record<StartupPage, string> = {
  calendar: 'Calendar',
  dashboard: 'Dashboard',
}

export const AUTH_MODE_LABELS: Record<AuthMode, string> = {
  single_user: 'Single user (no sign-in)',
  multiuser: 'Multiuser (accounts and sign-in)',
}

export function authModeLabel(mode: AuthMode): string {
  return AUTH_MODE_LABELS[mode]
}
