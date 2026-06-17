package validate

import (
	"fmt"
	"io"
	"os"
	"path/filepath"

	"cuelang.org/go/cue"
	cueyaml "cuelang.org/go/encoding/yaml"
)

// maxVariantBytes caps a variant document before it reaches the CUE front-end.
// CUE's parse + build cost is superlinear in node count, so an unbounded
// variant is a denial-of-service vector — and the variant is the
// non-engineer-authored input pack-validate exists to police. Real variants
// are a few KB (the largest product-config in the repo is ~2 KB); 1 MiB is
// generous headroom yet turns a pathological large input into a clean, fast
// rejection rather than a hang (ADR-PC-007 §169).
const maxVariantBytes = 1 << 20 // 1 MiB

// readVariantBounded reads up to maxVariantBytes from path, rejecting anything
// larger before it can reach the CUE front-end.
func readVariantBounded(path string) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	src, err := io.ReadAll(io.LimitReader(f, maxVariantBytes+1))
	if err != nil {
		return nil, err
	}
	if int64(len(src)) > maxVariantBytes {
		return nil, fmt.Errorf("variant %s exceeds %d-byte limit (pathological or non-variant input rejected)", filepath.Base(path), maxVariantBytes)
	}
	return src, nil
}

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
func peekVariant(ctx *cue.Context, path string) (m variantMeta, err error) {
	src, err := readVariantBounded(path)
	if err != nil {
		return m, err
	}
	// A malformed document that trips a panic deep in the CUE YAML/build path is
	// turned into a clean returned error (the caller maps it to a depth-1
	// malformed diagnostic) rather than crashing the binary.
	defer func() {
		if r := recover(); r != nil {
			err = fmt.Errorf("malformed variant rejected (internal parse failure: %v)", r)
		}
	}()
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
	VariantID         string `json:"variant_id"`
	Schema            string `json:"schema"`
	Pack              string `json:"pack"`
	DayCount          string `json:"day_count"`
	InterestVariant   string `json:"interest_variant"`
	AutoRenewalPolicy string `json:"auto_renewal_policy"`

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
