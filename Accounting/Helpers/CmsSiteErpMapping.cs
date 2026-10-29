using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **สาขาของเอกสารที่เกิดจากเว็บ** — รอบ 209 P6 · คำตัดสินข้อ 168 ฐ · R-209-D3/E12
///
/// <para><b>ที่มา</b>: <c>Site.BranchId</c> ("เว็บนี้ขายในนามสาขาไหน") เคยถูกใช้แค่เขียนข้อความ <c>"Branch: {guid}"</c> ลง
/// <c>Document.InternalNotes</c> ⇒ ใบขาย/ใบมัดจำ/ใบส่วนที่เหลือ/ใบเสนอราคาจากออเดอร์และการจองออกในนามสำนักงานใหญ่เสมอ
/// (รหัสสาขาบนกระดาษ §86/4(2) + ที่อยู่ + รายงานภาษีขายรายสาขาผิด)</para>
///
/// <para><b>กติกา</b>: เว็บไม่ได้ผูกสาขา ⇒ <c>null</c> = พฤติกรรมเดิมทุกตัวอักษร (สำนักงานใหญ่) · ผูกแล้ว ⇒ ส่ง <c>BranchId</c> เข้า
/// <c>CreateDocumentRequest</c> (DocumentService ตรวจว่าเป็นสาขาของบริษัทนี้ + ตรึง <c>IssuerBranchCode</c> ตอนออกเลข) ·
/// สาขาที่ถูกลบหลังผูกเว็บ ⇒ <c>null</c> (ใบออกในนามสำนักงานใหญ่แทนการล้มทั้งการรับเงิน — หน้าตั้งค่าเว็บเตือนให้เลือกใหม่)</para>
///
/// <para><b>เลขที่เอกสาร</b>: <c>DocumentNumberGenerator</c> รันเลขต่อ (บริษัท, ชุดเอกสาร, งวด) — ไม่แยกต่อสาขา · ใบของทุกสาขาจึงต่อเนื่อง
/// ไม่มีช่องว่างในชุดเดียวกัน (gap-free ระดับบริษัทครอบระดับสาขา) และพฤติกรรมเดียวกับใบที่ออกจากหน้าเอกสารพร้อมเลือกสาขา ·
/// ใบเดิมที่ออกไปแล้วไม่ถูกแตะ (ห้ามแก้ย้อนหลัง) — มีผลกับใบใหม่เท่านั้น</para>
/// </summary>
public static class CmsDocumentBranch
{
    public const string RuleCode = "CMS-BRANCH";

    /// <summary>pure — สาขาที่ผูกไว้กับเว็บ ⇒ สาขาของเอกสาร (<c>null</c>/<c>Guid.Empty</c> ⇒ <c>null</c> = สำนักงานใหญ่เดิม)</summary>
    internal static Guid? FromSiteBranch(Guid? siteBranchId, bool branchUsable = true)
        => siteBranchId is Guid g && g != Guid.Empty && branchUsable ? g : null;

    /// <summary>สาขาของเอกสารที่เกิดจากเว็บ <paramref name="siteId"/> (กรองบริษัท · สาขายังไม่ถูกลบ)</summary>
    public static async Task<Guid?> ForSiteAsync(AccountingDbContext db, Guid companyId, Guid siteId, CancellationToken ct = default)
    {
        var branchId = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => s.BranchId).FirstOrDefaultAsync(ct);
        if (FromSiteBranch(branchId) is not Guid b) return null;
        var usable = await db.Branches.AsNoTracking().AnyAsync(x => x.Id == b && x.CompanyId == companyId && !x.IsDeleted, ct);
        return FromSiteBranch(b, usable);
    }
}

/// <summary>คลังที่เว็บตัดสต็อก — ผลของ <see cref="CmsSiteWarehouse.Decide"/></summary>
public enum CmsStockSource
{
    /// <summary>คลังหลักของบริษัท (ledger เลือกให้ · พฤติกรรมเดิม)</summary>
    CompanyDefault,
    /// <summary>คลังที่ตั้งไว้กับเว็บ (<c>Site.DefaultWarehouseId</c>)</summary>
    SiteWarehouse,
    /// <summary>คลังของสาขาที่เว็บผูก (<c>IStockLedger.ResolveWarehouseIdAsync</c> — ตัวแปลงสาขา→คลังตัวเดียวกับเอกสาร)</summary>
    BranchWarehouse,
}

/// <summary>
/// **คลังที่ออเดอร์เว็บตัด/คืนสต็อก** — รอบ 209 P6 · R-209-D3 (เดิม <c>DefaultWarehouseId</c>/<c>UseGlobalInventory</c> ไม่มีผู้อ่าน)
/// <list type="bullet">
/// <item><c>UseGlobalInventory = true</c> (ค่าเริ่มต้น) ⇒ คลังหลักของบริษัท = พฤติกรรมเดิมทุกออเดอร์</item>
/// <item><c>false</c> + ตั้งคลังของเว็บ ⇒ คลังนั้น (ต้องเป็นของบริษัทนี้ · ไม่ลบ · ใช้งานอยู่ — ไม่ผ่าน ⇒ คลังหลัก)</item>
/// <item><c>false</c> + ไม่ตั้งคลัง + ผูกสาขา ⇒ คลังของสาขา (ตัวแปลงเดียวของ ledger)</item>
/// </list>
/// คลังที่ตัดจริงถูกตรึงบน <c>SiteOrderLine.WarehouseId</c> ⇒ คืนสต็อกเข้าคลังเดียวกันเสมอแม้ร้านเปลี่ยนค่าตั้งทีหลัง
/// </summary>
public static class CmsSiteWarehouse
{
    public static CmsStockSource Decide(bool useGlobalInventory, Guid? defaultWarehouseId, Guid? branchId)
    {
        if (useGlobalInventory) return CmsStockSource.CompanyDefault;
        if (defaultWarehouseId is Guid w && w != Guid.Empty) return CmsStockSource.SiteWarehouse;
        if (branchId is Guid b && b != Guid.Empty) return CmsStockSource.BranchWarehouse;
        return CmsStockSource.CompanyDefault;
    }

    /// <summary>คลังที่ใช้ตัดสต็อกของเว็บนี้ · <c>null</c> = ให้ ledger เลือกคลังหลัก (เดิม)</summary>
    public static async Task<Guid?> ResolveAsync(AccountingDbContext db, IStockLedger? stock, Guid companyId, Guid siteId, CancellationToken ct = default)
    {
        var site = await db.Sites.AsNoTracking().Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.UseGlobalInventory, s.DefaultWarehouseId, s.BranchId }).FirstOrDefaultAsync(ct);
        if (site == null) return null;
        switch (Decide(site.UseGlobalInventory, site.DefaultWarehouseId, site.BranchId))
        {
            case CmsStockSource.SiteWarehouse:
                var w = site.DefaultWarehouseId!.Value;
                var ok = await db.Warehouses.AsNoTracking().AnyAsync(x => x.Id == w && x.CompanyId == companyId && !x.IsDeleted && x.IsActive, ct);
                return ok ? w : null;
            case CmsStockSource.BranchWarehouse:
                return stock == null ? null : await stock.ResolveWarehouseIdAsync(companyId, site.BranchId, ct);
            default:
                return null;
        }
    }
}
