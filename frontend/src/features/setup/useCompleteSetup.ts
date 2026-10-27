import { useMutation, useQueryClient } from '@tanstack/react-query'

import { isApiError } from '../../api/client'
import { ErrorCodes } from '../../api/errorCodes'
import { completeSetup, type CompleteSetupRequest } from '../../api/setup'
import { sessionQueryKey } from '../auth/session'
import { setupStatusQueryKey } from './useSetupStatus'

/**
 * Completes setup and refreshes the session + setup status. In multiuser mode
 * the response establishes the first Admin session (cookie + CSRF token are
 * handled by the API client), so it is seeded straight into the session cache.
 * `409 SETUP_ALREADY_COMPLETED` also refreshes the caches so the routing
 * guards move the caller into the app/login flow instead of showing an error.
 */
export function useCompleteSetup() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (request: CompleteSetupRequest) => completeSetup(request),
    onSuccess: async (result) => {
      if (result.session) {
        queryClient.setQueryData(sessionQueryKey, result.session)
      }
      await queryClient.invalidateQueries({ queryKey: setupStatusQueryKey })
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    },
    onError: async (error) => {
      if (isApiError(error) && error.code === ErrorCodes.SetupAlreadyCompleted) {
        await queryClient.invalidateQueries({ queryKey: setupStatusQueryKey })
        await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      }
    },
  })
}
