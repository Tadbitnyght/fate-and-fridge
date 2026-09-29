"""API tests for the health endpoints (no database required)."""

from collections.abc import AsyncIterator

import pytest
from httpx import ASGITransport, AsyncClient

from app.main import app


@pytest.fixture
async def client() -> AsyncIterator[AsyncClient]:
    transport = ASGITransport(app=app)
    async with AsyncClient(transport=transport, base_url="http://testserver") as http_client:
        yield http_client


async def test_health_reports_app_and_version(client: AsyncClient) -> None:
    response = await client.get("/api/v1/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["app"] == "Fate & Fridge"
    assert body["version"]
    assert body["environment"]


async def test_database_health_reports_connected(
    client: AsyncClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    async def fake_check() -> bool:
        return True

    monkeypatch.setattr("app.api.routes.health.check_database_connection", fake_check)

    response = await client.get("/api/v1/health/db")

    assert response.status_code == 200
    assert response.json() == {"status": "ok", "database": "connected"}


async def test_database_health_reports_unavailable(
    client: AsyncClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    async def fake_check() -> bool:
        return False

    monkeypatch.setattr("app.api.routes.health.check_database_connection", fake_check)

    response = await client.get("/api/v1/health/db")

    assert response.status_code == 200
    assert response.json() == {"status": "degraded", "database": "unavailable"}
