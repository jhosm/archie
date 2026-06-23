using System.Runtime.CompilerServices;

// DepositInstanceFilterResolver is an internal family adapter (the public contract is the spine's
// IPackMigrationInstanceResolver). Exposing internals to the family's test assembly lets the resolver's
// product_family + currently_active guards be unit-tested directly, alongside the read-model store's
// ListActiveStreamIdsAsync.
[assembly: InternalsVisibleTo("Babelstone.Families.TermDeposit.Application.Tests")]
