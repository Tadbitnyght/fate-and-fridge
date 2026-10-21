import type { JSX } from 'react'

import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { useAbout } from './useAbout'

/**
 * Settings → About (docs/M13_PLAN.md §1.12; docs/API.md §24). Visible to every
 * authenticated actor — there is no Admin gate and no permission key.
 *
 * The update status is a deliberate placeholder: M13-09 fills it with the
 * cached GitHub release check. This section only owns the seam, so nothing is
 * fetched from the network here.
 */
export function AboutSection(): JSX.Element {
  const aboutQuery = useAbout()

  if (aboutQuery.isPending) {
    return <LoadingScreen label="Loading version information…" />
  }

  if (aboutQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load version information"
        error={aboutQuery.error}
        onRetry={() => void aboutQuery.refetch()}
      />
    )
  }

  const { app_version: appVersion, schema_version: schemaVersion } = aboutQuery.data

  return (
    <section className="settings-section" aria-labelledby="about-heading">
      <h3 id="about-heading">About</h3>

      <dl className="review" aria-label="Version information">
        <div>
          <dt>Application version</dt>
          <dd data-testid="about-app-version">{appVersion}</dd>
        </div>
        <div>
          <dt>Schema version</dt>
          <dd data-testid="about-schema-version">{schemaVersion ?? 'Unavailable'}</dd>
        </div>
      </dl>

      {schemaVersion === null && (
        <p className="page-note" role="note">
          The server could not read its database schema version. The application version above is
          still accurate.
        </p>
      )}

      <div
        className="settings-subsection about-updates"
        aria-labelledby="about-updates-heading"
        data-testid="about-update-placeholder"
      >
        <h4 id="about-updates-heading">Update status</h4>
        <p className="page-note">
          Not implemented yet. Update checking — the latest release, its date, and the
          enabled/disabled preference — arrives in M13-09.
        </p>
      </div>
    </section>
  )
}
