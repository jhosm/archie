using System.Text.RegularExpressions;
using Babelstone.EventStore.Migrations;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC — the SCHEMA-level twin of
/// <see cref="EngineFamilyAgnosticTests"/> (ADR-PC-021 §P2 / §D2, commitment-catalogue row 12).
///
/// The code-level test proves the engine *spine* carries no <c>ProjectReference</c> into a
/// family. This one proves the engine event-store *schema* stays family-agnostic too: events are
/// kept OPAQUE — keyed by the generic <c>family</c> / <c>event_type</c> columns, with the payload
/// an opaque <c>BYTEA</c> — so a new product family adds rows, never a column or a table. A
/// family-typed column or table leaking into the write-side schema (a <c>maturity_date</c> column,
/// a <c>coupons</c> table, a <c>deposit</c> foreign key) would be the schema-shaped erosion of the
/// same family-agnosticism the <c>.csproj</c> gate guards at the dependency level.
///
/// It reads the SAME SQL the runner applies — <see cref="MigrationSet.All"/>, the embedded
/// <c>Sql/NNNN_name.sql</c> resources <see cref="MigrationRunner"/> enumerates and executes
/// (no loose-file lookup, so the gate cannot drift from what ships). The check is infrastructure-
/// free and deterministic: it parses the DDL text, never stands up a database. Same disk/embedded-
/// resource discipline and high comment density as the sibling fitness tests.
///
/// SCOPE — the ENTIRE engine migration set. The engine now owns ZERO family tables: the read-side
/// CQRS surface (the <c>read_model.deposits</c> table, formerly engine migration 0013) was RELOCATED
/// to a term-deposit FAMILY-owned migration set (ADR-PC-021 family-owned ownership). So this gate no
/// longer carves out a read-side schema — every deny scan runs over the whole engine
/// <see cref="MigrationSet.All"/> with no exclusion, and an inverse positive guard
/// (<see cref="No_engine_migration_creates_a_read_model_schema_or_deposits_object"/>) RED-fails if a
/// family-named read model is ever re-introduced into the engine set.
/// </summary>
public sealed class EventStoreSchemaFamilyAgnosticTests
{
    /// <summary>
    /// Tokens that name a concrete product-family DOMAIN. A write-side table/column/FK identifier
    /// containing one of these is a family leak: the event store must not know what a "deposit" or
    /// a "coupon" is — those are the family's, carried OPAQUELY inside <c>events.payload</c> and
    /// keyed only by the generic <c>family</c> / <c>event_type</c> columns (see
    /// <see cref="AllowedGenericKeyColumns"/>). Drawn from the term-deposit reference family's
    /// vocabulary (its events, folds, and lifecycle), broadened toward the sibling banking nouns a
    /// second family would bring (<c>tranche</c>, <c>heir</c>), so an off-the-original-list family
    /// name is still caught.
    ///
    /// This is a NAME HEURISTIC over DDL identifiers, the same posture as the sibling
    /// <c>EmitContractFitnessTests</c> clock-driven / PII scans: it pattern-matches identifier
    /// substrings and cannot prove a non-obviously-named column is family-neutral. It is the cheap
    /// structural tripwire that fails fast at PR time; the authoritative guarantee remains the
    /// opaque-payload event-store contract (ADR-PC-001 §P1) and the <c>ENGINE_FAMILY_AGNOSTIC</c>
    /// dependency gate. Each token below is matched on a WORD boundary (see
    /// <see cref="IdentifierNamesFamilyDomain"/>) so a generic identifier that merely embeds the
    /// letters (e.g. <c>partition_key</c> does not match <c>tan</c>) does not false-RED.
    /// </summary>
    private static readonly string[] FamilyDomainTokens =
    [
        "deposit", "term_deposit", "coupon", "withholding", "accrual", "maturity",
        "tranche", "principal", "payout", "renewal", "heir", "tan",
    ];

