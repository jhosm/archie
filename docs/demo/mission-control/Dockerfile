# Mission Control UI image (bd babelstone-zla1.5.4).
#
# Plain English: packages the Mission Control demo UI (the static index.html + the tiny
# stdlib-only reverse-proxy serve.py) as a container so it can run on the staging cluster.
# It needs nothing but a Python runtime — no pip install, no requirements.txt.
#
# Build context is THIS directory (docs/demo/mission-control), matching the self-contained
# image-build legs (mcp-server, pack-validate); see .github/workflows/image-build.yml.
FROM python:3.12-slim

WORKDIR /app
COPY serve.py index.html ./

# In a container we must bind all interfaces (not serve.py's localhost-only code default),
# so the kube Service and the readiness/liveness probes reach it via the pod IP. The proxy
# upstreams (ENGINE_URL / ORCHESTRATOR_URL / TEMPO_URL / AGENT_URL) are set by the
# Deployment (zla1.5.5) to in-cluster Service DNS; the localhost defaults apply only off-cluster.
ENV MC_BIND=0.0.0.0 \
    MC_PORT=9000

EXPOSE 9000

# Run unprivileged (port 9000 needs no privilege); the copied files are world-readable.
USER 65534:65534

CMD ["python3", "serve.py"]
