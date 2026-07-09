# Rate-sheet-deploy image for the staging cluster (bd babelstone-zla1.21). The always-on staging box
# must have the committed rate sheets in its rate_sheets table or deposits 422 ("no rate sheet
# effective"). Rate sheets deploy through the VALIDATED seam (ADR-PC-008 §P2 — RateSheets.Api validates
# each sheet's TANs against the pack bounds, then stores it; never a raw INSERT). This image bakes both
# inputs the deploy needs and runs it once over EVERY committed sheet, so a `kubectl apply` provisions
# them on bring-up (idempotent — an unchanged re-deploy is a 200 no-op).
#
# Build context is the REPO ROOT (so the COPYs reach packs/ + rate-sheets/); the base tag is a
# build-arg so it can be pinned by digest for a real promotion (a movable :latest is fine for the demo
# box), exactly like the engine-staging / notification-staging derived images.

# Serialise EVERY committed rate-sheet YAML to JSON 1:1 (the deploy wire format) with the SAME pinned
# js-yaml the demo + scripts/deploy-rate-sheet.sh use, preserving the family subdir layout, so the
# baked JSON set cannot drift from rate-sheets/. Adding a new sheet (a version or a new family) is just
# committing its YAML — this stage picks it up (rate-sheets/** is an image-build trigger path).
FROM node:22-alpine AS serialise
WORKDIR /rs
COPY rate-sheets/ ./src/
RUN set -e; mkdir -p out; \
    for y in $(find src -name '*.yaml' -not -iname 'README*'); do \
      rel="${y#src/}"; out="out/${rel%.yaml}.json"; \
      mkdir -p "$(dirname "$out")"; \
      echo "serialising $y -> $out"; \
      npx --yes js-yaml@4.1.0 "$y" > "$out"; \
    done

ARG BASE_IMAGE=ghcr.io/jhosm/babelstone-ratesheets-api:latest
FROM ${BASE_IMAGE}
# The pack (for the deploy-time bound check — RateSheets:PacksDir=/app/packs, set on the Job) and every
# serialised sheet under /app/rate-sheets (RateSheets:DeployOnStartup=/app/rate-sheets — the Job deploys
# the whole directory). World-readable; the host runs as the non-root UID baked into the base.
COPY packs/pt.2026.1 /app/packs/pt.2026.1
COPY --from=serialise /rs/out /app/rate-sheets
