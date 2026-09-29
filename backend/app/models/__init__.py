"""ORM models.

Import model modules in this package so that Alembic autogenerate can see
them through ``Base.metadata``.
"""

from app.models.base import Base

__all__ = ["Base"]
