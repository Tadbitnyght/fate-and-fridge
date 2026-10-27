import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch, type MockFetch, type MockResponse } from '../../test/mockFetch'
import { renderWithProviders } from '../../test/utils'

const setupIncomplete = { setup_completed: false, auth_mode: null, recovery_key_present: false }

const singleUserSession = {
  authenticated: true,
  auth_mode: 'single_user',
  user: null,
  permissions: ['calendar.view'],
  must_change_password: false,
}

const multiuserSession = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'smith', role: 'admin' },
  permissions: ['calendar.view'],
  must_change_password: false,
}

const authRequired: MockResponse = {
  status: 401,
  body: { error: { code: 'AUTH_REQUIRED', message: 'A valid session is required.', details: {} } },
}

const setupRequired: MockResponse = {
  status: 409,
  body: { error: { code: 'SETUP_REQUIRED', message: 'Setup is not complete.', details: {} } },
}

interface SetupMockState {
  completed: boolean
  authMode: 'multiuser' | 'single_user'
  sessionAuthenticated: boolean
  complete: () => MockResponse
}

function installSetupMocks(): { state: SetupMockState; mock: MockFetch } {
  const state: SetupMockState = {
    completed: false,
    authMode: 'multiuser',
    sessionAuthenticated: true,
    complete: () => ({
      status: 201,
      body: {
        auth_mode: 'multiuser',
        recovery_key: 'RECOVERY-KEY-123',
        session: multiuserSession,
      },
    }),
  }

  const mock = installMockFetch([
    {
      path: '/setup/status',
      respond: () => ({
        status: 200,
        body: state.completed
          ? {
              setup_completed: true,
              auth_mode: state.authMode,
              recovery_key_present: true,
            }
          : setupIncomplete,
      }),
    },
    {
      path: '/auth/session',
      respond: () => {
        if (!state.completed) {
          return setupRequired
        }
        if (state.authMode === 'single_user') {
          return { status: 200, body: singleUserSession }
        }
        return state.sessionAuthenticated ? { status: 200, body: multiuserSession } : authRequired
      },
    },
    {
      path: '/setup/complete',
      method: 'POST',
      respond: () => {
        const response = state.complete()
        state.completed = response.status === 201 || response.status === 409
        return response
      },
    },
  ])

  return { state, mock }
}

async function chooseMode(mode: 'single_user' | 'multiuser'): Promise<void> {
  expect(
    await screen.findByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
  ).toBeInTheDocument()
  fireEvent.click(screen.getByLabelText(mode === 'single_user' ? /single user/i : /multiuser/i))
  fireEvent.click(screen.getByRole('button', { name: 'Next' }))
}

async function fillHousehold(name = 'The Smiths'): Promise<void> {
  expect(await screen.findByRole('heading', { level: 1, name: 'Household' })).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Household name'), { target: { value: name } })
  fireEvent.change(screen.getByLabelText('Timezone'), { target: { value: 'America/Chicago' } })
  fireEvent.click(screen.getByRole('button', { name: 'Next' }))
}

async function fillAdmin(
  username = 'smith',
  password = 'correct horse battery staple',
): Promise<void> {
  expect(
    await screen.findByRole('heading', { level: 1, name: 'Admin account' }),
  ).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Admin username'), { target: { value: username } })
  fireEvent.change(screen.getByLabelText('Admin password'), { target: { value: password } })
  fireEvent.change(screen.getByLabelText('Confirm admin password'), {
    target: { value: password },
  })
  fireEvent.click(screen.getByRole('button', { name: 'Next' }))
}

async function passAllowlist(): Promise<void> {
  expect(
    await screen.findByRole('heading', { level: 1, name: 'Network access' }),
  ).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Next' }))
}

async function finishSetup(): Promise<void> {
  expect(
    await screen.findByRole('heading', { level: 1, name: 'Review and finish' }),
  ).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Finish setup' }))
}

