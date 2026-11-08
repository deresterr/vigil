"""i18n (#52): every text marked in the panel pages has an English entry with the same placeholders.

Marked = t('…') / tn(n, '…') in scripts, data-i18n elements (normalized inner HTML) and data-i18n-attr
attributes. Unmarked Polish in a script string is caught too, as far as diacritics show it.
"""

import json
import re

import pytest

from app.pages import STATIC_DIR, page

PAGES = ("admin.html", "login.html", "setup.html")
_PL = re.compile(r"[ąćęłńóśźżĄĆĘŁŃÓŚŹŻ]")


def dictionary() -> dict[str, str]:
    js = (STATIC_DIR / "i18n.js").read_text(encoding="utf-8")
    return json.loads(js.split("/*I18N_EN*/", 1)[1].split("/*/I18N_EN*/", 1)[0])


def _js_string(raw: str) -> str:
    return raw.replace("\\'", "'").replace("\\n", "\n").replace("\\\\", "\\")


def marked(html: str) -> set[str]:
    keys = {_js_string(m) for m in re.findall(r"\btn?\((?:[^,()']+,\s*)?'((?:[^'\\]|\\.)*)'", html)}
    for _tag, inner in re.findall(r"<(\w+)\b[^>]*?\sdata-i18n(?=[\s>])[^>]*>(.*?)</\1>", html, flags=re.S):
        keys.add(re.sub(r"\s+", " ", inner.strip()))
    for tag in re.findall(r"<[^>]*\sdata-i18n-attr=\"[^\"]+\"[^>]*>", html):
        for attr in re.search(r"data-i18n-attr=\"([^\"]+)\"", tag).group(1).split():
            keys.add(re.search(rf"\s{attr}=\"([^\"]*)\"", tag).group(1))
    return keys


def _placeholders(s: str) -> set[str]:
    return set(re.findall(r"\{(\w+)\}", s))


@pytest.mark.parametrize("name", PAGES)
def test_every_marked_text_is_translated(name):
    en = dictionary()
    missing = sorted(k for k in marked((STATIC_DIR / name).read_text(encoding="utf-8")) if k not in en)
    assert missing == [], f"{name}: no English for {missing}"


def test_translations_keep_placeholders_and_plural_forms():
    for pl, en in dictionary().items():
        assert _placeholders(pl) == _placeholders(en), pl
        if "|" in pl:  # plural: Polish one|few|many, English one|other
            assert (pl.count("|"), en.count("|")) == (2, 1), pl


def test_no_unused_entries():
    used = set().union(*(marked((STATIC_DIR / n).read_text(encoding="utf-8")) for n in PAGES))
    assert sorted(set(dictionary()) - used) == []


@pytest.mark.parametrize("name", PAGES)
def test_no_unmarked_polish_in_scripts(name):
    html = (STATIC_DIR / name).read_text(encoding="utf-8")
    scripts = "\n".join(re.findall(r"<script>(.*?)</script>", html, flags=re.S))
    code = re.sub(r"//[^\n]*|/\*.*?\*/", "", scripts, flags=re.S)  # comments are not shown
    code = re.sub(r"\btn?\((?:[^,()']+,\s*)?'(?:[^'\\]|\\.)*'", "", code)  # marked texts
    leftovers = [s for s in re.findall(r"'((?:[^'\\]|\\.)*)'|`([^`]*)`|\"((?:[^\"\\]|\\.)*)\"", code)
                 for s in s if s and _PL.search(s)]
    assert leftovers == [], f"{name}: unmarked Polish {leftovers}"


def test_pages_get_the_script_inlined():
    for name in PAGES:
        html = page(name)
        assert "<!-- i18n -->" not in html and "function applyI18n" in html, name
