import { expect, test, type Locator, type Page } from '@playwright/test'

/**
 * Mobile audit (M14-03, docs/M14_PLAN.md §1.7 and §4).
 *
 * The touch flows run in the `Pixel 7` and `iPhone 13` Playwright projects
 * (they are skipped where `isMobile` is false); the 360/375/390/414 px overflow
 * sweep drives its own viewport and runs in every project. Everything renders
 * the real application bundle with `/api/v1/**` mocked — the same hermetic
 * pattern as `a11y.spec.ts`.
 *
 * "Today" is pinned twice so the run is deterministic: the app clock is fixed
 * with `page.clock.setFixedTime` (Date only, timers keep running) and the mock
 * calendar returns the same date as `today`. That keeps the month grid's
 * outside-month cells and the today cell present on every run.
 *
 * Asserted here, per docs/M14_PLAN.md §4:
 * - no horizontal overflow (`scrollWidth <= clientWidth + 1`);
 * - primary tap targets are at least 44×44 CSS px;
 * - dialogs open inside the viewport, take focus, and Escape returns focus;
 * - the main routes hold up at 360/375/390/414 px.
 */

/** The pinned calendar day used by every fixture and by the fixed clock. */
const TODAY = '2026-10-15'

/** WCAG 2.5.5 / platform guidance for a comfortable touch target. */
const MIN_TAP_TARGET = 44

const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

const adminPermissions = [
  'calendar.view',
  'calendar.edit',
  'groceries.view',
  'groceries.edit',
  'recipes.view',
  'recipes.edit',
  'reference_data.edit',
]

const adminSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'root', role: 'admin' },
  permissions: adminPermissions,
  must_change_password: false,
}

const householdSettings = {
  household_name: 'The Smiths',
  timezone: 'America/Chicago',
  week_starts_on: 0,
  default_diner_count: 4,
  startup_page: 'calendar',
  randomizer_repeat_lookback_days: 7,
  randomizer_repeat_lookahead_days: 14,
  randomizer_repeat_multiplier: 0.1,
  randomizer_special_day_multiplier: 0.02,
  randomizer_protein_multiplier: 0.7,
  randomizer_protein_neighbor_distance: 1,
  randomizer_favorite_multiplier: 1.1,
  randomizer_spacing_near_days: 3,
  randomizer_spacing_far_days: 8,
  randomizer_spacing_search_days: 90,
}

const updateStatus = {
  current_version: '0.1.0',
  latest_version: '0.1.0',
  update_available: false,
  last_checked_at: '2026-10-21T12:00:00Z',
  last_error: null,
  enabled: true,
  release_url: 'https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.1.0',
}

const TIMESTAMP = '2026-10-12T00:00:00+00:00'

const calendarEntry = {
  id: 1,
  entry_type: 'recipe',
  recipe: { id: 1, name: 'Alpha Chicken', image_url: null, status: 'normal' },
  display_text: 'Alpha Chicken',
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
  created_at: '2026-10-01T00:00:00+00:00',
  updated_at: '2026-10-01T00:00:00+00:00',
}

const recipes = [
  {
    id: 1,
    name: 'Alpha Chicken',
    status: 'normal',
    is_favorite: true,
    is_vegan: false,
    is_vegetarian: false,
    image: null,
    last_eaten: '2026-10-01',
    next_planned: null,
    times_eaten: 3,
    created_at: '2026-10-01T10:00:00Z',
    updated_at: '2026-10-01T10:00:00Z',
  },
  {
    id: 2,
    name: 'Beta Beef',
    status: 'retired',
    is_favorite: false,
    is_vegan: false,
    is_vegetarian: false,
    image: null,
    last_eaten: null,
    next_planned: null,
    times_eaten: null,
    created_at: '2026-09-01T10:00:00Z',
    updated_at: '2026-09-01T10:00:00Z',
  },
]

const units = [
  {
    id: 2,
    symbol: 'g',
    display_name: 'gram',
    dimension: 'mass',
    base_factor: 1,
    is_custom: false,
    is_active: true,
    aliases: [],
  },
  {
    id: 7,
    symbol: 'lb',
    display_name: 'Pound',
    dimension: 'mass',
    base_factor: 453.592,
    is_custom: false,
    is_active: true,
    aliases: [],
  },
]

