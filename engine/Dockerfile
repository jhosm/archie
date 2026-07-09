# Dockerfile — /engine (C# .NET 10 command/query host, Babelstone.Engine.Api).
# Runtime pinned by ADR-PC-010 (hand-rolled .NET 10 engine) + ADR-PC-001 (PostgreSQL).
#
# Build context is the repo root (the central Directory.Packages.props pins versions there,
# ManagePackageVersionsCentrally=true), so restore/publish target the host project explicitly —
# the same pattern orchestrator/Dockerfile uses. Build with:
#   docker build -f engine/Dockerfile -t babelstone-engine .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj
RUN dotnet publish engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj \
    -c Release -o /app --no-restore

# ── oci-tools stage: bring oras + cosign into the runtime (bd vrn4, ADR-PC-007 §P2/§P4) ──────────
# OCI pack mode (Engine:PackRegistry=oci) shells out to the `oras` and `cosign` CLIs at LOAD time
# to pull a regulatory pack by digest and cosign-verify its signature BEFORE trusting any content
# (OrasPackSource / CosignPackVerifier via OciPackStore). The chiseled runtime base below ships
# neither tool — nor a shell, curl, or tar — so this stage (which HAS a shell + curl + tar, from the
# SDK base already pulled for the build above) is where we fetch, CHECKSUM-VERIFY, and extract the
# two static binaries. They are then COPY'd into the chiseled runtime as plain executables on PATH.
#
# Both tools are pinned by VERSION and verified by SHA256 (ADR-PC-007 leans on cosign/oras for the
# pack pull+verify provenance contract — a tampered toolchain would undermine the very signature
# check it performs, so the toolchain itself is fetched fail-loud-on-mismatch). Versions match the
# dev/CI toolchain pinned in mise.toml (oras 1.3.2, cosign 3.1.1) so the in-container tools are the
# same the host integration tests + CI exercise. Bump in lockstep with mise.toml.
#
# The image-build.yml matrix builds linux/amd64 ONLY (staging is a Hetzner CPX42 x86 node; the
# arm64 leg was dropped — bd babelstone-zla1.13), so oras/cosign are fetched for amd64 against a
# fixed per-tool SHA256. If a multi-arch build is ever re-added, reintroduce a TARGETARCH switch
# selecting a per-arch checksum + `linux_<arch>` release asset (the arm64 leg image-build.yml would
# restore alongside it).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS oci-tools
ARG ORAS_VERSION=1.3.2
ARG COSIGN_VERSION=3.1.1
# SHA256s of the upstream amd64 release assets (oras_<v>_linux_amd64.tar.gz; cosign-linux-amd64).
ARG ORAS_SHA256=9229ccc6d17bb282039ad4a69abb16dcb887a5bce567c075d731d9b3c7ad8eaf
ARG COSIGN_SHA256=ae1ecd212663f3693ad9edf8b1a183900c9a52d3155ba6e354237f9a0f6463fc
WORKDIR /tools
RUN set -eux; \
    # oras: tarball → checksum-verify → extract just the `oras` binary.
    curl -fsSL -o oras.tar.gz \
      "https://github.com/oras-project/oras/releases/download/v${ORAS_VERSION}/oras_${ORAS_VERSION}_linux_amd64.tar.gz"; \
    echo "${ORAS_SHA256}  oras.tar.gz" | sha256sum -c -; \
    tar -xzf oras.tar.gz oras; \
    # cosign: single static binary → checksum-verify → mark executable.
    curl -fsSL -o cosign \
      "https://github.com/sigstore/cosign/releases/download/v${COSIGN_VERSION}/cosign-linux-amd64"; \
    echo "${COSIGN_SHA256}  cosign" | sha256sum -c -; \
    chmod +x oras cosign; \
    # Smoke-test that the verified binaries actually run before they ship.
    ./oras version; \
    ./cosign version

# Chiseled (distroless-style) runtime base: no shell / perl / ncurses / tar / full util-linux,
# which is where the base-image OS CVEs that the image-build.yml grype scan flags (ADR-IC-014 §S2)
# live. The `-extra` variant keeps tzdata + ICU/globalization — required for this engine's
# timezone- and culture-sensitive date/accrual math. Runs as non-root (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
# OCI-pack mode's toolchain (bd vrn4): the two verified static binaries land on PATH so the engine
# can pull + cosign-verify a signed pack in production (ADR-PC-007 §P3/§P4). These are statically
# linked Go binaries — no shell/libc-from-the-base needed — so they run in the chiseled image. This
# is the deliberate, BOUNDED addition the bd 4ow6 fail-fast guard deferred: two binaries, no shell,
# no package manager, so the base-OS CVE surface (ADR-IC-014 §S2) is unchanged. Disk mode never
# invokes them; they sit unused unless Engine:PackRegistry=oci.
COPY --from=oci-tools /tools/oras /usr/local/bin/oras
COPY --from=oci-tools /tools/cosign /usr/local/bin/cosign
ENTRYPOINT ["dotnet", "Babelstone.Engine.Api.dll"]
