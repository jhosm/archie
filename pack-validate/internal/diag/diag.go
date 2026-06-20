// Package diag is the versioned JSON diagnostic contract between the Go
// pack-validate binary and its consumers — the PM author's pre-commit hook, the
// PR CI gate, and (later) the .NET engine at pack-load (ADR-PC-006 §P2).
//
// The per-diagnostic shape is exactly the ADR's {depth, path, kind, message,
// pos}. The Report wrapper adds the contract version stamp (Open Action #3 —
// the contract is a versioned interface a CI test pins) and the per-depth
// budget/timing the PACK_VALIDATE_DEPTH_BUDGETS fitness function reads
// (commitment-catalogue row 10).
package diag

import (
	"encoding/json"
	"fmt"
	"io"
	"time"
)

// ContractVersion stamps the JSON output. Bump only on a breaking change to the
// Report/Diagnostic shape; the engine deserialises against an expected value
// (ADR-PC-006 Residual risk 3, Open Action #3).
const ContractVersion = "1"

// Depth identifies one of the four synchronous validator depths
// (ADR-PC-006 Context table; feature-design-configuration-authoring §5).
type Depth int

const (
	DepthSyntactic      Depth = 1 // variant parses + matches the schema's structural shape
	DepthType           Depth = 2 // field types/ranges + pack-bound primitive resolution
	DepthPackCompliance Depth = 3 // variant respects the pinned pack's bounds/obligations
	DepthRegulatory     Depth = 4 // cross-field regulatory invariants (Go-side)
)

// Name is the stable lowercase label used on the CLI and in JSON.
func (d Depth) Name() string {
	switch d {
	case DepthSyntactic:
		return "syntactic"
	case DepthType:
		return "type"
	case DepthPackCompliance:
		return "pack-compliance"
	case DepthRegulatory:
		return "regulatory"
	default:
		return "unknown"
	}
}

// Budget is the per-depth wall-clock budget from ADR-PC-006 (and the depth
// table in authoring §5). A depth that overruns is a bug, not a rejection — it
// is surfaced (over_budget) but does not by itself fail the variant.
func (d Depth) Budget() time.Duration {
	switch d {
	case DepthSyntactic:
		return 1 * time.Second
	case DepthType:
		return 5 * time.Second
	case DepthPackCompliance:
		return 10 * time.Second
	case DepthRegulatory:
		return 10 * time.Second
	default:
		return 0
	}
}

// AggregateBudget is the < 30 s ceiling across depths 1–4 (ADR-PC-006 §P3).
const AggregateBudget = 30 * time.Second

// Diagnostic kinds. Stable strings: a consumer may branch on them, so treat the
// set as part of the contract. Grouped by the depth that emits them.
const (
	// depth 1 — syntactic / structural
	KindMalformed       = "malformed"         // the variant YAML does not parse
	KindUnknownField    = "unknown_field"     // closed-struct rejection (no DSL escape hatch)
	KindMissingField    = "missing_field"     // a required field is absent / incomplete
	KindNoMatchingShape = "no_matching_shape" // no disjunction branch matches (flat XOR stepped, …)
	KindShapeMismatch   = "shape_mismatch"    // a structural shape pattern (version key, pack-binding form) is violated

	// depth 2 — type-check
	KindTypeMismatch     = "type_mismatch"     // wrong scalar type or enum member
	KindOutOfRange       = "out_of_range"      // numeric bound violated
	KindUnknownPrimitive = "unknown_primitive" // a pack-bound field names a primitive the pinned pack does not carry

	// depth 3 — pack compliance
	KindPackBoundViolation = "pack_bound_violation" // a pack-declared scalar bound is exceeded
	KindMissingReporting   = "missing_reporting"    // a pack-required (active) reporting hook is bypassed
	KindUnresolvedRateRef  = "unresolved_rate_ref"  // a rate-ref role does not resolve against the pack's rate-sheet refs

	// depth 4 — regulatory coherence
	KindNonAscendingSteps = "non_ascending_steps" // stepped-rate from_day not strictly ascending
	KindNonAscendingBands = "non_ascending_bands" // banded early-termination up_to_days not ascending
	KindOpenTailNotLast   = "open_tail_not_last"  // the open (null) band tail is not the single last element
	KindForbiddenDayCount = "forbidden_day_count" // the pack forbids this day-count for a deposit (PT: Act/360 only)

	KindForbiddenRenewalPolicy = "forbidden_renewal_policy" // the pack forbids this auto-renewal policy for the family (02 §2.4.4: SAME_TERM_SAME_RATE is pack-restricted)

	// F.12 partial-withdrawal cross-field coherence (structural, not pack-declared — like the steps/bands ordering checks)
	KindCarenciaExceedsTerm        = "carencia_exceeds_term"          // the carência lock-up meets/exceeds the term — the deposit could never be partially withdrawn
	KindRemainingExceedsMaxCents   = "remaining_exceeds_max_cents"    // min remaining balance meets/exceeds principal_bounds.max_cents — no deposit could ever host a legal partial withdrawal
	KindPartialWithdrawalOnAdvance = "partial_withdrawal_on_advance"  // a partial_withdrawal block on an ADVANCE (juros antecipados) variant — interest is pre-paid and cannot be re-based, so partial withdrawal is forbidden for the shape (bd babelstone-emtr)
)

