namespace Babelstone.EventStore.Migrations;

/// <summary>
/// A single forward-only DDL step (ADR-PC-001). Once applied, a migration is
/// immutable: its <see cref="Sql"/> is never edited, only superseded by a
/// higher-<see cref="Version"/> migration.
/// </summary>
/// <param name="Version">Monotonic, unique across the set. Parsed from the leading
/// digits of the embedded resource name (e.g. <c>0001</c>).</param>
/// <param name="Name">Human label after the version prefix (e.g. <c>events_and_outbox</c>).</param>
/// <param name="Sql">The DDL text. May contain multiple statements and DO blocks.</param>
public sealed record Migration(long Version, string Name, string Sql);
