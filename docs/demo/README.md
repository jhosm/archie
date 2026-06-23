# Babelstone — investor demo

This folder holds the two-part babelstone demo: a self-contained HTML **slide deck**
(`index.html`) and the live **Mission Control** UI (`mission-control/`). Slide 5 of the deck is
the cue to cut to the UI.

## In plain English

This is the pitch for babelstone. The deck is a single web page that looks polished, runs in any
browser, and exports to PDF; the story leads with the wow (an AI can operate the bank), proves it
with correctness (it can't double-book or miscompute tax), and defends it with engineering rigor
(the moat). The Mission Control UI then shows the real bank working — constitute a deposit, watch
the immutable events stream in, mature it, and see the money + the OpenTelemetry trace — and, with
the real-Claude agent, watch a model itself drive those operations through the MCP tools.

## Running the full demo

The full demo is the deck **plus** the UI driving the real backend. Run from the repo root; needs
Docker, the pinned toolchain (`make doctor`), and a browser (macOS-tested). The UI's modes,
per-mode bring-up, env vars, and gotchas live in **`mission-control/README.md`** — this is the
presenter's choreography on top of it.

**1. Bring it up.** Pick the path you want to show:

- `ANTHROPIC_API_KEY=sk-ant-… make demo-agent` — **the strongest**: engine + MCP server + the
  real-Claude **agent host** + the UI, one command. A real model operates the bank.
- `make demo-mcp` then `python3 docs/demo/mission-control/serve.py` — the engine direct (**LIVE·engine**).
- `make demo-saga` then `serve.py` — the constitution saga end to end (**LIVE·saga**).

(DEMO mode needs none of this — see *Stage safety* below.)

**2. Open both surfaces.** Deck — `open docs/demo/index.html`, then `f` for fullscreen. UI —
http://localhost:9000, then flip the **Mode** toggle to **LIVE·engine** / **LIVE·saga** (the LED
turns green when the backend is reachable).

**3. Present.** Run the deck to **slide 5 ("Watch this")**, cut to the UI, and drive the beats —
**Constitute**, **Retry — same key** (idempotency, no second event), **Mature** (payout
**€10,219.00**), **Operator → CLAUDE** (a real model calls the MCP tools — or an illustrative
narration if the agent host is down), **Telemetry → ON** (the real OpenTelemetry trace), and the
**monthly / advance** product variants. Then return to the deck for slides 6–11. Each beat's
mechanics (and the ADR refs) are in `mission-control/README.md`.

**4. Tear down.** `make demo-agent-down` (or `make demo-mcp-down` / `make demo-saga-down`), then
`make down` to stop the Docker infra.

**Stage safety.** If the laptop or network misbehaves, flip the UI **Mode** toggle to **DEMO** —
fully deterministic, no backend, the same numbers the engine computes. You can give the entire
walkthrough in DEMO and never bring up a backend.

## Presenting the deck

Just open `index.html` in a browser — no server, no build, no dependencies to install.

```
open docs/demo/index.html
```

| Key | Action |
| --- | --- |
| `→` / `Space` / click right | next slide |
| `←` / click far-left | previous slide |
| `f` | toggle fullscreen |
| `n` | toggle **presenter notes** (a per-slide overlay with what to say) |
| `Home` / `End` | first / last slide |

Two slides are interactive (slide 6, "the proof"): the payout figures **count up** on entry,
and the **Constitute** button demonstrates idempotency — click it twice to show the duplicate
being caught.

## Exporting a PDF leave-behind

`Cmd-P` → *Save as PDF*. Print CSS lays it out one clean slide per page (animations frozen,
chrome hidden). Set the paper to landscape if your browser doesn't pick up the page size.

## The narrative (12 slides)

1. **Title** — "Banking software that's correct by construction."
2. **Problem** — core banking is a haunted house.
3. **Insight** — immutable ledger + pure math = determinism.
4. **Twist** — so safe an AI can run it (MCP).
5. **Demo** — cut to the live Mission Control UI.
6. **Proof** — idempotency, flow-by-flow tax, deterministic replay (+ the worked payout).
7. **Rigor** — drift gates, generated docs, mutation testing, signed packs.
8. **Moat** — the combination is hard to copy.
9. **Status** *(placeholder)* — the hot path has no stubs; what's next.
10. **Ask / vision** *(placeholder)* — to be filled with the real ask.
11. **Appendix · architecture** — every piece on one board: a center stack (edge → ingress →
    families → math → event store → bus → saga → boundary) flanked by cross-cutting rails
    (Observability, Security/PII, Contracts/Governance). Honest status dots: ●live (incl. the
    orchestrator — saga consume loop host-wired), ◐partial (Security/PII), ○planned (ACL +
    notification still skeletons).
12. **Appendix · two ways to grow** — the same board re-lit to show what a change touches: a
    NEW FAMILY lights four boxes (Product Families, Engine API host module, Contracts/Governance,
    plus one-line additive wiring — ADR-PC-021 §D4 family-count-invariant composition), while a
    NEW VARIANT touches no box (product-config YAML + rate-sheet row + pack pin — pure data).

## Editing

It's one file (`index.html`) with the design tokens as CSS custom properties at the top
(`:root`), so the palette and type can be lifted straight into the Mission Control UI later for
a shared design language. Each slide is a `<section class="slide">`; presenter notes live in its
`data-notes` attribute.

## Still to do

- Real figures and framing for slides 9 (status) and 10 (the ask).

The Mission Control UI that slide 5 hands off to lives in `mission-control/` — see its README for
the three modes (DEMO / LIVE·engine / LIVE·saga), the real-Claude agent (Operator → CLAUDE), the
telemetry tab, and the product variants.
