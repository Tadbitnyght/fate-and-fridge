import { useEffect, useRef, useState } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import type { CompleteSetupResponse } from '../../api/setup'
import { extractServerFieldErrors, type FieldErrors } from '../../utils/fieldErrors'
import { detectTimezoneOptions, getDefaultTimezone } from '../../utils/timezones'
import { AdminStep } from './steps/AdminStep'
import { AllowlistStep } from './steps/AllowlistStep'
import { HouseholdStep } from './steps/HouseholdStep'
import { ModeStep } from './steps/ModeStep'
import { RecoveryKeyStep } from './steps/RecoveryKeyStep'
import { ReviewStep } from './steps/ReviewStep'
import { useCompleteSetup } from './useCompleteSetup'
import { useSetupStatus } from './useSetupStatus'
import {
  buildRequest,
  clearFieldError,
  clearStepErrors,
  nextStep,
  previousStep,
  stepForFieldErrors,
  validateAll,
  validateStep,
  wizardSteps,
  type SetupFormValues,
  type SetupStep,
  type ValidationContext,
} from './validation'

const STEP_TITLES: Record<SetupStep, string> = {
  mode: 'Welcome to Fate & Fridge',
  household: 'Household',
  admin: 'Admin account',
  allowlist: 'Network access',
  review: 'Review and finish',
  recovery: 'Save your recovery key',
}

function createInitialValues(timezone: string): SetupFormValues {
  return {
    authMode: null,
    householdName: '',
    timezone,
    weekStartsOn: 0,
    defaultDinerCount: '4',
    startupPage: 'calendar',
    adminUsername: '',
    adminPassword: '',
    adminPasswordConfirm: '',
    allowlistEnabled: false,
    allowlistRangesText: '',
    updateCheckEnabled: true,
  }
}

/**
 * Setup wizard (M1-13): mode, household, admin account, optional IP allowlist,
 * review, and the one-time recovery key. Client validation mirrors the server;
 * server field errors are mapped back to the step that owns the field.
 */
