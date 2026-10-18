import { expect, test, type Page } from '@playwright/test'

/**
 * Admin audit viewer (M12-03) against a mocked API, matching the hermetic
 * pattern of the other e2e specs: no real backend, but the real Vite bundle,
 * routing, and rendering.
 */

const session = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'root', role: 'admin' },
  permissions: [
    'calendar.view',
    'calendar.edit',
    'groceries.view',
    'groceries.edit',
    'recipes.view',
    'recipes.edit',
    'reference_data.edit',
  ],
  must_change_password: false,
}

const auditItem = {
  id: 42,
  source: 'audit',
  recorded_at: '2026-11-15T18:00:00+00:00',
  actor_user_id: 1,
  actor_username: 'root',
  event_type: 'calendar.updated',
  entity_type: 'calendar_entry',
  entity_id: '7',
  summary: 'Calendar entry changed',
  reason: 'Swapped dinner',
  ip_address: '203.0.113.11',
  before: { entry_type: 'eat_out' },
  after: { entry_type: 'recipe' },
  metadata: { meal_date: '2026-11-15' },
}

const historyItem = {
  id: 9,
  source: 'entry_history',
  recorded_at: '2026-11-14T12:30:00+00:00',
  actor_user_id: 1,
  actor_username: 'root',
  event_type: 'calendar.reroll',
  entity_type: 'calendar_entry',
  entity_id: '5',
  summary: 'Rerolled the week',
  reason: null,
  ip_address: null,
  before: null,
  after: { days: ['2026-11-20'] },
  metadata: { kind: 'reroll' },
}

/** Hermetic audit API: session, users for the actor picker, and the log. */
async function mockApi(page: Page, searches: string[]): Promise<void> {
  await page.route('**/api/v1/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname

    if (path.endsWith('/setup/status')) {
      await route.fulfill({
        json: { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true },
      })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: session })
      return
    }
    if (path.endsWith('/users')) {
      await route.fulfill({
        json: [
          {
            id: 1,
            username: 'root',
            role: 'admin',
            is_active: true,
            must_change_password: false,
            last_login_at: null,
            permissions: session.permissions,
          },
        ],
      })
      return
    }
    if (path.endsWith('/admin/audit')) {
      searches.push(url.search)
      const filtered = url.searchParams.get('event_type') === 'calendar.created'
      await route.fulfill({
        json: { items: filtered ? [auditItem] : [auditItem, historyItem], next_cursor: null },
      })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${path}`, details: {} } },
    })
  })
}

test('reads the admin audit log, expands a detail, and filters it', async ({ page }) => {
  const searches: string[] = []
  await mockApi(page, searches)

  await page.goto('/settings/audit')

  // Reachable from the settings navigation and rendered as a read-only log.
  await expect(page.getByRole('link', { name: 'Audit log' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible()

  const list = page.getByRole('list', { name: 'Audit entries' })
  await expect(list.getByRole('listitem')).toHaveCount(2)
  await expect(list.getByText('Calendar entry updated')).toBeVisible()
  await expect(list.getByText('Audit', { exact: true })).toBeVisible()
  // The reroll record is visibly labelled as coming from the entry history.
  await expect(list.getByText('Calendar history')).toBeVisible()
  await expect(list.getByText('Reroll record')).toBeVisible()

  // The detail toggle is a real button and reveals the stored before/after.
  const toggle = list.getByRole('button', { name: 'Details' }).first()
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  await expect(list.getByRole('heading', { name: 'Before' }).first()).toBeVisible()
  await expect(list.getByText(/"entry_type": "eat_out"/).first()).toBeVisible()

  // A filter is applied server-side and returns to the default page.
  await page.getByLabel('Event type').selectOption('calendar.created')
  await expect(list.getByRole('listitem')).toHaveCount(1)
  expect(searches.at(-1)).toContain('event_type=calendar.created')
  expect(searches.at(-1)).not.toContain('cursor=')

  // Read-only: no row-level edit or delete affordance exists anywhere.
  await expect(page.getByRole('button', { name: /edit|delete|remove/i })).toHaveCount(0)
  await expect(page.getByRole('link', { name: /edit|delete|remove/i })).toHaveCount(0)
})

test('keeps the audit log usable on a mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApi(page, [])

  await page.goto('/settings/audit')

  await expect(page.getByRole('list', { name: 'Audit entries' })).toBeVisible()
  await expect(page.getByLabel('Source')).toBeVisible()

  const hasOverflow = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth,
  )
  expect(hasOverflow).toBe(false)
})
