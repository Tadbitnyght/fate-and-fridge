/** Product name used in the browser tab (docs/PRODUCT_SPEC.md). */
export const APP_NAME = 'Fate & Fridge'

/**
 * Browser-tab titles by route, most specific first.
 * Each label matches the heading the route renders, so the tab and the page
 * say the same thing. Patterns are anchored and accept an optional trailing
 * slash; unknown paths (the startup redirect, the catch-all) fall back to the
 * product name alone.
 */
const ROUTE_TITLES: ReadonlyArray<readonly [RegExp, string]> = [
  [/^\/setup\/restore\/?$/, 'Restore from backup'],
  [/^\/setup\/?$/, 'Setup'],
  [/^\/login\/?$/, 'Sign in'],
  [/^\/change-password\/?$/, 'Change password'],
  [/^\/calendar\/print\/?$/, 'Print calendar'],
  [/^\/calendar\/?$/, 'Calendar'],
  [/^\/dashboard\/?$/, 'Dashboard'],
  [/^\/recipes\/new\/?$/, 'Add recipe'],
  [/^\/recipes\/import\/?$/, 'Import from URL'],
  [/^\/recipes\/[^/]+\/edit\/?$/, 'Edit recipe'],
  [/^\/recipes\/?$/, 'Recipes'],
  [/^\/groceries\/print\/?$/, 'Grocery list'],
  [/^\/groceries\/?$/, 'Groceries'],
  [/^\/statistics\/?$/, 'Statistics'],
  [/^\/settings\/reference-data\/departments\/?$/, 'Departments'],
  [/^\/settings\/reference-data\/units\/?$/, 'Units'],
  [/^\/settings\/reference-data\/ingredients\/?$/, 'Ingredients'],
  [/^\/settings\/reference-data\/proteins\/?$/, 'Proteins'],
  [/^\/settings\/reference-data\/tags\/?$/, 'Tags'],
  [/^\/settings\/reference-data\/?$/, 'Reference data'],
  [/^\/settings\/users\/?$/, 'Users'],
  [/^\/settings\/security\/?$/, 'Security'],
  [/^\/settings\/audit\/?$/, 'Audit log'],
  [/^\/settings\/backups\/?$/, 'Backups'],
  [/^\/settings\/account\/?$/, 'Account'],
  [/^\/settings\/about\/?$/, 'About'],
  [/^\/settings\/?$/, 'Settings'],
]

/** `<Page> · Fate & Fridge` for a router pathname. */
export function documentTitleForPath(pathname: string): string {
  for (const [pattern, title] of ROUTE_TITLES) {
    if (pattern.test(pathname)) {
      return `${title} · ${APP_NAME}`
    }
  }

  return APP_NAME
}
