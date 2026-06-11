# /mcp-server (Python MCP server on the official Python SDK).
# Python pinned by mise.toml (3.14, ADR-IC-010); placement by ADR-IC-013.
# E.5: the minimal dev server (constitute_deposit + get_deposit tools),
# Streamable HTTP, hitting the engine command/query host directly. Auth is Epic J.

FROM python:3.14-slim
WORKDIR /app
# Pull the latest Debian security patches into the base layer before installing the app. The
# python:3.14-slim base lags Debian's published fixes, which is where the OS-package CVEs the
# image-build.yml grype scan flags (ADR-IC-014 §S2) — libc6, perl-base, tar — live. There is no
# chiseled Python image (unlike the .NET services), so patch-on-build is the slim path here; the
# CPython-level CVEs clear only on a newer base tag, tracked as accepted residual risk.
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*
COPY . .
RUN pip install --no-cache-dir --upgrade pip setuptools \
    && pip install --no-cache-dir .
# Reaches the engine boundary at BABELSTONE_ENGINE_URL (default http://localhost:8080).
ENTRYPOINT ["python", "-m", "babelstone_mcp"]
