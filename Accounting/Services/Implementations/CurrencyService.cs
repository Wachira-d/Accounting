using Accounting.Data;
using Accounting.Models.DTOs.Currency;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CurrencyService : ICurrencyService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyService(AccountingDbContext db, ILogger<CurrencyService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ==================== Company Currencies ====================

    public async Task<CompanyCurrencyResponse> AddCurrencyAsync(Guid companyId, CreateCompanyCurrencyRequest request)
    {
        var exists = await _db.CompanyCurrencies
            .AnyAsync(c => c.CompanyId == companyId && c.CurrencyCode == request.CurrencyCode);
        if (exists)
            throw new InvalidOperationException($"สกุลเงิน {request.CurrencyCode} มีอยู่แล้ว");

        var currency = new CompanyCurrency
        {
            CompanyId = companyId,
            CurrencyCode = request.CurrencyCode,
            CurrencyName = request.CurrencyName,
            Symbol = request.Symbol,
            DecimalPlaces = request.DecimalPlaces
        };

        _db.CompanyCurrencies.Add(currency);
        await _db.SaveChangesAsync();
        return MapCurrencyToResponse(currency);
    }

    public async Task<List<CompanyCurrencyResponse>> GetCurrenciesAsync(Guid companyId)
    {
        var currencies = await _db.CompanyCurrencies
            .Where(c => c.CompanyId == companyId)
            .OrderBy(c => c.CurrencyCode)
            .ToListAsync();

        return currencies.Select(MapCurrencyToResponse).ToList();
    }

    public async Task<CompanyCurrencyResponse> GetCurrencyByIdAsync(Guid companyId, Guid currencyId)
    {
        var currency = await _db.CompanyCurrencies
            .FirstOrDefaultAsync(c => c.Id == currencyId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสกุลเงิน");

        return MapCurrencyToResponse(currency);
    }

    public async Task DeleteCurrencyAsync(Guid companyId, Guid currencyId)
    {
        var currency = await _db.CompanyCurrencies
            .FirstOrDefaultAsync(c => c.Id == currencyId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสกุลเงิน");

        _db.CompanyCurrencies.Remove(currency);
        await _db.SaveChangesAsync();
    }

    public async Task<CompanyCurrencyResponse> UpdateCurrencyAsync(Guid companyId, Guid currencyId, UpdateCompanyCurrencyRequest request)
    {
        var currency = await _db.CompanyCurrencies
            .FirstOrDefaultAsync(c => c.Id == currencyId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสกุลเงิน");

        if (request.CurrencyName != null) currency.CurrencyName = request.CurrencyName;
        if (request.Symbol != null) currency.Symbol = request.Symbol;
        if (request.DecimalPlaces.HasValue) currency.DecimalPlaces = request.DecimalPlaces.Value;
        if (request.IsActive.HasValue) currency.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return MapCurrencyToResponse(currency);
    }

    // ==================== Exchange Rates ====================

    public async Task<CurrencyRateResponse> AddRateAsync(Guid companyId, CreateCurrencyRateRequest request)
    {
        var rate = new CurrencyRate
        {
            CompanyId = companyId,
            FromCurrency = request.FromCurrency,
            ToCurrency = request.ToCurrency,
            EffectiveDate = request.EffectiveDate,
            BuyRate = request.BuyRate,
            SellRate = request.SellRate,
            MidRate = request.MidRate,
            Source = request.Source
        };

        _db.CurrencyRates.Add(rate);
        await _db.SaveChangesAsync();
        return MapRateToResponse(rate);
    }

    public async Task<List<CurrencyRateResponse>> GetRatesAsync(Guid companyId, string? fromCurrency = null, string? toCurrency = null)
    {
        var query = _db.CurrencyRates.Where(r => r.CompanyId == companyId);

        if (!string.IsNullOrEmpty(fromCurrency))
            query = query.Where(r => r.FromCurrency == fromCurrency);
        if (!string.IsNullOrEmpty(toCurrency))
            query = query.Where(r => r.ToCurrency == toCurrency);

        var rates = await query
            .OrderByDescending(r => r.EffectiveDate)
            .Take(100)
            .ToListAsync();

        return rates.Select(MapRateToResponse).ToList();
    }

    public async Task<CurrencyRateResponse?> GetLatestRateAsync(Guid companyId, string fromCurrency, string toCurrency)
    {
        var rate = await _db.CurrencyRates
            .Where(r => r.CompanyId == companyId && r.FromCurrency == fromCurrency && r.ToCurrency == toCurrency)
            .OrderByDescending(r => r.EffectiveDate)
            .FirstOrDefaultAsync();

        return rate != null ? MapRateToResponse(rate) : null;
    }

    // ==================== Currency Conversion ====================

    /// <summary>
    /// Convert amount from one currency to another using the latest available rate
    /// </summary>
    public async Task<decimal> ConvertAsync(Guid companyId, string fromCurrency, string toCurrency, decimal amount, DateTime? asOfDate = null, string? direction = null)
    {
        if (fromCurrency == toCurrency) return amount;

        var date = asOfDate ?? DateTime.UtcNow;

        // Try direct rate
        var rate = await _db.CurrencyRates
            .Where(r => r.CompanyId == companyId
                && r.FromCurrency == fromCurrency
                && r.ToCurrency == toCurrency
                && r.EffectiveDate <= date)
            .OrderByDescending(r => r.EffectiveDate)
            .FirstOrDefaultAsync();

        if (rate != null)
        {
            var effectiveRate = direction switch
            {
                "buy" => rate.BuyRate > 0 ? rate.BuyRate : rate.MidRate,
                "sell" => rate.SellRate > 0 ? rate.SellRate : rate.MidRate,
                _ => rate.MidRate
            };
            return amount * effectiveRate;
        }

        // Try inverse rate
        var inverseRate = await _db.CurrencyRates
            .Where(r => r.CompanyId == companyId
                && r.FromCurrency == toCurrency
                && r.ToCurrency == fromCurrency
                && r.EffectiveDate <= date)
            .OrderByDescending(r => r.EffectiveDate)
            .FirstOrDefaultAsync();

        if (inverseRate != null && inverseRate.MidRate != 0)
            return amount / inverseRate.MidRate;

        _logger.LogWarning("No exchange rate found for {From}/{To} as of {Date}", fromCurrency, toCurrency, date);
        throw new InvalidOperationException($"ไม่พบอัตราแลกเปลี่ยน {fromCurrency}/{toCurrency}");
    }

    /// <summary>
    /// Calculate unrealized gain/loss for foreign currency accounts at month-end
    /// </summary>
    public async Task<List<UnrealizedGainLossItem>> CalculateUnrealizedGainLossAsync(
        Guid companyId, string baseCurrency, DateTime asOfDate)
    {
        var result = new List<UnrealizedGainLossItem>();

        // Get all foreign currency bank accounts
        var bankAccounts = await _db.Set<BankAccount>()
            .Where(b => b.CompanyId == companyId && b.IsActive && !b.IsDeleted
                && b.Currency != baseCurrency)
            .ToListAsync();

        foreach (var account in bankAccounts)
        {
            var currentRate = await _db.CurrencyRates
                .Where(r => r.CompanyId == companyId
                    && r.FromCurrency == account.Currency
                    && r.ToCurrency == baseCurrency
                    && r.EffectiveDate <= asOfDate)
                .OrderByDescending(r => r.EffectiveDate)
                .FirstOrDefaultAsync();

            if (currentRate == null) continue;

            var revaluedBalance = account.CurrentBalance * currentRate.MidRate;

            // Get previous revaluation or original booking rate balance
            var previousRate = await _db.CurrencyRates
                .Where(r => r.CompanyId == companyId
                    && r.FromCurrency == account.Currency
                    && r.ToCurrency == baseCurrency
                    && r.EffectiveDate <= asOfDate.AddMonths(-1))
                .OrderByDescending(r => r.EffectiveDate)
                .FirstOrDefaultAsync();

            var previousBalance = previousRate != null
                ? account.CurrentBalance * previousRate.MidRate
                : revaluedBalance;

            var gainLoss = revaluedBalance - previousBalance;

            if (gainLoss != 0)
            {
                result.Add(new UnrealizedGainLossItem(
                    account.Id, account.AccountName, account.Currency,
                    account.CurrentBalance, currentRate.MidRate,
                    revaluedBalance, gainLoss));
            }
        }

        return result;
    }

    // ==================== Private Helpers ====================

    private static CompanyCurrencyResponse MapCurrencyToResponse(CompanyCurrency c) =>
        new(c.Id, c.CurrencyCode, c.CurrencyName, c.Symbol, c.DecimalPlaces, c.IsActive);

    private static CurrencyRateResponse MapRateToResponse(CurrencyRate r) =>
        new(r.Id, r.FromCurrency, r.ToCurrency, r.EffectiveDate,
            r.BuyRate, r.SellRate, r.MidRate, r.Source, r.CreatedAt);
}

