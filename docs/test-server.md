# Serwer testowy (do testów PR-ów na VM)

Serwer testowy chodzi na tym samym hoście co produkcja, jako osobny stack Dockge.

| | Produkcja | Test |
|---|---|---|
| Wersja | v0.4 (stara) | branch PR-a |
| Kontener | `vigil` | `vigil-test` |
| Obraz | `vigil:local` | `vigil-server:test` |
| Port | 8088 | 8089 |
| Katalog | własny stack w `/opt/docker/` | `/opt/docker/vigil-test/` |

**Stack testowy nie może używać nazwy kontenera ani obrazu produkcji.** `server/compose.yaml` w repo ma wartości
domyślne (`vigil`, `vigil-server:local`), a na starszych branchach obraz jest wpisany na sztywno — dlatego stack
testowy ma własny `compose.yaml`, który je nadpisuje niezależnie od wybranego brancha.

## Układ

Dockge widzi tylko katalogi stacków leżące bezpośrednio w `/opt/docker/`, więc repo jest podkatalogiem stacku:

```
/opt/docker/vigil-test/
├── compose.yaml   ← poniżej (nie z repo)
├── .env           ← VIGIL_PORT=8089
├── data/          ← baza SQLite (vigil.db)
└── src/           ← git clone https://github.com/deresterr/vigil.git
```

`compose.yaml`:

```yaml
# Dockge stack for the Vigil test server. Source: git clone in ./src (see vigil-test.sh).
services:
  vigil:
    extends:
      file: ./src/server/compose.yaml
      service: vigil
    image: vigil-server:test
    container_name: vigil-test
    volumes:
      - ./data:/data
```

Port, healthcheck i `restart` przychodzą z repo przez `extends`; nazwy i ścieżka danych są przypięte w stacku.

Klon przez HTTPS z fine-grained tokenem (tylko `deresterr/vigil`, *Contents: Read-only*), bez kluczy SSH:

```sh
sudo mkdir -p /opt/docker/vigil-test
sudo git clone https://github.com/deresterr/vigil.git /opt/docker/vigil-test/src
sudo git -C /opt/docker/vigil-test/src config credential.helper store   # token w /root/.git-credentials
```

## Aktualizacja do brancha — `/opt/docker/vigil-test.sh`

```sh
#!/bin/sh
set -eu
cd /opt/docker/vigil-test
git -C src fetch --prune -q origin
git -C src checkout -q --detach "origin/${1:-main}"
docker compose up -d --build
docker image prune -f
git -C src log -1 --oneline
```

`sudo ./vigil-test.sh feat/41-installer-release`, po merge'u `sudo ./vigil-test.sh` (= `main`).

- `--detach` na `origin/<branch>` — zawsze stan z GitHuba, bez lokalnych branchy.
- `up -d --build` sam odtwarza kontener po zmianie obrazu; ręczne stop/rm niepotrzebne.
- `docker image prune -f` usuwa tylko poprzedni obraz testowy. **Nie** `docker system prune -a`: kasuje
  nieużywane obrazy wszystkich stacków i cały cache buildów.
- Dockge pokazuje stack, ale kod aktualizuje tylko skrypt (Dockge nie robi `git fetch`).

## Kopia bazy produkcji (próba migracji)

Przed aktualizacją produkcji baza v0.4 przechodzi migracje 1–2 (D7) na serwerze testowym:

```sh
docker exec vigil python -c "import sqlite3; s=sqlite3.connect('/data/vigil.db'); d=sqlite3.connect('/data/vigil-copy.db'); s.backup(d); d.close()"
docker inspect -f '{{range .Mounts}}{{.Source}}{{end}}' vigil        # katalog danych produkcji na hoście
sudo docker compose -f /opt/docker/vigil-test/compose.yaml stop
sudo mv <katalog produkcji>/vigil-copy.db /opt/docker/vigil-test/data/vigil.db
sudo rm -f /opt/docker/vigil-test/data/vigil.db-wal /opt/docker/vigil-test/data/vigil.db-shm
sudo docker compose -f /opt/docker/vigil-test/compose.yaml up -d
docker logs vigil-test | grep -i "schema migrated"
```

`backup()` robi spójną kopię działającej bazy (WAL), produkcja nic nie traci. Logowanie do panelu testowego
hasłem produkcji. Komputer dziecka z produkcji nie łączy się z testem (inny port), więc jego wpis w
„Komputerach” jest tylko historią; VM dodaje się jako nowy komputer.
