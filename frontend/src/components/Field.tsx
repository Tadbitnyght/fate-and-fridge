import type { ReactNode } from 'react'

export function Field({
  id,
  label,
  hint,
  error,
  children,
}: {
  id: string
  label: string
  hint?: string
  error?: string
  children: ReactNode
}) {
  return (
    <div className="form__field">
      <label htmlFor={id}>{label}</label>
      {children}
      {hint && (
        <p className="form__hint" id={`${id}-hint`}>
          {hint}
        </p>
      )}
      {error && (
        <p className="form__error" id={`${id}-error`}>
          {error}
        </p>
      )}
    </div>
  )
}
