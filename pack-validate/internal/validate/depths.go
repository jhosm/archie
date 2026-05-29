package validate

import (
	"fmt"
	"strings"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
	"github.com/jhosm/babelstone/pack-validate/internal/pack"
)

// depth2PackBound resolves the variant's pack-bound primitives against the
// pinned pack's catalogues (ADR-PC-006 Context depth-2: "pack-bound fields
// resolve to a known primitive in the pinned pack"). The shape of the binding
// was already enforced at depth 1; here the well-formed name must name a
// primitive the pack actually carries.
func depth2PackBound(vd variantData, p *pack.Pack) []diag.Diagnostic {
	var out []diag.Diagnostic
	ns, key, ok := splitPackRef(vd.DayCount)
	if !ok {
		return out // malformed binding was a depth-1 shape error already
	}
	if ns != p.Namespace {
		out = append(out, diag.Diagnostic{
			Depth: diag.DepthType, Path: "day_count", Kind: diag.KindUnknownPrimitive,
			Message: fmt.Sprintf("day_count namespace %q does not match pinned pack namespace %q", ns, p.Namespace),
		})
		return out
	}
	if !p.DayCounts[key] {
		out = append(out, diag.Diagnostic{
			Depth: diag.DepthType, Path: "day_count", Kind: diag.KindUnknownPrimitive,
			Message: fmt.Sprintf("day_count %q resolves to no day-count primitive in pack %s", vd.DayCount, p.Key),
		})
	}
	return out
}

// depth3PackCompliance checks the variant against the pinned pack's declared
// bounds and obligations (ADR-PC-006 Context depth-3).
//
// Scope note: the canonical `tan_basis_points ≤ max_consumer_rate_bps` bound
// cannot fire here — a variant carries a rate *reference* (#RateRef), not a
// number; the numeric ceiling resolves at constitution after rate-sheet lookup
// (ADR-PC-008; C.6). v1 depth-3 enforces the pre-rate-resolution obligations:
// the variant is being validated against the pack it pins, its schema pin is
// the one the pack bundles, and the pack can price its family.
func depth3PackCompliance(vd variantData, fam Family, p *pack.Pack) []diag.Diagnostic {
	var out []diag.Diagnostic

	if vd.Pack != p.Key {
		out = append(out, diag.Diagnostic{
			Depth: diag.DepthPackCompliance, Path: "pack", Kind: diag.KindPackBoundViolation,
			Message: fmt.Sprintf("variant pins pack %q but is being validated against pack %q", vd.Pack, p.Key),
		})
	}

	if pin, ok := p.SchemaPins[fam.Name]; ok && vd.Schema != pin {
		out = append(out, diag.Diagnostic{
			Depth: diag.DepthPackCompliance, Path: "schema", Kind: diag.KindPackBoundViolation,
			Message: fmt.Sprintf("variant pins schema %q but pack %s bundles %q for family %s", vd.Schema, p.Key, pin, fam.Name),
		})
	}

	if !p.HasRateSheetFor(fam.Name) {
		out = append(out, diag.Diagnostic{
			Depth: diag.DepthPackCompliance, Path: "rate", Kind: diag.KindUnresolvedRateRef,
			Message: fmt.Sprintf("pack %s carries no rate-sheet ref for family %s — the variant's rate_ref cannot resolve at constitution", p.Key, fam.Name),
		})
	}

	return out
}

