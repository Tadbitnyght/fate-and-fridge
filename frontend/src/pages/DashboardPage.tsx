import { PagePlaceholder } from '../components/Screens'
import { StatusPanel } from '../components/StatusPanel'

export function DashboardPage() {
  return (
    <PagePlaceholder title="Dashboard">
      <p className="page-note">
        Tonight&rsquo;s dinner, upcoming meals, and the grocery summary will live here.
      </p>
      <StatusPanel />
    </PagePlaceholder>
  )
}
