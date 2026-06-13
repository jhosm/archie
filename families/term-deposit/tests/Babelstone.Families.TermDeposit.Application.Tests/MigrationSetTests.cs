using Babelstone.Families.TermDeposit.Application.Migrations;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Infrastructure-free checks on the term-deposit family's embedded migration set (mirrors the
/// engine's <c>MigrationSetTests</c>). The family OWNS its read-model schema (ADR-PC-021 family-owned
/// ownership): <c>read_model.deposits</c> is a family-named table, so it ships in this family-owned
/// set, NOT the engine's. These run Docker-free in the default unit lane, guarding the forward-only
/// packaging discipline before any DDL reaches a database.
/// </summary>
public sealed class MigrationSetTests
{
    [Fact]
    public void Discovers_the_embedded_read_model_migration()
    {
        Assert.NotEmpty(MigrationSet.All);
        Assert.Contains(MigrationSet.All, m => m.Name == "read_model");
        // The relocated read model is the family set's FIRST migration (0001), not the engine's 0013.
        Assert.Equal(1L, MigrationSet.All.Single(m => m.Name == "read_model").Version);
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
    public void Read_model_migration_declares_the_ADR_IC_005_contract_columns()
    {
        // The ADR-IC-005 CQRS read-model contract is the family's read surface; a missing column is a
        // contract regression worth catching without standing up a database. Asserts the §P3 freshness
        // pair, the routing-truth column (ADR-PC-018 §6.2), the honest product keys (bd babelstone-v794),
        // and the maturity range-scan dimension (ADR-IC-005's upcoming_maturities access pattern).
        var sql = MigrationSet.All.Single(m => m.Name == "read_model").Sql;
        string[] contractColumns =
        [
            "stream_id", "sor", "principal_cents", "tan_basis_points", "rate_sheet_version_id",
            "product_code", "term_days", "start_date", "maturity_date", "interest_variant",
            "auto_renewal_policy", "payment_period_months", "lifecycle",
            "accrued_gross_interest_cents", "withholding_to_date_cents", "net_interest_cents",
            "total_payout_cents", "coupons_paid", "detail", "last_sequence", "last_updated",
        ];
        Assert.All(contractColumns, c => Assert.Contains(c, sql));
    }

    [Fact]
    public void Read_model_migration_carries_the_engine_before_family_ordering_guard()
    {
        // The read model lives on the same Postgres tier as the engine event store (ADR-IC-005 §S1) and
        // GRANTs on the babelstone_engine role engine migration 0002 creates — a hard engine-before-family
        // ordering dependency. The migration RAISEs a clear EXCEPTION if that role is absent, so an
        // out-of-order run fails loud rather than with an opaque "role does not exist" deep in a GRANT.
        var sql = MigrationSet.All.Single(m => m.Name == "read_model").Sql;
        Assert.Contains("RAISE EXCEPTION", sql);
        Assert.Contains("babelstone_engine", sql);
    }
}
