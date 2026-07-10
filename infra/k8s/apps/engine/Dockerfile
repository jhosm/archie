# Pack-baked engine image for the staging cluster (bd babelstone-zla1.5.1).
#
# Plain English: the published engine image is regulatory-pack-agnostic — it pulls a
# signed pack at runtime. The always-on staging box runs the engine in the simpler
# "disk" pack mode, which needs the pack files present on disk at /app/packs. The pack
# is a directory tree (subdirs: primitives/, parameters/, test-corpus/, ...), which a
# flat ConfigMap can't carry — so we bake pt.2026.1 into a thin derived image here.
#
# This image is FROM the base engine image the image-build matrix already builds + pushes
# (amd64-only, bd babelstone-zla1.13), so it inherits the exact runtime + entrypoint; it only adds
# the pack layer. Build context is the REPO ROOT (so the COPY can reach packs/). The base
# tag is a build-arg so it can be pinned by digest for a real promotion (a movable :latest
# is fine for the demo box); see infra/k8s/apps/README.md.
ARG BASE_IMAGE=ghcr.io/jhosm/babelstone-engine:latest
FROM ${BASE_IMAGE}

# Engine__PackRegistry=disk + Engine__PacksDir=/app/packs (set on the Deployment) load the
# pack from here. World-readable; the engine runs as the non-root UID baked into the base.
COPY packs/pt.2026.1 /app/packs/pt.2026.1

# The term_deposit family reads its per-variant product-config YAMLs from disk too
# (Babelstone.RateSheets.YamlProductConfigStore). Like the pack tree, these are source data
# absent from the published base image (`COPY --from=build /app .` ships only build output),
# so bake them in and point the Deployment at them via Engine__ProductConfigsDir=/app/product-configs.
# Without this the first constitute 500s: FindProductConfigsDir walks up from /app and finds nothing.
COPY product-configs /app/product-configs
