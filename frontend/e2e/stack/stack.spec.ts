import { expect, test, type Page } from '@playwright/test'

/**
 * Real-stack end-to-end flow (M1-15 part C).
 *
 * Runs against a live, disposable stack — never against mocks. The suite is
 * skipped unless E2E_BASE_URL points at that stack, so the hermetic default
 * `npx playwright test` run is unaffected.
 *
 * Invocation:
 *   E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser npx playwright test e2e/stack
 *   E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=single_user npx playwright test e2e/stack
 *
 * The stack must be fresh/unconfigured. The multiuser flow consumes it (setup
 * is single-shot); a reset is required before a second run.
 */

const baseURL = process.env.E2E_BASE_URL
const setupMode = process.env.E2E_SETUP_MODE ?? 'multiuser'

test.skip(!baseURL, 'Set E2E_BASE_URL to run the real-stack suite (disposable stack required)')

// Generated per run and held in memory only; never committed or logged.
const adminUsername = 'e2e-admin'
const adminPassword = `E2e-admin-${crypto.randomUUID().slice(0, 12)}`
const memberUsername = 'e2e-member'

const RECOVERY_KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/ // 32 bytes, base64url, unpadded

async function csrfToken(page: Page): Promise<string> {
  const response = await page.context().request.get(`${baseURL}/api/v1/auth/session`)
  const token = response.headers()['x-csrf-token']
  expect(token, 'GET /auth/session must mint an X-CSRF-TOKEN header').toBeTruthy()
  return token
}

test.describe('multiuser household', () => {
  test.skip(setupMode !== 'multiuser', 'E2E_SETUP_MODE is not multiuser')

  test('setup, admin, member, permissions, and startup page', async ({ page }) => {
    test.setTimeout(240_000)

    // --- Fresh install routes to the setup wizard -------------------------
    await page.goto(`${baseURL}/`)
    await expect(page).toHaveURL(/\/setup$/)
    await expect(
      page.getByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
    ).toBeVisible()

    // --- Mode + household -------------------------------------------------
    await page.getByLabel(/Multiuser/).check()
    await page.getByRole('button', { name: 'Next' }).click()
    await page.getByLabel('Household name').fill('E2E Household')
    await page.getByLabel('Timezone').fill('America/Chicago')
    await page.getByRole('button', { name: 'Next' }).click()

    // --- Admin credentials (multiuser only) -------------------------------
    await expect(page.getByRole('heading', { level: 1, name: 'Admin account' })).toBeVisible()
    await page.getByLabel('Admin username').fill(adminUsername)
    await page.getByLabel('Admin password', { exact: true }).fill(adminPassword)
    await page.getByLabel('Confirm admin password').fill(adminPassword)
    await page.getByRole('button', { name: 'Next' }).click()

    // --- Optional allowlist stays disabled --------------------------------
    await expect(page.getByRole('heading', { level: 1, name: 'Network access' })).toBeVisible()
    await page.getByRole('button', { name: 'Next' }).click()

    // --- Review + submit --------------------------------------------------
    await expect(page.getByRole('heading', { level: 1, name: 'Review and finish' })).toBeVisible()
    await page.getByRole('button', { name: 'Finish setup' }).click()

    // --- Recovery key: shown once, shape asserted, gate required ----------
    const recoveryKey = page.getByRole('textbox', { name: 'Recovery key' })
    await expect(recoveryKey).toBeVisible()
    const keyValue = await recoveryKey.inputValue()
    expect(keyValue).toMatch(RECOVERY_KEY_PATTERN)

    const continueButton = page.getByRole('button', { name: /Continue to Fate & Fridge/ })
    await expect(continueButton).toBeDisabled()
    await page.getByLabel(/I have saved this recovery key/).check()
    await expect(continueButton).toBeEnabled()
    await continueButton.click()

    // --- Shell lands on the default startup page (Calendar) ---------------
    await expect(page).toHaveURL(/\/calendar$/)
    await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
    await expect(page.getByText(adminUsername)).toBeVisible()

    // --- Admin API: household PATCH succeeds, startup page is honored -----
    const adminToken = await csrfToken(page)
    const adminPatch = await page.context().request.patch(`${baseURL}/api/v1/settings/household`, {
      headers: { 'X-CSRF-TOKEN': adminToken },
      data: { default_diner_count: 5 },
    })
    expect(adminPatch.status(), 'an Admin may patch household settings').toBe(200)

    const toDashboard = await page.context().request.patch(`${baseURL}/api/v1/settings/household`, {
      headers: { 'X-CSRF-TOKEN': adminToken },
      data: { startup_page: 'dashboard' },
    })
    expect(toDashboard.status()).toBe(200)
    await page.goto(`${baseURL}/`)
    await expect(page).toHaveURL(/\/dashboard$/)
    await expect(page.getByRole('heading', { level: 2, name: 'Dashboard' })).toBeVisible()

    const toCalendar = await page.context().request.patch(`${baseURL}/api/v1/settings/household`, {
      headers: { 'X-CSRF-TOKEN': adminToken },
      data: { startup_page: 'calendar' },
    })
    expect(toCalendar.status()).toBe(200)
    await page.goto(`${baseURL}/`)
    await expect(page).toHaveURL(/\/calendar$/)

    // --- Unknown API path uses the shared envelope (built-app fallback) ---
    const unknown = await page.context().request.get(`${baseURL}/api/v1/does-not-exist`)
    expect(unknown.status()).toBe(404)
    const unknownBody = (await unknown.json()) as { error?: { code?: string } }
    expect(unknownBody.error?.code).toBe('NOT_FOUND')

    // --- Create a member with a one-time temporary password ---------------
    await page.goto(`${baseURL}/settings/users`)
    await expect(page.getByRole('heading', { level: 3, name: 'Users' })).toBeVisible()
    await page.getByRole('button', { name: 'Add user' }).click()
    await page.getByLabel('Username').fill(memberUsername)
    await page.getByRole('dialog').getByRole('button', { name: 'Add user' }).click()

    const oneTimePassword = page.getByRole('textbox', { name: 'Temporary password' })
    await expect(page.getByText(`Temporary password for ${memberUsername}`)).toBeVisible()
    const temporaryPassword = await oneTimePassword.inputValue()
    expect(temporaryPassword.length).toBeGreaterThanOrEqual(12)
    await page.getByRole('button', { name: 'Done' }).click()

    const memberRow = page.getByRole('row', { name: new RegExp(memberUsername) })
    await expect(memberRow.getByText('View only (3)')).toBeVisible()

    // --- Sign out and sign in as the member (forced password change) ------
    await page.getByRole('button', { name: 'Sign out' }).click()
    await expect(page).toHaveURL(/\/login$/)
    await page.getByLabel('Username').fill(memberUsername)
    await page.getByLabel('Password').fill(temporaryPassword)
    await page.getByRole('button', { name: 'Sign in' }).click()

    await expect(page).toHaveURL(/\/change-password$/)
    await expect(page.getByRole('heading', { level: 1, name: 'Change password' })).toBeVisible()
    const memberPassword = `E2e-member-${crypto.randomUUID().slice(0, 12)}`
    await page.getByLabel('Current password').fill(temporaryPassword)
    await page.getByLabel('New password').fill(memberPassword)
    await page.getByRole('button', { name: 'Change password' }).click()

    // The deep link from the sign-out is preserved through the forced change,
    // and the startup redirect still applies from the root.
    await expect(page).toHaveURL(/\/settings\/users$/)
    await page.goto(`${baseURL}/`)
    await expect(page).toHaveURL(/\/calendar$/)
    await expect(page.getByText(memberUsername)).toBeVisible()

    // --- Member sees only Account + Household, and Admin routes explain ---
    await page.goto(`${baseURL}/settings/users`)
    await expect(page.getByText(/limited to Admins/i)).toBeVisible()
    const settingsNav = page.getByRole('navigation', { name: 'Settings sections' })
    await expect(settingsNav.getByRole('link', { name: 'Household' })).toBeVisible()
    await expect(settingsNav.getByRole('link', { name: 'Account' })).toBeVisible()
    await expect(settingsNav.getByRole('link', { name: 'Users' })).toHaveCount(0)
    await expect(settingsNav.getByRole('link', { name: 'Security' })).toHaveCount(0)

    await page.goto(`${baseURL}/settings/account`)
    await expect(page.getByRole('heading', { level: 3, name: 'Account' })).toBeVisible()
    await expect(page.locator('main').getByText(memberUsername)).toBeVisible()

    // --- Member API cannot patch household settings (403, not CSRF) -------
    const memberToken = await csrfToken(page)
    const memberPatch = await page.context().request.patch(`${baseURL}/api/v1/settings/household`, {
      headers: { 'X-CSRF-TOKEN': memberToken },
      data: { default_diner_count: 9 },
    })
    expect(memberPatch.status(), 'a Member must not change household settings').toBe(403)
    const memberError = (await memberPatch.json()) as { error?: { code?: string } }
    expect(memberError.error?.code).toBe('FORBIDDEN')
  })
})

