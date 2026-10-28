import { useEffect } from 'react'
import { useLocation } from 'react-router-dom'

import { documentTitleForPath } from '../lib/documentTitle'

/**
 * Keeps the browser tab in step with the route. The document title is
 * the one piece of page context a screen reader announces before the content
 * and it is all a tab strip shows, so every product route names itself here.
 */
export function useDocumentTitle(): void {
  const { pathname } = useLocation()

  useEffect(() => {
    document.title = documentTitleForPath(pathname)
  }, [pathname])
}
