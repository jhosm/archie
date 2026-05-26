; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category         | Severity | Notes
--------|------------------|----------|--------------------------------------------------------
BMNY001 | Babelstone.Money | Warning  | MathRoundOnDecimalAnalyzer — ADR-PC-010 §P2 single rounding boundary
BMNY002 | Babelstone.Money | Warning  | DecimalStateOutsideMoneyAnalyzer — ADR-PC-010 §P1 decimal is not stored state
BMNY003 | Babelstone.Money | Warning  | MoneyOperatorReturningDecimalAnalyzer — ADR-PC-010 §P1 no decimal-returning operator
