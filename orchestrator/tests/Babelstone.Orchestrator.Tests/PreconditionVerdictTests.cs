using System.Collections.Immutable;
using Babelstone.Families.TermDeposit.Orchestration;
using Xunit;

namespace Babelstone.Orchestrator.Tests;

/// <summary>
/// Pure tests of the <see cref="PreconditionVerdict"/> acceptance rule (ADR-PC-024
/// §2: an upstream verdict is opaque provenance, "recorded, never parsed, never
/// re-adjudicated"). The decider branches ONLY on <see cref="PreconditionVerdict.Satisfied"/> +
/// the PRESENCE of each required key — NEVER on the verdict's age. The cornerstone test is the
/// stale-but-satisfied case: a verdict evaluated far in the past must still ACCEPT, because a
/// freshness check would make the decision depend on wall-clock and poison replay
/// (ADR-PC-010 §P5).
/// </summary>
public sealed class PreconditionVerdictTests
{
    [Fact]
    public void A_satisfied_verdict_with_evidence_accepts()
    {
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: "CLR-REF-001", EvaluatedAt: DateTimeOffset.UtcNow);
        Assert.True(verdict.Accepts());
    }

    [Fact]
    public void An_unsatisfied_verdict_refuses_however_recent()
    {
        // A refusal stamped a microsecond ago is still a refusal — recency does not rescue it.
        var verdict = new PreconditionVerdict(Satisfied: false, EvidenceRef: "CLR-REF-001", EvaluatedAt: DateTimeOffset.UtcNow);
        Assert.False(verdict.Accepts());
    }

    [Fact]
    public void A_stale_but_satisfied_verdict_STILL_ACCEPTS_no_freshness_check()
    {
        // THE cornerstone (replay determinism, ADR-PC-010 §P5): a verdict evaluated FAR in the
        // past — a decade ago — that is satisfied with all evidence present must STILL accept.
        // The decider performs NO evaluated_at-vs-now comparison; a stale verdict rebuilt from
        // the event log re-presents and re-accepts exactly (ADR-PC-024 §4
        // "replay re-presents the recorded verdicts"). If this ever fails, a freshness branch crept in.
        var tenYearsAgo = DateTimeOffset.UtcNow.AddYears(-10);
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: "CLR-REF-OLD", EvaluatedAt: tenYearsAgo);

        Assert.True(verdict.Accepts());

        // And with required keys present, still accepts — age is irrelevant on every path.
        var withKeys = verdict with
        {
            Evidence = ImmutableDictionary<string, string>.Empty
                .Add("sanctions_screen", "SCR-REF-7")
                .Add("source_of_funds", "SOF-REF-3"),
        };
        Assert.True(withKeys.Accepts("sanctions_screen", "source_of_funds"));
    }

    [Fact]
    public void A_future_dated_satisfied_verdict_also_accepts_age_is_never_consulted()
    {
        // The mirror of the stale case: even a verdict stamped in the FUTURE accepts — proving
        // the rule never compares evaluated_at to now in EITHER direction.
        var nextYear = DateTimeOffset.UtcNow.AddYears(1);
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: "CLR-REF-FUTURE", EvaluatedAt: nextYear);
        Assert.True(verdict.Accepts());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_satisfied_verdict_with_no_evidence_reference_refuses(string? evidenceRef)
    {
        // A clearance asserted with no opaque token to record is not a clearance
        // (ADR-PC-024 §1). Presence of the reference is a structural requirement — its
        // ABSENCE refuses, but that is a presence check, never a freshness one.
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: evidenceRef, EvaluatedAt: DateTimeOffset.UtcNow);
        Assert.False(verdict.Accepts());
    }

    [Fact]
    public void A_satisfied_verdict_missing_a_required_key_refuses()
    {
        // The refusal branches on PRESENCE of each required key. A satisfied verdict that omits a
        // demanded key refuses — but, again, on presence, never on age.
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: "CLR-REF-001", EvaluatedAt: DateTimeOffset.UtcNow)
        {
            Evidence = ImmutableDictionary<string, string>.Empty.Add("sanctions_screen", "SCR-REF-7"),
        };

        // sanctions_screen present → accepts when only that is required.
        Assert.True(verdict.Accepts("sanctions_screen"));
        // source_of_funds absent → refuses when it is also required.
        Assert.False(verdict.Accepts("sanctions_screen", "source_of_funds"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_required_key_present_but_blank_refuses(string blankValue)
    {
        // A required key whose value is blank is treated as absent — a structural presence
        // requirement, still nothing to do with the verdict's age.
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: "CLR-REF-001", EvaluatedAt: DateTimeOffset.UtcNow)
        {
            Evidence = ImmutableDictionary<string, string>.Empty.Add("sanctions_screen", blankValue),
        };
        Assert.False(verdict.Accepts("sanctions_screen"));
    }

    [Fact]
    public void Accepts_is_deterministic_across_repeated_calls()
    {
        // The same verdict + required keys yields the same answer every call — no hidden clock.
        var verdict = new PreconditionVerdict(Satisfied: true, EvidenceRef: "CLR-REF-001", EvaluatedAt: DateTimeOffset.UtcNow.AddDays(-365))
        {
            Evidence = ImmutableDictionary<string, string>.Empty.Add("k", "v"),
        };

        Assert.True(verdict.Accepts("k"));
        Assert.True(verdict.Accepts("k"));
        Assert.True(verdict.Accepts("k"));
    }
}
