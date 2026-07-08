# IAM runbook — register Backstage as a Boundary-6 Logto app (bd babelstone-zla1.10.11)

Plain English: staging is a public box, so the Backstage catalogue portal
(`backstage.babelstone.dev`) must not be open to the world — it now sits behind a **Logto login**,
exactly like Grafana and Mission Control. This is the operator guide for the one step that
**cannot** be automated: hand-registering a Logto application for Backstage and seeding its client
secret. The deployment manifests already point Backstage at Logto
(`overlays/staging/backstage-oidc.patch.yaml`); what a machine can't do for you is create the Logto
app and hand you back its client secret. Do this **before** the deploy that carries the gate, or the
pod fails closed (by design) and never comes up.

This is [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
**rollout step 3** (Boundary 6 — operators / ops console / developer surfaces; the same boundary as
Grafana). Open self-service onboarding (DCR / RFC 7591) is the accepted gap
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§C6), so at staging scale every client — Grafana, Mission Control, the MCP agents, and now Backstage
— is curated and hand-registered.

Scope: the single-node staging box (`overlays/staging`), Logto at `https://auth.babelstone.dev`,
Backstage at `https://backstage.babelstone.dev`. Prerequisite: Logto is deployed and seeded
(bd babelstone-zla1.10.2 — `logto.yaml` + `logto-jobs.yaml`).

