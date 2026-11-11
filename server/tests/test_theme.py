"""Colors (#116): every page's own CSS uses the theme's variables (static/theme.css), never a color of its own."""

import re

import pytest

from app.pages import STATIC_DIR

_COLOR = re.compile(r"#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl)\("
                    r"|(?<![-\w])(?:white|black|red|blue|green|orange)(?![-\w])")


@pytest.mark.parametrize("name", ("admin.html", "login.html", "setup.html"))
def test_page_styles_use_theme_variables(name):
    html = (STATIC_DIR / name).read_text(encoding="utf-8")
    css = "\n".join(re.findall(r"<style>(.*?)</style>", html, flags=re.S))
    css = re.sub(r"/\*.*?\*/", "", css, flags=re.S)  # comments may name issues (#108)
    assert _COLOR.findall(css) == [], f"{name}: colors outside theme.css"
