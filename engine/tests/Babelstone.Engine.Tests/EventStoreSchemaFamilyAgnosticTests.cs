using System.Text.RegularExpressions;
using Babelstone.EventStore.Migrations;
using Xunit;

namespace Babelstone.Engine.Tests;

/// <summary>
/// EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC — the SCHEMA-level twin of
/// <see cref="EngineFamilyAgnosticTests"/> (ADR-PC-021 §P2 / §D2, commitment-catalogue row 12a).
///
/// The code-level test proves the engine *spine* carries no <c>ProjectReference</c> into a
/// family. This one proves the engine event-store *schema* stays family-agnostic too: events are
/// kept OPAQUE — keyed by the generic <c>family</c> / <c>event_type</c> columns, with the payload
/// an opaque <c>BYTEA</c> — so a new product family adds rows, never a column or a table. A
/// family-typed table leaking into the write-side schema (a <c>deposits</c> table, a
/// <c>read_model</c> schema, a <c>maturity_date</c> column) would be the schema-shaped erosion of
/// the same family-agnosticism the <c>.csproj</c> gate guards at the dependency level.
///
/// It reads the SAME SQL the runner applies — <see cref="MigrationSet.All"/>, the embedded
/// <c>Sql/NNNN_name.sql</c> resources <see cref="MigrationRunner"/> enumerates and executes
/// (no loose-file lookup, so the gate cannot drift from what ships). The check is infrastructure-
/// free and deterministic: it parses the DDL text, never stands up a database.
///
/// DESIGN — ALLOWLIST primary (sound), denylist secondary (heuristic). The load-bearing invariant
/// is "the engine database creates ONLY its known generic spine objects". That is an ALLOWLIST
/// question, so the table-, schema-, and FK-target scans assert membership in a closed allowlist
/// (<see cref="AllowedEngineTables"/> / <see cref="AllowedEngineSchemas"/>). This is SOUND against a
/// FUTURE family: a <c>mortgages</c> or <c>cards</c> table mistakenly added to the engine set fails
/// because it is not a sanctioned spine object — the gate needs no knowledge of that family's
/// vocabulary, and adding a family never edits this test. The allowlist changes ONLY when the engine
/// itself legitimately gains a generic table — a deliberate, reviewed generic-engine change, exactly
/// where ADR-PC-021's open/closed boundary wants the friction (not per-family).
///
/// A name-based DENYLIST (<see cref="FamilyDomainTokens"/>) was the earlier approach. It is retained
/// only as a SECONDARY heuristic for the column scan (<see cref="No_engine_column_name_is_family_typed"/>),
/// where a per-table column allowlist would be high-churn: a family-typed column on a generic table
/// (an <c>ALTER TABLE events ADD COLUMN maturity_date</c>) is caught as a cheap tripwire. The denylist
/// is NOT sound — it only knows the term-deposit reference family's nouns, so a new family's column
/// (<c>escrow_balance</c>) would slip past it — hence it is explicitly the secondary layer, with the
/// allowlist carrying the soundness and the opaque-payload event-store contract (ADR-PC-001 §P1) the
/// authoritative guarantee.
///
/// SCOPE — the ENTIRE engine migration set. The engine owns ZERO family tables: the read-side CQRS
/// surface (<c>read_model.deposits</c>, formerly engine migration 0013) was RELOCATED to a
/// term-deposit FAMILY-owned migration set (ADR-PC-021 family-owned ownership). The table + schema
/// allowlists subsume the former inverse <c>read_model</c>/<c>deposits</c> guard: a re-added
/// <c>read_model</c> schema fails <see cref="Engine_migrations_create_no_unsanctioned_schema"/> and a
/// re-added <c>deposits</c> table fails <see cref="Engine_migrations_create_only_allowlisted_tables"/>.
/// </summary>
public sealed class EventStoreSchemaFamilyAgnosticTests
{
    /// <summary>
    /// PRIMARY (sound) — the closed set of generic spine tables the engine event-store migrations are
    /// allowed to create. Any <c>CREATE TABLE</c> in <see cref="MigrationSet.All"/> outside this set is
    /// a family leak, REGARDLESS of its name — that is what makes this sound against a future family
    /// whose vocabulary no denylist could anticipate. Keep this in lockstep with the engine's own DDL:
    /// when a migration legitimately adds a GENERIC spine table, add it here in the same change (a
    /// deliberate generic-engine edit); a FAMILY table never belongs in the engine set, so it is never
    /// added here — it goes to a family-owned migration set (ADR-PC-021).
    ///
    /// Derived from the engine <c>Sql/*.sql</c> resources: events + outbox (0001), snapshots (0003),
    /// rate_sheets (0004), projections (0005), pack_versions (0006), projection_checkpoints (0011),
    /// inbox (0012), command_dedup (0015 — the generic command-ingress idempotency ledger, ADR-PC-029),
    /// bulk_operation_jobs + bulk_operation_targets (0018 — the generic, family-agnostic bulk-operations
    /// runner work-table substrate, ADR-PC-035; the operation kind is a free VARCHAR and the per-item
    /// params/precondition are opaque JSONB, so the spine names no family).
    /// <c>schema_migrations</c> is created by <see cref="MigrationRunner"/> in code (not a
    /// <c>.sql</c> resource, so the parse never sees it today); it is listed defensively so that moving
    /// the ledger DDL into a migration file stays green.
    /// </summary>
    private static readonly string[] AllowedEngineTables =
    [
        "events", "outbox", "snapshots", "rate_sheets", "projections",
        "pack_versions", "projection_checkpoints", "inbox", "command_dedup", "schema_migrations",
        "bulk_operation_jobs", "bulk_operation_targets",
    ];

