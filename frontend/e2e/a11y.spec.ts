import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

/**
 * Accessibility gate.
 * Every key route is rendered with the real application bundle, real CSS, and
 * the real DOM, against a hermetic `/api/v1/**` mock (the same pattern as the
 * other e2e specs). axe-core then runs the WCAG 2.1 A/AA rule sets only — the
 * `best-practice` tag is deliberately excluded from the failing set, because
 * those rules are advisory and not part of the agreed bar.
 * A violation fails the test with the rule id, the CSS target, and the help
 * URL so a failure names the exact fix.
 */

/** The agreed bar: WCAG 2.1 A + AA. `best-practice` is intentionally absent. */
const WCAG_2_1_AA = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']

/**
 * The pinned calendar day shared by the fixtures and the app clock.
 * The outside-month cells are a contrast target (their day numbers use the
 * muted token), and the today cell carries the only entry. Deriving "today"
 * from the real clock meant those cells could silently disappear from the
 * audited render on another date, so the clock is fixed with
 * `page.clock.setFixedTime` (Date only — timers keep running) and the fixtures
 * return the same day. 2026-10-15 is a Thursday, so with `week_starts_on: 0`
 * the October 2026 grid starts on 27 September and always renders four leading
 * outside-month cells for the contrast rule to check.
 */
const TODAY = '2026-10-15'

/**
 * The slice of an axe violation this report needs. Declared locally so the spec
 * does not depend on axe-core's internal type layout (it is a transitive
 * devDependency of `@axe-core/playwright`).
 */
interface AxeViolation {
  id: string
  help: string
  helpUrl: string
  impact?: string | null
  nodes: Array<{ target: Array<string | string[]> }>
}

/** Renders the axe violations in a form that tells the reader what to fix. */
function describeViolations(violations: AxeViolation[]): string {
  if (violations.length === 0) {
    return 'no violations'
  }
  return violations
    .map((violation) => {
      const targets = violation.nodes.map((node) => `      - ${node.target.join(' ')}`).join('\n')
      return [
        `  [${violation.impact ?? 'unknown'}] ${violation.id}: ${violation.help}`,
        `    help: ${violation.helpUrl}`,
        '    targets:',
        targets,
      ].join('\n')
    })
    .join('\n\n')
}

/** Runs axe with the WCAG 2.1 AA tags and asserts zero violations. */
async function expectNoA11yViolations(page: Page, routeName: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(WCAG_2_1_AA).analyze()
  expect(
    results.violations,
    `axe found ${results.violations.length} WCAG 2.1 AA violation(s) on ${routeName}:\n${describeViolations(results.violations)}`,
  ).toHaveLength(0)
}

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

const memberSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 2, username: 'sam', role: 'member' },
  permissions: ['calendar.view', 'groceries.view', 'recipes.view'],
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
  current_version: '1.0.0',
  latest_version: '1.0.0',
  update_available: false,
  last_checked_at: '2026-10-21T12:00:00Z',
  last_error: null,
  enabled: true,
  release_url: 'https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v1.0.0',
}

const TIMESTAMP = '2026-10-12T00:00:00+00:00'

