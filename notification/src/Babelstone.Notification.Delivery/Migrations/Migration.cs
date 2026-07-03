namespace Babelstone.Notification.Delivery.Migrations;

/// <summary>
/// A single forward-only DDL step of the notification delivery estate's OWN migration series
/// (ADR-IC-011 — the notification service's own PostgreSQL delivery store; the discipline lifted
/// from the engine's <c>Babelstone.EventStore.Migrations</c> via the orchestrator's and the
/// lifecycle driver's copies, ADR-PC-001). Once applied, a migration is
/// immutable: its <see cref="Sql"/> is never edited, only superseded by a higher-<see cref="Version"/>
/// migration.
/// </summary>
/// <param name="Version">Monotonic, unique across the set. Parsed from the leading digits of the
/// embedded resource name (e.g. <c>0001</c>).</param>
/// <param name="Name">Human label after the version prefix (e.g. <c>notification_delivery</c>).</param>
/// <param name="Sql">The DDL text. May contain multiple statements and DO blocks.</param>
public sealed record Migration(long Version, string Name, string Sql);
