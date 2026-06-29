# hetzner-k3s — provision the staging cluster (Phase 1)

Plain English: this is the one declarative file that turns the empty Hetzner account
into a running single-node Kubernetes cluster. You run one command, `hetzner-k3s`
creates a CAX41 ARM server in Helsinki, installs single-node k3s on it with the Hetzner
cloud-controller + CSI driver, and writes you a kubeconfig. Everything else
(`infra/k8s/overlays/staging`) then deploys *into* that cluster. This is **Phase 1** of the
staging bring-up (bd babelstone-zla1.2); it sits one layer below the Kustomize manifests —
it *creates* the cluster they assume already exists.

It lives in its own top-level `infra/` directory (a sibling of `k8s/`, `kong/`, `otel/`, …)
because it is a distinct provisioning **tool**, not a Kubernetes manifest. Do **not** add
`cluster.yaml` to any kustomization `resources:` list — it is not a k8s object and would
break `kustomize build` + the `kubeconform` CI gate. It is operator-run, not CI-applied.

## Contents

- [`cluster.yaml`](./cluster.yaml) — the [hetzner-k3s](https://github.com/vitobotta/hetzner-k3s)
  cluster config (v2.6.0+ format): 1× CAX41 ARM, Hetzner Helsinki (`hel1`), single-node k3s,
  embedded etcd, the Hetzner CSI driver enabled. The Hetzner API token is **not** in this file —
  it is supplied at runtime via the `HCLOUD_TOKEN` environment variable (see below).

## Prerequisites (Phase 0 — bd babelstone-zla1.1)

- A Hetzner Cloud project and a **read/write API token** for it (kept secret, never committed).
- The `babelstone-staging` SSH keypair at `~/.ssh/babelstone-staging{,.pub}` — `cluster.yaml`
  references both. (Uploading the public key to the Hetzner console is optional; `hetzner-k3s`
  uploads it from `public_key_path` if absent.)
- The pinned `hetzner-k3s` binary. Install per its
  [docs](https://github.com/vitobotta/hetzner-k3s) (Homebrew / release binary); there is no
  in-cluster component to install.

## Provision

```bash
cd infra/hetzner-k3s

# 1. Supply the Hetzner token by env var — it takes precedence over the config and is the
#    upstream-blessed way to keep cluster.yaml committable. NEVER commit the token.
export HCLOUD_TOKEN=<your read/write Hetzner Cloud API token>

# 2. Lock SSH to your IP: replace the REPLACE_ME/32 placeholder in cluster.yaml's
#    networking.allowed_networks.ssh with your operator/jump-host /32.

# 3. Pin a valid k3s version (the committed one can be stale):
hetzner-k3s releases | tail        # list supported releases; set cluster.yaml's k3s_version

# 4. Create the cluster (writes ./kubeconfig — gitignored):
hetzner-k3s create --config cluster.yaml

# 5. Verify:
export KUBECONFIG="$PWD/kubeconfig"
kubectl get nodes              # the one node should reach Ready
kubectl get pods -A           # Hetzner CCM + CSI driver come up automatically
```

The generated `./kubeconfig` is a **cluster-admin credential** — it is gitignored and must never
be committed. It is what the deploy pipeline consumes: base64 it into the `KUBECONFIG_B64` GitHub
environment secret that [`.github/workflows/cd.yml`](../../.github/workflows/cd.yml) reads.

## Hand-off — what comes next

1. **Phase 2 — cluster bootstrap** ([`../k8s/overlays/staging/bootstrap/`](../k8s/overlays/staging/bootstrap/)):
   install cert-manager + the CSI snapshot controller + the system-upgrade-controller, then
   `kubectl apply -f` the issuers / `VolumeSnapshotClass` / k3s upgrade Plan. Point the
   `babelstone.dev` DNS A records at the new node IP.
2. **Phase 3 — deploy** the workloads: `kubectl apply -k ../k8s/overlays/staging` (or dispatch
   `cd.yml`).

The full operator runbook — bring-up, redeploy, restore, upgrade, backups — is
[`../runbooks/staging-ops.md`](../runbooks/staging-ops.md) (its §1.1 cross-references this file).

## Posture notes (recorded drift)

- **Single node is non-HA by choice.** One copy of every stateful service — notably a
  single-replica Postgres event store with no warm standby, plus a single-member k3s control
  plane — is the deliberate degraded posture for a demo box, versus the HA source-of-truth
  topology [ADR-PC-005](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
  mandates (which lives in `overlays/ha`). Recorded as an
  [ADR-PC-020 §D3](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
  explicit-drift in the introducing PR body — no silent divergence.
- **The k8s API (6443) is publicly reachable**, guarded by the cluster's mutual-TLS client cert
  rather than a network allow-list, because the GitHub-hosted `cd.yml` runner has no stable
  egress IP to allow-list. SSH (22) stays locked to the operator IP. Tighten the API exposure
  (self-hosted runner or tunnel) for a real tier.
