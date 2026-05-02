using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// BOT Daily Foreign Exchange Rates
/// Docs: https://apigw1.bot.or.th/bot/public/Fin-ExchangeRate/
/// Free tier: no API key required for daily rates endpoint
/// Format: JSON
/// </summary>
public class BotExchangeRateService : IBotExchangeRateService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AccountingDbContext _db;
    private readonly ILogger<BotExchangeRateService> _logger;

    private const string BotApiBaseUrl = "https://apigw1.bot.or.th/bot/public/Fin-ExchangeRate/v2/DailyAvg";

    public BotExchangeRateService(
        IHttpClientFactory httpClientFactory,
        AccountingDbContext db,
        ILogger<BotExchangeRateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _db = db;
        _logger = logger;
    }

    public async Task<List<BotExchangeRate>> GetDailyRatesAsync(DateTime? date = null)
    {
        var targetDate = date ?? DateTime.UtcNow.AddHours(7); // ICT = UTC+7
        var dateStr = targetDate.ToString("yyyy-MM-dd");

        try
        {
            var client = _httpClientFactory.CreateClient("Bot");
            client.DefaultRequestHeaders.Add("X-IBM-Client-Id", "not-required");

            var url = $"{BotApiBaseUrl}?start_period={dateStr}&end_period={dateStr}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BOT API returned {Status} for date {Date}", response.StatusCode, dateStr);

                // Fallback: try the RSS/XML endpoint which has been more stable
                return await GetRatesFromRssFallbackAsync(targetDate);
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return ParseBotJsonResponse(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BOT exchange rate fetch failed for {Date}", dateStr);

            // Fallback to RSS endpoint
            try
            {
                return await GetRatesFromRssFallbackAsync(targetDate);
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "BOT RSS fallback also failed");
                return new List<BotExchangeRate>();
            }
        }
    }

    public async Task<BotExchangeRate?> GetRateAsync(string currencyCode, DateTime? date = null)
    {
        var rates = await GetDailyRatesAsync(date);
        return rates.FirstOrDefault(r =>
            r.CurrencyCode.Equals(currencyCode, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> SyncRatesToCompanyAsync(Guid companyId, DateTime? date = null)
    {
        var rates = await GetDailyRatesAsync(date);
        if (rates.Count == 0) return 0;

        var synced = 0;
        foreach (var rate in rates)
        {
            var existing = await _db.CurrencyRates
                .FirstOrDefaultAsync(r => r.CompanyId == companyId
                    && r.FromCurrency == rate.CurrencyCode
                    && r.ToCurrency == "THB"
                    && r.EffectiveDate.Date == rate.Period.Date);

            if (existing != null) continue;

            _db.CurrencyRates.Add(new CurrencyRate
            {
                CompanyId = companyId,
                FromCurrency = rate.CurrencyCode,
                ToCurrency = "THB",
                EffectiveDate = rate.Period,
                BuyRate = rate.BuyingTransfer,
                SellRate = rate.Selling,
                MidRate = rate.MidRate,
                Source = "BOT"
            });
            synced++;
        }

        if (synced > 0) await _db.SaveChangesAsync();

        _logger.LogInformation("Synced {Count} BOT exchange rates for company {CompanyId}", synced, companyId);
        return synced;
    }

    private List<BotExchangeRate> ParseBotJsonResponse(JsonElement json)
    {
        var results = new List<BotExchangeRate>();

        if (!json.TryGetProperty("result", out var result)) return results;
        if (!result.TryGetProperty("data", out var data)) return results;
        if (!data.TryGetProperty("data_detail", out var details)) return results;

        foreach (var item in details.EnumerateArray())
        {
            var currCode = GetStr(item, "currency_id") ?? "";
            if (string.IsNullOrEmpty(currCode)) continue;

            var buying = GetDecimal(item, "buying_sight") ?? 0;
            var buyingTransfer = GetDecimal(item, "buying_transfer") ?? 0;
            var selling = GetDecimal(item, "selling") ?? 0;
            var mid = GetDecimal(item, "mid_rate") ?? ((buyingTransfer + selling) / 2);
            var periodStr = GetStr(item, "period") ?? "";

            DateTime.TryParseExact(periodStr, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var period);

            results.Add(new BotExchangeRate(
                CurrencyCode: currCode,
                CurrencyNameTh: GetStr(item, "currency_name_th") ?? currCode,
                CurrencyNameEn: GetStr(item, "currency_name_eng") ?? currCode,
                BuyingSight: buying,
                BuyingTransfer: buyingTransfer,
                Selling: selling,
                MidRate: mid,
                Period: period));
        }

        return results;
    }

    /// <summary>
    /// Fallback: BOT publishes rates via a simpler endpoint that doesn't require API gateway auth.
    /// </summary>
    private async Task<List<BotExchangeRate>> GetRatesFromRssFallbackAsync(DateTime date)
    {
        var client = _httpClientFactory.CreateClient();
        var dateStr = date.ToString("yyyy-MM-dd");

        var url = $"https://www.bot.or.th/api/fxrate?date={dateStr}";
        var response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode) return new List<BotExchangeRate>();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = new List<BotExchangeRate>();

        if (json.TryGetProperty("rates", out var rates))
        {
            foreach (var item in rates.EnumerateArray())
            {
                var code = GetStr(item, "currencyCode", "currency_code", "code") ?? "";
                if (string.IsNullOrEmpty(code)) continue;

                var buy = GetDecimal(item, "buyTransfer", "buying_transfer", "buy") ?? 0;
                var sell = GetDecimal(item, "sell", "selling") ?? 0;
                var mid = GetDecimal(item, "midRate", "mid_rate") ?? ((buy + sell) / 2);

                results.Add(new BotExchangeRate(
                    CurrencyCode: code,
                    CurrencyNameTh: GetStr(item, "currencyNameTh") ?? code,
                    CurrencyNameEn: GetStr(item, "currencyNameEn") ?? code,
                    BuyingSight: GetDecimal(item, "buySight", "buying_sight") ?? buy,
                    BuyingTransfer: buy,
                    Selling: sell,
                    MidRate: mid,
                    Period: date.Date));
            }
        }

        return results;
    }

    private static string? GetStr(JsonElement el, params string[] props)
    {
        foreach (var p in props)
            if (el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, params string[] props)
    {
        foreach (var p in props)
        {
            if (!el.TryGetProperty(p, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var d)) return d;
        }
        return null;
    }
}
