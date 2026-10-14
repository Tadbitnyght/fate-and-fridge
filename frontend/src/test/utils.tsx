import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, type RenderResult } from '@testing-library/react'
import type { ReactElement, ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'

export function createTestQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

export interface RenderWithProvidersOptions {
  route?: string
  /** Location state for the initial entry (e.g. `{ from: '/recipes' }`). */
  routeState?: unknown
  queryClient?: QueryClient
}

/** Renders `ui` with a fresh QueryClient and a MemoryRouter at `route`. */
export function renderWithProviders(
  ui: ReactElement,
  options: RenderWithProvidersOptions = {},
): RenderResult {
  const queryClient = options.queryClient ?? createTestQueryClient()
  // Split an optional `?query` off the route so `useSearchParams` works in tests.
  const [pathname, search] = (options.route ?? '/').split('?')
  const entry = {
    pathname,
    search: search === undefined ? '' : `?${search}`,
    state: options.routeState,
  }

  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[entry]}>{children}</MemoryRouter>
      </QueryClientProvider>
    )
  }

  return render(ui, { wrapper: Wrapper })
}
