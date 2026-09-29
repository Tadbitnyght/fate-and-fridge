"""Runtime configuration for the backend.

All values are environment driven; see `.env.example` at the repository root.
"""

from functools import lru_cache
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Application settings loaded from environment variables and `.env`."""

    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    app_name: str = "Fate & Fridge"
    app_env: str = "development"
    log_level: str = "info"

    # Async SQLAlchemy URL. Docker Compose overrides this with a URL pointing
    # at the private `postgres` service.
    database_url: str = "postgresql+asyncpg://fatefridge:fatefridge@localhost:5432/fatefridge"

    # Mounted persistent locations (docs/ARCHITECTURE.md section 4).
    data_dir: Path = Path("data")
    static_dir: Path = Path("static")


@lru_cache
def get_settings() -> Settings:
    """Return the process-wide settings object (cached)."""
    return Settings()
