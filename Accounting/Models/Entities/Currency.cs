namespace Accounting.Models.Entities;

/// <summary>
/// Multi-Currency Support (อัตราแลกเปลี่ยน)
/// เทียบเท่า PEAK: Multi-currency
/// </summary>
public class CurrencyRate : TenantEntity
{
    public string FromCurrency { get; set; } = null!;    // e.g. "USD"
    public string ToCurrency { get; set; } = "THB";
    public DateTime EffectiveDate { get; set; }
    public decimal BuyRate { get; set; }
    public decimal SellRate { get; set; }
    public decimal MidRate { get; set; }
    public string? Source { get; set; }                  // e.g. "BOT", "Manual"
}

/// <summary>
/// สกุลเงินที่บริษัทใช้
/// </summary>
public class CompanyCurrency : TenantEntity
{
    public string CurrencyCode { get; set; } = null!;    // e.g. "USD", "EUR"
    public string CurrencyName { get; set; } = null!;
    public string? Symbol { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public bool IsActive { get; set; } = true;
}
