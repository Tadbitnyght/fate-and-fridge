import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import {
  fetchHouseholdSettings,
  updateHouseholdSettings,
  type HouseholdSettings,
  type HouseholdSettingsPatch,
} from '../../api/household'

export const householdSettingsQueryKey = ['settings', 'household'] as const

export function useHouseholdSettings() {
  return useQuery<HouseholdSettings>({
    queryKey: householdSettingsQueryKey,
    queryFn: fetchHouseholdSettings,
    retry: false,
  })
}

export function useUpdateHouseholdSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (patch: HouseholdSettingsPatch) => updateHouseholdSettings(patch),
    onSuccess: async (settings) => {
      queryClient.setQueryData(householdSettingsQueryKey, settings)
      await queryClient.invalidateQueries({ queryKey: householdSettingsQueryKey })
    },
  })
}
