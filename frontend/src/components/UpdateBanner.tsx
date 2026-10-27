import { useState } from 'react'

import { useUpdates } from '../features/settings/useUpdates'
import { readDismissedVersion, rememberDismissedVersion, safeReleaseUrl } from '../lib/updateNotice'

/**
 * The dashboard notice for a newer release (docs/M13_PLAN.md §1.14). Rendered
 * by the shell above `<main>` for every actor; it only describes what is
 * available and never updates anything. The text carries the meaning, so no
 * colour-only signal is involved, and the dismiss button has its own label.
 *
 * Dismissal is remembered per device and keyed to the latest version, so a
 * newer release shows the banner again (see `lib/updateNotice`).
 */
export function UpdateBanner() {
  const updatesQuery = useUpdates()
  const [dismissedVersion, setDismissedVersion] = useState<string | null>(readDismissedVersion)

  const status = updatesQuery.data
  const latestVersion = status?.latest_version ?? null

  if (status === undefined || !status.update_available || latestVersion === null) {
    return null
  }

  if (dismissedVersion === latestVersion) {
    return null
  }

  // Narrowed once so the dismiss handler keeps the plain string type.
  const version: string = latestVersion
  const releaseUrl = safeReleaseUrl(status.release_url)

  function handleDismiss(): void {
    rememberDismissedVersion(version)
    setDismissedVersion(version)
  }

  return (
    <div
      className="update-banner"
      role="status"
      aria-labelledby="update-banner-heading"
      data-testid="update-banner"
    >
      <div className="update-banner__body">
        <h2 id="update-banner-heading" className="update-banner__title">
          A newer release is available: version {version}
        </h2>
        <p>
          This deployment is running version {status.current_version}. Updating is a manual rebuild
          and redeploy — Fate &amp; Fridge never updates itself.
        </p>
        {releaseUrl !== null && (
          <p>
            <a href={releaseUrl} target="_blank" rel="noreferrer">
              Read the {version} release notes
            </a>
          </p>
        )}
      </div>
      <button
        type="button"
        className="update-banner__dismiss"
        aria-label={`Dismiss update notice for version ${version}`}
        onClick={handleDismiss}
      >
        Dismiss
      </button>
    </div>
  )
}
