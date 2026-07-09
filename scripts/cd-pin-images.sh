#!/usr/bin/env bash
# scripts/cd-pin-images.sh — pin the first-party container images in a promoted
# overlay to their exact cosign-signed digests (Q.6 / bd babelstone-2t16.30 §A2,
# babelstone-2t16.31.2 §A4; ADR-PC-007 §A2/§A4).
#
# In plain English: the CD pipeline cosign-verifies each image by its immutable
# sha256 digest, but the manifests it deploys still say ":latest" — so the kubelet
# can pull different bytes than the ones that were verified (a time-of-check-to-
# time-of-use gap). This script closes that gap for the FIRST-PARTY images: it takes
# the overlay the promotion is about to apply, resolves every ghcr.io/jhosm/babelstone-*
# image to an immutable digest, cosign-verifies THAT digest against the image-build.yml
# signing identity, and pins the manifest to it. One digest flows resolve -> verify ->
# deploy, so the bytes verified are the bytes deployed.
#
# WHICH build a first-party image resolves to depends on PROMOTE_GIT_REF:
#   • unset (§A2, bd babelstone-2t16.30): the image's CURRENT tag (:latest) — "whatever
#     latest is now", atomic + verified but not tied to a specific commit.
#   • set to a full git SHA (§A4, bd babelstone-2t16.31.2): the build of the PROMOTED
#     COMMIT, via the immutable sha-<full-commit> tag image-build.yml stamps
#     (type=sha,prefix=sha-,format=long) — so a promotion deploys the exact commit's build.
#     Because image builds are PATH-SCOPED, a given commit may not have rebuilt every image,
#     so sha-<promoted> can 404 for some images. On a miss we walk the promoted commit's git
#     ancestors newest-first and pin the newest ancestor that DOES have a sha- build (loudly
#     logged) — i.e. "this image exactly as it stood as of the promoted commit". We NEVER
#     silently fall back to :latest. PROMOTE_STRICT=1 fails the promotion on a miss instead of
#     walking (require an exact-commit build for every image).
#
# Scope: first-party images only (the ones image-build.yml builds AND cosign-signs). Third-party
# images (svhd/logto, postgres, kong, …) are pinned for reproducibility SEPARATELY (bd
# babelstone-2t16.31.1, a sibling change — kustomize `images:` transformers, not cosign-verified) —
# the ghcr.io/jhosm/babelstone- prefix filter here deliberately excludes them (nothing signs them
# under our identity to verify).
#
# Modes:
#   --contract <overlay-dir>   HERMETIC (no registry, CI-runnable on the push/PR gate
#                              lane): render the overlay, list the first-party images it
#                              would resolve+verify+pin, and FAIL if there are none — the
#                              same "assert the contract with no live digests" posture the
#                              verify-images job uses. Proves the path stays wired. PROMOTE_GIT_REF
#                              does not change this hermetic mode (it needs a live registry + git).
#   --pin <overlay-dir>        REAL (needs registry read + cosign on PATH; the commit-pin path also
#                              needs the git history, so the promote job checks out fetch-depth:0):
#                              for each first-party image the overlay renders, resolve the digest
#                              (per PROMOTE_GIT_REF above), cosign-verify name@digest against the
#                              signing identity, then `kustomize edit set image` the overlay to that
#                              digest. Fail-closed: an unresolvable or unverifiable image aborts the
#                              promotion. cd.yml runs the subsequent `kustomize build | kubectl
#                              apply` against the pinned overlay.
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

# Resolve a first-party image NAME to the digest of the PROMOTED COMMIT's build (§A4).
# Echoes the sha256 digest on stdout; logs the human-readable choice to stderr (so it shows in the
# workflow log but is not captured). Returns non-zero — which, under `set -e`, aborts the promotion
# from the calling `$(...)` — if there is no usable build (never falling back to :latest).
#   $1 = image name (no tag/digest)   $2 = promoted commit, a full 40-hex git SHA
commit_build_digest() {
  local name="$1" ref="$2" built chosen dist
  # The set of commits this image actually has a build for (its sha-<40-hex> tags → bare SHAs).
  built="$(CRANE ls "$name" 2>/dev/null | sed -nE 's/^sha-([0-9a-f]{40})$/\1/p' || true)"
  if [ -z "$built" ]; then
    echo "::error::${name} has no sha-<commit> builds in the registry — cannot commit-pin (never falling back to :latest)." >&2
    return 1
  fi
  if printf '%s\n' "$built" | grep -qx "$ref"; then
    chosen="$ref"
    echo "  ${name}: exact build for the promoted commit (sha-${ref})" >&2
  elif [ -n "${PROMOTE_STRICT:-}" ]; then
    echo "::error::${name} has no build at the promoted commit (sha-${ref}) and PROMOTE_STRICT is set — refusing to fall back." >&2
    return 1
  else
    # Walk the promoted commit's ancestors newest-first; pick the first with a build. `git rev-list`
    # emits newest-first (the ref itself first), so `grep -m1` yields the newest ancestor build.
    chosen="$(git rev-list "$ref" | grep -m1 -Fxf <(printf '%s\n' "$built") || true)"
    if [ -z "$chosen" ]; then
      echo "::error::${name} has no build at the promoted commit (sha-${ref}) nor at any ancestor of it — cannot commit-pin (never falling back to :latest)." >&2
      return 1
    fi
    dist="$(git rev-list --count "${chosen}..${ref}" 2>/dev/null || echo '?')"
    echo "::warning::${name} has no build at the promoted commit; falling back to the newest ancestor build sha-${chosen} (${dist} commit(s) back) — this image as of sha-${ref}." >&2
  fi
  CRANE digest "${name}:sha-${chosen}"
}

usage() { echo "usage: $0 --contract|--pin <overlay-dir>   (env: PROMOTE_GIT_REF=<full-sha> for commit-pinning, PROMOTE_STRICT=1 to fail on a miss)" >&2; exit 2; }

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
    if [ -n "${PROMOTE_GIT_REF:-}" ]; then
      echo "commit-pinned promotion (§A4): resolving first-party images to the build of ${PROMOTE_GIT_REF}${PROMOTE_STRICT:+ [STRICT: no ancestor fallback]}"
    else
      echo "latest-resolved promotion (§A2): resolving first-party images to their current in-manifest tag"
    fi
    while IFS= read -r ref; do
      [ -z "$ref" ] && continue
      if [ -n "${PROMOTE_GIT_REF:-}" ]; then
        # Commit-pin: ignore the in-manifest tag; resolve the promoted commit's build by name.
        name="${ref%@*}"; name="${name%:*}"     # bare image name (strip any @digest then any :tag)
        digest="$(commit_build_digest "$name" "$PROMOTE_GIT_REF")"
      elif [[ "$ref" == *"@sha256:"* ]]; then
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
