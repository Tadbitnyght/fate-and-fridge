const API_BASE = '/api/v1'

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

/**
 * Antiforgery token contract (docs/API.md §5): the backend returns a fresh
 * token in the `X-CSRF-TOKEN` response header and the client echoes it on
 * unsafe requests. It lives in memory only — never in storage.
 */
let csrfToken: string | null = null

export function setCsrfToken(token: string | null): void {
  csrfToken = token
}

export function getCsrfToken(): string | null {
  return csrfToken
}

/** Parsed server error envelope: `{ error: { code, message, details } }`. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly details: Record<string, unknown>

  constructor(
    status: number,
    code: string,
    message: string,
    details: Record<string, unknown> = {},
  ) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.details = details
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError
}

export interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
  body?: unknown
  signal?: AbortSignal
}

/**
 * Performs a JSON request against `/api/v1`, with credentials (session
 * cookie), CSRF echo, and error-envelope parsing. Clients must branch on
 * `ApiError.code`, never on the message (docs/API.md §4).
 */
export async function apiRequest<T>(path: string, options: ApiRequestOptions = {}): Promise<T> {
  const method = options.method ?? 'GET'
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  if (UNSAFE_METHODS.has(method) && csrfToken !== null) {
    headers['X-CSRF-TOKEN'] = csrfToken
  }

  const response = await fetch(`${API_BASE}${path}`, {
    method,
    credentials: 'include',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
  })

  return parseResponse<T>(response)
}

/**
 * Multipart upload against `/api/v1` with the same credentials and CSRF echo
 * as {@link apiRequest}. Used for recipe image uploads (docs/API.md §23):
 * `Content-Type` is intentionally left for the browser to set with the
 * multipart boundary.
 */
export async function apiUpload<T>(
  path: string,
  formData: FormData,
  options: { method?: 'POST' | 'PUT' | 'PATCH' | 'DELETE'; signal?: AbortSignal } = {},
): Promise<T> {
  const method = options.method ?? 'PUT'
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (UNSAFE_METHODS.has(method) && csrfToken !== null) {
    headers['X-CSRF-TOKEN'] = csrfToken
  }

  const response = await fetch(`${API_BASE}${path}`, {
    method,
    credentials: 'include',
    headers,
    body: formData,
    signal: options.signal,
  })

  return parseResponse<T>(response)
}

/** Stores a rotated CSRF token and unwraps the shared response envelope. */
async function parseResponse<T>(response: Response): Promise<T> {
  const responseToken = response.headers.get('X-CSRF-TOKEN')
  if (responseToken) {
    csrfToken = responseToken
  }

  if (response.status === 204) {
    return undefined as unknown as T
  }

  const text = await response.text()
  let payload: unknown
  if (text.length > 0) {
    try {
      payload = JSON.parse(text)
    } catch {
      payload = undefined
    }
  }

  if (!response.ok) {
    throw toApiError(response.status, payload)
  }

  return payload as T
}

function toApiError(status: number, payload: unknown): ApiError {
  const envelope = payload as
    { error?: { code?: unknown; message?: unknown; details?: unknown } } | undefined
  const error = envelope?.error
  const code =
    typeof error?.code === 'string' && error.code.length > 0 ? error.code : 'UNKNOWN_ERROR'
  const message =
    typeof error?.message === 'string' && error.message.length > 0
      ? error.message
      : `Request failed with status ${status}`
  const details =
    typeof error?.details === 'object' && error.details !== null
      ? (error.details as Record<string, unknown>)
      : {}
  return new ApiError(status, code, message, details)
}

export interface Health {
  status: string
  app: string
  version: string
  environment: string
}

export interface DatabaseHealth {
  status: 'ok' | 'degraded'
  database: 'connected' | 'unavailable'
}

export function fetchHealth(): Promise<Health> {
  return apiRequest<Health>('/health')
}

export function fetchDatabaseHealth(): Promise<DatabaseHealth> {
  return apiRequest<DatabaseHealth>('/health/db')
}
