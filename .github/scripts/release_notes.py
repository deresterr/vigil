"""Release notes from CHANGELOG.md, for the release workflow and for checking a release pull request.

    python3 .github/scripts/release_notes.py 0.9.1 [v0.9.0]   -> the notes on stdout

Takes the section "## [0.9.1] - <date>", checks it follows the template in docs/releasing.md (a summary, then the
update table), joins the lines wrapped in the file (a GitHub release keeps every line break, a README does not)
and adds a footer: the server image, the client file, the compare link to the previous tag. Exits 1 with a
message when the section is missing or does not follow the template, before the workflow publishes anything.
"""

import os
import re
import sys
from pathlib import Path

CHANGELOG = Path(__file__).resolve().parents[2] / "CHANGELOG.md"
REPO = os.environ.get("GITHUB_REPOSITORY", "deresterr/vigil")
BLOCK_START = re.compile(r"(#{1,6} |[-*] |\d+\. |\||```|<|---|> )")


def section(text: str, version: str) -> list[str]:
    lines = text.splitlines()
    head = re.compile(rf"^## \[{re.escape(version)}\] - \d{{4}}-\d{{2}}-\d{{2}}$")
    start = next((i for i, line in enumerate(lines) if head.match(line)), None)
    if start is None:
        raise SystemExit(f"CHANGELOG.md has no section '## [{version}] - YYYY-MM-DD'")
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ") or lines[i].startswith("[")),
               len(lines))
    body = lines[start + 1:end]
    while body and not body[0].strip():
        body.pop(0)
    while body and not body[-1].strip():
        body.pop()
    return body


def check(body: list[str], version: str) -> None:
    """The template: a summary paragraph, then the update table with a Server and a Children's PCs row."""
    rows = [line for line in body if line.startswith("|")]
    problems = []
    if not body or BLOCK_START.match(body[0]):
        problems.append("it does not start with a summary paragraph")
    if not any(line.startswith("| Server |") for line in rows):
        problems.append("the update table has no '| Server |' row")
    if not any(line.startswith("| Children's PCs |") for line in rows):
        problems.append("the update table has no \"| Children's PCs |\" row")
    if not any(line.startswith("### ") for line in body):
        problems.append("it has no '### ' group of changes")
    if problems:
        raise SystemExit(f"CHANGELOG.md [{version}] does not follow docs/releasing.md: " + "; ".join(problems))


def unwrap(body: list[str]) -> list[str]:
    """Join a line wrapped in the file to the paragraph, list item or quote it continues."""
    out: list[str] = []
    fence = False
    for line in body:
        if line.startswith("```"):
            fence = not fence
            out.append(line)
            continue
        prev = out[-1] if out else ""
        flowing = bool(prev.strip()) and not fence and not re.match(r"(#{1,6} |\||```|<|---)", prev)
        stripped = line.strip()
        if fence or not stripped or not flowing:
            out.append(line)
        elif prev.startswith("> ") and line.startswith("> ") and not BLOCK_START.match(line[2:]):
            out[-1] = f"{prev} {line[2:].strip()}"
        elif not prev.startswith("> ") and not BLOCK_START.match(stripped):
            out[-1] = f"{prev} {stripped}"
        else:
            out.append(line)
    return out


def footer(version: str, previous: str | None) -> list[str]:
    owner = REPO.split("/")[0].lower()
    lines = [
        "",
        "---",
        "",
        f"**Server:** `ghcr.io/{owner}/vigil-server:{version}` (also `:latest`) · **Client:** `vigil_setup.exe` "
        f"below, the whole client in one file. New to Vigil? Start with the "
        f"[quick start](https://github.com/{REPO}#quick-start).",
    ]
    if previous:
        lines += ["", f"**All changes:** [{previous}...v{version}](https://github.com/{REPO}/compare/"
                      f"{previous}...v{version})"]
    return lines


def main() -> None:
    if len(sys.argv) not in (2, 3):
        raise SystemExit(__doc__)
    version = sys.argv[1].removeprefix("v")
    previous = sys.argv[2] if len(sys.argv) == 3 and sys.argv[2] else None
    body = section(CHANGELOG.read_text(encoding="utf-8"), version)
    check(body, version)
    print("\n".join(unwrap(body) + footer(version, previous)))


if __name__ == "__main__":
    main()
