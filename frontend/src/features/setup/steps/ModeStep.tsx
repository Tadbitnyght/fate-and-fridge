import { Link } from 'react-router-dom'

import type { StepProps } from './types'

/**
 * Welcome + installation mode. Single-user mode carries the explicit
 * trusted-network warning required by docs/SECURITY.md §5.
 */
export function ModeStep({ values, errors, onChange }: StepProps) {
  return (
    <div className="wizard__step">
      <p>
        Fate &amp; Fridge keeps dinner planning, recipes, and groceries in one place. A few quick
        choices and your household is ready.
      </p>

      <fieldset className="form__fieldset">
        <legend>Installation mode</legend>

        <label className="form__choice">
          <input
            type="radio"
            name="auth-mode"
            value="single_user"
            checked={values.authMode === 'single_user'}
            aria-invalid={errors['auth_mode'] ? true : undefined}
            aria-describedby={errors['auth_mode'] ? 'auth-mode-error' : undefined}
            onChange={() => onChange('authMode', 'single_user')}
          />
          <span>
            <strong>Single user</strong> — no sign-in. Anyone who can reach the server can use it.
          </span>
        </label>

        <label className="form__choice">
          <input
            type="radio"
            name="auth-mode"
            value="multiuser"
            checked={values.authMode === 'multiuser'}
            aria-invalid={errors['auth_mode'] ? true : undefined}
            aria-describedby={errors['auth_mode'] ? 'auth-mode-error' : undefined}
            onChange={() => onChange('authMode', 'multiuser')}
          />
          <span>
            <strong>Multiuser</strong> — Admin and Member accounts with sign-in.
          </span>
        </label>

        {errors['auth_mode'] && (
          <p className="form__error" id="auth-mode-error">
            {errors['auth_mode']}
          </p>
        )}
      </fieldset>

      {values.authMode === 'single_user' && (
        <div className="notice notice--warning" role="note" aria-labelledby="single-user-warning">
          <h3 id="single-user-warning">Before you choose single user</h3>
          <ul>
            <li>Use single-user mode only on a trusted private network.</li>
            <li>
              Do not expose this instance directly to the internet — there is no sign-in to stop
              visitors.
            </li>
            <li>
              If the network is not fully trusted, enable the IP allowlist in a later step or
              protect the server externally.
            </li>
            <li>You can switch to multiuser later from Settings.</li>
          </ul>
        </div>
      )}

      <p className="page-note">
        {values.authMode === 'multiuser'
          ? 'Sign-in will be required; the first Admin account is created in a later step.'
          : 'No sign-in will be required; the network boundary is the only protection.'}
      </p>

      <p className="page-note">
        Moving from another device or reinstalling?{' '}
        <Link to="/setup/restore">Restore from backup</Link> instead of starting fresh.
      </p>
    </div>
  )
}