/** A realistic recipe detail (the editor form's edit mode). */
const recipeDetail = {
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
  description: 'A weeknight roast.',
  source_site: null,
  source_url: null,
  author: null,
  prep_minutes: 10,
  cook_minutes: 40,
  notes: 'Double the sauce.',
  ingredient_groups: [{ id: 1, title: 'Main', sort_order: 0, ingredients: [] }],
  ingredients: [
    {
      id: 1,
      ingredient: { id: 5, name: 'Chicken', is_active: true },
      quantity: 2,
      unit: { id: 2, symbol: 'g', display_name: 'gram', is_active: true },
      preparation_note: 'diced',
      is_optional: false,
      sort_order: 0,
    },
  ],
  instruction_sections: [
    {
      id: 1,
      title: 'Cook',
      sort_order: 0,
      steps: [{ id: 1, text: 'Roast until golden.', sort_order: 0 }],
    },
  ],
  proteins: [{ id: 5, name: 'Chicken', is_active: true }],
  tags: [{ id: 7, name: 'Weeknight' }],
  statistics: { last_eaten: '2026-10-01', next_planned: null, times_eaten: 3 },
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

function hexIdentity(fill: string): string {
  return fill.repeat(64).slice(0, 64)
}

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
  {
    department_id: null,
    name: 'Other',
    sort_order: 2147483647,
    is_other: true,
    items: [
      {
        identity: '0000000000000000000000000000000000000000000000000000000000000005',
        name: 'Paper towels',
        quantity_text: '2',
        quantity: 2,
        unit: null,
        ingredient_id: null,
        department_id: null,
        checked: false,
        source_type: 'manual',
        manual_item_id: 5,
        sources: [],
      },
    ],
  },
]

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
  {
    id: 2,
    username: 'sam',
    role: 'member',
    is_active: true,
    must_change_password: false,
    last_login_at: '2026-09-28T12:00:00Z',
    permissions: ['calendar.view', 'groceries.view', 'recipes.view'],
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

interface MockOptions {
  /** `false` renders the setup wizard; defaults to a configured household. */
  setupCompleted?: boolean
  /** Session payload; `null` produces a 401 (the login page). */
  session?: unknown
}

/**
 * Hermetic API for the a11y matrix: one router with the realistic fixtures each
 * audited route needs. Unmatched paths answer 404 so a missing fixture shows up
 * as a visible error state instead of a silently empty page.
 */
async function mockAppApi(page: Page, options: MockOptions = {}): Promise<void> {
  const setupCompleted = options.setupCompleted ?? true
  const session = options.session === undefined ? adminSession : options.session

  // Pin the app's clock to `TODAY` so the calendar renders the same month (and
  // therefore the same outside-month cells) on every run. `setFixedTime`
  // changes Date only; timers, animations and network keep running.
  await page.clock.setFixedTime('2026-10-15T12:00:00')

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()

    if (path.endsWith('/setup/status')) {
      await route.fulfill({
        json: setupCompleted
          ? setupComplete
          : { setup_completed: false, auth_mode: null, recovery_key_present: false },
      })
      return
    }
    if (path.endsWith('/auth/session')) {
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
    if (path === '/about') {
      await route.fulfill({
        json: { app_version: '1.0.0', schema_version: '20261021023131_M13Backups' },
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
      const today = TODAY
      await route.fulfill({
        json: {
          start: url.searchParams.get('start') ?? today,
          end: url.searchParams.get('end') ?? today,
          today,
          week_starts_on: 0,
          entries: [
            {
              date: today,
              is_today: true,
              is_historical: false,
              can_edit: true,
              entry: {
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
                created_at: '2026-11-01T00:00:00+00:00',
                updated_at: '2026-11-01T00:00:00+00:00',
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
      const today = TODAY
      await route.fulfill({
        json: {
          date: today,
          is_today: true,
          is_historical: false,
          can_edit: true,
          entry: {
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
            created_at: '2026-11-01T00:00:00+00:00',
            updated_at: '2026-11-01T00:00:00+00:00',
          },
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

/** The route matrix: every key surface from */
const ROUTES: Array<{
  name: string
  path: string
  /** Mock overrides; defaults to a configured household with an admin session. */
  mock?: MockOptions
  ready: (page: Page) => Promise<void>
}> = [
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
    name: 'login',
    path: '/login',
    mock: { session: null },
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
      await expect(page.getByLabel('Username')).toBeVisible()
    },
  },
  {
    name: 'calendar month',
    path: '/calendar',
    ready: async (page) => {
      await expect(page.getByRole('grid')).toBeVisible()
      await expect(page.getByRole('grid').getByText('Alpha Chicken')).toBeVisible()
      // The outside-month day numbers are a contrast target (muted token); a
      // grid without them would quietly narrow the audit.
      await expect(page.locator('.calendar-grid__cell--outside').first()).toBeVisible()
    },
  },
  {
    name: 'recipe browser',
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
      await expect(page.getByLabel('Name')).toBeVisible()
    },
  },
  {
    name: 'recipe edit editor',
    path: '/recipes/1/edit',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Edit recipe' })).toBeVisible()
      await expect(page.getByLabel('Name')).toHaveValue('Alpha Chicken')
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
    name: 'statistics overview',
    path: '/statistics',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Statistics' })).toBeVisible()
      await expect(page.getByRole('heading', { level: 3, name: 'Most eaten' })).toBeVisible()
    },
  },
  {
    name: 'meal history',
    path: '/statistics?tab=history',
    ready: async (page) => {
      await expect(page.getByRole('region', { name: 'October 2026' })).toBeVisible()
    },
  },
  {
    name: 'dashboard',
    path: '/dashboard',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible()
      await expect(page.getByRole('link', { name: 'Open the calendar' })).toBeVisible()
    },
  },
  {
    name: 'settings household',
    path: '/settings',
    ready: async (page) => {
      await expect(page.getByRole('navigation', { name: 'Settings sections' })).toBeVisible()
      await expect(page.getByRole('heading', { level: 3, name: 'Household' })).toBeVisible()
    },
  },
  {
    name: 'settings users',
    path: '/settings/users',
    ready: async (page) => {
      await expect(page.getByRole('row', { name: /root/ })).toBeVisible()
    },
  },
  {
    // Reference data carries the label fixes: the department reorder
    // buttons (`Move up Produce` contains the visible label) and the ingredient
    // row editors. Auditing the tab is what proves those fixes hold.
    name: 'settings reference data (departments)',
    path: '/settings/reference-data',
    ready: async (page) => {
      await expect(page.getByRole('table', { name: 'Grocery departments' })).toBeVisible()
      await expect(page.getByRole('rowheader', { name: 'Produce' })).toBeVisible()
      await expect(page.getByRole('button', { name: 'Move up Bakery' })).toBeVisible()
    },
  },
  {
    name: 'settings reference data (ingredients)',
    path: '/settings/reference-data/ingredients',
    ready: async (page) => {
      await expect(page.getByRole('table', { name: 'Ingredients' })).toBeVisible()
      await expect(page.getByRole('rowheader', { name: 'Chicken' })).toBeVisible()
      await expect(page.getByRole('button', { name: 'Edit Chicken' })).toBeVisible()
    },
  },
  {
    name: 'settings reference data (units)',
    path: '/settings/reference-data/units',
    ready: async (page) => {
      await expect(page.getByRole('table', { name: 'Units of measure' })).toBeVisible()
      await expect(page.getByRole('rowheader', { name: 'g' })).toBeVisible()
    },
  },
  {
    name: 'settings reference data (proteins)',
    path: '/settings/reference-data/proteins',
    ready: async (page) => {
      await expect(page.getByRole('table', { name: 'Proteins' })).toBeVisible()
      await expect(page.getByRole('rowheader', { name: 'Chicken' })).toBeVisible()
      await expect(page.getByRole('button', { name: 'Rename Chicken' })).toBeVisible()
    },
  },
  {
    name: 'settings reference data (tags)',
    path: '/settings/reference-data/tags',
    ready: async (page) => {
      await expect(page.getByRole('table', { name: 'Tags' })).toBeVisible()
      await expect(page.getByRole('rowheader', { name: 'Weeknight' })).toBeVisible()
      await expect(page.getByRole('button', { name: 'Rename Weeknight' })).toBeVisible()
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
    name: 'settings backups',
    path: '/settings/backups',
    ready: async (page) => {
      await expect(page.getByRole('row', { name: new RegExp(backupFilename) })).toBeVisible()
    },
  },
  {
    name: 'settings audit log',
    path: '/settings/audit',
    ready: async (page) => {
      await expect(page.getByRole('heading', { level: 3, name: 'Audit log' })).toBeVisible()
    },
  },
  {
    name: 'settings about',
    path: '/settings/about',
    ready: async (page) => {
      await expect(page.getByTestId('about-app-version')).toHaveText('1.0.0')
    },
  },
]

test.describe('WCAG 2.1 AA — key routes (desktop)', () => {
  for (const route of ROUTES) {
    test(`${route.name}`, async ({ page }) => {
      await mockAppApi(page, route.mock ?? {})
      await page.goto(route.path)
      await route.ready(page)
      await expectNoA11yViolations(page, route.name)
    })
  }
})

test('calendar date-detail dialog (open state)', async ({ page }) => {
  await mockAppApi(page)

  await page.goto('/calendar')
  await page.getByRole('gridcell', { name: /today/i }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Selected:')).toBeVisible()

  await expectNoA11yViolations(page, 'calendar date-detail dialog')
})

test('groceries manual-item dialog and staples panel', async ({ page }) => {
  await mockAppApi(page)

  await page.goto('/groceries')
  await expect(page.getByRole('list', { name: 'Produce' })).toBeVisible()

  // The staples panel (a disclosure) is part of the audited surface.
  await page.getByRole('button', { name: 'Staples' }).click()
  await expect(page.getByRole('list', { name: 'Staples' })).toBeVisible()
  await expectNoA11yViolations(page, 'groceries staples panel')

  // The manual-item dialog adds a second modal surface.
  await page.getByRole('button', { name: 'Add grocery item' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add grocery item' })
  await expect(dialog).toBeVisible()
  await expectNoA11yViolations(page, 'groceries manual-item dialog')
})

test('settings sections a member can see', async ({ page }) => {
  await mockAppApi(page, { session: memberSession })

  await page.goto('/settings')
  await expect(page.getByRole('navigation', { name: 'Settings sections' })).toBeVisible()
  await expectNoA11yViolations(page, 'settings household (member)')

  await page.goto('/settings/account')
  await expect(page.getByRole('heading', { level: 3, name: 'Account' })).toBeVisible()
  await expectNoA11yViolations(page, 'settings account (member)')

  await page.goto('/settings/about')
  await expect(page.getByTestId('about-app-version')).toHaveText('1.0.0')
  await expectNoA11yViolations(page, 'settings about (member)')
})

const MOBILE_VIEWPORT = { width: 375, height: 720 }

test.describe('WCAG 2.1 AA — 375 px viewport', () => {
  test.use({ viewport: MOBILE_VIEWPORT })

  test('calendar month', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/calendar')
    await expect(page.getByRole('grid')).toBeVisible()
    await expect(page.getByRole('grid').getByText('Alpha Chicken')).toBeVisible()
    await expectNoA11yViolations(page, 'calendar month (375 px)')
  })

  test('calendar date-detail dialog', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/calendar')
    await page.getByRole('gridcell', { name: /today/i }).click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await expectNoA11yViolations(page, 'calendar date-detail dialog (375 px)')
  })

  test('groceries', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/groceries')
    await expect(page.getByRole('list', { name: 'Produce' })).toBeVisible()
    await expectNoA11yViolations(page, 'groceries (375 px)')
  })

  test('recipe browser', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/recipes')
    await expect(page.getByRole('link', { name: 'Alpha Chicken' }).first()).toBeVisible()
    await expectNoA11yViolations(page, 'recipe browser (375 px)')
  })
})

/**
 * The app honours `prefers-color-scheme: dark` (index.css keeps a full dark
 * token set), so the same AA bar applies to the dark palette. The contrast
 * tokens touched by are shared by both schemes.
 */
test.describe('WCAG 2.1 AA — dark colour scheme', () => {
  test.use({ colorScheme: 'dark' })

  test('calendar month', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/calendar')
    await expect(page.getByRole('grid').getByText('Alpha Chicken')).toBeVisible()
    await expectNoA11yViolations(page, 'calendar month (dark)')
  })

  test('groceries', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/groceries')
    await expect(page.getByRole('list', { name: 'Produce' })).toBeVisible()
    await expectNoA11yViolations(page, 'groceries (dark)')
  })

  test('recipe browser', async ({ page }) => {
    await mockAppApi(page)
    await page.goto('/recipes')
    await expect(page.getByRole('link', { name: 'Alpha Chicken' }).first()).toBeVisible()
    await expectNoA11yViolations(page, 'recipe browser (dark)')
  })
})
