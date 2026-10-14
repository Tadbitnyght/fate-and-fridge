# M9 Test Matrix — Live Sync

Status: **coverage map for M9-08 and M9-09** (2026-10-13, branch `feature/m9-live-sync`).
Feeds `docs/M9_VERIFICATION.md` at M9-10. Source of truth for the battery is
`docs/M9_PLAN.md` §4 plus the locked decisions §1.1–§1.16; the two-device flow is
`docs/TESTING.md` §10.

This file records, for every §4 battery area, the primary automated test(s) that
cover it — backend first, then frontend. It is the "audit result plus added
gaps" map: the per-issue suites were checked first and only the missing bullets
were added in
`backend/tests/FateAndFridge.Api.Tests/M9LiveSyncHubTests.cs` **(new M9-09)**,
`frontend/e2e/stack/live-sync.spec.ts` **(new M9-08)**, and this document.

## How to run the matrix

```bash
export FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts
cd backend
dotnet build -c Release FateAndFridge.slnx
ARTIFACTS=/tmp/fate-and-fridge-artifacts/bin/FateAndFridge.Api.Tests/release/FateAndFridge.Api.Tests
$ARTIFACTS -class "*M9LiveSync*" -class "*M9SessionRevocation*"
dotnet format --verify-no-changes
dotnet ef migrations has-pending-model-changes --project src/FateAndFridge.Api
```

Frontend gates run from a local mirror under `/tmp/opencode/frontend` (the 9p
share times out Vitest/Playwright workers) with a real local `node_modules`:

```bash
npm run lint && npm run format:check && npm run typecheck && npm test
npm run build
npx playwright test e2e/live-sync.spec.ts e2e/app.spec.ts e2e/groceries.spec.ts
```

The real-stack two-device suite is skipped unless `E2E_BASE_URL` points at a
fresh/unconfigured multiuser stack:

```bash
E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser \
  npx playwright test e2e/stack/live-sync.spec.ts
```

It has been **executed and passed** against a disposable stack (**1 test, 19.6 s**,
all four scenarios green); see `docs/M9_VERIFICATION.md` → Live deployment
verification for the invocation and evidence.

The M9 backend fixtures (all in the `M1 setup` collection) share one
Testcontainers/PostgreSQL container and reset the volatile rows per test. The
hub tests open real `HubConnection`s over the in-process TestServer (LongPolling;
the SPA uses `skipNegotiation` WebSockets in production). M9 adds **no schema**,
so `has-pending-model-changes` is expected to be empty.

## §4 battery → coverage

