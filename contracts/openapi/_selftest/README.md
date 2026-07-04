# OpenAPI catalogue gate — negative fixtures (self-test)

These are the deliberately-broken inputs the gate MUST reject. They are NOT part of the real
catalogue (they live outside `contracts/openapi/specs/`, so the PR gate never lints them). The
self-test harness — `scripts/openapi-catalog-validate.sh --self-test`, wired into CI and available
as `make openapi-catalog-selftest` — builds a throwaway spec set from the good baseline
(`contracts/openapi/specs/`) plus each case's overlay and asserts the gate FAILS.

Each case directory contains either:
- one or more `*.openapi.yaml` files that OVERWRITE same-named baseline files (or add new ones), and/or
- an `internal/` subdirectory whose `*.openapi.yaml` files overlay the INTERNAL catalogue baseline
  (`contracts/openapi/internal/`, bd babelstone-ax0b.5) the same way, and/or
- a `.remove` file listing baseline basenames to delete before running the gate.

Cases (ADR-IC-020 / bd babelstone-ax0b.2 + ax0b.3 acceptance criteria):

| case | defect | check it trips |
| --- | --- | --- |
| `missing-governance-field` | `engine-reads` spec drops `info.x-owner` | Spectral governance rule (ADR-IC-020 Decision §2) |
| `spec-path-not-a-route` | a spec documents `GET /v1/nonexistent` | REVERSE reconcile (not a public Kong route) |
| `route-with-no-spec` | the `engine-reads` spec is removed | FORWARD reconcile (public route with no spec) |
| `post-deposits-command` | a spec documents `POST /v1/deposits` | negative invariant + REVERSE reconcile |
| `internal-marker-on-a-public-route` | `x-internal-route` left on `GET /v1/deposits/maturities`, an exposed public route | marker-contradiction check (the waiver is not a REVERSE bypass; a route going public must drop the marker in the same change) |
| `internal-spec-missing-marker` | an internal-dir spec drops `info.x-internal` | internal-leg marker requirement (bd ax0b.5 — an internal spec must state WHY it is not public) |
| `internal-spec-on-public-route` | an internal-dir spec documents `GET /v1/deposits/maturities`, an exposed public route | NEVER-PUBLIC reconcile (bd ax0b.5 — an internal-catalogue surface can never also be a public Kong route) |

The breaking-change gate (oasdiff, ADR-IC-020 Decision §3) is exercised by the real `--fail-on ERR` diff
against `origin/main`, not by this hermetic self-test (which uses untracked throwaway specs with no
git baseline).
