import type { Session } from '../../api/auth'

export const sessionQueryKey = ['auth', 'session'] as const

/** States exposed by `useAuth()` (docs/ARCHITECTURE.md §8). */
export type SessionStatus = 'loading' | 'unauthenticated' | 'authenticated' | 'single_user'

export function deriveSessionStatus(
  isPending: boolean,
  session: Session | null | undefined,
): SessionStatus {
  if (isPending) {
    return 'loading'
  }
  if (!session) {
    return 'unauthenticated'
  }
  return session.auth_mode === 'single_user' ? 'single_user' : 'authenticated'
}
