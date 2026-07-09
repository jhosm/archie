#!/usr/bin/env bash
# scripts/cd-pin-images.sh — pin the first-party container images in a promoted
# overlay to their exact cosign-signed digests (Q.6 / bd babelstone-2t16.30;
# ADR-PC-007 §A2).
#
# In plain English: the CD pipeline cosign-verifies each image by its immutable
# sha256 digest, but the manifests it deploys still say ":latest" — so the kubelet
# can pull different bytes than the ones that were verified (a time-of-check-to-
# time-of-use gap). This script closes that gap for the FIRST-PARTY images: it takes
# the overlay the promotion is about to apply, resolves every ghcr.io/jhosm/babelstone-*
# image to the exact digest the tag currently points at, cosign-verifies THAT digest
# against the image-build.yml signing identity, and pins the manifest to it. One digest
# flows resolve -> verify -> deploy, so the bytes verified are the bytes deployed.
#
# Scope: first-party images only (the ones image-build.yml builds AND cosign-signs).
# Third-party images (svhd/logto, postgres, kong, …) and commit-pinned promotion are a
# separate follow-up (bd babelstone-2t16.31) — the ghcr.io/jhosm/babelstone- prefix
# filter deliberately excludes them (nothing signs them under our identity to verify).
#
# Modes:
#   --contract <overlay-dir>   HERMETIC (no registry, CI-runnable on the push/PR gate
#                              lane): render the overlay, list the first-party images it
#                              would resolve+verify+pin, and FAIL if there are none — the
#                              same "assert the contract with no live digests" posture the
#                              verify-images job uses. Proves the path stays wired.
#   --pin <overlay-dir>        REAL (needs registry read + cosign on PATH): for each
#                              first-party image the overlay renders, resolve its tag to a
#                              digest (crane), cosign-verify name@digest against the signing
#                              identity, then `kustomize edit set image` the overlay to that
#                              digest. Fail-closed: an unresolvable or unverifiable image
#                              aborts the promotion. cd.yml runs the subsequent
#                              `kustomize build | kubectl apply` against the pinned overlay.
#
# The signing identity mirrors the verify-images job in .github/workflows/cd.yml — the
# OIDC identity image-build.yml signs under (both encode the same immutable fact: the
# repo's signing workflow). Override via the two COSIGN_* env vars if that ever changes.

set -euo pipefail

MODE="${1:-}"
OVERLAY_DIR="${2:-}"

IMAGE_PREFIX="ghcr.io/jhosm/babelstone-"
COSIGN_CERT_IDENTITY_REGEX="${COSIGN_CERT_IDENTITY_REGEX:-https://github.com/jhosm/babelstone/.github/workflows/image-build.yml@.*}"
COSIGN_CERT_OIDC_ISSUER="${COSIGN_CERT_OIDC_ISSUER:-https://token.actions.githubusercontent.com}"

KUSTOMIZE() { mise exec -- kustomize "$@"; }
CRANE()     { mise exec -- crane "$@"; }

# Render the overlay and emit the unique first-party image refs (name:tag) it deploys.
first_party_images() {
  local dir="$1"
  KUSTOMIZE build --load-restrictor=LoadRestrictionsNone "$dir" \
    | grep -E '^[[:space:]]*image:[[:space:]]' \
    | sed -E 's/^[[:space:]]*image:[[:space:]]*//; s/["'\'']//g' \
    | grep -F "$IMAGE_PREFIX" \
    | sort -u
}

usage() { echo "usage: $0 --contract|--pin <overlay-dir>" >&2; exit 2; }

[ -n "$MODE" ] && [ -n "$OVERLAY_DIR" ] || usage
[ -d "$OVERLAY_DIR" ] || { echo "::error::overlay dir not found: $OVERLAY_DIR" >&2; exit 2; }

case "$MODE" in
  --contract)
    imgs="$(first_party_images "$OVERLAY_DIR" || true)"
    if [ -z "$imgs" ]; then
      echo "::error::no first-party ($IMAGE_PREFIX*) images found to pin in $OVERLAY_DIR" >&2
      exit 1
    fi
    echo "digest-pin contract OK — a real promotion would resolve + cosign-verify + pin:"
    while IFS= read -r img; do [ -n "$img" ] && echo "  - $img"; done <<< "$imgs"
    ;;

  --pin)
    imgs="$(first_party_images "$OVERLAY_DIR")"
    if [ -z "$imgs" ]; then
      echo "::error::no first-party ($IMAGE_PREFIX*) images to pin in $OVERLAY_DIR" >&2
      exit 1
    fi
    while IFS= read -r ref; do
      [ -z "$ref" ] && continue
      if [[ "$ref" == *"@sha256:"* ]]; then
        # Already digest-pinned in the manifest — verify as-is, nothing to resolve.
        name="${ref%@*}"; digest="${ref#*@}"
      else
        name="${ref%:*}"                       # strip the :tag (refs here carry no registry port)
        digest="$(CRANE digest "$ref")"        # resolve the movable tag to the immutable manifest digest
      fi
      pinned="${name}@${digest}"
      echo "::group::cosign verify $pinned"
      cosign verify \
        --certificate-identity-regexp "$COSIGN_CERT_IDENTITY_REGEX" \
        --certificate-oidc-issuer "$COSIGN_CERT_OIDC_ISSUER" \
        "$pinned" >/dev/null
      echo "::endgroup::"
      ( cd "$OVERLAY_DIR" && KUSTOMIZE edit set image "${name}@${digest}" )
      echo "pinned ${name} -> ${digest}"
    done <<< "$imgs"
    echo "all first-party images in $OVERLAY_DIR pinned to verified digests"
    ;;

  *) usage ;;
esac
