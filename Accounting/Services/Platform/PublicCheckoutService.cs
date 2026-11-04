using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Platform;

/// <summary>แคตตาล็อกหน้าซื้อแพ็กเกจสาธารณะ — ราคาทุกรอบจากแม่แบบ (<see cref="PlatformPlanPricing.CyclePrice"/>) · ป้าย/โหมด/นโยบายจากเซิร์ฟเวอร์</summary>
public sealed record PublicCheckoutCatalog(
    bool Enabled, bool OnlineAvailable, bool IsTestMode, string? Reason,
    IReadOnlyList<PublicCheckoutPlan> Plans, IReadOnlyList<object> Cycles,
    IReadOnlyList<object> MethodOptions, object? Card,
    object Policy, bool RegistrationOpen);

public sealed record PublicCheckoutPlan(string Plan, string Name, string? Description, decimal MonthlyPrice, decimal QuarterlyPrice,
    decimal SemiAnnualPrice, decimal AnnualPrice, int MaxUsers, int MaxDocumentsPerMonth, int MaxOcrPagesPerMonth, long MaxStorageBytes,
    int TrialDurationDays);

/// <summary>ใบเสนอยอดของตะกร้า — <see cref="Source"/> = "list" (ยังไม่ล็อกอิน: ราคาตั้ง) | "company" (มีบริษัท: ราคาพิเศษ+คูปองของบริษัทนั้นผ่าน <c>QuotePlanChangeAsync</c>)</summary>
public sealed record PublicCheckoutQuote(string Plan, string Cycle, string CycleLabel, decimal ListAmount, decimal Discount, decimal AmountDue,
    string? CouponCode, string? CouponNote, string Source, string Note,
    /// <summary>รอบ 233: สิ่งที่จะได้ถ้าจ่าย (แพ็ก/วันหมดอายุเดิม → ใหม่ · เครดิต · เหตุที่ชำระไม่ได้) — <c>Helpers/PlanPurchasePreview</c> ตัวเดียว ·
    /// <c>Preview.Allowed == false</c> ⇒ หน้าเว็บแสดงกล่องแดง + ปิดปุ่มชำระ (ไม่ใช่ error ลอย)</summary>
    PlanPurchasePreview.Result? Preview = null,
    /// <summary>รอบ 234 (คำตัดสิน 234-1): ภาษีหัก ณ ที่จ่ายที่ระบบเสนอ (<c>PlatformWhtSuggestion</c>) — <c>Eligible</c> ⇒ ช่องติ๊ก "หัก ณ ที่จ่าย" (ค่าตั้งต้น = ติ๊ก) · null = ยังไม่รู้บริษัท</summary>
    PlatformWhtSuggestion.Result? Withholding = null,
    /// <summary>รอบ 234 (คำตัดสิน 234-4): ข้อมูลผู้ซื้อสำหรับใบกำกับ — การ์ดในขั้นชำระ (ชนิดผู้ซื้อ · สิ่งที่ขาด · คำเตือน)</summary>
    PlatformPayerProfileGate.Result? PayerProfile = null,
    /// <summary>รอบ 234: ค่าตั้ง "หัก ณ ที่จ่ายทุกครั้ง" ของบริษัท (null = ยังไม่เลือก ⇒ ติ๊กให้เมื่อเข้าเกณฑ์)</summary>
    bool? WithholdTaxPreference = null,
    /// <summary>รอบ 237 (K-1): แบบอัปเกรดของใบเสนอนี้ + ตัวเลือกทุกแบบที่เปิด (ยอด/เครดิต/วันหมดอายุใหม่ต่อแบบ — จาก <c>QuotePlanChangeAsync</c>) · null = ไม่ใช่อัปเกรดกลางรอบ</summary>
    string? UpgradeMode = null,
    IReadOnlyList<UpgradeModeOption>? UpgradeOptions = null);

/// <summary>ผลการสร้าง/ใช้ต่อรายการค่าแพ็กเกจ — <see cref="Resumed"/> = ใช้รายการรอชำระเดิม (ไม่สร้างซ้ำ)</summary>
public sealed record PublicCheckoutOrder(Guid CompanyId, string CompanyName, Guid PaymentId, string PaymentNumber, decimal Amount, string Plan,
    string Cycle, string Status, bool Resumed, string Message, string? CouponCode, decimal CouponDiscountAmount, decimal? ListAmount,
    /// <summary>รอบ 234: ภาษีหัก ณ ที่จ่ายของรายการ (0 = ไม่หัก) — <see cref="Amount"/> = ยอดที่ตัดบัตร/QR (สุทธิ)</summary>
    decimal WithholdingTaxAmount = 0m,
    /// <summary>รอบ 235 (K-8): ชื่อแพ็กเกจที่แสดง · วันหมดอายุใหม่ถ้าชำระวันนี้ (preview — อีเมลยืนยันคำสั่งซื้อ) · null = ไม่รู้ (รายการเดิมที่ใช้ต่อ)</summary>
    string? PlanName = null, DateTime? ValidUntilUtc = null);

