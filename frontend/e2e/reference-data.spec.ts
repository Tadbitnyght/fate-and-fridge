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

interface DepartmentRow {
  id: number
  name: string
  sort_order: number
  is_active: boolean
  created_at: string
  updated_at: string
}

interface UnitRow {
  id: number
  symbol: string
  display_name: string
  dimension: string
  base_factor: number | null
  is_custom: boolean
  is_active: boolean
  aliases: Array<{ id: number; alias: string }>
}

interface IngredientRow {
  id: number
  canonical_name: string
  department_id: number | null
  is_active: boolean
  created_at: string
  updated_at: string
  aliases: Array<{ id: number; alias: string }>
}

interface ProteinRow {
  id: number
  name: string
  is_active: boolean
}

interface TagRow {
  id: number
  name: string
}

interface RecordedRequest {
  method: string
  path: string
  body?: Record<string, unknown>
}

/**
 * Hermetic reference-data API: a mutable in-memory vocabulary plus request
 * recording, so payload assertions do not need a real backend.
 */
async function mockReferenceDataApi(page: Page): Promise<{ requests: RecordedRequest[] }> {
  const now = '2026-10-01T10:00:00Z'
  const state = {
    departments: [
      { id: 1, name: 'Produce', sort_order: 1, is_active: true, created_at: now, updated_at: now },
      { id: 2, name: 'Pantry', sort_order: 2, is_active: true, created_at: now, updated_at: now },
    ] as DepartmentRow[],
    units: [
      {
        id: 1,
        symbol: 'g',
        display_name: 'gram',
        dimension: 'mass',
        base_factor: 1,
        is_custom: false,
        is_active: true,
        aliases: [],
      },
      {
        id: 2,
        symbol: 'cup',
        display_name: 'cup',
        dimension: 'volume',
        base_factor: 240,
        is_custom: false,
        is_active: true,
        aliases: [],
      },
    ] as UnitRow[],
    ingredients: [
      {
        id: 1,
        canonical_name: 'Tomato',
        department_id: 1,
        is_active: true,
        created_at: now,
        updated_at: now,
        aliases: [],
      },
      {
        id: 2,
        canonical_name: 'Tomato (canned)',
        department_id: 2,
        is_active: true,
        created_at: now,
        updated_at: now,
        aliases: [],
      },
    ] as IngredientRow[],
    proteins: [{ id: 1, name: 'Chicken', is_active: true }] as ProteinRow[],
    tags: [{ id: 1, name: 'Weeknight' }] as TagRow[],
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

    if (path === '/departments' && method === 'GET') {
      await route.fulfill({ json: state.departments })
      return
    }
    if (path === '/departments' && method === 'POST') {
      const created: DepartmentRow = {
        id: state.departments.length + 1,
        name: String(body?.['name'] ?? ''),
        sort_order: state.departments.length + 1,
        is_active: true,
        created_at: now,
        updated_at: now,
      }
      state.departments.push(created)
      await route.fulfill({ status: 201, json: created })
      return
    }
    if (path === '/departments/order' && method === 'PUT') {
      const ids = (body?.['ids'] ?? []) as number[]
      state.departments = ids.map((id, index) => {
        const row = state.departments.find((candidate) => candidate.id === id)
        return { ...(row as DepartmentRow), sort_order: index + 1 }
      })
      await route.fulfill({ json: state.departments })
      return
    }

    if (path === '/units' && method === 'GET') {
      await route.fulfill({ json: state.units })
      return
    }
    const unitMatch = path.match(/^\/units\/(\d+)$/)
    if (unitMatch && method === 'PATCH') {
      const unit = state.units.find((row) => row.id === Number(unitMatch[1]))
      if (unit) {
        Object.assign(unit, body)
      }
      await route.fulfill({ json: unit })
      return
    }

    if (path === '/ingredients' && method === 'GET') {
      await route.fulfill({ json: state.ingredients })
      return
    }
    if (path === '/ingredients/merge' && method === 'POST') {
      const sourceId = Number(body?.['source_id'])
      const targetId = Number(body?.['target_id'])
      const source = state.ingredients.find((row) => row.id === sourceId)
      if (source) {
        source.is_active = false
      }
      await route.fulfill({
        json: {
          source_id: sourceId,
          target_id: targetId,
          skipped_aliases: [],
          reassigned_reference_count: 1,
        },
      })
      return
    }

    if (path === '/proteins' && method === 'GET') {
      await route.fulfill({ json: state.proteins })
      return
    }
    if (path === '/proteins' && method === 'POST') {
      const name = String(body?.['name'] ?? '')
      const existing = state.proteins.find((row) => row.name.toLowerCase() === name.toLowerCase())
      if (existing) {
        await route.fulfill({
          status: 409,
          json: {
            error: {
              code: 'PROTEIN_EXISTS',
              message: 'A protein with this name already exists.',
              details: { existing_id: existing.id },
            },
          },
        })
        return
      }
      const created: ProteinRow = { id: state.proteins.length + 1, name, is_active: true }
      state.proteins.push(created)
      await route.fulfill({ status: 201, json: created })
      return
    }
    const proteinRetireMatch = path.match(/^\/proteins\/(\d+)\/retire$/)
    if (proteinRetireMatch && method === 'POST') {
      const protein = state.proteins.find((row) => row.id === Number(proteinRetireMatch[1]))
      if (protein) {
        protein.is_active = false
      }
      await route.fulfill({ json: protein })
      return
    }
    const proteinMatch = path.match(/^\/proteins\/(\d+)$/)
    if (proteinMatch && method === 'PATCH') {
      const protein = state.proteins.find((row) => row.id === Number(proteinMatch[1]))
      if (protein) {
        Object.assign(protein, body)
      }
      await route.fulfill({ json: protein })
      return
    }

    if (path === '/tags' && method === 'GET') {
      await route.fulfill({ json: state.tags })
      return
    }
    if (path === '/tags' && method === 'POST') {
      const name = String(body?.['name'] ?? '')
      const existing = state.tags.find((row) => row.name.toLowerCase() === name.toLowerCase())
      if (existing) {
        await route.fulfill({
          status: 409,
          json: {
            error: {
              code: 'TAG_EXISTS',
              message: 'A tag with this name already exists.',
              details: { existing_id: existing.id },
            },
          },
        })
        return
      }
      const created: TagRow = { id: state.tags.length + 1, name }
      state.tags.push(created)
      await route.fulfill({ status: 201, json: created })
      return
    }
    const tagMatch = path.match(/^\/tags\/(\d+)$/)
    if (tagMatch && method === 'DELETE') {
      await route.fulfill({
        status: 409,
        json: {
          error: {
            code: 'TAG_IN_USE',
            message: 'The tag is still referenced by 2 recipe(s).',
            details: { usage_count: 2 },
          },
        },
      })
      return
    }
    if (tagMatch && method === 'PATCH') {
      const tag = state.tags.find((row) => row.id === Number(tagMatch[1]))
      if (tag) {
        Object.assign(tag, body)
      }
      await route.fulfill({ json: tag })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${method} ${path}`, details: {} } },
    })
  })

  return { requests }
}

test('navigates Settings to Reference data and tracks aria-current across tabs', async ({
  page,
}) => {
  await mockReferenceDataApi(page)

  await page.goto('/settings')
  await page.getByRole('link', { name: 'Reference data' }).click()

  const tabs = page.getByRole('navigation', { name: 'Reference data sections' })
  for (const label of ['Departments', 'Units', 'Ingredients', 'Proteins', 'Tags']) {
    await expect(tabs.getByRole('link', { name: label })).toBeVisible()
  }
  await expect(tabs.getByRole('link', { name: 'Departments' })).toHaveAttribute(
    'aria-current',
    'page',
  )

  await tabs.getByRole('link', { name: 'Proteins' }).click()
  await expect(page).toHaveURL(/\/settings\/reference-data\/proteins$/)
  await expect(tabs.getByRole('link', { name: 'Proteins' })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('heading', { name: 'Proteins' })).toBeVisible()
})

test('departments: create and reorder smoke', async ({ page }) => {
  const { requests } = await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data')

  await page.getByRole('button', { name: 'Add department' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add department' })
  await dialog.getByLabel('Name').fill('Bakery')
  await dialog.getByRole('button', { name: 'Add department' }).click()
  await expect(dialog).toHaveCount(0)

  const table = page.getByRole('table', { name: 'Grocery departments' })
  await expect(table.getByRole('rowheader', { name: 'Bakery' })).toBeVisible()

  await page.getByRole('button', { name: 'Move up Bakery' }).click()
  await expect(table.getByRole('rowheader').nth(1)).toHaveText('Bakery')

  expect(requests).toContainEqual({
    method: 'POST',
    path: '/departments',
    body: { name: 'Bakery' },
  })
  const reorder = requests.find((entry) => entry.path === '/departments/order')
  expect(reorder?.method).toBe('PUT')
  expect((reorder?.body as { ids: number[] }).ids).toEqual([1, 3, 2])
})

test('units: edit a base factor', async ({ page }) => {
  const { requests } = await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data/units')

  await page.getByRole('button', { name: 'Edit factor for cup' }).click()
  const dialog = page.getByRole('dialog', { name: 'Edit base factor for cup' })
  await dialog.getByLabel('Base factor').fill('236.588')
  await dialog.getByRole('button', { name: 'Save factor' }).click()
  await expect(dialog).toHaveCount(0)

  const row = page.getByRole('row', { name: /cup/ })
  await expect(row.getByText('236.588')).toBeVisible()
  expect(requests).toContainEqual({
    method: 'PATCH',
    path: '/units/2',
    body: { base_factor: 236.588 },
  })
})

test('ingredients: merge one ingredient into another', async ({ page }) => {
  const { requests } = await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data/ingredients')

  await page.getByRole('button', { name: 'Merge ingredients' }).click()
  const dialog = page.getByRole('dialog', { name: 'Merge ingredients' })
  await dialog.getByLabel('Source', { exact: true }).selectOption({ label: 'Tomato (canned)' })
  await dialog.getByLabel('Target', { exact: true }).selectOption({ label: 'Tomato' })
  await dialog.getByRole('button', { name: 'Merge' }).click()

  await expect(dialog.getByText('Merged Tomato (canned) into Tomato.')).toBeVisible()
  await expect(dialog.getByText('1 reference reassigned.')).toBeVisible()
  await dialog.getByRole('button', { name: 'Done' }).click()

  expect(requests).toContainEqual({
    method: 'POST',
    path: '/ingredients/merge',
    body: { source_id: 2, target_id: 1 },
  })
})

test('proteins: create, retire, and re-enable', async ({ page }) => {
  const { requests } = await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data/proteins')

  await page.getByRole('button', { name: 'Add protein' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add protein' })
  await dialog.getByLabel('Name').fill('Tofu')
  await dialog.getByRole('button', { name: 'Add protein' }).click()
  await expect(dialog).toHaveCount(0)

  const table = page.getByRole('table', { name: 'Proteins' })
  await expect(table.getByRole('row', { name: /Tofu/ }).getByText('Active')).toBeVisible()

  await page.getByRole('button', { name: 'Retire Tofu' }).click()
  await expect(table.getByRole('rowheader', { name: 'Tofu' })).toHaveCount(0)

  await page.getByLabel('Include retired').check()
  const retiredRow = table.getByRole('row', { name: /Tofu/ })
  await expect(retiredRow.getByText('Retired')).toBeVisible()

  await page.getByRole('button', { name: 'Enable Tofu' }).click()
  await expect(table.getByRole('row', { name: /Tofu/ }).getByText('Active')).toBeVisible()

  expect(requests).toContainEqual({ method: 'POST', path: '/proteins', body: { name: 'Tofu' } })
  expect(requests).toContainEqual({ method: 'POST', path: '/proteins/2/retire' })
  expect(requests).toContainEqual({
    method: 'PATCH',
    path: '/proteins/2',
    body: { is_active: true },
  })
})

test('tags: create, then the TAG_IN_USE delete error keeps the dialog open', async ({ page }) => {
  const { requests } = await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data/tags')

  await page.getByRole('button', { name: 'Add tag' }).click()
  const createDialog = page.getByRole('dialog', { name: 'Add tag' })
  await createDialog.getByLabel('Name').fill('Holiday')
  await createDialog.getByRole('button', { name: 'Add tag' }).click()
  await expect(createDialog).toHaveCount(0)
  await expect(page.getByRole('rowheader', { name: 'Holiday' })).toBeVisible()

  await page.getByRole('button', { name: 'Delete Holiday' }).click()
  const confirm = page.getByRole('dialog', { name: 'Delete Holiday?' })
  await confirm.getByRole('button', { name: 'Delete tag' }).click()
  await expect(confirm.getByRole('alert')).toHaveText(
    '2 recipes use this tag. Remove the tag from those recipes first, then delete it.',
  )
  await expect(confirm).toBeVisible()

  expect(requests).toContainEqual({ method: 'DELETE', path: '/tags/2' })
})

test('proteins dialog: labelled field, focus trap, Escape, and text status', async ({ page }) => {
  await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data/proteins')

  const table = page.getByRole('table', { name: 'Proteins' })
  await expect(table.getByText('Active')).toBeVisible()
  await expect(page.getByRole('columnheader', { name: 'Status' })).toBeVisible()

  await page.getByRole('button', { name: 'Add protein' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add protein' })
  await expect(dialog).toBeFocused()
  await expect(dialog.getByLabel('Name')).toBeVisible()

  for (let index = 0; index < 6; index += 1) {
    await page.keyboard.press('Tab')
    expect(await dialog.evaluate((element) => element.matches(':focus-within'))).toBe(true)
  }

  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
})

test('keyboard-only: create a tag without a pointer', async ({ page }) => {
  const { requests } = await mockReferenceDataApi(page)

  await page.goto('/settings/reference-data/tags')

  await page.getByRole('button', { name: 'Add tag' }).focus()
  await page.keyboard.press('Enter')

  const dialog = page.getByRole('dialog', { name: 'Add tag' })
  await expect(dialog).toBeVisible()
  await expect(dialog).toBeFocused()

  await dialog.getByLabel('Name').focus()
  await page.keyboard.type('Keyboard')
  await dialog.getByRole('button', { name: 'Add tag' }).focus()
  await page.keyboard.press('Enter')

  await expect(dialog).toHaveCount(0)
  await expect(page.getByRole('rowheader', { name: 'Keyboard' })).toBeVisible()
  expect(requests).toContainEqual({ method: 'POST', path: '/tags', body: { name: 'Keyboard' } })
})
