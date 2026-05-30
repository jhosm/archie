# /pack-validate — the Go static binary embedding cuelang.org/go (ADR-PC-006).
# Runtime pinned to the Go version in mise.toml (1.26.x). A reproducible build
# from a pinned go.mod (ADR-PC-006 Open Action #2): CI stamps the binary's
# version so the engine can check it at pack-load (F2 mitigation). Static binary
# on distroless: no libc, minimal surface.
#
# The pack-validate binary needs the family schemas (contracts/cue) and the pack
# at runtime; this image carries only the validator. The engine mounts/bundles
# the digest-pinned schemas (ADR-PC-007 §P1) and passes --schema-dir/--pack.

FROM golang:1.26 AS build
WORKDIR /src
COPY . .
ENV CGO_ENABLED=0
# VERSION is the stamp the engine checks at boot (defaults to "dev").
ARG VERSION=dev
RUN go build -trimpath -ldflags "-X main.Version=${VERSION}" -o /pack-validate .

FROM gcr.io/distroless/static-debian12
COPY --from=build /pack-validate /pack-validate
ENTRYPOINT ["/pack-validate"]