const ingredients = [
  {
    id: 5,
    canonical_name: 'Chicken',
    department_id: 1,
    is_active: true,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
    aliases: [],
  },
  {
    id: 42,
    canonical_name: 'Milk',
    department_id: 1,
    is_active: true,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
    aliases: [],
  },
]

const departments = [
  {
    id: 1,
    name: 'Produce',
    sort_order: 1,
    is_active: true,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
  },
  {
    id: 2,
    name: 'Bakery',
    sort_order: 2,
    is_active: true,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
  },
]

const proteins = [{ id: 5, name: 'Chicken', is_active: true }]
const tags = [{ id: 7, name: 'Weeknight' }]

interface GroceryItem {
  identity: string
  name: string
  quantity_text: string
  quantity: number | null
  unit: null
  ingredient_id: number | null
  department_id: number | null
  checked: boolean
  source_type: string
  manual_item_id: number | null
  sources: Array<{ recipe_id: number; recipe_name: string; date: string }>
}

function hexIdentity(fill: string): string {
  return fill.repeat(64).slice(0, 64)
}

const groceryRange = {
  preset: 'next_7_days',
  start: '2026-10-12',
  end: '2026-10-18',
  day_count: 7,
}

function groceryDepartments(manualItems: GroceryItem[]) {
  const departmentsWithItems: Array<{
    department_id: number | null
    name: string
    sort_order: number
    is_other: boolean
    items: GroceryItem[]
  }> = [
    {
      department_id: 1,
      name: 'Produce',
      sort_order: 1,
      is_other: false,
      items: [
        {
          identity: hexIdentity('a'),
          name: 'Onion',
          quantity_text: '2',
          quantity: 2,
          unit: null,
          ingredient_id: 42,
          department_id: 1,
          checked: false,
          source_type: 'recipe',
          manual_item_id: null,
          sources: [{ recipe_id: 1, recipe_name: 'Tacos', date: '2026-10-13' }],
        },
        {
          identity: hexIdentity('b'),
          name: 'Carrot',
          quantity_text: '',
          quantity: null,
          unit: null,
          ingredient_id: null,
          department_id: 1,
          checked: true,
          source_type: 'recipe',
          manual_item_id: null,
          sources: [],
        },
      ],
    },
    {
      department_id: 2,
      name: 'Bakery',
      sort_order: 2,
      is_other: false,
      items: [
        {
          identity: hexIdentity('c'),
          name: 'Bread',
          quantity_text: '1',
          quantity: 1,
          unit: null,
          ingredient_id: null,
          department_id: 2,
          checked: false,
          source_type: 'recipe',
          manual_item_id: null,
          sources: [],
        },
      ],
    },
  ]
  if (manualItems.length > 0) {
    departmentsWithItems.push({
      department_id: null,
      name: 'Other',
      sort_order: 2147483647,
      is_other: true,
      items: manualItems,
    })
  }
  return departmentsWithItems
}

const groceryStaples = [
  {
    id: 3,
    name: 'Milk',
    ingredient_id: 42,
    department_id: 1,
    default_quantity_numeric: 1,
    unit_id: 7,
    is_active: true,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
  },
]

