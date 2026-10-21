import { useRef, useState } from 'react'
import { Link, NavLink, Outlet } from 'react-router-dom'

import { useAuth } from '../features/auth/AuthContext'
import { LiveSyncStatus } from '../features/live-sync/LiveSyncStatus'
import { useAdminAccess } from '../features/settings/access'
import { useAbout } from '../features/settings/useAbout'
import { useFocusOnRouteChange } from '../hooks/useFocusOnRouteChange'
import { MAIN_CONTENT_ID, SkipLink } from './SkipLink'
import { UpdateBanner } from './UpdateBanner'

interface NavItem {
  to: string
  label: string
  end?: boolean
  /**
   * Permission keys that reveal the item — any one is enough
   * (docs/M11_PLAN.md §1.30).
   */
  permissions?: string[]
}

/** Primary navigation (docs/PRODUCT_SPEC.md §5); Calendar is the default page. */
const NAV_ITEMS: NavItem[] = [
  { to: '/calendar', label: 'Calendar' },
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/recipes', label: 'Recipes' },
  { to: '/groceries', label: 'Groceries' },
  { to: '/statistics', label: 'Statistics', permissions: ['recipes.view', 'recipes.edit'] },
  { to: '/settings', label: 'Settings' },
]

function navLinkClass({ isActive }: { isActive: boolean }): string {
  return isActive ? 'app-nav__link app-nav__link--active' : 'app-nav__link'
}

export function AppShell() {
  const { status, user, permissions, logout } = useAuth()
  const { isAdmin } = useAdminAccess()
  const aboutQuery = useAbout()
  const [signingOut, setSigningOut] = useState(false)
  const [logoutError, setLogoutError] = useState<string | null>(null)
  const mainRef = useRef<HTMLElement>(null)
  useFocusOnRouteChange(mainRef)

  // Admins and the single-user system actor hold every permission key
  // server-side, so a permission-gated item is always visible to them.
  const visibleNavItems = NAV_ITEMS.filter(
    (item) =>
      item.permissions === undefined ||
      isAdmin ||
      item.permissions.some((permission) => permissions.includes(permission)),
  )

  async function handleLogout(): Promise<void> {
    setLogoutError(null)
    setSigningOut(true)
    try {
      await logout()
    } catch {
      setLogoutError('Sign-out failed. Check that the server is reachable.')
    } finally {
      setSigningOut(false)
    }
  }

  return (
    <div className="app-shell">
      <SkipLink />
      <header className="app-header">
        <div className="app-header__bar">
          <div>
            <h1>Fate &amp; Fridge</h1>
            <p className="app-subtitle">What&rsquo;s for dinner?</p>
          </div>
          <div className="app-account">
            <LiveSyncStatus />
            {user && (
              <span className="app-account__name">
                {user.username}
                {user.role === 'admin' && <span className="app-account__role">Admin</span>}
              </span>
            )}
            {status === 'authenticated' && (
              <button type="button" onClick={() => void handleLogout()} disabled={signingOut}>
                {signingOut ? 'Signing out…' : 'Sign out'}
              </button>
            )}
          </div>
        </div>
        {logoutError && (
          <p className="app-account__error" role="alert">
            {logoutError}
          </p>
        )}
        <nav className="app-nav" aria-label="Primary">
          <ul className="app-nav__list">
            {visibleNavItems.map((item) => (
              <li key={item.to} className="app-nav__item">
                <NavLink to={item.to} end={item.end} className={navLinkClass}>
                  {item.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
      </header>

      {/*
        Update notice (M13-09): only when a newer release is known and this
        device has not dismissed that exact version. It never shifts the shell
        badly because it is a normal block in the shell's flex column.
      */}
      <UpdateBanner />

      <main id={MAIN_CONTENT_ID} className="app-main" tabIndex={-1} ref={mainRef}>
        <Outlet />
      </main>

      <footer className="app-footer">
        <p>Self-hosted &middot; no telemetry &middot; your household data stays home</p>
        <p>
          <Link to="/progress">Milestone progress</Link>
        </p>
        {/*
          Version display (M13-08). While loading, or when the version cannot be
          read, the label renders nothing rather than an error: the footer must
          never make the app look broken.
        */}
        {aboutQuery.data && (
          <p className="app-footer__version">
            <Link to="/settings/about">Version {aboutQuery.data.app_version}</Link>
          </p>
        )}
      </footer>
    </div>
  )
}
