export interface FieldPropsOptions {
  error?: string
  hint?: boolean
}

/** Wiring shared by every wizard input: id, invalid state, described-by ids. */
export function fieldProps(
  id: string,
  options: FieldPropsOptions = {},
): {
  id: string
  'aria-invalid'?: boolean
  'aria-describedby'?: string
} {
  const describedBy = [options.hint ? `${id}-hint` : null, options.error ? `${id}-error` : null]
    .filter((value): value is string => value !== null)
    .join(' ')

  return {
    id,
    'aria-invalid': options.error ? true : undefined,
    'aria-describedby': describedBy.length > 0 ? describedBy : undefined,
  }
}
