"""Health and readiness endpoints."""

from fastapi import APIRouter

from app import __version__
from app.config import get_settings
from app.db import check_database_connection
from app.schemas.health import DatabaseHealthResponse, HealthResponse

router = APIRouter(tags=["health"])

_settings = get_settings()


@router.get("/health", summary="Application liveness")
async def health() -> HealthResponse:
    """Report that the application process is up."""
    return HealthResponse(
        status="ok",
        app=_settings.app_name,
        version=__version__,
        environment=_settings.app_env,
    )


@router.get("/health/db", summary="Database connectivity")
async def health_db() -> DatabaseHealthResponse:
    """Report whether the database is currently reachable."""
    if await check_database_connection():
        return DatabaseHealthResponse(status="ok", database="connected")
    return DatabaseHealthResponse(status="degraded", database="unavailable")
