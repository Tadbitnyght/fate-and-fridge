import { useEffect, useRef, useState, type FormEvent } from 'react'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { HouseholdSettings } from '../../api/household'
import { Field } from '../../components/Field'
import { fieldProps } from '../../components/fieldProps'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { STARTUP_PAGE_LABELS, WEEK_START_LABELS } from '../../utils/labels'
import { detectTimezoneOptions } from '../../utils/timezones'
import { useAdminAccess } from './access'
import {
  HOUSEHOLD_FORM_ERROR_KEYS,
  validateHouseholdForm,
  type HouseholdFormValues,
} from './householdValidation'
import { useHouseholdSettings, useUpdateHouseholdSettings } from './useHouseholdSettings'

function formValuesFrom(settings: HouseholdSettings): HouseholdFormValues {
  return {
    householdName: settings.household_name,
    timezone: settings.timezone,
    weekStartsOn: settings.week_starts_on,
    defaultDinerCount: String(settings.default_diner_count),
    startupPage: settings.startup_page,
  }
}

/** Household settings form (docs/API.md §21). Members see the values read-only. */
function HouseholdForm({
  initial,
  isAdmin,
  timezoneOptions,
}: {
  initial: HouseholdSettings
  isAdmin: boolean
  timezoneOptions: string[] | null
}) {
  const updateSettings = useUpdateHouseholdSettings()
  const [values, setValues] = useState<HouseholdFormValues>(() => formValuesFrom(initial))
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const errorSummaryRef = useRef<HTMLDivElement>(null)
  const [focusToken, setFocusToken] = useState(0)

  useEffect(() => {
    if (focusToken > 0) {
      errorSummaryRef.current?.focus()
    }
  }, [focusToken])

  function handleChange<K extends keyof HouseholdFormValues>(
    field: K,
    value: HouseholdFormValues[K],
  ): void {
    setValues((current) => ({ ...current, [field]: value }))
    setSaved(false)
    setFormError(null)
    const keys = HOUSEHOLD_FORM_ERROR_KEYS[field]
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
    const clientErrors = validateHouseholdForm(values, timezoneOptions)
    if (Object.keys(clientErrors).length > 0) {
      setErrors(clientErrors)
      setFocusToken((token) => token + 1)
      return
    }

    try {
      await updateSettings.mutateAsync({
        household_name: values.householdName.trim(),
        timezone: values.timezone.trim(),
        week_starts_on: values.weekStartsOn,
        default_diner_count: Number(values.defaultDinerCount),
        startup_page: values.startupPage,
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
    <>
      {!isAdmin && (
        <div className="notice" role="note">
          <p>Only Admins can change household settings. The current values are shown below.</p>
        </div>
      )}

      {errorMessages.length > 0 && (
        <div className="wizard__errors" role="alert" tabIndex={-1} ref={errorSummaryRef}>
          <h4>There is a problem</h4>
          <ul>
            {errorMessages.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        </div>
      )}

      <form className="form" onSubmit={(event) => void handleSubmit(event)} noValidate>
        <fieldset className="form__fieldset" disabled={!isAdmin}>
          <legend className="sr-only">Household settings</legend>

          <Field
            id="settings-household-name"
            label="Household name"
            error={errors['household_name']}
          >
            <input
              type="text"
              autoComplete="organization"
              {...fieldProps('settings-household-name', { error: errors['household_name'] })}
              value={values.householdName}
              onChange={(event) => handleChange('householdName', event.target.value)}
            />
          </Field>

          <Field
            id="settings-timezone"
            label="Timezone"
            error={errors['timezone']}
            hint={
              timezoneOptions
                ? 'Start typing to search IANA timezones.'
                : 'Enter an IANA timezone identifier.'
            }
          >
            <input
              type="text"
              list={timezoneOptions ? 'settings-timezone-options' : undefined}
              autoComplete="off"
              spellCheck={false}
              {...fieldProps('settings-timezone', { error: errors['timezone'], hint: true })}
              value={values.timezone}
              onChange={(event) => handleChange('timezone', event.target.value)}
            />
            {timezoneOptions && (
              <datalist id="settings-timezone-options">
                {timezoneOptions.map((zone) => (
                  <option key={zone} value={zone} />
                ))}
              </datalist>
            )}
          </Field>

          <Field id="settings-week-start" label="Week starts on" error={errors['week_starts_on']}>
            <select
              {...fieldProps('settings-week-start', { error: errors['week_starts_on'] })}
              value={values.weekStartsOn}
              onChange={(event) => handleChange('weekStartsOn', Number(event.target.value))}
            >
              {WEEK_START_LABELS.map((label, index) => (
                <option key={label} value={index}>
                  {label}
                </option>
              ))}
            </select>
          </Field>

          <Field
            id="settings-diner-count"
            label="Default diners"
            error={errors['default_diner_count']}
            hint="How many people usually eat dinner (1–20)."
          >
            <input
              type="number"
              min={1}
              max={20}
              step={1}
              inputMode="numeric"
              {...fieldProps('settings-diner-count', {
                error: errors['default_diner_count'],
                hint: true,
              })}
              value={values.defaultDinerCount}
              onChange={(event) => handleChange('defaultDinerCount', event.target.value)}
            />
          </Field>

          <fieldset className="form__fieldset">
            <legend>Startup page</legend>
            {(Object.keys(STARTUP_PAGE_LABELS) as Array<keyof typeof STARTUP_PAGE_LABELS>).map(
              (page) => (
                <label className="form__choice" key={page}>
                  <input
                    type="radio"
                    name="settings-startup-page"
                    value={page}
                    checked={values.startupPage === page}
                    aria-invalid={errors['startup_page'] ? true : undefined}
                    aria-describedby={
                      errors['startup_page'] ? 'settings-startup-page-error' : undefined
                    }
                    onChange={() => handleChange('startupPage', page)}
                  />
                  <span>{STARTUP_PAGE_LABELS[page]}</span>
                </label>
              ),
            )}
            {errors['startup_page'] && (
              <p className="form__error" id="settings-startup-page-error">
                {errors['startup_page']}
              </p>
            )}
          </fieldset>
        </fieldset>

        {isAdmin && (
          <div className="settings-actions">
            <button type="submit" className="button--primary" disabled={updateSettings.isPending}>
              {updateSettings.isPending ? 'Saving…' : 'Save household settings'}
            </button>
            {saved && (
              <p className="form__status" role="status">
                Household settings saved.
              </p>
            )}
          </div>
        )}
      </form>
    </>
  )
}

/**
 * Household settings (docs/API.md §21): any authenticated actor may read,
 * only Admins may change. Members see the values read-only.
 */
export function HouseholdSection() {
  const { isAdmin } = useAdminAccess()
  const [timezoneOptions] = useState<string[] | null>(() => detectTimezoneOptions())
  const settingsQuery = useHouseholdSettings()

  if (settingsQuery.isPending) {
    return <LoadingScreen label="Loading household settings…" />
  }

  if (settingsQuery.isError) {
    return (
      <ErrorScreen
        title="Cannot load household settings"
        error={settingsQuery.error}
        onRetry={() => void settingsQuery.refetch()}
      />
    )
  }

  return (
    <section className="settings-section" aria-labelledby="household-heading">
      <h3 id="household-heading">Household</h3>
      <HouseholdForm
        initial={settingsQuery.data}
        isAdmin={isAdmin}
        timezoneOptions={timezoneOptions}
      />
    </section>
  )
}
