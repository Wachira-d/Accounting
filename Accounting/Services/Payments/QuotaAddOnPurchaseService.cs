using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

/// <summary>คำขอซื้อโควตาเสริม — <paramref name="ExpectedPerUnit"/>/<paramref name="ExpectedAmount"/> = ยอดที่ผู้ซื้อเห็น (ไม่ตรงของจริง ⇒ 409 · ยังไม่คิดเงิน)</summary>
public sealed record QuotaPurchaseRequest(string ResourceKey, QuotaAddOnMode Mode, int Units, decimal? ExpectedPerUnit, decimal? ExpectedAmount);

/// <summary>ข้อเสนอที่เปลี่ยนระหว่างที่ผู้ซื้อดูอยู่ (409) — ลอก <c>NotifyTopUpOfferChangedException</c></summary>
public sealed class QuotaOfferChangedException : BusinessRuleException
{
    public QuotaOfferChangedException(string message) : base(message, "QUOTA-OFFER-CHANGED", 409) { }
}

public sealed record QuotaRecurringBillingResult(int Billed, decimal Total, int Expired, int SkippedInactive);

public interface IQuotaAddOnPurchaseService
{
    /// <summary>ข้อเสนอที่ซื้อได้ + ยอดของทุกวิธีซื้อ (เซิร์ฟเวอร์คิด — หน้าเว็บแสดงอย่างเดียว)</summary>
    Task<List<object>> OffersAsync(Guid companyId, int units, CancellationToken ct = default);
    Task<QuotaAddOnPricing.Quote> QuoteAsync(Guid companyId, string resourceKey, QuotaAddOnMode mode, int units, CancellationToken ct = default);
    /// <summary>สร้างการซื้อ (รอชำระ) — ตรวจข้อเสนอที่เห็น · เพดานซื้อต่อบริษัท · คีย์กันกดซ้ำ ใต้ล็อก</summary>
    Task<CompanyQuotaAddOn> CreateAsync(Guid companyId, QuotaPurchaseRequest req, string actor, string? idempotencyKey, CancellationToken ct = default);
    Task<PaymentIntent> StartOnlineAsync(Guid companyId, Guid purchaseId, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default);
    Task<CompanyQuotaAddOn> UploadSlipAsync(Guid companyId, Guid purchaseId, string slipPath, string? reference, string actor, CancellationToken ct = default,
        DateTime? transferDateUtc = null);
    /// <summary>แอดมินอนุมัติ — จากสลิปที่รอตรวจ หรือ (ไม่มีสลิป) ต้องมีหลักฐานรับเงินนอกระบบ + เหตุผล · ยอดรับจริง/วันที่โอน = ยอด/วันที่บนใบเสร็จ (§78/1)</summary>
    Task<CompanyQuotaAddOn> ApproveAsync(Guid companyId, Guid purchaseId, string actor, CancellationToken ct = default,
        string? offlineReference = null, string? reason = null, decimal? receivedAmount = null, DateTime? transferDateUtc = null);
    /// <summary>ฝ่ายค้าน S8: แอดมินเพิกถอนสิทธิ์ของการซื้อที่ใช้งานอยู่ (เช่น คืนเงินแล้ว) — มีผลทันที · ไม่ลบข้อมูลผู้ใช้ · audit</summary>
    Task<CompanyQuotaAddOn> RevokeAsync(Guid companyId, Guid purchaseId, string reason, string actor, CancellationToken ct = default);
    /// <summary>ฝ่ายค้าน S5: ออกใบเสร็จอีกครั้งของรายการออนไลน์ที่เงินเข้าแล้วแต่ไม่มีใบ · null = ออกไม่ได้</summary>
    Task<string?> ReissueGatewayReceiptAsync(Guid payerCompanyId, Guid purchaseId, Guid intentId, CancellationToken ct = default);
    Task<CompanyQuotaAddOn> RejectAsync(Guid companyId, Guid purchaseId, string reason, string actor, CancellationToken ct = default);
    /// <summary>เงินเข้าผ่าน gateway แล้ว — เรียกจาก handler เท่านั้น</summary>
    Task MarkPaidByGatewayAsync(Guid companyId, Guid purchaseId, Guid intentId, decimal amount, string actor, CancellationToken ct = default);
    Task<CompanyQuotaAddOn> CancelAsync(Guid companyId, Guid purchaseId, string actor, CancellationToken ct = default);
    Task<CompanyQuotaAddOn> SetRenewalAsync(Guid companyId, Guid purchaseId, bool renew, int? units, string actor, CancellationToken ct = default);
    Task<List<CompanyQuotaAddOn>> PurchasesAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>รายการที่จะต่ออายุพร้อมแพ็กเกจรอบหน้า (ราคาเต็มรอบ) — หน้าต่ออายุ</summary>
    Task<object> RenewalPreviewAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>งานรายเดือน: ค่าเดือนของแบบรายเดือน → UsageEvent · ปิดสถานะที่หมดอายุ</summary>
    Task<QuotaRecurringBillingResult> BillRecurringAsync(string period, CancellationToken ct = default);
}

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.5–§B.8 · คำตัดสินข้อ 229-1..6) — <b>เส้นซื้อโควตาเสริมเส้นเดียว</b>
/// <list type="bullet">
/// <item>ยอดคิดที่ <see cref="QuotaAddOnPricing"/> เท่านั้น · ผู้ซื้อส่งยอดที่เห็นมาเทียบ (ไม่ตรง ⇒ 409)</item>
/// <item>จ่ายออนไลน์ผ่าน <see cref="IPlatformPaymentService"/> (intent ใต้บริษัทผู้ให้บริการ · <c>PaymentSourceKind.QuotaAddOnPurchase</c> อยู่ใน <c>PlatformRevenueSource</c>)
/// · ยังไม่เปิดออนไลน์ ⇒ โอน + แนบสลิป → แอดมินตรวจ</item>
/// <item><b>ให้สิทธิ์เมื่อรับเงินแล้วเท่านั้น</b> (Active) — สลิปรอตรวจยังไม่ให้ (ของที่สร้างไปแล้วถอนไม่ได้เพราะไม่ลบข้อมูลผู้ใช้)</item>
/// <item>รับเงินแล้ว ⇒ ใบกำกับภาษี/ใบเสร็จผ่าน <c>IPlatformBillingDocumentIssuer.IssueAddOnReceiptAsync</c> เดิม (idempotent ด้วยเลขอ้างอิง) · ล้ม ⇒ audit
/// <c>RD-86/4-ADDON-RECEIPT</c> + log error (สิทธิ์เปิดแล้ว — แอดมินออกเอง)</item>
/// <item>OCR: เปิดสิทธิ์ = สร้างเครดิต OCR (Approved) ในตัวนับเดิม — ไม่มีตัวนับที่สอง</item>
/// </list>
/// </summary>
public class QuotaAddOnPurchaseService : IQuotaAddOnPurchaseService
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    /// <summary>ฝ่ายค้าน B1: resolve <see cref="IPlatformPaymentService"/> ตอนใช้ — inject ตรงเกิดวง DI
    /// (นี่ → IPlatformPaymentService → IPaymentIntentService → IEnumerable&lt;IPaymentCompletionHandler&gt; → QuotaAddOnPaymentHandler → นี่) ⇒ ValidateOnBuild ล้ม ·
    /// ล็อกด้วย tools/di_cycle_check.py (รุ่นที่เห็น IEnumerable + register หลายตัวแล้ว)</summary>
    private readonly IServiceProvider _services;
    private readonly ILogger<QuotaAddOnPurchaseService> _logger;
    private readonly IPlatformBillingDocumentIssuer? _issuer;

    public QuotaAddOnPurchaseService(AccountingDbContext db, ISubscriptionService subscriptions, IServiceProvider services,
        ILogger<QuotaAddOnPurchaseService> logger, IPlatformBillingDocumentIssuer? issuer = null)
    { _db = db; _subscriptions = subscriptions; _services = services; _logger = logger; _issuer = issuer; }

    private IPlatformPaymentService Platform => (IPlatformPaymentService)_services.GetService(typeof(IPlatformPaymentService))!;

    public const string StaleRuleCode = "QUOTA-STALE";

    /// <summary>ฝ่ายค้าน B2: ความล้าสมัยของรายการ ณ เวลา <paramref name="atUtc"/> (ตัวตัดสินเดียว <see cref="QuotaAddOnPricing.StaleCheck"/>) —
    /// เดือนเดียวแบบกำหนดวันไม่ล้าสมัย (ช่วงสิทธิ์เริ่มใหม่จากวันจ่าย <see cref="QuotaAddOnPricing.LatePaymentEnd"/>)</summary>
    private async Task<QuotaAddOnPricing.Staleness> StalenessAsync(CompanyQuotaAddOn row, DateTime atUtc)
    {
        if (QuotaAddOnPricing.IsFixedDays(row.Mode, row.PeriodStartUtc, row.PeriodEndUtc)) return QuotaAddOnPricing.Staleness.Fresh;
        DateTime? planEnd = row.Mode == QuotaAddOnMode.Coterm ? (await _subscriptions.GetEffectivePlanAsync(row.CompanyId))?.EndDate : null;
        return QuotaAddOnPricing.StaleCheck(row.Mode, row.CreatedAt, row.PeriodEndUtc, planEnd, atUtc);
    }

    private async Task RefuseIfStaleAsync(CompanyQuotaAddOn row)
    {
        var st = await StalenessAsync(row, DateTime.UtcNow);
        if (st != QuotaAddOnPricing.Staleness.Fresh)
            throw new BusinessRuleException(QuotaAddOnPricing.StaleMessage(st), StaleRuleCode, 409);
    }

    public const string ReceiptRuleCode = AddOnPurchaseService.AddOnReceiptRuleCode;

    /// <summary>เลขอ้างอิงของการรับเงินค่าโควตาเสริมหนึ่งครั้ง — ตัวกันออกใบซ้ำ และตัวหาใบเดิมตอนคืนเงิน (ออนไลน์ = ต่อ intent · สลิป = ต่อการซื้อ) · pure</summary>
    public static string ReceiptReference(Guid purchaseId, Guid? intentId)
        => intentId is Guid i ? $"QADD-{i:N}" : $"QADD-SLIP-{purchaseId:N}";

    private static QuotaAddOnPricing.Offer ToOffer(QuotaAddOnOffer o)
        => new(o.ResourceKey, o.MonthlyUnitPrice, o.AnnualUnitPrice, o.AllowedModes, o.MinChargeOneMonth, o.ValidDays, o.UnitSize, o.MaxUnitsPerCompany);

    /// <summary>บริบทแพ็กเกจของผู้ซื้อ — สถานะ/วันหมดอายุจาก resolver เดียว (<c>GetEffectivePlanAsync</c>) · ฟรีถาวร/รอบบิลจากแถวที่มีผล</summary>
    private async Task<QuotaAddOnPricing.PlanContext> PlanContextAsync(Guid companyId, CancellationToken ct)
    {
        var eff = await _subscriptions.GetEffectivePlanAsync(companyId);
        if (eff == null) return new QuotaAddOnPricing.PlanContext(false, false, false, DateTime.UtcNow, 1);
        var sub = await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.IsPermanentFree, s.BillingCycle, s.AccountSubscriptionId }).FirstOrDefaultAsync(ct);
        bool permanentFree = sub?.IsPermanentFree ?? false;
        var cycle = sub?.BillingCycle ?? BillingCycle.Monthly;
        if (eff.Source == "Account" && sub?.AccountSubscriptionId is Guid aid)
        {
            var acct = await _db.AccountSubscriptions.AsNoTracking().Where(a => a.Id == aid)
                .Select(a => new { a.BillingCycle, Free = a.PlanTemplate != null && a.PlanTemplate.IsPermanentFree }).FirstOrDefaultAsync(ct);
            if (acct != null) { cycle = acct.BillingCycle; permanentFree = acct.Free; }
        }
        return new QuotaAddOnPricing.PlanContext(eff.IsActive, eff.Status == SubscriptionStatus.Trial, permanentFree, eff.EndDate,
            PlatformPlanPricing.MonthsOf(cycle));
    }

    private Task<QuotaAddOnOffer?> OfferAsync(string resourceKey, CancellationToken ct)
        => _db.QuotaAddOnOffers.AsNoTracking().FirstOrDefaultAsync(o => o.ResourceKey == resourceKey && o.IsActive, ct);

    public async Task<QuotaAddOnPricing.Quote> QuoteAsync(Guid companyId, string resourceKey, QuotaAddOnMode mode, int units, CancellationToken ct = default)
    {
        var offer = await OfferAsync(resourceKey, ct) ?? throw new BusinessRuleException("ยังไม่เปิดขายโควตาเสริมของรายการนี้", "QUOTA-NO-OFFER", 404);
        return QuotaAddOnPricing.Compute(ToOffer(offer), mode, units, await PlanContextAsync(companyId, ct), DateTime.UtcNow);
    }

    /// <summary>หน่วยที่ "ถืออยู่/กำลังจะได้" ต่อทรัพยากร — ใช้ตรวจเพดานซื้อต่อบริษัท (Active ที่ยังมีผล + รอชำระ/รอตรวจ)</summary>
    private async Task<int> HeldUnitsAsync(Guid companyId, string resourceKey, DateTime now, CancellationToken ct)
    {
        var rows = await _db.CompanyQuotaAddOns.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.ResourceKey == resourceKey
                && (p.Status == QuotaAddOnStatus.Active || p.Status == QuotaAddOnStatus.AwaitingPayment || p.Status == QuotaAddOnStatus.PendingReview))
            .Select(p => new { p.Units, p.Status, p.PeriodStartUtc, p.PeriodEndUtc }).ToListAsync(ct);
        return rows.Where(r => r.Status != QuotaAddOnStatus.Active || QuotaLimitPolicy.IsLive(r.Status, r.PeriodStartUtc, r.PeriodEndUtc, now))
            .Sum(r => r.Units);
    }

    public async Task<List<object>> OffersAsync(Guid companyId, int units, CancellationToken ct = default)
    {
        if (units < 1) units = 1;
        var now = DateTime.UtcNow;
        var plan = await PlanContextAsync(companyId, ct);
        var offers = await _db.QuotaAddOnOffers.AsNoTracking().Where(o => o.IsActive && o.MonthlyUnitPrice > 0m)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.ResourceKey).ToListAsync(ct);
        var list = new List<object>();
        foreach (var o in offers)
        {
            var res = QuotaResources.Find(o.ResourceKey);
            if (res == null || !res.Sellable) continue;
            var po = ToOffer(o);
            var modes = new List<object>();
            foreach (var m in new[] { QuotaAddOnMode.OneOffMonth, QuotaAddOnMode.RecurringMonthly, QuotaAddOnMode.Coterm })
            {
                if ((QuotaAddOnPricing.ModesFor(o.ResourceKey, o.AllowedModes) & m) == 0) continue;
                var q = QuotaAddOnPricing.Compute(po, m, units, plan, now);
                modes.Add(new
                {
                    mode = m.ToString(), label = ModeLabel(m), allowed = q.Allowed, denyReason = q.DenyReason,
                    perUnit = q.PerUnit, amount = q.Amount, periodEnd = q.PeriodEndUtc, note = q.Note, nextMonthlyAmount = q.NextMonthlyAmount,
                });
            }
            var held = await HeldUnitsAsync(companyId, o.ResourceKey, now, ct);
            list.Add(new
            {
                resource = o.ResourceKey, label = o.Name ?? res.Label, unit = res.Unit, unitSize = o.UnitSize,
                unitText = o.UnitSize > 1 ? $"{o.UnitSize:N0} {res.Unit}" : $"1 {res.Unit}",
                monthlyUnitPrice = o.MonthlyUnitPrice, annualUnitPrice = o.AnnualUnitPrice ?? o.MonthlyUnitPrice * 12m,
                maxUnitsPerCompany = o.MaxUnitsPerCompany, heldUnits = held,
                remainingUnits = o.MaxUnitsPerCompany is int mx ? Math.Max(0, mx - held) : (int?)null,
                vatIncluded = await PriceIncludesVatAsync(ct),
                modes,
            });
        }
        return list;
    }

    public static string ModeLabel(QuotaAddOnMode m) => m switch
    {
        QuotaAddOnMode.OneOffMonth => "เดือนนี้เดือนเดียว",
        QuotaAddOnMode.RecurringMonthly => "ทุกเดือน (ยกเลิกได้)",
        QuotaAddOnMode.Coterm => "ถึงวันหมดอายุแพ็กเกจ",
        _ => m.ToString(),
    };

    public static string StatusLabel(QuotaAddOnStatus s) => s switch
    {
        QuotaAddOnStatus.AwaitingPayment => "รอชำระเงิน",
        QuotaAddOnStatus.PendingReview => "แนบสลิปแล้ว — รอผู้ดูแลระบบตรวจ",
        QuotaAddOnStatus.Active => "ใช้งานอยู่",
        QuotaAddOnStatus.Expired => "หมดอายุ",
        QuotaAddOnStatus.Cancelled => "ยกเลิก",
        QuotaAddOnStatus.Rejected => "ไม่ผ่านการตรวจ",
        _ => s.ToString(),
    };

    private async Task<bool> PriceIncludesVatAsync(CancellationToken ct)
        => await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => (bool?)s.PlatformPriceIncludesVat).FirstOrDefaultAsync(ct) ?? true;

    public async Task<CompanyQuotaAddOn> CreateAsync(Guid companyId, QuotaPurchaseRequest req, string actor, string? idempotencyKey,
        CancellationToken ct = default)
    {
        var res = QuotaResources.Find(req.ResourceKey);
        if (res == null || !res.Sellable) throw new BusinessRuleException("ทรัพยากรนี้ซื้อเพิ่มไม่ได้", "QUOTA-NO-OFFER");
        var requestKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey.Trim())))[..40].ToLowerInvariant();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaAddOnPurchase, res.Key) }, ct);

        // กดซ้ำ/เครือข่ายส่งซ้ำด้วยคีย์เดิม = คำขอเดียว ⇒ คืนรายการเดิม (ไม่สร้างรายการชำระซ้อน)
        if (requestKey != null)
        {
            var dup = await _db.CompanyQuotaAddOns.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.RequestKey == requestKey, ct);
            if (dup != null) { await tx.RollbackAsync(ct); return dup; }
        }

        var offer = await OfferAsync(res.Key, ct) ?? throw new BusinessRuleException("ยังไม่เปิดขายโควตาเสริมของรายการนี้", "QUOTA-NO-OFFER", 404);
        var now = DateTime.UtcNow;
        var plan = await PlanContextAsync(companyId, ct);
        var q = QuotaAddOnPricing.Compute(ToOffer(offer), req.Mode, req.Units, plan, now);
        if (!q.Allowed) throw new BusinessRuleException(q.DenyReason ?? "ซื้อไม่ได้", "QUOTA-PURCHASE-DENIED");
        if (QuotaAddOnPricing.OfferChanged(req.ExpectedPerUnit, req.ExpectedAmount, q) is string changed)
            throw new QuotaOfferChangedException(changed);
        if (offer.MaxUnitsPerCompany is int max)
        {
            var held = await HeldUnitsAsync(companyId, res.Key, now, ct);
            if (held + req.Units > max)
                throw new BusinessRuleException(
                    $"ซื้อ{res.Label}ได้สูงสุด {max:N0} หน่วยต่อบริษัท — ถืออยู่/รอชำระ {held:N0} หน่วย (เหลือซื้อได้ {Math.Max(0, max - held):N0})",
                    "QUOTA-MAX-UNITS", 409);
        }

        // ใบต่ออายุรอบหน้าออกแล้ว ⇒ แบบถึงวันหมดอายุที่ซื้อตอนนี้ไม่อยู่ในใบนั้น — ไม่ตั้ง "ต่อพร้อมแพ็กเกจ" เงียบ ๆ (บอกในหมายเหตุ)
        var renewalIssued = req.Mode == QuotaAddOnMode.Coterm && await _db.Subscriptions.AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && !s.IsDeleted && s.RenewalInvoiceDocumentId != null && s.RenewalInvoiceForEndDate == s.EndDate, ct);

        var row = new CompanyQuotaAddOn
        {
            CompanyId = companyId, OfferId = offer.Id, ResourceKey = res.Key, Units = req.Units, UnitSize = offer.UnitSize,
            Mode = req.Mode, PeriodStartUtc = now, PeriodEndUtc = q.PeriodEndUtc, Status = QuotaAddOnStatus.AwaitingPayment,
            RenewWithPlan = req.Mode == QuotaAddOnMode.Coterm && !renewalIssued,
            UnitPriceSnapshot = q.PerUnit, MonthlyUnitPriceSnapshot = offer.MonthlyUnitPrice, AmountDue = q.Amount,
            VatIncluded = await PriceIncludesVatAsync(ct),
            ProrationNote = q.Note + (renewalIssued ? " · ใบแจ้งหนี้ต่ออายุรอบหน้าออกแล้ว — รายการนี้หมดพร้อมรอบปัจจุบัน (ซื้อใหม่เมื่อต่ออายุ)" : ""),
            RequestKey = requestKey, CreatedBy = actor,
        };
        _db.CompanyQuotaAddOns.Add(row);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Create, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "QuotaAddOnPurchaseCreated", resource = res.Key, mode = req.Mode.ToString(), row.Units, row.UnitSize,
                perUnit = q.PerUnit, amount = q.Amount, periodEnd = q.PeriodEndUtc, note = q.Note, by = actor,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    private async Task<CompanyQuotaAddOn> LoadAsync(Guid companyId, Guid purchaseId, CancellationToken ct)
        => await _db.CompanyQuotaAddOns.FirstOrDefaultAsync(p => p.Id == purchaseId && p.CompanyId == companyId, ct)
           ?? throw new BusinessRuleException("ไม่พบรายการซื้อโควตาเสริมของบริษัทนี้", "QUOTA-NOT-FOUND", 404);

    public async Task<PaymentIntent> StartOnlineAsync(Guid companyId, Guid purchaseId, PaymentMethodKind method, string? returnUrl, string? cardToken,
        CancellationToken ct = default)
    {
        var row = await LoadAsync(companyId, purchaseId, ct);
        if (PlatformDoublePayment.OnlineStartRefusal(row.Status == QuotaAddOnStatus.PendingReview) is string slipNo)
            throw new InvalidOperationException(slipNo);
        // สลิปถูกปฏิเสธ ⇒ ชำระออนไลน์แทนได้ (ยอดเดิมของรายการ) · ใช้งานอยู่/ยกเลิก/หมดอายุ ⇒ ไม่ใช่รายการรอชำระ
        if (row.Status is not (QuotaAddOnStatus.AwaitingPayment or QuotaAddOnStatus.Rejected))
            throw new InvalidOperationException("รายการนี้ไม่อยู่ในสถานะรอชำระ");
        if (row.AmountDue <= 0m) throw new InvalidOperationException("รายการนี้ไม่มียอดค้างชำระ");
        await RefuseIfStaleAsync(row);   // ฝ่ายค้าน B2: ข้อเสนอเก่า (ข้ามเดือน/หมดช่วง/วันหมดอายุแพ็กเกจเปลี่ยน) ⇒ 409 สร้างรายการใหม่
        var res = QuotaResources.Find(row.ResourceKey);
        // ยอด = AmountDue ที่เซิร์ฟเวอร์คิดตอนสร้างรายการ (QuotaAddOnPricing) — ไม่รับยอดจากหน้าเว็บ
        return await Platform.StartAsync(companyId, PaymentSourceKind.QuotaAddOnPurchase, row.Id, row.AmountDue, method,
            $"โควตาเสริม {res?.Label ?? row.ResourceKey} {row.Units:N0} หน่วย", returnUrl, cardToken, ct);
    }

    public async Task<CompanyQuotaAddOn> UploadSlipAsync(Guid companyId, Guid purchaseId, string slipPath, string? reference, string actor,
        CancellationToken ct = default, DateTime? transferDateUtc = null)
    {
        var row = await LoadAsync(companyId, purchaseId, ct);
        if (row.Status is not (QuotaAddOnStatus.AwaitingPayment or QuotaAddOnStatus.Rejected))
            throw new BusinessRuleException("รายการนี้ไม่อยู่ในสถานะรอชำระ — แนบสลิปไม่ได้", "QUOTA-SLIP-STATE", 409);
        await RefuseIfStaleAsync(row);
        if (await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.QuotaAddOnPurchase && i.SourceId == row.Id
                && i.PayerCompanyId == companyId && (i.Status == PaymentIntentStatus.Succeeded || i.Status == PaymentIntentStatus.Pending), ct))
            throw new BusinessRuleException(PlatformDoublePayment.SlipAfterOnlineMessage, PlatformDoublePayment.RuleCode, 409);
        row.SlipPath = slipPath;
        row.SlipReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim()[..Math.Min(120, reference.Trim().Length)];
        row.SlipUploadedAt = DateTime.UtcNow;
        // S7: วันที่โอนตามสลิป = วันที่ใบเสร็จ (§78/1) · อนาคต = ไม่รับ (ใช้วันนี้)
        row.SlipTransferDate = transferDateUtc is DateTime td && td <= DateTime.UtcNow.AddHours(1) ? td : null;
        row.Status = QuotaAddOnStatus.PendingReview;
        row.RejectedReason = null; row.ReviewedAt = null; row.ReviewedBy = null;
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public Task<CompanyQuotaAddOn> ApproveAsync(Guid companyId, Guid purchaseId, string actor, CancellationToken ct = default,
        string? offlineReference = null, string? reason = null, decimal? receivedAmount = null, DateTime? transferDateUtc = null)
        => SessionAdvisoryLock.RunAsync(_db, AdvisoryLockKey.For(companyId, SessionAdvisoryLock.PlatformQuotaAddOnPayment, purchaseId.ToString("N")),
            async () =>
            {
                var row = await LoadAsync(companyId, purchaseId, ct);
                await _db.Entry(row).ReloadAsync(ct);
                if (row.Status == QuotaAddOnStatus.Active && row.PaymentIntentId != null)
                    throw new BusinessRuleException(PlatformDoublePayment.SlipAfterOnlineMessage, PlatformDoublePayment.RuleCode);
                if (row.Status == QuotaAddOnStatus.Active) return row;   // อนุมัติซ้ำ = ผลเดิม
                // ฝ่ายค้าน S2: อนุมัติปกติ = จากสลิปที่รอตรวจเท่านั้น · รอชำระ (ไม่มีสลิป) ⇒ ต้องมีหลักฐานเงินนอกระบบ + เหตุผล และห้ามขณะมีรายการชำระออนไลน์ค้าง
                if (row.Status == QuotaAddOnStatus.AwaitingPayment)
                {
                    if (string.IsNullOrWhiteSpace(offlineReference) || string.IsNullOrWhiteSpace(reason))
                        throw new BusinessRuleException("รายการนี้ยังไม่มีสลิป — อนุมัติได้เมื่อระบุหลักฐานการรับเงินนอกระบบ (เลขอ้างอิงการโอน) และเหตุผล", "QUOTA-APPROVE-EVIDENCE", 409);
                    if (await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.QuotaAddOnPurchase && i.SourceId == row.Id
                            && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending), ct))
                        throw new BusinessRuleException("มีรายการชำระออนไลน์ค้างอยู่ — รอผลก่อน (กันเก็บเงินซ้อน)", PlatformDoublePayment.RuleCode, 409);
                    row.OfflineReference = offlineReference.Trim()[..Math.Min(200, offlineReference.Trim().Length)];
                }
                else if (row.Status != QuotaAddOnStatus.PendingReview)
                    throw new BusinessRuleException($"อนุมัติไม่ได้ — สถานะ {StatusLabel(row.Status)}", "QUOTA-APPROVE-STATE", 409);
                await RefuseIfStaleAsync(row);   // B2: ข้อเสนอล้าสมัย ⇒ ไม่เปิดสิทธิ์ที่ช่วงใช้ไม่ได้ (คืนเงิน/ให้ลูกค้าสร้างรายการใหม่)
                var received = QuotaAddOnPricing.R2(receivedAmount ?? row.AmountDue);
                if (received < row.AmountDue)
                    throw new BusinessRuleException($"ยอดรับจริง {received:N2} น้อยกว่ายอดที่ต้องชำระ {row.AmountDue:N2} บาท — ไม่เปิดสิทธิ์ (ปฏิเสธพร้อมเหตุผลให้ลูกค้าโอนเพิ่ม)",
                        "QUOTA-UNDERPAID", 409);
                // S7: วันที่ใบเสร็จ = วันที่โอน (แอดมินระบุ > สลิป > วันอนุมัติ) · ไม่เกินวันนี้
                var now = DateTime.UtcNow;
                var paidAt = transferDateUtc ?? row.SlipTransferDate ?? now;
                if (paidAt > now) paidAt = now;
                row.ReviewedAt = now; row.ReviewedBy = actor;
                if (!string.IsNullOrWhiteSpace(reason))
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(),
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaAddOnApprovedOffline", offlineReference, reason, received, paidAt, by = actor }),
                        Timestamp = now,
                    });
                await ActivateAsync(row, paidAt, null, received, actor, ct);
                return row;
            }, ct);

    public async Task<CompanyQuotaAddOn> RevokeAsync(Guid companyId, Guid purchaseId, string reason, string actor, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length < 3) throw new BusinessRuleException("ต้องระบุเหตุผลการเพิกถอน (บันทึกใน audit)");
        var row = await LoadAsync(companyId, purchaseId, ct);
        if (row.Status != QuotaAddOnStatus.Active)
            throw new BusinessRuleException($"เพิกถอนได้เฉพาะรายการที่ใช้งานอยู่ (สถานะ {StatusLabel(row.Status)})", "QUOTA-REVOKE-STATE", 409);
        var now = DateTime.UtcNow;
        var before = row.PeriodEndUtc;
        row.Status = QuotaAddOnStatus.Cancelled;
        row.PeriodEndUtc = now; row.CancelAtPeriodEnd = true; row.RenewWithPlan = false;
        row.UpdatedAt = now; row.UpdatedBy = actor;
        // เครดิต OCR ที่เหลือหมดอายุทันที (แถวเครดิตยังอยู่ — ประวัติการใช้ไม่หาย) · ของที่ผู้ใช้สร้างไว้แล้วไม่ถูกแตะ (เกินเพดาน = บล็อกการเพิ่มเท่านั้น)
        if (row.OcrCreditPurchaseId is Guid cid)
        {
            var credit = await _db.OcrCreditPurchases.FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId, ct);
            if (credit != null) credit.ExpiresAt = now;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { status = "Active", periodEnd = before }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaAddOnRevoked", reason = why, by = actor }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<CompanyQuotaAddOn> RejectAsync(Guid companyId, Guid purchaseId, string reason, string actor, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length == 0) throw new BusinessRuleException("ต้องระบุเหตุผลที่ไม่อนุมัติ — ลูกค้าจะเห็นข้อความนี้");
        if (why.Length > 500) why = why[..500];
        var row = await LoadAsync(companyId, purchaseId, ct);
        if (row.Status == QuotaAddOnStatus.Active)
            throw new BusinessRuleException("รายการนี้ใช้งานอยู่แล้ว (รับเงินแล้ว) — ปฏิเสธไม่ได้ · คืนเงินผ่านหน้ารายการชำระ/ใบลดหนี้", "QUOTA-REJECT-ACTIVE", 409);
        row.Status = QuotaAddOnStatus.Rejected;
        row.RejectedReason = why; row.ReviewedAt = DateTime.UtcNow; row.ReviewedBy = actor;
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaAddOnSlipRejected", reason = why, by = actor }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public Task MarkPaidByGatewayAsync(Guid companyId, Guid purchaseId, Guid intentId, decimal amount, string actor, CancellationToken ct = default)
        => SessionAdvisoryLock.RunAsync(_db, AdvisoryLockKey.For(companyId, SessionAdvisoryLock.PlatformQuotaAddOnPayment, purchaseId.ToString("N")),
            async () =>
            {
                var row = await _db.CompanyQuotaAddOns.FirstOrDefaultAsync(p => p.Id == purchaseId && p.CompanyId == companyId, ct);
                if (row == null)
                {
                    _logger.LogError("เงินค่าโควตาเสริมเข้าแล้วแต่หารายการไม่พบ — บริษัท {Company} รายการ {Purchase} intent {Intent} ยอด {Amount:N2} · ต้องเปิดสิทธิ์/คืนเงินด้วยมือ",
                        companyId, purchaseId, intentId, amount);
                    throw new BusinessRuleException("เงินเข้าแล้วแต่หารายการซื้อโควตาเสริมไม่พบ — ผู้ดูแลระบบต้องตรวจ", "QUOTA-PAID-NO-ROW");
                }
                await _db.Entry(row).ReloadAsync(ct);
                if (row.Status == QuotaAddOnStatus.Active && row.PaymentIntentId == intentId) return;   // webhook ซ้ำ
                var settle = PlatformDoublePayment.OnGatewaySuccess(row.Status == QuotaAddOnStatus.Active, row.PaymentIntentId, intentId,
                    row.Status == QuotaAddOnStatus.PendingReview);
                if (settle != PlatformDoublePayment.Outcome.Apply || amount < row.AmountDue)
                {
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(), Action = AuditAction.Update,
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            action = PlatformDoublePayment.AuditAction, ruleCode = PlatformDoublePayment.RuleCode, intentId, amount, due = row.AmountDue,
                            outcome = settle.ToString(), status = row.Status.ToString(),
                        }),
                        Timestamp = DateTime.UtcNow,
                    });
                    await _db.SaveChangesAsync(ct);
                    if (settle == PlatformDoublePayment.Outcome.AlreadySettled)
                        throw new BusinessRuleException(PlatformDoublePayment.AlreadySettledMessage, PlatformDoublePayment.RuleCode);
                    if (amount < row.AmountDue)
                        throw new BusinessRuleException($"เงินเข้า {amount:N2} บาท น้อยกว่ายอดที่ต้องชำระ {row.AmountDue:N2} บาท — ไม่เปิดสิทธิ์อัตโนมัติ ผู้ดูแลระบบต้องตรวจ",
                            "QUOTA-UNDERPAID");
                }
                if (row.Status is QuotaAddOnStatus.Rejected or QuotaAddOnStatus.Cancelled or QuotaAddOnStatus.Expired)
                    _logger.LogWarning("เงินค่าโควตาเสริมเข้าหลังรายการเป็น {Status} — เปิดสิทธิ์ตามเงินที่รับจริง (รายการ {Purchase})", row.Status, row.Id);
                row.PaymentIntentId = intentId;
                var paidAt = await _db.PaymentIntents.AsNoTracking().Where(i => i.Id == intentId && i.PayerCompanyId == companyId)
                    .Select(i => i.PaidAt ?? i.ConfirmedAt).FirstOrDefaultAsync(ct) ?? DateTime.UtcNow;
                // ฝ่ายค้าน B2: เงินเข้าช้า — ช่วงสิทธิ์ที่คิดไว้ใช้ไม่ได้แล้ว (ข้ามเดือน/หมดช่วง/วันหมดอายุแพ็กเกจเปลี่ยน) ⇒ ไม่เปิดสิทธิ์ช่วงศูนย์วันเงียบ ๆ:
                // ประทับเรื่องค้าง QUOTA-STALE + audit + ล้มดังถึงผู้ส่งต่อ (ประวัติ ⚠️) ให้แอดมินคืนเงิน/ตกลงกับลูกค้า · เงินที่รับจริงบันทึกไว้
                var staleness = await StalenessAsync(row, paidAt);
                if (staleness != QuotaAddOnPricing.Staleness.Fresh)
                {
                    row.ReceivedAmount = amount;
                    row.AttentionRule = StaleRuleCode;
                    row.AttentionReason = $"เงินเข้า {amount:N2} บาท (intent {intentId:N}) หลังข้อเสนอล้าสมัย: {QuotaAddOnPricing.StaleMessage(staleness)} — ไม่เปิดสิทธิ์อัตโนมัติ · คืนเงินหรือเปิดให้ด้วยมือ";
                    row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(), Action = AuditAction.Update,
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "quota-addon-paid-stale", ruleCode = StaleRuleCode, intentId, amount, staleness = staleness.ToString(), paidAt }),
                        Timestamp = DateTime.UtcNow,
                    });
                    await _db.SaveChangesAsync(ct);
                    _logger.LogError("เงินค่าโควตาเสริมเข้าหลังข้อเสนอล้าสมัย ({Staleness}) — รายการ {Purchase} บริษัท {Company} ยอด {Amount:N2} · แอดมินต้องจัดการ",
                        staleness, row.Id, companyId, amount);
                    throw new BusinessRuleException(row.AttentionReason, StaleRuleCode);
                }
                await ActivateAsync(row, paidAt, intentId, amount, actor, ct);
            }, ct);

    /// <summary>รับเงินแล้ว ⇒ เปิดสิทธิ์ (+ เครดิต OCR) + ใบกำกับ/ใบเสร็จ + audit · เรียกใต้ล็อกของรายการเท่านั้น</summary>
    private async Task ActivateAsync(CompanyQuotaAddOn row, DateTime paidAtUtc, Guid? intentId, decimal amountReceived, string actor, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // B2: เดือนเดียวแบบกำหนดวัน — ช่วงสิทธิ์ยาวเท่าที่คิด เริ่มจากวันจ่าย (ไม่เสียวันเพราะจ่ายช้า) · ต้องคำนวณก่อนเปลี่ยน PeriodStart
        if (QuotaAddOnPricing.LatePaymentEnd(row.Mode, QuotaAddOnPricing.IsFixedDays(row.Mode, row.PeriodStartUtc, row.PeriodEndUtc),
                row.PeriodStartUtc, row.PeriodEndUtc, paidAtUtc > now ? now : paidAtUtc) is DateTime lateEnd)
            row.PeriodEndUtc = lateEnd;
        row.Status = QuotaAddOnStatus.Active;
        row.PeriodStartUtc = now;
        row.RejectedReason = null;
        row.AttentionRule = null; row.AttentionReason = null;
        row.ReceivedAmount = amountReceived;
        // เดือนแรกที่จ่ายแล้ว (คิดตามวัน) = เดือนของตอนคิดราคา (ด่าน B2 รับประกันว่ายังเป็นเดือนเดียวกัน) — งานรายเดือนเริ่มเดือนถัดไป
        if (row.Mode == QuotaAddOnMode.RecurringMonthly) row.LastBilledPeriod = AddOnBilling.PeriodOf(row.CreatedAt);
        row.UpdatedAt = now; row.UpdatedBy = actor;

        if (row.ResourceKey == QuotaResources.OcrPagesMonth && row.OcrCreditPurchaseId == null)
        {
            // OCR: ตัวนับเดิม (OcrQuotaService ใช้เครดิต Approved ที่ยังไม่หมดอายุ) — ไม่มีตัวนับที่สอง
            var subId = await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == row.CompanyId && !s.IsDeleted)
                .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            if (subId is Guid sid)
            {
                var pages = row.Units * row.UnitSize;
                var credit = new OcrCreditPurchase
                {
                    CompanyId = row.CompanyId, SubscriptionId = sid, PagesPurchased = pages, PagesRemaining = pages,
                    AmountPaid = amountReceived, Status = "Approved", PaymentReference = ReceiptReference(row.Id, intentId),
                    ReviewedAt = now, ReviewNotes = $"ซื้อผ่านโควตาเสริม (รายการ {row.Id:N})", ExpiresAt = row.PeriodEndUtc ?? now.AddDays(QuotaAddOnPricing.ConsumableDefaultValidDays),
                    CreatedBy = actor,
                };
                _db.OcrCreditPurchases.Add(credit);
                row.OcrCreditPurchaseId = credit.Id;
            }
            else _logger.LogError("ซื้อหน้า OCR แล้วแต่บริษัท {Company} ไม่มีแพ็กเกจ — สร้างเครดิตไม่ได้ (รายการ {Purchase})", row.CompanyId, row.Id);
        }

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = row.CompanyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "QuotaAddOnActivated", resource = row.ResourceKey, mode = row.Mode.ToString(), row.Units, amount = amountReceived,
                intentId, periodEnd = row.PeriodEndUtc, by = actor,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);

        // ใบกำกับภาษี/ใบเสร็จรับเงิน (ขายเงินสด) ใน tenant ผู้ให้บริการ — เส้นเดิมของส่วนเสริม (§86/4 · gap-free · รายงานภาษีขาย)
        await IssueReceiptAsync(row, amountReceived, paidAtUtc, intentId, ct);
    }

    /// <summary>ใบกำกับภาษี/ใบเสร็จของเงินที่รับ (idempotent ด้วยเลขอ้างอิง) · ล้ม ⇒ audit + log error (ไม่โยน — สิทธิ์เปิดแล้ว)</summary>
    private async Task<string?> IssueReceiptAsync(CompanyQuotaAddOn row, decimal amountReceived, DateTime paidAtUtc, Guid? intentId, CancellationToken ct)
    {
        // S7: ยอดบนใบ = เงินที่รับจริง · วันที่ = วันรับเงิน/วันโอน (§78/1)
        if (_issuer != null && amountReceived > 0m)
        {
            var res = QuotaResources.Find(row.ResourceKey);
            var unitText = row.UnitSize > 1 ? $"{row.Units:N0} × {row.UnitSize:N0} {res?.Unit}" : $"{row.Units:N0} {res?.Unit}";
            var desc = $"โควตาเสริม {res?.Label ?? row.ResourceKey} {unitText} · {ModeLabel(row.Mode)} · หน่วยละ {row.UnitPriceSnapshot:N2} บาท"
                + (string.IsNullOrWhiteSpace(row.ProrationNote) ? "" : $" · {row.ProrationNote}");
            var doc = await _issuer.IssueAddOnReceiptAsync(row.CompanyId, desc.Length > 480 ? desc[..480] : desc, amountReceived, paidAtUtc,
                ReceiptReference(row.Id, intentId), intentId);
            if (doc != null)
            {
                row.ReceiptDocumentId = doc.DocumentId; row.ReceiptNumber = doc.DocumentNumber;
                await _db.SaveChangesAsync(ct);
                return doc.DocumentNumber;
            }
            else
            {
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = row.CompanyId, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(), Action = AuditAction.Update,
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "quota-addon-receipt-not-issued", ruleCode = ReceiptRuleCode, amount = amountReceived, intentId,
                        note = "รับเงินค่าโควตาเสริมแล้วแต่ออกใบเสร็จ/ใบกำกับใน tenant ผู้ให้บริการไม่สำเร็จ — ออกด้วยมือในบริษัทผู้ให้บริการ",
                    }),
                    Timestamp = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync(ct);
                _logger.LogError("รับเงินค่าโควตาเสริม {Purchase} บริษัท {Company} ยอด {Amount:N2} แล้วแต่ออกใบเสร็จไม่สำเร็จ — ต้องออกด้วยมือ",
                    row.Id, row.CompanyId, row.AmountDue);
            }
        }
        return null;
    }

    /// <summary>ฝ่ายค้าน S5: "ออกใบเสร็จอีกครั้ง" ของรายการออนไลน์ที่เงินเข้าแล้วแต่ไม่มีใบ (หน้าแอดมินช่องทางรับชำระ) — เฉพาะรายการที่ intent นี้เปิดสิทธิ์ ·
    /// ยอด = เงินที่รับจริง · เลขอ้างอิงเดิม ⇒ ได้ใบเดิมถ้าเคยออกแล้ว</summary>
    public async Task<string?> ReissueGatewayReceiptAsync(Guid payerCompanyId, Guid purchaseId, Guid intentId, CancellationToken ct = default)
    {
        var row = await _db.CompanyQuotaAddOns.FirstOrDefaultAsync(p => p.Id == purchaseId && p.CompanyId == payerCompanyId, ct);
        if (row == null || row.PaymentIntentId != intentId || row.Status is not (QuotaAddOnStatus.Active or QuotaAddOnStatus.Expired or QuotaAddOnStatus.Cancelled))
            return null;
        if (row.ReceiptNumber != null) return row.ReceiptNumber;
        var intent = await _db.PaymentIntents.AsNoTracking().Where(i => i.Id == intentId && i.PayerCompanyId == payerCompanyId && i.SourceId == purchaseId)
            .Select(i => new { i.Amount, PaidAt = i.PaidAt ?? i.ConfirmedAt }).FirstOrDefaultAsync(ct);
        if (intent == null) return null;
        return await IssueReceiptAsync(row, row.ReceivedAmount ?? intent.Amount, intent.PaidAt ?? DateTime.UtcNow, intentId, ct);
    }

    public async Task<CompanyQuotaAddOn> CancelAsync(Guid companyId, Guid purchaseId, string actor, CancellationToken ct = default)
    {
        var row = await LoadAsync(companyId, purchaseId, ct);
        var now = DateTime.UtcNow;
        if (row.Status is QuotaAddOnStatus.AwaitingPayment or QuotaAddOnStatus.Rejected)
        {
            // ยังไม่ได้จ่าย ⇒ ยกเลิกรายการได้ทันที (ไม่มีเงิน/สิทธิ์เกี่ยวข้อง) — มีรายการชำระออนไลน์ค้าง ⇒ ห้าม (เงินอาจกำลังเข้า)
            if (await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.QuotaAddOnPurchase && i.SourceId == row.Id
                    && i.PayerCompanyId == companyId && (i.Status == PaymentIntentStatus.Pending || i.Status == PaymentIntentStatus.Created), ct))
                throw new BusinessRuleException("มีรายการชำระออนไลน์ค้างอยู่ — รอให้หมดอายุก่อนยกเลิก", "QUOTA-CANCEL-PENDING", 409);
            row.Status = QuotaAddOnStatus.Cancelled;
        }
        else if (row.Status == QuotaAddOnStatus.Active && row.Mode == QuotaAddOnMode.RecurringMonthly)
        {
            // ไม่คืนเงิน (นโยบาย §6.2c) — ใช้ได้ถึงสิ้นเดือนไทยนี้ แล้วหยุดคิดค่าเดือน
            row.CancelAtPeriodEnd = true;
            row.PeriodEndUtc = QuotaAddOnPricing.NextThaiMonthStartUtc(now);
        }
        else if (row.Status == QuotaAddOnStatus.Active && row.Mode == QuotaAddOnMode.Coterm)
        {
            // ฝ่ายค้าน S1: อยู่ในใบต่ออายุที่ออกแล้ว ⇒ ล็อก (เหมือน SetRenewalAsync) — ไม่งั้นเอาออกแล้วจ่ายใบ ระบบยังต่อให้ = silent no-op
            if (row.RenewalInvoiceDocumentId != null)
                throw new BusinessRuleException("ใบแจ้งหนี้ต่ออายุรอบหน้าออกแล้ว (รวมรายการนี้) — ยกเลิกการต่อไม่ได้ (ติดต่อผู้ดูแลระบบเพื่อออกใบใหม่)",
                    "QUOTA-RENEW-LOCKED", 409);
            row.RenewWithPlan = false;   // ไม่ต่อรอบหน้า — ใช้ได้ถึงวันหมดอายุที่จ่ายไว้
        }
        else throw new BusinessRuleException("รายการนี้ยกเลิกไม่ได้ (เดือนเดียว = หมดเองเมื่อครบกำหนด)", "QUOTA-CANCEL-STATE", 409);
        row.UpdatedAt = now; row.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaAddOnCancelled", status = row.Status.ToString(), row.PeriodEndUtc, by = actor }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<CompanyQuotaAddOn> SetRenewalAsync(Guid companyId, Guid purchaseId, bool renew, int? units, string actor, CancellationToken ct = default)
    {
        var row = await LoadAsync(companyId, purchaseId, ct);
        if (row.Mode != QuotaAddOnMode.Coterm || row.Status != QuotaAddOnStatus.Active)
            throw new BusinessRuleException("เลือกต่ออายุได้เฉพาะโควตาเสริมแบบ \"ถึงวันหมดอายุแพ็กเกจ\" ที่ใช้งานอยู่", "QUOTA-RENEW-STATE", 409);
        // ห้าม silent no-op: ใบต่ออายุออกแล้ว ⇒ ล็อก (เปลี่ยนรายการต้องให้ผู้ดูแลระบบออกใบใหม่)
        if (row.RenewalInvoiceDocumentId != null)
            throw new BusinessRuleException("ใบแจ้งหนี้ต่ออายุรอบหน้าออกแล้ว — เปลี่ยนรายการต่ออายุไม่ได้ (ติดต่อผู้ดูแลระบบเพื่อออกใบใหม่)",
                "QUOTA-RENEW-LOCKED", 409);
        if (units is int u)
        {
            if (u < 1) throw new BusinessRuleException("จำนวนที่ต่อต้องอย่างน้อย 1 (ไม่ต่อ = เอาเครื่องหมายออก)");
            var offer = await _db.QuotaAddOnOffers.AsNoTracking().FirstOrDefaultAsync(o => o.Id == row.OfferId, ct);
            if (offer?.MaxUnitsPerCompany is int mx && u > mx)
                throw new BusinessRuleException($"ต่อได้สูงสุด {mx:N0} หน่วย", "QUOTA-MAX-UNITS", 409);
        }
        row.RenewWithPlan = renew;
        row.RenewUnits = units;
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public Task<List<CompanyQuotaAddOn>> PurchasesAsync(Guid companyId, CancellationToken ct = default)
        => _db.CompanyQuotaAddOns.AsNoTracking().Where(p => p.CompanyId == companyId)
            .OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);

    public async Task<object> RenewalPreviewAsync(Guid companyId, CancellationToken ct = default)
    {
        var sub = await _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.EndDate, s.BillingCycle, s.ScheduledPlan, s.ScheduledBillingCycle, s.RenewalInvoiceNumber, s.RenewalInvoiceForEndDate, s.AccountSubscriptionId })
            .FirstOrDefaultAsync(ct);
        if (sub == null) return new { items = Array.Empty<object>(), note = "ยังไม่มีแพ็กเกจ" };
        var cycle = sub.ScheduledPlan != null ? (sub.ScheduledBillingCycle ?? sub.BillingCycle) : sub.BillingCycle;
        var lines = await QuotaRenewalLines.CandidatesAsync(_db, companyId, sub.EndDate, PlatformPlanPricing.MonthsOf(cycle), includeOptedOut: true, ct);
        var invoiceIssued = sub.RenewalInvoiceNumber != null && sub.RenewalInvoiceForEndDate == sub.EndDate;
        return new
        {
            endDate = sub.EndDate, cycleMonths = PlatformPlanPricing.MonthsOf(cycle), invoiceIssued, invoiceNumber = invoiceIssued ? sub.RenewalInvoiceNumber : null,
            licenseManaged = sub.AccountSubscriptionId != null,
            note = sub.AccountSubscriptionId != null
                ? "บริษัทนี้ใช้แพ็กเกจจาก License — โควตาเสริมแบบถึงวันหมดอายุหมดพร้อม License (ต่ออายุพร้อมใบแจ้งหนี้ยังไม่รองรับในรุ่นนี้ — ซื้อใหม่หลังต่ออายุ)"
                : invoiceIssued ? $"ใบแจ้งหนี้ต่ออายุ {sub.RenewalInvoiceNumber} ออกแล้ว — รายการที่ติ๊กไว้อยู่ในใบนั้น (เปลี่ยนไม่ได้แล้ว)"
                : "ค่าตั้งต้น = ต่อทุกรายการที่ใช้อยู่ · เอาเครื่องหมายออกเพื่อไม่ต่อ (หมดพร้อมรอบปัจจุบัน ข้อมูลไม่หาย)",
            items = lines.Select(l => (object)new
            {
                id = l.Row.Id, resource = l.Row.ResourceKey, label = QuotaResources.Find(l.Row.ResourceKey)?.Label ?? l.Row.ResourceKey,
                currentUnits = l.Row.Units, renewUnits = l.Units, renew = l.Row.RenewWithPlan, perUnit = l.PerUnit, amount = l.Amount,
                locked = l.Row.RenewalInvoiceDocumentId != null, offerAvailable = l.OfferAvailable,
            }).ToList(),
        };
    }

    public async Task<QuotaRecurringBillingResult> BillRecurringAsync(string period, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.AddOnMonthlyBilling, "quota:" + period) }, ct);

        // สถานะ: Active ที่เลยวันสิ้นสุดแล้ว ⇒ Expired (สุขอนามัยของรายการ — สิทธิ์ตัดสินด้วย IsLive อยู่แล้ว ไม่ขึ้นกับงานนี้)
        var ended = await _db.CompanyQuotaAddOns
            .Where(p => p.Status == QuotaAddOnStatus.Active && p.PeriodEndUtc != null && p.PeriodEndUtc <= now).ToListAsync(ct);
        foreach (var e in ended) { e.Status = QuotaAddOnStatus.Expired; e.UpdatedAt = now; e.UpdatedBy = "system:quota-addon"; }
        // ยังไม่ได้รับเงินค้างเกิน 14 วัน (รอชำระ · ถูกปฏิเสธ · สลิปรอตรวจ) ⇒ ยกเลิก (ยังไม่ได้ให้สิทธิ์ · คืนช่องเพดานซื้อ) — ไม่แตะรายการที่มีเงินออนไลน์ค้าง/เข้าแล้ว ·
        // สลิปรอตรวจที่ถูกยกเลิก = เรื่องค้างของแอดมิน (ตรวจเงินโอน/คืนเงิน) + audit — ไม่เงียบ
        var stale = await _db.CompanyQuotaAddOns
            .Where(p => (p.Status == QuotaAddOnStatus.AwaitingPayment || p.Status == QuotaAddOnStatus.Rejected || p.Status == QuotaAddOnStatus.PendingReview)
                && p.CreatedAt < now.AddDays(-14)).ToListAsync(ct);
        foreach (var e in stale)
        {
            if (await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.SourceKind == PaymentSourceKind.QuotaAddOnPurchase && i.SourceId == e.Id
                    && (i.Status == PaymentIntentStatus.Pending || i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Succeeded), ct))
                continue;
            var hadSlip = e.Status == QuotaAddOnStatus.PendingReview;
            e.Status = QuotaAddOnStatus.Cancelled; e.UpdatedAt = now; e.UpdatedBy = "system:quota-addon";
            if (hadSlip)
            {
                e.AttentionRule = "QUOTA-SLIP-UNREVIEWED";
                e.AttentionReason = "สลิปค้างตรวจเกิน 14 วัน — ยกเลิกรายการ (ยังไม่ได้ให้สิทธิ์) · ตรวจเงินโอนแล้วคืนเงินหรือให้ลูกค้าสร้างรายการใหม่";
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = e.CompanyId, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaAddOn), EntityId = e.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaAddOnUnpaidExpired", hadSlip, by = "system:quota-addon" }),
                Timestamp = now,
            });
        }
        await _db.SaveChangesAsync(ct);

        var rows = await _db.CompanyQuotaAddOns
            .Where(p => p.Status == QuotaAddOnStatus.Active && p.Mode == QuotaAddOnMode.RecurringMonthly
                && (p.LastBilledPeriod == null || p.LastBilledPeriod.CompareTo(period) < 0))
            .ToListAsync(ct);
        int billed = 0, skipped = 0; decimal total = 0m;
        var activeCache = new Dictionary<Guid, bool>();
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            if (!QuotaLimitPolicy.IsLive(row.Status, row.PeriodStartUtc, row.PeriodEndUtc, now)) continue;
            if (!activeCache.TryGetValue(row.CompanyId, out var planActive))
            {
                planActive = (await _subscriptions.GetEffectivePlanAsync(row.CompanyId))?.IsActive == true;
                activeCache[row.CompanyId] = planActive;
            }
            // แพ็กเกจไม่ active ⇒ ไม่มีบริการ ⇒ ไม่คิดค่าเดือน (ไม่ประทับงวด — ต่ออายุแล้วคิดได้ตามปกติ) · ตัวตัดสินเดียวกับค่าเหมาส่วนเสริม
            if (!planActive) { skipped++; continue; }
            var account = await _db.Companies.AsNoTracking().Where(c => c.Id == row.CompanyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync(ct);
            var acct = account is Guid aid ? await _db.BillingAccounts.FirstOrDefaultAsync(a => a.Id == aid, ct) : null;
            var amount = QuotaAddOnPricing.RecurringMonthAmount(row.Units, row.MonthlyUnitPriceSnapshot);
            var sandbox = acct?.IsSandbox == true;
            if (sandbox) amount = 0m;
            var ev = new UsageEvent
            {
                CompanyId = row.CompanyId, BillingAccountId = account, FeatureCode = QuotaResources.OfferCode(row.ResourceKey),
                Quantity = row.Units, UnitPriceSnapshot = QuotaAddOnPricing.R2(row.MonthlyUnitPriceSnapshot), ChargedAmount = amount,
                IsSandbox = sandbox, IdempotencyKey = $"qadd:{row.Id:N}:{period}", RefEntityType = nameof(CompanyQuotaAddOn), RefEntityId = row.Id,
                UnitLabelSnapshot = row.UnitSize > 1 ? $"{row.UnitSize:N0} {QuotaResources.Find(row.ResourceKey)?.Unit}" : QuotaResources.Find(row.ResourceKey)?.Unit,
                OccurredAt = now,
            };
            _db.UsageEvents.Add(ev);
            row.LastBilledPeriod = period;
            if (amount > 0 && acct is { PaymentModel: PaymentModel.Prepaid }) acct.CreditBalance -= amount;
            // ฝ่ายค้าน S4: savepoint ต่อแถว — แถวหนึ่งชน unique (instance อื่นออกไปแล้ว) ต้องไม่ทำให้ธุรกรรมทั้งงวด abort (PostgreSQL: error ⇒ ทั้ง tx ใช้ไม่ได้)
            var sp = "qadd_" + row.Id.ToString("N")[..12];
            await tx.CreateSavepointAsync(sp, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                billed++; total += amount;
            }
            catch (DbUpdateException)
            {
                // คีย์งวดซ้ำ — ไม่ใช่ error · ถอยกลับเฉพาะแถวนี้
                await tx.RollbackToSavepointAsync(sp, ct);
                _db.Entry(ev).State = EntityState.Detached;
                if (acct != null) await _db.Entry(acct).ReloadAsync(ct);
                await _db.Entry(row).ReloadAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return new QuotaRecurringBillingResult(billed, total, ended.Count, skipped);
    }
}

