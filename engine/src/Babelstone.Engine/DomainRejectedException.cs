namespace Babelstone.Engine;

/// <summary>
/// A command was rejected by a <b>domain precondition</b> — e.g. no rate sheet effective at
/// constitution, an unpriced <c>(product, role)</c>, or a deposit not in the lifecycle state an
/// operation requires. This is a fail-loud <i>business</i> rejection of the caller's request, NOT
/// a programming bug or an infrastructure fault.
/// </summary>
/// <remarks>
/// The distinction is load-bearing at a boundary host: a domain rejection maps to a 4xx (the
/// request cannot be processed as asked), whereas a corrupt row, a missing handler registration, or
/// a DB fault is a bug/fault and must surface as a 5xx — never be masked as a rejection. Catch this
/// type specifically; let every other exception propagate.
/// </remarks>
public sealed class DomainRejectedException(string message) : Exception(message);
