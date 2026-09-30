import { vi } from 'vitest'

export interface MockResponse {
  status?: number
  body?: unknown
  headers?: Record<string, string>
  /** Raw response text; overrides `body` (used for non-JSON payloads). */
  raw?: string
}

export interface MockRoute extends MockResponse {
  /** Path substring after `/api/v1`, e.g. `/auth/session`. */
  path: string
  method?: string
  /** Per-call override; receives the request init and the 1-based match count. */
  respond?: (init: RequestInit, callCount: number) => MockResponse
}

export interface MockFetch {
  fetchMock: ReturnType<typeof vi.fn>
  /** Number of requests handled by the route with this path. */
  callsFor: (path: string) => number
}

export function createResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
  raw?: string,
): Response {
  const text = raw ?? (body === null || body === undefined ? '' : JSON.stringify(body))
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers({ 'Content-Type': 'application/json', ...headers }),
    json: async () => JSON.parse(text) as unknown,
    text: async () => text,
  } as unknown as Response
}

/**
 * Installs a `fetch` stub that answers requests by matching the URL path and
 * HTTP method. Unmatched requests fail the test loudly with a 404 envelope.
 */
export function installMockFetch(routes: MockRoute[]): MockFetch {
  const counts = new Map<string, number>()
  const fetchMock = vi.fn(
    async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const url = String(input)
      const method = (init?.method ?? 'GET').toUpperCase()
      const route = routes.find(
        (candidate) =>
          url.includes(candidate.path) && (candidate.method ?? 'GET').toUpperCase() === method,
      )
      if (!route) {
        return createResponse(404, {
          error: {
            code: 'NOT_FOUND',
            message: `No mock route for ${method} ${url}`,
            details: {},
          },
        })
      }
      const count = (counts.get(route.path) ?? 0) + 1
      counts.set(route.path, count)
      const matched = route.respond?.(init ?? {}, count) ?? route
      return createResponse(
        matched.status ?? 200,
        matched.body ?? null,
        matched.headers ?? {},
        matched.raw,
      )
    },
  )
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, callsFor: (path) => counts.get(path) ?? 0 }
}
