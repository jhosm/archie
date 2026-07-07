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
  it is supplied at runtime via the `HCLOUD_TOKEN` environment variable (see below). The SSH
  allow-list is likewise **not** a real value in this file: it is the `REPLACE_ME/32` sentinel
  that `provision.sh` substitutes (deliberately invalid, so a direct `create` against the
  committed file cannot succeed).
- [`provision.sh`](./provision.sh) — the **only supported way to run `create`**
  (bd babelstone-zla1.12.6): a fail-closed preflight + render wrapper. It requires
  `SSH_ALLOWED_CIDR` (env, like `HCLOUD_TOKEN`), validates it as explicit IPv4 `/32`(s) —
  refusing `REPLACE_ME`, `0.0.0.0/0`, and any broader mask — renders the gitignored
  `cluster.rendered.yaml`, re-checks the rendered `networking.allowed_networks.ssh` list, and
  only then execs `hetzner-k3s create`. `--check-only` runs the preflight + render without
  creating anything. Rationale: the fastest "fix" for a failed create used to be pasting
  `0.0.0.0/0`, opening SSH to the whole internet on the box that holds etcd, the cluster-admin
  credential, and the cloud token (ADR-IC-006 minimise-public-surface posture; CIS host
  hardening).

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

# 2. Lock SSH to your IP — by env var, same discipline as the token. provision.sh REFUSES
#    to run when this is unset, still REPLACE_ME, 0.0.0.0/0, or not an explicit /32
#    (comma-separate a small list if you need more than one host). Don't edit cluster.yaml.
export SSH_ALLOWED_CIDR=<your operator/jump-host IP>/32

# 3. Pin a valid k3s version (the committed one can be stale):
hetzner-k3s releases | tail        # list supported releases; set cluster.yaml's k3s_version

# 4. Create the cluster via the fail-closed wrapper (preflights the SSH allow-list, renders
#    the gitignored cluster.rendered.yaml, then runs `hetzner-k3s create`; writes
#    ./kubeconfig — gitignored). `./provision.sh --check-only` dry-runs the preflight.
./provision.sh

# 5. Verify:
export KUBECONFIG="$PWD/kubeconfig"
kubectl get nodes              # the one node should reach Ready
kubectl get pods -A           # Hetzner CCM + CSI driver come up automatically
```

The generated `./kubeconfig` is a **cluster-admin credential** — it is gitignored and must never
be committed, and it is **for the human operator only** (bootstrap, break-glass). It is NOT what
the deploy pipeline consumes: [`.github/workflows/cd.yml`](../../.github/workflows/cd.yml) refuses
a cluster-admin credential at apply time (bd babelstone-zla1.12.1). Instead, at Phase-2 bootstrap
you apply the scoped deploy RBAC
([`../k8s/overlays/staging/bootstrap/cd-deploy-rbac.yaml`](../k8s/overlays/staging/bootstrap/cd-deploy-rbac.yaml))
and mint the least-privilege `cd-deployer` kubeconfig with
[`scripts/cd-kubeconfig.sh`](../../scripts/cd-kubeconfig.sh) — THAT is what goes into the
`KUBECONFIG_B64` GitHub environment secret.

## Hand-off — what comes next

1. **Phase 2 — cluster bootstrap** ([`../k8s/overlays/staging/bootstrap/`](../k8s/overlays/staging/bootstrap/)):
   install cert-manager + the CSI snapshot controller + the system-upgrade-controller, then
   `kubectl apply -f` the issuers / `VolumeSnapshotClass` / k3s upgrade Plan. Point the
   `babelstone.dev` DNS A records at the new node IP.
2. **Phase 3 — deploy** the workloads: `kubectl apply -k ../k8s/overlays/staging` (or dispatch
   `cd.yml`).

The full operator runbook — bring-up, redeploy, restore, upgrade, backups — is
[`../runbooks/staging-ops.md`](../runbooks/staging-ops.md) (its §1.1 cross-references this file).

## Control-plane hardening (bd babelstone-zla1.12.9 — applies at NEXT provision)

`cluster.yaml` now carries two CIS-aligned settings that only take effect when the cluster is
(re-)provisioned — they cannot retro-fit the running box:

- **kube-apiserver audit logging** (`kube_api_server_args` + the audit policy written by
  `additional_pre_k3s_commands` before k3s starts). The log lands on the node at
  `/var/lib/rancher/k3s/server/logs/audit.log` (30 days retained on-box). **Maintainer
  follow-up: ship it off-box** — an on-box audit log dies with the box. Point a log shipper
  (e.g. Grafana Alloy / promtail tailing that path) at the LGTM appliance's Loki so the trail
  survives node loss and is tamper-evident off the host.
- **etcd secrets-at-rest encryption** (`secrets-encryption: true` via a k3s
  `config.yaml.d` drop-in). After provisioning, verify on the node with
  `k3s secrets-encrypt status`, and rotate the key periodically with
  `k3s secrets-encrypt rotate-keys` (k3s re-encrypts existing Secrets). Without this, every
  Secret (the in-cluster Hetzner token, kubeconfigs, the OpenBao token, backup keys) is
  base64-plaintext to anyone with disk or snapshot access.

To pick these up on the EXISTING staging box either re-provision (restore from backups per
`../runbooks/staging-ops.md`), or apply the equivalent by hand on the node (write the two
files, add the apiserver args to the k3s config, restart k3s) — the committed config remains
the source of truth either way.

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
  (self-hosted runner or tunnel) for a real tier. A **cheap partial mitigation** is now
  documented inline in `cluster.yaml`'s `allowed_networks.api` section (commented option):
  allow-list your operator `/32` plus the GitHub Actions egress ranges from
  `https://api.github.com/meta` (`.actions[]`) — read its caveats (list size vs firewall rule
  limits, rotation breaking `cd.yml`, and it admitting any Actions tenant) before flipping it.
- **The public web ports (80/443) are provisioned out-of-band** (bd babelstone-zla1.14).
  hetzner-k3s synthesises the Hetzner Cloud Firewall from `cluster.yaml`'s `allowed_networks`,
  which only expresses the `ssh` and `api` lists — there is no knob for 80/443 there. So the
  inbound web rule is added by [`firewall-web.sh`](./firewall-web.sh), which fetches
  Cloudflare's published ranges and scopes 80/443 to them (only the Cloudflare proxy reaches
  the origin — the node IP is not directly scannable). Same rotation caveat as the Actions
  ranges above: re-run it after Cloudflare changes its published list, or a stale rule silently
  drops traffic. Pairs with the Traefik ingress controller
  ([`../k8s/overlays/staging/bootstrap/helm/traefik-values.yaml`](../k8s/overlays/staging/bootstrap/helm/traefik-values.yaml)),
  which binds those ports on the node — hetzner-k3s disables the bundled Traefik + servicelb, so
  both the controller and the firewall rule are required for `https://*.babelstone.dev` to answer.
