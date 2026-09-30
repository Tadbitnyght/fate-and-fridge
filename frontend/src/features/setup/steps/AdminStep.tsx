import { Field } from '../Field'
import { fieldProps } from '../fieldProps'
import type { StepProps } from './types'

export function AdminStep({ values, errors, onChange }: StepProps) {
  return (
    <div className="wizard__step">
      <p>
        The Admin account manages household settings, users, and permissions. An Admin always has
        every permission.
      </p>

      <Field
        id="admin-username"
        label="Admin username"
        error={errors['admin.username']}
        hint="3–32 characters: letters, digits, “.”, “_” or “-”."
      >
        <input
          type="text"
          autoComplete="username"
          spellCheck={false}
          {...fieldProps('admin-username', { error: errors['admin.username'], hint: true })}
          value={values.adminUsername}
          onChange={(event) => onChange('adminUsername', event.target.value)}
        />
      </Field>

      <Field
        id="admin-password"
        label="Admin password"
        error={errors['admin.password']}
        hint="12–128 characters. Use a passphrase you do not reuse elsewhere."
      >
        <input
          type="password"
          autoComplete="new-password"
          {...fieldProps('admin-password', { error: errors['admin.password'], hint: true })}
          value={values.adminPassword}
          onChange={(event) => onChange('adminPassword', event.target.value)}
        />
      </Field>

      <Field
        id="admin-password-confirm"
        label="Confirm admin password"
        error={errors['admin.password_confirm']}
      >
        <input
          type="password"
          autoComplete="new-password"
          {...fieldProps('admin-password-confirm', { error: errors['admin.password_confirm'] })}
          value={values.adminPasswordConfirm}
          onChange={(event) => onChange('adminPasswordConfirm', event.target.value)}
        />
      </Field>
    </div>
  )
}
