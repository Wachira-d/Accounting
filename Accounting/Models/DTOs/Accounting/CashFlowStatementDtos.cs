namespace Accounting.Models.DTOs.Accounting;

public record CashFlowStatementResponse(
    DateTime FromDate,
    DateTime ToDate,
    CashFlowSection OperatingActivities,
    CashFlowSection InvestingActivities,
    CashFlowSection FinancingActivities,
    decimal NetCashChange,
    decimal OpeningCashBalance,
    decimal ClosingCashBalance);

public record CashFlowSection(
    string SectionName,
    List<CashFlowLineItem> Items,
    decimal SubTotal);

public record CashFlowLineItem(
    string Description,
    string? AccountCode,
    decimal Amount);
