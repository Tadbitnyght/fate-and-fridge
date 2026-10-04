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

interface RecipeRow {
  id: number
  name: string
  status: string
  is_favorite: boolean
  is_vegan: boolean
  is_vegetarian: boolean
  image: null
  last_eaten: null
  times_eaten: null
  created_at: string
  updated_at: string
}

interface RecipeDetail extends RecipeRow {
  description: null
  source_site: null
  source_url: null
  author: null
  prep_minutes: null
  cook_minutes: null
  notes: null
  ingredient_groups: unknown[]
  ingredients: unknown[]
  instruction_sections: unknown[]
  proteins: Array<{ id: number; name: string; is_active: boolean }>
  tags: Array<{ id: number; name: string }>
  statistics: { last_eaten: null; next_planned: null; times_eaten: null }
}

function detail(
  name: string,
  recipeIngredients: unknown[],
  sections: unknown[],
  proteins: Array<{ id: number; name: string; is_active: boolean }>,
  tags: Array<{ id: number; name: string }>,
): RecipeDetail {
  return {
    id: 1,
    name,
    status: 'normal',
    is_favorite: false,
    is_vegan: false,
    is_vegetarian: false,
    image: null,
    last_eaten: null,
    times_eaten: null,
    created_at: '2026-10-01T10:00:00Z',
    updated_at: '2026-10-01T10:00:00Z',
    description: null,
    source_site: null,
    source_url: null,
    author: null,
    prep_minutes: null,
    cook_minutes: null,
    notes: null,
    ingredient_groups: [],
    ingredients: recipeIngredients,
    instruction_sections: sections,
    proteins,
    tags,
    statistics: { last_eaten: null, next_planned: null, times_eaten: null },
  }
}

/**
 * Hermetic recipe API: a mutable library plus request recording, so payload
 * assertions never need a real backend (mirrors e2e/reference-data.spec.ts).
 */
