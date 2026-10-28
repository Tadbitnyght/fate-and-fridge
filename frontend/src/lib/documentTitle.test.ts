import { describe, expect, it } from 'vitest'

import { APP_NAME, documentTitleForPath } from './documentTitle'

/**
 * Browser-tab titles. The map is the single source of truth for the
 * tab text, so this table is the specification: one row per product route.
 */
describe('documentTitleForPath', () => {
  it.each([
    ['/setup', 'Setup'],
    ['/setup/restore', 'Restore from backup'],
    ['/login', 'Sign in'],
    ['/change-password', 'Change password'],
    ['/calendar', 'Calendar'],
    ['/calendar/print', 'Print calendar'],
    ['/dashboard', 'Dashboard'],
    ['/recipes', 'Recipes'],
    ['/recipes/new', 'Add recipe'],
    ['/recipes/import', 'Import from URL'],
    ['/recipes/12/edit', 'Edit recipe'],
    ['/groceries', 'Groceries'],
    ['/groceries/print', 'Grocery list'],
    ['/statistics', 'Statistics'],
    ['/settings', 'Settings'],
    ['/settings/reference-data', 'Reference data'],
    ['/settings/reference-data/departments', 'Departments'],
    ['/settings/reference-data/units', 'Units'],
    ['/settings/reference-data/ingredients', 'Ingredients'],
    ['/settings/reference-data/proteins', 'Proteins'],
    ['/settings/reference-data/tags', 'Tags'],
    ['/settings/users', 'Users'],
    ['/settings/security', 'Security'],
    ['/settings/audit', 'Audit log'],
    ['/settings/backups', 'Backups'],
    ['/settings/account', 'Account'],
    ['/settings/about', 'About'],
  ])('titles %s as `%s · Fate & Fridge`', (pathname, page) => {
    expect(documentTitleForPath(pathname)).toBe(`${page} · Fate & Fridge`)
  })

  it('accepts a trailing slash', () => {
    expect(documentTitleForPath('/recipes/')).toBe('Recipes · Fate & Fridge')
    expect(documentTitleForPath('/settings/users/')).toBe('Users · Fate & Fridge')
  })

  it('falls back to the product name for the startup redirect and unknown paths', () => {
    expect(documentTitleForPath('/')).toBe(APP_NAME)
    expect(documentTitleForPath('/somewhere-else')).toBe(APP_NAME)
  })
})
