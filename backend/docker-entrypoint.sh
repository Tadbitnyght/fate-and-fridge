#!/bin/sh
# Fate & Fridge — container entrypoint.
# Applies database migrations (with retry while PostgreSQL warms up),
# then starts the API server which also serves the built frontend.
set -eu

DATA_ROOT="${DATA_DIR:-/data}"
mkdir -p "$DATA_ROOT/config" "$DATA_ROOT/images" "$DATA_ROOT/backups"

attempt=0
until alembic upgrade head; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 30 ]; then
        echo "ERROR: database migrations failed after $attempt attempts" >&2
        exit 1
    fi
    echo "Database not ready (attempt $attempt/30); retrying in 2s..." >&2
    sleep 2
done

exec uvicorn app.main:app --host 0.0.0.0 --port "${APP_PORT:-8080}"
