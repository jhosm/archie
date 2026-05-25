# Babelstone — task entrypoint. Run `make` (or `make help`) to list targets.
#
# This file currently fronts the local dev stack (infra/compose.yaml, P.1).
# The language toolchain bootstrap (.NET / Go / Python / CUE / cosign / …) lands
# here under P.2 — keep targets grouped by section.

COMPOSE := docker compose -f infra/compose.yaml

# Host endpoints the stack exposes (kept in sync with infra/compose.yaml).
PG_PORT       ?= 5432
SR_PORT       ?= 18081
KAFKA_PORT    ?= 19092
CONSOLE_PORT  ?= 8080

.DEFAULT_GOAL := help
.PHONY: help up down reset logs ps verify

help: ## List available targets
	@echo "Babelstone — make targets:"
	@grep -hE '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-10s\033[0m %s\n", $$1, $$2}'

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
	@echo "✓ Stack verified."
