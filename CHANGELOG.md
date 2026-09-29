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
