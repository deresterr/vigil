"""The panel's HTML pages (app/static) with the shared scripts inlined (#52, #102).

Pages are read per request (no cache). The `<!-- i18n -->` marker becomes a <script> with static/i18n.js, and
`<!-- schedule -->` one with static/schedule.js (the weekly grid of the panel and the setup wizard) preceded by
DEFAULT_WEEK, a new child's week from config: there is no static file route, and the login and setup pages are
public.
"""

from __future__ import annotations

import json
from pathlib import Path

from .config import default_schedule

STATIC_DIR = Path(__file__).parent / "static"
_I18N = "<!-- i18n -->"
_SCHEDULE = "<!-- schedule -->"


def page(name: str) -> str:
    html = (STATIC_DIR / name).read_text(encoding="utf-8")
    i18n = (STATIC_DIR / "i18n.js").read_text(encoding="utf-8")
    html = html.replace(_I18N, "<script>\n" + i18n + "</script>", 1)
    if _SCHEDULE in html:
        schedule = (STATIC_DIR / "schedule.js").read_text(encoding="utf-8")
        default_week = json.dumps(default_schedule(), separators=(",", ":"))
        html = html.replace(_SCHEDULE, f"<script>\nconst DEFAULT_WEEK = {default_week};\n{schedule}</script>", 1)
    return html