    /// <summary>
    /// The generic, family-NEUTRAL key columns the opaque event store is allowed to carry — the
    /// "keyed by generic columns" half of ADR-PC-001 §P1's opaque-event contract. <c>family</c> and
    /// <c>event_type</c> are the discriminators that let the spine dispatch ANY family's event
    /// without naming one (mirroring how the Avro codec binds an event to its <c>.avsc</c> by
    /// convention, ADR-PC-021 §P2). They are explicitly NOT family leaks even though a family NAME
    /// rides in their VALUES at runtime ('term_deposit'); the schema only declares the generic
    /// column, never a per-family column. <c>family</c> as a SUBSTRING ("family-prefixed", a comment)
    /// is already excluded by comment-stripping; this set additionally whitelists the columns
    /// themselves so the deny scan can never flag them.
    /// </summary>
    private static readonly string[] AllowedGenericKeyColumns =
        ["family", "event_type"];

    /// <summary>
    /// No engine migration table NAME may carry a family-domain token. The event store's tables are
    /// the generic spine vocabulary (<c>events</c>, <c>outbox</c>, <c>snapshots</c>,
    /// <c>projections</c>, <c>inbox</c>, …); a <c>deposits</c> / <c>coupons</c> /
    /// <c>maturity_calendar</c> table in the engine set would be a family leak. The read model
    /// (<c>read_model.deposits</c>) is no longer here — it is family-owned (ADR-PC-021) — so the
    /// whole engine set is scanned with no exclusion.
    /// </summary>
    [Fact]
    public void No_engine_table_name_is_family_typed()
    {
        var tables = EngineTableNames();

        // Non-vacuity: the parse must actually find the spine's tables. If this drops to (near)
        // zero the regex broke and the deny scan would pass vacuously — fail loud instead.
        Assert.True(
            tables.Count >= 6,
            $"expected to parse the engine spine tables from the migration set, found only "
            + $"[{string.Join(", ", tables.OrderBy(t => t))}]; the CREATE TABLE parse likely broke.");

        var violations = tables
            .Select(t => (table: t, token: MatchedFamilyToken(t)))
            .Where(x => x.token is not null)
            .Select(x => $"table '{x.table}' (family token '{x.token}')")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 §P2 / ADR-PC-001 §P1 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): no engine "
            + "migration table may be family-typed — events stay opaque, keyed by the generic "
            + "family/event_type columns with the payload an opaque BYTEA. A family-named table "
            + "(including a read model like read_model.deposits) belongs in a family-owned migration "
            + "set, never the engine's. Offending tables:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// No engine migration column NAME (from <c>CREATE TABLE</c> or <c>ALTER TABLE … ADD COLUMN</c>)
    /// may carry a family-domain token. The structural columns are the generic envelope (event_id,
    /// stream_id, sequence_number, family, event_type, payload, …, ADR-PC-001 §P1); a
    /// <c>maturity_date</c> / <c>coupon_count</c> / <c>withholding_cents</c> column would push
    /// family semantics into the engine store. The generic key columns family/event_type are
    /// explicitly allowed (<see cref="AllowedGenericKeyColumns"/>); the read model is no longer here
    /// (family-owned, ADR-PC-021), so the whole engine set is scanned with no exclusion.
    /// </summary>
    [Fact]
    public void No_engine_column_name_is_family_typed()
    {
        var columns = EngineColumns();

        // Non-vacuity: the spine declares dozens of columns (events alone has 16). A tiny count
        // means the column parse broke and the deny scan would pass vacuously.
        Assert.True(
            columns.Count >= 20,
            $"expected to parse the engine spine columns from the migration set, found only "
            + $"{columns.Count}; the column parse likely broke.");

        // The generic key columns are the opaque-keying mechanism, not a family leak — exclude them
        // before the deny scan so they can never be flagged (family/event_type are §P1 envelope keys).
        var violations = columns
            .Where(c => !AllowedGenericKeyColumns.Contains(c.Column, StringComparer.Ordinal))
            .Select(c => (c.Table, c.Column, token: MatchedFamilyToken(c.Column)))
            .Where(x => x.token is not null)
            .Select(x => $"{x.Table}.{x.Column} (family token '{x.token}')")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 §P2 / ADR-PC-001 §P1 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): no engine "
            + "migration column may be family-typed — the event store carries the generic envelope "
            + "columns and an opaque payload, never per-family structural columns. The generic family/"
            + "event_type keys ARE allowed (that is the opaque keying). Move a family-typed column "
            + "into a family-owned projection/migration. Offending columns:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// No engine-migration foreign key may TARGET a family-typed table. A <c>REFERENCES deposits(id)</c>
    /// would couple the opaque event store to a family table even if the referencing column itself
    /// were generically named. The spine carries no cross-table FKs into family tables today (the
    /// event log is a flat append-only relation, ADR-PC-001 §P1); this gate keeps it that way. The
    /// read model is no longer here (family-owned, ADR-PC-021), so the whole engine set is scanned.
    /// </summary>
    [Fact]
    public void No_engine_foreign_key_targets_a_family_typed_table()
    {
        var fkTargets = EngineForeignKeyTargetTables();

        var violations = fkTargets
            .Select(t => (table: t, token: MatchedFamilyToken(t)))
            .Where(x => x.token is not null)
            .Select(x => $"REFERENCES '{x.table}' (family token '{x.token}')")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 §P2 / ADR-PC-001 §P1 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): no engine "
            + "migration foreign key may target a family-typed table — that would couple the opaque "
            + "event store to a family's relational shape. Offending references:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>
    /// INVERSE POSITIVE GUARD — the proof the relocation holds. No engine migration may create the
    /// family-named <c>read_model</c> schema or a <c>deposits</c>-named object (table/index). The
    /// read model was RELOCATED to a term-deposit family-owned migration set (ADR-PC-021 family-owned
    /// ownership); re-introducing it into the engine set — a regression that would put a family table
    /// back where this gate proves none lives — RED-fails here. This complements the three deny scans
    /// above: they catch any family-typed identifier by heuristic; this pins the SPECIFIC artefacts
    /// that were moved, so a literal re-add (a copy-back of the old 0013) is caught even if a future
    /// refactor loosened the heuristic.
    /// </summary>
    [Fact]
    public void No_engine_migration_creates_a_read_model_schema_or_deposits_object()
    {
        var allSql = StripSqlComments(string.Concat(MigrationSet.All.Select(m => m.Sql + "\n")));

        Assert.False(
            Regex.IsMatch(allSql, @"\bCREATE\s+SCHEMA\b[^;]*\bread_model\b", RegexOptions.IgnoreCase),
            "ADR-PC-021 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): an engine migration creates the "
            + "family-named 'read_model' schema. The read model is FAMILY-OWNED — it belongs in the "
            + "term-deposit family's migration set (Babelstone.Families.TermDeposit.Application."
            + "Migrations), not the engine's. The engine event-store migrations must carry zero "
            + "family-named tables.");

        // A `deposits`-named object (CREATE TABLE/INDEX … deposits…) anywhere in the engine set is the
        // relocated family table sneaking back. The deny scans above already catch the `deposits`
        // token; this is the literal-artefact backstop, naming the specific moved object.
        Assert.False(
            Regex.IsMatch(allSql, @"\bCREATE\s+(?:TABLE|INDEX)\b[^;]*\bdeposits\b", RegexOptions.IgnoreCase),
            "ADR-PC-021 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): an engine migration creates a "
            + "'deposits'-named object (the relocated family read model). read_model.deposits is "
            + "family-owned (ADR-PC-021); the engine event-store migrations must carry zero "
            + "family-named tables.");
    }

