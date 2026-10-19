import { apiDownload, apiRequest, type DownloadedFile } from './client'

/**
 * Typed client for the M12 admin audit contract (docs/API.md §16,
 * docs/M12_PLAN.md §1.5, §2.2).
 *
 * `GET /admin/audit` is Admin-only: the existing `admin` role (and the
 * single-user system actor, which is an Admin) is the authority — no new
 * permission key. The read is a merged, newest-first timeline of all
 * `audit_events` rows (`source = "audit"`) and the reroll/batch
 * `calendar_entry_history` rows (`source = "entry_history"`). This module only
 * describes the wire shapes; the server enforces authorization. JSON property
 * names are snake_case.
 */

/** Which store a merged timeline row came from. */
export type AuditSource = 'audit' | 'entry_history'

/** One row of the merged audit timeline (docs/API.md §16). */
export interface AuditLogItem {
  id: number
  source: AuditSource
  /** ISO-8601 instant; render through `formatTimestamp`. */
  recorded_at: string
  actor_user_id: number | null
  /** Resolved by the server; null for the system actor / unknown user. */
  actor_username: string | null
  event_type: string
  entity_type: string
  entity_id: string | null
  summary: string
  reason: string | null
  ip_address: string | null
  /** Stored JSON documents (or null), embedded as JSON values. */
  before: unknown
  after: unknown
  metadata: unknown
}

export interface AuditLogResponse {
  items: AuditLogItem[]
  /** Opaque keyset cursor for the next (older) page, or null on the last page. */
  next_cursor: string | null
}

export interface AuditLogParams {
  /** Exact `event_type` match; history kinds map to `calendar.*` viewer events. */
  event_type?: string
  /** Exact `entity_type` match. */
  entity_type?: string
  /** Exact acting user id (`actor_user_id` / `changed_by_user_id`). */
  actor_user_id?: number
  source?: AuditSource
  /** Household-local `yyyy-MM-dd`, inclusive of the whole day. */
  from?: string
  /** Household-local `yyyy-MM-dd`, inclusive of the whole day. */
  to?: string
  /** Page size; server default 50, maximum 200. */
  limit?: number
  /** Opaque cursor from the previous page's `next_cursor`. */
  cursor?: string
}

export interface AuditCatalogueOption {
  value: string
  label: string
}

/**
 * Static, documented catalogue mirroring the backend M12-01 event types
 * (docs/M12_PLAN.md §1.3, §2.1; `AuditEventTypes` in
 * `Services/AuditService.cs`). Event types stay open strings server-side, so
 * the picker offers the known set (plus an "All" option in the UI) — it is
 * never derived from the current page of results. The `calendar.reroll` /
 * `calendar.batch_reroll` / `calendar.fill_empty` / `calendar.undo` entries are
 * the viewer-mapped names for the reroll `calendar_entry_history` rows
 * (docs/API.md §16).
 */
export const AUDIT_EVENT_TYPES: AuditCatalogueOption[] = [
  // M2 reference-data events.
  { value: 'created', label: 'Created' },
  { value: 'updated', label: 'Updated' },
  { value: 'retired', label: 'Retired' },
  { value: 'deleted', label: 'Deleted' },
  { value: 'merged', label: 'Merged' },
  { value: 'factor_changed', label: 'Factor changed' },
  { value: 'reordered', label: 'Reordered' },
  { value: 'image_updated', label: 'Image updated' },
  { value: 'image_removed', label: 'Image removed' },
  // Calendar meal changes.
  { value: 'calendar.created', label: 'Calendar entry created' },
  { value: 'calendar.updated', label: 'Calendar entry updated' },
  { value: 'calendar.deleted', label: 'Calendar entry deleted' },
  { value: 'calendar.locked', label: 'Calendar date locked' },
  { value: 'calendar.unlocked', label: 'Calendar date unlocked' },
  // Reroll records surfaced from the entry history (viewer-mapped names).
  { value: 'calendar.reroll', label: 'Calendar reroll (history)' },
  { value: 'calendar.batch_reroll', label: 'Calendar batch reroll (history)' },
  { value: 'calendar.fill_empty', label: 'Calendar fill empty (history)' },
  { value: 'calendar.undo', label: 'Calendar reroll undone (history)' },
  // Recipe lifecycle.
  { value: 'recipe.retired', label: 'Recipe retired' },
  // Users and permissions.
  { value: 'user.created', label: 'User created' },
  { value: 'user.renamed', label: 'User renamed' },
  { value: 'user.role_changed', label: 'User role changed' },
  { value: 'user.disabled', label: 'User disabled' },
  { value: 'user.enabled', label: 'User enabled' },
  { value: 'user.password_reset', label: 'User password reset' },
  { value: 'user.permissions_changed', label: 'User permissions changed' },
  // Settings.
  { value: 'settings.household_updated', label: 'Household settings updated' },
  { value: 'settings.security_updated', label: 'Security settings updated' },
  { value: 'settings.auth_mode_switched', label: 'Auth mode switched' },
  // Groceries.
  { value: 'groceries.manual_item_added', label: 'Manual grocery item added' },
  { value: 'groceries.manual_item_updated', label: 'Manual grocery item updated' },
  { value: 'groceries.manual_item_removed', label: 'Manual grocery item removed' },
  { value: 'groceries.staple_created', label: 'Grocery staple created' },
  { value: 'groceries.staple_updated', label: 'Grocery staple updated' },
  { value: 'groceries.staple_retired', label: 'Grocery staple retired' },
  // Auth mirror.
  { value: 'auth.login_succeeded', label: 'Login succeeded' },
  { value: 'auth.login_failed', label: 'Login failed' },
  { value: 'auth.logout', label: 'Logout' },
  // M13 backup/restore stubs (defined now, emitted by M13).
  { value: 'backup.created', label: 'Backup created' },
  { value: 'backup.restored', label: 'Backup restored' },
]

