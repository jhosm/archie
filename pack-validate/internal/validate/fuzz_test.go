package validate

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
)

// FuzzRun feeds arbitrary bytes as a variant document to the full validator
// pipeline (ADR-PC-007 §169: the engine refuses unparseable input). A
// malformed/garbage variant MUST yield a CLEAN result — either a toolchain
// error or a diagnostic report — and NEVER a panic.
//
// The pipeline is exercised at every depth (1→4) against the real committed
// family schema and pinned pack, so the garbage flows through peekVariant, the
// CUE load+unify pass, the variant decode, and the Go-side depth-3/4 checks.
// Run's contract: for a non-conformant *variant* it returns (report, nil) with
// report.OK == false; a *toolchain* failure is (nil, error). It must never
// panic and never return (nil, nil).
func FuzzRun(f *testing.F) {
	for _, seed := range variantSeeds(f) {
		f.Add(seed)
	}

	depths := []diag.Depth{
		diag.DepthSyntactic,
		diag.DepthType,
		diag.DepthPackCompliance,
		diag.DepthRegulatory,
	}

	f.Fuzz(func(t *testing.T, data []byte) {
		dir := t.TempDir()
		variantPath := filepath.Join(dir, "variant.yaml")
		if err := os.WriteFile(variantPath, data, 0o600); err != nil {
			t.Fatalf("writing fuzz variant: %v", err)
		}

		for _, d := range depths {
			rep, err := Run(Options{
				VariantPath: variantPath,
				SchemaDir:   schemaDir,
				PackDir:     packDir,
				MaxDepth:    d,
			})
			// A toolchain error is an acceptable clean rejection of garbage; what
			// matters is that we never panicked and never got (nil, nil).
			if err == nil && rep == nil {
				t.Fatalf("depth %d: Run returned (nil, nil) for fuzz input", d)
			}
		}
	})
}

// variantSeeds collects the committed accept/reject fixtures the test suite
// already pins, plus a few raw malformations, as the fuzz corpus seed.
func variantSeeds(f *testing.F) [][]byte {
	f.Helper()
	var out [][]byte
	dirs := []string{cueValidDir, cueInvalidDir, packInvalidDir}
	for _, dir := range dirs {
		entries, err := os.ReadDir(dir)
		if err != nil {
			continue // schema fixtures live alongside the repo; skip if absent
		}
		for _, e := range entries {
			if e.IsDir() || filepath.Ext(e.Name()) != ".yaml" {
				continue
			}
			b, err := os.ReadFile(filepath.Join(dir, e.Name()))
			if err != nil {
				continue
			}
			out = append(out, b)
		}
	}
	// Raw malformations that stress the YAML/CUE front-end.
	for _, m := range []string{
		"",
		"\x00\x01\x02",
		"{",
		"schema: term_deposit@2026.1\npack: pt.2026.1\n:\n",
		"a: &a [*a]",
		"- - - - - - -",
		"schema: term_deposit@2026.1\nday_count: pt.\n",
	} {
		out = append(out, []byte(m))
	}
	return out
}
