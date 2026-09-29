"""Declarative base and shared metadata conventions for ORM models."""

from sqlalchemy import MetaData
from sqlalchemy.orm import DeclarativeBase

# Stable, explicit constraint/index names keep migrations predictable and
# reviewable (docs/ARCHITECTURE.md section 19).
NAMING_CONVENTION: dict[str, str] = {
    "ix": "ix_%(table_name)s_%(column_0_N_name)s",
    "uq": "uq_%(table_name)s_%(column_0_N_name)s",
    "ck": "ck_%(table_name)s_%(constraint_name)s",
    "fk": "fk_%(table_name)s_%(column_0_N_name)s_%(referred_table_name)s",
    "pk": "pk_%(table_name)s",
}


class Base(DeclarativeBase):
    """Base class for all Fate & Fridge ORM models."""

    metadata = MetaData(naming_convention=NAMING_CONVENTION)
