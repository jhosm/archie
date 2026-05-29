// Package pack loads a regulatory pack directory (ADR-PC-007 §P1 layout) into
// the lookup tables the validator's depth-2/3/4 checks resolve against:
// the primitive catalogues, the active reporting hooks, the scalar parameters,
// and the rate-sheet-ref roles.
//
// It reads the *committed* pack source under packs/<key>/ — the same data
// packs/pack.sh validates. (The signed OCI artefact additionally bundles a
// digest-pinned schemas/ copy; the validator there would point --schema-dir at
// that copy. Loading the catalogues is identical either way.)
//
// YAML is decoded through cuelang.org/go's YAML front-end so the binary carries
// exactly one third-party dependency (ADR-PC-006 F1: pin the CUE library; no
// second YAML decoder to track).
package pack

import (
	"fmt"
	"os"
	"path/filepath"

	"cuelang.org/go/cue"
	"cuelang.org/go/cue/cuecontext"
	cueyaml "cuelang.org/go/encoding/yaml"
)

// Pack is the loaded, query-ready view of a pack directory.
type Pack struct {
	Dir        string
	ID         string            // e.g. "pt"
	Version    string            // e.g. "2026.1"
	Key        string            // e.g. "pt.2026.1"
	Namespace  string            // jurisdiction namespace, e.g. "pt"
	SchemaPins map[string]string // family → "<family>@YYYY.N"

	DayCounts     map[string]bool          // primitive keys in primitives/day-count.yaml
	Withholdings  map[string]bool          // primitive keys in primitives/withholding.yaml
	Reporting     map[string]ReportingHook // hooks in primitives/reporting.yaml
	Params        map[string]int64         // scalars in parameters/constants.yaml
	RateSheetRefs []RateSheetRef           // refs in rate-sheet-refs/deposits-pt.yaml
}

// ReportingHook mirrors one entry of primitives/reporting.yaml.
type ReportingHook struct {
	Active    bool   `json:"active"`
	Frequency string `json:"frequency"`
	Regulator string `json:"regulator"`
}

// RateSheetRef mirrors one entry of rate-sheet-refs/<sheet>.yaml `refs:`.
type RateSheetRef struct {
	ProductFamily      string `json:"product_family"`
	RateSheetVersionID string `json:"rate_sheet_version_id"`
}

// manifest mirrors the subset of pack.yaml the validator needs.
type manifest struct {
	PackID      string            `json:"pack_id"`
	PackVersion string            `json:"pack_version"`
	Namespace   string            `json:"namespace"`
	SchemaPins  map[string]string `json:"schema_pins"`
}

// Load reads a pack directory and returns its query-ready view. A missing
// required file is a hard error (fail-loud, never a silent skip — ADR-PC-007).
func Load(dir string) (*Pack, error) {
	ctx := cuecontext.New()

	var m manifest
	if err := decodeYAML(ctx, filepath.Join(dir, "pack.yaml"), &m); err != nil {
		return nil, fmt.Errorf("pack manifest: %w", err)
	}
	if m.PackID == "" || m.PackVersion == "" {
		return nil, fmt.Errorf("pack.yaml missing pack_id/pack_version")
	}

	p := &Pack{
		Dir:        dir,
		ID:         m.PackID,
		Version:    m.PackVersion,
		Key:        m.PackID + "." + m.PackVersion,
		Namespace:  m.Namespace,
		SchemaPins: m.SchemaPins,
	}

	var err error
	if p.DayCounts, err = decodeKeySet(ctx, filepath.Join(dir, "primitives", "day-count.yaml")); err != nil {
		return nil, err
	}
	if p.Withholdings, err = decodeKeySet(ctx, filepath.Join(dir, "primitives", "withholding.yaml")); err != nil {
		return nil, err
	}

	p.Reporting = map[string]ReportingHook{}
	if err := decodeYAML(ctx, filepath.Join(dir, "primitives", "reporting.yaml"), &p.Reporting); err != nil {
		return nil, fmt.Errorf("reporting primitives: %w", err)
	}

	p.Params = map[string]int64{}
	if err := decodeYAML(ctx, filepath.Join(dir, "parameters", "constants.yaml"), &p.Params); err != nil {
		return nil, fmt.Errorf("parameters: %w", err)
	}

	// Rate-sheet refs may be split across one file per sheet; v1 ships one.
	refDir := filepath.Join(dir, "rate-sheet-refs")
	entries, err := os.ReadDir(refDir)
	if err != nil {
		return nil, fmt.Errorf("rate-sheet-refs: %w", err)
	}
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".yaml" {
			continue
		}
		var doc struct {
			Refs []RateSheetRef `json:"refs"`
		}
		if err := decodeYAML(ctx, filepath.Join(refDir, e.Name()), &doc); err != nil {
			return nil, fmt.Errorf("rate-sheet-refs/%s: %w", e.Name(), err)
		}
		p.RateSheetRefs = append(p.RateSheetRefs, doc.Refs...)
	}

	return p, nil
}

// RateRolesFor returns the set of rate-sheet roles available to a product
// family. v1 keys a rate sheet on a single version id per family; the role
// surface is "does a rate sheet exist for this family", which the rate-ref's
// role_selector resolves against at constitution (the numeric resolution is
// C.6 / deploy-time, not commit-time — see depth-3 scope note).
func (p *Pack) HasRateSheetFor(family string) bool {
	for _, r := range p.RateSheetRefs {
		if r.ProductFamily == family {
			return true
		}
	}
	return false
}

// ActiveReportingHooks returns the hooks the pack marks active — the ones a
// variant must not bypass (depth-3 pack compliance).
func (p *Pack) ActiveReportingHooks() []string {
	var out []string
	for name, h := range p.Reporting {
		if h.Active {
			out = append(out, name)
		}
	}
	return out
}

// decodeYAML extracts a YAML file through CUE and decodes it into target.
func decodeYAML(ctx *cue.Context, path string, target any) error {
	src, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	file, err := cueyaml.Extract(path, src)
	if err != nil {
		return err
	}
	v := ctx.BuildFile(file)
	if err := v.Err(); err != nil {
		return err
	}
	return v.Decode(target)
}

// decodeKeySet decodes a top-level YAML map and returns its key set — used for
// the primitive catalogues, where the map key (e.g. act_360) is the reference
// id a variant names.
func decodeKeySet(ctx *cue.Context, path string) (map[string]bool, error) {
	var m map[string]any
	if err := decodeYAML(ctx, path, &m); err != nil {
		return nil, fmt.Errorf("%s: %w", filepath.Base(path), err)
	}
	set := make(map[string]bool, len(m))
	for k := range m {
		set[k] = true
	}
	return set, nil
}
