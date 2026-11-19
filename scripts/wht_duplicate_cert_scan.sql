-- รอบ 271 แพ็ก P0 — สแกน 50 ทวิ ที่อาจนับซ้ำ (อ่านอย่างเดียว · ไม่มี UPDATE/DELETE/DDL)
-- ใช้: psql "$DB" -v ON_ERROR_STOP=1 -f scripts/wht_duplicate_cert_scan.sql
-- ที่มา: erp-review/2026-11-16/team-271-pv-wht-link.md §2 F6 · §4.2 — ข้อมูลก่อนตัดสินสร้าง partial unique index
--   (CompanyId, DocumentId, PayeeContactId, COALESCE(SourcePaymentId)) WHERE Status <> Voided — ถ้าสแกน (1) เจอแถว ⇒ CREATE INDEX จะล้ม ⇒ ห้ามสร้างจนกว่าจะเคลียร์
-- สถานะ: 0 = Draft · 1 = Issued · 2 = Voided · 3 = Printed
BEGIN TRANSACTION READ ONLY;

-- (1) ใบที่ยังไม่ยกเลิกมากกว่า 1 ใบต่อ (เอกสาร, ผู้ถูกหัก, งวดจ่าย) — นับซ้ำในแบบ ภ.ง.ด. ของการจ่ายเดียวกัน
SELECT w."CompanyId", w."DocumentId", d."DocumentNumber", w."PayeeContactId",
       COALESCE(w."SourcePaymentId", '00000000-0000-0000-0000-000000000000'::uuid) AS "PaymentKey",
       COUNT(*) AS "ActiveCerts", SUM(w."TotalTaxAmount") AS "TaxSum",
       string_agg(w."CertificateNumber", ', ' ORDER BY w."CertificateNumber") AS "Certs"
FROM "WithholdingTaxCerts" w
LEFT JOIN "Documents" d ON d."Id" = w."DocumentId" AND d."CompanyId" = w."CompanyId"
WHERE w."DocumentId" IS NOT NULL AND w."Status" <> 2 AND NOT w."IsDeleted"
GROUP BY w."CompanyId", w."DocumentId", d."DocumentNumber", w."PayeeContactId",
         COALESCE(w."SourcePaymentId", '00000000-0000-0000-0000-000000000000'::uuid)
HAVING COUNT(*) > 1
ORDER BY w."CompanyId", d."DocumentNumber";

-- (2) F6 — ใบมือ (ไม่ผูกเอกสาร · ไม่ใช่รอบเงินเดือน · ยังไม่ยืนยันว่าไม่มีใบสำคัญจ่าย) ที่มีใบผูกเอกสารของ
--     ผู้ถูกหัก + แบบ + ปี/เดือน + ภาษี เดียวกัน (ตัวเดียวกับตัวกรอง "อาจนับซ้ำ" บนหน้า 50 ทวิ) — ระบบไม่ตัดสินแทน: ผู้ใช้กด "ตรวจคู่"
SELECT m."CompanyId", m."CertificateNumber" AS "ManualCert", y."CertificateNumber" AS "LinkedCert", d."DocumentNumber",
       m."TaxFormType", m."TaxYear", m."TaxMonth", m."TotalTaxAmount"
FROM "WithholdingTaxCerts" m
JOIN "WithholdingTaxCerts" y
  ON y."CompanyId" = m."CompanyId" AND y."Id" <> m."Id" AND y."DocumentId" IS NOT NULL AND y."Status" <> 2 AND NOT y."IsDeleted"
 AND y."PayeeContactId" = m."PayeeContactId" AND y."TaxFormType" = m."TaxFormType"
 AND y."TaxYear" = m."TaxYear" AND y."TaxMonth" = m."TaxMonth" AND y."TotalTaxAmount" = m."TotalTaxAmount"
LEFT JOIN "Documents" d ON d."Id" = y."DocumentId" AND d."CompanyId" = y."CompanyId"
WHERE m."DocumentId" IS NULL AND m."SourcePayrollRunId" IS NULL AND m."Status" <> 2 AND NOT m."IsDeleted"
  AND m."NoSourceDocumentReason" IS NULL
ORDER BY m."CompanyId", m."TaxYear", m."TaxMonth", m."CertificateNumber";

-- (3) สรุปต่อบริษัท
SELECT c."CompanyId",
       (SELECT COUNT(*) FROM (
            SELECT 1 FROM "WithholdingTaxCerts" w
            WHERE w."CompanyId" = c."CompanyId" AND w."DocumentId" IS NOT NULL AND w."Status" <> 2 AND NOT w."IsDeleted"
            GROUP BY w."DocumentId", w."PayeeContactId", COALESCE(w."SourcePaymentId", '00000000-0000-0000-0000-000000000000'::uuid)
            HAVING COUNT(*) > 1) g) AS "DocPayeePaymentGroupsWithDuplicates",
       (SELECT COUNT(*) FROM "WithholdingTaxCerts" w
        WHERE w."CompanyId" = c."CompanyId" AND w."DocumentId" IS NULL AND w."SourcePayrollRunId" IS NULL
          AND w."Status" <> 2 AND NOT w."IsDeleted") AS "UnlinkedActiveCerts"
FROM (SELECT DISTINCT "CompanyId" FROM "WithholdingTaxCerts") c
ORDER BY 2 DESC, 3 DESC;

ROLLBACK;
