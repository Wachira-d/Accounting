namespace Accounting.Models.DTOs.Currency;

/// <summary>รอบ 193 · A02: <c>CurrencyName</c> เดิม non-nullable ⇒ [Required] โดยปริยาย ขณะที่ฟอร์ม
/// "เพิ่มสกุลเงิน" ไม่เคยส่ง ⇒ เพิ่มสกุลเงินไม่ได้เลย. ตอนนี้ว่าง = ใช้รหัสเป็นชื่อ (derive ที่ service).
/// <c>InitialRate</c>: ฟอร์มมีช่อง "อัตราแลกเปลี่ยนเริ่มต้น" มาตลอดแต่สัญญาไม่มีที่ลง ⇒ ค่าที่กรอก
/// หายเงียบ — ตอนนี้ถ้าส่งมา service บันทึกเป็นอัตรา {Code}→THB วันนี้ (ต้อง &gt; 0)</summary>
public record CreateCompanyCurrencyRequest(
    string CurrencyCode,
    string? CurrencyName,
    string? Symbol,
    int DecimalPlaces = 2,
    decimal? InitialRate = null);

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

/// <summary>รอบ 193 · A03: หน้าเว็บเคยส่ง <c>rate</c> (ไม่มีในสัญญา) ⇒ ทั้งสามช่องเป็น 0 แล้วถูก**บันทึกจริง**
/// ⇒ แปลงค่าเงินได้ 0 เงียบ ๆ. ตอนนี้ <c>MidRate</c> ต้อง &gt; 0 (BusinessRuleException ไทย) ·
/// Buy/Sell = 0 แปลว่า "ไม่ระบุ" (ConvertAsync ถอยไปใช้ MidRate อยู่แล้ว) · ห้ามติดลบ</summary>
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

public record UnrealizedGainLossItem(
    Guid BankAccountId, string AccountName, string Currency,
    decimal ForeignBalance, decimal ExchangeRate,
    decimal BaseAmount, decimal GainLoss);
