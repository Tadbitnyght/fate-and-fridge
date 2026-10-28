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
  /** Include an unmatched "salt" line that must be resolved in review. */
  unknownIngredient?: boolean
  /** Give the matched flour line an unrecognized unit ("pkg"). */
  unknownUnit?: boolean
  /** Report that the actor may create reference data inline. */
  canCreateReferenceData?: boolean
  /** Report that the page's exact normalized source URL already exists. */
  sourceUrlMatch?: boolean
  /** Similar-name candidates on the parsed draft. */
  nameMatches?: Array<{ id: number; name: string }>
  /** Similar-name candidates returned by the rename recheck. */
  checkNameMatches?: Array<{ id: number; name: string }>
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
    name_matches: options.nameMatches ?? [],
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
        unit:
          options.unknownUnit === true
            ? {
                raw: 'pkg',
                matched_unit_id: null,
                matched_symbol: null,
                is_unknown: true,
                suggested_dimension: 'mass',
                suggestions: [{ id: 20, symbol: 'g', display_name: 'gram' }],
              }
            : {
                raw: 'cups',
                matched_unit_id: 3,
                matched_symbol: 'cup',
                is_unknown: false,
                suggested_dimension: null,
                suggestions: [],
              },
      },
      ...(options.unknownIngredient === true
        ? [
            {
              raw: 'a pinch of salt',
              quantity: null,
              name: 'salt',
              preparation_note: null,
              matched_ingredient_id: null,
              match_kind: null,
              is_unknown: true,
              suggestions: [{ id: 14, name: 'Salt' }],
              unit: {
                raw: '',
                matched_unit_id: null,
                matched_symbol: null,
                is_unknown: false,
                suggested_dimension: null,
                suggestions: [],
              },
            },
          ]
        : []),
    ],
    instruction_sections: [{ title: null, steps: ['Boil the noodles.'] }],
    proteins: [],
    tags: [],
    warnings: options.unknownIngredient === true ? ['ingredient_unknown'] : [],
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
          {
            id: 14,
            canonical_name: 'Salt',
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
    if (path === '/departments') {
      await route.fulfill({ json: [] })
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
          {
            id: 20,
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
          source_url_match:
            options.sourceUrlMatch === true ? { id: 6, name: 'Lasagna Bolognese' } : null,
          capabilities: {
            can_create_reference_data: options.canCreateReferenceData === true,
          },
          recipes,
        },
      })
      return
    }
    if (path === '/import/check' && method === 'POST') {
      await route.fulfill({
        json: {
          source_url_match:
            options.sourceUrlMatch === true ? { id: 6, name: 'Lasagna Bolognese' } : null,
          name_matches: options.checkNameMatches ?? [],
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

test('import: resolve an unknown ingredient line before saving', async ({ page }) => {
  const { requests } = await mockImportApi(page, { unknownIngredient: true })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  await expect(
    page.getByRole('heading', { level: 2, name: 'Review imported recipe' }),
  ).toBeVisible()
  // The unknown line is surfaced with its raw text and a suggestion.
  await expect(page.getByText('Needs an ingredient before this recipe can be saved.')).toBeVisible()
  await expect(page.getByText('a pinch of salt')).toBeVisible()

  // Saving is blocked while the line is unresolved.
  await page.getByRole('button', { name: 'Save recipe' }).click()
  await expect(
    page.getByRole('heading', { name: 'Fix these problems before saving' }),
  ).toBeVisible()
  expect(requests.some((entry) => entry.method === 'POST' && entry.path === '/recipes')).toBe(false)

  // Map the line to the existing suggested ingredient, then save.
  await page.getByRole('button', { name: /Use Salt for ingredient 2/i }).click()
  await expect(page.getByText('Needs an ingredient before this recipe can be saved.')).toHaveCount(
    0,
  )
  await page.getByRole('button', { name: 'Save recipe' }).click()

  await expect(page).toHaveURL('/recipes/1/edit')
  const createRequest = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/recipes',
  )
  expect(createRequest?.body).toMatchObject({
    ingredients: [{ ingredient_id: 12 }, { ingredient_id: 14 }],
  })
})

test('import: resolve an unknown unit before saving', async ({ page }) => {
  const { requests } = await mockImportApi(page, { unknownUnit: true })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  await expect(
    page.getByRole('heading', { level: 2, name: 'Review imported recipe' }),
  ).toBeVisible()
  // The unrecognized unit is surfaced with its raw text and a suggestion.
  await expect(page.getByText('Unrecognized unit:')).toBeVisible()
  await expect(page.getByText('pkg')).toBeVisible()
  await expect(page.getByText('looks like Mass')).toBeVisible()

  // Saving is blocked while the unit is unresolved.
  await page.getByRole('button', { name: 'Save recipe' }).click()
  await expect(
    page.getByRole('heading', { name: 'Fix these problems before saving' }),
  ).toBeVisible()
  expect(requests.some((entry) => entry.method === 'POST' && entry.path === '/recipes')).toBe(false)

  // Map the unit to the existing suggestion, then save.
  await page.getByRole('button', { name: /Use g for ingredient 1 unit/i }).click()
  await expect(
    page.getByText('Choose or create a unit before this recipe can be saved.'),
  ).toHaveCount(0)
  await page.getByRole('button', { name: 'Save recipe' }).click()

  await expect(page).toHaveURL('/recipes/1/edit')
  const createRequest = requests.find(
    (entry) => entry.method === 'POST' && entry.path === '/recipes',
  )
  expect(createRequest?.body).toMatchObject({
    ingredients: [{ ingredient_id: 12, unit_id: 20 }],
  })
})

test('import: warns on an exact source-URL duplicate and can import anyway', async ({ page }) => {
  const { requests } = await mockImportApi(page, { sourceUrlMatch: true })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  const dialog = page.getByRole('dialog', { name: 'This page has already been imported' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('link', { name: 'Open existing recipe' })).toHaveAttribute(
    'href',
    '/recipes/6/edit',
  )
  // While the warning is open the rest of the app is inert, so the review form
  // and its Save button cannot be reached before the user chooses.
  await expect(page.locator('#root')).toHaveAttribute('inert', '')

  await dialog.getByRole('button', { name: 'Import anyway' }).click()
  await expect(dialog).toHaveCount(0)
  await expect(page.locator('#root')).not.toHaveAttribute('inert', '')

  await page.getByRole('button', { name: 'Save recipe' }).click()
  await expect(page).toHaveURL('/recipes/1/edit')
  expect(requests.some((entry) => entry.method === 'POST' && entry.path === '/recipes')).toBe(true)
})

test('import: cancelling the duplicate warning discards the draft', async ({ page }) => {
  await mockImportApi(page, { sourceUrlMatch: true })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  const dialog = page.getByRole('dialog', { name: 'This page has already been imported' })
  await dialog.getByRole('button', { name: 'Cancel' }).click()

  await expect(page.getByRole('heading', { level: 2, name: 'Import from URL' })).toBeVisible()
  await expect(page.getByLabel('Recipe URL')).toHaveValue('')
})

test('import: the duplicate warning is usable at a mobile viewport', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 720 })
  await mockImportApi(page, { sourceUrlMatch: true })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  const dialog = page.getByRole('dialog', { name: 'This page has already been imported' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Import anyway' })).toBeVisible()

  const openExisting = dialog.getByRole('link', { name: 'Open existing recipe' })
  await expect(openExisting).toBeVisible()
  const box = await openExisting.boundingBox()
  expect(box).not.toBeNull()
  expect(box!.height).toBeGreaterThanOrEqual(44)
  expect(box!.x + box!.width).toBeLessThanOrEqual(375)
})

test('import: rechecks similar names after a rename', async ({ page }) => {
  await mockImportApi(page, {
    nameMatches: [{ id: 6, name: 'Lasagna Bolognese' }],
    checkNameMatches: [{ id: 9, name: 'Grandma Lasagna' }],
  })

  await page.goto('/recipes/import')
  await page.getByLabel('Recipe URL').fill('https://example.com/lasagna')
  await page.getByRole('button', { name: 'Import' }).click()

  await expect(page.getByRole('link', { name: 'Lasagna Bolognese' })).toBeVisible()

  await page.getByLabel('Name').fill('Totally Different Dish')

  await expect(page.getByRole('link', { name: 'Grandma Lasagna' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Lasagna Bolognese' })).toHaveCount(0)
})

test('import: an entry point on the recipe browser opens the import page', async ({ page }) => {
  await mockImportApi(page)

  await page.goto('/recipes')
  await page.getByRole('link', { name: 'Import from URL' }).click()
  await expect(page).toHaveURL('/recipes/import')
  await expect(page.getByRole('heading', { level: 2, name: 'Import from URL' })).toBeVisible()
})
