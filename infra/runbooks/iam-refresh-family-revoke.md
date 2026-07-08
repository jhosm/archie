# IAM runbook — prove refresh-token reuse revokes the whole family (C3, bd babelstone-zla1.10.5 slice 3)

Plain English: this is the operator guide (and the durable, re-runnable proof) for the single scariest
"does it actually work?" question in the IAM design — if an attacker steals a refresh token and the real
client later rotates it, does replaying the stolen token kill the ENTIRE login (every token), or just
bounce the one token? ADR-IC-021 §C3 (`IAM_REFRESH_REUSE_FAMILY_REVOKE`) requires the whole-family kill,
and flagged it as "must be proven before relied upon" because it was undocumented for our deployment.
It is now **PROVEN** on the live staging Logto (v1.41.0) by `scripts/iam/prove-refresh-family-revoke.py`.

Read alongside [ADR-IC-021](../../docs/product-management/integration_concepts/adrs/ADR-IC-021-iam-oauth-authorization-server.md)
§C3 and its Residual Risks.

> **Secrets discipline.** The proof reads its Management-API token from `$LOGTO_MGMT_TOKEN`; the throwaway
> user's password is generated locally per run and never persisted or echoed. The throwaway client + user
> are deleted in a `finally`. Nothing secret is committed (memory: secrets off the bus; ADR-PC-004 §A1).

---

## Run it

```bash
export LOGTO_MGMT_TOKEN=$(curl -s -A babelstone-iam/1.0 -u "$MGMT_APP_ID:$MGMT_APP_SECRET" \
  -d grant_type=client_credentials --data-urlencode resource=https://default.logto.app/api \
  -d scope=all https://auth.babelstone.dev/oidc/token | jq -r .access_token)
python3 scripts/iam/prove-refresh-family-revoke.py
# → RESULT: PASS — C3 proven. ... both the ancient RT0 AND the newest RT2 -> invalid_grant.
```

The script is self-contained and self-cleaning: it creates a throwaway **public (Native)** client with
`rotateRefreshToken` + a throwaway user, drives a **headless authorization-code + PKCE(S256)** login through
Logto's `/api/experience` cookie flow (no browser) to mint a refresh token, rotates it twice, replays the
consumed token, and asserts the whole family dies. Then it deletes the fixtures.

## What it proves (and the gotchas it encodes)

1. **Whole-family revoke works.** After two rotations `RT0 → RT1 → RT2`, replaying the ancient `RT0`
   returns `invalid_grant` **and** the newest `RT2` also returns `invalid_grant` — the entire `grantId`
   family is revoked, not just the reused token. This is the theft-response behaviour C3 depends on.

2. **The rotation GRACE window (the reason a naive test false-negatives).** Logto keeps a just-rotated
   refresh token valid for a brief grace window (a few seconds — it tolerates concurrent refreshes /
   client retries). Replaying **inside** that window is still legitimately accepted (it mints yet another
   token), so a back-to-back reuse test wrongly concludes "no reuse-detection." The proof waits
   `C3_GRACE_WAIT` (default 5 s) past the grace before replaying. **If you ever see this test fail,
   check the wait first** — a too-short wait is the most likely cause, not a real regression.

3. **Headless login gotchas** (each cost a debugging round; encoded in the script so they don't recur):
   - Logto usernames must match `^[A-Za-z_]\w*$` — **no hyphens**.
   - Since slice 2 enabled MFA (UserControlled), sign-in `submit` returns `422 user.suggest_mfa`; the
     probe skips the optional binding via `POST /api/experience/profile/mfa/mfa-skipped` and re-submits.
   - `offline_access` is silently stripped for a first-party app unless consent is recorded, so the
     authorize request uses `prompt=consent` (Logto auto-consents a trusted first-party app), which is
     what actually yields a refresh token.
   - A **public** client is used so rotation fires on every refresh (a confidential client only rotates
     at ≥70 % of a multi-day TTL — untestable in seconds).

## Result (2026-07-08)

`RESULT: PASS` on Logto **v1.41.0** (`svhd/logto@sha256:7f79547e…`). C3 is empirically verified: the
ADR-IC-021 §C3 residual ("undocumented; must be proven") is **resolved positively**. The catalogue row
`IAM_REFRESH_REUSE_FAMILY_REVOKE` stays `Gap`/`Planned` for **CI** (it cannot run against live staging in
CI — the honest-split decision, bd zla1.10.5 slice 5), but now carries this script + run as its documented
empirical proof.
