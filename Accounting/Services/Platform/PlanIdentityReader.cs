using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Platform;

/// <summary>
/// รอบ 236 (คำตัดสิน 236-1…236-6) — ตัวอ่านข้อเท็จจริงจากฐานให้ตัวตัดสิน pure <see cref="PlanIdentityResolver"/> (แพ็กเกจที่มีผล · ที่มา · ซื้อเองได้ไหม) ·
/// static (รับ DbContext) เพื่อให้ทุกทางเข้าเรียกได้โดยไม่สร้างวงพึ่งพา DI (<c>SaasBillingDocumentService</c> ⇄ <c>SubscriptionService</c>)
/// <para>ผู้เรียก (ด่านเงิน — ล็อกด้วย <c>tools/required_call_site_check.py</c> R236): <c>SubscriptionService</c> (สร้างรายการชำระ/เปลี่ยนแพ็กเกจ/แจ้งชำระ/
/// แอดมินบันทึกรับเงิน + ใบเสนอ/preview) · <c>PublicCheckoutService</c> (ยอดตะกร้า/สร้างคำสั่งซื้อ/ดรอปดาวน์บริษัท) · <c>PlatformPaymentService</c> (เริ่มชำระออนไลน์) ·
/// <c>SaasBillingDocumentService.GenerateRenewalInvoiceAsync</c> (ไม่ออกใบต่ออายุให้แถวบริษัทที่ License คุมอยู่) · <c>AdminController</c> (ป้าย/แท็บ Subscription)</para>
/// </summary>
public static class PlanIdentityReader
{
    /// <summary>ตัวตนแพ็กเกจของบริษัท (null = ไม่มีแถว Subscription)</summary>
    public static async Task<PlanIdentityResolver.PlanIdentity?> ForCompanyAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted, ct);
        return sub == null ? null : await ForSubscriptionAsync(db, sub, ct);
    }

    /// <summary>ตัวตนแพ็กเกจจากแถว Subscription ที่โหลดแล้ว — License (ไม่ถูกลบ) ชนะ · ชื่อแพ็กเกจบริษัท = PlanTemplate ที่เปิดใช้ของระดับนั้น
    /// (ฟรีถาวรเลือกแม่แบบฟรีถาวรก่อน — FreeTrial ใช้ร่วมระหว่าง "ทดลองใช้" กับ "Free Edition")</summary>
    public static async Task<PlanIdentityResolver.PlanIdentity> ForSubscriptionAsync(AccountingDbContext db, Subscription sub, CancellationToken ct = default)
    {
        PlanIdentityResolver.LicenseSide? license = null;
        if (sub.AccountSubscriptionId is Guid aid)
        {
            var a = await db.AccountSubscriptions.AsNoTracking()
                .Where(x => x.Id == aid && !x.IsDeleted)
                .Select(x => new
                {
                    x.Id, x.Status, x.EndDate, x.GracePeriodDays, x.OwnerUserId,
                    Plan = x.PlanTemplate != null ? (SubscriptionPlan?)x.PlanTemplate.Plan : null,
                    PlanName = x.PlanTemplate != null ? x.PlanTemplate.Name : null,
                    OwnerName = x.Owner != null ? x.Owner.FullName : null,
                    OwnerEmail = x.Owner != null ? x.Owner.Email : null,
                })
                .FirstOrDefaultAsync(ct);
            if (a != null)
                license = new PlanIdentityResolver.LicenseSide(a.Plan ?? SubscriptionPlan.FreeTrial, a.PlanName, a.Status, a.EndDate,
                    a.Id, a.OwnerName, a.OwnerEmail, a.GracePeriodDays, a.OwnerUserId);
        }
        string? companyPlanName = null;
        if (license == null)
            companyPlanName = await db.PlanTemplates.AsNoTracking()
                .Where(t => t.Plan == sub.Plan && t.IsActive && !t.IsDeleted)
                .OrderByDescending(t => t.IsPermanentFree == sub.IsPermanentFree)
                .ThenBy(t => t.CreatedAt)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct);
        return PlanIdentityResolver.Resolve(sub.Plan, sub.Status, sub.EndDate, companyPlanName, license);
    }

    /// <summary>ผู้ทำรายการจากสตริง performedBy (ผู้เรียกในแอปส่ง UserId.ToString()) · ไม่ใช่ Guid (แอดมิน/ระบบ/ลิงก์) ⇒ null = ไม่ใช่ผู้ถือ License</summary>
    public static Guid? ActorOf(string? performedBy) => Guid.TryParse(performedBy, out var g) ? g : null;

    /// <summary>ผู้ทำรายการเป็นผู้ถือ License ของตัวตนนี้ไหม</summary>
    public static bool IsHolder(PlanIdentityResolver.PlanIdentity id, Guid? actorUserId)
        => actorUserId is Guid a && id.License?.OwnerUserId == a;

    /// <summary>ผลด่านซื้อของตัวตนที่โหลดแล้ว (ไม่ query ซ้ำ)</summary>
    public static PlanIdentityResolver.PurchaseVerdict Verdict(PlanIdentityResolver.PlanIdentity id, Guid? actorUserId, SubscriptionPlan? targetPlan)
        => PlanIdentityResolver.CompanyPurchase(id, DateTime.UtcNow, IsHolder(id, actorUserId), targetPlan);

    /// <summary>บริษัทซื้อ/ต่ออายุ/เปลี่ยนแพ็กเกจระดับบริษัทเองได้ไหม (ผลสำหรับหน้าเว็บ/preview — ไม่ throw) · รอบ 236 B1: ผู้ถือ License + แพ็กระดับ License ⇒ ยอม (ต่ออายุ License)</summary>
    public static async Task<PlanIdentityResolver.PurchaseVerdict> PurchaseVerdictAsync(AccountingDbContext db, Guid companyId,
        Guid? actorUserId = null, SubscriptionPlan? targetPlan = null, CancellationToken ct = default)
    {
        var id = await ForCompanyAsync(db, companyId, ct);
        return id == null ? PlanIdentityResolver.PurchaseVerdict.Ok : Verdict(id, actorUserId, targetPlan);
    }

    /// <summary><b>ด่านเงิน</b> — บริษัทที่ License คุมอยู่ ⇒ <see cref="BusinessRuleException"/> 409 (ข้อความไทย: License · ผู้ถือ · วันหมดอายุ · ทางไปต่อ) ·
    /// ทุกทางเข้าที่สร้างรายการชำระ/ใบต่ออายุ/เปลี่ยนแพ็กเกจระดับบริษัทต้องเรียกก่อนเขียนอะไร · คืนผล (ยอมแบบต่ออายุ License ⇒ <c>LicenseRenewal</c>)</summary>
    public static async Task<PlanIdentityResolver.PurchaseVerdict> EnsureCompanyMayPurchaseAsync(AccountingDbContext db, Guid companyId,
        Guid? actorUserId, SubscriptionPlan? targetPlan, CancellationToken ct = default)
    {
        var v = await PurchaseVerdictAsync(db, companyId, actorUserId, targetPlan, ct);
        if (!v.Allowed) throw new BusinessRuleException(RefusalMessage(v), PlanIdentityResolver.LicenseCoveredRuleCode, 409);
        return v;
    }

    /// <summary>งานระบบ (ใบต่ออายุ · ตัดบัตรอัตโนมัติ) ไม่มีผู้ทำรายการ ⇒ ถือว่า "ผู้จ่าย = ผู้ถือ License" เมื่อผู้ถือเป็นเจ้าของ (Owner) บริษัทนี้ ·
    /// แพ็กที่จะเรียกเก็บ = แพ็กที่ตั้งเปลี่ยนไว้ หรือแพ็กปัจจุบันของแถวบริษัท</summary>
    public static async Task<PlanIdentityResolver.PurchaseVerdict> SystemVerdictAsync(AccountingDbContext db, Subscription sub, CancellationToken ct = default)
    {
        var id = await ForSubscriptionAsync(db, sub, ct);
        if (!id.ViaLicense) return PlanIdentityResolver.PurchaseVerdict.Ok;
        var holder = id.License?.OwnerUserId;
        var holderOwnsCompany = holder is Guid h && await db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == sub.CompanyId && cu.UserId == h && cu.Role == UserRole.Owner, ct);
        return PlanIdentityResolver.CompanyPurchase(id, DateTime.UtcNow, holderOwnsCompany, sub.ScheduledPlan ?? sub.Plan);
    }

    /// <summary>รอบ 236 (ฝ่ายค้าน S2): งานเก็บเงินอัตโนมัติ (ตัดบัตร · ทวงหนี้) ต้องหยุดเรียกเก็บ<b>ค่าแพ็กเกจระดับบริษัท</b> (ใบต่ออายุ) ของบริษัทนี้ไหม —
    /// License คุมอยู่ และไม่ใช่กรณีต่ออายุ License (ผู้ถือเป็นเจ้าของบริษัท + แพ็กระดับ License) · ใบค่าใช้งาน/ส่วนเสริมไม่เกี่ยว (หนี้ตามการใช้จริง)</summary>
    public static async Task<bool> StopsCompanyPlanBillingAsync(AccountingDbContext db, Subscription sub, CancellationToken ct = default)
        => sub.AccountSubscriptionId != null && !(await SystemVerdictAsync(db, sub, ct)).Allowed;

    /// <summary>ข้อความเดียวของการปฏิเสธ (เหตุ → ทางไปต่อ)</summary>
    public static string RefusalMessage(PlanIdentityResolver.PurchaseVerdict v)
        => v.NextStep == null ? v.Reason ?? "" : $"{v.Reason} → {v.NextStep}";

    /// <summary>ชุด Subscription.Id ที่ License (ไม่ถูกลบ) คุมอยู่ — งานเตือนหมดอายุ/ใบต่ออายุข้ามแถวเหล่านี้ (แถวบริษัทไม่มีผล ⇒ ไม่ทวง "ใกล้หมดอายุ/ระงับ")</summary>
    public static async Task<HashSet<Guid>> LicenseGovernedSubscriptionIdsAsync(AccountingDbContext db, IEnumerable<Subscription> subs, CancellationToken ct = default)
    {
        var withLicense = subs.Where(s => s.AccountSubscriptionId != null).ToList();
        if (withLicense.Count == 0) return new HashSet<Guid>();
        var licenseIds = withLicense.Select(s => s.AccountSubscriptionId!.Value).Distinct().ToList();
        var live = (await db.AccountSubscriptions.AsNoTracking()
            .Where(a => licenseIds.Contains(a.Id) && !a.IsDeleted)
            .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        return withLicense.Where(s => live.Contains(s.AccountSubscriptionId!.Value)).Select(s => s.Id).ToHashSet();
    }
}
