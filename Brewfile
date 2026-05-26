# Host prerequisites for the babelstone monorepo (macOS / Homebrew).
# `brew bundle` (run by `make bootstrap`) installs these; the pinned language
# toolchain (.NET, Go, Python, CUE, cosign, oras) is managed by mise — see mise.toml.
#
# Linux: install the equivalents via your package manager (mise covers the
# language toolchain identically), then `make bootstrap`. See INSTALL.md.

# Version-managed toolchain installer (reads mise.toml).
brew "mise"

# Issue tracker — the mandated backlog tool and its Dolt data store, so
# `bd` / `bd dolt push` work in any session (CLAUDE.md session protocol).
# `beads` (homebrew-core) provides the `bd` binary; do NOT use the redundant
# steveyegge/beads/bd tap formula — it collides on the same `bd` symlink.
brew "beads"
brew "dolt"

# C4 diagram rendering: PlantUML (+ its JDK) and Graphviz (`dot`) for layout.
# Each .puml under docs/**/diagrams/ is pre-rendered to a committed .svg.
brew "plantuml"
brew "graphviz"
