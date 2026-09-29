# syntax=docker/dockerfile:1
#
# Fate & Fridge — single application image.
#
# Stages:
#   frontend-build  builds the React/Vite frontend
#   backend-build   installs the FastAPI backend into a virtualenv
#   runtime         serves the API and the built frontend
#
# The container applies Alembic migrations on startup, then runs uvicorn.

# --------------------------------------------------------------- frontend ---
FROM node:24-alpine AS frontend-build
WORKDIR /build
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# ----------------------------------------------------------- backend deps ---
FROM python:3.13-slim AS backend-build
ENV PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
WORKDIR /src
COPY backend/pyproject.toml ./
COPY backend/app ./app
RUN pip install .

# ---------------------------------------------------------------- runtime ---
FROM python:3.13-slim AS runtime
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PATH="/opt/venv/bin:$PATH" \
    DATA_DIR=/data \
    STATIC_DIR=/app/static

COPY --from=backend-build /opt/venv /opt/venv

WORKDIR /app
COPY backend/alembic.ini ./
COPY backend/migrations ./migrations
COPY backend/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
COPY --from=frontend-build /build/dist ./static

RUN chmod +x /usr/local/bin/docker-entrypoint.sh \
    && mkdir -p /data/config /data/images /data/backups

EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