| §4 area | Primary coverage |
|---------|------------------|
| Auth — no anonymous socket in multiuser mode | `M9LiveSyncHubTests.Multiuser_connection_without_a_session_cookie_is_refused` |
| Auth — a valid `faf_session` connects | `M9LiveSyncHubTests.Multiuser_connection_with_a_valid_session_cookie_connects` |
| Auth — a socket works in single-user mode as the system actor | `M9LiveSyncHubTests.Single_user_connection_without_a_cookie_connects_as_the_system_actor`, `Single_user_system_actor_connection_is_not_aborted_by_revalidation` |
| Auth — a pre-setup or must-change-password session cannot connect | `M9LiveSyncHubTests.Connection_before_setup_is_refused`, `Connection_for_a_must_change_password_session_is_refused` |
| Auth — no token accepted via query string | Structural: there is no query-string auth path at all — the hub reads only the `faf_session` cookie / connection principal (`LiveSyncHub.OnConnectedAsync`), so a token in the URL cannot authenticate. The discriminating test is `Multiuser_connection_without_a_session_cookie_is_refused` (a URL token still has no cookie). |
| Events — each write in the §2.2 table emits exactly its type | `M9LiveSyncEventMatrixTests` (calendar CRUD/reroll/batch/undo, groceries manual/staples/add-to-list/check/uncheck, recipes incl. images, settings + reference data); negative cases `Validation_error_write_emits_nothing`, `Not_found_write_emits_nothing`, `Rolled_back_conflict_write_emits_nothing`, `Rolled_back_batch_apply_emits_nothing`, `Read_request_emits_nothing`, `Import_draft_emits_nothing`, `Import_check_read_emits_nothing`, `Household_writes_never_emit_session_revoked` |
| Events — per-user preference writes emit nothing | `M9LiveSyncEventMatrixTests.Grocery_preferences_patch_emits_nothing` |
| Events — every mutating route decides its emission | `M9LiveSyncEventMatrixTests.Event_matrix_covers_every_mutating_route` (route inventory over the endpoint data source) |
| Events — a two-client broadcast of each type (household group, incl. the originator) | `M9LiveSyncEventMatrixTests.Household_group_receives_every_event_type_over_signalr` (all five broadcast types to two clients), `M9LiveSyncHubTests.Broadcast_reaches_both_clients_and_revocation_closes_only_that_session` |
| Lock separation — a lock toggle emits only `calendar.lock_changed`; other writes only `calendar.updated` | `M9LiveSyncEventMatrixTests.Calendar_lock_emits_only_calendar_lock_changed`, `Calendar_unlock_emits_only_calendar_lock_changed`, `Calendar_put_emits_calendar_updated_with_the_entry_id` |
| Envelope — `type`/`entity`/`id`/`operation_id`/`actor_user_id`/`timestamp`, no entity bodies, UTC, null actor for the system actor | `M9LiveSyncEventMatrixTests.AssertSingleEvent` (per-write), `Calendar_put_actor_is_the_session_user`, `Single_user_write_emits_the_event_with_a_null_system_actor`; snake_case wire fields asserted in `Household_group_receives_every_event_type_over_signalr`; `M9LiveSyncHubTests.AssertSessionRevokedEnvelope` |
| Revocation — logout / admin revocation / user disable / expiry emits `session.revoked` and aborts those sockets | `M9LiveSyncHubTests.Logout_aborts_the_current_session_socket_and_sends_session_revoked`, `User_disable_aborts_all_of_the_users_sockets`, `Password_reset_aborts_all_of_the_users_sockets`, `Privilege_change_aborts_all_of_the_users_sockets`, `Password_change_publishes_a_user_scoped_revocation_excluding_the_current_session` |
| Revocation — a revoked session receives nothing afterwards; other sessions are unaffected | `M9LiveSyncHubTests.A_revoked_session_receives_no_further_events`, `Per_broadcast_revalidation_aborts_a_revoked_session_and_spares_the_others`, `User_scoped_revocation_honors_exceptSessionId`, `Session_revoked_reaches_only_the_target_session_and_others_keep_working` **(new M9-09)** |
| Revocation — per-broadcast re-validation; a re-validation read failure fails closed | `M9LiveSyncHubTests.Per_broadcast_revalidation_aborts_a_revoked_session_and_spares_the_others`, `Broadcast_skips_delivery_when_revalidation_fails_and_does_not_abort_connections` |
| Guards — same-origin rejects a foreign `Origin` | `M9LiveSyncHubTests.Foreign_origin_is_refused`, `Matching_origin_connects` |
| Guards — the IP allowlist applies to the upgrade | `M9LiveSyncHubTests.Ip_allowlist_applies_to_the_hub_connect` |
| Guards — a >32 KB inbound frame is rejected | `M9LiveSyncHubTests.Oversized_inbound_frame_is_rejected_and_the_server_survives` **(new M9-09)**; the configured cap is asserted in `Hub_guards_are_configured` and `Transport_limits_are_configured_as_documented` |
| Guards — the per-session and household connection caps hold | `M9LiveSyncHubTests.Per_session_connection_cap_refuses_an_extra_connection`, `Household_connection_cap_refuses_an_extra_connection`, `Reconnect_storm_drains_the_registry_and_keeps_the_caps` **(new M9-09)** |
| Guards — detailed errors off, keepalive/timeout configured | `M9LiveSyncHubTests.Hub_guards_are_configured`, `Transport_limits_are_configured_as_documented` **(new M9-09)** |
| Guards — a missing request context is refused (fail closed) | `M9LiveSyncHubTests.Connection_is_refused_when_the_request_context_is_missing` |
| Guards — register/unregister race keeps the connection tracked | `M9LiveSyncHubTests.Unregister_racing_with_register_keeps_the_session_connection_tracked` |
| No RPC — no client-invokable methods; a client cannot mutate state over the socket; unknown invocations are rejected | `M9LiveSyncHubTests.Hub_exposes_no_client_invokable_methods` **(new M9-09)** (reflection over the hub type), `A_client_invocation_of_an_unknown_method_errors_and_changes_nothing` **(new M9-09)** |
| Reconnect — a reconnect ends up consistent (full refetch); a storm does not error the server or leak connections | `LiveSyncProvider.test.tsx` ("runs the full invalidation pass on connect and again on reconnect"), `liveSyncKeys.test.ts` ("runs the full pass over every distinct live-sync key once"), `M9LiveSyncHubTests.Reconnect_storm_drains_the_registry_and_keeps_the_caps` **(new M9-09)** |
| Frontend — each event invalidates exactly the mapped query keys | `frontend/src/features/live-sync/liveSyncKeys.test.ts` (per-type mapping, `invalidateForEvent`, full pass), `LiveSyncProvider.test.tsx` ("invalidates the mapped keys when an event arrives") |
| Frontend — the status indicator reflects connected/reconnecting accessibly | `frontend/src/features/live-sync/LiveSyncStatus.test.tsx` ("renders the %s state as a polite live region", "is non-blocking: no interactive role and no focusable content"), `LiveSyncProvider.test.tsx` ("surfaces connecting, reconnecting, and connected states") |
| Frontend — logout closes the socket; `session.revoked` forces sign-out | `LiveSyncProvider.test.tsx` ("tears the connection down on logout", "forces a sign-out and stops reconnecting on session.revoked"), `LiveSyncProvider.test.tsx` ("connects with skipNegotiation over WebSockets at /hub/live") |
| Frontend — the hermetic socket path (no backend) | `frontend/e2e/live-sync.spec.ts` ("live sync: a groceries.updated frame refetches the list and shows Live", "live sync: the status indicator stays non-blocking and REST keeps working") |
| Two-device — grocery check on A updates B; manual grocery added on A updates B; calendar meal change updates B; lock state updates B | `frontend/e2e/stack/live-sync.spec.ts` **(new M9-08)** ("a change on one device reaches the other over the live socket", scenarios 1–4, two real browser contexts over a real stack) — **executed and passed** against a disposable stack (1 test, 19.6 s, all four scenarios green) |
| Two-device — a stale client's write is handled by the existing conflict paths | Deferred to the M6/M7 conflict suites (`UNDO_CONFLICT`, `BATCH_CONFLICT`, `BATCH_DRAFT_EXPIRED`, historical immutability); M9 adds no version token (docs/M9_PLAN.md §1.10). See the deferred list below. |

