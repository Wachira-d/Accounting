namespace Accounting.Helpers;

/// <summary>
/// รอบ 288 — "งานอัตโนมัติกับคู่ค้า" ใช้งานได้ทั้งหมด: (1) อนุมัติใบซื้อร่างจากคู่ค้าเองภายใต้เพดาน (watermark + เหตุบนลิงก์ของใบ) ·
/// (2) รับสำเนาสลิปการโอนจากคู่ค้า (ลิงก์ intent → ไฟล์สลิปฝั่งผู้ขาย · ไฟล์สำเนาฝั่งผู้ซื้อ) · idempotent (<c>ADD COLUMN IF NOT EXISTS</c>) ·
/// commit b281ce5b · 8535cd5e · 288b bfded8a0 · 9871b8ea · คอลัมน์ใหม่ทั้งหมด = "ยังไม่เคยประเมิน/ยังไม่มีสำเนา" — ไม่มีของเสียที่ persist ไว้ต้องซ่อม · intent ก่อนรอบนี้ไม่มีลิงก์ไฟล์ ⇒ ไม่ถูกส่งสำเนา (ไม่เดาไฟล์จากเวลา)
/// </summary>
public static class Round288Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "CrossTenantDocumentLinks" ADD COLUMN IF NOT EXISTS "AutoApproveCode" varchar(32) NULL;""",
        """ALTER TABLE "CrossTenantDocumentLinks" ADD COLUMN IF NOT EXISTS "AutoApproveReason" varchar(500) NULL;""",
        """ALTER TABLE "CrossTenantDocumentLinks" ADD COLUMN IF NOT EXISTS "AutoApproveEvaluatedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "SlipAttachmentId" uuid NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipCopyAttachmentId" uuid NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipCopyCheckedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipCopyAttempts" integer NOT NULL DEFAULT 0;""",
        // รอบ 288b (ฝ่ายค้านข้อ 3): คิวสำเนาสลิปไม่บังกันเอง + สถานะปลายทางที่บอกเหตุ · แถวเดิม null = ตรวจได้เลย (ไม่มีของเสียที่ต้องซ่อม — แถวที่ค้างรอถูกเลื่อนเองในรอบแรก)
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipCopyNextCheckAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipCopyNote" varchar(300) NULL;""",
    };
}
