# M9 Verification — Live Sync

Status: **complete** (2026-10-13, tag `m9-complete`). Tracking issue: #130
(M9-01…M9-10 are #121–#130). The milestone is marked `complete` in the progress
manifest and the live deployment was rebuilt, redeployed, and verified (see
**Live deployment verification** below).

M9 delivers **household live sync**: an authenticated SignalR hub that pushes
coarse change notifications to every connected session, and a frontend that
invalidates/refetches the mapped queries. It is **not** a persisted event log,
presence/typing, collaborative editing, RPC over sockets, or optimistic
concurrency tokens.

## Schema, entities, and routes

**No schema change.** Live sync is stateless — there is **no migration**, no new
table, no event log, no sequence, and no entity. M9 adds backend `LiveSync/`
services only:

- `LiveSyncEvent` + `LiveSyncEventType` — the envelope record and the six type
  constants.
- `ILiveSyncPublisher` / `SignalRLiveSyncPublisher` — the publish seam and the
  SignalR-backed implementation.
- `LiveSyncConnectionRegistry` — `sessionId → connectionIds` tracking (plus the
  per-session/household caps) for revocation.
- `LiveSyncHub` — the hub mapped at `/hub/live`.
- `LiveSyncPaths` — the shared hub path.

`dotnet ef migrations has-pending-model-changes` is expected to be **empty**, and
is (see Evidence). `docs/DATABASE.md` records the no-schema note.

## Hub contract (summary)

The full contract is in `docs/API.md` §19; `docs/ARCHITECTURE.md` §17 carries the
event list.

- **Endpoint:** SignalR hub at **`/hub/live`**, mapped before `MapFrontend()`.
- **Transport:** WebSockets only with `skipNegotiation: true` (no `/negotiate`
  POST); the hub path is exempt from `CsrfMiddleware`, while
  `SetupGatingMiddleware` and `MustChangePasswordMiddleware` still apply.
- **Auth:** the opaque `faf_session` cookie in multiuser mode (no cookie → the
  connection is refused; no anonymous socket, no token in the URL); in
  single-user mode the socket acts as the **system actor** (`actor_user_id` is
  `null`).
- **Direction:** server → client only; **no client-invokable methods**; all
  writes stay on the REST API. One client method, `event`.
- **Envelope:** `{ type, entity, id, operation_id, actor_user_id, timestamp }`
  (snake_case; `timestamp` ISO-8601 UTC offset; only `type` required).
- **Events (six):** `calendar.updated`, `calendar.lock_changed`,
  `groceries.updated`, `recipe.updated`, `settings.updated`, `session.revoked` —
  emitted only after a committed write, broadcast to the `household` group
  (originator included).
- **Guards:** same-origin `Origin` check, the existing IP allowlist on the
  upgrade, a 32 KiB inbound frame cap, `EnableDetailedErrors = false`, and
  per-session (8) / per-household (32) connection caps.
- **Revocation:** a valid session is required at connect and re-validated per
  broadcast; logout/admin revocation/user disable/expiry emits `session.revoked`
  and aborts those sockets (a re-validation read failure fails closed).
- **Client:** one connection per tab, automatic reconnect with backoff, a full
  query-invalidation pass on every (re)connect (no replay), the documented
  event → query-key mapping, an accessible non-blocking indicator, and socket
  teardown on logout.

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet format --verify-no-changes
dotnet build -c Release FateAndFridge.slnx
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
# Full suite (rebuild first; run the binary directly — do not use `dotnet test`):
$ARTIFACTS
# Or the M9 battery only:
$ARTIFACTS -class "*M9LiveSync*" -class "*M9SessionRevocation*"
```

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers): `npm run lint && npm run format:check
&& npm run typecheck && npm test`, then `npm run build`, then
`npx playwright test`.

The real-stack two-device suite is skipped unless `E2E_BASE_URL` points at a
fresh/unconfigured multiuser stack **and** the `E2E_STACK_LIVE_SYNC=1` opt-in is
set (both stack suites consume the single-shot first-run setup, so this one is
gated to keep it out of the default run — see `BUG-010`):

```bash
E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser \
  E2E_STACK_LIVE_SYNC=1 npx playwright test e2e/stack/live-sync.spec.ts
```

It has been **executed and passed** against a disposable stack (see **Live
deployment verification**).

The M9 backend fixtures (all in the `M1 setup` collection) share one
Testcontainers/PostgreSQL container and reset the volatile rows per test. The hub
tests open real `HubConnection`s over the in-process TestServer (LongPolling; the
SPA uses `skipNegotiation` WebSockets in production). M9 adds **no schema**, so
`has-pending-model-changes` is empty.

## §4 battery → coverage

The exhaustive per-area coverage map is **`docs/M9_TEST_MATRIX.md`** (M9-08/M9-09)
— it lists every §4 battery bullet from `docs/M9_PLAN.md` §4 and its primary
automated test(s), backend first, then frontend, and records the gaps closed in
the M9-08/M9-09 sweep. Reproduced compactly:

| §4 area | Primary coverage (see `docs/M9_TEST_MATRIX.md` for the full list) |
|---------|--------------------------------------------------------------------|
| Auth (no anonymous socket; valid session; single-user system actor; pre-setup / must-change refused; no URL token) | `M9LiveSyncHubTests` |
| Events (each §2.2 write emits its type; negative/rolled-back/read writes emit nothing; per-user preferences excluded; full route inventory) | `M9LiveSyncEventMatrixTests` |
| Two-client broadcast (household group incl. the originator, every type) | `M9LiveSyncEventMatrixTests`, `M9LiveSyncHubTests.Broadcast_reaches_both_clients_and_revocation_closes_only_that_session` |
| Lock separation (toggle emits only `calendar.lock_changed`) | `M9LiveSyncEventMatrixTests.Calendar_lock_emits_only_calendar_lock_changed` (+ unlock, + put) |
| Envelope (`type`/`entity`/`id`/`operation_id`/`actor_user_id`/`timestamp`, no bodies, UTC, null system actor) | `M9LiveSyncEventMatrixTests.AssertSingleEvent` (+ actor/system-actor cases), `M9LiveSyncHubTests.AssertSessionRevokedEnvelope` |
| Revocation (logout/revoke/disable/expiry emits + aborts; revoked receives nothing; others unaffected; per-broadcast re-validation; fail-closed read failure) | `M9LiveSyncHubTests` |
| Guards (foreign origin; IP allowlist on upgrade; >32 KiB frame; per-session/household caps; detailed errors off; missing request context; register/unregister race) | `M9LiveSyncHubTests` |
| No RPC (reflection surface + unknown invocation) | `M9LiveSyncHubTests.Hub_exposes_no_client_invokable_methods`, `A_client_invocation_of_an_unknown_method_errors_and_changes_nothing` |
| Reconnect (full refetch; storm does not error/leak) | `LiveSyncProvider.test.tsx`, `liveSyncKeys.test.ts`, `M9LiveSyncHubTests.Reconnect_storm_drains_the_registry_and_keeps_the_caps` |
| Frontend (event → key mapping; accessible status; logout/session.revoked teardown; hermetic socket) | `liveSyncKeys.test.ts`, `LiveSyncStatus.test.tsx`, `LiveSyncProvider.test.tsx`, hermetic `e2e/live-sync.spec.ts` |
| Two-device (check/manual/calendar/lock on A → B) | real-stack `e2e/stack/live-sync.spec.ts` (M9-08) |
| Two-device — stale write | Deferred to the existing M6/M7 conflict suites; M9 adds no version token (`docs/M9_PLAN.md` §1.10) |

## Evidence at completion

Recorded on the M9-10 commit chain (branch `feature/m9-live-sync`, HEAD
`4b10338`). The milestone is marked `complete` in the progress manifest; the
`m9-complete` tag points at the final verification commit after the live checks
below.

| Gate | Result |
|------|--------|
| `dotnet format --verify-no-changes` | clean (exit 0) |
| Backend Release build (`FateAndFridge.slnx`) | 0 warnings / 0 errors |
| `dotnet ef migrations has-pending-model-changes` | no pending model changes |
| Backend full suite (`FateAndFridge.Api.Tests`) | **Total 2114, Errors 0, Failed 0, Skipped 0, Not Run 0** (1219.995 s) |
| Frontend lint / format:check / typecheck (local mirror) | clean |
| Vitest | **341 passed** (37 files) |
| Playwright (hermetic, `npx playwright test`) | **49 passed / 3 pre-existing stack skips** |
| Playwright (real-stack, `e2e/stack/live-sync.spec.ts`) | **1 passed** (19.6 s), all four two-device scenarios green |
| `npm run build` | succeeds (pre-existing >500 kB chunk warning only) |

No stale full-suite expectation failed: M9 adds no schema, so
`M2SchemaBaselineModelTests.All_expected_entities_are_mapped` is unaffected.

Independent review outcome: the M9 backend and UI were reviewed per issue on the
branch. The M9-08 two-device battery and the M9-09 security/limits battery
(`docs/M9_TEST_MATRIX.md`) found **no §4 battery failure and no remaining product
defect**; the M9-09 sweep added only tests (an actual oversized frame, the
reflection no-RPC surface, the full transport-limit configuration, a
three-session revocation isolation case, and a concurrent reconnect storm), and
the M9-08 sweep added the real-stack two-browser-context flow.

## Live deployment verification

Verified on 2026-10-13 after rebuilding and redeploying via `docker-admin` using
`scripts/deploy.sh up` (`docker compose up -d --build` on the NAS).

- **Deploy-host checkout:** `/mnt/Data/calendarplanner` at `db9569c` on `main`,
  working tree clean. `/mnt/y` is the same SMB/9p-exported tree, so there is one
  checkout. Operational note: the `CPlanner` deploy user cannot `git fetch`/`pull`
  because `.git` is not writable by that user; `HEAD` already matched
  `origin/main` at `db9569c`, so no pull was needed.
- **Image `fate-and-fridge:0.1.0`:** `sha256:a773cd21318f…` →
  `sha256:fd085e5d9d56cb5076fad4d754cb9cc4281d6748692f0e934188f02fbc0b9110`,
  created `2026-10-13T14:03Z`. This was a **real rebuild** — the frontend
  `npm ci` + `vite build` and `dotnet publish` layers re-ran, not a cache hit. The
  app container was recreated and is healthy (`restarts=0`); postgres was
  untouched.
- **No migration:** M9 adds no schema, so no migration was applied; this is
  consistent with `dotnet ef migrations has-pending-model-changes` being empty.
- **Health:** `GET /api/v1/health` →
  `200 {"status":"ok","app":"Fate & Fridge","version":"0.1.0","environment":"production"}`.
- **Hub reachability (non-mutating):** `POST /hub/live/negotiate?negotiateVersion=1`
  → `200` with a valid SignalR negotiate body
  (`availableTransports: WebSockets, ServerSentEvents, LongPolling`);
  `GET /hub/live` → `400 "Connection ID required"`; an unknown route
  `/api/v1/definitely-not-a-route` → `404` baseline; and a SPA deep-link →
  `200 text/html`. So the hub is served and is not swallowed by the SPA fallback.
- **Frontend bundle:** `/assets/index-kH8uUUK-.js` (629,131 B, sha256
  `4c8cf40d90e21588ebefe721edcb98b748781f9efe38f9fadcd55a954353ecc5`), containing
  the `Live Sync` UI string and the M9 completion date `2026-10-13`.
- **Live browser verification** (read-only Playwright, no mutations):
  - `/progress` renders `10 of 15 milestones complete (67%).` with the
    **M9 — Live Sync** card showing **Complete** / `Completed 2026-10-13` and
    M10–M14 as Planned.
  - The app-shell live-sync indicator settles to **`● Live`**
    (`aria-live="polite"`, `aria-atomic="true"`, `data-state="connected"`).
  - A genuine **`101 Switching Protocols` WebSocket handshake to
    `ws://…/hub/live`** was captured (Kestrel) carrying a SignalR frame.
  - A **second** page independently connected (`Live`) with no socket errors.
  - Forcing the socket closed rendered `Offline` and the client
    **auto-reconnected** — a second WebSocket opened within ~3 s and the indicator
    returned to `Live`.
  - Screenshots: `.playwright-mcp/m9-progress.png`,
    `.playwright-mcp/m9-shell-live-sync-calendar.png`.
- **PostgreSQL** still publishes no host port; nothing persistent was deleted (only
  the app container was recreated).
- **Real-stack two-device spec — executed and passed.** The M9-08
  two-browser-context flow `frontend/e2e/stack/live-sync.spec.ts` was run against
  a **disposable** local compose project (`faf-m9-e2e`, `HOST_PORT=50900`,
  throwaway `HOST_DATA_DIR=/tmp/opencode/faf-m9-e2e-data`) — never the production
  NAS stack. The stack was fresh/unconfigured before the run
  (`setup_completed:false`) and was configured as multiuser by the spec itself,
  proving it consumed a genuine fresh stack. The spec file was **not modified**.
  Invocation (from a local mirror; the 9p share times out Playwright workers):

  ```bash
  rsync -a --exclude node_modules --exclude dist --exclude test-results \
    --exclude playwright-report /mnt/y/frontend/ /tmp/opencode/frontend/
  cd /tmp/opencode/frontend
  E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser \
    E2E_STACK_LIVE_SYNC=1 npx playwright test e2e/stack/live-sync.spec.ts
  ```

  Result: **1 test passed (19.6 s)** — all four scenarios inside
  `two-device live sync › a change on one device reaches the other over the live
  socket` were green:

  1. a manual grocery added on A became visible on B;
  2. a grocery check on A showed checked on B;
  3. a calendar meal change (Misc label) on A appeared in B's grid;
  4. a lock on A rendered the `Locked` badge on B.

  Both devices asserted `live-sync-status` `data-state="connected"` / `Live`
  before each scenario. Hermetic safety was re-verified (`npx playwright test
  e2e/stack/` with no `E2E_BASE_URL` → **3 skipped, exit 0**) and
  lint/format/typecheck were green in the mirror. The disposable stack was then
  torn down (`docker compose -p faf-m9-e2e down -v`, data dir removed); the
  production NAS stack was untouched.

## CI status after completion

`main`'s CI was **red** when this milestone was marked complete. Both failures
were diagnosed and fixed after the fact on `main`:

- **BUG-009** — the M9 test-only `Microsoft.AspNetCore.SignalR.Client 10.0.12`
  pulled `Microsoft.AspNetCore.SignalR.Common 10.0.12`, affected by
  CVE-2026-70159 / GHSA-334f-vvp4-7g6p (High). Because the repository treats
  NuGet audit warnings as errors, the Backend job failed at `Restore` (`NU1903`).
  Fixed in `d8b09ef` by bumping the test dependency to `10.0.13`. The production
  API project never referenced SignalR NuGet packages, so it was not affected.
- **BUG-010** — the M9-08 real-stack spec (`frontend/e2e/stack/live-sync.spec.ts`)
  requires and consumes the single-shot first-run setup, and ran before the
  pre-existing acceptance spec, so the "Docker stack acceptance + Playwright" job
  failed at `stack.spec.ts:45` (landing on `/login` instead of `/setup`). Fixed in
  `3913ad6` (gate the spec behind `E2E_STACK_LIVE_SYNC=1` and give it its own
  fresh stack in CI) and `8c9f994` (the reset must also clear the **bind-mounted**
  data directory, because `docker compose down -v` removes no named volumes in
  this stack).

Process lesson recorded in `docs/BUG_TODO.md`: **CI must be green before a
milestone is marked complete or tagged.** The verification above was performed on
the completion commits; these CI fixes landed on `main` afterwards and do not
change the milestone's product scope or contracts.
