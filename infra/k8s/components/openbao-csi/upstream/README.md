# Vendored upstream — Secrets Store CSI driver

These files are **verbatim, unmodified** copies of the upstream
[`kubernetes-sigs/secrets-store-csi-driver`](https://github.com/kubernetes-sigs/secrets-store-csi-driver)
`deploy/` manifests, **pinned to tag `v1.6.0`**. They are vendored (not fetched by
a remote kustomize reference) so `kustomize build ..` is hermetic/offline and
every applied line is reviewable in-tree.

Keep them **byte-identical to upstream** — do not hand-edit. To bump the pin,
re-vendor with the loop in [`../README.md`](../README.md) ("Bumping the vendored
driver") against the new tag, then re-run `kustomize build`.

| File | Upstream `deploy/` source | Kinds |
|---|---|---|
| `secrets-store.csi.x-k8s.io_secretproviderclasses.yaml` | same name | `CustomResourceDefinition` (SecretProviderClass) |
| `secrets-store.csi.x-k8s.io_secretproviderclasspodstatuses.yaml` | same name | `CustomResourceDefinition` (SecretProviderClassPodStatus) |
| `rbac-secretproviderclass.yaml` | same name | `ServiceAccount`, `ClusterRole`, `ClusterRoleBinding` |
| `rbac-secretprovidersyncing.yaml` | same name | `ClusterRole`, `ClusterRoleBinding` |
| `csidriver.yaml` | same name | `CSIDriver` |
| `secrets-store-csi-driver.yaml` | same name | `DaemonSet` (node plugin, `kube-system`) |

Pinned images (in `secrets-store-csi-driver.yaml`):
`registry.k8s.io/csi-secrets-store/driver:v1.6.0`,
`registry.k8s.io/sig-storage/csi-node-driver-registrar:v2.16.0`,
`registry.k8s.io/sig-storage/livenessprobe:v2.18.0`.
