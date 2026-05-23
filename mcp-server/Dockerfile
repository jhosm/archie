# Skeleton Dockerfile — /mcp-server (Python MCP server on the official Python SDK).
# Runtime pinned by ADR-IC-010 (Python SDK); placement by ADR-IC-013.
# Source (pyproject.toml, src/) and the ENTRYPOINT land with the MCP server build task.

FROM python:3.12-slim
WORKDIR /app
COPY . .
RUN pip install --no-cache-dir .
# ENTRYPOINT ["python", "-m", "babelstone_mcp"]   # set with the MCP server build task
