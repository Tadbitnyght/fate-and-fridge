/** Field error maps shared by the setup wizard and the admin console. */

export type FieldErrors = Record<string, string>

/** Extracts the first message per field from a server error envelope's details. */
export function extractServerFieldErrors(details: Record<string, unknown>): FieldErrors {
  const raw = details.field_errors
  const errors: FieldErrors = {}
  if (typeof raw !== 'object' || raw === null) {
    return errors
  }
  for (const [field, messages] of Object.entries(raw as Record<string, unknown>)) {
    if (Array.isArray(messages) && typeof messages[0] === 'string' && messages[0].length > 0) {
      errors[field] = messages[0]
    }
  }
  return errors
}

/** First message in the map, used for form-level error summaries. */
export function firstFieldError(errors: FieldErrors): string | null {
  for (const message of Object.values(errors)) {
    if (message.length > 0) {
      return message
    }
  }
  return null
}
