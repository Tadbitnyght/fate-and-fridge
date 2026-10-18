import { useState, type JSX } from 'react'

import {
  AUDIT_ENTITY_TYPES,
  AUDIT_EVENT_TYPES,
  AUDIT_SOURCE_LABELS,
  AUDIT_SOURCES,
  auditEntityTypeLabel,
  auditEventTypeLabel,
  type AuditLogItem,
  type AuditLogParams,
  type AuditSource,
} from '../../api/audit'
import { ErrorScreen, LoadingScreen } from '../../components/Screens'
import { formatTimestamp } from '../../lib/dateFormat'
import { useAdminAccess } from './access'
import { AdminOnlyNotice } from './AdminOnlyNotice'
import { useAuditLog } from './useAuditLog'
import { useUsers } from './useUsers'

const PAGE_LIMIT = 50

function detailKey(item: AuditLogItem): string {
  return `${item.source}:${item.id}`
}

function detailPanelId(item: AuditLogItem): string {
  return `audit-detail-${item.source}-${item.id}`
}

/**
 * Admin-only audit viewer (M12-03, docs/API.md §16, docs/M12_PLAN.md §1.5,
 * §1.6, §2.3).
 *
 * A single read-only, newest-first timeline merging `audit_events` rows with
 * the reroll/batch `calendar_entry_history` rows. Filters are server-side; each
 * filter change starts a fresh first page, and "Load more" follows the opaque
 * `next_cursor`. There is deliberately no edit or delete affordance — audit
 * rows are append-only, and the server is the sole authority on access.
 */
