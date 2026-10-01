/** Stable id for the main content region targeted by the skip link. */
export const MAIN_CONTENT_ID = 'main-content'

/**
 * First focusable element on every page: visually hidden until focused, then
 * jumps keyboard users past the header/navigation to the main content.
 */
export function SkipLink() {
  return (
    <a className="skip-link" href={`#${MAIN_CONTENT_ID}`}>
      Skip to main content
    </a>
  )
}
