package main

// Version is the build stamp the engine checks at pack-load to confirm the
// bundled validator matches the pinned expectation (ADR-PC-006 F2 mitigation,
// Open Action #2: "CI publishes the binary with a version digest the engine
// checks at boot"). Set at build time via the linker:
//
//	go build -ldflags "-X main.Version=$(git rev-parse --short HEAD)"
//
// It defaults to "dev" for a plain `go build`/`go run`.
var Version = "dev"
