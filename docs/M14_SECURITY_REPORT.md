# M14 Security Report — Hardening Evidence

Status: **in progress** — M14-01 (dependency and image gate) landed; the
hands-on penetration pass (M14-09) still extends this file.
Runbook: `docs/M14_PLAN.md` (§1.3, §1.4, §4). Tracking: #173; M14-01: #174.

This file is the single record for:

1. the permanent dependency and container-image gates (M14-01), and
2. every accepted vulnerability exception (an advisory with **no available
   fix**), and
3. the hands-on penetration-pass findings and fixes (M14-09, additive sections
   at the end — do not renumber the existing sections).

---

## 1. Dependency and image gate

Policy (M14 decision, `docs/M14_PLAN.md` §1.3): CI **fails** on any
**High/Critical** advisory. The image gate applies the "that has a fix" filter
(`--ignore-unfixed`); NuGet and npm fail on severity alone because neither
toolchain can express a per-advisory ignore (the NuGet gate is stricter still —
see the note below). Medium/Low are reported and tracked through the artifacts.
An advisory with **no available fix** may be accepted only with a documented
exception row in §2.

| Gate | Where | Command | Fails on |
|------|-------|---------|----------|
| NuGet | `.github/workflows/ci.yml` → `backend` job | `dotnet restore` (with `TreatWarningsAsErrors=true`) | any NuGet audit warning promoted to an error (`NU1901`–`NU1905`), i.e. Low/Moderate/High/Critical, direct and transitive |
| NuGet report | same job, `if: always()` | `dotnet list <project> package --vulnerable --include-transitive --no-restore --format json` | nothing (record only; uploaded as the `nuget-vulnerability-report` artifact) |
| npm | `ci.yml` → `frontend` job | `npm audit --audit-level=high` | **any** High/Critical advisory, fix or no fix (npm has no advisory ignore) |
| npm report | same job, `if: always()` | `npm audit --json` → `npm-audit-report` artifact (all severities) | nothing (record only) |
| Image | `ci.yml` → `image-scan` job | `aquasec/trivy:0.76.1@sha256:790fd66d… image --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1` (tag **and** digest pinned; reports uploaded as `trivy-runtime-image-report`, missing reports fail the step) | High/Critical advisories that have a fix, in the built runtime image |
| Image report | same job, `if: always()` | unfiltered `trivy image --format json` → `trivy-runtime-image-all.json` | nothing (Medium/Low record only) |

Notes:

- The NuGet gate is stricter than the stated policy on purpose: the repo has
  pinned `TreatWarningsAsErrors=true` in `backend/Directory.Build.props`, which
  promotes every audit warning (Low/Moderate included) to an error and applies
  to **both** `backend/src/FateAndFridge.Api` and
  `backend/tests/FateAndFridge.Api.Tests`.
- The image gate scans the **runtime** stage of `Dockerfile` (the compose `app`
  service builds that final stage), so it sees exactly the layers that ship:
  `mcr.microsoft.com/dotnet/aspnet:10.0` plus `ca-certificates`, `curl` and
  `postgresql-client-17` from PGDG. Nothing test-only is in that stage.
- The image scan runs on a GitHub-hosted runner, never on the production host.

### 1.1 Accepted-exception mechanics

There is no silent suppression path:

- **NuGet** — an accepted no-fix advisory must be suppressed explicitly with a
  `NuGetAuditSuppress` item on the advisory URL (in the affected `.csproj`) and
  a matching row in §2. The suppression is visible in review.
- **npm** — there is no per-advisory ignore. A no-fix advisory is cleared by
  upgrading or by a root `overrides` pin to a patched transitive. If no patched
  version exists, the gate cannot pass; the Lead must change `--audit-level` in
  the same commit as the §2 row, and that decision is re-reviewed when a fix
  ships.
- **Image** — `--ignore-unfixed` already excludes no-fix advisories from the
  gate; every such High/Critical advisory observed in a scan is a row in §2.

