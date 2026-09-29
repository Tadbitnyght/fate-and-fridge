"""Database engine, session factory, and connectivity helper."""

import logging
from collections.abc import AsyncIterator

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine

from app.config import get_settings

logger = logging.getLogger(__name__)

# Engine creation is lazy: no connection is opened until first use, so
# importing this module never requires a running database.
engine = create_async_engine(get_settings().database_url, pool_pre_ping=True)
async_session_factory = async_sessionmaker(engine, expire_on_commit=False)


async def get_session() -> AsyncIterator[AsyncSession]:
    """FastAPI dependency yielding a request-scoped database session."""
    async with async_session_factory() as session:
        yield session


async def check_database_connection() -> bool:
    """Return True when the database answers a trivial query."""
    try:
        async with engine.connect() as connection:
            await connection.execute(text("SELECT 1"))
    except Exception:  # health reporting must never raise
        logger.warning("Database health check failed", exc_info=True)
        return False
    return True
