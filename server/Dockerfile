FROM python:3.12-slim

# tzdata: zoneinfo (the time zone chosen in the wizard)
# curl:   the compose healthcheck
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/*

# TZ (optional, from .env) only sets the time zone of the log timestamps.
ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    LISTEN_PORT=8088

WORKDIR /app

# Requirements first: their own layer, cached across code changes
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# compose.dev.yaml bind-mounts the code over this for development
COPY app/ /app/app/

EXPOSE ${LISTEN_PORT}

# One worker: the time accounting relies on one process (CLAUDE.md).
# Shell form, so ${LISTEN_PORT} is expanded at run time.
# --timeout-keep-alive must stay above the watchdog's PooledConnectionIdleTimeout (30 s), see HeartbeatClient.cs
CMD uvicorn app.main:app --host 0.0.0.0 --port ${LISTEN_PORT} --workers 1 --timeout-keep-alive 75
