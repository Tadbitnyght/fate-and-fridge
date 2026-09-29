import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { App } from './App'

const healthPayload = {
  status: 'ok',
  app: 'Fate & Fridge',
  version: '0.1.0',
  environment: 'test',
}

const databasePayload = { status: 'ok', database: 'connected' }

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      const payload = url.includes('/health/db') ? databasePayload : healthPayload
      return {
        ok: true,
        status: 200,
        json: async () => payload,
      } as unknown as Response
    }),
  )
}

function renderApp(): void {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('App', () => {
  it('shows application and database status from the health endpoints', async () => {
    stubFetch()
    renderApp()

    expect(await screen.findByText('Operational')).toBeInTheDocument()
    expect(await screen.findByText('connected')).toBeInTheDocument()
    expect(screen.getByText(/Fate & Fridge v0\.1\.0/)).toBeInTheDocument()
  })
})
