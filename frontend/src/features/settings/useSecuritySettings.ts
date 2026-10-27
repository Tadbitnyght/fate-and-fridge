import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  fetchSecuritySettings,
  switchToMultiuser,
  updateSecuritySettings,
  type SecuritySettings,
  type SecuritySettingsPatch,
} from '../../api/security'
import { sessionQueryKey } from '../auth/session'

export const securitySettingsQueryKey = ['settings', 'security'] as const

export function useSecuritySettings(enabled = true) {
  return useQuery<SecuritySettings>({
    queryKey: securitySettingsQueryKey,
    queryFn: fetchSecuritySettings,
    retry: false,
    enabled,
  })
}

export function useUpdateSecuritySettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (patch: SecuritySettingsPatch) => updateSecuritySettings(patch),
    onSuccess: async (settings) => {
      queryClient.setQueryData(securitySettingsQueryKey, settings)
      await queryClient.invalidateQueries({ queryKey: securitySettingsQueryKey })
    },
  })
}

/**
 * Single-user → multiuser switch. The response is a signed-in session; it is
 * seeded into the session cache so the shell updates without a reload, and the
 * Admin-only sections become available.
 */
export function useSwitchToMultiuser() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (admin: { username: string; password: string }) => switchToMultiuser(admin),
    onSuccess: async (session) => {
      queryClient.setQueryData(sessionQueryKey, session)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      await queryClient.invalidateQueries({ queryKey: securitySettingsQueryKey })
      await queryClient.invalidateQueries({ queryKey: ['users'] })
      await queryClient.invalidateQueries({ queryKey: ['settings', 'household'] })
    },
  })
}
