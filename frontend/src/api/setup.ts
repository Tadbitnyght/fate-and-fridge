import { apiRequest } from './client'
import type { AuthMode } from './auth'

/** Payload of `GET /setup/status` (docs/API.md §21). Public while unconfigured. */
export interface SetupStatus {
  setup_completed: boolean
  auth_mode: AuthMode | null
  recovery_key_present: boolean
}

export function fetchSetupStatus(): Promise<SetupStatus> {
  return apiRequest<SetupStatus>('/setup/status')
}
