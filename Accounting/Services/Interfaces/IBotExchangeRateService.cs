namespace Accounting.Services.Interfaces;

/// <summary>
/// ดึงอัตราแลกเปลี่ยนจากธนาคารแห่งประเทศไทย (BOT)
/// API: https://apigw1.bot.or.th/bot/public/Fin-ExchangeRate/
/// </summary>
public interface IBotExchangeRateService
{
    Task<List<BotExchangeRate>> GetDailyRatesAsync(DateTime? date = null);
    Task<BotExchangeRate?> GetRateAsync(string currencyCode, DateTime? date = null);
    Task<int> SyncRatesToCompanyAsync(Guid companyId, DateTime? date = null);
}

public record BotExchangeRate(
    string CurrencyCode,
    string CurrencyNameTh,
    string CurrencyNameEn,
    decimal BuyingSight,
    decimal BuyingTransfer,
    decimal Selling,
    decimal MidRate,
    DateTime Period);
