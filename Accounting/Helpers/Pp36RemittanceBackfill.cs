namespace Accounting.Helpers;

/// <summary>
/// <b>SQL ผูกใบ ↔ รายการนำส่ง ภ.พ.36 ของข้อมูลก่อนรอบ 203</b> (migration · <c>DatabaseMigrationHelper</c>) — แยกเป็นตัวสร้างเพื่อให้เทสต์ล็อกเงื่อนไขได้
///
/// <para>═══ รอบ 203 ฝ่ายค้าน P2-1 ═══ รุ่นแรกใช้ <c>d."CreatedAt" &lt;= r."CreatedAt"</c> ⇒ ใบที่ <b>สร้างเป็นร่าง</b>ก่อนวันนำส่งแต่<b>อนุมัติทีหลัง</b>
/// (ยอดนำส่งเดิมไม่ได้รวมใบนี้) ถูกผูกว่า "นำส่งแล้ว" ⇒ ไม่ขึ้นยอดค้าง · ถูกรับรู้ภาษีซื้อทั้งที่ VAT ยังไม่ได้นำส่ง ⇒ เกณฑ์ที่ถูกคือ
/// <b>เวลาที่ JE หลักของใบถูกสร้าง</b> (JE ที่ <c>SourceDocumentId</c> = ใบ · Posted · ไม่ใช่คู่กลับรายการ) ≤ เวลาบันทึกการนำส่ง —
/// หนี้ 21912 ต้องมีอยู่แล้วตอนกดนำส่งจึงจะถูกนับในยอดนั้น · ใบที่ไม่มี JE ไม่ถูกผูก (สถานะ "ไม่มี JE ต้องซ่อม" เครื่องมือซ่อมลงได้แล้วนำส่งเพิ่มเติม)</para>
/// <para>เงื่อนไขอื่น: ใบเจ้าของหนี้เท่านั้น (= <c>ForeignServiceVat.OwnsPp36Query</c>: PI/Expense + PV ไม่อ้างใบต้นทาง + มี VAT) · ออกแล้วไม่ยกเลิก ·
/// งวด = วันจ่าย ?? วันที่เอกสาร · ครั้งเดียวต่อรายการนำส่ง (รายการที่มีแถวผูกแล้วไม่ถูกเติมอีก) · ใบละแถว</para>
/// </summary>
public static class Pp36RemittanceBackfill
{
    /// <summary>ผู้บันทึกของแถวที่ migration สร้าง (แยกจากแถวของเส้นนำส่งจริง)</summary>
    public const string Actor = "migration-pp36-backfill";

    /// <summary>คำสั่ง INSERT ... SELECT (idempotent) — enum เก็บเป็นตัวเลข: DocumentType PI=8 · Expense=9 · PV=13 · DocumentStatus ไม่ออก/ยกเลิก = 0,1,6,8 ·
    /// JournalEntryStatus.Posted = 1</summary>
    public static string BuildSql() => $"""
        INSERT INTO "Pp36RemittanceDocuments" ("Id","CompanyId","StatutoryRemittanceId","DocumentId","PeriodYear","PeriodMonth","VatAmount","RecognizedAmount","CreatedAt","CreatedBy","IsDeleted")
        SELECT gen_random_uuid(), d."CompanyId", r."Id", d."Id", r."PeriodYear", r."PeriodMonth",
               ROUND(d."VatAmount" * CASE WHEN d."ExchangeRate" <= 0 THEN 1 ELSE d."ExchangeRate" END, 2),
               CASE WHEN d."InputVatBecameClaimableAt" IS NOT NULL THEN ROUND(d."VatAmount" * CASE WHEN d."ExchangeRate" <= 0 THEN 1 ELSE d."ExchangeRate" END, 2) ELSE 0 END,
               now(), '{Actor}', false
        FROM "Documents" d
        JOIN "StatutoryRemittances" r ON r."CompanyId" = d."CompanyId" AND r."RemittanceType" = 'VatPp36' AND r."IsDeleted" = false
            AND r."PeriodYear" = EXTRACT(YEAR FROM COALESCE(d."PaymentDate", d."DocumentDate"))::int
            AND r."PeriodMonth" = EXTRACT(MONTH FROM COALESCE(d."PaymentDate", d."DocumentDate"))::int
        WHERE d."IsForeignService" = true AND d."VatAmount" > 0 AND d."IsDeleted" = false
          AND (d."DocumentType" IN (8, 9) OR (d."DocumentType" = 13 AND d."RelatedDocumentId" IS NULL))
          AND d."Status" NOT IN (0, 1, 6, 8)
          AND EXISTS (SELECT 1 FROM "JournalEntries" j
                      WHERE j."CompanyId" = d."CompanyId" AND j."SourceDocumentId" = d."Id" AND j."IsDeleted" = false
                        AND j."Status" = 1 AND j."OriginalEntryId" IS NULL AND j."ReversedByEntryId" IS NULL
                        AND j."CreatedAt" <= r."CreatedAt")
          AND NOT EXISTS (SELECT 1 FROM "Pp36RemittanceDocuments" x WHERE x."CompanyId" = d."CompanyId" AND x."DocumentId" = d."Id" AND x."IsDeleted" = false)
          AND NOT EXISTS (SELECT 1 FROM "Pp36RemittanceDocuments" y WHERE y."CompanyId" = r."CompanyId" AND y."StatutoryRemittanceId" = r."Id")
          AND r."Id" = (SELECT r2."Id" FROM "StatutoryRemittances" r2 WHERE r2."CompanyId" = r."CompanyId" AND r2."RemittanceType" = 'VatPp36' AND r2."IsDeleted" = false
                        AND r2."PeriodYear" = r."PeriodYear" AND r2."PeriodMonth" = r."PeriodMonth" ORDER BY r2."CreatedAt" LIMIT 1);
        """;
}
