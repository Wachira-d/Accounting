using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 216 ฝ่ายค้าน (ผู้ประสานงาน: ความเสี่ยงค้างข้อ 3) — <b>ทางเขียน "ที่อยู่หลัก" ทุกทางของ <c>SiteCustomerAddresses</c> ต้องผ่านที่นี่</b>
/// เพื่อไม่ชน partial unique index <c>UX_SiteCustomerAddresses_Customer_Type_Default</c> (<see cref="MemberProfileMigration"/>):
/// <list type="number">
/// <item><see cref="LockCustomerAsync"/> — <c>SELECT … FOR UPDATE</c> แถวลูกค้า (กรองบริษัท) ใต้ธุรกรรมของผู้เรียก ⇒ คำขอพร้อมกันของลูกค้าคนเดียวเรียงคิว</item>
/// <item><see cref="ClearDefaultsAsync"/> — ปลดแถวหลักเดิมของชนิดนั้นด้วย UPDATE ตรงถึงฐาน <b>ก่อน</b> INSERT/ตั้งแถวหลักใหม่
/// (ลำดับคำสั่งใน SaveChanges เดียวไม่แน่นอน — เคยเป็นความเสี่ยงของ <c>CmsCustomerService.AddAddressAsync</c>)</item>
/// </list>
/// ผู้เรียก: <c>CmsCustomerService.AddAddressAsync</c> (พนักงาน) · <c>StorefrontCustomerService.ApplyProfileAsync</c> (สมาชิก/บันทึกจากฟอร์ม)
/// </summary>
public static class CustomerAddressDefaults
{
    /// <summary>ล็อกแถวลูกค้าของบริษัทนี้ (ต้องอยู่ในธุรกรรม) · คืน false = ไม่พบลูกค้าในบริษัทนี้</summary>
    public static async Task<bool> LockCustomerAsync(AccountingDbContext db, Guid companyId, Guid customerId)
    {
        var ids = await db.Database.SqlQuery<Guid>(
            $"""SELECT "Id" AS "Value" FROM "SiteCustomers" WHERE "Id" = {customerId} AND "CompanyId" = {companyId} FOR UPDATE""").ToListAsync();
        return ids.Count > 0;
    }

    /// <summary>ปลดแถวหลักทุกแถวของ (ลูกค้า, ชนิด) ตรงถึงฐาน — เรียกก่อนเพิ่ม/ตั้งแถวหลักใหม่ในธุรกรรมเดียวกัน</summary>
    public static Task<int> ClearDefaultsAsync(AccountingDbContext db, Guid companyId, Guid customerId, SiteAddressType type)
        => db.SiteCustomerAddresses
            .Where(a => a.CompanyId == companyId && a.CustomerId == customerId && a.AddressType == type && a.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false).SetProperty(a => a.UpdatedAt, DateTime.UtcNow));
}
