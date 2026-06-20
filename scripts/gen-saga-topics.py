#!/usr/bin/env python3
"""Generate the saga's family-integration-topic manifest from the AsyncAPI catalogue.

Plain-English: the orchestrator's constitution/renewal sagas have to subscribe to the
Kafka topic the engine publishes term-deposit facts to. That topic name is the
`aggregate_type` ("term_deposit"), which is exactly the channel name in the governed
AsyncAPI catalogue (`contracts/catalog/events/*.asyncapi.yaml`). Hand-writing the topic in
the saga code risks it drifting from the catalogue — and a missed topic is a saga that
silently never advances. So this script DERIVES the per-family integration-topic set from
the catalogue channels and writes it as a checked-in, generated C# constant the saga reads.
A CI gate runs this in --check mode and fails (a build failure, never a runtime stall) if
the checked-in manifest drifts from what the catalogue would produce.

Why a generated C# list and not runtime YAML parsing or a Kafka SubscribePattern regex:
  * ADR-IC-003 §A8 — the orchestrator/family depends ONLY on Confluent.Kafka, with no
    coupling to the docs pipeline or the engine's Avro codec. A generated constant keeps
    that boundary: the runtime reads a plain string, never the catalogue YAML.
  * ADR-IC-001 (one consumer group per consumer) — an explicit generated list cannot pull
    in `deposits.process.events` or an unrelated topic the way a too-broad SubscribePattern
    regex could, which would break the one-group-per-consumer guarantee.

The topic == channel == aggregate_type convention is the relay's documented behaviour
(OutboxDrainer.PublishAsync: `topic = row.AggregateType`).

Parsing is STDLIB-ONLY (no PyYAML), mirroring scripts/docs-gen/generate.py: the catalogue
files have a fixed, governed shape (`info.x-owner`, top-level `channels:` keys) the
AsyncAPI gate (scripts/asyncapi-catalog-validate.sh) already enforces, so a narrow structural
parse is sufficient and keeps the gate dependency-free in CI.

Usage:
  gen-saga-topics.py            # regenerate the manifest in place
  gen-saga-topics.py --check    # exit non-zero (no write) if the manifest is stale — the CI gate
"""

from __future__ import annotations

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CATALOG_EVENTS = REPO_ROOT / "contracts" / "catalog" / "events"

# The one family this manifest governs today. Keyed by the catalogue `x-owner` value, mapped
# to the generated file's home in the family's OWN orchestration tree (co-located with the
# orchestrator/family deploy inputs, NOT the docs-gen reference tree — ADR-IC-003 §A8). A second
# family adds a row here + its own generated manifest; the substrate and the gate are unchanged.
FAMILIES = {
    "term-deposit": {
        "namespace": "Babelstone.Families.TermDeposit.Orchestration",
        "out": REPO_ROOT
        / "families"
        / "term-deposit"
        / "src"
        / "Babelstone.Families.TermDeposit.Orchestration"
        / "FamilyIntegrationTopics.g.cs",
    },
}


def _strip_comment(line: str) -> str:
    """Drop a trailing ' #...' comment outside quotes. The catalogue uses simple scalar values, so a
    naive split on ' #' (space-hash) is safe and avoids a false positive on a '#' inside a value."""
    idx = line.find(" #")
    return line[:idx] if idx != -1 else line


def _top_level_value(lines: list[str], parent: str, key: str) -> str | None:
    """The scalar value of `key` nested one level (2-space indent) under the top-level `parent:` block."""
    in_parent = False
    for raw in lines:
        line = raw.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        # A top-level key (no leading whitespace) ends the parent block.
        if not line[:1].isspace():
            in_parent = line.split(":", 1)[0].strip() == parent
            continue
        if in_parent and line.startswith("  ") and not line.startswith("   "):
            name, _, value = _strip_comment(line).strip().partition(":")
            if name.strip() == key:
                return value.strip().strip("'\"")
    return None


