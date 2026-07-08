# Dockerfile — the babelstone Backstage catalogue portal (ADR-IC-015; bd babelstone-zla1.6.3).
#
# In plain English: this turns the scaffolded Backstage app under backstage/ into a real,
# self-contained container image — it compiles the app INSIDE docker (no host toolchain) and
# bakes the WHOLE governed catalogue tree in, so the portal renders the events, reconciliation
# contracts and OpenAPI surfaces with zero drift from the source-of-truth files.
#
# Build context is the REPO ROOT (context: .), NOT backstage/, because the image must COPY
# files from OUTSIDE the app tree: the governed app-config (infra/backstage/app-config.yaml)
# and the catalogue descriptor tree (contracts/catalog/ + the contracts/openapi/ files it
# $text-references). Every backstage/ path below is therefore repo-root-relative. This mirrors
# the .NET legs in .github/workflows/image-build.yml, which also build with the repo root as
# context. Build with:
#   docker build -f backstage/Dockerfile -t babelstone-backstage .
#
# The stock create-app packages/backend/Dockerfile is single-stage and expects host-prebuilt
# dist artefacts (yarn install + yarn build:backend run on the HOST first). This is instead the
# multi-stage variant Backstage documents
# (https://backstage.io/docs/deployment/docker#multi-stage-build): the app is installed and built
# in-image on node 22 (bd zla1.6.3 mandate), so CI needs no node/yarn on the runner.
#
# amd64-only: staging is a Hetzner CPX42 x86 node (bd babelstone-zla1.13), so a single
# linux/amd64 manifest is all that ever runs — the image-build.yml matrix pins platforms
# accordingly and no QEMU/arm64 leg is emitted.

# ── Stage 1 — yarn install skeleton ──────────────────────────────────────────────────────────
# Copy only the manifests (package.json per workspace + the lockfile) so the heavy `yarn install`
# layer below caches on dependency changes, not source edits — the standard Backstage skeleton
# trick. `find` strips everything but the package.json files at packages/*/.
FROM node:22-bookworm-slim AS packages

WORKDIR /app
COPY backstage/.yarn ./.yarn
COPY backstage/.yarnrc.yml backstage/package.json backstage/yarn.lock backstage/backstage.json ./

COPY backstage/packages packages
# plugins/ carries no local plugin packages today (just a README), but keep the copy so the
# workspace glob (plugins/*) resolves identically to a full checkout.
COPY backstage/plugins plugins

RUN find packages \! -name "package.json" -mindepth 2 -maxdepth 2 -exec rm -rf {} \+

# ── Stage 2 — install deps + build the backend (which bundles the frontend) ───────────────────
FROM node:22-bookworm-slim AS build

