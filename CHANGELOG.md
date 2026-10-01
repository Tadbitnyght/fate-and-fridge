# Changelog

All notable changes to Fate & Fridge are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Milestone 0 — project skeleton:
  - .NET 10 / ASP.NET Core backend (C# 14, minimal APIs) with `/api/v1/health`
    and `/api/v1/health/db`, an OpenAPI document, and static SPA hosting
  - EF Core (Npgsql, snake_case conventions) with the `InitialCreate` migration;
    migrations apply at startup (`Database__MigrateOnStartup`) or on demand via
    `dotnet FateAndFridge.Api.dll --migrate`
  - React 19 + TypeScript + Vite frontend with a TanStack Query status screen
  - PostgreSQL 17 service, private to the Docker network, persistent volume
  - Single application image (Vite build served by the API) and a Docker
    Compose stack with `.env.example`
  - Test runners: xUnit v3 + FluentAssertions + Testcontainers (backend),
    Vitest + Testing Library (frontend), Playwright (end to end)
  - Enforced linting/formatting/type checks: `dotnet format` and analyzers for
    the backend; ESLint, Prettier, and `tsc` for the frontend
  - CI: backend format/build/EF-model-drift/tests, frontend lint/format/types/
    tests/build, compose validation, and a stack acceptance job that runs
    Docker Compose plus Playwright
- TrueNAS deployment tooling: `scripts/deploy.sh`, dedicated `fatefridge` SSH
  access for the `CPlanner` account, `docs/DEPLOYMENT.md`, and a periodic ZFS
  snapshot task for the `Data/calendarplanner` dataset
- OpenCode MCP configuration: GitHub (PAT), Playwright, Roslyn, and Docker
  pointed at the NAS Docker daemon over SSH

- Milestone 1 — setup and authentication:
  - Database: household settings, application settings, users, per-user
    permissions, sessions, and auth-event tables (EF Core migration
    `M1SchemaBaseline`)
  - Setup: single-shot, race-safe `POST /setup/complete` (household fields,
    first Admin, optional IP allowlist, update-check preference) with a
    one-time recovery key stored at `/data/config/recovery.key` (mode 0600)
  - Auth: Argon2id password hashing (libsodium via Geralt), opaque DB-backed
    sessions (`faf_session`, 30-day idle / 90-day absolute, rotation and
    revocation), CSRF via `X-CSRF-TOKEN`, login lockout (5 failures →
    progressive delay, 10 → 15 min, 5 lockouts/24 h → 24 h), per-IP failure
    limiting, forced password change, and Admin password reset
  - Authorization: seven server-enforced permission keys (edit implies view),
    Admin-only user management with last-admin protection, and single → multiuser
    conversion
  - Security settings: app-wide IP allowlist (health/loopback exempt),
    trusted-proxy-aware forwarded headers, and an Admin security screen
  - Frontend: routing/session shell, setup wizard with recovery-key
    confirmation, admin console (household/users/security), startup-page
    routing, and an accessibility sweep (skip link, focus management, modal
    focus traps)
  - Hardening: request body limits and `application/json` policy, strict
    canonical PHC parsing, unified error envelopes (404/415/500 codes), and an
    expanded verification suite (643 backend tests; 94 Vitest, 11 hermetic
    Playwright, plus a real-stack e2e suite)

### Changed

- Default published port is now **50080** (`HOST_PORT` in `.env`, default in
  `compose.yaml`, CI, Playwright, and docs). The container-internal port
  remains 8080.
- Backend stack decision: Python/FastAPI replaced by .NET 10 / ASP.NET Core
  (the Python scaffold remains in git history)

### Notes

- Milestone 0 acceptance verified: `docker compose up -d --build` produces a
  reachable, healthy application shell on the household TrueNAS server and in
  CI, with the status page and health API covered by Playwright.