/// <summary>สถานะที่หน้าสำเร็จแสดง — ตัดสินโดย <see cref="PublicCheckoutPolicy.Resolve"/> ตัวเดียว</summary>
public sealed record PublicCheckoutOrderStatus(Guid CompanyId, Guid PaymentId, string PaymentNumber, decimal Amount, string Plan, string Cycle,
    string PaymentStatus, string? IntentStatus, bool IsTestMode, string Outcome, string Message, string? ReceiptNumber, bool ReceiptAvailable,
    DateTime? SubscriptionEndDate);

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Checkout-2) — <b>หน้าซื้อแพ็กเกจสาธารณะของ NextAcc</b> (cart → checkout → จ่ายในหน้าเดียวกัน → สำเร็จ) ที่ Omise ต้องการตรวจ
/// <para>ไม่คิดราคา/ไม่สร้าง intent เอง — เรียกของเดิมทั้งสาย: ชนิดคำสั่งซื้อตัดสินก่อนด้วย <see cref="PublicCheckoutPolicy.DecideOrder"/> (ฝ่ายค้าน B1) ⇒
/// ซื้อใหม่/อัปเกรด = <c>ISubscriptionService.ConvertTrialAsync</c> (ยอดจาก <c>RequestPlanPaymentAsync</c>: ราคาพิเศษ → คูปอง) · ต่ออายุแพ็กเกจ+รอบเดิม =
/// <c>ISubscriptionService.SubmitPaymentAsync</c> (ราคาต่ออายุ <c>IsRenewalPurchase</c> · เซิร์ฟเวอร์ตรวจยอดซ้ำ) · ดาวน์เกรด/รอบสั้นลง = ปฏิเสธก่อนมี side-effect
/// → เริ่มชำระ <c>IPlatformPaymentService.StartSubscriptionPaymentAsync</c> เส้นเดียวทั้ง Live/Test (ฝ่ายค้าน S1: Test ส่ง <c>allowTestMode: true</c> — ด่านอื่นเดิมครบ ·
/// intent ประทับ <c>IsTestMode</c> ⇒ <see cref="TestModePaymentPolicy"/> กันผลทางธุรกิจ) → เงินเข้า ⇒ <c>SubscriptionPaymentHandler</c> เปิดสิทธิ์ + ออกใบเสร็จ (เส้นเดิม)</para>
/// <para>ตัวตัดสิน: <see cref="PublicCheckoutPolicy"/> (สวิตช์ · โหมด · resume · ผลลัพธ์) · ทุก query กรอง CompanyId ของผู้ซื้อ (เจ้าของบริษัทเท่านั้น)</para>
/// </summary>
public interface IPublicCheckoutService
{
    Task<PublicCheckoutPolicy.Availability> AvailabilityAsync(CancellationToken ct = default);
    Task<PublicCheckoutCatalog> CatalogAsync(CancellationToken ct = default);
    /// <summary>ยอดของตะกร้า — <paramref name="companyId"/> null = ราคาตั้ง (ยังไม่ล็อกอิน) · มี = ราคาพิเศษ/คูปองของบริษัทนั้น (ตัวเดียวกับตอนยืนยัน)</summary>
    /// <param name="actorUserId">รอบ 236 B1: ผู้ใช้ที่ดูตะกร้า — ผู้ถือ License + แพ็กระดับ License ⇒ "ต่ออายุ License" (preview วันหมดอายุของ License)</param>
    /// <param name="upgradeMode">รอบ 237 (K-1): แบบอัปเกรดกลางรอบที่ผู้ซื้อเลือก (null = ค่าที่ค่าตั้งเลือกไว้ให้)</param>
    Task<PublicCheckoutQuote> QuoteAsync(Guid? companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode, CancellationToken ct = default,
        Guid? actorUserId = null, PlatformUpgradeMode? upgradeMode = null);
    /// <summary>บริษัทที่ผู้ใช้เป็น<b>เจ้าของ</b> (ซื้อแพ็กเกจให้ได้) — ไม่เปิดข้อมูลบริษัทอื่น</summary>
    /// <para>รอบ 233: <c>LowerPlans</c> = แพ็กเกจที่หน้านี้ขายไม่ได้ให้บริษัทนั้นเพราะต่ำกว่าแพ็กปัจจุบัน (ตัดสินที่เซิร์ฟเวอร์ — <c>pricing.html</c> ติดป้ายอย่างเดียว)</para>
    /// <summary>บริษัทที่ผู้ใช้เป็นเจ้าของ · รอบ 236: <c>Label</c> = ป้ายแพ็กเกจที่มีผล (<c>PlanIdentityResolver.Label</c> — License ชนะ · ไม่ใช่ชื่อ enum) ·
    /// <c>PurchaseBlocked</c> = เหตุที่ซื้อเองไม่ได้ (License คุมอยู่) · null = ซื้อได้</summary>
    Task<List<(Guid Id, string Name, string Plan, string Status, IReadOnlyList<string> LowerPlans, string? LowerNote, string Label, string? PurchaseBlocked, string? LicenseRenewalPlan)>> OwnedCompaniesAsync(Guid userId, CancellationToken ct = default);
    /// <summary>สร้าง (หรือใช้ต่อ) รายการค่าแพ็กเกจของบริษัทนี้ — ยอดจากเซิร์ฟเวอร์ · ผู้เรียกบันทึกหลักฐานยอมรับนโยบายเองใน controller (ClaimsPrincipal)</summary>
    Task<PublicCheckoutOrder> CreateOrderAsync(Guid userId, Guid companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode,
        CancellationToken ct = default, bool? withholdTax = null, PlatformUpgradeMode? upgradeMode = null);
    /// <summary>เริ่มชำระออนไลน์ของรายการ — เส้นเดียว platform-pay ทั้ง Live/Test (Test ส่ง <c>allowTestMode</c>) · ยอดจากรายการเท่านั้น</summary>
    Task<PaymentIntent> StartIntentAsync(Guid companyId, Guid paymentId, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default, PlatformCardRefs? savedCard = null, Guid? actorUserId = null);
    Task<PaymentIntent?> RefreshIntentAsync(Guid companyId, Guid intentId, CancellationToken ct = default);
    Task<PublicCheckoutOrderStatus?> OrderStatusAsync(Guid companyId, Guid paymentId, Guid? intentId, CancellationToken ct = default);
    /// <summary>อีเมลยืนยันคำสั่งซื้อ (ส่งไม่ได้ = log · ไม่ล้มคำสั่งซื้อ)</summary>
    Task SendOrderEmailAsync(Guid userId, PublicCheckoutOrder order, string publicOrigin, CancellationToken ct = default);
}

