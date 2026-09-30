import { ErrorScreen, LoadingScreen } from '../components/Screens'
import { SetupWizard } from '../features/setup/SetupWizard'
import { useSetupStatus } from '../features/setup/useSetupStatus'

/**
 * Setup route: loads the public setup status, then hands off to the M1-13
 * wizard. The wizard owns the "already completed" redirect so the one-time
 * recovery key screen survives status refreshes.
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

  return <SetupWizard />
}
