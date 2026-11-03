using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §3.2 "ศูนย์บิลของผู้เช่า" · คำตัดสินข้อ 180) — ทุกอย่างที่หน้า <c>/pages/billing.html</c> แสดง ประกอบที่เซิร์ฟเวอร์:
/// แพ็กเกจปัจจุบัน/รอบถัดไป/การเปลี่ยนที่รอมีผล · ใบแจ้งหนี้ค่าบริการ (tenant ผู้ให้บริการ · ตัวหาเดียว <see cref="PlatformInvoiceQueries"/>) พร้อม "จ่ายได้ไหม/ทำไม" ·
/// ประวัติการชำระ + ใบเสร็จ · ส่วนเสริมพร้อมสถานะ · ช่องทางชำระ + นโยบายที่ต้องติ๊ก (<c>PlatformPayInfo.BuildAsync</c> ของ part B — ตัวเดียวกับหน้าแพ็กเกจ/ส่วนเสริม) ·
/// นโยบายระงับ (<see cref="PlatformBillingSettingsRules.DescribeSuspension"/>)
/// </summary>
public interface IPlatformBillingCenterService
{
    Task<BillingCenterSummary> GetSummaryAsync(Guid companyId, bool canPay, bool canManagePlan, CancellationToken ct = default);
}

public class PlatformBillingCenterService : IPlatformBillingCenterService
{
    private const int InvoiceTake = 60, PaymentTake = 60, LineTake = 5, ReceiptTake = 60;
    private readonly AccountingDbContext _db;
    private readonly Accounting.Services.Payments.IPlatformPaymentService _platform;
    private readonly ILegalDocumentService _legal;
    private readonly ISubscriptionGateShadowLog _gate;
    private readonly IConfiguration _config;

    public PlatformBillingCenterService(AccountingDbContext db, Accounting.Services.Payments.IPlatformPaymentService platform,
        ILegalDocumentService legal, ISubscriptionGateShadowLog gate, IConfiguration config)
    { _db = db; _platform = platform; _legal = legal; _gate = gate; _config = config; }

    public async Task<BillingCenterSummary> GetSummaryAsync(Guid companyId, bool canPay, bool canManagePlan, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบ subscription");
        var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var mode = SubscriptionEnforcementResolver.Resolve(await _gate.ReadAdminSwitchAsync(ct), _config).EffectiveMode;
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);

        // ── แพ็กเกจ ──
        var planName = await _db.PlanTemplates.AsNoTracking().Where(t => t.Plan == sub.Plan && t.IsActive)
            .Select(t => t.Name).FirstOrDefaultAsync(ct);
        var renewalIssued = sub.RenewalInvoiceForEndDate == sub.EndDate && sub.RenewalInvoiceNumber != null;
        // รอบ 232: วันหมดอายุ/วันออกใบต่ออายุ/"ไม่หมดอายุ" ผ่าน SubscriptionExpiry ตัวเดียว — ค่าแทน ค.ศ. 2126 (ธงฟรีถาวรหลุด) เคยหลุดถึงจอเป็น "2 เม.ย. 2669"
        var openEnded = SubscriptionExpiry.IsOpenEnded(sub.IsPermanentFree, sub.EndDate, now);
        var nextRenewalAt = SubscriptionExpiry.NextRenewalInvoiceAt(sub.IsPermanentFree, sub.EndDate, now,
            isActive: sub.Status == SubscriptionStatus.Active, renewalAlreadyIssued: renewalIssued, cancelAtPeriodEnd: sub.CancelAtPeriodEnd,
            leadDays: policy.RenewalInvoiceLeadDays);
        var plan = new BillingCenterPlan(sub.Plan.ToString(), planName, sub.Status.ToString(), StatusLabel(sub.Status),
            sub.BillingCycle.ToString(), sub.PricePerCycle, SubscriptionExpiry.DisplayEnd(sub.IsPermanentFree, sub.EndDate, now), sub.IsPermanentFree,
            nextRenewalAt, sub.ScheduledPlan?.ToString(), sub.ScheduledPlanEffectiveAt,
            sub.CancelAtPeriodEnd, sub.CancelAtPeriodEnd && !openEnded ? sub.EndDate : null,
            !sub.CancelAtPeriodEnd && sub.Status is SubscriptionStatus.Active or SubscriptionStatus.Trial or SubscriptionStatus.PastDue,
            SubscriptionCancelPolicy.ResumeRefusal(sub.CancelAtPeriodEnd, sub.Status, sub.EndDate, now) == null,
            sub.CancelAtPeriodEnd ? SubscriptionCancelPolicy.Message(SubscriptionCancelPolicy.Outcome.AtPeriodEnd, sub.EndDate) : null,
            IsOpenEnded: openEnded);

