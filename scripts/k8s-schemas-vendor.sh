#!/usr/bin/env bash
# Vendor the Kubernetes JSON schemas the manifest-validation gate needs, so
# `kubeconform` never fetches them from raw.githubusercontent.com at CI time
# (bd babelstone-6qt9).
#
# Plain English: renders every k8s overlay, works out which schema files
# kubeconform will ask for, and downloads any not already committed under
# infra/k8s/schemas/. The CI `infra` job (ci.yml) and CD `render` job (cd.yml)
# validate against those committed files with `-schema-location`, so they never
# hit raw.githubusercontent.com — which rate-limits (HTTP 429) the shared
# GitHub-runner IPs and made the gate flakily red on unrelated changes.
#
# Run this once after adding a NEW Kind to any manifest: a Kind with no vendored
# schema makes the gate fail loud ("no schema found for …") — that is the signal
# to re-run this. CI itself does NOT run this; it only reads the committed set.
#
# Usage:  make k8s-schemas   (or: ./scripts/k8s-schemas-vendor.sh)
#
# Portable to bash 3.2 (macOS default): no `mapfile`, no arrays.
set -euo pipefail

# Keep K8S_VER in lockstep with `-kubernetes-version` in ci.yml + cd.yml.
K8S_VER="v1.31.0"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"
DIR="infra/k8s/schemas/${K8S_VER}-standalone-strict"
BASE="https://raw.githubusercontent.com/yannh/kubernetes-json-schema/master/${K8S_VER}-standalone-strict"
mkdir -p "$DIR"

rendered="$(mktemp)"
needed="$(mktemp)"
trap 'rm -f "$rendered" "$needed"' EXIT

echo "== rendering overlays to enumerate the schemas kubeconform needs =="
for t in base overlays/ha overlays/staging; do
  mise exec -- kustomize build --load-restrictor=LoadRestrictionsNone "infra/k8s/$t"
  echo "---"
done > "$rendered"

# Map each rendered (apiVersion, kind) to the file kubeconform requests. stdlib
# only (no PyYAML dep): top-level keys sit at column 0 in kustomize output, so a
# nested apiVersion/kind (e.g. in ownerReferences) is never matched. The suffix
# rule mirrors kubeconform's own template: core "v1" -> "-v1"; "apps/v1" ->
# "-apps-v1"; "networking.k8s.io/v1" -> "-networking-v1" (first group label).
python3 - "$rendered" <<'PY' | sort -u > "$needed"
import re, sys
for doc in re.split(r'(?m)^---\s*$', open(sys.argv[1]).read()):
    av = re.search(r'(?m)^apiVersion:\s*(\S+)', doc)
    kd = re.search(r'(?m)^kind:\s*(\S+)', doc)
    if not (av and kd):
        continue
    av, kd = av.group(1), kd.group(1)
    if kd == "List":
        continue
    if "/" in av:
        grp, ver = av.split("/", 1)
        suffix = f"-{grp.split('.')[0]}-{ver}"
    else:
        suffix = f"-{av}"
    print(f"{kd.lower()}{suffix}.json")
PY
echo "$(wc -l < "$needed" | tr -d ' ') distinct schemas required by the manifests"

present=0; fetched=0; failed=0
while IFS= read -r f; do
  [ -n "$f" ] || continue
  if [ -s "$DIR/$f" ]; then present=$((present + 1)); continue; fi
  printf '  fetching %s ... ' "$f"
  # raw.githubusercontent.com 429-rate-limits bursts; --retry-all-errors + a
  # delay rides the throttle out (that is the whole bug this script exists for).
  if curl -fsS --retry 8 --retry-delay 4 --retry-all-errors --max-time 30 "$BASE/$f" -o "$DIR/$f"; then
    echo "ok"; fetched=$((fetched + 1))
  else
    echo "FAILED"; rm -f "$DIR/$f"; failed=$((failed + 1))
  fi
  sleep 1
done < "$needed"

echo "schemas: $present already vendored, $fetched newly fetched, $failed failed"
if [ "$failed" -ne 0 ]; then
  echo "ERROR: could not vendor $failed schema(s) — retry (raw.githubusercontent.com rate limit)." >&2
  exit 1
fi

# Informational: flag vendored files no longer referenced (not pruned automatically).
for existing in "$DIR"/*.json; do
  b="$(basename "$existing")"
  grep -qxF "$b" "$needed" \
    || echo "  note: $b is vendored but no longer referenced by any manifest (safe to delete)"
done

echo "OK — $DIR is in sync with the rendered manifests."
