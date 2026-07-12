# IAM runbook — SCA step-up + MFA on the default tenant (Boundary 6/9, bd babelstone-zla1.10.5 slice 2)

Plain English: this is the operator guide for turning on strong, multi-factor sign-in for the tenant
your operators, agents, and Grafana SSO authenticate through, and for wiring the "step-up" that
PSD2/SCA needs before an irreversible money-mover. It also records something the security verification
discovered the hard way: the Logto we deployed does **not** emit the OIDC `acr` claim the architecture
originally assumed, so step-up *strength* has to be manufactured — the *freshness* half works natively.
Read this alongside [ADR-IC-010 Amendment 2026-07-08 (§A16/§A17)](../../docs/product-management/integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)
and [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
commitments C4 (`IAM_SCA_ACR_AUTH_TIME`) and C7 (`IAM_OPS_CONSOLE_STEP_UP`).

Scope: the single-node staging box (`overlays/staging`), Logto at `https://auth.babelstone.dev` (the
DEFAULT tenant — **not** the `auth-admin` admin console, which is a separate `admin` tenant and keeps
its own passkey untouched by anything here). Prerequisite: Logto is deployed + seeded (bd
babelstone-zla1.10.2) and you can mint a Management-API token (§"Management-API token" below).

> **Secrets discipline.** No client secret or token is ever committed or echoed. Client secrets live in
> the OpenBao-seeded `babelstone-dev-secrets` Secret, injected at deploy; Management-API tokens are
> minted at runtime and never written to the repo (memory: secrets off the bus; ADR-PC-004 §A1). The
> two scripts below read the token from `$LOGTO_MGMT_TOKEN` and write nothing secret;
> `register-ops-console.py` deliberately suppresses the client secret from its output.

---

## 0. The finding: Logto emits no native OIDC `acr` (the C4 watch-item, resolved)

ADR-IC-021 §C4 flagged "Logto `acr` maturity" as the watch-item. Verified against the live discovery
document on 2026-07-08 (`https://auth.babelstone.dev/oidc/.well-known/openid-configuration`):

| Capability | Live value | Consequence |
|---|---|---|
| `acr_values_supported` | **absent** | No `acr`-value / RFC 9470 essential-`acr` step-up on this deployment |
| `acr` in `claims_supported` | **absent** | Logto never stamps a native `acr` — its `node-oidc-provider` core defaults `acrValues=[]` |
| `request_parameter_supported` / `claims_parameter_supported` | `false` / `false` | A client cannot request an essential `acr` via a request object or the `claims` parameter |
| `auth_time` in `claims_supported` | **present** | Freshness IS native — and is the load-bearing SCA signal |
| `code_challenge_methods_supported` | `["S256"]` only | PKCE-S256 enforced (ADR-IC-021 C2) |

So SCA step-up splits into two legs (see §3 and §4). This is not a blindside — the enforcement gate
never depended on the strength marker to *move money*, only on freshness, which is native. Today's
`acr` in the POC/CI path is minted by the **stub authorization server**
(`infra/stub-as/mint-stepup-token.sh`) signing a literal `acr` with the committed POC key, so
`MCP_SCA_GATE_CANNOT_BYPASS` is proven and `Live` **independent of Logto**.

---

## Management-API token

The `babelstone-mgmt` M2M app drives the Management API. Mint a fresh token (expires in 1h); never
print the secret:

```bash
export LOGTO_MGMT_TOKEN=$(curl -s -A babelstone-iam/1.0 \
  -u "$MGMT_APP_ID:$MGMT_APP_SECRET" \
  -d grant_type=client_credentials \
  --data-urlencode resource=https://default.logto.app/api \
  -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
```

The Management-API base is `https://auth.babelstone.dev/api` (the DEFAULT-tenant host — cross-tenant
routing matters; the `auth-admin` host mints admin-tenant tokens the default tenant rejects).

---

## 1. Offer WebAuthn + TOTP on the default sign-in experience

Idempotent; `UserControlled` policy ("offer, non-forcing" — nothing is forced to enroll on its next
login; a factor is only demanded on a step-up). This is the C4/C7 prerequisite: it lets a re-auth
actually PERFORM the SCA whose result the strength-synthesis (§3) reads.

```bash
python3 scripts/iam/enable-default-mfa.py
# → MFA after: factors=['Totp', 'WebAuthn'] policy=UserControlled
```

Logto config is live Management-API DB state, NOT captured in the manifests or `logto db seed` — a
re-onboard wipes it. This script is the reproduce path (mirrors `scripts/iam/register-mcp-resource.py`).

---

## 2. Register the ops-console client (Mission Control)

Mission Control (`app.babelstone.dev`) is the C7 ops console. Its Deployment already runs
`MC_AUTH_MODE=oidc` against client id `babelstone-mission-control`, but the client is wiped on a
re-onboard. Re-create it idempotently:

```bash
python3 scripts/iam/register-ops-console.py
# → ops-console app created: id=<generated> name=babelstone-mission-control (secret NOT printed)
```

**Two maintainer follow-ups the script prints** (both are secret-writes / cluster mutations, so they
are yours to run, not the agent's):

1. Logto assigns a **random App ID**; set `OIDC_CLIENT_ID` in
   `infra/k8s/overlays/staging/mission-control.yaml` to the printed id and redeploy.
2. Seed `LOGTO_MISSION_CONTROL_CLIENT_SECRET` into `babelstone-dev-secrets` per
   [`mission-control-oidc-registration.md`](./mission-control-oidc-registration.md) §3.

This closes the live "ops-console client missing" gap C7 depends on (also advances bd
babelstone-zla1.10.8).

---

## 3. Step-up STRENGTH — the synthesised non-`acr` claim (the real-Logto path)

Because Logto emits no native `acr` (§0), the `acr`-equivalent strength marker is produced by Logto's
custom-JWT-claims feature (`getCustomJwtClaims`), whose access-token `context` exposes the current
sign-in's `interactionEvent.verificationRecords` (`Totp` / `WebAuthn`). The script reads those and
emits a custom claim under a name that is **not** `acr` — a claim literally named `acr` collides with a
`node-oidc-provider` built-in and is **silently dropped**. Kong's `/mcp` SCA `pre-function`, which today
reads a claim named `acr`, is repointed to read that custom name (a `kong.yml` pre-function edit only —
no edge-contract or route change; the same class of deploy-time repoint as the ADR-IC-006 §P7
issuer/JWKS swap).

The synthesised claim lands on the **access token only** and only on a **fresh interactive grant** (not
a silent `refresh_token`) — exactly what the ADR-IC-010 §A7 engine-`422`→re-auth→refreshed-token loop
forces. Deploying the `getCustomJwtClaims` script to Logto + the Kong read-name repoint is the
real-Logto realisation of ADR-IC-010 §A16; it is tracked as follow-up under bd babelstone-zla1.10.5 and
is **not** wired in this slice (the stub-AS `acr` keeps the gate `Live` until then).

---

## 4. Step-up FRESHNESS — native `auth_time` (load-bearing, already Live)

The load-bearing SCA signal is freshness, and it is native: Logto emits `auth_time`, refreshed by a
genuine re-authentication (`prompt=login` / `max_age`, honoured by the `node-oidc-provider` default
interaction policy). Kong's `/mcp` SCA `pre-function` and the engine `ScaPrecondition` already gate on
`X-SCA-Auth-Time` against `SCA_MAX_AGE` (300 s) — **no change needed**. This leg is why
`MCP_SCA_GATE_CANNOT_BYPASS` / `SETTLEMENT_LEG_SCA_GATE_CANNOT_BYPASS` are `Live` (engine + orchestrator
Testcontainers lanes) regardless of the AS: the gate settles only on a fresh, gateway-attested
`auth_time` a courier agent cannot forge.

> Empirical note: `prompt=login`/`max_age` honouring on `svhd/logto:latest` is a defensible inference
> from the `node-oidc-provider` default policy (its metadata does not advertise `prompt_values_supported`);
> confirm it end-to-end when the headless auth-code flow lands (bd babelstone-zla1.10.5 slice 3 shares
> that machinery).

---

## 5. Mission Control operator money-movers through Kong (bd babelstone-zla1.10.9)

Plain English: today a Mission Control operator who clicks a **manual** money-mover in the UI does
**not** go through Kong — serve.py proxies straight to the engine (which `422`s `SCA_REQUIRED`, because
no gateway attested the step-up), and in local dev serve.py mints a stand-in `X-SCA-*` header itself
(bd babelstone-e4mq). [bd babelstone-zla1.10.9](../../docs/product-management/integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)
routes that path through Kong with the operator's **real** access token, so the step-up rides Logto's
native `auth_time` (§4) + the synthesised strength claim (§3), attested at the edge exactly like the
`/mcp` agent path. When that epic lands:

- the operator's token must carry the shared **product-API resource** `aud` + a fresh `auth_time` —
  register the resource per
  [`mission-control-oidc-registration.md` §2a](./mission-control-oidc-registration.md); serve.py
  requests it (`resource=`) in bd babelstone-zla1.10.9.3;
- Kong attests `X-SCA-Acr` / `X-SCA-Auth-Time` on the human money-mover routes
  (bd babelstone-zla1.10.9.2) in the **attest-not-deny** model — the engine `422`s → the UI drives a
  fresh step-up → retry settles — so the human path reuses the same `X-SCA-Auth-Time` freshness gate
  this section describes;
- serve.py's dev-mode stub-AS `X-SCA-*` mint (bd babelstone-e4mq) is **retired** on the Kong-fronted
  path (bd babelstone-zla1.10.9.3) — it never forged SCA against a real deployment (it is gated on
  `AUTH is None`), and Kong now attests the real proof.

> Ordering note: the human-path attestation depends on the §3 strength-claim leg (the `getCustomJwtClaims`
> synthesis + the Kong read-name repoint, ADR-IC-010 §A16). Confirm that leg is actually deployed against
> the live Logto **before** bd babelstone-zla1.10.9.2/.9.3 retire the stub-AS mint, or the human
> money-mover `X-SCA-Acr` arrives empty and every manual money-mover `422`s.
