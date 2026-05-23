# Skeleton Dockerfile — /pack-validate (Go static binary embedding cuelang.org/go).
# Runtime pinned by ADR-PC-006. Source (go.mod, main.go) lands with the
# pack-validate build task. Static binary on distroless: no libc, minimal surface.

FROM golang:1.23 AS build
WORKDIR /src
COPY . .
ENV CGO_ENABLED=0
RUN go build -o /pack-validate ./...

FROM gcr.io/distroless/static-debian12
COPY --from=build /pack-validate /pack-validate
ENTRYPOINT ["/pack-validate"]
