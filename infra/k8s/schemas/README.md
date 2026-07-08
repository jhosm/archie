# Vendored Kubernetes JSON schemas (offline `kubeconform` validation)

Plain English: these are the Kubernetes API schemas the manifest-validation gate
checks our YAML against. They are committed here so CI validates **offline** and
never downloads them at run time.

## Why they are vendored (bd babelstone-6qt9)

The CI `infra` job ([`ci.yml`](../../../.github/workflows/ci.yml)) and the CD
`render` job ([`cd.yml`](../../../.github/workflows/cd.yml)) validate the rendered
manifests with `kubeconform -strict`. By default `kubeconform` fetches each Kind's
schema from `raw.githubusercontent.com/yannh/kubernetes-json-schema`. That CDN
**rate-limits (HTTP 429) the shared GitHub-runner IP ranges**, so the fetches
intermittently fail (`failed downloading schema … giving up after 3 attempt(s)`)
and turned the gate flakily red on changes that had nothing to do with it — a
re-run does not reliably clear it, because the runners stay throttled.

Both jobs now pass `-schema-location` pointing here, so validation is fully
hermetic: **no network fetch on the happy path**, deterministic offline.

## Layout

```
v<major.minor.patch>-standalone-strict/<kind><suffix>.json
```

The directory name and file names mirror `kubeconform`'s own template for the
pinned `-kubernetes-version` (currently `v1.31.0`): core `v1` → `service-v1.json`;
`apps/v1` → `deployment-apps-v1.json`; `networking.k8s.io/v1` →
`ingress-networking-v1.json` (the first label of the API group).

## Refreshing (add a new Kind, or bump the K8s version)

A Kind used in a manifest but **not** vendored here makes the gate fail loud
(`no schema found for …`) — that is the signal to re-vendor:

```bash
make k8s-schemas          # → scripts/k8s-schemas-vendor.sh
```

It renders every overlay, works out exactly which schemas are needed, and
downloads any missing ones (riding out the 429 with backoff). To bump the pinned
Kubernetes version, change `K8S_VER` in the script **and** the `-kubernetes-version`
flag in both `ci.yml` and `cd.yml`, then re-run. CI never runs this script — it
only reads the committed files.
