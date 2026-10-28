using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>"ผู้ใช้คนนี้เห็นเว็บไหนบ้าง" ของมุมมอง<b>ทุกไซต์</b> (คำสั่งซื้อ · จองคิว · Lead ข้ามเว็บ) — <b>ตัวตัดสินตัวเดียว</b> (OWNER file · R-45)
///
/// ที่มา: หน้า cms-orders/cms-leads ทำโหมดทุกไซต์<b>ฝั่ง client</b> (วนยิง endpoint รายเว็บ 20 แถว/เว็บ) และ cms-bookings ไม่มีโหมดนี้เลย ·
/// ตอนนี้มี endpoint ระดับบริษัท (<c>…/cms/overview/{orders|bookings|leads}</c>) ที่แบ่งหน้าจริง — ด่านสิทธิ์ต่อแถวจึงต้องอยู่ฝั่งเซิร์ฟเวอร์
/// ด้วยกติกา<b>เดียวกับ</b> <c>CmsRbacMiddleware</c> (<c>[RequireSiteRole]</c> รายเว็บ): เจ้าของบริษัท (<see cref="UserRole.Owner"/>) เห็นทุกเว็บ ·
/// คนอื่นเห็นเฉพาะเว็บที่มี <c>SiteStaffAccess</c> ที่ยังใช้งาน และบทบาทอยู่ในชุดที่ endpoint รายเว็บนั้นยอม (ชุดว่าง = บทบาทใดก็ได้)
///
/// เทสต์ทิศตรงข้าม (เกณฑ์เสร็จ W4): ผู้ใช้ที่ไม่มีบทบาทบนเว็บ B ต้องไม่เห็นแถวของ B ⇒ B ไม่อยู่ในผลของ <see cref="AllowedSiteIds"/>
/// และ query ของ service กรองด้วย <c>siteIds.Contains(x.SiteId)</c> เสมอ (รายการว่าง = ไม่มีแถว ไม่ใช่ "ทุกแถว")</summary>
public static class CmsSiteScope
{
    public sealed record AccessRow(Guid SiteId, SiteStaffRole Role, bool IsActive);

    /// <summary>บทบาทที่ endpoint รายเว็บของออเดอร์ยอม — ต้องตรงกับ <c>[RequireSiteRole]</c> บน <c>GET commerce/orders</c></summary>
    public static readonly IReadOnlyCollection<SiteStaffRole> OrderRoles = new[] { SiteStaffRole.Admin, SiteStaffRole.OrderManager };

    /// <summary>จองคิว/Lead: endpoint รายเว็บไม่จำกัดบทบาท ⇒ มีสิทธิ์บนเว็บ (บทบาทใดก็ได้) ก็เห็น</summary>
    public static readonly IReadOnlyCollection<SiteStaffRole> AnyRole = System.Array.Empty<SiteStaffRole>();

    /// <summary>เว็บที่ผู้ใช้เห็นในมุมมองทุกไซต์ — เรียงตามลำดับที่ส่งมาใน <paramref name="companySiteIds"/> (ไม่ซ้ำ)</summary>
    /// <param name="isCompanyOwner">บทบาทระดับบริษัท = Owner (จาก <c>HttpContext.Items["UserRole"]</c>)</param>
    /// <param name="companySiteIds">เว็บทั้งหมดของบริษัทนี้ (ผู้เรียกกรอง <c>CompanyId</c> มาแล้ว)</param>
    /// <param name="access">แถว <c>SiteStaffAccess</c> ของผู้ใช้คนนี้ (ทุกเว็บ · ผู้เรียกกรอง UserId)</param>
    /// <param name="allowedRoles">บทบาทที่ยอม — ว่าง = บทบาทใดก็ได้ที่ยังใช้งาน</param>
    public static IReadOnlyList<Guid> AllowedSiteIds(
        bool isCompanyOwner,
        IEnumerable<Guid> companySiteIds,
        IEnumerable<AccessRow> access,
        IReadOnlyCollection<SiteStaffRole> allowedRoles)
    {
        var company = companySiteIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (isCompanyOwner) return company;

        var ok = new HashSet<Guid>();
        foreach (var a in access)
        {
            if (!a.IsActive) continue;
            if (allowedRoles.Count > 0 && !allowedRoles.Contains(a.Role)) continue;
            ok.Add(a.SiteId);
        }
        // ตัดด้วยรายการเว็บของบริษัท — แถวสิทธิ์ของเว็บที่ถูกลบ/ของบริษัทอื่นไม่เปิดอะไร
        return company.Where(ok.Contains).ToList();
    }

    /// <summary>ขนาดหน้าของมุมมองทุกไซต์ — บีบเข้าช่วง (ค่าผิดจาก query string ไม่ทำให้ดึงทั้งฐาน)</summary>
    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 200);

    public static int ClampPage(int page) => Math.Max(1, page);
}
