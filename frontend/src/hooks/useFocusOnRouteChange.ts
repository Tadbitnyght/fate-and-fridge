import { useEffect, type RefObject } from 'react'
import { useLocation } from 'react-router-dom'

/**
 * Moves focus to the page heading on route changes (falling back to the main
 * container) so keyboard and screen-reader users land on the new content.
 * Only runs when the pathname changes, so in-page interactions such as form
 * validation or modal handling keep their own focus behaviour.
 */
export function useFocusOnRouteChange(container: RefObject<HTMLElement | null>): void {
  const location = useLocation()

  useEffect(() => {
    const root = container.current
    if (!root) {
      return
    }

    const heading = root.querySelector<HTMLElement>('h1[tabindex="-1"], h2[tabindex="-1"]')
    if (heading) {
      heading.focus()
      return
    }

    root.focus()
  }, [container, location.pathname])
}
