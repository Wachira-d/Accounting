using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ผู้ติดต่อกลาง "ลูกค้าเงินสด" ของบริษัท — ตัวสร้าง/ตัวหาตัวเดียว** (รอบ 198 ทีม C: ย้ายออกจาก <c>IntegrationService</c>
/// เพราะใบขายสรุปรายวันของ settlement ต้องใช้แถวเดียวกัน — ถ้าเขียนสำเนาที่สอง บริษัทจะได้ "ลูกค้าเงินสด" สองแถว
/// แล้ว <c>FirstOrDefault(IsWalkInCustomer)</c> ของอีกเส้นหยิบแถวไหนก็ได้)
///
/// <para>สร้างครั้งเดียวต่อบริษัท ใช้ซ้ำทุกใบ · Address "-" ให้ §86/4 มีค่าพิมพ์บนใบกำกับ (แนวปฏิบัติค้าปลีกที่สรรพากรยอมรับ —
/// ผู้ซื้อที่ไม่แจ้งข้อมูลเคลมภาษีซื้อไม่ได้อยู่แล้ว) · ด่านอนุมัติ §86/4 ของ <c>DocumentService</c> ยกเว้นผู้ติดต่อ
/// <c>IsWalkInCustomer</c> เอง — ผู้เรียก<b>ห้าม</b>ตั้ง <c>BuyerDeclinedTaxInvoice</c> แทนผู้ซื้อ (เจตนาของมนุษย์ · คำตัดสินรอบ 181)</para>
///
/// <para>บันทึกเอง (SaveChanges) — เรียกนอกธุรกรรมที่มีของค้างใน change tracker ที่ยังไม่อยากบันทึก</para>
/// </summary>
public static class WalkInCustomerContact
{
    public const string DefaultName = "ลูกค้าเงินสด (ไม่ประสงค์รับใบกำกับภาษี)";

    public static async Task<Contact> GetOrCreateAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
    {
        var walkIn = await db.Set<Contact>().FirstOrDefaultAsync(
            c => c.CompanyId == companyId && c.IsWalkInCustomer && !c.IsDeleted, ct);
        if (walkIn != null) return walkIn;

        walkIn = new Contact
        {
            CompanyId = companyId,
            Name = DefaultName,
            Address = "-",
            IsCustomer = true,
            IsActive = true,
            IsWalkInCustomer = true,
            ContactType = ContactType.Individual,
        };
        db.Set<Contact>().Add(walkIn);
        await db.SaveChangesAsync(ct);
        return walkIn;
    }
}
