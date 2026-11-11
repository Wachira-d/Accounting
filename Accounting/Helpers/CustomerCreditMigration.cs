namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" (erp-review/2026-11-11/team-D-lump-payment.md §3.1) — คอลัมน์ + ผัง 21630 ให้บริษัทเดิม + backfill ครั้งเดียว
/// <para>ผู้เรียก: <c>DatabaseMigrationHelper.GetAlterStatements</c> (หลัง <c>OneShotDataFixesTableSql</c>) · idempotent ทุกบรรทัด ·
/// ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated) และเรียก <see cref="BackfillSql"/> ตรง ๆ ใน Db test</para>
/// <para><b>Backfill (F2 ข้อ 9 — ของที่ persist ไว้ก่อนแก้):</b> การรับชำระหลายใบเดิมที่มีเงินส่วนเกิน ⇒ ประทับ
/// <c>UnappliedAmount</c> (สูตรเดียวกับ <see cref="UnappliedCreditBalance.Compute"/>) · <c>UnappliedAccountId</c> = ผังขา Cr ของ JE ส่วนเกิน ·
/// <c>ContactId</c> = ผู้ติดต่อของใบแรก · เหตุผล = Unknown (ขึ้นคิวให้คนตัดสิน) · <b>เฉพาะ</b>แถวที่พิสูจน์ได้: ฝั่งรับ (สมุดรายวันรับ) ·
/// สกุลบาท · JE ส่วนเกินยังมีผลและ<b>ยังไม่ถูก</b> <c>apply-journal-deposit</c> (P-2 — แถวที่ถูกใช้ไปแล้วไม่แตะ ให้คนตัดสินรายแถว · 258-1-12)</para>
/// </summary>
public static class CustomerCreditMigration
{
    /// <summary>คีย์ marker ของ backfill ครั้งเดียว (ตาราง <c>OneShotDataFixes</c>)</summary>
    public const string BackfillMarkerKey = "r258-unapplied-backfill";

    /// <summary>คำอธิบาย JE เงินส่วนเกินที่ <c>CreateMultiDocPaymentAsync</c> เขียน (ขึ้นต้นด้วยคำนี้) — backfill ใช้ระบุ JE</summary>
    public const string ExcessJournalDescriptionPrefix = "เงินส่วนเกินยังไม่จัดสรร";

    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ContactId" uuid NULL;""",
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedAccountId" uuid NULL;""",
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedReason" varchar(40) NULL;""",
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedNote" text NULL;""",
        """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "UnappliedResolvedAt" timestamp NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "IsLateAllocation" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "AppliedAt" timestamp NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "JournalEntryId" uuid NULL;""",
        """ALTER TABLE "PaymentAllocations" ADD COLUMN IF NOT EXISTS "StampedDocumentPaymentDate" boolean NOT NULL DEFAULT false;""",
        """CREATE INDEX IF NOT EXISTS "IX_Payments_Company_Unapplied" ON "Payments" ("CompanyId", "ContactId") WHERE "UnappliedAmount" > 0 AND "IsDeleted" = false;""",
        // ผัง 21630 ให้บริษัทเดิมที่มีกลุ่ม 216 (บริษัทผังกำหนดเอง = สร้างตอนใช้/ล้มดัง — UnappliedReceiptAccount.EnsureAsync)
        UnappliedReceiptAccount.MigrationSeedSql(),
        BackfillSql(),
    };

    /// <summary>backfill ครั้งเดียวใต้ marker — เงื่อนไขอยู่ในคำอธิบายคลาส (JournalType 3 = สมุดรายวันรับ · Status 1 = Posted)</summary>
    public static string BackfillSql() => $$"""
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM "OneShotDataFixes" WHERE "Key" = '{{BackfillMarkerKey}}') THEN
                UPDATE "Payments" p SET
                    "UnappliedAmount" = x.remaining,
                    "UnappliedAccountId" = x.account_id,
                    "ContactId" = x.contact_id,
                    "UnappliedReason" = 'Unknown'
                FROM (
                    SELECT p2."Id" AS pid,
                           round(p2."Amount" - COALESCE((SELECT sum(a."AllocatedAmount") FROM "PaymentAllocations" a
                                                         WHERE a."PaymentId" = p2."Id" AND a."CompanyId" = p2."CompanyId" AND a."IsDeleted" = false), 0), 2) AS remaining,
                           (SELECT l."AccountId" FROM "JournalEntryLines" l
                            WHERE l."JournalEntryId" = j."Id" AND l."IsDeleted" = false AND l."CreditAmount" > 0
                            ORDER BY l."LineOrder" LIMIT 1) AS account_id,
                           d."ContactId" AS contact_id
                    FROM "Payments" p2
                    JOIN "JournalEntries" j ON j."CompanyId" = p2."CompanyId" AND j."Reference" = p2."PaymentNumber"
                         AND j."Status" = 1 AND j."IsDeleted" = false AND j."JournalType" = 3
                         AND j."OriginalEntryId" IS NULL AND j."ReversedByEntryId" IS NULL
                         AND j."SourceDocumentId" IS NULL AND j."DepositAppliedToDocumentId" IS NULL
                         AND j."Description" LIKE '{{ExcessJournalDescriptionPrefix}}%'
                    JOIN "Documents" d ON d."Id" = p2."DocumentId" AND d."CompanyId" = p2."CompanyId"
                    WHERE p2."IsDeleted" = false AND p2."UnappliedAccountId" IS NULL
                      AND COALESCE(d."Currency", 'THB') = 'THB' AND COALESCE(p2."ExchangeRate", 1) = 1
                      AND EXISTS (SELECT 1 FROM "PaymentAllocations" a WHERE a."PaymentId" = p2."Id" AND a."CompanyId" = p2."CompanyId" AND a."IsDeleted" = false)
                ) x
                WHERE p."Id" = x.pid AND x.remaining > 0.005 AND x.account_id IS NOT NULL;
                INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ('{{BackfillMarkerKey}}', (now() at time zone 'utc')) ON CONFLICT DO NOTHING;
            END IF;
        END $$;
        """;
}
