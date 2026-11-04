namespace Accounting.Helpers;

/// <summary>รอบ 237 (K-1 · DECISIONS 237-1/237-5) — อัปเกรดกลางรอบ 2 แบบเลือกได้: ค่าตั้งแพลตฟอร์ม <c>SiteSettings.PlatformUpgradeModeOffer</c>
/// (0 = CustomerChooses — ค่าตั้งต้น · แถวเดิมได้พฤติกรรมเดิม: เลือกแบบคงวันหมดอายุไว้ให้) + แบบที่ลูกค้าเลือกบน <c>SubscriptionPayments.UpgradeMode</c>
/// (NULL = แถวก่อนรอบนี้ = แบบคงวันหมดอายุตามธงเดิม — ไม่ backfill) · ADD COLUMN IF NOT EXISTS ⇒ รันทุกบูตได้</summary>
public static class PlatformUpgradeModeMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformUpgradeModeOffer" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "UpgradeMode" integer NULL;""",
        // ฝ่ายค้าน 237 N2: วันเริ่มรอบจริงที่ประทับตอนอนุมัติ (null = แถวเก่า — ข้อความใช้วันชำระแทน)
        """ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "PaidPeriodStartUtc" timestamp with time zone NULL;""",
    };
}