/// <summary>รอบ 229 (QUOTA_ADDON §B.6): บรรทัดต่ออายุโควตาเสริมแบบ "ถึงวันหมดอายุ" พร้อมแพ็กเกจ — static (ใช้ DbContext ตรง) เพราะผู้เรียกอยู่ลึกใน DI
/// (<c>SaasBillingDocumentService.GenerateRenewalInvoiceAsync</c> ออกใบ · <c>SubscriptionService.ReviewPaymentAsync</c> ตัดชำระแล้วสร้างรอบใหม่) · ทุกคำสั่งกรอง CompanyId</summary>
public static class QuotaRenewalLines
{
    public sealed record Line(CompanyQuotaAddOn Row, int Units, decimal PerUnit, decimal Amount, bool OfferAvailable, string Description);

    /// <summary>รายการที่หมดพร้อมรอบนี้ (PeriodEnd ≈ วันหมดอายุแพ็กเกจ ±1 วัน) · <paramref name="includeOptedOut"/> = รวมที่ไม่ติ๊ก (หน้าต่ออายุ) ·
    /// ข้อเสนอถูกปิด/ไม่มีราคา ⇒ ต่อไม่ได้ (หมดพร้อมรอบ — ข้อมูลไม่หาย)</summary>
    public static async Task<List<Line>> CandidatesAsync(AccountingDbContext db, Guid companyId, DateTime planEndUtc, int cycleMonths, bool includeOptedOut,
        CancellationToken ct = default)
    {
        var from = planEndUtc.AddDays(-1); var to = planEndUtc.AddDays(1);
        var rows = await db.CompanyQuotaAddOns
            .Where(p => p.CompanyId == companyId && p.Mode == QuotaAddOnMode.Coterm && p.Status == QuotaAddOnStatus.Active
                && p.PeriodEndUtc != null && p.PeriodEndUtc >= from && p.PeriodEndUtc <= to
                && (includeOptedOut || p.RenewWithPlan))
            .OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var offerIds = rows.Select(r => r.OfferId).Distinct().ToList();
        var offers = await db.QuotaAddOnOffers.AsNoTracking().Where(o => offerIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var list = new List<Line>();
        foreach (var r in rows)
        {
            offers.TryGetValue(r.OfferId, out var o);
            var available = o is { IsActive: true } && o.MonthlyUnitPrice > 0m;
            var units = r.RenewUnits ?? r.Units;
            var (per, amount) = available
                ? QuotaAddOnPricing.RenewalLine(new QuotaAddOnPricing.Offer(o!.ResourceKey, o.MonthlyUnitPrice, o.AnnualUnitPrice, o.AllowedModes,
                    o.MinChargeOneMonth, o.ValidDays, o.UnitSize, o.MaxUnitsPerCompany), units, cycleMonths)
                : (0m, 0m);
            var res = QuotaResources.Find(r.ResourceKey);
            var desc = $"โควตาเสริม {res?.Label ?? r.ResourceKey} ต่ออายุพร้อมแพ็กเกจ ({cycleMonths} เดือน) · หน่วยละ {per:N2} บาท";
            list.Add(new Line(r, units, per, amount, available, desc));
        }
        return list;
    }

    /// <summary>ใบต่ออายุถูกตัดชำระ (อนุมัติรายการชำระที่ผูกใบนี้) ⇒ สร้างรอบใหม่ของทุกบรรทัดที่อยู่ในใบ — idempotent (unique RenewedFromId) ·
    /// <paramref name="newEndUtc"/> = วันหมดอายุแพ็กเกจหลังต่อ</summary>
    public static async Task<int> ActivateForInvoiceAsync(AccountingDbContext db, Guid companyId, Guid renewalInvoiceId, DateTime newEndUtc, string actor,
        CancellationToken ct = default, bool save = true)
    {
        var rows = await db.CompanyQuotaAddOns
            .Where(p => p.CompanyId == companyId && p.RenewalInvoiceDocumentId == renewalInvoiceId).ToListAsync(ct);
        var created = 0;
        foreach (var r in rows)
        {
            if (await db.CompanyQuotaAddOns.AnyAsync(x => x.CompanyId == companyId && x.RenewedFromId == r.Id, ct)
                || db.ChangeTracker.Entries<CompanyQuotaAddOn>().Any(e => e.State == EntityState.Added && e.Entity.RenewedFromId == r.Id)) continue;
            var start = r.PeriodEndUtc ?? DateTime.UtcNow;
            if (newEndUtc <= start) continue;   // ต่ออายุไม่ได้ขยายรอบ (ไม่ควรเกิด) — ไม่สร้างแถวที่ไม่มีช่วงสิทธิ์
            var units = r.RenewUnits ?? r.Units;
            var per = r.RenewUnitPrice ?? r.UnitPriceSnapshot;
            db.CompanyQuotaAddOns.Add(new CompanyQuotaAddOn
            {
                CompanyId = companyId, OfferId = r.OfferId, ResourceKey = r.ResourceKey, Units = units, UnitSize = r.UnitSize,
                Mode = QuotaAddOnMode.Coterm, PeriodStartUtc = start, PeriodEndUtc = newEndUtc, Status = QuotaAddOnStatus.Active,
                RenewWithPlan = true, RenewedFromId = r.Id, UnitPriceSnapshot = per, MonthlyUnitPriceSnapshot = r.MonthlyUnitPriceSnapshot,
                AmountDue = QuotaAddOnPricing.R2(units * per), VatIncluded = r.VatIncluded,
                ProrationNote = "ต่ออายุพร้อมแพ็กเกจ (ชำระในใบแจ้งหนี้ต่ออายุ)", ReviewedAt = DateTime.UtcNow, ReviewedBy = actor, CreatedBy = actor,
            });
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, Action = AuditAction.Create, EntityType = nameof(CompanyQuotaAddOn), EntityId = r.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "QuotaAddOnRenewedWithPlan", invoiceId = renewalInvoiceId, units, perUnit = per, newEndUtc, by = actor }),
                Timestamp = DateTime.UtcNow,
            });
            created++;
        }
        // save:false = ผู้เรียกบันทึกพร้อมการอนุมัติในธุรกรรมที่ล็อกไว้ (ฝ่ายค้าน S3 — ต่ออายุแพ็กเกจกับโควตาเสริมสำเร็จ/ล้มด้วยกัน)
        if (created > 0 && save) await db.SaveChangesAsync(ct);
        return created;
    }
}
