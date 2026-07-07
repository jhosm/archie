# Pack-baked notification image for the staging cluster (bd babelstone-zla1.5.10).
#
# Plain English: the published notification image is regulatory-pack-agnostic — its host
# resolves the instance-pinned pack at startup (ADR-PC-007 §P4), which on the always-on
# staging box means reading the pack files off disk at /app/packs. The pack is a directory
# tree (subdirs: primitives/, parameters/, templates/, rate-sheet-refs/, test-corpus/) that
# a flat ConfigMap can't carry — so we bake pt.2026.1 into a thin derived image here, exactly
# as the engine-staging image does (bd babelstone-zla1.5.1).
#
# This image is FROM the base notification image the image-build matrix already builds +
# pushes (multi-arch), so it inherits the exact runtime + entrypoint (Babelstone.Notification.Host);
# it only adds the pack layer. Build context is the REPO ROOT (so the COPY can reach packs/). The
# base tag is a build-arg so it can be pinned by digest for a real promotion (a movable :latest is
# fine for the demo box); see infra/k8s/apps/README.md.
ARG BASE_IMAGE=ghcr.io/jhosm/babelstone-notification:latest
FROM ${BASE_IMAGE}

# Engine__PacksDir=/app/packs (set on the Deployment) points the host's LoadPinnedPack disk walk
# here. World-readable; the host runs as the non-root UID baked into the base image.
COPY packs/pt.2026.1 /app/packs/pt.2026.1
