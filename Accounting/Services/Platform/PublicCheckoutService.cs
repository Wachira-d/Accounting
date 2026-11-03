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
    string? CouponCode, string? CouponNote, string Source, string Note);

/// <summary>ผลการสร้าง/ใช้ต่อรายการค่าแพ็กเกจ — <see cref="Resumed"/> = ใช้รายการรอชำระเดิม (ไม่สร้างซ้ำ)</summary>
public sealed record PublicCheckoutOrder(Guid CompanyId, string CompanyName, Guid PaymentId, string PaymentNumber, decimal Amount, string Plan,
    string Cycle, string Status, bool Resumed, string Message, string? CouponCode, decimal CouponDiscountAmount, decimal? ListAmount);

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
    Task<PublicCheckoutQuote> QuoteAsync(Guid? companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode, CancellationToken ct = default);
    /// <summary>บริษัทที่ผู้ใช้เป็น<b>เจ้าของ</b> (ซื้อแพ็กเกจให้ได้) — ไม่เปิดข้อมูลบริษัทอื่น</summary>
    Task<List<(Guid Id, string Name, string Plan, string Status)>> OwnedCompaniesAsync(Guid userId, CancellationToken ct = default);
    /// <summary>สร้าง (หรือใช้ต่อ) รายการค่าแพ็กเกจของบริษัทนี้ — ยอดจากเซิร์ฟเวอร์ · ผู้เรียกบันทึกหลักฐานยอมรับนโยบายเองใน controller (ClaimsPrincipal)</summary>
    Task<PublicCheckoutOrder> CreateOrderAsync(Guid userId, Guid companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode,
        CancellationToken ct = default);
    /// <summary>เริ่มชำระออนไลน์ของรายการ — เส้นเดียว platform-pay ทั้ง Live/Test (Test ส่ง <c>allowTestMode</c>) · ยอดจากรายการเท่านั้น</summary>
    Task<PaymentIntent> StartIntentAsync(Guid companyId, Guid paymentId, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default);
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

    public async Task<PublicCheckoutQuote> QuoteAsync(Guid? companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode, CancellationToken ct = default)
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
            var (decision, q, renewalAmount) = await DecideOrderAsync(sub, template, cycle, code, ct);
            if (decision.Kind == PublicCheckoutPolicy.OrderKind.Refused)
                throw new BusinessRuleException(decision.Refusal ?? PublicCheckoutPolicy.DowngradeRefusal, PublicCheckoutPolicy.RuleCode);
            if (decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal)
                return new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), renewalAmount, 0m, renewalAmount,
                    null, null, "company", $"ต่ออายุ {template.Name} {PlatformPlanPricing.MonthsOf(cycle)} เดือน — ต่อจากวันหมดอายุเดิม");
            // ตัวตัดสินเดียวกับตอนยืนยัน (QuotePlanChangeAsync → ราคาพิเศษของบริษัท + คูปอง) — ไม่คิดซ้ำที่นี่
            if (q!.CouponProblem != null) throw new BusinessRuleException(q.CouponProblem, PublicCheckoutPolicy.RuleCode);   // คูปองใช้ไม่ได้ = บอก ไม่เงียบ
            if (q.Blocked != null) throw new BusinessRuleException(q.Blocked, PublicCheckoutPolicy.RuleCode);
            return new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), q.ListAmount, q.Discount, q.AmountDue,
                code, q.CouponLabel ?? q.SpecialPriceNote, "company", q.Message);
        }
        var list = PlatformPlanPricing.AmountDue(template, cycle);
        return new PublicCheckoutQuote(plan.ToString(), cycle.ToString(), PublicCheckoutPolicy.CycleLabel(cycle), list, 0m, list, code,
            code == null ? null : "คูปองจะถูกตรวจและหักให้หลังเข้าสู่ระบบ/สร้างบริษัท (ยอดสุดท้ายแสดงก่อนชำระ)", "list",
            $"ชำระ {template.Name} {PlatformPlanPricing.MonthsOf(cycle)} เดือน — เริ่มรอบจากวันที่ชำระ");
    }

    public async Task<List<(Guid Id, string Name, string Plan, string Status)>> OwnedCompaniesAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.UserId == userId && cu.Role == UserRole.Owner && !cu.Company.IsDeleted)
            .Select(cu => new { cu.CompanyId, cu.Company.Name })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.CompanyId).ToList();
        var subs = await _db.Subscriptions.AsNoTracking().Where(s => ids.Contains(s.CompanyId))
            .Select(s => new { s.CompanyId, s.Plan, s.Status }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var s = subs.FirstOrDefault(x => x.CompanyId == r.CompanyId);
            return (r.CompanyId, r.Name, s?.Plan.ToString() ?? "-", s?.Status.ToString() ?? "-");
        }).ToList();
    }

    private async Task RequireOwnerAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        var owner = await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.UserId == userId && cu.CompanyId == companyId && cu.Role == UserRole.Owner, ct);
        if (!owner) throw new BusinessRuleException("ซื้อแพ็กเกจได้เฉพาะเจ้าของบริษัท", PublicCheckoutPolicy.RuleCode, 403);
    }

    /// <summary>ฝ่ายค้าน B1: ชนิดคำสั่งซื้อ — <b>อ่านอย่างเดียว</b> (ไม่มี side-effect) · ตัวเดียวของยอดตะกร้า (QuoteAsync) และตอนยืนยัน (CreateOrderAsync) ·
    /// คืนใบเสนอ <c>QuotePlanChangeAsync</c> (เส้นซื้อใหม่/เปลี่ยน) หรือยอดต่ออายุ (<c>PlatformPriceOverrideStore.AmountDueAsync</c> + <c>currentPlan</c> ⇒ ราคาต่ออายุ ·
    /// ตัวเดียวกับที่ <c>SubmitPaymentAsync</c>/เริ่มชำระออนไลน์/handler ตรวจ)</summary>
    private async Task<(PublicCheckoutPolicy.OrderDecision Decision, PlanChangeQuote? Quote, decimal RenewalAmount)> DecideOrderAsync(
        Subscription sub, PlanTemplate template, BillingCycle cycle, string? code, CancellationToken ct)
    {
        var activePaid = sub.Status == SubscriptionStatus.Active && !sub.IsPermanentFree;
        var current = activePaid
            ? await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == sub.Plan && t.IsActive, ct)
            : null;
        var renewalShape = activePaid && PlatformPlanPricing.IsRenewalPurchase(sub.Plan, template.Plan) && cycle == sub.BillingCycle;
        // ใบเสนอของเส้นเปลี่ยน/ซื้อใหม่ (อ่านอย่างเดียว) — ต่ออายุไม่ใช้ (QuotePlanChangeAsync ตอบ "แพ็กเกจปัจจุบันอยู่แล้ว" ยอด 0)
        var q = renewalShape ? null : await _subs.QuotePlanChangeAsync(sub.CompanyId, template.Plan, cycle, code);
        var decision = PublicCheckoutPolicy.DecideOrder(sub.Status, sub.IsPermanentFree, sub.Plan, sub.BillingCycle, current?.MonthlyPrice ?? 0m,
            template.Plan, cycle, template.MonthlyPrice, q?.ListAmount ?? 0m,
            renewalInvoiceIssued: PlatformPlanPricing.ScheduleBlockedByIssuedRenewal(sub.RenewalInvoiceForEndDate, sub.EndDate) != null,
            hasCoupon: code != null);
        var renewalAmount = decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal
            ? await PlatformPriceOverrideStore.AmountDueAsync(_db, sub.CompanyId, sub.AccountSubscriptionId, template, cycle, DateTime.UtcNow,
                currentPlan: sub.Plan, ct: ct)
            : 0m;
        if (decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal && renewalAmount <= 0m)
            return (new PublicCheckoutPolicy.OrderDecision(PublicCheckoutPolicy.OrderKind.Refused, "แพ็กเกจนี้ไม่มีค่าบริการต่ออายุตามตารางราคา — ติดต่อผู้ดูแลระบบ NextAcc"), q, 0m);
        return (decision, q, renewalAmount);
    }

    public async Task<PublicCheckoutOrder> CreateOrderAsync(Guid userId, Guid companyId, SubscriptionPlan plan, BillingCycle cycle, string? couponCode,
        CancellationToken ct = default)
    {
        var avail = await AvailabilityAsync(ct);
        if (!avail.Enabled) throw new BusinessRuleException(avail.Reason ?? PublicCheckoutPolicy.DisabledMessage, PublicCheckoutPolicy.RuleCode, 404);
        await RequireOwnerAsync(userId, companyId, ct);
        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new BusinessRuleException("บริษัทนี้ยังไม่มีข้อมูลแพ็กเกจ — เข้าสู่ระบบแล้วเริ่มทดลองใช้ก่อน", PublicCheckoutPolicy.RuleCode);
        var code = PlatformCouponRule.Normalize(couponCode);

        // ไม่สร้างซ้ำ: รายการรอชำระของแพ็กเกจ/รอบเดียวกันที่ยังไม่แนบสลิป/ไม่ถูกพัก ⇒ ใช้ต่อ (ConvertTrialAsync ก็ reuse เมื่อยอดตรง — ด่านนี้กันก่อนให้ชัด)
        var pending = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == sub.Id && p.Subscription.CompanyId == companyId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.PaymentNumber, p.Amount, p.Status, p.RequestedPlan, p.RequestedBillingCycle, p.PlatformInvoiceDocumentId, p.SlipFileName,
                p.AttentionReason, p.CouponCode, p.CouponDiscountAmount, p.ListAmount })
            .FirstOrDefaultAsync(p => p.Status == SubscriptionPaymentStatus.Pending, ct);
        if (pending != null && PublicCheckoutPolicy.CanResume(pending.Status, pending.RequestedPlan, pending.RequestedBillingCycle, plan, cycle,
                pending.PlatformInvoiceDocumentId != null, pending.SlipFileName != null, pending.AttentionReason != null)
            && (code == null || string.Equals(code, pending.CouponCode, StringComparison.OrdinalIgnoreCase)))
        {
            return new PublicCheckoutOrder(companyId, company, pending.Id, pending.PaymentNumber, pending.Amount, plan.ToString(), cycle.ToString(),
                pending.Status.ToString(), true, $"ใช้รายการรอชำระเดิม {pending.PaymentNumber} ยอด {pending.Amount:N2} บาท", pending.CouponCode,
                pending.CouponDiscountAmount, pending.ListAmount);
        }

        // ฝ่ายค้าน B1: ตัดสินชนิดคำสั่งซื้อ<b>ก่อน</b>เรียก service ที่มี side-effect — ดาวน์เกรด/รอบสั้นลงไม่ถึง ChangeSubscriptionAsync (ที่บันทึก ScheduledPlan ก่อน)
        var template = await _db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == plan && t.IsActive, ct)
            ?? throw new BusinessRuleException("ไม่พบแพ็กเกจนี้", PublicCheckoutPolicy.RuleCode, 404);
        if (!PublicCheckoutPolicy.IsSellable(template.IsActive, template.IsPermanentFree, template.MonthlyPrice))
            throw new BusinessRuleException("แพ็กเกจนี้ไม่มีค่าใช้จ่าย — สมัครใช้งานได้ที่หน้าสมัครสมาชิก", PublicCheckoutPolicy.RuleCode);
        var (decision, _, renewalAmount) = await DecideOrderAsync(sub, template, cycle, code, ct);
        if (decision.Kind == PublicCheckoutPolicy.OrderKind.Refused)
            throw new BusinessRuleException(decision.Refusal ?? PublicCheckoutPolicy.DowngradeRefusal, PublicCheckoutPolicy.RuleCode);

        if (decision.Kind == PublicCheckoutPolicy.OrderKind.Renewal)
        {
            // ต่ออายุแพ็กเกจ+รอบเดิม — เส้นแจ้งชำระเดิม (ยอดตรวจซ้ำที่เซิร์ฟเวอร์ด้วยราคาต่ออายุ IsRenewalPurchase · อนุมัติแล้วต่อจากวันหมดอายุเดิม PeriodStart)
            var months = PlatformPlanPricing.MonthsOf(cycle);
            var renewed = await _subs.SubmitPaymentAsync(companyId, new SubmitSubscriptionPaymentRequest(sub.Id, renewalAmount, DateTime.UtcNow,
                PaymentMethod.BankTransfer, null, null, null, null, null, plan, cycle, months,
                "ต่ออายุผ่านหน้าซื้อแพ็กเกจออนไลน์ (รอชำระ)", PolicyAccepted: true), userId.ToString());
            return new PublicCheckoutOrder(companyId, company, renewed.Id, renewed.PaymentNumber, renewed.Amount, plan.ToString(), cycle.ToString(),
                renewed.Status.ToString(), false, $"สร้างรายการต่ออายุ {renewed.PaymentNumber} ยอด {renewed.Amount:N2} บาทแล้ว — ต่อจากวันหมดอายุเดิมเมื่อชำระเงิน",
                renewed.CouponCode, renewed.CouponDiscountAmount, renewed.ListAmount);
        }

        // ซื้อใหม่ (ทดลองใช้/หมดอายุ/ฟรีถาวร) หรืออัปเกรด/รอบยาวขึ้น (Active) — ConvertTrialAsync → RequestPlanPaymentAsync ตัวเดียว (ยอดจากเซิร์ฟเวอร์)
        var result = await _subs.ConvertTrialAsync(companyId, new ConvertTrialRequest(plan, cycle, PromoCode: code), userId.ToString());
        if (result.Payment == null)
            throw new BusinessRuleException(result.Message, PublicCheckoutPolicy.RuleCode);
        var pay = result.Payment;
        return new PublicCheckoutOrder(companyId, company, pay.Id, pay.PaymentNumber, pay.Amount, plan.ToString(), cycle.ToString(), pay.Status.ToString(), false,
            result.Message, pay.CouponCode, pay.CouponDiscountAmount, pay.ListAmount);
    }

    public async Task<PaymentIntent> StartIntentAsync(Guid companyId, Guid paymentId, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default)
    {
        var avail = await AvailabilityAsync(ct);
        if (!avail.Enabled) throw new BusinessRuleException(avail.Reason ?? PublicCheckoutPolicy.DisabledMessage, PublicCheckoutPolicy.RuleCode, 404);
        if (!avail.OnlineAvailable) throw new BusinessRuleException(avail.Reason ?? "ชำระออนไลน์ไม่ได้ในขณะนี้", PublicCheckoutPolicy.RuleCode);
        // ฝ่ายค้าน S1: เส้นเดียวทั้ง Live/Test — ด่านเดิมครบ (รายการรอชำระของบริษัทนี้ · ไม่ซ้อนสลิป/พัก · ยอดตรงราคาปัจจุบัน S3 · วิธีที่เปิด · PlatformDoublePayment) ·
        // ต่างกันเฉพาะโหมดทดสอบผ่านด่านช่องทางได้เมื่อหน้านี้ตัดสินว่าเป็นโหมดทดสอบ (intent ประทับ IsTestMode ⇒ TestModePaymentPolicy กันผลทางธุรกิจ)
        return await _platform.StartSubscriptionPaymentAsync(companyId, paymentId, method, returnUrl, cardToken, ct, allowTestMode: avail.IsTestMode);
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
        var html = $"""
            <p>สวัสดี — ระบบได้รับคำสั่งซื้อแพ็กเกจ NextAcc ของบริษัท <b>{E(order.CompanyName)}</b> แล้ว</p>
            <p>เลขที่รายการ <b>{E(order.PaymentNumber)}</b> · แพ็กเกจ {E(order.Plan)} · รอบบิล {E(order.Cycle)} · ยอด <b>{order.Amount:N2} บาท</b></p>
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
