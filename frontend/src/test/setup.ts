import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach } from 'vitest'

// Rendering a route resolves setup status -> session -> the tab's list request
// before row actions exist. Parallel Vitest workers can starve the event loop
// past Testing Library's 1 s async default, so give async queries the same
// headroom as the Vitest test timeout.
configure({ asyncUtilTimeout: 5000 })

afterEach(() => {
  cleanup()
})
