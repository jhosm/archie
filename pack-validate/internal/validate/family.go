package validate

import (
	"fmt"
	"strings"
)

// Family describes how to validate one product family against its CUE schema.
// v1 ships a single coarse family (term_deposit) — the "coarse-start, fine-drift"
// model of feature-design-configuration-authoring §3.1. Adding a family is a new
// registry entry plus its depth-3/4 rules; the pipeline is family-agnostic.
type Family struct {
	Name        string   // e.g. "term_deposit"
	SchemaFiles []string // schema files relative to the schema dir, same CUE package
	RootDef     string   // the closed root definition, e.g. "#TermDeposit"
}

var registry = map[string]Family{
	"term_deposit": {
		Name:        "term_deposit",
		SchemaFiles: []string{"common.cue", "families/term-deposit.cue"},
		RootDef:     "#TermDeposit",
	},
}

// FamilyFromSchemaPin resolves a family name from a variant's `schema:` pin,
// e.g. "term_deposit@2026.1" → "term_deposit" (common.cue #SchemaRef shape).
func FamilyFromSchemaPin(pin string) string {
	if i := strings.IndexByte(pin, '@'); i >= 0 {
		return pin[:i]
	}
	return pin
}

// LookupFamily returns the descriptor for a family name.
func LookupFamily(name string) (Family, error) {
	f, ok := registry[name]
	if !ok {
		return Family{}, fmt.Errorf("unknown product family %q (v1 supports: term_deposit)", name)
	}
	return f, nil
}
