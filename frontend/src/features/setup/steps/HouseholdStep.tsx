import { Field } from '../../../components/Field'
import { fieldProps } from '../../../components/fieldProps'
import { STARTUP_PAGE_LABELS, WEEK_START_LABELS } from '../../../utils/labels'
import type { StepProps } from './types'

export function HouseholdStep({ values, errors, timezoneOptions, onChange }: StepProps) {
  return (
    <div className="wizard__step">
      <Field
        id="household-name"
        label="Household name"
        error={errors['household_name']}
        hint="Shown in the app header, for example “The Smiths”."
      >
        <input
          type="text"
          maxLength={120}
          autoComplete="organization"
          {...fieldProps('household-name', { error: errors['household_name'], hint: true })}
          value={values.householdName}
          onChange={(event) => onChange('householdName', event.target.value)}
        />
      </Field>

      <Field
        id="timezone"
        label="Timezone"
        error={errors['timezone']}
        hint={
          timezoneOptions
            ? 'Start typing to search IANA timezones, for example America/Chicago.'
            : 'Enter an IANA timezone identifier, for example America/Chicago.'
        }
      >
        <input
          type="text"
          list={timezoneOptions ? 'timezone-options' : undefined}
          autoComplete="off"
          spellCheck={false}
          {...fieldProps('timezone', { error: errors['timezone'], hint: true })}
          value={values.timezone}
          onChange={(event) => onChange('timezone', event.target.value)}
        />
        {timezoneOptions && (
          <datalist id="timezone-options">
            {timezoneOptions.map((zone) => (
              <option key={zone} value={zone} />
            ))}
          </datalist>
        )}
      </Field>

      <Field
        id="week-starts-on"
        label="Week starts on"
        error={errors['week_starts_on']}
        hint="Used by the calendar grid."
      >
        <select
          {...fieldProps('week-starts-on', { error: errors['week_starts_on'], hint: true })}
          value={values.weekStartsOn}
          onChange={(event) => onChange('weekStartsOn', Number(event.target.value))}
        >
          {WEEK_START_LABELS.map((label, index) => (
            <option key={label} value={index}>
              {label}
            </option>
          ))}
        </select>
      </Field>

      <Field
        id="default-diner-count"
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
          {...fieldProps('default-diner-count', {
            error: errors['default_diner_count'],
            hint: true,
          })}
          value={values.defaultDinerCount}
          onChange={(event) => onChange('defaultDinerCount', event.target.value)}
        />
      </Field>

      <fieldset className="form__fieldset">
        <legend>Startup page</legend>
        {(Object.keys(STARTUP_PAGE_LABELS) as Array<keyof typeof STARTUP_PAGE_LABELS>).map(
          (page) => (
            <label className="form__choice" key={page}>
              <input
                type="radio"
                name="startup-page"
                value={page}
                checked={values.startupPage === page}
                aria-invalid={errors['startup_page'] ? true : undefined}
                aria-describedby={errors['startup_page'] ? 'startup-page-error' : undefined}
                onChange={() => onChange('startupPage', page)}
              />
              <span>{STARTUP_PAGE_LABELS[page]}</span>
            </label>
          ),
        )}
        {errors['startup_page'] && (
          <p className="form__error" id="startup-page-error">
            {errors['startup_page']}
          </p>
        )}
      </fieldset>
    </div>
  )
}
