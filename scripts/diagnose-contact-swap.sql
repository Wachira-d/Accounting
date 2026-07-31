-- ============ ชุดตรวจ "ชื่อคู่ค้าเปลี่ยนเอง" (รันบน DBeaver ทีละ query) ============
-- แทน :CID ด้วย CompanyId ของบริษัทคุณ (หาได้จาก query 0)

-- 0) หา CompanyId
SELECT "Id", "Name" FROM "Companies" WHERE "Name" LIKE '%มังกร%';

-- 1) ⭐ ชี้ขาด: แถวรายงานที่ชื่อ "สมพงษ์" ชี้ไปเอกสารใบไหน และเอกสารนั้นคู่ค้าคือใคร
--    ถ้า TaxPayerName (snapshot) != c."Name" (ปัจจุบัน) = contact ถูกเปลี่ยน/ย้ายทีหลัง
--    ถ้าเท่ากันและ d."DocumentNumber" ไม่ใช่ PV-20260701-0012 = คนละใบ (ใบของสมพงษ์เอง
--    ที่ถูกกรอกเลขใบกำกับ 000755 ผิด)
SELECT l."TaxPayerName", l."TaxPayerId", l."TransactionDate", l."TaxAmount",
       d."DocumentNumber", d."SupplierInvoiceNumber", d."ContactId",
       c."Name" AS contact_now, c."TaxId" AS contact_tax_now, c."IsDeleted" AS contact_deleted,
       r."Year", r."Month", r."Status"
FROM "TaxReportLines" l
JOIN "TaxReports" r ON r."Id" = l."TaxReportId"
LEFT JOIN "Documents" d ON d."Id" = l."DocumentId"
LEFT JOIN "Contacts" c ON c."Id" = d."ContactId"
WHERE r."CompanyId" = :CID AND r."TaxType" = 0
  AND (l."TaxPayerName" LIKE '%สมพงษ์%' OR d."SupplierInvoiceNumber" = '000755')
ORDER BY r."Year", r."Month", l."LineOrder";

-- 2) เอกสารใบที่เป็นปัญหาชี้คู่ค้าไหนอยู่ตอนนี้
SELECT d."DocumentNumber", d."DocumentType", d."Status", d."SupplierInvoiceNumber",
       d."SupplierTaxInvoiceDate", d."UpdatedAt", d."ContactId",
       c."Name", c."TaxId", c."IsDeleted"
FROM "Documents" d LEFT JOIN "Contacts" c ON c."Id" = d."ContactId"
WHERE d."CompanyId" = :CID AND d."DocumentNumber" = 'PV-20260701-0012';

-- 3) มีกี่ใบที่ใช้เลขใบกำกับ 000755 (ซ้ำ = ต้นเหตุแถวปน)
SELECT d."DocumentNumber", d."Status", d."SupplierInvoiceNumber", d."SupplierTaxInvoiceDate",
       d."TotalAmount", c."Name", c."TaxId"
FROM "Documents" d LEFT JOIN "Contacts" c ON c."Id" = d."ContactId"
WHERE d."CompanyId" = :CID AND d."SupplierInvoiceNumber" IN ('000755','001733');

-- 4) ⭐ มีการ "รวมผู้ติดต่อ" เกิดขึ้นไหม (ถ้ามี = ตัวการ + ใช้ย้อนคืนได้)
SELECT "Timestamp", "EntityId" AS kept_contact, "OldValues", "NewValues"
FROM "AuditLogs"
WHERE "CompanyId" = :CID AND "EntityType" = 'ContactMerge'
ORDER BY "Timestamp" DESC;

-- 5) contact 2 รายนี้หน้าตาเป็นอย่างไร (ดู IsDeleted/UpdatedAt/UpdatedBy ว่าถูกแก้เมื่อไร โดยอะไร)
SELECT "Id", "Name", "TaxId", "BranchCode", "IsDeleted", "IsActive",
       "CreatedAt", "UpdatedAt", "UpdatedBy"
FROM "Contacts"
WHERE "CompanyId" = :CID
  AND ("Name" LIKE '%ปิโตรเลียม%' OR "Name" LIKE '%สมพงษ์%' OR "TaxId" IN ('0105535099511','3679900108913'))
ORDER BY "Name";

-- 6) contact ที่ถูกแก้ล่าสุดโดย OCR (UpdatedBy = OCR-Enrich/OCR-DbdEnrich) — เส้นที่เขียนทับเลขภาษี
SELECT "Name", "TaxId", "UpdatedAt", "UpdatedBy"
FROM "Contacts" WHERE "CompanyId" = :CID AND "UpdatedBy" LIKE 'OCR%'
ORDER BY "UpdatedAt" DESC LIMIT 30;

-- 7) เอกสารที่ชี้ contact ที่ถูกลบไปแล้ว (ร่องรอยการ merge/ลบ)
SELECT d."DocumentNumber", d."DocumentDate", c."Name", c."IsDeleted"
FROM "Documents" d JOIN "Contacts" c ON c."Id" = d."ContactId"
WHERE d."CompanyId" = :CID AND c."IsDeleted" = true
ORDER BY d."DocumentDate" DESC LIMIT 30;

-- 8) ⭐ ใครเปลี่ยน "ชื่อ/เลขภาษี" ของผู้ติดต่อ และกระทบเอกสารกี่ใบ
--    (มีข้อมูลตั้งแต่ commit ที่เพิ่ม audit นี้เป็นต้นไป — เคสเก่าดู UpdatedAt/UpdatedBy ใน query 5)
SELECT "Timestamp", "EntityId" AS contact_id, "OldValues", "NewValues"
FROM "AuditLogs"
WHERE "CompanyId" = :CID AND "EntityType" = 'ContactIdentity'
ORDER BY "Timestamp" DESC;
