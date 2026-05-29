package main

import (
	"reflect"
	"testing"
)

func TestReorderFlagsFirst(t *testing.T) {
	cases := []struct {
		name string
		in   []string
		want []string
	}{
		{"flags after positional", []string{"v.yaml", "--pack", "p", "--format", "json"},
			[]string{"--pack", "p", "--format", "json", "v.yaml"}},
		{"flags before positional", []string{"--pack", "p", "v.yaml"},
			[]string{"--pack", "p", "v.yaml"}},
		{"inline =", []string{"v.yaml", "--format=json"},
			[]string{"--format=json", "v.yaml"}},
		{"end-of-flags --", []string{"--pack", "p", "--", "-weird.yaml"},
			[]string{"--pack", "p", "-weird.yaml"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := reorderFlagsFirst(tc.in); !reflect.DeepEqual(got, tc.want) {
				t.Errorf("reorderFlagsFirst(%v) = %v, want %v", tc.in, got, tc.want)
			}
		})
	}
}

// TestRunExitCodes — 0 conforms, 1 diagnostics, 2 usage/toolchain.
func TestRunExitCodes(t *testing.T) {
	const valid = "../contracts/cue/testdata/term-deposit/valid/flat-at-maturity.yaml"
	const pack = "../packs/pt.2026.1"
	const act365 = "testdata/term-deposit/invalid/depth4-act365-deposit.yaml"

	cases := []struct {
		name string
		args []string
		want int
	}{
		{"no args", nil, 2},
		{"version", []string{"version"}, 0},
		{"unknown subcommand", []string{"bogus"}, 2},
		{"bad format", []string{"syntactic", valid, "--format", "xml"}, 2},
		{"missing variant", []string{"regulatory", "--pack", pack}, 2},
		{"valid conforms", []string{"regulatory", valid, "--pack", pack, "--format", "json"}, 0},
		{"invalid rejected", []string{"regulatory", act365, "--pack", pack, "--format", "json"}, 1},
		{"syntactic no pack", []string{"syntactic", valid}, 0},
		{"depth ≥2 requires pack", []string{"type", valid}, 2},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := run(tc.args); got != tc.want {
				t.Errorf("run(%v) = %d, want %d", tc.args, got, tc.want)
			}
		})
	}
}
