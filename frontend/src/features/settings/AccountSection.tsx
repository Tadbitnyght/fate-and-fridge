import { Link } from 'react-router-dom'

import { useAuth } from '../auth/AuthContext'
import { PERMISSION_LABELS, type PermissionKey } from './permissions'

function permissionLabel(permission: string): string {
  return PERMISSION_LABELS[permission as PermissionKey] ?? permission
}

/** Read-only account information; password changes happen on /change-password. */
export function AccountSection() {
  const { status, user, permissions } = useAuth()

  if (status === 'single_user') {
    return (
      <section className="settings-section" aria-labelledby="account-heading">
        <h3 id="account-heading">Account</h3>
        <div className="notice" role="note">
          <p>
            Single-user mode has no sign-in. Switch to multiuser from Security to add accounts and
            permissions.
          </p>
        </div>
      </section>
    )
  }

  return (
    <section className="settings-section" aria-labelledby="account-heading">
      <h3 id="account-heading">Account</h3>

      <dl className="review">
        <div>
          <dt>Username</dt>
          <dd>{user?.username ?? '—'}</dd>
        </div>
        <div>
          <dt>Role</dt>
          <dd>{user?.role === 'admin' ? 'Admin (all permissions)' : 'Member'}</dd>
        </div>
        <div>
          <dt>Permissions</dt>
          <dd>
            {permissions.length === 0 ? (
              'None'
            ) : (
              <ul className="permission-list">
                {permissions.map((permission) => (
                  <li key={permission}>{permissionLabel(permission)}</li>
                ))}
              </ul>
            )}
          </dd>
        </div>
      </dl>

      <p>
        <Link to="/change-password">Change password</Link>
      </p>
      <p className="page-note">
        If the household requires a password change, you are redirected there automatically after
        signing in.
      </p>
    </section>
  )
}
