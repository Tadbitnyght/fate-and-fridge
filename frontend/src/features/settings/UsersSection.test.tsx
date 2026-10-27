import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from '../../App'
import { installMockFetch, type MockFetch } from '../../test/mockFetch'
import {
  adminSession,
  adminUserRecord,
  ALL_PERMISSIONS,
  memberUserRecord,
  setupCompleteStatus,
} from '../../test/fixtures'
import { renderWithProviders } from '../../test/utils'

const generatedPassword = 'Kd7mQ2xRf9Tn4Wpz8Hs6'

function requestsFor(mock: MockFetch, suffix: string): Array<Record<string, unknown>> {
  return mock.fetchMock.mock.calls
    .filter(([url]) => String(url).endsWith(suffix))
    .map(([, init]) => {
      const request = init as RequestInit | undefined
      return {
        method: request?.method ?? 'GET',
        ...(request?.body
          ? { body: JSON.parse(String(request.body)) as Record<string, unknown> }
          : {}),
      }
    })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('users section', () => {
  it('lists users with roles, status, last sign-in and permission summaries', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [adminUserRecord, memberUserRecord] },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    const table = await screen.findByRole('table', { name: 'Household users' })
    expect(within(table).getByText('root')).toBeInTheDocument()
    expect(within(table).getByText('All permissions (Admin)')).toBeInTheDocument()
    expect(within(table).getByText('View only (3)')).toBeInTheDocument()
    expect(within(table).getAllByText('Never').length).toBeGreaterThan(0)
  })

  it('shows a specific empty state when the household has no users', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [] },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    expect(
      await screen.findByText(
        'No users yet. Add the first one to give someone else access to the household.',
      ),
    ).toBeVisible()
    expect(screen.queryByRole('table', { name: 'Household users' })).not.toBeInTheDocument()
  })

  it('creates a member without a password and shows the one-time password once', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })

    const createdMember = {
      ...memberUserRecord,
      id: 3,
      username: 'newbie',
      last_login_at: null,
      permissions: ['calendar.view', 'groceries.view', 'recipes.view'],
      temporary_password: generatedPassword,
    }
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/users',
        respond: (_init, callCount) =>
          callCount === 1
            ? { status: 200, body: [adminUserRecord, memberUserRecord] }
            : { status: 200, body: [adminUserRecord, memberUserRecord, createdMember] },
      },
      { path: '/users', method: 'POST', body: createdMember },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add user' }))
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'newbie' } })
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add user' }))

    expect(await screen.findByText(`Temporary password for newbie`)).toBeInTheDocument()
    expect(screen.getByLabelText('Temporary password')).toHaveValue(generatedPassword)

    fireEvent.click(screen.getByRole('button', { name: 'Copy' }))
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(generatedPassword))

    // Member role was sent without a password field.
    expect(requestsFor(mock, '/users')).toContainEqual({
      method: 'POST',
      body: { username: 'newbie', role: 'member' },
    })

    // Dismissing removes the one-time password for good.
    fireEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(screen.queryByText(`Temporary password for newbie`)).not.toBeInTheDocument()
    expect(await screen.findByText('newbie')).toBeInTheDocument()
  })

  it('does not show a one-time password when an explicit password is sent', async () => {
    const createdMember = { ...memberUserRecord, id: 4, username: 'explicit' }
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [adminUserRecord, memberUserRecord] },
      { path: '/users', method: 'POST', body: createdMember },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add user' }))
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'explicit' } })
    fireEvent.change(screen.getByLabelText('Password (optional)'), {
      target: { value: 'a much better passphrase' },
    })
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add user' }))

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Add user' })).not.toBeInTheDocument(),
    )
    expect(screen.queryByText(/Temporary password/)).not.toBeInTheDocument()
    expect(requestsFor(mock, '/users')).toContainEqual({
      method: 'POST',
      body: { username: 'explicit', role: 'member', password: 'a much better passphrase' },
    })
  })

  it('surfaces LAST_ADMIN_PROTECTED when demoting the last admin', async () => {
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [adminUserRecord, memberUserRecord] },
      {
        path: '/users/1',
        method: 'PATCH',
        status: 409,
        body: {
          error: {
            code: 'LAST_ADMIN_PROTECTED',
            message: 'The last active Admin cannot be demoted.',
            details: {},
          },
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    fireEvent.click(await screen.findByRole('button', { name: 'Edit root' }))
    fireEvent.change(screen.getByLabelText('Role'), { target: { value: 'member' } })

    expect(screen.getByText(/revokes their sessions/i)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The last active Admin cannot be demoted.',
    )
    expect(requestsFor(mock, '/users/1')).toContainEqual({
      method: 'PATCH',
      body: { username: 'root', role: 'member' },
    })
  })

  it('saves permission presets and explains that edit implies view', async () => {
    let memberPermissions = [...memberUserRecord.permissions]
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/users',
        respond: () => ({
          status: 200,
          body: [adminUserRecord, { ...memberUserRecord, permissions: memberPermissions }],
        }),
      },
      {
        path: '/users/2/permissions',
        method: 'PATCH',
        respond: (init) => {
          const body = JSON.parse(String(init.body)) as { permissions: string[] }
          memberPermissions = body.permissions
          return { status: 200, body: { ...memberUserRecord, permissions: memberPermissions } }
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    fireEvent.click(await screen.findByRole('button', { name: 'Permissions for sam' }))
    expect(screen.getByText(/edit implies view/i)).toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Full member'))
    fireEvent.click(screen.getByRole('button', { name: 'Save permissions' }))

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Permissions for sam' })).not.toBeInTheDocument(),
    )
    expect(requestsFor(mock, '/users/2/permissions')).toContainEqual({
      method: 'PATCH',
      body: { permissions: ALL_PERMISSIONS },
    })
    // Reference data edit is part of the preset, so the summary counts seven.
    expect(await screen.findByText('Full member (7)')).toBeInTheDocument()
  })

  it('confirms disabling and resets the password with a one-time display', async () => {
    let reset = false
    const mock = installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      {
        path: '/users',
        respond: () => ({
          status: 200,
          body: [adminUserRecord, { ...memberUserRecord, must_change_password: reset }],
        }),
      },
      { path: '/users/2/disable', method: 'POST', body: { ...memberUserRecord, is_active: false } },
      {
        path: '/users/2/reset-password',
        method: 'POST',
        respond: () => {
          reset = true
          return { status: 200, body: { temporary_password: generatedPassword } }
        },
      },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    fireEvent.click(await screen.findByRole('button', { name: 'Disable sam' }))
    expect(screen.getByRole('dialog', { name: 'Disable sam?' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Disable user' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(requestsFor(mock, '/users/2/disable')).toContainEqual({ method: 'POST' })

    fireEvent.click(screen.getByRole('button', { name: 'Reset password for sam' }))
    fireEvent.click(screen.getByRole('button', { name: 'Reset password' }))

    expect(await screen.findByText('Temporary password for sam')).toBeInTheDocument()
    expect(screen.getByLabelText('Temporary password')).toHaveValue(generatedPassword)
    expect(requestsFor(mock, '/users/2/reset-password')).toContainEqual({ method: 'POST' })

    // The reset refreshes the list, so the forced-change tag appears at once.
    expect(await screen.findByText('Must change password')).toBeInTheDocument()
  })

  it('clears password errors while typing in the create dialog', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [adminUserRecord, memberUserRecord] },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    fireEvent.click(await screen.findByRole('button', { name: 'Add user' }))
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'newbie' } })
    fireEvent.change(screen.getByLabelText('Password (optional)'), { target: { value: 'short' } })
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add user' }))

    expect(await screen.findByText('Use 12–128 characters.')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Password (optional)'), {
      target: { value: 'a much better passphrase' },
    })
    expect(screen.queryByText('Use 12–128 characters.')).not.toBeInTheDocument()
  })

  it('closes dialogs with Escape and returns focus to the trigger', async () => {
    installMockFetch([
      { path: '/setup/status', body: setupCompleteStatus },
      { path: '/auth/session', body: adminSession },
      { path: '/users', body: [adminUserRecord, memberUserRecord] },
    ])

    renderWithProviders(<App />, { route: '/settings/users' })

    const trigger = await screen.findByRole('button', { name: 'Add user' })
    trigger.focus()
    fireEvent.click(trigger)
    expect(screen.getByRole('dialog', { name: 'Add user' })).toBeInTheDocument()

    fireEvent.keyDown(document, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(document.activeElement).toBe(trigger))
  })
})
