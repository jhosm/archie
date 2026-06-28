using System.Runtime.CompilerServices;

// LoanInstanceFilterResolver is an internal family adapter (the public contract is the spine's
// IPackMigrationInstanceResolver). Exposing internals to the family's test assembly lets the resolver's
// product_family + currently_active guards — and its fold-the-event-store active-loan resolution — be
// tested directly. Mirrors the term-deposit family's AssemblyInfo.
[assembly: InternalsVisibleTo("Babelstone.Families.PersonalLoan.Application.Tests")]
