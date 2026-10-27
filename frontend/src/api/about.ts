import { apiRequest } from './client'

/** `GET /about` payload (docs/API.md §24). */
export interface AboutInfo {
  /** SemVer only (`AppInfo.Version`); never a build hash. */
  app_version: string
  /**
   * Last applied EF Core migration id, or `null` when the server could not
   * read its database. The endpoint still answers `200` in that case.
   */
  schema_version: string | null
}

/** Any authenticated actor (member, Admin, or the single-user system actor). */
export function fetchAbout(signal?: AbortSignal): Promise<AboutInfo> {
  return apiRequest<AboutInfo>('/about', { signal })
}
