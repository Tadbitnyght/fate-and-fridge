import { apiRequest } from './client'
import type { StartupPage } from './setup'

/** `GET /settings/household` payload (docs/API.md §21). */
export interface HouseholdSettings {
  household_name: string
  timezone: string
  week_starts_on: number
  default_diner_count: number
  startup_page: StartupPage
  randomizer_repeat_lookback_days: number
  randomizer_repeat_lookahead_days: number
  randomizer_repeat_multiplier: number
  randomizer_special_day_multiplier: number
  randomizer_protein_multiplier: number
  randomizer_protein_neighbor_distance: number
  randomizer_favorite_multiplier: number
  randomizer_spacing_near_days: number
  randomizer_spacing_far_days: number
  randomizer_spacing_search_days: number
}

/** Partial PATCH body; unknown fields are ignored by the server. */
export interface HouseholdSettingsPatch {
  household_name?: string
  timezone?: string
  week_starts_on?: number
  default_diner_count?: number
  startup_page?: StartupPage
  randomizer_repeat_lookback_days?: number
  randomizer_repeat_lookahead_days?: number
  randomizer_repeat_multiplier?: number
  randomizer_special_day_multiplier?: number
  randomizer_protein_multiplier?: number
  randomizer_protein_neighbor_distance?: number
  randomizer_favorite_multiplier?: number
  randomizer_spacing_near_days?: number
  randomizer_spacing_far_days?: number
  randomizer_spacing_search_days?: number
}

/** Any authenticated actor (including the single-user system actor) may read. */
export function fetchHouseholdSettings(): Promise<HouseholdSettings> {
  return apiRequest<HouseholdSettings>('/settings/household')
}

/** Admin-only; returns the updated settings. */
export function updateHouseholdSettings(patch: HouseholdSettingsPatch): Promise<HouseholdSettings> {
  return apiRequest<HouseholdSettings>('/settings/household', { method: 'PATCH', body: patch })
}
