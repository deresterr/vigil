"""GET /health: public, so it tells whether the server runs and nothing more."""

import sqlite3

import app.db
from app.main import app as fastapi_app


def test_health_answers_without_details(client):
    r = client.get("/health")
    assert r.status_code == 200
    assert r.json() == {"status": "ok"}


def test_health_hides_the_database_error(client):
    class Broken:
        def execute(self, *_):
            raise sqlite3.OperationalError("disk I/O error at /data/vigil.db")

    async def broken_db():
        yield Broken()

    fastapi_app.dependency_overrides[app.db.get_db] = broken_db
    try:
        r = client.get("/health")
    finally:
        fastapi_app.dependency_overrides.clear()
    assert r.status_code == 503
    assert r.json() == {"detail": "db_error"}
