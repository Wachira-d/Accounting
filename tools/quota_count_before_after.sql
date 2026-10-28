-- tools/quota_count_before_after.sql — เทียบโควตาเอกสาร "ก่อน-หลัง" กติการอบ 205 ทีม W1 (R-03/R-04) ทุกบริษัท · **อ่านอย่างเดียว**
--
-- ใช้:  psql "$DATABASE_URL" -v month='2026-10-01' -f tools/quota_count_before_after.sql
--       (month = วันแรกของเดือนที่ต้องการเทียบ · ไม่ส่ง = เดือนปัจจุบัน)
--
-- ทำไม: BACKLOG_REGISTER R-04 ขั้น 2 ("ย้ายทุกชนิดไปนับตอนอนุมัติ") อนุญาตเฉพาะเมื่อรายงานนี้ยืนยันว่า**ไม่มีบริษัทใดนับเพิ่ม** ·
--       ขั้น 1 (รอบนี้) ก็ต้องแนบผลเพื่อให้เห็นว่ากติกาใหม่ลดอย่างเดียว · ตัวนับจริง (Subscriptions.CurrentMonthDocuments) ไม่ถูกแตะย้อนหลัง
--       (LICENSING §13.1 ตัวนับไม่ลด) — รายงานนี้คำนวณจากเอกสารใหม่ ไม่ใช่จากตัวนับ
--
-- กติกาที่จำลอง (ต้องตรงกับ Helpers/DocumentQuotaPolicy.Classify — ถ้าแก้ Classify ให้แก้ที่นี่ในคอมมิตเดียว):
--   เดิม  : นับ TaxInvoice/Invoice/Receipt/Quotation/PO/PR ที่สร้างในเดือน ทุกสถานะ (รวมใบร่าง) ยกเว้นใบแทน §86/6 (ReplacesDocumentId)
--   ใหม่  : เดิม − ใบมัดจำใบที่ 2+ ของ BookingNumber เดียวกัน − ใบมัดจำลักษณะเงินประกัน (DepositNature=2) − ใบกำกับยอดริบ (Reference = เลขใบมัดจำ
--           + หมายเหตุริบ) − ใบจากต้นทางอัตโนมัติหน้าเว็บที่ยังเป็นร่าง/ถูกปฏิเสธ (OriginModule ∈ CmsBooking/site-order และ Status ∈ Draft/WaitingApproval/Rejected)
--   ⚠️ ยังไม่ได้รันกับฐานจริงในเครื่องนี้ (ไม่มี PostgreSQL) — เจ้าของรันบน staging/production (อ่านอย่างเดียว) แล้วแนบผลใน CHANGELOG
--
-- ผลลัพธ์: บริษัท · นับแบบเดิม · นับแบบใหม่ · ส่วนต่าง (ต้อง ≤ 0 ทุกแถว — มีแถวบวก = กติกาใหม่ทำให้นับเพิ่ม ⇒ ห้ามไปขั้น 2)

\set month_start :'month'
WITH params AS (
    SELECT COALESCE(NULLIF(:'month_start', '')::date, date_trunc('month', now() AT TIME ZONE 'Asia/Bangkok')::date) AS m0
),
-- เลข enum ตาม Models/Enums/AllEnums.cs (DocumentType) ณ 2026-10-26 — ถ้า enum เปลี่ยนให้แก้ที่นี่
type_map(name, val) AS (VALUES
    ('Quotation', 1), ('Invoice', 2), ('Receipt', 3), ('TaxInvoice', 4),
    ('PurchaseOrder', 7), ('PurchaseRequisition', 12)
),
counted_types AS (SELECT val FROM type_map),
docs AS (
    SELECT d."Id", d."CompanyId", d."DocumentType", d."Status", d."IsDeposit", d."BookingNumber", d."DepositNature",
           d."OriginModule", d."Reference", d."Notes", d."ReplacesDocumentId", d."CreatedAt",
           d."QuotaCountDeferred"
    FROM "Documents" d, params p
    WHERE d."IsDeleted" = false
      AND d."CreatedAt" >= (p.m0::timestamp AT TIME ZONE 'Asia/Bangkok')
      AND d."CreatedAt" <  ((p.m0 + interval '1 month')::timestamp AT TIME ZONE 'Asia/Bangkok')
      AND d."DocumentType" IN (SELECT val FROM counted_types)
),
old_rule AS (
    SELECT "CompanyId", count(*) AS n_old
    FROM docs
    WHERE "ReplacesDocumentId" IS NULL
    GROUP BY "CompanyId"
),
deposit_rank AS (
    SELECT "Id", row_number() OVER (PARTITION BY "CompanyId", "BookingNumber" ORDER BY "CreatedAt", "Id") AS rn
    FROM docs WHERE "IsDeposit" = true AND "BookingNumber" IS NOT NULL
),
deposit_numbers AS (
    SELECT d."CompanyId", d."DocumentNumber" FROM "Documents" d WHERE d."IsDeposit" = true AND d."IsDeleted" = false AND d."DocumentNumber" IS NOT NULL
),
new_rule AS (
    SELECT x."CompanyId", count(*) AS n_new
    FROM docs x
    WHERE x."ReplacesDocumentId" IS NULL
      -- R-03: ใบมัดจำใบที่ 2+ ของการจองเดียวกัน
      AND NOT EXISTS (SELECT 1 FROM deposit_rank r WHERE r."Id" = x."Id" AND r.rn > 1)
      -- R-03: เงินประกันที่ต้องคืน (DepositNature.RefundableSecurity = 2)
      AND NOT (x."IsDeposit" = true AND x."DepositNature" = 2)
      -- R-03: ใบกำกับยอดริบที่อ้างเลขใบมัดจำ (เส้นริบเขียน Reference = เลขใบมัดจำ + หมายเหตุ "ริบ")
      AND NOT (x."IsDeposit" = false AND x."Notes" ILIKE '%ริบ%' AND EXISTS (
            SELECT 1 FROM deposit_numbers dn WHERE dn."CompanyId" = x."CompanyId" AND dn."DocumentNumber" = x."Reference"))
      -- R-04 ขั้น 1: ใบจากเส้นอัตโนมัติหน้าเว็บที่ยังไม่อนุมัติ (นับตอนอนุมัติ)
      AND NOT (lower(coalesce(x."OriginModule", '')) IN ('cmsbooking', 'site-order') AND x."Status" IN (0, 1, 8))
    GROUP BY x."CompanyId"
)
SELECT c."Id" AS company_id, c."Name" AS company,
       coalesce(o.n_old, 0) AS counted_old_rule,
       coalesce(n.n_new, 0) AS counted_new_rule,
       coalesce(n.n_new, 0) - coalesce(o.n_old, 0) AS delta_must_be_le_0,
       s."CurrentMonthDocuments" AS counter_actual_untouched
FROM "Companies" c
LEFT JOIN old_rule o ON o."CompanyId" = c."Id"
LEFT JOIN new_rule n ON n."CompanyId" = c."Id"
LEFT JOIN "Subscriptions" s ON s."CompanyId" = c."Id" AND s."IsDeleted" = false
WHERE coalesce(o.n_old, 0) > 0 OR coalesce(n.n_new, 0) > 0
ORDER BY delta_must_be_le_0 DESC, company;
