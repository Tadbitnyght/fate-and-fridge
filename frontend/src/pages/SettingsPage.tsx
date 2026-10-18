import { NavLink, Outlet } from 'react-router-dom'

import { useAdminAccess } from '../features/settings/access'

interface SettingsNavItem {
  to: string
  label: string
  end?: boolean
  adminOnly?: boolean
}

const SETTINGS_NAV: SettingsNavItem[] = [
  { to: '/settings', label: 'Household', end: true },
  { to: '/settings/reference-data', label: 'Reference Data' },
  { to: '/settings/users', label: 'Users', adminOnly: true },
  { to: '/settings/security', label: 'Security', adminOnly: true },
  { to: '/settings/audit', label: 'Audit log', adminOnly: true },
  { to: '/settings/account', label: 'Account' },
]

function navClass({ isActive }: { isActive: boolean }): string {
  return isActive ? 'settings-nav__link settings-nav__link--active' : 'settings-nav__link'
}

/** Settings layout: section navigation plus the active section (M1-14). */
export function SettingsPage() {
  const { isAdmin } = useAdminAccess()

  return (
    <section className="page settings">
      <h2 tabIndex={-1}>Settings</h2>

      <nav aria-label="Settings sections" className="settings-nav">
        <ul className="settings-nav__list">
          {SETTINGS_NAV.filter((item) => !item.adminOnly || isAdmin).map((item) => (
            <li key={item.to}>
              <NavLink to={item.to} end={item.end} className={navClass}>
                {item.label}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>

      <Outlet />
    </section>
  )
}
