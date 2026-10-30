using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>สถานะช่องทางรับชำระค่าบริการของ NextAcc ที่หน้าแพ็กเกจ/ส่วนเสริมใช้ตัดสินว่าจะแสดงปุ่ม "ชำระออนไลน์" ไหม
/// (<c>Online</c> = มีช่องทางที่<b>เปิดรับเงินจริงแล้ว</b>เท่านั้น — โหมดทดสอบ/ปิด/ยังไม่ตั้ง ⇒ เส้นโอน + แนบสลิป)</summary>
public sealed record PlatformPayStatus(bool Online, bool Configured, string? Mode, string? ProviderCode, IReadOnlyList<string> Methods, string? OfflineReason);

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179 · ต่อจากเฟส 0 ข้อ 178) — <b>ทางเดียว</b>ที่สร้างรายการชำระค่าบริการ NextAcc แบบออนไลน์:
/// intent อยู่ใต้<b>บริษัทผู้ให้บริการ</b> (<c>SiteSettings.PlatformCompanyId</c>) ด้วย config ของบริษัทนั้น + ระบุ <c>PayerCompanyId</c> ·
/// <b>ยอดคำนวณที่เซิร์ฟเวอร์เสมอ</b> (ส่วนเสริม = <c>IAddOnPurchaseService.AmountDueAsync</c> · แพ็กเกจ = ยอดของรายการชำระที่สร้างจาก
/// <c>PlatformPlanPricing</c>) · ด่านเฟส 0 (<see cref="PlatformRevenueSource"/>) ใน <c>PaymentIntentService.StartAsync</c> ยังตรวจซ้ำ ·
/// ศูนย์ชำระค่าบริการ (ทีมเฟส 0) เรียก <see cref="StartAsync"/> ด้วยชนิดต้นทาง/ยอดของตัวเอง
/// </summary>
public interface IPlatformPaymentService
{
    Task<Guid?> PlatformCompanyIdAsync(CancellationToken ct = default);
    /// <summary>config ผู้ให้บริการออนไลน์ (ไม่ใช่สลิป) ของบริษัทผู้ให้บริการ — null = ยังไม่ตั้ง</summary>
    Task<PaymentProviderConfig?> PlatformConfigAsync(CancellationToken ct = default);
    Task<PlatformPayStatus> StatusAsync(CancellationToken ct = default);
    /// <summary>เริ่มชำระออนไลน์ (ช่องทางต้องเปิดรับเงินจริงแล้ว) — <paramref name="serverAmount"/> ต้องมาจากเซิร์ฟเวอร์ · ไม่ผ่าน ⇒ InvalidOperationException (ข้อความไทย)</summary>
    Task<PaymentIntent> StartAsync(Guid payerCompanyId, PaymentSourceKind kind, Guid sourceId, decimal serverAmount, PaymentMethodKind method,
        string? description, string? returnUrl, string? cardToken, CancellationToken ct = default);
    Task<PaymentIntent> StartAddOnAsync(Guid payerCompanyId, string featureCode, PaymentMethodKind method, string? returnUrl, string? cardToken, CancellationToken ct = default);
    Task<PaymentIntent> StartSubscriptionPaymentAsync(Guid payerCompanyId, Guid subscriptionPaymentId, PaymentMethodKind method, string? returnUrl, string? cardToken, CancellationToken ct = default);
    /// <summary>สถานะสดของรายการที่บริษัทนี้เป็นผู้จ่าย — รายการของบริษัทอื่น/ของร้าน ⇒ null</summary>
    Task<PaymentIntent?> RefreshForPayerAsync(Guid payerCompanyId, Guid intentId, CancellationToken ct = default);
    /// <summary>ต้นทางของรายการ (ส่วนเสริม: CompanyFeature.Id · แพ็กเกจ: SubscriptionPayment.Id) — ใช้ผูกหลักฐานยอมรับนโยบาย</summary>
    Task<Guid?> AddOnSourceIdAsync(Guid payerCompanyId, string featureCode, CancellationToken ct = default);
}

public class PlatformPaymentService : IPlatformPaymentService
{
    private readonly AccountingDbContext _db;
    private readonly IPaymentIntentService _intents;
    private readonly IAddOnPurchaseService _addons;
    private readonly IEnumerable<IPaymentProvider> _providers;

    public PlatformPaymentService(AccountingDbContext db, IPaymentIntentService intents, IAddOnPurchaseService addons,
        IEnumerable<IPaymentProvider> providers)
    { _db = db; _intents = intents; _addons = addons; _providers = providers; }

