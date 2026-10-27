import { useState, type FormEvent } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'

import { LoadingScreen } from '../components/Screens'
import { useAuth } from '../features/auth/AuthContext'
import { loginErrorMessage } from '../features/auth/messages'
import { sanitizeReturnTo } from '../features/auth/redirect'
import { useSetupStatus } from '../features/setup/useSetupStatus'

interface LocationState {
  from?: unknown
}

export function LoginPage() {
  const { status, login } = useAuth()
  const setup = useSetupStatus()
  const location = useLocation()
  const navigate = useNavigate()

  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const returnTo = sanitizeReturnTo((location.state as LocationState | null)?.from)

  if (setup.isPending || status === 'loading') {
    return <LoadingScreen label="Loading sign-in…" />
  }

  if (setup.isSuccess && !setup.data.setup_completed) {
    return <Navigate to="/setup" replace />
  }

  if (status === 'authenticated' || status === 'single_user') {
    return <Navigate to={returnTo} replace />
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login({ username, password })
      navigate(returnTo, { replace: true })
    } catch (caught) {
      setError(loginErrorMessage(caught))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="screen screen--narrow">
      <h1>Sign in</h1>
      <p className="page-note">Sign in to plan dinners for your household.</p>

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <div className="form__field">
          <label htmlFor="login-username">Username</label>
          <input
            id="login-username"
            name="username"
            type="text"
            autoComplete="username"
            autoFocus
            required
            value={username}
            onChange={(event) => setUsername(event.target.value)}
          />
        </div>

        <div className="form__field">
          <label htmlFor="login-password">Password</label>
          <input
            id="login-password"
            name="password"
            type="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </div>

        {error && (
          <p className="form__error" role="alert">
            {error}
          </p>
        )}

        <button type="submit" disabled={submitting}>
          {submitting ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </div>
  )
}
