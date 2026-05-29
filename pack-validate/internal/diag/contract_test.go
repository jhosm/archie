package diag

import (
	"bytes"
	"encoding/json"
	"sort"
	"testing"
	"time"
)

// TestContractShape pins the versioned JSON diagnostic contract (ADR-PC-006 §P2,
// Open Action #3 — "a CI test asserting the .NET engine deserialises the Go
// validator's output shape; the contract is versioned"). It locks the exact key
// set at each level so a field rename/removal that would break the engine's
// deserialiser fails here first. Adding a field is forward-compatible; removing
// or renaming one requires a ContractVersion bump.
func TestContractShape(t *testing.T) {
	rep := NewReport("dpz_pt_demo", "pt.2026.1")
	rep.RecordDepth(DepthSyntactic, 4*time.Millisecond, nil)
	rep.RecordDepth(DepthType, 0, []Diagnostic{{
		Depth: DepthType, Path: "day_count", Kind: KindUnknownPrimitive,
		Message: "…", Pos: "variant.yaml:7:12",
	}})

	var buf bytes.Buffer
	if err := rep.WriteJSON(&buf); err != nil {
		t.Fatalf("WriteJSON: %v", err)
	}

	var top map[string]json.RawMessage
	if err := json.Unmarshal(buf.Bytes(), &top); err != nil {
		t.Fatalf("contract is not valid JSON: %v", err)
	}
	assertKeys(t, "report", top, []string{
		"contract_version", "variant", "pack", "ok", "depths", "diagnostics",
	})

	if v := unquote(t, top["contract_version"]); v != ContractVersion {
		t.Errorf("contract_version = %q, want %q", v, ContractVersion)
	}

	var depths []map[string]json.RawMessage
	mustUnmarshal(t, top["depths"], &depths)
	if len(depths) != 2 {
		t.Fatalf("expected 2 depth results, got %d", len(depths))
	}
	assertKeys(t, "depth", depths[0], []string{
		"depth", "name", "budget_ms", "elapsed_ms", "ok", "over_budget",
	})

	var diags []map[string]json.RawMessage
	mustUnmarshal(t, top["diagnostics"], &diags)
	if len(diags) != 1 {
		t.Fatalf("expected 1 diagnostic, got %d", len(diags))
	}
	// pos is omitempty, but present here — the full per-diagnostic shape from
	// ADR-PC-006 §P2 is {depth, path, kind, message, pos}.
	assertKeys(t, "diagnostic", diags[0], []string{
		"depth", "path", "kind", "message", "pos",
	})
}

// TestDiagnosticOmitsEmptyPos — pos is omitted when unknown (a malformed-YAML
// diagnostic has no position), keeping the contract clean for consumers.
func TestDiagnosticOmitsEmptyPos(t *testing.T) {
	b, err := json.Marshal(Diagnostic{Depth: DepthSyntactic, Kind: KindMalformed, Message: "x"})
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]json.RawMessage
	mustUnmarshal(t, b, &m)
	if _, ok := m["pos"]; ok {
		t.Errorf("empty pos should be omitted, got %s", b)
	}
}

func assertKeys(t *testing.T, what string, m map[string]json.RawMessage, want []string) {
	t.Helper()
	got := make([]string, 0, len(m))
	for k := range m {
		got = append(got, k)
	}
	sort.Strings(got)
	sort.Strings(want)
	if len(got) != len(want) {
		t.Fatalf("%s keys = %v, want %v", what, got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("%s keys = %v, want %v", what, got, want)
		}
	}
}

func unquote(t *testing.T, raw json.RawMessage) string {
	t.Helper()
	var s string
	mustUnmarshal(t, raw, &s)
	return s
}

func mustUnmarshal(t *testing.T, raw json.RawMessage, v any) {
	t.Helper()
	if err := json.Unmarshal(raw, v); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
}
