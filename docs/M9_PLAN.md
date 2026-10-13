# Fate & Fridge — Milestone 9 Plan (Live Sync)

Status: **approved, ready to execute**
Approved: 2026-10-12 (product owner answered the full M9 decision set)
Purpose: authoritative M9 runbook. A fresh session should execute M9 from this
file plus `AGENTS.md`, `docs/MILESTONES.md`, and the existing product, API,
architecture, database, security, and testing specifications without asking
further product questions. Where this file and an older spec disagree, escalate
to the Lead before implementing.

---

## 1. Locked product decisions

1. **Transport.** A **SignalR hub** (docs/ARCHITECTURE.md §17 and its container
   diagram). The server side ships in the ASP.NET Core shared framework — **no
   new backend package**; the tests add `Microsoft.AspNetCore.SignalR.Client`
   and the frontend adds `@microsoft/signalr`. **No Redis, no backplane, no
   reverse proxy, no compose change**: one app instance, one household.
2. **Endpoint.** The hub is mapped at **`/hub/live`** and documented there.
   (`docs/API.md` §19's suggested `/ws` is superseded by this decision and §19
   is corrected to `/hub/live`.) The hub is mapped **before** `MapFrontend()`.
3. **Authentication.** The upgrade requires the existing opaque `faf_session`
   cookie in multiuser mode (no valid session → the connection is rejected, no
   anonymous socket). In **single-user mode** there is no cookie, so the socket
   acts as the **system actor** — live sync must work on the household's
   single-user TrueNAS deployment. **No tokens in query strings or URLs.**
