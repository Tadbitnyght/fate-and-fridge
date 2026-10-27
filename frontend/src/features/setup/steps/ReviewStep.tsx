import { AUTH_MODE_LABELS, STARTUP_PAGE_LABELS, WEEK_START_LABELS } from '../../../utils/labels'
import { validateAllowlistText } from '../../../utils/ipRanges'
import type { StepProps } from './types'

/** Read-only summary of everything that will be sent to `POST /setup/complete`. */
export function ReviewStep({ values }: StepProps) {
  const { ranges } = validateAllowlistText(values.allowlistRangesText)

  return (
    <div className="wizard__step">
      <p>Check the details below. Setup can only be completed once.</p>

      <dl className="review">
        <div>
          <dt>Installation mode</dt>
          <dd>{values.authMode ? AUTH_MODE_LABELS[values.authMode] : '—'}</dd>
        </div>
        <div>
          <dt>Household name</dt>
          <dd>{values.householdName.trim() || '—'}</dd>
        </div>
        <div>
          <dt>Timezone</dt>
          <dd>{values.timezone.trim() || '—'}</dd>
        </div>
        <div>
          <dt>Week starts on</dt>
          <dd>{WEEK_START_LABELS[values.weekStartsOn] ?? '—'}</dd>
        </div>
        <div>
          <dt>Default diners</dt>
          <dd>{values.defaultDinerCount.trim() || '—'}</dd>
        </div>
        <div>
          <dt>Startup page</dt>
          <dd>{STARTUP_PAGE_LABELS[values.startupPage]}</dd>
        </div>

        {values.authMode === 'multiuser' && (
          <div>
            <dt>Admin username</dt>
            <dd>{values.adminUsername.trim() || '—'}</dd>
          </div>
        )}

        <div>
          <dt>IP allowlist</dt>
          <dd>
            {values.allowlistEnabled
              ? ranges.length > 0
                ? ranges.join(', ')
                : 'Enabled, no valid ranges yet'
              : 'Disabled'}
          </dd>
        </div>
        <div>
          <dt>Update checks</dt>
          <dd>{values.updateCheckEnabled ? 'Enabled' : 'Disabled'}</dd>
        </div>
      </dl>

      {values.authMode === 'multiuser' && (
        <p className="page-note">
          The Admin password is not shown here. It is sent once and stored hashed.
        </p>
      )}
    </div>
  )
}
