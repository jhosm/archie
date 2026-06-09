package pack

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const ptPackDir = "../../../packs/pt.2026.1"

// TestLoadDayCountPermittedFor proves the regulatory permitted-set is read from
// the pack data (primitives/day-count.yaml `permitted_for`), not a hardcoded Go
// map. The pt.2026.1 pack declares act_360 permitted for term_deposit and the
// other conventions permitted for nothing (02 §2.2).
func TestLoadDayCountPermittedFor(t *testing.T) {
	p, err := Load(ptPackDir)
	if err != nil {
		t.Fatalf("load pt.2026.1: %v", err)
	}

	// The catalogue carries all three conventions (depth-2 membership).
	for _, key := range []string{"act_360", "act_365", "30_360_european"} {
		if !p.HasDayCount(key) {
			t.Errorf("expected pack to carry day-count %q", key)
		}
	}

	// act_360 is the only one regulatorily permitted for term_deposit.
	if !p.PermitsDayCountFor("act_360", "term_deposit") {
		t.Errorf("act_360 must be permitted for term_deposit")
	}
	for _, key := range []string{"act_365", "30_360_european"} {
		if p.PermitsDayCountFor(key, "term_deposit") {
			t.Errorf("%q must NOT be permitted for term_deposit", key)
		}
	}

	// A day-count the pack does not carry is not permitted (and not in the set).
	if p.PermitsDayCountFor("act_999", "term_deposit") {
		t.Errorf("an absent day-count must not be permitted")
	}

	// The permitted-set query is sorted and exact.
	got := p.PermittedDayCountsFor("term_deposit")
	if len(got) != 1 || got[0] != "act_360" {
		t.Errorf("PermittedDayCountsFor(term_deposit) = %v, want [act_360]", got)
	}
}

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
	if !p.HasDayCount("act_360") {
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
