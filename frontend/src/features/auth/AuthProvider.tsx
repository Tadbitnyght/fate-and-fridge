import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, type ReactNode } from 'react'

import {
  changePassword,
  fetchSession,
  login,
  logout,
  type ChangePasswordRequest,
  type LoginRequest,
  type Session,
} from '../../api/auth'
import { setupStatusQueryKey } from '../setup/useSetupStatus'
import { AuthContext, type AuthContextValue } from './AuthContext'
import { deriveSessionStatus, sessionQueryKey } from './session'

/**
 * The identity that owns every non-session cached read (BUG-007). `null` means
 * "no session at all"; otherwise the auth mode plus the user id (or the
 * single-user system actor) identifies the data owner.
 */
function sessionScope(session: Session | null | undefined): string | null {
  if (session === null || session === undefined) {
    return null
  }
  return `${session.auth_mode}:${session.user?.id ?? 'system'}`
}

/**
 * Cache roots the shell itself depends on, kept out of the BUG-007 sweep below.
 * `removeQueries` on a query that still has a mounted observer destroys the
 * cache entry and cancels its in-flight fetch *without* telling the observer, so
 * the re-render rebuilds a fresh, permanently pending query that nothing will
 * ever refetch. The session query is excluded because it *is* the identity
 * source (removing it would make the identity momentarily unknown and re-trigger
 * the sweep), and the setup-status query is excluded because `RequireSession`
 * gates every route on it for the whole authenticated lifetime: sweeping it left
 * the router on its loading screen so sign-out never reached `/login` (CI run
 * 40145178694). It carries household routing state (`setup_completed`
 * `auth_mode`), never user data.
 */
const SWEEP_EXEMPT_QUERY_ROOTS: ReadonlySet<string> = new Set([
  sessionQueryKey[0],
  setupStatusQueryKey[0],
])

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

  /**
   * Drops every cached read that belongs to an identity (BUG-007): groceries
   * recipes, statistics, settings and so on. The session query and the household
   * setup status are deliberately excluded — see `SWEEP_EXEMPT_QUERY_ROOTS`.
   * Logout overwrites the session entry with `null` instead, so no previous-user
   * data survives.
   */
  const clearUserScopedCache = useCallback(() => {
    queryClient.removeQueries({
      predicate: (query) => !SWEEP_EXEMPT_QUERY_ROOTS.has(String(query.queryKey[0])),
    })
  }, [queryClient])

  const refreshSession = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
  }, [queryClient])

  const loginMutation = useMutation({
    mutationFn: login,
    onSuccess: async (session) => {
      // A different identity must never render the previous user's cached
      // reads, even for the single frame before a refetch replaces them.
      const previous = sessionScope(queryClient.getQueryData<Session | null>(sessionQueryKey))
      if (previous !== sessionScope(session)) {
        clearUserScopedCache()
      }
      queryClient.setQueryData(sessionQueryKey, session)
      await queryClient.invalidateQueries({ queryKey: sessionQueryKey })
    },
  })

  const logoutMutation = useMutation({
    mutationFn: logout,
    // Deliberately synchronous and non-throwing. The POST succeeding *is* the
    // sign-out: nothing after it may reject (AppShell would show "Sign-out
    // failed…" and stay on the page) or leave the session query in an error
    // state (the router renders "Session unavailable" instead of `/login`).
    // A post-logout session refetch can only add those failure modes, so the
    // identity is cleared locally instead of re-read.
    onSuccess: () => {
      // Clear the identity first: the shell flips to `/login` from this alone.
      queryClient.setQueryData(sessionQueryKey, null)
      // BUG-007: dropping only the session key left user-scoped entries fresh
      // for up to `staleTime`, so the next user in this tab could be shown the
      // previous user's data until a refetch happened.
      clearUserScopedCache()
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

  // Covers identity transitions that do not run through the mutations above —
  // most importantly a 401-driven session loss (a revoked/expired session is
  // re-fetched and resolves to `null`, leaving no logout call to hook into).
  const scope = sessionScope(sessionQuery.data)
  const lastScopeRef = useRef<string | null>(null)
  useEffect(() => {
    const previous = lastScopeRef.current
    lastScopeRef.current = scope
    if (previous !== null && previous !== scope) {
      clearUserScopedCache()
    }
  }, [scope, clearUserScopedCache])

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
