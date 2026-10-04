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

interface ImportDraftOptions {
  /** Number of parsed candidates to return (>= 1). */
  candidates?: number
}

/**
 * Hermetic import + recipe API (mirrors e2e/recipes.spec.ts): route interception
 * for `POST /import/recipe-url` and `POST /recipes`, no real backend. Records
 * every request so payload/navigation assertions stay deterministic.
 */
async function mockImportApi(
  page: Page,
  options: ImportDraftOptions = {},
): Promise<{ requests: RecordedRequest[] }> {
  const now = '2026-10-03T00:00:00+00:00'
  const candidateCount = options.candidates ?? 1
  const requests: RecordedRequest[] = []

  const draft = (index: number, name: string) => ({
    index,
    name,
    description: 'A cozy bake',
    author: 'Jane Cook',
    source_url: 'https://example.com/lasagna',
    source_site: 'example.com',
    prep_minutes: 20,
    cook_minutes: 45,
    notes: null,
    image_source_url: null,
    name_matches: [],
    ingredients: [
      {
        raw: '1 1/2 cups flour, sifted',
        quantity: 1.5,
        name: 'Flour',
        preparation_note: 'sifted',
        matched_ingredient_id: 12,
        match_kind: 'exact',
        is_unknown: false,
        suggestions: [],
        unit: {
          raw: 'cups',
          matched_unit_id: 3,
          matched_symbol: 'cup',
          is_unknown: false,
          suggested_dimension: null,
          suggestions: [],
        },
      },
    ],
    instruction_sections: [{ title: null, steps: ['Boil the noodles.'] }],
    proteins: [],
    tags: [],
    warnings: [],
  })

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
            id: 12,
            canonical_name: 'Flour',
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
            id: 3,
            symbol: 'cup',
            display_name: 'cup',
            dimension: 'volume',
            base_factor: 1,
            is_custom: false,
            is_active: true,
            aliases: [],
          },
        ],
      })
      return
    }
    if (path === '/proteins' || path === '/tags') {
      await route.fulfill({ json: [] })
      return
    }
    if (path === '/import/recipe-url' && method === 'POST') {
      const recipes =
        candidateCount === 1
          ? [draft(0, 'Lasagna')]
          : [draft(0, 'Lasagna'), draft(1, 'Lasagna Bolognese')]
      await route.fulfill({
        json: {
          source: {
            url: 'https://example.com/lasagna',
            site: 'example.com',
            page_title: null,
            author: 'Jane Cook',
            fetched_at: now,
            parse_method: 'json_ld',
          },
          source_url_match: null,
          capabilities: { can_create_reference_data: false },
          recipes,
        },
      })
      return
    }
    if (path === '/recipes' && method === 'POST') {
      const created = {
        id: 1,
        name: String(body?.['name'] ?? 'Imported'),
        description: null,
        source_site: null,
        source_url: null,
        author: null,
        prep_minutes: null,
        cook_minutes: null,
        notes: null,
        status: 'normal',
        is_favorite: false,
        is_vegan: false,
        is_vegetarian: false,
        created_at: now,
        updated_at: now,
        image: null,
        ingredient_groups: [],
        ingredients: [],
        instruction_sections: [],
        proteins: [],
        tags: [],
        statistics: { last_eaten: null, next_planned: null, times_eaten: null },
      }
      await route.fulfill({ status: 201, json: created })
      return
    }
    if (path === '/recipes/1' && method === 'GET') {
      await route.fulfill({
        json: {
          id: 1,
          name: 'Lasagna',
          description: null,
          source_site: null,
          source_url: null,
          author: null,
          prep_minutes: null,
          cook_minutes: null,
          notes: null,
          status: 'normal',
          is_favorite: false,
          is_vegan: false,
          is_vegetarian: false,
          created_at: now,
          updated_at: now,
          image: null,
          ingredient_groups: [],
          ingredients: [],
          instruction_sections: [],
          proteins: [],
          tags: [],
          statistics: { last_eaten: null, next_planned: null, times_eaten: null },
        },
      })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock route for ${method} ${path}` } },
    })
  })

  return { requests }
}

test('import: paste a URL, review the draft, and save', async ({ page }) => {
  const { requests } = await mockImportApi(page)

  await page.goto('/recipes/import')
  await expect(page.getByRole('heading', { level: 2, name: 'Import from URL' })).toBeVisible()

  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  await expect(
    page.getByRole('heading', { level: 2, name: 'Review imported recipe' }),
  ).toBeVisible()
  await expect(page.getByLabel('Name')).toHaveValue('Lasagna')
  await expect(page.getByRole('textbox', { name: 'Step 1', exact: true })).toHaveValue(
    'Boil the noodles.',
  )

  await page.getByRole('button', { name: 'Save recipe' }).click()

  await expect(page).toHaveURL('/recipes/1/edit')
  const createRequest = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/recipes',
  )
  expect(createRequest?.body).toMatchObject({
    name: 'Lasagna',
    ingredients: [{ ingredient_id: 12, quantity: 1.5, unit_id: 3, preparation_note: 'sifted' }],
  })
  const parseRequest = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/import/recipe-url',
  )
  expect(parseRequest?.body).toEqual({ url: 'https://example.com/lasagna' })
})

test('import: pick a candidate when the page has several recipes', async ({ page }) => {
  const { requests } = await mockImportApi(page, { candidates: 2 })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/many')
  await page.getByRole('button', { name: 'Import' }).click()

  await expect(page.getByText('This page has 2 recipes')).toBeVisible()
  await page.getByRole('radio', { name: 'Lasagna Bolognese' }).check()
  await expect(page.getByLabel('Name')).toHaveValue('Lasagna Bolognese')

  await page.getByRole('button', { name: 'Save recipe' }).click()
  await expect(page).toHaveURL('/recipes/1/edit')

  const createRequest = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/recipes',
  )
  expect(createRequest?.body).toMatchObject({ name: 'Lasagna Bolognese' })
})

test('import: an entry point on the recipe browser opens the import page', async ({ page }) => {
  await mockImportApi(page)

  await page.goto('/recipes')
  await page.getByRole('link', { name: 'Import from URL' }).click()
  await expect(page).toHaveURL('/recipes/import')
  await expect(page.getByRole('heading', { level: 2, name: 'Import from URL' })).toBeVisible()
})