public class PublicCheckoutService : IPublicCheckoutService
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subs;
    private readonly IPlatformPaymentService _platform;
    private readonly ILegalDocumentService _legal;
    private readonly IEmailService _email;
    private readonly ILogger<PublicCheckoutService> _logger;

    public PublicCheckoutService(AccountingDbContext db, ISubscriptionService subs, IPlatformPaymentService platform,
        ILegalDocumentService legal, IEmailService email, ILogger<PublicCheckoutService> logger)
    { _db = db; _subs = subs; _platform = platform; _legal = legal; _email = email; _logger = logger; }

    public async Task<PublicCheckoutPolicy.Availability> AvailabilityAsync(CancellationToken ct = default)
    {
        var enabled = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => (bool?)s.PublicCheckoutEnabled).FirstOrDefaultAsync(ct);
        var cfg = await _platform.PlatformConfigAsync(ct);
        var status = await _platform.StatusAsync(ct);   // Methods/Card มาแม้ช่องทางยังไม่ "Online" (โหมดทดสอบ)
        return PublicCheckoutPolicy.Decide(enabled, cfg != null, cfg?.IsActive ?? false, cfg?.Mode, status.Methods.Count,
            cfg?.PlatformLiveConfirmedAt != null);
    }

    public async Task<PublicCheckoutCatalog> CatalogAsync(CancellationToken ct = default)
    {
        var avail = await AvailabilityAsync(ct);
        var status = await _platform.StatusAsync(ct);
        var templates = await _db.PlanTemplates.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.MonthlyPrice).ToListAsync(ct);
        var plans = templates
            .Where(t => PublicCheckoutPolicy.IsSellable(t.IsActive, t.IsPermanentFree, t.MonthlyPrice))
            .Select(t => new PublicCheckoutPlan(t.Plan.ToString(), t.Name, t.Description, t.MonthlyPrice, t.QuarterlyPrice, t.SemiAnnualPrice, t.AnnualPrice,
                t.MaxUsers, t.MaxDocumentsPerMonth, t.MaxOcrPagesPerMonth, t.MaxStorageBytes, t.TrialDurationDays))
            .ToList();
        var cycles = Enum.GetValues<BillingCycle>()
            .Select(c => (object)new { code = c.ToString(), months = PlatformPlanPricing.MonthsOf(c), label = PublicCheckoutPolicy.CycleLabel(c) })
            .ToList();
        var policy = PurchasePolicyAcceptance.PlatformInfo((await _legal.GetPurchasePolicyVersionAsync(ct)).Version);
        var registrationEnabled = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => (bool?)s.RegistrationEnabled).FirstOrDefaultAsync(ct);
        return new PublicCheckoutCatalog(avail.Enabled, avail.OnlineAvailable, avail.IsTestMode, avail.Reason, plans, cycles,
            avail.OnlineAvailable
                ? PlatformPayMethods.Options(status.Methods)
                : new List<object>(),
            avail.OnlineAvailable && status.Card is { } c ? new { providerCode = c.ProviderCode, scriptUrl = c.ScriptUrl, publicKey = c.PublicKey, isTestMode = c.IsTestMode } : null,
            new
            {
                text = policy.Text, version = policy.Version, termsUrl = policy.TermsUrl, refundUrl = policy.RefundUrl,
                privacyUrl = LegalDocumentDefaults.PlatformPath(LegalDocumentDefaults.KindPrivacy),
            },
            RegistrationPolicy.IsOpen(registrationEnabled));
    }

    public async Task<PublicCheckoutQuote> QuoteAsync(Guid? companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode, CancellationToken ct = default,
        Guid? actorUserId = null, PlatformUpgradeMode? upgradeMode = null)
    {
        var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == plan && t.IsActive, ct)
            ?? throw new BusinessRuleException("ไม่พบแพ็กเกจนี้", PublicCheckoutPolicy.RuleCode, 404);
        if (!PublicCheckoutPolicy.IsSellable(template.IsActive, template.IsPermanentFree, template.MonthlyPrice))
            throw new BusinessRuleException("แพ็กเกจนี้ไม่มีค่าใช้จ่าย — สมัครใช้งานได้ที่หน้าสมัครสมาชิก", PublicCheckoutPolicy.RuleCode);
        if (PlatformPlanPricing.MonthsOf(cycle) <= 0) throw new BusinessRuleException("รอบบิลไม่ถูกต้อง", PublicCheckoutPolicy.RuleCode);
        var code = PlatformCouponRule.Normalize(couponCode);
        if (companyId is Guid cid)
        {
            // ฝ่ายค้าน B1: ชนิดคำสั่งซื้อตัวเดียวกับตอนยืนยัน (DecideOrderAsync) — ดาวน์เกรด/รอบสั้นลงบอกเหตุตั้งแต่ตะกร้า · ต่ออายุแสดงราคาต่ออายุ (ไม่ใช่ 0)
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == cid, ct)
                ?? throw new BusinessRuleException("บริษัทนี้ยังไม่มีข้อมูลแพ็กเกจ — เข้าสู่ระบบแล้วเริ่มทดลองใช้ก่อน", PublicCheckoutPolicy.RuleCode);
            // รอบ 236 (คำตัดสิน 236-2): License คุมอยู่ ⇒ "ชำระไม่ได้" ตั้งแต่ตะกร้า — แพ็กเกจปัจจุบัน = แพ็กเกจของ License (ไม่ใช่แถวบริษัท Free Edition)
            var identity = await PlanIdentityReader.ForSubscriptionAsync(_db, sub, ct);
            var licenseVerdict = PlanIdentityReader.Verdict(identity, actorUserId, plan);   // B1: ผู้ถือ + แพ็กระดับ License = ต่ออายุ License
            // ฝ่ายค้าน B1: ยอมแบบต่ออายุ License ⇒ ยอดจากเส้นเดิม แต่ preview/หมายเหตุ = "ต่ออายุ License" + วันหมดอายุใหม่ของ License
            async Task<PublicCheckoutQuote> AsLicenseRenewal(PublicCheckoutQuote r) => !licenseVerdict.LicenseRenewal ? r : r with
            {
                Preview = PlanPurchasePreview.ForLicenseRenewal(identity, PlanPurchasePreview.TargetOf(template, cycle),
                    new PlanPurchasePreview.Money(r.ListAmount, r.Discount, r.AmountDue, CouponLabel: r.CouponNote), DateTime.UtcNow,
                    await PlatformWithholdingReader.PlatformIssuesTaxInvoiceAsync(_db, ct)),
                Note = PlanIdentityResolver.LicenseRenewalHeadline,
            };
            if (!licenseVerdict.Allowed)
            {
                var byLicense = PlanPurchasePreview.RefusedByLicense(identity, PlanPurchasePreview.TargetOf(template, cycle), licenseVerdict, DateTime.UtcNow);
                return new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), 0m, 0m, 0m,
                    code, null, "company", byLicense.RefusalReason ?? "", byLicense);
            }
            var (decision, q, renewalAmount) = await DecideOrderAsync(sub, template, cycle, code, ct, actorUserId, upgradeMode);
            // รอบ 233 (คำตัดสิน 233-3 · เจ้าของ: "ถ้าเลือกแพ็คต่ำกว่าต้องขึ้นแจ้งว่าชำระไม่ได้เพราะอะไร"): ปฏิเสธ ⇒ คืนใบเสนอที่ Preview.Allowed=false
            // (เหตุ + วันที่ + ทางไปต่อ จาก PlanPurchasePreview.Refused ตัวเดียว) — หน้าเว็บแสดงในกล่องสรุปและใต้ปุ่ม · ตอนสร้างรายการยัง throw ข้อความเดียวกัน
            if (decision.Kind == PublicCheckoutPolicy.OrderKind.Refused)
            {
                var refused = await RefusalPreviewAsync(sub, template, cycle, decision.Refusal, ct);
                return new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), 0m, 0m, 0m,
                    code, null, "company", refused.RefusalReason ?? "", refused);
            }
            if (decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal)
            {
                // รอบ 235 (K-2 · คำตัดสิน 235-1): คูปองตอนต่ออายุ — ตัวตัดสินเดียวกับตอนจอง (PlatformCouponStore → PlatformCouponRule · isRenewal) ·
                // ใช้ไม่ได้ (เช่น ตั้ง "ซื้อใหม่เท่านั้น") = บอก ไม่เงียบ (เหมือนเส้นเปลี่ยน)
                // ฝ่ายค้าน S-2 (235-1b): ต่ออายุที่ได้ราคาพิเศษอยู่แล้ว ⇒ คูปองไม่ลดซ้อน — ตัวตัดสินเดียวกับตอนจอง (ราคาพิเศษ forRenewal ตัวเดียวกับ RequestPlanPaymentAsync)
                var renewalSpecial = code != null
                    && (await PlatformPriceOverrideStore.ResolveForSubscriptionAsync(_db, sub, template, cycle, DateTime.UtcNow, forRenewal: true)).Applied;
                var ev = await Accounting.Services.Implementations.PlatformCouponStore.PreviewAsync(_db, cid, code, PlatformCouponScope.Subscription, plan.ToString(), cycle.ToString(), null,
                    renewalAmount, DateTime.UtcNow, ct, isRenewal: true, renewalSpecialPrice: renewalSpecial);
                if (ev is { Ok: false }) throw new BusinessRuleException(ev.Problem ?? PlatformCouponRule.InvalidCodeMessage, PublicCheckoutPolicy.RuleCode);
                var discount = ev is { Ok: true } ? ev.Discount : 0m;
                var due = ev is { Ok: true } ? ev.AmountAfter : renewalAmount;
                var renewalPreview = await RenewalPreviewAsync(sub, template, cycle,
                    new PlanPurchasePreview.Money(renewalAmount, discount, due, CouponLabel: ev is { Ok: true } ? ev.Label : null), ct);
                // รอบ 234: ภาษีหักที่ระบบเสนอ (ตัวตัดสินเดียว) + ข้อมูลผู้ซื้อ — ฐาน = ยอดหลังคูปอง (ใบลดก่อน VAT) · มูลค่าสัญญา = ราคาต่อรอบ (234-2)
                return await AsLicenseRenewal(new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), renewalAmount, discount, due,
                    ev is { Ok: true } ? code : null, ev is { Ok: true } ? ev.Label : null, "company", renewalPreview.DateLine, renewalPreview,
                    await PlatformWithholdingReader.ForReceivedAsync(_db, cid, due, true,
                        PlatformWhtSuggestion.AnnualContractGross(renewalAmount, PlatformPlanPricing.MonthsOf(cycle)), ct),
                    await PlatformWithholdingReader.PayerAsync(_db, cid, ct), sub.WithholdTaxPreference));
            }
            // ตัวตัดสินเดียวกับตอนยืนยัน (QuotePlanChangeAsync → ราคาพิเศษของบริษัท + คูปอง) — ไม่คิดซ้ำที่นี่
            if (q!.CouponProblem != null) throw new BusinessRuleException(q.CouponProblem, PublicCheckoutPolicy.RuleCode);   // คูปองใช้ไม่ได้ = บอก ไม่เงียบ
            // รอบ 237 (K-1): แบบอัปเกรดที่เลือกใช้ไม่ได้ (ไม่เปิด / เครดิตครอบยอด) ⇒ ใบเสนอพร้อมตัวเลือกอื่น (preview Allowed=false + เหตุ) ไม่ใช่ error ลอย
            if (q.Blocked != null && q.UpgradeOptions == null) throw new BusinessRuleException(q.Blocked, PublicCheckoutPolicy.RuleCode);
            // รอบ 234: q.Withholding/q.PayerProfile มาจาก QuotePlanChangeAsync (ตัวตัดสินเดียวกับตอนสร้างรายการ)
            return await AsLicenseRenewal(new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), q.ListAmount, q.Discount, q.AmountDue,
                code, q.CouponLabel ?? q.SpecialPriceNote, "company", q.Preview?.DateLine ?? q.Message, q.Preview,
                q.Withholding, q.PayerProfile, sub.WithholdTaxPreference, q.UpgradeMode, q.UpgradeOptions));
        }
        var list = PlatformPlanPricing.AmountDue(template, cycle);
        var anon = PlanPurchasePreview.ForAnonymous(PlanPurchasePreview.TargetOf(template, cycle), list, DateTime.UtcNow);
        return new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), list, 0m, list, code,
            code == null ? null : "คูปองจะถูกตรวจและหักให้หลังเข้าสู่ระบบ/สร้างบริษัท (ยอดสุดท้ายแสดงก่อนชำระ)", "list",
            $"ชำระ {template.Name} {PlatformPlanPricing.MonthsOf(cycle)} เดือน — เริ่มรอบจากวันที่ชำระ", anon);
    }

    public async Task<List<(Guid Id, string Name, string Plan, string Status, IReadOnlyList<string> LowerPlans, string? LowerNote, string Label, string? PurchaseBlocked, string? LicenseRenewalPlan)>> OwnedCompaniesAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.UserId == userId && cu.Role == UserRole.Owner && !cu.Company.IsDeleted)
            .Select(cu => new { cu.CompanyId, cu.Company.Name })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.CompanyId).ToList();
        var subs = await _db.Subscriptions.AsNoTracking().Where(x => ids.Contains(x.CompanyId) && !x.IsDeleted).ToListAsync(ct);
        var templates = await _db.PlanTemplates.AsNoTracking().Where(t => t.IsActive)
            .Select(t => new { t.Plan, t.Name, t.MonthlyPrice, t.IsActive, t.IsPermanentFree }).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var result = new List<(Guid Id, string Name, string Plan, string Status, IReadOnlyList<string> LowerPlans, string? LowerNote, string Label, string? PurchaseBlocked, string? LicenseRenewalPlan)>();
        foreach (var r in rows)
        {
            var s = subs.FirstOrDefault(x => x.CompanyId == r.CompanyId);
            // รอบ 236 (236-1/236-5): ตัวตนแพ็กเกจที่มีผล (License ชนะ) + ป้ายตัวเดียว — เดิมพิมพ์ชื่อ enum ของแถวบริษัท ("FreeTrial · Active") ทั้งที่บริษัทใช้ Enterprise ผ่าน License
            var identity = s == null ? null : await PlanIdentityReader.ForSubscriptionAsync(_db, s, ct);
            var verdict = identity == null ? PlanIdentityResolver.PurchaseVerdict.Ok : PlanIdentityReader.Verdict(identity, userId, null);
            // รอบ 233 (233-3): เงื่อนไขเดียวกับ PublicCheckoutPolicy.DecideOrder (Active จ่ายเงิน + ราคาต่อเดือนต่ำกว่า = ปฏิเสธ) — ไม่ใช่สำเนากติกาในหน้าเว็บ ·
            // รอบ 236: License คุมอยู่ ⇒ ไม่มีป้าย "ต่ำกว่า" (ซื้อแพ็กใด ๆ ไม่ได้อยู่แล้ว — เหตุอยู่ใน PurchaseBlocked)
            IReadOnlyList<string> lower = Array.Empty<string>();
            string? note = null;
            if (verdict.Allowed && s != null && s.Status == SubscriptionStatus.Active && !s.IsPermanentFree
                && templates.FirstOrDefault(t => t.Plan == s.Plan) is { } curTpl)
                lower = templates.Where(t => PublicCheckoutPolicy.IsSellable(t.IsActive, t.IsPermanentFree, t.MonthlyPrice)
                        && PlatformPlanPricing.Classify(curTpl.MonthlyPrice, t.MonthlyPrice) == PlatformPlanPricing.ChangeKind.Downgrade)
                    .Select(t => t.Plan.ToString()).ToList();
            if (lower.Count > 0 && s != null && templates.FirstOrDefault(t => t.Plan == s.Plan) is { } cur2)
                note = PlanPurchasePreview.LowerPlanNote(r.Name, cur2.Name, s.IsPermanentFree, s.EndDate, now);   // ฝ่ายค้าน N5
            result.Add((r.CompanyId, r.Name, identity?.Plan.ToString() ?? "-", identity?.Status.ToString() ?? "-", lower, note,
                identity == null ? "ยังไม่มีแพ็กเกจ" : PlanIdentityResolver.Label(identity, s?.IsPermanentFree == true),
                verdict.Allowed ? null : PlanIdentityReader.RefusalMessage(verdict),
                verdict.HolderMayRenew && identity != null ? identity.Plan.ToString() : null));
        }
        return result;
    }

    private async Task RequireOwnerAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        var owner = await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.UserId == userId && cu.CompanyId == companyId && cu.Role == UserRole.Owner, ct);
        if (!owner) throw new BusinessRuleException("ซื้อแพ็กเกจได้เฉพาะเจ้าของบริษัท", PublicCheckoutPolicy.RuleCode, 403);
    }

    /// <summary>รอบ 233: สถานะปัจจุบันของบริษัทในรูปที่ <see cref="PlanPurchasePreview"/> ใช้ (แม่แบบของแพ็กเกจปัจจุบัน — ชื่อ/ราคาต่อเดือน)</summary>
    private async Task<PlanPurchasePreview.Current> CurrentForPreviewAsync(Subscription sub, CancellationToken ct)
        => PlanPurchasePreview.CurrentOf(sub, await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == sub.Plan && t.IsActive, ct));

    /// <summary>รอบ 233 (233-3): เหตุที่ชำระไม่ได้ + วันที่ + ทางไปต่อ — ตัวเดียวของยอดตะกร้าและตอนสร้างรายการ</summary>
    private async Task<PlanPurchasePreview.Result> RefusalPreviewAsync(Subscription sub, PlanTemplate template, BillingCycle cycle, string? refusal, CancellationToken ct)
        => PlanPurchasePreview.Refused(await CurrentForPreviewAsync(sub, ct), PlanPurchasePreview.TargetOf(template, cycle), refusal, DateTime.UtcNow);

    /// <summary>รอบ 233 (233-1): ต่ออายุแพ็กเกจ+รอบเดิม — วันหมดอายุใหม่ = max(วันหมดอายุเดิม, วันชำระ) + รอบ (PlanPurchasePreview.PaidPeriod ตัวเดียวกับตอนอนุมัติ)</summary>
    private async Task<PlanPurchasePreview.Result> RenewalPreviewAsync(Subscription sub, PlanTemplate template, BillingCycle cycle, PlanPurchasePreview.Money money, CancellationToken ct)
    {
        // ฝ่ายค้าน N3: ธง VAT ตัวตัดสินเดียวกับตัวออกใบ
        var vs = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => new { s.PlatformIsVatRegistered, s.PlatformSellerTaxId }).FirstOrDefaultAsync(ct);
        var vat = vs != null && PlanPurchasePreview.IssuesTaxInvoice(vs.PlatformIsVatRegistered, vs.PlatformSellerTaxId);
        return PlanPurchasePreview.ForPurchase(await CurrentForPreviewAsync(sub, ct), PlanPurchasePreview.TargetOf(template, cycle),
            money, DateTime.UtcNow, vat);
    }

    /// <summary>รอบ 236 (B1): วันหมดอายุใหม่ของ License ถ้าชำระวันนี้ (preview ต่ออายุ License ตัวเดียว)</summary>
    private async Task<DateTime?> LicenseValidUntilAsync(Subscription sub, PlanTemplate template, BillingCycle cycle, CancellationToken ct)
        => PlanPurchasePreview.ForLicenseRenewal(await PlanIdentityReader.ForSubscriptionAsync(_db, sub, ct), PlanPurchasePreview.TargetOf(template, cycle),
            new PlanPurchasePreview.Money(0m, 0m, 0m), DateTime.UtcNow, false).NewEndUtc;

    /// <summary>รอบ 235 (K-8): วันหมดอายุใหม่ถ้าชำระวันนี้ ของการต่ออายุ — preview ตัวเดียว (PaidPeriod · ไม่คำนวณเอง)</summary>
    private async Task<DateTime?> ValidUntilIfPaidTodayAsync(Subscription sub, PlanTemplate template, BillingCycle cycle, CancellationToken ct)
        => (await RenewalPreviewAsync(sub, template, cycle, new PlanPurchasePreview.Money(0m, 0m, 0m), ct)).NewEndUtc;

    /// <summary>ฝ่ายค้าน B1: ชนิดคำสั่งซื้อ — <b>อ่านอย่างเดียว</b> (ไม่มี side-effect) · ตัวเดียวของยอดตะกร้า (QuoteAsync) และตอนยืนยัน (CreateOrderAsync) ·
    /// คืนใบเสนอ <c>QuotePlanChangeAsync</c> (เส้นซื้อใหม่/เปลี่ยน) หรือยอดต่ออายุ (<c>PlatformPriceOverrideStore.AmountDueAsync</c> + <c>currentPlan</c> ⇒ ราคาต่ออายุ ·
    /// ตัวเดียวกับที่ <c>SubmitPaymentAsync</c>/เริ่มชำระออนไลน์/handler ตรวจ)</summary>
    private async Task<(PublicCheckoutPolicy.OrderDecision Decision, PlanChangeQuote? Quote, decimal RenewalAmount)> DecideOrderAsync(
        Subscription sub, PlanTemplate template, BillingCycle cycle, string? code, CancellationToken ct, Guid? actorUserId = null, PlatformUpgradeMode? upgradeMode = null)
    {
        var activePaid = sub.Status == SubscriptionStatus.Active && !sub.IsPermanentFree;
        var current = activePaid
            ? await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == sub.Plan && t.IsActive, ct)
            : null;
        var renewalShape = activePaid && PlatformPlanPricing.IsRenewalPurchase(sub.Plan, template.Plan) && cycle == sub.BillingCycle;
        // ใบเสนอของเส้นเปลี่ยน/ซื้อใหม่ (อ่านอย่างเดียว) — ต่ออายุไม่ใช้ (QuotePlanChangeAsync ตอบ "แพ็กเกจปัจจุบันอยู่แล้ว" ยอด 0)
        var q = renewalShape ? null : await _subs.QuotePlanChangeAsync(sub.CompanyId, template.Plan, cycle, code, actorUserId, upgradeMode);   // รอบ 236 B1: ผู้ถือ License · รอบ 237: แบบอัปเกรด
        // รอบ 237 (K-1 · ฝ่ายค้าน S4): ใบเสนออัปเกรดที่ "แบบที่เลือก" ใช้ไม่ได้ (ไม่เปิด / เครดิตครอบยอด) ส่งเป็นธงตรง ๆ — ไม่แต่งยอดขึ้นเอง ·
        // ใบเสนอเองบอกเหตุ (preview Allowed=false) · สร้างรายการถูกด่านเดียวกันปฏิเสธ (ChangeSubscriptionAsync)
        var decision = PublicCheckoutPolicy.DecideOrder(sub.Status, sub.IsPermanentFree, sub.Plan, sub.BillingCycle, current?.MonthlyPrice ?? 0m,
            template.Plan, cycle, template.MonthlyPrice, q?.ListAmount ?? 0m,
            renewalInvoiceIssued: PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(sub.RenewalInvoiceForEndDate, sub.EndDate) != null,
            upgradeBlocked: q is { UpgradeMode: not null, Blocked: not null });
        var renewalAmount = decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal
            ? await PlatformPriceOverrideStore.AmountDueAsync(_db, sub.CompanyId, sub.AccountSubscriptionId, template, cycle, DateTime.UtcNow,
                currentPlan: sub.Plan, ct: ct)
            : 0m;
        if (decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal && renewalAmount <= 0m)
            return (new PublicCheckoutPolicy.OrderDecision(PublicCheckoutPolicy.OrderKind.Refused, "แพ็กเกจนี้ไม่มีค่าบริการต่ออายุตามตารางราคา — ติดต่อผู้ดูแลระบบ NextAcc"), q, 0m);
        return (decision, q, renewalAmount);
    }

    public async Task<PublicCheckoutOrder> CreateOrderAsync(Guid userId, Guid companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode,
        CancellationToken ct = default, bool? withholdTax = null, PlatformUpgradeMode? upgradeMode = null)
    {
        var avail = await AvailabilityAsync(ct);
        if (!avail.Enabled) throw new BusinessRuleException(avail.Reason ?? PublicCheckoutPolicy.DisabledMessage, PublicCheckoutPolicy.RuleCode, 404);
        await RequireOwnerAsync(userId, companyId, ct);
        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("บริษัทนี้ยังไม่มีข้อมูลแพ็กเกจ — เข้าสู่ระบบแล้วเริ่มทดลองใช้ก่อน", PublicCheckoutPolicy.RuleCode);
        // รอบ 236 (คำตัดสิน 236-2): ด่านเงิน — License คุมอยู่ ⇒ ไม่สร้าง/ไม่ resume รายการค่าแพ็กเกจระดับบริษัท (ข้อความเดียวกับกล่องสรุป)
        var purchaseVerdict = await PlanIdentityReader.EnsureCompanyMayPurchaseAsync(_db, companyId, userId, plan, ct);   // B1: ผู้ถือ + แพ็กระดับ License = ต่ออายุ License
        var code = PlatformCouponRule.Normalize(couponCode);

        // ไม่สร้างซ้ำ: รายการรอชำระของแพ็กเกจ/รอบเดียวกันที่ยังไม่แนบสลิป/ไม่ถูกพัก ⇒ ใช้ต่อ (ConvertTrialAsync ก็ reuse เมื่อยอดตรง — ด่านนี้กันก่อนให้ชัด)
        var pending = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == sub.Id && p.Subscription.CompanyId == companyId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.PaymentNumber, p.Amount, p.WithholdingTaxAmount, p.Status, p.RequestedPlan, p.RequestedBillingCycle, p.PlatformInvoiceDocumentId, p.SlipFileName,
                p.AttentionReason, p.CouponCode, p.CouponDiscountAmount, p.ListAmount, p.IsProration, p.IsCycleChange, p.UpgradeMode })
            .FirstOrDefaultAsync(p => p.Status == SubscriptionPaymentStatus.Pending, ct);
        var resumable = pending != null && PublicCheckoutPolicy.CanResume(pending.Status, pending.RequestedPlan, pending.RequestedBillingCycle, plan, cycle,
                pending.PlatformInvoiceDocumentId != null, pending.SlipFileName != null, pending.AttentionReason != null)
            && (code == null || string.Equals(code, pending.CouponCode, StringComparison.OrdinalIgnoreCase));
        if (resumable && pending != null && (pending.IsProration || pending.IsCycleChange))
        {
            // รอบ 233 ฝ่ายค้าน S5 (K-6): ส่วนต่าง/เปลี่ยนรอบเก่าคิดวันที่เหลือ ณ วันสร้าง ⇒ คิดใหม่วันนี้ · ยอดไม่ตรง (และยังไม่เริ่มชำระออนไลน์) ⇒ ไม่ resume
            var fresh = await _subs.QuotePlanChangeAsync(companyId, plan, cycle, code, userId, upgradeMode);
            var hasIntent = await _db.PaymentIntents.AsNoTracking()
                .AnyAsync(i => i.SourceKind == PaymentSourceKind.SubscriptionPayment && i.SourceId == pending.Id, ct);
            if (PublicCheckoutPolicy.MustRequote(true, pending.ListAmount ?? PlatformWhtSuggestion.GrossOf(pending.Amount, pending.WithholdingTaxAmount), fresh.ListAmount, hasIntent)) resumable = false;
            // รอบ 237 (K-1): รายการเดิมเป็นอีกแบบอัปเกรด (แถวเก่า null = คงวันหมดอายุ) ⇒ ไม่ใช้ต่อ (ยอด/วันหมดอายุคนละชุด) — สร้างใหม่ผ่านเส้นเดิมที่ยกเลิกใบเก่า
            if (fresh.UpgradeMode != null && (pending.UpgradeMode ?? PlatformUpgradeMode.KeepExpiry).ToString() != fresh.UpgradeMode) resumable = false;
        }
        // รอบ 234 (คำตัดสิน 234-1): ผู้เช่าเปลี่ยนตัวเลือก "หัก ณ ที่จ่าย" จากรายการเดิม ⇒ ไม่ใช้ต่อตรง ๆ (ส่งเข้าเส้นสร้างรายการ — RequestPlanPaymentAsync คิดยอดหัก/สุทธิใหม่บนรายการเดิม)
        if (resumable && pending != null && withholdTax is bool wantWht)
        {
            var d = await PlatformWithholdingReader.ForReceivedAsync(_db, companyId, PlatformWhtSuggestion.GrossOf(pending.Amount, pending.WithholdingTaxAmount), wantWht, null, ct);
            if (d.WithholdingAmount != pending.WithholdingTaxAmount) resumable = false;
        }
        if (resumable && pending != null)
        {
            return new PublicCheckoutOrder(companyId, company, pending.Id, pending.PaymentNumber, pending.Amount, plan.ToString(), cycle.ToString(),
                pending.Status.ToString(), true, $"ใช้รายการรอชำระเดิม {pending.PaymentNumber} ยอด {pending.Amount:N2} บาท", pending.CouponCode,
                pending.CouponDiscountAmount, pending.ListAmount, pending.WithholdingTaxAmount);
        }

        // ฝ่ายค้าน B1: ตัดสินชนิดคำสั่งซื้อ<b>ก่อน</b>เรียก service ที่มี side-effect — ดาวน์เกรด/รอบสั้นลงไม่ถึง ChangeSubscriptionAsync (ที่บันทึก ScheduledPlan ก่อน)
        var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == plan && t.IsActive, ct)
            ?? throw new BusinessRuleException("ไม่พบแพ็กเกจนี้", PublicCheckoutPolicy.RuleCode, 404);
        if (!PublicCheckoutPolicy.IsSellable(template.IsActive, template.IsPermanentFree, template.MonthlyPrice))
            throw new BusinessRuleException("แพ็กเกจนี้ไม่มีค่าใช้จ่าย — สมัครใช้งานได้ที่หน้าสมัครสมาชิก", PublicCheckoutPolicy.RuleCode);
        var (decision, _, renewalAmount) = await DecideOrderAsync(sub, template, cycle, code, ct, userId, upgradeMode);
        if (decision.Kind == PublicCheckoutPolicy.OrderKind.Refused)   // รอบ 233: ข้อความเดียวกับกล่องสรุป (เหตุ + วันที่ + ทางไปต่อ)
            throw new BusinessRuleException((await RefusalPreviewAsync(sub, template, cycle, decision.Refusal, ct)).RefusalReason
                ?? decision.Refusal ?? PublicCheckoutPolicy.DowngradeRefusal, PublicCheckoutPolicy.RuleCode);

        if (decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal)
        {
            // รอบ 235 (K-2 · คำตัดสิน 235-1/235-9): ต่ออายุแพ็กเกจ+รอบเดิม — เส้นสร้างรายการเดียวกับซื้อใหม่ (RequestPlanPaymentAsync ผ่าน RequestRenewalPaymentAsync:
            // ราคาต่ออายุจากเซิร์ฟเวอร์ · คูปองจองใต้ล็อก · ภาษีหักจากตัวตัดสินเดียว) — เดิมใช้ SubmitPaymentAsync (ไม่รับคูปอง + แจ้ง "รอตรวจ" ทั้งที่ยังไม่จ่าย)
            var renewed = await _subs.RequestRenewalPaymentAsync(companyId, plan, cycle, code, withholdTax, userId.ToString());
            return new PublicCheckoutOrder(companyId, company, renewed.Id, renewed.PaymentNumber, renewed.Amount, plan.ToString(), cycle.ToString(),
                renewed.Status.ToString(), false, $"สร้างรายการต่ออายุ {renewed.PaymentNumber} ยอด {renewed.Amount:N2} บาทแล้ว — ต่อจากวันหมดอายุเดิมเมื่อชำระเงิน",
                renewed.CouponCode, renewed.CouponDiscountAmount, renewed.ListAmount, renewed.WithholdingTaxAmount,
                template.Name, purchaseVerdict.LicenseRenewal ? await LicenseValidUntilAsync(sub, template, cycle, ct) : await ValidUntilIfPaidTodayAsync(sub, template, cycle, ct));
        }

        // ซื้อใหม่ (ทดลองใช้/หมดอายุ/ฟรีถาวร) หรืออัปเกรด/รอบยาวขึ้น (Active) — ConvertTrialAsync → RequestPlanPaymentAsync ตัวเดียว (ยอดจากเซิร์ฟเวอร์)
        var result = await _subs.ConvertTrialAsync(companyId, new ConvertTrialRequest(plan, cycle, PromoCode: code, WithholdTax: withholdTax, UpgradeMode: upgradeMode), userId.ToString());
        if (result.Payment == null)
            throw new BusinessRuleException(result.Message, PublicCheckoutPolicy.RuleCode);
        var pay = result.Payment;
        return new PublicCheckoutOrder(companyId, company, pay.Id, pay.PaymentNumber, pay.Amount, plan.ToString(), cycle.ToString(), pay.Status.ToString(), false,
            result.Message, pay.CouponCode, pay.CouponDiscountAmount, pay.ListAmount, pay.WithholdingTaxAmount,
            template.Name, (await _subs.QuotePlanChangeAsync(companyId, plan, cycle, null, userId, upgradeMode)).Preview?.NewEndUtc);   // รอบ 235 K-8 · รอบ 236 B1: ต่ออายุ License ⇒ วันหมดอายุของ License: วันที่ "ถ้าชำระวันนี้" จาก preview ตัวเดียว
    }

    public async Task<PaymentIntent> StartIntentAsync(Guid companyId, Guid paymentId, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default, PlatformCardRefs? savedCard = null, Guid? actorUserId = null)
    {
        var avail = await AvailabilityAsync(ct);
        if (!avail.Enabled) throw new BusinessRuleException(avail.Reason ?? PublicCheckoutPolicy.DisabledMessage, PublicCheckoutPolicy.RuleCode, 404);
        if (!avail.OnlineAvailable) throw new BusinessRuleException(avail.Reason ?? "ชำระออนไลน์ไม่ได้ในขณะนี้", PublicCheckoutPolicy.RuleCode);
        // ฝ่ายค้าน S1: เส้นเดียวทั้ง Live/Test — ด่านเดิมครบ (รายการรอชำระของบริษัทนี้ · ไม่ซ้อนสลิป/พัก · ยอดตรงราคาปัจจุบัน S3 · วิธีที่เปิด · PlatformDoublePayment) ·
        // ต่างกันเฉพาะโหมดทดสอบผ่านด่านช่องทางได้เมื่อหน้านี้ตัดสินว่าเป็นโหมดทดสอบ (intent ประทับ IsTestMode ⇒ TestModePaymentPolicy กันผลทางธุรกิจ)
        // รอบ 235 (ข้อ 10): บัตรที่เพิ่งบันทึก (เส้นเดียวกับ platform-pay — PlatformSavedCardStart) ⇒ ตัดด้วยบัตรนั้น
        return await _platform.StartSubscriptionPaymentAsync(companyId, paymentId, method, returnUrl, cardToken, ct, savedCard, allowTestMode: avail.IsTestMode,
            actorUserId: actorUserId);   // รอบ 236 B1: ผู้ถือ License ต่ออายุ License ได้
    }

    public Task<PaymentIntent?> RefreshIntentAsync(Guid companyId, Guid intentId, CancellationToken ct = default)
        => _platform.RefreshForPayerAsync(companyId, intentId, ct);   // intent ใต้บริษัทผู้ให้บริการ + PayerCompanyId ต้องเป็นบริษัทนี้ (ทั้ง Live/Test)

    public async Task<PublicCheckoutOrderStatus?> OrderStatusAsync(Guid companyId, Guid paymentId, Guid? intentId, CancellationToken ct = default)
    {
        var pay = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.Id == paymentId && p.Subscription.CompanyId == companyId)
            .Select(p => new { p.Id, p.PaymentNumber, p.Amount, p.Status, p.RequestedPlan, p.RequestedBillingCycle, p.Subscription.EndDate })
            .FirstOrDefaultAsync(ct);
        if (pay == null) return null;
        PaymentIntent? intent = intentId is Guid iid ? await _platform.RefreshForPayerAsync(companyId, iid, ct) : null;
        if (intent == null && (await _platform.PlatformCompanyIdAsync(ct)) is Guid pid)
        {
            // ไม่ส่ง intent มา (เปิดหน้าสำเร็จใหม่) ⇒ intent ล่าสุดของรายการนี้ที่ผู้จ่ายเป็นบริษัทนี้
            var latest = await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.CompanyId == pid && i.PayerCompanyId == companyId && i.SourceKind == PaymentSourceKind.SubscriptionPayment && i.SourceId == paymentId)
                .OrderByDescending(i => i.CreatedAt).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
            if (latest is Guid lid) intent = await _platform.RefreshForPayerAsync(companyId, lid, ct);
        }
        var isTest = intent != null && PaymentIntentTestMode.Decide(intent.IsTestMode, null);
        var outcome = PublicCheckoutPolicy.Resolve(pay.Status, intent?.Status, isTest);
        var resp = await _subs.GetPaymentAsync(paymentId);   // ReceiptNumber จากตัวออกใบเสร็จของแพลตฟอร์ม (เส้นเดิม)
        return new PublicCheckoutOrderStatus(companyId, pay.Id, pay.PaymentNumber, pay.Amount, pay.RequestedPlan.ToString(), pay.RequestedBillingCycle.ToString(),
            pay.Status.ToString(), intent?.Status.ToString(), isTest, outcome.ToString(), PublicCheckoutPolicy.OutcomeMessage(outcome),
            resp.ReceiptNumber, resp.ReceiptNumber != null, outcome == PublicCheckoutPolicy.Outcome.Activated ? resp.SubscriptionExtendedTo ?? pay.EndDate : null);
    }

    public async Task SendOrderEmailAsync(Guid userId, PublicCheckoutOrder order, string publicOrigin, CancellationToken ct = default)
    {
        var to = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(to)) return;
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var link = $"{publicOrigin}/checkout-success.html?company={order.CompanyId}&payment={order.PaymentId}";
        // รอบ 235 (K-8 · คำตัดสิน 235-4): แพ็ก · รอบ · ใช้ได้ถึง (พ.ศ.) · ยอด · หัก ณ ที่จ่าย — ข้อความจากตัวเดียว PlatformPurchaseEmailText (HtmlEncode ทุกบรรทัด)
        var detail = string.Join("", PlatformPurchaseEmailText.Lines(order.PlanName ?? order.Plan,
                Enum.TryParse<BillingCycle>(order.Cycle, out var oc) ? (BillingCycle?)oc : null, order.ValidUntilUtc, validUntilIsEstimate: true,
                order.Amount, order.WithholdingTaxAmount)
            .Select(l => $"<li>{E(l)}</li>"));
        var html = $"""
            <p>สวัสดี — ระบบได้รับคำสั่งซื้อแพ็กเกจ NextAcc ของบริษัท <b>{E(order.CompanyName)}</b> แล้ว</p>
            <p>เลขที่รายการ <b>{E(order.PaymentNumber)}</b></p>
            <ul>{detail}</ul>
            <p>ตรวจสถานะ/ชำระต่อได้ที่ <a href="{E(link)}">{E(link)}</a> (ต้องเข้าสู่ระบบด้วยอีเมลนี้)</p>
            <p>ใบเสร็จ/ใบกำกับภาษีจะออกอัตโนมัติเมื่อระบบยืนยันการรับเงิน และดาวน์โหลดได้จากหน้าเดียวกัน</p>
            """;
        try
        {
            var ok = await _email.TrySendAsync(to, $"[NextAcc] คำสั่งซื้อแพ็กเกจ {order.PaymentNumber}", html);
            if (!ok) _logger.LogWarning("ส่งอีเมลยืนยันคำสั่งซื้อ {No} ไม่สำเร็จ (อีเมลระบบยังไม่ตั้ง/ส่งไม่ผ่าน) — คำสั่งซื้อยังอยู่", order.PaymentNumber);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ส่งอีเมลยืนยันคำสั่งซื้อ {No} ไม่สำเร็จ — คำสั่งซื้อยังอยู่", order.PaymentNumber);
        }
    }
}
