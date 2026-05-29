// Command pack-validate is the purpose-built Go validator for the engine's CUE
// family-schema language (ADR-PC-006). It embeds cuelang.org/go and exposes the
// four synchronous validator depths as subcommands, emitting the versioned JSON
// diagnostic contract that the PM author's pre-commit hook, the PR CI gate, and
// the .NET engine all consume.
//
//	pack-validate syntactic       <variant.yaml>                 # depth 1
//	pack-validate type            <variant.yaml> --pack <dir>    # depths 1→2
//	pack-validate pack-compliance <variant.yaml> --pack <dir>    # depths 1→3
//	pack-validate regulatory      <variant.yaml> --pack <dir>    # depths 1→4
//	pack-validate validate        <variant.yaml> --pack <dir>    # alias = regulatory
//	pack-validate version
//
// Each depth subcommand runs depths 1..N layered, stopping at the first depth
// that produces diagnostics. Exit 0 = conforms, 1 = diagnostics, 2 = usage or
// toolchain error.
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/jhosm/babelstone/pack-validate/internal/diag"
	"github.com/jhosm/babelstone/pack-validate/internal/validate"
)

const usage = `pack-validate — CUE family-schema validator (ADR-PC-006)

usage:
  pack-validate <depth-subcommand> <variant.yaml> [flags]
  pack-validate version

depth subcommands (each runs depths 1..N, layered):
  syntactic        depth 1 — variant parses + matches the schema's structural shape
  type             depths 1→2 — field types/ranges + pack-bound primitive resolution
  pack-compliance  depths 1→3 — variant respects the pinned pack's bounds/obligations
  regulatory       depths 1→4 — cross-field regulatory invariants
  validate         alias for 'regulatory' (the full depths-1→4 run)

flags:
  --pack <dir>        pinned regulatory pack directory (required for depths ≥ 2)
  --schema-dir <dir>  CUE family-schema dir (default: auto-discovered contracts/cue)
  --format json|human output format (default: human)
`

// subcommandDepth maps each subcommand to the maximum depth it runs through.
var subcommandDepth = map[string]diag.Depth{
	"syntactic":       diag.DepthSyntactic,
	"type":            diag.DepthType,
	"pack-compliance": diag.DepthPackCompliance,
	"regulatory":      diag.DepthRegulatory,
	"validate":        diag.DepthRegulatory,
}

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	if len(args) == 0 {
		fmt.Fprint(os.Stderr, usage)
		return 2
	}
	sub := args[0]
	if sub == "version" {
		fmt.Printf("pack-validate %s (diagnostic-contract v%s)\n", Version, diag.ContractVersion)
		return 0
	}

	maxDepth, ok := subcommandDepth[sub]
	if !ok {
		fmt.Fprintf(os.Stderr, "pack-validate: unknown subcommand %q\n\n%s", sub, usage)
		return 2
	}

	fs := flag.NewFlagSet(sub, flag.ContinueOnError)
	fs.Usage = func() { fmt.Fprint(os.Stderr, usage) }
	packDir := fs.String("pack", "", "pinned regulatory pack directory")
	schemaDir := fs.String("schema-dir", "", "CUE family-schema dir (default: auto-discovered contracts/cue)")
	format := fs.String("format", "human", "output format: json|human")
	// Permute so the variant positional may appear before or after the flags
	// (CI invokes `pack-validate validate <variant> --pack <dir>`); Go's flag
	// package otherwise stops at the first positional. All our flags take a
	// value, so a dash-token without '=' consumes the following token.
	if err := fs.Parse(reorderFlagsFirst(args[1:])); err != nil {
		return 2
	}
	if fs.NArg() != 1 {
		fmt.Fprintf(os.Stderr, "pack-validate %s: expected exactly one <variant.yaml>\n\n%s", sub, usage)
		return 2
	}
	variantPath := fs.Arg(0)
	if *format != "human" && *format != "json" {
		fmt.Fprintf(os.Stderr, "pack-validate: --format must be json or human\n")
		return 2
	}

	sd, err := resolveSchemaDir(*schemaDir, variantPath, *packDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "pack-validate: %v\n", err)
		return 2
	}

	report, err := validate.Run(validate.Options{
		VariantPath: variantPath,
		SchemaDir:   sd,
		PackDir:     *packDir,
		MaxDepth:    maxDepth,
	})
	if err != nil {
		fmt.Fprintf(os.Stderr, "pack-validate: %v\n", err)
		return 2
	}

	if *format == "json" {
		if err := report.WriteJSON(os.Stdout); err != nil {
			fmt.Fprintf(os.Stderr, "pack-validate: %v\n", err)
			return 2
		}
	} else {
		report.WriteHuman(os.Stdout)
	}
	if !report.OK {
		return 1
	}
	return 0
}

// resolveSchemaDir picks the CUE family-schema directory. Precedence:
//  1. explicit --schema-dir;
//  2. a schemas/ dir bundled inside the pack artefact (the digest-pinned copy
//     the engine validates against — ADR-PC-007 §P1);
//  3. auto-discovery of contracts/cue by walking up from the variant, then cwd
//     (the author/CI context, where the source-of-truth schemas live).
func resolveSchemaDir(explicit, variantPath, packDir string) (string, error) {
	if explicit != "" {
		return explicit, nil
	}
	if packDir != "" {
		if bundled := filepath.Join(packDir, "schemas"); isDir(bundled) {
			return bundled, nil
		}
	}
	starts := []string{}
	if abs, err := filepath.Abs(variantPath); err == nil {
		starts = append(starts, filepath.Dir(abs))
	}
	if cwd, err := os.Getwd(); err == nil {
		starts = append(starts, cwd)
	}
	for _, start := range starts {
		if found := walkUpFor(start, filepath.Join("contracts", "cue", "common.cue")); found != "" {
			return filepath.Dir(found), nil
		}
	}
	return "", fmt.Errorf("could not locate contracts/cue (pass --schema-dir)")
}

// walkUpFor walks up from dir looking for the relative marker path; returns the
// absolute marker path when found, else "".
func walkUpFor(dir, marker string) string {
	for {
		candidate := filepath.Join(dir, marker)
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return ""
		}
		dir = parent
	}
}

func isDir(p string) bool {
	info, err := os.Stat(p)
	return err == nil && info.IsDir()
}

// reorderFlagsFirst moves flag tokens (and the value each consumes) ahead of
// positionals so flags may be given in any position. Every flag in this CLI
// takes a value, so a "-"/"--" token without an inline '=' consumes the next
// token as its value.
func reorderFlagsFirst(args []string) []string {
	var flags, pos []string
	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--" { // explicit end-of-flags: rest are positional
			pos = append(pos, args[i+1:]...)
			break
		}
		if strings.HasPrefix(a, "-") && a != "-" {
			flags = append(flags, a)
			if !strings.Contains(a, "=") && i+1 < len(args) {
				flags = append(flags, args[i+1])
				i++
			}
			continue
		}
		pos = append(pos, a)
	}
	return append(flags, pos...)
}
