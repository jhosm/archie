package pack

import (
	"os"
	"path/filepath"
	"testing"
)

// FuzzLoad feeds arbitrary bytes to the pack loader (ADR-PC-007 §169: the
// engine refuses unparseable packs). pack-validate is the highest-churn,
// non-engineer-authored surface, so a malformed/garbage pack file MUST yield a
// CLEAN rejection — a returned error — and NEVER a panic.
//
// pack.Load reads a *directory* of YAML files. The fuzzer mutates one file at a
// time, holding the rest at their known-good content, so a single corpus entry
// exercises every loader entrypoint (manifest, key-set catalogues, the
// reporting-hook / parameter / rate-sheet-ref struct decodes) against the same
// garbage. The query accessors are then called on whatever (possibly nil-field)
// Pack a successful Load returns — they must not panic either.
func FuzzLoad(f *testing.F) {
	// Seed the corpus from the committed pack source (the same data packs/pack.sh
	// validates) plus a handful of hand-picked malformations.
	for _, seed := range seedCorpus() {
		f.Add(seed)
	}

	// fileNames are the loader's required inputs, in the layout pack.Load walks.
	fileNames := []string{
		"pack.yaml",
		filepath.Join("primitives", "day-count.yaml"),
		filepath.Join("primitives", "withholding.yaml"),
		filepath.Join("primitives", "reporting.yaml"),
		filepath.Join("parameters", "constants.yaml"),
		filepath.Join("rate-sheet-refs", "deposits-pt.yaml"),
	}

	f.Fuzz(func(t *testing.T, data []byte) {
		// Each iteration mutates exactly one of the required files; the rest stay
		// known-good so the garbage is exercised in isolation at every entrypoint.
		for _, target := range fileNames {
			dir := writeGoldenPack(t)
			path := filepath.Join(dir, target)
			if err := os.WriteFile(path, data, 0o600); err != nil {
				t.Fatalf("seeding fuzz input into %s: %v", target, err)
			}

			// The contract: Load either returns a usable *Pack and nil error, or a
			// non-nil error. It must NEVER panic and must NEVER return (nil, nil).
			p, err := Load(dir)
			if err == nil {
				if p == nil {
					t.Fatalf("Load(%s mutated) returned (nil, nil)", target)
				}
				// A clean load must survive the query accessors on arbitrary data.
				_ = p.HasRateSheetFor("term_deposit")
				_ = p.ActiveReportingHooks()
			}
		}
	})
}

// goldenPack is the known-good content for each required file, captured inline
// so the fuzzer does not depend on the on-disk packs/ tree (and so each
// iteration starts from a clean, valid baseline).
var goldenPack = map[string]string{
	"pack.yaml": `pack_id: pt
pack_version: "2026.1"
namespace: pt
manifest_schema_version: 1
schema_pins:
  term_deposit: term_deposit@2026.1
`,
	filepath.Join("primitives", "day-count.yaml"): `act_360:
  formula_ref: engine.day_count.actual_360
act_365:
  formula_ref: engine.day_count.actual_365
`,
	filepath.Join("primitives", "withholding.yaml"): `irs_resident_individual:
  rate_bps: 2800
`,
	filepath.Join("primitives", "reporting.yaml"): `bdp_estatisticas_taxas_juro:
  active: true
  frequency: monthly
  regulator: banco_de_portugal
`,
	filepath.Join("parameters", "constants.yaml"): `max_consumer_rate_bps: 2000
auto_renewal_optout_window_days: 14
`,
	filepath.Join("rate-sheet-refs", "deposits-pt.yaml"): `refs:
  - product_family: term_deposit
    rate_sheet_version_id: pt-deposits-2026.1
`,
}

// writeGoldenPack materialises a fresh, valid pack directory under t.TempDir()
// and returns its path. The caller overwrites one file with the fuzz input.
func writeGoldenPack(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	for name, content := range goldenPack {
		path := filepath.Join(dir, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatalf("mkdir %s: %v", filepath.Dir(path), err)
		}
		if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
			t.Fatalf("write %s: %v", path, err)
		}
	}
	return dir
}

// seedCorpus returns the fuzz seeds: each known-good file body, plus a set of
// hand-picked malformations that historically break YAML/CUE front-ends.
func seedCorpus() [][]byte {
	var out [][]byte
	for _, content := range goldenPack {
		out = append(out, []byte(content))
	}
	mal := []string{
		"",                               // empty
		"\x00\x01\x02\x03",               // raw binary
		"{",                              // unterminated flow map
		"[",                              // unterminated flow seq
		":\n:\n:\n",                      // bare colons
		"a: &a [*a]",                     // YAML alias cycle (billion-laughs shape)
		"!!binary not-base64",            // bad tag payload
		"- - - - - - - - - -",            // deep nesting
		"\ufeffpack_id: pt",              // BOM prefix
		"pack_id: pt\npack_id: pt2",      // duplicate key
		"key: !!str 123\n\t- tab indent", // tab indentation (illegal in YAML)
	}
	for _, m := range mal {
		out = append(out, []byte(m))
	}
	return out
}
