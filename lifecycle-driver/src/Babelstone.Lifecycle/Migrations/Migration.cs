namespace Babelstone.Lifecycle.Migrations;

/// <summary>
/// A single forward-only DDL step of the lifecycle-driver host's OWN migration series (ADR-PC-038
/// §Decision 1 — "a table in the lifecycle-driver host's own forward-only migration series"; the
/// discipline lifted from the engine's <c>Babelstone.EventStore.Migrations</c> via the orchestrator's
/// copy, ADR-PC-001 §P5). Once applied, a migration is immutable: its <see cref="Sql"/> is never edited,
/// only superseded by a higher-<see cref="Version"/> migration.
/// </summary>
/// <param name="Version">Monotonic, unique across the set. Parsed from the leading digits of the
/// embedded resource name (e.g. <c>0001</c>).</param>
/// <param name="Name">Human label after the version prefix (e.g. <c>lifecycle_dispatch_ledger</c>).</param>
/// <param name="Sql">The DDL text. May contain multiple statements and DO blocks.</param>
public sealed record Migration(long Version, string Name, string Sql);
