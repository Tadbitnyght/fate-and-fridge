/**
 * Audit retention validation shared by the security settings UI
 * (docs/API.md §21). Mirrors the server's
 * `SecuritySettingsService.IsValidAuditRetentionDays`: `0` keeps audit entries
 * forever, otherwise the window must be a whole number of days in `7..3650`.
 */

/** Smallest bounded retention window, exclusive of "keep forever" (0). */
export const MIN_AUDIT_RETENTION_DAYS = 7

/** Largest bounded retention window (ten years). */
export const MAX_AUDIT_RETENTION_DAYS = 3650

/**
 * Validates the audit retention field: `0` keeps entries forever, otherwise a
 * whole number between 7 and 3650. Rejects blanks, decimals, and signs so the
 * client and the server agree before the request is sent.
 */
export function validateAuditRetentionDays(raw: string): { value?: number; error?: string } {
  const trimmed = raw.trim()
  if (!/^\d+$/.test(trimmed)) {
    return {
      error: 'Enter 0 (keep forever) or a whole number of days between 7 and 3650.',
    }
  }
  const value = Number(trimmed)
  if (
    !Number.isSafeInteger(value) ||
    (value !== 0 && (value < MIN_AUDIT_RETENTION_DAYS || value > MAX_AUDIT_RETENTION_DAYS))
  ) {
    return {
      error: 'Enter 0 (keep forever) or a whole number of days between 7 and 3650.',
    }
  }
  return { value }
}
