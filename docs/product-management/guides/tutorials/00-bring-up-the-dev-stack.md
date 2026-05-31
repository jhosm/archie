# Tutorial 00 — Bring up the dev stack

**Persona:** [Operator](../../reading-paths/README.md) (and the first step for everyone else).
**You will finish with:** the full local stack running and smoke-tested on your laptop, ready for [Tutorial 01](./01-constitute-a-term-deposit-end-to-end.md).

This is a hand-held, run-it-yourself first contact. Every command is shown and every expected result is visible. For *why* the stack looks the way it does, this tutorial links out — it does not re-explain the design ([guides invariant](../README.md)).

## Before you start

You need two things on the host: **Docker** (Engine + Compose v2) and the pinned **toolchain** (mise + the languages/CLIs). The prerequisites, what gets installed, and the non-macOS path are documented once in [`INSTALL.md`](../../../../INSTALL.md) — read that if any command below reports a missing tool.

Check Docker is up first:

```bash
docker compose version    # should print Compose v2.x
docker info               # must succeed — start Docker Desktop if it errors
```

## Step 1 — Install the toolchain (once per machine)

From the repo root:

```bash
make bootstrap
```

This runs `brew bundle` (host prerequisites) then `mise install` (the pinned languages + CLIs). Expect it to take a few minutes on first run while it downloads .NET, Go, Python, CUE, cosign, and oras.

Confirm the pins are active:

```bash
make doctor
```

Expected: `make doctor` prints the resolved version for each tool (no `MISSING` lines). If `dotnet`, `go`, or `python` resolve to the wrong version, activate mise in your shell as [`INSTALL.md`](../../../../INSTALL.md) describes, or prefix ad-hoc commands with `mise exec --`.

## Step 2 — Start the stack

```bash
make up
```

`make up` pulls images, starts the containers, and **blocks until every health check passes**. When it returns it prints the endpoint table — PostgreSQL, the Kafka API, Schema Registry, Redpanda Console, the Kong edge gateway, OpenBao, Grafana, the OTLP collector, the OCI registry, and EventCatalog. (What each service is *for* is covered in the [plumbing patterns](../../integration_concepts/04-plumbing-patterns.md) and [observability](../../integration_concepts/06-observability-and-tracing.md) concept docs; this tutorial only gets them running.)

Expected tail of the output:

```
Stack is healthy. Endpoints:
  PostgreSQL        localhost:5432   (db=babelstone user=babelstone pass=babelstone)
  ...
  Grafana           http://localhost:3000   ...
```

## Step 3 — Smoke-test it

```bash
make verify
```

`make verify` probes each service in turn and prints a `✓` line per check, ending with:

```
✓ Stack verified.
```

If a probe fails, follow the live logs with `make logs` and re-run `make verify` once the service settles. The Postgres credentials, ports, and the full target list are in [`infra/README.md`](../../../../infra/README.md).

## Step 4 — Stop it (when you're done)

```bash
make down     # stop the stack, keep your data volumes
```

To wipe data and start clean instead, use `make reset` (destroys volumes, then `make up`).

## What you just did, and where to go next

You installed the pinned toolchain and brought up a health-checked local stack — the substrate every other tutorial assumes. The stack itself is the dev realisation of the architecture in [feature-design-c4-architecture](../../product_concepts/feature-design-c4-architecture.md).

- **Next:** [Tutorial 01 — Constitute a term deposit end-to-end](./01-constitute-a-term-deposit-end-to-end.md) drives a real deposit through the engine and the [MCP](../../reference/glossary.md#mcp-model-context-protocol) tool surface.
- **Then:** [Tutorial 02 — Author and load a PT pack](./02-author-and-load-a-pt-pack.md) takes you into the [regulatory pack](../../reference/glossary.md#pack-regulatory-pack) format.
- **All tutorials:** the [tutorials index](./README.md) · the [guides root](../README.md).
