import { useQuery } from '@tanstack/react-query'

import { fetchDatabaseHealth, fetchHealth } from '../api/client'

interface StatusBadgeProps {
  ok: boolean
  label: string
}

function StatusBadge({ ok, label }: StatusBadgeProps) {
  return (
    <span className={ok ? 'status-badge status-badge--ok' : 'status-badge status-badge--bad'}>
      {label}
    </span>
  )
}

export function StatusPanel() {
  const health = useQuery({ queryKey: ['health'], queryFn: fetchHealth })
  const database = useQuery({ queryKey: ['health', 'db'], queryFn: fetchDatabaseHealth })

  const appOk = health.isSuccess && health.data.status === 'ok'
  const databaseOk = database.isSuccess && database.data.status === 'ok'

  return (
    <section className="status-panel" aria-labelledby="status-heading">
      <h3 id="status-heading">System status</h3>

      <dl className="status-grid">
        <div className="status-row">
          <dt>Application</dt>
          <dd role="status">
            {health.isPending && <span>Checking…</span>}
            {health.isError && <StatusBadge ok={false} label="Unreachable" />}
            {health.isSuccess && (
              <>
                <StatusBadge ok={appOk} label={appOk ? 'Operational' : health.data.status} />
                <span className="status-detail">
                  {health.data.app} v{health.data.version} ({health.data.environment})
                </span>
              </>
            )}
          </dd>
        </div>

        <div className="status-row">
          <dt>Database</dt>
          <dd role="status">
            {database.isPending && <span>Checking…</span>}
            {database.isError && <StatusBadge ok={false} label="Unreachable" />}
            {database.isSuccess && <StatusBadge ok={databaseOk} label={database.data.database} />}
          </dd>
        </div>
      </dl>
    </section>
  )
}
