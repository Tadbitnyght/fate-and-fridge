import { expect, test, type Page } from '@playwright/test'

const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

const adminSession = {
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

interface RecordedRequest {
  method: string
  path: string
  body?: Record<string, unknown>
}

function isoDate(date: Date): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

interface CalendarMockOptions {
  /** Include an entry on today. */
  withTodayEntry?: boolean
  /** Treat today as historical (read-only). */
  historical?: boolean
}

/** Hermetic calendar + recipe API mirroring the other e2e specs. */
async function mockCalendarApi(
  page: Page,
  options: CalendarMockOptions = {},
): Promise<{ requests: RecordedRequest[]; today: string }> {
  const now = new Date()
  const today = isoDate(now)
  const requests: RecordedRequest[] = []

  const entry = {
    id: 1,
    entry_type: 'recipe',
    recipe: { id: 5, name: 'Lasagna', image_url: null, status: 'normal' },
    display_text: 'Lasagna',
    is_locked: false,
    diner_count: null,
    effective_diner_count: 4,
    restaurant_name: null,
    location_text: null,
    custom_label: null,
    notes: null,
    icon_key: null,
    include_groceries: true,
    blocks_automation: false,
    created_at: '2026-11-01T00:00:00+00:00',
    updated_at: '2026-11-01T00:00:00+00:00',
  }

  const day = {
    date: today,
    is_today: !options.historical,
    is_historical: options.historical === true,
    can_edit: options.historical !== true,
    entry: options.withTodayEntry === false ? null : entry,
    repeat_warning: null,
  }

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()
    const rawBody = request.postData()
    const body = rawBody === null ? undefined : (JSON.parse(rawBody) as Record<string, unknown>)
    requests.push(body === undefined ? { method, path } : { method, path, body })

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: adminSession })
      return
    }
    if (path === '/health' || path === '/health/db') {
      await route.fulfill({ json: { status: 'ok' } })
      return
    }
    if (path === '/recipes') {
      await route.fulfill({ json: { items: [], total: 0, limit: 5, offset: 0 } })
      return
    }
    if (path.endsWith('/repeat-check')) {
      await route.fulfill({
        json: { is_repeat: false, window_start: today, window_end: today, occurrences: [] },
      })
      return
    }
    if (path.startsWith('/calendar/') && path.endsWith('/lock')) {
      await route.fulfill({ json: { ...day, entry: { ...entry, is_locked: true } } })
      return
    }
    if (path === '/calendar' && method === 'GET') {
      await route.fulfill({
        json: {
          start: url.searchParams.get('start') ?? today,
          end: url.searchParams.get('end') ?? today,
          today,
          week_starts_on: 0,
          entries: day.entry === null ? [] : [day],
        },
      })
      return
    }
    if (path.startsWith('/calendar/') && method === 'GET') {
      await route.fulfill({ json: day })
      return
    }
    if (path.startsWith('/calendar/') && method === 'PUT') {
      await route.fulfill({ json: day })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock route for ${method} ${path}` } },
    })
  })

  return { requests, today }
}

test('calendar: month grid shows an entry and opens the detail', async ({ page }) => {
  const { requests } = await mockCalendarApi(page, { withTodayEntry: true })

  await page.goto('/calendar')
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
  await expect(page.getByRole('grid')).toBeVisible()
  await expect(page.getByRole('grid').getByText('Lasagna')).toBeVisible()

  await page.getByRole('gridcell', { name: /today/i }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Selected:')).toBeVisible()

  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(dialog).toBeHidden()

  const put = requests.find(
    (entry) => entry.method === 'PUT' && entry.path.startsWith('/calendar/'),
  )
  expect(put?.body).toMatchObject({ entry_type: 'recipe', recipe_id: 5 })
})

test('calendar: the agenda toggle lists the month', async ({ page }) => {
  // The Month/Agenda toggle is the mobile affordance; the desktop grid has no toggle.
  await page.setViewportSize({ width: 420, height: 900 })
  await mockCalendarApi(page, { withTodayEntry: true })

  await page.goto('/calendar')
  await expect(page.getByRole('grid')).toBeVisible()

  await page.getByRole('button', { name: 'Agenda' }).click()

  const agenda = page.getByRole('list', { name: 'Agenda' })
  await expect(agenda).toBeVisible()
  await expect(agenda.getByText('Lasagna')).toBeVisible()
})

test('calendar: a historical date is read-only', async ({ page }) => {
  await mockCalendarApi(page, { withTodayEntry: true, historical: true })

  await page.goto('/calendar')
  await page.getByRole('grid').getByText('Lasagna').click()

  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText(/Past dates are read-only/i)).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Save' })).toHaveCount(0)
})

test('calendar: arrow keys move focus across the grid', async ({ page }) => {
  await mockCalendarApi(page, { withTodayEntry: true })

  await page.goto('/calendar')
  const grid = page.getByRole('grid')
  await expect(grid).toBeVisible()

  await grid.getByRole('gridcell', { name: /today/i }).focus()
  await page.keyboard.press('ArrowRight')

  // Focus stays inside the grid after keyboard navigation.
  await expect(page.locator(':focus')).toHaveAttribute('role', 'gridcell')
})