    /// <summary>
    /// PRIMARY (sound) — the closed set of custom schemas the engine migrations may create. EMPTY: the
    /// engine writes its spine tables in the default <c>public</c> schema (which it never
    /// <c>CREATE SCHEMA</c>s) and owns no dedicated schema. The read-side <c>read_model</c> schema is
    /// FAMILY-owned (ADR-PC-021), so any <c>CREATE SCHEMA</c> in the engine set is a leak. Add an entry
    /// here only if the engine ever legitimately needs its own generic schema (a deliberate
    /// generic-engine change).
    /// </summary>
    private static readonly string[] AllowedEngineSchemas = [];

    /// <summary>
    /// SECONDARY (heuristic) — tokens that name a concrete product-family DOMAIN, used ONLY by the
    /// column scan (<see cref="No_engine_column_name_is_family_typed"/>) as a cheap tripwire for a
    /// family-typed column added to a generic table. NOT sound: drawn from the term-deposit reference
    /// family's vocabulary (broadened toward sibling banking nouns), it cannot recognise a NEW family's
    /// nouns (<c>escrow</c>, <c>apr</c>, …). The soundness lives in the table/schema allowlists above and
    /// the opaque-payload contract (ADR-PC-001 §P1); this is the supplementary layer. Each token is
    /// matched on a word / snake_case-segment boundary (see <see cref="IdentifierNamesFamilyDomain"/>) so
    /// a generic identifier that merely embeds the letters (e.g. <c>partition_key</c> vs <c>tan</c>) does
    /// not false-RED.
    /// </summary>
    private static readonly string[] FamilyDomainTokens =
    [
        "deposit", "term_deposit", "coupon", "withholding", "accrual", "maturity",
        "tranche", "principal", "payout", "renewal", "heir", "tan",
    ];

    /// <summary>
    /// The generic, family-NEUTRAL key columns the opaque event store is allowed to carry — the
    /// "keyed by generic columns" half of ADR-PC-001 §P1's opaque-event contract. <c>family</c> and
    /// <c>event_type</c> are the discriminators that let the spine dispatch ANY family's event without
    /// naming one; they are explicitly NOT family leaks even though a family NAME rides in their VALUES
    /// at runtime ('term_deposit'). Whitelisted so the column deny scan can never flag them.
    /// </summary>
    private static readonly string[] AllowedGenericKeyColumns =
        ["family", "event_type"];

