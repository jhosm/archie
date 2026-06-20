# Stub authorization server — step-up SCA token issuer (POC-only)

**In plain English.** When an AI agent asks the bank to do something irreversible (mature a deposit,
pay a coupon), the rules say the human must pass a fresh strong-customer-authentication (SCA) challenge
first, and the bank — not the agent — must vouch that it passed. In production a real bank
authorization server (AS) does that: after the customer re-authenticates, the AS signs a fresh `acr`
(authentication-context-class) and `auth_time` into a new access token. This reference system has no
real bank AS, so this directory is the **stub** that stands in for it: it mints the step-up token the
agent presents on the retry after completing SCA. It signs with a **throwaway dev key only** — it
proves nothing about a real customer and grants nothing on a real deployment.

## What it is

A thin issuer (`mint-stepup-token.sh`) that produces an OAuth bearer JWT carrying a **fresh** step-up
SCA proof — a non-empty `acr` and an `auth_time` of *now* — bound to the MCP server audience. It is the
post-SCA half of the Q-BE flow (bd `babelstone-ziu3.5`):

1. The agent calls `mature_deposit` / `pay_interest` with whatever token it holds.
2. The engine's `ScaPrecondition` finds no fresh SCA and returns `422 SCA_REQUIRED` (Q1).
3. The MCP tool fires the URL-mode step-up elicitation; the human re-authenticates at the
   bank-controlled URL.
4. **This stub** issues the refreshed token (the bank's signed "they passed" signal); the agent
   retries with it. Kong validates the signature, attests `X-SCA-Acr` / `X-SCA-Auth-Time` to the
   engine, and the engine settles (Q2).

The trust anchor is the **AS signature** Kong validates — never anything the agent reports. An agent
that fabricates an "accept" without a genuinely refreshed token is still `422`'d on the retry.

## Signing key

The stub signs with the **same throwaway POC RSA private key** committed in
[`infra/kong/kong.yml`](../kong/kong.yml) (the key paired with the `iam-issuer` consumer's
`rsa_public_key`, the one Kong's `jwt` plugin verifies against). That keeps the stub self-contained:
a token it mints verifies at the live Kong edge with no extra wiring. The key is a **POC throwaway,
not a secret** — it is committed precisely so the local edge runs byte-for-byte.

> ⚠️ **Never run this against a real or shared deployment.** Replacing the throwaway key in `kong.yml`
> at deploy time (a `deck sync` from a secret store) makes every token this stub mints invalid — by
> design. The real bank AS issues step-up tokens only after a genuine SCA. Real key material must
> **never** be committed: a `.gitignore` here blocks `*.pem` / `*.key` / `secrets/`.

## Usage

```bash
# Mint a FRESH step-up token bound to the MCP server (acr present, auth_time = now), write/read scopes:
infra/stub-as/mint-stepup-token.sh

# Bind to a different audience / scopes / acr level:
infra/stub-as/mint-stepup-token.sh --aud http://localhost:8000/mcp --scope "deposits:write" --acr urn:bank:sca:psd2

# Exercise the ENGINE 422 path: a token with NO step-up SCA (the pre-step-up state):
infra/stub-as/mint-stepup-token.sh --no-sca

# Exercise the stale-SCA 422: SCA completed 10 minutes ago (> the 300s freshness window):
infra/stub-as/mint-stepup-token.sh --auth-age 600
```

It delegates to the repo's `scripts/mint-edge-token.sh` (the single token-minting implementation), so
the JWT shape, the `iss`/signing-key pairing, and the claim-escaping safety are identical to every
other edge token in the system. This directory is the **named, documented home** of the step-up issuer
role; the script is the thin, scoped front door to it.
