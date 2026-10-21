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
      audit_retention_days: 365,
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

const backupFilename = 'fatefridge-0.1.0-20261021T120000Z.ffbackup'

interface BackupRow {
  id: number
  filename: string
  kind: string
  status: string
  app_version: string
  schema_version: string
  created_at: string
  size_bytes: number
  checksum: string
  error: string | null
}

/** Hermetic Admin backups API: a mutable archive list and a 202 restore. */
async function mockBackupsApi(page: Page): Promise<void> {
  const state = {
    rows: [
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
    ] as BackupRow[],
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
      await route.fulfill({ json: adminSession })
      return
    }
    if (path.endsWith('/health')) {
      // The restore is still running: the page must keep waiting.
      await route.fulfill({
        status: 503,
        json: {
          error: { code: 'RESTORE_IN_PROGRESS', message: 'The restore is running.', details: {} },
        },
      })
      return
    }
    if (path.endsWith('/admin/backups/restore') && method === 'POST') {
      await route.fulfill({
        status: 202,
        json: {
          restore_id: '20261021T150000Z-1f2e3d4c5b6a7988',
          backup_id: 12,
          filename: backupFilename,
          safety_backup_filename: 'fatefridge-0.1.0-20261021T150500Z.ffbackup',
        },
      })
      return
    }
    if (path.endsWith('/download')) {
      await route.fulfill({
        status: 200,
        headers: {
          'Content-Type': 'application/octet-stream',
          'Content-Disposition': `attachment; filename="${backupFilename}"`,
        },
        body: 'encrypted-archive',
      })
      return
    }
    if (path.endsWith('/admin/backups') && method === 'GET') {
      await route.fulfill({ json: state.rows })
      return
    }
    if (path.endsWith('/admin/backups') && method === 'POST') {
      const created: BackupRow = {
        ...state.rows[0],
        id: state.rows.length + 12,
        filename: 'fatefridge-0.1.0-20261021T130000Z.ffbackup',
        created_at: '2026-10-21T13:00:00+00:00',
      }
      state.rows.unshift(created)
      await route.fulfill({ status: 201, json: created })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${method} ${path}`, details: {} } },
    })
  })
}

test('admin lists, creates, and downloads a backup', async ({ page }) => {
  await mockBackupsApi(page)
  await page.goto('/settings/backups')

  const nav = page.getByRole('navigation', { name: 'Settings sections' })
  await expect(nav.getByRole('link', { name: 'Backups' })).toBeVisible()

  const row = page.getByRole('row', { name: new RegExp(backupFilename) })
  await expect(row.getByText('Manual')).toBeVisible()
  await expect(row.getByText('Completed')).toBeVisible()
  await expect(row.getByText('1 MB')).toBeVisible()

  await page.getByRole('button', { name: 'Create backup' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Reason').fill('Before the upgrade')
  await dialog.getByRole('checkbox').check()
  await dialog.getByRole('button', { name: 'Create backup' }).click()

  await expect(page.getByText('Backup created:')).toBeVisible()
  await expect(
    page.getByRole('row', { name: /fatefridge-0.1.0-20261021T130000Z.ffbackup/ }),
  ).toBeVisible()

  const downloadPromise = page.waitForEvent('download')
  await page.getByRole('button', { name: `Download ${backupFilename}` }).click()
  const download = await downloadPromise
  expect(download.suggestedFilename()).toBe(backupFilename)
})

test('admin restore shows the restarting state after the 202', async ({ page }) => {
  await mockBackupsApi(page)
  await page.goto('/settings/backups')

  await page.getByRole('button', { name: `Restore ${backupFilename}` }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Reason').fill('Rolling back the upgrade')
  await dialog.getByRole('checkbox').check()
  await dialog.getByLabel('Type RESTORE to confirm').fill('RESTORE')
  await dialog.getByRole('button', { name: 'Restore backup' }).click()

  const status = page.getByRole('status')
  await expect(status).toContainText(/restart/i)
  await expect(status).toContainText('20261021T150000Z-1f2e3d4c5b6a7988')
  await expect(page.getByRole('dialog')).toHaveCount(0)
})
