# /mcp-server (Python MCP server on the official Python SDK).
# Python pinned by mise.toml (3.14, ADR-IC-010); placement by ADR-IC-013.
# E.5: the minimal dev server (constitute_deposit + get_deposit tools),
# Streamable HTTP, hitting the engine command/query host directly. Auth is Epic J.

FROM python:3.14-slim
WORKDIR /app
COPY . .
RUN pip install --no-cache-dir .
# Reaches the engine boundary at BABELSTONE_ENGINE_URL (default http://localhost:8080).
ENTRYPOINT ["python", "-m", "babelstone_mcp"]
