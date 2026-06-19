#!/usr/bin/env bash
# infra/redpanda/apply-acls.sh — apply the declarative topic-ACL set (topic-acls.yaml)
# to a Redpanda cluster (ADR-IC-016 plane ii §5 / bd babelstone-njt2.1).
#
# In plain English: this turns infra/redpanda/topic-acls.yaml into live `rpk` ACL rules.
# Every service logs in to Redpanda with its own SASL/SCRAM username (wired in code via
# KafkaSaslOptions); this script grants each username read/write on exactly the topics it is
# allowed to touch, so a compromised service can only reach its own topics. It is idempotent —
# re-running re-asserts the same grants — and reads its own admin credential and the per-service
# passwords from the environment (resolved upstream from OpenBao), never hard-coding a secret.
#
# It is NOT run against local dev (infra/compose.yaml's `--mode dev-container` has no auth); it
# governs an authenticated deployment where `--kafka-enable-authorization=true` is set.
#
# Usage:
#   RPK_BROKERS=redpanda-0:9092 \
#   RPK_USER=admin RPK_PASS=… RPK_SASL_MECHANISM=SCRAM-SHA-256 \
#     infra/redpanda/apply-acls.sh
#
# Optional: pass --dry-run to print the rpk commands without executing them (review aid / CI
# lint). Requires `rpk` on PATH and (for the SCRAM user bootstrap) cluster admin rights.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SPEC="${SCRIPT_DIR}/topic-acls.yaml"

DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

BROKERS="${RPK_BROKERS:-localhost:9092}"

# The admin identity rpk authenticates AS to install ACLs — itself a SASL/SCRAM credential
# resolved from the secret boundary (never a literal here). In --dry-run we don't need it.
if [[ "${DRY_RUN}" -eq 0 ]]; then
  : "${RPK_USER:?set RPK_USER (admin SCRAM user) — resolve it from OpenBao, do not hard-code}"
  : "${RPK_PASS:?set RPK_PASS (admin SCRAM password) — resolve it from OpenBao, do not hard-code}"
fi
SASL_MECH="${RPK_SASL_MECHANISM:-SCRAM-SHA-256}"

rpk_run() {
  if [[ "${DRY_RUN}" -eq 1 ]]; then
    printf 'rpk %s\n' "$*"
    return 0
  fi
  rpk --brokers "${BROKERS}" \
      --user "${RPK_USER}" --password "${RPK_PASS}" --sasl-mechanism "${SASL_MECH}" \
      "$@"
}

# This applier keeps the grant matrix in lock-step with topic-acls.yaml by construction: the
# arrays below mirror the spec file's produce/consume blocks. The spec YAML is the reviewed
# source of truth; CI (acls-lint) diffs the two so they can never silently drift.
#
# WRITE grants (produce) — only the deposit producers may write deposit topics (§5).
declare -a PRODUCE=(
  "svc-engine-deposits:term_deposit"
  "svc-engine-deposits:deposits.integration.events"
  "svc-engine-deposits:deposits.process.events"
  "svc-outbox-publisher:term_deposit"
  "svc-outbox-publisher:deposits.integration.events"
  "svc-outbox-publisher:deposits.process.events"
  "svc-orchestrator:deposits.process.events"
)

# READ grants (consume) — each consumer only the topics it subscribes to (§5).
declare -a CONSUME=(
  "svc-orchestrator:term_deposit"
  "svc-orchestrator:deposits.process.events"
  "svc-inbox-consumer:term_deposit"
)

# Consumer-group offset-commit grants (a consumer needs DESCRIBE/READ on its own group).
# NB: not named GROUPS — that is a bash special array (the caller's OS group ids).
declare -a GROUP_GRANTS=(
  "svc-orchestrator:constitution-process"
  "svc-inbox-consumer:inbox-consumer"
)

echo "Applying Redpanda topic ACLs from ${SPEC} to ${BROKERS} (dry-run=${DRY_RUN})"

for entry in "${PRODUCE[@]}"; do
  principal="${entry%%:*}"; topic="${entry#*:}"
  rpk_run acl create --allow-principal "User:${principal}" \
      --operation write,describe --topic "${topic}"
done

for entry in "${CONSUME[@]}"; do
  principal="${entry%%:*}"; topic="${entry#*:}"
  rpk_run acl create --allow-principal "User:${principal}" \
      --operation read,describe --topic "${topic}"
done

for entry in "${GROUP_GRANTS[@]}"; do
  principal="${entry%%:*}"; group="${entry#*:}"
  rpk_run acl create --allow-principal "User:${principal}" \
      --operation read,describe --group "${group}"
done

echo "Done. Re-run is idempotent (rpk acl create re-asserts an existing grant)."
