# IAM runbook — register Mission Control as a Boundary-1 Logto app (bd babelstone-zla1.10.8.3)

Plain English: staging is a public box, so the Mission Control demo UI (`app.babelstone.dev`) must
not be open to the world — it now sits behind a **Logto login**. This is the operator guide for the
one step that **cannot** be automated: hand-registering a Logto application for Mission Control and
seeding its secret. The deployment manifests already point Mission Control at Logto
(`overlays/staging/mission-control.yaml`); what a machine can't do for you is create the Logto app
and hand you back its client secret. Do this **before** the first staging deploy that carries the
gate, or the pod fails closed (by design) and never comes up.

This is [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
**rollout step 2** (Boundary 1 — the owned web/mobile/Mission-Control channels). Open self-service
onboarding (DCR / RFC 7591) is the accepted gap
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§C6), so at staging scale every client — Grafana, the MCP agents, and now Mission Control — is
curated and hand-registered.

Scope: the single-node staging box (`overlays/staging`), Logto at `https://auth.babelstone.dev`,
Mission Control at `https://app.babelstone.dev`. Prerequisite: Logto is deployed and seeded
(bd babelstone-zla1.10.2 — `logto.yaml` + `logto-jobs.yaml`).

> **Secrets discipline.** No client secret or signing key is ever committed. The Logto client
> secret and Mission Control's session-signing key live in the OpenBao-seeded Kubernetes Secret
> `babelstone-dev-secrets` and are injected at deploy (memory: secrets off the bus;
> [ADR-PC-004](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
> §A1). The committed `secrets.example.yaml` carries only trivial dev placeholders.

**Ordering — read this first.** The gate this wiring configures is serve.py's OIDC login gate, which
ships in the Mission Control image built from PR #490. `MC_AUTH_MODE=oidc` on a public bind makes
serve.py **refuse to start** unless the gate is fully configured and Logto discovery succeeds — the
deliberate fail-closed contract. So the manifest change (`MC_AUTH_MODE=oidc`) must land on an image
that already carries the gate code, and the Logto app + the two secret keys below must exist before
that image is deployed.

---

## 0. Reach the Logto Admin Console

Same as the MCP-resource runbook: open the Console at its own HTTPS host,
**`https://auth-admin.babelstone.dev`**, and sign in with the Logto admin account. Do **not** use a
`kubectl port-forward` to `localhost:3002` — Logto OSS v1.41 mints Management-API tokens with
`iss = {ADMIN_ENDPOINT}/oidc` and the default tenant rejects any other issuer, so every Console
write 401s on a port-forward. See
[`iam-mcp-resource-registration.md` §0](./iam-mcp-resource-registration.md#0-reach-the-logto-admin-console)
for the full rationale.

## 1. Create the Mission Control application

1. Console → **Applications** → **Create application** → type **Traditional Web** (a *confidential*
   server-side client). Mission Control's `serve.py` runs the code→token exchange **server-side**
   with a client secret (`client_secret_post`) **plus** PKCE — it is not a browser SPA, so pick the
   confidential Web app type, not Single-Page App.
2. Name it **exactly `babelstone-mission-control`** — not a free-form display name. This is the
   `OPS_APP_NAME` that [`scripts/iam/register-ops-console.py`](../../scripts/iam/register-ops-console.py)
   (and the CD `configure-logto` job, which does not override it) key on to find the app
   idempotently. Register it under any other name and the automated reconcile won't match it — it
   will create a **second, duplicate** app named `babelstone-mission-control`, and operator login
   ends up split across two clients.
3. Logto **generates** the **App ID** (= the OAuth `client_id`) and it is immutable — you do NOT get
   to choose it. Copy the generated value into `OIDC_CLIENT_ID` in
   `overlays/staging/mission-control.yaml` and redeploy; the two MUST match. (The current staging
   value is `237fljdnwu8hx5t634uv7`; Logto regenerates this id on every re-seed, so treat it as
   illustrative, not fixed — it is the *requirement* to pin the minted id that is bd babelstone-zla1.10.12.)
   The CD `configure-logto` job **enforces**
   this match: it runs `scripts/iam/register-ops-console.py` with `OPS_EXPECT_CLIENT_ID` set to the
   deployed `OIDC_CLIENT_ID`, and the script **fails loud** (non-zero, blocking the promote) if the
   Logto-registered App ID differs. So on a fresh re-onboard the first promote fails by design until
   you pin the newly-minted id here and redeploy — that is the fail-loud gate working, not a fault.

## 2. Configure the redirect + flow (auth-code + PKCE S256)

On the new application:

| Setting | Value | Why |
|---|---|---|
| **Redirect URI** | `https://app.babelstone.dev/callback` | serve.py derives this from `MC_PUBLIC_BASE_URL` as `{base}/callback`; it must match **exactly** (Logto is strict) |
| **Post-sign-out redirect URI** | `https://app.babelstone.dev/` | where `/logout` bounces after Logto clears its session |
| **Grant type** | Authorization Code | the only flow serve.py runs; no implicit, no tokens in query strings |
| **PKCE** | required, **S256** | serve.py always sends an S256 `code_challenge` |
| **Token endpoint auth** | `client_secret_post` | serve.py posts `client_secret` in the token-exchange body |

Grant the application the scopes **`openid profile email`** (the serve.py `OIDC_SCOPES` default —
the manifest deliberately leaves `OIDC_SCOPES` unset). For the LOGIN itself that is all that is
needed: Mission Control is an owned-channel *login*.

> **Update (bd babelstone-zla1.10.9).** Once Mission Control routes its product-API calls
> (`/v1/*` + `/api/v1/*`) **through Kong** with a real access token, the login is no longer *only* a
> login — it must also obtain a token the product API accepts. That needs the shared **product-API
> API resource + scopes** to exist in Logto and to be granted to the operator. The earlier "no
> API-resource scopes are needed" posture held only while the demo bypassed Kong; see **§2a** below.

> **PSD2 SCA at login (Boundary 1).** Where the Mission Control session will drive money operations,
> configure Logto's WebAuthn/TOTP MFA so login emits `acr`/`auth_time`
> ([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
> rollout step 2). serve.py's gate establishes the *session*; the engine-side SCA gate remains the
> control that refuses to settle without fresh `acr`/`auth_time`.

## 2a. Register the product-API resource + grant it to the operator (bd babelstone-zla1.10.9.1)

Plain English: give the operator's login a real product-API *audience* to ask for, so that once
Mission Control routes through Kong (bd babelstone-zla1.10.9) the engine/orchestrator accept its
token. This is the AS-side substrate; it does not change any demo traffic on its own.

1. **Create the resource + scopes** (idempotent; the CD `configure-logto` job runs this on every
   promote, so a re-onboard self-heals — the same reproduce-path guarantee as
   [`register-mcp-resource.py`](../../scripts/iam/register-mcp-resource.py)):

   ```bash
   python3 scripts/iam/register-product-api-resource.py
   # → resource created: id=<generated> indicator=https://api.babelstone.dev/
   #   scope created: deposits:read / deposits:write / loans:write
   ```

   The indicator `https://api.babelstone.dev/` is ONE shared resource covering **both** the engine
   `/v1/*` and orchestrator `/api/v1/*` surfaces (bd babelstone-zla1.10.9 decision). It is DISTINCT
   from the MCP-server resource `https://api.babelstone.dev/mcp` (an operator token and an agent
   token are different audiences and must not be replayable across). Override with
   `PRODUCT_API_RESOURCE_URI`, but the value MUST equal **byte-for-byte** the `resource=` serve.py
   sends (bd babelstone-zla1.10.9.3) and any `aud` Kong enforces (bd babelstone-zla1.10.9.2) — a
   trailing-slash / host mismatch breaks `aud` binding (indicator slash-sensitivity is
   [`iam-mcp-resource-registration.md` §1 step 2 + §4](./iam-mcp-resource-registration.md); on the
   **interactive** auth-code flow serve.py uses, an unsent/mismatched `resource` fails *silently* to a
   default resource rather than the M2M flow's fail-closed `invalid_target`).

2. **Grant the scopes to the operator — curated hand-step.** Operator access is role-scoped
   (ADR-IC-021 **C7**): operators sign in as Logto *users*, so their `authorization_code` access token
   carries a resource's scopes via their **role** (RBAC), not via an app grant. In the Logto console:
   create (or reuse) an operator role, add the `babelstone-product-api` scopes to it, and assign that
   role to the operator user(s). Until a user holds a role granting these scopes, their token will not
   carry them. Grant by hand rather than auto-granting — the same curated, no-DCR discipline
   ADR-IC-021 §C6 applies to agent clients.

3. **serve.py requests them at login** by sending `resource=https://api.babelstone.dev/` + the scopes
   on the authorization *and* token requests (`OIDC_RESOURCE`) — wired in bd babelstone-zla1.10.9.3.
   Kong then validates the resulting token at the edge in bd babelstone-zla1.10.9.2.

## 3. Seed the client secret + the session-signing key

Mission Control's Deployment `secretKeyRef`s two keys out of `babelstone-dev-secrets`
(non-optionally — the pod will not start without them):

| Secret key | Where it comes from |
|---|---|
| `LOGTO_MISSION_CONTROL_CLIENT_SECRET` | **copy** the client secret Logto shows for this application |
| `MC_SESSION_SIGNING_KEY` | **generate** a fresh HMAC key — `openssl rand -base64 32`. This is Mission Control's OWN cookie-signing key, NOT a Logto value; rotating it invalidates every live session |

Add both when you provision the real Secret — see
[`staging-ops.md` §1 step 5](./staging-ops.md#1-provision--first-bring-up-phases-02-account-gated)
(the single `kubectl create secret` that seeds all seven keys). Never commit either value; the
`cd-secret-preflight.sh` gate fails the deploy closed if the live Secret is missing a key or still
holds a `dev-placeholder-…` value.

## 4. Verify the gate end to end (after deploy)

1. `/healthz` is gate-exempt and must return `200` regardless of session — the readiness/liveness
   probes use it:

   ```bash
   curl -s -o /dev/null -w '%{http_code}\n' https://app.babelstone.dev/healthz
   # → 200  ({"status":"ok","auth":"oidc"})
   ```

2. An **unauthenticated browser navigation** to `/` must 302-redirect into Logto (auth-code + PKCE),
   not render the UI:

   ```bash
   curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' \
     -H 'Accept: text/html' -H 'Sec-Fetch-Mode: navigate' https://app.babelstone.dev/
   # → 302 https://auth.babelstone.dev/oidc/auth?...client_id=237fljdnwu8hx5t634uv7...
   ```

   A `302` to `auth.babelstone.dev` with `client_id=237fljdnwu8hx5t634uv7` confirms the app is
   registered and the discovery + redirect wiring agree. Completing the login in a browser lands you
   back at `https://app.babelstone.dev/callback` and then the UI.

3. An unauthenticated **non-navigation** request (an XHR/asset, exactly what a kube probe looks like)
   gets a clean `401`, not the UI — this is why the probes target `/healthz`, not `/`.

---

## Production gate (do NOT skip when opening beyond a curated cohort)

Registering Mission Control by hand is the **curated** posture, identical in kind to hand-registering
each MCP agent ([`iam-mcp-resource-registration.md`](./iam-mcp-resource-registration.md)). It is fine
here because there is exactly one owned-channel UI. DCR (RFC 7591) or an RFC 7591 → Logto-Management-API
shim ([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§C6) is the forward path if owned-channel apps ever need to self-register; until then, this runbook is
run by hand.