    /// <summary>
    /// PRIMARY (sound). Every <c>CREATE TABLE</c> in the engine migration set must name a sanctioned
    /// generic spine table (<see cref="AllowedEngineTables"/>). A table outside the allowlist is a family
    /// leak whatever its name — this is the check that holds for a FUTURE family, since it needs no
    /// knowledge of that family's vocabulary. A family-named table (a read model like
    /// <c>read_model.deposits</c>, or a new family's <c>mortgages</c>) belongs in a family-owned
    /// migration set, never the engine's.
    /// </summary>
    [Fact]
    public void Engine_migrations_create_only_allowlisted_tables()
    {
        var tables = EngineTableNames();

        // Non-vacuity: the parse must actually find the spine's tables. If this drops to (near) zero the
        // regex broke and the allowlist membership check would pass vacuously — fail loud instead.
        Assert.True(
            tables.Count >= 6,
            $"expected to parse the engine spine tables from the migration set, found only "
            + $"[{string.Join(", ", tables.OrderBy(t => t))}]; the CREATE TABLE parse likely broke.");

        var violations = tables
            .Where(t => !AllowedEngineTables.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"table '{t}' is not a sanctioned generic spine table")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 §P2 / ADR-PC-001 §P1 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): the engine event-store "
            + "migrations may create ONLY the generic spine tables on the allowlist — events stay opaque, "
            + "keyed by the generic family/event_type columns with the payload an opaque BYTEA. A "
            + "family-named table (including a read model like read_model.deposits, or a new family's "
            + "tables) belongs in a family-owned migration set, never the engine's. If you ADDED a generic "
            + "spine table on purpose, add it to AllowedEngineTables in the same change.\n"
            + $"Allowlist: [{string.Join(", ", AllowedEngineTables)}]\n"
            + "Offending tables:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// PRIMARY (sound). The engine migrations may create ONLY the schemas on
    /// <see cref="AllowedEngineSchemas"/> (today: none — the spine lives in <c>public</c>). A
    /// <c>CREATE SCHEMA</c> for <c>read_model</c> (the relocated family read model) or any other family
    /// schema fails here. This, with the table allowlist, subsumes the former inverse
    /// <c>read_model</c>/<c>deposits</c> guard.
    /// </summary>
    [Fact]
    public void Engine_migrations_create_no_unsanctioned_schema()
    {
        var schemas = EngineSchemaNames();

        var violations = schemas
            .Where(s => !AllowedEngineSchemas.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(s => $"CREATE SCHEMA '{s}'")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): the engine migrations create no custom "
            + "schema (the spine lives in 'public'). The read-side 'read_model' schema is FAMILY-owned "
            + "(Babelstone.Families.TermDeposit.Application.Migrations), not the engine's. A CREATE SCHEMA "
            + "in the engine set is a family leak.\n"
            + $"Allowed engine schemas: [{(AllowedEngineSchemas.Length == 0 ? "(none)" : string.Join(", ", AllowedEngineSchemas))}]\n"
            + "Offending schemas:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// PRIMARY (sound). Every <c>REFERENCES</c> in the engine migration set must TARGET a sanctioned
    /// engine table (<see cref="AllowedEngineTables"/>). The spine declares no cross-table FKs today (the
    /// event log is a flat append-only relation, ADR-PC-001 §P1); this keeps a future FK from coupling
    /// the engine store to a family table (e.g. <c>REFERENCES read_model.deposits</c> — <c>deposits</c> is
    /// not an engine table) even if the referencing column were generically named.
    /// </summary>
    [Fact]
    public void Engine_foreign_keys_target_only_allowlisted_tables()
    {
        var fkTargets = EngineForeignKeyTargetTables();

        var violations = fkTargets
            .Where(t => !AllowedEngineTables.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"REFERENCES '{t}' (not a sanctioned engine table)")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "ADR-PC-021 §P2 / ADR-PC-001 §P1 (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC): an engine migration "
            + "foreign key may target only a generic engine table — targeting a family table would couple "
            + "the opaque event store to a family's relational shape.\n"
            + $"Allowlist: [{string.Join(", ", AllowedEngineTables)}]\n"
            + "Offending references:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// SECONDARY (heuristic). No engine migration column NAME (from <c>CREATE TABLE</c> or
    /// <c>ALTER TABLE … ADD COLUMN</c>) may carry a family-domain token. This complements the table/schema
    /// allowlists with a cheap tripwire for a family-typed column added to a GENERIC, allowlisted table
    /// (an <c>ALTER TABLE events ADD COLUMN maturity_date</c>) — a case a table allowlist alone would not
    /// see. It is a NAME HEURISTIC over <see cref="FamilyDomainTokens"/>: it catches the reference family's
    /// nouns but is NOT sound for a NEW family's columns; the authoritative guarantee remains the
    /// opaque-payload contract (ADR-PC-001 §P1) and the allowlists above. The generic key columns
    /// family/event_type are explicitly allowed (<see cref="AllowedGenericKeyColumns"/>).
    /// </summary>
    [Fact]
    public void No_engine_column_name_is_family_typed()
    {
        var columns = EngineColumns();

        // Non-vacuity: the spine declares dozens of columns (events alone has 16). A tiny count means the
        // column parse broke and the deny scan would pass vacuously.
        Assert.True(
            columns.Count >= 20,
            $"expected to parse the engine spine columns from the migration set, found only "
            + $"{columns.Count}; the column parse likely broke.");

        // The generic key columns are the opaque-keying mechanism, not a family leak — exclude them before
        // the deny scan so they can never be flagged (family/event_type are §P1 envelope keys).
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
            + "into a family-owned projection/migration. (This is the secondary heuristic; the table/"
            + "schema allowlists are the sound primary.) Offending columns:\n  "
            + string.Join("\n  ", violations));
    }

    // -----------------------------------------------------------------------------------------
    // Parsing helpers — all operate over the COMMENT-STRIPPED migration text, so a family name that
    // appears only in a SQL comment (e.g. "family-prefixed, e.g. 'term_deposit.deposit_position'" in
    // 0010/0011/0012) cannot trip any scan. Only executable DDL identifiers are examined. The engine
    // now owns ZERO family tables (the read model is family-owned, ADR-PC-021), so the whole engine
    // MigrationSet.All is in scope.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Every <c>CREATE TABLE</c> table name in the engine migration set. Schema-qualified names are
    /// split to their bare object name (<c>a.b</c> → <c>b</c>); the bare table name is what the allowlist
    /// check sees.
    /// </summary>
    private static IReadOnlyList<string> EngineTableNames()
    {
        var sql = EngineSql();
        var names = new List<string>();

        foreach (Match m in Regex.Matches(
            sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z_][\w.]*)", RegexOptions.IgnoreCase))
        {
            names.Add(BareName(m.Groups[1].Value));
        }

        return names;
    }

    /// <summary>
    /// Every schema a <c>CREATE SCHEMA</c> in the engine migration set creates. The engine declares none
    /// today (the spine lives in <c>public</c>); the allowlist check keeps any family schema
    /// (<c>read_model</c>) out.
    /// </summary>
    private static IReadOnlyList<string> EngineSchemaNames()
    {
        var sql = EngineSql();
        var names = new List<string>();

        foreach (Match m in Regex.Matches(
            sql, @"CREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z_]\w*)", RegexOptions.IgnoreCase))
        {
            names.Add(m.Groups[1].Value);
        }

        return names;
    }

    /// <summary>
    /// Every (table, column) pair declared by a <c>CREATE TABLE</c> body or an
    /// <c>ALTER TABLE … ADD COLUMN</c> in the engine migration set. For a <c>CREATE TABLE</c> it splits
    /// the parenthesised body on top-level commas and reads the leading identifier of each clause that is
    /// a column definition (skipping <c>CONSTRAINT</c> / table-level <c>PRIMARY KEY</c> / <c>UNIQUE</c> /
    /// <c>CHECK</c> / <c>FOREIGN KEY</c> clauses). For an <c>ALTER … ADD COLUMN</c> it reads the added
    /// column name. A column name wrapped in double-quotes (<c>"maturity_date" DATE</c>) is handled — the
    /// surrounding quotes are stripped so the bare name still reaches the deny scan.
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
                // surrounding double-quote so a quoted identifier ("maturity_date" DATE) is caught too —
                // the bare name is what the deny scan must see.
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
    /// Every table a <c>REFERENCES</c> clause in the engine migration set targets (the FK target table).
    /// Today the spine declares none — the event log is a flat append-only relation — so this returns
    /// empty, and the allowlist check keeps a future family-targeting FK out.
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
    /// The full engine migration-set SQL, COMMENT-STRIPPED. This is the single text every parse runs over,
    /// so the comment-strip is applied once, consistently. Read off <see cref="MigrationSet.All"/> — the
    /// SAME embedded resources <see cref="MigrationRunner"/> applies — so the gate sees exactly the shipped
    /// schema, deterministically and with no database. The engine set owns zero family tables (the read
    /// model is family-owned, ADR-PC-021), so the whole set is in scope.
    /// </summary>
    private static string EngineSql()
    {
        Assert.NotEmpty(MigrationSet.All); // non-vacuity: the embedded migrations were discovered.

        return StripSqlComments(string.Concat(MigrationSet.All.Select(m => m.Sql + "\n")));
    }

    /// <summary>
    /// Strips SQL line comments (<c>-- …</c> to end of line) and block comments (<c>/* … */</c>) so a
    /// family name appearing only in prose (the illustrative <c>'term_deposit.deposit_position'</c> in the
    /// 0010/0011/0012 column comments) cannot trip an identifier deny scan — only executable DDL is
    /// examined.
    /// </summary>
    private static string StripSqlComments(string sql)
    {
        var noBlock = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"--.*?$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>
    /// The forbidden family token an identifier carries, or null if none. Matches a token from
    /// <see cref="FamilyDomainTokens"/> on a WORD boundary within the (lowercased) identifier so a family
    /// noun is caught as a whole word or snake_case segment (<c>maturity_date</c>, <c>coupon_count</c>)
    /// but a generic identifier that merely embeds the letters is not (<c>partition_key</c> does not match
    /// <c>tan</c>).
    /// </summary>
    private static string? MatchedFamilyToken(string identifier)
        => FamilyDomainTokens.FirstOrDefault(token => IdentifierNamesFamilyDomain(identifier, token));

    /// <summary>
    /// True iff <paramref name="token"/> appears as a whole word / snake_case segment in
    /// <paramref name="identifier"/> (case-insensitive). The boundary is the snake_case <c>_</c> or the
    /// string ends — so <c>tan</c> matches <c>tan</c> and <c>tan_basis_points</c> but NOT
    /// <c>partition_key</c> or <c>instant</c>, and <c>deposit</c> matches <c>deposits</c> /
    /// <c>deposit_position</c> yet a token is never matched mid-word against an unrelated longer word.
    /// </summary>
    private static bool IdentifierNamesFamilyDomain(string identifier, string token)
        => Regex.IsMatch(identifier, $@"(^|_){Regex.Escape(token)}(s?($|_)|[a-z])", RegexOptions.IgnoreCase);

    /// <summary>The bare object name from a possibly schema-qualified identifier (<c>a.b</c> → <c>b</c>).</summary>
    private static string BareName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }

    /// <summary>
    /// The text inside a balanced parenthesis run, given the index of the opening <c>(</c>. Counts nested
    /// parens so a column-level <c>CHECK (status IN (…))</c> inside a <c>CREATE TABLE</c> body does not
    /// terminate the body early. Returns null if the parens are unbalanced (defensive).
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
    /// Splits a <c>CREATE TABLE</c> body on TOP-LEVEL commas only — a comma inside a nested <c>(…)</c> (a
    /// multi-column constraint, a CHECK list) does not split a clause.
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
    /// definition — so the column parse skips it. A clause whose leading keyword is <c>CONSTRAINT</c> /
    /// <c>PRIMARY</c> / <c>UNIQUE</c> / <c>CHECK</c> / <c>FOREIGN</c> declares a constraint, not a column.
    /// </summary>
    private static bool IsTableLevelClause(string clause)
        => Regex.IsMatch(clause, @"^(CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN)\b", RegexOptions.IgnoreCase);
}
