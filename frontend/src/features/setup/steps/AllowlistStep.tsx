import { Field } from '../../../components/Field'
import { fieldProps } from '../../../components/fieldProps'
import { validateAllowlistText } from '../../../utils/ipRanges'
import type { StepProps } from './types'

/** Optional IP allowlist with the explicit lockout warning (docs/SECURITY.md §5). */
export function AllowlistStep({ values, errors, onChange }: StepProps) {
  const parsed = validateAllowlistText(values.allowlistRangesText)
  const preview = parsed.ranges.slice(0, 8).join(', ')

  return (
    <div className="wizard__step">
      <p>
        The allowlist restricts Fate &amp; Fridge to specific networks. It applies to every route
        except health checks, and loopback is always allowed. Leave it off unless you need it.
      </p>

      <label className="form__choice">
        <input
          type="checkbox"
          checked={values.allowlistEnabled}
          aria-invalid={errors['ip_allowlist.ranges'] ? true : undefined}
          aria-describedby={errors['ip_allowlist.ranges'] ? 'allowlist-ranges-error' : undefined}
          onChange={(event) => onChange('allowlistEnabled', event.target.checked)}
        />
        <span>Enable the IP allowlist</span>
      </label>

      {values.allowlistEnabled && (
        <>
          <div className="notice notice--warning" role="note" aria-labelledby="allowlist-warning">
            <h3 id="allowlist-warning">A wrong range can lock out your household</h3>
            <ul>
              <li>
                Every device outside these ranges receives <strong>403</strong> — including this one
                if its address is missing.
              </li>
              <li>
                The server checks that the address of the device completing setup is covered before
                saving; otherwise setup fails and nothing is written.
              </li>
              <li>Keep loopback available for container health checks.</li>
            </ul>
          </div>

          <Field
            id="allowlist-ranges"
            label="Allowed addresses and ranges"
            error={errors['ip_allowlist.ranges']}
            hint="One IPv4/IPv6 address or CIDR per line, for example 192.168.1.0/24 or 2001:db8::/32."
          >
            <textarea
              rows={5}
              spellCheck={false}
              placeholder={'192.168.1.0/24\n10.0.0.1'}
              {...fieldProps('allowlist-ranges', {
                error: errors['ip_allowlist.ranges'],
                hint: true,
              })}
              value={values.allowlistRangesText}
              onChange={(event) => onChange('allowlistRangesText', event.target.value)}
            />
          </Field>

          {parsed.errors.length > 0 && (
            <ul className="form__error-list" aria-label="Allowlist range problems">
              {parsed.errors.map((message) => (
                <li key={message}>{message}</li>
              ))}
            </ul>
          )}

          {parsed.errors.length === 0 && parsed.ranges.length > 0 && (
            <p className="page-note">
              Ready to save {parsed.ranges.length} range{parsed.ranges.length === 1 ? '' : 's'}:{' '}
              {preview}
              {parsed.ranges.length > 8 ? ` …` : ''}
            </p>
          )}
        </>
      )}

      <fieldset className="form__fieldset">
        <legend>Update checks</legend>
        <label className="form__choice">
          <input
            type="checkbox"
            checked={values.updateCheckEnabled}
            aria-invalid={errors['update_check_enabled'] ? true : undefined}
            aria-describedby={errors['update_check_enabled'] ? 'update-check-hint' : undefined}
            onChange={(event) => onChange('updateCheckEnabled', event.target.checked)}
          />
          <span>Check for new releases automatically</span>
        </label>
        <p className="form__hint" id="update-check-hint">
          Fate &amp; Fridge can look for new versions. There is no telemetry; turning this off
          disables all outbound update requests.
        </p>
      </fieldset>

      <p className="page-note">You can change the allowlist later in Settings → Security.</p>
    </div>
  )
}
