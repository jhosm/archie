package validate

import (
	"os"
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
	valid := []string{"flat-at-maturity.yaml", "stepped-periodic.yaml", "advance-new-money.yaml", "partial-withdrawal.yaml"}
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
		{cueInvalidDir, "partial-withdrawal-unknown-field.yaml", diag.DepthSyntactic, diag.KindUnknownField},
		// pack-aware fixtures — depths 2–4
		{packInvalidDir, "depth2-unknown-primitive.yaml", diag.DepthType, diag.KindUnknownPrimitive},
		{packInvalidDir, "depth3-wrong-pack.yaml", diag.DepthPackCompliance, diag.KindPackBoundViolation},
		{packInvalidDir, "depth4-act365-deposit.yaml", diag.DepthRegulatory, diag.KindForbiddenDayCount},
		{packInvalidDir, "depth4-descending-steps.yaml", diag.DepthRegulatory, diag.KindNonAscendingSteps},
		{packInvalidDir, "depth4-open-tail-not-last.yaml", diag.DepthRegulatory, diag.KindOpenTailNotLast},
		{packInvalidDir, "depth4-same-term-same-rate.yaml", diag.DepthRegulatory, diag.KindForbiddenRenewalPolicy},
		{packInvalidDir, "depth4-carencia-exceeds-term.yaml", diag.DepthRegulatory, diag.KindCarenciaExceedsTerm},
		{packInvalidDir, "depth4-remaining-exceeds-max.yaml", diag.DepthRegulatory, diag.KindRemainingExceedsMaxCents},
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

// clonePackWithDayCount copies the real pt.2026.1 pack into a temp dir and
// overwrites primitives/day-count.yaml with dayCountYAML. The validator reads
// the pack's data files, so this lets a test prove depth-4 reads the
// pack-declared permitted-set rather than a hardcoded Go map: change the data,
// change the verdict. pack.Load reads pack.yaml + primitives/{day-count,
// withholding,reporting} + parameters + rate-sheet-refs, so those are copied.
func clonePackWithDayCount(t *testing.T, dayCountYAML string) string {
	t.Helper()
	dst := t.TempDir()
	copyFile := func(rel string) {
		t.Helper()
		src := filepath.Join(packDir, rel)
		b, err := os.ReadFile(src)
		if err != nil {
			t.Fatalf("read %s: %v", rel, err)
		}
		out := filepath.Join(dst, rel)
		if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
			t.Fatalf("mkdir for %s: %v", rel, err)
		}
		if err := os.WriteFile(out, b, 0o644); err != nil {
			t.Fatalf("write %s: %v", rel, err)
		}
	}
	for _, rel := range []string{
		"pack.yaml",
		"primitives/withholding.yaml",
		"primitives/reporting.yaml",
		"parameters/constants.yaml",
		"rate-sheet-refs/deposits-pt.yaml",
	} {
		copyFile(rel)
	}
	dcPath := filepath.Join(dst, "primitives", "day-count.yaml")
	if err := os.MkdirAll(filepath.Dir(dcPath), 0o755); err != nil {
		t.Fatalf("mkdir primitives: %v", err)
	}
	if err := os.WriteFile(dcPath, []byte(dayCountYAML), 0o644); err != nil {
		t.Fatalf("write day-count.yaml: %v", err)
	}
	return dst
}

// The day-count catalogue keys a valid fixture (flat-at-maturity) binds
// pt.act_360. We vary only its permitted_for to drive depth-4 either way.
const (
	dayCountAct360PermittedForTermDeposit = `act_360:
  formula_ref: engine.day_count.actual_360
  permitted_for: [term_deposit]
act_365:
  formula_ref: engine.day_count.actual_365
  permitted_for: []
`
	dayCountAct360PermittedForNothing = `act_360:
  formula_ref: engine.day_count.actual_360
  permitted_for: []
act_365:
  formula_ref: engine.day_count.actual_365
  permitted_for: []
`
)

// TestDepth4ReadsPackDeclaredDayCounts proves the depth-4 regulatory permitted
// day-count check is driven by PACK DATA (primitives/day-count.yaml
// `permitted_for`), not a hardcoded validator map. A valid fixture binding
// pt.act_360 is accepted when the (cloned) pack declares act_360 permitted for
// term_deposit, and REJECTED at depth-4 (forbidden_day_count) when the same
// pack declares act_360 permitted for nothing — same variant, same binary,
// opposite verdict, driven only by the pack's declared data.
func TestDepth4ReadsPackDeclaredDayCounts(t *testing.T) {
	variant := filepath.Join(cueValidDir, "flat-at-maturity.yaml")

	t.Run("permitted_for term_deposit ⇒ accepted at depth 4", func(t *testing.T) {
		pk := clonePackWithDayCount(t, dayCountAct360PermittedForTermDeposit)
		rep, err := Run(Options{
			VariantPath: variant, SchemaDir: schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
		})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if !rep.OK {
			t.Fatalf("expected OK, got diagnostics: %+v", rep.Diagnostics)
		}
	})

	t.Run("permitted_for empty ⇒ rejected at depth 4", func(t *testing.T) {
		pk := clonePackWithDayCount(t, dayCountAct360PermittedForNothing)
		rep, err := Run(Options{
			VariantPath: variant, SchemaDir: schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
		})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if rep.OK {
			t.Fatalf("expected depth-4 rejection when pack permits nothing, got OK")
		}
		d0 := rep.Diagnostics[0]
		if d0.Depth != diag.DepthRegulatory {
			t.Errorf("rejected at depth %d, expected %d (regulatory)", d0.Depth, diag.DepthRegulatory)
		}
		if !hasKind(rep.Diagnostics, diag.KindForbiddenDayCount) {
			t.Errorf("expected a %q diagnostic, got %+v", diag.KindForbiddenDayCount, rep.Diagnostics)
		}
	})
}

