import { expect, test, type Page } from '@playwright/test'

const setupIncomplete = { setup_completed: false, auth_mode: null, recovery_key_present: false }

const calendarSettings = {
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

const dashboardSettings = { ...calendarSettings, startup_page: 'dashboard' }

const singleUserSession = {
  authenticated: true,
  auth_mode: 'single_user',
  user: null,
  permissions: ['calendar.view', 'calendar.edit'],
  must_change_password: false,
}

const multiuserSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'smith', role: 'admin' },
  permissions: ['calendar.view', 'calendar.edit'],
  must_change_password: false,
}

interface CompleteResponse {
  status?: number
  body: unknown
}

interface MockApiOptions {
  setupCompleted: boolean
  /** Session payload for an already-configured household. Omit for 401. */
  session?: unknown
  /** Response for `POST /setup/complete`; defaults to single-user success. */
  complete?: CompleteResponse
  /** Household settings used for the startup-page redirect (default: calendar). */
  household?: unknown
  /**
   * Response for `POST /setup/restore`. When set, the mocked app simulates the
   * restart: the first status poll fails, the second is the maintenance 503,
   * and later polls report a completed single-user setup.
   */
  restore?: CompleteResponse
}

async function mockApi(page: Page, options: MockApiOptions): Promise<void> {
  let completed = options.setupCompleted
  let authMode: string | null = options.setupCompleted ? 'multiuser' : null
  let sessionPayload: unknown = options.session
  let restoring = false
  let restoreStatusPolls = 0

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname

    if (path.endsWith('/setup/status')) {
      if (restoring) {
        restoreStatusPolls += 1
        if (restoreStatusPolls === 1) {
          // The container has gone away to restart.
          await route.abort('failed')
          return
        }
        if (restoreStatusPolls === 2) {
          await route.fulfill({
            status: 503,
            json: {
              error: {
                code: 'RESTORE_IN_PROGRESS',
                message: 'A restore is in progress.',
                details: {},
              },
            },
          })
          return
        }
        completed = true
        authMode = 'single_user'
      }
      await route.fulfill({
        json: completed
          ? { setup_completed: true, auth_mode: authMode, recovery_key_present: true }
          : setupIncomplete,
      })
      return
    }

    if (path.endsWith('/setup/restore') && request.method() === 'POST' && options.restore) {
      restoring = true
      await route.fulfill({
        status: options.restore.status ?? 202,
        json: options.restore.body,
      })
      return
    }

    if (path.endsWith('/setup/complete') && request.method() === 'POST') {
      const response = options.complete ?? {
        status: 201,
        body: { auth_mode: 'single_user', recovery_key: 'E2E-RECOVERY-KEY' },
      }
      const status = response.status ?? 201
      completed = status === 201 || status === 409
      const body = response.body as { auth_mode?: string; session?: unknown }
      authMode = body.auth_mode ?? authMode
      if (body.session) {
        sessionPayload = body.session
      }
      await route.fulfill({ status, json: response.body })
      return
    }

    if (path.endsWith('/auth/session')) {
      if (!completed) {
        await route.fulfill({
          status: 409,
          json: {
            error: { code: 'SETUP_REQUIRED', message: 'Setup is not complete.', details: {} },
          },
        })
        return
      }
      if (sessionPayload) {
        await route.fulfill({ json: sessionPayload })
        return
      }
      if (authMode === 'single_user') {
        await route.fulfill({ json: singleUserSession })
        return
      }
      await route.fulfill({
        status: 401,
        json: {
          error: { code: 'AUTH_REQUIRED', message: 'A valid session is required.', details: {} },
        },
      })
      return
    }

    if (path.endsWith('/settings/household')) {
      await route.fulfill({ json: options.household ?? calendarSettings })
      return
    }
    if (path.endsWith('/health/db')) {
      await route.fulfill({ json: { status: 'ok', database: 'connected' } })
      return
    }
    if (path.endsWith('/health')) {
      await route.fulfill({
        json: { status: 'ok', app: 'Fate & Fridge', version: '0.1.0', environment: 'test' },
      })
      return
    }

    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${path}`, details: {} } },
    })
  })
}

test('shows the setup wizard for an unconfigured household', async ({ page }) => {
  await mockApi(page, { setupCompleted: false })

  await page.goto('/')

  await expect(page).toHaveURL(/\/setup$/)
  await expect(
    page.getByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
  ).toBeVisible()
  await expect(page.getByLabel(/Multiuser/)).toBeVisible()
})

test('routes unauthenticated multiuser visitors to the login page', async ({ page }) => {
  await mockApi(page, { setupCompleted: true })

  await page.goto('/')

  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible()
  await expect(page.getByLabel('Username')).toBeVisible()
  await expect(page.getByLabel('Password')).toBeVisible()
})

test('renders the app shell for an authenticated session', async ({ page }) => {
  await mockApi(page, { setupCompleted: true, session: multiuserSession })

  await page.goto('/')

  // The shell index redirects to the household's startup page.
  await expect(page).toHaveURL(/\/calendar$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Primary' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Settings' })).toBeVisible()
})

test('honors the household startup page', async ({ page }) => {
  await mockApi(page, {
    setupCompleted: true,
    session: multiuserSession,
    household: dashboardSettings,
  })

  await page.goto('/')

  await expect(page).toHaveURL(/\/dashboard$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible()
})

test('dashboard offers product quick links and no roadmap', async ({ page }) => {
  await mockApi(page, { setupCompleted: true, session: multiuserSession })

  await page.goto('/dashboard')

  await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Open the calendar' })).toHaveAttribute(
    'href',
    '/calendar',
  )
  await expect(page.getByRole('link', { name: 'Browse recipes' })).toHaveAttribute(
    'href',
    '/recipes',
  )
  await expect(page.getByRole('link', { name: 'View the grocery list' })).toHaveAttribute(
    'href',
    '/groceries',
  )
  await expect(page.getByRole('link', { name: 'See the statistics' })).toHaveAttribute(
    'href',
    '/statistics',
  )

  // The in-app roadmap is gone: no link, card, or footer entry points at it.
  await expect(page.getByRole('link', { name: /milestone|roadmap/i })).toHaveCount(0)
  await expect(page.getByText(/milestone|roadmap/i)).toHaveCount(0)
})

test('the retired /progress route is not a product route', async ({ page }) => {
  await mockApi(page, { setupCompleted: true, session: multiuserSession })

  await page.goto('/progress')

  // Unknown routes fall back to the household's startup page, so the roadmap
  // content is unreachable even though the path is bookmarked.
  await expect(page).toHaveURL(/\/calendar$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
  await expect(page.getByRole('heading', { level: 2, name: 'Progress' })).toHaveCount(0)
  await expect(page.getByText(/milestone|roadmap|future & ideas/i)).toHaveCount(0)
})

test('skip link moves keyboard focus to the main content', async ({ page }) => {
  await mockApi(page, { setupCompleted: true, session: multiuserSession })

  await page.goto('/')
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()

  // The skip link is the first focusable element (asserted in the DOM order by
  // the Vitest suite); here the keyboard flow is exercised explicitly, since
  // Chromium's sequential-focus starting point follows the route-focus heading.
  const skipLink = page.getByRole('link', { name: 'Skip to main content' })
  await skipLink.focus()
  await expect(skipLink).toBeFocused()

  await page.keyboard.press('Enter')
  await expect(page.locator('#main-content')).toBeFocused()
})

test('completes single-user setup and gates the recovery key', async ({ page }) => {
  await mockApi(page, {
    setupCompleted: false,
    complete: {
      status: 201,
      body: { auth_mode: 'single_user', recovery_key: 'E2E-RECOVERY-KEY' },
    },
  })

  await page.goto('/')
  await expect(page).toHaveURL(/\/setup$/)

  await page.getByLabel(/Single user/).check()
  await expect(page.getByText(/trusted private network/i)).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()

  await page.getByLabel('Household name').fill('The Smiths')
  await page.getByLabel('Timezone').fill('America/Chicago')
  await page.getByRole('button', { name: 'Next' }).click()

  // Single-user mode has no admin step.
  await expect(page.getByRole('heading', { level: 1, name: 'Network access' })).toBeVisible()
  await page.getByRole('button', { name: 'Next' }).click()

  await expect(page.getByRole('heading', { level: 1, name: 'Review and finish' })).toBeVisible()
  await page.getByRole('button', { name: 'Finish setup' }).click()

  await expect(page.getByRole('textbox', { name: 'Recovery key' })).toHaveValue('E2E-RECOVERY-KEY')
  const continueButton = page.getByRole('button', { name: /Continue to Fate & Fridge/ })
  await expect(continueButton).toBeDisabled()
  await page.getByLabel(/I have saved this recovery key/).check()
  await expect(continueButton).toBeEnabled()
  await continueButton.click()

  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
})

test('completes multiuser setup with an admin account', async ({ page }) => {
  await mockApi(page, {
    setupCompleted: false,
    complete: {
      status: 201,
      body: {
        auth_mode: 'multiuser',
        recovery_key: 'E2E-RECOVERY-KEY',
        session: multiuserSession,
      },
    },
  })

  await page.goto('/setup')

  await page.getByLabel(/Multiuser/).check()
  await page.getByRole('button', { name: 'Next' }).click()

  await page.getByLabel('Household name').fill('The Smiths')
  await page.getByLabel('Timezone').fill('America/Chicago')
  await page.getByRole('button', { name: 'Next' }).click()

  await expect(page.getByRole('heading', { level: 1, name: 'Admin account' })).toBeVisible()
  await page.getByLabel('Admin username').fill('smith')
  await page.getByLabel('Admin password', { exact: true }).fill('correct horse battery staple')
  await page.getByLabel('Confirm admin password').fill('correct horse battery staple')
  await page.getByRole('button', { name: 'Next' }).click()

  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByRole('button', { name: 'Finish setup' }).click()

  await expect(page.getByRole('textbox', { name: 'Recovery key' })).toHaveValue('E2E-RECOVERY-KEY')
  await page.getByLabel(/I have saved this recovery key/).check()
  await page.getByRole('button', { name: /Continue to Fate & Fridge/ }).click()

  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
  await expect(page.getByText('smith')).toBeVisible()
})

test('gates restore-from-backup and enters the app once the server returns', async ({ page }) => {
  await mockApi(page, {
    setupCompleted: false,
    restore: {
      status: 202,
      body: {
        restore_id: '20261021T150000Z-1f2e3d4c5b6a7988',
        filename: 'household.ffbackup',
        safety_backup_filename: 'fatefridge-0.1.0-20261021T150000Z.ffbackup',
      },
    },
  })

  await page.goto('/setup/restore')
  await expect(page.getByRole('heading', { level: 1, name: 'Restore from backup' })).toBeVisible()

  const submit = page.getByRole('button', { name: 'Restore backup' })
  await expect(submit).toBeDisabled()

  await page.getByLabel('Backup archive').setInputFiles({
    name: 'household.ffbackup',
    mimeType: 'application/octet-stream',
    buffer: Buffer.from('mock archive bytes'),
  })
  await expect(submit).toBeDisabled()

  await page.getByLabel('Recovery key').fill('pasted-recovery-key')
  await expect(submit).toBeDisabled()

  await page.getByLabel('Type RESTORE to confirm').fill('RESTORE')
  await expect(submit).toBeEnabled()
  await submit.click()

  // The restore accepted the request; success must not be claimed until the
  // restarted app answers with a completed setup.
  await expect(page.getByRole('status')).toContainText(/restart/i)
  await expect(page).toHaveURL(/\/calendar$/, { timeout: 20000 })
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
})
