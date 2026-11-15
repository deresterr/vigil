"""Which client the panel calls current (#159)."""

import os
import re

_RELEASE = re.compile(r"(\d+)\.(\d+)\.(\d+)")

# The newest client release up to this server: release.yml takes the latest CHANGELOG.md section whose
# "Children's PCs" row says **Yes.** (release_notes.py --client-version) and passes it as
# --build-arg VIGIL_CLIENT_VERSION (Dockerfile ENV). Not the server's own version: a server-only release would flag
# every PC. Empty in a local or CI build: nothing to compare with.
CLIENT_VERSION = os.environ.get("VIGIL_CLIENT_VERSION") or ""


def _release(version: str | None) -> tuple[int, int, int] | None:
    m = _RELEASE.fullmatch(version or "")
    return (int(m[1]), int(m[2]), int(m[3])) if m else None


def update_needed(pc_version: str | None) -> bool:
    """Whether the PC runs an older client than CLIENT_VERSION. A heartbeat without a version comes from a client
    up to 0.9; a CI or local build of either side (0.0.0-ci.N, 0.0.0-dev, dev) is never flagged."""
    newest = _release(CLIENT_VERSION)
    if newest is None:
        return False
    if pc_version is None:
        return True
    pc = _release(pc_version)
    return pc is not None and pc < newest
