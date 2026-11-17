"""The README's panel screenshots (#96): a local server of this checkout seeded through its API (the wizard, two
paired PCs, the laptop shut down, a restart alert, +30 min), then the events' times and the minutes set in the
database so the journal reads naturally; Chrome 420 px wide at DPR 2, English.

    pip install -r server/requirements-dev.txt httpx pyotp playwright    # and Google Chrome
    python3 -I .github/scripts/panel_shots.py server out/ [time zone]

The time zone (the wizard's and the browser's) should be one where it is about 13:00-14:00 now, so the times in
the journal look like a day's: late evening in Poland America/Los_Angeles (the default), about 19:00
America/New_York. Then crop from the top at the full width of 840 px, as in the README: journal-alert.png to
1468 px, panel-settings.png to 2372 px (under the child's card); panel-status.png stays whole."""

import base64
import hashlib
import hmac
import json
import os
import secrets
import shutil
import sqlite3
import subprocess
import sys
import time
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

import httpx
import pyotp
from playwright.sync_api import sync_playwright

SERVER, OUT = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
OUT.mkdir(parents=True, exist_ok=True)
DATA = Path("/tmp/vigil-shots")
PORT = 8099
BASE = f"http://127.0.0.1:{PORT}"
PASSWORD = "correct horse battery"
TZ = sys.argv[3] if len(sys.argv) > 3 else "America/Los_Angeles"
CHROME = os.environ.get("CHROME", "/usr/bin/google-chrome")
CLIENT_VERSION = "1.0.0"  # the PCs are up to date: no "Update needed" in the shots

shutil.rmtree(DATA, ignore_errors=True)
DATA.mkdir()
env = {**os.environ, "DATABASE_PATH": str(DATA / "vigil.db"), "NO_COLOR": "1",
       "VIGIL_CLIENT_VERSION": CLIENT_VERSION}
srv = subprocess.Popen([sys.executable, "-m", "uvicorn", "app.main:app", "--port", str(PORT), "--workers", "1"],
                       cwd=SERVER, env=env)


def wait_up():
    for _ in range(100):
        try:
            if httpx.get(BASE + "/health").status_code == 200:
                return
        except httpx.HTTPError:
            pass
        time.sleep(0.2)
    raise SystemExit("server did not start")


class Pc:
    def __init__(self, key_b64: str):
        self.key = base64.urlsafe_b64decode(key_b64 + "=" * (-len(key_b64) % 4))
        self.instance = str(uuid.uuid4())
        self.awake = 600_000
        self.start = 0

    def post(self, path: str, body: dict) -> httpx.Response:
        raw = json.dumps(body).encode()
        nonce = base64.urlsafe_b64encode(secrets.token_bytes(16)).rstrip(b"=").decode()
        ts = str(int(datetime.now(UTC).timestamp() * 1000))
        canonical = "\n".join(("POST", path, ts, nonce, hashlib.sha256(raw).hexdigest())).encode()
        sig = base64.b64encode(hmac.new(self.key, canonical, hashlib.sha256).digest()).decode()
        r = httpx.post(BASE + path, content=raw, headers={
            "X-Nonce": nonce, "X-Timestamp": ts, "X-Signature": sig, "Content-Type": "application/json"})
        r.raise_for_status()
        return r

    def heartbeat(self, active=True):
        self.awake += 5000
        return self.post("/v1/heartbeat", {
            "session_active": active, "user_active": active,
            "current_time_utc": datetime.now(UTC).isoformat(), "instance_id": self.instance,
            "awake_ms": self.awake, "awake_ms_at_start": self.start, "failed_attempts": 0,
            "client_version": CLIENT_VERSION, "child_admin": False})


try:
    wait_up()
    totp = pyotp.parse_uri(httpx.get(BASE + "/setup/totp_qr").json()["uri"])
    r = httpx.post(BASE + "/setup/complete", json={
        "child_name": "Alex", "password": PASSWORD, "totp_code": totp.now(), "timezone": TZ})
    r.raise_for_status()

    s = httpx.Client(base_url=BASE, follow_redirects=False)
    s.post("/admin/login", data={"password": PASSWORD})

    def pair(name: str) -> Pc:
        code = s.post("/admin/api/pairing_code", json={"child_id": 1, "device_name": name}).json()["code"]
        return Pc(httpx.post(BASE + "/v1/pair", json={"code": code}).json()["signing_key"])

    pc, laptop = pair("Alex-PC"), pair("Alex-Laptop")
    laptop.heartbeat()
    laptop.post("/v1/goodbye", {"instance_id": laptop.instance, "reason": "shutdown"})
    pc.heartbeat()
    # Restarted without a goodbye, 12 min later on the same boot: the alert.
    pc.instance, pc.start = str(uuid.uuid4()), pc.awake + 720_000
    pc.awake = pc.start
    pc.heartbeat()
    s.post("/admin/api/children/1/grant", json={"minutes": 30, "mode": "limit"}).raise_for_status()
    pc.heartbeat()

    # Times of the day that read naturally (the seed ran within a second), by event order.
    now = datetime.now(UTC)
    db = sqlite3.connect(DATA / "vigil.db")
    rows = db.execute("SELECT id, event_type, device_id FROM events ORDER BY id").fetchall()
    ago = {"device_paired": 380, "watchdog_started": 375, "watchdog_shutdown": 135,
           "tamper_watchdog_restarted": 27, "grant_applied": 6}
    for eid, etype, _dev in rows:
        if etype in ago:
            db.execute("UPDATE events SET ts_utc = ? WHERE id = ?",
                       ((now - timedelta(minutes=ago[etype])).isoformat(), eid))
    db.execute("UPDATE child_daily SET seconds_used = ?", (73 * 60,))
    db.execute("UPDATE devices SET seconds_today = ? WHERE name = 'Alex-PC'", (58 * 60,))
    db.execute("UPDATE devices SET seconds_today = ?, last_heartbeat_utc = ?, last_active_utc = ? "
               "WHERE name = 'Alex-Laptop'", (15 * 60, *(2 * [(now - timedelta(hours=2, minutes=15)).isoformat()])))
    db.commit()
    db.close()
    pc.heartbeat()

    with sync_playwright() as p:
        b = p.chromium.launch(executable_path=CHROME)
        ctx = b.new_context(viewport={"width": 420, "height": 900}, device_scale_factor=2, locale="en-US",
                            extra_http_headers={"Accept-Language": "en-US"}, timezone_id=TZ)
        ctx.add_cookies([{"name": k, "value": v, "url": BASE} for k, v in s.cookies.items()])
        page = ctx.new_page()
        for name, tab in (("panel-status", "status"), ("journal-alert", "journal"), ("panel-settings", "settings")):
            page.goto(f"{BASE}/admin#{tab}")
            page.wait_for_load_state("networkidle")
            page.evaluate("t => { const b = document.querySelector(`[data-tab='${t}']`); if (b) b.click(); }", tab)
            page.wait_for_timeout(800)
            page.screenshot(path=OUT / f"{name}.png", full_page=True)
        b.close()
finally:
    srv.terminate()
    srv.wait()
