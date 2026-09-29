"""Pydantic response schemas for health endpoints."""

from typing import Literal

from pydantic import BaseModel


class HealthResponse(BaseModel):
    """Application liveness payload."""

    status: Literal["ok"] = "ok"
    app: str
    version: str
    environment: str


class DatabaseHealthResponse(BaseModel):
    """Database connectivity payload."""

    status: Literal["ok", "degraded"]
    database: Literal["connected", "unavailable"]
