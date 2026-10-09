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
}

async function mockApi(page: Page, options: MockApiOptions): Promise<void> {
  let completed = options.setupCompleted
  let authMode: string | null = options.setupCompleted ? 'multiuser' : null
  let sessionPayload: unknown = options.session

  await page.route('**/api/v1/**', async (route) => {
    const request = route.request()
    const path = new URL(request.url()).pathname

    if (path.endsWith('/setup/status')) {
      await route.fulfill({
        json: completed
          ? { setup_completed: true, auth_mode: authMode, recovery_key_present: true }
          : setupIncomplete,
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

test('progress roadmap is reachable from the dashboard card and the footer', async ({ page }) => {
  await mockApi(page, { setupCompleted: true, session: multiuserSession })

  await page.goto('/')
  await expect(page).toHaveURL(/\/calendar$/)

  await page.getByRole('link', { name: 'Dashboard' }).click()
  await expect(page).toHaveURL(/\/dashboard$/)

  await page.getByRole('link', { name: 'Open the milestone roadmap' }).click()
  await expect(page).toHaveURL(/\/progress$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Progress' })).toBeVisible()
  await expect(page.getByText('8 of 15 milestones complete (53%).')).toBeVisible()
  await expect(
    page.getByRole('heading', { level: 3, name: 'M1 — Setup and Authentication' }),
  ).toBeVisible()
  await expect(page.getByRole('heading', { level: 3, name: 'M14 — Hardening' })).toBeVisible()

  // The footer link points at the same roadmap.
  await expect(page.getByRole('link', { name: 'Milestone progress' })).toBeVisible()
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
