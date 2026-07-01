# babelstone

**A reference for building a modern core banking product engine — the architecture written down, and the working code that proves it.**

babelstone is two things in one repository. It is a **documentation library** that explains, from first principles, how to build a configurable banking product engine and integrate it into a real bank's estate. And it is a **working implementation** of that engine — an event-sourced .NET kernel, its boundary services, the contracts that hold them together, and a dev stack you can run end-to-end. The docs are the reasoning; the code is the proof the reasoning holds.

Everything is grounded in one concrete example: a **Portuguese term deposit** (*depósito a prazo*). The patterns are general — they apply to loans, mortgages, current accounts, and cards just as well — but the running example is specific enough to make every decision real and exercise the whole system end to end.

> **New here?** Skim [Documentation](#documentation) for the *why*, then jump to [Quickstart](#quickstart) to run the engine. If you're contributing, start with [CLAUDE.md](./CLAUDE.md) (also mirrored as [AGENTS.md](./AGENTS.md) for non-Claude agents).

---

## What's in here

This is a **hybrid docs + code monorepo**. The two halves are deliberately co-located so the architecture and its implementation can never drift apart silently.

- **The docs** (`docs/`) answer three questions about a banking ecosystem: what math is correct, what configurable product implements that math, and how that product integrates with the bank. They are backed by a set of **Architectural Decision Records (ADRs)** that pick concrete tools and lock in contracts.
- **The code** (repo root) is the product engine and the services around its boundary: an event-sourced kernel, domain family handlers, an orchestrator, an anti-corruption layer, a notification service, an MCP server for LLM-agent access, the schema contracts, a pack validator, and a Docker Compose dev stack.

---

## Quickstart

The toolchain (.NET 10, Go, Python) is pinned in [`mise.toml`](./mise.toml). Full setup is in [INSTALL.md](./INSTALL.md).

```bash
make bootstrap   # one-time: brew prerequisites + mise install
make doctor      # verify the pinned toolchain versions are active
```

Run the local dev stack (Redpanda, Postgres, Schema Registry, Kong, OpenBao, Grafana):

```bash
make up          # start the stack, wait until healthy
make verify      # smoke-test: Postgres + Redpanda + Schema Registry reachable
make down        # stop, keep data volumes
```

See the whole system run. The **Mission Control** demos bring up the backend and a UI you can drive:

```bash
make demo        # the whole backend in one bring-up — flip between DEMO / LIVE·engine /
                 # LIVE·saga modes and an Operator YOU/CLAUDE toggle (real agent if
                 # ANTHROPIC_API_KEY is set), then open http://localhost:9000
make demo-down   # stop the demo hosts (run `make down` to stop the infra too)
```

Single-slice variants: `make demo-mcp` (engine→MCP walking skeleton), `make demo-saga` (the full edge→saga→settlement→engine path), `make demo-agent` (a real Claude model operating the bank through the MCP edge). Run `make help` for the complete target list.

> **Always prefix builds and tests with `mise exec --`** so `dotnet`/`go`/`python` resolve to the pinned versions (e.g. `mise exec -- dotnet test engine/tests/Babelstone.Engine.Tests/`). See [CLAUDE.md → Dev Stack & Toolchain](./CLAUDE.md#dev-stack--toolchain) for why.

---

## Repository layout

### Code components

| Path | What it is |
|---|---|
| [`engine/`](./engine/) | C# (.NET 10) event-sourced product kernel — the event store, outbox worker, handler dispatch, and PostgreSQL migrations |
| [`families/`](./families/) | Domain family handlers — event types, pure fold handlers, projections, and lifecycle state machines (`term-deposit` is the v1 family) |
| [`orchestrator/`](./orchestrator/) · [`acl/`](./acl/) · [`notification/`](./notification/) | .NET boundary services — the saga orchestrator, anti-corruption layer, and notifications |
| [`mcp-server/`](./mcp-server/) | Python MCP server exposing the bank as tools to LLM agents (ADR-IC-010) |
| [`contracts/`](./contracts/) | The governed contract surface — Avro payloads, CUE family schemas, and the AsyncAPI event catalogue |
| [`pack-validate/`](./pack-validate/) | Go binary that validates regulatory packs (ADR-PC-006) |
| [`packs/`](./packs/) · [`product-configs/`](./product-configs/) · [`rate-sheets/`](./rate-sheets/) | Regulatory packs, product variant configs, and rate sheets — the data the engine is configured with |
| [`infra/`](./infra/) | Docker Compose dev stack, Kong/OpenBao/Grafana config, and runbooks — start with the [**Infrastructure & Security guide**](./infra/docs/README.md) for a readable tour of the topology and the trust boundaries |
| [`scripts/`](./scripts/) | The shell entry points behind the `make` targets (demos, CI gates, deploys) |

### Documentation

| Path | What it is |
|---|---|
| [`docs/product-management/`](./docs/product-management/) | The three concern-axis documentation series (below) and their ADRs |
| [`docs/product-docs/reference/`](./docs/product-docs/reference/README.md) | **Generated reference** — event payloads, family schemas, the MCP tool surface, the ADR index, and the glossary, rendered from the contracts and diff-gated in CI so it cannot drift |
| [`CLAUDE.md`](./CLAUDE.md) / [`AGENTS.md`](./AGENTS.md) | Contributor and AI-agent instructions: workflow, conventions, ADR governance |

---

## Documentation

The three series are self-contained — each addresses a distinct concern, and they share the Portuguese term-deposit example but can be read independently.

| Series | The question it answers | Start here |
|---|---|---|
| [**financial_concepts/**](./docs/product-management/financial_concepts/banking_products_financial_mathematics.md) | What math is correct | The cash-flow framework, present value, IRR/TAEG, and the amortization systems across term deposits, loans, current accounts, and cards |
| [**product_concepts/**](./docs/product-management/product_concepts/README.md) | What configurable product implements that math | The product brief: one engine that collapses every retail family into a swappable configuration surface + swappable regulatory pack |
| [**integration_concepts/**](./docs/product-management/integration_concepts/00-introduction-and-decisions.md) | How that product integrates with the bank | The integration backbone — read in sequence (docs 00–11), from the three driving constraints down to security and the chat-agent channel |
| [**implementation_guidelines/**](./docs/product-management/implementation_guidelines/code-comments.md) | How we write the code, not just what it does | Cross-cutting authoring conventions — starting with the code-comment guideline (comment the *why*, cite only durable/verifiable anchors) |

> **Looking something up?** The [**generated reference**](./docs/product-docs/reference/README.md) collects the machine-derived lookup material — event payloads, family schemas, the MCP tool surface, a cross-namespace ADR index, and the glossary. It is rendered from the contracts and regenerated-and-diffed in CI so it cannot drift (`make docs-gen` / `make docs-verify`). The documentation architecture is [ADR-PC-022](./docs/product-management/product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md).

### The three constraints behind the architecture

Before any patterns were chosen, three constraints were fixed — every integration decision is traceable to one or more of them ([Document 00](./docs/product-management/integration_concepts/00-introduction-and-decisions.md)):

- **Sub-500ms edge response.** Coordinating Core + Compliance + CRM + Workflow synchronously within that budget is impossible, so the system validates what fits, persists the request, returns `202 Accepted`, and runs the saga asynchronously.
- **Hybrid saga.** Multi-step flows with complex compensation use a stateful orchestrator; uncoordinated fan-out uses choreography.
- **Compensation, not transactionality.** Classical 2PC/XA is unavailable in most Core Banking systems; compensation is the right trade-off — and the integration docs are largely about implementing it robustly.

---

## Contributing

- **All changes reach `main` only via pull request** — never commit or push directly to `main`. Branch off the latest `main`, commit there, and open a PR. (See [CLAUDE.md → Branching & PR Policy](./CLAUDE.md#branching--pr-policy).)
- **ADR governance.** No change may silently contradict an Accepted ADR — every PR body names the ADRs it touches or honours, and CI enforces it. Divergence is allowed; *silent* divergence is not.
- **Issue tracking** uses [beads](https://github.com/gastownhall/beads) (`bd`) — run `bd ready` to find work and `bd prime` for the full workflow.

The full conventions — toolchain, diagrams, document layout, communication style — live in [CLAUDE.md](./CLAUDE.md).

---

## License

babelstone is licensed under the **Business Source License 1.1**. See [LICENSE.md](./LICENSE.md) for terms.
