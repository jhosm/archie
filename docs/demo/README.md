# Babelstone — investor demo

This folder holds the two-part babelstone demo: a self-contained HTML **slide deck**
(`index.html`) and the live **Mission Control** UI (`mission-control/`). Slide 5 of the deck is
the cue to cut to the UI.

## In plain English

This is the pitch for babelstone. The deck is a single web page that looks polished, runs in any
browser, and exports to PDF; the story leads with the wow (an AI can operate the bank), proves it
with correctness (it can't double-book or miscompute tax), and defends it with engineering rigor
(the moat). The Mission Control UI then shows the real bank working — constitute a deposit, watch
the immutable events stream in, mature it, and see the money + the OpenTelemetry trace.

## Running the live demo (end-to-end)

The full live demo is the deck **plus** the UI driving the real engine. Run everything from the
repo root. Prerequisites: Docker running, the pinned toolchain (`make doctor`), and a browser.
macOS-tested.

**1. Bring up the engine** (Postgres + migrations + rate sheet + engine on `:8080`, plus the MCP
server on `:8000`):

```bash
docker compose -f infra/compose.yaml down -v   # wipe any stale volume first (see "Gotchas")
scripts/demo-mcp.sh up
```

`up` self-checks a constitute → read → mature loop and prints the canonical numbers — if it
finishes green, the engine is healthy.

**2. Start the Mission Control UI** (serves the page and reverse-proxies `/v1/*` to the engine,
so the browser is same-origin and CORS never bites):

```bash
python3 docs/demo/mission-control/serve.py     # → http://localhost:9000
```

**3. Open both surfaces:**

- **Deck** — `open docs/demo/index.html`, then `f` for fullscreen.
- **UI** — http://localhost:9000, then flip the **Mode** toggle to **LIVE** (the LED turns green:
  "engine reachable").

**4. Present.** Run the deck to **slide 5 ("Watch this")**, then cut to the UI and drive the loop:

| In the UI (LIVE) | What it proves |
| --- | --- |
| **Constitute deposit** | a `DepositConstituted` event appears; the position folds out (ACTIVE, rate from the sheet) |
| **Retry — same key** | "duplicate caught · same commit · no second event" — idempotency (ADR-PC-029) |
| **Mature deposit** | `InterestAccrued → WithholdingApplied → DepositMatured`, payout **€10,219.00** |
| **Operator → CLAUDE** | the bottom drawer shows the **MCP tool calls** behind each action |
| **Telemetry → ON** | the **OpenTelemetry span waterfall** (real attributes, no PII) |
| **Product → monthly / advance** | the PERIODIC (coupons) and ADVANCE (interest upfront) variants |

Then return to the deck for slides 6–11 (proof, rigor, moat, status, ask, architecture appendix).

**5. Tear down:**

```bash
pkill -f serve.py
scripts/demo-mcp.sh down
docker compose -f infra/compose.yaml down       # add -v to also wipe the volume
```

**Stage safety:** if the laptop or network misbehaves, flip the UI **Mode** toggle to **DEMO** —
it's fully deterministic, needs no backend, and computes the same numbers the engine does. You can
run the entire walkthrough in DEMO and never touch steps 1–2.

### Gotchas

- **Always wipe the volume first** (`down -v`) on a returning machine. `demo-mcp.sh` skips
  migrations when the `events` table already exists, so a stale volume can miss newer tables
  (`command_dedup`, …) and the constitute call 500s. (Tracked: `babelstone-qotf`.)
- **Variants need a priced rate sheet** — the script's dev sheet prices all three products
  (`venc`/`mensais`/`antecip`); if you swap in a different sheet, PERIODIC/ADVANCE will 422 until
  it prices them. See `mission-control/README.md` for the LIVE-mode details.

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

## The narrative (11 slides)

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

## Editing

It's one file (`index.html`) with the design tokens as CSS custom properties at the top
(`:root`), so the palette and type can be lifted straight into the Mission Control UI later for
a shared design language. Each slide is a `<section class="slide">`; presenter notes live in its
`data-notes` attribute.

## Still to do

- Real figures and framing for slides 9 (status) and 10 (the ask).

The Mission Control UI that slide 5 hands off to lives in `mission-control/` — see its README for
the two modes (DEMO / LIVE), the telemetry tab, and the product variants.