async function mockRecipeApi(page: Page): Promise<{ requests: RecordedRequest[] }> {
  const now = '2026-10-01T10:00:00Z'
  const chicken = { id: 5, name: 'Chicken', is_active: true }
  const tag = { id: 7, name: 'Weeknight' }
  const state = {
    recipes: [
      {
        id: 1,
        name: 'Alpha Chicken',
        status: 'normal',
        is_favorite: true,
        is_vegan: false,
        is_vegetarian: false,
        image: null,
        last_eaten: null,
        times_eaten: null,
        created_at: now,
        updated_at: now,
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
        times_eaten: null,
        created_at: now,
        updated_at: now,
      },
    ] as RecipeRow[],
    detail: detail('Alpha Chicken', [], [], [chicken], [tag]),
  }
  const requests: RecordedRequest[] = []

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname.replace(/^\/api\/v1/, '')
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
    if (path === '/ingredients') {
      await route.fulfill({
        json: [
          {
            id: 5,
            canonical_name: 'Chicken',
            department_id: null,
            is_active: true,
            created_at: now,
            updated_at: now,
            aliases: [],
          },
        ],
      })
      return
    }
    if (path === '/units') {
      await route.fulfill({
        json: [
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
        ],
      })
      return
    }
    if (path === '/proteins') {
      await route.fulfill({ json: [chicken] })
      return
    }
    if (path === '/tags') {
      await route.fulfill({ json: [tag] })
      return
    }

    if (path === '/recipes' && method === 'GET') {
      const query = new URL(request.url()).searchParams
      let items = state.recipes
      const search = query.get('search')
      if (search) {
        items = items.filter((recipe) => recipe.name.toLowerCase().includes(search.toLowerCase()))
      }
      const status = query.get('status')
      if (status) {
        items = items.filter((recipe) => recipe.status === status)
      }
      await route.fulfill({ json: { items, total: items.length, limit: 24, offset: 0 } })
      return
    }

    if (path === '/recipes' && method === 'POST') {
      const name = String(body?.['name'] ?? '')
      const writeIngredients = (body?.['ingredients'] as Array<Record<string, unknown>>) ?? []
      const ingredients = writeIngredients.map((row, index) => ({
        id: index + 1,
        ingredient: {
          id: Number(row['ingredient_id']),
          name: chicken.name,
          is_active: true,
        },
        quantity: row['quantity'] ?? null,
        unit: null,
        preparation_note: row['preparation_note'] ?? null,
        is_optional: Boolean(row['is_optional']),
        sort_order: index,
      }))
      const writeSections = (body?.['instruction_sections'] as Array<Record<string, unknown>>) ?? []
      const sections = writeSections.map((section, index) => ({
        id: index + 1,
        title: section['title'] ?? null,
        sort_order: index,
        steps: ((section['steps'] as string[]) ?? []).map((text, stepIndex) => ({
          id: stepIndex + 1,
          text,
          sort_order: stepIndex,
        })),
      }))
      const created = detail(name, ingredients, sections, [chicken], [tag])
      state.detail = created
      await route.fulfill({ status: 201, json: created })
      return
    }

    if (path === '/recipes/1' && method === 'GET') {
      await route.fulfill({ json: state.detail })
      return
    }
    if (path === '/recipes/1' && method === 'PATCH') {
      state.detail = { ...state.detail, name: String(body?.['name'] ?? state.detail.name) }
      await route.fulfill({ json: state.detail })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock route for ${method} ${path}` } },
    })
  })

  return { requests }
}

test('recipes: browse the card grid and filter through the server API', async ({ page }) => {
  const { requests } = await mockRecipeApi(page)

  await page.goto('/recipes')

  await expect(page.getByRole('heading', { level: 2, name: 'Recipes' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Alpha Chicken' })).toBeVisible()
  await expect(page.locator('.recipe-badges').filter({ hasText: 'Retired' })).toBeVisible()
  await expect(page.getByText('No history yet').first()).toBeVisible()

  await page.getByLabel('Status').selectOption('retired')
  await expect(page.getByRole('link', { name: 'Alpha Chicken' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Beta Beef' })).toBeVisible()

  await page.getByRole('button', { name: 'List' }).click()
  await expect(page.getByRole('table', { name: 'Recipes' })).toBeVisible()

  expect(requests.some((entry) => entry.method === 'GET' && entry.path === '/recipes')).toBe(true)
})

test('recipes: create then edit with ingredients and instructions', async ({ page }) => {
  const { requests } = await mockRecipeApi(page)

  await page.goto('/recipes/new')
  await expect(page.getByRole('heading', { level: 2, name: 'New recipe' })).toBeVisible()

  await page.getByLabel('Name').fill('New Dinner')
  await page.getByRole('button', { name: 'Add ingredient' }).click()
  await page.getByLabel('Ingredient', { exact: true }).selectOption('5')
  await page.getByLabel('Quantity').fill('1.5')
  await page.getByRole('button', { name: 'Add section' }).click()
  await page.getByLabel('Step 1', { exact: true }).fill('Roast the chicken.')
  await page.getByLabel('Chicken').check()
  await page.getByLabel('Weeknight').check()

  await page.getByRole('button', { name: 'Create recipe' }).click()

  await expect(page).toHaveURL('/recipes/1/edit')
  const createRequest = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/recipes',
  )
  expect(createRequest?.body).toMatchObject({
    name: 'New Dinner',
    ingredients: [{ ingredient_id: 5, quantity: 1.5, unit_id: null, is_optional: false }],
    instruction_sections: [{ title: null, steps: ['Roast the chicken.'] }],
    protein_ids: [5],
    tag_ids: [7],
  })

  const nameInput = page.getByLabel('Name')
  await expect(nameInput).toHaveValue('New Dinner')
  await nameInput.fill('New Dinner (edited)')
  await page.getByRole('button', { name: 'Save changes' }).click()

  await expect(page.getByRole('status')).toContainText('Recipe saved.')
  const patchRequest = requests.find(
    (entry) => entry.method === 'PATCH' && entry.path === '/recipes/1',
  )
  expect(patchRequest?.body).toMatchObject({ name: 'New Dinner (edited)' })
})

test('recipes: create a recipe with the keyboard only', async ({ page }) => {
  const { requests } = await mockRecipeApi(page)

  await page.goto('/recipes/new')
  await expect(page.getByRole('heading', { level: 2, name: 'New recipe' })).toBeVisible()

  await page.getByLabel('Name').focus()
  await page.keyboard.type('Keyboard Dinner')
  await page.keyboard.press('Enter')

  await expect(page).toHaveURL('/recipes/1/edit')
  expect(requests.some((entry) => entry.method === 'POST' && entry.path === '/recipes')).toBe(true)
})

test('recipes: the card grid collapses to one column on a phone viewport', async ({ page }) => {
  await mockRecipeApi(page)
  await page.setViewportSize({ width: 390, height: 844 })

  await page.goto('/recipes')
  await expect(page.getByRole('link', { name: 'Alpha Chicken' })).toBeVisible()

  const columns = await page
    .locator('.recipe-grid')
    .evaluate(
      (element) => element.ownerDocument.defaultView!.getComputedStyle(element).gridTemplateColumns,
    )
  expect(columns.trim().split(/\s+/)).toHaveLength(1)
})