## Audit result

**Backend two-client gap (M9-08 #2): none found.** The audit confirmed every
broadcast type already has a two-client assertion:
`M9LiveSyncEventMatrixTests.Household_group_receives_every_event_type_over_signalr`
covers all five broadcast types (`calendar.updated`, `calendar.lock_changed`,
`groceries.updated`, `recipe.updated`, `settings.updated`) to two connected
clients, and `M9LiveSyncHubTests.Broadcast_reaches_both_clients_and_revocation_closes_only_that_session`
covers the revocation path. No per-type broadcast test was missing, so no
backend addition was made for M9-08.

**M9-09 gaps closed.** The per-issue suites already covered the origin check, the
IP allowlist on the upgrade, the 32 KiB cap being *configured*, the per-session
(8) and household (32) caps, the register/unregister race, the auth matrix, and
the revocation battery. The gaps added were: an *actual* oversized inbound frame
(not just the configured cap), the reflection-based no-RPC surface plus a runtime
unknown-invocation check, the full transport-limit configuration (keepalive,
client timeout, handshake timeout alongside size/detailed errors), a
three-session `session.revoked` isolation test with post-revocation normal
events, and a concurrent reconnect storm across four sessions with a mid-storm
cap check and a drain-to-zero assertion.

**M9-08 gap closed.** The real-stack two-browser-context flow
(`frontend/e2e/stack/live-sync.spec.ts`) is new; the hermetic
`page.routeWebSocket` invalidation (`frontend/e2e/live-sync.spec.ts`) already
existed from M9-07.

No §4 battery bullet failed, so no product defect was fixed in M9-08/M9-09. One
documentation detail surfaced during the battery: `HubOptions.ClientTimeoutInterval`
is left unset (null), so SignalR's documented 30-second default applies; the
transport-limits test asserts the unset option rather than a redundant override.

## Deferred items (not covered here; recorded for M9-10/M14)

- Stale-write conflict handling for a live-synced client (docs/M9_PLAN.md §1.10):
  the existing M6 `UNDO_CONFLICT`, M7 `BATCH_CONFLICT` / `BATCH_DRAFT_EXPIRED` /
  batch-undo conflict, and historical-immutability suites cover it; M9 adds no
  version/ETag token, and the residual gap analysis is deferred to the M14
  hardening pass.
- Multi-instance / horizontal scaling and a Redis backplane, a durable event log,
  sequence numbers and missed-event replay, presence/typing, and fine-grained
  per-entry payloads (docs/M9_PLAN.md §6) are out of scope for M9.
- Push/browser notifications while the app is closed, statistics/history live
  updates, and update-availability checks are later milestones (§6).
