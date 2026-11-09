using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>สถานะช่องทางรับชำระค่าบริการของ NextAcc ที่หน้าแพ็กเกจ/ส่วนเสริมใช้ตัดสินว่าจะแสดงปุ่ม "ชำระออนไลน์" ไหม
/// (<c>Online</c> = มีช่องทางที่<b>เปิดรับเงินจริงแล้ว</b>เท่านั้น — โหมดทดสอบ/ปิด/ยังไม่ตั้ง ⇒ เส้นโอน + แนบสลิป)
/// <para>รอบ 228: <c>Card</c> = สิ่งที่หน้าชำระต้องใช้รับบัตร (สคริปต์ + คีย์สาธารณะของโหมดนี้) — null = รับบัตรไม่ได้ (วิธี "บัตร" ไม่อยู่ใน <c>Methods</c> ด้วย)</para></summary>
public sealed record PlatformPayStatus(bool Online, bool Configured, string? Mode, string? ProviderCode, IReadOnlyList<string> Methods, string? OfflineReason,
    PaymentClientSetup? Card = null);

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
    /// <para>รอบ 230 (§5.2): <paramref name="savedCard"/> = ตัดด้วยบัตรที่บันทึกไว้ของผู้จ่ายรายนี้ (วิธีต้องเป็นบัตร · ไม่ต้องมีโทเคน)</para>
    Task<PaymentIntent> StartAsync(Guid payerCompanyId, PaymentSourceKind kind, Guid sourceId, decimal serverAmount, PaymentMethodKind method,
        string? description, string? returnUrl, string? cardToken, CancellationToken ct = default, PlatformCardRefs? savedCard = null);
    Task<PaymentIntent> StartAddOnAsync(Guid payerCompanyId, string featureCode, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default, PlatformCardRefs? savedCard = null);
    /// <param name="allowTestMode">รอบ 232 (ฝ่ายค้าน S1): true เฉพาะหน้าซื้อแพ็กเกจสาธารณะ (<c>PublicCheckoutService</c>) — ให้โหมดทดสอบผ่านด่านช่องทางได้
    /// (<see cref="PlatformPayAvailability.StartRefusal"/>) · ด่านอื่นเหมือนเดิมทั้งหมด · ผู้เรียกเดิมไม่ส่ง ⇒ โหมดทดสอบถูกปฏิเสธเหมือนเดิม</param>
    Task<PaymentIntent> StartSubscriptionPaymentAsync(Guid payerCompanyId, Guid subscriptionPaymentId, PaymentMethodKind method, string? returnUrl,
        string? cardToken, CancellationToken ct = default, PlatformCardRefs? savedCard = null, bool allowTestMode = false, Guid? actorUserId = null);
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
        => Accounting.Helpers.PlatformCompanyLookup.IdAsync(_db, ct);

    public async Task<PaymentProviderConfig?> PlatformConfigAsync(CancellationToken ct = default)
    {
        if (await PlatformCompanyIdAsync(ct) is not Guid pid) return null;
        // ฝ่ายค้าน (หมายเหตุ): เลือกแน่นอน — ตัวที่ผ่านด่านแพลตฟอร์มแล้วก่อน แล้วตามลำดับที่ตั้ง/สร้าง/Id (ไม่ขึ้นกับลำดับที่ฐานคืน)
        return await _db.PaymentProviderConfigs
            .Where(c => c.CompanyId == pid && !c.IsDeleted && c.ProviderCode != Providers.ManualSlipPaymentProvider.Code)
            .OrderByDescending(c => c.PlatformLiveConfirmedAt != null)
            .ThenBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PlatformPayStatus> StatusAsync(CancellationToken ct = default)
    {
        var cfg = await PlatformConfigAsync(ct);
        if (cfg == null)
            return new PlatformPayStatus(false, false, null, null, Array.Empty<string>(),
                "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินแล้วแนบสลิป");
        var methods = MethodsOf(cfg);
        var reason = PlatformPayAvailability.OfflineReason(cfg.IsActive, cfg.Mode, methods.Count, cfg.PlatformLiveConfirmedAt != null);
        var card = methods.Contains(nameof(PaymentMethodKind.Card)) ? CardSetupOf(cfg) : null;
        return new PlatformPayStatus(reason == null, true, cfg.Mode.ToString(), cfg.ProviderCode, methods, reason, card);
    }

    /// <summary>วิธีจ่ายที่เปิด = ที่ตั้งไว้ ∩ ที่ผู้ให้บริการรองรับ (ไม่ตั้ง = ทุกวิธีที่รองรับ ยกเว้นสลิป) ∩ ที่หน้าชำระของ NextAcc ทำจบได้ — ชื่อ enum
    /// (รอบ 227: ตัวตัดสินเดียว <see cref="PlatformPayMethods.Effective"/> — เดิมบัตร/วอลเล็ท/แบงก์กิ้งโผล่ให้ผู้เช่าทั้งที่หน้าชำระทำไม่จบ)</summary>
    private List<string> MethodsOf(PaymentProviderConfig cfg)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode);
        if (provider == null) return new List<string>();
        return PlatformPayMethods.Effective(provider.Capabilities.Methods, ConfiguredMethods(cfg.EnabledMethodsJson),
            cardClientReady: PaymentClientSetup.For(provider, cfg) != null);
    }

    /// <summary>รอบ 228 (§5.1): สคริปต์ + คีย์สาธารณะของโหมดปัจจุบัน — ตัวเดียวกับเส้นของร้าน (<see cref="PaymentClientSetup.For"/>)</summary>
    private PaymentClientSetup? CardSetupOf(PaymentProviderConfig cfg)
        => PaymentClientSetup.For(_providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode), cfg);

    /// <summary>วิธีที่แอดมินติ๊ก (JSON ที่เก็บไว้) — อ่านไม่ได้ = ไม่ได้ติ๊ก</summary>
    public static List<string> ConfiguredMethods(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new List<string>(); }
    }

    public Task<PaymentIntent> StartAsync(Guid payerCompanyId, PaymentSourceKind kind, Guid sourceId, decimal serverAmount,
        PaymentMethodKind method, string? description, string? returnUrl, string? cardToken, CancellationToken ct = default,
        PlatformCardRefs? savedCard = null)
    {
        return StartCoreAsync(payerCompanyId, kind, sourceId, serverAmount, method, description, returnUrl, cardToken, ct, savedCard, allowTestMode: false);
    }

    /// <summary>รอบ 232 (ฝ่ายค้าน S1): ตัวเริ่มชำระตัวเดียว — <paramref name="allowTestMode"/> ส่งต่อจาก <see cref="StartSubscriptionPaymentAsync"/> เท่านั้น</summary>
    private async Task<PaymentIntent> StartCoreAsync(Guid payerCompanyId, PaymentSourceKind kind, Guid sourceId, decimal serverAmount,
        PaymentMethodKind method, string? description, string? returnUrl, string? cardToken, CancellationToken ct,
        PlatformCardRefs? savedCard, bool allowTestMode)
    {
        if (!PlatformRevenueSource.IsPlatformRevenue(kind))
            throw new InvalidOperationException("ช่องทางนี้รับเฉพาะค่าบริการ NextAcc");
        var cfg = await PlatformConfigAsync(ct);
        var reason = cfg == null ? "ยังไม่เปิดชำระค่าบริการ NextAcc ออนไลน์ — กรุณาโอนเงินแล้วแนบสลิป"
            : PlatformPayAvailability.StartRefusal(cfg.IsActive, cfg.Mode, MethodsOf(cfg).Count, cfg.PlatformLiveConfirmedAt != null, allowTestMode);
        if (reason != null) throw new InvalidOperationException(reason);
        if (!MethodsOf(cfg!).Contains(method.ToString()))
            throw new InvalidOperationException("ช่องทางรับชำระของ NextAcc ไม่ได้เปิดวิธีชำระนี้");
        // รอบ 230: บัตรที่บันทึกไว้ต้องเป็นของผู้จ่ายรายนี้ + ผู้ให้บริการ/โหมดเดียวกับช่องทางตอนนี้ (ตัวตัดสินเดียว PlatformSavedCardRules.UseRefusal)
        if (savedCard != null && PlatformSavedCardRules.UseRefusal(method == PaymentMethodKind.Card, savedCard.PayerCompanyId == payerCompanyId,
                savedCard.ProviderCode == cfg!.ProviderCode, savedCard.IsTestMode == (cfg.Mode == PaymentProviderMode.Test)) is string cardNo)
            throw new InvalidOperationException(cardNo);
        return await _intents.StartAsync(cfg!.CompanyId, new StartPaymentRequest(
            kind, sourceId, serverAmount, method, description, ReturnUrl: returnUrl, CardToken: savedCard == null ? cardToken : null,
            PayerCompanyId: payerCompanyId, CustomerRef: savedCard?.CustomerRef, SavedCardRef: savedCard?.CardRef),
            providerCode: cfg.ProviderCode, ct: ct);
    }

    public async Task<Guid?> AddOnSourceIdAsync(Guid payerCompanyId, string featureCode, CancellationToken ct = default)
        => await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == payerCompanyId && f.FeatureCode == featureCode && !f.IsDeleted)
            .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);

    public async Task<PaymentIntent> StartAddOnAsync(Guid payerCompanyId, string featureCode, PaymentMethodKind method, string? returnUrl,
        string? cardToken, CancellationToken ct = default, PlatformCardRefs? savedCard = null)
    {
        var sourceId = await AddOnSourceIdAsync(payerCompanyId, featureCode, ct)
            ?? throw new InvalidOperationException("ยังไม่ได้เปิดใช้ส่วนเสริมนี้");
        // ฝ่ายค้าน S2: แนบสลิปรอตรวจอยู่ ⇒ ห้ามชำระออนไลน์ซ้อน
        var addOnStatus = await _db.CompanyFeatures.AsNoTracking().Where(f => f.Id == sourceId).Select(f => f.PaymentStatus).FirstOrDefaultAsync(ct);
        if (PlatformDoublePayment.OnlineStartRefusal(addOnStatus == AddOnPaymentStatus.PendingReview) is string slipNo)
            throw new InvalidOperationException(slipNo);
        var due = await _addons.AmountDueAsync(payerCompanyId, featureCode, ct);
        if (due <= 0m) throw new InvalidOperationException("ส่วนเสริมนี้ไม่มียอดค้างชำระ");
        var name = await _db.ApiFeatures.AsNoTracking().Where(f => f.FeatureCode == featureCode).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? featureCode;
        return await StartAsync(payerCompanyId, PaymentSourceKind.AddOnPurchase, sourceId, due, method,
            $"ค่าส่วนเสริม {name}", returnUrl, cardToken, ct, savedCard);
    }

    public async Task<PaymentIntent> StartSubscriptionPaymentAsync(Guid payerCompanyId, Guid subscriptionPaymentId, PaymentMethodKind method,
        string? returnUrl, string? cardToken, CancellationToken ct = default, PlatformCardRefs? savedCard = null, bool allowTestMode = false, Guid? actorUserId = null)
    {
        var pay = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.Id == subscriptionPaymentId && p.Subscription.CompanyId == payerCompanyId)
            .Select(p => new { p.Id, p.Amount, p.WithholdingTaxAmount, p.Status, p.PaymentNumber, p.SlipFileName, p.RequestedPlan, p.RequestedBillingCycle,
                p.PlatformInvoiceDocumentId, Held = p.AttentionReason != null, p.QuotedAmount, p.Subscription.AccountSubscriptionId, p.SubscriptionId })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("ไม่พบรายการชำระค่าแพ็กเกจของบริษัทนี้");
        if (pay.Status is not (SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview))
            throw new InvalidOperationException("รายการนี้ไม่อยู่ในสถานะรอชำระ");
        // ฝ่ายค้าน S2 (part B) + เฟส 2 N7: แนบสลิปแล้ว/รอตรวจ/พักให้แอดมิน ⇒ ห้ามชำระออนไลน์ซ้อน
        if (PlatformDoublePayment.OnlineStartRefusal(pay.SlipFileName != null || pay.Status == SubscriptionPaymentStatus.UnderReview || pay.Held) is string slipNo)
            throw new InvalidOperationException(slipNo);
        // ฝ่ายค้าน S3 (part B): ยอดของรายการแบบเดิม (ไม่ผูกใบ) ต้องยังตรงราคาแพ็กเกจปัจจุบันก่อนเก็บเงิน (ตัวตั้งเดียวกับ handler) ·
        // รายการที่ผูกใบแจ้งหนี้ (ศูนย์บิล/ลิงก์ชำระ) ยอด = ยอดค้างของใบ (เซิร์ฟเวอร์) ไม่ใช่ราคาแพ็กเกจ — handler ก็ข้ามด่านราคาเหมือนกัน
        if (pay.PlatformInvoiceDocumentId == null)
        {
            // รอบ 236 (236-2): รายการค่าแพ็กเกจระดับบริษัทที่สร้างก่อนบริษัทถูกผูก License ⇒ ไม่เริ่มเก็บเงิน (จ่ายแล้วไม่มีผล) · รายการชำระใบแจ้งหนี้ (หนี้จริง) ไม่ถูกกัน
            // ฝ่ายค้าน B1: ผู้ถือ License จ่ายแพ็กระดับ License = ต่ออายุ License (ยอม) · ผู้เรียกที่ไม่ส่งผู้ทำรายการ (งานระบบ) = ไม่ใช่ผู้ถือ
            await Accounting.Services.Platform.PlanIdentityReader.EnsureCompanyMayPurchaseAsync(_db, payerCompanyId, actorUserId, pay.RequestedPlan, ct);
            var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == pay.RequestedPlan && t.IsActive, ct);
            // รอบ 230 โควตา/บิล (ฝ่ายค้าน B1): ตัวตัดสินเดียวกับ handler — รายการที่เซิร์ฟเวอร์เสนอยอดเอง (ส่วนต่างอัปเกรด/คูปอง/แอดมินแก้) เทียบ QuotedAmount ·
            // รายการราคาเต็มเทียบราคาแพ็กเกจปัจจุบันตามเดิม (ราคาเปลี่ยน = ปฏิเสธ)
            // รอบ 231 (231-8): ราคาเต็ม = ราคาพิเศษของบริษัทนี้ (PlatformPriceOverrideStore — ตัวเดียวกับ handler)
            var listPrice = PlatformPlanPricing.AutoApproveExpected(pay.QuotedAmount,
                template == null ? 0m : await PlatformPriceOverrideStore.AmountDueAsync(_db, payerCompanyId, pay.AccountSubscriptionId, template, pay.RequestedBillingCycle, DateTime.UtcNow,
                    currentPlan: await _db.Subscriptions.AsNoTracking().Where(s => s.Id == pay.SubscriptionId).Select(s => (SubscriptionPlan?)s.Plan).FirstOrDefaultAsync(ct), ct: ct));   // ฝ่ายค้าน S4
            // รอบ 234: รายการที่ผู้เช่าหักภาษี ณ ที่จ่าย — ราคาเทียบกับยอดเต็ม (เงินที่จะรับ + ภาษีหัก · PlatformWhtSuggestion.GrossOf) · ยอดเก็บเงินจริง = Amount (สุทธิ)
            if ((template == null && pay.QuotedAmount == null) || listPrice <= 0m
                || !PlatformPlanPricing.ClaimedAmountAcceptable(PlatformWhtSuggestion.GrossOf(pay.Amount, pay.WithholdingTaxAmount), listPrice) || pay.Amount <= 0m)
                throw new InvalidOperationException("ยอดของรายการชำระนี้ไม่ตรงกับราคาแพ็กเกจปัจจุบัน (ราคาอาจเปลี่ยนหลังสร้างรายการ) — "
                    + "กรุณาเลือกแพ็กเกจใหม่เพื่อสร้างรายการชำระตามราคาปัจจุบัน หรือติดต่อผู้ดูแลระบบ");
        }
        else if (pay.Amount <= 0m) throw new InvalidOperationException("รายการชำระใบแจ้งหนี้นี้ไม่มียอด");
        // ฝ่ายค้าน 234 S1: รายการที่ผู้เช่าหักภาษี ⇒ ตรวจสิทธิ์หักซ้ำก่อนเก็บเงินยอดสุทธิ (ผู้ซื้อยังเป็นนิติบุคคล + ข้อมูลบริษัทครบ · ตัวตัดสินเดียว)
        if (pay.WithholdingTaxAmount > 0m)
        {
            var payerNow = await Accounting.Services.Platform.PlatformWithholdingReader.PayerAsync(_db, payerCompanyId, ct);
            var whyNot = !payerNow.IsJuristic
                ? "บริษัทไม่ใช่นิติบุคคลแล้ว (เลขผู้เสียภาษีเปลี่ยน) จึงหักภาษี ณ ที่จ่ายไม่ได้"
                : PlatformPayerProfileGate.WithholdingRefusal(payerNow);
            if (whyNot != null)
                throw new InvalidOperationException($"รายการ {pay.PaymentNumber} ตั้งหักภาษี ณ ที่จ่ายไว้ แต่{whyNot} — แก้ข้อมูลบริษัทที่ศูนย์บิล "
                    + "หรือเลือกแพ็กเกจใหม่แบบไม่ติ๊กหัก ณ ที่จ่าย แล้วชำระอีกครั้ง");
        }
        // ยอด = ยอดของรายการชำระที่เซิร์ฟเวอร์คำนวณตอนสร้าง (PlatformPlanPricing.AmountDue) — handler ตรวจกับตารางราคาอีกชั้น
        return await StartCoreAsync(payerCompanyId, PaymentSourceKind.SubscriptionPayment, pay.Id, pay.Amount, method,
            $"ค่าแพ็กเกจ {pay.PaymentNumber}", returnUrl, cardToken, ct, savedCard, allowTestMode);
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
