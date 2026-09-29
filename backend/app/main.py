"""FastAPI application factory for Fate & Fridge."""

from pathlib import Path

from fastapi import APIRouter, FastAPI, HTTPException
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

from app import __version__
from app.api.routes import health
from app.config import get_settings

APP_DESCRIPTION = (
    "Self-hosted household dinner planner: recipes, calendar, randomization, "
    "and grocery lists. Codename: Operation Dinner Plate."
)


def create_app() -> FastAPI:
    """Create and configure the FastAPI application."""
    settings = get_settings()

    app = FastAPI(
        title=settings.app_name,
        description=APP_DESCRIPTION,
        version=__version__,
        docs_url="/api/docs",
        redoc_url=None,
        openapi_url="/api/openapi.json",
    )

    api = APIRouter(prefix="/api/v1")
    api.include_router(health.router)
    app.include_router(api)

    _mount_frontend(app, settings.static_dir)
    return app


def _mount_frontend(app: FastAPI, static_dir: Path) -> None:
    """Serve the built frontend when present; otherwise expose a JSON root.

    Unknown non-API paths fall through to `index.html` so client-side routing
    keeps working. API paths always stay under `/api` and return JSON errors.
    """
    index_file = static_dir / "index.html"
    if index_file.is_file():
        assets_dir = static_dir / "assets"
        if assets_dir.is_dir():
            app.mount("/assets", StaticFiles(directory=assets_dir), name="assets")

        @app.get("/{path:path}", include_in_schema=False)
        async def spa(path: str) -> FileResponse:
            if path.startswith("api/"):
                raise HTTPException(status_code=404, detail="Not Found")
            return FileResponse(index_file)

        return

    @app.get("/", include_in_schema=False)
    async def root() -> dict[str, str]:
        return {
            "app": get_settings().app_name,
            "version": __version__,
            "docs": "/api/docs",
            "note": "Frontend build not present; use the Vite dev server.",
        }


app = create_app()
