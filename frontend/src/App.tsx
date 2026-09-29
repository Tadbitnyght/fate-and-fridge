import { StatusPage } from './pages/StatusPage'

export function App() {
  return (
    <div className="app-shell">
      <header className="app-header">
        <h1>Fate &amp; Fridge</h1>
        <p className="app-subtitle">What&rsquo;s for dinner? &middot; Operation Dinner Plate</p>
      </header>

      <main className="app-main">
        <StatusPage />
      </main>

      <footer className="app-footer">
        <p>Self-hosted &middot; no telemetry &middot; your household data stays home</p>
      </footer>
    </div>
  )
}
