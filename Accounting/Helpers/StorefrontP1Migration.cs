namespace Accounting.Helpers;

/// <summary>รอบ 209 · P1 (หน้าร้านฝั่งลูกค้า · R-209-A1/B3/B4/A9) — คอลัมน์ใหม่ของ <c>SiteOrders</c> · <b>รันทุกบูตได้</b> (IF NOT EXISTS) ·
/// <para><c>PublicToken</c> = กุญแจหน้าสถานะออเดอร์ของแขก (สุ่ม · เดาไม่ได้ · ไม่ใช่ GUID ของออเดอร์) — ออเดอร์เดิม NULL ⇒ backfill ด้วย
/// <c>gen_random_uuid()</c> สองชุด (สุ่มแบบ CSPRNG ของ PostgreSQL 13+) · ออเดอร์ใหม่ออกโดย <see cref="SiteOrderPublic.NewToken"/> ·
/// <c>ShippingCarrier</c> = ผู้ขนส่ง (จากอัตราที่ลูกค้าเลือก · พนักงานแก้คู่กับเลขพัสดุ) · ออเดอร์เดิม NULL = แสดง "-" ·
/// <c>IdempotencyKey</c> = คีย์กันกดสั่งซ้ำจากหน้าเว็บ · unique ต่อ (บริษัท, เว็บ) เฉพาะที่ไม่ NULL — ออเดอร์เดิม NULL ไม่ชนกัน</para></summary>
public static class StorefrontP1Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "SiteOrders" ADD COLUMN IF NOT EXISTS "PublicToken" varchar(64) NULL;""",
        """ALTER TABLE "SiteOrders" ADD COLUMN IF NOT EXISTS "ShippingCarrier" varchar(100) NULL;""",
        """ALTER TABLE "SiteOrders" ADD COLUMN IF NOT EXISTS "IdempotencyKey" varchar(80) NULL;""",
        // ออเดอร์เดิมได้โทเคนครั้งเดียว (WHERE NULL ⇒ บูตรอบถัดไปไม่แตะ) — ลิงก์ order-success เดิม (GUID) ยังเปิดหน้าข้อความได้ ไม่พัง
        """UPDATE "SiteOrders" SET "PublicToken" = replace(gen_random_uuid()::text, '-', '') || replace(gen_random_uuid()::text, '-', '') WHERE "PublicToken" IS NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SiteOrders_PublicToken" ON "SiteOrders" ("PublicToken") WHERE "PublicToken" IS NOT NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SiteOrders_Company_Site_IdempotencyKey" ON "SiteOrders" ("CompanyId", "SiteId", "IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_SiteOrders_Company_Site_Customer" ON "SiteOrders" ("CompanyId", "SiteId", "CustomerId") WHERE "CustomerId" IS NOT NULL;""",
    };
}