        // ── ใบแจ้งหนี้ ──
        var invoices = platformId is Guid pid
            ? await PlatformInvoiceQueries.ForBuyer(_db, pid, companyId)
                .OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.DocumentNumber)
                .Take(InvoiceTake).AsRows(companyId).ToListAsync(ct)
            : new List<PlatformInvoiceRow>();
        var invIds = invoices.Select(i => i.Id).ToList();
        var pendingByInvoice = (await _db.SubscriptionPayments.AsNoTracking()
                .Where(p => p.SubscriptionId == sub.Id && p.PlatformInvoiceDocumentId != null && invIds.Contains(p.PlatformInvoiceDocumentId.Value)
                    && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview
                        || p.Status == SubscriptionPaymentStatus.Approved))
                // ฝ่ายค้าน B2 + รอบสุดท้าย 3: "จ่ายแล้ว รอฝั่งเรา" (สลิป · พัก · อนุมัติแล้วรอตัดชำระ) = ตัวตัดสินเดียว PlatformInvoiceClaims — ห้ามจ่ายซ้ำ
                .Select(p => new { InvoiceId = p.PlatformInvoiceDocumentId!.Value, p.PaymentNumber, p.CreatedAt,
                    HasSlip = PlatformInvoiceClaims.IsAwaitingPlatform(p.Status, true, p.SlipFileName != null, p.AttentionReason != null, p.PlatformDocumentId != null),
                    Open = p.Status != SubscriptionPaymentStatus.Approved || p.PlatformDocumentId == null })
                .ToListAsync(ct))
            .Where(p => p.Open)
            .GroupBy(p => p.InvoiceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First());

        var invoiceDtos = new List<BillingCenterInvoice>();
        foreach (var inv in invoices)
        {
            var state = PlatformInvoiceRules.StateOf(inv.Status, inv.BalanceDue, inv.DueDate, now);
            var late = PlatformInvoiceRules.DaysPastDue(inv.DueDate, now);
            var kind = PlatformInvoiceRules.KindOf(inv.Reference);
            var open = PlatformInvoiceRules.IsOpen(inv.Status, inv.BalanceDue);
            pendingByInvoice.TryGetValue(inv.Id, out var pending);
            var reason = inv.Id == sub.RenewalInvoiceDocumentId && open
                    && PlatformInvoiceRules.RenewalNotPayableReason(sub.CancelAtPeriodEnd) is string cancelled
                ? cancelled
                // ฝ่ายค้าน N6: ใบต่ออายุที่ถูกแทนที่ — ตัวตัดสินเดียวกับด่านแจ้งชำระ
                : open && PlatformInvoiceRules.SupersededRenewalReason(inv.Reference, inv.Id, sub.RenewalInvoiceDocumentId) is string superseded
                ? superseded
                : PayRefusal(open, canPay, pending?.HasSlip == true, pending?.PaymentNumber);
            var lines = open && platformId is Guid p2
                ? (await PlatformInvoiceQueries.LineSummaryAsync(_db, p2, inv.Id, LineTake, ct))
                    .Select(l => $"{l.Description} — {l.Amount:N2}").ToList()
                : new List<string>();
            invoiceDtos.Add(new BillingCenterInvoice(inv.Id, inv.DocumentNumber, kind.ToString(), PlatformInvoiceRules.KindLabel(kind),
                inv.DocumentDate, inv.DueDate, inv.TotalAmount, open ? inv.BalanceDue : 0m, state.ToString(),
                PlatformInvoiceRules.StateLabel(state, late), open ? late : null, reason == null, reason,
                pending?.PaymentNumber, lines));
        }
        var openList = invoiceDtos.Where(i => i.BalanceDue > 0m).ToList();

        // ── ประวัติการชำระ ──
        var payments = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.SubscriptionId == sub.Id)
            .OrderByDescending(p => p.CreatedAt).Take(PaymentTake)
            .ToListAsync(ct);
        var payInvIds = payments.Where(p => p.PlatformInvoiceDocumentId != null).Select(p => p.PlatformInvoiceDocumentId!.Value)
            .Except(invIds).Distinct().ToList();
        var invNumbers = invoices.ToDictionary(i => i.Id, i => i.DocumentNumber);
        if (payInvIds.Count > 0 && platformId is Guid p3)
            foreach (var x in await _db.Documents.AsNoTracking().Where(d => d.CompanyId == p3 && payInvIds.Contains(d.Id))
                         .Select(d => new { d.Id, d.DocumentNumber }).ToListAsync(ct))
                invNumbers[x.Id] = x.DocumentNumber;
        var paymentDtos = payments.Select(p =>
        {
            string? invNo = p.PlatformInvoiceDocumentId is Guid g && invNumbers.TryGetValue(g, out var n) ? n : null;
            return new BillingCenterPayment(p.Id, p.PaymentNumber, p.Amount, p.PaymentDate, p.Status.ToString(),
                PaymentStatusLabel(p.Status, p.SlipFileName != null),
                // รอบ 230: ส่วนต่างอัปเกรดกลางรอบ — บอกตามจริง (ไม่ใช่ "แพ็กเกจ 0 เดือน")
                p.IsProration
                    ? $"ส่วนต่างอัปเกรด {p.ProrationFromPlan} → {p.RequestedPlan} ({p.ProrationDays}/{p.ProrationCycleDays} วัน)"
                    // รอบ 231 (ฝ่ายค้าน S1): รายการเปลี่ยนรอบบิล — บอกเครดิตที่หักและรอบใหม่นับจากวันชำระ
                    : p.IsCycleChange
                    ? $"เปลี่ยนรอบบิลเป็นราย{Accounting.Helpers.PlatformPlanPricing.CycleLabel(p.RequestedBillingCycle)} {p.RequestedPlan} (หักมูลค่าคงเหลือ {p.CycleChangeCredit ?? 0m:N2} · {p.ProrationDays}/{p.ProrationCycleDays} วัน · รอบใหม่นับจากวันชำระ)"
                      + (p.StandardAmount is decimal sa ? $" · ราคาพิเศษ (ราคาตั้ง {sa:N2})" : "")
                    : PaymentPurpose(p.PlatformInvoiceDocumentId != null, invNo, p.Kind, p.RequestedPlan, p.RequestedPeriodMonths),
                invNo, p.ReceiptNumber, p.ReceiptIsTaxInvoice, p.ReceiptNumber != null, p.SlipFileName != null,
                p.Status == SubscriptionPaymentStatus.Rejected ? p.RejectionReason : null,
                p.WithholdingTaxAmount > 0m && p.WhtCertificateFileName == null
                    && p.Status is not (SubscriptionPaymentStatus.Rejected or SubscriptionPaymentStatus.Cancelled),
                p.WithholdingTaxAmount,
                canPay && p.Status == SubscriptionPaymentStatus.Pending && p.SlipFileName == null && p.PlatformInvoiceDocumentId == null && p.Amount > 0m,
                p.CouponDiscountAmount, p.CouponCode);
        }).ToList();

        // ── ส่วนเสริม ──
        var features = await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted && (f.IsEnabled || f.PaymentStatus != AddOnPaymentStatus.NotRequired))
            .ToListAsync(ct);
        var codes = features.Select(f => f.FeatureCode).ToList();
        var names = await _db.ApiFeatures.AsNoTracking().Where(f => codes.Contains(f.FeatureCode))
            .ToDictionaryAsync(f => f.FeatureCode, f => f.Name, ct);
        var addOns = features.Where(f => f.IsEnabled).Select(f => new BillingCenterAddOn(
            f.FeatureCode, names.GetValueOrDefault(f.FeatureCode, f.FeatureCode),
            f.PaymentStatus.ToString(), AddOnPaymentPolicy.StatusLabel(f.PaymentStatus),
            f.BillingChoice.ToString(), AddOnPaymentPolicy.ChoiceLabel(f.BillingChoice),
            f.TrialUntil > now ? f.TrialUntil : null,
            f.PaymentStatus == AddOnPaymentStatus.AwaitingPayment ? AddOnPaymentPolicy.GraceEndsAt(f.PaymentAwaitingSince) : null,
            AddOnPaymentPolicy.UsableWhilePending(f.PaymentStatus, f.PaymentAwaitingSince, now),
            f.SnapshotUnitPrice ?? f.AcceptedUnitPrice,
            f.PaymentReceiptDocumentId, f.PaymentReceiptNumber)).ToList();

        // ── ใบเสร็จ/ใบกำกับ/ใบลดหนี้ค่าบริการ (รอบ 228 §5.7) — ผู้ซื้อ = บริษัทนี้ (ผู้ติดต่อผูก tenant) · ล่าสุดก่อน ──
        var receipts = platformId is Guid p4
            ? (await PlatformInvoiceQueries.PaidDocumentsForBuyer(_db, p4, companyId)
                    .OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.DocumentNumber).Take(ReceiptTake)
                    .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount, d.Reference, d.Status })
                    .ToListAsync(ct))
                .Select(d => new BillingCenterReceipt(d.Id, d.DocumentNumber, d.DocumentType.ToString(), ReceiptTypeLabel(d.DocumentType),
                    d.DocumentDate, d.TotalAmount, d.Reference, d.Status == DocumentStatus.Voided))
                .ToList()
            : null;

        var pay = await Accounting.Controllers.PlatformPayInfo.BuildAsync(_db, _platform, _legal, ct);
        return new BillingCenterSummary(
            platformId != null,
            platformId == null
                ? "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ใบแจ้งหนี้ต่ออายุแบบ PDF ดาวน์โหลดและแจ้งชำระได้ที่หน้าแพ็กเกจ"
                : null,
            plan,
            openList.Sum(i => i.BalanceDue),
            openList.Where(i => i.State == nameof(PlatformInvoiceState.Overdue)).Sum(i => i.BalanceDue),
            openList.Count(i => i.State == nameof(PlatformInvoiceState.Overdue)),
            invoiceDtos, paymentDtos, addOns,
            pay,
            PlatformBillingSettingsRules.DescribeSuspension(policy, mode == SubscriptionEnforcementMode.Enforce),
            canPay, canManagePlan, receipts);
    }

    /// <summary>ป้ายชนิดเอกสารที่ผู้เช่าได้รับ (ตัวเดียวของศูนย์บิล)</summary>
    public static string ReceiptTypeLabel(DocumentType t) => t switch
    {
        DocumentType.TaxInvoice => "ใบกำกับภาษี/ใบเสร็จรับเงิน",
        DocumentType.Receipt => "ใบเสร็จรับเงิน",
        DocumentType.CreditNote => "ใบลดหนี้",
        _ => t.ToString(),
    };

    /// <summary>เหตุที่จ่ายใบนี้ไม่ได้ (null = จ่ายได้) — ตัวเดียวที่หน้าเว็บอ่าน</summary>
    internal static string? PayRefusal(bool open, bool canPay, bool pendingWithSlip, string? pendingNumber)
    {
        if (!open) return "ไม่มียอดค้าง";
        if (!canPay) return "ต้องได้รับสิทธิ์ \"จัดการค่าใช้จ่าย/ส่วนเสริม\" จากเจ้าของบริษัท";
        if (pendingWithSlip) return $"แจ้งชำระแล้ว ({pendingNumber}) — รอผู้ดูแลระบบตรวจสอบ";
        return null;
    }

    internal static string StatusLabel(SubscriptionStatus s) => s switch
    {
        SubscriptionStatus.Trial => "ทดลองใช้",
        SubscriptionStatus.Active => "ใช้งานอยู่",
        SubscriptionStatus.PastDue => "เกินกำหนดต่ออายุ",
        SubscriptionStatus.Cancelled => "ยกเลิกแล้ว",
        SubscriptionStatus.Expired => "หมดอายุ",
        SubscriptionStatus.Suspended => "ถูกระงับ",
        _ => s.ToString(),
    };

    internal static string PaymentStatusLabel(SubscriptionPaymentStatus s, bool hasSlip) => s switch
    {
        SubscriptionPaymentStatus.Pending => hasSlip ? "รอตรวจสอบ" : "รอแนบสลิป",
        SubscriptionPaymentStatus.UnderReview => "กำลังตรวจสอบ",
        SubscriptionPaymentStatus.Approved => "ยืนยันแล้ว",
        SubscriptionPaymentStatus.Rejected => "ไม่ผ่านการตรวจสอบ",
        SubscriptionPaymentStatus.Cancelled => "ยกเลิก",
        _ => s.ToString(),
    };

    internal static string PaymentPurpose(bool invoiceLinked, string? invoiceNumber, SubscriptionPaymentKind kind,
        SubscriptionPlan plan, int months)
    {
        if (invoiceLinked) return $"ชำระใบแจ้งหนี้ {invoiceNumber ?? ""}".Trim();
        if (kind == SubscriptionPaymentKind.Waived) return "ยกเว้นค่าบริการ";
        return months > 0 ? $"แพ็กเกจ {plan} ({months} เดือน)" : $"แพ็กเกจ {plan}";
    }
}
