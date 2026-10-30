namespace Accounting.Helpers;

/// <summary>
/// รอบ 216 ฝ่ายค้าน (คำตัดสินข้อ 174 ภาคผนวก) — <b>ที่อยู่หลักต่อชนิดของสมาชิกหน้าร้านมีได้แถวเดียว</b> (<c>SiteCustomerAddresses</c>) ·
/// รันทุกบูตได้ (idempotent):
/// <list type="number">
/// <item>ล้างแถวหลักซ้ำของข้อมูลเดิม — คง<b>แถวล่าสุด</b> (CreatedAt · Id) ต่อ (CustomerId, AddressType) เป็นหลัก แถวอื่นเป็น "ไม่ใช่หลัก" (ไม่ลบที่อยู่ของลูกค้า)</item>
/// <item>partial unique index (CustomerId, AddressType) WHERE IsDefault AND NOT IsDeleted — คำขอพร้อมกันสองคำขอสร้างแถวหลักซ้ำไม่ได้</item>
/// </list>
/// ฝั่งเขียน (<c>StorefrontCustomerService.Profile.cs</c>) ปลดแถวหลักอื่นของชนิดเดียวกันใน SaveChanges เดียวกับแถวที่เลือกอยู่แล้ว
/// </summary>
public static class MemberProfileMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """
        UPDATE "SiteCustomerAddresses" a SET "IsDefault" = false
        WHERE a."IsDefault" = true AND a."IsDeleted" = false
          AND EXISTS (SELECT 1 FROM "SiteCustomerAddresses" b
                      WHERE b."CustomerId" = a."CustomerId" AND b."AddressType" = a."AddressType"
                        AND b."IsDefault" = true AND b."IsDeleted" = false
                        AND (b."CreatedAt" > a."CreatedAt" OR (b."CreatedAt" = a."CreatedAt" AND b."Id" > a."Id")));
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SiteCustomerAddresses_Customer_Type_Default" ON "SiteCustomerAddresses" ("CustomerId", "AddressType") WHERE "IsDefault" = true AND "IsDeleted" = false;""",
    };
}
