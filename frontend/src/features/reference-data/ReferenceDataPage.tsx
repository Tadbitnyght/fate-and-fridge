import { NavLink, Outlet } from 'react-router-dom'

import { useReferenceDataAccess } from './access'

interface ReferenceTab {
  to: string
  label: string
  end?: boolean
}

const REFERENCE_TABS: ReferenceTab[] = [
  { to: '/settings/reference-data', label: 'Departments', end: true },
  { to: '/settings/reference-data/units', label: 'Units' },
  { to: '/settings/reference-data/ingredients', label: 'Ingredients' },
  { to: '/settings/reference-data/proteins', label: 'Proteins' },
  { to: '/settings/reference-data/tags', label: 'Tags' },
]

function tabClass({ isActive }: { isActive: boolean }): string {
  return isActive ? 'settings-nav__link settings-nav__link--active' : 'settings-nav__link'
}

/**
 * Reference-data shell (M2-11): route-based tabs over the household
 * vocabulary. Reads are open to every authenticated actor; without
 * `reference_data.edit` the page shows a read-only notice (the server
 * enforces writes regardless).
 */
export function ReferenceDataPage() {
  const { canEdit } = useReferenceDataAccess()

  return (
    <section className="settings-section reference-data" aria-labelledby="reference-data-heading">
      <h3 id="reference-data-heading">Reference data</h3>

      {!canEdit && (
        <div className="notice" role="note">
          <p>
            You have read-only access to reference data. Ask an Admin for the “Reference data —
            Edit” permission to make changes.
          </p>
        </div>
      )}

      <nav aria-label="Reference data sections" className="settings-nav reference-data__tabs">
        <ul className="settings-nav__list">
          {REFERENCE_TABS.map((tab) => (
            <li key={tab.to}>
              <NavLink to={tab.to} end={tab.end} className={tabClass}>
                {tab.label}
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>

      <Outlet />
    </section>
  )
}
