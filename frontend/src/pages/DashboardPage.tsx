import { Link } from 'react-router-dom'

import { StatusPanel } from '../components/StatusPanel'

interface QuickLink {
  to: string
  title: string
  description: string
  action: string
}

/**
 * Product dashboard (docs/PRODUCT_SPEC.md §5): the household's starting points
 * for the three questions the app answers — what's for dinner, what's coming
 * up, and what do we need from the store.
 * The links are static, so this page reads nothing of its own beyond the
 * system-status panel that was already here.
 */
const QUICK_LINKS: QuickLink[] = [
  {
    to: '/calendar',
    title: 'Calendar',
    description: 'Plan this week, lock the nights that are settled, and see what is coming up.',
    action: 'Open the calendar',
  },
  {
    to: '/recipes',
    title: 'Recipes',
    description: 'Browse the household cookbook, add a new dish, or import one from a link.',
    action: 'Browse recipes',
  },
  {
    to: '/groceries',
    title: 'Groceries',
    description: 'Everything the planned meals need from the store, grouped by department.',
    action: 'View the grocery list',
  },
  {
    to: '/statistics',
    title: 'Statistics',
    description: 'What the household has been eating lately, and the meals it repeats most.',
    action: 'See the statistics',
  },
]

export function DashboardPage() {
  return (
    <section className="page" aria-labelledby="page-dashboard">
      <h2 id="page-dashboard" tabIndex={-1}>
        Dashboard
      </h2>
      <p className="page-note">
        What&rsquo;s for dinner? Start with the calendar, the cookbook, or the grocery list.
      </p>
      {QUICK_LINKS.map((link) => (
        <section key={link.to} className="card" aria-labelledby={`dashboard-${link.to.slice(1)}`}>
          <h3 id={`dashboard-${link.to.slice(1)}`}>{link.title}</h3>
          <p className="page-note">{link.description}</p>
          <p>
            <Link to={link.to} className="dashboard-quick-link">
              {link.action}
            </Link>
          </p>
        </section>
      ))}
      <StatusPanel />
    </section>
  )
}
