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
	"io"
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

	DayCounts       map[string]DayCount      // primitives in primitives/day-count.yaml, keyed by reference id
	Withholdings    map[string]bool          // primitive keys in primitives/withholding.yaml
	Reporting       map[string]ReportingHook // hooks in primitives/reporting.yaml
	RenewalPolicies map[string]RenewalPolicy // restrictions in primitives/renewal-policies.yaml
	Params          map[string]int64         // scalars in parameters/constants.yaml
	RateSheetRefs   []RateSheetRef           // refs in rate-sheet-refs/deposits-pt.yaml
}

// DayCount mirrors one entry of primitives/day-count.yaml. PermittedFor is the
// pack-declared regulatory permitted-set: the product families this day-count
// may be used by (02 §2.2 — PT term deposits require act_360). depth-4 reads it
// rather than a hardcoded validator map.
type DayCount struct {
	FormulaRef   string   `json:"formula_ref"`
	PermittedFor []string `json:"permitted_for"`
}

// PermitsDayCountFor reports whether the pack declares day-count key permitted
// for product family. A day-count the pack does not carry returns false (its
// catalogue membership is a separate depth-2 check).
func (p *Pack) PermitsDayCountFor(key, family string) bool {
	dc, ok := p.DayCounts[key]
	if !ok {
		return false
	}
	for _, f := range dc.PermittedFor {
		if f == family {
			return true
		}
	}
	return false
}

// HasDayCount reports whether the pack carries a day-count primitive under key
// (the depth-2 catalogue-membership check).
func (p *Pack) HasDayCount(key string) bool {
	_, ok := p.DayCounts[key]
	return ok
}

// PermittedDayCountsFor returns the sorted reference ids the pack declares
// permitted for product family — used for the depth-4 diagnostic message.
func (p *Pack) PermittedDayCountsFor(family string) []string {
	var out []string
	for key, dc := range p.DayCounts {
		for _, f := range dc.PermittedFor {
			if f == family {
				out = append(out, key)
				break
			}
		}
	}
	sortStrings(out)
	return out
}

// sortStrings is a tiny insertion sort — small sets, deterministic for
// diagnostic messages, no import of sort needed for one call site.
func sortStrings(s []string) {
	for i := 1; i < len(s); i++ {
		for j := i; j > 0 && s[j-1] > s[j]; j-- {
			s[j-1], s[j] = s[j], s[j-1]
		}
	}
}

// RenewalPolicy mirrors one entry of primitives/renewal-policies.yaml. PermittedFor
// is the pack-declared regulatory permitted-set for a RESTRICTED auto-renewal policy
// (02 §2.4.4 — SAME_TERM_SAME_RATE is "less common, pack-restricted"): the product
// families that may use it. The same `permitted_for` shape day-count.yaml uses, so the
// regulatory rule is auditor-visible in the signed pack rather than a hardcoded Go map.
type RenewalPolicy struct {
	Description  string   `json:"description"`
	PermittedFor []string `json:"permitted_for"`
}

// PermitsRenewalPolicyFor reports whether the pack declares the auto-renewal policy key
// permitted for product family. A policy the pack does not carry a restriction entry for
// returns false — only the RESTRICTED policies (SAME_TERM_SAME_RATE) declare a
// permitted-set; the unrestricted ones (NONE, SAME_TERM_CURRENT_RATE) are never checked
// against this map by the caller.
func (p *Pack) PermitsRenewalPolicyFor(key, family string) bool {
	rp, ok := p.RenewalPolicies[key]
	if !ok {
		return false
	}
	for _, f := range rp.PermittedFor {
		if f == family {
			return true
		}
	}
	return false
}

// HasRenewalRestriction reports whether the pack carries a restriction entry for the
// auto-renewal policy key. A policy with no entry is UNRESTRICTED (every family may use
// it); only a policy with an entry is gated by PermitsRenewalPolicyFor.
func (p *Pack) HasRenewalRestriction(key string) bool {
	_, ok := p.RenewalPolicies[key]
	return ok
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
	p.DayCounts = map[string]DayCount{}
	if err := decodeYAML(ctx, filepath.Join(dir, "primitives", "day-count.yaml"), &p.DayCounts); err != nil {
		return nil, fmt.Errorf("day-count primitives: %w", err)
	}
	if p.Withholdings, err = decodeKeySet(ctx, filepath.Join(dir, "primitives", "withholding.yaml")); err != nil {
		return nil, err
	}

	p.Reporting = map[string]ReportingHook{}
	if err := decodeYAML(ctx, filepath.Join(dir, "primitives", "reporting.yaml"), &p.Reporting); err != nil {
		return nil, fmt.Errorf("reporting primitives: %w", err)
	}

	// renewal-policies.yaml declares auto-renewal-policy restrictions (02 §2.4.4). It is
	// OPTIONAL: a pack predating the F.5 follow-up (bd k6r8.6) carries no file, which means
	// "no policy is restricted" (every structurally-allowed policy is permitted) — the
	// fail-OPEN default for a pre-restriction pack, so loading such a pack never errors. A
	// present file narrows the set; an absent one leaves RenewalPolicies empty.
	p.RenewalPolicies = map[string]RenewalPolicy{}
	renewalPath := filepath.Join(dir, "primitives", "renewal-policies.yaml")
	if _, statErr := os.Stat(renewalPath); statErr == nil {
		if err := decodeYAML(ctx, renewalPath, &p.RenewalPolicies); err != nil {
			return nil, fmt.Errorf("renewal-policy primitives: %w", err)
		}
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

// maxPackFileBytes caps the size of any single pack-source file before it is
// handed to the CUE front-end. CUE's parse + BuildFile cost is superlinear in
// the number of nodes, so an unbounded file is a denial-of-service vector on
// this non-engineer-authored, highest-churn surface: a multi-megabyte garbage
// file would take CUE seconds-to-minutes to chew on. Real pack files are
// kilobytes (the largest in pt.2026.1 is ~1.3 KB); 1 MiB is ~750x that — ample
// headroom for any legitimate pack, yet it turns the pathological large input
// into a clean, fast, returned error rather than a hang (ADR-PC-007 §169: the
// engine refuses unparseable packs).
const maxPackFileBytes = 1 << 20 // 1 MiB

// decodeYAML extracts a YAML file through CUE and decodes it into target. A
// pathological input (oversized, or one that makes the CUE front-end panic) is
// reported as a returned error — never a hang or a process crash.
func decodeYAML(ctx *cue.Context, path string, target any) (err error) {
	src, err := readBounded(path, maxPackFileBytes)
	if err != nil {
		return err
	}
	// Belt-and-braces: a malformed document that trips a panic deep in the CUE
	// YAML/build path becomes a clean rejection rather than crashing the binary.
	defer func() {
		if r := recover(); r != nil {
			err = fmt.Errorf("%s: malformed YAML rejected (internal parse failure: %v)", filepath.Base(path), r)
		}
	}()
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

// readBounded reads up to limit bytes from path and rejects (without loading
// the whole file) anything larger, so an oversized pack file never reaches the
// CUE front-end.
func readBounded(path string, limit int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	// Read limit+1 so a file exactly at the cap is accepted and the first byte
	// over it is detected.
	src, err := io.ReadAll(io.LimitReader(f, limit+1))
	if err != nil {
		return nil, err
	}
	if int64(len(src)) > limit {
		return nil, fmt.Errorf("%s: pack file exceeds %d-byte limit (pathological or non-pack input rejected)", filepath.Base(path), limit)
	}
	return src, nil
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
