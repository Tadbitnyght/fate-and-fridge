export const DEFAULT_RETURN_TO = '/'

/**
 * Defense-in-depth for post-login navigation: only internal, same-origin
 * paths are accepted. Absolute URLs, protocol-relative URLs (`//host`)
 * backslash variants (`/\host`), and non-path schemes (`javascript:`, …) all
 * fall back to {@link DEFAULT_RETURN_TO}. The value is resolved with the URL
 * parser the browser uses, so the origin comparison cannot be bypassed with
 * control characters (tabs/newlines are stripped during parsing).
 */
export function sanitizeReturnTo(value: unknown): string {
  if (typeof value !== 'string' || !value.startsWith('/')) {
    return DEFAULT_RETURN_TO
  }

  try {
    const url = new URL(value, window.location.origin)
    if (url.origin !== window.location.origin || !url.pathname.startsWith('/')) {
      return DEFAULT_RETURN_TO
    }
    return `${url.pathname}${url.search}${url.hash}`
  } catch {
    return DEFAULT_RETURN_TO
  }
}
