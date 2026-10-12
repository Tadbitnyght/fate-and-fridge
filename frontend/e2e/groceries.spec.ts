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

/** A member who may read groceries but not write them. */
const viewerSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 2, username: 'sam', role: 'member' },
  permissions: ['calendar.view', 'groceries.view'],
  must_change_password: false,
}

interface GrocerySource {
  recipe_id: number
  recipe_name: string
  date: string
}

interface GroceryItem {
  identity: string
  name: string
  quantity_text: string
  quantity: number | null
  unit: string | null
  ingredient_id: number | null
  department_id: number | null
  checked: boolean
  source_type: 'recipe' | 'manual'
  manual_item_id: number | null
  sources: GrocerySource[]
}

interface GroceryDepartment {
  department_id: number | null
  name: string
  sort_order: number
  is_other: boolean
  items: GroceryItem[]
}

interface GroceryStaple {
  id: number
  name: string
  ingredient_id: number | null
  department_id: number | null
  default_quantity_numeric: number | null
  unit_id: number | null
  is_active: boolean
  created_at: string
  updated_at: string
}

interface GroceryPreferences {
  hide_checked: boolean
  range_preset: string | null
  range_start: string | null
  range_days: number | null
}

interface RecordedRequest {
  method: string
  path: string
  body?: Record<string, unknown>
}

const RANGE = { preset: 'next_7_days', start: '2026-10-12', end: '2026-10-18', day_count: 7 }
const TIMESTAMP = '2026-10-12T00:00:00+00:00'

/** Builds a realistic 64-char lowercase hex identity from a repeat character. */
function hexIdentity(fill: string): string {
  return fill.repeat(64).slice(0, 64)
}

/** Mirrors `GroceryItemIdentity.ForManualItem` shape: a 64-char lowercase hex hash. */
function manualIdentity(id: number): string {
  return id.toString(16).padStart(64, '0')
}

function groceryItem(overrides: Partial<GroceryItem> & { identity: string }): GroceryItem {
  return {
    name: 'Item',
    quantity_text: '',
    quantity: null,
    unit: null,
    ingredient_id: null,
    department_id: null,
    checked: false,
    source_type: 'recipe',
    manual_item_id: null,
    sources: [],
    ...overrides,
  }
}

function defaultDepartments(): GroceryDepartment[] {
  return [
    {
      department_id: 1,
      name: 'Produce',
      sort_order: 1,
      is_other: false,
      items: [
        groceryItem({
          identity: hexIdentity('a'),
          name: 'Onion',
          quantity_text: '2',
          quantity: 2,
          ingredient_id: 42,
          department_id: 1,
          sources: [{ recipe_id: 5, recipe_name: 'Tacos', date: '2026-10-13' }],
        }),
        groceryItem({
          identity: hexIdentity('b'),
          name: 'Carrot',
          checked: true,
          department_id: 1,
        }),
      ],
    },
    {
      department_id: 2,
      name: 'Bakery',
      sort_order: 2,
      is_other: false,
      items: [groceryItem({ identity: hexIdentity('c'), name: 'Bread', department_id: 2 })],
    },
    {
      department_id: null,
      name: 'Other',
      sort_order: 2147483647,
      is_other: true,
      items: [
        groceryItem({
          identity: manualIdentity(5),
          name: 'Paper towels',
          source_type: 'manual',
          manual_item_id: 5,
          quantity_text: '2',
          quantity: 2,
        }),
      ],
    },
  ]
}

