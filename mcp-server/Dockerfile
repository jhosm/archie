# /mcp-server (Python MCP server on the official Python SDK).
# Python pinned by mise.toml (3.14, ADR-IC-010); placement by ADR-IC-013.
# E.5: the minimal dev server (constitute_deposit + get_deposit tools),
# Streamable HTTP, hitting the engine command/query host directly. Auth is Epic J.

FROM python:3.14-slim
WORKDIR /app
# Best-effort base hardening: apply whatever Debian security updates ARE published before
# installing the app. This is cheap insurance, not a guaranteed fix — it only clears CVEs Debian
# has already patched. Most of the OS-package CVEs the image-build.yml grype scan flags (ADR-IC-014
# §S2) on this base — libc6, perl-base, tar, util-linux, ncurses — are currently unfixed upstream,
# so they REMAIN as accepted residual risk after this step (verified: the post-rebuild scan cleared
# none of them). The two CPython-level CVEs are interpreter-level and apt cannot touch them at all.
# There is no chiseled Python image (unlike the .NET services), so the real remediation lever is a
# newer base tag — surfaced automatically by the `docker` Dependabot ecosystem (.github/dependabot.yml).
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*
COPY . .
RUN pip install --no-cache-dir --upgrade pip setuptools \
    && pip install --no-cache-dir .
# Reaches the engine boundary at BABELSTONE_ENGINE_URL (default http://localhost:8080).
ENTRYPOINT ["python", "-m", "babelstone_mcp"]
