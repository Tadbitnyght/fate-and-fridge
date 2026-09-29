const API_BASE = '/api/v1'

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

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`)
  if (!response.ok) {
    throw new Error(`Request to ${path} failed with status ${response.status}`)
  }
  return (await response.json()) as T
}

export function fetchHealth(): Promise<Health> {
  return getJson<Health>('/health')
}

export function fetchDatabaseHealth(): Promise<DatabaseHealth> {
  return getJson<DatabaseHealth>('/health/db')
}
