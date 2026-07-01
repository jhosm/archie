# Infrastructure & Security — start here

**In plain English:** Babelstone's infrastructure and its security disciplines are
real and fairly involved, but until now you had to piece them together from a dozen
decision records, a threat-model document, and a few READMEs. These pages are the
**synthesis layer** — a readable way in for two kinds of reader:

- **You, orienting yourself.** Read top-to-bottom and you'll have a working mental
  model of what runs, how it connects, and what protects it.
- **A security or infra expert you want to critique it.** They get one coherent,
  honest picture — including what's *not* finished — instead of having to
  reverse-engineer it.

These pages **don't restate** the decisions — they link to them. Think of this as a
guided tour with the authoritative detail one click away.

> **Readability over ceremony, and a moving target.** These docs favour being
> understandable over matching the formal ADR house style. The infra is still being
> built and will likely be refactored, so the **status labels are a snapshot** —
> the boundaries, services, and mechanisms are the durable part.

---

## The two guides

| Page | What it answers | Read it when |
|---|---|---|
| **[Topology](./topology.md)** | What runs, how the pieces connect, and which are real vs placeholder | You want the map of the estate |
| **[Security posture](./security-posture.md)** | The nine trust boundaries, the control on each, and what's enforced today | You want to understand or critique the security side |

Both are built around a diagram (under [`diagrams/`](./diagrams/), C4 PlantUML
rendered to SVG so GitHub displays it). The CI render-check keeps the SVGs honest.

---

## The estate in three sentences

One **engine** (event-sourced, the source of truth) is fronted by a single
**Kong** gateway and surrounded by backing infrastructure — **Postgres**,
**Redpanda**, **OpenBao**, an **OTel/Grafana** observability stack, an OCI
**registry**, and a **Backstage** catalogue. A few boundary services — the
**orchestrator** (sagas), the **MCP server** (agent channel), and the
**ACL** (to Core Banking, currently a stub) — connect it to the outside world.
Security is organised as **nine trust boundaries** across three planes: mutual
TLS between services, SASL + ACLs on the Redpanda event bus, and a no-PII-leaks
observability plane.

---

## Where the authoritative detail lives

These guides are the on-ramp; here's the source material they point into, so you
know what to open when you need the real depth.

**Operational / how-to:**
- [`infra/README.md`](../README.md) — the dev Compose stack, endpoint by endpoint.
- [`infra/k8s/README.md`](../k8s/README.md) — the deployed stack: `base` plus the `ha` / `staging` overlays.
- [`infra/runbooks/`](../runbooks/) — operational procedures (DR, snapshots, reconciliation).
- The root [`Makefile`](../../Makefile) and `scripts/demo-*.sh` — bring-up and the runnable demos.

**Config that *is* the security policy:**
- [`infra/kong/kong.yml`](../kong/kong.yml) — the entire edge policy in one file.
- [`infra/redpanda/topic-acls.yaml`](../redpanda/topic-acls.yaml) — who may read/write which topic.

**The architecture narrative and decisions:**
- [Document 10 — Security and Threat Model](../../docs/product-management/integration_concepts/10-security-and-threat-model.md) — the nine boundaries and six principles in full.
- The [integration_concepts series](../../docs/product-management/integration_concepts/) (00–11) — the integration architecture, in sequence.
- The integration ADRs ([`adrs/`](../../docs/product-management/integration_concepts/adrs/)) — the tool and pattern decisions (event backbone, gateway, observability, service identity, MCP, IAM).
- The product ADRs ([`adrs/`](../../docs/product-management/product_concepts/adrs/)) — event store, crypto-shredding, packs, DR.

---

## Maintaining these pages

- The diagrams are **C4 PlantUML** (`diagrams/*.puml`) rendered to committed SVG —
  GitHub can't render PlantUML, so the SVG is what readers see. After editing a
  `.puml`, re-render it: `plantuml -tsvg infra/docs/diagrams/<file>.puml`. The
  pre-commit hook does this automatically, and CI's diagram render-check
  (`.github/scripts/diagram-render-check.sh`) verifies every `.puml` still renders.
- When a status flips (a boundary becomes enforced, a skeleton becomes a real
  service), update the relevant table here rather than letting it drift — these
  pages are only as useful as their honesty about what runs.