// Diagnostic is one finding. The shape is ADR-PC-006 §P2 verbatim:
// {depth, path, kind, message, pos}.
type Diagnostic struct {
	Depth   Depth  `json:"depth"`
	Path    string `json:"path"`          // dotted CUE/variant path, "" when not field-scoped
	Kind    string `json:"kind"`          // one of the Kind* constants
	Message string `json:"message"`       // human-readable detail
	Pos     string `json:"pos,omitempty"` // "file:line:col" when known
}

// DepthResult records that a depth ran, how long it took, and whether it passed.
type DepthResult struct {
	Depth      Depth  `json:"depth"`
	Name       string `json:"name"`
	BudgetMs   int64  `json:"budget_ms"`
	ElapsedMs  int64  `json:"elapsed_ms"`
	OK         bool   `json:"ok"`
	OverBudget bool   `json:"over_budget"`
}

// Report is the full result of a (possibly partial, --max-depth-bounded) run.
type Report struct {
	ContractVersion string        `json:"contract_version"`
	Variant         string        `json:"variant"`
	Pack            string        `json:"pack,omitempty"`
	OK              bool          `json:"ok"`
	Depths          []DepthResult `json:"depths"`
	Diagnostics     []Diagnostic  `json:"diagnostics"`
}

// NewReport seeds a report with the contract stamp and the variant/pack identity.
func NewReport(variant, pack string) *Report {
	return &Report{
		ContractVersion: ContractVersion,
		Variant:         variant,
		Pack:            pack,
		OK:              true,
		Depths:          []DepthResult{},
		Diagnostics:     []Diagnostic{},
	}
}

// RecordDepth appends a depth's timing/outcome and folds its pass/fail into the
// aggregate OK. diags are the diagnostics that depth produced.
func (r *Report) RecordDepth(d Depth, elapsed time.Duration, diags []Diagnostic) {
	r.Depths = append(r.Depths, DepthResult{
		Depth:      d,
		Name:       d.Name(),
		BudgetMs:   d.Budget().Milliseconds(),
		ElapsedMs:  elapsed.Milliseconds(),
		OK:         len(diags) == 0,
		OverBudget: d.Budget() > 0 && elapsed > d.Budget(),
	})
	if len(diags) > 0 {
		r.OK = false
		r.Diagnostics = append(r.Diagnostics, diags...)
	}
}

// WriteJSON emits the contract as indented JSON (CI + engine consumption).
func (r *Report) WriteJSON(w io.Writer) error {
	enc := json.NewEncoder(w)
	enc.SetIndent("", "  ")
	return enc.Encode(r)
}

// WriteHuman emits a terse, author-friendly rendering (pre-commit context).
func (r *Report) WriteHuman(w io.Writer) {
	for _, d := range r.Depths {
		status := "ok"
		if !d.OK {
			status = "FAIL"
		}
		over := ""
		if d.OverBudget {
			over = fmt.Sprintf("  ⚠ over budget (%dms > %dms)", d.ElapsedMs, d.BudgetMs)
		}
		fmt.Fprintf(w, "  depth %d %-16s %-4s  %dms%s\n", d.Depth, d.Name, status, d.ElapsedMs, over)
	}
	for _, dg := range r.Diagnostics {
		loc := dg.Path
		if dg.Pos != "" {
			loc = dg.Pos
		}
		fmt.Fprintf(w, "  ✗ depth %d  %s  [%s]  %s\n", dg.Depth, loc, dg.Kind, dg.Message)
	}
	if r.OK {
		fmt.Fprintln(w, "OK")
	} else {
		fmt.Fprintln(w, "FAILED")
	}
}
