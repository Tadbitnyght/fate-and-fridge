import { expect, test, type Page } from '@playwright/test'

/**
 * Statistics + meal-history UI (M11-04/M11-05) against a mocked API, matching
 * the hermetic pattern of the other e2e specs: no real backend, but the real
 * Vite bundle, routing, and rendering.
 */

const session = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'smith', role: 'admin' },
  permissions: ['calendar.view', 'recipes.view'],
  must_change_password: false,
}

const overview = {
  windows: {
    today: '2026-10-17',
    timezone: 'America/Chicago',
    month: { label: 'October 2026', start: '2026-10-01', end: '2026-10-31' },
    year: { label: '2026', start: '2026-01-01', end: '2026-12-31' },
  },
  most_eaten: [
    { recipe_id: 5, name: 'Tacos', status: 'normal', times_eaten: 9, last_eaten: '2026-10-15' },
  ],
  least_recently_eaten: [
    {
      recipe_id: null,
      name: "Grandma's Stew",
      status: 'deleted',
      times_eaten: 3,
      last_eaten: '2025-12-24',
    },
  ],
  never_eaten: 42,
  meal_count_month: 12,
  meal_count_year: 140,
  protein_distribution: {
    proteins: [{ protein_id: 2, name: 'Chicken', count: 30 }],
    no_primary_protein: 7,
  },
  special_event_counts: [
    { entry_type: 'eat_out', count: 4 },
    { entry_type: 'host_family_dinner', count: 1 },
    { entry_type: 'go_to_family_dinner', count: 2 },
    { entry_type: 'holiday', count: 1 },
    { entry_type: 'misc', count: 0 },
    { entry_type: 'fend_for_yourself', count: 6 },
    { entry_type: 'vacation', count: 1 },
  ],
}

const october = {
  range: { start: '2025-11-01', end: '2026-10-16', timezone: 'America/Chicago' },
  month: { key: '2026-10', label: 'October 2026', start: '2026-10-01', end: '2026-10-31' },
  days: [
    { date: '2026-10-15', entry_type: 'recipe', recipe_id: 5, name: 'Tacos', protein: 'Chicken' },
    {
      date: '2026-10-14',
      entry_type: 'eat_out',
      recipe_id: null,
      name: 'Sushi place',
      protein: null,
    },
  ],
  paging: { newer: null, older: '2026-09' },
}

const september = {
  range: { start: '2025-11-01', end: '2026-10-16', timezone: 'America/Chicago' },
  month: { key: '2026-09', label: 'September 2026', start: '2026-09-01', end: '2026-09-30' },
  days: [],
  paging: { newer: '2026-10', older: null },
}

async function mockApi(page: Page, requestSearches: string[]): Promise<void> {
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
    if (path.endsWith('/statistics/overview')) {
      await route.fulfill({ json: overview })
      return
    }
    if (path.endsWith('/statistics/history')) {
      requestSearches.push(url.search)
      await route.fulfill({
        json: url.searchParams.get('month') === '2026-09' ? september : october,
      })
      return
    }
    if (path.endsWith('/proteins')) {
      await route.fulfill({ json: [{ id: 2, name: 'Chicken', is_active: true }] })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${path}`, details: {} } },
    })
  })
}

test('browses the read-only meal history from the Statistics History tab', async ({ page }) => {
  const historySearches: string[] = []
  await mockApi(page, historySearches)

  await page.goto('/statistics')

  // Overview is the default view.
  await expect(page.getByRole('heading', { level: 2, name: 'Statistics' })).toBeVisible()
  await expect(page.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByText('Eaten 9 times')).toBeVisible()

  await page.getByRole('tab', { name: 'History' }).click()

  // The newest month loads with no `month` parameter (the API default).
  const month = page.getByRole('region', { name: 'October 2026' })
  await expect(month).toBeVisible()
  expect(historySearches).toHaveLength(1)
  expect(historySearches[0]).not.toContain('month=')

  await expect(month.getByRole('listitem')).toHaveCount(2)
  await expect(month.getByRole('link', { name: 'Tacos' })).toHaveAttribute(
    'href',
    '/recipes/5/edit',
  )
  // A no-recipe day and a no-primary-protein day are both labelled explicitly.
  await expect(month.getByText('Sushi place')).toBeVisible()
  await expect(month.getByText('No primary protein')).toBeVisible()

  // Paging to the older month asks for it and disables the end control.
  await page.getByRole('button', { name: 'Older month: Sep 2026' }).click()
  await expect(page.getByRole('region', { name: 'September 2026' })).toBeVisible()
  await expect(page.getByText('No meals logged in this month.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Older month' })).toBeDisabled()
  expect(historySearches.at(-1)).toContain('month=2026-09')

  // A filter is applied server-side and returns to the default page.
  await page.getByLabel('Entry type').selectOption('eat_out')
  await expect(page.getByRole('region', { name: 'October 2026' })).toBeVisible()
  expect(historySearches.at(-1)).toContain('entry_type=eat_out')
  expect(historySearches.at(-1)).not.toContain('month=')

  // Read-only: the only buttons are the tab stops, the paging controls, and the
  // filter submit — no row-level edit or delete anywhere.
  await expect(page.getByRole('button', { name: /edit|delete|remove/i })).toHaveCount(0)
})

test('keeps the history log usable on a mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApi(page, [])

  await page.goto('/statistics?tab=history')

  const month = page.getByRole('region', { name: 'October 2026' })
  await expect(month).toBeVisible()
  await expect(month.getByText('Oct 15, 2026')).toBeVisible()
  await expect(page.getByLabel('Recipe')).toBeVisible()

  // The controls stay reachable without horizontal scrolling.
  const hasOverflow = await page.evaluate(
    () => document.documentElement.scrollWidth > window.innerWidth,
  )
  expect(hasOverflow).toBe(false)
})
