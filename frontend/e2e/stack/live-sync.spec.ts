import { expect, test, type Locator, type Page } from '@playwright/test'

/**
 * Real-stack two-device live-sync flow (M9-08, docs/M9_PLAN.md §4 "Two-device",
 * docs/TESTING.md §10).
 *
 * Runs against a live, disposable stack — never against mocks. The suite is
 * skipped unless E2E_BASE_URL points at that stack, so the hermetic default
 * `npx playwright test` run is unaffected.
 *
 * It is also gated behind E2E_STACK_LIVE_SYNC=1: like the M1-15 acceptance spec
 * (`stack.spec.ts`) it consumes the single-shot first-run setup, and the two
 * suites cannot share one stack. Gating keeps it out of the default
 * `npm run test:e2e` run so the acceptance spec always sees a fresh install; CI
 * gives this flow its own reset stack before running it (see
 * `.github/workflows/ci.yml` and docs/TESTING.md §10).
 *
 * Invocation (needs its own fresh/unconfigured multiuser stack):
 *   E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser \
 *     E2E_STACK_LIVE_SYNC=1 npx playwright test e2e/stack/live-sync.spec.ts
 *
 * The stack must be fresh/unconfigured (multiuser): setup is single-shot, so a
 * reset is required before a second run. One test owns the whole flow because
 * the stack can only be configured once; the four scenarios inside it are
 * self-contained (each seeds its own data and asserts on the live UI), so none
 * depends on the order of the others.
 *
 * Two browser contexts model the two devices: context A is the Admin, context B
 * is a view-only Member. Every mutation is driven through A's UI and every
 * assertion reads B's rendered UI (roles/text), never internal state.
 */

const baseURL = process.env.E2E_BASE_URL
const setupMode = process.env.E2E_SETUP_MODE ?? 'multiuser'

test.skip(!baseURL, 'Set E2E_BASE_URL to run the real-stack suite (disposable stack required)')
test.skip(
  process.env.E2E_STACK_LIVE_SYNC !== '1',
  'Set E2E_STACK_LIVE_SYNC=1 to run the live-sync flow (it consumes its own fresh stack)',
)

const RECOVERY_KEY_PATTERN = /^[A-Za-z0-9_-]{43}$/ // 32 bytes, base64url, unpadded

// Generated per run and held in memory only; never committed or logged.
const adminUsername = 'e2e-sync-admin'
const adminPassword = `E2e-sync-admin-${crypto.randomUUID().slice(0, 12)}`
const memberUsername = 'e2e-sync-member'

/** Waits until the accessible live-sync indicator reports a connected socket. */
async function expectLive(page: Page): Promise<void> {
  const indicator = page.getByTestId('live-sync-status')
  await expect(indicator).toHaveAttribute('data-state', 'connected', { timeout: 30_000 })
  await expect(indicator).toContainText('Live')
}

