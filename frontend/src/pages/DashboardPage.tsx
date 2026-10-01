import { Link } from 'react-router-dom'

import { PagePlaceholder } from '../components/Screens'
import { StatusPanel } from '../components/StatusPanel'

export function DashboardPage() {
  return (
    <PagePlaceholder title="Dashboard">
      <p className="page-note">
        Tonight&rsquo;s dinner, upcoming meals, and the grocery summary will live here.
      </p>
      <section className="card" aria-labelledby="progress-card-heading">
        <h3 id="progress-card-heading">Project progress</h3>
        <p className="page-note">
          Milestones, completion dates, test evidence, and the tracked GitHub issues.
        </p>
        <p>
          <Link to="/progress">Open the milestone roadmap</Link>
        </p>
      </section>
      <StatusPanel />
    </PagePlaceholder>
  )
}