const statisticsOverview = {
  windows: {
    today: TODAY,
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
    proteins: [{ protein_id: 5, name: 'Chicken', count: 30 }],
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

const mealHistory = {
  range: { start: '2025-11-01', end: '2026-10-16', timezone: 'America/Chicago' },
  month: { key: '2026-10', label: 'October 2026', start: '2026-10-01', end: '2026-10-31' },
  days: [
    { date: TODAY, entry_type: 'recipe', recipe_id: 5, name: 'Tacos', protein: 'Chicken' },
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

const users = [
  {
    id: 1,
    username: 'root',
    role: 'admin',
    is_active: true,
    must_change_password: false,
    last_login_at: '2026-09-29T12:00:00Z',
    permissions: adminPermissions,
  },
]

const securitySettings = {
  ip_allowlist_enabled: false,
  ip_allowlist_ranges: ['192.168.1.0/24'],
  trusted_proxies: [],
  audit_retention_days: 365,
}

const backupFilename = 'fatefridge-0.1.0-20261021T120000Z.ffbackup'

const backups = [
  {
    id: 12,
    filename: backupFilename,
    kind: 'manual',
    status: 'completed',
    app_version: '0.1.0',
    schema_version: '20261021023131_M13Backups',
    created_at: '2026-10-21T12:00:00+00:00',
    size_bytes: 1048576,
    checksum: '9f2c8a1b4d6e0f3a5c7b9d1e2f4a6b8c0d2e4f6a8b0c2d4e6f8a0b2c4d6e8f0a',
    error: null,
  },
]

const auditItems = [
  {
    id: 1,
    source: 'audit',
    recorded_at: '2026-10-21T12:00:00+00:00',
    actor_user_id: 1,
    actor_username: 'root',
    event_type: 'settings.household_updated',
    entity_type: 'household_settings',
    entity_id: '1',
    summary: 'Household settings updated.',
    reason: null,
    ip_address: '192.168.1.10',
    before: null,
    after: null,
    metadata: null,
  },
]

/**
 * Hermetic API with the fixtures the mobile flows need. The grocery list is
 * stateful so a checked item and a manual item survive the refetch that follows
 * each write, exactly like the real list projection.
 */
async function mockAppApi(page: Page): Promise<void> {
  const items = [
    { identity: hexIdentity('a'), name: 'Onion', checked: false },
    { identity: hexIdentity('b'), name: 'Carrot', checked: true },
    { identity: hexIdentity('c'), name: 'Bread', checked: false },
  ]
  const manualItems: GroceryItem[] = []

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()
    const body = request.postDataJSON() as Record<string, unknown> | null

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: adminSession })
      return
    }
    if (path === '/health' || path === '/health/db') {
      await route.fulfill({ json: { status: 'ok', database: 'connected' } })
      return
    }
    if (path === '/about') {
      await route.fulfill({
        json: { app_version: '0.1.0', schema_version: '20261021023131_M13Backups' },
      })
      return
    }
    if (path === '/updates') {
      await route.fulfill({ json: updateStatus })
      return
    }
    if (path === '/settings/household') {
      await route.fulfill({ json: householdSettings })
      return
    }
    if (path === '/settings/security') {
      await route.fulfill({ json: securitySettings })
      return
    }
    if (path === '/users' && method === 'GET') {
      await route.fulfill({ json: users })
      return
    }
    if (path === '/admin/backups' && method === 'GET') {
      await route.fulfill({ json: backups })
      return
    }
    if (path === '/admin/audit' && method === 'GET') {
      await route.fulfill({ json: { items: auditItems, next_cursor: null } })
      return
    }
    if (path === '/calendar' && method === 'GET') {
      await route.fulfill({
        json: {
          start: url.searchParams.get('start') ?? TODAY,
          end: url.searchParams.get('end') ?? TODAY,
          today: TODAY,
          week_starts_on: 0,
          entries: [
            {
              date: TODAY,
              is_today: true,
              is_historical: false,
              can_edit: true,
              entry: calendarEntry,
              repeat_warning: null,
            },
          ],
        },
      })
      return
    }
    if (/^\/calendar\/[^/]+\/repeat-check$/.test(path)) {
      await route.fulfill({
        json: {
          is_repeat: false,
          window_start: '2026-10-01',
          window_end: '2026-10-31',
          occurrences: [],
        },
      })
      return
    }
    if (/^\/calendar\/[^/]+$/.test(path) && method === 'GET') {
      await route.fulfill({
        json: {
          date: TODAY,
          is_today: true,
          is_historical: false,
          can_edit: true,
          entry: calendarEntry,
          repeat_warning: null,
        },
      })
      return
    }
    if (path === '/recipes' && method === 'GET') {
      await route.fulfill({
        json: { items: recipes, total: recipes.length, limit: 24, offset: 0 },
      })
      return
    }
    if (path === '/departments' && method === 'GET') {
      await route.fulfill({ json: departments })
      return
    }
    if (path === '/units' && method === 'GET') {
      await route.fulfill({ json: units })
      return
    }
    if (path === '/ingredients' && method === 'GET') {
      await route.fulfill({ json: ingredients })
      return
    }
    if (path === '/proteins' && method === 'GET') {
      await route.fulfill({ json: proteins })
      return
    }
    if (path === '/tags' && method === 'GET') {
      await route.fulfill({ json: tags })
      return
    }
    if (path === '/groceries/preferences' && method === 'GET') {
      await route.fulfill({
        json: {
          hide_checked: false,
          range_preset: 'next_7_days',
          range_start: null,
          range_days: null,
        },
      })
      return
    }
    if (path === '/groceries/preferences' && method === 'PATCH') {
      await route.fulfill({
        json: {
          hide_checked: body?.['hide_checked'] === true,
          range_preset: 'next_7_days',
          range_start: null,
          range_days: null,
        },
      })
      return
    }
    if (path === '/groceries/staples' && method === 'GET') {
      await route.fulfill({ json: { staples: groceryStaples } })
      return
    }
    if (path === '/groceries/check' && method === 'POST') {
      const identity = String(body?.['identity'] ?? '')
      const item = items.find((row) => row.identity === identity)
      if (item) {
        item.checked = true
      }
      await route.fulfill({
        json: { identity, checked: true, checked_at: TIMESTAMP, checked_by_user_id: 1 },
      })
      return
    }
    if (path === '/groceries/uncheck' && method === 'POST') {
      const identity = String(body?.['identity'] ?? '')
      const item = items.find((row) => row.identity === identity)
      if (item) {
        item.checked = false
      }
      await route.fulfill({
        json: { identity, checked: false, checked_at: null, checked_by_user_id: 1 },
      })
      return
    }
    if (path === '/groceries/manual' && method === 'POST') {
      const name = String(body?.['name'] ?? '')
      manualItems.push({
        identity: hexIdentity('e'),
        name,
        quantity_text: '',
        quantity: null,
        unit: null,
        ingredient_id: null,
        department_id: null,
        checked: false,
        source_type: 'manual',
        manual_item_id: 5,
        sources: [],
      })
      await route.fulfill({
        status: 201,
        json: {
          id: 5,
          name,
          quantity_numeric: null,
          unit_id: null,
          ingredient_id: null,
          department_id: null,
          save_for_future: false,
          created_at: TIMESTAMP,
          updated_at: TIMESTAMP,
        },
      })
      return
    }
    if (path === '/groceries' && method === 'GET') {
      const departmentsWithState = groceryDepartments(manualItems).map((department) => ({
        ...department,
        items: department.items
          .map((item) => {
            const state = items.find((row) => row.identity === item.identity)
            return state === undefined ? item : { ...item, checked: state.checked }
          })
          .sort((a, b) => Number(a.checked) - Number(b.checked)),
      }))
      await route.fulfill({
        json: { range: groceryRange, departments: departmentsWithState },
      })
      return
    }
    if (path === '/statistics/overview' && method === 'GET') {
      await route.fulfill({ json: statisticsOverview })
      return
    }
    if (path === '/statistics/history' && method === 'GET') {
      await route.fulfill({ json: mealHistory })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${method} ${path}`, details: {} } },
    })
  })
}

/** Navigates with the pinned app clock so every run sees the same "today". */
async function openRoute(page: Page, path: string): Promise<void> {
  await page.clock.setFixedTime('2026-10-15T12:00:00')
  await page.goto(path)
}

/** Fails when the document scrolls sideways (the classic mobile layout bug). */
async function expectNoHorizontalOverflow(page: Page, where: string): Promise<void> {
  const metrics = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }))
  expect(
    metrics.scrollWidth,
    `${where}: document.documentElement.scrollWidth (${metrics.scrollWidth}) exceeds clientWidth (${metrics.clientWidth}) + 1`,
  ).toBeLessThanOrEqual(metrics.clientWidth + 1)
}

/** Asserts a visible control is at least `min` × `min` CSS pixels. */
async function expectTapTarget(
  locator: Locator,
  name: string,
  min = MIN_TAP_TARGET,
): Promise<void> {
  await expect(locator, `${name} is visible`).toBeVisible()
  const box = await locator.boundingBox()
  expect(box, `${name} has a layout box`).not.toBeNull()
  const { width, height } = box as { width: number; height: number }
  expect(width, `${name} is ${width.toFixed(1)} px wide (needs >= ${min})`).toBeGreaterThanOrEqual(
    min,
  )
  expect(
    height,
    `${name} is ${height.toFixed(1)} px tall (needs >= ${min})`,
  ).toBeGreaterThanOrEqual(min)
}

/** Asserts every visible link in a navigation landmark is a usable tap target. */
async function expectNavTapTargets(page: Page, landmark: string): Promise<void> {
  const links = page.getByRole('navigation', { name: landmark }).getByRole('link')
  const count = await links.count()
  expect(count, `${landmark} navigation has links`).toBeGreaterThan(0)
  for (let index = 0; index < count; index += 1) {
    const link = links.nth(index)
    const label = (await link.textContent())?.trim() ?? `link ${index}`
    await expectTapTarget(link, `${landmark} link "${label}"`)
  }
}

/** A dialog must sit inside the viewport horizontally and start on screen. */
async function expectDialogUsable(page: Page, dialog: Locator, name: string): Promise<void> {
  await expect(dialog).toBeVisible()
  const box = await dialog.boundingBox()
  expect(box, `${name} has a layout box`).not.toBeNull()
  const viewport = page.viewportSize()
  expect(viewport).not.toBeNull()
  const { width, height } = viewport as { width: number; height: number }
  const rect = box as { x: number; y: number; width: number; height: number }
  expect(rect.x, `${name} starts at or right of the viewport edge`).toBeGreaterThanOrEqual(-1)
  expect(
    rect.x + rect.width,
    `${name} right edge (${(rect.x + rect.width).toFixed(1)}) exceeds the ${width} px viewport`,
  ).toBeLessThanOrEqual(width + 1)
  expect(rect.y, `${name} starts above the viewport`).toBeGreaterThanOrEqual(-1)
  // Tall content scrolls inside the backdrop, so a tall dialog is fine as long
  // as it starts on screen and can be scrolled to its actions.
  expect(rect.height, `${name} has no height`).toBeGreaterThan(0)
  expect(height).toBeGreaterThan(0)
}

/** Focus must be inside the dialog (the modal focuses its panel on open). */
async function expectFocusInsideDialog(page: Page): Promise<void> {
  const inside = await page.evaluate(() => {
    const active = document.activeElement
    return active !== null && active.closest('[role="dialog"]') !== null
  })
  expect(inside, 'keyboard focus moved inside the dialog').toBe(true)
}

/**
 * The mobile-specific flows run in the two mobile Playwright projects only
 * (`docs/M14_PLAN.md` §1.7): a desktop viewport is not a touch context, so the
 * 44 px tap-target bar does not apply there. The 360–414 px overflow sweep
 * below sets its own viewport and therefore runs in every project.
 */
test.describe('mobile audit — key flows', () => {
  test.skip(
    ({ isMobile }) => !isMobile,
    'Mobile flows run in the touch-emulation projects (Pixel 7, iPhone 13).',
  )

  test('calendar month and agenda fit a phone and stay tappable', async ({ page }) => {
    await mockAppApi(page)
    await openRoute(page, '/calendar')

    await expect(page.getByRole('grid')).toBeVisible()
    await expect(page.getByRole('grid').getByText('Alpha Chicken')).toBeVisible()
    await expectNoHorizontalOverflow(page, 'calendar month')

    await expectNavTapTargets(page, 'Primary')

    const monthToggle = page.getByRole('button', { name: 'Month', exact: true })
    const agendaToggle = page.getByRole('button', { name: 'Agenda', exact: true })
    await expectTapTarget(monthToggle, 'Month toggle')
    await expectTapTarget(agendaToggle, 'Agenda toggle')

    const todayCell = page.getByRole('gridcell', { name: /today/i })
    await expectTapTarget(todayCell, 'today grid cell')

    await agendaToggle.click()
    const agenda = page.getByRole('list', { name: 'Agenda' })
    await expect(agenda).toBeVisible()
    await expectNoHorizontalOverflow(page, 'calendar agenda')
    await expectTapTarget(
      agenda.getByRole('button', { name: new RegExp(TODAY.slice(8)) }).first(),
      'agenda day',
    )
  })

  test('calendar date-detail dialog is usable and Escape returns focus', async ({ page }) => {
    await mockAppApi(page)
    await openRoute(page, '/calendar')

    const todayCell = page.getByRole('gridcell', { name: /today/i })
    await todayCell.click()

    const dialog = page.getByRole('dialog')
    await expectDialogUsable(page, dialog, 'date detail dialog')
    await expectFocusInsideDialog(page)
    await expectNoHorizontalOverflow(page, 'calendar date detail')

    await expectTapTarget(dialog.getByRole('button', { name: 'Save' }), 'date detail Save')
    await expectTapTarget(dialog.getByRole('button', { name: 'Cancel' }), 'date detail Cancel')

    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)
    await expect(todayCell).toBeFocused()
  })

  test('groceries: list, check toggle and manual item work on a phone', async ({ page }) => {
    await mockAppApi(page)
    await openRoute(page, '/groceries')

    const produce = page.getByRole('list', { name: 'Produce' })
    await expect(produce).toBeVisible()
    await expectNoHorizontalOverflow(page, 'groceries')

    await expectTapTarget(
      page.getByRole('button', { name: 'Add grocery item' }),
      'Add grocery item',
    )
    await expectTapTarget(page.getByRole('button', { name: 'Staples' }), 'Staples')

    // The checkbox input is 28 px, but its wrapping label is the hit area.
    const onionLabel = page.locator('label.grocery-item__label', { hasText: 'Onion' })
    await expectTapTarget(onionLabel, 'checked-item label (Onion)')

    const onionToggle = page.getByLabel('Mark Onion as bought')
    await onionToggle.click()
    await expect(page.getByLabel('Mark Onion as not bought')).toBeChecked()

    // Non-colour cue: the checked row keeps its strike-through.
    await expect(page.getByText('Onion', { exact: true })).toHaveCSS(
      'text-decoration-line',
      'line-through',
    )
    await expectNoHorizontalOverflow(page, 'groceries after checking an item')

    await page.getByRole('button', { name: 'Add grocery item' }).click()
    const dialog = page.getByRole('dialog', { name: 'Add grocery item' })
    await expectDialogUsable(page, dialog, 'manual-item dialog')
    await expectFocusInsideDialog(page)
    await expectTapTarget(dialog.getByRole('button', { name: 'Add item' }), 'manual-item Add item')

    await dialog.getByLabel('Name').fill('Dish soap')
    await dialog.getByRole('button', { name: 'Add item' }).click()

    await expect(dialog).toHaveCount(0)
    await expect(page.getByRole('list', { name: 'Other' }).getByText('Dish soap')).toBeVisible()
    await expectNoHorizontalOverflow(page, 'groceries after adding a manual item')
  })

  test('recipe browse fits a phone and its primary controls are tappable', async ({ page }) => {
    await mockAppApi(page)
    await openRoute(page, '/recipes')

    await expect(page.getByRole('heading', { level: 2, name: 'Recipes' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Alpha Chicken' }).first()).toBeVisible()
    await expectNoHorizontalOverflow(page, 'recipe browser')

    await expectTapTarget(page.getByRole('link', { name: 'New recipe' }), 'New recipe')
    await expectTapTarget(page.getByRole('link', { name: 'Import from URL' }), 'Import from URL')
    await expectTapTarget(
      page.getByRole('button', { name: 'List', exact: true }),
      'List view toggle',
    )

    await page.getByRole('button', { name: 'List', exact: true }).click()
    await expect(page.getByRole('link', { name: 'Alpha Chicken' }).first()).toBeVisible()
    await expectNoHorizontalOverflow(page, 'recipe list view')
  })

  test('statistics and meal history fit a phone', async ({ page }) => {
    await mockAppApi(page)
    await openRoute(page, '/statistics')

    await expect(page.getByRole('heading', { level: 2, name: 'Statistics' })).toBeVisible()
    await expect(page.getByRole('heading', { level: 3, name: 'Most eaten' })).toBeVisible()
    await expectNoHorizontalOverflow(page, 'statistics overview')

    const historyTab = page.getByRole('tab', { name: 'History' })
    await expectTapTarget(page.getByRole('tab', { name: 'Overview' }), 'Overview tab')
    await expectTapTarget(historyTab, 'History tab')

    await historyTab.click()
    await expect(page.getByRole('region', { name: 'October 2026' })).toBeVisible()
    await expectNoHorizontalOverflow(page, 'meal history')
  })

  test('settings sections fit a phone and their navigation is tappable', async ({ page }) => {
    await mockAppApi(page)
    await openRoute(page, '/settings')

    await expect(page.getByRole('heading', { level: 3, name: 'Household' })).toBeVisible()
    await expectNoHorizontalOverflow(page, 'settings household')
    await expectNavTapTargets(page, 'Settings sections')

    const sections: Array<{ path: string; ready: (page: Page) => Promise<void> }> = [
      {
        path: '/settings/reference-data',
        ready: async (current) => {
          await expect(current.getByRole('table', { name: 'Grocery departments' })).toBeVisible()
        },
      },
      {
        path: '/settings/reference-data/ingredients',
        ready: async (current) => {
          await expect(current.getByRole('rowheader', { name: 'Chicken' })).toBeVisible()
        },
      },
      {
        path: '/settings/reference-data/units',
        ready: async (current) => {
          await expect(current.getByRole('rowheader', { name: 'g' })).toBeVisible()
        },
      },
      {
        path: '/settings/users',
        ready: async (current) => {
          await expect(current.getByRole('row', { name: /root/ })).toBeVisible()
        },
      },
      {
        path: '/settings/security',
        ready: async (current) => {
          await expect(current.getByRole('heading', { level: 3, name: 'Security' })).toBeVisible()
        },
      },
      {
        path: '/settings/backups',
        ready: async (current) => {
          await expect(current.getByRole('row', { name: new RegExp(backupFilename) })).toBeVisible()
        },
      },
      {
        path: '/settings/audit',
        ready: async (current) => {
          await expect(current.getByRole('heading', { level: 3, name: 'Audit log' })).toBeVisible()
        },
      },
      {
        path: '/settings/about',
        ready: async (current) => {
          await expect(current.getByTestId('about-app-version')).toHaveText('0.1.0')
        },
      },
    ]

    for (const section of sections) {
      await openRoute(page, section.path)
      await section.ready(page)
      await expectNoHorizontalOverflow(page, `settings ${section.path}`)
    }

    // Reference data keeps its reorder row and tab strip usable on a phone.
    await openRoute(page, '/settings/reference-data')
    await expectTapTarget(page.getByRole('button', { name: 'Move up Bakery' }), 'Move up Bakery')
    await expectNavTapTargets(page, 'Reference data sections')
  })
})

/**
 * The manual 360/375/390/414 px check. It drives its own viewport, so it runs
 * in every project (including the desktop one), unlike the touch-specific
 * flows above.
 */
test.describe('mobile widths — 360/375/390/414 px', () => {
  test('main routes never scroll sideways at 360/375/390/414 px', async ({ page }) => {
    await mockAppApi(page)

    const routes = [
      { path: '/calendar', ready: (current: Page) => current.getByRole('grid').first().waitFor() },
      {
        path: '/groceries',
        ready: (current: Page) => current.getByRole('list', { name: 'Produce' }).waitFor(),
      },
      {
        path: '/recipes',
        ready: (current: Page) =>
          current.getByRole('link', { name: 'Alpha Chicken' }).first().waitFor(),
      },
      {
        path: '/statistics',
        ready: (current: Page) =>
          current.getByRole('heading', { level: 3, name: 'Most eaten' }).waitFor(),
      },
      {
        path: '/settings',
        ready: (current: Page) =>
          current.getByRole('heading', { level: 3, name: 'Household' }).waitFor(),
      },
      {
        path: '/settings/reference-data',
        ready: (current: Page) =>
          current.getByRole('table', { name: 'Grocery departments' }).waitFor(),
      },
    ]

    const measured: string[] = []
    for (const width of [360, 375, 390, 414]) {
      await page.setViewportSize({ width, height: 800 })
      for (const route of routes) {
        await page.goto(route.path)
        await route.ready(page)
        await expectNoHorizontalOverflow(page, `${route.path} at ${width} px`)
        const metrics = await page.evaluate(() => ({
          scrollWidth: document.documentElement.scrollWidth,
          clientWidth: document.documentElement.clientWidth,
        }))
        measured.push(`${width}:${route.path}=${metrics.scrollWidth}/${metrics.clientWidth}`)
      }
    }
    // Printed in the test output so the evidence doc can quote the numbers.
    console.log(`mobile overflow measurements (scrollWidth/clientWidth) ${measured.join(' ')}`)
  })
})
