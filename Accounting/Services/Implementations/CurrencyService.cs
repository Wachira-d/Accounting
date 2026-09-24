using Accounting.Data;
using Accounting.Helpers;
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
        var code = NormalizeCurrencyCode(request.CurrencyCode, "รหัสสกุลเงิน");
        if (request.InitialRate.HasValue && request.InitialRate.Value <= 0)
            throw new BusinessRuleException("อัตราแลกเปลี่ยนเริ่มต้นต้องมากกว่า 0 — ถ้ายังไม่ทราบให้เว้นว่างไว้");

        var exists = await _db.CompanyCurrencies
            .AnyAsync(c => c.CompanyId == companyId && c.CurrencyCode == code);
        if (exists)
            throw new BusinessRuleException($"สกุลเงิน {code} มีอยู่แล้ว");

        var currency = new CompanyCurrency
        {
            CompanyId = companyId,
            CurrencyCode = code,
            // ไม่ส่งชื่อมา = ใช้รหัสเป็นชื่อ (derive ได้ ไม่ต้องบังคับผู้ใช้/partner)
            CurrencyName = string.IsNullOrWhiteSpace(request.CurrencyName) ? code : request.CurrencyName.Trim(),
            Symbol = request.Symbol,
            DecimalPlaces = request.DecimalPlaces
        };

        _db.CompanyCurrencies.Add(currency);

        // ช่อง "อัตราแลกเปลี่ยนเริ่มต้น (1 หน่วย = ? THB)" บนฟอร์ม — เดิมถูกทิ้งเงียบ ๆ
        if (request.InitialRate.HasValue)
        {
            _db.CurrencyRates.Add(new CurrencyRate
            {
                CompanyId = companyId,
                FromCurrency = code,
                ToCurrency = "THB",
                EffectiveDate = ThaiDate.CalendarDateUtc(DateTime.UtcNow),
                MidRate = request.InitialRate.Value,
                Source = "Manual"
            });
        }

        await _db.SaveChangesAsync();
        return MapCurrencyToResponse(currency);
    }

    /// <summary>รหัส ISO 4217 — ตัวพิมพ์ใหญ่ 3 ตัว; ผิดรูป = ข้อความไทยที่ชี้ช่อง</summary>
    private static string NormalizeCurrencyCode(string? raw, string fieldLabel)
    {
        var code = (raw ?? "").Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(c => c >= 'A' && c <= 'Z'))
            throw new BusinessRuleException($"{fieldLabel}ต้องเป็นตัวอักษรภาษาอังกฤษ 3 ตัวตาม ISO 4217 (เช่น USD)");
        return code;
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
        var from = NormalizeCurrencyCode(request.FromCurrency, "สกุลเงินต้นทาง");
        var to = NormalizeCurrencyCode(request.ToCurrency, "สกุลเงินปลายทาง");
        if (from == to)
            throw new BusinessRuleException("สกุลเงินต้นทางและปลายทางต้องไม่ใช่สกุลเดียวกัน");
        // อัตรา 0 = "ค่าที่แต่งขึ้น" ที่ทำให้ทุกการแปลงค่าเงินได้ 0 เงียบ ๆ (DOCTRINE §1) — ปฏิเสธ
        if (request.MidRate <= 0)
            throw new BusinessRuleException("อัตราแลกเปลี่ยนต้องมากกว่า 0");
        if (request.BuyRate < 0 || request.SellRate < 0)
            throw new BusinessRuleException("อัตราซื้อ/อัตราขายต้องไม่ติดลบ (เว้นว่าง = ใช้อัตรากลาง)");

        var rate = new CurrencyRate
        {
            CompanyId = companyId,
            FromCurrency = from,
            ToCurrency = to,
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
            // MidRate > 0: แถวอัตรา 0 ที่ค้างจากบั๊กฟอร์มก่อนรอบ 193 (A03) ไม่ถูกหยิบมาใช้
            .Where(r => r.CompanyId == companyId && r.FromCurrency == fromCurrency && r.ToCurrency == toCurrency
                && r.MidRate > 0)
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
                && r.EffectiveDate <= date
                // แถว MidRate 0 (ค้างจากบั๊ก A03 ก่อนรอบ 193) = ไม่มีอัตรา ไม่ใช่ "อัตรา 0"
                && r.MidRate > 0)
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
                && r.EffectiveDate <= date
                && r.MidRate > 0)
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
                    && r.EffectiveDate <= asOfDate
                    && r.MidRate > 0)
                .OrderByDescending(r => r.EffectiveDate)
                .FirstOrDefaultAsync();

            if (currentRate == null) continue;

            var revaluedBalance = account.CurrentBalance * currentRate.MidRate;

            // Get previous revaluation or original booking rate balance
            var previousRate = await _db.CurrencyRates
                .Where(r => r.CompanyId == companyId
                    && r.FromCurrency == account.Currency
                    && r.ToCurrency == baseCurrency
                    && r.EffectiveDate <= asOfDate.AddMonths(-1)
                    && r.MidRate > 0)
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

