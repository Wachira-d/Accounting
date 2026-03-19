using Accounting.Data;
using Accounting.Models.DTOs.Currency;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CurrencyService : ICurrencyService
{
    private readonly AccountingDbContext _db;

    public CurrencyService(AccountingDbContext db)
    {
        _db = db;
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

    // ==================== Private Helpers ====================

    private static CompanyCurrencyResponse MapCurrencyToResponse(CompanyCurrency c) =>
        new(c.Id, c.CurrencyCode, c.CurrencyName, c.Symbol, c.DecimalPlaces, c.IsActive);

    private static CurrencyRateResponse MapRateToResponse(CurrencyRate r) =>
        new(r.Id, r.FromCurrency, r.ToCurrency, r.EffectiveDate,
            r.BuyRate, r.SellRate, r.MidRate, r.Source, r.CreatedAt);
}
