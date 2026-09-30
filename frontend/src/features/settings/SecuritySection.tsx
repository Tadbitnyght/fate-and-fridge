import { useEffect, useRef, useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { SecuritySettings } from '../../api/security'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { validateAllowlistText } from '../../utils/ipRanges'
import { validateAdminPassword, validateAdminUsername } from '../../utils/validation'
import { useAdminAccess } from './access'
import { AdminOnlyNotice } from './AdminOnlyNotice'
import {
  useSecuritySettings,
  useSwitchToMultiuser,
  useUpdateSecuritySettings,
} from './useSecuritySettings'

interface SecurityFormValues {
  enabled: boolean
  rangesText: string
  proxiesText: string
  confirmed: boolean
}

function formValuesFrom(settings: SecuritySettings): SecurityFormValues {
  return {
    enabled: settings.ip_allowlist_enabled,
    rangesText: settings.ip_allowlist_ranges.join('\n'),
    proxiesText: settings.trusted_proxies.join('\n'),
    confirmed: settings.ip_allowlist_enabled,
  }
}

const SECURITY_FIELD_ERROR_KEYS: Record<keyof SecurityFormValues, string[]> = {
  enabled: ['ip_allowlist_enabled', 'confirmed', 'ip_allowlist_ranges'],
  rangesText: ['ip_allowlist_ranges'],
  proxiesText: ['trusted_proxies'],
  confirmed: ['confirmed'],
}

function SwitchToMultiuserForm() {
  const switchMutation = useSwitchToMultiuser()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)

  function clearError(field: string): void {
    setErrors((current) => {
      if (!(field in current)) {
        return current
      }
      const next = { ...current }
      delete next[field]
      return next
    })
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setFormError(null)
    const clientErrors: FieldErrors = {}
    const usernameError = validateAdminUsername(username)
    if (usernameError) {
      clientErrors['admin.username'] = usernameError
    }
    const passwordError = validateAdminPassword(password)
    if (passwordError) {
      clientErrors['admin.password'] = passwordError
    }
    if (confirmPassword !== password) {
      clientErrors['admin.password_confirm'] = 'The passwords do not match.'
    }
    if (Object.keys(clientErrors).length > 0) {
      setErrors(clientErrors)
      return
    }

    try {
      await switchMutation.mutateAsync({ username: username.trim(), password })
      setErrors({})
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          return
        }
      }
      setErrors({})
      setFormError(
        isApiError(error)
          ? error.message
          : 'Switching failed. Check that the server is reachable and try again.',
      )
    }
  }

  return (
    <div className="notice" role="note">
      <h4>Switch to multiuser</h4>
      <p>
        Create the first Admin account. Single-user access ends immediately and everyone must sign
        in afterwards. Downgrading back to single-user is not supported.
      </p>

      {formError && (
        <p className="form__error" role="alert">
          {formError}
        </p>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <Field id="switch-username" label="Admin username" error={errors['admin.username']}>
          <input
            type="text"
            autoComplete="username"
            spellCheck={false}
            {...fieldProps('switch-username', { error: errors['admin.username'] })}
            value={username}
            onChange={(event) => {
              setUsername(event.target.value)
              clearError('admin.username')
            }}
          />
        </Field>

        <Field id="switch-password" label="Admin password" error={errors['admin.password']}>
          <input
            type="password"
            autoComplete="new-password"
            {...fieldProps('switch-password', { error: errors['admin.password'] })}
            value={password}
            onChange={(event) => {
              setPassword(event.target.value)
              clearError('admin.password')
            }}
          />
        </Field>

        <Field
          id="switch-password-confirm"
          label="Confirm admin password"
          error={errors['admin.password_confirm']}
        >
          <input
            type="password"
            autoComplete="new-password"
            {...fieldProps('switch-password-confirm', { error: errors['admin.password_confirm'] })}
            value={confirmPassword}
            onChange={(event) => {
              setConfirmPassword(event.target.value)
              clearError('admin.password_confirm')
            }}
          />
        </Field>

        <button type="submit" className="button--primary" disabled={switchMutation.isPending}>
          {switchMutation.isPending ? 'Switching…' : 'Switch to multiuser'}
        </button>
      </form>
    </div>
  )
}

function SecurityForm({ initial }: { initial: SecuritySettings }) {
  const updateSettings = useUpdateSecuritySettings()
  const [values, setValues] = useState<SecurityFormValues>(() => formValuesFrom(initial))
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const errorSummaryRef = useRef<HTMLDivElement>(null)
  const [focusToken, setFocusToken] = useState(0)
  // Enabling the allowlist for the first time requires the explicit confirmation.
  const needsConfirmation = !initial.ip_allowlist_enabled

  useEffect(() => {
    if (focusToken > 0) {
      errorSummaryRef.current?.focus()
    }
  }, [focusToken])

  const ranges = validateAllowlistText(values.rangesText)
  const proxies = validateAllowlistText(values.proxiesText)

  function handleChange<K extends keyof SecurityFormValues>(
    field: K,
    value: SecurityFormValues[K],
  ): void {
    setValues((current) => ({ ...current, [field]: value }))
    setSaved(false)
    setFormError(null)
    const keys = SECURITY_FIELD_ERROR_KEYS[field]
    setErrors((current) => {
      const next: FieldErrors = {}
      for (const [key, message] of Object.entries(current)) {
        if (!keys.includes(key)) {
          next[key] = message
        }
      }
      return next
    })
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault()
    setSaved(false)
    setFormError(null)

    const clientErrors: FieldErrors = {}
    if (ranges.errors.length > 0) {
      clientErrors['ip_allowlist_ranges'] = ranges.errors.join(' ')
    } else if (values.enabled && ranges.ranges.length === 0) {
      clientErrors['ip_allowlist_ranges'] = 'Add at least one range when the allowlist is enabled.'
    }
    if (proxies.errors.length > 0) {
      clientErrors['trusted_proxies'] = proxies.errors.join(' ')
    }
    if (values.enabled && needsConfirmation && !values.confirmed) {
      clientErrors['confirmed'] =
        'Confirm that you understand the lockout risk before enabling the allowlist.'
    }

    if (Object.keys(clientErrors).length > 0) {
      setErrors(clientErrors)
      setFocusToken((token) => token + 1)
      return
    }

    try {
      await updateSettings.mutateAsync({
        ip_allowlist_enabled: values.enabled,
        ip_allowlist_ranges: ranges.ranges,
        trusted_proxies: proxies.ranges,
        ...(values.enabled ? { confirmed: values.confirmed } : {}),
      })
      setErrors({})
      setSaved(true)
    } catch (error) {
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          setFocusToken((token) => token + 1)
          return
        }
      }
      setErrors({})
      setFormError(
        isApiError(error)
          ? error.message
          : 'Saving failed. Check that the server is reachable and try again.',
      )
      setFocusToken((token) => token + 1)
    }
  }

  const errorMessages = [...(formError !== null ? [formError] : []), ...Object.values(errors)]

  return (
    <div className="settings-subsection">
      <h4>IP allowlist and trusted proxies</h4>
      <p className="page-note">
        The allowlist limits every route except health checks to the listed networks; loopback is
        always allowed. Trusted proxies are the only peers whose forwarded headers are honored.
      </p>

      {errorMessages.length > 0 && (
        <div className="wizard__errors" role="alert" tabIndex={-1} ref={errorSummaryRef}>
          <h5>There is a problem</h5>
          <ul>
            {errorMessages.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        </div>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <label className="form__choice">
          <input
            type="checkbox"
            checked={values.enabled}
            aria-describedby={errors['ip_allowlist_ranges'] ? 'security-ranges-error' : undefined}
            onChange={(event) => handleChange('enabled', event.target.checked)}
          />
          <span>Enable the IP allowlist</span>
        </label>

        {values.enabled && (
          <div className="notice notice--warning" role="note" aria-labelledby="security-warning">
            <h5 id="security-warning">A wrong range can lock out your household</h5>
            <ul>
              <li>
                Every device outside these ranges receives <strong>403</strong> — including this one
                if its address is missing.
              </li>
              <li>The server verifies that this session's address is covered before saving.</li>
              <li>Keep loopback available for container health checks.</li>
            </ul>
          </div>
        )}

        {values.enabled && needsConfirmation && (
          <label className="form__choice">
            <input
              type="checkbox"
              checked={values.confirmed}
              aria-invalid={errors['confirmed'] ? true : undefined}
              aria-describedby={errors['confirmed'] ? 'security-confirmed-error' : undefined}
              onChange={(event) => handleChange('confirmed', event.target.checked)}
            />
            <span>
              I understand that a misconfigured allowlist can lock every device out of Fate &amp;
              Fridge.
            </span>
          </label>
        )}
        {errors['confirmed'] && (
          <p className="form__error" id="security-confirmed-error">
            {errors['confirmed']}
          </p>
        )}

        <Field
          id="security-ranges"
          label="Allowed addresses and ranges"
          error={errors['ip_allowlist_ranges']}
          hint="One IPv4/IPv6 address or CIDR per line."
        >
          <textarea
            rows={5}
            spellCheck={false}
            placeholder={'192.168.1.0/24\n10.0.0.1'}
            {...fieldProps('security-ranges', {
              error: errors['ip_allowlist_ranges'],
              hint: true,
            })}
            value={values.rangesText}
            onChange={(event) => handleChange('rangesText', event.target.value)}
          />
        </Field>

        {ranges.errors.length > 0 && (
          <ul className="form__error-list" aria-label="Allowlist range problems">
            {ranges.errors.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        )}

        <Field
          id="security-proxies"
          label="Trusted proxies"
          error={errors['trusted_proxies']}
          hint="One IPv4/IPv6 address or CIDR per line. Forwarded headers from other peers are ignored."
        >
          <textarea
            rows={3}
            spellCheck={false}
            {...fieldProps('security-proxies', {
              error: errors['trusted_proxies'],
              hint: true,
            })}
            value={values.proxiesText}
            onChange={(event) => handleChange('proxiesText', event.target.value)}
          />
        </Field>

        {proxies.errors.length > 0 && (
          <ul className="form__error-list" aria-label="Trusted proxy problems">
            {proxies.errors.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        )}

        <div className="settings-actions">
          <button type="submit" className="button--primary" disabled={updateSettings.isPending}>
            {updateSettings.isPending ? 'Saving…' : 'Save security settings'}
          </button>
          {saved && (
            <p className="form__status" role="status">
              Security settings saved.
            </p>
          )}
        </div>
      </form>
    </div>
  )
}

/** Security settings (docs/API.md §21) and single-user → multiuser conversion. */
export function SecuritySection() {
  const { isAdmin, isSingleUser } = useAdminAccess()
  const settingsQuery = useSecuritySettings(isAdmin)

  if (!isAdmin) {
    return (
      <section className="settings-section" aria-labelledby="security-heading">
        <h3 id="security-heading">Security</h3>
        <AdminOnlyNotice feature="Security settings" />
      </section>
    )
  }

  return (
    <section className="settings-section" aria-labelledby="security-heading">
      <h3 id="security-heading">Security</h3>

      {isSingleUser ? (
        <SwitchToMultiuserForm />
      ) : (
        <div className="notice" role="note">
          <p>
            This household uses multiuser mode. Downgrading to single-user is not supported; the
            first Admin account was created during setup.
          </p>
        </div>
      )}

      {settingsQuery.isPending && <LoadingScreen label="Loading security settings…" />}
      {settingsQuery.isError && (
        <ErrorScreen
          title="Cannot load security settings"
          error={settingsQuery.error}
          onRetry={() => void settingsQuery.refetch()}
        />
      )}
      {settingsQuery.isSuccess && <SecurityForm initial={settingsQuery.data} />}
    </section>
  )
}
