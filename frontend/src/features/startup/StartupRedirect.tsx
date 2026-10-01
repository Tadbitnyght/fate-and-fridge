import { useEffect, useState } from 'react'
import { Navigate } from 'react-router-dom'

import { LoadingScreen } from '../../components/Screens'
import { useAuth } from '../auth/AuthContext'
import { useHouseholdSettings } from '../settings/useHouseholdSettings'

const FALLBACK_PAGE = 'calendar'
const LOADING_TIMEOUT_MS = 4000

/**
 * The shell index route honors `household_settings.startup_page`
 * (docs/PRODUCT_SPEC.md §5): `/` redirects to `/calendar` or `/dashboard`.
 * On error or a slow response it falls back to `/calendar` so the app always
 * opens.
 */
export function StartupRedirect() {
  const { status } = useAuth()
  const canReadSettings = status === 'authenticated' || status === 'single_user'
  const settings = useHouseholdSettings(canReadSettings)
  const [timedOut, setTimedOut] = useState(false)

  useEffect(() => {
    if (settings.isSuccess || settings.isError) {
      return
    }

    const timer = setTimeout(() => setTimedOut(true), LOADING_TIMEOUT_MS)
    return () => clearTimeout(timer)
  }, [settings.isSuccess, settings.isError])

  const startupPage = settings.data?.startup_page
  if (startupPage) {
    return <Navigate to={`/${startupPage}`} replace />
  }

  if (settings.isError || timedOut) {
    return <Navigate to={`/${FALLBACK_PAGE}`} replace />
  }

  return <LoadingScreen label="Opening your household…" />
}
