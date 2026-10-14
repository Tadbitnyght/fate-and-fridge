# Fate & Fridge
## Codename: Operation Dinner Plate

Fate & Fridge is a self-hosted household dinner planner and grocery generator.

This documentation pack defines the intended V1 before implementation begins.

## Start Here
1. `AGENTS.md`
2. `docs/PRODUCT_SPEC.md`
3. `docs/ARCHITECTURE.md`
4. `docs/DATABASE.md`
5. `docs/API.md`
6. `docs/RANDOMIZER.md`
7. `docs/SECURITY.md`
8. `docs/TESTING.md`
9. `docs/MILESTONES.md`
10. `docs/DEPLOYMENT.md`
11. `docs/V2_BACKLOG.md`

## Recommended AI Workflow
- Lead Developer owns architecture and issue breakdown.
- Implementer builds one issue at a time.
- Independent Reviewer validates correctness and security.

Do not allow agents to treat the V2 backlog as current scope.

## V1 Stack
- .NET 10 / ASP.NET Core (C# 14, minimal APIs)
- PostgreSQL
- Entity Framework Core (Npgsql)
- React
- TypeScript
- Vite
- TanStack Query
- SignalR
- Playwright
- Docker Compose

## Repository Layout
```text
backend/        ASP.NET Core application, EF Core migrations, xUnit tests
frontend/       React + TypeScript + Vite application
scripts/        Operational scripts (TrueNAS deploy helper)
docs/           Product, architecture, and operations specifications
compose.yaml    Docker Compose stack (app + PostgreSQL)
Dockerfile      Single application image (API + built frontend)
```

## Local Development
Backend (.NET 10 SDK):

```bash
cd backend
dotnet restore
dotnet build
dotnet test -- --filter-not-trait Category=Integration   # unit tests (no Docker needed)
dotnet format --verify-no-changes                        # formatting check
dotnet run --project src/FateAndFridge.Api               # http://localhost:8000
```

Integration tests use Testcontainers and need a running Docker daemon; CI runs
the full suite (`dotnet test`). When working from the SMB share (WSL), export
`FATE_ARTIFACTS_PATH=/tmp/fate-and-fridge-artifacts` before dotnet commands —
see `docs/DEPLOYMENT.md` §2.5.

Frontend (Node 24+):

```bash
cd frontend
npm install
npm run dev         # Vite dev server on :5173, proxies /api to the backend
npm run lint
npm run format:check
npm run typecheck
npm run test        # Vitest unit tests
npm run test:e2e    # hermetic Playwright smoke tests (set E2E_BASE_URL for a real stack)
E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser npm run test:e2e:stack  # live stack (fresh install; consumes it)
E2E_BASE_URL=http://127.0.0.1:50900 E2E_SETUP_MODE=multiuser E2E_STACK_LIVE_SYNC=1 \
  npx playwright test e2e/stack/live-sync.spec.ts  # two-device live-sync flow (own fresh stack)
```

The dev proxy targets `http://localhost:50080` (the Compose host port) by
default; run `VITE_API_PROXY=http://localhost:8000 npm run dev` when the API
runs through `dotnet run`.

The API root returns a small JSON index when no built frontend is present; the
Vite dev server serves the UI during development.

## Deployment
The household deployment target is TrueNAS SCALE; the repository lives on the
NAS and is deployed over SSH. See `docs/DEPLOYMENT.md` for access setup,
configuration, data layout, and operations. For a generic Docker host:

```bash
cp .env.example .env    # set POSTGRES_PASSWORD and paths
docker compose up -d --build
```

## Product Rule
> Fate & Fridge should feel like a simple household meal planner hiding a sophisticated database—not a sophisticated database pretending to be a meal planner.
