package validate

import (
	"path/filepath"
	"testing"
	"time"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
)

// TestPackValidateDepthBudgets is the PACK_VALIDATE_DEPTH_BUDGETS fitness
// function (commitment-catalogue row 10; ADR-PC-006 §P3). It asserts that a
// full depths-1→4 run of each accept fixture meets every per-depth budget
// (syntactic < 1 s, type < 5 s, pack-compliance < 10 s, regulatory < 10 s) and
// the < 30 s aggregate ceiling.
//
// The depth budgets live in diag.Depth.Budget(); this test reads the validator's
// own per-depth timing rather than re-measuring, so it gates the same numbers
// the JSON contract reports.
func TestPackValidateDepthBudgets(t *testing.T) {
	valid := []string{"flat-at-maturity.yaml", "stepped-periodic.yaml", "advance-new-money.yaml"}
	for _, name := range valid {
		t.Run(name, func(t *testing.T) {
			rep, err := Run(opts(filepath.Join(cueValidDir, name), diag.DepthRegulatory))
			if err != nil {
				t.Fatalf("toolchain error: %v", err)
			}
			var aggregate time.Duration
			for _, d := range rep.Depths {
				elapsed := time.Duration(d.ElapsedMs) * time.Millisecond
				aggregate += elapsed
				if budget := diag.Depth(d.Depth).Budget(); elapsed > budget {
					t.Errorf("depth %d (%s) over budget: %v > %v", d.Depth, d.Name, elapsed, budget)
				}
			}
			if aggregate > diag.AggregateBudget {
				t.Errorf("aggregate over budget: %v > %v", aggregate, diag.AggregateBudget)
			}
		})
	}
}
