import { expect, test, type Page } from '@playwright/test'

/**
 * Product-copy regression gate (M15-03, docs/M15_PLAN.md §2.4).
 *
 * Every product route is rendered against a hermetic `/api/v1/**` mock and the
 * visible body text is scanned for development-language markers that must never
 * ship: milestone/roadmap vocabulary, bug ids, milestone ids, endpoint jargon,
 * and "not implemented" style promises. The check is a bounded body-text match
 * rather than a snapshot, so ordinary copy edits do not churn the gate while a
 * reintroduced development string fails loudly.
 */

const BANNED =
  /milestone|roadmap|manifest|BUG-|M\d+-\d+|not implemented yet|later milestone|endpoint|placeholder/gi

const TIMESTAMP = '2026-10-12T00:00:00+00:00'
const TODAY = '2026-10-15'

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

const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

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

const recipeImage = null

const recipeList = [
  {
    id: 1,
    name: 'Alpha Chicken',
    status: 'normal',
    is_favorite: true,
    is_vegan: false,
    is_vegetarian: false,
    image: recipeImage,
    last_eaten: '2026-10-01',
    next_planned: null,
    times_eaten: 3,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
  },
]

const recipeDetail = {
  ...recipeList[0],
  description: 'A weeknight roast.',
  source_site: null,
  source_url: null,
  author: null,
  prep_minutes: 10,
  cook_minutes: 40,
  notes: 'Double the sauce.',
  ingredient_groups: [{ id: 1, title: 'Main', sort_order: 0, ingredients: [] }],
  ingredients: [],
  instruction_sections: [{ id: 1, title: 'Cook', sort_order: 0, steps: [] }],
  proteins: [{ id: 5, name: 'Chicken', is_active: true }],
  tags: [{ id: 7, name: 'Weeknight' }],
  statistics: { last_eaten: '2026-10-01', next_planned: null, times_eaten: 3 },
}