function defaultStaples(): GroceryStaple[] {
  return [
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
}

const REFERENCE_DEPARTMENTS = [
  { id: 1, name: 'Produce', sort_order: 1, is_active: true, created_at: '', updated_at: '' },
  { id: 2, name: 'Bakery', sort_order: 2, is_active: true, created_at: '', updated_at: '' },
]

const REFERENCE_UNITS = [
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

const REFERENCE_INGREDIENTS = [
  {
    id: 42,
    canonical_name: 'Milk',
    department_id: 1,
    is_active: true,
    created_at: '',
    updated_at: '',
    aliases: [],
  },
]

/** Unchecked first, then checked; alphabetical by name within each group. */
function orderItems(items: GroceryItem[]): GroceryItem[] {
  return [...items].sort((left, right) => {
    if (left.checked !== right.checked) {
      return left.checked ? 1 : -1
    }
    return left.name.localeCompare(right.name)
  })
}

interface GroceryMockOptions {
  session?: unknown
  departments?: GroceryDepartment[]
  staples?: GroceryStaple[]
  preferences?: GroceryPreferences
}

/**
 * Hermetic grocery API: a mutable in-memory mirror of the docs/API.md §13
 * contract plus request recording, so the UI can be driven without a backend.
 * Each mutation updates the mock state and the following list read re-applies
 * the server's unchecked-then-checked ordering.
 */
async function mockGroceryApi(
  page: Page,
  options: GroceryMockOptions = {},
): Promise<{ requests: RecordedRequest[] }> {
  const departments = structuredClone(options.departments ?? defaultDepartments())
  const staples = structuredClone(options.staples ?? defaultStaples())
  const preferences: GroceryPreferences = structuredClone(
    options.preferences ?? {
      hide_checked: false,
      range_preset: 'next_7_days',
      range_start: null,
      range_days: null,
    },
  )
  const session = options.session ?? adminSession
  const requests: RecordedRequest[] = []
  let manualCounter = 100

  const record = (method: string, path: string, body?: Record<string, unknown>): void => {
    requests.push(body === undefined ? { method, path } : { method, path, body })
  }

  const unitSymbol = (unitId: number | null): string | null =>
    unitId === null ? null : (REFERENCE_UNITS.find((unit) => unit.id === unitId)?.symbol ?? null)

  const findItem = (identity: string): GroceryItem | undefined => {
    for (const department of departments) {
      const item = department.items.find((row) => row.identity === identity)
      if (item) {
        return item
      }
    }
    return undefined
  }

  const departmentFor = (departmentId: number | null): GroceryDepartment =>
    departments.find((row) => row.department_id === departmentId) ??
    departments[departments.length - 1]

  const addManualItem = (fields: {
    name: string
    quantity: number | null
    unit_id: number | null
    ingredient_id: number | null
    department_id: number | null
    range_start: string
    range_days: number
    save_for_future: boolean
  }): Record<string, unknown> => {
    const id = manualCounter++
    const department = departmentFor(fields.department_id)
    department.items.push(
      groceryItem({
        identity: manualIdentity(id),
        name: fields.name,
        quantity_text: fields.quantity !== null ? String(fields.quantity) : '',
        quantity: fields.quantity,
        unit: unitSymbol(fields.unit_id),
        ingredient_id: fields.ingredient_id,
        department_id: department.department_id,
        source_type: 'manual',
        manual_item_id: id,
      }),
    )
    return {
      id,
      name: fields.name,
      quantity_numeric: fields.quantity,
      unit_id: fields.unit_id,
      ingredient_id: fields.ingredient_id,
      department_id: department.department_id,
      range_start: fields.range_start,
      range_days: fields.range_days,
      save_for_future: fields.save_for_future,
      created_at: TIMESTAMP,
      updated_at: TIMESTAMP,
    }
  }

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname.replace(/^\/api\/v1/, '')
    const method = request.method()
    const rawBody = request.postData()
    const body = rawBody === null ? undefined : (JSON.parse(rawBody) as Record<string, unknown>)

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: session })
      return
    }
    if (path === '/health' || path === '/health/db') {
      await route.fulfill({ json: { status: 'ok', database: 'connected' } })
      return
    }
    if (path === '/departments' && method === 'GET') {
      await route.fulfill({ json: REFERENCE_DEPARTMENTS })
      return
    }
    if (path === '/units' && method === 'GET') {
      await route.fulfill({ json: REFERENCE_UNITS })
      return
    }
    if (path === '/ingredients' && method === 'GET') {
      await route.fulfill({ json: REFERENCE_INGREDIENTS })
      return
    }

    if (path === '/groceries/preferences' && method === 'GET') {
      await route.fulfill({ json: preferences })
      return
    }
    if (path === '/groceries/preferences' && method === 'PATCH') {
      record('PATCH', path, body)
      if (typeof body?.['hide_checked'] === 'boolean') {
        preferences.hide_checked = body['hide_checked']
      }
      await route.fulfill({ json: preferences })
      return
    }

    if (path === '/groceries/staples' && method === 'GET') {
      await route.fulfill({ json: { staples } })
      return
    }
    const addToListMatch = /^\/groceries\/staples\/(\d+)\/add-to-list$/.exec(path)
    if (addToListMatch && method === 'POST') {
      record('POST', path, body)
      const staple = staples.find((row) => row.id === Number(addToListMatch[1]))
      if (!staple || !staple.is_active) {
        await route.fulfill({
          status: 404,
          json: { error: { code: 'NOT_FOUND', message: 'missing', details: {} } },
        })
        return
      }
      const manual = addManualItem({
        name: staple.name,
        quantity: staple.default_quantity_numeric,
        unit_id: staple.unit_id,
        ingredient_id: staple.ingredient_id,
        department_id: staple.department_id,
        range_start: String(body?.['range_start'] ?? RANGE.start),
        range_days: Number(body?.['range_days'] ?? RANGE.day_count),
        save_for_future: false,
      })
      await route.fulfill({ status: 201, json: manual })
      return
    }

    if (path === '/groceries/manual' && method === 'POST') {
      record('POST', path, body)
      const manual = addManualItem({
        name: String(body?.['name'] ?? ''),
        quantity: (body?.['quantity_numeric'] as number | null) ?? null,
        unit_id: (body?.['unit_id'] as number | null) ?? null,
        ingredient_id: (body?.['ingredient_id'] as number | null) ?? null,
        department_id: (body?.['department_id'] as number | null) ?? null,
        range_start: String(body?.['range_start'] ?? RANGE.start),
        range_days: Number(body?.['range_days'] ?? RANGE.day_count),
        save_for_future: body?.['save_for_future'] === true,
      })
      await route.fulfill({ status: 201, json: manual })
      return
    }

    if (path === '/groceries/check' && method === 'POST') {
      record('POST', path, body)
      const identity = String(body?.['identity'] ?? '')
      const item = findItem(identity)
      if (item) {
        item.checked = true
      }
      await route.fulfill({
        json: { identity, checked: true, checked_at: TIMESTAMP, checked_by_user_id: 1 },
      })
      return
    }
    if (path === '/groceries/uncheck' && method === 'POST') {
      record('POST', path, body)
      const identity = String(body?.['identity'] ?? '')
      const item = findItem(identity)
      if (item) {
        item.checked = false
      }
      await route.fulfill({
        json: { identity, checked: false, checked_at: null, checked_by_user_id: 1 },
      })
      return
    }

    if (path === '/groceries' && method === 'GET') {
      await route.fulfill({
        json: {
          range: RANGE,
          departments: departments.map((department) => ({
            ...department,
            items: orderItems(department.items),
          })),
        },
      })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${method} ${path}`, details: {} } },
    })
  })

  return { requests }
}

test('groceries: departments render in order with Other last and attribution', async ({ page }) => {
  await mockGroceryApi(page)

  await page.goto('/groceries')
  await expect(page.getByRole('heading', { level: 2, name: 'Groceries' })).toBeVisible()

  const headings = await page.getByRole('heading', { level: 3 }).allTextContents()
  expect(headings).toEqual(['Produce', 'Bakery', 'Other'])

  const produce = page.getByRole('list', { name: 'Produce' })
  await expect(produce.getByText('Onion')).toBeVisible()
  await expect(produce.getByText(/Tacos/)).toBeVisible()
  await expect(produce.getByText(/Tue 10\/13/)).toBeVisible()
})

test('groceries: checking an item strikes it through and moves it below unchecked', async ({
  page,
}) => {
  const { requests } = await mockGroceryApi(page, {
    departments: [
      {
        department_id: 1,
        name: 'Produce',
        sort_order: 1,
        is_other: false,
        items: [
          groceryItem({ identity: hexIdentity('a'), name: 'Apple', department_id: 1 }),
          groceryItem({ identity: hexIdentity('b'), name: 'Banana', department_id: 1 }),
        ],
      },
    ],
  })

  await page.goto('/groceries')
  const produce = page.getByRole('list', { name: 'Produce' })
  await expect(produce.getByText('Apple')).toBeVisible()

  const labelsBefore = await produce
    .getByRole('checkbox')
    .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('aria-label')))
  expect(labelsBefore).toEqual(['Mark Apple as bought', 'Mark Banana as bought'])

  await page.getByLabel('Mark Apple as bought').click()

  await expect(page.getByLabel('Mark Apple as not bought')).toBeChecked()
  const labelsAfter = await produce
    .getByRole('checkbox')
    .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('aria-label')))
  expect(labelsAfter).toEqual(['Mark Banana as bought', 'Mark Apple as not bought'])

  // The checked line is struck through (M8-07 §1.12).
  await expect(page.getByText('Apple', { exact: true })).toHaveCSS(
    'text-decoration-line',
    'line-through',
  )

  expect(requests).toContainEqual({
    method: 'POST',
    path: '/groceries/check',
    body: { identity: hexIdentity('a') },
  })
})

test('groceries: the hide-checked toggle persists through preferences', async ({ page }) => {
  const { requests } = await mockGroceryApi(page)

  await page.goto('/groceries')
  await expect(page.getByText('Carrot')).toBeVisible()

  await page.getByLabel('Hide checked items').check()

  await expect(page.getByText('Carrot')).toHaveCount(0)
  const patch = requests.find(
    (entry) => entry.method === 'PATCH' && entry.path === '/groceries/preferences',
  )
  expect(patch?.body).toMatchObject({ hide_checked: true })

  // The preference is server-side, so a reload brings back the hidden list.
  await page.reload()
  await expect(page.getByLabel('Hide checked items')).toBeChecked()
  await expect(page.getByText('Carrot')).toHaveCount(0)
})

test('groceries: adds a manual item from the dialog', async ({ page }) => {
  const { requests } = await mockGroceryApi(page)

  await page.goto('/groceries')
  await page.getByRole('button', { name: 'Add grocery item' }).click()

  const dialog = page.getByRole('dialog', { name: 'Add grocery item' })
  await dialog.getByLabel('Name').fill('Dish soap')
  await dialog.getByRole('button', { name: 'Add item' }).click()

  await expect(dialog).toHaveCount(0)
  await expect(page.getByRole('list', { name: 'Other' }).getByText('Dish soap')).toBeVisible()

  const post = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/groceries/manual',
  )
  expect(post?.body).toMatchObject({
    name: 'Dish soap',
    range_start: '2026-10-12',
    range_days: 7,
  })
})

test('groceries: adds a staple to the list as a manual item', async ({ page }) => {
  const { requests } = await mockGroceryApi(page)

  await page.goto('/groceries')
  await page.getByRole('button', { name: 'Staples' }).click()

  const staples = page.getByRole('list', { name: 'Staples' })
  await expect(staples.getByText('Milk', { exact: true })).toBeVisible()
  await staples.getByRole('button', { name: 'Add to list' }).click()

  const produce = page.getByRole('list', { name: 'Produce' })
  await expect(produce.getByText('Milk', { exact: true })).toBeVisible()
  const row = produce.getByRole('listitem').filter({ hasText: 'Milk' })
  await expect(row.getByText('Manual')).toBeVisible()

  const post = requests.find(
    (entry) =>
      entry.method === 'POST' && /^\/groceries\/staples\/\d+\/add-to-list$/.test(entry.path),
  )
  expect(post?.body).toMatchObject({ range_start: '2026-10-12', range_days: 7 })
})

test('groceries: write controls are hidden or disabled without groceries.edit', async ({
  page,
}) => {
  await mockGroceryApi(page, { session: viewerSession })

  await page.goto('/groceries')
  await expect(page.getByText('Onion')).toBeVisible()

  await expect(page.getByRole('button', { name: 'Add grocery item' })).toHaveCount(0)
  await expect(page.getByLabel('Mark Onion as bought')).toBeDisabled()

  await page.getByRole('button', { name: 'Staples' }).click()
  const staples = page.getByRole('list', { name: 'Staples' })
  await expect(staples.getByRole('button', { name: 'Add to list' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'New staple' })).toHaveCount(0)
})

test('groceries: the list offers a print entry point that opens the print view', async ({
  page,
}) => {
  await mockGroceryApi(page)

  await page.goto('/groceries')
  await expect(page.getByText('Onion')).toBeVisible()

  await page.getByRole('link', { name: 'Print' }).click()

  await expect(page).toHaveURL(/\/groceries\/print$/)
  await expect(page.getByRole('heading', { level: 2, name: /Grocery list/ })).toBeVisible()
})

test('groceries print: departments, attribution, manual badge, and the include-checked toggle', async ({
  page,
}) => {
  await mockGroceryApi(page)

  await page.goto('/groceries/print')
  await expect(page.getByRole('heading', { level: 2, name: /Grocery list/ })).toContainText(
    'Oct 12 – Oct 18',
  )

  const headings = await page.getByRole('heading', { level: 3 }).allTextContents()
  expect(headings).toEqual(['Produce', 'Bakery', 'Other'])

  const produce = page.getByRole('list', { name: 'Produce' })
  await expect(produce.getByText('Onion')).toBeVisible()
  await expect(produce.getByText(/Tacos/)).toBeVisible()
  await expect(produce.getByText(/Tue 10\/13/)).toBeVisible()

  // Manual items are included and carry the icon+text Manual badge.
  const other = page.getByRole('list', { name: 'Other' })
  await expect(other.getByText('Paper towels')).toBeVisible()
  await expect(
    other.getByRole('listitem').filter({ hasText: 'Paper towels' }).getByText('Manual'),
  ).toBeVisible()

  // A checked item is excluded by default and revealed by the toggle.
  await expect(page.getByText('Carrot')).toHaveCount(0)
  await page.getByLabel('Include checked items').check()
  await expect(page.getByText('Carrot')).toBeVisible()
})

test('groceries: mobile layout keeps department headers sticky and tap targets ≥44px', async ({
  page,
}) => {
  await mockGroceryApi(page)
  await page.setViewportSize({ width: 375, height: 720 })

  await page.goto('/groceries')
  await expect(page.getByText('Onion')).toBeVisible()

  // The department header is sticky so the group label stays anchored while the
  // list scrolls (docs/M8_PLAN.md §2.4).
  await expect(page.locator('.grocery-department__header').first()).toHaveCSS('position', 'sticky')

  // The primary controls expose a ≥44px tap target on a phone viewport. The
  // row checkbox is wrapped by its label, which is the touch target.
  const targets = [
    page.getByRole('button', { name: 'Add grocery item' }),
    page.getByRole('button', { name: 'Staples' }),
    page.locator('.grocery-controls__hide'),
    page.locator('.grocery-item__label').first(),
  ]

  for (const target of targets) {
    await expect(target).toBeVisible()
    const box = await target.boundingBox()
    expect(box, 'tap target is laid out').not.toBeNull()
    expect(box!.height).toBeGreaterThanOrEqual(44)
  }
})

test('groceries print: print media hides app chrome and screen-only controls', async ({ page }) => {
  await mockGroceryApi(page)

  await page.goto('/groceries/print')
  await expect(page.getByRole('list', { name: 'Produce' }).getByText('Onion')).toBeVisible()

  await page.emulateMedia({ media: 'print' })

  await expect(page.locator('.app-header')).toBeHidden()
  await expect(page.locator('.app-nav')).toBeHidden()
  await expect(page.locator('.app-footer')).toBeHidden()
  await expect(page.locator('.skip-link')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Print' })).toBeHidden()
  await expect(page.getByLabel('Include checked items')).toBeHidden()

  // Black-on-white: the printable list drops the screen palette.
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(255, 255, 255)')
  await expect(page.locator('body')).toHaveCSS('color', 'rgb(0, 0, 0)')

  // The printable list and its paper checkboxes survive.
  await expect(page.getByRole('heading', { level: 3, name: 'Produce' })).toBeVisible()
  await expect(page.getByRole('list', { name: 'Produce' }).getByText('Onion')).toBeVisible()
  const box = page.locator('.grocery-print__box').first()
  await expect(box).toBeVisible()
  await expect(box).toHaveCSS('border-top-color', 'rgb(0, 0, 0)')
})
