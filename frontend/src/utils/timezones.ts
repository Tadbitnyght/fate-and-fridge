/** IANA timezone helpers shared by the setup wizard and household settings. */

/**
 * Supported IANA zones, or null when the runtime cannot enumerate them (the UI
 * then falls back to a plain text input).
 */
export function detectTimezoneOptions(): string[] | null {
  try {
    if (typeof Intl.supportedValuesOf !== 'function') {
      return null
    }
    const zones = Intl.supportedValuesOf('timeZone')
    return Array.isArray(zones) && zones.length > 0 ? zones : null
  } catch {
    return null
  }
}

/** True when the runtime already considers this the selected zone (aliases such as UTC). */
export function isDetectedTimeZone(value: string): boolean {
  try {
    const detected = Intl.DateTimeFormat().resolvedOptions().timeZone
    return typeof detected === 'string' && detected.toLowerCase() === value.toLowerCase()
  } catch {
    return false
  }
}

export function getDefaultTimezone(options: string[] | null): string {
  let detected = ''
  try {
    detected = Intl.DateTimeFormat().resolvedOptions().timeZone ?? ''
  } catch {
    detected = ''
  }

  if (detected.length > 0) {
    if (options === null) {
      return detected
    }
    // Keep the detected zone (aliases such as UTC are valid server-side even
    // though Intl omits them); the membership check only prefers canonical form.
    return options.find((zone) => zone.toLowerCase() === detected.toLowerCase()) ?? detected
  }

  return options?.[0] ?? 'UTC'
}

export function validateTimezone(value: string, options: string[] | null): string | undefined {
  const trimmed = value.trim()
  if (trimmed.length === 0) {
    return 'Choose a timezone.'
  }
  if (options === null) {
    return undefined
  }
  if (options.some((zone) => zone.toLowerCase() === trimmed.toLowerCase())) {
    return undefined
  }
  // `Intl.supportedValuesOf` omits aliases such as UTC even though the server
  // accepts them; the detected browser zone is always allowed.
  if (isDetectedTimeZone(trimmed)) {
    return undefined
  }
  return 'Choose a valid IANA timezone, for example America/Chicago.'
}