const departments = [
  {
    id: 1,
    name: 'Produce',
    sort_order: 1,
    is_active: true,
    created_at: TIMESTAMP,
    updated_at: TIMESTAMP,
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
]

const proteins = [{ id: 5, name: 'Chicken', is_active: true }]
const tags = [{ id: 7, name: 'Weeknight' }]

const groceryRange = {
  preset: 'next_7_days',
  start: '2026-10-12',
  end: '2026-10-18',
  day_count: 7,
}

const groceryDepartments = [
  {
    department_id: 1,
    name: 'Produce',
    sort_order: 1,
    is_other: false,
    items: [
      {
        identity: 'a'.repeat(64),
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
    ],
  },
]

const groceryStaples: unknown[] = []

const statisticsOverview = {
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
    { date: '2026-10-15', entry_type: 'recipe', recipe_id: 5, name: 'Tacos', protein: 'Chicken' },
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
  ip_allowlist_ranges: [],
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
    schema_version: '20261021023131_Backups',
    created_at: '2026-10-21T12:00:00+00:00',
    size_bytes: 1048576,
    checksum: 'a'.repeat(64),
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

const updateStatus = {
  current_version: '1.0.0',
  latest_version: '1.0.0',
  update_available: false,
  last_checked_at: '2026-10-21T12:00:00Z',
  last_error: null,
  enabled: true,
  release_url: null,
}

interface MockOptions {
  /** `false` renders the setup wizard; defaults to a configured household. */
  setupCompleted?: boolean
  /** Session payload; `null` answers 401 (the login page). */
  session?: unknown
}

/**
 * One hermetic API router with the fixtures each product route needs. Anything
 * unmatched answers 404, so a missing fixture surfaces as a visible error state
 * (whose copy is then checked like any other) rather than a silent empty page.
 */
async function mockAppApi(page: Page, options: MockOptions = {}): Promise<void> {
  const setupCompleted = options.setupCompleted ?? true
  const session = options.session === undefined ? adminSession : options.session

  await page.clock.setFixedTime(`${TODAY}T12:00:00`)

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()

    if (path === '/setup/status') {
      await route.fulfill({
        json: setupCompleted
          ? setupComplete
          : { setup_completed: false, auth_mode: null, recovery_key_present: false },
      })
      return
    }
    if (path === '/auth/session') {
      if (session === null) {
        await route.fulfill({
          status: 401,
          json: {
            error: { code: 'AUTH_REQUIRED', message: 'A valid session is required.', details: {} },
          },
        })
        return
      }
      await route.fulfill({ json: session })
      return
    }
    if (path === '/health' || path === '/health/db') {
      await route.fulfill({ json: { status: 'ok', database: 'connected' } })
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
    if (path === '/about') {
      await route.fulfill({ json: { app_version: '1.0.0', schema_version: '20261021023131' } })
      return
    }
    if (path === '/updates') {
      await route.fulfill({ json: updateStatus })
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
              entry: {
                id: 1,
                entry_type: 'recipe',
                recipe: {
                  id: 1,
                  name: 'Alpha Chicken',
                  image_url: null,
                  status: 'normal',
                },
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
                created_at: TIMESTAMP,
                updated_at: TIMESTAMP,
              },
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
          entry: null,
          repeat_warning: null,
        },
      })
      return
    }
    if (path === '/recipes' && method === 'GET') {
      await route.fulfill({
        json: { items: recipeList, total: recipeList.length, limit: 24, offset: 0 },
      })
      return
    }
    if (path === '/recipes/1' && method === 'GET') {
      await route.fulfill({ json: recipeDetail })
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
    if (path === '/groceries' && method === 'GET') {
      await route.fulfill({ json: { range: groceryRange, departments: groceryDepartments } })
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
    if (path === '/groceries/staples' && method === 'GET') {
      await route.fulfill({ json: { staples: groceryStaples } })
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

interface RouteCase {
  name: string
  path: string
  mock?: MockOptions
  ready: (page: Page) => Promise<void>
}

/** Every product route; each must render without development-language copy. */
const ROUTES: RouteCase[] = [
  {
    name: 'login',
    path: '/login',
    mock: { session: null },
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
    },
  },
  {
    name: 'setup wizard',
    path: '/setup',
    mock: { setupCompleted: false, session: null },
    ready: async (page) => {
      await expect(
        page.getByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
      ).toBeVisible()
    },
  },
  {
    name: 'restore from backup',
    path: '/setup/restore',
    mock: { setupCompleted: false, session: null },
    ready: async (page) => {
      await expect(
        page.getByRole('heading', { level: 1, name: 'Restore from backup' }),
      ).toBeVisible()
    },
  },
  {
    name: 'calendar',
    path: '/calendar',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
      await expect(page.getByRole('grid')).toBeVisible()
    },
  },
  {
    name: 'calendar print',
    path: '/calendar/print?month=2026-10',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Print calendar' })).toBeVisible()
    },
  },
  {
    name: 'dashboard',
    path: '/dashboard',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible()
    },
  },
  {
    name: 'recipes',
    path: '/recipes',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Recipes' })).toBeVisible()
      await expect(page.getByRole('link', { name: 'Alpha Chicken' }).first()).toBeVisible()
    },
  },
  {
    name: 'recipe create editor',
    path: '/recipes/new',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Add recipe' })).toBeVisible()
    },
  },
  {
    name: 'recipe edit editor',
    path: '/recipes/1/edit',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Edit recipe' })).toBeVisible()
    },
  },
  {
    name: 'recipe import',
    path: '/recipes/import',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Import from URL' })).toBeVisible()
    },
  },
  {
    name: 'groceries',
    path: '/groceries',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Groceries' })).toBeVisible()
      await expect(page.getByRole('list', { name: 'Produce' })).toBeVisible()
    },
  },
  {
    name: 'groceries print',
    path: '/groceries/print',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: /Grocery list/ })).toBeVisible()
    },
  },
  {
    name: 'statistics overview',
    path: '/statistics',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Statistics' })).toBeVisible()
      await expect(page.getByRole('heading', { level: 3, name: 'Most eaten' })).toBeVisible()
    },
  },
  {
    name: 'statistics history',
    path: '/statistics?tab=history',
    ready: async (page) => {
      await expect(page.getByRole('region', { name: 'October 2026' })).toBeVisible()
    },
  },
  {
    name: 'settings household',
    path: '/settings',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Settings' })).toBeVisible()
      await expect(page.getByRole('heading', { level: 3, name: 'Household' })).toBeVisible()
    },
  },
  {
    name: 'settings reference data',
    path: '/settings/reference-data',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'Reference data' })).toBeVisible()
      await expect(page.getByRole('table', { name: 'Grocery departments' })).toBeVisible()
    },
  },
  {
    name: 'settings users',
    path: '/settings/users',
    ready: async (page) => {
      await expect(page.getByRole('table', { name: 'Household users' })).toBeVisible()
    },
  },
  {
    name: 'settings security',
    path: '/settings/security',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'Security' })).toBeVisible()
    },
  },
  {
    name: 'settings audit log',
    path: '/settings/audit',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'Audit log' })).toBeVisible()
      await expect(page.getByRole('list', { name: 'Audit entries' })).toBeVisible()
    },
  },
  {
    name: 'settings backups',
    path: '/settings/backups',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'Backups' })).toBeVisible()
      await expect(page.getByRole('row', { name: new RegExp(backupFilename) })).toBeVisible()
    },
  },
  {
    name: 'settings about',
    path: '/settings/about',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'About' })).toBeVisible()
      await expect(page.getByTestId('about-app-version')).toHaveText('1.0.0')
    },
  },
  {
    name: 'settings account',
    path: '/settings/account',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'Account' })).toBeVisible()
    },
  },
]

/** Fails with the offending strings and a text excerpt, not a whole snapshot. */
async function expectNoDevelopmentCopy(page: Page, routeName: string): Promise<void> {
  const body = await page.locator('body').innerText()
  expect(body.trim().length, `${routeName} rendered no visible text`).toBeGreaterThan(0)

  BANNED.lastIndex = 0
  const matches = [...body.matchAll(BANNED)].map((match) => match[0])
  expect(
    matches,
    `${routeName} rendered development-language copy: ${matches.join(', ')}\n\nBody text:\n${body.slice(0, 2000)}`,
  ).toEqual([])
}

test('setup wizard steps contain no development-language copy', async ({ page }) => {
  await mockAppApi(page, { setupCompleted: false, session: null })
  await page.goto('/setup')
  await expect(
    page.getByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
  ).toBeVisible()
  await expectNoDevelopmentCopy(page, 'setup mode step')

  await page.getByLabel(/Single user/).check()
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Household' })).toBeVisible()
  await expectNoDevelopmentCopy(page, 'setup household step')

  await page.getByLabel('Household name').fill('The Smiths')
  await page.getByLabel('Timezone').fill('America/Chicago')
  await page.getByRole('button', { name: 'Next' }).click()
  // Single-user mode skips the Admin step and lands on the network allowlist,
  // whose guidance used to cite a milestone id.
  await expect(page.getByRole('heading', { level: 1, name: 'Network access' })).toBeVisible()
  await expectNoDevelopmentCopy(page, 'setup network-access step')
})

test.describe('product copy', () => {
  for (const route of ROUTES) {
    test(`${route.name} contains no development-language copy`, async ({ page }) => {
      await mockAppApi(page, route.mock ?? {})
      await page.goto(route.path)
      await route.ready(page)
      await expectNoDevelopmentCopy(page, route.name)
    })
  }
})