async function signIn(page: Page, username: string, password: string): Promise<void> {
  await page.goto(`${baseURL}/login`)
  await page.getByLabel('Username').fill(username)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

/** Adds a manual grocery item through the dialog. */
async function addManualGrocery(page: Page, name: string): Promise<void> {
  await page.getByRole('button', { name: 'Add grocery item' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add grocery item' })
  await expect(dialog).toBeVisible()
  await dialog.getByLabel('Name').fill(name)
  await dialog.getByRole('button', { name: 'Add item' }).click()
  await expect(dialog).toHaveCount(0)
}

/** Opens the date-detail dialog for the household's today grid cell. */
async function openTodayDialog(page: Page): Promise<Locator> {
  await page.getByRole('gridcell', { name: /today/i }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  return dialog
}

/** Saves a Misc entry with the given label on the household's today. */
async function setTodayMealLabel(page: Page, label: string): Promise<void> {
  const dialog = await openTodayDialog(page)
  await dialog.getByLabel('Type').selectOption('misc')
  await dialog.getByLabel('Label (required)').fill(label)
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(dialog).toHaveCount(0)
}

test.describe('two-device live sync', () => {
  test.skip(setupMode !== 'multiuser', 'E2E_SETUP_MODE is not multiuser')

  test('a change on one device reaches the other over the live socket', async ({ browser }) => {
    test.setTimeout(300_000)

    // The browser timezone tracks the household so "today" and the displayed
    // month agree between the server and both devices.
    const contextOptions = { baseURL, timezoneId: 'America/Chicago' }

    // --- One-time setup on the fresh stack (consumes it) ------------------
    const adminContext = await browser.newContext(contextOptions)
    const adminPage = await adminContext.newPage()

    await adminPage.goto(`${baseURL}/`)
    await expect(adminPage).toHaveURL(/\/setup$/)
    await adminPage.getByLabel(/Multiuser/).check()
    await adminPage.getByRole('button', { name: 'Next' }).click()
    await adminPage.getByLabel('Household name').fill('Two-Device Household')
    await adminPage.getByLabel('Timezone').fill('America/Chicago')
    await adminPage.getByRole('button', { name: 'Next' }).click()
    await adminPage.getByLabel('Admin username').fill(adminUsername)
    await adminPage.getByLabel('Admin password', { exact: true }).fill(adminPassword)
    await adminPage.getByLabel('Confirm admin password').fill(adminPassword)
    await adminPage.getByRole('button', { name: 'Next' }).click()
    await adminPage.getByRole('button', { name: 'Next' }).click()
    await adminPage.getByRole('button', { name: 'Finish setup' }).click()

    const recoveryKey = adminPage.getByRole('textbox', { name: 'Recovery key' })
    await expect(recoveryKey).toBeVisible()
    expect(await recoveryKey.inputValue()).toMatch(RECOVERY_KEY_PATTERN)
    await adminPage.getByLabel(/I have saved this recovery key/).check()
    await adminPage.getByRole('button', { name: /Continue to Fate & Fridge/ }).click()
    await expect(adminPage).toHaveURL(/\/calendar$/)

    // --- Create the view-only member (device B) ---------------------------
    await adminPage.goto(`${baseURL}/settings/users`)
    await adminPage.getByRole('button', { name: 'Add user' }).click()
    await adminPage.getByLabel('Username').fill(memberUsername)
    await adminPage.getByRole('button', { name: 'Create user' }).click()
    const temporaryPassword = await adminPage
      .getByRole('textbox', { name: 'Temporary password' })
      .inputValue()
    await adminPage.getByRole('button', { name: 'Done' }).click()

    const memberContext = await browser.newContext(contextOptions)
    const memberPage = await memberContext.newPage()
    const memberPassword = `E2e-sync-member-${crypto.randomUUID().slice(0, 12)}`
    await signIn(memberPage, memberUsername, temporaryPassword)
    await expect(memberPage).toHaveURL(/\/change-password$/)
    await memberPage.getByLabel('Current password').fill(temporaryPassword)
    await memberPage.getByLabel('New password').fill(memberPassword)
    await memberPage.getByRole('button', { name: 'Change password' }).click()
    await expect(memberPage).not.toHaveURL(/\/change-password$/)

    try {
      // --- Scenario 1: manual grocery added on A reaches B ----------------
      await adminPage.goto(`${baseURL}/groceries`)
      await memberPage.goto(`${baseURL}/groceries`)
      await expect(adminPage.getByRole('heading', { level: 2, name: 'Groceries' })).toBeVisible()
      await expect(memberPage.getByRole('heading', { level: 2, name: 'Groceries' })).toBeVisible()
      await expectLive(adminPage)
      await expectLive(memberPage)

      const addedItem = `Sync Added ${crypto.randomUUID().slice(0, 6)}`
      await addManualGrocery(adminPage, addedItem)
      await expect(memberPage.getByText(addedItem)).toBeVisible({ timeout: 15_000 })

      // --- Scenario 2: grocery check on A reaches B -----------------------
      const checkedItem = `Sync Checked ${crypto.randomUUID().slice(0, 6)}`
      await addManualGrocery(adminPage, checkedItem)
      await expect(memberPage.getByText(checkedItem)).toBeVisible({ timeout: 15_000 })

      await adminPage.getByLabel(`Mark ${checkedItem} as bought`).click()
      await expect(memberPage.getByLabel(`Mark ${checkedItem} as not bought`)).toBeChecked({
        timeout: 15_000,
      })

      // --- Scenario 3: a calendar meal change on A reaches B --------------
      await adminPage.goto(`${baseURL}/calendar`)
      await memberPage.goto(`${baseURL}/calendar`)
      await expect(adminPage.getByRole('grid')).toBeVisible()
      await expect(memberPage.getByRole('grid')).toBeVisible()
      await expectLive(adminPage)
      await expectLive(memberPage)

      const mealLabel = `Sync Meal ${crypto.randomUUID().slice(0, 6)}`
      await setTodayMealLabel(adminPage, mealLabel)
      await expect(memberPage.getByRole('grid').getByText(mealLabel)).toBeVisible({
        timeout: 15_000,
      })

      // --- Scenario 4: a lock change on A reaches B -----------------------
      const lockLabel = `Sync Lock ${crypto.randomUUID().slice(0, 6)}`
      await setTodayMealLabel(adminPage, lockLabel)
      const lockDialog = await openTodayDialog(adminPage)
      await lockDialog.getByRole('button', { name: 'Lock' }).click()
      await expect(lockDialog.getByRole('button', { name: 'Unlock' })).toBeVisible()

      const memberTodayCell = memberPage.getByRole('gridcell', { name: /today/i })
      await expect(memberTodayCell.getByLabel('Locked')).toBeVisible({ timeout: 15_000 })
    } finally {
      await memberContext.close()
      await adminContext.close()
    }
  })
})
