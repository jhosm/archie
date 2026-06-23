package validate

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
)

// The loan fixtures exercise the SECOND product family — personal_loan — end to
// end through the binary, proving the file-discovery family resolver (family.go)
// recognises a family the moment its families/*.cue lands, with no registry edit.
// Before this, the validator's hardcoded map carried only term_deposit, so a real
// loan variant was rejected unknown-family before any deeper check could run.
//
// Paths are relative to this package dir.
const (
	cueLoanValidDir   = "../../../contracts/cue/testdata/personal-loan/valid"
	cueLoanInvalidDir = "../../../contracts/cue/testdata/personal-loan/invalid"
	packLoanValidDir  = "../../testdata/personal-loan/valid"
	packLoanInvalid   = "../../testdata/personal-loan/invalid"
)

// clonePackDeclaringLoan copies the real pt.2026.1 pack into a temp dir and
// AUGMENTS it so it declares the personal_loan family: a schema pin and a
// rate-sheet ref the loan variant's rate_ref resolves against at constitution.
// The committed pt.2026.1 pack declares term_deposit only, so without this a loan
// variant fails depth-3 unresolved_rate_ref — the augmentation is the "a pack
// DECLARING the loan validates" half of the acceptance criteria. The pack KEY is
// preserved (pt.2026.1) so the loan fixtures' `pack: pt.2026.1` pin still matches.
func clonePackDeclaringLoan(t *testing.T) string {
	t.Helper()
	dst := t.TempDir()

	// Copy the pack files pack.Load reads, verbatim except pack.yaml.
	for _, rel := range []string{
		"primitives/day-count.yaml",
		"primitives/withholding.yaml",
		"primitives/reporting.yaml",
		"primitives/renewal-policies.yaml",
		"parameters/constants.yaml",
		"rate-sheet-refs/deposits-pt.yaml",
	} {
		copyPackFile(t, dst, rel)
	}

	// pack.yaml with personal_loan added to schema_pins. We read the real manifest
	// and append the pin so the rest of the manifest (namespace, version) is real.
	manifest, err := os.ReadFile(filepath.Join(packDir, "pack.yaml"))
	if err != nil {
		t.Fatalf("read pack.yaml: %v", err)
	}
	augmented := strings.Replace(string(manifest),
		"schema_pins:\n  term_deposit: term_deposit@2026.1",
		"schema_pins:\n  term_deposit: term_deposit@2026.1\n  personal_loan: personal_loan@2026.1",
		1)
	if augmented == string(manifest) {
		t.Fatalf("schema_pins anchor not found in pack.yaml — fixture helper is stale")
	}
	writePackFile(t, dst, "pack.yaml", []byte(augmented))

	// A rate-sheet ref for personal_loan so HasRateSheetFor(personal_loan) is true
	// (depth-3 needs the pack to be able to price the family).
	writePackFile(t, dst, "rate-sheet-refs/loans-pt.yaml",
		[]byte("refs:\n  - product_family: personal_loan\n    rate_sheet_version_id: pt-loans-2026.1\n"))

	return dst
}

func copyPackFile(t *testing.T, dst, rel string) {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(packDir, rel))
	if err != nil {
		t.Fatalf("read %s: %v", rel, err)
	}
	writePackFile(t, dst, rel, b)
}

func writePackFile(t *testing.T, dst, rel string, b []byte) {
	t.Helper()
	out := filepath.Join(dst, rel)
	if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
		t.Fatalf("mkdir for %s: %v", rel, err)
	}
	if err := os.WriteFile(out, b, 0o644); err != nil {
		t.Fatalf("write %s: %v", rel, err)
	}
}

