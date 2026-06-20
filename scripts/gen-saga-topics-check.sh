#!/usr/bin/env bash
# scripts/gen-saga-topics-check.sh — the CI gate for the catalogue-derived saga
# integration-topic manifest (bd babelstone-9w2k.4, ADR-IC-003).
#
# What it proves: the checked-in, generated FamilyIntegrationTopics.g.cs that the saga
# subscribes through is EXACTLY what the AsyncAPI catalogue channels would produce
# (topic == channel == aggregate_type, OutboxDrainer). A missed/extra catalogue channel
# = a saga that silently never advances (or joins the wrong topic), so a drift is a CI
# FAILURE here, not a runtime stall (the exhaustive-and-correct-at-startup constraint,
# ADR-IC-003). Hermetic: re-derives from the working-tree catalogue and diffs — no broker,
# no registry, no network, no PyYAML (stdlib-only parse, like scripts/docs-gen/generate.py).
#
# Dev runs this via `make gen-saga-topics-check` (which pins python3 through mise);
# regenerate after a catalogue change with `make gen-saga-topics`. CI invokes it directly,
# with python3 already on PATH via the mise-action toolchain.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
exec python3 "$REPO_ROOT/scripts/gen-saga-topics.py" --check
