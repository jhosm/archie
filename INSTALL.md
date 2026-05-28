# Local Tooling

This repository is **mostly documentation**, with the engine build now underway. The fast path: run **`make bootstrap`** once (installs the full toolchain), then **`make up`** to start the local stack. What you actually need depends on what you are doing:

- **Reading or editing prose:** nothing.
- **Building the engine / running tests:** the pinned language toolchain — `make bootstrap` (see [Toolchain](#toolchain)).
- **Running the local dev stack:** **Docker** (see [Local dev stack](#local-dev-stack)).
- **Editing C4 diagrams:** **PlantUML + Graphviz** — installed by `make bootstrap` on macOS (see [PlantUML renderer](#plantuml-renderer)).

> Versions are pinned in two files, both installed by `make bootstrap`: `mise.toml` (languages + CLIs — .NET, Go, Python, CUE, cosign, oras) and `Brewfile` (host prerequisites — mise, bd, dolt, plantuml, graphviz).

---

## Toolchain

The system is polyglot only at the boundary — a **.NET 10** engine, a **Go** CUE validator, a **Python** MCP server — plus signing/registry CLIs. All language versions are pinned in `mise.toml` for reproducibility across machines and CI; host prerequisites are pinned in the `Brewfile`.

### One-time setup

```bash
make bootstrap   # brew bundle (host prereqs) + mise install (pinned toolchain)
make doctor      # print every resolved version, to confirm the pins are active
```

`make bootstrap` is the single command a newcomer runs after cloning. On macOS it runs `brew bundle` for the host prerequisites, then `mise trust` + `mise install` for the pinned language toolchain.

### What gets installed

| Tool | Version | Managed by | Why |
|---|---|---|---|
| **.NET SDK** | 10.0 (LTS) | mise | Engine + orchestrator + ACL + notification ([ADR-PC-010](./docs/product-management/product_concepts/adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)) |
| **Go** | 1.26 | mise | `pack-validate` / CUE validator binary ([ADR-PC-006](./docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)) |
| **Python** | 3.14 | mise | MCP server ([ADR-IC-010](./docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)) |
| **CUE** | 0.16 | mise | Family-schema constraint language ([ADR-PC-006](./docs/product-management/product_concepts/adrs/ADR-PC-006-cue-schema-language.md)) |
| **cosign** | 3.0 | mise | Pack + image signing ([ADR-PC-007](./docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)) |
| **oras** | 1.3 | mise | OCI pack push/pull by digest ([ADR-PC-007](./docs/product-management/product_concepts/adrs/ADR-PC-007-signed-yaml-oci-pack.md)) |
| **mise** | latest | brew | The version manager (reads `mise.toml`) |
| **bd** + **dolt** | latest | brew | Mandated issue tracker (`beads`) + its Dolt data store |
| **PlantUML** + **Graphviz** | latest | brew | C4 diagram rendering |

### Activating mise in your shell

mise installs the pinned tools into its own store; activate it so `dotnet` / `go` / `python` / `cue` resolve to the pinned versions:

```bash
echo 'eval "$(mise activate zsh)"' >> ~/.zshrc   # bash: use 'mise activate bash'
```

Without activation, invoke a pinned tool ad-hoc with `mise exec -- <cmd>` (e.g. `mise exec -- dotnet --version`).

### Linux / non-Homebrew

mise is cross-platform and installs the language toolchain identically. Install the host prerequisites (mise, dolt, plantuml, graphviz, and the beads `bd` CLI) via your package manager, then run `make bootstrap` — it detects the missing `brew`, skips the bundle step, and runs `mise install`.

---

## Local dev stack

The backing infrastructure to run the engine on a laptop — **PostgreSQL + Redpanda (with its built-in Schema Registry) + Redpanda Console**. This is the only prerequisite, and it is one tool:

| Tool | Why | Notes |
|---|---|---|
| **Docker** (Engine + Compose v2) | Runs the stack | Docker Desktop (macOS/Windows) or Docker Engine + the Compose plugin (Linux). `docker compose version` should print v2.x |

Then, from the repo root:

```bash
make up        # start the stack and wait until healthy (prints endpoints)
make verify    # smoke-test PostgreSQL, Redpanda, and the Schema Registry
make down      # stop it (keeps data); `make reset` wipes data and restarts
```

`make up` is the single command a newcomer runs after cloning — it pulls images, starts the three containers, and blocks until their health checks pass. Connection details, ports, and the full target list live in [`infra/README.md`](./infra/README.md).

---

## PlantUML renderer

> On macOS, `make bootstrap` already installs PlantUML + Graphviz via the `Brewfile`. This section is the reference detail and the manual / non-macOS path.

You need the renderer if you:

- edit a `.puml` diagram source under `docs/**/diagrams/`, or
- want the pre-commit hook (below) to keep diagrams in sync automatically.

GitHub does **not** render PlantUML (it renders only Mermaid), so each diagram is pre-rendered to SVG and the SVG is committed and embedded in the Markdown. The `.puml` is the source of truth; the `.svg` is generated output.

---

## Requirements

| Tool | Why | Notes |
|---|---|---|
| **PlantUML** | Renders `.puml` → `.svg` | Runs on the JVM |
| **Java (JDK/JRE)** | PlantUML is a Java program | Java 17+ is fine |
| **Graphviz** (`dot`) | C4-PlantUML uses Graphviz for layout | Without it, C4 diagrams fail to lay out |

### Install

**macOS (Homebrew):**

```bash
brew install graphviz plantuml
```

(The `plantuml` formula pulls in a JDK as a dependency, so Java is covered.)

**Debian / Ubuntu:**

> ⚠️ The apt `plantuml` package on Ubuntu 24.04 ships a C4 bundled-stdlib too old
> to resolve `!include <C4/C4_Component>`, so every C4 diagram fails to render. Take
> Graphviz + a JRE from apt, but install PlantUML as a pinned jar — the same version
> [CI pins](.github/workflows/ci.yml) (the render-check is version-tolerant; any
> release new enough to carry the C4 stdlib works):

```bash
sudo apt-get install graphviz default-jre
PLANTUML_VERSION=1.2026.4
sudo curl -fsSL -o /usr/local/lib/plantuml.jar \
  "https://github.com/plantuml/plantuml/releases/download/v${PLANTUML_VERSION}/plantuml-${PLANTUML_VERSION}.jar"
printf '#!/usr/bin/env bash\nexec java -jar /usr/local/lib/plantuml.jar "$@"\n' \
  | sudo tee /usr/local/bin/plantuml >/dev/null && sudo chmod +x /usr/local/bin/plantuml
```

**Other platforms:** install a JDK, Graphviz, and PlantUML from your package manager, or run the PlantUML JAR directly (`java -jar plantuml.jar`). See <https://plantuml.com/starting>. Note the C4 caveat above: pick a PlantUML new enough to bundle the C4 stdlib.

### Verify

```bash
plantuml -version     # prints the PlantUML version
dot -V                # prints the Graphviz version
java -version         # any JDK 17+
```

---

## Rendering diagrams

Render one diagram, or all of them:

```bash
# one
plantuml -tsvg docs/product-management/product_concepts/diagrams/c4-l1-system-context.puml

# all
plantuml -tsvg docs/product-management/product_concepts/diagrams/*.puml
```

The SVG is written next to its source. **Convention:** each diagram's `@startuml <id>` must match the `.puml` filename (without extension), so `c4-l1-system-context.puml` renders to `c4-l1-system-context.svg`. The pre-commit hook relies on this.

---

## Pre-commit hook (keeps SVGs in sync)

A version-controlled hook at [`.githooks/pre-commit`](./.githooks/pre-commit) re-renders any `.puml` staged in a commit and stages the resulting `.svg`, so a committed diagram is never out of date with its source. If the hook fires without PlantUML installed, it fails with install instructions rather than committing a stale SVG.

### Activate it

Either point Git at the version-controlled hooks directory (recommended — one source of truth, nothing to keep in sync):

```bash
git config core.hooksPath .githooks
```

…or copy the hook into your local `.git/hooks/`:

```bash
cp .githooks/pre-commit .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
```

> A delegating shim may already be present at `.git/hooks/pre-commit` (it just `exec`s `.githooks/pre-commit`). If so, the hook is active and you don't need to do anything. `core.hooksPath` takes precedence over `.git/hooks/` when both exist.

### Bypass (discouraged)

`git commit --no-verify` skips the hook, but then a committed SVG can drift from its `.puml`. Install the renderer instead.
