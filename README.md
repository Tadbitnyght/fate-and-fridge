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
- FastAPI
- PostgreSQL
- SQLAlchemy
- Alembic
- React
- TypeScript
- Vite
- TanStack Query
- WebSockets
- Docker Compose

## Repository Layout
```text
backend/        FastAPI application, Alembic migrations, pytest suite
frontend/       React + TypeScript + Vite application
scripts/        Operational scripts (TrueNAS deploy helper)
docs/           Product, architecture, and operations specifications
compose.yaml    Docker Compose stack (app + PostgreSQL)
Dockerfile      Single application image (API + built frontend)
```

## Local Development
Backend (Python 3.13+):

```bash
cd backend
python -m venv .venv && . .venv/bin/activate
pip install -e ".[dev]"
pytest              # tests (no database required)
ruff check .        # lint
ruff format .       # format
mypy app            # type check
```

Frontend (Node 24+):

```bash
cd frontend
npm install
npm run dev         # Vite dev server on :5173, proxies /api to :8000
npm run lint
npm run typecheck
npm run test
```

Run the backend for development with `uvicorn app.main:app --reload --port 8000`
(from `backend/`). Without a built frontend the API root returns a small JSON
index; the Vite dev server serves the UI.

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