# node-gyp / better-sqlite3 compile native addons during install, so the build toolchain is
# needed here. python3 is the node-gyp interpreter (ENV PYTHON, matching the stock backend
# Dockerfile's create-app posture for this yarn 4 app).
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    apt-get update && \
    apt-get install -y --no-install-recommends python3 g++ build-essential && \
    rm -rf /var/lib/apt/lists/*
ENV PYTHON=/usr/bin/python3

# Least-privileged `node` user (uid 1000) for install + build.
USER node
WORKDIR /app

# Pull in the skeleton, then install against the immutable lockfile (fails loud on drift).
COPY --from=packages --chown=node:node /app .
RUN --mount=type=cache,target=/home/node/.cache/yarn,sharing=locked,uid=1000,gid=1000 \
    yarn install --immutable

# Now the full app source, then compile. `yarn build:backend` builds the backend AND its local
# dependency closure — including the frontend `app` package (link:../app) — packing the result,
# frontend static assets and all, into packages/backend/dist/bundle.tar.gz that the runtime
# stage unpacks. `yarn tsc` typechecks the whole workspace first.
COPY --chown=node:node backstage/ ./
RUN yarn tsc
RUN yarn build:backend

# ── Stage 3 — slim runtime ────────────────────────────────────────────────────────────────────
FROM node:22-bookworm-slim

# The runtime reinstalls production deps (yarn workspaces focus), which rebuilds better-sqlite3's
# native addon, so the same toolchain is needed. libsqlite3-dev matches the stock runtime.
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    apt-get update && \
    apt-get install -y --no-install-recommends python3 g++ build-essential libsqlite3-dev && \
    rm -rf /var/lib/apt/lists/*
ENV PYTHON=/usr/bin/python3

# Non-root at runtime (uid 1000) — satisfies the runAsNonRoot posture the k8s Deployment pins
# (infra/k8s/base/backstage.yaml; ADR-IC-016 zero-trust / CIS PSA).
USER node
WORKDIR /app

# Yarn config + the install skeleton, then a production-only install (no devDependencies).
COPY --from=build --chown=node:node /app/.yarn ./.yarn
COPY --from=build --chown=node:node /app/.yarnrc.yml ./
COPY --from=build --chown=node:node /app/backstage.json ./
COPY --from=build --chown=node:node /app/yarn.lock /app/package.json /app/packages/backend/dist/skeleton.tar.gz ./
RUN tar xzf skeleton.tar.gz && rm skeleton.tar.gz

ENV NODE_ENV=production
# Backstage's scaffolder isolate-vm needs node snapshots disabled (stock create-app posture).
ENV NODE_OPTIONS="--no-node-snapshot"

RUN --mount=type=cache,target=/home/node/.cache/yarn,sharing=locked,uid=1000,gid=1000 \
    yarn workspaces focus --all --production && rm -rf "$(yarn cache clean)"

# The built backend bundle (backend + frontend static assets).
COPY --from=build --chown=node:node /app/packages/backend/dist/bundle.tar.gz ./
RUN tar xzf bundle.tar.gz && rm bundle.tar.gz

# ── The babelstone catalogue bake (ADR-IC-015 no-drift invariant; bd zla1.6.3) ────────────────
# THE POINT of this image: carry the governed catalogue tree inside it. A flat ConfigMap can't
# hold the events/ + reconciliation/ SUBDIRS, so the descriptor + all the files its $text refs
# point at must live in the image at paths that keep those relative refs resolving from
# /catalog/catalog-info.yaml:
#   * ./events/*.asyncapi.yaml         -> /catalog/events/…          (from contracts/catalog/)
#   * ./reconciliation/*.yaml          -> /catalog/reconciliation/…  (from contracts/catalog/)
#   * ../openapi/specs|internal/*.yaml -> /openapi/specs|internal/…  (referenced by catalog-info.yaml)
# The OpenAPI specs/ + internal/ dirs are baked at /openapi so the 9 OpenAPI API entities' $text
# refs resolve too — omitting them would leave the governed catalogue rendering with broken
# $text refs, contradicting the very no-drift invariant this image exists to serve. The _selftest/
# fixtures under contracts/openapi/ are intentionally NOT baked (malformed-by-design gate inputs,
# unreferenced by the descriptor).
COPY --chown=node:node contracts/catalog/ /catalog/
COPY --chown=node:node contracts/openapi/specs/ /openapi/specs/
COPY --chown=node:node contracts/openapi/internal/ /openapi/internal/

# app-config layering (the REVIEW-MANDATED overwrite, adr-conformance advisory on zla1.6.3):
#   * app-config.yaml            — the app's base defaults (auth providers, csp/cors, techdocs,
#                                  permissions) the governed override does NOT restate.
#   * app-config.production.yaml — the GOVERNED infra/backstage/app-config.yaml, whose single
#                                  catalog.locations entry (/catalog/catalog-info.yaml) ARRAY-
#                                  REPLACES the base's example Locations. This deliberately does
#                                  NOT copy create-app's stock backstage/app-config.production.yaml
#                                  (which still carries ./examples/*.yaml demo Locations) — that
#                                  file never enters the image. Loaded LAST in the CMD so its
#                                  array-replace wins: no example Location can leak into the portal.
COPY --chown=node:node backstage/app-config.yaml ./app-config.yaml
COPY --chown=node:node infra/backstage/app-config.yaml ./app-config.production.yaml

EXPOSE 7007

# Liveness of the backend HTTP listener on 7007 (matches the k8s readiness probe's path/port).
# node -e keeps the slim image curl/wget-free; a <500 status is healthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD node -e "require('http').get('http://127.0.0.1:7007/',r=>process.exit(r.statusCode<500?0:1)).on('error',()=>process.exit(1))"

CMD ["node", "packages/backend", "--config", "app-config.yaml", "--config", "app-config.production.yaml"]
