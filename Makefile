# Babelstone — task entrypoint. Run `make` (or `make help`) to list targets.
#
# Two sections: the toolchain bootstrap (Brewfile + mise.toml, P.2) and the
# local dev stack (infra/compose.yaml, P.1). Keep targets grouped by section.

COMPOSE := docker compose -f infra/compose.yaml

# Host endpoints the stack exposes (kept in sync with infra/compose.yaml).
PG_PORT         ?= 5432
SR_PORT         ?= 18081
KAFKA_PORT      ?= 19092
CONSOLE_PORT    ?= 8080
KONG_PROXY_PORT ?= 8000
KONG_ADMIN_PORT ?= 8001
OPENBAO_PORT    ?= 8200
GRAFANA_PORT    ?= 3000
OTLP_GRPC_PORT  ?= 4317
OTLP_HTTP_PORT  ?= 4318
COLLECTOR_HEALTH_PORT ?= 13133
REGISTRY_PORT     ?= 5001
EVENTCATALOG_PORT ?= 8082

.DEFAULT_GOAL := help
.PHONY: help bootstrap doctor contracts-check avro-compat-check asyncapi-catalog-validate asyncapi-catalog-reconcile validate-variant pack-validate-test pack-validate pack-build pack-verify docs-gen docs-verify docs-site docs-site-serve up down reset logs ps verify demo-mcp demo-mcp-down

PACK ?= pt.2026.1
VARIANT ?=

