using System.Runtime.CompilerServices;

// The pack-migration endpoint's pure validation/dispatch plan (PackMigrationsEndpoints.Plan and the
// internal PackMigrationPlan) is unit-tested in Babelstone.Engine.Tests against stub services/resolvers,
// so the dispatch rules (explicit-ids XOR predicate, product_family selection, the v1 currently_active
// guard) are covered without spinning up the HTTP stack or a database.
[assembly: InternalsVisibleTo("Babelstone.Engine.Tests")]
