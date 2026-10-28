using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ขายคอร์สผ่านร้านออนไลน์ (รอบ 205 ทีม W6 · R-41 B2 · COURSE_CREDIT_DESIGN §5 B2 · คำตัดสินข้อ 161: ไม่ขายที่ POS — ขายที่หน้า "คอร์ส &amp; เครดิต"/ร้านออนไลน์) —
/// สินค้าบนเว็บที่ผูกแพ็กเกจคอร์ส (<c>SiteProduct.CreditPackageId</c>) ⇒ บรรทัดใบขายของออเดอร์ลงผัง<b>ค่าบริการรับล่วงหน้า</b> (21713) แทนรายได้ ·
/// ใบอนุมัติแล้ว ⇒ <c>IssuedDocumentHooks</c> เปิดบัตรผ่าน <c>ICourseCreditService.ActivateFromDocumentAsync</c> (จุดต่อเดิม · ใบเดียว = บัตรเดียว)
///
/// <para><b>จุดเชื่อม (ไม่แตะ <c>CmsCommerceService.ConfirmPaymentAsync</c> ของทีม U):</b> (1) <c>CmsCommerceService.SyncOrderToErpCoreAsync</c> เรียก
/// <see cref="UnearnedAccountsAsync"/> แล้วส่งเข้า <c>BuildOrderErpLines</c> (บรรทัดคอร์สได้ AccountId) · (2) <c>IssuedDocumentHooks.RunAsync</c> (จุดเดียวหลังอนุมัติทุกใบ —
/// ทั้งเส้น gateway และยืนยันสลิปโดยพนักงาน) เรียก <see cref="Judge"/> แล้วเปิดบัตร · ล้ม ⇒ ป้าย <c>[COURSE-PASS-FAILED]</c> บนใบ + log (ไม่กลืน)</para>
/// </summary>
public static class CourseCreditOnlineSale
{
    public const string RuleCode = "TFRS-NPAE-6-CREDIT-ONLINE";
    public const string FailedMarker = "[COURSE-PASS-FAILED]";
    public const string Actor = "storefront-order";

    public enum Verdict { None = 0, Activate = 1, TooMany = 2, AlreadyOpen = 3 }

    /// <summary>ออเดอร์นี้ต้องเปิดบัตรไหม — <paramref name="courseUnits"/> = Σ จำนวนของบรรทัดที่ผูกแพ็กเกจคอร์ส · ใบเดียวเปิดได้บัตรเดียว ⇒ มากกว่า 1 หน่วย = TooMany (ไม่เปิดเอง · ป้ายบนใบ)</summary>
    public static Verdict Judge(decimal courseUnits, bool passAlreadyOpen)
    {
        if (courseUnits <= 0m) return Verdict.None;
        if (passAlreadyOpen) return Verdict.AlreadyOpen;
        return courseUnits == 1m ? Verdict.Activate : Verdict.TooMany;
    }

    /// <summary>สินค้าที่ผูกแพ็กเกจคอร์สสั่งได้ครั้งละ 1 (ใบขายหนึ่งใบ = บัตรหนึ่งใบ) — บังคับที่ค่าตั้งสินค้าตอนบันทึก</summary>
    public static int? MaxOrderQuantityFor(Guid? creditPackageId, int? requested)
        => creditPackageId is Guid ? 1 : requested;

    public static string TooManyNote(string orderNumber, decimal courseUnits)
        => $"{FailedMarker} ออเดอร์ {orderNumber} มีคอร์ส {courseUnits:0.##} หน่วยในใบเดียว — ระบบเปิดบัตรอัตโนมัติได้เฉพาะใบที่มีคอร์ส 1 หน่วย (ใบเดียว = บัตรเดียว) · "
           + "ทางไปต่อ: เปิดบัตรใบแรกด้วยปุ่ม \"เปิดบัตรจากเอกสารขาย\" ที่หน้า \"คอร์ส & เครดิต\" แล้วขายคอร์สที่เหลือจากหน้านั้น (ออกใบใหม่) และออกใบลดหนี้ส่วนที่เกินจากใบนี้";

    public static string FailureNote(string orderNumber, string reason)
        => $"{FailedMarker} ออเดอร์ {orderNumber}: เปิดบัตรคอร์สอัตโนมัติไม่สำเร็จ — {reason} · ทางไปต่อ: ปุ่ม \"เปิดบัตรจากเอกสารขาย\" ที่หน้า \"คอร์ส & เครดิต\" (อย่าขายซ้ำ)";

    /// <summary>ผังค่าบริการรับล่วงหน้าต่อบรรทัดคอร์สของออเดอร์ (key = SiteProductId) — ผังของแพ็กเกจ · ไม่ระบุ ⇒ 21713 ผ่าน <see cref="DeferredRevenueAccount.Pick"/> ·
    /// ไม่พบ ⇒ ล้มดัง (ใบขายไม่ถูกสร้าง — ConfirmPaymentAsync ปักหมายเหตุบนออเดอร์ตามเส้นเดิม) · ออเดอร์ที่ไม่มีคอร์ส ⇒ ว่าง (เส้นเดิมทุกตัวอักษร)</summary>
    public static async Task<IReadOnlyDictionary<Guid, Guid>> UnearnedAccountsAsync(AccountingDbContext db, Guid companyId, SiteOrder order, CancellationToken ct = default)
    {
        var courseLines = order.Lines
            .Where(l => l.SiteProduct?.CreditPackageId != null)
            .Select(l => (l.SiteProductId, PackageId: l.SiteProduct!.CreditPackageId!.Value))
            .Distinct().ToList();
        var result = new Dictionary<Guid, Guid>();
        if (courseLines.Count == 0) return result;

        var pkgIds = courseLines.Select(c => c.PackageId).Distinct().ToList();
        var pkgAccounts = await db.CreditPackages.AsNoTracking()
            .Where(p => p.CompanyId == companyId && pkgIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.UnearnedAccountId, ct);
        Guid? defaultUnearned = null;
        if (courseLines.Any(c => !pkgAccounts.TryGetValue(c.PackageId, out var a) || a == null))
        {
            var postable = await db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.AccountType == AccountType.Liability
                            && a.AccountCode.StartsWith(DeferredRevenueAccount.GroupPrefix))
                .Select(a => new DeferredRevenueAccount.Candidate(a.Id, a.AccountCode, a.AccountType))
                .ToListAsync(ct);
            defaultUnearned = DeferredRevenueAccount.Pick(postable)
                ?? throw new BusinessRuleException(
                    "ออเดอร์มีสินค้าคอร์ส แต่" + DeferredRevenueAccount.MissingMessage(DeferredRevenueAccount.GroupCandidateCount(postable))
                    + " หรือระบุผังค่าบริการรับล่วงหน้าในแพ็กเกจคอร์ส", RuleCode);
        }
        foreach (var c in courseLines)
        {
            if (!pkgAccounts.ContainsKey(c.PackageId))
                throw new BusinessRuleException($"สินค้าในออเดอร์ผูกแพ็กเกจคอร์สที่ไม่พบในบริษัทนี้ ({c.PackageId}) — แก้ค่าตั้งสินค้าบนเว็บไซต์ก่อน", RuleCode);
            result[c.SiteProductId] = pkgAccounts[c.PackageId] ?? defaultUnearned!.Value;
        }
        return result;
    }
}
