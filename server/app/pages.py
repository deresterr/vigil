"""The panel's HTML pages (app/static) with the shared i18n script inlined (#52).

Pages are read per request (no cache) and the `<!-- i18n -->` marker becomes a <script> with static/i18n.js:
there is no static file route, and the login and setup pages are public.
"""

from __future__ import annotations

from pathlib import Path

STATIC_DIR = Path(__file__).parent / "static"
_MARKER = "<!-- i18n -->"


def page(name: str) -> str:
    html = (STATIC_DIR / name).read_text(encoding="utf-8")
    script = (STATIC_DIR / "i18n.js").read_text(encoding="utf-8")
    return html.replace(_MARKER, "<script>\n" + script + "</script>", 1)