## 2. Vulnerability exceptions

Advisories with **no available fix** that are accepted for this release. Each
row needs a review date; rows expire when a fix is published, and the advisory
must then be fixed or re-reviewed.

| Package / image | Advisory id | Severity | Scope | Why no fix | Accepted | Review date |
|-----------------|-------------|----------|-------|------------|----------|-------------|
| _(none as of 2026-10-22)_ | — | — | — | — | — | 2026-10-22 |

## 3. M14-01 scan results (2026-10-22)

| Check | Result |
|-------|--------|
| `dotnet restore --force-evaluate` (both projects) | clean, no audit findings |
| `dotnet list package --vulnerable --include-transitive` (Api) | `The given project has no vulnerable packages` |
| `dotnet list package --vulnerable --include-transitive` (Tests) | `The given project has no vulnerable packages` |
| `npm audit --audit-level=high` | **4 findings before the bump** (2 high, 2 moderate) → **0 after** |
| Trivy scan of the built runtime image (`fate-and-fridge:ci-scan`, `sha256:faf425f4…`) | **0** HIGH/CRITICAL advisories — with **and** without the fix filter; gate exit 0 (see §3.2) |
| Trivy scan of the runtime **base** image (`mcr.microsoft.com/dotnet/aspnet:10.0`, ubuntu 24.04, .NET 10.0.13) | **0** HIGH/CRITICAL advisories with a fix (97 OS packages + both .NET dependency manifests scanned) |
| npm advisory drift (2026-10-24 re-check) | 2 new High advisories appeared after the first bump: `undici` 8.11.2 → 8.12.3, `zod` 4.6.5 → 4.6.7 (lockfile-only); `npm audit --audit-level=high` → 0 again |

npm findings fixed by the dev-dependency bump in `frontend/package.json`
(`vite` 8.3.1 → 8.3.5, `vitest` 5.0.2 → 5.0.5, and the lockfile refresh that
brings `source-map-js` 1.2.1 → 1.2.2, `@vitest/mocker` 5.0.2 → 5.0.5,
`nanoid` 3.3.19 → 3.3.20, `postcss` 8.5.28 → 8.5.31, `rolldown` 1.2.11 →
1.2.15):

- `source-map-js` — GHSA-68fv-2mgg-jv7q (high, event-loop DoS via indexed
  source-map sections).
- `vite` — GHSA-698v-4r3x-3vcv, GHSA-r825-w8mm-5pxw, GHSA-587h-7wmj-h5pm,
  GHSA-9jrq-w75r-8gcw, GHSA-rq7h-c2jc-7f22, GHSA-vfpm-58rq-9qcg (high,
  launch-editor / dev-server issues).
- `@vitest/mocker` — GHSA-qxg6-89mg-rqv8, GHSA-xp26-3454-mjgp (moderate,
  test-runner mock registration).

All changed packages are build/test tooling: no package in `dependencies`
changed, and none of them is copied into the runtime image (see §4).

### 3.1 Evidence — Trivy on the runtime base image

Run locally with the pinned scanner binary (no Docker involved, so only the
base image — not the built app image — could be scanned here):

```
TRIVY_CACHE_DIR=/tmp/opencode/trivy/cache ./trivy image \
  --image-src remote --platform linux/amd64 --scanners vuln \
  --severity HIGH,CRITICAL --ignore-unfixed \
  --format table mcr.microsoft.com/dotnet/aspnet:10.0
```

```
mcr.microsoft.com/dotnet/aspnet:10.0 (ubuntu 24.04)                ubuntu       0
usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.13/...deps.json  dotnet-core  0
usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.13/...deps.json     dotnet-core  0
```

The remaining runtime layer the base-image scan cannot see is the Dockerfile's
own additions (`ca-certificates`, `curl`, `postgresql-client-17` from PGDG);
the CI `image-scan` job scans those because it scans the built image.

### 3.2 Evidence — Trivy on the built runtime image

