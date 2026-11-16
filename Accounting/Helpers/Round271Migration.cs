namespace Accounting.Helpers;

/// <summary>
/// รอบ 271 — ผูกใบสำคัญจ่ายกับ 50 ทวิ ที่ออกไว้แล้ว · คอลัมน์ใหม่ทั้งหมด NULL (แถวเดิม = พฤติกรรมเดิม · ไม่ซ่อม/ไม่เดาย้อนหลัง) · idempotent
/// <list type="bullet">
/// <item><c>Documents.WhtLinkCertId/WhtLinkAckCodes</c> — ความตั้งใจบนร่าง "ผูกใบนี้แทนการออกใหม่" + คำเตือนที่รับทราบ</item>
/// <item><c>WithholdingTaxCerts.LinkedAt/LinkedByUserId</c> — ประทับเมื่อผูกด้วยมือ · <c>SupersededByCertId</c> — ใบเดิมที่ถูกแทนตอนเปลี่ยนการผูก ·
/// <c>NoSourceDocumentReason</c> — D5 ยืนยันว่าไม่มีใบสำคัญจ่าย</item>
/// </list>
/// ไม่มี data migration: ใบซ้ำเดิม (F6) ระบบตัดสินแทนไม่ได้ว่าใบไหนส่งมอบแล้ว ⇒ ตัวตรวจ "อาจนับซ้ำ" อ่านอย่างเดียว + สคริปต์ <c>scripts/wht_duplicate_cert_scan.sql</c>
/// </summary>
public static class Round271Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtLinkCertId" uuid NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtLinkAckCodes" text NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "LinkedAt" timestamp NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "LinkedByUserId" uuid NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "SupersededByCertId" uuid NULL;""",
        """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "NoSourceDocumentReason" text NULL;""",
    };
}
