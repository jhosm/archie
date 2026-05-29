package validate

import (
	"fmt"
	"os"
	"path/filepath"

	"cuelang.org/go/cue"
	cueyaml "cuelang.org/go/encoding/yaml"
)

// variantMeta is the identity envelope peeked before schema resolution: a
// variant names the family schema and pack it was authored against
// (term-deposit.cue "version envelope"; authoring §6).
type variantMeta struct {
	VariantID string `json:"variant_id"`
	Schema    string `json:"schema"`
	Pack      string `json:"pack"`
}

// peekVariant extracts the identity envelope without a schema — used to resolve
// which family schema to load. A YAML that does not parse is reported as a
// depth-1 malformed diagnostic by the caller; here it is a plain error.
func peekVariant(ctx *cue.Context, path string) (variantMeta, error) {
	var m variantMeta
	src, err := os.ReadFile(path)
	if err != nil {
		return m, err
	}
	file, err := cueyaml.Extract(filepath.Base(path), src)
	if err != nil {
		return m, err
	}
	v := ctx.BuildFile(file)
	if err := v.Err(); err != nil {
		return m, err
	}
	// Decode best-effort: a missing field leaves its zero value; the schema
	// validation at depth 1 is what enforces presence.
	_ = v.Decode(&m)
	return m, nil
}

// variantData is the typed view the Go-side depth-3/4 checks read. Only the
// fields those checks touch are modelled; the closed CUE schema is the full
// contract. Pointers/empties distinguish "absent branch" (flat rate, flat
// early-termination) from "present".
type variantData struct {
	VariantID       string `json:"variant_id"`
	Schema          string `json:"schema"`
	Pack            string `json:"pack"`
	DayCount        string `json:"day_count"`
	InterestVariant string `json:"interest_variant"`

	Rate struct {
		Stepped *struct {
			Steps []struct {
				FromDay int64 `json:"from_day"`
			} `json:"steps"`
		} `json:"stepped"`
		Flat *struct{} `json:"flat"`
	} `json:"rate"`

	EarlyTermination struct {
		Banded []struct {
			UpToDays *int64 `json:"up_to_days"` // nil ⇒ the open (null) tail
		} `json:"banded"`
	} `json:"early_termination"`
}

// decodeVariant decodes the parsed variant value into the typed view. Only
// called once depths 1–2 pass, so the value is structurally sound.
func decodeVariant(v cue.Value) (variantData, error) {
	var vd variantData
	if err := v.Decode(&vd); err != nil {
		return vd, fmt.Errorf("decoding variant for depth-3/4 checks: %w", err)
	}
	return vd, nil
}
