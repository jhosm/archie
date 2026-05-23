# Local Tooling

This is a **documentation-only** repository — there is no application to build or run. The only local tooling you need is a **PlantUML renderer**, used to (re)generate the committed SVGs for the C4 architecture view ([`feature-design-c4-architecture.md`](./docs/product-management/product_concepts/feature-design-c4-architecture.md)). If you are only reading or editing prose, you need nothing installed.

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

```bash
sudo apt-get install graphviz plantuml
```

**Other platforms:** install a JDK, Graphviz, and PlantUML from your package manager, or run the PlantUML JAR directly (`java -jar plantuml.jar`). See <https://plantuml.com/starting>.

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
