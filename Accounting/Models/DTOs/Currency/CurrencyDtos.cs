namespace Accounting.Models.DTOs.Currency;

public record CreateCompanyCurrencyRequest(
    string CurrencyCode,
    string CurrencyName,
    string? Symbol,
    int DecimalPlaces = 2);

public record UpdateCompanyCurrencyRequest(
    string? CurrencyName,
    string? Symbol,
    int? DecimalPlaces,
    bool? IsActive);

public record CompanyCurrencyResponse(
    Guid Id,
    string CurrencyCode,
    string CurrencyName,
    string? Symbol,
    int DecimalPlaces,
    bool IsActive);

public record CreateCurrencyRateRequest(
    string FromCurrency,
    string ToCurrency,
    DateTime EffectiveDate,
    decimal BuyRate,
    decimal SellRate,
    decimal MidRate,
    string? Source);

public record CurrencyRateResponse(
    Guid Id,
    string FromCurrency,
    string ToCurrency,
    DateTime EffectiveDate,
    decimal BuyRate,
    decimal SellRate,
    decimal MidRate,
    string? Source,
    DateTime CreatedAt);
