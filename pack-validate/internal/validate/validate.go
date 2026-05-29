// Package validate runs the four synchronous pack-validate depths over a
// variant, layered (each depth assumes the previous passed) and short-circuiting
// at the first depth that produces diagnostics (ADR-PC-006; authoring §5).
//
// Depth attribution over CUE's monolithic unification: depths 1 and 2 are one
// `cue vet`-equivalent unification pass. Its wall-time is attributed to depth 1
// (the parse + structural unify is the syntactic work); depth 2 reports the
// type/range diagnostics classified out of that same pass plus the Go-side
// pack-bound primitive resolution it additionally performs. Depths 3 and 4 are
// pure Go checks over the decoded variant. The per-depth budgets are ceilings
// surfaced for the PACK_VALIDATE_DEPTH_BUDGETS fitness function, not the basis
// of rejection.
package validate

import (
	"fmt"
	"time"

	"cuelang.org/go/cue/cuecontext"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
	"github.com/jhosm/babelstone/pack-validate/internal/pack"
)

// Options configures one validation run.
type Options struct {
	VariantPath string     // path to the variant YAML
	SchemaDir   string     // dir holding the family schema files (contracts/cue)
	PackDir     string     // pinned pack dir; required for MaxDepth ≥ 2
	MaxDepth    diag.Depth // run depths 1..MaxDepth
}

// Run executes the pipeline and returns the diagnostic report. The returned
// error is for *toolchain* failures (schema won't compile, pack won't load,
// unknown family) — a malformed or non-conformant *variant* is reported as
// diagnostics with a nil error, so callers branch on report.OK.
func Run(opts Options) (*diag.Report, error) {
	if opts.MaxDepth < diag.DepthSyntactic || opts.MaxDepth > diag.DepthRegulatory {
		return nil, fmt.Errorf("invalid max depth %d", opts.MaxDepth)
	}

	ctx := cuecontext.New()
	meta, perr := peekVariant(ctx, opts.VariantPath)
	rep := diag.NewReport(meta.VariantID, meta.Pack)

	// A variant whose YAML does not parse is a pure depth-1 syntactic failure.
	if perr != nil {
		start := time.Now()
		rep.RecordDepth(diag.DepthSyntactic, time.Since(start), []diag.Diagnostic{{
			Depth: diag.DepthSyntactic, Kind: diag.KindMalformed, Message: perr.Error(),
			Pos: posOf(perr),
		}})
		return rep, nil
	}

	// A schema pin that does not resolve to a known family schema is itself a
	// depth-1 syntactic failure of the variant (malformed #SchemaRef, or a
	// family v1 does not carry) — not a toolchain error: we cannot pick a schema
	// to run the structural check against, so this is as far as depth 1 gets.
	fam, err := LookupFamily(FamilyFromSchemaPin(meta.Schema))
	if err != nil {
		start := time.Now()
		rep.RecordDepth(diag.DepthSyntactic, time.Since(start), []diag.Diagnostic{{
			Depth: diag.DepthSyntactic, Path: "schema", Kind: diag.KindShapeMismatch,
			Message: fmt.Sprintf("schema pin %q does not resolve to a known family schema: %v", meta.Schema, err),
		}})
		return rep, nil
	}

	// Load the pinned pack once if any depth ≥ 2 will run.
	var p *pack.Pack
	if opts.MaxDepth >= diag.DepthType {
		if opts.PackDir == "" {
			return nil, fmt.Errorf("depth %d (%s) requires --pack", opts.MaxDepth, opts.MaxDepth.Name())
		}
		if p, err = pack.Load(opts.PackDir); err != nil {
			return nil, fmt.Errorf("loading pack %s: %w", opts.PackDir, err)
		}
	}

	// --- depths 1 & 2: one CUE unification pass ---------------------------
	start := time.Now()
	run, err := loadAndUnify(opts.SchemaDir, opts.VariantPath, fam)
	unifyElapsed := time.Since(start)
	if err != nil {
		return nil, err // schema compile / load failure = toolchain error
	}

	rep.RecordDepth(diag.DepthSyntactic, unifyElapsed, run.diags1)
	if len(run.diags1) > 0 || opts.MaxDepth == diag.DepthSyntactic {
		return rep, nil
	}

	// depth 2 = CUE type/range diagnostics (already computed) + pack-bound
	// primitive resolution (timed here).
	start = time.Now()
	vd, derr := decodeVariant(run.variant)
	if derr != nil {
		return nil, derr
	}
	diags2 := append([]diag.Diagnostic{}, run.diags2...)
	diags2 = append(diags2, depth2PackBound(vd, p)...)
	rep.RecordDepth(diag.DepthType, time.Since(start), diags2)
	if len(diags2) > 0 || opts.MaxDepth == diag.DepthType {
		return rep, nil
	}

	// --- depth 3: pack compliance ----------------------------------------
	start = time.Now()
	diags3 := depth3PackCompliance(vd, fam, p)
	rep.RecordDepth(diag.DepthPackCompliance, time.Since(start), diags3)
	if len(diags3) > 0 || opts.MaxDepth == diag.DepthPackCompliance {
		return rep, nil
	}

	// --- depth 4: regulatory coherence -----------------------------------
	start = time.Now()
	diags4 := depth4Regulatory(vd, p)
	rep.RecordDepth(diag.DepthRegulatory, time.Since(start), diags4)

	return rep, nil
}