// clonePackWithRenewalPolicies copies the real pt.2026.1 pack into a temp dir and writes
// primitives/renewal-policies.yaml with renewalYAML (the empty string ⇒ NO file, i.e. the
// pre-restriction fail-open pack). Lets a test prove the depth-4 SAME_TERM_SAME_RATE check
// is driven by PACK DATA (`permitted_for`), not a hardcoded map: change the data, change
// the verdict on the same variant + binary. Copies the same file set pack.Load reads.
func clonePackWithRenewalPolicies(t *testing.T, renewalYAML string) string {
	t.Helper()
	dst := t.TempDir()
	for _, rel := range []string{
		"pack.yaml",
		"primitives/day-count.yaml",
		"primitives/withholding.yaml",
		"primitives/reporting.yaml",
		"parameters/constants.yaml",
		"rate-sheet-refs/deposits-pt.yaml",
	} {
		src := filepath.Join(packDir, rel)
		b, err := os.ReadFile(src)
		if err != nil {
			t.Fatalf("read %s: %v", rel, err)
		}
		out := filepath.Join(dst, rel)
		if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
			t.Fatalf("mkdir for %s: %v", rel, err)
		}
		if err := os.WriteFile(out, b, 0o644); err != nil {
			t.Fatalf("write %s: %v", rel, err)
		}
	}
	if renewalYAML != "" {
		rpPath := filepath.Join(dst, "primitives", "renewal-policies.yaml")
		if err := os.WriteFile(rpPath, []byte(renewalYAML), 0o644); err != nil {
			t.Fatalf("write renewal-policies.yaml: %v", err)
		}
	}
	return dst
}

const (
	sameTermSameRatePermittedForTermDeposit = `same_term_same_rate:
  description: permitted for term deposits in this (hypothetical) pack
  permitted_for: [term_deposit]
`
	sameTermSameRatePermittedForNothing = `same_term_same_rate:
  description: pack-restricted, permitted for nobody
  permitted_for: []
`
)

// TestDepth4ReadsPackDeclaredRenewalPolicies proves the SAME_TERM_SAME_RATE restriction is
// driven by PACK DATA (primitives/renewal-policies.yaml `permitted_for`), not a hardcoded
// validator map — the data-driven mirror of the day-count test. The SAME variant declaring
// auto_renewal_policy: SAME_TERM_SAME_RATE is accepted when the (cloned) pack permits it for
// term_deposit, REJECTED at depth-4 (forbidden_renewal_policy) when permitted for nothing,
// and (the fail-open default) accepted when the pack carries no renewal-policies file at all.
func TestDepth4ReadsPackDeclaredRenewalPolicies(t *testing.T) {
	variant := filepath.Join(packInvalidDir, "depth4-same-term-same-rate.yaml")

	t.Run("permitted_for term_deposit ⇒ accepted at depth 4", func(t *testing.T) {
		pk := clonePackWithRenewalPolicies(t, sameTermSameRatePermittedForTermDeposit)
		rep, err := Run(Options{VariantPath: variant, SchemaDir: schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if !rep.OK {
			t.Fatalf("expected OK when the pack permits SAME_TERM_SAME_RATE, got diagnostics: %+v", rep.Diagnostics)
		}
	})

	t.Run("permitted_for empty ⇒ rejected at depth 4", func(t *testing.T) {
		pk := clonePackWithRenewalPolicies(t, sameTermSameRatePermittedForNothing)
		rep, err := Run(Options{VariantPath: variant, SchemaDir: schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if rep.OK {
			t.Fatalf("expected depth-4 rejection when the pack permits nothing, got OK")
		}
		if !hasKind(rep.Diagnostics, diag.KindForbiddenRenewalPolicy) {
			t.Errorf("expected a %q diagnostic, got %+v", diag.KindForbiddenRenewalPolicy, rep.Diagnostics)
		}
	})

	t.Run("no renewal-policies file ⇒ fail-open, accepted", func(t *testing.T) {
		// A pre-restriction pack (no renewal-policies.yaml) restricts no policy — the same
		// variant is accepted, so adding the file is the only thing that turns the gate on.
		pk := clonePackWithRenewalPolicies(t, "")
		rep, err := Run(Options{VariantPath: variant, SchemaDir: schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if !rep.OK {
			t.Fatalf("expected OK for a pre-restriction pack, got diagnostics: %+v", rep.Diagnostics)
		}
	})
}

func hasKind(ds []diag.Diagnostic, kind string) bool {
	for _, d := range ds {
		if d.Kind == kind {
			return true
		}
	}
	return false
}
