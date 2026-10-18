import { useInfiniteQuery } from '@tanstack/react-query'

import { getAuditLog, type AuditLogParams, type AuditLogResponse } from '../../api/audit'

/**
 * Query-key prefix for every audit read (docs/API.md §16). M12-04's export and
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
