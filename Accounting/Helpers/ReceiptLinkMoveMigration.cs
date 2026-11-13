namespace Accounting.Helpers;

/// <summary>
/// รอบ 266 แพ็ก B — คอลัมน์ของ "ย้ายการรับชำระระหว่างใบ" (คำตัดสิน 266-1-23) · <c>ADD COLUMN IF NOT EXISTS</c> (รันทุกบูตได้ · ห้ามใช้ EF Migrations)
/// <para>ไม่มี backfill: ข้อมูลก่อนรอบนี้ไม่มีการย้าย ⇒ <c>MovedToDocumentId</c>/<c>ReleasedAt</c> เป็น null ทุกแถว = พฤติกรรมเดิมทุกตัวอักษร</para>
/// </summary>
public static class ReceiptLinkMoveMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "MovedToDocumentId" uuid NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "ReleasedAt" timestamp NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "ReleasedBy" varchar(100) NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "ReleaseJournalEntryId" uuid NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "MovedFromAllocationId" uuid NULL;""",
        // รอบ 266 ฝ่ายค้าน B-8: กระดาษที่การย้ายแตะ (ยกเลิก/ออก) — null ทุกแถวเก่า ⇒ ถอดได้ตามกติกาเดิม (ด่านใบแทน ReplacesDocumentId ยังกันใบแทนที่ออกก่อนรอบนี้)
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "MoveVoidedReceiptId" uuid NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "MoveIssuedReceiptId" uuid NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_Payments_Company_MovedTo" ON "Payments" ("CompanyId", "MovedToDocumentId") WHERE "MovedToDocumentId" IS NOT NULL;""",
    };
}
