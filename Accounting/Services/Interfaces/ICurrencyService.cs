using Accounting.Models.DTOs.Currency;

namespace Accounting.Services.Interfaces;

public interface ICurrencyService
{
    // Company Currencies
    Task<CompanyCurrencyResponse> AddCurrencyAsync(Guid companyId, CreateCompanyCurrencyRequest request);
    Task<List<CompanyCurrencyResponse>> GetCurrenciesAsync(Guid companyId);
    Task<CompanyCurrencyResponse> UpdateCurrencyAsync(Guid companyId, Guid currencyId, UpdateCompanyCurrencyRequest request);

    // Exchange Rates
    Task<CurrencyRateResponse> AddRateAsync(Guid companyId, CreateCurrencyRateRequest request);
    Task<List<CurrencyRateResponse>> GetRatesAsync(Guid companyId, string? fromCurrency = null, string? toCurrency = null);
    Task<CurrencyRateResponse?> GetLatestRateAsync(Guid companyId, string fromCurrency, string toCurrency);

    // Conversion & Gain/Loss
    Task<decimal> ConvertAsync(Guid companyId, string fromCurrency, string toCurrency, decimal amount, DateTime? asOfDate = null);
    Task<List<UnrealizedGainLossItem>> CalculateUnrealizedGainLossAsync(Guid companyId, string baseCurrency, DateTime asOfDate);
}
