import { useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'

import { useAuth } from '../features/auth/AuthContext'

interface NavItem {
  to: string
  label: string
  end?: boolean
}

/** Primary navigation (docs/PRODUCT_SPEC.md §5); Calendar is the default page. */
const NAV_ITEMS: NavItem[] = [
  { to: '/', label: 'Calendar', end: true },
  { to: '/dashboard', label: 'Dashboard' },
  { to: '/recipes', label: 'Recipes' },
  { to: '/groceries', label: 'Groceries' },
  { to: '/statistics', label: 'Statistics' },
  { to: '/settings', label: 'Settings' },
]

function navLinkClass({ isActive }: { isActive: boolean }): string {
  return isActive ? 'app-nav__link app-nav__link--active' : 'app-nav__link'
}

export function AppShell() {
  const { status, user, logout } = useAuth()
  const [signingOut, setSigningOut] = useState(false)
  const [logoutError, setLogoutError] = useState<string | null>(null)

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
      <header className="app-header">
        <div className="app-header__bar">
          <div>
            <h1>Fate &amp; Fridge</h1>
            <p className="app-subtitle">What&rsquo;s for dinner?</p>
          </div>
          <div className="app-account">
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
            {NAV_ITEMS.map((item) => (
              <li key={item.to} className="app-nav__item">
                <NavLink to={item.to} end={item.end} className={navLinkClass}>
                  {item.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
      </header>

      <main className="app-main">
        <Outlet />
      </main>

      <footer className="app-footer">
        <p>Self-hosted &middot; no telemetry &middot; your household data stays home</p>
      </footer>
    </div>
  )
}
