package validate

import (
	"path/filepath"
	"testing"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
)

// The validator reuses C.1's schema fixtures (the canonical accept/reject set,
// schema-only) plus this module's pack-aware fixtures (depths 2–4, which need a
// pinned pack). Paths are relative to this package dir.
const (
	schemaDir      = "../../../contracts/cue"
	packDir        = "../../../packs/pt.2026.1"
	cueValidDir    = "../../../contracts/cue/testdata/term-deposit/valid"
	cueInvalidDir  = "../../../contracts/cue/testdata/term-deposit/invalid"
	packInvalidDir = "../../testdata/term-deposit/invalid"
)

func opts(variant string, depth diag.Depth) Options {
	return Options{VariantPath: variant, SchemaDir: schemaDir, PackDir: packDir, MaxDepth: depth}
}

// TestValidFixturesPassAllDepths — every accept fixture conforms through all
// four depths, with no diagnostics and no depth over budget.
func TestValidFixturesPassAllDepths(t *testing.T) {
	valid := []string{"flat-at-maturity.yaml", "stepped-periodic.yaml", "advance-new-money.yaml"}
	for _, name := range valid {
		t.Run(name, func(t *testing.T) {
			rep, err := Run(opts(filepath.Join(cueValidDir, name), diag.DepthRegulatory))
			if err != nil {
				t.Fatalf("toolchain error: %v", err)
			}
			if !rep.OK {
				t.Fatalf("expected OK, got diagnostics: %+v", rep.Diagnostics)
			}
			if len(rep.Depths) != 4 {
				t.Fatalf("expected 4 depth results, got %d", len(rep.Depths))
			}
			for _, d := range rep.Depths {
				if !d.OK {
					t.Errorf("depth %d not ok", d.Depth)
				}
				if d.OverBudget {
					t.Errorf("depth %d over budget: %dms > %dms", d.Depth, d.ElapsedMs, d.BudgetMs)
				}
			}
		})
	}
}

// rejectCase pins an invalid fixture to the depth and kind it must be rejected
// at — the real spec for the depth classifier (ADR-PC-006 §P2; authoring §5).
type rejectCase struct {
	dir   string
	file  string
	depth diag.Depth
	kind  string
}

func TestInvalidFixturesRejectAtExpectedDepth(t *testing.T) {
	cases := []rejectCase{
		// schema-only (C.1) fixtures — depths 1–2
		{cueInvalidDir, "unknown-field.yaml", diag.DepthSyntactic, diag.KindUnknownField},
		{cueInvalidDir, "both-rate-shapes.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{cueInvalidDir, "rate-neither-shape.yaml", diag.DepthSyntactic, diag.KindMissingField},
		{cueInvalidDir, "early-termination-both-shapes.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{cueInvalidDir, "early-termination-neither.yaml", diag.DepthSyntactic, diag.KindMissingField},
		{cueInvalidDir, "periodic-missing-period.yaml", diag.DepthSyntactic, diag.KindMissingField},
		{cueInvalidDir, "periodic-semiannual.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{cueInvalidDir, "period-without-periodic.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{cueInvalidDir, "rateref-missing-role.yaml", diag.DepthSyntactic, diag.KindMissingField},
		{cueInvalidDir, "malformed-version-key.yaml", diag.DepthSyntactic, diag.KindShapeMismatch},
		{cueInvalidDir, "unbound-day-count.yaml", diag.DepthSyntactic, diag.KindShapeMismatch},
		{cueInvalidDir, "band-up-to-days-zero.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{cueInvalidDir, "penalty-out-of-range.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{cueInvalidDir, "non-eur-currency.yaml", diag.DepthType, diag.KindTypeMismatch},
		{cueInvalidDir, "principal-max-below-min.yaml", diag.DepthType, diag.KindOutOfRange},
		// pack-aware fixtures — depths 2–4
		{packInvalidDir, "depth2-unknown-primitive.yaml", diag.DepthType, diag.KindUnknownPrimitive},
		{packInvalidDir, "depth3-wrong-pack.yaml", diag.DepthPackCompliance, diag.KindPackBoundViolation},
		{packInvalidDir, "depth4-act365-deposit.yaml", diag.DepthRegulatory, diag.KindForbiddenDayCount},
		{packInvalidDir, "depth4-descending-steps.yaml", diag.DepthRegulatory, diag.KindNonAscendingSteps},
		{packInvalidDir, "depth4-open-tail-not-last.yaml", diag.DepthRegulatory, diag.KindOpenTailNotLast},
	}
	for _, tc := range cases {
		t.Run(tc.file, func(t *testing.T) {
			rep, err := Run(opts(filepath.Join(tc.dir, tc.file), diag.DepthRegulatory))
			if err != nil {
				t.Fatalf("toolchain error: %v", err)
			}
			if rep.OK {
				t.Fatalf("expected rejection, got OK")
			}
			// The first diagnostic must be at the expected depth (layered: the
			// run short-circuits at the first failing depth, so all diagnostics
			// share that depth).
			d0 := rep.Diagnostics[0]
			if d0.Depth != tc.depth {
				t.Errorf("rejected at depth %d, expected %d (%s)", d0.Depth, tc.depth, tc.kind)
			}
			if !hasKind(rep.Diagnostics, tc.kind) {
				t.Errorf("expected a %q diagnostic, got %+v", tc.kind, rep.Diagnostics)
			}
			// Layered short-circuit: the last recorded depth is the failing one;
			// no later depth ran.
			last := rep.Depths[len(rep.Depths)-1]
			if last.Depth != tc.depth {
				t.Errorf("last depth run was %d, expected short-circuit at %d", last.Depth, tc.depth)
			}
		})
	}
}

// TestLayeredShortCircuit — a --max-depth below the failing depth reports OK
// (the deeper problem is not reached), confirming depths are layered.
func TestLayeredShortCircuit(t *testing.T) {
	// depth4-act365-deposit fails only at depth 4; depths 1–3 pass.
	v := filepath.Join(packInvalidDir, "depth4-act365-deposit.yaml")
	for _, d := range []diag.Depth{diag.DepthSyntactic, diag.DepthType, diag.DepthPackCompliance} {
		rep, err := Run(opts(v, d))
		if err != nil {
			t.Fatalf("depth %d: %v", d, err)
		}
		if !rep.OK {
			t.Errorf("depth %d: expected OK (failure is at depth 4), got %+v", d, rep.Diagnostics)
		}
	}
	rep, _ := Run(opts(v, diag.DepthRegulatory))
	if rep.OK {
		t.Errorf("depth 4: expected rejection")
	}
}

// TestSyntacticNeedsNoPack — depth 1 runs without a pack (the fast pre-commit
// path; ADR-PC-006 S1 "the author's pre-commit hook").
func TestSyntacticNeedsNoPack(t *testing.T) {
	rep, err := Run(Options{
		VariantPath: filepath.Join(cueValidDir, "flat-at-maturity.yaml"),
		SchemaDir:   schemaDir,
		MaxDepth:    diag.DepthSyntactic,
	})
	if err != nil {
		t.Fatalf("toolchain error: %v", err)
	}
	if !rep.OK || len(rep.Depths) != 1 {
		t.Fatalf("expected OK with one depth, got ok=%v depths=%d", rep.OK, len(rep.Depths))
	}
}

func hasKind(ds []diag.Diagnostic, kind string) bool {
	for _, d := range ds {
		if d.Kind == kind {
			return true
		}
	}
	return false
}
