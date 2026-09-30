/** Shared payload fixtures for the admin-console tests (M1-14). */

export const ALL_PERMISSIONS = [
  'calendar.view',
  'calendar.edit',
  'groceries.view',
  'groceries.edit',
  'recipes.view',
  'recipes.edit',
  'reference_data.edit',
]

export const setupCompleteStatus = {
  setup_completed: true,
  auth_mode: 'multiuser',
  recovery_key_present: true,
}

export const singleUserSetupStatus = {
  setup_completed: true,
  auth_mode: 'single_user',
  recovery_key_present: true,
}

export const adminSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'root', role: 'admin' },
  permissions: ALL_PERMISSIONS,
  must_change_password: false,
}

export const memberSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 2, username: 'sam', role: 'member' },
  permissions: ['calendar.view', 'groceries.view', 'recipes.view'],
  must_change_password: false,
}

export const singleUserSession = {
  authenticated: true,
  auth_mode: 'single_user',
  user: null,
  permissions: ALL_PERMISSIONS,
  must_change_password: false,
}

export const householdSettings = {
  household_name: 'The Smiths',
  timezone: 'America/Chicago',
  week_starts_on: 0,
  default_diner_count: 4,
  startup_page: 'calendar',
}

export const securitySettings = {
  ip_allowlist_enabled: false,
  ip_allowlist_ranges: [],
  trusted_proxies: [],
}

export const adminUserRecord = {
  id: 1,
  username: 'root',
  role: 'admin',
  is_active: true,
  must_change_password: false,
  last_login_at: '2026-09-29T12:00:00Z',
  permissions: ALL_PERMISSIONS,
}

export const memberUserRecord = {
  id: 2,
  username: 'sam',
  role: 'member',
  is_active: true,
  must_change_password: false,
  last_login_at: null,
  permissions: ['calendar.view', 'groceries.view', 'recipes.view'],
}
