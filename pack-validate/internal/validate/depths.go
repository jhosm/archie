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
	if !p.HasDayCount(key) {
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
func depth4Regulatory(vd variantData, fam Family, p *pack.Pack) []diag.Diagnostic {
	var out []diag.Diagnostic

	// (a) day-count must be regulatorily permitted for this product family.
	// PT retail term deposits require Act/360 (02 §2.2; ADR-PC-006 §4 worked
	// example "PT pack rejects Act/365 for a deposit"). The permitted set is
	// PACK-DECLARED (primitives/day-count.yaml `permitted_for`), not encoded
	// here, so the regulatory rule is auditor-visible in the signed pack. The
	// day-count's catalogue membership was already enforced at depth 2; here we
	// reject a carried-but-not-permitted day-count for the family.
	if _, key, ok := splitPackRef(vd.DayCount); ok && p.HasDayCount(key) {
		if !p.PermitsDayCountFor(key, fam.Name) {
			out = append(out, diag.Diagnostic{
				Depth: diag.DepthRegulatory, Path: "day_count", Kind: diag.KindForbiddenDayCount,
				Message: fmt.Sprintf("day-count %q is not regulatorily permitted for a %s %s (pack %s permits: %s)",
					key, strings.ToUpper(p.Namespace), fam.Name, p.Key,
					strings.Join(p.PermittedDayCountsFor(fam.Name), ", ")),
			})
		}
	}

	// (a2) the SAME_TERM_SAME_RATE auto-renewal policy is PACK-RESTRICTED (02 §2.4.4: "less
	// common, pack-restricted"). The family schema structurally allows it (depths 1–2 pass), but
	// a product may auto-renew at the ORIGINAL rate only where the pack permits it for this family.
	// The permitted-set is PACK-DECLARED (primitives/renewal-policies.yaml `permitted_for`), so the
	// regulatory rule is auditor-visible in the signed pack, not encoded here — mirroring the
	// day-count `permitted_for` restriction above. NONE and SAME_TERM_CURRENT_RATE are unrestricted
	// and never checked. (F.5 follow-up, bd k6r8.6 — the babelstone-k4yr restriction the engine's
	// renewal decider recorded as "missing pack primitive".)
	const sameTermSameRatePolicy = "SAME_TERM_SAME_RATE"
	const sameTermSameRateKey = "same_term_same_rate" // the pack catalogue key (lower_snake of the policy)
	if vd.AutoRenewalPolicy == sameTermSameRatePolicy &&
		p.HasRenewalRestriction(sameTermSameRateKey) &&
		!p.PermitsRenewalPolicyFor(sameTermSameRateKey, fam.Name) {
		out = append(out, diag.Diagnostic{
			Depth: diag.DepthRegulatory, Path: "auto_renewal_policy", Kind: diag.KindForbiddenRenewalPolicy,
			Message: fmt.Sprintf(
				"auto-renewal policy %q is pack-restricted and not permitted for a %s %s (pack %s; 02 §2.4.4)",
				sameTermSameRatePolicy, strings.ToUpper(p.Namespace), fam.Name, p.Key),
		})
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

	// (d) + (e) F.12 partial-withdrawal cross-field coherence (only when the
	// optional block is present). Like the steps/bands ordering checks above,
	// these are STRUCTURAL coherence invariants — universal, not pack-declared:
	// a policy that violates them makes the partial-withdrawal feature dead on
	// every deposit, regardless of jurisdiction. CUE cannot express either
	// element-wise across the two optional blocks, so the schema defers them
	// here (term-deposit.cue #PartialWithdrawal comment; ADR-PC-006 depth-4).
	if pw := vd.PartialWithdrawal; pw != nil {
		// (d) the lock-up must be strictly shorter than the term — a
		// lock-up that meets or outlasts the term leaves no day on which a
		// partial withdrawal is ever legal.
		if pw.LockupPeriodDays >= vd.TermDays {
			out = append(out, diag.Diagnostic{
				Depth: diag.DepthRegulatory, Path: "partial_withdrawal.lockup_period_days",
				Kind: diag.KindLockupExceedsTerm,
				Message: fmt.Sprintf("lockup_period_days %d is not strictly less than term_days %d — the lock-up outlasts the term, so no partial withdrawal could ever be legal",
					pw.LockupPeriodDays, vd.TermDays),
			})
		}
		// (e) the minimum remaining balance must be strictly below the corridor
		// ceiling — only checkable when max_cents is present. If the floor meets
		// or exceeds the ceiling, no deposit (≤ max_cents) could leave at least
		// the floor on deposit while withdrawing a positive amount.
		if max := vd.PrincipalBounds.MaxCents; max != nil && pw.MinRemainingBalanceCents >= *max {
			out = append(out, diag.Diagnostic{
				Depth: diag.DepthRegulatory, Path: "partial_withdrawal.min_remaining_balance_cents",
				Kind: diag.KindRemainingExceedsMaxCents,
				Message: fmt.Sprintf("min_remaining_balance_cents %d is not strictly less than principal_bounds.max_cents %d — no deposit in the corridor could ever host a legal partial withdrawal",
					pw.MinRemainingBalanceCents, *max),
			})
		}
		// NOTE: forbidding a partial_withdrawal block on an ADVANCE (interest in advance)
		// variant is a presence-given-enum constraint the SCHEMA expresses declaratively
		// (term-deposit.cue: `if interest_variant == "ADVANCE" { partial_withdrawal?: _|_ }`,
		// the same shape as payment_period_months), so it is rejected at depth-1, not here —
		// unlike (d)/(e), which need scalar arithmetic across sub-blocks that CUE cannot do
		// (bd babelstone-emtr). The runtime PartialWithdrawalDecider is the backstop.
	}

	return out
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