test.describe('single-user household', () => {
  test.skip(setupMode !== 'single_user', 'E2E_SETUP_MODE is not single_user')

  test('setup, no-login shell, settings, and startup page', async ({ page }) => {
    test.setTimeout(240_000)

    await page.goto(`${baseURL}/`)
    await expect(page).toHaveURL(/\/setup$/)

    // --- Single-user path: warning, no admin step, no sign-in afterwards --
    await page.getByLabel(/Single user/).check()
    await expect(page.getByText(/trusted private network/i)).toBeVisible()
    await page.getByRole('button', { name: 'Next' }).click()

    await page.getByLabel('Household name').fill('E2E Single Household')
    await page.getByLabel('Timezone').fill('America/Chicago')
    await page.getByRole('button', { name: 'Next' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Network access' })).toBeVisible()
    await page.getByRole('button', { name: 'Next' }).click()
    await page.getByRole('button', { name: 'Finish setup' }).click()

    const recoveryKey = page.getByRole('textbox', { name: 'Recovery key' })
    await expect(recoveryKey).toBeVisible()
    expect(await recoveryKey.inputValue()).toMatch(RECOVERY_KEY_PATTERN)
    await page.getByLabel(/I have saved this recovery key/).check()
    await page.getByRole('button', { name: /Continue to Fate & Fridge/ }).click()

    // --- Shell directly, no login, Calendar start page --------------------
    await expect(page).toHaveURL(/\/calendar$/)
    await expect(page.getByRole('heading', { level: 2, name: 'Calendar' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0)

    // --- Settings are reachable for the system actor ----------------------
    await page.goto(`${baseURL}/settings`)
    await expect(page.getByRole('heading', { level: 3, name: 'Household' })).toBeVisible()

    // --- Startup redirect still applies -----------------------------------
    await page.goto(`${baseURL}/`)
    await expect(page).toHaveURL(/\/calendar$/)
  })
})
