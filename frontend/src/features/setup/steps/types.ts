import type { FieldErrors, SetupFormValues } from '../validation'

export interface StepProps {
  values: SetupFormValues
  errors: FieldErrors
  timezoneOptions: string[] | null
  onChange: <K extends keyof SetupFormValues>(field: K, value: SetupFormValues[K]) => void
}
