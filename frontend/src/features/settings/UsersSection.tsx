import { useState } from 'react'

import { isApiError } from '../../api/client'
import type { UserSummary } from '../../api/users'
import { ConfirmDialog } from '../../components/Modal'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { formatTimestamp } from '../../lib/dateFormat'
import { useAdminAccess } from './access'
import { AdminOnlyNotice } from './AdminOnlyNotice'
import { OneTimePasswordPanel } from './OneTimePasswordPanel'
import { PermissionsDialog } from './PermissionsDialog'
import { UserFormDialog } from './UserFormDialog'
import { permissionsSummary } from './permissions'
import { useDisableUser, useEnableUser, useResetUserPassword, useUsers } from './useUsers'

type DialogState =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserSummary }
  | { kind: 'permissions'; user: UserSummary }
  | { kind: 'disable'; user: UserSummary }
  | { kind: 'enable'; user: UserSummary }
  | { kind: 'reset'; user: UserSummary }

function formatLastLogin(value: string | null): string {
  if (!value) {
    return 'Never'
  }
  return formatTimestamp(value)
}

/** User management (docs/API.md §6): Admin-only; the server enforces this. */
export function UsersSection() {
  const { isAdmin } = useAdminAccess()
  const usersQuery = useUsers(isAdmin)
  const disableUser = useDisableUser()
  const enableUser = useEnableUser()
  const resetPassword = useResetUserPassword()

  const [dialog, setDialog] = useState<DialogState | null>(null)
  const [confirmError, setConfirmError] = useState<string | null>(null)
  const [oneTime, setOneTime] = useState<{ username: string; password: string } | null>(null)

  if (!isAdmin) {
    return (
      <section className="settings-section" aria-labelledby="users-heading">
        <h3 id="users-heading">Users</h3>
        <AdminOnlyNotice feature="User management" />
      </section>
    )
  }

  if (usersQuery.isPending) {
    return <LoadingScreen label="Loading users…" />
  }

  if (usersQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load users"
        error={usersQuery.error}
        onRetry={() => void usersQuery.refetch()}
      />
    )
  }

  const users = usersQuery.data
  const confirmPending = disableUser.isPending || enableUser.isPending || resetPassword.isPending

  async function handleConfirm(): Promise<void> {
    if (!dialog || !('user' in dialog) || dialog.kind === 'edit' || dialog.kind === 'permissions') {
      return
    }
    setConfirmError(null)
    try {
      if (dialog.kind === 'disable') {
        await disableUser.mutateAsync(dialog.user.id)
      } else if (dialog.kind === 'enable') {
        await enableUser.mutateAsync(dialog.user.id)
      } else if (dialog.kind === 'reset') {
        const result = await resetPassword.mutateAsync(dialog.user.id)
        setOneTime({ username: dialog.user.username, password: result.temporary_password })
      }
      setDialog(null)
    } catch (caught) {
      setConfirmError(
        isApiError(caught) ? caught.message : 'The action failed. Check the server and try again.',
      )
    }
  }

  return (
    <section className="settings-section" aria-labelledby="users-heading">
      <div className="settings-section__header">
        <h3 id="users-heading">Users</h3>
        <button
          type="button"
          className="button--primary"
          onClick={() => setDialog({ kind: 'create' })}
        >
          Add user
        </button>
      </div>

      {oneTime && (
        <OneTimePasswordPanel
          username={oneTime.username}
          password={oneTime.password}
          onDismiss={() => setOneTime(null)}
        />
      )}

      <div className="table-wrap">
        <table className="data-table">
          <caption className="sr-only">Household users</caption>
          <thead>
            <tr>
              <th scope="col">Username</th>
              <th scope="col">Role</th>
              <th scope="col">Status</th>
              <th scope="col">Last sign-in</th>
              <th scope="col">Permissions</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {users.map((user) => (
              <tr key={user.id}>
                <th scope="row">{user.username}</th>
                <td>{user.role === 'admin' ? 'Admin' : 'Member'}</td>
                <td>
                  <span className={user.is_active ? 'tag tag--ok' : 'tag tag--bad'}>
                    {user.is_active ? 'Active' : 'Disabled'}
                  </span>
                  {user.must_change_password && <span className="tag">Must change password</span>}
                </td>
                <td>{formatLastLogin(user.last_login_at)}</td>
                <td>{permissionsSummary(user.permissions, user.role)}</td>
                <td className="data-table__actions">
                  <button
                    type="button"
                    aria-label={`Edit ${user.username}`}
                    onClick={() => setDialog({ kind: 'edit', user })}
                  >
                    Edit
                  </button>
                  <button
                    type="button"
                    aria-label={`Permissions for ${user.username}`}
                    onClick={() => setDialog({ kind: 'permissions', user })}
                  >
                    Permissions
                  </button>
                  <button
                    type="button"
                    aria-label={`Reset password for ${user.username}`}
                    onClick={() => {
                      setConfirmError(null)
                      setDialog({ kind: 'reset', user })
                    }}
                  >
                    Reset password
                  </button>
                  {user.is_active ? (
                    <button
                      type="button"
                      aria-label={`Disable ${user.username}`}
                      onClick={() => {
                        setConfirmError(null)
                        setDialog({ kind: 'disable', user })
                      }}
                    >
                      Disable
                    </button>
                  ) : (
                    <button
                      type="button"
                      aria-label={`Enable ${user.username}`}
                      onClick={() => {
                        setConfirmError(null)
                        setDialog({ kind: 'enable', user })
                      }}
                    >
                      Enable
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {dialog?.kind === 'create' && (
        <UserFormDialog
          mode="create"
          onClose={() => setDialog(null)}
          onSaved={(created) => {
            setDialog(null)
            if (created.temporary_password) {
              setOneTime({ username: created.username, password: created.temporary_password })
            }
          }}
        />
      )}

      {dialog?.kind === 'edit' && (
        <UserFormDialog
          mode="edit"
          user={dialog.user}
          onClose={() => setDialog(null)}
          onSaved={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'permissions' && (
        <PermissionsDialog user={dialog.user} onClose={() => setDialog(null)} />
      )}

      {dialog?.kind === 'disable' && (
        <ConfirmDialog
          title={`Disable ${dialog.user.username}?`}
          message={
            <p>
              Their sessions are revoked immediately and they cannot sign in until re-enabled. The
              last active Admin cannot be disabled.
            </p>
          }
          confirmLabel="Disable user"
          pending={confirmPending}
          error={confirmError}
          onConfirm={() => void handleConfirm()}
          onClose={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'enable' && (
        <ConfirmDialog
          title={`Enable ${dialog.user.username}?`}
          message={<p>They can sign in again and their lockout state is cleared.</p>}
          confirmLabel="Enable user"
          pending={confirmPending}
          error={confirmError}
          onConfirm={() => void handleConfirm()}
          onClose={() => setDialog(null)}
        />
      )}

      {dialog?.kind === 'reset' && (
        <ConfirmDialog
          title={`Reset password for ${dialog.user.username}?`}
          message={
            <p>
              A one-time temporary password is generated and shown once. All of their sessions are
              revoked; they must change the password at next sign-in.
            </p>
          }
          confirmLabel="Reset password"
          pending={confirmPending}
          error={confirmError}
          onConfirm={() => void handleConfirm()}
          onClose={() => setDialog(null)}
        />
      )}
    </section>
  )
}
