import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  checkForUpdates,
  getUpdates,
  setUpdateChecksEnabled,
  type UpdateStatus,
} from '../../api/updates'

/**
 * Query-key prefix for the update status (docs/API.md §25). The About section
 * and the shell notice share it, and both mutations invalidate the prefix so
 * every surface reflects the server's answer without a reload.
 */
export const updatesQueryKey = ['updates'] as const

/**
 * The cached update status. Readable by every authenticated actor, so the shell
 * notice and the About section fetch it unconditionally; `retry: false` keeps a
 * failure quiet — an unreachable status feed is a degraded display, never a
 * page error.
 */
export function useUpdates() {
  return useQuery<UpdateStatus>({
    queryKey: updatesQueryKey,
    queryFn: ({ signal }) => getUpdates(signal),
    retry: false,
  })
}

/**
 * Forces one check (Admin only). A feed failure still resolves with a recorded
 * `last_error`; only a `403`/connectivity problem rejects. Either way the
 * cached status is refreshed.
 */
export function useCheckForUpdates() {
  const queryClient = useQueryClient()
  return useMutation<UpdateStatus, unknown, void>({
    mutationFn: () => checkForUpdates(),
    onSettled: async () => {
      // A failed check still stamps `last_checked_at`/`last_error`, so refresh
      // even when the request rejected.
      await queryClient.invalidateQueries({ queryKey: updatesQueryKey })
    },
  })
}

/** Persists the Admin-only `update_check_enabled` preference. */
export function useSetUpdateChecksEnabled() {
  const queryClient = useQueryClient()
  return useMutation<UpdateStatus, unknown, boolean>({
    mutationFn: (enabled) => setUpdateChecksEnabled(enabled),
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: updatesQueryKey })
    },
  })
}