function setupRequestBodies(mock: MockFetch): Array<Record<string, unknown>> {
  return mock.fetchMock.mock.calls
    .filter(
      ([url, init]) =>
        String(url).endsWith('/setup/complete') &&
        (init as RequestInit | undefined)?.method === 'POST',
    )
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>)
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('setup wizard', () => {
  it('completes single-user setup and gates the one-time recovery key', async () => {
    const { state, mock } = installSetupMocks()
    state.authMode = 'single_user'
    state.complete = () => ({
      status: 201,
      body: { auth_mode: 'single_user', recovery_key: 'RECOVERY-KEY-123' },
    })

    renderWithProviders(<App />, { route: '/setup' })

    // Select single user and confirm the trusted-network warning before continuing.
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
    ).toBeInTheDocument()
    fireEvent.click(screen.getByLabelText(/single user/i))
    expect(screen.getByText(/trusted private network/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))

    // Focus moves to the new step heading.
    await waitFor(() =>
      expect(document.activeElement).toBe(
        screen.getByRole('heading', { level: 1, name: 'Household' }),
      ),
    )

    // Empty household name blocks Next and shows the problem summary.
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    expect(screen.getAllByText('Enter a household name.').length).toBeGreaterThan(0)
    expect(screen.getByRole('heading', { level: 1, name: 'Household' })).toBeInTheDocument()

    await fillHousehold()
    // Single-user skips the admin step entirely.
    await passAllowlist()
    await finishSetup()

    const keyInput = await screen.findByLabelText('Recovery key')
    expect(keyInput).toHaveValue('RECOVERY-KEY-123')

    const continueButton = screen.getByRole('button', { name: /continue to fate & fridge/i })
    expect(continueButton).toBeDisabled()
    fireEvent.click(screen.getByLabelText(/I have saved this recovery key/i))
    expect(continueButton).toBeEnabled()
    fireEvent.click(continueButton)

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()

    const bodies = setupRequestBodies(mock)
    expect(bodies).toHaveLength(1)
    expect(bodies[0]).toEqual({
      household_name: 'The Smiths',
      timezone: 'America/Chicago',
      week_starts_on: 0,
      default_diner_count: 4,
      startup_page: 'calendar',
      auth_mode: 'single_user',
      ip_allowlist: { enabled: false, ranges: [] },
      update_check_enabled: true,
    })
  })

  it('completes multiuser setup with admin credentials', async () => {
    const { mock } = installSetupMocks()

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('multiuser')
    await fillHousehold()
    await fillAdmin()
    await passAllowlist()
    await finishSetup()

    expect(await screen.findByLabelText('Recovery key')).toHaveValue('RECOVERY-KEY-123')
    fireEvent.click(screen.getByLabelText(/I have saved this recovery key/i))
    fireEvent.click(screen.getByRole('button', { name: /continue to fate & fridge/i }))

    expect(await screen.findByRole('heading', { level: 2, name: 'Calendar' })).toBeInTheDocument()
    expect(screen.getByText('smith')).toBeInTheDocument()

    const bodies = setupRequestBodies(mock)
    expect(bodies[0]).toMatchObject({
      auth_mode: 'multiuser',
      admin: { username: 'smith', password: 'correct horse battery staple' },
    })
  })

  it('blocks invalid admin credentials before submitting', async () => {
    const { mock } = installSetupMocks()

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('multiuser')
    await fillHousehold()
    await screen.findByRole('heading', { level: 1, name: 'Admin account' })
    fireEvent.change(screen.getByLabelText('Admin username'), { target: { value: 'bad user!' } })
    fireEvent.change(screen.getByLabelText('Admin password'), { target: { value: 'short' } })
    fireEvent.change(screen.getByLabelText('Confirm admin password'), {
      target: { value: 'different password' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))

    expect(screen.getAllByText(/letters, digits/i).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/12–128 characters/i).length).toBeGreaterThan(0)
    expect(screen.getAllByText('The passwords do not match.').length).toBeGreaterThan(0)
    expect(screen.getByRole('heading', { level: 1, name: 'Admin account' })).toBeInTheDocument()
    expect(setupRequestBodies(mock)).toHaveLength(0)
  })

  it('maps server field errors back to the step that owns them', async () => {
    const { state } = installSetupMocks()
    state.complete = () => ({
      status: 400,
      body: {
        error: {
          code: 'VALIDATION_FAILED',
          message: 'The request body failed validation.',
          details: {
            field_errors: {
              'admin.username': ['admin.username is already taken.'],
              'ip_allowlist.ranges': [
                'Each ip_allowlist range must be a valid IPv4 or IPv6 address or CIDR.',
              ],
            },
          },
        },
      },
    })

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('multiuser')
    await fillHousehold()
    await fillAdmin()
    // Enable the allowlist with a valid range so client validation passes.
    fireEvent.click(screen.getByLabelText(/enable the ip allowlist/i))
    fireEvent.change(screen.getByLabelText(/allowed addresses/i), {
      target: { value: '10.0.0.0/8' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    await finishSetup()

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Admin account' }),
    ).toBeInTheDocument()
    expect(screen.getAllByText('admin.username is already taken.').length).toBeGreaterThan(0)

    fireEvent.change(screen.getByLabelText('Admin username'), { target: { value: 'smith2' } })
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Network access' }),
    ).toBeInTheDocument()
    expect(
      screen.getAllByText(/Each ip_allowlist range must be a valid IPv4 or IPv6 address or CIDR\./)
        .length,
    ).toBeGreaterThan(0)
  })

  it('enters the login flow when setup was completed elsewhere (409)', async () => {
    const { state } = installSetupMocks()
    state.sessionAuthenticated = false
    state.complete = () => ({
      status: 409,
      body: {
        error: {
          code: 'SETUP_ALREADY_COMPLETED',
          message: 'Setup has already been completed.',
          details: {},
        },
      },
    })

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('multiuser')
    await fillHousehold()
    await fillAdmin()
    await passAllowlist()
    await finishSetup()

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in' })).toBeInTheDocument()
  })

  it('keeps focus in the field when an edit clears the error', async () => {
    installSetupMocks()

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('multiuser')
    const nameInput = screen.getByLabelText('Household name')

    // A failed Next focuses the error summary…
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    expect(document.activeElement).toBe(screen.getByRole('alert'))

    // …but correcting the field keeps focus in the input.
    nameInput.focus()
    expect(document.activeElement).toBe(nameInput)
    fireEvent.change(nameInput, { target: { value: 'The Smiths' } })

    await waitFor(() => expect(document.activeElement).toBe(nameInput))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('wires group-level errors to the radio and checkbox controls', async () => {
    installSetupMocks()

    renderWithProviders(<App />, { route: '/setup' })

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
    ).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    const singleUser = screen.getByLabelText(/single user/i)
    expect(singleUser).toHaveAttribute('aria-invalid', 'true')
    expect(singleUser).toHaveAttribute('aria-describedby', 'auth-mode-error')

    fireEvent.click(singleUser)
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    await fillHousehold()

    const allowlistToggle = screen.getByLabelText(/enable the ip allowlist/i)
    fireEvent.click(allowlistToggle)
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))
    expect(allowlistToggle).toHaveAttribute('aria-invalid', 'true')
    expect(allowlistToggle).toHaveAttribute('aria-describedby', 'allowlist-ranges-error')
  })

  it('sends update_check_enabled=false when the toggle is turned off', async () => {
    const { state, mock } = installSetupMocks()
    state.authMode = 'single_user'
    state.complete = () => ({
      status: 201,
      body: { auth_mode: 'single_user', recovery_key: 'RECOVERY-KEY-123' },
    })

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('single_user')
    await fillHousehold()
    const updateToggle = screen.getByLabelText(/check for new releases/i)
    expect(updateToggle).toBeChecked()
    fireEvent.click(updateToggle)
    expect(updateToggle).not.toBeChecked()
    fireEvent.click(screen.getByRole('button', { name: 'Next' }))

    await finishSetup()
    await screen.findByLabelText('Recovery key')

    expect(setupRequestBodies(mock)[0]).toMatchObject({ update_check_enabled: false })
  })

  it('copies the recovery key to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    })

    const { state } = installSetupMocks()
    state.authMode = 'single_user'
    state.complete = () => ({
      status: 201,
      body: { auth_mode: 'single_user', recovery_key: 'RECOVERY-KEY-123' },
    })

    renderWithProviders(<App />, { route: '/setup' })

    await chooseMode('single_user')
    await fillHousehold()
    await passAllowlist()
    await finishSetup()

    fireEvent.click(await screen.findByRole('button', { name: 'Copy' }))

    await waitFor(() => expect(writeText).toHaveBeenCalledWith('RECOVERY-KEY-123'))
    expect(await screen.findByText('Copied to clipboard.')).toBeInTheDocument()
  })

  it('offers the restore-from-backup route without disturbing the setup flow', async () => {
    installSetupMocks()

    renderWithProviders(<App />, { route: '/setup' })

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
    ).toBeInTheDocument()
    const restoreLink = screen.getByRole('link', { name: 'Restore from backup' })
    expect(restoreLink).toHaveAttribute('href', '/setup/restore')

    fireEvent.click(restoreLink)

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Restore from backup' }),
    ).toBeInTheDocument()

    fireEvent.click(screen.getByRole('link', { name: 'Back to setup' }))

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Welcome to Fate & Fridge' }),
    ).toBeInTheDocument()
    // The normal wizard still starts at step 1 with its usual controls.
    expect(screen.getByRole('button', { name: 'Next' })).toBeInTheDocument()
  })
})
