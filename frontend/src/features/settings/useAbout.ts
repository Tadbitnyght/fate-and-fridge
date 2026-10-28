import { useQuery } from '@tanstack/react-query'

import { fetchAbout, type AboutInfo } from '../../api/about'

/**
 * Query-key prefix for the version display (docs/API.md §24). 's update
 * status reuses this prefix so the About section and the shell footer refresh
 * together.
 */
export const aboutQueryKey = ['about'] as const

/**
 * The app + schema version. Readable by every authenticated actor, so the
 * footer and the About section fetch it unconditionally; a failure is a
 * degraded display, never a page error (`retry: false` keeps it quiet).
 */
export function useAbout() {
  return useQuery<AboutInfo>({
    queryKey: aboutQueryKey,
    queryFn: ({ signal }) => fetchAbout(signal),
    retry: false,
  })
}
