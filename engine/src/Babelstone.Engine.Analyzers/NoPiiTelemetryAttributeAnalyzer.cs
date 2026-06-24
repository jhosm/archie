using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Babelstone.Engine.Analyzers;

/// <summary>
/// BENG005 — the BUILD-TIME mechanical half of <c>OBS_NO_PII_ATTRS</c> (commitment-catalogue
/// row OBS-3; ADR-IC-007 §P4; ADR-PC-004 §P2; ADR-IC-016 plane (iii)). It fails the build when a
/// telemetry span/log attribute carries personal data — a NIF, IBAN, account number, customer
/// name, or e-mail — either in the attribute <b>key</b> (e.g. <c>client_nif</c>, <c>core.account</c>)
/// or in a constant attribute <b>value</b> (e.g. an IBAN/NIF/e-mail literal stamped onto a tag).
/// Only the structural <c>babelstone.*</c> operational tier is admitted; money rides as integer
/// cents under the <c>babelstone.*_cents</c> keys, never a formatted decimal.
/// </summary>
/// <remarks>
/// <para>
/// The cultural control ADR-IC-007 §P4 names ("code review that treats span attributes with the
/// same PII discipline as log messages") is turned into a mechanical gate here: a single engineer
/// who adds <c>client_name</c> or <c>client_email</c> as a span attribute no longer creates a GDPR
/// incident inside the trace backend that only a forgotten runtime test (<c>TelemetrySpanTests</c>)
/// might catch — the build breaks the moment the attribute is written. This is the dedicated
/// analyser leg the catalogue reserved under the <c>unit / analyser</c> gate column.
/// </para>
/// <para>
/// Mechanism (extended-analyser-safe — no <c>GetSemanticModel</c>, RS1030): for every invocation of
/// a telemetry attribute setter — <c>System.Diagnostics.Activity.SetTag</c>/<c>AddTag</c>/
/// <c>SetBaggage</c>, the <c>ActivityTagsCollection</c>/<c>TagList.Add</c> collection setters, and the
/// <c>Microsoft.Extensions.Logging</c> structured-state path is covered by its message-template
/// scanning below — we read the first argument as the KEY and the second as the VALUE. A constant
/// string key is checked against the admitted <c>babelstone.*</c> structural tier and the PII
/// key-fragment set (the same fragments the <c>TelemetrySpanTests</c> structural assertion scans:
/// nif, iban, account, name, email, client, phone, address, tax_id); a non-admitted key that carries
/// a PII fragment is flagged. A constant string value is checked against PII-shaped literals
/// (IBAN / Portuguese NIF / e-mail). The <c>babelstone.subject_pseudonym</c> key — a salted one-way
/// hash, ADR-IC-016 plane (iii) §8 — is admitted: it starts with <c>babelstone.</c> and deliberately
/// avoids every PII fragment, so it passes the same scan a real call site would.
/// </para>
/// <para>
/// A residual gap, narrow by design and mirroring the BENG004 within-method limit: a PII key or
/// value laundered through a non-constant expression (a variable read from elsewhere, a helper
/// return, interpolation of a runtime value) is not constant-foldable here and is not flagged — the
/// common, reviewable case (a literal PII key or a literal PII value written at the call site) is
/// closed, exactly the shape §P4's code-review control targets.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoPiiTelemetryAttributeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(EngineDiagnostics.PiiTelemetryAttribute);

    // Key fragments that mark a non-structural attribute key as PII-bearing — the same set the
    // TelemetrySpanTests structural assertion scans, so the runtime test and this analyser agree.
    private static readonly string[] PiiKeyFragments =
        ["nif", "iban", "account", "name", "email", "client", "phone", "address", "tax_id"];

    // A Portuguese IBAN (PT + 23 digits) — the §P4 personal-restricted example.
    private static readonly Regex IbanValue =
        new(@"\bPT\d{2}[\s.]?(?:\d[\s.]?){19,21}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // An e-mail address literal.
    private static readonly Regex EmailValue =
        new(@"\b[^@\s]+@[^@\s]+\.[^@\s]+\b", RegexOptions.Compiled);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterOperationAction(
            op => AnalyzeInvocation((IInvocationOperation)op.Operation, op),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(IInvocationOperation invocation, OperationAnalysisContext context)
    {
        if (!IsAttributeSetter(invocation.TargetMethod, out var keyIndex, out var valueIndex))
        {
            return;
        }

        var key = ConstantString(ArgumentAt(invocation, keyIndex));
        var value = ConstantString(ArgumentAt(invocation, valueIndex));

        // (1) The KEY carries PII (e.g. client_nif, core.account) and is not the admitted
        //     babelstone.* structural tier.
        if (key is not null && KeyIsPii(key))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                EngineDiagnostics.PiiTelemetryAttribute,
                invocation.Syntax.GetLocation(),
                key));
            return;
        }

        // (2) The VALUE is a PII-shaped literal (an IBAN / NIF / e-mail stamped onto the tag),
        //     even under a structurally-named key.
        if (value is not null && ValueIsPii(value))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                EngineDiagnostics.PiiTelemetryAttribute,
                invocation.Syntax.GetLocation(),
                key ?? "<value>"));
        }
    }

    // The telemetry attribute-setter surface. Returns the (key, value) argument indices.
    private static bool IsAttributeSetter(IMethodSymbol method, out int keyIndex, out int valueIndex)
    {
        keyIndex = 0;
        valueIndex = 1;

        var owner = method.ContainingType?.ToDisplayString();
        return (owner, method.Name) switch
        {
            // Span attributes: Activity.SetTag(string, object?), AddTag(string, object?),
            // SetBaggage(string, string?). Key is arg0, value is arg1.
            ("System.Diagnostics.Activity", "SetTag" or "AddTag" or "SetBaggage") => true,
            // Collection setters: ActivityTagsCollection.Add(string, object?) and
            // System.Diagnostics.TagList.Add(string, object?).
            ("System.Diagnostics.ActivityTagsCollection" or "System.Diagnostics.TagList", "Add") => true,
            _ => false,
        };
    }

    private static IOperation? ArgumentAt(IInvocationOperation invocation, int index)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Ordinal == index)
            {
                return argument.Value;
            }
        }

        return null;
    }

    // The constant string an argument folds to (a string literal, or a const that folds to one),
    // or null if it is not a compile-time-constant string.
    private static string? ConstantString(IOperation? operation)
    {
        // Unwrap an implicit conversion (e.g. string → object? on the value parameter).
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation is { ConstantValue: { HasValue: true, Value: string s } } ? s : null;
    }

    // A key is PII unless it is the admitted babelstone.* operational tier; any non-admitted key
    // carrying a PII fragment is flagged.
    private static bool KeyIsPii(string key)
    {
        var lowered = key.ToLowerInvariant();

        // The structural operational tier — admitted. babelstone.subject_pseudonym lives here and
        // deliberately avoids every PII fragment, so it is never flagged.
        if (lowered.StartsWith("babelstone.", System.StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var fragment in PiiKeyFragments)
        {
            if (lowered.Contains(fragment))
            {
                return true;
            }
        }

        return false;
    }

    // A constant value is PII when it is shaped like an IBAN, a Portuguese NIF (9 digits), or an
    // e-mail address.
    private static bool ValueIsPii(string value)
    {
        if (IbanValue.IsMatch(value) || EmailValue.IsMatch(value))
        {
            return true;
        }

        // A bare Portuguese NIF: exactly 9 digits (optionally spaced), nothing else.
        var trimmed = value.Trim();
        var digits = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsDigit(ch))
            {
                digits++;
            }
            else if (ch != ' ' && ch != '.' && ch != '-')
            {
                return false;
            }
        }

        return digits == 9;
    }
}
