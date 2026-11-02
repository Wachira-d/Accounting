namespace Accounting.Helpers;

/// <summary>รอบ 228 (BOOKING_PAYMENT_AUDIT §9) — คอลัมน์ของ "จัดการการจองของฉัน" + บัญชีรายได้ค่าจัดส่ง + โซนที่ชนะโซนถัดไป · <b>รันทุกบูตได้</b> (IF NOT EXISTS) ·
/// DEFAULT = พฤติกรรมเดิมทุกตัว: ลูกค้าเปลี่ยนวิธีรับเองไม่ได้ (NULL) · ค่าจัดส่งลงบัญชีรายได้ตั้งต้น (NULL) · ทุกโซนที่ตรงให้ลูกค้าเลือก (false) ·
/// ไม่ UPDATE ข้อมูลเดิม · ชนิดต้องตรงกับ entity (<c>SiteBookingSettings</c> · <c>SiteCommerceConfig</c> · <c>SiteShippingZone</c>)</summary>
public static class BookingManageMigration
{
    public static IReadOnlyList<string> Statements() => StatementsCore().Concat(SiteTransferAccount.MigrationStatements()).ToList();   // ฝ่ายค้านรอบ 228 S-1

    private static IReadOnlyList<string> StatementsCore() => new[]
    {
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "FulfillmentChangeCutoffHours" integer NULL;""",
        """ALTER TABLE "SiteCommerceConfigs" ADD COLUMN IF NOT EXISTS "ShippingRevenueAccountId" uuid NULL;""",
        """ALTER TABLE "SiteShippingZones" ADD COLUMN IF NOT EXISTS "StopWhenMatched" boolean NOT NULL DEFAULT false;""",
        // รอบ 228 (CMS_SETTINGS_UX_AUDIT แพ็ก C · คำตัดสิน Q3): เจ้าของยืนยัน "ส่งฟรีจริง" ⇒ เลิกเตือน "จัดส่งเปิดแต่ไม่ได้ตั้งค่าส่ง" · ค่าเดิม false = เตือน
        """ALTER TABLE "SiteCommerceConfigs" ADD COLUMN IF NOT EXISTS "FreeDeliveryConfirmed" boolean NOT NULL DEFAULT false;""",
    };
}
