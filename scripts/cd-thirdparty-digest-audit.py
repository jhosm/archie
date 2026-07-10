#!/usr/bin/env python3
"""cd-thirdparty-digest-audit.py — audit + refresh the third-party image digest pins.

In plain English: the Kubernetes manifests pin every third-party image (postgres, kong,
redpanda, logto, the k3s control-plane upgrader, …) to an immutable sha256 digest so a
redeploy pulls the EXACT bytes we validated (bd babelstone-2t16.31.1; ADR-PC-007 §A3). A
pinned digest goes stale as upstream rebuilds a rolling tag (postgres:18-alpine,
ghcr.io/logto-io/logto:1.41.0, …) — an unmaintained pin is a silent staleness hazard. This script is the
DELIBERATE bump lever the scheduled .github/workflows/cd-thirdparty-digest-audit.yml runs: it
re-resolves each pinned tag with `crane` and, in --write mode, refreshes the committed digest,
so a maintainer reviews a PR instead of the pins rotting quietly.

Why hand-rolled (not Dependabot): Dependabot's `docker` ecosystem parses Dockerfiles only —
it cannot see a kustomize `images:` transformer digest or the `image:` on a system-upgrade
Plan, which is exactly what we pin. Renovate can, but ADR-IC-014 chose Dependabot and rejected
Renovate on operational-simplicity grounds; this ~200-line auditor keeps that posture (no new
bot) while covering the niche Dependabot structurally cannot (ADR-IC-014 amendment 2026-07-09).

The pinned set is DISCOVERED from the manifests, never a hardcoded list that could drift out of
sync: the `- name:/newTag:/digest:` entries in the base + overlay kustomizations, and the
`image: …@sha256:…` on the bootstrap k3s-upgrade Plan.

Modes:
  --list    print the discovered pinned set and exit (no network — handy for testing discovery).
  --check   (default) re-resolve every pinned tag and report drift; also verify the same image
            carries the SAME digest everywhere it is pinned (postgres/openbao recur across
            kustomizations). Exit 1 on any drift or inconsistency, 0 when everything matches.
  --write   rewrite each drifted digest in place. The replacement is a global old→new digest
            substitution — a sha256 digest is unique, so every comment and line of formatting is
            preserved. The workflow opens a PR from the resulting git diff.

`crane` must be on PATH (the workflow gets it from mise-action; locally run under
`mise exec -- python3 scripts/cd-thirdparty-digest-audit.py …`). Override with the CRANE env var.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CRANE = os.environ.get("CRANE", "crane")

# The manifests that carry third-party digest pins. Kustomizations pin via `images:` transformer
# entries; the k3s-upgrade Plan pins its `image:` directly (it is a bootstrap CRD, not part of any
# overlay). Extend this list when a new overlay grows third-party pins (a reviewer sees the add).
KUSTOMIZATIONS = [
    REPO_ROOT / "infra/k8s/base/kustomization.yaml",
    REPO_ROOT / "infra/k8s/overlays/staging/kustomization.yaml",
    REPO_ROOT / "infra/k8s/overlays/ha/kustomization.yaml",
]
DIRECT_MANIFESTS = [
    REPO_ROOT / "infra/k8s/overlays/staging/bootstrap/k3s-upgrade-plan.yaml",
]

# A kustomize `images:` entry: `- name: X` then `newTag: Y` then `digest: sha256:…` (the order
# this repo writes them in). Whitespace-tolerant, anchored on the three consecutive keys.
_KUSTOMIZE_ENTRY = re.compile(
    r"-\s+name:\s+(?P<name>\S+)\s*\n"
    r"\s+newTag:\s+(?P<tag>\S+)\s*\n"
    r"\s+digest:\s+(?P<digest>sha256:[0-9a-f]+)",
    re.MULTILINE,
)
# A directly-pinned `image: name:tag@sha256:…` (only matches refs that ARE digest-pinned, so a
# floating first-party `image: ghcr.io/…:latest` is ignored).
_DIRECT_IMAGE = re.compile(
    r"image:\s+(?P<name>[^\s:@]+):(?P<tag>[^\s@]+)@(?P<digest>sha256:[0-9a-f]+)",
)


@dataclass(frozen=True)
class Pin:
    name: str          # image name without tag/digest, e.g. "postgres" or "docker.redpanda.com/redpandadata/redpanda"
    tag: str           # the legible tag, e.g. "18-alpine"
    digest: str        # the committed sha256:… digest
    source: Path       # the manifest the pin lives in

    @property
    def ref(self) -> str:
        return f"{self.name}:{self.tag}"


def discover() -> list[Pin]:
    pins: list[Pin] = []
    for path in KUSTOMIZATIONS:
        text = path.read_text()
        for m in _KUSTOMIZE_ENTRY.finditer(text):
            pins.append(Pin(m["name"], m["tag"], m["digest"], path))
    for path in DIRECT_MANIFESTS:
        text = path.read_text()
        for m in _DIRECT_IMAGE.finditer(text):
            pins.append(Pin(m["name"], m["tag"], m["digest"], path))
    return pins


def resolve(ref: str) -> str:
    """Resolve an image ref (name:tag) to its current manifest digest via crane."""
    out = subprocess.run(
        [CRANE, "digest", ref],
        capture_output=True, text=True, check=True,
    )
    return out.stdout.strip()


def rel(path: Path) -> str:
    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


def check_consistency(pins: list[Pin]) -> list[str]:
    """Same image name pinned in >1 place must carry the SAME committed digest (postgres/openbao
    recur across kustomizations). A split means a partial manual bump — a deploy hazard."""
    by_name: dict[str, set[str]] = {}
    for p in pins:
        by_name.setdefault(p.name, set()).add(p.digest)
    problems = []
    for name, digests in sorted(by_name.items()):
        if len(digests) > 1:
            where = ", ".join(f"{rel(p.source)} -> {p.digest}" for p in pins if p.name == name)
            problems.append(f"INCONSISTENT: {name} is pinned to {len(digests)} different digests: {where}")
    return problems


def main() -> int:
    ap = argparse.ArgumentParser(description="Audit/refresh third-party image digest pins.")
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--list", action="store_true", help="print the discovered pins and exit (no network)")
    g.add_argument("--check", action="store_true", help="re-resolve + report drift/inconsistency (default)")
    g.add_argument("--write", action="store_true", help="rewrite drifted digests in place")
    args = ap.parse_args()

    pins = discover()
    if not pins:
        print("::error::no third-party digest pins discovered — has the manifest layout changed?", file=sys.stderr)
        return 2

    if args.list:
        for p in sorted(pins, key=lambda p: (rel(p.source), p.name)):
            print(f"{rel(p.source):55s} {p.ref} @ {p.digest}")
        print(f"\n{len(pins)} pin(s) across {len({p.source for p in pins})} manifest(s).")
        return 0

    # --check (default) and --write both need live digests.
    problems = check_consistency(pins)
    drift: list[tuple[Pin, str]] = []
    errors: list[str] = []
    # Resolve each distinct ref once (postgres:18-alpine recurs); map ref -> live digest.
    live: dict[str, str] = {}
    for ref in sorted({p.ref for p in pins}):
        try:
            live[ref] = resolve(ref)
        except subprocess.CalledProcessError as e:
            errors.append(f"could not resolve {ref}: {e.stderr.strip().splitlines()[-1] if e.stderr.strip() else e}")

    for p in pins:
        current = live.get(p.ref)
        if current is None:
            continue  # resolution failed; already recorded in errors
        if current != p.digest:
            drift.append((p, current))

    for prob in problems:
        print(f"::warning::{prob}")
    for p, current in drift:
        print(f"DRIFT: {p.ref} in {rel(p.source)}\n  pinned {p.digest}\n  latest {current}")
    for err in errors:
        print(f"::warning::{err}")

    if args.write:
        if not drift:
            print("no drift — nothing to rewrite.")
        else:
            # A sha256 digest is globally unique, so a plain old->new substitution touches only the
            # intended lines and preserves every surrounding comment. Group by (source, old->new).
            edits: dict[Path, dict[str, str]] = {}
            for p, current in drift:
                edits.setdefault(p.source, {})[p.digest] = current
            for source, mapping in edits.items():
                text = source.read_text()
                for old, new in mapping.items():
                    text = text.replace(old, new)
                source.write_text(text)
                print(f"rewrote {len(mapping)} digest(s) in {rel(source)}")
        # In --write the drift is the point of the run, not a failure; only a hard error/inconsistency fails.
        return 1 if (errors or problems) else 0

    # --check: any drift, unresolved ref, or inconsistency is a non-zero (the scheduled workflow
    # turns a non-zero into a bump PR).
    if drift or errors or problems:
        print(f"\n{len(drift)} drifted, {len(errors)} unresolved, {len(problems)} inconsistent.")
        return 1
    print(f"all {len(pins)} third-party digest pins are current and consistent.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
