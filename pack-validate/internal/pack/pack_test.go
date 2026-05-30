package pack

import "testing"

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

	// The catalogue carries all four conventions (depth-2 membership).
	for _, key := range []string{"act_360", "act_365", "act_act_isda", "30_360_european"} {
		if !p.HasDayCount(key) {
			t.Errorf("expected pack to carry day-count %q", key)
		}
	}

	// act_360 is the only one regulatorily permitted for term_deposit.
	if !p.PermitsDayCountFor("act_360", "term_deposit") {
		t.Errorf("act_360 must be permitted for term_deposit")
	}
	for _, key := range []string{"act_365", "act_act_isda", "30_360_european"} {
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
