// scripts/rate-sheet-check.mjs — the rule engine behind rate-sheet-check.sh (bd babelstone-alfy).
//
// Reads a rate-sheet body as JSON on stdin (the pinned-js-yaml serialisation of the committed YAML)
// and asserts it has the shape POST /v1/rate-sheets would accept. It re-expresses, in JS, exactly the
// invariants the engine enforces — RateBandJsonConverter's per-band shape and RateSheetValidator's
// cross-band contiguity/exhaustiveness + pack bound — so a malformed committed sheet fails in CI on
// the PR rather than at the deploy boundary or first constitution (ADR-PC-008 §P1/§P2).
//
// Env in: RS_FILE (path, for messages), RS_EXPECTED_ID (filename sans .yaml; the version id must equal
// it), RS_MAX_BPS (pack ceiling, or "" to skip the bound check).
// Exit 0 = valid; exit 1 = one or more diagnostics printed.

const file = process.env.RS_FILE || "<stdin>";
const expectedId = process.env.RS_EXPECTED_ID || "";
const maxBpsRaw = process.env.RS_MAX_BPS || "";
const maxBps = maxBpsRaw === "" ? null : Number(maxBpsRaw);
const MIN_BPS = 0; // ADR-PC-008 §P2: the floor is 0 (a negative TAN is rejected at deploy).

let raw = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (d) => (raw += d));
process.stdin.on("end", () => {
  const diags = [];
  const add = (m) => diags.push(m);

  let body;
  try {
    body = JSON.parse(raw);
  } catch (e) {
    add(`not valid JSON after YAML serialisation: ${e.message}`);
    return report(diags);
  }

  // --- Envelope (the columns on the stored row; ADR-PC-008 §P1) ---
  const envelope = [
    "rate_sheet_version_id",
    "product_family",
    "pack_version",
    "effective_from",
    "approved_by",
    "approval_ref",
  ];
  for (const field of envelope) {
    const v = body[field];
    if (v === undefined || v === null || (typeof v === "string" && v.trim() === "")) {
      add(`envelope field '${field}' is required and must be non-empty (ADR-PC-008 §P1).`);
    }
  }
  // effective_from must be an ISO instant the DateTimeOffset binder accepts.
  if (typeof body.effective_from === "string" && body.effective_from.trim() !== "") {
    if (Number.isNaN(Date.parse(body.effective_from))) {
      add(`effective_from '${body.effective_from}' is not a parseable timestamp.`);
    }
  }
  // The filename is the version id (README: "the filename is the rate_sheet_version_id").
  if (expectedId && typeof body.rate_sheet_version_id === "string" &&
      body.rate_sheet_version_id !== expectedId) {
    add(`rate_sheet_version_id '${body.rate_sheet_version_id}' must equal the filename '${expectedId}' ` +
        `(the filename IS the version id — a new version is a new file).`);
  }

  // --- Body: products -> role -> bands ---
  const products = body.products;
  if (products === undefined || products === null || typeof products !== "object") {
    add(`'products' is required and must be a map of product_id -> role -> bands.`);
    return report(diags);
  }
  if (Object.keys(products).length === 0) {
    add(`rate sheet has no products.`);
  }

  for (const [productId, roles] of Object.entries(products)) {
    if (roles === null || typeof roles !== "object") {
      add(`product '${productId}' must be a map of role -> bands.`);
      continue;
    }
    if (Object.keys(roles).length === 0) {
      add(`product '${productId}' has no roles.`);
    }
    for (const [role, roleRates] of Object.entries(roles)) {
      validateBands(productId, role, roleRates, add);
    }
  }

  report(diags);
});

function isInt(n) {
  return typeof n === "number" && Number.isInteger(n);
}

function validateBands(productId, role, roleRates, add) {
  const where = `${productId}/${role}`;
  if (roleRates === null || typeof roleRates !== "object" || !Array.isArray(roleRates.bands)) {
    add(`${where}: must be an object with a 'bands' array.`);
    return;
  }
  const bands = roleRates.bands;
  if (bands.length === 0) {
    add(`${where}: no bands.`);
    return;
  }

  // Per-band shape (RateBandJsonConverter): principal_cents == [lower, upper], non-null non-negative
  // integer lower, upper either null or a strictly-greater integer; tan_basis_points an integer.
  const shaped = [];
  for (let i = 0; i < bands.length; i++) {
    const band = bands[i];
    const at = `${where} band ${i}`;
    if (band === null || typeof band !== "object") {
      add(`${at}: must be an object { principal_cents: [lower, upper], tan_basis_points: n }.`);
      continue;
    }
    const pc = band.principal_cents;
    if (!Array.isArray(pc) || pc.length !== 2) {
      add(`${at}: principal_cents must be exactly [lower, upper].`);
      continue;
    }
    const [lower, upper] = pc;
    if (!isInt(lower)) {
      add(`${at}: principal_cents lower bound must be a non-null integer.`);
      continue;
    }
    if (lower < 0) {
      add(`${at}: principal_cents lower bound ${lower} must be >= 0.`);
      continue;
    }
    if (upper !== null && !isInt(upper)) {
      add(`${at}: principal_cents upper bound must be null (open-ended top band) or an integer.`);
      continue;
    }
    if (upper !== null && upper <= lower) {
      add(`${at}: principal_cents upper bound ${upper} must be greater than lower bound ${lower}.`);
      continue;
    }
    if (!isInt(band.tan_basis_points)) {
      add(`${at}: tan_basis_points must be an integer.`);
      continue;
    }
    // Pack bound (ADR-PC-008 §P2), when the ceiling is known.
    if (maxBps !== null && (band.tan_basis_points < MIN_BPS || band.tan_basis_points > maxBps)) {
      add(`${at}: tan_basis_points ${band.tan_basis_points} is outside the pack-declared bounds [${MIN_BPS}, ${maxBps}].`);
    }
    shaped.push({ from: lower, to: upper });
  }

  // Cross-band contiguity + exhaustiveness (RateSheetValidator.ValidateBands): sorted by lower bound,
  // each band's upper meets the next band's lower; exactly the highest band is open-ended.
  if (shaped.length !== bands.length) {
    return; // a malformed band already failed; cross-band checks would be noise.
  }
  const sorted = [...shaped].sort((a, b) => a.from - b.from);
  for (let i = 0; i < sorted.length - 1; i++) {
    const cur = sorted[i];
    const next = sorted[i + 1];
    if (cur.to === null) {
      add(`${where}: an open-ended band (no upper bound) is not the highest band; higher bands are unreachable.`);
      break;
    }
    if (cur.to !== next.from) {
      const kind = cur.to < next.from ? "gap" : "overlap";
      add(`${where}: ${kind} between a band ending at ${cur.to} and a band starting at ${next.from}; bands must be contiguous and non-overlapping.`);
    }
  }
  if (sorted[sorted.length - 1].to !== null) {
    add(`${where}: the highest band must be open-ended (null upper bound) so the principal range is exhaustive; got upper bound ${sorted[sorted.length - 1].to}.`);
  }
}

function report(diags) {
  if (diags.length === 0) {
    process.exit(0);
  }
  for (const d of diags) {
    console.error(`  FAIL  ${d}`);
  }
  process.exit(1);
}
