package pack

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// TestLoadRejectsOversizedFile pins the guard the fuzzer surfaced: CUE's
// parse+build cost is superlinear in node count, so an unbounded pack file is a
// denial-of-service vector. An oversized file must reject cleanly (returned
// error) and fast — never hang the loader (ADR-PC-007 §169).
func TestLoadRejectsOversizedFile(t *testing.T) {
	dir := writeTestPack(t)
	// Just over the 1 MiB cap, shaped as many mapping keys (the superlinear
	// shape). Without the cap this took the CUE front-end tens of seconds.
	oversized := strings.Repeat("k: 1\n", (maxPackFileBytes/5)+10)
	if err := os.WriteFile(filepath.Join(dir, "pack.yaml"), []byte(oversized), 0o600); err != nil {
		t.Fatal(err)
	}

	done := make(chan error, 1)
	start := time.Now()
	go func() {
		_, err := Load(dir)
		done <- err
	}()
	select {
	case err := <-done:
		if err == nil {
			t.Fatalf("expected oversized pack.yaml to be rejected, got nil error")
		}
		if !strings.Contains(err.Error(), "limit") {
			t.Fatalf("expected a size-limit rejection, got: %v", err)
		}
		if el := time.Since(start); el > time.Second {
			t.Errorf("rejection was slow (%v) — the cap should reject before CUE parses", el)
		}
	case <-time.After(10 * time.Second):
		t.Fatalf("Load hung on an oversized pack file (>10s) — the size cap is not bounding CUE")
	}
}

// TestLoadGoldenPackOK confirms the cap does not regress a legitimate pack:
// the in-memory known-good pack still loads and its accessors work.
func TestLoadGoldenPackOK(t *testing.T) {
	dir := writeTestPack(t)
	p, err := Load(dir)
	if err != nil {
		t.Fatalf("known-good pack failed to load: %v", err)
	}
	if !p.HasRateSheetFor("term_deposit") {
		t.Errorf("expected a rate-sheet ref for term_deposit")
	}
	if len(p.ActiveReportingHooks()) == 0 {
		t.Errorf("expected at least one active reporting hook")
	}
	if !p.DayCounts["act_360"] {
		t.Errorf("expected act_360 in the day-count catalogue")
	}
}

// writeTestPack materialises a minimal valid pack directory under t.TempDir().
func writeTestPack(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	files := map[string]string{
		"pack.yaml": `pack_id: pt
pack_version: "2026.1"
namespace: pt
manifest_schema_version: 1
schema_pins:
  term_deposit: term_deposit@2026.1
`,
		filepath.Join("primitives", "day-count.yaml"): `act_360:
  formula_ref: engine.day_count.actual_360
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
`,
		filepath.Join("rate-sheet-refs", "deposits-pt.yaml"): `refs:
  - product_family: term_deposit
    rate_sheet_version_id: pt-deposits-2026.1
`,
	}
	for name, content := range files {
		path := filepath.Join(dir, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	return dir
}