def _block_child_keys(lines: list[str], parent: str) -> list[str]:
    """The direct child keys (2-space indent) of the top-level `parent:` block, in document order."""
    keys: list[str] = []
    in_parent = False
    for raw in lines:
        line = raw.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if not line[:1].isspace():
            in_parent = line.split(":", 1)[0].strip() == parent
            continue
        if in_parent and line.startswith("  ") and not line.startswith("   "):
            name = _strip_comment(line).strip().rstrip(":").strip()
            if name:
                keys.append(name)
    return keys


def derive_topics(owner: str) -> list[str]:
    """The DISTINCT, sorted catalogue channel names (== topics == aggregate_type) for the events
    this family owns. A channel is a topic; an event owned by `owner` contributes its channel."""
    topics: set[str] = set()
    if not CATALOG_EVENTS.is_dir():
        sys.stderr.write(f"FATAL: catalogue events dir not found: {CATALOG_EVENTS}\n")
        sys.exit(2)

    for path in sorted(CATALOG_EVENTS.glob("*.asyncapi.yaml")):
        lines = path.read_text().splitlines(keepends=True)
        if _top_level_value(lines, "info", "x-owner") != owner:
            continue
        for channel_name in _block_child_keys(lines, "channels"):
            topics.add(channel_name)

    if not topics:
        sys.stderr.write(
            f"FATAL: no catalogue channels found for owner '{owner}'. A family saga with an "
            "empty integration-topic set would silently never advance — refusing to generate.\n"
        )
        sys.exit(2)

    return sorted(topics)


def render(namespace: str, owner: str, topics: list[str]) -> str:
    entries = "\n".join(f'        "{t}",' for t in topics)
    return f"""// <auto-generated>
//     Generated by scripts/gen-saga-topics.py from the AsyncAPI catalogue
//     (contracts/catalog/events/*.asyncapi.yaml). DO NOT EDIT BY HAND.
//
//     The family-integration Kafka topics the saga subscribes to, DERIVED from the catalogue
//     channels (topic == channel == aggregate_type — the relay's documented convention,
//     OutboxDrainer.PublishAsync). Regenerate with `make gen-saga-topics`; the CI gate
//     `make gen-saga-topics-check` fails the build (never a runtime stall, ADR-IC-003) if this
//     drifts from the catalogue. Keeping the runtime on a generated CONSTANT — not a YAML read —
//     preserves the orchestrator/family's depends-only-on-Confluent.Kafka boundary (ADR-IC-003
//     §A8); an explicit list (not a Kafka SubscribePattern regex) keeps one-group-per-consumer
//     intact (ADR-IC-001).
// </auto-generated>

namespace {namespace};

/// <summary>
/// The catalogue-derived family-integration topics for the <c>{owner}</c> family (generated —
/// see the header). Exhaustive and correct AT STARTUP by construction: it is exactly the set of
/// AsyncAPI catalogue channels the family owns, CI-gated against the catalogue.
/// </summary>
public static class FamilyIntegrationTopics
{{
    /// <summary>The family-integration Kafka topics, derived from the catalogue channels.</summary>
    public static readonly IReadOnlyList<string> All =
    [
{entries}
    ];
}}
"""


def main() -> int:
    check = "--check" in sys.argv[1:]
    stale: list[str] = []

    for owner, cfg in FAMILIES.items():
        topics = derive_topics(owner)
        rendered = render(cfg["namespace"], owner, topics)
        out: Path = cfg["out"]

        if check:
            current = out.read_text() if out.exists() else ""
            if current != rendered:
                stale.append(str(out.relative_to(REPO_ROOT)))
            else:
                print(f"  up to date  {out.relative_to(REPO_ROOT)}  [{', '.join(topics)}]")
        else:
            out.write_text(rendered)
            print(f"  wrote       {out.relative_to(REPO_ROOT)}  [{', '.join(topics)}]")

    if check and stale:
        sys.stderr.write(
            "\nFATAL: saga integration-topic manifest is STALE vs the AsyncAPI catalogue:\n"
            + "".join(f"  - {s}\n" for s in stale)
            + "Run `make gen-saga-topics` and commit the result. A missed catalogue channel = a "
            "saga that silently never advances (ADR-IC-003), so this is a CI failure by design.\n"
        )
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