    // -----------------------------------------------------------------------------------------
    // Parsing helpers — all operate over the COMMENT-STRIPPED migration text, so a family name that
    // appears only in a SQL comment (e.g. "family-prefixed, e.g. 'term_deposit.deposit_position'" in
    // 0010/0011/0012) cannot trip any deny scan. Only executable DDL identifiers are examined. The
    // engine now owns ZERO family tables (the read model is family-owned, ADR-PC-021), so there is no
    // read-side exclusion: the whole engine MigrationSet.All is scanned.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Every <c>CREATE TABLE</c> table name in the engine migration set. Schema-qualified names are
    /// split to their bare object name (<c>a.b</c> → <c>b</c>); the bare table name is what the deny
    /// scan sees. The engine set carries no family-named table — that is what this gate proves.
    /// </summary>
    private static IReadOnlyList<string> EngineTableNames()
    {
        var sql = EngineSql();
        var names = new List<string>();

        // CREATE TABLE [IF NOT EXISTS] [schema.]name ( — capture the (optionally schema-qualified)
        // identifier, reduced to its bare object name. The engine set carries no schema-qualified
        // family table (the read model is family-owned, ADR-PC-021), so every name here is a generic
        // spine table.
        foreach (Match m in Regex.Matches(
            sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z_][\w.]*)", RegexOptions.IgnoreCase))
        {
            names.Add(BareName(m.Groups[1].Value));
        }

        return names;
    }

