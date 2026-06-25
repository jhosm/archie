using Babelstone.Engine;
using Babelstone.Engine.Api;
using Babelstone.FinancialTypes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Babelstone.Engine.Api.Tests;

/// <summary>
/// OBS_NO_PII_ATTRS (OBS-3 / ADR-IC-007 §P4–§P5, bd njt2.10): a structured log field must never carry
/// a raw funding account number. The dev <see cref="LoggingSettlementPort"/> previously logged
/// <c>{Account}</c> = <c>instruction.Account</c> (a full IBAN — the §P4 'account' personal-restricted
/// fragment). This pins the call-site fix: the raw account appears in neither the rendered message body
/// nor a structured <c>Account</c> field, while the structural deposit id (the §P5 sufficient reference)
/// is retained. The runtime log no-PII guard would strip the structured field at emit, but the rendered
/// BODY would still carry the value — so the leak is closed here, the one fix the processor cannot make.
/// </summary>
public sealed class LoggingSettlementPortPiiTests
{
    [Fact]
    public async Task LoggingSettlementPort_does_not_log_the_raw_funding_account_number()
    {
        var logger = new CapturingLogger<LoggingSettlementPort>();
        var port = new LoggingSettlementPort(logger);
        const string fundingAccount = "PT50003300004516123456705"; // a full IBAN — personal-restricted PII

        await port.SettleAsync(new SettlementInstruction(
            AggregateId: Guid.NewGuid(),
            Direction: SettlementDirection.Debit,
            Amount: new Money(500_000),
            Account: fundingAccount,
            Reason: "constitution"));

        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain(fundingAccount, entry.Message);                  // not rendered into the body
        Assert.DoesNotContain(entry.State, kv => kv.Key == "Account");          // not a structured field
        Assert.Contains(entry.State, kv => kv.Key == "AggregateId");            // the §P5 reference is kept
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var fields = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add((message, fields));
        }
    }
}