// TestLoanValidFixturesPassAllDepths — every loan accept fixture conforms through
// all four depths against a pack that declares personal_loan. This is the
// acceptance criterion "a pack declaring the loan validates rather than failing
// unknown-family": the same fixtures would fail depth-1 unknown-family under the
// old hardcoded map.
func TestLoanValidFixturesPassAllDepths(t *testing.T) {
	pk := clonePackDeclaringLoan(t)
	valid := []string{"general-36m.yaml", "education-eligibility-gated.yaml"}
	for _, name := range valid {
		t.Run(name, func(t *testing.T) {
			rep, err := Run(Options{
				VariantPath: filepath.Join(packLoanValidDir, name),
				SchemaDir:   schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
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
			for _, d := range rep.Depths {
				if !d.OK {
					t.Errorf("depth %d not ok", d.Depth)
				}
			}
		})
	}
}

// TestLoanInvalidFixturesRejectAtExpectedDepth — each loan reject fixture is
// rejected at exactly the depth/kind it targets, pinning that the depth classifier
// runs for the DISCOVERED loan family (not just term_deposit). The depth-3 fixture
// in particular proves the Go-side pack-compliance checks run for the loan.
func TestLoanInvalidFixturesRejectAtExpectedDepth(t *testing.T) {
	pk := clonePackDeclaringLoan(t)
	cases := []rejectCase{
		{cueLoanInvalidDir, "non-eur-currency.yaml", diag.DepthType, diag.KindTypeMismatch},
		{cueLoanInvalidDir, "unknown-purpose.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{packLoanInvalid, "depth1-unknown-purpose.yaml", diag.DepthSyntactic, diag.KindNoMatchingShape},
		{packLoanInvalid, "depth2-commission-over-cap.yaml", diag.DepthType, diag.KindOutOfRange},
		{packLoanInvalid, "depth3-wrong-pack.yaml", diag.DepthPackCompliance, diag.KindPackBoundViolation},
	}
	for _, tc := range cases {
		t.Run(tc.file, func(t *testing.T) {
			rep, err := Run(Options{
				VariantPath: filepath.Join(tc.dir, tc.file),
				SchemaDir:   schemaDir, PackDir: pk, MaxDepth: diag.DepthRegulatory,
			})
			if err != nil {
				t.Fatalf("toolchain error: %v", err)
			}
			if rep.OK {
				t.Fatalf("expected rejection, got OK")
			}
			d0 := rep.Diagnostics[0]
			if d0.Depth != tc.depth {
				t.Errorf("rejected at depth %d, expected %d (%s)", d0.Depth, tc.depth, tc.kind)
			}
			if !hasKind(rep.Diagnostics, tc.kind) {
				t.Errorf("expected a %q diagnostic, got %+v", tc.kind, rep.Diagnostics)
			}
		})
	}
}

// TestLoanRecognisedNotUnknownFamily is the focused regression for the bug this
// change fixes: a personal_loan variant must NOT be rejected with the depth-1
// unknown-family shape_mismatch the hardcoded term_deposit-only map produced. We
// run depth 1 only (no pack needed) and assert the loan schema pin RESOLVES — the
// variant is accepted (the discovered family schema unifies cleanly).
func TestLoanRecognisedNotUnknownFamily(t *testing.T) {
	rep, err := Run(Options{
		VariantPath: filepath.Join(packLoanValidDir, "general-36m.yaml"),
		SchemaDir:   schemaDir, MaxDepth: diag.DepthSyntactic,
	})
	if err != nil {
		t.Fatalf("toolchain error: %v", err)
	}
	if !rep.OK {
		// Specifically guard against the old unknown-family rejection.
		for _, d := range rep.Diagnostics {
			if d.Kind == diag.KindShapeMismatch && strings.Contains(d.Message, "does not resolve to a known family") {
				t.Fatalf("loan rejected unknown-family — discovery did not pick up personal_loan: %+v", d)
			}
		}
		t.Fatalf("expected loan to be recognised + accepted at depth 1, got: %+v", rep.Diagnostics)
	}
}

// TestDiscoverFamiliesFindsBothFamilies pins the file-discovery resolver directly:
// the committed contracts/cue/families/ dir carries exactly term_deposit and
// personal_loan, each with the kebab→snake name and kebab→Pascal root def. A new
// families/*.cue would extend this set with no code change.
func TestDiscoverFamiliesFindsBothFamilies(t *testing.T) {
	fams, err := DiscoverFamilies(schemaDir)
	if err != nil {
		t.Fatalf("discovery error: %v", err)
	}
	want := map[string]string{ // family name → root def
		"term_deposit":  "#TermDeposit",
		"personal_loan": "#PersonalLoan",
	}
	for name, def := range want {
		f, ok := fams[name]
		if !ok {
			t.Errorf("family %q not discovered (got: %v)", name, fams)
			continue
		}
		if f.RootDef != def {
			t.Errorf("family %q root def = %q, want %q", name, f.RootDef, def)
		}
		if len(f.SchemaFiles) == 0 || f.SchemaFiles[0] != "common.cue" {
			t.Errorf("family %q schema files = %v, want common.cue first", name, f.SchemaFiles)
		}
	}
}
