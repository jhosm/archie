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

.DEFAULT_GOAL := help
.PHONY: help bootstrap doctor up down reset logs ps verify

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
	@echo "✓ Stack verified."
