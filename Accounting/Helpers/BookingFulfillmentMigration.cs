namespace Accounting.Helpers;

/// <summary>รอบ 226 — คอลัมน์จัดส่ง/รับเองของการจอง (<see cref="BookingFulfillment"/>) · <b>รันทุกบูตได้</b> (IF NOT EXISTS) ·
/// DEFAULT = พฤติกรรมเดิมทุกตัว: เว็บปิดทั้งจัดส่ง/รับเอง ⇒ ไม่มีขั้นนี้ · บริการ NULL = ตามเว็บ · การจองเดิมไม่มีวิธีรับ + ค่าส่ง 0 ·
/// ไม่ UPDATE ข้อมูลเดิม · ชนิด/ความยาวต้องตรงกับ <c>AccountingDbContext</c></summary>
public static class BookingFulfillmentMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "FulfillmentDeliveryEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "FulfillmentPickupEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "FulfillmentDefault" varchar(16) NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "FulfillmentPickupNote" varchar(1000) NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "FulfillmentDeliveryMinAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "FulfillmentDeliveryEnabled" boolean NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "FulfillmentPickupEnabled" boolean NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "FulfillmentDefault" varchar(16) NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "FulfillmentMethod" varchar(16) NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "DeliveryFee" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "DeliveryAddress" varchar(1000) NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "DeliveryProvince" varchar(100) NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "DeliveryPostalCode" varchar(10) NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "DeliveryRateId" uuid NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "DeliveryRateName" varchar(256) NULL;""",
        // ฝ่ายค้าน ข้อ 1: ชนิดบริการตรึงบนการจอง — แถวเดิม = ชนิดของบริการ ณ วันที่อัปเดต (ชนิดที่เอกสารเดิมออกตาม) แล้วบังคับ NOT NULL
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "BookingType" integer NULL;""",
        """UPDATE "SiteBookings" b SET "BookingType" = s."BookingType" FROM "SiteBookingServices" s WHERE b."BookingServiceId" = s."Id" AND b."BookingType" IS NULL;""",
        """UPDATE "SiteBookings" SET "BookingType" = 1 WHERE "BookingType" IS NULL;""",
        """ALTER TABLE "SiteBookings" ALTER COLUMN "BookingType" SET DEFAULT 1;""",
        """ALTER TABLE "SiteBookings" ALTER COLUMN "BookingType" SET NOT NULL;""",
        // ฝ่ายค้าน ข้อ 3: เว็บโหมดบังคับที่ไม่เคยกรอกกำหนดชำระ (NULL = ไม่ถือรอบ ก่อนรอบ 226) ⇒ ตั้ง 0 = "ไม่กำหนด" ชัด ๆ (พฤติกรรมเดิม) ·
        // ค่าตั้งต้น 1440 ใช้กับค่าที่ว่างหลังรอบนี้เท่านั้น · รันซ้ำได้ (หลังจากนี้ "ว่าง" ถูกเก็บเป็น NULL โดยตั้งใจ ⇒ ต้องไม่ทับ) — ทำครั้งเดียวด้วยธง
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDeadlineLegacyFixed" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDeadlineLegacyNote" boolean NOT NULL DEFAULT false;""",
        """UPDATE "SiteBookingSettings" SET "PaymentDeadlineMinutes" = 0, "PaymentDeadlineLegacyNote" = true WHERE "PaymentDeadlineLegacyFixed" = false AND "PaymentMode" IN (1, 3) AND "PaymentDeadlineMinutes" IS NULL;""",
        """UPDATE "SiteBookingSettings" SET "PaymentDeadlineLegacyFixed" = true WHERE "PaymentDeadlineLegacyFixed" = false;""",
        // แถวที่สร้างหลังจากนี้ไม่ต้องแปลง (ว่าง = ค่าตั้งต้น 1440 โดยตั้งใจ) — คอลัมน์ไม่อยู่ใน EF ⇒ ค่า DEFAULT ของฐานมีผล
        """ALTER TABLE "SiteBookingSettings" ALTER COLUMN "PaymentDeadlineLegacyFixed" SET DEFAULT true;""",
    };
}
