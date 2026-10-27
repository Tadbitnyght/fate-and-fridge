/**
 * Helpers for the M13-09 update notice (docs/M13_PLAN.md §1.14): the per-device
 * dismissal memory and the release-link guard. Kept out of the component file
 * so React Fast Refresh keeps working.
 */

/**
 * Per-device dismissal, keyed to the latest version. Dismissing `0.2.0` hides
 * that notice on this device only, and a newer release shows the banner again.
 */
export const UPDATE_NOTICE_DISMISSED_KEY = 'faf.update-notice.dismissed-version'

/** The version this device dismissed, or `null` when nothing is remembered. */
export function readDismissedVersion(): string | null {
  try {
    return window.localStorage.getItem(UPDATE_NOTICE_DISMISSED_KEY)
  } catch {
    // Storage can be unavailable (private mode, disabled cookies): the notice
    // then simply is not remembered rather than breaking the shell.
    return null
  }
}

/** Remembers one dismissed version for this device. */
export function rememberDismissedVersion(version: string): void {
  try {
    window.localStorage.setItem(UPDATE_NOTICE_DISMISSED_KEY, version)
  } catch {
    // Nothing to do: the in-memory state still hides it for this page view.
  }
}

/**
 * The release page the server supplied, but only when it is an absolute
 * `https` URL. The server already normalizes this; the guard keeps a bad value
 * from ever becoming a `javascript:` link.
 */
export function safeReleaseUrl(value: string | null): string | null {
  if (value === null || value.trim().length === 0) {
    return null
  }
  try {
    const url = new URL(value)
    return url.protocol === 'https:' ? url.toString() : null
  } catch {
    return null
  }
}
