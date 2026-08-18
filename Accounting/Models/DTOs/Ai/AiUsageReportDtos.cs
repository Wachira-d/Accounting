using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Ai;

/// <summary>
/// ตัวเลขชุดเดียวที่ใช้ซ้ำทุกระดับของรายงาน (ทั้งแพลตฟอร์ม / รายลูกค้า /
/// รายช่องทาง / รายฟีเจอร์ / รายคีย์ API) — คำนวณจากที่เดียวเสมอ
/// (<c>AiUsageTotalsBuilder</c>) ไม่ให้สูตรแตกกันคนละจอ
/// </summary>
public record AiUsageTotals(
    int CallsTotal,
    /// <summary>ยิงถึง provider สำเร็จ = ครั้งที่ **เสียเงินจริง**</summary>
    int CallsAi,
    int CallsCached,
    /// <summary>local model ตอบเองได้จนไม่ต้องเรียก AI</summary>
    int CallsLocalServed,
    int CallsFailed,
    int CallsBudgetBlocked,
    int CallsNoProvider,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    decimal CostThb,
    /// <summary>เฉลี่ยแบบถ่วงน้ำหนักจริง (ผลรวม ÷ จำนวนตัวอย่าง) —
    /// ไม่ใช่เฉลี่ยของเฉลี่ย</summary>
    int AvgLatencyMs,
    int UserReviewed,
    int UserAcceptedAi,
    /// <summary>สัดส่วนที่ต้องพึ่ง AI จริง = CallsAi / CallsTotal.
    /// **ยิ่งต่ำยิ่งดี** — ตัวชี้วัดว่า local โตพอจะยืนเองแค่ไหน (กฎเหล็ก #1)</summary>
    decimal AiUsageRate,
    /// <summary>สัดส่วนที่ระบบตอบได้เองโดยไม่จ่ายเงิน (local + แคช)</summary>
    decimal SelfServedRate,
    /// <summary>ผู้ใช้รับคำตอบ AI ไปตรง ๆ กี่ % ของครั้งที่ตัดสินใจแล้ว —
    /// "AI แม่นแค่ไหนในสายตาคนใช้จริง" ไม่ใช่ความมั่นใจที่โมเดลบอกเอง</summary>
    decimal AcceptanceRate)
{
    public static readonly AiUsageTotals Empty =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0m, 0m, 0, 0, 0, 0m, 0m, 0m);
}

/// <summary>ลูกค้ารายหนึ่งในตารางสรุป</summary>
public record AiUsageCustomerRow(
    Guid CompanyId,
    string CompanyName,
    string? TaxId,
    Guid? BillingAccountId,
    string? BillingAccountName,
    /// <summary>เคยใช้ผ่านหน้าเว็บ NextAcc ในช่วงนี้ไหม</summary>
    bool UsesWeb,
    /// <summary>เคยยิงผ่าน API key ในช่วงนี้ไหม</summary>
    bool UsesApi,
    /// <summary>"เว็บอย่างเดียว" / "API อย่างเดียว" / "ผสม" / "งานระบบเท่านั้น"
    /// — ป้ายที่ตอบคำถาม "ลูกค้ารายนี้เป็นลูกค้าแบบไหน" ในคอลัมน์เดียว</summary>
    string CustomerKind,
    int ApiClientCount,
    string? TopFeature,
    int TopFeatureCalls,
    DateTime? LastUsedAt,
    AiUsageTotals Totals);

public record AiUsageChannelRow(
    AiUsageChannel Channel, string ChannelLabel, AiUsageTotals Totals);

public record AiUsageFeatureRow(
    string FeatureKey, string FeatureLabel, AiUsageTotals Totals);

public record AiUsageProviderRow(
    AiProviderType Provider, string ProviderLabel, AiUsageTotals Totals);

/// <summary>จุดบนกราฟแนวโน้มรายวัน</summary>
public record AiUsageDailyPoint(
    DateTime Date, int CallsTotal, int CallsAi, int CallsLocalServed,
    int CallsCached, decimal CostUsd, decimal CostThb);

/// <summary>คีย์ API หนึ่งใบของลูกค้า — หัวใจของมุมมอง "ลูกค้าที่ใช้ API อย่างเดียว"</summary>
public record AiUsageApiClientRow(
    Guid ApiClientId,
    string Name,
    string KeyPrefix,
    bool IsSandbox,
    string? Scopes,
    DateTime? KeyLastUsedAt,
    DateTime? LastAiCallAt,
    AiUsageTotals Totals);

/// <summary>ตัวอย่าง call ล่าสุด — ให้แอดมินเห็นของจริงไม่ใช่แค่ตัวเลขรวม</summary>
public record AiUsageCallSample(
    Guid FeedbackId,
    DateTime OccurredAt,
    string FeatureKey,
    AiUsageChannel Channel,
    string ChannelLabel,
    string StatusLabel,
    string? ApiClientName,
    int? LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    decimal? CostUsd,
    string? AiAnswer,
    string? LocalAnswer,
    string? UserChoice,
    bool? UserAcceptedAi,
    string? SourceEntityType,
    Guid? SourceEntityId);

/// <summary>ผลของหน้าสรุประดับแพลตฟอร์ม</summary>
public record AiUsageSummaryResponse(
    DateTime FromDate,
    DateTime ToDate,
    decimal UsdToThb,
    bool IncludesSandbox,
    AiUsageTotals Totals,
    IReadOnlyList<AiUsageCustomerRow> Customers,
    IReadOnlyList<AiUsageChannelRow> Channels,
    IReadOnlyList<AiUsageFeatureRow> Features,
    IReadOnlyList<AiUsageProviderRow> Providers,
    IReadOnlyList<AiUsageDailyPoint> Daily,
    /// <summary>สรุปเชิงธุรกิจให้อ่านเป็นประโยค — แอดมินไม่ต้องตีความเอง</summary>
    IReadOnlyList<string> Highlights);

/// <summary>ผลของหน้าเจาะลึกรายลูกค้า</summary>
public record AiUsageCustomerDetailResponse(
    Guid CompanyId,
    string CompanyName,
    string? TaxId,
    Guid? BillingAccountId,
    string? BillingAccountName,
    string CustomerKind,
    DateTime FromDate,
    DateTime ToDate,
    decimal UsdToThb,
    AiUsageTotals Totals,
    IReadOnlyList<AiUsageChannelRow> Channels,
    IReadOnlyList<AiUsageFeatureRow> Features,
    IReadOnlyList<AiUsageProviderRow> Providers,
    IReadOnlyList<AiUsageApiClientRow> ApiClients,
    IReadOnlyList<AiUsageDailyPoint> Daily,
    IReadOnlyList<AiUsageCallSample> RecentCalls,
    IReadOnlyList<string> Highlights);