/**
 * Static catalogue mirroring the backend `AuditEntityTypes` (docs/M12_PLAN.md
 * §2.1). Entity types stay open strings server-side; reroll history rows are
 * always `calendar_entry`.
 */
export const AUDIT_ENTITY_TYPES: AuditCatalogueOption[] = [
  { value: 'department', label: 'Department' },
  { value: 'unit', label: 'Unit' },
  { value: 'ingredient', label: 'Ingredient' },
  { value: 'protein', label: 'Protein' },
  { value: 'tag', label: 'Tag' },
  { value: 'recipe', label: 'Recipe' },
  { value: 'calendar_entry', label: 'Calendar entry' },
  { value: 'user', label: 'User' },
  { value: 'settings', label: 'Settings' },
  { value: 'grocery_manual_item', label: 'Manual grocery item' },
  { value: 'grocery_staple', label: 'Grocery staple' },
  { value: 'session', label: 'Session' },
  { value: 'auth', label: 'Auth' },
  { value: 'backup', label: 'Backup' },
]

export const AUDIT_SOURCES: AuditSource[] = ['audit', 'entry_history']

export const AUDIT_SOURCE_LABELS: Record<AuditSource, string> = {
  audit: 'Audit',
  entry_history: 'Calendar history',
}

const EVENT_TYPE_LABELS = new Map(AUDIT_EVENT_TYPES.map((option) => [option.value, option.label]))
const ENTITY_TYPE_LABELS = new Map(AUDIT_ENTITY_TYPES.map((option) => [option.value, option.label]))

/** Turns an open-string value into a readable label (fallback for unknown ones). */
function humanize(value: string): string {
  const words = value.replace(/[._]/g, ' ').trim()
  if (words.length === 0) {
    return value
  }
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/** A readable event-type label; unknown (open-string) values are humanized. */
export function auditEventTypeLabel(eventType: string): string {
  return EVENT_TYPE_LABELS.get(eventType) ?? humanize(eventType)
}

/** A readable entity-type label; unknown (open-string) values are humanized. */
export function auditEntityTypeLabel(entityType: string): string {
  return ENTITY_TYPE_LABELS.get(entityType) ?? humanize(entityType)
}

function appendFilterParams(query: URLSearchParams, params: AuditLogParams): void {
  if (params.event_type !== undefined && params.event_type.length > 0) {
    query.set('event_type', params.event_type)
  }
  if (params.entity_type !== undefined && params.entity_type.length > 0) {
    query.set('entity_type', params.entity_type)
  }
  if (params.actor_user_id !== undefined) {
    query.set('actor_user_id', String(params.actor_user_id))
  }
  if (params.source !== undefined) {
    query.set('source', params.source)
  }
  if (params.from !== undefined && params.from.length > 0) {
    query.set('from', params.from)
  }
  if (params.to !== undefined && params.to.length > 0) {
    query.set('to', params.to)
  }
}

function auditQuery(params: AuditLogParams): string {
  const query = new URLSearchParams()
  appendFilterParams(query, params)
  if (params.limit !== undefined) {
    query.set('limit', String(params.limit))
  }
  if (params.cursor !== undefined && params.cursor.length > 0) {
    query.set('cursor', params.cursor)
  }
  const text = query.toString()
  return text.length > 0 ? `?${text}` : ''
}

/** Reads one newest-first page of the merged audit timeline (Admin only). */
export function getAuditLog(params: AuditLogParams = {}): Promise<AuditLogResponse> {
  return apiRequest<AuditLogResponse>(`/admin/audit${auditQuery(params)}`)
}

/** The two file formats the audit export offers (docs/API.md §16). */
export type AuditExportFormat = 'csv' | 'json'

/**
 * Builds the export query for the **current filtered view**: every viewer
 * filter is forwarded, but the paging controls (`limit`, `cursor`) are
 * deliberately omitted because an export is the whole filtered view.
 */
export function auditExportPath(params: AuditLogParams, format: AuditExportFormat): string {
  const query = new URLSearchParams()
  appendFilterParams(query, params)
  query.set('format', format)
  return `/admin/audit/export?${query.toString()}`
}

/**
 * Downloads the current filtered audit view as CSV or JSON (Admin only). A
 * `400 VALIDATION_FAILED` (unknown format or over the 100k row cap) rejects with
 * an {@link ApiError}; the caller surfaces the message.
 */
export function downloadAuditLog(
  params: AuditLogParams,
  format: AuditExportFormat,
): Promise<DownloadedFile> {
  return apiDownload(auditExportPath(params, format), `audit-log.${format}`)
}

/** Body of the confirmed retention prune (`POST /admin/audit/prune`). */
export interface AuditPruneRequest {
  reason: string
  /** Must be exactly `PRUNE`; the server verifies it independently of the UI. */
  confirmation: string
}

/** Result of one executed prune (docs/API.md §16). */
export interface AuditPruneResult {
  deleted_count: number
  /** ISO-8601 instant of the age cutoff that was applied. */
  cutoff: string
  retention_days: number
}

/**
 * Permanently deletes audit entries older than the configured retention window
 * (Admin only). The server enforces the reason, the exact confirmation, and the
 * "keep forever" refusal.
 */
export function pruneAuditLog(body: AuditPruneRequest): Promise<AuditPruneResult> {
  return apiRequest<AuditPruneResult>('/admin/audit/prune', { method: 'POST', body })
}
