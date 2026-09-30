import { Navigate } from 'react-router-dom'

import { ErrorScreen, LoadingScreen } from '../components/Screens'
import { useSetupStatus } from '../features/setup/useSetupStatus'

/**
 * Placeholder for the setup wizard (M1-13). Setup itself is implemented
 * server-side (`POST /api/v1/setup/complete`); this page only routes into the
 * flow and shows the current status.
 */
export function SetupPage() {
  const setup = useSetupStatus()

  if (setup.isPending) {
    return <LoadingScreen label="Checking setup status…" />
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

  if (setup.data.setup_completed) {
    return <Navigate to="/" replace />
  }

  return (
    <div className="screen screen--narrow">
      <h1>Set up Fate &amp; Fridge</h1>
      <p>This household has not been configured yet.</p>
      <dl className="setup-facts">
        <div>
          <dt>Auth mode</dt>
          <dd>{setup.data.auth_mode ?? 'Not chosen yet'}</dd>
        </div>
        <div>
          <dt>Recovery key</dt>
          <dd>{setup.data.recovery_key_present ? 'Present' : 'Not created yet'}</dd>
        </div>
      </dl>
      <p className="page-note">
        The guided setup wizard arrives with milestone M1-13 (auth mode, household, admin account,
        IP allowlist, and one-time recovery key display).
      </p>
    </div>
  )
}