export function SetupWizard() {
  const setup = useSetupStatus()
  const completeSetup = useCompleteSetup()
  const navigate = useNavigate()

  const [timezoneOptions] = useState<string[] | null>(() => detectTimezoneOptions())
  const [values, setValues] = useState<SetupFormValues>(() =>
    createInitialValues(getDefaultTimezone(timezoneOptions)),
  )
  const [step, setStep] = useState<SetupStep>('mode')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [result, setResult] = useState<CompleteSetupResponse | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)
  // True while a setup submission is in flight: the status query turns
  // "completed" during the mutation's own cache invalidation, and the redirect
  // must not fire before the one-time recovery key screen has rendered.
  const [submitted, setSubmitted] = useState(false)

  const headingRef = useRef<HTMLHeadingElement>(null)
  const errorSummaryRef = useRef<HTMLDivElement>(null)
  const context: ValidationContext = { timezoneOptions }
  const steps = wizardSteps(values.authMode)
  const hasErrors = Object.keys(errors).length > 0 || formError !== null

  // Focus moves only for explicit navigation/submission events — not when an
  // edit clears the last error, which would pull focus out of the field.
  const [focusRequest, setFocusRequest] = useState<{ target: 'heading' | 'summary' } | null>({
    target: 'heading',
  })

  useEffect(() => {
    if (focusRequest === null) {
      return
    }
    const node = focusRequest.target === 'summary' ? errorSummaryRef.current : headingRef.current
    node?.focus()
  }, [focusRequest])

  // A refresh (or a repeat submission) after setup completed must not block the
  // recovery key screen: only redirect while this wizard has no result to show
  // and no submission in flight.
  if (setup.isSuccess && setup.data.setup_completed && result === null && !submitted) {
    return <Navigate to="/" replace />
  }

  function handleChange<K extends keyof SetupFormValues>(
    field: K,
    value: SetupFormValues[K],
  ): void {
    setValues((current) => ({ ...current, [field]: value }))
    setFormError(null)
    setErrors((current) => clearFieldError(field, current))
  }

  function handleNext(): void {
    const stepErrors = validateStep(step, values, context)
    if (Object.keys(stepErrors).length > 0) {
      setErrors(stepErrors)
      setFocusRequest({ target: 'summary' })
      return
    }
    // Keep errors owned by other steps (for example server field errors) so
    // they are still visible when the user reaches that step.
    setErrors((current) => clearStepErrors(step, current))
    setFormError(null)
    setFocusRequest({ target: 'heading' })
    setStep(nextStep(steps, step))
  }

  function handleBack(): void {
    setFormError(null)
    setFocusRequest({ target: 'heading' })
    setStep(previousStep(steps, step))
  }

  async function handleSubmit(): Promise<void> {
    if (completeSetup.isPending) {
      return
    }
    setFormError(null)
    const validationErrors = validateAll(values, context)
    if (Object.keys(validationErrors).length > 0) {
      setErrors(validationErrors)
      setFocusRequest({ target: 'summary' })
      setStep(stepForFieldErrors(validationErrors))
      return
    }

    setSubmitted(true)
    try {
      const response = await completeSetup.mutateAsync(buildRequest(values))
      setErrors({})
      setResult(response)
      setFocusRequest({ target: 'heading' })
      setStep('recovery')
    } catch (error) {
      // The status query may already have flipped to "completed" (for example
      // the 409 path): let the redirect guard take over again.
      setSubmitted(false)
      if (isApiError(error) && error.code === ErrorCodes.SetupAlreadyCompleted) {
        // useCompleteSetup refreshes setup/session status; the guard above
        // redirects into the app or login flow.
        return
      }
      if (isApiError(error) && error.code === ErrorCodes.ValidationFailed) {
        const serverErrors = extractServerFieldErrors(error.details)
        if (Object.keys(serverErrors).length > 0) {
          setErrors(serverErrors)
          setFocusRequest({ target: 'summary' })
          setStep(stepForFieldErrors(serverErrors))
          return
        }
      }
      setErrors({})
      setFormError(
        isApiError(error)
          ? error.message
          : 'Setup could not be completed. Check that the server is reachable and try again.',
      )
      setFocusRequest({ target: 'summary' })
    }
  }

  if (step === 'recovery' && result !== null) {
    return (
      <div className="screen screen--narrow wizard">
        <h1 className="wizard__title" tabIndex={-1} ref={headingRef}>
          {STEP_TITLES.recovery}
        </h1>
        <RecoveryKeyStep
          recoveryKey={result.recovery_key}
          acknowledged={acknowledged}
          onAcknowledgedChange={setAcknowledged}
          onContinue={() => navigate('/', { replace: true })}
        />
      </div>
    )
  }

  const stepIndex = steps.indexOf(step)
  const errorMessages = [...(formError !== null ? [formError] : []), ...Object.values(errors)]

  function renderStep() {
    switch (step) {
      case 'mode':
        return (
          <ModeStep
            values={values}
            errors={errors}
            timezoneOptions={timezoneOptions}
            onChange={handleChange}
          />
        )
      case 'household':
        return (
          <HouseholdStep
            values={values}
            errors={errors}
            timezoneOptions={timezoneOptions}
            onChange={handleChange}
          />
        )
      case 'admin':
        return (
          <AdminStep
            values={values}
            errors={errors}
            timezoneOptions={timezoneOptions}
            onChange={handleChange}
          />
        )
      case 'allowlist':
        return (
          <AllowlistStep
            values={values}
            errors={errors}
            timezoneOptions={timezoneOptions}
            onChange={handleChange}
          />
        )
      case 'review':
        return (
          <ReviewStep
            values={values}
            errors={errors}
            timezoneOptions={timezoneOptions}
            onChange={handleChange}
          />
        )
      case 'recovery':
        return null
    }
  }

  return (
    <div className="screen screen--narrow wizard">
      <ol className="wizard__progress" aria-label="Setup progress">
        {steps.map((wizardStep) => (
          <li key={wizardStep} aria-current={wizardStep === step ? 'step' : undefined}>
            {STEP_TITLES[wizardStep]}
          </li>
        ))}
      </ol>

      <p className="wizard__position">
        Step {stepIndex + 1} of {steps.length}
      </p>

      <h1 className="wizard__title" tabIndex={-1} ref={headingRef}>
        {STEP_TITLES[step]}
      </h1>

      {hasErrors && (
        <div
          className="wizard__errors"
          role="alert"
          tabIndex={-1}
          ref={errorSummaryRef}
          aria-labelledby="wizard-errors-title"
        >
          <h2 id="wizard-errors-title">There is a problem</h2>
          <ul>
            {errorMessages.map((message) => (
              <li key={message}>{message}</li>
            ))}
          </ul>
        </div>
      )}

      <form
        className="wizard__form"
        onSubmit={(event) => {
          event.preventDefault()
          void handleSubmit()
        }}
        noValidate
      >
        {renderStep()}
      </form>

      {/*
        Footer buttons sit outside the form and are always type="button": React
        reuses the same DOM node when the step changes, and a node that flips to
        type="submit" during its own click would submit the form as the event's
        default action.
      */}
      <div className="wizard__footer">
        <button
          type="button"
          onClick={handleBack}
          disabled={stepIndex === 0 || completeSetup.isPending}
        >
          Back
        </button>
        {step === 'review' ? (
          <button
            type="button"
            className="button--primary"
            disabled={completeSetup.isPending}
            onClick={() => void handleSubmit()}
          >
            {completeSetup.isPending ? 'Setting up…' : 'Finish setup'}
          </button>
        ) : (
          <button type="button" className="button--primary" onClick={handleNext}>
            Next
          </button>
        )}
      </div>
    </div>
  )
}
