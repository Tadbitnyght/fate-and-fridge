import { Navigate, Outlet, useLocation } from 'react-router-dom'

import { useAuth } from '../features/auth/AuthContext'
import { useSetupStatus } from '../features/setup/useSetupStatus'
import { ErrorScreen, LoadingScreen } from './Screens'

/**
 * Routing/redirect rules (docs/PRODUCT_SPEC.md §5, docs/API.md §21)
 * setup incomplete → /setup; multiuser unauthenticated → /login
 * forced password change → /change-password; otherwise the shell renders.
 */
export function RequireSession() {
  const location = useLocation()
  const { status, session, error, refetchSession } = useAuth()
  const setup = useSetupStatus()
  const currentLocation = `${location.pathname}${location.search}${location.hash}`

  if (setup.isPending || status === 'loading') {
    return <LoadingScreen label="Loading Fate & Fridge…" />
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

  if (error) {
    return (
      <ErrorScreen
        title="Session unavailable"
        error={error}
        onRetry={() => void refetchSession()}
      />
    )
  }

  if (status === 'unauthenticated') {
    return <Navigate to="/login" replace state={{ from: currentLocation }} />
  }

  if (status === 'authenticated' && session?.must_change_password) {
    return <Navigate to="/change-password" replace state={{ from: currentLocation }} />
  }

  return <Outlet />
}