export function AuditLogSection(): JSX.Element {
  const { isAdmin } = useAdminAccess()

  const [eventType, setEventType] = useState('')
  const [entityType, setEntityType] = useState('')
  const [actorId, setActorId] = useState('')
  const [source, setSource] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [expanded, setExpanded] = useState<string | null>(null)

  // The actor picker is fed by the existing users list; both stay unfetched
  // for a non-Admin, because the UI is not the authority (docs/SECURITY.md §4).
  const usersQuery = useUsers(isAdmin)

  const params: AuditLogParams = {
    event_type: eventType === '' ? undefined : eventType,
    entity_type: entityType === '' ? undefined : entityType,
    actor_user_id: actorId === '' ? undefined : Number(actorId),
    source: source === '' ? undefined : (source as AuditSource),
    from: from === '' ? undefined : from,
    to: to === '' ? undefined : to,
    limit: PAGE_LIMIT,
  }

  const auditQuery = useAuditLog(params, isAdmin)

  if (!isAdmin) {
    return (
      <section className="settings-section" aria-labelledby="audit-heading">
        <h3 id="audit-heading">Audit log</h3>
        <AdminOnlyNotice feature="The audit log" />
      </section>
    )
  }

  const users = usersQuery.data ?? []
  const rows = auditQuery.data?.pages.flatMap((page) => page.items) ?? []

  return (
    <section className="settings-section" aria-labelledby="audit-heading">
      <div className="settings-section__header">
        <h3 id="audit-heading">Audit log</h3>
      </div>

      <p className="page-note">
        Read-only history of household changes, newest first. Calendar history rows are reroll and
        batch records kept in the entry history. Audit entries cannot be edited or deleted here.
      </p>

      <div className="audit-filters" role="group" aria-label="Audit filters">
        <div className="form__field">
          <label htmlFor="audit-event-type">Event type</label>
          <select
            id="audit-event-type"
            value={eventType}
            onChange={(event) => setEventType(event.target.value)}
          >
            <option value="">All event types</option>
            {AUDIT_EVENT_TYPES.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="audit-entity-type">Entity type</label>
          <select
            id="audit-entity-type"
            value={entityType}
            onChange={(event) => setEntityType(event.target.value)}
          >
            <option value="">All entity types</option>
            {AUDIT_ENTITY_TYPES.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="audit-actor">Actor</label>
          <select
            id="audit-actor"
            value={actorId}
            onChange={(event) => setActorId(event.target.value)}
          >
            <option value="">Any actor</option>
            {users.map((user) => (
              <option key={user.id} value={user.id}>
                {user.is_active ? user.username : `${user.username} (disabled)`}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="audit-source">Source</label>
          <select
            id="audit-source"
            value={source}
            onChange={(event) => setSource(event.target.value)}
          >
            <option value="">All sources</option>
            {AUDIT_SOURCES.map((value) => (
              <option key={value} value={value}>
                {AUDIT_SOURCE_LABELS[value]}
              </option>
            ))}
          </select>
        </div>

        <div className="form__field">
          <label htmlFor="audit-from">From</label>
          <input
            id="audit-from"
            type="date"
            value={from}
            onChange={(event) => setFrom(event.target.value)}
          />
        </div>

        <div className="form__field">
          <label htmlFor="audit-to">To</label>
          <input
            id="audit-to"
            type="date"
            value={to}
            onChange={(event) => setTo(event.target.value)}
          />
        </div>
      </div>

      {auditQuery.isPending && <LoadingScreen label="Loading audit log…" />}

      {auditQuery.isError && (
        <ErrorScreen
          title="Cannot load the audit log"
          error={auditQuery.error}
          onRetry={() => void auditQuery.refetch()}
        />
      )}

      {auditQuery.isSuccess && rows.length === 0 && (
        <p className="stats-empty">No audit entries match these filters.</p>
      )}

      {rows.length > 0 && (
        <>
          <ol className="audit-list" aria-label="Audit entries">
            {rows.map((item) => {
              const key = detailKey(item)
              return (
                <AuditRow
                  key={key}
                  item={item}
                  expanded={expanded === key}
                  onToggle={() => setExpanded(expanded === key ? null : key)}
                />
              )
            })}
          </ol>

          {auditQuery.hasNextPage && (
            <div className="audit-list__more">
              <button
                type="button"
                onClick={() => void auditQuery.fetchNextPage()}
                disabled={auditQuery.isFetchingNextPage}
              >
                {auditQuery.isFetchingNextPage ? 'Loading more…' : 'Load more'}
              </button>
            </div>
          )}
        </>
      )}
    </section>
  )
}

function AuditRow({
  item,
  expanded,
  onToggle,
}: {
  item: AuditLogItem
  expanded: boolean
  onToggle: () => void
}) {
  const isHistory = item.source === 'entry_history'
  const panelId = detailPanelId(item)
  const entity = auditEntityTypeLabel(item.entity_type)
  const entityLabel = item.entity_id === null ? entity : `${entity} #${item.entity_id}`

  return (
    <li className="audit-row">
      <div className="audit-row__head">
        <time className="audit-row__time" dateTime={item.recorded_at}>
          {formatTimestamp(item.recorded_at)}
        </time>
        <span className="audit-row__event">{auditEventTypeLabel(item.event_type)}</span>
        <span className={isHistory ? 'tag audit-source--history' : 'tag'}>
          {AUDIT_SOURCE_LABELS[item.source]}
        </span>
        {isHistory && <span className="audit-row__kind">Reroll record</span>}
      </div>

      <p className="audit-row__summary">{item.summary}</p>

      <p className="audit-row__meta">
        <span className="audit-row__entity">{entityLabel}</span>
        <span>Actor: {item.actor_username ?? 'System'}</span>
        {item.reason !== null && item.reason.length > 0 && <span>Reason: {item.reason}</span>}
        {item.ip_address !== null && <span>IP {item.ip_address}</span>}
      </p>

      <button
        type="button"
        className="audit-row__toggle"
        aria-expanded={expanded}
        aria-controls={panelId}
        onClick={onToggle}
      >
        {expanded ? 'Hide details' : 'Details'}
      </button>

      {expanded && <AuditDetail id={panelId} item={item} />}
    </li>
  )
}

function AuditDetail({ id, item }: { id: string; item: AuditLogItem }) {
  return (
    <div id={id} className="audit-row__detail">
      <dl className="audit-detail">
        <div>
          <dt>Recorded</dt>
          <dd>{formatTimestamp(item.recorded_at)}</dd>
        </div>
        <div>
          <dt>Source</dt>
          <dd>{AUDIT_SOURCE_LABELS[item.source]}</dd>
        </div>
        <div>
          <dt>Event type</dt>
          <dd>{item.event_type}</dd>
        </div>
        <div>
          <dt>Entity</dt>
          <dd>
            {item.entity_type}
            {item.entity_id === null ? '' : ` #${item.entity_id}`}
          </dd>
        </div>
        <div>
          <dt>Actor</dt>
          <dd>{item.actor_username ?? 'System'}</dd>
        </div>
        {item.reason !== null && item.reason.length > 0 && (
          <div>
            <dt>Reason</dt>
            <dd>{item.reason}</dd>
          </div>
        )}
        {item.ip_address !== null && (
          <div>
            <dt>IP address</dt>
            <dd>{item.ip_address}</dd>
          </div>
        )}
      </dl>

      <AuditJson label="Before" value={item.before} />
      <AuditJson label="After" value={item.after} />
      <AuditJson label="Metadata" value={item.metadata} />
    </div>
  )
}

function AuditJson({ label, value }: { label: string; value: unknown }) {
  const hasValue = value !== null && value !== undefined
  return (
    <div className="audit-json">
      <h4 className="audit-json__label">{label}</h4>
      {hasValue ? (
        <pre className="audit-json__value">{JSON.stringify(value, null, 2)}</pre>
      ) : (
        <p className="audit-json__empty">None recorded.</p>
      )}
    </div>
  )
}
