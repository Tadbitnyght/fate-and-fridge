import { expect, test, type Page } from '@playwright/test'

const setupIncomplete = { setup_completed: false, auth_mode: null, recovery_key_present: false }
const setupComplete = { setup_completed: true, auth_mode: 'multiuser', recovery_key_present: true }

const multiuserSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'smith', role: 'admin' },
  permissions: ['calendar.view', 'calendar.edit'],
  must_change_password: false,
}

interface MockApiOptions {
  setupCompleted: boolean
  /** Omit for an unauthenticated (401) session. */
  session?: unknown
}

async function mockApi(page: Page, options: MockApiOptions): Promise<void> {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/setup/status')) {
      await route.fulfill({ json: options.setupCompleted ? setupComplete : setupIncomplete })
      return
    }
    if (path.endsWith('/auth/session')) {
      if (options.session) {
        await route.fulfill({ json: options.session })
      } else {
        await route.fulfill({
          status: 401,
          json: {
            error: { code: 'AUTH_REQUIRED', message: 'A valid session is required.', details: {} },
          },
        })
      }
      return
    }
    await route.fulfill({
      status: 404,
      json: { error: { code: 'NOT_FOUND', message: `No mock for ${path}`, details: {} } },
    })
  })
}

test('routes an unconfigured household to the setup placeholder', async ({ page }) => {
  await mockApi(page, { setupCompleted: false })

  await page.goto('/')

  await expect(page).toHaveURL(/\/setup$/)
  await expect(page.getByRole('heading', { level: 1, name: 'Set up Fate & Fridge' })).toBeVisible()
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

  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Primary' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Settings' })).toBeVisible()
})