Executed by `docker-admin` on the local Docker context (`default`; the
`truenas` context was never selected) from commit `8a4e6dc`, replicating the CI
job exactly:

```
APP_VERSION=ci-scan docker compose build app
docker save fate-and-fridge:ci-scan -o /tmp/scan/runtime-image.tar
docker run --rm -v /tmp/scan:/scan \
  aquasec/trivy:0.76.1@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c \
  image --input /scan/runtime-image.tar --scanners vuln \
  --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 --timeout 10m
```

| Item | Result |
|------|--------|
| Image | `fate-and-fridge:ci-scan`, ID `sha256:faf425f40d9db58d2c5ea00b373843962e1b058b1fc84d72aad0b453928598d9`, `linux/amd64`, ~315 MiB |
| Base build | `mcr.microsoft.com/dotnet/aspnet:10.0` (ubuntu 24.04, .NET 10.0.13) + `ca-certificates`, `curl`, `postgresql-client-17` |
| Gate scan (`--ignore-unfixed`, `--exit-code 1`) | **exit 0** — CRITICAL 0, HIGH 0 across all four targets (OS layer, app deps, ASP.NET Core deps, .NET runtime deps) |
| Unfiltered scan (no `--ignore-unfixed`) | **0** HIGH/CRITICAL at all, so the no-fix set is empty; no §2 row is needed for this image |
| Runtime surface | `/app` holds 11 managed assemblies (the app, AngleSharp, Geralt, EF Core + Abstractions + Relational, EFCore.NamingConventions, Npgsql + Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.AspNetCore.OpenApi, Microsoft.OpenApi) and **no** xunit/FluentAssertions/Testcontainers/EFCore.Design/test assembly; `FateAndFridge.Api.deps.json` references none of them |
| Cleanup | the disposable `fate-and-fridge:ci-scan` tag and the scan directory were removed; no volume, dataset, production container or the `truenas` context was touched |

Caveat: the Trivy vulnerability DB is downloaded at scan time and is not pinned,
so the exact advisory set is date-dependent. The gate (and this record) is
therefore refreshed on each CI run; a newly published fixable advisory fails the
job rather than passing silently.

## 4. Runtime image surface

The `Dockerfile` final stage (`runtime`) contains only:

- `mcr.microsoft.com/dotnet/aspnet:10.0` (framework runtime + OS),
- `ca-certificates` and `curl` (healthcheck),
- `postgresql-client-17` from PGDG (backups — the only approved extra),
- the published API output (`/app`) and the built frontend (`wwwroot`).

**No test-only package reaches the runtime image.** Evidence:

- the `frontend-build` stage runs `npm ci` from `frontend/package.json`, but only
  `frontend/dist` is copied to `wwwroot` — never `node_modules`;
- the `backend-build` stage restores and publishes only
  `src/FateAndFridge.Api/FateAndFridge.Api.csproj`; the test project is not in
  the build context of that stage and is never published;
- `Microsoft.EntityFrameworkCore.Design` is `PrivateAssets="all"`, so the
  design-time provider is not a runtime dependency of the published output;
- the new dev dependencies are declared under `devDependencies` and their
  advisories/docs are only ever consumed by the `frontend`/`image-scan` CI jobs;
- a real `dotnet publish src/FateAndFridge.Api --configuration Release` produces
  11 assemblies (the app, AngleSharp, Geralt, EF Core + Abstractions +
  Relational, EFCore.NamingConventions, Npgsql +
  Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.AspNetCore.OpenApi,
  Microsoft.OpenApi) and **no** `FluentAssertions`, `xunit`,
  `Testcontainers`, `Microsoft.EntityFrameworkCore.Design` or test assembly.

## 5. Penetration pass (M14-09 — to be completed)

Reserved for the M14-09 hands-on pass and any fixes it produces:

| # | Finding | Severity | Status | Fix / reference |
|---|---------|----------|--------|-----------------|
| — | _pending_ | — | — | — |
