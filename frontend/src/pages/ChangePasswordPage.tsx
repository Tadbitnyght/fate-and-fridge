import { useState, type FormEvent } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'

import { ErrorScreen, LoadingScreen } from '../components/Screens'
import { useAuth } from '../features/auth/AuthContext'
import { changePasswordErrorMessage } from '../features/auth/messages'
import { sanitizeReturnTo } from '../features/auth/redirect'
import { useSetupStatus } from '../features/setup/useSetupStatus'

interface LocationState {
  from?: unknown
}

/**
 * Password change page (docs/API.md §5). Reached voluntarily or because the
 * backend set `must_change_password` (forced-change gate).
 */
export function ChangePasswordPage() {
  const { status, session, changePassword } = useAuth()
  const setup = useSetupStatus()
  const location = useLocation()
  const navigate = useNavigate()
  const returnTo = sanitizeReturnTo((location.state as LocationState | null)?.from)

  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  if (setup.isPending || status === 'loading') {
    return <LoadingScreen label="Loading…" />
  }

  if (setup.isError) {
    return (
      <ErrorScreen
        title="Cannot reach the server"
        error={setup.error}
        onRetry={() => void setup.refetch()}
      />
    )
  }

  if (!setup.data.setup_completed) {
    return <Navigate to="/setup" replace />
  }

  if (status === 'unauthenticated') {
    return <Navigate to="/login" replace />
  }

  if (status === 'single_user') {
    return <Navigate to="/" replace />
  }

  const forced = session?.must_change_password === true

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await changePassword({ current_password: currentPassword, new_password: newPassword })
      navigate(returnTo, { replace: true })
    } catch (caught) {
      setError(changePasswordErrorMessage(caught))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="screen screen--narrow">
      <h1>Change password</h1>
      {forced ? (
        <p className="form__notice" role="status">
          You must set a new password before continuing.
        </p>
      ) : (
        <p className="page-note">Choose a new password for your account.</p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <div className="form__field">
          <label htmlFor="current-password">Current password</label>
          <input
            id="current-password"
            name="current_password"
            type="password"
            autoComplete="current-password"
            autoFocus
            required
            value={currentPassword}
            onChange={(event) => setCurrentPassword(event.target.value)}
          />
        </div>

        <div className="form__field">
          <label htmlFor="new-password">New password</label>
          <input
            id="new-password"
            name="new_password"
            type="password"
            autoComplete="new-password"
            required
            minLength={12}
            maxLength={128}
            aria-describedby="new-password-help"
            value={newPassword}
            onChange={(event) => setNewPassword(event.target.value)}
          />
          <p id="new-password-help" className="form__hint">
            12–128 characters. The server enforces the full policy.
          </p>
        </div>

        {error && (
          <p className="form__error" role="alert">
            {error}
          </p>
        )}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Saving…' : 'Change password'}
        </button>
      </form>
    </div>
  )
}
