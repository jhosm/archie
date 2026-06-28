using Babelstone.Families.PersonalLoan.Application.Migrations;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Application.Tests;

/// <summary>
/// Infrastructure-free checks on the personal_loan family's embedded migration set (mirrors the
/// term-deposit family's <c>MigrationSetTests</c> and the engine's). The family OWNS its read-model schema
/// (ADR-PC-021 family-owned ownership): <c>read_model.installment_calendar</c> is a family-named table, so
/// it ships in this family-owned set, NOT the engine's. These run Docker-free in the default unit lane,
/// guarding the forward-only packaging discipline before any DDL reaches a database.
/// </summary>
public sealed class MigrationSetTests
{
    [Fact]
    public void Discovers_the_embedded_installment_calendar_migration()
    {
        Assert.NotEmpty(MigrationSet.All);
        Assert.Contains(MigrationSet.All, m => m.Name == "installment_calendar");
        // The installment calendar is the family set's FIRST migration (0001) — this is the family's first
        // ever read-model table.
        Assert.Equal(1L, MigrationSet.All.Single(m => m.Name == "installment_calendar").Version);
    }

    [Fact]
    public void Discovers_the_read_model_body_migration_that_adds_the_detail_column()
    {
        // bd babelstone-6cpq.12: the producer needs an opaque read body the ReadModelRunner re-hydrates to
        // continue its accumulating fold (IReadModelRow.Detail). 0001 created the table without it; 0002
        // ADDs the detail column, so the family set's second migration is read_model @ version 2.
        var migration = MigrationSet.All.Single(m => m.Name == "read_model");
        Assert.Equal(2L, migration.Version);
        Assert.Contains("detail", migration.Sql);
        Assert.Contains("ADD COLUMN", migration.Sql);
        Assert.Contains("read_model.installment_calendar", migration.Sql);
    }

    [Fact]
    public void Versions_are_strictly_ascending_and_unique()
    {
        var versions = MigrationSet.All.Select(m => m.Version).ToArray();
        var sortedDistinct = versions.Distinct().Order().ToArray();
        Assert.Equal(sortedDistinct, versions);
    }

    [Fact]
    public void Every_migration_carries_sql()
    {
        Assert.All(MigrationSet.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Sql)));
    }

    [Fact]
    public void Installment_calendar_migration_declares_the_read_surface_columns()
    {
        // The forward installment-calendar read surface is the family's read side; a missing column is a
        // contract regression worth catching without standing up a database. Asserts the schedule facts, the
        // forward next-occurrence pair (the range-scan dimension), the routing-truth column (ADR-PC-018
        // §6.2), and the ADR-IC-005 §P3 freshness pair.
        var sql = MigrationSet.All.Single(m => m.Name == "installment_calendar").Sql;
        string[] contractColumns =
        [
            "stream_id", "sor", "first_installment_date", "term_months", "installment_amount_cents",
            "installments_paid", "next_installment_number", "next_due_date", "last_sequence", "last_updated",
        ];
        Assert.All(contractColumns, c => Assert.Contains(c, sql));
    }

    [Fact]
    public void Installment_calendar_migration_carries_the_engine_before_family_ordering_guard()
    {
        // The read model lives on the same Postgres tier as the engine event store (ADR-IC-005 §S1) and
        // GRANTs on the babelstone_engine role engine migration 0002 creates — a hard engine-before-family
        // ordering dependency. The migration RAISEs a clear EXCEPTION if that role is absent, so an
        // out-of-order run fails loud rather than with an opaque "role does not exist" deep in a GRANT.
        var sql = MigrationSet.All.Single(m => m.Name == "installment_calendar").Sql;
        Assert.Contains("RAISE EXCEPTION", sql);
        Assert.Contains("babelstone_engine", sql);
    }
}
