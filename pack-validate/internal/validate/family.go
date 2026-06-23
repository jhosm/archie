package validate

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

// Family describes how to validate one product family against its CUE schema.
// A family is no longer a hand-maintained registry entry: it is DISCOVERED from
// the schema dir's families/*.cue files, exactly as contracts/cue/check.sh and
// the Avro/AsyncAPI gates self-discover (a hand-kept list silently skips any
// family it omits). The kebab basename is the source of every derived name:
//
//	families/personal-loan.cue
//	  → Name       "personal_loan"   (snake_case — the schema-pin / event family)
//	  → RootDef    "#PersonalLoan"    (PascalCase — the closed root definition)
//	  → SchemaFiles ["common.cue", "families/personal-loan.cue"]
//
// The pipeline stays family-agnostic; the depth-3/4 Go checks read the discovered
// descriptor's Name and run only the rules that apply to the variant's own shape.
type Family struct {
	Name        string   // e.g. "term_deposit"
	SchemaFiles []string // schema files relative to the schema dir, same CUE package
	RootDef     string   // the closed root definition, e.g. "#TermDeposit"
}

// FamilyFromSchemaPin resolves a family name from a variant's `schema:` pin,
// e.g. "term_deposit@2026.1" → "term_deposit" (common.cue #SchemaRef shape).
func FamilyFromSchemaPin(pin string) string {
	if i := strings.IndexByte(pin, '@'); i >= 0 {
		return pin[:i]
	}
	return pin
}

// DiscoverFamilies walks schemaDir/families/*.cue and returns the descriptor for
// each, keyed by snake_case family name. It is the Go mirror of check.sh's
// families/*.cue sweep: a new family's schema is recognised the moment its .cue
// lands, with no code change. A schema dir with no families/ dir is an error
// (the schema dir is misconfigured), never a silent empty set.
func DiscoverFamilies(schemaDir string) (map[string]Family, error) {
	famDir := filepath.Join(schemaDir, "families")
	entries, err := os.ReadDir(famDir)
	if err != nil {
		return nil, fmt.Errorf("discovering families in %s: %w", famDir, err)
	}
	out := map[string]Family{}
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".cue" {
			continue
		}
		base := strings.TrimSuffix(e.Name(), ".cue") // e.g. "personal-loan"
		out[kebabToSnake(base)] = Family{
			Name:        kebabToSnake(base),
			SchemaFiles: []string{"common.cue", filepath.Join("families", e.Name())},
			RootDef:     kebabToDef(base),
		}
	}
	if len(out) == 0 {
		return nil, fmt.Errorf("no family schemas found in %s", famDir)
	}
	return out, nil
}

// kebabToSnake turns the canonical kebab .cue basename into the snake_case family
// name a variant pins (personal-loan → personal_loan). '_' is tolerated so a
// basename already in snake form round-trips unchanged.
func kebabToSnake(name string) string {
	return strings.ReplaceAll(name, "-", "_")
}

// kebabToDef turns the kebab basename into the PascalCase closed root definition
// (personal-loan → #PersonalLoan), the same #<Family> convention check.sh's
// kebab_to_def derives and the new-family-schema skill follows.
func kebabToDef(name string) string {
	var b strings.Builder
	b.WriteByte('#')
	for _, part := range strings.FieldsFunc(name, func(r rune) bool { return r == '-' || r == '_' }) {
		b.WriteString(strings.ToUpper(part[:1]))
		b.WriteString(part[1:])
	}
	return b.String()
}

// knownNames returns the sorted family names for a diagnostic message.
func knownNames(fams map[string]Family) string {
	names := make([]string, 0, len(fams))
	for n := range fams {
		names = append(names, n)
	}
	sort.Strings(names)
	return strings.Join(names, ", ")
}
