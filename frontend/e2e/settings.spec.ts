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

const memberSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 2, username: 'sam', role: 'member' },
  permissions: ['calendar.view', 'groceries.view', 'recipes.view'],
  must_change_password: false,
}

interface UserRow {
  id: number
  username: string
  role: string
  is_active: boolean
  must_change_password: boolean
  last_login_at: string | null
  permissions: string[]
  temporary_password?: string
}

const viewOnly = ['calendar.view', 'groceries.view', 'recipes.view']

/** Hermetic settings API: a mutable user list, security row, and session. */
async function mockSettingsApi(page: Page, session: unknown): Promise<void> {
  const state = {
    users: [
      {
        id: 1,
        username: 'root',
        role: 'admin',
        is_active: true,
        must_change_password: false,
        last_login_at: '2026-09-29T12:00:00Z',
        permissions: [...adminSession.permissions],
      },
    ] as UserRow[],
    security: {
      ip_allowlist_enabled: false,
      ip_allowlist_ranges: [] as string[],
      trusted_proxies: [] as string[],
    },
  }

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    const method = request.method()

    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: setupComplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      await route.fulfill({ json: session })
      return
    }
    if (path.endsWith('/settings/household')) {
      await route.fulfill({
        json: {
          household_name: 'The Smiths',
          timezone: 'America/Chicago',
          week_starts_on: 0,
          default_diner_count: 4,
          startup_page: 'calendar',
        },
      })
      return
    }
    if (path.endsWith('/settings/security')) {
      if (method === 'PATCH') {
        const body = JSON.parse(request.postData() ?? '{}') as Partial<typeof state.security>
        state.security = { ...state.security, ...body }
      }
      await route.fulfill({ json: state.security })
      return
    }
    if (path.endsWith('/users') && method === 'GET') {
      await route.fulfill({ json: state.users })
      return
    }
    if (path.endsWith('/users') && method === 'POST') {
      const body = JSON.parse(request.postData() ?? '{}') as { username: string; role?: string }
      const created: UserRow = {
        id: state.users.length + 1,
        username: body.username,
        role: body.role ?? 'member',
        is_active: true,
        must_change_password: true,
        last_login_at: null,
        permissions: [...viewOnly],
        temporary_password: 'Kd7mQ2xRf9Tn4Wpz8Hs6',
      }
      state.users.push(created)
      await route.fulfill({ status: 201, json: created })
      return
    }
    const permissionsMatch = path.match(/\/users\/(\d+)\/permissions$/)
    if (permissionsMatch && method === 'PATCH') {
      const id = Number(permissionsMatch[1])
      const body = JSON.parse(request.postData() ?? '{}') as { permissions: string[] }
      const user = state.users.find((candidate) => candidate.id === id)
      if (user) {
        user.permissions = body.permissions
      }
      await route.fulfill({ json: user ?? {} })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${method} ${path}`, details: {} } },
    })
  })
}

test('admin creates a member with view-only defaults and a one-time password', async ({ page }) => {
  await mockSettingsApi(page, adminSession)

  await page.goto('/settings/users')

  await page.getByRole('button', { name: 'Add user' }).click()
  await page.getByLabel('Username').fill('casey')
  await page.getByRole('button', { name: 'Create user' }).click()

  await expect(page.getByText('Temporary password for casey')).toBeVisible()
  await expect(page.getByRole('textbox', { name: 'Temporary password' })).toHaveValue(
    'Kd7mQ2xRf9Tn4Wpz8Hs6',
  )
  await page.getByRole('button', { name: 'Done' }).click()

  await expect(page.getByText('Temporary password for casey')).toHaveCount(0)
  const row = page.getByRole('row', { name: /casey/ })
  await expect(row).toBeVisible()
  await expect(row.getByText('View only (3)')).toBeVisible()
})

test('admin edits a member to full permissions', async ({ page }) => {
  await mockSettingsApi(page, adminSession)

  await page.goto('/settings/users')
  await page.getByRole('button', { name: 'Add user' }).click()
  await page.getByLabel('Username').fill('casey')
  await page.getByRole('button', { name: 'Create user' }).click()
  await page.getByRole('button', { name: 'Done' }).click()

  await page.getByRole('button', { name: 'Permissions for casey' }).click()
  await page.getByLabel('Full member').check()
  await page.getByRole('button', { name: 'Save permissions' }).click()

  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('row', { name: /casey/ }).getByText('Full member (7)')).toBeVisible()
})

test('members see Account-only settings and no Admin sections', async ({ page }) => {
  await mockSettingsApi(page, memberSession)

  await page.goto('/settings/users')

  const nav = page.getByRole('navigation', { name: 'Settings sections' })
  await expect(nav.getByRole('link', { name: 'Household' })).toBeVisible()
  await expect(nav.getByRole('link', { name: 'Account' })).toBeVisible()
  await expect(nav.getByRole('link', { name: 'Users' })).toHaveCount(0)
  await expect(nav.getByRole('link', { name: 'Security' })).toHaveCount(0)

  await page.getByRole('link', { name: 'Account' }).click()
  await expect(page.getByRole('heading', { name: 'Account' })).toBeVisible()
  await expect(page.locator('main').getByText('sam')).toBeVisible()
})

test('admin saves the IP allowlist with the lockout confirmation', async ({ page }) => {
  await mockSettingsApi(page, adminSession)

  await page.goto('/settings/security')

  await page.getByLabel(/Enable the IP allowlist/).check()
  await page.getByLabel('Allowed addresses and ranges').fill('192.168.1.0/24')
  await page.getByLabel(/I understand that a misconfigured allowlist/).check()
  await page.getByRole('button', { name: 'Save security settings' }).click()

  await expect(page.getByText('Security settings saved.')).toBeVisible()
})
