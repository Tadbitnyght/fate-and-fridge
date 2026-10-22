import type { JSX } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { UpdateStatus } from '../../api/updates'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { formatTimestamp } from '../../lib/dateFormat'
import { useAdminAccess } from './access'
import { useAbout } from './useAbout'
import { useCheckForUpdates, useSetUpdateChecksEnabled, useUpdates } from './useUpdates'

/**
 * Settings → About (docs/M13_PLAN.md §1.12–§1.14; docs/API.md §24, §25).
 * Visible to every authenticated actor — there is no Admin gate and no
 * permission key for reading the versions or the update status.
 *
 * The update status read is shared with the shell notice. **Check now** is
 * shown to everyone because the server decides who may force a check: a member
 * gets the server's `403` and this section renders it readably. The
 * enabled/disabled preference is Admin-only in the UI, while the server remains
 * the authority. Nothing here updates the application.
 */

/** A quiet, readable message for a failed manual check (never a page error). */
function checkFailureMessage(error: unknown): string {
  if (isApiError(error) && (error.status === 403 || error.code === ErrorCodes.Forbidden)) {
    return 'Only an Admin can force an update check. Ask an Admin to run it, or wait for the automatic check.'
  }
  if (isApiError(error)) {
    return error.message.length > 0
      ? `The update check could not run: ${error.message}`
      : 'The update check could not run. Check that the server is reachable and try again.'
  }
  return 'The update check request could not reach the server. Check that it is reachable and try again.'
}

/** A quiet, readable message for a failed preference change. */
function preferenceFailureMessage(error: unknown): string {
  if (isApiError(error) && (error.status === 403 || error.code === ErrorCodes.Forbidden)) {
    return 'Only an Admin can change update-check settings.'
  }
  if (isApiError(error) && error.message.length > 0) {
    return `The update-check setting was not saved: ${error.message}`
  }
  return 'The update-check setting could not be saved. Check that the server is reachable and try again.'
}

/** `latest_version` with the leading `v` stripped by the server, or Unknown. */
function latestVersionLabel(status: UpdateStatus): string {
  return status.latest_version ?? 'Unknown'
}

/** The single, textual update state — never conveyed by colour alone. */
function updateStateLabel(status: UpdateStatus): string {
  if (!status.enabled) {
    return 'Update checks are off'
  }
  return status.update_available ? 'Update available' : 'Up to date'
}

function UpdateStatusPanel({ status }: { status: UpdateStatus }): JSX.Element {
  const { isAdmin } = useAdminAccess()
  const checkMutation = useCheckForUpdates()
  const preferenceMutation = useSetUpdateChecksEnabled()

  // Disabled means the server makes no outbound call at all, so a forced check
  // would silently return the same status. Say that instead of no-oping.
  const checkDisabled = !status.enabled || checkMutation.isPending
  const preferencePending = preferenceMutation.isPending

  // A 200 answer can still carry a recorded `last_error`: the check ran, but the
  // feed failed. Claiming success then would contradict the failure note, so the
  // success line is only shown when neither answer recorded an error.
  const checkCompletedCleanly =
    checkMutation.isSuccess &&
    !checkMutation.isPending &&
    checkMutation.data?.last_error === null &&
    status.last_error === null

  return (
    <div
      className="settings-subsection about-updates"
      aria-labelledby="about-updates-heading"
      data-testid="about-update-status"
    >
      <h4 id="about-updates-heading">Update status</h4>

      <dl className="review" aria-label="Update status">
        <div>
          <dt>Current version</dt>
          <dd data-testid="about-current-version">{status.current_version}</dd>
        </div>
        <div>
          <dt>Latest release</dt>
          <dd data-testid="about-latest-version">{latestVersionLabel(status)}</dd>
        </div>
        <div>
          <dt>State</dt>
          <dd data-testid="about-update-state">{updateStateLabel(status)}</dd>
        </div>
        <div>
          <dt>Last checked</dt>
          <dd data-testid="about-last-checked">
            {status.last_checked_at === null ? (
              'Never checked'
            ) : (
              <time dateTime={status.last_checked_at}>
                {formatTimestamp(status.last_checked_at)}
              </time>
            )}
          </dd>
        </div>
      </dl>

      {!status.enabled && (
        <p
          className="page-note"
          role="note"
          id="about-updates-disabled"
          data-testid="about-updates-disabled"
        >
          Update checks are turned off, so nothing is sent to the release feed. Turn them on to see
          the latest published version; Check now does nothing while they are off.
        </p>
      )}

      {status.enabled && status.latest_version === null && (
        <p className="page-note" role="note" data-testid="about-no-release">
          No published release has been found yet. Prereleases and drafts are ignored.
        </p>
      )}

      {/*
        A failed check is not an application error: the server keeps the last
        known release and records a short note here.
      */}
      {status.last_error !== null && (
        <p className="page-note" role="note" data-testid="about-update-error">
          The last check did not complete: {status.last_error}
        </p>
      )}

      <div className="settings-actions">
        <button
          type="button"
          onClick={() => checkMutation.mutate()}
          disabled={checkDisabled}
          aria-describedby={!status.enabled ? 'about-updates-disabled' : undefined}
        >
          {checkMutation.isPending ? 'Checking…' : 'Check now'}
        </button>
        {checkCompletedCleanly && (
          <p className="form__status" role="status">
            Checked with the release feed.
          </p>
        )}
      </div>

      {checkMutation.isError && (
        <p className="form__error" role="alert">
          {checkFailureMessage(checkMutation.error)}
        </p>
      )}

      {/*
        The toggle is Admin-only in the UI; the server rejects a member's PATCH
        regardless. A member therefore never sees a control they cannot use.
      */}
      {isAdmin && (
        <div className="about-updates__preference">
          <label className="form__choice">
            <input
              type="checkbox"
              checked={status.enabled}
              disabled={preferencePending}
              onChange={(event) => preferenceMutation.mutate(event.target.checked)}
            />
            <span>Enable update checks</span>
          </label>
          <p className="page-note">
            The only outbound request is a bodiless <code>GET</code> for the public release
            metadata, with a neutral <code>FateAndFridge/&lt;version&gt;</code> user agent. No
            household data or identifiers are sent, and nothing is sent while this is off.
          </p>
          {preferenceMutation.isError && (
            <p className="form__error" role="alert">
              {preferenceFailureMessage(preferenceMutation.error)}
            </p>
          )}
        </div>
      )}

      <p className="page-note">
        Fate &amp; Fridge never updates itself. A newer release is applied by rebuilding and
        redeploying this deployment.
      </p>
    </div>
  )
}

export function AboutSection(): JSX.Element {
  const aboutQuery = useAbout()
  const updatesQuery = useUpdates()

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

      {updatesQuery.isPending && (
        <p className="page-note" role="status" data-testid="about-update-loading">
          Loading update status…
        </p>
      )}

      {updatesQuery.isError && (
        <div className="settings-subsection about-updates" aria-labelledby="about-updates-heading">
          <h4 id="about-updates-heading">Update status</h4>
          <p className="page-note" role="note" data-testid="about-update-unavailable">
            The update status is unavailable right now. The versions above are still accurate.
          </p>
          <button type="button" onClick={() => void updatesQuery.refetch()}>
            Reload update status
          </button>
        </div>
      )}

      {updatesQuery.data && <UpdateStatusPanel status={updatesQuery.data} />}
    </section>
  )
}