    /// <summary>
    /// Every (table, column) pair declared by a <c>CREATE TABLE</c> body or an
    /// <c>ALTER TABLE … ADD COLUMN</c> in the engine migration set. For a <c>CREATE TABLE</c> it
    /// splits the parenthesised body on top-level commas and reads the leading identifier of each
    /// clause that is a column definition (skipping <c>CONSTRAINT</c> / table-level
    /// <c>PRIMARY KEY</c> / <c>UNIQUE</c> / <c>CHECK</c> / <c>FOREIGN KEY</c> clauses). For an
    /// <c>ALTER … ADD COLUMN</c> it reads the added column name. Keyed to the migrations' house DDL
    /// style; a column declared by an idiom this does not parse must be added knowingly (the
    /// non-vacuity guard in the caller catches a wholesale parse break). A column name wrapped in
    /// double-quotes (a quoted identifier, <c>"maturity_date" DATE</c>) is handled — the surrounding
    /// quotes are stripped so the bare name still reaches the deny scan.
    /// </summary>
    private static IReadOnlyList<(string Table, string Column)> EngineColumns()
    {
        var sql = EngineSql();
        var columns = new List<(string, string)>();

        // --- CREATE TABLE bodies ---
        // CREATE TABLE [IF NOT EXISTS] [schema.]name ( <body> )  — match the body via balanced-paren
        // scan from the opening '(' so a column-level CHECK '(...)' inside the body does not end it early.
        foreach (Match m in Regex.Matches(
            sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z_][\w.]*)\s*\(", RegexOptions.IgnoreCase))
        {
            var table = BareName(m.Groups[1].Value);
            var body = BalancedParenBody(sql, m.Index + m.Length - 1);
            if (body is null)
            {
                continue;
            }

            foreach (var clause in SplitTopLevel(body))
            {
                var trimmed = clause.Trim();
                if (trimmed.Length == 0 || IsTableLevelClause(trimmed))
                {
                    continue;
                }

                // The column name is the leading identifier of the clause. Tolerate an OPTIONAL
                // surrounding double-quote so a quoted identifier ("maturity_date" DATE) is caught
                // too — the bare name is what the deny scan must see. The leading '"' is consumed
                // here and the trailing one stripped, so the captured group is the unquoted name.
                var nameMatch = Regex.Match(trimmed, @"^""?([A-Za-z_]\w*)""?");
                if (nameMatch.Success)
                {
                    columns.Add((table, nameMatch.Groups[1].Value));
                }
            }
        }

        // --- ALTER TABLE ... ADD COLUMN ---
        // ALTER TABLE [schema.]name ADD COLUMN col  — capture the added column name.
        foreach (Match m in Regex.Matches(
            sql, @"ALTER\s+TABLE\s+([A-Za-z_][\w.]*)\s+ADD\s+COLUMN\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase))
        {
            columns.Add((BareName(m.Groups[1].Value), m.Groups[2].Value));
        }

        return columns;
    }

    /// <summary>
    /// Every table a <c>REFERENCES</c> clause in the engine migration set targets (the FK target
    /// table). Today the spine declares none — the event log is a flat append-only relation — so this
    /// returns empty, and the gate keeps a future family-targeting FK out.
    /// </summary>
    private static IReadOnlyList<string> EngineForeignKeyTargetTables()
    {
        var sql = EngineSql();
        var targets = new List<string>();

        // REFERENCES [schema.]table — the column-list that may follow is irrelevant to the target table.
        foreach (Match m in Regex.Matches(
            sql, @"REFERENCES\s+([A-Za-z_][\w.]*)", RegexOptions.IgnoreCase))
        {
            targets.Add(BareName(m.Groups[1].Value));
        }

        return targets;
    }

    /// <summary>
    /// The full engine migration-set SQL, COMMENT-STRIPPED. This is the single text every parse runs
    /// over, so the comment-strip is applied once, consistently. Read off
    /// <see cref="MigrationSet.All"/> — the SAME embedded resources <see cref="MigrationRunner"/>
    /// applies — so the gate sees exactly the shipped schema, deterministically and with no database.
    /// No read-side exclusion: the engine set owns zero family tables (the read model is family-owned,
    /// ADR-PC-021), so the whole set is in scope.
    /// </summary>
    private static string EngineSql()
    {
        Assert.NotEmpty(MigrationSet.All); // non-vacuity: the embedded migrations were discovered.

        return StripSqlComments(string.Concat(MigrationSet.All.Select(m => m.Sql + "\n")));
    }

    /// <summary>
    /// Strips SQL line comments (<c>-- …</c> to end of line) and block comments (<c>/* … */</c>) so
    /// a family name appearing only in prose (the illustrative <c>'term_deposit.deposit_position'</c>
    /// in the 0010/0011/0012 column comments) cannot trip an identifier deny scan — only executable
    /// DDL is examined. Mirrors <c>EmitContractFitnessTests.StripLineComments</c>, extended to SQL's
    /// <c>--</c> line form and <c>/* */</c> block form.
    /// </summary>
    private static string StripSqlComments(string sql)
    {
        var noBlock = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"--.*?$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>
    /// The forbidden family token an identifier carries, or null if none. Matches a token from
    /// <see cref="FamilyDomainTokens"/> on a WORD boundary within the (lowercased) identifier so a
    /// family noun is caught as a whole word or snake_case segment (<c>maturity_date</c>,
    /// <c>coupon_count</c>, <c>withholding_cents</c>) but a generic identifier that merely embeds the
    /// letters is not (<c>partition_key</c> does not match <c>tan</c>; <c>created_at</c> does not
    /// match any token).
    /// </summary>
    private static string? MatchedFamilyToken(string identifier)
        => FamilyDomainTokens.FirstOrDefault(token => IdentifierNamesFamilyDomain(identifier, token));

    /// <summary>
    /// True iff <paramref name="token"/> appears as a whole word / snake_case segment in
    /// <paramref name="identifier"/> (case-insensitive). The boundary is the snake_case <c>_</c> or
    /// the string ends — so <c>tan</c> matches <c>tan</c> and <c>tan_basis_points</c> but NOT
    /// <c>partition_key</c> or <c>instant</c>, and <c>deposit</c> matches <c>deposits</c> /
    /// <c>deposit_position</c> (prefix of a word) yet a token is never matched mid-word against an
    /// unrelated longer word.
    /// </summary>
    private static bool IdentifierNamesFamilyDomain(string identifier, string token)
        // (^|_) before the token and (_|s?$|[A-Za-z]) tolerance: anchor the token to a segment start
        // (string start or after '_') and allow it to be the whole segment, a plural, or the head of
        // a compound segment (deposit_position). \b alone would mis-handle the snake '_' (which is a
        // word char), so segment anchoring is explicit.
        => Regex.IsMatch(identifier, $@"(^|_){Regex.Escape(token)}(s?($|_)|[a-z])", RegexOptions.IgnoreCase);

    /// <summary>The bare object name from a possibly schema-qualified identifier (<c>a.b</c> → <c>b</c>).</summary>
    private static string BareName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }

    /// <summary>
    /// The text inside a balanced parenthesis run, given the index of the opening <c>(</c>. Counts
    /// nested parens so a column-level <c>CHECK (status IN (…))</c> inside a <c>CREATE TABLE</c> body
    /// does not terminate the body early. Returns null if the parens are unbalanced (defensive).
    /// </summary>
    private static string? BalancedParenBody(string sql, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < sql.Length; i++)
        {
            if (sql[i] == '(')
            {
                depth++;
            }
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return sql[(openParenIndex + 1)..i];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a <c>CREATE TABLE</c> body on TOP-LEVEL commas only — a comma inside a nested
    /// <c>(…)</c> (a multi-column constraint, a CHECK list) does not split a clause.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return body[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return body[start..];
    }

    /// <summary>
    /// True if a <c>CREATE TABLE</c> body clause is a TABLE-LEVEL constraint rather than a column
    /// definition — so the column parse skips it. A clause whose leading keyword is
    /// <c>CONSTRAINT</c> / <c>PRIMARY</c> / <c>UNIQUE</c> / <c>CHECK</c> / <c>FOREIGN</c> declares a
    /// constraint, not a column (the constraint NAME, e.g. <c>events_stream_seq_uq</c>, is not a
    /// column and must not be scanned as one).
    /// </summary>
    private static bool IsTableLevelClause(string clause)
        => Regex.IsMatch(clause, @"^(CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN)\b", RegexOptions.IgnoreCase);
}
