using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 256 (คำตัดสิน 256-2) — ผู้อ่าน <c>SiteSettings.PlatformCompanyId</c> (บริษัทผู้ให้บริการ) <b>ตัวเดียว</b>ของเส้นที่ตัดสินด้วยค่านี้:
/// <c>PlatformPaymentService.PlatformCompanyIdAsync</c> · กลุ่ม LINE ผ่าน OA กลาง (<c>ContactLineGroupService</c> · <c>LineNotifyService</c> · <c>ContactRecipientResolver</c>) ·
/// แถวแรกตามเวลาสร้าง (แถวเดียวของระบบ) · ไม่มี cache ข้ามคำขอ ⇒ แอดมินเปลี่ยนบริษัทผู้ให้บริการแล้วมีผลทันทีทุกเครื่อง
/// </summary>
public static class PlatformCompanyLookup
{
    public static Task<Guid?> IdAsync(AccountingDbContext db, CancellationToken ct = default)
        => db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.PlatformCompanyId).FirstOrDefaultAsync(ct);
}
