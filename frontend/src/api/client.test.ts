import { afterEach, describe, expect, it, vi } from 'vitest'

import { installMockFetch } from '../test/mockFetch'
import { ApiError, apiRequest, getCsrfToken, isApiError, setCsrfToken } from './client'

const sessionBody = {
  authenticated: true,
  auth_mode: 'multiuser',
  user: { id: 1, username: 'smith', role: 'admin' },
  permissions: ['calendar.view'],
  must_change_password: false,
}

afterEach(() => {
  vi.unstubAllGlobals()
  setCsrfToken(null)
})

describe('apiRequest', () => {
  it('requests /api/v1 paths with credentials and JSON headers', async () => {
    const mock = installMockFetch([{ path: '/health', body: { status: 'ok' } }])

    await expect(apiRequest('/health')).resolves.toEqual({ status: 'ok' })

    const [url, init] = mock.fetchMock.mock.calls[0]
    expect(String(url)).toBe('/api/v1/health')
    expect(init?.credentials).toBe('include')
    expect(init?.method).toBe('GET')
    expect((init?.headers as Record<string, string>)['Accept']).toBe('application/json')
    expect(init?.body).toBeUndefined()
  })

  it('serializes request bodies as JSON', async () => {
    const mock = installMockFetch([{ path: '/auth/login', method: 'POST', body: sessionBody }])

    await apiRequest('/auth/login', { method: 'POST', body: { username: 'smith' } })

    const [, init] = mock.fetchMock.mock.calls[0]
    expect(init?.body).toBe(JSON.stringify({ username: 'smith' }))
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json')
  })

  it('throws a typed ApiError with status, code, message, and details', async () => {
    installMockFetch([
      {
        path: '/auth/login',
        method: 'POST',
        status: 400,
        body: {
          error: {
            code: 'VALIDATION_FAILED',
            message: 'The request body failed validation.',
            details: { field_errors: { username: ['Username is required.'] } },
          },
        },
      },
    ])

    const error = await apiRequest('/auth/login', { method: 'POST', body: {} }).catch(
      (caught: unknown) => caught,
    )

    expect(isApiError(error)).toBe(true)
    const apiError = error as ApiError
    expect(apiError.status).toBe(400)
    expect(apiError.code).toBe('VALIDATION_FAILED')
    expect(apiError.message).toBe('The request body failed validation.')
    expect(apiError.details).toEqual({ field_errors: { username: ['Username is required.'] } })
  })

  it('falls back to a generic code for non-envelope or non-JSON failures', async () => {
    installMockFetch([{ path: '/health', status: 502, raw: '<html>bad gateway</html>' }])

    const error = await apiRequest('/health').catch((caught: unknown) => caught)

    expect(isApiError(error)).toBe(true)
    const apiError = error as ApiError
    expect(apiError.status).toBe(502)
    expect(apiError.code).toBe('UNKNOWN_ERROR')
    expect(apiError.message).toBe('Request failed with status 502')
    expect(apiError.details).toEqual({})
  })

  it('returns undefined for 204 responses', async () => {
    installMockFetch([{ path: '/auth/logout', method: 'POST', status: 204 }])

    await expect(apiRequest('/auth/logout', { method: 'POST' })).resolves.toBeUndefined()
  })

  it('stores the latest X-CSRF-TOKEN response header', async () => {
    installMockFetch([
      {
        path: '/auth/login',
        method: 'POST',
        headers: { 'X-CSRF-TOKEN': 'token-1' },
        body: sessionBody,
      },
    ])

    expect(getCsrfToken()).toBeNull()
    await apiRequest('/auth/login', { method: 'POST', body: {} })
    expect(getCsrfToken()).toBe('token-1')
  })

  it('echoes the stored CSRF token on unsafe requests only', async () => {
    const mock = installMockFetch([
      {
        path: '/auth/login',
        method: 'POST',
        headers: { 'X-CSRF-TOKEN': 'token-1' },
        body: sessionBody,
      },
      { path: '/health', body: { status: 'ok' } },
      { path: '/auth/logout', method: 'POST', status: 204 },
    ])

    await apiRequest('/auth/login', { method: 'POST', body: {} })
    await apiRequest('/health')
    await apiRequest('/auth/logout', { method: 'POST' })

    const [, healthInit] = mock.fetchMock.mock.calls[1]
    expect((healthInit?.headers as Record<string, string>)['X-CSRF-TOKEN']).toBeUndefined()

    const [, logoutInit] = mock.fetchMock.mock.calls[2]
    expect((logoutInit?.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('token-1')
  })
})