help: ## List available targets
	@echo "Babelstone — make targets:"
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-10s\033[0m %s\n", $$1, $$2}'

## ----------------------------------------------------------------------------
## Toolchain (Brewfile host prereqs + mise.toml pinned languages/CLIs)
## ----------------------------------------------------------------------------

bootstrap: ## Install the full toolchain: brew prereqs + pinned mise tools
	@if command -v brew >/dev/null 2>&1; then \
		echo "→ brew bundle (host prerequisites, install-only) ..."; \
		brew bundle --no-upgrade --file=Brewfile; \
	else \
		echo "⚠ Homebrew not found — install mise, bd, dolt, plantuml, graphviz via your"; \
		echo "  package manager (see INSTALL.md), then re-run. Continuing to mise ..."; \
	fi
	@echo "→ mise install (pinned toolchain from mise.toml) ..."
	@mise trust --quiet
	@mise install
	@echo ""
	@echo "✓ Toolchain installed. Run 'make doctor' to verify versions."

doctor: ## Print resolved toolchain versions (verifies the pins are active)
	@echo "→ mise-managed (mise.toml):"
	@mise current
	@echo "→ host prerequisites:"
	@printf "  %-10s " "bd";       bd version 2>/dev/null      | head -1 || echo "MISSING"
	@printf "  %-10s " "dolt";     dolt version 2>/dev/null    | head -1 || echo "MISSING"
	@printf "  %-10s " "plantuml"; plantuml -version 2>/dev/null | head -1 || echo "MISSING"
	@printf "  %-10s " "dot";      dot -V 2>&1                 | head -1 || echo "MISSING"
	@printf "  %-10s " "docker";   docker --version 2>/dev/null || echo "MISSING"

## ----------------------------------------------------------------------------
## Contracts (the governed CUE + Avro + EventCatalog surface)
## ----------------------------------------------------------------------------

contracts-check: ## Validate the CUE family schemas (fmt + accept/reject fixtures, ADR-PC-006)
	@./contracts/cue/check.sh

avro-compat-check: ## Avro §P1/§P2 lint + §P3 SR compatibility vs origin/main (ADR-IC-002, needs Docker)
	@./scripts/avro-compat-check.sh

asyncapi-catalog-validate: ## AsyncAPI catalogue §P1–§P6 gate (fast, hermetic; needs Node + jq, ADR-IC-015)
	@./scripts/asyncapi-catalog-validate.sh

asyncapi-catalog-reconcile: ## Live check: catalogue subjects exist in a throwaway SR (ADR-IC-015 §8, needs Docker)
	@./scripts/asyncapi-catalog-reconcile.sh

validate-variant: ## Run pack-validate depths 1–4 on a variant (VARIANT=path PACK=pt.2026.1, ADR-PC-006)
	@test -n "$(VARIANT)" || { echo "usage: make validate-variant VARIANT=<path/to/variant.yaml> [PACK=pt.2026.1]"; exit 2; }
	@go -C pack-validate run . validate "$(abspath $(VARIANT))" --pack "$(abspath packs/$(PACK))"

pack-validate-test: ## Build + test the Go pack-validate binary (ADR-PC-006)
	@go -C pack-validate build ./... && go -C pack-validate test ./...

pack-validate: ## cue-vet a pack's manifest + data (PACK=pt.2026.1, ADR-PC-007)
	@./packs/pack.sh validate packs/$(PACK)

pack-build: ## Build a pack into an OCI layout, print its digest (PACK=pt.2026.1)
	@./packs/pack.sh build packs/$(PACK)

pack-verify: ## Build then pull-by-digest + re-validate a pack (PACK=pt.2026.1)
	@DIGEST="$$(./packs/pack.sh build packs/$(PACK))" && ./packs/pack.sh verify packs/$(PACK) --digest "$$DIGEST"

## ----------------------------------------------------------------------------
## Generated reference docs (ADR-PC-022 §P2 — the un-driftable reference quadrant)
## ----------------------------------------------------------------------------

docs-gen: ## Regenerate docs/.../reference/ from its Avro/CUE/MCP/ADR sources (ADR-PC-022)
	@mise exec -- python3 scripts/docs-gen/generate.py

docs-verify: ## Fail if the generated reference/ tree is stale vs its sources (ADR-PC-022)
	@mise exec -- python3 scripts/docs-gen/generate.py --check

## ----------------------------------------------------------------------------
## Docs site (ADR-PC-026 §P3 — DocFX: C# API reference on GitHub Pages; corpus not stitched)
## ----------------------------------------------------------------------------

docs-site: ## Build the DocFX site (C# XML-doc API reference; corpus not stitched, ADR-PC-026 §P3) into docfx/_site
	@mise exec -- dotnet restore engine/Babelstone.slnx
	@mise exec -- dotnet tool restore
	@mise exec -- dotnet docfx docfx/docfx.json

docs-site-serve: ## Build the DocFX site, then serve it on http://localhost:8080 (ADR-PC-026)
	@mise exec -- dotnet restore engine/Babelstone.slnx
	@mise exec -- dotnet tool restore
	@mise exec -- dotnet docfx docfx/docfx.json --serve

## ----------------------------------------------------------------------------
## Local dev stack (infra/compose.yaml) — PostgreSQL + Redpanda + Console
## ----------------------------------------------------------------------------

up: ## Start the local dev stack and wait until healthy
	$(COMPOSE) up -d --wait
	@echo ""
	@echo "Stack is healthy. Endpoints:"
	@echo "  PostgreSQL        localhost:$(PG_PORT)   (db=babelstone user=babelstone pass=babelstone)"
	@echo "  Kafka API         localhost:$(KAFKA_PORT)"
	@echo "  Schema Registry   http://localhost:$(SR_PORT)"
	@echo "  Redpanda Console  http://localhost:$(CONSOLE_PORT)"
	@echo "  Kong proxy        http://localhost:$(KONG_PROXY_PORT)   (edge gateway)"
	@echo "  Kong admin        http://localhost:$(KONG_ADMIN_PORT)"
	@echo "  OpenBao           http://localhost:$(OPENBAO_PORT)   (UI at /ui; dev root token: root)"
	@echo "  Grafana           http://localhost:$(GRAFANA_PORT)   (LGTM: logs/traces/metrics; anonymous admin)"
	@echo "  OTLP endpoint     localhost:$(OTLP_GRPC_PORT) (gRPC) / localhost:$(OTLP_HTTP_PORT) (HTTP)  — export telemetry here"
	@echo "  OCI registry      localhost:$(REGISTRY_PORT)   (oras push/pull packs; e.g. localhost:$(REGISTRY_PORT)/babelstone-packs/…)"
	@echo "  EventCatalog      http://localhost:$(EVENTCATALOG_PORT)"

down: ## Stop the stack, keep data volumes
	$(COMPOSE) down

reset: ## Destroy the stack AND its data volumes, then start fresh
	$(COMPOSE) down -v
	@$(MAKE) up

logs: ## Follow logs from all stack services
	$(COMPOSE) logs -f

ps: ## Show stack service status
	$(COMPOSE) ps

verify: ## Smoke-test the stack: Postgres reachable, Redpanda healthy, SR responding
	@echo "→ PostgreSQL ..."
	@$(COMPOSE) exec -T postgres pg_isready -U babelstone -d babelstone
	@echo "→ Redpanda cluster ..."
	@$(COMPOSE) exec -T redpanda rpk cluster health | grep -E 'Healthy:.+true'
	@echo "→ Schema Registry ..."
	@curl -fsS http://localhost:$(SR_PORT)/subjects >/dev/null && echo "Schema Registry OK (GET /subjects)"
	@echo "→ Kong gateway ..."
	@curl -fsS http://localhost:$(KONG_ADMIN_PORT)/status >/dev/null && echo "Kong OK (admin /status)"
	@echo "→ OpenBao ..."
	@curl -fsS http://localhost:$(OPENBAO_PORT)/v1/sys/health >/dev/null && echo "OpenBao OK (sys/health)"
	@echo "→ Grafana (LGTM) ..."
	@curl -fsS http://localhost:$(GRAFANA_PORT)/api/health >/dev/null && echo "Grafana OK (api/health)"
	@echo "→ OTel Collector ..."
	@curl -fsS http://localhost:$(COLLECTOR_HEALTH_PORT)/ >/dev/null && echo "Collector OK (health_check)"
	@echo "→ OCI registry ..."
	@curl -fsS http://localhost:$(REGISTRY_PORT)/v2/ >/dev/null && echo "Registry OK (GET /v2/)"
	@echo "→ EventCatalog host ..."
	@curl -fsS http://localhost:$(EVENTCATALOG_PORT)/ >/dev/null && echo "EventCatalog OK (static host)"
	@echo "✓ Stack verified."

## ----------------------------------------------------------------------------
## Walking-skeleton demo (Epic E — thin term-deposit slice via MCP, bd 7puj)
## ----------------------------------------------------------------------------

demo-mcp: ## Run the Epic-E walking skeleton end-to-end (Postgres→deploy→engine→MCP), leave it up
	@./scripts/demo-mcp.sh up

demo-mcp-down: ## Stop the demo's engine + MCP processes (Postgres is left running)
	@./scripts/demo-mcp.sh down
