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
        // ฝ่ายค้าน M4 (คำสั่งผู้ประสานงาน): หนึ่งการจ่าย = 50 ทวิ ที่ไม่ยกเลิกหนึ่งใบ — คีย์ (บริษัท, เอกสาร, ผู้ถูกหัก, งวดจ่าย) เพราะการโอนก้อนเดียวหลายใบ
        // ใช้ payment id ร่วมกันทุกใบ และใบหลายผู้ถูกหักออกใบต่อผู้ถูกหัก (คีย์แค่งวดจ่ายจะกันใบที่ถูกต้อง) · ใบไม่ผูกเอกสาร (มือ/payroll) ไม่อยู่ในดัชนี ·
        // ข้อมูลเดิมซ้ำอยู่แล้ว ⇒ CREATE ล้ม ⇒ ApplyMissingColumns log คำเตือน (ไม่บล็อกบูต) + ReportWhtCertDuplicates บอกจำนวน (สแกน scripts/wht_duplicate_cert_scan.sql)
        UniqueActiveCertIndexSql,
    };

    public const string UniqueActiveCertIndexName = "UX_WithholdingTaxCerts_ActivePerPayment";

    public const string UniqueActiveCertIndexSql =
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_WithholdingTaxCerts_ActivePerPayment" ON "WithholdingTaxCerts" ("CompanyId", "DocumentId", "PayeeContactId", COALESCE("SourcePaymentId", '00000000-0000-0000-0000-000000000000'::uuid)) WHERE "Status" <> 2 AND NOT "IsDeleted" AND "DocumentId" IS NOT NULL;""";

    /// <summary>จำนวนกลุ่มที่ละเมิดดัชนี (อ่านอย่างเดียว · ตัวเดียวกับสแกนข้อ (1)) — &gt; 0 ⇒ ดัชนีสร้างไม่ได้ ต้องให้คนเคลียร์ (เปลี่ยนการผูก/ยกเลิกใบซ้ำ)</summary>
    public const string DuplicateGroupsCountSql =
        """SELECT COUNT(*)::int AS "Value" FROM (SELECT 1 FROM "WithholdingTaxCerts" WHERE "DocumentId" IS NOT NULL AND "Status" <> 2 AND NOT "IsDeleted" GROUP BY "CompanyId", "DocumentId", "PayeeContactId", COALESCE("SourcePaymentId", '00000000-0000-0000-0000-000000000000'::uuid) HAVING COUNT(*) > 1) g""";
}