    public Task<Guid?> PlatformCompanyIdAsync(CancellationToken ct = default)
        => _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.PlatformCompanyId).FirstOrDefaultAsync(ct);

    public async Task<PaymentProviderConfig?> PlatformConfigAsync(CancellationToken ct = default)
    {
        if (await PlatformCompanyIdAsync(ct) is not Guid pid) return null;
        return await _db.PaymentProviderConfigs
            .Where(c => c.CompanyId == pid && !c.IsDeleted && c.ProviderCode != Providers.ManualSlipPaymentProvider.Code)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PlatformPayStatus> StatusAsync(CancellationToken ct = default)
    {
        var cfg = await PlatformConfigAsync(ct);
        if (cfg == null)
            return new PlatformPayStatus(false, false, null, null, Array.Empty<string>(),
                "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินแล้วแนบสลิป");
        var methods = MethodsOf(cfg);
        var reason = PlatformPayAvailability.OfflineReason(cfg.IsActive, cfg.Mode, methods.Count);
        return new PlatformPayStatus(reason == null, true, cfg.Mode.ToString(), cfg.ProviderCode, methods, reason);
    }

    /// <summary>วิธีจ่ายที่เปิด = ที่ตั้งไว้ ∩ ที่ผู้ให้บริการรองรับ (ไม่ตั้ง = ทุกวิธีที่รองรับ ยกเว้นสลิป) — ชื่อ enum</summary>
    private List<string> MethodsOf(PaymentProviderConfig cfg)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode);
        if (provider == null) return new List<string>();
        var supported = provider.Capabilities.Methods.Where(m => m != PaymentMethodKind.ManualSlip).Select(m => m.ToString()).ToList();
        List<string> configured;
        try { configured = string.IsNullOrWhiteSpace(cfg.EnabledMethodsJson) ? new() : System.Text.Json.JsonSerializer.Deserialize<List<string>>(cfg.EnabledMethodsJson) ?? new(); }
        catch (System.Text.Json.JsonException) { configured = new(); }
        return configured.Count == 0 ? supported : supported.Where(m => configured.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public async Task<PaymentIntent> StartAsync(Guid payerCompanyId, PaymentSourceKind kind, Guid sourceId, decimal serverAmount,
        PaymentMethodKind method, string? description, string? returnUrl, string? cardToken, CancellationToken ct = default)
    {
        if (!PlatformRevenueSource.IsPlatformRevenue(kind))
            throw new InvalidOperationException("ช่องทางนี้รับเฉพาะค่าบริการ NextAcc");
        var cfg = await PlatformConfigAsync(ct);
        var reason = cfg == null ? "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินแล้วแนบสลิป"
            : PlatformPayAvailability.OfflineReason(cfg.IsActive, cfg.Mode, MethodsOf(cfg).Count);
        if (reason != null) throw new InvalidOperationException(reason);
        if (!MethodsOf(cfg!).Contains(method.ToString()))
            throw new InvalidOperationException("ช่องทางรับชำระของ NextAcc ไม่ได้เปิดวิธีชำระนี้");
        return await _intents.StartAsync(cfg!.CompanyId, new StartPaymentRequest(
            kind, sourceId, serverAmount, method, description, ReturnUrl: returnUrl, CardToken: cardToken,
            PayerCompanyId: payerCompanyId), providerCode: cfg.ProviderCode, ct: ct);
    }

    public async Task<Guid?> AddOnSourceIdAsync(Guid payerCompanyId, string featureCode, CancellationToken ct = default)
        => await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == payerCompanyId && f.FeatureCode == featureCode && !f.IsDeleted)
            .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);

    public async Task<PaymentIntent> StartAddOnAsync(Guid payerCompanyId, string featureCode, PaymentMethodKind method, string? returnUrl,
        string? cardToken, CancellationToken ct = default)
    {
        var sourceId = await AddOnSourceIdAsync(payerCompanyId, featureCode, ct)
            ?? throw new InvalidOperationException("ยังไม่ได้เปิดใช้ส่วนเสริมนี้");
        var due = await _addons.AmountDueAsync(payerCompanyId, featureCode, ct);
        if (due <= 0m) throw new InvalidOperationException("ส่วนเสริมนี้ไม่มียอดค้างชำระ");
        var name = await _db.ApiFeatures.AsNoTracking().Where(f => f.FeatureCode == featureCode).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? featureCode;
        return await StartAsync(payerCompanyId, PaymentSourceKind.AddOnPurchase, sourceId, due, method,
            $"ค่าส่วนเสริม {name}", returnUrl, cardToken, ct);
    }

    public async Task<PaymentIntent> StartSubscriptionPaymentAsync(Guid payerCompanyId, Guid subscriptionPaymentId, PaymentMethodKind method,
        string? returnUrl, string? cardToken, CancellationToken ct = default)
    {
        var pay = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.Id == subscriptionPaymentId && p.Subscription.CompanyId == payerCompanyId)
            .Select(p => new { p.Id, p.Amount, p.Status, p.PaymentNumber })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("ไม่พบรายการชำระค่าแพ็กเกจของบริษัทนี้");
        if (pay.Status is not (SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview))
            throw new InvalidOperationException("รายการนี้ไม่อยู่ในสถานะรอชำระ");
        // ยอด = ยอดของรายการชำระที่เซิร์ฟเวอร์คำนวณตอนสร้าง (PlatformPlanPricing.AmountDue) — handler ตรวจกับตารางราคาอีกชั้น
        return await StartAsync(payerCompanyId, PaymentSourceKind.SubscriptionPayment, pay.Id, pay.Amount, method,
            $"ค่าแพ็กเกจ {pay.PaymentNumber}", returnUrl, cardToken, ct);
    }

    public async Task<PaymentIntent?> RefreshForPayerAsync(Guid payerCompanyId, Guid intentId, CancellationToken ct = default)
    {
        if (await PlatformCompanyIdAsync(ct) is not Guid pid) return null;
        var owned = await _db.PaymentIntents.AsNoTracking()
            .AnyAsync(i => i.Id == intentId && i.CompanyId == pid && i.PayerCompanyId == payerCompanyId, ct);
        if (!owned) return null;
        return await _intents.RefreshAsync(pid, intentId, ct);
    }
}
