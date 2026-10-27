import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  createBackup,
  deleteBackup,
  listBackups,
  restoreBackup,
  type BackupRestoreSource,
  type BackupRow,
  type DeleteBackupRequest,
  type RestoreAccepted,
  type RestoreBackupAction,
} from '../../api/backups'

/**
 * Query-key prefix for the Admin backup catalogue (docs/API.md §18). Create,
 * delete, and restore all invalidate the prefix so the list reflects the
 * server's rows without a reload.
 */
export const backupsQueryKey = ['backups'] as const

/**
 * The backup list, newest first. `backups` is Admin-only, so a non-Admin
 * caller passes `enabled = false` and performs no fetch — the UI is not the
 * authority (docs/SECURITY.md §4).
 */
export function useBackups(enabled = true) {
  return useQuery<BackupRow[]>({
    queryKey: backupsQueryKey,
    queryFn: ({ signal }) => listBackups(signal),
    retry: false,
    enabled,
  })
}

/** Creates one manual backup; the request stays open for the whole dump. */
export function useCreateBackup() {
  const queryClient = useQueryClient()
  return useMutation<BackupRow, unknown, string>({
    mutationFn: (reason) => createBackup(reason),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: backupsQueryKey })
    },
  })
}

/** Deletes one archive after the shared dangerous-action confirmation. */
export function useDeleteBackup() {
  const queryClient = useQueryClient()
  return useMutation<BackupRow, unknown, { id: number; action: DeleteBackupRequest }>({
    mutationFn: ({ id, action }) => deleteBackup(id, action),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: backupsQueryKey })
    },
  })
}

/** Starts one restore; a `202` means the background restore has begun. */
export function useRestoreBackup() {
  const queryClient = useQueryClient()
  return useMutation<
    RestoreAccepted,
    unknown,
    { source: BackupRestoreSource; action: RestoreBackupAction }
  >({
    mutationFn: ({ source, action }) => restoreBackup(source, action),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: backupsQueryKey })
    },
  })
}