// depth4Regulatory checks the cross-field regulatory invariants the CUE schema
// comments explicitly defer to the validator because they are not expressible
// element-wise in CUE (ADR-PC-006 Context depth-4; term-deposit.cue comments on
// #SteppedRate.steps and #BandedPolicy.banded).
func depth4Regulatory(vd variantData, p *pack.Pack) []diag.Diagnostic {
	var out []diag.Diagnostic

	// (a) day-count must be regulatorily permitted for a deposit. PT retail
	// term deposits require Act/360 (02 §2.2; ADR-PC-006 §4 worked example
	// "PT pack rejects Act/365 for a deposit"). The permitted set is keyed on
	// the pack's jurisdiction namespace. See depositPermittedDayCounts for the
	// follow-up to make this pack-declared rather than validator-encoded.
	if _, key, ok := splitPackRef(vd.DayCount); ok {
		if permitted, known := depositPermittedDayCounts[p.Namespace]; known && !permitted[key] {
			out = append(out, diag.Diagnostic{
				Depth: diag.DepthRegulatory, Path: "day_count", Kind: diag.KindForbiddenDayCount,
				Message: fmt.Sprintf("day-count %q is not regulatorily permitted for a %s deposit (permitted: %s)",
					key, strings.ToUpper(p.Namespace), strings.Join(sortedKeys(permitted), ", ")),
			})
		}
	}

	// (b) stepped-rate step boundaries must be strictly ascending by from_day.
	if vd.Rate.Stepped != nil {
		prev := int64(-1)
		for i, s := range vd.Rate.Stepped.Steps {
			if s.FromDay <= prev {
				out = append(out, diag.Diagnostic{
					Depth: diag.DepthRegulatory, Path: fmt.Sprintf("rate.stepped.steps.%d.from_day", i),
					Kind:    diag.KindNonAscendingSteps,
					Message: fmt.Sprintf("from_day %d is not strictly greater than the preceding step (%d)", s.FromDay, prev),
				})
			}
			prev = s.FromDay
		}
	}

	// (c) banded early-termination: up_to_days ascending, with exactly one open
	// (null) tail and it must be last (02 §2.5 first-match semantics).
	bands := vd.EarlyTermination.Banded
	prev := int64(-1)
	for i, b := range bands {
		isLast := i == len(bands)-1
		if b.UpToDays == nil {
			if !isLast {
				out = append(out, diag.Diagnostic{
					Depth: diag.DepthRegulatory, Path: fmt.Sprintf("early_termination.banded.%d.up_to_days", i),
					Kind:    diag.KindOpenTailNotLast,
					Message: "the open (null) up_to_days band must be the single last band",
				})
			}
			continue
		}
		if *b.UpToDays <= prev {
			out = append(out, diag.Diagnostic{
				Depth: diag.DepthRegulatory, Path: fmt.Sprintf("early_termination.banded.%d.up_to_days", i),
				Kind:    diag.KindNonAscendingBands,
				Message: fmt.Sprintf("up_to_days %d is not strictly greater than the preceding band (%d)", *b.UpToDays, prev),
			})
		}
		prev = *b.UpToDays
	}

	return out
}

// depositPermittedDayCounts is the per-jurisdiction regulatory permitted-set
// for term-deposit day-counts. PT: Act/360 only (02 §2.2). This is jurisdiction
// law, not pack-tunable config, so it lives in the engine-owned validator — but
// migrating it to an explicit pack `regulatory:` section is filed as a
// follow-up so the rule is auditor-visible in the pack itself.
var depositPermittedDayCounts = map[string]map[string]bool{
	"pt": {"act_360": true},
}

// splitPackRef splits a #PackBoundPrimitive value "pt.act_360" into namespace
// "pt" and primitive key "act_360" (the catalogue map key). Returns ok=false
// for a value without a namespace segment (a depth-1 shape failure).
func splitPackRef(ref string) (ns, key string, ok bool) {
	i := strings.IndexByte(ref, '.')
	if i <= 0 || i == len(ref)-1 {
		return "", "", false
	}
	return ref[:i], ref[i+1:], true
}

func sortedKeys(m map[string]bool) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	// small sets; insertion-order-free, deterministic for messages
	for i := 1; i < len(out); i++ {
		for j := i; j > 0 && out[j-1] > out[j]; j-- {
			out[j-1], out[j] = out[j], out[j-1]
		}
	}
	return out
}