4. **Direction.** **Server-to-client notifications only.** No client-invokable
   hub methods (docs/SECURITY.md §16: "prefer sockets for notifications, not
   arbitrary remote procedure calls"). All writes stay on the REST API, which
   already enforces permissions, CSRF, and historical rules.
5. **Event types.** Six stable types:
   - `calendar.updated` — any calendar change other than a lock toggle
     (create/edit/delete/reroll/batch preview is not a change, batch apply/undo).
   - `calendar.lock_changed` — a lock or unlock of a date, **and only that**;
     lock toggles do **not** also emit `calendar.updated` (no double invalidation).
   - `groceries.updated` — manual items, staples, add-to-list, and check/uncheck.
     Per-user preferences (hide-checked, remembered range) are private and
     **never** broadcast.
   - `recipe.updated` — **any household-visible recipe write**: create, edit
     (name, ingredients, units/quantities, proteins, tags, diet flags, status),
     image set/replace/remove, delete.
   - `settings.updated` — household settings, security settings, **and
     reference-data writes** (departments incl. name/`sort_order`, units,
     ingredients + merges, proteins, tags), because those change grocery
     ordering and recipe displays on other devices. Per-user account settings do
     **not** broadcast.
   - `session.revoked` — sent to the affected session's connections immediately
     before they are closed (see decision 13).
6. **Payload.** A **coarse, stable envelope** — clients invalidate/refetch
   (docs/ARCHITECTURE.md §17, `docs/API.md` §19). No entity bodies, no diffs.
   Envelope fields: `type`, `entity`, `id`, `operation_id`, `actor_user_id`,
   `timestamp` (all but `type` nullable as applicable). Delivered via a single
   client method (`connection.on('event', …)`) with `type` as the discriminator,
   so a new event type never needs a new client method.
7. **Delivery.** Broadcast to **every connected session of the household**,
   including the originating one (invalidating a fresh query is idempotent and
   avoids connection bookkeeping). The envelope carries `actor_user_id` so a
   client may skip its own echo if it wants.
8. **Reconnect.** Client auto-reconnect with backoff; on **every** (re)connect it
   invalidates all live-sync query keys once, reconciling anything missed while
   offline. **No server-side event log, sequence numbers, or replay.**
9. **Client status UI.** An accessible, **non-blocking** indicator (`Live` /
   `Reconnecting…`) with `aria-live`; data stays usable and REST writes keep
   working while the socket is down.
10. **Stale writes.** Satisfied by the **existing** conflict handling plus
    invalidation refetch (M6 `UNDO_CONFLICT`, M7 `BATCH_CONFLICT`/
    `BATCH_DRAFT_EXPIRED`/batch-undo conflict, historical immutability). M9 adds
    **no** version/ETag token scheme; the residual gap analysis is deferred to
    the M14 hardening pass and recorded there.
11. **Guards.** The existing `IpAllowlistMiddleware` applies to the upgrade
    (health/loopback exemptions unchanged); a **same-origin** `Origin` check is
    required for browsers; and documented caps apply: inbound message size
    **32 KB**, SignalR default keepalive/timeouts, `EnableDetailedErrors = false`,
    and a bounded number of concurrent connections per session/household.
12. **CSRF/negotiation.** The SPA connects with **`skipNegotiation: true` over
    the WebSockets transport only**, so no `/negotiate` POST is made; the hub
    path is additionally exempted from `CsrfMiddleware` (a body-less, same-origin,
    cookie-authenticated upgrade). `SetupGatingMiddleware` and
    `MustChangePasswordMiddleware` still apply to the connect request.
13. **Revocation.** A valid session is required at connect **and** the session is
    re-validated per broadcast; logging out, an admin revoking sessions,
    disabling the user, or the session expiring emits `session.revoked` to that
    session's connections and **aborts** them. A revoked session cannot keep
    receiving household events. If the per-broadcast re-validation read itself
    fails, the broadcast **fails closed**: that event is skipped for every
    connection (bounded staleness — it self-heals on the next event or a client
    reconnect) rather than delivered to unvalidated sockets; the connections are
    not aborted, since a read failure is not proof of revocation.
14. **Scope.** M9 is live-sync notifications + client invalidation + the guards
    and tests around them. It is **not** a persisted event log, presence/typing
    indicators, collaborative editing, RPC over sockets, or optimistic
    concurrency tokens.
15. **No schema change.** Live sync is stateless: **no new tables, no migration**.
16. **Verification.** The final issue mirrors the M4–M8 sweep: the §4 battery, an
    independent review, docs, the manifest M9 `complete` with evidence, tag
    `m9-complete`, and closing the tracking issue.

---

## 2. M9 architecture and contracts

### 2.1 Backend layout (`src/FateAndFridge.Api/`)

- `LiveSync/LiveSyncEvent.cs` — the envelope record + the `LiveSyncEventType`
  constants (`calendar.updated`, `calendar.lock_changed`, `groceries.updated`,
  `recipe.updated`, `settings.updated`, `session.revoked`).
- `LiveSync/ILiveSyncPublisher.cs` — `PublishAsync(LiveSyncEvent)`; the seam the
  write paths call after a successful commit. A recording fake is registered in
  tests so the event matrix is testable without sockets.
- `LiveSync/SignalRLiveSyncPublisher.cs` — the SignalR-backed implementation
  (broadcasts to the `household` group; routes `session.revoked` to a session's
  connections and aborts them).
- `LiveSync/LiveSyncConnectionRegistry.cs` — singleton mapping
  `sessionId → connectionIds` (+ actor metadata) for revocation; tracks the
  per-session connection cap.
- `LiveSync/LiveSyncHub.cs` — `[Authorize]`-equivalent gating done in the
  existing middleware chain; `OnConnectedAsync` resolves the actor via the
  existing `CurrentActorProvider`/session, rejects a missing/invalid multiuser
  session, enforces the same-origin check and the connection cap, and joins the
  `household` group; `OnDisconnectedAsync` unregisters.
- `Endpoints/LiveSyncEndpoints.cs` (or a small `MapLiveSync` extension) — maps
  the hub at `/hub/live` before `MapFrontend()`.
- No new entity, table, or migration.

### 2.2 Event contract (recorded in `docs/API.md` §19 before completion)

Envelope:

```json
{
  "type": "groceries.updated",
  "entity": "groceries",
  "id": null,
  "operation_id": null,
  "actor_user_id": 3,
  "timestamp": "2026-10-12T13:20:41.000Z"
}
```

| Type | Emitted by |
|------|-----------|
| `calendar.updated` | calendar entry create/update/delete; single reroll apply + undo; batch apply + batch undo |
| `calendar.lock_changed` | lock/unlock of a date |
| `groceries.updated` | manual item create/update/delete; staple create/update; add-to-list; check/uncheck |
| `recipe.updated` | recipe create/update (incl. ingredients/proteins/tags/status/diet), image set/replace/remove, delete |
| `settings.updated` | household settings; security settings; department/unit/ingredient(+merge)/protein/tag writes |
| `session.revoked` | logout, admin session revocation, user disable, session expiry |

Client method: `event`. Group: `household`. Emitted only after a successful,
committed write — never on a failed/rolled-back operation or a validation error.

### 2.3 Frontend (`src/features/live-sync/`)

- `LiveSyncProvider.tsx` — one connection per tab, created inside/next to
  `AuthProvider`; `withUrl('/hub/live', { skipNegotiation: true, transport: WebSockets })`;
  automatic reconnect with backoff; on every (re)connect a full live-sync
  invalidation pass; closes on logout.
- `liveSyncKeys.ts` — the documented **event → query-key mapping**:

  | Event | Invalidated query keys |
  |-------|------------------------|
  | `calendar.updated` | calendar, operations, dashboard |
  | `calendar.lock_changed` | calendar, operations |
  | `groceries.updated` | groceries (list + staples) |
  | `recipe.updated` | recipes, groceries, calendar |
  | `settings.updated` | settings, reference-data, groceries, recipes |
  | `session.revoked` | force sign-out / reload |

- `LiveSyncStatus.tsx` — the accessible status indicator (in `AppShell`).
- Reuses TanStack Query's `queryClient.invalidateQueries`; **no component talks
  to the socket directly**, and no view duplicates server state from events.

### 2.4 Not in the wire/persistence layer

No event persistence, no replay cursor, no per-connection subscriptions, no
client→server methods, no presence.

---

## 3. Issue breakdown and execution order

| # | Issue | Deliverable | Depends on |
|---|-------|-------------|-----------|
| M9-01 | Hub foundation | `AddSignalR`, hub at `/hub/live` (mapped before `MapFrontend`), session + single-user auth, same-origin/IP-allowlist guards, CSRF exemption, connection registry + cap, `ILiveSyncPublisher` + recording fake, `session.revoked` plumbing; no events yet | — |
| M9-02 | Calendar events | `calendar.updated` on entry CRUD/reroll/batch apply/undo and `calendar.lock_changed` on lock toggles | M9-01 |
| M9-03 | Grocery events | `groceries.updated` on manual items, staples, add-to-list, check/uncheck (preferences excluded) | M9-01 |
| M9-04 | Recipe events | `recipe.updated` on all household-visible recipe writes incl. images and delete | M9-01 |
| M9-05 | Settings + reference-data events | `settings.updated` for household/security settings and department/unit/ingredient(+merge)/protein/tag writes | M9-01 |
| M9-06 | Session revocation | `session.revoked` + forced abort on logout, admin revocation, user disable, session expiry; per-broadcast re-validation | M9-01 |
| M9-07 | Frontend live sync | provider, reconnect + full refetch, event→key mapping, accessible status indicator, logout teardown | M9-01…M9-06 |
| M9-08 | Two-device sync tests | backend two-client matrix, hermetic Playwright `routeWebSocket` invalidation, `e2e/stack` two-browser-context flow | M9-02…M9-07 |
| M9-09 | Security/limits battery | origin, size, IP allowlist on upgrade, no-RPC, revocation, reconnect-storm and cap tests | M9-01…M9-08 |
| M9-10 | M9 verification sweep | independent §4 battery, docs (`API.md` §19, `ARCHITECTURE.md` §17, `TESTING.md` §10, `CHANGELOG.md`), manifest M9 `complete`, live rebuild/redeploy + two-device live proof, tag `m9-complete`, close tracking issue | M9-01…M9-09 |

Milestone mapping: 1→M9-01; 2→M9-02; 3→M9-03; 4→M9-02; 5→M9-07; 6→M9-08;
verification→M9-10. The M9 additions of `recipe.updated`, `settings.updated`, and
reference-data coverage come from `docs/ARCHITECTURE.md` §17 and this decision set.

---

## 4. M9 verification battery

- **Auth:** no anonymous socket in multiuser mode; a valid `faf_session` connects;
  a socket works in single-user mode as the system actor; a must-change-password
  or pre-setup session cannot connect; no token accepted via query string.
- **Events:** each write in the §2.2 table emits exactly its type to **connected
  sessions of the household**, including the originator; a failed/rolled-back or
  validation-error write emits nothing; per-user preference writes emit nothing.
- **Lock separation:** a lock toggle emits only `calendar.lock_changed`; other
  calendar writes emit only `calendar.updated`.
- **Envelope:** `type`/`entity`/`id`/`operation_id`/`actor_user_id`/`timestamp`
  present and correct; no entity bodies; timestamps UTC; actor null for the
  system actor.
- **Revocation:** logout / admin revocation / user disable / expiry emits
  `session.revoked` and aborts those sockets; a revoked session receives nothing
  afterwards; other sessions are unaffected.
- **Guards:** same-origin rejects a foreign `Origin`; the IP allowlist applies to
  the upgrade (loopback/health exemptions unchanged); a >32 KB inbound frame is
  rejected; the per-session connection cap holds; detailed errors are off.
- **No RPC:** no client-invokable methods; a client cannot mutate state over the
  socket; unknown client invocations are rejected.
- **Reconnect:** a client that reconnects after a missed event ends up consistent
  (full refetch); a reconnect storm does not error the server or leak connections.
- **Frontend:** each event invalidates exactly the mapped query keys; the status
  indicator reflects connected/reconnecting accessibly; logout closes the socket.
- **Two-device:** TESTING.md §10 — grocery check on A updates B; manual grocery
  added on A updates B; calendar meal change updates B; lock change updates B;
  a stale client's write is handled by the existing conflict paths.

---

## 5. Automation protocol

1. Read `AGENTS.md`, this file, the M9 section of `docs/MILESTONES.md`, and the
   issue before each issue. Decisions in §1 are final; contradictions stop
   execution and escalate to the Lead.
2. Use the Roslyn MCP first for every C# inspection/edit/diagnostic. Pass the
   **absolute** `.csproj` to `projectPath`
   (`/mnt/y/backend/src/FateAndFridge.Api/FateAndFridge.Api.csproj`); `filePath`
   resolves against the workspace root `/mnt/y/backend`. Confirm with
   `roslyn_get_project_info` (`is_msbuild_workspace: true`). Never use
   `String.Normalize` (invariant globalization).
3. Frontend gates: Vitest/Playwright workers time out on the 9p share `/mnt/y`,
   so run them from a local mirror under `/tmp/opencode/frontend` with a **real
   local `node_modules`** (`npm ci`; never symlink it to the 9p share; sync with
   `rsync --exclude node_modules`, copy formatter output back with `cp`). Never
   commit from the mirror. Add `ws: true` to the Vite dev proxy for `/hub/live`.
4. Backend gates: `dotnet format --verify-no-changes`, Release build, Release
   tests with `FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts`, and
   `dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api`
   (expected: no pending changes — M9 adds no schema).
   Do not use `dotnet test` (it misreports the MTP runner); run the test binary
   with `-class` filters.
5. The reviewer must independently execute the relevant tests and review auth,
   the event matrix, lock separation, revocation, guards/limits, no-RPC,
   reconnect consistency, and the frontend invalidation mapping.
6. At M9 start, set M9 `in_progress` in
   `frontend/src/features/progress/milestones.ts` and create issues. At M9-10,
   all gates must pass, then delegate a rebuild/redeploy to `docker-admin`
   (`scripts/deploy.sh up`) and verify the live instance (healthy; the hub
   handshakes; at least one cross-device sync on the running instance; and
   `/progress` shows M9 complete) before setting M9 `complete` with evidence,
   updating the progress tests and the Playwright progress assertion,
   `CHANGELOG.md`, and the docs, pushing `main`, tagging `m9-complete`, and
   closing the M9 tracking issue. See `AGENTS.md` → "Milestone completion gate".

### 5.1 Execution cadence

M9 is executed in **deliverable-sized sessions, not issue-sized ones**, with a
**checkpoint commit after every verified deliverable** (see `docs/M6_PLAN.md`
§5.1). One session owns one authorable *and* independently verifiable unit; commit
as soon as its gates pass; never leave more than one unverified unit in the
working tree. `implementer` runs at `steps: 80`, `reviewer` 28, `docker-admin`
16. The Lead orchestrates and does not author milestone code.

---

## 6. Explicitly deferred from M9

- Multi-instance / horizontal scaling and a Redis (or other) backplane: no
  demonstrated need; V1 is a single household on one instance.
- A durable event log, sequence numbers, and missed-event replay.
- Client→server RPC over the socket (writes stay REST-only).
- Presence, "who's viewing", typing-style indicators, and chat.
- Collaborative/optimistic editing and optimistic-concurrency version tokens
  (revisit in M14 hardening).
- Update-availability checks and the update notice: M13 (PRODUCT_SPEC §45).
- Push/browser notifications when the app is closed.
- Statistics/history live updates: M11.
- Fine-grained per-entry event payloads and per-user event filtering.
