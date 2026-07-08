package validate

import (
	"path/filepath"
	"testing"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
)

// The current_account fixtures exercise the THIRD product family — current_account — end to end
// through the binary, and specifically pin the depth-3 rate-sheet gate (depths.go): the requirement
// that "the pack can price the family" applies only to a variant that carries a `rate:` block to
// resolve at constitution. A demand account declares no `rate:` at all (its overdraft-interest rate is
// resolved separately, not from a variant rate_ref), so it must validate against a pack that carries
// NO rate-sheet ref for it — otherwise every rate-less family would be un-shippable.
//
// Paths are relative to this package dir; caPtStandard / caPtBasic are the committed runtime configs.
const (
	caPtStandard = "../../../product-configs/current-account/ca_pt_standard.yaml"
	caPtBasic    = "../../../product-configs/current-account/ca_pt_basic.yaml"
)

// TestCurrentAccountVariantsPassAllDepthsWithoutRateSheetRef — the real committed current-account
// product-configs conform through all four depths against the REAL pt.2026.1 pack, which carries no
// current_account rate-sheet ref. This is the depth-3 gate fix: a rate-less variant prices nothing at
// constitution, so the "pack can price the family" obligation does not apply to it.
func TestCurrentAccountVariantsPassAllDepthsWithoutRateSheetRef(t *testing.T) {
	for _, variant := range []string{caPtStandard, caPtBasic} {
		t.Run(filepath.Base(variant), func(t *testing.T) {
			rep, err := Run(Options{
				VariantPath: variant,
				SchemaDir:   schemaDir, PackDir: packDir, MaxDepth: diag.DepthRegulatory,
			})
			if err != nil {
				t.Fatalf("toolchain error: %v", err)
			}
			if !rep.OK {
				t.Fatalf("expected OK, got diagnostics: %+v", rep.Diagnostics)
			}
			if len(rep.Depths) != 4 {
				t.Fatalf("expected 4 depth results, got %d", len(rep.Depths))
			}
		})
	}
}

// TestRateGateFiresOnlyForRateBearingVariants pins BOTH directions of the depth-3 rate gate against a
// pack stripped of every rate-sheet ref: a rate-BEARING term-deposit variant still fails
// unresolved_rate_ref (the gate is preserved, not removed), while a rate-LESS current_account variant
// passes (the gate correctly stays silent). One pack proves the gate keys on the variant's rate block.
func TestRateGateFiresOnlyForRateBearingVariants(t *testing.T) {
	pk := clonePackWithoutRateSheets(t)

	t.Run("rate-bearing term-deposit (rate.flat) still fails unresolved_rate_ref", func(t *testing.T) {
		rep, err := Run(Options{
			VariantPath: "../../../product-configs/dpz_pt_12m_juros_venc.yaml",
			SchemaDir:   schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
		})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if rep.OK {
			t.Fatalf("expected the rate-bearing deposit variant to fail without a rate-sheet ref, got OK")
		}
		if !hasKind(rep.Diagnostics, diag.KindUnresolvedRateRef) {
			t.Errorf("expected an unresolved_rate_ref diagnostic, got %+v", rep.Diagnostics)
		}
	})

	// personal-loan carries `rate: #FixedRate` = { fixed: { rate_ref } } — the inner `fixed` shape the
	// typed variantData.Rate struct does NOT model. This arm pins that the presence-based HasRate gate
	// still catches it (a shape allow-list of flat/stepped would silently exempt a genuinely-priced loan).
	t.Run("rate-bearing personal-loan (rate.fixed) still fails unresolved_rate_ref", func(t *testing.T) {
		rep, err := Run(Options{
			VariantPath: "../../testdata/personal-loan/valid/general-36m.yaml",
			SchemaDir:   schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
		})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if rep.OK {
			t.Fatalf("expected the rate-bearing loan variant to fail without a rate-sheet ref, got OK")
		}
		if !hasKind(rep.Diagnostics, diag.KindUnresolvedRateRef) {
			t.Errorf("expected an unresolved_rate_ref diagnostic, got %+v", rep.Diagnostics)
		}
	})

	t.Run("rate-less current-account passes", func(t *testing.T) {
		rep, err := Run(Options{
			VariantPath: caPtStandard,
			SchemaDir:   schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
		})
		if err != nil {
			t.Fatalf("toolchain error: %v", err)
		}
		if !rep.OK {
			t.Fatalf("expected the rate-less current-account variant to pass, got: %+v", rep.Diagnostics)
		}
	})
}

// TestCurrentAccountIsDiscovered pins the file-discovery resolver picks up current_account from its
// families/current-account.cue with the kebab→snake name and kebab→Pascal root def — no registry edit.
func TestCurrentAccountIsDiscovered(t *testing.T) {
	fams, err := DiscoverFamilies(schemaDir)
	if err != nil {
		t.Fatalf("discovery error: %v", err)
	}
	f, ok := fams["current_account"]
	if !ok {
		t.Fatalf("family current_account not discovered (got: %v)", fams)
	}
	if f.RootDef != "#CurrentAccount" {
		t.Errorf("current_account root def = %q, want %q", f.RootDef, "#CurrentAccount")
	}
}

// clonePackWithoutRateSheets copies the real pt.2026.1 pack into a temp dir carrying NO rate-sheet ref,
// so HasRateSheetFor returns false for every family. pack.Load reads the rate-sheet-refs/ directory
// directly (not the manifest list), so the directory must exist but resolve to an empty refs set — an
// empty `refs:` list on the manifest-named deposits-pt.yaml does exactly that. The pack KEY is preserved
// (pt.2026.1) so the fixtures' `pack: pt.2026.1` pin still matches. Only the files pack.Load reads are copied.
func clonePackWithoutRateSheets(t *testing.T) string {
	t.Helper()
	dst := t.TempDir()

	for _, rel := range []string{
		"pack.yaml",
		"primitives/day-count.yaml",
		"primitives/withholding.yaml",
		"primitives/reporting.yaml",
		"primitives/renewal-policies.yaml",
		"parameters/constants.yaml",
	} {
		copyPackFile(t, dst, rel)
	}

	writePackFile(t, dst, "rate-sheet-refs/deposits-pt.yaml", []byte("refs: []\n"))

	return dst
}