> **Secrets discipline.** No secret is ever committed. Backstage needs **two** keys in the
> OpenBao-seeded Kubernetes Secret `babelstone-dev-secrets`, injected at deploy
> ([ADR-PC-004](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
> §A1): the **Logto client secret** and Backstage's **own session-signing key** (the generic OIDC
> provider stores the auth-code state/nonce in an express-session, which needs a signing secret —
> the same shape as Mission Control's `MC_SESSION_SIGNING_KEY`). The committed `secrets.example.yaml`
> carries only trivial dev placeholders. (Backstage's *token*-signing keys, separately, live in its
> in-memory SQLite store and regenerate per restart — fine for a single-replica catalogue gate; the
> session-signing key must be stable, so it is seeded.)

**Ordering — read this first.** The OIDC wiring (the backend gate provider + the `oidc.production`
app-config block) ships **baked into the Backstage image** by the zla1.10.11 PR. The
`backstage-oidc.patch.yaml` sets `BACKSTAGE_AUTH_ENVIRONMENT=production`, which makes the app-config
`oidc.production` block live — and a missing `BACKSTAGE_OIDC_CLIENT_SECRET` then stops the pod coming
up (the deliberate fail-closed contract). So the manifest change must land on an image that already
carries the gate code, and the Logto app + the client secret below must exist before that image is
deployed.

---

## 0. Reach the Logto Admin Console

Same as the Mission Control / MCP-resource runbooks: open the Console at its own HTTPS host,
**`https://auth-admin.babelstone.dev`**, and sign in with the Logto admin account. Do **not** use a
`kubectl port-forward` to `localhost:3002` — Logto OSS v1.41 mints Management-API tokens with
`iss = {ADMIN_ENDPOINT}/oidc` and the default tenant rejects any other issuer, so every Console
write 401s on a port-forward. See
[`iam-mcp-resource-registration.md` §0](./iam-mcp-resource-registration.md#0-reach-the-logto-admin-console)
for the full rationale.

## 1. Create the Backstage Logto application

1. Console → **Applications** → **Create application** → type **Traditional Web** (a *confidential*
   server-side client). Backstage's auth backend runs the code→token exchange **server-side** with a
   client secret plus PKCE — it is not a browser SPA, so pick the confidential Web app type, not
   Single-Page App.
2. Name it so it is unmistakable in the cohort register, e.g. **Backstage (staging)**.
3. **Read the App ID** (client_id) Logto assigns. Logto generates this value — it is NOT settable to
   a friendly name. For the current staging app it is **`xs0shrdb5iem7pqgyt86m`**, already wired into
   `BACKSTAGE_OIDC_CLIENT_ID` in `overlays/staging/backstage-oidc.patch.yaml`. If you ever re-create
   the app, Logto assigns a NEW App ID — update `BACKSTAGE_OIDC_CLIENT_ID` to match and redeploy; the
   two MUST be equal.

## 2. Configure the redirect + flow (auth-code + PKCE S256)

On the new application:

| Setting | Value | Why |
|---|---|---|
| **Redirect URI** | `https://backstage.babelstone.dev/api/auth/oidc/handler/frame` | Backstage's OAuth handler path; it must match **exactly** (Logto is strict). Derived from `BACKSTAGE_BASE_URL`. |
| **Post-sign-out redirect URI** | `https://backstage.babelstone.dev` | where a sign-out bounces after Logto clears its session |
| **Grant type** | Authorization Code | the only flow Backstage runs; no implicit, no tokens in query strings |
| **PKCE** | required, **S256** | the Backstage OAuth handler sends an S256 `code_challenge` |
| **Token endpoint auth** | `client_secret_post` | Backstage posts `client_secret` in the token-exchange body |

Grant the application the scopes **`openid profile email`** (the frontend `defaultScopes` in
`packages/app/src/apis.tsx`). No API-resource scopes are needed: Backstage is an owned-surface
*login* (a read-only catalogue gate), not an MCP agent calling a protected resource.

> **Gate-only — no user directory.** Backstage signs in **any** principal Logto authenticates and
> mints its identity from the OIDC `sub`, with **no** catalog `User` entity required
> (`packages/backend/src/oidcGateProvider.ts`). So no personal identity data is stored in Backstage,
> and the deferred GDPR data-inventory item (bd babelstone-zla1.6.8 / ADR-IC-015 Residual Risk) stays
> deferred. If you later restrict access to specific people or add `User`/`Group` catalog entities,
> that is what re-triggers zla1.6.8.

## 3. Seed the two secrets

Backstage's Deployment `secretKeyRef`s two keys out of `babelstone-dev-secrets` (non-optionally —
the pod will not start without them once `BACKSTAGE_AUTH_ENVIRONMENT=production`):

| Secret key | Where it comes from |
|---|---|
| `LOGTO_BACKSTAGE_CLIENT_SECRET` | **copy** the client secret Logto shows for this application |
| `BACKSTAGE_AUTH_SESSION_SECRET` | **generate** a fresh key — `openssl rand -base64 32`. Backstage's OWN OIDC session-signing secret, NOT a Logto value; rotating it drops live sessions |

Add both when you provision the real Secret — see
[`staging-ops.md` §1 step 5](./staging-ops.md#1-provision--first-bring-up-phases-02-account-gated)
(the single `kubectl` secret seed). Never commit the value; the `cd-secret-preflight.sh` gate fails
the deploy closed if the live Secret is missing a key or still holds a `dev-placeholder-…` value.

## 4. Deploy + verify the gate end to end

Redeploy Backstage on an image that carries the zla1.10.11 gate wiring (the `image-build.yml` path
filter covers `backstage/**` + `infra/backstage/**`, so a merge rebuilds `:latest`; redeploy per
`staging-ops.md`). Then:

1. The portal shell still serves (the app is up):

   ```bash
   curl -s -o /dev/null -w '%{http_code}\n' https://backstage.babelstone.dev
   # → 200
   ```

2. The OIDC start endpoint 302-redirects into Logto (auth-code + PKCE) with the right client_id —
   this proves the app registration + discovery + redirect wiring agree:

   ```bash
   curl -s -o /dev/null -w '%{http_code} %{redirect_url}\n' \
     'https://backstage.babelstone.dev/api/auth/oidc/start?env=production'
   # → 302 https://auth.babelstone.dev/oidc/auth?...client_id=xs0shrdb5iem7pqgyt86m...
   ```

3. In a browser, `https://backstage.babelstone.dev` transparently redirects to Logto (the frontend
   `auto` provider), and completing the login lands you back in the catalogue. A user with no catalog
   `User` entity still gets in — that is the gate-only design.

If the pod is stuck `CrashLoopBackOff` / not-ready after the deploy, the secret is almost certainly
missing or still a placeholder (fail-closed) — check `LOGTO_BACKSTAGE_CLIENT_SECRET` in the live
Secret, and the backstage pod logs for the Logto discovery/auth error.

---

## Production gate (do NOT skip when opening beyond a curated cohort)

Registering Backstage by hand is the **curated** posture, identical in kind to hand-registering
Grafana, Mission Control, and each MCP agent. It is fine here because the staging cohort is small and
curated. DCR (RFC 7591) or an RFC 7591 → Logto-Management-API shim
([ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§C6) is the forward path if these surfaces ever need to self-register; until then, this runbook is
run by hand.
