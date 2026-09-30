import { apiRequest } from './client'
import type { StartupPage } from './setup'

/** `GET /settings/household` payload (docs/API.md §21). */
export interface HouseholdSettings {
  household_name: string
  timezone: string
  week_starts_on: number
  default_diner_count: number
  startup_page: StartupPage
}

/** Partial PATCH body; unknown fields are ignored by the server. */
export interface HouseholdSettingsPatch {
  household_name?: string
  timezone?: string
  week_starts_on?: number
  default_diner_count?: number
  startup_page?: StartupPage
}

/** Any authenticated actor (including the single-user system actor) may read. */
export function fetchHouseholdSettings(): Promise<HouseholdSettings> {
  return apiRequest<HouseholdSettings>('/settings/household')
}

/** Admin-only; returns the updated settings. */
export function updateHouseholdSettings(patch: HouseholdSettingsPatch): Promise<HouseholdSettings> {
  return apiRequest<HouseholdSettings>('/settings/household', { method: 'PATCH', body: patch })
}
