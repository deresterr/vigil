FROM python:3.12-slim

# tzdata: dla zoneinfo("Europe/Warsaw")
# curl:   dla healthcheck z compose
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata curl \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=Europe/Warsaw \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    LISTEN_PORT=8088

WORKDIR /app

# Requirements najpierw — osobny layer, żeby cache działał
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Kod aplikacji wjeżdża i tak, ale przy dev-mode jest nadpisywany bind mountem z compose
COPY app/ /app/app/

EXPOSE ${LISTEN_PORT}

# Single worker — wymóg architektoniczny (SQLite WAL + atomicity logiki tickowania)
# Shell form (nie JSON array) żeby ${LISTEN_PORT} był ekspandowany w runtime
# --timeout-keep-alive must stay above the watchdog's PooledConnectionIdleTimeout (30 s), see HeartbeatClient.cs
CMD uvicorn app.main:app --host 0.0.0.0 --port ${LISTEN_PORT} --workers 1 --timeout-keep-alive 75
