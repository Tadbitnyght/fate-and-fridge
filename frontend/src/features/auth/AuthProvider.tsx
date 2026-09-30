import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, type ReactNode } from 'react'

import {
  changePassword,
  fetchSession,
  login,
  logout,
  type ChangePasswordRequest,
  type LoginRequest,
} from '../../api/auth'
import { AuthContext, type AuthContextValue } from './AuthContext'
import { deriveSessionStatus, sessionQueryKey } from './session'

/**
 * Session state for the app (docs/API.md §5). TanStack Query owns the
 * `GET /auth/session` server state; login/logout/change-password refresh it.
 * No server business rules are duplicated here.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()

  const sessionQuery = useQuery({
    queryKey: sessionQueryKey,
    queryFn: fetchSession,
    retry: false,
    staleTime: 0,
  })

  const refreshSession = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
  }, [queryClient])

  const loginMutation = useMutation({
    mutationFn: login,
    onSuccess: async (session) => {
      queryClient.setQueryData(sessionQueryKey, session)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    },
  })

  const logoutMutation = useMutation({
    mutationFn: logout,
    onSuccess: async () => {
      queryClient.setQueryData(sessionQueryKey, null)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    },
  })

  const changePasswordMutation = useMutation({
    mutationFn: changePassword,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    },
  })

  const { mutateAsync: loginAsync } = loginMutation
  const { mutateAsync: logoutAsync } = logoutMutation
  const { mutateAsync: changePasswordAsync } = changePasswordMutation

  const status = deriveSessionStatus(sessionQuery.isPending, sessionQuery.data)
  const session = sessionQuery.data ?? null

  const value = useMemo<AuthContextValue>(
    () => ({
      status,
      session,
      user: session?.user ?? null,
      permissions: session?.permissions ?? [],
      error: sessionQuery.error,
      refetchSession: refreshSession,
      login: async (request: LoginRequest) => {
        await loginAsync(request)
      },
      logout: async () => {
        await logoutAsync()
      },
      changePassword: async (request: ChangePasswordRequest) => {
        await changePasswordAsync(request)
      },
    }),
    [
      status,
      session,
      sessionQuery.error,
      refreshSession,
      loginAsync,
      logoutAsync,
      changePasswordAsync,
    ],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
