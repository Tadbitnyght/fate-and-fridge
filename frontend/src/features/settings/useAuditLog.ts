import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query'

import {
  getAuditLog,
  pruneAuditLog,
  type AuditLogParams,
  type AuditLogResponse,
  type AuditPruneRequest,
  type AuditPruneResult,
} from '../../api/audit'

/**
 * Query-key prefix for every audit read (docs/API.md §16). 's export and
 * prune reuse the prefix so live-sync invalidation can target the whole family.
 */
export const auditQueryKey = ['audit'] as const

/**
 * The Admin audit timeline, one page at a time. The filters are part of the
 * query key, so changing any of them starts a fresh first page (the cursor
 * resets by construction); "Load more" follows the server's opaque
 * `next_cursor` and appends. `audit` is Admin-only, so a non-Admin caller
 * passes `enabled = false` and performs no fetch — the server stays the sole
 * authority.
 */
export function useAuditLog(params: AuditLogParams = {}, enabled = true) {
  return useInfiniteQuery<AuditLogResponse>({
    queryKey: [...auditQueryKey, params],
    queryFn: ({ pageParam }) => getAuditLog({ ...params, cursor: pageParam as string | undefined }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage) => lastPage.next_cursor ?? undefined,
    retry: false,
    enabled,
  })
}

/**
 * The confirmed, age-based audit retention prune. On success the
 * viewer is invalidated so the pruned rows disappear without a reload. The
 * export is an imperative download rather than a query, so it has no hook here.
 */
export function usePruneAuditLog() {
  const queryClient = useQueryClient()
  return useMutation<AuditPruneResult, unknown, AuditPruneRequest>({
    mutationFn: (body) => pruneAuditLog(body),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: auditQueryKey })
    },
  })
}
