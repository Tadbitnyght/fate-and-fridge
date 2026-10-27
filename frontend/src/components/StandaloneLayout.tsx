import { useRef } from 'react'
import { Outlet } from 'react-router-dom'

import { useFocusOnRouteChange } from '../hooks/useFocusOnRouteChange'
import { MAIN_CONTENT_ID, SkipLink } from './SkipLink'

/** Layout for unauthenticated pages (setup, login, forced password change). */
export function StandaloneLayout() {
  const mainRef = useRef<HTMLElement>(null)
  useFocusOnRouteChange(mainRef)

  return (
    <>
      <SkipLink />
      <main id={MAIN_CONTENT_ID} className="screen-host" tabIndex={-1} ref={mainRef}>
        <Outlet />
      </main>
    </>
  )
}
