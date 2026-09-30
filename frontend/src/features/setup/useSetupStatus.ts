import { useQuery } from '@tanstack/react-query'

import { fetchSetupStatus, type SetupStatus } from '../../api/setup'

export const setupStatusQueryKey = ['setup', 'status'] as const

/**
 * Setup status gates the whole app (docs/API.md §21): an incomplete household
 * is routed to the setup wizard before any session handling.
 */
export function useSetupStatus() {
  return useQuery<SetupStatus>({
    queryKey: setupStatusQueryKey,
    queryFn: fetchSetupStatus,
    retry: false,
    staleTime: 0,
  })
}
