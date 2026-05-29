package validate

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"cuelang.org/go/cue"
	"cuelang.org/go/cue/cuecontext"
	cueerrors "cuelang.org/go/cue/errors"
	"cuelang.org/go/cue/load"
	cueyaml "cuelang.org/go/encoding/yaml"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
)

// cueRun is the result of loading + unifying a variant against its family
// schema. It carries the parsed variant value (for the Go-side depth-3/4
// checks) and the structural (depth-1) / type-range (depth-2) diagnostics
// partitioned out of CUE's single unification pass.
type cueRun struct {
	ctx     *cue.Context
	variant cue.Value // the variant data alone (concrete)
	diags1  []diag.Diagnostic
	diags2  []diag.Diagnostic
}

// loadAndUnify compiles the family schema, parses the variant YAML, unifies
// them, and partitions the resulting errors into depth 1 (structural) and
// depth 2 (type/range). A schema that does not compile is a toolchain error
// (returned as error), never a variant diagnostic.
func loadAndUnify(schemaDir, variantPath string, fam Family) (*cueRun, error) {
	ctx := cuecontext.New()
	r := &cueRun{ctx: ctx}

	insts := load.Instances(fam.SchemaFiles, &load.Config{Dir: schemaDir})
	if len(insts) == 0 {
		return nil, fmt.Errorf("no CUE instance loaded from %s", schemaDir)
	}
	if insts[0].Err != nil {
		return nil, fmt.Errorf("loading family schema: %w", insts[0].Err)
	}
	schema := ctx.BuildInstance(insts[0])
	if err := schema.Err(); err != nil {
		return nil, fmt.Errorf("compiling family schema: %w", err)
	}
	// LookupPath only — do NOT call def.Err(): a bare definition carries open
	// disjunctions (interest_variant, rate) by design and is "incomplete" until
	// unified with data; .Err() would force concreteness and misreport that.
	def := schema.LookupPath(cue.ParsePath(fam.RootDef))
	if !def.Exists() {
		return nil, fmt.Errorf("root definition %s not found in family schema", fam.RootDef)
	}

	src, err := os.ReadFile(variantPath)
	if err != nil {
		return nil, err
	}
	base := filepath.Base(variantPath)
	file, err := cueyaml.Extract(base, src)
	if err != nil {
		// The variant YAML does not parse — a pure depth-1 syntactic failure.
		r.diags1 = append(r.diags1, diag.Diagnostic{
			Depth: diag.DepthSyntactic, Kind: diag.KindMalformed,
			Message: err.Error(), Pos: posOf(err),
		})
		return r, nil
	}
	r.variant = ctx.BuildFile(file)
	if err := r.variant.Err(); err != nil {
		r.diags1 = append(r.diags1, classifyAll(err)...)
		return r, nil
	}

	// Unify the data with the closed root definition and validate it concretely
	// — the same check as `cue vet -d '#Root' variant.yaml schema...`. Closed-
	// struct violations (unknown fields) surface here even when otherwise valid.
	unified := def.Unify(r.variant)
	if err := unified.Validate(cue.Concrete(true)); err != nil {
		for _, d := range classifyAll(err) {
			if d.Depth == diag.DepthSyntactic {
				r.diags1 = append(r.diags1, d)
			} else {
				r.diags2 = append(r.diags2, d)
			}
		}
	}
	return r, nil
}

// classifyAll turns a CUE error tree into depth-tagged diagnostics.
func classifyAll(err error) []diag.Diagnostic {
	var out []diag.Diagnostic
	for _, e := range cueerrors.Errors(err) {
		depth, kind := classify(e)
		out = append(out, diag.Diagnostic{
			Depth:   depth,
			Path:    strings.Join(e.Path(), "."),
			Kind:    kind,
			Message: e.Error(),
			Pos:     posOf(e),
		})
	}
	return out
}

// shapeFields carry structural patterns (version keys, the pack-binding dotted
// form, snake-case ids) whose regex failure is a depth-1 *shape* error — "this
// isn't the right shape of reference" — not a depth-2 type/range error. The
// pack-binding shape is depth 1 per common.cue's #PackBoundPrimitive comment;
// resolving the well-formed name against the pinned pack is depth 2.
var shapeFields = map[string]bool{
	"variant_id":     true,
	"schema":         true,
	"pack":           true,
	"day_count":      true, // #PackBoundPrimitive shape
	"effective_from": true,
	"pricing_band":   true,
	"role_selector":  true,
	"sheet":          true,
}

// classify partitions one CUE error into (depth, kind). CUE evaluates
// structure, type and range in one monolithic pass, so the depths are
// reconstructed from the *kind* of constraint each error violated. The fixture
// tests pin every fixture to its expected depth — they are the real spec for
// this classifier (ADR-PC-006 §P2; authoring §5 "logically-distinct checks").
func classify(e cueerrors.Error) (diag.Depth, string) {
	msg := e.Error()
	path := strings.Join(e.Path(), ".")
	// CUE phrases a regex constraint as `out of bound =~"…"` (or, on some paths,
	// `does not match`); detect it so a *shape* pattern failure (version key,
	// pack-binding form) is attributed to depth-1 shape rather than depth-2
	// range.
	isRegex := has(msg, "=~") || has(msg, "does not match")
	switch {
	// --- depth 1: structural shape -------------------------------------
	case has(msg, "field not allowed"), has(msg, "not allowed in closed struct"):
		return diag.DepthSyntactic, diag.KindUnknownField
	case has(msg, "explicit error"), has(msg, "_|_"):
		// a conditional/forbidden-field guard fired (e.g. payment_period_months
		// present on a non-PERIODIC variant) — structurally this shape is not
		// allowed (a cross-field rule the schema encodes declaratively).
		return diag.DepthSyntactic, diag.KindNoMatchingShape
	case has(msg, "incomplete value"), has(msg, "not present"), has(msg, "field is required"):
		// absent required field — check before the regex rule, since an absent
		// regex-constrained field reads as "incomplete value =~…".
		return diag.DepthSyntactic, diag.KindMissingField
	case isRegex && lastSegmentIn(path, shapeFields):
		return diag.DepthSyntactic, diag.KindShapeMismatch
	case has(msg, "empty disjunction"), has(msg, "no values remaining"):
		return diag.DepthSyntactic, diag.KindNoMatchingShape

	// --- depth 2: type / range -----------------------------------------
	case isRegex:
		// a regex failure on a non-shape field is a value mismatch
		return diag.DepthType, diag.KindTypeMismatch
	case has(msg, "out of bound"), has(msg, "greater than"), has(msg, "less than"):
		return diag.DepthType, diag.KindOutOfRange
	case has(msg, "conflicting values"), has(msg, "mismatched types"):
		return diag.DepthType, diag.KindTypeMismatch
	default:
		// Conservative: an unclassified unification error is a type-level
		// failure (it survived structural unification). The test corpus keeps
		// this bucket empty; a new fixture landing here is a signal to refine.
		return diag.DepthType, diag.KindTypeMismatch
	}
}

func has(s, sub string) bool { return strings.Contains(s, sub) }

// lastSegmentIn reports whether the final dotted path segment is in set —
// matches e.g. "rate.stepped.steps.0.pricing_band" → "pricing_band".
func lastSegmentIn(path string, set map[string]bool) bool {
	if path == "" {
		return false
	}
	seg := path
	if i := strings.LastIndexByte(path, '.'); i >= 0 {
		seg = path[i+1:]
	}
	return set[seg]
}

func posOf(err error) string {
	for _, p := range cueerrors.Positions(err) {
		if p.IsValid() {
			return p.String()
		}
	}
	return ""
}
