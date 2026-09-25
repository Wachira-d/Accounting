using Microsoft.EntityFrameworkCore;

namespace Accounting.Data;

/// <summary>
/// Adds missing columns to existing tables that were created before
/// new entity properties were added. EnsureCreated() does not alter
/// existing tables, so we run ALTER TABLE … ADD COLUMN IF NOT EXISTS guards.
/// </summary>
public static class DatabaseMigrationHelper
{
    public static void ApplyMissingColumns(AccountingDbContext db, ILogger? logger = null)
    {
        var statements = GetAlterStatements();
        foreach (var sql in statements)
        {
            try
            {
                db.Database.ExecuteSqlRaw(sql);
            }
            catch (Exception ex)
            {
                // Existing-column / existing-index errors are expected and benign
                // (idempotent ALTER ADD COLUMN IF NOT EXISTS still triggers a
                // benign "already exists" on some PG versions). Anything else —
                // syntax error in a new statement, FK constraint refusing the
                // CREATE TABLE — used to silently disappear and we'd only learn
                // about it when runtime queries failed. Log everything so
                // genuine breakage is visible in the startup log.
                var msg = ex.Message ?? "";
                var benign = msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                          || msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
                if (!benign)
                    logger?.LogWarning(ex, "[DbMigration] Statement failed (continuing): {Sql}",
                        sql.Length > 200 ? sql[..200] + "…" : sql);
            }
        }
    }

    /// <summary>ฝ่ายค้านรอบสี่ R4-3 — สร้างคอลัมน์ <c>Documents.DepositBaseDeducted</c> และย้ายค่าเดิมจาก <c>BillDiscountAmount</c>
    /// <b>ในขั้นเดียวกับที่คอลัมน์ถูกสร้างเท่านั้น</b> (ตรวจ information_schema ก่อน · มีคอลัมน์แล้ว = ไม่ย้าย) · ครอบด้วย
    /// <c>pg_advisory_xact_lock</c> คีย์คงที่ (<see cref="Accounting.Helpers.AdvisoryLockKey"/>) ⇒ สองเครื่องบูตพร้อมกันย้ายครั้งเดียว.
    /// เดิมเป็น UPDATE ที่รันทุกครั้งที่บูต ⇒ จับแถวที่โค้ดใหม่สร้างได้ (อ้างเลขใบมัดจำ + ส่วนลดบาท · ฐานมัดจำ 0) แล้วย้ายส่วนลดการค้า
    /// เป็นฐานมัดจำ (R3-1 กลับมาเงียบ) และหักซ้ำเมื่อบูตพร้อมกัน.
    /// <para>ข้อมูลที่ต้องย้ายมาจากโค้ดรอบ 193 ก่อนแยกช่องเท่านั้น (ยังไม่อยู่ใน master): อนุมัติแล้ว = ยอดที่รับรู้มัดจำจริง
    /// (JE ผูก <c>DepositRealizedForDocumentId</c>) · ยังไม่อนุมัติ = ทั้งก้อน (ความหมายเดิม) · <b>ทุกใบที่ถูกย้ายติดหมายเหตุภายใน</b>
    /// <c>[DEPOSIT-BASE-SPLIT]</c> ให้ตรวจ — ก้อนเดิมอาจมีส่วนลดการค้ารวมอยู่ (แยกจากข้อมูลไม่ได้)</para></summary>
    internal static string DepositBaseSplitMigrationSql()
    {
        return """
        DO $mig$
        BEGIN
          PERFORM pg_advisory_xact_lock(__LOCK_KEY__);
          IF EXISTS (SELECT 1 FROM information_schema.columns
                      WHERE table_schema = current_schema() AND table_name = 'Documents' AND column_name = 'DepositBaseDeducted') THEN
            RETURN;
          END IF;
          ALTER TABLE "Documents" ADD COLUMN "DepositBaseDeducted" numeric(18,2) NOT NULL DEFAULT 0;
          WITH cand AS (
            SELECT d."Id",
              (d."Status" IN (2,3,4,5,7)) AS posted,
              CASE WHEN d."Status" IN (2,3,4,5,7) THEN LEAST(d."BillDiscountAmount", COALESCE((
                     SELECT SUM(j."TotalDebit") FROM "JournalEntries" j
                      WHERE j."CompanyId" = d."CompanyId" AND j."DepositRealizedForDocumentId" = d."Id"
                        AND j."OriginalEntryId" IS NULL AND j."ReversedByEntryId" IS NULL
                        AND j."Status" = 1 AND j."IsDeleted" = false), 0))
                   WHEN d."DepositAppliedAmount" <= 0 THEN d."BillDiscountAmount"
                   ELSE 0 END AS mv
              FROM "Documents" d
             WHERE d."BillDiscountAmount" > 0 AND d."IsDeposit" = false
               AND COALESCE(btrim(d."DepositAppliedRef"), '') <> ''
               AND EXISTS (
                 SELECT 1 FROM "Documents" p
                  WHERE p."CompanyId" = d."CompanyId" AND p."IsDeposit" = true AND p."IsDeleted" = false
                    AND p."Status" NOT IN (0, 6, 8) AND p."VatAmount" > 0.005
                    AND NOT (p."DepositOutputVatDeferred" = true AND p."DepositOutputVatRecognizedAt" IS NULL)
                    AND (p."DocumentNumber" IN (SELECT btrim(x) FROM unnest(string_to_array(d."DepositAppliedRef", ',')) x)
                      OR p."Reference" IN (SELECT btrim(x) FROM unnest(string_to_array(d."DepositAppliedRef", ',')) x)))
          )
          UPDATE "Documents" t
             SET "DepositBaseDeducted" = c.mv,
                 "BillDiscountAmount" = t."BillDiscountAmount" - c.mv,
                 "InternalNotes" = CASE WHEN COALESCE(t."InternalNotes", '') = '' THEN '' ELSE t."InternalNotes" || ' · ' END
                   || '[DEPOSIT-BASE-SPLIT] ย้าย ' || to_char(c.mv, 'FM999,999,999,990.00')
                   || CASE WHEN c.posted
                        THEN ' จากส่วนหักท้ายบิลเป็น “ฐานมัดจำที่หัก” ตามยอดที่รับรู้มัดจำไว้ (ระบบรุ่นก่อนแยกช่องเก็บส่วนลดการค้ากับฐานมัดจำรวมกัน) — ถ้ายอดนี้มีส่วนลดการค้ารวมอยู่ มัดจำลูกค้าถูกรับรู้เกิน ให้ตรวจที่หน้า “เงินมัดจำ”'
                        ELSE ' จากส่วนหักท้ายบิลเป็น “ฐานมัดจำที่หัก” ทั้งก้อน (ระบบรุ่นก่อนแยกช่อง) — ถ้าก้อนนี้มีส่วนลดการค้ารวมอยู่ ห้ามอนุมัติใบนี้: สร้างใบใหม่จากฟอร์ม (ขายเงินสดใบเดียว + เลือกมัดจำ + ส่วนลดท้ายบิล) แล้วลบร่างนี้'
                      END
            FROM cand c
           WHERE t."Id" = c."Id" AND c.mv > 0 AND t."DepositBaseDeducted" = 0;
        END
        $mig$;
        """.Replace("__LOCK_KEY__", DepositBaseSplitLockKey, StringComparison.Ordinal);
    }

    /// <summary>คีย์ล็อกของการสร้างคอลัมน์/ย้ายค่าข้างบน — deterministic ข้ามเครื่อง (FNV ผ่าน AdvisoryLockKey · ห้าม GetHashCode)</summary>
    internal static string DepositBaseSplitLockKey =>
        Accounting.Helpers.AdvisoryLockKey.For("db-migration", "Documents.DepositBaseDeducted").ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>รอบ 194 — ประเภทเงินมัดจำ (spec S1/S8 · plan §4) · <b>รันทุกบูตได้</b>:
    /// ① ตาราง <c>DepositKinds</c> + unique index (ต้องตรงกับ <c>AccountingDbContext</c> — ฐานใหม่ได้จาก EnsureCreated) ·
    /// ② คอลัมน์ใหม่บน Documents / LodgingProperties / LodgingReservations (ADD COLUMN IF NOT EXISTS · ค่าเดิม = NULL/0) ·
    /// ③ seed ประเภทเริ่มต้นทุกบริษัทจากตารางเดียว (<see cref="Accounting.Helpers.DepositKindSeed.MigrationSeedSql"/>) ·
    /// ④ ที่พักที่เคยตั้งโหมดเอง ⇒ ประเภท <c>lp:{id}</c> (ลักษณะ "ราคา" + โหมดเดิม + เหตุผลว่าย้ายมาจากค่าเดิม) ·
    /// ⑤ ผูกที่พักเข้ากับประเภทนั้นเฉพาะเมื่อ <c>RoomDepositKindId</c> ยังว่าง (หน้าตั้งค่าที่เลือก "ตามบริษัท" ต้องล้างค่าเดิมของที่พักด้วย
    /// ไม่งั้นบูตถัดไปผูกกลับ)
    /// <para>⚠️ <b>ห้าม UPDATE "Documents"</b> — ใบเดิมคง DepositKindId/DepositNature = NULL (= ไม่ทราบ · spec S1 ห้าม backfill) ·
    /// INSERT ใช้ ON CONFLICT DO NOTHING (กุญแจ SeedKey ต่อบริษัท) ⇒ แถวที่ผู้ใช้ลบแล้วไม่ถูก seed คืน</para></summary>
    internal static IReadOnlyList<string> DepositKindMigrationStatements()
    {
        const int price = (int)Accounting.Models.Enums.DepositNature.PartOfPrice;
        const int full = (int)Accounting.Models.Enums.DepositVatTreatment.FullDeposit;
        const int undue = (int)Accounting.Models.Enums.DepositVatTreatment.VatPendingUndue;
        var lp = Accounting.Helpers.DepositKindSeed.LodgingPropertySeedPrefix;
        return new[]
        {
            """CREATE TABLE IF NOT EXISTS "DepositKinds" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "Code" varchar(32) NOT NULL, "Name" varchar(256) NOT NULL, "Nature" integer NOT NULL DEFAULT 1, "VatTreatment" integer NULL, "LiabilityAccountCode" varchar(20) NULL, "ForfeitAccountCode" varchar(20) NULL, "PolicyReason" text NULL, "Description" text NULL, "IsDefault" boolean NOT NULL DEFAULT false, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "SeedKey" varchar(64) NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_DepositKinds_Company_SeedKey" ON "DepositKinds" ("CompanyId", "SeedKey") WHERE "SeedKey" IS NOT NULL;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_DepositKinds_Company_Code" ON "DepositKinds" ("CompanyId", "Code") WHERE "IsDeleted" = false;""",
            // รอบ 194 ทีม C — ประเภทเริ่มต้นตัวเดียวต่อบริษัท (seed ให้ ADVANCE ตัวเดียว · lp:{id} ไม่ใช่เริ่มต้น ⇒ ข้อมูลเดิมไม่ชน)
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_DepositKinds_Company_Default" ON "DepositKinds" ("CompanyId", "IsDefault") WHERE "IsDefault" = true AND "IsDeleted" = false;""",
            // ใบมัดจำตรึงประเภท/ลักษณะ/ชื่อ/หมายเหตุนโยบายตอนสร้าง — ใบเดิม NULL (ไม่ทราบ)
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositKindId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositNature" integer NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositKindName" text NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositPolicyNote" text NULL;""",
            // ที่พัก: ประเภทมัดจำค่าห้อง · ประเภท/ยอดเงินประกันความเสียหาย · ใบรับเงินประกันของการจอง
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "RoomDepositKindId" uuid NULL;""",
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "SecurityDepositKindId" uuid NULL;""",
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "SecurityDepositAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "SecurityDepositDocumentId" uuid NULL;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "SecurityDepositSettledAt" timestamptz NULL;""",
            Accounting.Helpers.DepositKindSeed.MigrationSeedSql(),
            $$"""INSERT INTO "DepositKinds" ("Id","CompanyId","Code","Name","Nature","VatTreatment","PolicyReason","Description","IsDefault","IsActive","SortOrder","SeedKey","CreatedAt","IsDeleted") SELECT gen_random_uuid(), p."CompanyId", 'LP-' || upper(left(replace(p."Id"::text, '-', ''), 8)), left('มัดจำค่าห้องพัก — ' || p."Name", 256), {{price}}, p."DepositVatTreatment", CASE WHEN p."DepositVatTreatment" IN ({{full}}, {{undue}}) THEN 'ย้ายจากค่าตั้งเดิมของที่พัก (ก่อนรอบ 194) — ผู้ใช้เลือกวิธีบันทึกนี้ไว้ก่อนมีประเภทเงินมัดจำ · ตรวจลักษณะเงินอีกครั้ง: มัดจำค่าห้องเป็นส่วนหนึ่งของราคา ภาษีถึงกำหนดตอนรับเงิน (มาตรา 78/1)' END, 'สร้างจากค่าตั้งเดิมของที่พัก ' || p."Name", false, true, 40, '{{lp}}' || p."Id"::text, now(), false FROM "LodgingProperties" p WHERE p."DepositVatTreatment" IS NOT NULL AND p."IsDeleted" = false ON CONFLICT DO NOTHING;""",
            $$"""UPDATE "LodgingProperties" p SET "RoomDepositKindId" = k."Id" FROM "DepositKinds" k WHERE k."CompanyId" = p."CompanyId" AND k."SeedKey" = '{{lp}}' || p."Id"::text AND k."IsDeleted" = false AND p."RoomDepositKindId" IS NULL AND p."DepositVatTreatment" IS NOT NULL;""",
        };
    }

    internal static List<string> GetAlterStatements()
    {
        return
        [
            // ===== Users: security fields =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "FailedLoginAttempts" integer NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "LockoutEnd" timestamp NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetToken" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordResetTokenExpiry" timestamp NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "EmailVerified" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PasswordWeakDetectedAt" timestamp NULL;
            """,

            // ===== Users: Signature fields (auto-stamped on documents) =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignatureImageBase64" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignatureName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "SignatureTitle" varchar(200) NULL;
            """,

            // ===== Contacts: structured address fields (ETDA-compliant) =====
            // Pre-existing schema had only `Address` text. New columns enable proper
            // ETDA Schematron-conformant XML (BuildingNumber/CityName/CitySubDivisionName/
            // CountrySubDivisionID required when CountryID=TH).
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BranchName" varchar(200) NULL;
            """,
            // Contact LINE binding — ส่งเอกสารผ่าน LINE flex message ให้ลูกค้า
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "LineUserId" varchar(100) NULL;""",
            // ภาษาเอกสารเริ่มต้นต่อผู้ติดต่อ (th/en, null = ตามค่าบริษัท) — ประทับ
            // ลงใบตอนสร้าง ไม่ resolve ตอนพิมพ์ (ดู comment ที่ Contact entity)
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DocumentLanguage" varchar(5) NULL;""",
            // ที่อยู่บริษัทภาษาอังกฤษ (เอกสารโหมด en) — null = ถอดอักษรอัตโนมัติ
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "AddressEn" varchar(500) NULL;""",
            // ชื่อ/ที่อยู่ภาษาอังกฤษของ **คู่ค้า** — คู่ขนานกับฝั่งบริษัท. เดิมมีแต่
            // ฝั่งเรา ทำให้ที่อยู่ลูกค้า/ผู้ขายบนใบภาษาอังกฤษถูกถอดอักษรอัตโนมัติ
            // เสมอและแก้ทับไม่ได้เลย (ThaiRomanizer เป็น RTGS แบบประมาณ ชื่อ
            // ตำบล/อำเภอ/ถนนสะกดเพี้ยนได้เป็นปกติ)
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "TitleTh" varchar(50) NULL;""",
            """ALTER TABLE "TaxReportLines" ADD COLUMN IF NOT EXISTS "TaxPayerTitle" varchar(50) NULL;""",
            """ALTER TABLE "TaxReportLines" ADD COLUMN IF NOT EXISTS "TaxPayerBranchCode" varchar(5) NULL;""",
            """ALTER TABLE "TaxReportLines" ADD COLUMN IF NOT EXISTS "WhtCondition" integer NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "NameEn" varchar(300) NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "AddressEn" varchar(500) NULL;""",
            // ร่องรอยการ sync/ตรวจข้อมูลจากระบบภายนอก — **ภายในเท่านั้น** ห้ามพิมพ์ลงเอกสาร
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "InternalNotes" text NULL;""",
            // Integration external id — match ผู้ติดต่อเดิมเวลา sync กัน contact ซ้ำ
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "ExternalId" varchar(200) NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(100) NULL;""",
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_CompanyId_ExternalSystem_ExternalId"
                ON "Contacts" ("CompanyId", "ExternalSystem", "ExternalId")
                WHERE "ExternalId" IS NOT NULL;
            """,
            // Normalize เลขผู้เสียภาษีเดิมให้เป็นตัวเลขล้วน (ตัด -, เว้นวรรค) ครั้งเดียว
            // → match แบบ digits-only ใน integration ทำงานถูก + กัน contact ซ้ำจาก
            // รูปแบบเลขต่างกัน ("0-1055-..." vs "0105512..."). TaxId semantics = ตัวเลข.
            """
            UPDATE "Contacts" SET "TaxId" = regexp_replace("TaxId", '[^0-9]', '', 'g')
            WHERE "TaxId" IS NOT NULL AND "TaxId" ~ '[^0-9]';
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BuildingNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BuildingName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "StreetName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "SubDistrict" varchar(100) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "District" varchar(100) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "Province" varchar(100) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "PostalCode" varchar(10) NULL;
            """,
            """
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "CountryCode" varchar(3) NOT NULL DEFAULT 'TH';
            """,
            // ลูกค้ารายนี้ออกใบกำกับภาษีเสมอ (pre-select TaxInvoice ตอนสร้างเอกสาร)
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultIssueTaxInvoice" boolean NOT NULL DEFAULT false;""",
            // เครดิตเทอมต่อลูกค้า — เติมวันครบกำหนดอัตโนมัติตอนสร้างเอกสารขาย
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "PaymentDueDays" integer NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "PaymentTerms" varchar(100) NULL;""",

            // ===== Companies: structured address — add missing BuildingNumber/Name/StreetName =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BuildingNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BuildingName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "StreetName" varchar(200) NULL;
            """,

            // ===== Users: onboarding tour dismissal (ปิดการสอนถาวร ต่อ user) =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "DismissedToursJson" text NULL;
            """,

            // ===== Companies: suspend metadata (WP-A2 enforcement) =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "SuspendReason" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "SuspendedAt" timestamptz NULL;
            """,

            // ===== Documents: currency field =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "Currency" varchar(3) NOT NULL DEFAULT 'THB';
            """,

            // ===== Documents: PaymentType (Cash vs Credit settlement basis) =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentType" int NULL;
            """,

            // ===== Documents: PricesIncludeVat (VAT-inclusive line pricing) =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PricesIncludeVat" boolean NOT NULL DEFAULT false;
            """,

            // ===== OcrScanResults: ล้าง "ตัวชี้ค้าง" ไปยังเอกสารที่ถูกลบไปแล้ว =====
            // DeleteDocumentAsync ลบแถว Documents ทิ้งจริง (hard delete ใบร่าง) แต่เดิม
            // ไม่เคยแตะ OcrScanResult.CreatedDocumentId ซึ่งไม่มี FK ⇒ การ์ดบนหน้า OCR
            // ติดป้าย "สร้างแล้ว" ค้าง · กดแล้วไม่พบเอกสาร · สร้างใหม่จากสแกนเดิมไม่ได้
            // เพราะด่านกันซ้ำอ่านช่องนี้ (ผู้ใช้รายงาน 2026-09-18)
            // — แก้โค้ดอย่างเดียวไม่พอ แถวที่พังไปแล้วต้องถูกซ่อมด้วย (หลักการข้อ 9)
            """
            UPDATE "OcrScanResults" s
               SET "CreatedDocumentId" = NULL,
                   "ProcessingNotes" = COALESCE(s."ProcessingNotes", '')
                       || E'\n[Unlink] เอกสารที่สร้างจากสแกนนี้ถูกลบไปแล้ว — สแกนกลับไปสถานะ "ยังไม่ได้สร้างเอกสาร" และสร้างใหม่ได้'
             WHERE s."CreatedDocumentId" IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM "Documents" d WHERE d."Id" = s."CreatedDocumentId");
            """,

            // ===== Documents: คำแนะนำหัก ณ ที่จ่าย จากชั้นเรียนรู้ (กฎเหล็ก #1) =====
            // FeedbackId เก็บไว้ปิดวงจรตอนผู้ใช้กดอนุมัติ — ไม่เก็บ = ถามแล้วไม่เคยรู้
            // ว่าคำตอบถูกไหม ⇒ นักเรียนไม่มีวันโต
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtAdviceAiFeedbackId" uuid NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtAdviceAnswer" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtAdviceUsedAi" boolean NOT NULL DEFAULT false;
            """,

            // ===== DocumentLines: ProductCode =====
            """
            ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "ProductCode" varchar(50) NULL;
            """,

            // ===== Documents: per-document appendix/notes overrides =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomAppendix" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomFooterNotes" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomTermsAndConditions" text NULL;
            """,

            // ===== Documents: Revenue Contract auto-link =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RevenueContractId" uuid NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PerformanceObligationId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_RevenueContractId" ON "Documents" ("RevenueContractId");
            """,

            // ===== Documents: Project linking (header-level) =====
            // Tags an entire document to a project — propagates to JE.ProjectId
            // on auto-post so per-project P&L picks up revenue/cost automatically.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_ProjectId" ON "Documents" ("ProjectId");
            """,

            // ===== DocumentLines: per-line project override =====
            // Allows a single document to bill multiple projects (e.g. shared invoice).
            """
            ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_DocumentLines_ProjectId" ON "DocumentLines" ("ProjectId");
            """,

            // ===== CompanySettings: e-Tax fields =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxEnabled" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ShowProjectOnDocuments" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ShowCostCenterOnDocuments" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "UseCustomAuthorizedSignatory" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AuthorizedSignatoryName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AuthorizedSignatoryTitle" varchar(200) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AuthorizedSignatorySignatureBase64" text NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxCertificatePath" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxCertificatePassword" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxRdApiKey" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxRdApiSecret" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxTestMode" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxAutoSign" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxAutoSubmit" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AutoAttachWhtCertPdf" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxServiceProvider" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxXmlOutputPath" varchar(500) NULL;
            """,

            // ===== ตราประทับบริษัท (company seal) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "StampPath" varchar(500) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "StampUrl" varchar(500) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "StampWidthMm" numeric(6,2) NOT NULL DEFAULT 32;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "StampHeightMm" numeric(6,2) NOT NULL DEFAULT 32;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "StampAlign" varchar(20) NOT NULL DEFAULT 'Right';""",

            // ===== Recurring late-fee accrual policy =====
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeRatePerDay" numeric(8,4) NOT NULL DEFAULT 0.05;""",
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeGraceDays" int NOT NULL DEFAULT 7;""",
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "LateFeeMaxPercent" numeric(5,2) NULL DEFAULT 20.0;""",

            // ===== Recurring: ส่งอีเมลเอกสารให้ผู้ติดต่ออัตโนมัติเมื่อสร้าง =====
            """ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "AutoSendEmail" boolean NOT NULL DEFAULT false;""",

            // ===== ใบวางบิลรวมใบแจ้งหนี้: บรรทัด "แทนทั้งใบ" ชี้เอกสารต้นทาง =====
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "SourceDocumentId" uuid NULL;""",

            // ===== SSO / OAuth ตั้งจากหน้าแอดมินได้ (เดิมมีแต่ appsettings) =====
            """ALTER TABLE "HelpResources" ADD COLUMN IF NOT EXISTS "IsPublic" boolean NOT NULL DEFAULT false;""",
            // ===== คู่มือสอนใช้งานเป็นสาธารณะ (เจ้าของระบบสั่ง 2026-09-08) =====
            // เดิม default=false ⇒ เส้น /api/help/public ไม่เคยมีเนื้อหาเลยสักชิ้น
            // แถวเก่าที่เผยแพร่แล้วต้องเปิดตามด้วย ไม่งั้นแก้โค้ดอย่างเดียวไม่พอ
            """ALTER TABLE "HelpResources" ALTER COLUMN "IsPublic" SET DEFAULT true;""",
            """UPDATE "HelpResources" SET "IsPublic" = true WHERE "IsDeleted" = false AND "IsPublished" = true AND "IsPublic" = false;""",
            """ALTER TABLE "HelpResources" ADD COLUMN IF NOT EXISTS "Slug" varchar(120) NULL;""",
            """ALTER TABLE "HelpResources" ADD COLUMN IF NOT EXISTS "Body" text NULL;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_HelpResources_Slug" ON "HelpResources" ("Slug") WHERE "Slug" IS NOT NULL AND "IsDeleted" = false;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "ContactAddress" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "BusinessHours" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "GoogleLoginEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "GoogleClientId" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "GoogleClientSecret" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FacebookLoginEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FacebookAppId" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FacebookAppSecret" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineLoginEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineLoginChannelId" text NULL;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LineLoginChannelSecret" text NULL;""",

            // ===== POS deposit support — IsDeposit + DepositRealizedAt =====
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "IsDeposit" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "DepositRealizedAt" timestamp with time zone NULL;""",

            // ===== PDPA ม.26 — widen Employee PII columns เพื่อรองรับ ciphertext =====
            // (Base64 of nonce 12 + ciphertext 13 + tag 16 = ~56 chars + prefix)
            """ALTER TABLE "Employees" ALTER COLUMN "CitizenId" TYPE varchar(200);""",
            """ALTER TABLE "Employees" ALTER COLUMN "TaxId" TYPE varchar(200);""",
            """ALTER TABLE "Employees" ALTER COLUMN "PassportNumber" TYPE varchar(200);""",

            // ===== CompanySettings: §82/5(6) vehicle dealer override =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "IsVehicleDealer" boolean NOT NULL DEFAULT false;
            """,

            // ===== Companies: IndustryType =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IndustryType" integer NOT NULL DEFAULT 0;
            """,

            // ===== Users: SSO columns =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "AuthProvider" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "AuthProviderId" text NULL;
            """,
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "IsSystemAdmin" boolean NOT NULL DEFAULT false;
            """,

            // ===== Companies: Business Registration & Setup =====
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BranchName" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "JuristicId" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsVatRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "VatRate" decimal(18,2) NOT NULL DEFAULT 7;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsWhtRegistered" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsSocialSecurityRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "SocialSecurityAccountNo" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "Fax" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "Website" text NULL;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsSetupComplete" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BaseCurrency" varchar(10) NOT NULL DEFAULT 'THB';
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BusinessType" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "FiscalYearStartMonth" int NOT NULL DEFAULT 1;
            """,

            // ===== DocumentLines: WHT income type =====
            """
            ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "IncomeTypeCode" text NULL;
            """,

            // ===== SubscriptionPayments: admin manual/waived recording (WP-C1) =====
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "WaiveReason" integer NULL;
            """,

            // ===== SubscriptionPayments: SaaS receipt/tax-invoice (WP-B2) =====
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ReceiptNumber" text NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ReceiptAttachmentId" uuid NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ReceiptIssuedAt" timestamptz NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "ReceiptIsTaxInvoice" boolean NOT NULL DEFAULT false;
            """,

            // ===== SubscriptionPayments: slip OCR assist (WP-C3) =====
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "SlipOcrAmount" decimal(18,2) NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "SlipOcrDate" timestamptz NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "SlipOcrReference" text NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "SlipOcrAmountMatches" boolean NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "SlipOcrParsedAt" timestamptz NULL;
            """,

            // ===== SiteSettings: platform billing seller identity (WP-B2) =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformSellerName" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformSellerTaxId" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformSellerBranchCode" text NULL DEFAULT '00000';
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformSellerAddress" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformSellerPhone" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformSellerEmail" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformIsVatRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformPriceIncludesVat" boolean NOT NULL DEFAULT true;
            """,

            // ===== ออกเอกสารค่าบริการผ่าน tenant ของผู้ให้บริการ (ACCOUNT_STRUCTURE §6.1) =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformCompanyId" uuid NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformRevenueAccountCode" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PlatformCashAccountCode" text NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "PlatformDocumentId" uuid NULL;
            """,
            """
            ALTER TABLE "SubscriptionPayments" ADD COLUMN IF NOT EXISTS "WithholdingTaxAmount" numeric(18,2) NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalInvoiceDocumentId" uuid NULL;
            """,
            // §86/9 เหตุผลใบเพิ่มหนี้ (คู่กับ CreditNoteReason ของ §86/10)
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DebitNoteReason" integer NULL;
            """,

            // ===== Subscriptions: renewal invoice (WP-B1) =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalInvoiceNumber" text NULL;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalInvoiceIssuedAt" timestamptz NULL;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalInvoiceForEndDate" timestamptz NULL;
            """,

            // ===== Subscriptions: Notification settings =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBeforeExpiry" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyDaysBeforeExpiry" text NOT NULL DEFAULT '30,15,7,3,1';
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyOnExpiry" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyAfterExpiry" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyDaysAfterExpiry" text NOT NULL DEFAULT '1,3,7';
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "DeactivationDaysAfterExpiry" integer NOT NULL DEFAULT 14;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyBeforeDeactivation" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "NotifyDaysBeforeDeactivation" text NOT NULL DEFAULT '7,3,1';
            """,

            // ===== Subscriptions / PlanTemplates: Permanent free flag =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "IsPermanentFree" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "IsPermanentFree" boolean NOT NULL DEFAULT false;
            """,

            // ===== JournalEntries: Project / branch / dimension / metadata / source doc =====
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "DimensionId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "Note" varchar(2000) NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "Tags" varchar(500) NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "DimensionId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntryLines" ADD COLUMN IF NOT EXISTS "Tags" varchar(500) NULL;
            """,

            // ===== CompanySettings: Cross-tenant knowledge sharing =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ShareTrainingDataAnonymously" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OwnTrainingBonusMultiplier" decimal(4,2) NOT NULL DEFAULT 2.0;
            """,

            // ===== CompanySettings: Landing page services =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingContactPhone" varchar(50) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingContactLine" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingContactEmail" varchar(200) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LandingServicesJson" text NULL;
            """,

            // ===== POS Tables =====
            """
            CREATE TABLE IF NOT EXISTS "PosTerminals" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(200) NOT NULL,
                "BusinessMode" integer NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Location" varchar(500) NULL,
                "SettingsJson" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosTerminals" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosTerminals_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosSessions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TerminalId" uuid NOT NULL,
                "OpenedByUserId" uuid NOT NULL,
                "ClosedByUserId" uuid NULL,
                "OpenedAt" timestamp NOT NULL DEFAULT now(),
                "ClosedAt" timestamp NULL,
                "OpeningBalance" decimal(18,2) NOT NULL,
                "ClosingBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "ExpectedBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 1,
                "Notes" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosSessions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosSessions_PosTerminals" FOREIGN KEY ("TerminalId") REFERENCES "PosTerminals"("Id"),
                CONSTRAINT "FK_PosSessions_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosOrders" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "SessionId" uuid NOT NULL,
                "OrderNumber" varchar(50) NOT NULL,
                "OrderType" integer NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "CustomerId" uuid NULL,
                "CustomerName" varchar(500) NULL,
                "TableNumber" varchar(50) NULL,
                "GuestCount" integer NULL,
                "QueueNumber" varchar(50) NULL,
                "AppointmentTime" timestamp NULL,
                "PrimaryStaffId" uuid NULL,
                "SubTotal" decimal(18,2) NOT NULL DEFAULT 0,
                "DiscountAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "DiscountPercent" decimal(5,2) NOT NULL DEFAULT 0,
                "ServiceChargePercent" decimal(5,2) NOT NULL DEFAULT 0,
                "ServiceChargeAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "VatAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "TotalAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "RoundingAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "NetAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "Reference" varchar(200) NULL,
                "JournalEntryId" uuid NULL,
                "DocumentId" uuid NULL,
                "CompletedAt" timestamp NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosOrders" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosOrders_PosSessions" FOREIGN KEY ("SessionId") REFERENCES "PosSessions"("Id"),
                CONSTRAINT "FK_PosOrders_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ServicePackages" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(500) NOT NULL,
                "NameEn" varchar(500) NULL,
                "Description" text NULL,
                "Sku" varchar(50) NULL,
                "Category" varchar(200) NULL,
                "Price" decimal(18,2) NOT NULL,
                "CostPrice" decimal(18,2) NULL,
                "DurationMinutes" integer NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "IsVatIncluded" boolean NOT NULL DEFAULT true,
                "RevenueAccountId" uuid NULL,
                "ImageUrl" text NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ServicePackages" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ServicePackages_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ServiceComponents" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PackageId" uuid NOT NULL,
                "StepOrder" integer NOT NULL,
                "Name" varchar(500) NOT NULL,
                "NameEn" varchar(500) NULL,
                "Description" text NULL,
                "DurationMinutes" integer NOT NULL,
                "CommissionType" integer NOT NULL DEFAULT 1,
                "CommissionValue" decimal(18,2) NOT NULL DEFAULT 0,
                "RequiresStaff" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ServiceComponents" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ServiceComponents_ServicePackages" FOREIGN KEY ("PackageId") REFERENCES "ServicePackages"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosOrderItems" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderId" uuid NOT NULL,
                "ProductId" uuid NULL,
                "ServicePackageId" uuid NULL,
                "ItemName" varchar(500) NOT NULL,
                "ItemCode" varchar(50) NULL,
                "Quantity" decimal(18,4) NOT NULL DEFAULT 1,
                "Unit" varchar(50) NULL,
                "UnitPrice" decimal(18,2) NOT NULL,
                "DiscountAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "DiscountPercent" decimal(5,2) NOT NULL DEFAULT 0,
                "SubTotal" decimal(18,2) NOT NULL DEFAULT 0,
                "VatAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "TotalAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "LineOrder" integer NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosOrderItems" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosOrderItems_PosOrders" FOREIGN KEY ("OrderId") REFERENCES "PosOrders"("Id"),
                CONSTRAINT "FK_PosOrderItems_Products" FOREIGN KEY ("ProductId") REFERENCES "Products"("Id"),
                CONSTRAINT "FK_PosOrderItems_ServicePackages" FOREIGN KEY ("ServicePackageId") REFERENCES "ServicePackages"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosOrderItemModifiers" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderItemId" uuid NOT NULL,
                "ModifierOptionId" uuid NULL,
                "ModifierGroupName" varchar(200) NOT NULL,
                "ModifierName" varchar(200) NOT NULL,
                "PriceAdjustment" decimal(18,2) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosOrderItemModifiers" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosOrderItemModifiers_PosOrderItems" FOREIGN KEY ("OrderItemId") REFERENCES "PosOrderItems"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosPayments" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderId" uuid NOT NULL,
                "PaymentMethod" integer NOT NULL,
                "Amount" decimal(18,2) NOT NULL,
                "ReceivedAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "ChangeAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "ReferenceNo" varchar(200) NULL,
                "CardLastFour" varchar(4) NULL,
                "PaidAt" timestamp NOT NULL DEFAULT now(),
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosPayments" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosPayments_PosOrders" FOREIGN KEY ("OrderId") REFERENCES "PosOrders"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosServiceActivities" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OrderItemId" uuid NOT NULL,
                "ComponentId" uuid NOT NULL,
                "StaffId" uuid NULL,
                "StaffName" varchar(200) NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "StartedAt" timestamp NULL,
                "CompletedAt" timestamp NULL,
                "CommissionAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosServiceActivities" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosServiceActivities_PosOrderItems" FOREIGN KEY ("OrderItemId") REFERENCES "PosOrderItems"("Id"),
                CONSTRAINT "FK_PosServiceActivities_ServiceComponents" FOREIGN KEY ("ComponentId") REFERENCES "ServiceComponents"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ProductModifierGroups" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "IsRequired" boolean NOT NULL DEFAULT false,
                "AllowMultiple" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductModifierGroups" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductModifierGroups_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ProductModifierGroupLinks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ProductId" uuid NOT NULL,
                "ModifierGroupId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductModifierGroupLinks" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductModifierGroupLinks_Products" FOREIGN KEY ("ProductId") REFERENCES "Products"("Id"),
                CONSTRAINT "FK_ProductModifierGroupLinks_Groups" FOREIGN KEY ("ModifierGroupId") REFERENCES "ProductModifierGroups"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ProductModifierOptions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "GroupId" uuid NOT NULL,
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "PriceAdjustment" decimal(18,2) NOT NULL DEFAULT 0,
                "IsDefault" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductModifierOptions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductModifierOptions_Groups" FOREIGN KEY ("GroupId") REFERENCES "ProductModifierGroups"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "StaffCommissionSummaries" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "StaffId" uuid NOT NULL,
                "StaffName" varchar(200) NOT NULL,
                "PeriodStart" timestamp NOT NULL,
                "PeriodEnd" timestamp NOT NULL,
                "TotalActivities" integer NOT NULL DEFAULT 0,
                "TotalCommission" decimal(18,2) NOT NULL DEFAULT 0,
                "PaidAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "RemainingAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "IsPaid" boolean NOT NULL DEFAULT false,
                "JournalEntryId" uuid NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_StaffCommissionSummaries" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_StaffCommissionSummaries_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,

            // Contact Inquiries (public contact form)
            """
            -- ===== JournalEntries: JournalType column =====
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "JournalType" integer NOT NULL DEFAULT 0;
            """,
            """
            -- ===== WithholdingTaxCerts: DocumentId column =====
            ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "DocumentId" uuid NULL;
            """,
            """
            -- ===== Contacts: ContactType column =====
            ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "ContactType" integer NOT NULL DEFAULT 1;
            """,
            """
            -- ===== Auto-classify existing contacts: BranchCode != NULL/00000 → JuristicPerson =====
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE lower(table_name)='contacts' AND lower(column_name)='contacttype') THEN
                UPDATE "Contacts" SET "ContactType" = 2
                WHERE "ContactType" = 1
                  AND "BranchCode" IS NOT NULL AND "BranchCode" <> '' AND "BranchCode" <> '00000';
            END IF;
            END $$;
            """,
            """
            CREATE TABLE IF NOT EXISTS "ContactInquiries" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(500) NOT NULL,
                "Email" varchar(500) NOT NULL,
                "Phone" varchar(50) NULL,
                "Company" varchar(500) NULL,
                "Subject" varchar(1000) NOT NULL,
                "Message" text NOT NULL,
                "IsRead" boolean NOT NULL DEFAULT false,
                "IsReplied" boolean NOT NULL DEFAULT false,
                "IpAddress" varchar(100) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ContactInquiries" PRIMARY KEY ("Id")
            );
            """,

            // ===== JournalEntries: Reversal tracking =====
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "ReversedByEntryId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "OriginalEntryId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "DepositAppliedToDocumentId" uuid NULL;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "DepositRealizedForDocumentId" uuid NULL;
            """,

            // ===== SiteSettings: global site configuration (singleton) =====
            // ===== BankTransactions: AI Reconciliation MatchGroupId =====
            """
            ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "MatchGroupId" text NULL;
            """,
            // ===== BankTransactions: many-to-one matched entry IDs (JSON array) =====
            """
            ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "MatchedEntryIdsJson" text NULL;
            """,
            // ===== BankTransactions: link to true M:N reconciliation group =====
            """
            ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "ReconciliationGroupId" uuid NULL;
            """,
            // ===== ReconciliationGroups: header table for M:N + net-off matching =====
            """
            CREATE TABLE IF NOT EXISTS "ReconciliationGroups" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankAccountId" uuid NOT NULL,
                "GroupNumber" varchar(40) NOT NULL,
                "ReconciledDate" timestamp with time zone NOT NULL,
                "TotalBankAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "TotalMatchedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "IsBalanced" boolean NOT NULL DEFAULT false,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconciliationGroups_CompanyId_GroupNumber"
                ON "ReconciliationGroups" ("CompanyId", "GroupNumber");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationGroups_BankAccountId"
                ON "ReconciliationGroups" ("BankAccountId");
            """,
            // ===== ReconciliationGroupItems: line items (polymorphic) =====
            """
            CREATE TABLE IF NOT EXISTS "ReconciliationGroupItems" (
                "Id" uuid PRIMARY KEY,
                "GroupId" uuid NOT NULL REFERENCES "ReconciliationGroups"("Id") ON DELETE CASCADE,
                "ItemType" int NOT NULL,
                "ItemId" uuid NOT NULL,
                "AllocatedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationGroupItems_GroupId"
                ON "ReconciliationGroupItems" ("GroupId");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_ReconciliationGroupItems_ItemType_ItemId"
                ON "ReconciliationGroupItems" ("ItemType", "ItemId");
            """,
            // ===== BankReconciliationPatterns: AI learning store =====
            """
            CREATE TABLE IF NOT EXISTS "BankReconciliationPatterns" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankAccountId" uuid NOT NULL,
                "DescriptionSignature" varchar(500) NOT NULL,
                "AmountBucket" varchar(20) NOT NULL,
                "TargetType" int NOT NULL,
                "ContactId" uuid NULL,
                "TargetAccountCode" varchar(20) NULL,
                "TimesConfirmed" int NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "AvgAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "MinAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "MaxAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_BankReconciliationPatterns_Lookup"
                ON "BankReconciliationPatterns" ("CompanyId", "BankAccountId", "DescriptionSignature", "AmountBucket");
            """,

            // ===== ChequeBook + Cheque (Thai SME cheque management) =====
            """
            CREATE TABLE IF NOT EXISTS "ChequeBooks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "BankAccountId" uuid NOT NULL,
                "BookNumber" varchar(50) NOT NULL,
                "StartChequeNumber" bigint NOT NULL,
                "EndChequeNumber" bigint NOT NULL,
                "NextNumber" bigint NOT NULL,
                "ReceivedFromBankAt" timestamp NOT NULL DEFAULT now(),
                "ExhaustedAt" timestamp NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ChequeBooks" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ChequeBooks_BankAccount" FOREIGN KEY ("BankAccountId") REFERENCES "BankAccounts"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ChequeBooks_Company_Bank" ON "ChequeBooks" ("CompanyId", "BankAccountId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "Cheques" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ChequeBookId" uuid NULL,
                "ChequeNumber" bigint NOT NULL,
                "ChequeDate" timestamp NOT NULL,
                "Amount" decimal(18,2) NOT NULL,
                "Currency" varchar(3) NOT NULL DEFAULT 'THB',
                "ContactId" uuid NULL,
                "IssuingBankName" varchar(100) NULL,
                "PaymentId" uuid NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "ClearedAt" timestamp NULL,
                "BounceReason" text NULL,
                "ReplacesChequeId" uuid NULL,
                "IsInbound" boolean NOT NULL DEFAULT false,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_Cheques" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_Cheques_Book" FOREIGN KEY ("ChequeBookId") REFERENCES "ChequeBooks"("Id"),
                CONSTRAINT "FK_Cheques_Replaces" FOREIGN KEY ("ReplacesChequeId") REFERENCES "Cheques"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Cheques_Company_Status" ON "Cheques" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_Cheques_Payment" ON "Cheques" ("PaymentId") WHERE "PaymentId" IS NOT NULL AND "IsDeleted" = false;""",

            // ===== StampDutyRecords (อากรแสตมป์) =====
            """
            CREATE TABLE IF NOT EXISTS "StampDutyRecords" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Reference" varchar(100) NOT NULL,
                "ContactId" uuid NULL,
                "InstrumentEntityType" varchar(50) NULL,
                "InstrumentEntityId" uuid NULL,
                "RdScheduleNumber" integer NOT NULL,
                "InstrumentType" varchar(50) NOT NULL,
                "InstrumentValue" decimal(18,2) NOT NULL,
                "DutyAmount" decimal(18,2) NOT NULL,
                "InstrumentDate" timestamp NOT NULL,
                "PaymentMethod" varchar(20) NOT NULL DEFAULT 'ESD',
                "PaidAt" timestamp NULL,
                "RdReceiptNumber" varchar(50) NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_StampDutyRecords" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_StampDutyRecords_Company" ON "StampDutyRecords" ("CompanyId", "InstrumentDate") WHERE "IsDeleted" = false;""",

            // ===== Product costing extension =====
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "CostingMethod" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "AverageUnitCost" decimal(18,4) NOT NULL DEFAULT 0;""",

            // ===== Petty cash + stock count =====
            """
            CREATE TABLE IF NOT EXISTS "PettyCashFunds" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Name" varchar(100) NOT NULL,
                "CustodianUserId" uuid NULL,
                "ImprestAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "CurrentBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "LinkedAccountId" uuid NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PettyCashFunds" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PettyCashTransactions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FundId" uuid NOT NULL,
                "TransactionDate" timestamp NOT NULL,
                "Amount" decimal(18,2) NOT NULL,
                "Type" varchar(20) NOT NULL,
                "Description" text NOT NULL,
                "ReceiptReference" varchar(100) NULL,
                "ExpenseAccountId" uuid NULL,
                "JournalEntryId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PettyCashTransactions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PettyCashTransactions_Fund" FOREIGN KEY ("FundId") REFERENCES "PettyCashFunds"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PettyCashTransactions_Fund_Date" ON "PettyCashTransactions" ("FundId", "TransactionDate") WHERE "IsDeleted" = false;""",
            // StockCount table already exists from prior schema; just
            // add the UnitCost column on the line we need for variance JE.
            """ALTER TABLE "StockCountLines" ADD COLUMN IF NOT EXISTS "UnitCost" decimal(18,4) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "StockCounts" ADD COLUMN IF NOT EXISTS "CountType" varchar(20) NOT NULL DEFAULT 'Full';""",

            // ===== PDPA + BOM + Consignment (Tier 3) =====
            """
            CREATE TABLE IF NOT EXISTS "PdpaDataSubjectRequests" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "RequestNumber" varchar(50) NOT NULL,
                "RequestedAt" timestamp NOT NULL DEFAULT now(),
                "RequesterContact" varchar(200) NOT NULL,
                "RequesterName" varchar(200) NULL,
                "LinkedUserId" uuid NULL,
                "LinkedContactId" uuid NULL,
                "RequestType" varchar(30) NOT NULL,
                "Description" text NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "DueBy" timestamp NOT NULL,
                "CompletedAt" timestamp NULL,
                "CompletionNote" text NULL,
                "AssignedDpoUserId" uuid NULL,
                "RejectionReason" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PdpaDataSubjectRequests" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Pdpa_Status_Due" ON "PdpaDataSubjectRequests" ("CompanyId", "Status", "DueBy") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "BillsOfMaterials" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ParentProductId" uuid NOT NULL,
                "Version" varchar(20) NOT NULL DEFAULT 'v1',
                "EffectiveFrom" timestamp NOT NULL DEFAULT now(),
                "EffectiveTo" timestamp NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_BillsOfMaterials" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "BomLines" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "BomId" uuid NOT NULL,
                "ComponentProductId" uuid NOT NULL,
                "QuantityPerParent" decimal(18,4) NOT NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_BomLines" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_BomLines_Bom" FOREIGN KEY ("BomId") REFERENCES "BillsOfMaterials"("Id")
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "ConsignmentRecords" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ProductId" uuid NOT NULL,
                "ContactId" uuid NOT NULL,
                "Direction" varchar(20) NOT NULL,
                "QuantityOnHand" decimal(18,4) NOT NULL DEFAULT 0,
                "AgreedUnitPrice" decimal(18,4) NULL,
                "ReceivedAt" timestamp NOT NULL,
                "ReturnedOrSettledAt" timestamp NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ConsignmentRecords" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_Consignment_Product_Contact" ON "ConsignmentRecords" ("CompanyId", "ProductId", "ContactId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "VendorPortalTokens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "TokenHash" varchar(64) NOT NULL,
                "ContactId" uuid NOT NULL,
                "Role" varchar(20) NOT NULL DEFAULT 'Vendor',
                "IssuedAt" timestamp NOT NULL DEFAULT now(),
                "ExpiresAt" timestamp NOT NULL,
                "LastUsedAt" timestamp NULL,
                "RevokedAt" timestamp NULL,
                "RevokedReason" text NULL,
                "IssuedByUserId" uuid NULL,
                "RecipientEmail" varchar(200) NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_VendorPortalTokens" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_VendorPortalTokens_Hash" ON "VendorPortalTokens" ("TokenHash") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_VendorPortalTokens_Contact" ON "VendorPortalTokens" ("CompanyId", "ContactId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "ProductionOrders" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "OrderNumber" varchar(50) NOT NULL,
                "ParentProductId" uuid NOT NULL,
                "BomId" uuid NOT NULL,
                "PlannedQty" decimal(18,4) NOT NULL,
                "CompletedQty" decimal(18,4) NOT NULL DEFAULT 0,
                "PlannedStartAt" timestamp NOT NULL,
                "CompletedAt" timestamp NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Planned',
                "CumulativeComponentCost" decimal(18,4) NOT NULL DEFAULT 0,
                "JournalEntryId" uuid NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL, "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductionOrders" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ProductionOrders_Company_Status" ON "ProductionOrders" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",

            // ===== TaxReport e-Filing ACK lifecycle =====
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdAckNumber" varchar(50) NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdAcknowledgedAt" timestamp NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdSubmissionStatus" varchar(30) NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdRejectionReason" text NULL;""",
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RdAcknowledgementDocumentUrl" text NULL;""",

            // ============================================================
            // ERP Upgrade — Task 1 (period close + migration), Task 4
            // (VAT deferral + filing lock + e-Filing), Task 5 (OCR audit)
            // ============================================================

            // FiscalPeriods: soft/hard close + year-end closing JE link
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "ClosedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "ClosedBy" text NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "LockedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "LockedBy" text NULL;
            """,
            """
            ALTER TABLE "FiscalPeriods" ADD COLUMN IF NOT EXISTS "YearEndJournalEntryId" uuid NULL;
            """,

            // TaxReports: filing-lock + e-filing audit + rejection workflow
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "FilingLockedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "FilingLockedBy" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "EFilingExportedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "EFilingReferenceNumber" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RejectionReason" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RejectedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "RejectedBy" text NULL;
            """,
            """
            ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "ReversalJournalEntryId" uuid NULL;
            """,

            // Documents: OCR compliance + aging cache
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrConfidenceScore" numeric(5,4) NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RdComplianceStatus" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RdComplianceIssuesJson" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrTenantMismatchFlag" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "AgingDays" int NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "AgingLastEvaluatedAt" timestamp with time zone NULL;
            """,
            // OverdueDunningJob — track last dunning send + level (1/2/3)
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "LastDunningSentAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "LastDunningLevel" int NULL;
            """,
            // Undue Input VAT (§82/3) — PV/PurchaseInvoice ที่ใบกำกับยังไม่ครบ §86/4
            // → VAT post เข้า 11640 ก่อน, รอ user มาแก้ครบแล้ว gen adjusting JE
            // Dr 11610 / Cr 11640. BecameClaimableAt = tax-point จริงสำหรับ ภ.พ.30.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatPostedAsUndue" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatBecameClaimableAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OutputVatDueAt" timestamp with time zone NULL;
            """,
            // §82/3: ภาษีซื้อ 11640 พ้น 6 เดือน → reclassify เป็นค่าใช้จ่าย
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatExpiredAt" timestamp with time zone NULL;""",
            // ใบแจ้งหนี้/ใบกำกับภาษี (combined) — type=TaxInvoice แต่พิมพ์หัวรวม
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CombinedInvoiceTaxInvoice" boolean NOT NULL DEFAULT false;""",
            // ขายเงินสด B2B (isCashSale) → e-Tax T03 + หัว "ใบเสร็จรับเงิน/ใบกำกับภาษี"
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IssuedAsCashReceipt" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaidOnIssue" boolean NOT NULL DEFAULT false;""",
            // User override ผัง VAT ปลายทาง (เช่น "51000" = ลงต้นทุนขายแทน)
            // — ใช้ AccountCode (string) เพื่อ portable, validator แปลงเป็น Id ตอน post
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InputVatAccountCodeOverride" varchar(20) NULL;
            """,
            // เงินมัดจำ/รับล่วงหน้า — ใบเสร็จที่พักรายได้ไว้ "ขายรอรับรู้" (217xx)
            // แต่รับรู้ภาษีขายทันที (tax point §78). RealizeDeposit ตัด → รายได้.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsDeposit" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRealizedAmount" numeric(18,2) NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRealizedAt" timestamp with time zone NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositDeferredAccountCode" varchar(20) NULL;
            """,
            // มัดจำ: เคสภาษีขาย — Deferred=true → 21913 (รอเรียกเก็บ, ยังไม่เข้า
            // ภ.พ.30); RecognizedAt = วันย้าย 21913→21911 (tax point เกิดจริง)
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositOutputVatDeferred" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositOutputVatRecognizedAt" timestamp with time zone NULL;
            """,

            // OpeningBalances — per-period per-account opening figures
            """
            CREATE TABLE IF NOT EXISTS "OpeningBalances" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FiscalPeriodId" uuid NOT NULL,
                "AccountId" uuid NOT NULL,
                "OpeningDebit" numeric(18,2) NOT NULL DEFAULT 0,
                "OpeningCredit" numeric(18,2) NOT NULL DEFAULT 0,
                "DimensionId" uuid NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_OpeningBalances_Lookup"
                ON "OpeningBalances" ("CompanyId", "FiscalPeriodId", "AccountId");
            """,

            // YearEndClosings — audit per closed fiscal year
            """
            CREATE TABLE IF NOT EXISTS "YearEndClosings" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FiscalYear" int NOT NULL,
                "RetainedEarningsAccountId" uuid NOT NULL,
                "TransferredAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ClosingJournalEntryId" uuid NOT NULL,
                "LockedFiscalPeriods" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_YearEndClosings_CompanyYear"
                ON "YearEndClosings" ("CompanyId", "FiscalYear");
            """,

            // MigrationSessions + AccountMappings (Task 1 wizard)
            """
            CREATE TABLE IF NOT EXISTS "MigrationSessions" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "SessionName" varchar(200) NOT NULL,
                "MigrationType" int NOT NULL,
                "Status" int NOT NULL DEFAULT 0,
                "TargetFiscalPeriodId" uuid NULL,
                "MappingsJson" text NULL,
                "ImportSummaryJson" text NULL,
                "CompletedAt" timestamp with time zone NULL,
                "CompletedBy" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_MigrationSessions_CompanyStatus"
                ON "MigrationSessions" ("CompanyId", "Status");
            """,
            """
            CREATE TABLE IF NOT EXISTS "AccountMappings" (
                "Id" uuid PRIMARY KEY,
                "MigrationSessionId" uuid NOT NULL REFERENCES "MigrationSessions"("Id") ON DELETE CASCADE,
                "LegacyCode" varchar(50) NOT NULL,
                "LegacyName" varchar(200) NULL,
                "MappedAccountId" uuid NULL,
                "LegacyDebit" numeric(18,2) NOT NULL DEFAULT 0,
                "LegacyCredit" numeric(18,2) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_AccountMappings_Session"
                ON "AccountMappings" ("MigrationSessionId", "LegacyCode");
            """,

            // VatDeferrals (Task 4)
            """
            CREATE TABLE IF NOT EXISTS "VatDeferrals" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "OriginalTaxReportId" uuid NOT NULL,
                "DeferredAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "DeferralReason" varchar(500) NULL,
                "DeferredFromPeriod" int NOT NULL,
                "DeferredToPeriod" int NOT NULL,
                "ClaimedAt" timestamp with time zone NULL,
                "ClaimedTaxReportId" uuid NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_VatDeferrals_Target"
                ON "VatDeferrals" ("CompanyId", "DeferredToPeriod");
            """,

            // EFilingExports — RD pipe-delimited file artifacts
            """
            CREATE TABLE IF NOT EXISTS "EFilingExports" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FormType" varchar(20) NOT NULL,
                "PeriodYear" int NOT NULL,
                "PeriodMonth" int NOT NULL,
                "FileContent" text NOT NULL,
                "LineCount" int NOT NULL DEFAULT 0,
                "TotalAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "TotalTax" numeric(18,2) NOT NULL DEFAULT 0,
                "RdReferenceNumber" text NULL,
                "GeneratedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "GeneratedBy" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_EFilingExports_FormPeriod"
                ON "EFilingExports" ("CompanyId", "FormType", "PeriodYear", "PeriodMonth");
            """,

            // OcrValidationLogs (Task 5)
            """
            CREATE TABLE IF NOT EXISTS "OcrValidationLogs" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "RuleCode" varchar(50) NOT NULL,
                "IsValid" boolean NOT NULL DEFAULT false,
                "Severity" varchar(20) NOT NULL DEFAULT 'Warning',
                "Message" text NULL,
                "FieldValue" text NULL,
                "OverriddenBy" uuid NULL,
                "OverriddenAt" timestamp with time zone NULL,
                "OverrideReason" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrValidationLogs_Document"
                ON "OcrValidationLogs" ("DocumentId");
            """,

            // ===== CompanySettings: e-Tax mode + by-email registration columns =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxMode" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRdRegistered" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRegistrationDate" timestamp NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRegistrationNumber" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailSenderEmail" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailRdTimestampAddress" varchar(256) NOT NULL DEFAULT 'csemail@etax.teda.th';
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailEmbedXml" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EtaxByEmailAutoSendOnApprove" boolean NOT NULL DEFAULT false;
            """,

            // ===== CompanySettings: Email sending configuration columns =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailProvider" int NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailFromAddress" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailConfigured" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailLastTestedAt" timestamp NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailLastTestStatus" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpHost" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpPort" int NOT NULL DEFAULT 587;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpUsername" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpPassword" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailSmtpUseSsl" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsTenantId" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsClientId" varchar(100) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsClientSecret" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailMsSenderUpn" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailClientId" varchar(256) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailClientSecret" varchar(500) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailRefreshToken" varchar(2000) NULL;
            """,
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EmailGmailServiceAccountJson" text NULL;
            """,
            // OCR document-target default — 13 = DocumentType.PaymentVoucher.
            // Match the entity default so existing rows behave like the new
            // "cash-basis" default (which most Thai SMEs want).
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OcrBuyerInvoiceDefaultTarget" integer NOT NULL DEFAULT 13;
            """,

            // ===== RecurringTransactions: PreferredDay to prevent date drift on monthly schedules =====
            """
            ALTER TABLE "RecurringTransactions" ADD COLUMN IF NOT EXISTS "PreferredDay" integer NULL;
            """,

            // ===== RevenueContracts: ProjectId (optional link to Projects) =====
            """
            ALTER TABLE "RevenueContracts" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_RevenueContracts_ProjectId" ON "RevenueContracts" ("ProjectId");
            """,

            // ===== BankFeedImports: allow null BankConnectionId for manual file imports =====
            """
            ALTER TABLE "BankFeedImports" ALTER COLUMN "BankConnectionId" DROP NOT NULL;
            """,

            // ===== PlanTemplates: convert legacy "FreeTrial" template to permanent-free Free Edition =====
            // The Free plan was originally a 14-day trial; we now offer it as ฟรีตลอดชีพ.
            """
            UPDATE "PlanTemplates"
            SET "IsPermanentFree" = true,
                "Name" = 'Free Edition',
                "TrialDurationDays" = 36500,
                "TrialMaxExtensions" = 0
            WHERE "Plan" = 0  -- SubscriptionPlan.FreeTrial
              AND ("IsPermanentFree" IS NULL OR "IsPermanentFree" = false);
            """,
            """
            UPDATE "Subscriptions"
            SET "IsPermanentFree" = true,
                "Status" = 1,                            -- Active
                "EndDate" = "StartDate" + INTERVAL '100 years'
            WHERE "Plan" = 0                              -- SubscriptionPlan.FreeTrial
              AND ("IsPermanentFree" IS NULL OR "IsPermanentFree" = false);
            """,

            // ===== EtaxInvoices: file path columns for PDF/A-3 + XML persistence (Thai e-Tax by Email compliance) =====
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "PdfFilePath" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "XmlFilePath" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "VoidedAt" timestamp NULL;
            """,
            """
            ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "VoidReason" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "DocumentEmailLogs" ADD COLUMN IF NOT EXISTS "PdfFilePath" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "DocumentEmailLogs" ADD COLUMN IF NOT EXISTS "XmlFilePath" varchar(1000) NULL;
            """,

            """
            CREATE TABLE IF NOT EXISTS "SiteSettings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ContactPhone" varchar(50) NULL,
                "ContactLine" varchar(100) NULL,
                "ContactEmail" varchar(256) NULL,
                "ServicesJson" jsonb NULL,
                "PricingSectionTitle" varchar(500) NULL,
                "PricingSectionSubtitle" varchar(1000) NULL,
                "SiteName" varchar(200) NULL,
                "SiteDescription" varchar(1000) NULL,
                "SiteLogoUrl" varchar(500) NULL,
                "FacebookUrl" varchar(500) NULL,
                "LineOfficialUrl" varchar(500) NULL,
                "WebsiteUrl" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SiteSettings" PRIMARY KEY ("Id")
            );
            """,

            // ===== SiteSettings: Extended branding & system behavior =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FaviconUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LoginBackgroundUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "PrimaryColor" varchar(20) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "HeroTitle" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "HeroSubtitle" varchar(1000) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "FooterCopyright" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "YouTubeUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "InstagramUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "RegistrationEnabled" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "MaintenanceMode" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "MaintenanceMessage" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "DefaultLanguage" varchar(5) NOT NULL DEFAULT 'th';
            """,

            // ===== SiteSettings: System Email config (admin-managed SMTP/API) =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailProvider" int NOT NULL DEFAULT 0;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailFromAddress" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailFromName" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailReplyTo" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailConfigured" boolean NOT NULL DEFAULT false;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailLastTestedAt" timestamp NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemEmailLastTestStatus" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpHost" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpPort" int NOT NULL DEFAULT 587;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpUsername" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpPassword" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemSmtpUseSsl" boolean NOT NULL DEFAULT true;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsTenantId" varchar(100) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsClientId" varchar(100) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsClientSecret" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemMsSenderUpn" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemGmailClientId" varchar(256) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemGmailClientSecret" varchar(500) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "SystemGmailRefreshToken" varchar(2000) NULL;
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AppBaseUrl" varchar(500) NULL;
            """,

            // ===== Documents: bank account + expense category link =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BankAccountId" uuid NULL;
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ExpenseCategoryId" uuid NULL;
            """,

            // ===== Payments: proper FK to BankAccount =====
            """
            ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "BankAccountId" uuid NULL;
            """,

            // ===== Documents: PaymentAccountId for non-bank money accounts =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentAccountId" uuid NULL;
            """,

            // Document template layout style — the chosen structural look.
            """
            ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "LayoutStyle" varchar(40) NOT NULL DEFAULT 'Classic';
            """,
            // ===== OcrScanResults: duplicate detection fields =====
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "FileHash" varchar(64) NULL;
            """,
            // JE-only path: tracks the Journal Entry recorded straight from a
            // scan (no business document), so we can block double-posting.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "CreatedJournalEntryId" uuid NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "RetryCount" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ContentFingerprint" varchar(64) NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrScanResults_CompanyId_ContentFingerprint"
            ON "OcrScanResults" ("CompanyId", "ContentFingerprint")
            WHERE "ContentFingerprint" IS NOT NULL;
            """,

            // ===== Performance indexes — added after audit identified hot-path query slowdowns =====
            // 60-second pre-upload duplicate check (OcrController.UploadAndScan):
            // covers (CompanyId + FileHash + CreatedAt) for the most-recent-duplicate lookup.
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrScanResults_CompanyId_FileHash_CreatedAt"
            ON "OcrScanResults" ("CompanyId", "FileHash", "CreatedAt");
            """,
            // Self-correction maintenance scans by IsNegativeExample + LastConfirmedAt:
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrLearnedPatterns_IsNegativeExample_LastConfirmedAt"
            ON "OcrLearnedPatterns" ("IsNegativeExample", "LastConfirmedAt");
            """,
            // OcrCreditPurchases SUM aggregation in GetQuotaStatusAsync filters (Status, ExpiresAt, PagesRemaining):
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrCreditPurchases_Status_ExpiresAt"
            ON "OcrCreditPurchases" ("Status", "ExpiresAt") WHERE "PagesRemaining" > 0;
            """,
            // Documents.RelatedDocumentId — used for cycle detection in ConvertDocumentAsync
            // and for the "show child documents" UI in document chain rendering:
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_RelatedDocumentId"
            ON "Documents" ("RelatedDocumentId") WHERE "RelatedDocumentId" IS NOT NULL;
            """,
            // Documents.RevenueContractId — used for "list invoices for contract" lookups:
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_RevenueContractId"
            ON "Documents" ("RevenueContractId") WHERE "RevenueContractId" IS NOT NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "IsDuplicate" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "DuplicateOfScanId" uuid NULL;
            """,
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "BuyerName" varchar(500) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "BuyerTaxId" varchar(20) NULL;""",
            // §86/4 สาขา + ที่อยู่ที่อ่านได้จากใบ (กฎเหล็ก #3 — pre-fill ครบทุก field)
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "VendorBranchCode" varchar(10) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "VendorAddress" varchar(1000) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "BuyerBranchCode" varchar(10) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "BuyerAddress" varchar(1000) NULL;""",
            // ตัวชี้วัดคุณภาพ OCR: "ผู้ใช้แก้จริง" ต้องแยกจาก "ระบบบันทึกแถว"
            // (เดิมนับจาก UpdatedAt != null ⇒ ~100% ทุก tenant = อ่านไม่ได้)
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "UserCorrectedAt" timestamptz NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "UserCorrectedFields" varchar(500) NULL;""",
            // สมุดที่มาของค่ารายช่อง (D1) — ผู้ชนะ + ตัวเลือกที่แพ้ของแต่ละช่อง
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "FieldDecisionsJson" text NULL;""",
            // สกุลเงินที่เอกสารประกาศไว้ (e-Tax XML) — เลิกให้สองที่เดาเอง
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "Currency" varchar(3) NULL;""",
            // AI GL suggestion transparency — เก็บ AI primary แม้ถูก confidence guard ปฏิเสธ
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountAiSuggestedCode" varchar(20) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountAiConfidence" numeric(5,4) NULL;""",

            // OcrScanResults: which OCR engine produced this result
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OcrEngine" varchar(40) NULL;
            """,

            // OcrScanResults: potential fixed asset detection flags (Phase 4)
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HasPotentialFixedAsset" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PotentialAssetLinesJson" text NULL;
            """,
            // OcrScanResults: handwriting detection
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HasHandwriting" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HandwritingConfidence" decimal(4,2) NULL;
            """,

            // ===== OcrScanResults: document role inference fields =====
            // Thai-accounting workflow: a scanned receipt from a supplier should
            // create a PaymentVoucher in our books — not a "Receipt" document.
            // These three columns let OcrDocumentRoleInferrer record the
            // separation between the paper (ScannedDocumentType), our role
            // (OurRole: Buyer/Seller), and the target doc to create
            // (TargetDocumentType).
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ScannedDocumentType" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OurRole" varchar(20) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "TargetDocumentType" varchar(50) NULL;
            """,

            // ===== OcrScanResults: expense/account suggestion fields =====
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ExpenseCategory" text NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "SuggestedAccountsJson" text NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "HasWht" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "WhtRate" numeric(5,2) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PaymentTermsDays" integer NULL;
            """,

            // External-system metadata payload (project/order info uploaded
            // alongside the file) — drives auto project allocation per line.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ExternalMetadataJson" text NULL;
            """,
            // Business-flow hints computed at scan time: suggested entry mode
            // (Stock/Expense) + open POs of the matched vendor.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "SuggestedEntryMode" varchar(20) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OpenPoNumbersJson" text NULL;
            """,
            // PO linkage (the "ฟังก์ชันชื่อแทน / รับตาม PO" function): which PO
            // the operator linked this scan to + per-line OCR↔PO mappings.
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPurchaseOrderId" uuid NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPurchaseOrderNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PoLineMappingsJson" text NULL;
            """,
            // ใบต้นทางทุกชนิด (ทั่วไปกว่า PO) — Helpers/OcrPredecessorMatcher
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPredecessorDocumentId" uuid NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPredecessorNumber" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LinkedPredecessorType" varchar(40) NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PredecessorLinkReason" text NULL;
            """,
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "PredecessorCandidatesJson" text NULL;
            """,
            // Header discount read off the paper (raw-text enrichment).
            """
            ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "ExtractedDiscountAmount" numeric(18,2) NULL;
            """,

            // ===== SsoYearConfigs: per-company-per-year SSO ceiling/rate
            // override (default schedule lives in Helpers.SsoRateSchedule:
            // 15,000 → 17,500 (2026) → 20,000 (2029) → 23,000 (2032)) =====
            """
            CREATE TABLE IF NOT EXISTS "SsoYearConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Year" integer NOT NULL,
                "WageCeiling" numeric(18,2) NOT NULL,
                "RatePercent" numeric(5,2) NOT NULL DEFAULT 5,
                "EmployerRatePercent" numeric(5,2) NOT NULL DEFAULT 5,
                "Notes" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SsoYearConfigs" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SsoYearConfigs_Company_Year"
            ON "SsoYearConfigs" ("CompanyId", "Year") WHERE "IsDeleted" = false;
            """,

            // ===== OcrLearnedPatterns: zone analyzer learning =====
            """
            CREATE TABLE IF NOT EXISTS "OcrLearnedPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorTaxId" varchar(13) NULL,
                "FieldName" varchar(50) NOT NULL,
                "ContextKeyword" varchar(200) NOT NULL,
                "ExtractionRegex" text NULL,
                "SearchRadius" integer NOT NULL DEFAULT 300,
                "TimesConfirmed" integer NOT NULL DEFAULT 1,
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrLearnedPatterns" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_OcrLearnedPatterns_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrLearnedPatterns_CompanyId_VendorTaxId_FieldName"
            ON "OcrLearnedPatterns" ("CompanyId", "VendorTaxId", "FieldName");
            """,
            """
            ALTER TABLE "OcrLearnedPatterns" ADD COLUMN IF NOT EXISTS "IsNegativeExample" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "OcrLearnedPatterns" ADD COLUMN IF NOT EXISTS "NegativeValue" text NULL;
            """,
            """
            ALTER TABLE "OcrLearnedPatterns" ADD COLUMN IF NOT EXISTS "FailureCount" integer NOT NULL DEFAULT 0;
            """,

            // ===== VendorKnownGoodValues: Azure-DI-sourced canonical field values =====
            // Populated whenever Azure DI extracts a high-confidence field
            // for a recognized vendor. The Tier-2/3 local OCR cascade
            // fuzzy-matches its own noisy output against these values
            // and substitutes the canonical version when similarity ≥ 0.80.
            """
            CREATE TABLE IF NOT EXISTS "VendorKnownGoodValues" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorTaxId" varchar(13) NULL,
                "FieldName" varchar(50) NOT NULL,
                "Value" text NOT NULL,
                "Confidence" numeric(5,4) NOT NULL DEFAULT 0,
                "ConfirmedCount" integer NOT NULL DEFAULT 1,
                "Source" varchar(20) NOT NULL DEFAULT 'AzureDI',
                "LastSeenAt" timestamp NOT NULL DEFAULT now(),
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_VendorKnownGoodValues" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_VendorKnownGoodValues_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_VendorKnownGoodValues_CompanyId_VendorTaxId_FieldName"
            ON "VendorKnownGoodValues" ("CompanyId", "VendorTaxId", "FieldName");
            """,

            // ===== Sites: CMS columns added after initial table creation =====
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CaptchaProvider" varchar(50) NULL;
            """,
            // รอบ 158 — ประเภทธุรกิจของเว็บ (เดิมรับมาใน CreateSiteRequest แล้วทิ้ง ⇒ เว็บที่พักไม่มีใครรู้ว่าเป็นที่พัก)
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "IndustryType" integer NOT NULL DEFAULT 0;
            """,
            // รอบ 159 — เว็บที่ "มีที่พักผูกอยู่จริง" คือเว็บที่พักแน่นอน ⇒ เติมย้อนหลังให้ตรง ไม่ใช่ปล่อยเป็น
            // General ทั้งหมด (ไม่งั้นการ์ด/โมดัลเติมเทมเพลตจะโชว์ "ทั่วไป" ให้ทุกเว็บที่สร้างก่อนคอลัมน์นี้
            // และการกดเติมเทมเพลตโดยไม่แตะ dropdown จะเปลี่ยนเว็บที่พักเป็นเว็บทั่วไป). 15 = IndustryType.Hotel
            """UPDATE "Sites" SET "IndustryType" = 15 WHERE "IndustryType" = 0 AND "Id" IN (SELECT "SiteId" FROM "LodgingProperties" WHERE "SiteId" IS NOT NULL AND "IsDeleted" = false);""",
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CaptchaSiteKey" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CookieConsentEnabled" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "PrivacyPolicyUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "TermsOfServiceUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxStorageBytes" bigint NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxBandwidthBytesPerMonth" bigint NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxProducts" integer NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MaxPages" integer NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CurrentStorageUsed" bigint NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CurrentBandwidthUsed" bigint NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "BandwidthResetDate" timestamp NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "OgImageUrl" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CanonicalUrl" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "RobotsDirective" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CustomHeadScripts" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "CustomBodyScripts" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MetaTitle" varchar(500) NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MetaDescription" text NULL;
            """,
            """
            ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "MetaKeywords" text NULL;
            """,

            // ===== SiteSettings: Azure Document Intelligence + OCR config (system-wide) =====
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiEndpoint" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiApiKey" text NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiModelId" varchar(100) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiApiVersion" varchar(20) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiEnabled" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiLastTestedAt" timestamp NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiLastTestStatus" text NULL;
            """,
            // Tier-aware throttle. Default 1 / 1500ms is safe for F0
            // (1 TPS analyze, 1 TPS poll). Paid S0 admins bump in UI.
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiMaxConcurrentSubmits" integer NOT NULL DEFAULT 1;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AzureDiPollIntervalMs" integer NOT NULL DEFAULT 1500;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrProvider" varchar(20) NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrLocalServiceUrl" text NULL;
            """,
            // OcrGoogleApiKey + OcrTesseractApiKey columns are intentionally not created
            // for new installs — those providers were removed in favor of Azure DI v4 +
            // Local (PaddleOCR+EasyOCR). Existing installs keep the columns harmlessly;
            // a future cleanup migration can DROP them when no rows reference them.
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrAutoCreateThreshold" decimal(5,2) NOT NULL DEFAULT 0.85;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesTrial" integer NOT NULL DEFAULT 10;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesBasic" integer NOT NULL DEFAULT 50;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesPro" integer NOT NULL DEFAULT 500;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrFreePagesEnterprise" integer NOT NULL DEFAULT 5000;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrCreditPricePerPage" decimal(10,2) NOT NULL DEFAULT 2.00;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrCreditMinPurchase" integer NOT NULL DEFAULT 100;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "LastOcrMaintenanceAt" timestamp NULL;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayMaxPenalty" decimal(5,2) NOT NULL DEFAULT 0.60;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayMathTolerance" decimal(10,2) NOT NULL DEFAULT 2.0;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayTaxIdPenalty" decimal(5,2) NOT NULL DEFAULT 0.15;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayMathPenalty" decimal(5,2) NOT NULL DEFAULT 0.20;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayDatePenalty" decimal(5,2) NOT NULL DEFAULT 0.15;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayVatRatePenalty" decimal(5,2) NOT NULL DEFAULT 0.10;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrGatewayLowConfidencePenalty" decimal(5,2) NOT NULL DEFAULT 0.05;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrMaxRetriesPerScan" integer NOT NULL DEFAULT 1;
            """,
            """
            ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "OcrMaxPagesPerScan" integer NULL DEFAULT 10;
            """,

            // ===== Subscriptions: OCR quota fields =====
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "MaxOcrPagesPerMonth" integer NOT NULL DEFAULT 10;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CurrentMonthOcrPages" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "OcrBonusPages" integer NOT NULL DEFAULT 0;
            """,
            // Per-engine OCR quotas — split Azure DI vs local OCR
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "AzureOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "LocalOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "FallbackToLocalWhenAzureExhausted" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CurrentMonthAzureOcrPages" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "CurrentMonthLocalOcrPages" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "AzureOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "LocalOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "FallbackToLocalWhenAzureExhausted" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "OcrBonusExpiresAt" timestamp NULL;
            """,

            // ===== PlanTemplates: OCR quota fields =====
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "MaxOcrPagesPerMonth" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxOcrPagesPerMonth" integer NOT NULL DEFAULT 10;
            """,
            // PlanTemplate entity defines these per-engine columns but they
            // were only migrated on Subscriptions / SubscriptionPlans —
            // /api/admin/plans 500'd because EF SELECT *'d the missing
            // columns from PlanTemplates.
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "AzureOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "LocalOcrPagesPerMonth" integer NULL;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "FallbackToLocalWhenAzureExhausted" boolean NOT NULL DEFAULT true;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "Currency" text NOT NULL DEFAULT 'THB';
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxUsers" integer NOT NULL DEFAULT 2;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxDocumentsPerMonth" integer NOT NULL DEFAULT 20;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialMaxJournalEntriesPerMonth" integer NOT NULL DEFAULT 50;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialBlockOnExpiry" boolean NOT NULL DEFAULT false;
            """,
            """
            ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "TrialGracePeriodDays" integer NOT NULL DEFAULT 7;
            """,

            // ===== TrialConfigs: OCR quota fields =====
            """
            ALTER TABLE "TrialConfigs" ADD COLUMN IF NOT EXISTS "TrialMaxOcrPagesPerMonth" integer NOT NULL DEFAULT 10;
            """,

            // ===== OcrCategoryMappings: learned vendor → expense-account mappings =====
            """
            CREATE TABLE IF NOT EXISTS "OcrCategoryMappings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "DescriptionKeyword" varchar(200) NOT NULL DEFAULT '',
                "AccountCode" varchar(20) NOT NULL DEFAULT '',
                "AccountName" text NULL,
                "TimesUsed" integer NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp NOT NULL DEFAULT now(),
                "TrainedByUserId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrCategoryMappings" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrCategoryMappings_CompanyId_VendorKey_Description"
            ON "OcrCategoryMappings" ("CompanyId", "VendorKey", "DescriptionKeyword");
            """,

            // ===== OcrVendorIntelligence: per-vendor aggregated stats for self-learning =====
            // One row per vendor per company. Updated each time a Document is approved.
            // Read on every OCR scan to suggest DocumentType / debit account / WHT rate.
            """
            CREATE TABLE IF NOT EXISTS "OcrVendorIntelligence" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "VendorName" varchar(300) NULL,
                "VendorTaxId" varchar(20) NULL,
                "MostCommonDocumentType" varchar(50) NULL,
                "MostCommonDocumentTypeCount" integer NOT NULL DEFAULT 0,
                "TotalDocuments" integer NOT NULL DEFAULT 0,
                "DocumentTypeBreakdownJson" text NULL,
                "MostCommonDebitAccountCode" varchar(20) NULL,
                "MostCommonDebitAccountName" text NULL,
                "MostCommonDebitAccountCount" integer NOT NULL DEFAULT 0,
                "DebitAccountBreakdownJson" text NULL,
                "TypicallyHasWht" boolean NOT NULL DEFAULT false,
                "TypicalWhtRate" decimal(5,2) NULL,
                "WhtUsageCount" integer NOT NULL DEFAULT 0,
                "AvgTotalAmount" decimal(18,2) NULL,
                "MinTotalAmount" decimal(18,2) NULL,
                "MaxTotalAmount" decimal(18,2) NULL,
                "MedianTotalAmount" decimal(18,2) NULL,
                "TypicalPaymentTermsDays" integer NULL,
                "LastTrainedAt" timestamp NOT NULL DEFAULT now(),
                "LastDocumentDate" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrVendorIntelligence" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_OcrVendorIntelligence_CompanyId_VendorKey"
            ON "OcrVendorIntelligence" ("CompanyId", "VendorKey");
            """,

            // OcrVendorIntelligence: learned-pattern columns for OCR boosting
            // (added after the table existed for some tenants — must be
            // idempotent ALTER, not part of the original CREATE TABLE).
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "TypicalDocNumberPrefix" varchar(50) NULL;
            """,
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "TopLineKeywordsJson" text NULL;
            """,
            // z-score anomaly detection needs running log-amount mean/variance
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "LogAmountMean" decimal(10,4) NULL;
            """,
            """
            ALTER TABLE "OcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "LogAmountVariance" decimal(10,4) NULL;
            """,

            // ===== SystemOcrCategoryMappings: system-wide vendor → account knowledge =====
            // Mirrors OcrCategoryMappings but without CompanyId. Trained by SystemAdmin
            // from /admin/ocr-config; consulted as fallback when tenant has no row.
            """
            CREATE TABLE IF NOT EXISTS "SystemOcrCategoryMappings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "DescriptionKeyword" varchar(200) NOT NULL DEFAULT '',
                "AccountCode" varchar(20) NOT NULL DEFAULT '',
                "AccountName" text NULL,
                "TimesUsed" integer NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp NOT NULL DEFAULT now(),
                "TrainedByUserId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemOcrCategoryMappings" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_SystemOcrCategoryMappings_VendorKey_Description"
            ON "SystemOcrCategoryMappings" ("VendorKey", "DescriptionKeyword");
            """,

            // ===== SystemOcrVendorIntelligence: system-wide per-vendor stats =====
            // One row per VendorKey for the whole system; tenant rows always win at
            // prediction time, this is consulted as fallback for unseen vendors.
            """
            CREATE TABLE IF NOT EXISTS "SystemOcrVendorIntelligence" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL DEFAULT '',
                "VendorName" varchar(300) NULL,
                "VendorTaxId" varchar(20) NULL,
                "MostCommonDocumentType" varchar(50) NULL,
                "MostCommonDocumentTypeCount" integer NOT NULL DEFAULT 0,
                "TotalDocuments" integer NOT NULL DEFAULT 0,
                "DocumentTypeBreakdownJson" text NULL,
                "MostCommonDebitAccountCode" varchar(20) NULL,
                "MostCommonDebitAccountName" text NULL,
                "MostCommonDebitAccountCount" integer NOT NULL DEFAULT 0,
                "DebitAccountBreakdownJson" text NULL,
                "TypicallyHasWht" boolean NOT NULL DEFAULT false,
                "TypicalWhtRate" decimal(5,2) NULL,
                "WhtUsageCount" integer NOT NULL DEFAULT 0,
                "AvgTotalAmount" decimal(18,2) NULL,
                "MinTotalAmount" decimal(18,2) NULL,
                "MaxTotalAmount" decimal(18,2) NULL,
                "MedianTotalAmount" decimal(18,2) NULL,
                "TypicalPaymentTermsDays" integer NULL,
                "LastTrainedAt" timestamp NOT NULL DEFAULT now(),
                "LastDocumentDate" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemOcrVendorIntelligence" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_SystemOcrVendorIntelligence_VendorKey"
            ON "SystemOcrVendorIntelligence" ("VendorKey");
            """,

            // Industry-similarity weighting: track which industries
            // contributed to each cross-tenant aggregate so predict-time
            // consumers can boost rows whose contributing tenants share
            // their IndustryType.
            """
            ALTER TABLE "SystemOcrCategoryMappings" ADD COLUMN IF NOT EXISTS "IndustryBreakdownJson" text NULL;
            """,
            """
            ALTER TABLE "SystemOcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "IndustryBreakdownJson" text NULL;
            """,

            // ===== SystemOcrAssociationRules: basket-analysis output =====
            // Discovered association rules from system-wide Apriori mining over
            // approved-document transactions. Refreshed by an admin-triggered
            // background job, not per-scan.
            """
            CREATE TABLE IF NOT EXISTS "SystemOcrAssociationRules" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "AntecedentJson" text NOT NULL DEFAULT '[]',
                "Consequent" varchar(100) NOT NULL DEFAULT '',
                "Support" decimal(8,6) NOT NULL DEFAULT 0,
                "Confidence" decimal(8,6) NOT NULL DEFAULT 0,
                "Lift" decimal(10,4) NOT NULL DEFAULT 0,
                "TransactionCount" integer NOT NULL DEFAULT 0,
                "MinedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemOcrAssociationRules" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_SystemOcrAssociationRules_Consequent"
            ON "SystemOcrAssociationRules" ("Consequent");
            """,

            // ===== TradingPartnerships: cross-tenant B2B handshake =====
            // Single row per ordered (CompanyA, CompanyB) pair; both sides
            // must accept before any cross-tenant document flow is allowed.
            """
            CREATE TABLE IF NOT EXISTS "TradingPartnerships" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyAId" uuid NOT NULL,
                "CompanyBId" uuid NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "InvitedByCompanyId" uuid NOT NULL,
                "InvitedByUserId" uuid NOT NULL,
                "InvitationMessage" text NULL,
                "AcceptedAt" timestamp NULL,
                "AcceptedByUserId" uuid NULL,
                "RejectedAt" timestamp NULL,
                "RejectionReason" text NULL,
                "AutoApproveAmountLimit" decimal(18,2) NULL,
                "NotifyEmail" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_TradingPartnerships" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_TradingPartnerships_AB"
            ON "TradingPartnerships" ("CompanyAId", "CompanyBId");
            """,

            // ===== CrossTenantDocumentLinks: per-step routing log =====
            // One row per document flow event (Quotation→approval, PO→supplier,
            // Invoice→buyer, ...). SnapshotJson preserves the source doc state
            // at the moment of approval for immutable audit.
            """
            CREATE TABLE IF NOT EXISTS "CrossTenantDocumentLinks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TradingPartnershipId" uuid NOT NULL,
                "SourceCompanyId" uuid NOT NULL,
                "SourceDocumentId" uuid NOT NULL,
                "TargetCompanyId" uuid NOT NULL,
                "TargetDocumentId" uuid NULL,
                "LinkType" integer NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "ApproverUserId" uuid NULL,
                "ApprovedAt" timestamp NULL,
                "DocumentApprovalId" uuid NULL,
                "RejectionReason" text NULL,
                "SnapshotJson" text NULL,
                "Comment" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CrossTenantDocumentLinks" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_CrossTenantDocumentLinks_Target_Status"
            ON "CrossTenantDocumentLinks" ("TargetCompanyId", "Status");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_CrossTenantDocumentLinks_Source_Status"
            ON "CrossTenantDocumentLinks" ("SourceCompanyId", "Status");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_CrossTenantDocumentLinks_SourceDoc"
            ON "CrossTenantDocumentLinks" ("SourceDocumentId");
            """,

            // ===== WorkflowAutomationConfigs: per-company toggles =====
            """
            CREATE TABLE IF NOT EXISTS "WorkflowAutomationConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "AutoApproveIncomingQuotations" boolean NOT NULL DEFAULT false,
                "AutoApproveMinAmount" decimal(18,2) NULL,
                "AutoApproveMaxAmount" decimal(18,2) NULL,
                "AutoCreatePoOnQuotationApproval" boolean NOT NULL DEFAULT false,
                "AutoCreateInvoiceFromIncomingPo" boolean NOT NULL DEFAULT false,
                "AutoCreateReceiptFromIncomingPayment" boolean NOT NULL DEFAULT false,
                "AutoStampSignature" boolean NOT NULL DEFAULT false,
                "DefaultApproverUserId" uuid NULL,
                "DefaultSignatureId" uuid NULL,
                "NotifyOnIncomingDocument" boolean NOT NULL DEFAULT true,
                "NotifyEmail" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_WorkflowAutomationConfigs" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_WorkflowAutomationConfigs_CompanyId"
            ON "WorkflowAutomationConfigs" ("CompanyId");
            """,

            // ===== OcrCreditPurchases table =====
            """
            CREATE TABLE IF NOT EXISTS "OcrCreditPurchases" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "SubscriptionId" uuid NOT NULL,
                "PagesPurchased" integer NOT NULL DEFAULT 0,
                "PagesRemaining" integer NOT NULL DEFAULT 0,
                "AmountPaid" decimal(18,2) NOT NULL DEFAULT 0,
                "Currency" varchar(3) NOT NULL DEFAULT 'THB',
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "PaymentReference" text NULL,
                "SlipFileName" text NULL,
                "SlipStoragePath" text NULL,
                "ReviewedByUserId" uuid NULL,
                "ReviewedAt" timestamp NULL,
                "ReviewNotes" text NULL,
                "ExpiresAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_OcrCreditPurchases" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_OcrCreditPurchases_CompanyId_SubscriptionId"
            ON "OcrCreditPurchases" ("CompanyId", "SubscriptionId");
            """,

            // ===== Custom Roles & Per-Menu Permissions (per-company RBAC) =====
            // Each company can define its own roles and assign per-menu access.
            // CompanyUsers.CompanyRoleId is nullable so existing members default
            // to "no custom role" (full access — backward compatible).
            """
            CREATE TABLE IF NOT EXISTS "CompanyRoles" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Name" varchar(100) NOT NULL,
                "Description" text NULL,
                "Color" varchar(20) NOT NULL DEFAULT '#6B7280',
                "Icon" varchar(10) NOT NULL DEFAULT '👤',
                "IsSystemRole" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CompanyRoles" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyRoles_CompanyId_Name"
            ON "CompanyRoles" ("CompanyId", "Name");
            """,
            """
            CREATE TABLE IF NOT EXISTS "CompanyRolePermissions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyRoleId" uuid NOT NULL,
                "MenuItemId" varchar(100) NOT NULL,
                "CanAccess" boolean NOT NULL DEFAULT true,
                CONSTRAINT "PK_CompanyRolePermissions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CompanyRolePermissions_CompanyRoles" FOREIGN KEY ("CompanyRoleId")
                    REFERENCES "CompanyRoles"("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyRolePermissions_CompanyRoleId_MenuItemId"
            ON "CompanyRolePermissions" ("CompanyRoleId", "MenuItemId");
            """,
            // Add CompanyRoleId to existing CompanyUsers — nullable so old rows stay valid.
            """
            ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "CompanyRoleId" uuid NULL;
            """,

            // ===== Documents: CertificateInLieu (ใบรับรองแทนใบเสร็จ) fields =====
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CertificateReason" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CertifierName" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CertifierPosition" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WitnessName" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WitnessPosition" text NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentDate" timestamp NULL;
            """,
            // Product gallery images (JSON array of /uploads/products/... URLs).
            """
            ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "ImageUrlsJson" text NULL;
            """,

            // Asset type discriminator — broadens the module from PPE-only to
            // cover Intangible / RightOfUse / InvestmentProperty too.
            // Default 1 = Tangible so existing rows behave unchanged.
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "SourceDocumentId" uuid NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "SourceDocumentLineId" uuid NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "NeedsReview" boolean NOT NULL DEFAULT false;""",
            // ทบทวนอายุการใช้งานแบบ prospective (TFRS for NPAEs บทที่ 10) — ผลตรวจทีม E · E-06
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "DepreciableBaseAtReview" numeric NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "RemainingLifeMonthsAtReview" integer NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "ReviewEffectiveFromMonthIndex" integer NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "UsefulLifeReviewedAt" timestamptz NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "UsefulLifeReviewedBy" text NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "AssetType" integer NOT NULL DEFAULT 1;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "LeaseTermMonths" integer NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "LessorName" varchar(200) NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "MonthlyLeasePayment" numeric(18,2) NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "LeaseLiabilityAccountId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_FixedAssets_AssetType" ON "FixedAssets" ("CompanyId", "AssetType") WHERE "IsDeleted" = false;""",

            // POS: tip + coupon columns
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "TipAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "CouponCode" varchar(50) NULL;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "CouponDiscountAmount" numeric(18,2) NOT NULL DEFAULT 0;""",

            // Loyalty points on Contact (per-tenant; reset never).
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "LoyaltyPoints" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "LastVisitAt" timestamp NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "TotalVisitCount" integer NOT NULL DEFAULT 0;""",

            // Multi-printer routing — Product.PrintStation routes kitchen
            // tickets per item ("Kitchen-Hot" / "Kitchen-Cold" / "Bar" / "Drinks").
            // Null = goes to default cashier printer only.
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "PrintStation" varchar(50) NULL;""",

            // Sensitivity classification: 0=None / 1=Payroll / 2=ExecutivePay / 3=HrPersonal / 9=Confidential.
            // Documents (payroll vouchers) and JEs (auto-generated payroll JEs) stamp
            // this so SensitivityService can redact for users lacking the matching role.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "Sensitivity" integer NOT NULL DEFAULT 0;
            """,
            """
            ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "Sensitivity" integer NOT NULL DEFAULT 0;
            """,

            // ── ใบปิดบัญชีต้องแยกออกจากงบกำไรขาดทุน (ผลตรวจ C-T02) ──
            """ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "IsClosingEntry" boolean NOT NULL DEFAULT false;""",
            // แถวเก่า: ใบปิดที่ CloseFiscalPeriodAsync สร้างไว้ทุกเดือนใช้เลข
            // "CL-yyyyMM-XXXX" และเป็น IsAutoGenerated เสมอ — ติดธงย้อนหลังให้
            // งบกำไรขาดทุนของลูกค้าที่เคยกด "ปิดงวด" กลับมาแสดงยอดจริง
            // (ไม่ลบรายการ — เป็นรายการ GL ที่ถูกต้องและต้องเก็บตาม พ.ร.บ.บัญชี ม.10)
            """UPDATE "JournalEntries" SET "IsClosingEntry" = true WHERE "IsClosingEntry" = false AND "IsAutoGenerated" = true AND ("EntryNumber" LIKE 'CL-%' OR "Reference" LIKE 'YE-%');""",
            // Per-company allow-list — one row per (Company, Kind, Role).
            """
            CREATE TABLE IF NOT EXISTS "SensitivityAccessRules" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Kind" integer NOT NULL,
                "Role" integer NOT NULL,
                "CanView" boolean NOT NULL DEFAULT true,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SensitivityAccessRules" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_SensitivityAccessRules_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SensitivityAccessRules_Company_Kind_Role" ON "SensitivityAccessRules" ("CompanyId", "Kind", "Role") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_Sensitivity" ON "Documents" ("CompanyId", "Sensitivity") WHERE "Sensitivity" > 0 AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_JournalEntries_Sensitivity" ON "JournalEntries" ("CompanyId", "Sensitivity") WHERE "Sensitivity" > 0 AND "IsDeleted" = false;""",

            // VAT filing history (ภ.พ.30 ย้อนหลังที่ยื่นในระบบเดิม).
            // One row per (Company, Year, Month) keeps it simple — upsert semantics.
            """
            CREATE TABLE IF NOT EXISTS "VatFilingHistories" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Year" integer NOT NULL,
                "Month" integer NOT NULL,
                "SalesTotal" numeric(18,2) NOT NULL DEFAULT 0,
                "OutputVat" numeric(18,2) NOT NULL DEFAULT 0,
                "PurchaseTotal" numeric(18,2) NOT NULL DEFAULT 0,
                "InputVat" numeric(18,2) NOT NULL DEFAULT 0,
                "NetPayable" numeric(18,2) NOT NULL DEFAULT 0,
                "IsFiled" boolean NOT NULL DEFAULT true,
                "FiledAt" timestamp NULL,
                "FilingReference" varchar(100) NULL,
                "Notes" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_VatFilingHistories" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_VatFilingHistories_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_VatFilingHistories_Company_Year_Month" ON "VatFilingHistories" ("CompanyId", "Year", "Month") WHERE "IsDeleted" = false;""",

            // Visual floor plan + tables for POS.
            """
            CREATE TABLE IF NOT EXISTS "PosFloorPlans" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "Name" varchar(100) NOT NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CanvasWidth" integer NOT NULL DEFAULT 1200,
                "CanvasHeight" integer NOT NULL DEFAULT 800,
                "BackgroundImageUrl" text NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosFloorPlans" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosFloorPlans_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS "PosTables" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "FloorPlanId" uuid NOT NULL,
                "TableNumber" varchar(50) NOT NULL,
                "Seats" integer NOT NULL DEFAULT 4,
                "Shape" varchar(20) NOT NULL DEFAULT 'rectangle',
                "X" integer NOT NULL DEFAULT 0,
                "Y" integer NOT NULL DEFAULT 0,
                "Width" integer NOT NULL DEFAULT 100,
                "Height" integer NOT NULL DEFAULT 80,
                "Rotation" integer NOT NULL DEFAULT 0,
                "Color" varchar(20) NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosTables" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosTables_FloorPlan" FOREIGN KEY ("FloorPlanId") REFERENCES "PosFloorPlans"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PosTables_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PosTables_FloorPlan" ON "PosTables" ("FloorPlanId") WHERE "IsDeleted" = false;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosTables_Floor_Number" ON "PosTables" ("FloorPlanId", "TableNumber") WHERE "IsDeleted" = false;""",

            // Reservations.
            """
            CREATE TABLE IF NOT EXISTS "PosReservations" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "TableId" uuid NULL,
                "TableNumber" varchar(50) NULL,
                "ContactId" uuid NULL,
                "CustomerName" varchar(200) NOT NULL,
                "Phone" varchar(30) NULL,
                "Email" varchar(200) NULL,
                "PartySize" integer NOT NULL DEFAULT 2,
                "ReservedAt" timestamp NOT NULL,
                "DurationMinutes" integer NOT NULL DEFAULT 90,
                "Status" integer NOT NULL DEFAULT 0,
                "PosOrderId" uuid NULL,
                "Notes" text NULL,
                "Source" varchar(50) NULL,
                "ReminderCount" integer NOT NULL DEFAULT 0,
                "LastReminderAt" timestamp NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PosReservations" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PosReservations_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PosReservations_Table" FOREIGN KEY ("TableId") REFERENCES "PosTables"("Id") ON DELETE SET NULL,
                CONSTRAINT "FK_PosReservations_Contact" FOREIGN KEY ("ContactId") REFERENCES "Contacts"("Id") ON DELETE SET NULL
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PosReservations_Company_Date" ON "PosReservations" ("CompanyId", "ReservedAt") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_PosReservations_Table_Date" ON "PosReservations" ("TableId", "ReservedAt") WHERE "IsDeleted" = false AND "TableId" IS NOT NULL;""",
            // Cancellation + deposit columns.
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "FreeCancelHoursBefore" integer NOT NULL DEFAULT 24;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositPaid" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "LateCancelRefundPercent" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositPaidAt" timestamp NULL;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "DepositReference" varchar(200) NULL;""",
            """ALTER TABLE "PosReservations" ADD COLUMN IF NOT EXISTS "PublicToken" varchar(64) NULL;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosReservations_PublicToken" ON "PosReservations" ("PublicToken") WHERE "PublicToken" IS NOT NULL AND "IsDeleted" = false;""",

            // Account-level subscription — one paying User covers N Companies.
            // Resolves the "freelancer accountant pays once for 10 client books"
            // and "holding owner runs 5 sub-companies on one plan" cases.
            """
            CREATE TABLE IF NOT EXISTS "AccountSubscriptions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "OwnerUserId" uuid NOT NULL,
                "PlanTemplateId" uuid NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "StartDate" timestamp NOT NULL DEFAULT now(),
                "EndDate" timestamp NOT NULL,
                "MaxCompanies" integer NOT NULL DEFAULT 1,
                "MaxUsersPerCompany" integer NOT NULL DEFAULT 1,
                "MaxDocumentsPerMonth" integer NOT NULL DEFAULT 30,
                "MaxJournalEntriesPerMonth" integer NOT NULL DEFAULT 50,
                "MaxStorageBytes" bigint NOT NULL DEFAULT 104857600,
                "MaxOcrPagesPerMonth" integer NOT NULL DEFAULT 0,
                "AzureOcrPagesPerMonth" integer NULL,
                "LocalOcrPagesPerMonth" integer NULL,
                "EnabledFeatures" bigint NOT NULL DEFAULT 0,
                "MonthlyPrice" numeric(18,2) NOT NULL DEFAULT 0,
                "AnnualPrice" numeric(18,2) NOT NULL DEFAULT 0,
                "BillingCycle" integer NOT NULL DEFAULT 0,
                "LastPaidAt" timestamp NULL,
                "GracePeriodDays" integer NOT NULL DEFAULT 7,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AccountSubscriptions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_AccountSubscriptions_Users" FOREIGN KEY ("OwnerUserId") REFERENCES "Users"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AccountSubscriptions_Plan" FOREIGN KEY ("PlanTemplateId") REFERENCES "PlanTemplates"("Id")
            );
            """,
            // Only one ACTIVE account-plan per user. Trial(0) / Active(1) /
            // PastDue(2) / Suspended(5) are non-terminal — Expired(4) and
            // Cancelled(3) free the slot so the user can subscribe again.
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AccountSubscriptions_Owner_Active" ON "AccountSubscriptions" ("OwnerUserId") WHERE "Status" IN (0,1,2,5) AND "IsDeleted" = false;""",
            // Subscription gets the AccountSubscriptionId opt-in column.
            """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "AccountSubscriptionId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Subscriptions_AccountSub" ON "Subscriptions" ("AccountSubscriptionId") WHERE "AccountSubscriptionId" IS NOT NULL AND "IsDeleted" = false;""",
            // History rows from cascaded actions point at both layers.
            """ALTER TABLE "SubscriptionHistories" ADD COLUMN IF NOT EXISTS "AccountSubscriptionId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_SubscriptionHistories_AccountSub" ON "SubscriptionHistories" ("AccountSubscriptionId") WHERE "AccountSubscriptionId" IS NOT NULL;""",
            // Reminder bookkeeping on AccountSubscription so the daily job
            // doesn't double-send.
            """ALTER TABLE "AccountSubscriptions" ADD COLUMN IF NOT EXISTS "LastExpiryReminderAt" timestamp NULL;""",
            """ALTER TABLE "AccountSubscriptions" ADD COLUMN IF NOT EXISTS "ExpiryRemindersSentMask" integer NOT NULL DEFAULT 0;""",
            // หมู่ที่ on Contact addresses — needed for provincial / rural
            // tenants where building numbers alone don't identify a property.
            // ETDA e-Tax schema doesn't have a dedicated element so it gets
            // folded into the composed free-text Address by ComposeAddress.
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "Moo" text NULL;""",

            // CompanyInvitation — lets Owners invite people whose email
            // isn't on the platform yet. The invitee gets an email link
            // that doubles as a signup shortcut: signing up with the token
            // auto-joins the company. Existing users open the same link,
            // sign in, and accept.
            """
            CREATE TABLE IF NOT EXISTS "CompanyInvitations" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Email" text NOT NULL,
                "Role" integer NOT NULL DEFAULT 5,
                "InvitedByUserId" uuid NOT NULL,
                "Token" text NOT NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "ExpiresAt" timestamp NOT NULL,
                "AcceptedAt" timestamp NULL,
                "AcceptedByUserId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CompanyInvitations" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CompanyInvitations_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CompanyInvitations_InvitedBy" FOREIGN KEY ("InvitedByUserId") REFERENCES "Users"("Id") ON DELETE RESTRICT
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyInvitations_Token" ON "CompanyInvitations" ("Token") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_CompanyInvitations_Company_Email" ON "CompanyInvitations" ("CompanyId", "Email") WHERE "IsDeleted" = false;""",
            // Mirror on Company so seller addresses on e-Tax XML don't drop
            // Moo when the tenant's own office is provincial / rural.
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "Moo" text NULL;""",

            // CreditNoteReason — required for new CreditNotes; existing rows
            // get NULL (silent grandfather; UI badges them "ไม่ระบุเหตุผล").
            // 1=Return (restocks), 2=Discount, 3=Adjustment, 4=Writeoff.
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CreditNoteReason" integer NULL;""",

            // Payment.OverrideBankAccountId — when populated, settlement GL
            // hits this bank instead of doc.BankAccountId. Lets operators
            // record "Invoice was for Bangkok Bank but cheque cleared via
            // Kasikorn" without editing the original document.
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "OverrideBankAccountId" uuid NULL;""",

            // Payment.WithholdingTaxAmount — per-installment WHT, required
            // under cash-basis WHT (§50/§52) when the customer withholds
            // proportionally on each partial payment. Existing rows default
            // to 0 (pre-cash-basis world; the source invoice already booked
            // WHT-Asset upfront so per-payment WHT is irrelevant for them).
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "WithholdingTaxAmount" numeric(18,2) NOT NULL DEFAULT 0;""",

            // CompanySettings.WhtRecognitionBasis — 1=Cash (legal default
            // per §50/§52), 2=Accrual (existing SMB practice). Existing
            // tenants need to stay on Accrual to keep their historical GL
            // consistent, so they get 2; new tenants will be created with 1.
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "WhtRecognitionBasis" integer NOT NULL DEFAULT 2;""",

            // EF Core convention auto-created a shadow "OwnerId" FK column +
            // FK constraint because AccountSubscription.Owner nav wasn't bound
            // to OwnerUserId in OnModelCreating. The shadow column defaults to
            // Guid.Empty on insert and trips the FK — every "create License"
            // call blew up with FK_AccountSubscriptions_Users_OwnerId. We've
            // since added explicit fluent config; this cleans up the orphan
            // on existing databases.
            """ALTER TABLE "AccountSubscriptions" DROP CONSTRAINT IF EXISTS "FK_AccountSubscriptions_Users_OwnerId";""",
            """ALTER TABLE "AccountSubscriptions" DROP COLUMN IF EXISTS "OwnerId";""",
            // OCR self-learning idempotency watermark — set when VendorIntelligenceService
            // counts this document into the per-vendor stats; prevents double-counting on
            // re-approval (Draft → Approved → Rejected → Draft → Approved).
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OcrIntelTrainedAt" timestamp NULL;
            """,
            // WHT cert dismissal — operator skips a doc from the "waiting to
            // issue WHT cert" list without deleting the source document.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "WhtCertSkipped" boolean NOT NULL DEFAULT false;
            """,
            // External preparer signature override — name + signature image
            // (base64/data-URI) supplied by an integrating system for the
            // "ผู้จัดทำ" slot when the preparer is not a NextAcc User.
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PreparerName" varchar(200) NULL;
            """,
            """
            ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PreparerSignatureBase64" text NULL;
            """,
            // Per-payment GL funding override — pay from เงินทดรองกรรมการ /
            // เงินสดย่อย / clearing instead of the default cash/bank GL.
            """
            ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "OverridePaymentAccountId" uuid NULL;
            """,
            // Integration → NextAcc user mapping: attribute integration actions
            // (and creator signatures) to the real operator the partner sends.
            """
            CREATE TABLE IF NOT EXISTS "IntegrationUserMappings" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "IntegrationId" uuid NOT NULL,
                "ExternalUserKey" varchar(256) NOT NULL,
                "UserId" uuid NOT NULL,
                "ExternalUserName" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_IntegrationUserMappings_Integration_Key"
                ON "IntegrationUserMappings" ("IntegrationId", "ExternalUserKey");
            """,
            // ── สิทธิ์ของคีย์ integration + ธงคีย์รุ่นเก่า (รอบ 193 · G2-01 · คำตัดสินเจ้าของข้อ 37) ──
            // SQL อยู่กับนโยบายที่ Helpers/IntegrationKeyPolicy.MigrationStatements (ตัวตั้งตัวเดียวของ
            // LegacyGraceDays) · แถวที่มีอยู่ ณ วันเพิ่มคอลัมน์ = คีย์รุ่นเก่า (สิทธิ์เต็มจนถึงวันเลิกใช้) ·
            // idempotent: ADD เป็น no-op เมื่อรันซ้ำ และ UPDATE แตะเฉพาะแถวรุ่นเก่าที่ยังไม่มีวัน
            .. Accounting.Helpers.IntegrationKeyPolicy.MigrationStatements(),
            // ===== Bank reconciliation: rejected-match memory + audit log =====
            // New tables; CREATE IF NOT EXISTS so existing DBs gain them on
            // first startup after deploy (the GenerateCreateScript path also
            // creates them on a brand-new DB, but this guarantees it for an
            // existing one without an EF migration).
            """
            CREATE TABLE IF NOT EXISTS "BankMatchExclusions" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankTransactionId" uuid NOT NULL,
                "CandidateId" uuid NOT NULL,
                "CandidateType" varchar(32) NOT NULL DEFAULT '',
                "RejectedAt" timestamp NOT NULL DEFAULT now(),
                "RejectionReason" text NULL,
                "RejectedByUserId" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_BankMatchExclusions_Lookup"
                ON "BankMatchExclusions" ("CompanyId", "BankTransactionId", "CandidateId");
            """,
            """
            CREATE TABLE IF NOT EXISTS "BankMatchAuditLogs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "BankTransactionId" uuid NOT NULL,
                "AppliedByUserId" uuid NOT NULL,
                "AppliedAt" timestamp NOT NULL DEFAULT now(),
                "ConfidenceAtApply" numeric(5,4) NOT NULL DEFAULT 0,
                "OutcomeJson" text NULL,
                "AlternativesJson" text NULL,
                "WasAiValidated" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_BankMatchAuditLog_Lookup"
                ON "BankMatchAuditLogs" ("CompanyId", "BankTransactionId");
            """,
        ];
    }

    /// <summary>
    /// Creates PostgreSQL extensions and GIN indexes for full-text search and trigram matching.
    /// This dramatically improves LIKE '%term%' and text search performance on hot search paths.
    /// </summary>
    public static void ApplyFullTextSearchIndexes(AccountingDbContext db)
    {
        var statements = GetFullTextSearchStatements();
        foreach (var sql in statements)
        {
            try
            {
                db.Database.ExecuteSqlRaw(sql);
            }
            catch
            {
                // Index may already exist or table may not exist — safe to skip
            }
        }
    }

    /// <summary>คำสั่งชุดหลัง (index + คอลัมน์/ย้ายค่าที่ถูกต่อท้ายไว้ที่นี่) — แยกเป็นเมธอดให้เทสต์ตรวจ "คำสั่งที่รันจริง" ได้
    /// (รอบ 193 ฝ่ายค้านรอบสี่ R4-3: ล็อกว่าการย้ายค่าไป DepositBaseDeducted มีคำสั่งเดียว)</summary>
    internal static string[] GetFullTextSearchStatements()
    {
        return new[]
        {
            // Enable pg_trgm for trigram-based LIKE/ILIKE optimization
            """CREATE EXTENSION IF NOT EXISTS pg_trgm;""",
            // Enable unaccent for accent-insensitive search (useful for Thai + multilingual)
            """CREATE EXTENSION IF NOT EXISTS unaccent;""",

            // === Documents: searched by DocumentNumber + Contact name ===
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_DocumentNumber_trgm"
            ON "Documents" USING gin ("DocumentNumber" gin_trgm_ops);
            """,

            // === Contacts: searched by Name, TaxId ===
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_Name_trgm"
            ON "Contacts" USING gin ("Name" gin_trgm_ops);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_TaxId_trgm"
            ON "Contacts" USING gin ("TaxId" gin_trgm_ops);
            """,

            // === Products: searched by Name, Code ===
            """
            CREATE INDEX IF NOT EXISTS "IX_Products_Name_trgm"
            ON "Products" USING gin ("Name" gin_trgm_ops);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Products_Code_trgm"
            ON "Products" USING gin ("Code" gin_trgm_ops);
            """,

            // === ChartOfAccounts: searched by AccountCode, AccountName ===
            """
            CREATE INDEX IF NOT EXISTS "IX_ChartOfAccounts_AccountName_trgm"
            ON "ChartOfAccounts" USING gin ("AccountName" gin_trgm_ops);
            """,

            // === FixedAssets: searched by Name, AssetCode ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'FixedAssets') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_FixedAssets_Name_trgm" ON "FixedAssets" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Loans: searched by LoanNumber, Name ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Loans') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_Loans_Name_trgm" ON "Loans" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Projects: searched by Code, Name ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Projects') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_Projects_Name_trgm" ON "Projects" USING gin ("Name" gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // === Employees: searched by EmployeeCode, FirstNameTh, LastNameTh, FirstNameEn ===
            """
            DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Employees') THEN
                EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_Employees_Names_trgm" ON "Employees" USING gin (("EmployeeCode" || '' '' || COALESCE("FirstNameTh",'''') || '' '' || COALESCE("LastNameTh",'''') || '' '' || COALESCE("FirstNameEn",'''')) gin_trgm_ops)';
            END IF;
            END $$;
            """,

            // ===== CompanySettings: show GL posting summary (Dr/Cr) on printed documents =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ShowGlEntryOnDocument" boolean NOT NULL DEFAULT false;
            """,

            // ===== Employees: accounting payee link (mirror employee as a Contact) =====
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ContactId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Employees_ContactId" ON "Employees" ("ContactId");
            """,

            // ===== EmployeeLeaves: rejection reason for the reject workflow =====
            """
            ALTER TABLE "EmployeeLeaves" ADD COLUMN IF NOT EXISTS "RejectionReason" text NULL;
            """,

            // ===== ExpenseClaims: link to the auto-generated PaymentVoucher document =====
            """
            ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "PaymentVoucherDocumentId" uuid NULL;
            """,

            // ===== CompanySettings: per-company annual leave quotas (JSON by LeaveType) =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LeaveQuotasJson" text NULL;
            """,

            // ===== SalaryAdvances: employee salary-advance workflow → posts to central GL =====
            """
            CREATE TABLE IF NOT EXISTS "SalaryAdvances" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "AdvanceNumber" varchar(50) NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "RequestDate" timestamp NOT NULL,
                "Amount" numeric(18,2) NOT NULL DEFAULT 0,
                "Reason" text NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Draft',
                "MonthlyDeduction" numeric(18,2) NOT NULL DEFAULT 0,
                "ClearedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "OutstandingAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ApprovedByUserId" uuid NULL,
                "ApprovedAt" timestamp NULL,
                "ApprovalNotes" text NULL,
                "RejectionReason" text NULL,
                "DisbursedAt" timestamp NULL,
                "DisbursementDocumentId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SalaryAdvances" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SalaryAdvances_CompanyId_AdvanceNumber"
                ON "SalaryAdvances" ("CompanyId", "AdvanceNumber");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_SalaryAdvances_CompanyId_EmployeeId_Status"
                ON "SalaryAdvances" ("CompanyId", "EmployeeId", "Status");
            """,

            // ===== Departments: first-class org-structure table (replaces
            //                    Employee.Department string + maps to AccountingDimension) =====
            """
            CREATE TABLE IF NOT EXISTS "Departments" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Code" varchar(50) NOT NULL,
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "Description" text NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "ParentDepartmentId" uuid NULL,
                "BranchId" uuid NULL,
                "DimensionId" uuid NULL,
                "DefaultExpenseAccountId" uuid NULL,
                "ManagerEmployeeId" uuid NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_Departments" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Departments_CompanyId_Code"
                ON "Departments" ("CompanyId", "Code");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Departments_DimensionId" ON "Departments" ("DimensionId");
            """,

            // ===== Positions / Job titles =====
            """
            CREATE TABLE IF NOT EXISTS "Positions" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Code" varchar(50) NOT NULL,
                "Title" varchar(200) NOT NULL,
                "TitleEn" varchar(200) NULL,
                "Description" text NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Band" varchar(50) NULL,
                "MinSalary" numeric(18,2) NULL,
                "MaxSalary" numeric(18,2) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_Positions" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Positions_CompanyId_Code"
                ON "Positions" ("CompanyId", "Code");
            """,

            // ===== Employees: org-structure FKs (additive; legacy string
            //                  Department / Position fields preserved for
            //                  back-compat + historical payslip display) =====
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "DepartmentId" uuid NULL;
            """,
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "PositionId" uuid NULL;
            """,
            """
            ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "DirectManagerId" uuid NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Employees_DepartmentId" ON "Employees" ("DepartmentId");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Employees_DirectManagerId" ON "Employees" ("DirectManagerId");
            """,

            // ===== CompanySettings: HR approval enforcement flag =====
            """
            ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EnforceManagerApproval" boolean NOT NULL DEFAULT false;
            """,

            // ===== Users: LINE Messaging API binding (per-user push) =====
            """
            ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "LineUserId" varchar(64) NULL;
            """,

            // ===== NotificationSettings: per-company event × recipient × channel matrix =====
            """
            CREATE TABLE IF NOT EXISTS "NotificationSettings" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "EventKey" varchar(80) NOT NULL,
                "RecipientRole" varchar(50) NOT NULL,
                "EnableSystem" boolean NOT NULL DEFAULT false,
                "EnableEmail" boolean NOT NULL DEFAULT false,
                "EnableLine" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_NotificationSettings" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_NotificationSettings_CompanyId_EventKey_RecipientRole"
                ON "NotificationSettings" ("CompanyId", "EventKey", "RecipientRole");
            """,

            // ===== NotificationPreferences: per-user suppression overrides =====
            """
            CREATE TABLE IF NOT EXISTS "NotificationPreferences" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "EventKey" varchar(80) NOT NULL,
                "SuppressSystem" boolean NOT NULL DEFAULT false,
                "SuppressEmail" boolean NOT NULL DEFAULT false,
                "SuppressLine" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_NotificationPreferences" PRIMARY KEY ("Id")
            );
            """,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_NotificationPreferences_CompanyId_UserId_EventKey"
                ON "NotificationPreferences" ("CompanyId", "UserId", "EventKey");
            """,

            // ===== SystemAccountTemplates: admin-editable master Chart of Accounts =====
            """
            CREATE TABLE IF NOT EXISTS "SystemAccountTemplates" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "AccountCode" varchar(20) NOT NULL,
                "AccountNameTh" varchar(256) NOT NULL,
                "AccountNameEn" text NULL,
                "AccountType" integer NOT NULL DEFAULT 0,
                "Level" integer NOT NULL DEFAULT 1,
                "IsActive" boolean NOT NULL DEFAULT true,
                "BusinessType" integer NULL,
                "IndustryType" integer NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SystemAccountTemplates" PRIMARY KEY ("Id")
            );
            """,
            // Scope columns — ADD IF NOT EXISTS for tables created before they existed.
            """ALTER TABLE "SystemAccountTemplates" ADD COLUMN IF NOT EXISTS "BusinessType" integer NULL;""",
            """ALTER TABLE "SystemAccountTemplates" ADD COLUMN IF NOT EXISTS "IndustryType" integer NULL;""",
            // Old single-column unique index no longer valid (a code may repeat
            // across scopes) — replace with a plain lookup index.
            """DROP INDEX IF EXISTS "IX_SystemAccountTemplates_AccountCode";""",
            """
            CREATE INDEX IF NOT EXISTS "IX_SystemAccountTemplates_Scope"
                ON "SystemAccountTemplates" ("BusinessType", "IndustryType", "AccountCode");
            """,

            // ===== DocumentLines.SourceLineId: partial / flexible document composition =====
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "SourceLineId" uuid NULL;""",
            """
            CREATE INDEX IF NOT EXISTS "IX_DocumentLines_SourceLineId"
                ON "DocumentLines" ("SourceLineId") WHERE "SourceLineId" IS NOT NULL;
            """,

            // ===== ChartOfAccounts.InputVatClaimable: prohibited input VAT (ภาษีซื้อต้องห้าม) =====
            """ALTER TABLE "ChartOfAccounts" ADD COLUMN IF NOT EXISTS "InputVatClaimable" boolean NOT NULL DEFAULT true;""",
            // Back-fill: mark existing entertainment (ค่ารับรอง) accounts non-claimable
            // so their input VAT stops being credited on the ภ.พ.30. Scoped to
            // IsSystemAccount — those are seeded/managed by the system and cannot
            // be edited by admins, so this stays safe to re-run on every startup
            // without overriding a deliberate admin change on a custom account.
            """
            UPDATE "ChartOfAccounts" SET "InputVatClaimable" = false
            WHERE ("AccountCode" LIKE '54460%' OR "AccountName" LIKE '%รับรอง%')
              AND "IsSystemAccount" = true
              AND "InputVatClaimable" = true;
            """,

            // ===== DocumentBrands: ชื่อทางการค้า/แบรนด์ที่ใช้ออกเอกสาร =====
            // กิจการเดียวขายหลายแบรนด์ — ใบเสนอราคา/ใบแจ้งหนี้/ใบส่งของ ขึ้นหัวเป็น
            // ชื่อร้าน + โลโก้ร้านได้ ส่วนเอกสารภาษี (ใบกำกับ ฯลฯ) ยังบังคับชื่อ
            // นิติบุคคลเป็นตัวหลักตาม §86/4 (ด่านอยู่ที่ DocumentIssuerIdentity)
            """
            CREATE TABLE IF NOT EXISTS "DocumentBrands" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Name" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "TagLine" varchar(300) NULL,
                "TagLineEn" varchar(300) NULL,
                "LogoPath" varchar(500) NULL,
                "LogoUrl" varchar(1000) NULL,
                "Address" varchar(1000) NULL,
                "AddressEn" varchar(1000) NULL,
                "Phone" varchar(100) NULL,
                "Email" varchar(200) NULL,
                "Website" varchar(300) NULL,
                "PrimaryColor" varchar(20) NULL,
                "SecondaryColor" varchar(20) NULL,
                "DefaultTemplateId" uuid NULL,
                "FooterNotes" varchar(2000) NULL,
                "FooterNotesEn" varchar(2000) NULL,
                "LegalNamePlacement" varchar(20) NOT NULL DEFAULT 'Footer',
                "IsActive" boolean NOT NULL DEFAULT true,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_DocumentBrands_Company" ON "DocumentBrands" ("CompanyId", "IsActive");""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BrandId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DocumentTemplateId" uuid NULL;""",
            // Delete แบรนด์สแกนหาเอกสารที่ใช้อยู่ — ไม่มี index = full scan ต่อบริษัท
            """CREATE INDEX IF NOT EXISTS "IX_Documents_Brand" ON "Documents" ("CompanyId", "BrandId") WHERE "BrandId" IS NOT NULL;""",

            // ===== สาขาผู้ออกเอกสาร (เฟส 1 ระบบหลายสาขา) =====
            // null ทั้งสองคอลัมน์ = พฤติกรรมเดิมทุกประการ (กิจการสาขาเดียวไม่กระทบ)
            // IssuerBranchCode = snapshot รหัส 5 หลักที่พิมพ์จริงตอนอนุมัติ — แก้ทะเบียน
            // สาขาภายหลังต้องไม่ย้อนไปเปลี่ยนใบกำกับที่ออกไปแล้ว (§86/4)
            // ที่อยู่ของแบรนด์ผูกกับทะเบียนบริษัท/สาขาได้ — แถวเก่าเป็น 'Custom'
            // (พิมพ์เอง) เพื่อคงพฤติกรรมเดิมทุกประการ
            """ALTER TABLE "DocumentBrands" ADD COLUMN IF NOT EXISTS "AddressSource" varchar(20) NOT NULL DEFAULT 'Custom';""",
            """ALTER TABLE "DocumentBrands" ADD COLUMN IF NOT EXISTS "AddressSourceBranchId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IssuerBranchCode" varchar(5) NULL;""",
            // รายงานภาษีซื้อ/ขายแยกตามสถานประกอบการ (§87) สแกนด้วยคู่นี้
            """CREATE INDEX IF NOT EXISTS "IX_Documents_Branch" ON "Documents" ("CompanyId", "BranchId") WHERE "BranchId" IS NOT NULL;""",

            // ===== Backfill: DocumentLine.Amount ที่เก็บ "ยอดรวม VAT" (ผิด convention) =====
            // OCR รุ่นก่อน 2026-08-14 เก็บยอดรวม VAT ลง Line.Amount บนใบที่ราคารวม VAT
            // ⇒ ตอนอนุมัติ JE ลง Dr ค่าใช้จ่าย(รวม VAT) + Dr ภาษีซื้อ(VAT ซ้ำ) =
            // เดบิตเกินเครดิตเท่ายอด VAT พอดี → อนุมัติไม่ได้ตลอดกาล (เคสจริง IKEA
            // 1,396.00 = 1,304.68 + VAT 91.32 → Dr 1,487.32 ≠ Cr 1,396.00)
            // ต้นทางแก้แล้ว แต่แถวเก่าค้างของเสีย — ซ่อมให้ตรงนี้ครั้งเดียว
            // เงื่อนไข Σ Amount = SubTotal + VatAmount เป็นจริงได้เฉพาะตอนเก็บ gross
            // (ถ้าเก็บ net อยู่แล้ว Σ Amount = SubTotal) ⇒ พอซ่อมเสร็จเงื่อนไขเป็นเท็จ
            // = รันซ้ำทุก startup ได้ไม่พัง (idempotent) และไม่แตะยอดหัวเอกสารเลย
            """
            UPDATE "DocumentLines" dl
               SET "Amount" = ROUND(dl."Amount" - dl."VatAmount", 2)
              FROM "Documents" d
             WHERE dl."DocumentId" = d."Id"
               AND d."PricesIncludeVat" = true
               AND d."VatAmount" > 0.02
               AND d."IsDeleted" = false
               AND dl."IsDeleted" = false
               AND (SELECT ROUND(SUM(x."Amount"), 2) FROM "DocumentLines" x
                     WHERE x."DocumentId" = d."Id" AND x."IsDeleted" = false)
                   = ROUND(d."SubTotal" + d."VatAmount", 2)
               AND (SELECT ROUND(SUM(x."VatAmount"), 2) FROM "DocumentLines" x
                     WHERE x."DocumentId" = d."Id" AND x."IsDeleted" = false)
                   = ROUND(d."VatAmount", 2);
            """,

            // ===== TaxReportLines.IsExcluded: accountant include/exclude toggle =====
            """ALTER TABLE "TaxReportLines" ADD COLUMN IF NOT EXISTS "IsExcluded" boolean NOT NULL DEFAULT false;""",

            // ===== Documents.IsOpeningBalance: migrated opening AR/AP subledger docs =====
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsOpeningBalance" boolean NOT NULL DEFAULT false;""",

            // ===== PosOrderItems.RefundedQuantity: POS partial refunds =====
            """ALTER TABLE "PosOrderItems" ADD COLUMN IF NOT EXISTS "RefundedQuantity" numeric NOT NULL DEFAULT 0;""",
            // ===== PosOrderItems.CostOfGoodsSold: COGS ที่บิลขายลงไว้จริง (E-01 รอบ 193) =====
            // NULL = บิลเก่า → คืนเงินกลับตามสูตรขายเดิม (Helpers/PosCogsBooking.LegacyUnitCost) · ไม่ backfill
            """ALTER TABLE "PosOrderItems" ADD COLUMN IF NOT EXISTS "CostOfGoodsSold" numeric NULL;""",

            // ===== ServiceComponents.CommissionTypeConfirmedAt (รอบ 193 · M2) =====
            // NULL = แถวที่บันทึกก่อนแก้ฟอร์ม pos-packages (option 0/1 กลับด้าน) → ติดป้ายให้ตรวจ · ไม่ backfill
            """ALTER TABLE "ServiceComponents" ADD COLUMN IF NOT EXISTS "CommissionTypeConfirmedAt" timestamp NULL;""",

            // ===== PosOrders.ClientOrderId: offline-sale idempotency key =====
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "ClientOrderId" uuid NULL;""",
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosOrders_CompanyId_ClientOrderId"
                ON "PosOrders" ("CompanyId", "ClientOrderId") WHERE "ClientOrderId" IS NOT NULL;
            """,

            // ===== Composite indexes for hot read paths flagged by the perf audit =====
            // Every TenantEntity query filters by CompanyId first; the secondary
            // filter is usually a foreign key + date. Single-column FKs alone
            // make Postgres seq-scan within the FK group.
            """
            CREATE INDEX IF NOT EXISTS "IX_BankTransactions_CompanyId_BankAccount_Date"
                ON "BankTransactions" ("CompanyId", "BankAccountId", "TransactionDate" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_StockMovements_CompanyId_Product_Date"
                ON "StockMovements" ("CompanyId", "ProductId", "CreatedAt" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_EmployeeLeaves_Employee_Status_StartDate"
                ON "EmployeeLeaves" ("EmployeeId", "Status", "StartDate");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_JournalEntries_CompanyId_EntryDate"
                ON "JournalEntries" ("CompanyId", "EntryDate" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_Type_Date"
                ON "Documents" ("CompanyId", "DocumentType", "DocumentDate" DESC);
            """,
            // §86/4 backstop — เลขเอกสารที่ออกจริงต้องไม่ซ้ำต่อ (CompanyId, DocumentNumber).
            // เดิมกันด้วย pg_advisory_xact_lock ตอนออกเลขเท่านั้น (ไม่มี index กัน insert
            // ที่หลุด lock/นอก transaction). ทำเป็น partial unique: ยกเว้น draft
            // (DRAFT-{guid} placeholder) และ soft-deleted เพื่อไม่ชนกับ lifecycle ปกติ.
            // ถ้ามี dup เดิมในฐานข้อมูล → CREATE ล้ม แต่ถูก catch+log ใน ApplyMissingColumns
            // (advisory lock ยังกันต่อไป) — เคลียร์ dup แล้ว index จะสร้างได้รอบถัดไป.
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_Documents_CompanyId_DocumentNumber"
                ON "Documents" ("CompanyId", "DocumentNumber")
                WHERE "IsDeleted" = false AND "DocumentNumber" NOT LIKE 'DRAFT-%';
            """,
            // Document.ExchangeRate — multi-currency FX rate persisted per doc
            // so JE auto-post can convert non-THB amounts to THB consistently.
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ExchangeRate" numeric(18,6) NOT NULL DEFAULT 1;""",

            // ===== Performance indexes (S5 sprint) — composite covering ที่
            // ใช้บ่อยในรายงาน/หน้าเอกสาร/AR/AP/audit. WHERE !IsDeleted กรอง
            // soft-delete รวด (Postgres bitmap-scan ใช้ได้ทันที) =====
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_Status_Date"
                ON "Documents" ("CompanyId", "Status", "DocumentDate" DESC)
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_ContactId_Date"
                ON "Documents" ("CompanyId", "ContactId", "DocumentDate" DESC)
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_RelatedDoc"
                ON "Documents" ("CompanyId", "RelatedDocumentId")
                WHERE "RelatedDocumentId" IS NOT NULL AND "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Documents_OutstandingAR"
                ON "Documents" ("CompanyId", "DueDate")
                WHERE "Status" IN (1,3,8) AND "BalanceDue" > 0 AND "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_DocumentLines_AccountId_Doc"
                ON "DocumentLines" ("AccountId", "DocumentId")
                WHERE "AccountId" IS NOT NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Payments_CompanyId_Document"
                ON "Payments" ("CompanyId", "DocumentId")
                WHERE "IsDeleted" = false;
            """,
            // outbound integration payments-list — filter/order ตาม (CompanyId, PaymentDate)
            """
            CREATE INDEX IF NOT EXISTS "IX_Payments_CompanyId_PaymentDate"
                ON "Payments" ("CompanyId", "PaymentDate" DESC)
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_JournalEntryLines_AccountId_Date"
                ON "JournalEntryLines" ("AccountId", "JournalEntryId");
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_AuditLogs_CompanyId_EntityType_Time"
                ON "AuditLogs" ("CompanyId", "EntityType", "Timestamp" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_TaxReportLines_DocumentId_Excluded"
                ON "TaxReportLines" ("DocumentId", "IsExcluded")
                WHERE "DocumentId" IS NOT NULL;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_StockMovements_Company_Product_Date"
                ON "StockMovements" ("CompanyId", "ProductId", "MovementDate" DESC);
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Contacts_Company_Active"
                ON "Contacts" ("CompanyId")
                WHERE "IsDeleted" = false;
            """,
            """
            CREATE INDEX IF NOT EXISTS "IX_Notifications_User_Unread"
                ON "Notifications" ("UserId", "CreatedAt" DESC)
                WHERE "IsRead" = false AND "IsDeleted" = false;
            """,

            // ===== LineBindCodes — LINE bot user-to-account linking =====
            """
            CREATE TABLE IF NOT EXISTS "LineBindCodes" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
                "Code" varchar(10) NOT NULL,
                "ExpiresAt" timestamptz NOT NULL,
                "UsedAt" timestamptz NULL,
                "UsedByLineUserId" varchar(64) NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NULL,
                "CreatedBy" varchar(64) NULL,
                "UpdatedBy" varchar(64) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_LineBindCodes_Code" ON "LineBindCodes" ("Code") WHERE "UsedAt" IS NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Users_LineUserId" ON "Users" ("LineUserId") WHERE "LineUserId" IS NOT NULL;""",
            // LINE เดียว = บัญชีเดียว — เดิมเป็น index ธรรมดา (บรรทัดถัดขึ้นไปในไฟล์
            // เดียวกันเขียน CREATE UNIQUE INDEX ให้ LineUserStates อยู่แล้ว) และ
            // ตอนผูกก็ไม่เคยล้างค่าเดิมของผู้ใช้รายอื่น ⇒ มีสองแถวค่าเท่ากันได้ แล้ว
            // `FirstOrDefaultAsync` ที่ไม่มี ORDER BY หยิบแถวไหนก็ได้ตามแผน query
            // ⇒ บอทตอบในนามบัญชีที่ผู้ใช้ไม่ได้ตั้งใจ (ผลตรวจทีม E · E-07)
            // ต้องล้างของซ้ำก่อน ไม่งั้น CREATE UNIQUE INDEX ล้ม: เก็บแถวที่ผูกล่าสุด
            """UPDATE "Users" u SET "LineUserId" = NULL WHERE "LineUserId" IS NOT NULL AND EXISTS (SELECT 1 FROM "Users" v WHERE v."LineUserId" = u."LineUserId" AND (COALESCE(v."UpdatedAt", v."CreatedAt"), v."Id") > (COALESCE(u."UpdatedAt", u."CreatedAt"), u."Id"));""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_LineUserId" ON "Users" ("LineUserId") WHERE "LineUserId" IS NOT NULL;""",

            // ===== LineUserStates — multi-company active selection per LINE user =====
            """
            CREATE TABLE IF NOT EXISTS "LineUserStates" (
                "Id" uuid PRIMARY KEY,
                "LineUserId" varchar(64) NOT NULL,
                "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
                "ActiveCompanyId" uuid NULL REFERENCES "Companies"("Id") ON DELETE SET NULL,
                "LastInteractionAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamptz NULL,
                "CreatedBy" varchar(64) NULL,
                "UpdatedBy" varchar(64) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LineUserStates_LineUserId" ON "LineUserStates" ("LineUserId");""",

            // ===== ProductAliases — OCR-driven product name aliases for fuzzy matching =====
            """
            CREATE TABLE IF NOT EXISTS "ProductAliases" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ProductId" uuid NOT NULL,
                "ContactId" uuid NULL,
                "AliasName" varchar(500) NOT NULL,
                "NormalizedName" varchar(500) NOT NULL,
                "TimesUsed" integer NOT NULL DEFAULT 1,
                "LastUsedAt" timestamp NOT NULL DEFAULT now(),
                "Source" varchar(20) NOT NULL DEFAULT 'user',
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductAliases" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductAliases_Products" FOREIGN KEY ("ProductId") REFERENCES "Products"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ProductAliases_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_CompanyId_NormalizedName" ON "ProductAliases" ("CompanyId", "NormalizedName") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_CompanyId_ContactId_NormalizedName" ON "ProductAliases" ("CompanyId", "ContactId", "NormalizedName") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_ProductId" ON "ProductAliases" ("ProductId") WHERE "IsDeleted" = false;""",
            // Trigram index on the normalized form — powers SIMILARITY()
            // queries from the OCR product matcher in sub-50ms.
            """CREATE INDEX IF NOT EXISTS "IX_ProductAliases_NormalizedName_Trgm" ON "ProductAliases" USING gin ("NormalizedName" gin_trgm_ops);""",
            // Trigram index on Product.Name too — first-pass match when no
            // alias exists yet for a freshly OCR'd description.
            """CREATE INDEX IF NOT EXISTS "IX_Products_Name_Trgm" ON "Products" USING gin ("Name" gin_trgm_ops);""",

            // ===== GlobalProductPatterns — cross-tenant federated learning of product wordings =====
            // Anonymized, system-wide. No tenant identifiers in the row itself —
            // distinct-tenant counting is offloaded to the tiny join table below.
            """
            CREATE TABLE IF NOT EXISTS "GlobalProductPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "NormalizedKey" varchar(500) NOT NULL,
                "CanonicalLabel" varchar(500) NULL,
                "Brand" varchar(100) NULL,
                "Unit" varchar(50) NULL,
                "CategoryHint" varchar(100) NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalProductPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GlobalProductPatterns_NormalizedKey" ON "GlobalProductPatterns" ("NormalizedKey") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GlobalProductPatterns_Status" ON "GlobalProductPatterns" ("Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GlobalProductPatterns_NormalizedKey_Trgm" ON "GlobalProductPatterns" USING gin ("NormalizedKey" gin_trgm_ops);""",

            // Tracks DISTINCT tenants who confirmed each pattern — uniqueness
            // counter ONLY. One row per (PatternId, CompanyId). Never joined
            // back to tenant data in matching queries; the per-tenant data
            // is queried inside that tenant's own ProductAlias table.
            """
            CREATE TABLE IF NOT EXISTS "GlobalProductPatternTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalProductPatternTenantSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GPPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalProductPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GPPSeens_PatternId_CompanyId" ON "GlobalProductPatternTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== SuppliesUsageLogs — multi-line notes + issued-to fields =====
            """ALTER TABLE "SuppliesUsageLogs" ADD COLUMN IF NOT EXISTS "Notes" text NULL;""",
            """ALTER TABLE "SuppliesUsageLogs" ADD COLUMN IF NOT EXISTS "IssuedToUserId" text NULL;""",
            """ALTER TABLE "SuppliesUsageLogs" ADD COLUMN IF NOT EXISTS "IssuedToName" varchar(200) NULL;""",

            // ===== ProductNegativeAliases — "this OCR wording is NOT this product" =====
            // Stops the matcher from re-suggesting a candidate the user has
            // explicitly rejected. Scoped to (CompanyId, NormalizedKey,
            // RejectedProductId) — rejection is per-wording, not blanket.
            """
            CREATE TABLE IF NOT EXISTS "ProductNegativeAliases" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "NormalizedName" varchar(500) NOT NULL,
                "RejectedProductId" uuid NOT NULL,
                "ContactId" uuid NULL,
                "Reason" varchar(200) NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_ProductNegativeAliases" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ProductNegativeAliases_Products" FOREIGN KEY ("RejectedProductId") REFERENCES "Products"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_ProductNegativeAliases_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_NegAlias_Company_Norm" ON "ProductNegativeAliases" ("CompanyId", "NormalizedName") WHERE "IsDeleted" = false;""",

            // ===== GlobalExpenseCategoryPatterns — federated category prediction =====
            """
            CREATE TABLE IF NOT EXISTS "GlobalExpenseCategoryPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL,
                "DescriptionKeyword" varchar(200) NULL,
                "AccountCode" varchar(50) NOT NULL,
                "AccountName" varchar(200) NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalExpenseCategoryPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_GECP_VendorKeyword" ON "GlobalExpenseCategoryPatterns" ("VendorKey", "DescriptionKeyword") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GECP_Status" ON "GlobalExpenseCategoryPatterns" ("Status") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "GlobalExpenseCategoryTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GECPSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GECPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalExpenseCategoryPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GECPSeens_Pattern_Company" ON "GlobalExpenseCategoryTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== SystemOcrVendorIntelligence — track distinct contributing tenants =====
            """ALTER TABLE "SystemOcrVendorIntelligence" ADD COLUMN IF NOT EXISTS "TenantContributionCount" integer NOT NULL DEFAULT 0;""",

            """
            CREATE TABLE IF NOT EXISTS "SystemOcrVendorIntelTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "SystemIntelId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_SVITenantSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_SVITenantSeens_Intel" FOREIGN KEY ("SystemIntelId") REFERENCES "SystemOcrVendorIntelligence"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_SVITenantSeens_Intel_Company" ON "SystemOcrVendorIntelTenantSeens" ("SystemIntelId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== GlobalDocWorkflowPatterns — federated doc-type prediction =====
            // (VendorKey, ScannedDocType) → TargetDocType consensus across tenants.
            """
            CREATE TABLE IF NOT EXISTS "GlobalDocWorkflowPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "VendorKey" varchar(200) NOT NULL,
                "ScannedDocType" varchar(50) NOT NULL,
                "TargetDocType" varchar(50) NOT NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalDocWorkflowPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_GDWP_Vendor_Scanned" ON "GlobalDocWorkflowPatterns" ("VendorKey", "ScannedDocType") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "GlobalDocWorkflowTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GDWPSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GDWPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalDocWorkflowPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GDWPSeens_Pattern_Company" ON "GlobalDocWorkflowTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== GlobalAssetCategoryPatterns — federated FixedAsset category + useful-life =====
            """
            CREATE TABLE IF NOT EXISTS "GlobalAssetCategoryPatterns" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "NormalizedKey" varchar(500) NOT NULL,
                "Category" varchar(200) NOT NULL,
                "UsefulLifeMonths" integer NOT NULL,
                "DepreciationMethod" varchar(50) NULL,
                "TenantCount" integer NOT NULL DEFAULT 0,
                "TotalConfirms" integer NOT NULL DEFAULT 0,
                "FirstSeenAt" timestamp NOT NULL DEFAULT now(),
                "LastConfirmedAt" timestamp NOT NULL DEFAULT now(),
                "Status" varchar(20) NOT NULL DEFAULT 'candidate',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GlobalAssetCategoryPatterns" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_GACP_NormalizedKey" ON "GlobalAssetCategoryPatterns" ("NormalizedKey") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_GACP_Status" ON "GlobalAssetCategoryPatterns" ("Status") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "GlobalAssetCategoryTenantSeens" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PatternId" uuid NOT NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_GACPSeens" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_GACPSeens_Pattern" FOREIGN KEY ("PatternId") REFERENCES "GlobalAssetCategoryPatterns"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_GACPSeens_Pattern_Company" ON "GlobalAssetCategoryTenantSeens" ("PatternId", "CompanyId") WHERE "IsDeleted" = false;""",

            // ===== CmsLeads — unified lead capture for RFQ/viewing/demo/enrollment/etc =====
            """
            CREATE TABLE IF NOT EXISTS "CmsLeads" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "SiteId" uuid NOT NULL,
                "LeadNumber" varchar(50) NOT NULL,
                "LeadType" integer NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "SourceSlug" varchar(100) NULL,
                "CustomerName" varchar(200) NULL,
                "CustomerEmail" varchar(200) NULL,
                "CustomerPhone" varchar(50) NULL,
                "CustomerCompany" varchar(200) NULL,
                "CustomerTaxId" varchar(50) NULL,
                "Message" text NULL,
                "DataJson" text NULL,
                "AssignedToUserId" uuid NULL,
                "AssignedToName" varchar(200) NULL,
                "QualifiedAt" timestamp NULL,
                "QuotedAt" timestamp NULL,
                "WonAt" timestamp NULL,
                "LostAt" timestamp NULL,
                "LostReason" varchar(500) NULL,
                "InternalNotes" text NULL,
                "ErpDocumentId" uuid NULL,
                "ContactId" uuid NULL,
                "CompanyId" uuid NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_CmsLeads" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_CmsLeads_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id"),
                CONSTRAINT "FK_CmsLeads_Sites" FOREIGN KEY ("SiteId") REFERENCES "Sites"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_CmsLeads_Contacts" FOREIGN KEY ("ContactId") REFERENCES "Contacts"("Id") ON DELETE SET NULL
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_CmsLeads_Company_Site_Created" ON "CmsLeads" ("CompanyId", "SiteId", "CreatedAt" DESC) WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_CmsLeads_Status" ON "CmsLeads" ("CompanyId", "SiteId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CmsLeads_Company_LeadNumber" ON "CmsLeads" ("CompanyId", "LeadNumber") WHERE "IsDeleted" = false;""",

            // ===== AI Integration tables (DeepSeek / OpenAI / Anthropic / etc) =====
            // Provider registry — one row per configured provider, at most
            // one IsActive at a time (enforced by partial unique index).
            """
            CREATE TABLE IF NOT EXISTS "AiProviderConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "ProviderType" integer NOT NULL,
                "DisplayName" varchar(200) NOT NULL DEFAULT '',
                "IsActive" boolean NOT NULL DEFAULT false,
                "IsEnabled" boolean NOT NULL DEFAULT true,
                "Endpoint" varchar(500) NULL,
                "ApiKey" text NULL,
                "Model" varchar(200) NOT NULL DEFAULT 'deepseek-chat',
                "Temperature" decimal(4,2) NOT NULL DEFAULT 0.10,
                "MaxOutputTokens" integer NOT NULL DEFAULT 1024,
                "RequestTimeoutSeconds" integer NOT NULL DEFAULT 8,
                "DailyCallCap" integer NULL,
                "MonthlyBudgetUsd" decimal(18,4) NULL,
                "PricePerInputTokenUsd1M" decimal(18,6) NULL,
                "PricePerOutputTokenUsd1M" decimal(18,6) NULL,
                "LastTestedAt" timestamp NULL,
                "LastTestStatus" varchar(500) NULL,
                "ExtraSettingsJson" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiProviderConfigs" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiProviderConfigs_OneActive" ON "AiProviderConfigs" ("IsActive") WHERE "IsActive" = true;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiProviderConfigs_ProviderType" ON "AiProviderConfigs" ("ProviderType");""",

            // Per-call feedback row — the training set. Every orchestrator
            // invocation writes here (Success / Cached / Failed / Skipped).
            """
            CREATE TABLE IF NOT EXISTS "AiSuggestionFeedbacks" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "PromptHash" varchar(80) NOT NULL,
                "PromptJson" jsonb NOT NULL,
                "ResponseJson" jsonb NULL,
                "AiPrimaryAnswer" text NULL,
                "AiConfidence" decimal(5,4) NULL,
                "LocalModelAnswer" text NULL,
                "LocalModelConfidence" decimal(5,4) NULL,
                "LocalModelVersion" varchar(100) NULL,
                "UserChosenAnswer" text NULL,
                "UserChosenAt" timestamp NULL,
                "UserAcceptedAi" boolean NULL,
                "SourceEntityType" varchar(100) NULL,
                "SourceEntityId" uuid NULL,
                "Status" integer NOT NULL DEFAULT 1,
                "ProviderUsed" integer NOT NULL DEFAULT 1,
                "ModelVersion" varchar(200) NULL,
                "LatencyMs" integer NULL,
                "InputTokens" integer NULL,
                "OutputTokens" integer NULL,
                "CostUsd" decimal(18,6) NULL,
                "CacheHitOfFeedbackId" uuid NULL,
                "ErrorMessage" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiSuggestionFeedbacks" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_AiSuggestionFeedbacks_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_Company_Feature_Date" ON "AiSuggestionFeedbacks" ("CompanyId", "FeatureKey", "CreatedAt" DESC) WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_PromptHash" ON "AiSuggestionFeedbacks" ("PromptHash") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_Feature_UserChosen" ON "AiSuggestionFeedbacks" ("FeatureKey", "UserChosenAt") WHERE "IsDeleted" = false;""",

            // ── ป้ายกำกับ "ใครเป็นคนเรียก" (รายงานใช้ AI แยกรายลูกค้า/ช่องทาง) ──
            // นิยามชุดเดียวกับ UsageEvent: ApiClientId เป็น null = ใช้ผ่านหน้าเว็บ
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "Channel" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "BillingAccountId" uuid NULL;""",
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;""",
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "ApiClientId" uuid NULL;""",
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "UserId" uuid NULL;""",
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "IsSandbox" boolean NOT NULL DEFAULT false;""",
            // เจาะดูรายคีย์ของลูกค้า API — ไล่ตามช่วงวันที่
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_ApiClient_Date" ON "AiSuggestionFeedbacks" ("ApiClientId", "CreatedAt" DESC) WHERE "IsDeleted" = false AND "ApiClientId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiSuggestionFeedbacks_Company_Channel_Date" ON "AiSuggestionFeedbacks" ("CompanyId", "Channel", "CreatedAt" DESC) WHERE "IsDeleted" = false;""",

            // ── สรุปรายวันแยกลูกค้า + ช่องทาง (ตารางหลักของรายงาน) ──
            // แยกจาก AiUsageDailies โดยตั้งใจ — ตารางเดิม unique ที่
            // (วัน, provider, feature) และวิดเจ็ตแอดมินอ่านอยู่ ถ้าเอาแถวแยก
            // บริษัทไปปนกับแถวรวมในตารางเดียว ผลรวมจะนับซ้ำทันที
            """
            CREATE TABLE IF NOT EXISTS "AiUsageDailyTenants" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "UsageDate" timestamp NOT NULL,
                "CompanyId" uuid NOT NULL,
                "BillingAccountId" uuid NULL,
                "ProviderType" integer NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "Channel" integer NOT NULL DEFAULT 0,
                "CallsTotal" integer NOT NULL DEFAULT 0,
                "CallsAi" integer NOT NULL DEFAULT 0,
                "CallsCached" integer NOT NULL DEFAULT 0,
                "CallsLocalServed" integer NOT NULL DEFAULT 0,
                "CallsFailed" integer NOT NULL DEFAULT 0,
                "CallsBudgetBlocked" integer NOT NULL DEFAULT 0,
                "CallsNoProvider" integer NOT NULL DEFAULT 0,
                "InputTokensTotal" bigint NOT NULL DEFAULT 0,
                "OutputTokensTotal" bigint NOT NULL DEFAULT 0,
                "CostUsdTotal" decimal(18,6) NOT NULL DEFAULT 0,
                "LatencySumMs" bigint NOT NULL DEFAULT 0,
                "LatencySamples" integer NOT NULL DEFAULT 0,
                "UserReviewed" integer NOT NULL DEFAULT 0,
                "UserAcceptedAi" integer NOT NULL DEFAULT 0,
                "IsSandbox" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiUsageDailyTenants" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiUsageDailyTenants_Day_Co_Provider_Feature_Channel" ON "AiUsageDailyTenants" ("UsageDate", "CompanyId", "ProviderType", "FeatureKey", "Channel") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiUsageDailyTenants_Company_Date" ON "AiUsageDailyTenants" ("CompanyId", "UsageDate" DESC) WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiUsageDailyTenants_BillingAccount_Date" ON "AiUsageDailyTenants" ("BillingAccountId", "UsageDate" DESC) WHERE "IsDeleted" = false;""",

            // Prompt response cache — tenant-scoped, content-addressed.
            """
            CREATE TABLE IF NOT EXISTS "AiResponseCaches" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "PromptHash" varchar(80) NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "ResponseJson" jsonb NOT NULL,
                "ProviderUsed" integer NOT NULL,
                "ModelVersion" varchar(200) NULL,
                "Confidence" decimal(5,4) NULL,
                "CompanyId" uuid NULL,
                "HitCount" integer NOT NULL DEFAULT 0,
                "LastHitAt" timestamp NULL,
                "ExpiresAt" timestamp NOT NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiResponseCaches" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiResponseCaches_Hash_Company" ON "AiResponseCaches" ("PromptHash", "CompanyId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiResponseCaches_ExpiresAt" ON "AiResponseCaches" ("ExpiresAt") WHERE "IsDeleted" = false;""",

            // Per-feature local-model health (one row per FeatureKey).
            """
            CREATE TABLE IF NOT EXISTS "LocalModelHealths" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "FeatureKey" varchar(100) NOT NULL,
                "LocalModelVersion" varchar(100) NOT NULL DEFAULT 'v1',
                "SamplesLast30d" integer NOT NULL DEFAULT 0,
                "LocalAccuracy30d" decimal(5,4) NOT NULL DEFAULT 0,
                "AiAccuracy30d" decimal(5,4) NOT NULL DEFAULT 0,
                "AgreementRate30d" decimal(5,4) NOT NULL DEFAULT 0,
                "LastEvaluatedAt" timestamp NOT NULL DEFAULT now(),
                "Status" integer NOT NULL DEFAULT 1,
                "Recommendation" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_LocalModelHealths" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocalModelHealths_FeatureKey" ON "LocalModelHealths" ("FeatureKey") WHERE "IsDeleted" = false;""",

            // ── สุขภาพนักเรียนต้องแยกต่อบริษัท (รอบ 178) ──────────────────────
            // เดิม unique ที่ FeatureKey อย่างเดียว ⇒ เก็บได้แถวเดียวต่อ feature
            // = ตัวเลขของทุก tenant ถูกเฉลี่ยรวมกัน (ทีม T3) · แถว CompanyId = NULL
            // คือยอดรวมทั้งแพลตฟอร์มที่หน้าแอดมินอ่าน — ต้องคงไว้
            """ALTER TABLE "LocalModelHealths" ADD COLUMN IF NOT EXISTS "CompanyId" uuid NULL;""",
            """ALTER TABLE "LocalModelHealths" ADD COLUMN IF NOT EXISTS "LocalSamplesLast30d" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "LocalModelHealths" ADD COLUMN IF NOT EXISTS "LocalCoverage30d" numeric(5,4) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "LocalModelHealths" ADD COLUMN IF NOT EXISTS "ExplicitLabels30d" integer NOT NULL DEFAULT 0;""",
            """DROP INDEX IF EXISTS "IX_LocalModelHealths_FeatureKey";""",
            // ⚠️ Postgres ถือว่า NULL ต่างกันเสมอใน unique index ⇒ index เดียวบน
            // ("FeatureKey","CompanyId") **ไม่กัน** แถวยอดรวม (CompanyId IS NULL) ซ้ำ
            // ต้องแยกเป็นสอง partial index (หลักการ "ด่านที่ไม่กันอะไรเลย = ไม่มีด่าน")
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocalModelHealths_Feature_Company" ON "LocalModelHealths" ("FeatureKey", "CompanyId") WHERE "IsDeleted" = false AND "CompanyId" IS NOT NULL;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocalModelHealths_Feature_Platform" ON "LocalModelHealths" ("FeatureKey") WHERE "IsDeleted" = false AND "CompanyId" IS NULL;""",

            // Per-feature routing policy — admin sets mode + thresholds
            // per AiFeatureKey. Sparse (rows missing fall back to global
            // defaults in the orchestrator).
            """
            CREATE TABLE IF NOT EXISTS "AiFeatureRoutingConfigs" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "FeatureKey" varchar(100) NOT NULL,
                "Mode" integer NOT NULL DEFAULT 3,
                "LocalConfidenceThreshold" decimal(5,4) NULL,
                "ProviderSamplingRate" decimal(5,4) NULL,
                "AdminNote" text NULL,
                "LastModifiedBy" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiFeatureRoutingConfigs" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiFeatureRoutingConfigs_FeatureKey" ON "AiFeatureRoutingConfigs" ("FeatureKey") WHERE "IsDeleted" = false;""",

            // Daily usage rollup — drives the admin AI burn widget. One
            // row per (day, provider, feature). Job upserts at end-of-day.
            """
            CREATE TABLE IF NOT EXISTS "AiUsageDailies" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "UsageDate" timestamp NOT NULL,
                "ProviderType" integer NOT NULL,
                "FeatureKey" varchar(100) NOT NULL,
                "CallsAttempted" integer NOT NULL DEFAULT 0,
                "CallsSuccessful" integer NOT NULL DEFAULT 0,
                "CallsCached" integer NOT NULL DEFAULT 0,
                "CallsFailed" integer NOT NULL DEFAULT 0,
                "CallsBudgetBlocked" integer NOT NULL DEFAULT 0,
                "InputTokensTotal" bigint NOT NULL DEFAULT 0,
                "OutputTokensTotal" bigint NOT NULL DEFAULT 0,
                "CostUsdTotal" decimal(18,6) NOT NULL DEFAULT 0,
                "AvgLatencyMs" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_AiUsageDailies" PRIMARY KEY ("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiUsageDailies_Day_Provider_Feature" ON "AiUsageDailies" ("UsageDate", "ProviderType", "FeatureKey") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_AiUsageDailies_UsageDate" ON "AiUsageDailies" ("UsageDate" DESC) WHERE "IsDeleted" = false;""",

            // SiteSettings — AI master switches.
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiAugmentationEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiReviewConfidenceThreshold" decimal(5,4) NOT NULL DEFAULT 0.6500;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiSamplingRate" decimal(5,4) NOT NULL DEFAULT 0.1000;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiDefaultCacheTtlDays" integer NOT NULL DEFAULT 30;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiStripPiiInPrompts" boolean NOT NULL DEFAULT true;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiVerifyAgainstThaiComplianceRules" boolean NOT NULL DEFAULT true;""",
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "AiLastFeedbackTrainingAt" timestamp NULL;""",

            // OcrScanResult — AI augmentation trail.
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "AiSuggestedContactId" uuid NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "AiSuggestionFeedbackId" uuid NULL;""",
            // OcrScanResult — GL-account (expense) DeepSeek classification trail.
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountUsedAi" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "GlAccountAiFeedbackId" uuid NULL;""",
            // Document — Payment Voucher อ้างใบกำกับภาษี (RD §86/4 + §86/14).
            // HasTaxInvoiceReference = flag จาก checkbox "ใช้งานใบกำกับภาษี".
            // SupplierBranchCode = snapshot สาขาผู้ขายตอนออกใบกำกับ
            // (กัน Contact.BranchCode ถูกแก้ภายหลังแล้วรายงานภาษีซื้อย้อนหลังเพี้ยน).
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "HasTaxInvoiceReference" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SupplierBranchCode" varchar(5) NULL;""",

            // EmployeeLeave — half-day support added 2026.
            """ALTER TABLE "EmployeeLeaves" ADD COLUMN IF NOT EXISTS "HalfDayMarker" integer NOT NULL DEFAULT 0;""",

            // CompanySettings — owner-level feature + menu overrides
            // (subtractive only; Subscription.EnabledFeatures is the
            // upper bound, this lets the Owner opt-out features they
            // don't use / hide menus they find noisy).
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OwnerDisabledFeatures" bigint NOT NULL DEFAULT 0;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "OwnerHiddenMenuIdsJson" text NULL;""",

            // LeaveType — HR-configurable catalog (replaces hardcoded
            // string keys in CompanySettings.LeaveQuotasJson).
            """
            CREATE TABLE IF NOT EXISTS "LeaveTypes" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Code" varchar(50) NOT NULL,
                "NameTh" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "AnnualQuota" decimal(10,2) NOT NULL DEFAULT 0,
                "IsPaid" boolean NOT NULL DEFAULT true,
                "AllowHalfDay" boolean NOT NULL DEFAULT true,
                "CarryForward" boolean NOT NULL DEFAULT false,
                "CarryForwardCap" decimal(10,2) NULL,
                "AdvanceNoticeDays" integer NOT NULL DEFAULT 0,
                "RequiresAttachment" boolean NOT NULL DEFAULT false,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Color" varchar(20) NOT NULL DEFAULT '#6366f1',
                "Icon" varchar(20) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_LeaveTypes" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_LeaveTypes_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LeaveTypes_Company_Code" ON "LeaveTypes" ("CompanyId", "Code") WHERE "IsDeleted" = false;""",

            // PublicHoliday — calendar.
            """
            CREATE TABLE IF NOT EXISTS "PublicHolidays" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Date" timestamp NOT NULL,
                "NameTh" varchar(200) NOT NULL,
                "NameEn" varchar(200) NULL,
                "Category" varchar(50) NOT NULL DEFAULT 'Public',
                "IsSubstitute" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_PublicHolidays" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_PublicHolidays_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PublicHolidays_Company_Date" ON "PublicHolidays" ("CompanyId", "Date") WHERE "IsDeleted" = false;""",

            // EmployeeLeaveBalance — carry-forward + manual HR adjustment.
            """
            CREATE TABLE IF NOT EXISTS "EmployeeLeaveBalances" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "Year" integer NOT NULL,
                "LeaveTypeCode" varchar(50) NOT NULL,
                "CarriedForwardDays" decimal(10,2) NOT NULL DEFAULT 0,
                "AdjustmentDays" decimal(10,2) NOT NULL DEFAULT 0,
                "Notes" text NULL,
                "Phase" varchar(20) NOT NULL DEFAULT 'Manual',
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_EmployeeLeaveBalances" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_ELB_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id"),
                CONSTRAINT "FK_ELB_Employees" FOREIGN KEY ("EmployeeId") REFERENCES "Employees"("Id")
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ELB_Company_Emp_Year_Type" ON "EmployeeLeaveBalances" ("CompanyId", "EmployeeId", "Year", "LeaveTypeCode") WHERE "IsDeleted" = false;""",

            // Contact — per-contact GL account overrides. Default AR =
            // "113" prefix in FindAccountAsync; specific contacts can pin
            // their own (e.g. ลูกหนี้พนักงาน vs ลูกหนี้การค้า).
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultArAccountId" uuid NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultApAccountId" uuid NULL;""",
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "DefaultIrGrAccountId" uuid NULL;""",

            // ExpenseClaim — no-receipt claim (§65 ทวิ) auto-generates a
            // Document(CertificateInLieu) on Approve. New columns added
            // 2026 — idempotent ADD COLUMN IF NOT EXISTS.
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "NoReceipt" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "NoReceiptReason" varchar(500) NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "WitnessName" varchar(200) NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "WitnessPosition" varchar(200) NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "CertificateInLieuDocumentId" uuid NULL;""",

            // AnomalyDetection — lazy AI explanation cache.
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiVerdict" varchar(50) NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiConfidence" decimal(5,4) NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiReasoning" text NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiSuggestedActionsJson" text NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiRisksJson" text NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiFeedbackId" uuid NULL;""",
            """ALTER TABLE "AnomalyDetections" ADD COLUMN IF NOT EXISTS "AiExplainedAt" timestamp NULL;""",

            // ===== Project external sync (partner system linkage) =====
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "ExternalId" varchar(200) NULL;""",
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(50) NULL;""",
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "LastSyncedAt" timestamp NULL;""",
            """ALTER TABLE "Projects" ADD COLUMN IF NOT EXISTS "ExternalUrl" text NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Projects_External" ON "Projects" ("CompanyId", "ExternalSystem", "ExternalId") WHERE "ExternalId" IS NOT NULL AND "IsDeleted" = false;""",

            // ===== Project allocation rolled out to remaining entities =====
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "ExpenseClaims" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "ExpenseClaimLines" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "PettyCashTransactions" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "Cheques" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "StampDutyRecords" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """ALTER TABLE "Budgets" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Payments_Project" ON "Payments" ("ProjectId") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ExpenseClaims_Project" ON "ExpenseClaims" ("ProjectId") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",

            // Approval rule per-project scope — lets a company route project X
            // spend through PM Alice and project Y through PM Bob at the same
            // amount tier. Backfilled NULL = applies to all projects (legacy).
            """ALTER TABLE "ApprovalRules" ADD COLUMN IF NOT EXISTS "ProjectId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ApprovalRules_Project" ON "ApprovalRules" ("ProjectId") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",

            // Document gains supplier-side tax invoice metadata for
            // PurchaseInvoice + credit-term fields. Indexed by
            // (CompanyId, SupplierInvoiceNumber) so partner-statement
            // reconciliation can find a row in one hop.
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SupplierInvoiceNumber" varchar(100) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SupplierTaxInvoiceDate" timestamp NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CreditDays" integer NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "PaymentTerms" varchar(100) NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_Supplier_Invoice" ON "Documents" ("CompanyId", "SupplierInvoiceNumber") WHERE "SupplierInvoiceNumber" IS NOT NULL AND "IsDeleted" = false;""",

            // Reverse-lookup index — finding all child docs of a source
            // (e.g. all DNs spawned from a QT) currently does a full scan.
            // Index lets the detail-modal "เอกสารต่อเนื่อง" section + the
            // ?relatedDocumentId= filter return in O(log n).
            """CREATE INDEX IF NOT EXISTS "IX_Documents_RelatedDocument" ON "Documents" ("CompanyId", "RelatedDocumentId") WHERE "RelatedDocumentId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_DocumentLines_SourceLine" ON "DocumentLines" ("SourceLineId") WHERE "SourceLineId" IS NOT NULL;""",

            // Per-line link from a ProjectCostEntry back to the DocumentLine
            // that spawned it. Lets the doc UI flag each line "🏗️ ลงโครงการแล้ว"
            // without re-parsing the auto-marker in the Description.
            """ALTER TABLE "ProjectCostEntries" ADD COLUMN IF NOT EXISTS "DocumentLineId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProjectCostEntries_DocumentLine" ON "ProjectCostEntries" ("DocumentLineId") WHERE "DocumentLineId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProjectCostEntries_Document" ON "ProjectCostEntries" ("CompanyId", "DocumentId") WHERE "DocumentId" IS NOT NULL AND "IsDeleted" = false;""",

            // Attendance metadata on EmployeeProjectTime — optional
            // fields synced from clock-in / attendance systems. If
            // never set, payroll calc behaves exactly as before.
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "OvertimeHours" decimal(8,2) NULL;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "IsHoliday" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "HasPerDiem" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "HasAccommodation" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "HasOvertimeMeal" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "EmployeeProjectTimes" ADD COLUMN IF NOT EXISTS "AttendanceMetadataJson" text NULL;""",

            // Per-employee + company-wide compensation profile tables.
            """
            CREATE TABLE IF NOT EXISTS "EmployeeCompensationProfiles" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "OvertimeRateMultiplierWeekday" decimal(6,3) NULL,
                "OvertimeRateMultiplierHoliday" decimal(6,3) NULL,
                "PerDiemRate" decimal(18,2) NULL,
                "AccommodationAllowance" decimal(18,2) NULL,
                "OvertimeMealAllowance" decimal(18,2) NULL,
                "CustomBenefitsJson" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmployeeCompProfile_Employee" ON "EmployeeCompensationProfiles" ("CompanyId", "EmployeeId") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "CompanyCompensationDefaults" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "OvertimeRateMultiplierWeekday" decimal(6,3) NOT NULL DEFAULT 1.5,
                "OvertimeRateMultiplierHoliday" decimal(6,3) NOT NULL DEFAULT 3.0,
                "PerDiemRate" decimal(18,2) NOT NULL DEFAULT 500,
                "AccommodationAllowance" decimal(18,2) NOT NULL DEFAULT 800,
                "OvertimeMealAllowance" decimal(18,2) NOT NULL DEFAULT 30,
                "StandardWorkHoursPerDay" decimal(6,2) NOT NULL DEFAULT 8,
                "StandardWorkDaysPerMonth" decimal(6,2) NOT NULL DEFAULT 30,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyCompDefaults_Company" ON "CompanyCompensationDefaults" ("CompanyId") WHERE "IsDeleted" = false;""",
            // Daily meal (คนละกรณีกับ OT meal) + custom allowances list
            // ที่บริษัทตั้งเองได้ (โทรศัพท์ ค่าเดินทาง ค่าน้ำมัน ฯลฯ).
            """ALTER TABLE "CompanyCompensationDefaults" ADD COLUMN IF NOT EXISTS "DailyMealAllowance" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "CompanyCompensationDefaults" ADD COLUMN IF NOT EXISTS "CustomAllowancesJson" text NULL;""",
            // PayrollDetail.AdvanceRecovered — per-employee advance repayment
            // recorded at Pay time so Void can restore it precisely. Without
            // this, voiding a payroll silently zeroed each employee's
            // outstanding advance balance.
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "AdvanceRecovered" numeric(18,2) NOT NULL DEFAULT 0;""",
            // ฐานค่าจ้างประกันสังคม (ม.33) รายคนต่อรอบ — ตัวเลขที่ต้องปรากฏใน
            // ช่อง "ค่าจ้าง" ของ สปส.1-10 และเป็นฐานของทั้งฝั่งลูกจ้าง/นายจ้าง
            // เดิม exporter เอา GrossIncome มาใส่ช่องค่าจ้างแต่ใช้ยอดสมทบที่เก็บไว้
            // ⇒ คู่ตัวเลขขัดกันเองทันทีที่มีการแก้ยอด. 0 = แถวเก่าที่ยังไม่เคย
            // ตั้งฐาน (Helpers/SsoWageBase.Resolve อนุมานให้จากยอดสมทบจริง)
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "SocialSecurityBase" numeric(18,2) NOT NULL DEFAULT 0;""",
            // PayrollRun กลับรายการจ่าย (Paid → Approved) — ร่องรอยว่าใครกลับ
            // รายการเมื่อไรเพราะอะไร ใช้ทั้ง audit และแบนเนอร์เตือนบนหน้าจอว่า
            // เอกสาร ภ.ง.ด.1/สปส.1-10/สลิป ที่แนบไว้ยังเป็นฉบับก่อนแก้
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ReopenedAt" timestamptz NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ReopenedBy" text NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ReopenReason" text NULL;""",
            // Employee tax allowances §47/47ทวิ — ละเอียดขึ้นจากที่เก่า
            // เป็น count × 30K เฉย ๆ (ครอบครัวใหญ่ over-withhold).
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "HasSpouseAllowance" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ChildAllowanceCount" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "SecondAndLaterChildren" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ParentAllowanceCount" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "LifeInsurancePremium" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "RmfSsfContribution" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "DonationAmount" numeric(18,2) NOT NULL DEFAULT 0;""",

            // ===== Auto-email scheduling =====
            """
            CREATE TABLE IF NOT EXISTS "EmailScheduleRules" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Trigger" varchar(40) NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "DocumentType" varchar(40) NULL,
                "OffsetDays" integer NOT NULL DEFAULT 0,
                "SendAtHour" integer NOT NULL DEFAULT 9,
                "RepeatEveryDays" integer NOT NULL DEFAULT 0,
                "SubjectTemplate" text NULL,
                "BodyTemplate" text NULL,
                "BccEmails" text NULL,
                "CreatedByName" varchar(200) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_EmailScheduleRules" PRIMARY KEY ("Id")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EmailRules_Company_Trigger" ON "EmailScheduleRules" ("CompanyId", "Trigger") WHERE "IsDeleted" = false;""",
            """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "DayOfMonth" integer NOT NULL DEFAULT 5;""",
            """ALTER TABLE "EmailScheduleRules" ADD COLUMN IF NOT EXISTS "AudienceFilter" varchar(100) NULL;""",

            // ── Seed กฎเตือนใกล้ครบกำหนดให้ tenant เดิม ──
            // ที่มา: `BackgroundJobService.ProcessPaymentReminders` เคยส่งอีเมล
            // เตือนให้ **ทุก** บริษัทโดยไม่ต้องตั้งกฎ แต่ส่งตรงทุก 15 นาที
            // ไม่มี marker ⇒ ~380 ฉบับต่อใบแจ้งหนี้ 1 ใบ. เมื่อลบตัวนั้นทิ้ง
            // งานย้ายไปอยู่กับ EmailScheduleService ซึ่งทำงาน **เฉพาะ tenant
            // ที่มีกฎ** ⇒ ถ้าไม่ seed ให้ บริษัทเดิมจะเงียบไปเลยโดยไม่รู้ตัว
            //
            // ค่าที่ seed ตรงกับพฤติกรรมเดิมที่สุด: ล่วงหน้า 3 วัน (OffsetDays
            // = -3 ตามกติกา DueSoon) ส่ง 09:00 น. ครั้งเดียว (RepeatEveryDays=0)
            // เฉพาะ Invoice. บริษัทที่ตั้งกฎ DocumentDueSoon ไว้เองแล้วจะถูกข้าม
            // (WHERE NOT EXISTS) — ห้ามทับเจตนาของผู้ใช้
            """
            INSERT INTO "EmailScheduleRules"
                ("Id","CompanyId","Trigger","IsActive","DocumentType","OffsetDays",
                 "SendAtHour","RepeatEveryDays","CreatedBy")
            SELECT gen_random_uuid(), c."Id", 'DocumentDueSoon', true, 'Invoice', -3, 9, 0,
                   'seed:replaces-ProcessPaymentReminders'
            FROM "Companies" c
            WHERE c."IsDeleted" = false
              AND NOT EXISTS (
                    SELECT 1 FROM "EmailScheduleRules" r
                    WHERE r."CompanyId" = c."Id"
                      AND r."Trigger" = 'DocumentDueSoon'
                      AND r."IsDeleted" = false);
            """,
            // ===== ล้างค่า "เลขที่เอกสาร" ออกจากคลังค่าประจำผู้ขาย =====
            // VendorKnownGoodCorrector เคยเอาเลขที่เอกสารของใบก่อนหน้ามา
            // fuzzy-match แล้วทับเลขของใบที่กำลังสแกน (เลขรันติดกันต่างกันหลัก
            // เดียว = similarity 0.83-0.92 เกินเกณฑ์ 0.80 เสมอ) ⇒ รายงานภาษีซื้อ
            // §87 ยื่นเลขใบกำกับผิด. แก้โค้ดอย่างเดียวไม่พอ — แถวที่สะสมไว้แล้ว
            // ยังนอนอยู่ในตาราง (1 แถว/1 ใบ) ต้องลบทิ้งด้วย
            """DELETE FROM "VendorKnownGoodValues" WHERE "FieldName" = 'DocumentNumber';""",
            // Refresh-token reuse detection (security hardening).
            """ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "PreviousRefreshToken" varchar(500) NULL;""",
            """ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "RefreshTokenRevokedAt" timestamp NULL;""",
            // Negative-inventory guard. Default false = strict (refuse OUT
            // that would take CurrentStock below zero).
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AllowNegativeStock" boolean NOT NULL DEFAULT false;""",

            // ===== Payment: per-request payer signature override =====
            // ใช้ใน PV PDF "ผู้จ่ายเงิน" slot สำหรับ caller ที่ user ไม่มีลายเซ็น
            // upload ไว้ (เช่น service account ของระบบเชื่อมต่อ).
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "PayerSignatureBase64" text NULL;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "PayerSignatureName" varchar(200) NULL;""",

            // ===== Payments: settlement-day FX rate (realized FX gain/loss) =====
            // rate ณ วันชำระจริงของเอกสารสกุลต่างประเทศ — ต่างจาก rate เอกสาร →
            // post กำไร/ขาดทุนอัตราแลกเปลี่ยนที่เกิดขึ้นจริง (42600/54950)
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ExchangeRate" numeric(18,6) NULL;""",
            // ค่าธรรมเนียม marketplace/gateway/ธนาคาร ที่ถูกหักจากยอดโอน
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "FeeAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "FeeAccountId" uuid NULL;""",

            // ===== Contact: credit limit (วงเงินเครดิต) =====
            // null = ไม่จำกัด (เดิม). ใช้ดู AR เทียบเตือนตอนสร้าง Invoice ใหม่.
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "CreditLimit" numeric(18,2) NULL;""",

            // ===== AiSuggestionMemory — online-learning store (train ไปเลย) =====
            // upsert ทันทีเมื่อ user ยืนยัน/แก้ suggestion; suggestion ครั้งถัด
            // อ่านค่าที่เรียนแล้วก่อน ไม่ต้องรอ nightly job / ไม่ต้องกดปุ่ม.
            """
            CREATE TABLE IF NOT EXISTS "AiSuggestionMemories" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FeatureKey" varchar(64) NOT NULL,
                "InputKey" varchar(256) NOT NULL,
                "LearnedAnswer" varchar(512) NOT NULL DEFAULT '',
                "AcceptCount" integer NOT NULL DEFAULT 0,
                "OverrideCount" integer NOT NULL DEFAULT 0,
                "Confidence" numeric(5,4) NOT NULL DEFAULT 0,
                "LastLearnedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_AiSuggestionMemories_Company_Feature_Input" ON "AiSuggestionMemories" ("CompanyId", "FeatureKey", "InputKey") WHERE "IsDeleted" = false;""",

            // ===== LINE Messaging API per-company (was global in appsettings) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineChannelAccessToken" varchar(2000) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineChannelSecret" varchar(500) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineDefaultGroupId" varchar(100) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineConfigured" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineLastTestedAt" timestamp NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineLastTestStatus" varchar(500) NULL;""",

            // ===== CompanySettings: OCR แหล่งเงิน default (ฝั่ง Cr ของ PV/Receipt) =====
            // null = พฤติกรรมเดิม (auto-pick bank รหัสต่ำสุด). ตั้งเป็น
            // ChartOfAccount.Id ของบัญชีเงินสด/ธนาคารหลัก → OCR ใช้เป็น default.
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "DefaultPaymentAccountId" uuid NULL;""",

            // ===== CompanySettings: กองทุนเงินทดแทน (กท.20ก) =====
            // อัตราสมทบนายจ้างฝ่ายเดียว 0.2–1.0% ตามประเภทกิจการ. default 0.2%
            // (หมวด 1 สำนักงาน); ปิดไว้ default เพื่อไม่ดับเบิ้ลโพสต์ข้อมูลเดิม.
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "WorkersCompensationRatePercent" decimal(4,2) NOT NULL DEFAULT 0.2;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "WorkersCompensationEnabled" boolean NOT NULL DEFAULT false;""",

            // ===== CompanySettings: §86/4 hard-block opt-in =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EnforceFullTaxInvoiceFields" boolean NOT NULL DEFAULT false;""",
            // รูปแบบการออกใบกำกับ/ใบเสร็จ (ReceiptIssueMode): 0=ใบเดียวจบ (เดิม)
            // 1=แยกใบกำกับ–ใบเสร็จเสมอ 2=ค้าปลีกใบเดียวที่จุดขาย. DEFAULT 0 ⇒
            // tenant เดิมไม่กระทบ
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "ReceiptIssueMode" integer NOT NULL DEFAULT 0;""",
            // ใบเสร็จ standalone ที่มีสินค้าคงคลัง: 0=ไม่แตะสต๊อก (เดิม) 1=ตัดสต๊อก+COGS (ค่าแนะนำ · DEFAULT) 2=บล็อก
            // DEFAULT 1 ตั้งใจเปลี่ยนพฤติกรรม — ของเดิมคือบั๊กกำไรขั้นต้น (ERP_REVIEW_2026-09-05 A-06)
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "CashSaleStockPolicy" integer NOT NULL DEFAULT 1;""",
            // บัญชีทิปพนักงานค้างจ่ายที่ POS/TipPayout ใช้ (NULL = default 21814→21819 · ERP_REVIEW H-07)
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "PosTipPayableAccountCode" varchar(20) NULL;""",
            // "หัวมีคำว่าใบกำกับภาษี → เลขชุด TIV เสมอ" (แกนคนละแกนกับ ReceiptIssueMode)
            // **nullable โดยตั้งใจ**: NULL = ยังไม่เคยตั้ง → ระบบใช้ค่าแนะนำ = เปิด
            // (TaxInvoiceSeriesPolicy.IsUnifiedSeriesEnabled) ⇒ ไม่ต้องให้ผู้ใช้ไปหา
            // สวิตช์เอง. ผู้ที่ไม่ต้องการเก็บ false ไว้ซึ่งจะไม่ถูกทับอีก —
            // จงใจ**ไม่มี** UPDATE ไล่ตั้ง true เพราะ migration รันทุกครั้งที่สตาร์ท
            // ⇒ จะทับเจตนาผู้ใช้ทุกรอบ (defect class "ห้ามทับค่าที่ผู้ใช้ตั้งเอง")
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "UnifyTaxInvoiceNumberSeries" boolean NULL;""",
            """ALTER TABLE "CompanySettings" ALTER COLUMN "UnifyTaxInvoiceNumberSeries" DROP NOT NULL;""",
            """ALTER TABLE "CompanySettings" ALTER COLUMN "UnifyTaxInvoiceNumberSeries" DROP DEFAULT;""",
            // วิธีบันทึกเงินมัดจำฝั่งขาย (รอบ 193 #34) — **nullable โดยตั้งใจ**: NULL = ยังไม่เคยตั้ง
            // → Helpers/DepositPolicyResolver ใช้ค่าตามประเภทธุรกิจ · ห้าม UPDATE ไล่ตั้งค่า
            // (migration รันทุกครั้งที่สตาร์ท ⇒ จะทับเจตนาผู้ใช้)
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "DepositVatTreatment" integer NULL;""",
            // บทบาททางกฎหมายของเอกสาร ตรึงตอนอนุมัติพร้อมเลขที่ — nullable เพราะ
            // ใบที่อนุมัติก่อนมีฟีเจอร์นี้ "ยังไม่เคยตรึง" (≠ ไม่ใช่ใบกำกับ)
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsTaxInvoiceByLaw" boolean NULL;""",

            // ===== DocumentAdjustingJournalLines (Option 1: เพิ่ม Dr/Cr ลอย) =====
            // ใช้รองรับเคส PV/Doc 1 ใบ มี Dr/Cr เพิ่มเติมที่ไม่ map กับ DocumentLine
            // ปกติ (ค่าธรรมเนียมโอน, สำรอง, ปันส่วน). AutoPost รวมเข้า JE +
            // validate balance Dr=Cr รวม.
            """
            CREATE TABLE IF NOT EXISTS "DocumentAdjustingJournalLines" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "DocumentId" uuid NOT NULL REFERENCES "Documents"("Id") ON DELETE CASCADE,
                "LineOrder" integer NOT NULL DEFAULT 0,
                "AccountId" uuid NOT NULL,
                "DebitAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "CreditAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Description" varchar(500) NULL,
                "ProjectId" uuid NULL,
                "Reason" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_DocumentAdjustingJournalLines_Doc" ON "DocumentAdjustingJournalLines" ("DocumentId");""",

            // ===== CompanySettings: ผู้ทำบัญชี (พ.ร.บ.การบัญชี ม.7) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "BookkeeperName" varchar(200) NULL;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "BookkeeperCpdNumber" varchar(50) NULL;""",

            // ===== PDPA Wave 3: RoPA / Consent / PiiAccessLog / Breach =====
            // RoPA (ม.39) — บันทึกกิจกรรมการประมวลผลข้อมูลส่วนบุคคล
            """
            CREATE TABLE IF NOT EXISTS "PdpaProcessingActivities" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "Purpose" varchar(500) NOT NULL,
                "LegalBasis" varchar(100) NOT NULL,
                "DataCategories" varchar(1000) NOT NULL,
                "RetentionPeriod" varchar(500) NOT NULL,
                "Recipients" varchar(1000) NULL,
                "TransfersOutsideThailand" boolean NOT NULL DEFAULT false,
                "TransferSafeguards" varchar(1000) NULL,
                "Notes" text NULL,
                "LastReviewedAt" timestamp NOT NULL DEFAULT now(),
                "LastReviewedBy" varchar(200) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaProcessingActivities_Company" ON "PdpaProcessingActivities" ("CompanyId");""",

            // Consent records (ม.19, 22) — เก็บประวัติยินยอม + ถอน
            """
            CREATE TABLE IF NOT EXISTS "PdpaConsentRecords" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "SubjectUserId" uuid NULL,
                "SubjectContactId" uuid NULL,
                "SubjectContact" varchar(200) NULL,
                "Purpose" varchar(500) NOT NULL,
                "PolicyVersion" varchar(50) NOT NULL DEFAULT '1.0',
                "GrantedAt" timestamp NOT NULL DEFAULT now(),
                "WithdrawnAt" timestamp NULL,
                "WithdrawnReason" varchar(500) NULL,
                "EvidenceHash" varchar(100) NULL,
                "Channel" varchar(50) NULL,
                "IpAddress" varchar(45) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaConsentRecords_Subject" ON "PdpaConsentRecords" ("CompanyId", "SubjectUserId", "SubjectContactId");""",

            // PII access log (ม.37(4)) — เก็บ event อ่าน PII ≥ 1 ปี
            """
            CREATE TABLE IF NOT EXISTS "PdpaPiiAccessLogs" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "ActorUserId" uuid NOT NULL,
                "ActorEmail" varchar(200) NOT NULL,
                "SubjectType" varchar(50) NOT NULL,
                "SubjectId" uuid NOT NULL,
                "FieldName" varchar(500) NOT NULL,
                "Operation" varchar(50) NOT NULL DEFAULT 'Read',
                "Purpose" varchar(500) NOT NULL,
                "At" timestamp NOT NULL DEFAULT now(),
                "IpAddress" varchar(45) NULL,
                "UserAgent" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaPiiAccessLogs_At" ON "PdpaPiiAccessLogs" ("CompanyId", "At" DESC);""",
            """CREATE INDEX IF NOT EXISTS "IX_PdpaPiiAccessLogs_Subject" ON "PdpaPiiAccessLogs" ("CompanyId", "SubjectType", "SubjectId");""",

            // ===== ส่งสลิปเงินเดือนทาง LINE (self-service bind + secure token) =====
            // Employee.LineId (LINE userId Uxxxx) — push แจ้งเตือน/สลิปทาง LINE.
            // ใช้มาก่อน (NotificationEngine/PayslipLineDelivery/PayrollService) แต่
            // ไม่เคยมี migration → DB เก่า (สร้างก่อนมี field นี้) จะไม่มีคอลัมน์ →
            // 500 ตอนแจ้งเตือนพนักงาน/ส่งสลิป LINE. IF NOT EXISTS = idempotent.
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "LineId" varchar(100) NULL;""",
            // LINE OA basic id (@xxx) ต่อบริษัท — ทำลิงก์/QR เพิ่มเพื่อนให้พนักงาน
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "LineOaBasicId" varchar(100) NULL;""",

            // ===== ECL — ค่าเผื่อหนี้สงสัยจะสูญอัตโนมัติ (TFRS NPAEs บทที่ 9) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EclEnabled" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "EclLossRatesJson" text NULL;""",

            // ===== SoD + Commitment control (internal control ระดับ ERP) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SodBlockSelfApproval" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "BudgetCommitmentMode" varchar(10) NOT NULL DEFAULT 'Off';""",

            // ===== Landed cost — ต้นทุนแฝงการซื้อ/นำเข้า เกลี่ยเข้าต้นทุนสินค้า =====
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "IsLandedCost" boolean NOT NULL DEFAULT false;""",

            // ===== Cost center / มิติ บนเอกสาร → ไหลลง JE (รายงาน P&L ต่อสาขา/แผนก) =====
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DimensionId" uuid NULL;""",

            // ===== Quotation online accept (ลิงก์ลูกค้ากดยอมรับใบเสนอราคา) =====
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAcceptToken" varchar(80) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAcceptTokenExpiresAt" timestamptz NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAcceptedAt" timestamptz NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAcceptedBy" varchar(200) NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_QuotationAcceptToken" ON "Documents" ("QuotationAcceptToken") WHERE "QuotationAcceptToken" IS NOT NULL;""",

            // ===== Delivery e-sign (ลูกค้าเซ็นรับสินค้าออนไลน์ — Proof of Delivery) =====
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliverySignToken" varchar(80) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliverySignTokenExpiresAt" timestamptz NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliverySignedAt" timestamptz NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliverySignedBy" varchar(200) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliverySignatureBase64" text NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_DeliverySignToken" ON "Documents" ("DeliverySignToken") WHERE "DeliverySignToken" IS NOT NULL;""",

            // ===== หักเงินมัดจำบนใบรับเงินสุดท้าย (display-only) =====
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositAppliedAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositAppliedRef" varchar(100) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositAppliedDrivesJournal" boolean NOT NULL DEFAULT false;""",
            // ส่วนลดท้ายบิล (จากยอดรวม) — เฉลี่ย pro-rata ลงบรรทัด
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BillDiscountPercent" numeric(9,4) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BillDiscountAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            // ใบเสร็จรับเงินหลักฐานคู่กับการชำระ (evidence-only, ไม่ลง JE ซ้ำ)
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsSettlementReceipt" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SettlementPaymentId" uuid NULL;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "ReceiptDocumentId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BuyerDeclinedTaxInvoice" boolean NOT NULL DEFAULT false;""",

            // ===== หัวเรื่องเอกสารตั้งเอง (ต่อประเภท + เงื่อนไข) =====
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "DocumentTitleOverridesJson" text NULL;""",
            // ภาษาเอกสารที่ออก (th/en) — ค่าตั้งต้นระดับบริษัท + override รายใบ
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "DocumentLanguage" varchar(5) NOT NULL DEFAULT 'th';""",
            // ===== ยืนยันสถานะจด VAT แล้วหรือยัง (รอบ 193 ฝ่ายค้าน C-9) =====
            // DEFAULT now() ตอนเพิ่มคอลัมน์ = เติมเวลาให้แถว**ที่มีอยู่แล้ว**ครั้งเดียว (ไม่ถามซ้ำบริษัทเดิม) · IF NOT EXISTS
            // ทำให้การเติมเกิดครั้งเดียว · แล้วถอด default ⇒ แถวใหม่ต้องได้ค่าจากคนยืนยันจริงเท่านั้น (สมัคร/SSO = null)
            """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "VatStatusConfirmedAt" timestamptz NULL DEFAULT now();""",
            """ALTER TABLE "CompanySettings" ALTER COLUMN "VatStatusConfirmedAt" DROP DEFAULT;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DocumentLanguage" varchar(5) NULL;""",

            // ===== ลูกค้าเงินสดไม่ประสงค์รับใบกำกับ (ผู้ซื้อกลางของใบกำกับขายปลีก) =====
            """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "IsWalkInCustomer" boolean NOT NULL DEFAULT false;""",

            // ===== ตำแหน่งป้าย ต้นฉบับ/สำเนา บน PDF (ลายน้ำกลางหน้า หรือป้ายมุมบน) =====
            """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "CopyLabelPosition" varchar(20) NOT NULL DEFAULT 'Watermark';""",
            // ค่าเริ่มต้นฝั่ง "ฟอร์มสร้างเอกสาร" ต่อชนิด — บ้านคือ template
            // (per-DocumentType + IsDefault อยู่แล้ว) ไม่สร้าง entity ใหม่ให้ตั้ง 2 ที่
            """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "DefaultPaymentAccountId" uuid NULL;""",
            """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "DefaultPaymentTerms" varchar(300) NULL;""",
            """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "DefaultCreditDays" integer NULL;""",
            // ซ่อนคอลัมน์ส่วนลดเมื่อไม่มีบรรทัดไหนมีส่วนลดเลย (default true) —
            // ใบส่วนใหญ่ไม่มีส่วนลด คอลัมน์ 0.00 ทั้งแถวกินความกว้างเปล่า ๆ
            """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "HideEmptyDiscountColumn" boolean NOT NULL DEFAULT true;""",
            // หัวกระดาษ 3 ส่วน (บริษัท/หัวเอกสาร/ลูกค้า) ซ้ำทุกหน้าของเอกสารหลายหน้า
            """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "RepeatHeaderEveryPage" boolean NOT NULL DEFAULT true;""",
            // §86/14 — เลขที่/วันที่ใบเสร็จ RD จากการนำส่ง ภ.พ.36 (= ใบกำกับภาษี
            // ของภาษีซื้อ self-assess) stamp ตอนกด "รับรู้ภาษีซื้อ"
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "Pp36RdReceiptNumber" varchar(50) NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "Pp36RdReceiptDate" timestamptz NULL;""",
            // Backfill: ใบสำคัญจ่าย ภ.พ.36 ที่ "รับรู้ภาษีซื้อ" ไปแล้วก่อนมีการแก้
            // ครั้งนี้ ติดค้างมองไม่เห็นใน ภ.พ.30/รายการดึงเอกสาร เพราะทั้งสองทาง
            // รับ PV เข้าฝั่งภาษีซื้อเฉพาะที่ HasTaxInvoiceReference=true ซึ่ง
            // recognition เดิมไม่เคยตั้ง (ใบเสร็จ RD = ใบกำกับ §86/14 → สิทธิ์
            // สมบูรณ์แล้ว). idempotent — รันซ้ำได้ ไม่แตะใบที่ตั้งไว้แล้ว
            """UPDATE "Documents" SET "HasTaxInvoiceReference" = true WHERE "IsForeignService" = true AND "DocumentType" = 13 AND "InputVatBecameClaimableAt" IS NOT NULL AND "HasTaxInvoiceReference" = false;""",
            // Backfill เลข/วันที่ใบเสร็จ RD ให้ใบที่รับรู้ก่อนมี field นี้ — ดึงจาก
            // เลขรับ (FilingNumber) ของการนำส่ง ภ.พ.36 งวดเดียวกัน (เดือนจ่าย)
            // idempotent: เฉพาะใบที่ยังไม่มีเลข + remittance มีเลขรับจริง
            """UPDATE "Documents" d SET "Pp36RdReceiptNumber" = r."FilingNumber", "Pp36RdReceiptDate" = COALESCE(d."Pp36RdReceiptDate", r."PayDate") FROM "StatutoryRemittances" r WHERE d."IsForeignService" = true AND d."InputVatBecameClaimableAt" IS NOT NULL AND d."Pp36RdReceiptNumber" IS NULL AND r."CompanyId" = d."CompanyId" AND r."RemittanceType" = 'VatPp36' AND r."IsDeleted" = false AND r."FilingNumber" IS NOT NULL AND r."FilingNumber" <> '' AND r."PeriodYear" = EXTRACT(YEAR FROM COALESCE(d."PaymentDate", d."DocumentDate"))::int AND r."PeriodMonth" = EXTRACT(MONTH FROM COALESCE(d."PaymentDate", d."DocumentDate"))::int;""",

            // ===== ความมั่นใจรายช่องของ OCR — เดิมไม่ได้เก็บลงฐานเลย =====
            // ค่าอยู่ในหน่วยความจำเฉพาะตอนสแกนสด พอ reload หน้าค่าหายหมด แล้ว
            // ป้าย % ข้างทุกช่องตกไปใช้ confidence ของทั้งใบ ⇒ ไฮไลต์เหลือง
            // "ตรวจสอบอีกครั้ง" (กฎเหล็ก #3 ข้อ 3) ใช้งานไม่ได้จริง
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "FieldConfidenceJson" text NULL;""",

            // ===== ปิด loop AI ของการจำแนก "เอกสารที่จะสร้าง" =====
            // เดิม loop ปิดจริงแค่ 3 ช่อง (ผังบัญชี/ผู้ติดต่อ/โครงการ) — การแก้
            // ชนิดเอกสารซึ่งเป็นคำถามที่พลาดแล้วแพงที่สุด ไม่เคยไปถึง student
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "TargetDocTypeAiFeedbackId" uuid NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LineSplitAiFeedbackId" uuid NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "LineSplitUsedAi" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "StockImportedAt" timestamp NULL;""",
            // หมายเหตุที่ผู้ใช้พิมพ์เอง (เหตุผลทางธุรกิจของรายจ่าย) — หน้าเบิก
            // บนมือถือมีช่องนี้มาตลอดแต่ไม่เคยส่งค่าไปไหน
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "UserNotes" text NULL;""",
            """ALTER TABLE "FixedAssets" ADD COLUMN IF NOT EXISTS "SourceScanResultId" uuid NULL;""",

            // ── กัน JE งานประจำเดือนซ้ำข้าม instance ──
            // `EclAllowanceJob` (สำรองหนี้สงสัยจะสูญ) กันซ้ำด้วย
            // `AnyAsync(Reference == "ECL-yyyyMM" && Posted)` แบบ read-then-write
            // ธรรมดา ไม่มี advisory lock ⇒ สอง instance ตื่นห่างกันเสี้ยววินาที
            // ใน 3 วันสุดท้ายของเดือน ทั้งคู่ได้ already=false ⇒ ตั้งสำรอง 2 ชุด
            // (Dr 57130 / Cr 18100 สองครั้ง) ⇒ กำไรต่ำกว่าจริง แล้วเดือนถัดไป
            // adjustment = required − currentAllowance กลับรายการก้อนโตผิดปกติ
            // unique index ทำให้ race เป็นไปไม่ได้เชิงโครงสร้าง (instance ที่สอง
            // ล้มที่ INSERT แล้ว job จับ exception ไว้อยู่แล้ว = ข้ามรอบไปเงียบ ๆ)
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntries_MonthlyJobRef"
            ON "JournalEntries" ("CompanyId", "Reference")
            WHERE "Reference" IS NOT NULL AND "Reference" LIKE 'ECL-%' AND "IsDeleted" = false;
            """,
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "TargetDocTypeAiSuggested" varchar(50) NULL;""",
            // เราเป็นผู้ซื้อ/ผู้ขาย — ถาม AI เฉพาะเคสที่ OcrPartyResolver ตัดสินไม่ได้ (รอบ 156)
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OurRoleAiFeedbackId" uuid NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OurRoleAiSuggested" varchar(20) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "OurRoleUsedAi" boolean NOT NULL DEFAULT false;""",
            // ── ล้างของเสียที่ตัวเรียนรู้จาก Azure สอนตัวเองไว้ (รอบ 156) ──
            // ตัวเรียนรู้เคยรัน **ก่อน** ขั้นแก้ไขทุกขั้น ⇒ (1) แพตเทิร์นเลขที่เอกสารถูกสร้างด้วย
            // regex กวาดทุกอย่าง `([A-Za-z0-9\-/]+)` ซึ่งบนสแกนรอบถัดไปคว้า token แรกที่เจอ
            // ("CASHSALE") มาเป็นเลขที่เอกสาร (2) ชื่อบริษัทเราเองถูกจำเป็น "ผู้ขายที่รู้ว่าถูก"
            // ของผู้ขายนิรนาม (VendorTaxId NULL) ⇒ ตัวซ่อมชื่อจะดึงชื่อเรากลับมาเป็นผู้ขายทุกใบ
            // แก้โค้ดอย่างเดียวไม่พอ — แถวเก่ายังสอนผิดต่อไป (บทเรียนเดิม OcrLearnedPatterns.ExtractionRegex)
            """DELETE FROM "OcrLearnedPatterns" WHERE "FieldName" = 'DocumentNumber' AND "ExtractionRegex" = '([A-Za-z0-9\-/]+)';""",
            // ลบตาม **ตัวตน** (เลขภาษีของผู้ขายที่จำไว้ = เลขของบริษัทเราเอง) ไม่ใช่ตามความคล้ายของชื่อ
            // — ทีมตรวจ 2026-09-11: กติกา "ชื่อมีคำของเราอยู่" จะลบผู้ขายจริงของ tenant ที่ชื่อสั้น
            // ("สยาม") ทุกรายซ้ำทุกครั้งที่บูต (ลิสต์นี้รันทุก startup) = ระบบไม่มีวันเรียนผู้ขายเหล่านั้น
            """DELETE FROM "VendorKnownGoodValues" v USING "Companies" c WHERE v."CompanyId" = c."Id" AND v."VendorTaxId" IS NOT NULL AND regexp_replace(v."VendorTaxId", '[^0-9]', '', 'g') = regexp_replace(c."TaxId", '[^0-9]', '', 'g');""",
            // ── ที่อยู่ผู้ขายที่ถูกตัดเลขบ้านทิ้งแล้ว "จำไว้ว่าถูก" (รอบ 190 · ใบ Wine Pro "12/861" → "/861") ──
            // OcrPartyAddress.StripLeakedNameFragment เคยตีเลข "12" ท้าย "Branch 00012" ว่าเป็นเศษชื่อ แล้ว
            // ตัดเลขบ้านทิ้ง · ตัวเรียนรู้ Azure จำค่านั้นเป็น known-good ⇒ แม้แก้โค้ดแล้ว ใบถัดไปที่อ่าน
            // "12/861 …" ถูก จะถูก VendorKnownGoodCorrector (similarity ≥ 0.80) ดึง "/861 …" กลับมาทับ ·
            // ลบตาม**รูปของค่า**ที่เป็นไปไม่ได้ (ที่อยู่ขึ้นต้นด้วย "/เลข" ไม่มีจริง) ไม่ใช่ตามความคล้าย
            // · ไม่แตะแถว UserCorrection (ผู้ใช้ยืนยันเอง = ตัดสินแล้ว)
            """DELETE FROM "VendorKnownGoodValues" WHERE "FieldName" = 'VendorAddress' AND "Source" <> 'UserCorrection' AND "Value" ~ '^[[:space:]]*/[0-9]';""",
            """DELETE FROM "OcrLearnedPatterns" p USING "Companies" c WHERE p."CompanyId" = c."Id" AND p."VendorTaxId" IS NOT NULL AND regexp_replace(p."VendorTaxId", '[^0-9]', '', 'g') = regexp_replace(c."TaxId", '[^0-9]', '', 'g');""",
            // แถวสแกนที่โดนอาการ "ย้ายแล้วไม่ sync": ผู้ขาย = ผู้ซื้อ (สตริงเดียวกันหลัง normalize)
            // ทั้งที่บทบาทคือผู้ซื้อ ⇒ ล้างช่องผู้ขายให้ตรงกับที่หน่วยความจำตัดสินไว้ — เทียบ**เท่ากัน**
            // ระหว่างสองช่องของแถวเดียวกัน ไม่ใช่เทียบกับชื่อบริษัท (ไม่มีทางลบข้อมูลของแถวอื่น)
            """UPDATE "OcrScanResults" s SET "ExtractedVendorName" = NULL, "ExtractedVendorTaxId" = NULL WHERE s."OurRole" = 'Buyer' AND s."ExtractedVendorName" IS NOT NULL AND s."BuyerName" IS NOT NULL AND regexp_replace(lower(s."ExtractedVendorName"), '(บริษัท|ห้างหุ้นส่วนจำกัด|หจก|บจก|บมจ|จำกัด|มหาชน|สำนักงานใหญ่|[[:space:].,()])', '', 'g') = regexp_replace(lower(s."BuyerName"), '(บริษัท|ห้างหุ้นส่วนจำกัด|หจก|บจก|บมจ|จำกัด|มหาชน|สำนักงานใหญ่|[[:space:].,()])', '', 'g') AND length(regexp_replace(lower(s."BuyerName"), '(บริษัท|ห้างหุ้นส่วนจำกัด|หจก|บจก|บมจ|จำกัด|มหาชน|สำนักงานใหญ่|[[:space:].,()])', '', 'g')) >= 4;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "TargetDocTypeUsedAi" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "SuggestedWhtRate" numeric(5,2) NULL;""",
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "WhtIncomeTypeCode" varchar(10) NULL;""",

            // ===== ล้าง regex ที่กลืนขึ้นบรรทัดใหม่ออกจากรูปแบบที่เรียนรู้ไว้แล้ว =====
            // OcrLearnedPatterns เก็บ "regex ที่ใช้ดึงค่า" ลงฐานข้อมูล ⇒ แถวที่
            // เรียนไว้ก่อนหน้ายังถือ pattern เดิมที่ตัวคั่นเป็น [-\s]? ซึ่ง \s ครอบ
            // \n ⇒ ต่อเลขท้ายบรรทัดกับเลขต้นบรรทัดถัดไปเป็นเลข 13 หลักที่ไม่มีบน
            // กระดาษ (บั๊กจริง PI-20260820-0005) — แก้โค้ดอย่างเดียวไม่พอ ต้องล้าง
            // ของที่ค้างในฐานด้วย. ตั้งเป็น NULL ให้ zone analyzer กลับไปใช้ทาง
            // กระดาษ (บั๊กจริง PI-20260820-0005) — แก้โค้ดอย่างเดียวไม่พอ ต้องล้าง
            // ของที่ค้างในฐานด้วย
            //
            // เทียบแบบ **เท่ากันเป๊ะ** ไม่ใช่ LIKE '%\s%' สองเหตุผล: (1) pattern
            // ของชื่อบริษัทก็มี \s อยู่ในตัว (`\s*\(มหาชน\)`) ซึ่งข้ามบรรทัดได้
            // ตามตั้งใจ จะไปทับผิดตัว (2) ใน LIKE ของ Postgres `\` เป็น escape
            // char โดยปริยาย ⇒ '%[-\s]?%' ถูกอ่านเป็น '%[-s]?%' แล้วไม่ match อะไรเลย
            // ค่านี้เป็นค่าเดียวที่ BuildExtractionRegex เคยออกให้เลข 13 หลัก
            // → เขียนทับด้วย pattern ที่แก้แล้ว (เก็บสิ่งที่เรียนรู้ไว้ ไม่ทิ้ง)
            """UPDATE "OcrLearnedPatterns" SET "ExtractionRegex" = '(?<!\d)(\d{1}[- \t]?\d{4}[- \t]?\d{5}[- \t]?\d{2}[- \t]?\d{1})(?!\d)' WHERE "ExtractionRegex" = '(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})';""",   // regex-line-span-ok — pattern เดิมอยู่ที่นี่ในฐานะ "ค่าที่จะถูกแทน" ไม่ใช่ regex ที่ใช้งาน

            // ===== Snapshot ยอดจริงจาก statement ล่าสุด (แสดงคู่ยอด GL ให้เห็นผลต่าง) =====
            """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "StatementBalance" numeric(18,2) NULL;""",
            """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "StatementBalanceDate" timestamptz NULL;""",
            """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "StatementImportedAt" timestamptz NULL;""",
            // รหัสผูก LINE ระดับพนักงาน (6 หลัก, หมดอายุ 24 ชม.)
            """
            CREATE TABLE IF NOT EXISTS "EmployeeLineBindCodes" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "Code" varchar(6) NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "UsedAt" timestamp with time zone NULL,
                "UsedByLineUserId" varchar(100) NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp with time zone NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EmployeeLineBindCodes_Code" ON "EmployeeLineBindCodes" ("Code") WHERE "UsedAt" IS NULL;""",
            // Token เข้าถึงสลิปแบบสาธารณะ (ปุ่มดาวน์โหลดใน LINE)
            """
            CREATE TABLE IF NOT EXISTS "PayslipShareTokens" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "PayrollRunId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "Token" varchar(120) NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "RevokedAt" timestamp with time zone NULL,
                "Channel" varchar(30) NOT NULL DEFAULT 'LINE',
                "AccessCount" integer NOT NULL DEFAULT 0,
                "LastAccessedAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp with time zone NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PayslipShareTokens_Token" ON "PayslipShareTokens" ("Token");""",

            // Breach incidents (ม.37(4)) — แจ้ง PDPC ภายใน 72 ชม.
            """
            CREATE TABLE IF NOT EXISTS "PdpaBreachIncidents" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "IncidentNumber" varchar(50) NOT NULL,
                "DetectedAt" timestamp NOT NULL DEFAULT now(),
                "NotifyPdpcDueBy" timestamp NOT NULL,
                "Severity" varchar(20) NOT NULL DEFAULT 'Medium',
                "Description" text NOT NULL,
                "AffectedDataCategories" varchar(1000) NOT NULL,
                "AffectedSubjectsCount" integer NULL,
                "Status" varchar(50) NOT NULL DEFAULT 'Detected',
                "PdpcNotifiedAt" timestamp NULL,
                "PdpcReferenceNumber" varchar(100) NULL,
                "SubjectsNotifiedAt" timestamp NULL,
                "Mitigation" text NULL,
                "RootCause" text NULL,
                "ReportedByUserId" uuid NULL,
                "ClosedAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PdpaBreachIncidents_Status" ON "PdpaBreachIncidents" ("CompanyId", "Status", "NotifyPdpcDueBy");""",

            // ===== AuditLogs: DB-level immutability (tamper-evident defense-in-depth) =====
            // app layer ตัด AuditLog ออกจาก ChangeTracker อยู่แล้ว (append-only)
            // แต่ DBA/SQL ตรง ๆ ยังลบได้ → เพิ่ม trigger บล็อค DELETE ที่ระดับ DB
            // เพื่อให้ hash-chain ตรวจสอบความถูกต้องได้จริง (PDPA ม.37 + พ.ร.บ.บัญชี).
            // บล็อคเฉพาะ DELETE (UPDATE เผื่อ migration backfill hash ในอนาคต).
            """
            CREATE OR REPLACE FUNCTION block_auditlog_delete() RETURNS TRIGGER AS $func$
            BEGIN RAISE EXCEPTION 'AuditLogs are append-only (tamper-evident) — DELETE blocked'; END;
            $func$ LANGUAGE plpgsql;
            """,
            """
            DROP TRIGGER IF EXISTS audit_log_no_delete ON "AuditLogs";
            """,
            """
            CREATE TRIGGER audit_log_no_delete BEFORE DELETE ON "AuditLogs"
                FOR EACH ROW EXECUTE FUNCTION block_auditlog_delete();
            """,

            // ===== Employees: PDPA ม.26 encrypt bank/SSN — widen cols for ciphertext =====
            // ciphertext = Base64(nonce+ct+tag) ~80 chars สำหรับ input สั้น ๆ →
            // ขยายเป็น 200. Legacy plaintext คงอยู่ + re-save migrate เป็น ciphertext.
            """ALTER TABLE "Employees" ALTER COLUMN "BankAccountNumber" TYPE varchar(200);""",
            """ALTER TABLE "Employees" ALTER COLUMN "SocialSecurityNumber" TYPE varchar(200);""",

            // ===== PayrollRuns: ประกันสังคมรอนำส่ง + กองทุนเงินทดแทน =====
            // SsoSettled* fields ติดตามว่าได้นำส่งให้ สปส. แล้วหรือยัง (กฎหมาย
            // วันที่ 15 ของเดือนถัดไป). เพิ่มเงินทดแทนรวมเพื่อทำ กท.20ก รายปี.
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoSettledAt" timestamp NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoSettlementJournalEntryId" uuid NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoSettlementDocumentId" uuid NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoFilingNumber" varchar(100) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SsoLateFeeAmount" decimal(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "TotalWorkersCompensation" decimal(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "WorkersCompensation" decimal(18,2) NOT NULL DEFAULT 0;""",

            // ===== PayrollRuns: external import (TakeTime ส่งยอดสำเร็จรูป) =====
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(100) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "ExternalRunRef" varchar(200) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "IsExternalImport" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "SalaryExpenseAccountCode" varchar(50) NULL;""",
            """ALTER TABLE "PayrollRuns" ADD COLUMN IF NOT EXISTS "NetPaymentAccountCode" varchar(50) NULL;""",
            // แหล่งจ่ายเงินสุทธิรายคน (split Cr เงินสด/ธนาคารตอน Pay) — null=ใช้ค่าระดับ run
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "NetPaymentAccountCode" varchar(50) NULL;""",
            // idempotency — unique partial index บน (CompanyId, ExternalRunRef)
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_PayrollRuns_ExternalRunRef" ON "PayrollRuns" ("CompanyId", "ExternalRunRef") WHERE "ExternalRunRef" IS NOT NULL;""",

            // ===== StatutoryRemittances: นำส่งภาษี/ประกันสังคมรวม (สปส.1-10/ภงด.1/3/53/ภพ.30) =====
            """
            CREATE TABLE IF NOT EXISTS "StatutoryRemittances" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "RemittanceType" varchar(40) NOT NULL,
                "PeriodYear" integer NOT NULL,
                "PeriodMonth" integer NOT NULL,
                "Amount" decimal(18,2) NOT NULL DEFAULT 0,
                "LateFee" decimal(18,2) NOT NULL DEFAULT 0,
                "PayDate" timestamp NOT NULL DEFAULT now(),
                "BankGlAccountId" uuid NULL,
                "JournalEntryId" uuid NULL,
                "DocumentId" uuid NULL,
                "FilingNumber" varchar(100) NULL,
                "ReceiptAttachmentId" uuid NULL,
                "Note" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            // นำส่ง 1 ครั้ง/ประเภท/งวด (กันจ่ายซ้ำ) — เฉพาะ row ที่ยังไม่ลบ
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_StatutoryRemittances_Period" ON "StatutoryRemittances" ("CompanyId", "RemittanceType", "PeriodYear", "PeriodMonth") WHERE "IsDeleted" = false;""",

            """
            CREATE TABLE IF NOT EXISTS "EmailQueues" (
                "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "RuleId" uuid NULL,
                "EntityType" varchar(40) NOT NULL,
                "EntityId" uuid NOT NULL,
                "ToEmail" varchar(500) NOT NULL,
                "CcEmail" varchar(500) NULL,
                "BccEmail" varchar(500) NULL,
                "Subject" text NOT NULL,
                "Body" text NOT NULL,
                "AttachPdf" boolean NOT NULL DEFAULT true,
                "AttachXml" boolean NOT NULL DEFAULT false,
                "ScheduledFor" timestamp NOT NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "RetryCount" integer NOT NULL DEFAULT 0,
                "SentAt" timestamp NULL,
                "ErrorMessage" text NULL,
                "IdempotencyKey" varchar(200) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "PK_EmailQueues" PRIMARY KEY ("Id")
            );
            """,
            // Worker scan index: Pending + due soon.
            """CREATE INDEX IF NOT EXISTS "IX_EmailQueue_Status_Schedule" ON "EmailQueues" ("Status", "ScheduledFor") WHERE "IsDeleted" = false;""",
            // Idempotency — กัน enqueue ซ้ำสำหรับ event เดียวกัน.
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmailQueue_Idempotency" ON "EmailQueues" ("CompanyId", "IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL AND "IsDeleted" = false;""",

            // PaymentAllocation — one Payment may settle many Documents.
            // Existing Payment rows stay valid (legacy 1:1 path); new
            // multi-doc payments insert one row per target document.
            """
            CREATE TABLE IF NOT EXISTS "PaymentAllocations" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "PaymentId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "AllocatedAmount" decimal(18,2) NOT NULL,
                "WithholdingTaxAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Note" text NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PaymentAllocations_Payment" ON "PaymentAllocations" ("PaymentId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_PaymentAllocations_Document" ON "PaymentAllocations" ("CompanyId", "DocumentId") WHERE "IsDeleted" = false;""",

            // Inbound cheques need an explicit deposit-bank link so
            // MarkCleared can update bank balance — outbound uses
            // ChequeBook.BankAccount, inbound has no chequeBook.
            """ALTER TABLE "Cheques" ADD COLUMN IF NOT EXISTS "DepositBankAccountId" uuid NULL;""",

            // Orphan NOT NULL columns on CompanySettings — added by past
            // migrations without DEFAULT, never carried into the C# entity.
            // Every GetOrCreateSettingsAsync on a brand-new company exploded
            // because EF's INSERT omits unknown columns; Postgres rejects
            // the row on the NOT NULL constraint. Specific casualties:
            // AllowFreelanceAccess, MaxFreelanceUsers, possibly others.
            //
            // Rather than patch each name individually (we keep finding more),
            // this DO block walks information_schema and SETs a type-
            // appropriate DEFAULT on every NOT NULL column that lacks one,
            // then backfills NULLs (defensive — should be none since the
            // constraint already blocks them, but safe). Idempotent.
            """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN
                    SELECT column_name, data_type, udt_name
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'CompanySettings'
                      AND is_nullable = 'NO'
                      AND column_default IS NULL
                LOOP
                    IF r.data_type IN ('boolean') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT false', r.column_name);
                    ELSIF r.data_type IN ('integer','bigint','smallint','numeric','real','double precision') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT 0', r.column_name);
                    ELSIF r.data_type IN ('character varying','varchar','text','character','char') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT ''''', r.column_name);
                    ELSIF r.data_type IN ('timestamp without time zone','timestamp with time zone','timestamptz') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT now()', r.column_name);
                    ELSIF r.data_type IN ('date') THEN
                        EXECUTE format('ALTER TABLE "CompanySettings" ALTER COLUMN %I SET DEFAULT CURRENT_DATE', r.column_name);
                    ELSIF r.data_type IN ('uuid') THEN
                        -- Don't auto-default uuids; CompanyId etc. should always be supplied.
                        NULL;
                    END IF;
                END LOOP;
            END $$;
            """,

            // Employee EmployeeCode uniqueness is now scoped to active
            // (non-soft-deleted) rows so partners can recreate / restore
            // an employee code after deletion. Drop the legacy unique
            // constraint first, then create the partial unique index.
            """ALTER TABLE "Employees" DROP CONSTRAINT IF EXISTS "AK_Employees_CompanyId_EmployeeCode";""",
            """DROP INDEX IF EXISTS "IX_Employees_CompanyId_EmployeeCode";""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Employees_CompanyId_EmployeeCode_Active" ON "Employees" ("CompanyId", "EmployeeCode") WHERE "IsDeleted" = false;""",

            // HR: employee external-sync + cost-behavior fields. ExternalId/
            // ExternalSystem let attendance / HRIS push or pull rows without
            // name-matching. CostBehavior drives the Fixed-vs-Variable cost
            // report — defaulted to Fixed (monthly salaried is the common
            // case; daily/hourly should be flipped via UpdateEmployee).
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ExternalId" varchar(200) NULL;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "ExternalSystem" varchar(50) NULL;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "LastSyncedAt" timestamp NULL;""",
            """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "CostBehavior" varchar(20) NOT NULL DEFAULT 'Fixed';""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Employees_ExternalSync" ON "Employees" ("CompanyId", "ExternalSystem", "ExternalId") WHERE "ExternalId" IS NOT NULL AND "IsDeleted" = false;""",

            // ProjectCostEntry cost behavior — Fixed (rent, salaried) vs
            // Variable (hourly labor, materials) — drives the cost report.
            """ALTER TABLE "ProjectCostEntries" ADD COLUMN IF NOT EXISTS "CostBehavior" varchar(20) NOT NULL DEFAULT 'Variable';""",

            // ChartOfAccount cost behavior — nullable; lets the report
            // classify non-project GL costs (rent, utilities, depreciation)
            // that bypass ProjectCostEntry. Seeded NULL; admins tag the
            // relevant expense accounts via the COA UI.
            """ALTER TABLE "ChartOfAccounts" ADD COLUMN IF NOT EXISTS "CostBehavior" varchar(20) NULL;""",

            // Employee project time allocation — feeds payroll → ProjectCostEntry
            // labor allocation. Sync-friendly (external attendance systems).
            """
            CREATE TABLE IF NOT EXISTS "EmployeeProjectTimes" (
                "Id" uuid PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "EmployeeId" uuid NOT NULL,
                "ProjectId" uuid NULL,
                "ProjectTaskId" uuid NULL,
                "WorkDate" timestamp NOT NULL,
                "Hours" decimal(8,2) NOT NULL DEFAULT 0,
                "Description" text NULL,
                "Category" varchar(50) NOT NULL DEFAULT 'Billable',
                "IsAllocated" boolean NOT NULL DEFAULT false,
                "AllocatedPayrollRunId" uuid NULL,
                "ProjectCostEntryId" uuid NULL,
                "ExternalId" varchar(200) NULL,
                "ExternalSystem" varchar(50) NULL,
                "LastSyncedAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "UpdatedAt" timestamp NULL,
                "CreatedBy" varchar(100) NULL,
                "UpdatedBy" varchar(100) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EmployeeProjectTimes_Employee_Date" ON "EmployeeProjectTimes" ("CompanyId", "EmployeeId", "WorkDate") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_EmployeeProjectTimes_Project_Date" ON "EmployeeProjectTimes" ("CompanyId", "ProjectId", "WorkDate") WHERE "ProjectId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_EmployeeProjectTimes_ExternalSync" ON "EmployeeProjectTimes" ("CompanyId", "ExternalSystem", "ExternalId") WHERE "ExternalId" IS NOT NULL AND "IsDeleted" = false;""",

            // ===== DocumentLines: ภาษีซื้อต้องห้าม (Non-claimable Input VAT) =====
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "IsVatClaimable" boolean NOT NULL DEFAULT true;""",
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "VatNonClaimableReason" text NULL;""",
            // GL-account AI feedback id — ปิดลูปการสอน local model ตามกฎเหล็ก #1
            // (ตอน user แก้/ยืนยัน AccountId, ระบบเรียก RecordUserChoiceAsync)
            """ALTER TABLE "DocumentLines" ADD COLUMN IF NOT EXISTS "GlAccountAiFeedbackId" uuid NULL;""",
            // Tax Point §78/§78/1 + Retention §87/3 + §65 ตรี add-back + LateReason §82/3
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DeliveryDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OwnershipTransferDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ServiceUsedDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "TaxPointDate" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RetentionUntil" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "NonDeductibleAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "NonDeductibleRuleJson" text NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "LateReason" text NULL;""",
            // Deposit lifecycle: refund (ยกเลิกการจอง) + offset เข้าใบสุดท้าย
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRefundedAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRefundedAt" timestamp with time zone NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositRefundReason" text NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "DepositAppliedToDocumentId" uuid NULL;""",
            // Booking number — ผูกเอกสารหลายใบ (มัดจำ→ใบสุดท้าย→ใบเสร็จ) เข้า booking เดียว
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "BookingNumber" varchar(50) NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_Documents_BookingNumber" ON "Documents" ("CompanyId", "BookingNumber") WHERE "BookingNumber" IS NOT NULL;""",

            // ===== TaxRuleConfig: configurable PIT brackets + allowances ต่อปี =====
            // Per company × per year. Engine fallback ถ้าไม่มี config → ใช้
            // hard-coded constants (PayrollService.Pit*) เพื่อ backward compat
            // กับบริษัทที่ยังไม่เคยตั้งค่า.
            """
            CREATE TABLE IF NOT EXISTS "TaxRuleConfigs" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "FiscalYear" integer NOT NULL,
                "BracketsJson" text NOT NULL DEFAULT '',
                "PersonalAllowance" numeric(18,2) NOT NULL DEFAULT 60000,
                "SpouseAllowance" numeric(18,2) NOT NULL DEFAULT 60000,
                "ChildAllowance" numeric(18,2) NOT NULL DEFAULT 30000,
                "ChildAllowancePost2561" numeric(18,2) NOT NULL DEFAULT 60000,
                "ParentAllowance" numeric(18,2) NOT NULL DEFAULT 30000,
                "Section42TwiCap" numeric(18,2) NOT NULL DEFAULT 100000,
                "LifeInsuranceCap" numeric(18,2) NOT NULL DEFAULT 100000,
                "HealthInsuranceCap" numeric(18,2) NOT NULL DEFAULT 25000,
                "PvdCap" numeric(18,2) NOT NULL DEFAULT 500000,
                "MortgageInterestCap" numeric(18,2) NOT NULL DEFAULT 100000,
                "DonationCapPercent" numeric(8,4) NOT NULL DEFAULT 10,
                "Notes" text NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0,
                CONSTRAINT "UX_TaxRuleConfigs_CompanyYear" UNIQUE ("CompanyId", "FiscalYear")
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_TaxRuleConfigs_CompanyId" ON "TaxRuleConfigs" ("CompanyId") WHERE "IsDeleted" = false;""",

            // ===== WithholdingTaxCerts: link to source PayrollRun สำหรับ
            // monthly auto-issue + idempotency check (re-post payroll = re-issue cert).
            """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "SourcePayrollRunId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_WithholdingTaxCerts_SourcePayrollRunId" ON "WithholdingTaxCerts" ("SourcePayrollRunId") WHERE "SourcePayrollRunId" IS NOT NULL;""",
            // 50 ทวิ ต่อ "งวดการจ่าย" (ภ.ง.ด.3/53 = cash basis) — เอกสารที่ทยอยจ่าย
            // มีใบละงวด ยอดตามที่หักจริงของงวดนั้น; ใช้เป็น idempotency key
            """ALTER TABLE "WithholdingTaxCerts" ADD COLUMN IF NOT EXISTS "SourcePaymentId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_WithholdingTaxCerts_SourcePaymentId" ON "WithholdingTaxCerts" ("SourcePaymentId") WHERE "SourcePaymentId" IS NOT NULL;""",

            // ===== PayrollDetails: TaxableGross — รายได้ที่ใช้คำนวณ WHT
            """ALTER TABLE "PayrollDetails" ADD COLUMN IF NOT EXISTS "TaxableGross" numeric(18,2) NOT NULL DEFAULT 0;""",

            // ===== Companies.PaidUpCapital — ทุนชำระแล้ว สำหรับ §65 ทวิ (4)
            // entertainment cap 0.3% revenue/capital max 10M (F11).
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "PaidUpCapital" numeric(18,2) NOT NULL DEFAULT 0;""",

            // ===== ChartOfAccounts.CashFlowSection — per-account override
            // ของหมวด Cash Flow (Operating / Investing / Financing).
            """ALTER TABLE "ChartOfAccounts" ADD COLUMN IF NOT EXISTS "CashFlowSection" smallint NOT NULL DEFAULT 0;""",

            // ===== AuditLogs: F14 hash chain (forensic tamper-evident)
            """ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "PrevHash" text NULL;""",
            """ALTER TABLE "AuditLogs" ADD COLUMN IF NOT EXISTS "RowHash" text NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_AuditLogs_Company_Id" ON "AuditLogs" ("CompanyId", "Id");""",

            // ===== Advanced Thai SME entities =====
            // PostDatedCheck (เช็คล่วงหน้า) — B2B Thai norm. Inbound / Outbound
            // + lifecycle Held → Deposited → Cleared / Dishonored / Returned.
            """
            CREATE TABLE IF NOT EXISTS "PostDatedChecks" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "Direction" smallint NOT NULL,
                "CheckNumber" text NOT NULL DEFAULT '',
                "BankName" text NOT NULL DEFAULT '',
                "BankBranch" text NULL,
                "Amount" numeric(18,2) NOT NULL DEFAULT 0,
                "CheckDate" timestamp with time zone NOT NULL,
                "IssueDate" timestamp with time zone NOT NULL,
                "ScheduledDepositDate" timestamp with time zone NOT NULL,
                "DepositedAt" timestamp with time zone NULL,
                "ClearedAt" timestamp with time zone NULL,
                "DishonoredAt" timestamp with time zone NULL,
                "DishonorReason" text NULL,
                "Status" smallint NOT NULL DEFAULT 1,
                "ContactId" uuid NULL,
                "SourceDocumentId" uuid NULL,
                "BankAccountId" uuid NULL,
                "RelatedVoucherId" uuid NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_PostDatedChecks_Company_Status" ON "PostDatedChecks" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_PostDatedChecks_ScheduledDeposit" ON "PostDatedChecks" ("CompanyId", "ScheduledDepositDate") WHERE "IsDeleted" = false AND "Status" IN (1,2);""",
            """CREATE INDEX IF NOT EXISTS "IX_PostDatedChecks_Source" ON "PostDatedChecks" ("SourceDocumentId") WHERE "SourceDocumentId" IS NOT NULL;""",

            // CashAdvanceRequest (เบิก-เคลียร์เงินสดล่วงหน้า)
            """
            CREATE TABLE IF NOT EXISTS "CashAdvanceRequests" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "RequestNumber" text NOT NULL DEFAULT '',
                "EmployeeId" uuid NOT NULL,
                "RequestedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ApprovedAmount" numeric(18,2) NULL,
                "DisbursedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ClearedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "RefundAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "RequestDate" timestamp with time zone NOT NULL,
                "ApprovedAt" timestamp with time zone NULL,
                "DisbursedAt" timestamp with time zone NULL,
                "ClearanceDueDate" timestamp with time zone NULL,
                "ClearedAt" timestamp with time zone NULL,
                "Status" smallint NOT NULL DEFAULT 1,
                "Purpose" text NOT NULL DEFAULT '',
                "ApproverNote" text NULL,
                "RejectionReason" text NULL,
                "ApproverUserId" uuid NULL,
                "DisbursementVoucherId" uuid NULL,
                "ClearanceDocumentIdsJson" text NOT NULL DEFAULT '[]',
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_CashAdvanceRequests_Company_Status" ON "CashAdvanceRequests" ("CompanyId", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_CashAdvanceRequests_Employee" ON "CashAdvanceRequests" ("EmployeeId");""",
            """CREATE INDEX IF NOT EXISTS "IX_CashAdvanceRequests_ClearanceDue" ON "CashAdvanceRequests" ("CompanyId", "ClearanceDueDate") WHERE "Status" IN (3,4);""",

            // EarlyPaymentDiscountTerm (ส่วนลดเงินสด 2/10 net 30)
            """
            CREATE TABLE IF NOT EXISTS "EarlyPaymentDiscountTerms" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "Code" text NOT NULL DEFAULT '',
                "DisplayName" text NOT NULL DEFAULT '',
                "DiscountWindowDays" integer NOT NULL DEFAULT 0,
                "DiscountPercent" numeric(8,4) NOT NULL DEFAULT 0,
                "NetTermDays" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_EarlyPaymentDiscountTerms_Company" ON "EarlyPaymentDiscountTerms" ("CompanyId") WHERE "IsDeleted" = false;""",

            // ===== Document: EarlyPaymentDiscountTermId — link เอกสารกับ
            // discount term ที่ใช้ (auto-apply ตอน receipt มาถึงในช่วงเวลา).
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "EarlyPaymentDiscountTermId" uuid NULL;""",

            // ===== Document: IsForeignService — ภ.พ.36 self-assess VAT flag
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "IsForeignService" boolean NOT NULL DEFAULT false;""",

            // ===== StockMovement: warehouse + lot/serial tracking
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "WarehouseId" uuid NULL;""",
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "LotNumber" text NULL;""",
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "SerialNumber" text NULL;""",
            """ALTER TABLE "StockMovements" ADD COLUMN IF NOT EXISTS "TransferPairId" uuid NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_StockMovements_WhProduct" ON "StockMovements" ("WarehouseId", "ProductId") WHERE "WarehouseId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_StockMovements_Lot" ON "StockMovements" ("LotNumber") WHERE "LotNumber" IS NOT NULL;""",
            // NOTE: StockTransfers + StockTransferLines tables มีอยู่แล้ว
            // (สร้างจาก AdvancedOperations migration เดิม) — ไม่สร้างซ้ำ.

            // ===== DocumentComments — collaboration on documents (#23)
            """
            CREATE TABLE IF NOT EXISTS "DocumentComments" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "EntityType" text NOT NULL DEFAULT 'Document',
                "EntityId" uuid NOT NULL,
                "AuthorUserId" uuid NOT NULL,
                "Body" text NOT NULL DEFAULT '',
                "MentionedUserIdsJson" text NOT NULL DEFAULT '[]',
                "ParentCommentId" uuid NULL,
                "EditedAt" timestamp with time zone NULL,
                "AttachmentUrl" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_DocumentComments_Entity" ON "DocumentComments" ("CompanyId", "EntityType", "EntityId") WHERE "IsDeleted" = false;""",

            // ===== ScheduledReports — email digest schedule (#15)
            """
            CREATE TABLE IF NOT EXISTS "ScheduledReports" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "Name" text NOT NULL DEFAULT '',
                "ReportCode" text NOT NULL DEFAULT '',
                "Frequency" text NOT NULL DEFAULT 'Weekly',
                "DayOfWeek" integer NULL,
                "DayOfMonth" integer NULL,
                "HourBangkok" integer NOT NULL DEFAULT 8,
                "RecipientsJson" text NOT NULL DEFAULT '[]',
                "IsActive" boolean NOT NULL DEFAULT true,
                "LastSentAt" timestamp with time zone NULL,
                "LastResult" text NULL,
                "Format" text NOT NULL DEFAULT 'PDF',
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ScheduledReports_Company" ON "ScheduledReports" ("CompanyId") WHERE "IsDeleted" = false AND "IsActive" = true;""",

            // ===== ImportConflicts — duplicate detection during import flows
            """
            CREATE TABLE IF NOT EXISTS "ImportConflicts" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "SessionId" uuid NULL,
                "SessionRef" text NULL,
                "RowNumber" integer NOT NULL DEFAULT 0,
                "EntityType" text NOT NULL DEFAULT '',
                "StagedDataJson" text NOT NULL DEFAULT '{}',
                "ExistingEntityId" uuid NULL,
                "ExistingDataJson" text NOT NULL DEFAULT '{}',
                "MatchScore" double precision NOT NULL DEFAULT 0,
                "MatchReason" text NOT NULL DEFAULT '',
                "Resolution" integer NOT NULL DEFAULT 0,
                "ResolvedAt" timestamp with time zone NULL,
                "ResolvedBy" text NULL,
                "MergeChoicesJson" text NULL,
                "UserNote" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ImportConflicts_Session" ON "ImportConflicts" ("SessionId") WHERE "SessionId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ImportConflicts_SessionRef" ON "ImportConflicts" ("CompanyId", "SessionRef") WHERE "SessionRef" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ImportConflicts_Pending" ON "ImportConflicts" ("CompanyId", "Resolution") WHERE "Resolution" = 0 AND "IsDeleted" = false;""",

            // ===== ProductLot — FIFO/FEFO tracking
            """
            CREATE TABLE IF NOT EXISTS "ProductLots" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CompanyId" uuid NOT NULL,
                "ProductId" uuid NOT NULL,
                "LotNumber" text NOT NULL DEFAULT '',
                "ManufactureDate" timestamp with time zone NULL,
                "ExpirationDate" timestamp with time zone NULL,
                "QuantityOnHand" numeric(18,4) NOT NULL DEFAULT 0,
                "UnitCost" numeric(18,4) NOT NULL DEFAULT 0,
                "WarehouseId" uuid NULL,
                "SupplierBatchRef" text NULL,
                "Notes" text NULL,
                "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
                "UpdatedAt" timestamp with time zone NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                "Version" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ProductLots_Product" ON "ProductLots" ("CompanyId", "ProductId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ProductLots_Expiry" ON "ProductLots" ("CompanyId", "ExpirationDate") WHERE "ExpirationDate" IS NOT NULL AND "QuantityOnHand" > 0;""",

            // ===== Document revision (Rev.) — เลขแก้ไข + snapshot ประวัติ =====
            // เดิมชื่อ QuotationRevision (รอบแรกรองรับเฉพาะใบเสนอราคา) — ตอนนี้ใช้
            // กับเอกสาร operational ทุกชนิด จึง rename ให้ตรงความหมาย. guard ด้วย
            // information_schema: rename เฉพาะตอนที่คอลัมน์เก่ามีและใหม่ยังไม่มี
            // (idempotent — รันซ้ำได้ทั้ง DB ที่เคยรันรอบเก่าและ DB ใหม่)
            """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'Documents' AND column_name = 'QuotationRevision')
                   AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'Documents' AND column_name = 'RevisionNumber')
                THEN
                    ALTER TABLE "Documents" RENAME COLUMN "QuotationRevision" TO "RevisionNumber";
                END IF;
            END $$;
            """,
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RevisionNumber" integer NOT NULL DEFAULT 0;""",
            // ใบลดหนี้/ใบเพิ่มหนี้: บังคับฝั่งด้วยมือ (true=ซื้อ, false=ขาย, NULL=ให้ระบบ
            // ตัดสินเอง) — ใช้ตอนผู้ใช้กด "ย้ายฝั่ง" เพราะใบถูกจัดฝั่งผิดตั้งแต่อนุมัติ
            // แล้ว JE ลงผิดฝั่งถาวร ทำให้ยอดไปโผล่ผิดฝั่งใน ภ.พ.30
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CnDnPurchaseSideOverride" boolean NULL;""",
            // ── ทะเบียนหนังสือรับรองหัก ณ ที่จ่ายที่ "เราได้รับ" (เครดิต ภ.ง.ด.50/51) ──
            // แยกตารางจาก WithholdingTaxCerts (ใบที่เราออกให้ผู้อื่น) เพราะเป็นเอกสาร
            // คนละชนิดทางกฎหมาย — ดู WHT_CREDIT_PLAN.md
            """
            CREATE TABLE IF NOT EXISTS "WhtCreditsReceived" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "TaxYear" integer NOT NULL DEFAULT 0,
                "CertificateNumber" varchar(50) NULL,
                "CertificateDate" timestamptz NULL,
                "PayerContactId" uuid NULL,
                "PayerName" varchar(300) NOT NULL DEFAULT '',
                "PayerTaxId" varchar(20) NULL,
                "PayerFormType" integer NOT NULL DEFAULT 53,
                "IncomeTypeCode" varchar(20) NULL,
                "IncomeAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "WhtRate" decimal(9,4) NOT NULL DEFAULT 0,
                "WhtAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Status" integer NOT NULL DEFAULT 0,
                "DocumentId" uuid NULL,
                "PaymentId" uuid NULL,
                "AttachmentId" uuid NULL,
                "ClaimedInTaxReportId" uuid NULL,
                "ClaimedAt" timestamptz NULL,
                "CarriedFromTaxYear" integer NULL,
                "Notes" text NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                "UpdatedAt" timestamptz NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_WhtCreditsReceived_Year" ON "WhtCreditsReceived" ("CompanyId", "TaxYear", "Status") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_WhtCreditsReceived_Doc" ON "WhtCreditsReceived" ("CompanyId", "DocumentId") WHERE "DocumentId" IS NOT NULL AND "IsDeleted" = false;""",
            // กันสร้างซ้ำจากเอกสารเดียวกัน (approve ซ้ำ/regenerate)
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_WhtCreditsReceived_Doc" ON "WhtCreditsReceived" ("CompanyId", "DocumentId") WHERE "DocumentId" IS NOT NULL AND "IsDeleted" = false;""",
            """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_WhtCreditsReceived_Companies') THEN
                    ALTER TABLE "WhtCreditsReceived"
                        ADD CONSTRAINT "FK_WhtCreditsReceived_Companies"
                        FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_WhtCreditsReceived_Documents') THEN
                    ALTER TABLE "WhtCreditsReceived"
                        ADD CONSTRAINT "FK_WhtCreditsReceived_Documents"
                        FOREIGN KEY ("DocumentId") REFERENCES "Documents"("Id") ON DELETE SET NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_WhtCreditsReceived_Contacts') THEN
                    ALTER TABLE "WhtCreditsReceived"
                        ADD CONSTRAINT "FK_WhtCreditsReceived_Contacts"
                        FOREIGN KEY ("PayerContactId") REFERENCES "Contacts"("Id") ON DELETE SET NULL;
                END IF;
            END $$;
            """,
            // รอบ 193 S2 (P7): ไฟล์ 50 ทวิ ที่แนบก่อนบันทึกค้างอยู่ในถังเจ้าของว่าง (WhtCredit/0000…) ทั้งที่รายการชี้มาแล้ว
            // ⇒ ย้ายไปผูกกับรายการที่ชี้ถึง (ตัวเดียวกับที่ WhtCreditService.AdoptUnsavedAttachmentAsync ทำตอนบันทึก) · idempotent
            """
            UPDATE "FileAttachments" f
               SET "EntityId" = w."Id", "UpdatedAt" = now()
              FROM "WhtCreditsReceived" w
             WHERE w."AttachmentId" = f."Id"
               AND w."CompanyId" = f."CompanyId"
               AND f."EntityType" = 'WhtCredit'
               AND f."EntityId" = '00000000-0000-0000-0000-000000000000';
            """,
            """
            CREATE TABLE IF NOT EXISTS "DocumentRevisions" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "DocumentId" uuid NOT NULL,
                "RevisionNumber" integer NOT NULL DEFAULT 0,
                "SnapshotJson" text NOT NULL DEFAULT '',
                "TotalAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "Reason" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_DocumentRevisions_Doc" ON "DocumentRevisions" ("DocumentId", "RevisionNumber");""",

            // ===== BillingAccount — ชั้นผู้จ่ายเงินเหนือ Company (ACCOUNT_STRUCTURE.md §3.2) =====
            // Additive ล้วน: ทุกคอลัมน์ที่เพิ่มบน Companies/AccountSubscriptions เป็น
            // nullable หรือมี default ตรงกับพฤติกรรมเดิม → ระบบที่รันอยู่ไม่เปลี่ยนอะไรเลย
            """
            CREATE TABLE IF NOT EXISTS "BillingAccounts" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "Name" varchar(200) NOT NULL DEFAULT '',
                "TaxId" varchar(13) NULL,
                "BillingAddress" text NULL,
                "BillingBranchCode" varchar(5) NULL DEFAULT '00000',
                "BillingEmail" varchar(200) NULL,
                "ContactPhone" varchar(50) NULL,
                "BillingMode" integer NOT NULL DEFAULT 1,
                "PaymentModel" integer NOT NULL DEFAULT 1,
                "CreditBalance" decimal(18,2) NOT NULL DEFAULT 0,
                "PostpaidCreditLimit" decimal(18,2) NOT NULL DEFAULT 0,
                "GracePeriodDays" integer NOT NULL DEFAULT 7,
                "IsSandbox" boolean NOT NULL DEFAULT false,
                "Status" integer NOT NULL DEFAULT 1,
                "SuspendReason" varchar(500) NULL,
                "SuspendedAt" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_BillingAccounts_TaxId" ON "BillingAccounts" ("TaxId") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "BillingAccountAdmins" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "BillingAccountId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "IsPrimary" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false,
                CONSTRAINT "FK_BillingAccountAdmins_Account" FOREIGN KEY ("BillingAccountId")
                    REFERENCES "BillingAccounts"("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_BillingAccountAdmins_User" FOREIGN KEY ("UserId")
                    REFERENCES "Users"("Id") ON DELETE CASCADE
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BillingAccountAdmins_Account_User" ON "BillingAccountAdmins" ("BillingAccountId", "UserId") WHERE "IsDeleted" = false;""",

            // FK บน Companies — nullable ทั้งคู่ บริษัทเดิมที่ไม่ได้อยู่กลุ่มไหนไม่กระทบ
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "BillingAccountId" uuid NULL;""",
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "ParentCompanyId" uuid NULL;""",
            // CompanyKind default 1 = Full = พฤติกรรมเดิมของทุกบริษัทที่มีอยู่
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "CompanyKind" integer NOT NULL DEFAULT 1;""",
            """CREATE INDEX IF NOT EXISTS "IX_Companies_BillingAccount" ON "Companies" ("BillingAccountId") WHERE "IsDeleted" = false;""",
            """ALTER TABLE "AccountSubscriptions" ADD COLUMN IF NOT EXISTS "BillingAccountId" uuid NULL;""",

            // FK แยกจาก ADD COLUMN + guard ด้วย pg_constraint — ALTER TABLE ADD
            // CONSTRAINT ไม่มี IF NOT EXISTS ใน Postgres รันซ้ำจะ error
            """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Companies_BillingAccount') THEN
                    ALTER TABLE "Companies" ADD CONSTRAINT "FK_Companies_BillingAccount"
                        FOREIGN KEY ("BillingAccountId") REFERENCES "BillingAccounts"("Id") ON DELETE SET NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Companies_ParentCompany') THEN
                    ALTER TABLE "Companies" ADD CONSTRAINT "FK_Companies_ParentCompany"
                        FOREIGN KEY ("ParentCompanyId") REFERENCES "Companies"("Id") ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_AccountSubscriptions_BillingAccount') THEN
                    ALTER TABLE "AccountSubscriptions" ADD CONSTRAINT "FK_AccountSubscriptions_BillingAccount"
                        FOREIGN KEY ("BillingAccountId") REFERENCES "BillingAccounts"("Id") ON DELETE SET NULL;
                END IF;
            END $$;
            """,

            // ===== Backfill: AccountSubscription เดิม 1 แถว → BillingAccount 1 แถว =====
            // ลูกค้าเก่าต้องไม่รู้สึกอะไรเลย — แพลนกลุ่มที่มีอยู่ได้ "องค์กรผู้จ่าย"
            // ทันทีโดยไม่ต้องให้ใครมากรอกอะไร เจ้าของเดิมกลายเป็นผู้ดูแลหลัก
            // และบริษัททุกใบใต้แพลนนั้นถูกผูกเข้ากลุ่มให้เอง
            //
            // idempotent: ทุก statement มี NOT EXISTS/IS NULL guard รันซ้ำได้ไม่ซ้ำซ้อน
            """
            DO $$
            DECLARE r RECORD;
                    newId uuid;
            BEGIN
                FOR r IN
                    SELECT a."Id" AS acct_id, a."OwnerUserId", u."FullName", u."Email"
                    FROM "AccountSubscriptions" a
                    JOIN "Users" u ON u."Id" = a."OwnerUserId"
                    WHERE a."BillingAccountId" IS NULL AND a."IsDeleted" = false
                LOOP
                    newId := gen_random_uuid();
                    INSERT INTO "BillingAccounts" ("Id", "Name", "BillingEmail", "CreatedBy")
                    VALUES (newId,
                            COALESCE(NULLIF(r."FullName", ''), r."Email", 'บัญชีผู้ใช้'),
                            r."Email",
                            'migration:billing-account-backfill');

                    INSERT INTO "BillingAccountAdmins" ("Id", "BillingAccountId", "UserId", "IsPrimary", "CreatedBy")
                    VALUES (gen_random_uuid(), newId, r."OwnerUserId", true,
                            'migration:billing-account-backfill');

                    UPDATE "AccountSubscriptions" SET "BillingAccountId" = newId WHERE "Id" = r.acct_id;

                    -- บริษัททุกใบที่ subscription ชี้มาที่แพลนกลุ่มนี้ → เข้ากลุ่มเดียวกัน
                    UPDATE "Companies" c SET "BillingAccountId" = newId
                    FROM "Subscriptions" s
                    WHERE s."CompanyId" = c."Id"
                      AND s."AccountSubscriptionId" = r.acct_id
                      AND s."IsDeleted" = false
                      AND c."BillingAccountId" IS NULL;
                END LOOP;
            END $$;
            """,

            // ===== Metering: ฟีเจอร์ / ราคา / การใช้งาน (ACCOUNT_STRUCTURE.md §6) =====
            """
            CREATE TABLE IF NOT EXISTS "ApiFeatures" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "FeatureCode" varchar(60) NOT NULL,
                "Name" varchar(200) NOT NULL DEFAULT '',
                "NameEn" varchar(200) NULL,
                "Description" text NULL,
                "UnitLabel" varchar(50) NOT NULL DEFAULT 'รายการ',
                "IsPublished" boolean NOT NULL DEFAULT true,
                "RequiredScopes" varchar(300) NOT NULL DEFAULT '',
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_ApiFeatures_Code" ON "ApiFeatures" ("FeatureCode") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "CompanyFeatures" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "FeatureCode" varchar(60) NOT NULL,
                "IsEnabled" boolean NOT NULL DEFAULT false,
                "EnabledAt" timestamp NULL,
                "EnabledBy" varchar(200) NULL,
                "DisabledAt" timestamp NULL,
                "DisabledBy" varchar(200) NULL,
                "AcceptedUnitPrice" decimal(18,4) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_CompanyFeatures_Company_Code" ON "CompanyFeatures" ("CompanyId", "FeatureCode") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "ApiPricingPlans" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "FeatureCode" varchar(60) NOT NULL,
                "BillingAccountId" uuid NULL,
                "Method" integer NOT NULL DEFAULT 1,
                "UnitPrice" decimal(18,4) NOT NULL DEFAULT 0,
                "FreeQuotaPerMonth" integer NOT NULL DEFAULT 0,
                "TierJson" text NULL,
                "EffectiveFrom" timestamp NOT NULL DEFAULT now(),
                "EffectiveTo" timestamp NULL,
                "AdminNote" varchar(500) NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ApiPricingPlans_Feature_From" ON "ApiPricingPlans" ("FeatureCode", "EffectiveFrom") WHERE "IsDeleted" = false;""",
            """
            CREATE TABLE IF NOT EXISTS "UsageEvents" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NOT NULL,
                "BillingAccountId" uuid NULL,
                "BranchId" uuid NULL,
                "ApiClientId" uuid NULL,
                "FeatureCode" varchar(60) NOT NULL,
                "Quantity" integer NOT NULL DEFAULT 1,
                "UnitPriceSnapshot" decimal(18,4) NOT NULL DEFAULT 0,
                "ChargedAmount" decimal(18,2) NOT NULL DEFAULT 0,
                "CoveredByFreeQuota" boolean NOT NULL DEFAULT false,
                "IsSandbox" boolean NOT NULL DEFAULT false,
                "IdempotencyKey" varchar(200) NULL,
                "RefEntityType" varchar(100) NULL,
                "RefEntityId" uuid NULL,
                "BilledPeriod" varchar(7) NULL,
                "BilledDocumentId" uuid NULL,
                "OccurredAt" timestamp NOT NULL DEFAULT now(),
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_UsageEvents_Company_At" ON "UsageEvents" ("CompanyId", "OccurredAt") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_UsageEvents_Account_At" ON "UsageEvents" ("BillingAccountId", "OccurredAt") WHERE "IsDeleted" = false;""",
            // กันเก็บเงินซ้ำที่ระดับฐานข้อมูล — เช็คในโค้ดอย่างเดียวไม่พอเมื่อ
            // 2 request ของ client ชนกันพอดี (service จับ DbUpdateException แล้ว
            // คืนผลว่าเป็น duplicate ตามเจตนาของ idempotency)
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_UsageEvents_Idem" ON "UsageEvents" ("CompanyId", "IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL AND "IsDeleted" = false;""",

            // Seed แคตตาล็อกฟีเจอร์ตั้งต้น — ไม่ตั้งราคาให้ (ApiPricingPlan ว่าง)
            // เพราะราคาเป็นการตัดสินใจทางธุรกิจที่ admin ต้องกรอกเอง; ระบบจะนับ
            // การใช้งานไว้ที่ราคา 0 จนกว่าจะตั้งราคา แล้วเห็นปริมาณจริงก่อนตั้งได้
            """
            INSERT INTO "ApiFeatures" ("Id","FeatureCode","Name","NameEn","UnitLabel","RequiredScopes","SortOrder","Description","CreatedBy")
            SELECT * FROM (VALUES
                (gen_random_uuid(),'ocr.scan','สแกนเอกสารด้วย AI (OCR)','AI Document OCR','เอกสาร','ocr:write',10,'อ่านใบกำกับ/ใบเสร็จ แล้วคืนข้อมูลครบตาม §86/4 พร้อมระดับความมั่นใจรายฟิลด์','seed'),
                (gen_random_uuid(),'bank.recon','กระทบยอดธนาคารอัตโนมัติ','Bank Reconciliation','บรรทัด','bank:write',20,'จับคู่รายการใน statement กับเอกสาร/JE รวมถึงแบบหลายรายการรวมเป็นหนึ่ง','seed'),
                (gen_random_uuid(),'document.create','สร้างเอกสารผ่าน API','Create Document via API','ฉบับ','documents:write',30,'สร้างใบกำกับ/ใบเสร็จ/ใบสำคัญจ่าย พร้อมเลขที่ gap-free และลง JE ให้','seed'),
                (gen_random_uuid(),'etax.generate','ออก e-Tax Invoice','e-Tax Invoice','ฉบับ','etax:write',40,'สร้าง XML ETDA + ลงลายมือชื่อดิจิทัล และนำส่งกรมสรรพากร','seed'),
                (gen_random_uuid(),'assistant.ask','ผู้ช่วย AI ตอบคำถามบัญชี','AI Accounting Assistant','คำถาม','assistant:read',50,'ถามข้อมูลในระบบและกฎบัญชี/ภาษีไทย พร้อมอ้างอิงเอกสารจริง','seed')
            ) AS v("Id","FeatureCode","Name","NameEn","UnitLabel","RequiredScopes","SortOrder","Description","CreatedBy")
            WHERE NOT EXISTS (SELECT 1 FROM "ApiFeatures" f WHERE f."FeatureCode" = v."FeatureCode");
            """,

            // ═══ โควตาพิเศษ: ซื้อ top-up / ทำภารกิจแลกโควตา (LODGING_LICENSING_PLAN §11-12) ═══
            // `lodging.promo` ถูก seed เป็น IsPublished=true แต่ **ฟีเจอร์ยังไม่มีจริง**:
            // `LodgingReservation.PromoCode` เก็บเป็นข้อความเฉย ๆ — `LodgingPricingEngine`
            // ไม่เคยอ่านค่านี้มาคิดส่วนลดเลย ⇒ ลูกค้าจ่ายเดือนละ ฿200 แล้วไม่ได้อะไรเพิ่ม
            // ปิดการขายไว้ก่อน (ผู้ที่เปิดไปแล้วยังใช้ต่อและปิดเองได้ตามกติกา unpublish)
            // — CLAUDE.md: "feature ที่ยังไม่มีจริง ห้ามเขียนว่ามีแล้ว"
            """UPDATE "ApiFeatures" SET "IsPublished" = false, "Description" = "Description" || ' (ยังไม่เปิดขาย — อยู่ระหว่างพัฒนา)' WHERE "FeatureCode" = 'lodging.promo' AND "IsPublished" = true;""",
            // ── ข้อมูลติดต่อของเว็บไซต์ + ล้าง placeholder ที่ seed ค้างไว้ ──
            // เดิมเทมเพลตหน้า "ติดต่อเรา" ฝัง 02-XXX-XXXX / info@example.com เป็นข้อความ
            // ตายตัว ⇒ ลูกค้ากรอกเบอร์จริงในหน้าตั้งค่าแล้วหน้าเว็บยังโชว์ตัวอย่างอยู่
            // แก้โค้ด seeder อย่างเดียวไม่พอ — เว็บที่สร้างไปแล้วยังถือข้อความเก่า
            // (บทเรียนเดียวกับ OcrLearnedPatterns.ExtractionRegex / VendorKnownGoodValues)
            // ═══ POS เฟส 0: ยุบสองความจริงของสต็อกให้เหลือหนึ่ง ═══
            // เดิม Product.CurrentStock (ตัวเลขเดียวทั้งบริษัท) กับ WarehouseStock (ต่อคลัง)
            // เป็นระบบคู่ขนานที่ไม่คุยกัน — POS/ใบซื้อ/นับสต็อก/ผลิต เขียนตัวแรก ส่วนใบโอนคลัง
            // เขียนตัวหลัง ⇒ โอนของไปสาขาแล้วยอดที่ POS ตัดไม่ขยับ (POS_MULTI_BRANCH §2.2)
            // หลังรอบนี้: WarehouseStock = ความจริง · CurrentStock = ผลรวมที่ derive มา
            //
            // 1) ทุกบริษัทที่มีสินค้าต้องมี "คลังหลัก" — บริษัทที่ไม่เคยใช้ระบบคลังต้องทำงาน
            //    ได้เหมือนเดิมโดยไม่ต้องรู้ว่ามีคำว่าคลัง (ข้อสรุปทีม UX)
            """INSERT INTO "Warehouses" ("Id","CompanyId","Code","Name","IsDefault","IsActive","CreatedAt","CreatedBy","IsDeleted") SELECT gen_random_uuid(), c."Id", 'MAIN', 'คลังหลัก', true, true, now(), 'system:migration', false FROM "Companies" c WHERE c."IsDeleted" = false AND NOT EXISTS (SELECT 1 FROM "Warehouses" w WHERE w."CompanyId" = c."Id" AND w."IsDeleted" = false);""",
            // 2) บริษัทที่มีคลังอยู่แล้วแต่ไม่มีตัวไหนเป็น default → ตั้งตัวที่เก่าสุดเป็น default
            //    (ledger เลือก default ก่อน ถ้าไม่มีจะได้คลังเก่าสุดซึ่งเป็นพฤติกรรมเดียวกัน
            //    แต่ตั้งให้ชัดเพื่อไม่ให้ผลลัพธ์ขึ้นกับลำดับแถว)
            """UPDATE "Warehouses" w SET "IsDefault" = true WHERE w."IsDeleted" = false AND NOT EXISTS (SELECT 1 FROM "Warehouses" d WHERE d."CompanyId" = w."CompanyId" AND d."IsDeleted" = false AND d."IsDefault" = true) AND w."Id" = (SELECT w2."Id" FROM "Warehouses" w2 WHERE w2."CompanyId" = w."CompanyId" AND w2."IsDeleted" = false ORDER BY w2."CreatedAt", w2."Id" LIMIT 1);""",
            // 3) ย้ายยอดคงเหลือเดิมเข้าคลังหลัก **เฉพาะสินค้าที่ยังไม่มีแถวคลังใด ๆ**
            //    — สินค้าที่เคยผ่านใบโอนคลังมาแล้วมี WarehouseStock อยู่ ห้ามยัดเพิ่มซ้ำ
            //    (จะกลายเป็นยอดสองเท่า) · ยอดจะถูกซ่อมด้วย ReconcileProductTotalsAsync
            """INSERT INTO "WarehouseStocks" ("Id","CompanyId","WarehouseId","ProductId","Quantity","ReservedQuantity","AvailableQuantity","CreatedAt","CreatedBy","IsDeleted") SELECT gen_random_uuid(), p."CompanyId", w."Id", p."Id", p."CurrentStock", 0, p."CurrentStock", now(), 'system:migration', false FROM "Products" p JOIN "Warehouses" w ON w."CompanyId" = p."CompanyId" AND w."IsDefault" = true AND w."IsDeleted" = false WHERE p."IsDeleted" = false AND p."TrackStock" = true AND NOT EXISTS (SELECT 1 FROM "WarehouseStocks" s WHERE s."ProductId" = p."Id" AND s."IsDeleted" = false);""",
            // 4) สินค้าที่มีแถวคลังอยู่แล้ว → ปรับ CurrentStock ให้เท่าผลรวมคลัง
            //    (ก่อนหน้านี้สองตัวเลขนี้ไม่เคยตรงกัน ต้องเลือกให้คลังเป็นความจริง)
            """UPDATE "Products" p SET "CurrentStock" = COALESCE((SELECT SUM(s."Quantity") FROM "WarehouseStocks" s WHERE s."ProductId" = p."Id" AND s."IsDeleted" = false), 0) WHERE p."IsDeleted" = false AND p."TrackStock" = true;""",
            // 5) StockMovement เก่าที่ไม่มี WarehouseId → ยัดคลังหลักย้อนหลัง เพื่อให้รายงาน
            //    แยกคลังไม่มีรูโหว่ "ก่อนวันที่ระบบรู้จักคลัง"
            """UPDATE "StockMovements" m SET "WarehouseId" = (SELECT w."Id" FROM "Warehouses" w WHERE w."CompanyId" = m."CompanyId" AND w."IsDefault" = true AND w."IsDeleted" = false LIMIT 1) WHERE m."WarehouseId" IS NULL;""",
            // 6) index สำหรับการอ่านยอดต่อ (คลัง, สินค้า) ซึ่งเป็น hot path ของ POS
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_WarehouseStocks_Wh_Product" ON "WarehouseStocks" ("WarehouseId", "ProductId") WHERE "IsDeleted" = false;""",

            // ═══ POS เฟส 1: ผูกเครื่อง POS เข้ากับสาขา/คลัง/บัญชีรับเงิน ═══
            // เดิมมีแค่ `Location` เป็นข้อความอิสระ ⇒ ระบบไม่รู้ว่าเครื่องไหนอยู่สาขาไหน
            // ทำรายงานรายสาขา / รหัสสาขาบนใบกำกับ (§86/4) / ตัดสต็อกของสาขาไม่ได้เลย
            """ALTER TABLE "PosTerminals" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;""",
            """ALTER TABLE "PosTerminals" ADD COLUMN IF NOT EXISTS "WarehouseId" uuid NULL;""",
            """ALTER TABLE "PosTerminals" ADD COLUMN IF NOT EXISTS "CashAccountId" uuid NULL;""",
            """ALTER TABLE "PosTerminals" ADD COLUMN IF NOT EXISTS "BankAccountId" uuid NULL;""",
            """ALTER TABLE "PosTerminals" ADD COLUMN IF NOT EXISTS "AbbreviatedInvoicePrefix" varchar(20) NULL;""",
            // snapshot บนบิล — ห้าม resolve สดจากเครื่องตอนทำรายงาน (เครื่องย้ายสาขาได้
            // แล้วยอดขายย้อนหลังจะย้ายตามไปทั้งก้อน)
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "WarehouseId" uuid NULL;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "AbbreviatedInvoiceNumber" varchar(50) NULL;""",
            """ALTER TABLE "PosOrders" ADD COLUMN IF NOT EXISTS "IssuerBranchCode" varchar(5) NULL;""",
            // เลขใบกำกับภาษีอย่างย่อต้อง gap-free **ต่อสาขา** (§86/6) — unique ต่อบริษัท
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosOrders_AbbrevNo" ON "PosOrders" ("CompanyId", "AbbreviatedInvoiceNumber") WHERE "AbbreviatedInvoiceNumber" IS NOT NULL;""",
            // ครัวกลาง: ใบสั่งผลิตเบิก/รับที่คลังไหน (null = คลังหลัก)
            """ALTER TABLE "ProductionOrders" ADD COLUMN IF NOT EXISTS "WarehouseId" uuid NULL;""",

            // ═══ POS เฟส 2: สิทธิ์ออกใบกำกับภาษีอย่างย่อ (§86/6 · ภ.พ.06) ═══
            // เดิมสลิป POS พิมพ์คำว่า "ใบกำกับภาษีอย่างย่อ" ทุกใบโดยไม่ตรวจอะไรเลย
            // = ออกใบกำกับโดยไม่มีสิทธิ์ · ผู้ซื้อเคลมภาษีซื้อไม่ได้ตาม §82/5(5)
            // default false = ทุกบริษัทเริ่มจาก "ยังไม่ได้รับอนุมัติ" (ปลอดภัยกว่าเดา)
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "IsRetailApproved" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "PhoR06ApprovedDate" timestamp with time zone NULL;""",

            // สวิตช์ระดับแพลตฟอร์ม: บังคับ ภ.พ.06 ก่อนออกใบกำกับอย่างย่อหรือไม่
            // (คำตัดสินเจ้าของ 2026-09-19 — เผื่อกฎหมายเปลี่ยนจะได้ไม่ต้องแก้โค้ด)
            // default true = กฎหมายวันนี้ · ตัวตัดสิน Helpers/AbbreviatedTaxInvoiceRule
            """ALTER TABLE "SiteSettings" ADD COLUMN IF NOT EXISTS "RequirePhoR06ForAbbreviatedTaxInvoice" boolean NOT NULL DEFAULT true;""",

            // ═══ POS เฟส 3: ขายแล้วกินวัตถุดิบตามสูตร (sell-consumes-BOM) ═══
            // เดิม BOM ถูกอ่านจาก ProductionOrderService ที่เดียว (ผลิตล่วงหน้า) ·
            // การขายไม่เคยอ่านสูตรเลย ⇒ ขายชานม 1 แก้วตัดสต็อก "ชานมไข่มุก" ตัวเดียว
            // (ซึ่งไม่เคยมีของอยู่จริง = ยอดติดลบตลอดกาล) ส่วนใบชา/นม/ไข่มุก/แก้ว/หลอด
            // ไม่ถูกตัดเลย ⇒ ต้นทุนขายผิดทุกแก้ว
            // default false = ร้านเดิมไม่รู้สึกอะไร
            """ALTER TABLE "Products" ADD COLUMN IF NOT EXISTS "ConsumesBomOnSale" boolean NOT NULL DEFAULT false;""",
            // ท็อปปิ้งกินวัตถุดิบ ("+ไข่มุกเพิ่ม" กินไข่มุกจริงอีก 30 กรัม ไม่ใช่แค่บวกราคา)
            """ALTER TABLE "ProductModifierOptions" ADD COLUMN IF NOT EXISTS "ComponentProductId" uuid NULL;""",
            """ALTER TABLE "ProductModifierOptions" ADD COLUMN IF NOT EXISTS "ComponentQuantity" numeric(18,4) NOT NULL DEFAULT 0;""",

            // ═══ POS เฟส 6: ขอบเขตสาขาของผู้ใช้ ═══
            // แคชเชียร์ของสาขา B เปิดกะบนเครื่องของสาขา A ได้ ⇒ ยอดขายลงผิดสาขา ·
            // ตัดสต็อกผิดคลัง · เห็นยอดของสาขาที่ไม่ได้ดูแล
            // NULL = ทุกสาขา (พฤติกรรมเดิม — ห้ามให้ "ยังไม่ตั้งค่า" แปลว่า "ห้ามทุกอย่าง"
            // ไม่งั้นทุก tenant ที่อัปเกรดมาจะล็อกตัวเองออกจากระบบทันที)
            """ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "AllowedBranchIds" text NULL;""",

            // ═══ ใบแทน: ใบเสร็จ/ใบกำกับอย่างย่อ → ใบกำกับภาษีเต็มรูป (§86/6 → §86/4) ═══
            // การขายครั้งเดียวมีใบกำกับได้ใบเดียว — ออกใบเต็มรูปเพิ่มโดยไม่เรียกคืนใบเดิม
            // = ภาษีขายเข้า ภ.พ.30 สองรอบ ⇒ ต้องผูกกันสองทางแล้วให้รายงานนับใบแทน
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ReplacedByDocumentId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ReplacesDocumentId" uuid NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ReplacementReason" text NULL;""",
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ReplacedAt" timestamp with time zone NULL;""",
            // ใบหนึ่งใบถูกแทนได้ครั้งเดียว และเป็นใบแทนของใบเดียว
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Documents_ReplacesDoc" ON "Documents" ("ReplacesDocumentId") WHERE "ReplacesDocumentId" IS NOT NULL;""",

            // ═══ Payment gateway เฟส 1: ชั้นกลาง (PAYMENT_GATEWAY_DESIGN.md) ═══
            // ระบบมี 4 เส้นทางรับเงินแบบสลิปที่ต่างคนต่างเขียน — ถ้าต่อ gateway ทีละทาง
            // จะได้สำเนา 4 ชุดที่ drift แน่นอน · ทุกทางเข้าจึงเดินผ่าน PaymentIntent
            // คีย์ทุกช่องผ่าน ISecretProtector เท่านั้น (SitePaymentGateway เดิมใช้
            // EncryptionHelper คนละทางกับที่ webhook ใช้ถอด = สองทางเข้ารหัสในไฟล์เดียว)
            """CREATE TABLE IF NOT EXISTS "PaymentProviderConfigs" ("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "CompanyId" uuid NOT NULL, "ProviderCode" varchar(50) NOT NULL, "DisplayName" varchar(200) NULL, "TestPublicKey" text NULL, "TestSecretKeyProtected" text NULL, "LivePublicKey" text NULL, "LiveSecretKeyProtected" text NULL, "WebhookSecretProtected" text NULL, "Mode" integer NOT NULL DEFAULT 0, "LiveEnabledAt" timestamptz NULL, "LastTestPassedAt" timestamptz NULL, "LastWebhookAt" timestamptz NULL, "EnabledMethodsJson" text NULL, "ClearingAccountId" uuid NULL, "FeeExpenseAccountId" uuid NULL, "ExpectedFeePercentByMethodJson" text NULL, "WhtOnFee" integer NOT NULL DEFAULT 0, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false, CONSTRAINT "FK_PaymentProviderConfigs_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id"));""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentProviderConfigs_Company_Provider" ON "PaymentProviderConfigs" ("CompanyId", "ProviderCode") WHERE "IsDeleted" = false;""",

            """CREATE TABLE IF NOT EXISTS "PaymentIntents" ("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "CompanyId" uuid NOT NULL, "ProviderConfigId" uuid NULL, "ProviderCode" varchar(50) NOT NULL, "SourceKind" integer NOT NULL, "SourceId" uuid NOT NULL, "SiteId" uuid NULL, "ContactId" uuid NULL, "Amount" numeric(18,2) NOT NULL DEFAULT 0, "Currency" varchar(3) NOT NULL DEFAULT 'THB', "Description" text NULL, "CustomerEmail" text NULL, "CustomerPhone" text NULL, "Status" integer NOT NULL DEFAULT 0, "MethodKind" integer NOT NULL DEFAULT 1, "ProviderRef" varchar(200) NULL, "ProviderStatusRaw" varchar(100) NULL, "QrPayload" text NULL, "QrExpiresAt" timestamptz NULL, "ReturnUrl" text NULL, "AuthorizeUrl" text NULL, "FailureCode" varchar(100) NULL, "FailureMessage" text NULL, "FeeEstimated" numeric(18,2) NOT NULL DEFAULT 0, "FeeActual" numeric(18,2) NULL, "SettledAmount" numeric(18,2) NULL, "SettledAt" timestamptz NULL, "SettlementRef" varchar(200) NULL, "ConfirmedAt" timestamptz NULL, "ConfirmedBy" varchar(200) NULL, "ReceiptDocumentId" uuid NULL, "JournalEntryId" uuid NULL, "SettlementJournalEntryId" uuid NULL, "AttemptCount" integer NOT NULL DEFAULT 0, "LastPolledAt" timestamptz NULL, "IdempotencyKey" varchar(300) NOT NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false, CONSTRAINT "FK_PaymentIntents_Companies" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id"));""",
            // กันสร้าง intent ซ้ำสำหรับการจ่ายครั้งเดียวกัน (สองแท็บกดพร้อมกัน)
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentIntents_Idem" ON "PaymentIntents" ("CompanyId", "IdempotencyKey");""",
            // charge id ของ provider ต้องผูกกับ intent เดียว — webhook + poll เข้ามาพร้อมกันได้
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentIntents_ProviderRef" ON "PaymentIntents" ("ProviderCode", "ProviderRef") WHERE "ProviderRef" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_PaymentIntents_Source" ON "PaymentIntents" ("CompanyId", "SourceKind", "SourceId");""",
            // job กระทบยอดไล่เฉพาะใบที่ยังเปิดอยู่ (Created=0 · Pending=1)
            """CREATE INDEX IF NOT EXISTS "IX_PaymentIntents_Open" ON "PaymentIntents" ("Status", "LastPolledAt") WHERE "Status" IN (0, 1);""",

            """CREATE TABLE IF NOT EXISTS "PaymentIntentEvents" ("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "CompanyId" uuid NOT NULL, "IntentId" uuid NOT NULL, "At" timestamptz NOT NULL DEFAULT now(), "Source" integer NOT NULL, "FromStatus" integer NULL, "ToStatus" integer NOT NULL, "PayloadJson" text NULL, "Note" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false, CONSTRAINT "FK_PaymentIntentEvents_Intents" FOREIGN KEY ("IntentId") REFERENCES "PaymentIntents"("Id") ON DELETE CASCADE);""",
            """CREATE INDEX IF NOT EXISTS "IX_PaymentIntentEvents_Intent" ON "PaymentIntentEvents" ("IntentId", "At");""",

            // ผูกการชำระเงินเดิมเข้ากับ intent — ค่า null = รายการก่อนมีระบบนี้ (ปกติ)
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "PaymentIntentId" uuid NULL;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "GatewayFeeAmount" numeric(18,2) NULL;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "GatewayRef" varchar(200) NULL;""",
            """ALTER TABLE "PosPayments" ADD COLUMN IF NOT EXISTS "PaymentIntentId" uuid NULL;""",

            // ศูนย์ช่วยเหลือ (เอกสาร + วิดีโอสอนใช้งาน) — ระดับแพลตฟอร์ม ไม่มี CompanyId
            """CREATE TABLE IF NOT EXISTS "HelpResources" ("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "Title" varchar(300) NOT NULL DEFAULT '', "Description" text NULL, "Category" integer NOT NULL DEFAULT 1, "ModuleCode" varchar(50) NULL, "Kind" integer NOT NULL DEFAULT 1, "Provider" integer NOT NULL DEFAULT 0, "SourceUrl" text NULL, "StoragePath" text NULL, "FileName" text NULL, "FileSizeBytes" bigint NOT NULL DEFAULT 0, "DurationSeconds" integer NOT NULL DEFAULT 0, "ThumbnailUrl" text NULL, "IsPublished" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "ViewCount" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_HelpResources_Cat" ON "HelpResources" ("Category", "SortOrder") WHERE "IsDeleted" = false;""",
            """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "ContactPhone" varchar(50) NULL;""",
            """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "ContactEmail" varchar(256) NULL;""",
            """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "LineId" varchar(100) NULL;""",
            """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "FacebookUrl" text NULL;""",
            """ALTER TABLE "Sites" ADD COLUMN IF NOT EXISTS "InstagramUrl" text NULL;""",
            """UPDATE "PageBlocks" SET "ConfigJson" = replace(replace(replace("ConfigJson", '02-XXX-XXXX', '{{company.phone}}'), '081-XXX-XXXX', '{{company.phone}}'), '086-XXX-XXXX', '{{company.phone}}') WHERE "ConfigJson" LIKE '%XXX-XXXX%';""",
            """UPDATE "PageBlocks" SET "ConfigJson" = replace(replace(replace("ConfigJson", 'info@example.com', '{{company.email}}'), 'hello@shop.com', '{{company.email}}'), 'reservations@hotel.com', '{{company.email}}') WHERE "ConfigJson" LIKE '%@example.com%' OR "ConfigJson" LIKE '%hello@shop.com%' OR "ConfigJson" LIKE '%reservations@hotel.com%';""",
            """UPDATE "PageBlockTranslations" SET "ConfigJson" = replace(replace(replace("ConfigJson", '02-XXX-XXXX', '{{company.phone}}'), '081-XXX-XXXX', '{{company.phone}}'), '086-XXX-XXXX', '{{company.phone}}') WHERE "ConfigJson" LIKE '%XXX-XXXX%';""",
            """UPDATE "PageBlockTranslations" SET "ConfigJson" = replace("ConfigJson", 'info@example.com', '{{company.email}}') WHERE "ConfigJson" LIKE '%@example.com%';""",
            """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "DocumentBonusQuota" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "DocumentBonusExpiresAt" timestamptz NULL;""",
            """ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "QuotaRewardBlocked" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PlanTemplates" ADD COLUMN IF NOT EXISTS "AllowQuotaReward" boolean NOT NULL DEFAULT false;""",
            """CREATE TABLE IF NOT EXISTS "QuotaRewardOptions" ("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "Kind" integer NOT NULL DEFAULT 1, "Title" varchar(200) NOT NULL DEFAULT '', "Description" text NULL, "ImageUrl" text NULL, "MediaUrl" text NULL, "PartnerUrl" text NULL, "DurationSeconds" integer NOT NULL DEFAULT 60, "RewardDocuments" integer NOT NULL DEFAULT 5, "RewardValidDays" integer NOT NULL DEFAULT 30, "MaxPerDay" integer NOT NULL DEFAULT 2, "MaxPerMonth" integer NOT NULL DEFAULT 10, "EstimatedRevenuePerView" numeric(18,2) NOT NULL DEFAULT 0, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "QuotaRewardGrants" ("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(), "CompanyId" uuid NOT NULL, "OptionId" uuid NOT NULL REFERENCES "QuotaRewardOptions"("Id") ON DELETE CASCADE, "UserId" uuid NULL, "Kind" integer NOT NULL DEFAULT 1, "GrantedDocuments" integer NOT NULL DEFAULT 0, "GrantedAt" timestamptz NOT NULL DEFAULT now(), "ExpiresAt" timestamptz NOT NULL DEFAULT now(), "ClickedThrough" boolean NOT NULL DEFAULT false, "WatchedSeconds" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_QuotaRewardGrants_Company_At" ON "QuotaRewardGrants" ("CompanyId", "GrantedAt");""",
            // seed ตัวอย่าง "วิดีโอสอนฟีเจอร์ของเราเอง" — ไม่มีโฆษณาเครือข่ายเป็นค่าเริ่มต้น
            // (ปิดไว้ IsActive=false ⇒ ทั้งฟีเจอร์ปิดจนกว่าแอดมินจะเปิด — ชั้นที่ 1 ของสวิตช์)
            """INSERT INTO "QuotaRewardOptions" ("Id","Kind","Title","Description","DurationSeconds","RewardDocuments","RewardValidDays","MaxPerDay","MaxPerMonth","IsActive","SortOrder","CreatedBy") SELECT gen_random_uuid(), 2, 'ดูวิธีใช้ฟีเจอร์ที่ยังไม่ได้ใช้ 1 นาที', 'ดูวิดีโอสั้นแล้วรับโควตาเอกสารเพิ่ม 5 ฉบับ (ใช้ได้ 30 วัน)', 60, 5, 30, 2, 10, false, 10, 'seed' WHERE NOT EXISTS (SELECT 1 FROM "QuotaRewardOptions");""",

            // ═══ ส่วนขยาย add-on catalog (LODGING_LICENSING_PLAN.md §6) ═══
            // ใช้ ApiFeature/CompanyFeature เดิมเป็น catalog กลางของ "ของที่ขายเพิ่มได้
            // ทุกชนิด" แทนการสร้างระบบ license คู่ขนาน (สองแคตตาล็อก = drift แน่นอน)
            """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 1;""",
            """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "MinPlanCsv" varchar(120) NULL;""",
            """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "TrialDays" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "Icon" varchar(16) NULL;""",
            """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "ModuleCode" varchar(40) NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "TrialUntil" timestamptz NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "AutoDisableAfterTrial" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "GrantSource" integer NOT NULL DEFAULT 1;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "SnapshotUnitPrice" numeric(18,4) NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "LastBilledPeriod" varchar(7) NULL;""",
            // การชำระเงินของ add-on (LDG-P0-03) — เดิมเปิดใช้ได้ฟรีทันที ไม่มีทั้ง gateway และสลิป
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentStatus" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentIntentId" uuid NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentSlipUrl" text NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "SlipUploadedAt" timestamptz NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentReference" varchar(120) NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaidAmount" numeric(18,2) NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentReviewedAt" timestamptz NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentReviewedBy" text NULL;""",
            """ALTER TABLE "CompanyFeatures" ADD COLUMN IF NOT EXISTS "PaymentRejectedReason" text NULL;""",
            // แคตตาล็อกเดิมทั้งหมดคือผลิตภัณฑ์ Connected API — ติดป้ายให้ตรงความจริง
            // (แถวใหม่ที่ seed ด้านล่างเป็น BusinessAddOn/SystemMeter)
            """UPDATE "ApiFeatures" SET "ModuleCode" = 'Api' WHERE "ModuleCode" IS NULL AND "Kind" = 1;""",

            // ── แถวเก่าที่ติดป้าย "เรียก AI แล้ว" ทั้งที่ไม่เคยเรียก (ผลตรวจ AI-02/AI-03) ──
            // 27 endpoint heuristic ใน AiSuggestionController เขียน Status=Success +
            // ProviderUsed=DeepSeek มาตลอด ⇒ AiBudgetGuard นับเป็น call ที่เสียเงิน
            // (daily cap เต็มเพราะปุ่มที่ไม่เสียตังค์) และรายงานขึ้น "สำเร็จ (เรียก AI)"
            // ให้ call ที่ไม่เคยเกิด. แก้โค้ดอย่างเดียวไม่พอ — แถวที่สะสมไว้ยังโกหกต่อไป
            // (บทเรียนเดียวกับ OcrLearnedPatterns.ExtractionRegex / VendorKnownGoodValues)
            //
            // เกณฑ์คัดต้องแม่น เพราะเดาผิดฝั่งไหนก็เสียหาย: ปล่อยแถวจริงหลุด = cap ไม่กัน
            // ของจริง · ตีแถวจริงเป็น local = นับต้นทุนขาด. ใช้ลายเซ็นของ "การเรียก HTTP
            // ที่เกิดขึ้นจริง" 3 อย่างพร้อมกัน — มี latency · มี token · มีต้นทุน
            // ซึ่ง call จริงมีครบเสมอ ส่วน heuristic เขียน 0 ไว้ตายตัวทั้งสามช่อง ·
            // และเว้นแถวลูกสังเคราะห์ของ bulk call (CacheHitOfFeedbackId ไม่ null)
            // ที่ latency/token เป็น 0 โดยชอบธรรมเพราะแม่ของมันเป็น call จริง
            """UPDATE "AiSuggestionFeedbacks" SET "Status" = 8, "ProviderUsed" = 0 WHERE "Status" = 1 AND "ProviderUsed" <> 0 AND COALESCE("LatencyMs", 0) = 0 AND COALESCE("InputTokens", 0) = 0 AND COALESCE("OutputTokens", 0) = 0 AND COALESCE("CostUsd", 0) = 0 AND "CacheHitOfFeedbackId" IS NULL;""",

            // ── แถวที่ orchestrator ประทับ DeepSeek ทั้งที่ไม่เคยยิง provider (รอบ 177) ──
            // `AiOrchestrator.ReturnLocalAsync` / `RecordSkip` ส่ง `AiProviderType.DeepSeek`
            // ตายตัว ทั้งที่ทั้งสองเส้นเกิด**ก่อน**การยิง HTTP เสมอ (Skipped=4 ตอนนักเรียน
            // ตอบเองหรือปิด augmentation · BudgetExceeded=5 · NoProvider=6) ⇒ แถวนักเรียน
            // ถูกนับใน `AiSamples` ของ `AiFeedbackTrainingJob` ทั้งที่ `AiPrimaryAnswer`
            // เป็น null ⇒ `AiAccuracy30d` ต่ำเทียม ⇒ ยิ่งนักเรียนเก่ง สถานะยิ่ง "Healthy"
            // ⇒ ตัววัดที่ควรบอกว่า local อ่อน กลับบอกว่าแข็งเสมอ
            //
            // สถานะสามค่านี้เป็นลายเซ็นที่แม่นพอในตัวเอง — ไม่มีเส้นไหนในเรพตั้งค่าเหล่านี้
            // **หลัง**ยิง provider สำเร็จ/ล้ม (ล้มจริงคือ Failed=3 / InvalidResponse=7)
            """UPDATE "AiSuggestionFeedbacks" SET "ProviderUsed" = 0 WHERE "ProviderUsed" <> 0 AND "Status" IN (4, 5, 6);""",

            // ── แยก "ผู้ใช้เลือกเอง" ออกจาก "ค่าที่ระบบเติมแล้วถูกกดผ่าน" (รอบ 178) ──
            """ALTER TABLE "AiSuggestionFeedbacks" ADD COLUMN IF NOT EXISTS "UserChoiceOrigin" integer NULL;""",
            """ALTER TABLE "AiSuggestionMemories" ADD COLUMN IF NOT EXISTS "ExplicitAcceptCount" integer NOT NULL DEFAULT 0;""",

            // ⚠️ **ต้อง backfill** ไม่งั้นของที่ทำงานอยู่แล้วพัง: ฝั่งอ่านคลังคำตอบ
            // ใช้ `ExplicitAcceptCount >= 1` เป็นด่านใหม่ ⇒ แถวเก่าที่มีค่า 0 ทั้งหมด
            // จะหยุดเสิร์ฟทันทีที่ deploy ทั้งที่เคยเสิร์ฟถูกมาตลอด (กฎเหล็ก #4 H
            // "อะไรที่ทำได้ดีแล้ว ห้ามทำให้แย่ลง")
            // พฤติกรรมเดิมเทียบเท่ากับ "ทุกคำยืนยันนับเป็นการลงมือเลือก" ⇒ คัดลอก
            // AcceptCount มาเป็นยอดตั้งต้น แล้วให้กติกาใหม่มีผลกับของที่เรียนต่อจากนี้
            """UPDATE "AiSuggestionMemories" SET "ExplicitAcceptCount" = "AcceptCount" WHERE "ExplicitAcceptCount" = 0 AND "AcceptCount" > 0;""",

            // ═══ รอบ 182 · D7-1 — ล้าง "สตริงธง" ที่ไหลเข้าไปเป็นคำตอบจริง ═══
            // ปุ่ม "ใช้ของเดิม" ในป็อปอัพ (`wwwroot/js/ai-suggestion.js`) ส่ง
            // `chosenAnswer: '__USER_KEPT_EXISTING__'` เป็นธงบอกว่า "ผู้ใช้ไม่รับคำแนะนำ"
            // แต่ฝั่งเซิร์ฟเวอร์ **ไม่มีใครกรอง** ⇒ ถูกเก็บเป็นคำตอบ แล้วไหลต่อเข้า
            // `OcrCategoryMapping.AccountCode` ⇒ **รหัสผังบัญชีปลอมเข้าไปอยู่ในตัวแนะนำ GL**
            // โค้ดกรองแล้วที่ `Helpers/AiSentinelAnswers` (3 ชั้น) — ตรงนี้ล้างของที่ค้างอยู่
            """DELETE FROM "OcrCategoryMappings" WHERE "AccountCode" = '__USER_KEPT_EXISTING__';""",
            """DELETE FROM "AiSuggestionMemories" WHERE "LearnedAnswer" = '__USER_KEPT_EXISTING__';""",
            // แถว feedback: ล้าง**คำตอบ** แต่คง**การปฏิเสธ**ไว้ (ไม่แตะ UserChosenAt)
            // ⇒ สถิติ "ผู้ใช้ตรวจแล้วกี่ครั้ง" ยังถูก และตัวเรียนรู้จะอ่านเป็น
            // "คำตอบ AI ไม่ถูกใช้" (คะแนนลบ) แทนที่จะเรียนสตริงธงเป็นคำตอบ
            """UPDATE "AiSuggestionFeedbacks" SET "UserChosenAnswer" = NULL, "UserAcceptedAi" = false WHERE "UserChosenAnswer" = '__USER_KEPT_EXISTING__';""",

            // ═══ รอบ 182 · D7-4 — แถว routing ที่ค้างเป็น 0 ก่อนรอบ 178 ═══
            // โค้ด clamp ตอนอ่านแล้ว migration นี้ทำให้ "ค่าที่เก็บ" ตรงกับ "ค่าที่ใช้จริง"
            // ไม่งั้นหน้าแอดมินโชว์ 0 ทั้งที่ระบบสุ่มถามครูอยู่ = หน้าจอโกหก
            // Mode 1 = LocalOnly = เจตนาปิดครูที่ประกาศชัด — ห้ามแตะ
            """UPDATE "AiFeatureRoutingConfigs" SET "ProviderSamplingRate" = 0.01, "UpdatedAt" = NOW() WHERE "ProviderSamplingRate" IS NOT NULL AND "ProviderSamplingRate" < 0.01 AND "Mode" <> 1;""",

            // ═══ รอบ 182 · D7-3 — backfill ExplicitAcceptCount รอบสอง ═══
            // รอบ 178 ตั้งด่าน "ต้องมี Explicit ≥ 1" แต่หน้าเว็บ 6 จุดยังไม่ส่ง `source`
            // ⇒ แถวที่เกิดหลังรอบ 178 ของ 3 feature นี้ตกเป็น Implicit ทั้งที่ผู้เขียนแถว
            // มีทางเดียวคือ handler `change` (= ผู้ใช้เปลี่ยนค่าเอง) — เปิดไฟล์ยืนยันแล้ว
            // ⚠️ **ห้ามเหมารวม**: OcrFullReview/DocumentConversionSuggestion ยิงตอน "บันทึก"
            // (Implicit จริง) · feature จากป็อปอัพเพิ่งเริ่มส่ง Explicit รอบนี้ แถวเก่าพิสูจน์ไม่ได้
            """UPDATE "AiSuggestionMemories" SET "ExplicitAcceptCount" = "AcceptCount" WHERE "ExplicitAcceptCount" = 0 AND "AcceptCount" >= 1 AND "FeatureKey" IN ('ManualJeAccountSuggestion', 'ProductCategoryTagging', 'GlAccountSlotSuggestion');""",

            // ═══ รอบ 183 · D6-3 — ลักษณะเงินได้ของรายการเงินเดือน ═══
            // เครื่องคำนวณภาษีต้องแยก "ประจำ (ฉายไปงวดที่เหลือ)" ออกจาก
            // "ครั้งคราว/OT/คอมมิชชัน/โบนัส (ไม่ฉาย)" — เดิมตัดสินจาก **prefix ของรหัส**
            // (`OT*` · `COM` · `BONUS` · ที่เหลือถือเป็นประจำ) ⇒ โบนัสที่ HR ตั้งรหัสเอง
            // เป็น `BN01` ถูกฉาย × งวดที่เหลือ ⇒ ประมาณการรายได้ทั้งปีสูงเกินจริง
            // ⇒ หักภาษีพนักงานเกินทุกงวด
            """ALTER TABLE "PayrollItems" ADD COLUMN IF NOT EXISTS "IncomeNature" integer NOT NULL DEFAULT 0;""",
            // backfill ด้วย **สูตรเดียวกับกฎรหัสเดิมเป๊ะ ๆ** (`PayrollIncomeNatureRules.FromLegacyCode`)
            // ⇒ ตัวเลขของ payroll run ที่คำนวณไปแล้ว **ไม่ขยับแม้แต่สตางค์เดียว**
            // (กฎเหล็ก #4 H: "อะไรที่ทำได้ดีแล้ว ห้ามทำให้แย่ลง") · HR มาแก้ทีหลังได้ที่
            // แท็บ "รายการเงินเดือน" — ถ้าไม่รัน backfill ระบบก็ยังถูก (โค้ดตกกลับไปกฎเดิม
            // เมื่อค่าเป็น 0 = Unspecified) แต่ทุกแถวจะขึ้นป้าย "⚠️ ยังไม่ระบุ"
            // 1=RecurringAllowance 3=Overtime 4=Commission 5=Bonus
            """
            UPDATE "PayrollItems"
            SET "IncomeNature" = CASE
                    WHEN upper("Code") LIKE 'OT%' THEN 3
                    WHEN upper("Code") = 'COM'    THEN 4
                    WHEN upper("Code") = 'BONUS'  THEN 5
                    ELSE 1
                END
            WHERE "IncomeNature" = 0 AND "ItemType" = 'Earning';
            """,

            // ═══ รอบ 184 · ซ่อมอัตรา VAT ที่คำนวณได้ซึ่งถูกเขียนลงฐานไปแล้ว (รอบ 183) ═══
            // `PosTaxInvoiceLines` รอบ 183 คืน `VatRate` ที่**คำนวณ**จากยอดของบรรทัด
            // ⇒ บรรทัดที่รับเศษ VAT ได้ค่าอย่าง `7.01` หรือ `-0.00` · ค่านี้ไม่ได้อยู่เฉย ๆ:
            // `TaxService` ใช้ `Max(l.VatRate)` พิมพ์ลง**รายงานภาษีขาย/แบบยื่น 11 จุด** และ
            // `EtaxInvoiceService` ใช้เป็นอัตราทั้งหัวใบและรายบรรทัดใน XML ที่ยื่นกรมสรรพากร
            // ⇒ ใบกำกับประกาศอัตรา 7.01% ต่อสรรพากร · โค้ดบังคับให้เป็น 0/7/-1 แล้ว
            // ⚠️ แก้เฉพาะ**อัตราที่ประกาศ** ไม่แตะ `Amount`/`VatAmount` ⇒ ยอดหัวเอกสารไม่ขยับ
            //    (idempotent · ช่วงแคบพอที่จะไม่กลืนอัตราอื่นที่ตั้งใจ)
            """UPDATE "DocumentLines" SET "VatRate" = 7 WHERE "VatRate" <> 7 AND "VatRate" > 6.5 AND "VatRate" < 7.5 AND "IsDeleted" = false;""",
            """UPDATE "DocumentLines" SET "VatRate" = 0 WHERE "VatRate" <> 0 AND "VatRate" > -0.5 AND "VatRate" < 0.5 AND "IsDeleted" = false;""",
            // **บรรทัดติดลบที่ออกไปแล้วห้ามแก้** — ใบที่ออกและอาจยื่นภาษีไปแล้ว
            // การแก้ย้อนหลังผิด §86/4 ("ห้ามแก้ไขย้อนหลัง") · คิวรีนับอยู่ในรายงานรอบนี้

            // ═══ รอบ 184 · D-1 — ชนิดผู้ติดต่อ "ยังไม่รู้" ต้องเห็นได้ ═══
            // `ContactType` ตัดสิน **ภ.ง.ด.3 vs ภ.ง.ด.53** และ scheme ของ e-Tax XML
            // (`NIDN` vs `TXID`) · ค่าตั้งต้นเดิมคือ `Individual` ⇒ คู่ค้าที่ไม่มีใคร
            // เคยเลือกชนิดให้ กลายเป็น "บุคคลธรรมดาที่พิสูจน์แล้ว" ในสายตา
            // `WhtPayeeKind.Detect` ⇒ ระบบเงียบจนกระทั่งแบบยื่นผิดไปแล้ว
            // ⚠️ **ห้าม backfill แถวเก่า** — การเปลี่ยน 1 → 0 จะพลิกคำตอบของ
            //   `WhtPayeeKind.Detect` จาก "พิสูจน์แล้วว่าบุคคล" เป็น "ไม่รู้"
            //   ⇒ **50 ทวิ ย้ายแบบยื่นโดยไม่มีใครสั่ง** = ความเสียหายเงียบ
            //   บรรทัดนี้แตะแค่ DEFAULT ของคอลัมน์ ไม่แตะแถวใดเลย
            //   (enum เดิมคือ 1/2/3 · ยืนยันแล้วว่าไม่มีแถวไหนเก็บ 0 อยู่)
            """ALTER TABLE "Contacts" ALTER COLUMN "ContactType" SET DEFAULT 0;""",

            // ═══ รอบ 184 · D-3 — ที่มาของข้อเสนออัตราหัก ณ ที่จ่ายบนผลสแกน ═══
            // เดิมเดาที่มาจาก "มีรหัส ม.40 ติดมาไหม" ⇒ ประโยค "หมวดรายจ่ายนี้กฎหมายให้
            // ผู้จ่ายหัก…" โผล่บนใบที่ข้อเสนอมาจาก**นิสัยผู้ขาย** (อ้างกฎหมายผิด)
            // ⚠️ **ไม่ backfill โดยเจตนา** — "ที่มา" ของใบเก่าไม่มีอยู่ในข้อมูลต้นทางแล้ว
            //   0 = None = "ไม่ทราบที่มา" และฝั่งอ่านพิมพ์ประโยคที่บอกตรง ๆ ว่าไม่ทราบ
            //   (ไม่รู้ = บอกว่าไม่รู้ · G3 — ดีกว่าเดาที่มาให้ใบเก่าทุกใบ)
            """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "SuggestedWhtSource" integer NOT NULL DEFAULT 0;""",

            // ═══ รอบ 184 · KPI คู่ — แยก "นักเรียนโตจริง" ออกจาก "ระบบเงียบลง" ═══
            // `UsedAi` ที่ลดลงตีความได้สองทางที่ตรงกันข้าม (นักเรียนเก่งขึ้น = ดี ·
            // ด่านปิด/เกินงบ/เลิกสุ่มถามครู = แย่) ⇒ ตัวเลขเดียวแยกไม่ออก
            // DEFAULT 0 = NotEnoughData = "ยังตัดสินไม่ได้" (ไม่ใช่ "ปกติ") · job เขียนทับรอบถัดไป
            """ALTER TABLE "LocalModelHealths" ADD COLUMN IF NOT EXISTS "AiSamplesLast30d" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "LocalModelHealths" ADD COLUMN IF NOT EXISTS "GrowthState" integer NOT NULL DEFAULT 0;""",

            // ═══ รอบ 184 · D4-2 ต่อ — คู่ที่เป็น "เอกสาร" + เหตุผลของสถานะ ═══
            // รอบ 183 ปิดช่องโหว่ "Matched ที่ไม่มีคู่" ด้วยการ **ไม่ประทับสถานะเลย**
            // เมื่อ AI เสนอ *Document* (ตารางไม่มีคอลัมน์เก็บ document id) ⇒ ความสามารถ
            // นั้นหายไปทั้งเส้น · รอบนี้คืนมาอย่างถูกวิธี: มีที่เก็บคู่ ⇒ ประทับได้
            // `MatchRuleCode`/`MatchReason` = เหตุผลที่ `BankMatchArbiter` ตัดสิน เดินทาง
            // ถึงหน้าจอ (ผู้ใช้ต้องเห็นว่า "ทำไมระบบเสนอใบนี้" และ "ทำไมไม่ประทับให้เอง")
            """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "SuggestedDocumentId" uuid NULL;""",
            """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "MatchRuleCode" text NULL;""",
            """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "MatchReason" text NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_BankTransactions_SuggestedDocumentId" ON "BankTransactions" ("CompanyId", "SuggestedDocumentId") WHERE "SuggestedDocumentId" IS NOT NULL;""",

            // ป้าย "AI-Batch" ที่ระบบเขียนให้ทุกแถวแม้ `WasAiValidated = false` — โกหกสองชั้น
            // (บอกว่า AI ทำ ทั้งที่คนกด · และทิ้งชื่อคนที่กดไปเลย ทั้งที่ audit row รู้)
            // แหล่งความจริงคือ `BankMatchAuditLogs` ซึ่งเก็บทั้งผู้กดและธง AI ไว้แล้ว
            // ⇒ นี่ไม่ใช่การ "แก้ประวัติ" แต่เป็นการ **คืนค่าที่ถูกจากบันทึกต้นทาง**
            // ทับค่าที่ระบบแต่งขึ้น · แถวที่ไม่มี audit row จะไม่ถูกแตะ (JOIN ไม่ติด)
            """
            UPDATE "BankTransactions" t
               SET "ReconciledBy" = a."AppliedByUserId"::text || CASE WHEN a."WasAiValidated" THEN '+ai' ELSE '' END
              FROM (SELECT DISTINCT ON ("BankTransactionId") "BankTransactionId", "AppliedByUserId", "WasAiValidated"
                      FROM "BankMatchAuditLogs" ORDER BY "BankTransactionId", "CreatedAt" DESC) a
             WHERE t."Id" = a."BankTransactionId" AND t."ReconciledBy" = 'AI-Batch';
            """,

            // ═══ รอบ 184 · D4-7 — ธง §65 ตรี(9) ของเงินสดย่อย ═══
            // เงินที่จ่ายจากลิ้นชักโดยไม่มีใบเสร็จ **หักเป็นรายจ่ายไม่ได้** แต่ "ห้ามบันทึก"
            // ไม่ใช่ทางออก — เงินออกไปแล้วจริง ถ้าไม่ให้บันทึก ยอดในระบบจะไม่ตรงกับเงิน
            // ในลิ้นชัก **ถาวร** และไม่มีใครเห็น ⇒ เลือกทิศที่ความเสียหายถูก**นับ**
            // (ธงไหลเข้า worksheet บวกกลับ ภ.ง.ด.50 เอง) ไม่ใช่ถูก**ซ่อน**
            // ⚠️ ข้อนี้ต่างจากตัวอักษรใน CLAUDE.md §L(9) ที่เขียนว่า hard block — ดูหมายเหตุในคอมมิต
            """ALTER TABLE "PettyCashTransactions" ADD COLUMN IF NOT EXISTS "IsNonDeductible" boolean NOT NULL DEFAULT false;""",
            """ALTER TABLE "PettyCashTransactions" ADD COLUMN IF NOT EXISTS "NonDeductibleRuleCode" text NULL;""",
            // แถวเก่าที่ไม่มีเลขที่ใบเสร็จเข้าข่ายเดียวกัน — ติดธงให้เห็น ไม่ได้เปลี่ยนยอดเงิน
            """
            UPDATE "PettyCashTransactions"
               SET "IsNonDeductible" = true, "NonDeductibleRuleCode" = 'RD-65TER-9'
             WHERE "Type" = 'Disbursement'
               AND ("ReceiptReference" IS NULL OR btrim("ReceiptReference") = '')
               AND "IsNonDeductible" = false;
            """,

            // ═══ รอบ 184 · Q1 — ฐานเงินสมทบ ม.5 รายรายการ ═══
            // ม.5 นิยาม "ค่าจ้าง" กว้างกว่าเงินเดือนพื้นฐาน (เบี้ยขยัน/ค่าตำแหน่ง/ค่าครองชีพ
            // ที่จ่ายประจำ = ค่าจ้าง · ค่าเดินทาง/ที่พักตามจ่ายจริง = ไม่ใช่) เส้นแบ่งขึ้นกับ
            // ข้อตกลงการจ้างของแต่ละบริษัท ⇒ เป็นธงที่ HR ติ๊กเอง ไม่ใช่สิ่งที่ระบบเดาได้
            // ⚠️ **ห้าม backfill** — NULL = "ยังไม่มีใครตัดสิน" = ไม่รวมในฐาน = พฤติกรรมเดิมเป๊ะ
            //   ⇒ ไม่มีบริษัทไหนยอดนำส่ง/ไฟล์ สปส.1-10 ขยับเพราะการอัปเดตระบบ
            //   (ต่างจาก IncomeNature รอบ 183 ที่ backfill ได้เพราะ "ไม่ระบุ" มีสูตรเดิมรองรับ)
            """ALTER TABLE "PayrollItems" ADD COLUMN IF NOT EXISTS "CountsForSsoBase" boolean NULL;""",

            // ═══ รอบ 183 · D2-B1a — รายงานที่ "ยื่นแล้ว" โดยไม่มีเลขรับ ═══
            // `FileTaxReportAsync` เคยประทับ `Filed` + `FilingLockedAt` จาก**การกดปุ่ม**
            // อย่างเดียว ⇒ ล็อกเอกสาร/JE ทั้งงวดด้วยเหตุการณ์ที่ระบบไม่รู้ว่าเกิดจริง
            // โค้ดแยกเป็น `Submitted` (ประกาศ ไม่ล็อก) / `Filed` (มีเลขรับ ล็อก) แล้ว
            // ⚠️ แถวเก่า **ห้ามลดชั้นย้อนหลัง** (`Filed` → `Submitted`) เพราะจะปลดล็อก
            // งวดที่ผู้ใช้อาจยื่นไปจริง — ติดธงให้ตามเก็บเลขรับแทน (UI ขึ้นปุ่ม "บันทึกเลขรับ")
            """UPDATE "TaxReports" SET "RdSubmissionStatus" = 'Declared' WHERE "Status" = 1 AND ("RdAckNumber" IS NULL OR btrim("RdAckNumber") = '') AND "RdSubmissionStatus" IS NULL;""",

            // ═══ รอบ 183 · D1-11 — ล้างคำตอบ "นักเรียน" ที่ระบบแต่งขึ้นเอง ═══
            // `WorkflowPrompts` ตั้ง `LocalModelVersion = "ApprovalWarningCollector-v1"` +
            // `LocalModelAnswer = "Acknowledge"` ให้ **ทุกแถว** ของคำเตือนก่อนอนุมัติ
            // ทั้งที่ไม่มีโมเดลไหนตอบ ⇒ คลังฝึกเต็มไปด้วย "นักเรียนตอบ Acknowledge"
            // ที่นักเรียนไม่เคยพูด ⇒ `ApprovalWarningDistillationModel` เรียนว่า
            // "ปล่อยผ่านทุก template" · โค้ดเลิกแต่งแล้ว ตรงนี้ล้างของที่ค้างอยู่
            // (ลายเซ็น `LocalModelVersion` แยกแถวปลอมออกจากคำตอบจริงได้ 100%)
            """UPDATE "AiSuggestionFeedbacks" SET "LocalModelAnswer" = NULL, "LocalModelConfidence" = NULL, "LocalModelVersion" = NULL WHERE "FeatureKey" = 'ApprovalWarningFixSuggestion' AND "LocalModelVersion" = 'ApprovalWarningCollector-v1' AND "LocalModelAnswer" = 'Acknowledge';""",
            // แถวที่จดว่า "ผู้ใช้เห็นด้วยกับ AI" ทั้งที่ AI ไม่เคยตอบ — เป็นไปไม่ได้
            // โดยนิยาม (ไม่มีคำตอบให้เห็นด้วย) · คง `UserChosenAt` ไว้ = ผู้ใช้ตรวจจริง
            """UPDATE "AiSuggestionFeedbacks" SET "UserAcceptedAi" = false WHERE "FeatureKey" = 'ApprovalWarningFixSuggestion' AND "UserAcceptedAi" = true AND ("AiPrimaryAnswer" IS NULL OR "AiPrimaryAnswer" = '');""",

            // ═══ รอบ 183 · D4-2 — "จับคู่แล้ว" ที่ไม่มีคู่ (ราก R1) ═══
            // `BankFeedService.TryAutoMatchAsync` เคยประทับ `Matched`/`Suggested` โดย
            // **ไม่เก็บ id ของคู่** (เจอ Document แต่ตารางไม่มีคอลัมน์เก็บ document id)
            // ⇒ ผู้ใช้เห็น "เงินก้อนนี้มีที่มาที่ไปแล้ว" โดยไม่มีอะไรให้กดดู และยอด
            // "ยังไม่กระทบยอด" ต่ำกว่าความจริง · โค้ดปิดแล้ว ตรงนี้คืนแถวเก่าเป็น
            // `Unmatched` เพื่อให้กลับเข้าคิวจับคู่ด้วยมือ (0=Unmatched 1=Matched 3=Suggested)
            // ⚠️ เงื่อนไขไล่ครบ **ทุกช่องที่เก็บคู่ได้** (เดี่ยว/M:N เก่า/กลุ่มใหม่)
            // มิฉะนั้นจะไปถอนการจับคู่ที่ถูกต้องอยู่แล้วทิ้ง
            """
            UPDATE "BankTransactions"
            SET    "ReconciliationStatus" = 0, "ReconciledAt" = NULL, "ReconciledBy" = NULL
            WHERE  "ReconciliationStatus" IN (1, 3)
              AND  "MatchedPaymentId"      IS NULL
              AND  "MatchedJournalEntryId" IS NULL
              AND  "MatchedEntryIdsJson"   IS NULL
              AND  "MatchGroupId"          IS NULL
              AND  "ReconciliationGroupId" IS NULL;
            """,

            // ── seed add-on ของโมดูลที่พัก + มิเตอร์ระบบ ──
            // ต่างจาก seed ของ Connected API ตรงที่ **ตั้งราคาตั้งต้นให้ด้วย** (ด้านล่าง)
            // เพราะเจ้าของระบบกำหนดตัวเลขมาแล้ว ("guest portal +100/เดือน") และ add-on ที่
            // ไม่มีราคา = เปิดใช้ได้ฟรีเงียบ ๆ ซึ่งเป็นช่องรั่วรายได้แบบเดียวกับที่
            // LODGING_LICENSING_PLAN §6 เตือนไว้. admin เปลี่ยนราคาได้ทุกเมื่อผ่านหน้า
            // /admin/addons.html (สร้าง ApiPricingPlan แถวใหม่ที่มี EffectiveFrom ใหม่)
            """
            INSERT INTO "ApiFeatures" ("Id","FeatureCode","Name","NameEn","UnitLabel","RequiredScopes","SortOrder","Description","Kind","ModuleCode","Icon","TrialDays","IsPublished","CreatedBy")
            SELECT * FROM (VALUES
                (gen_random_uuid(),'lodging.guest-portal','Guest Portal Pro','Guest Portal Pro','เดือน','',110,'QR ต่อห้อง · ให้แขกแจ้งขอผ้า/แจ้งซ่อม/รูมเซอร์วิสเอง · แจ้งเตือนก่อนเช็คอินและขอรีวิวอัตโนมัติ (หน้าการจองด้วยลิงก์ให้แขกดู/อัปโหลดสลิป/ยกเลิก = ใช้ฟรีอยู่แล้ว)',2,'Lodging','🛎️',14,true,'seed'),
                (gen_random_uuid(),'lodging.promo','โค้ดส่วนลด/โปรโมชัน','Promo codes','เดือน','',120,'สร้างโค้ดส่วนลดสำหรับจองตรง — ดึงลูกค้าจาก OTA ที่คิดค่าคอมมิชชัน 15-18%',2,'Lodging','🏷️',14,false,'seed'),
                (gen_random_uuid(),'lodging.channel-manager','เชื่อม OTA (Agoda/Booking)','Channel manager','เดือน','',130,'ซิงก์ห้องว่างและราคาไปยัง OTA อัตโนมัติ — กัน overbooking และเลิกคีย์สองระบบ',2,'Lodging','🔗',14,false,'seed'),
                (gen_random_uuid(),'lodging.pos-folio','ชาร์จ POS เข้าห้องพัก','POS to folio','เดือน','',140,'สั่งอาหาร/เครื่องดื่มที่ POS แล้วเข้าบิลห้องอัตโนมัติ ปิดยอดตอนเช็คเอาต์',2,'Lodging','🍽️',14,false,'seed'),
                (gen_random_uuid(),'lodging.analytics','รายงาน Occupancy/ADR/RevPAR','Lodging analytics','เดือน','',150,'อัตราเข้าพัก · ราคาเฉลี่ยต่อห้อง · รายได้ต่อห้องที่มี — ผูกกับตัวเลขบัญชีจริง',2,'Lodging','📊',14,false,'seed'),
                (gen_random_uuid(),'lodging.multi-property','ที่พักหลายแห่ง','Multi-property','แห่ง/เดือน','',160,'เปิดที่พักแห่งที่ 2 ขึ้นไปในบริษัทเดียวกัน (แห่งแรกใช้ฟรี)',2,'Lodging','🏘️',0,true,'seed'),
                (gen_random_uuid(),'lodging.i18n','หน้าจองหลายภาษา/สกุลเงิน','Multi-language booking','เดือน','',170,'หน้าจองรองรับหลายภาษาและแสดงราคาหลายสกุลเงินสำหรับแขกต่างชาติ',2,'Lodging','🌏',14,false,'seed'),
                (gen_random_uuid(),'lodging.early-late-fee','คิดค่า early/late check-out อัตโนมัติ','Early/late fee','เดือน','',180,'คิดค่าธรรมเนียมเข้าก่อน/ออกช้าตามที่ตั้งไว้ให้อัตโนมัติ ไม่ต้องคีย์เอง',2,'Lodging','⏰',14,false,'seed'),
                (gen_random_uuid(),'lodging.loyalty','สะสมแต้ม/สมาชิก','Loyalty','เดือน','',190,'สะสมแต้มและสิทธิ์สมาชิกสำหรับแขกที่กลับมาพักซ้ำ',2,'Lodging','⭐',14,false,'seed'),
                (gen_random_uuid(),'documents.overage','เอกสารเกินโควตาแพ็กเกจ','Document overage','ฉบับ','',900,'เอกสารบัญชีที่ออกเกินโควตาของแพ็กเกจในเดือนนั้น — คิดต่อฉบับ ไม่มีการบล็อกการออกเอกสารที่กฎหมายบังคับ',3,'Billing','📄',0,true,'seed'),
                (gen_random_uuid(),'lodging.stay','การเข้าพักที่ปิดสถานะ','Closed stay','การเข้าพัก','',910,'นับ 1 หน่วยต่อการเข้าพักที่เช็คเอาต์/ไม่มา/ยกเลิกโดยมีมัดจำ — เป็นมิเตอร์ของโมดูลที่พัก',3,'Lodging','🛏️',0,true,'seed'),
                (gen_random_uuid(),'lodging.email.overage','อีเมลแจ้งเตือนเกินโควตา','Email overage','ฉบับ','',920,'อีเมลยืนยัน/แจ้งเตือนของที่พักที่เกินโควตาฟรีต่อเดือน',3,'Lodging','✉️',0,true,'seed'),
                (gen_random_uuid(),'lodging.notify.sms','SMS แจ้งเตือน','SMS notification','ข้อความ','',930,'ข้อความ SMS ถึงแขก — ต้นทุนต่อข้อความจริงจากผู้ให้บริการ',3,'Lodging','📱',0,false,'seed'),
                (gen_random_uuid(),'documents.topup','ซื้อโควตาเอกสารเพิ่ม','Document top-up','แพ็ก 100 ฉบับ','',940,'ซื้อโควตาเอกสารเพิ่มเป็นก้อนสำหรับเดือนที่ยอดจองสูงกว่าปกติ',3,'Billing','➕',0,true,'seed')
            ) AS v("Id","FeatureCode","Name","NameEn","UnitLabel","RequiredScopes","SortOrder","Description","Kind","ModuleCode","Icon","TrialDays","IsPublished","CreatedBy")
            WHERE NOT EXISTS (SELECT 1 FROM "ApiFeatures" f WHERE f."FeatureCode" = v."FeatureCode");
            """,

            // ราคาตั้งต้น (LODGING_LICENSING_PLAN §3.1) — Method: 3=FlatMonthly, 1=PerUnit
            // FreeQuotaPerMonth ของ lodging.email.overage = 200 (โควตาฟรีของ Standard)
            """
            INSERT INTO "ApiPricingPlans" ("Id","FeatureCode","Method","UnitPrice","FreeQuotaPerMonth","EffectiveFrom","AdminNote","CreatedBy")
            SELECT gen_random_uuid(), v."FeatureCode", v."Method", v."UnitPrice", v."FreeQuota", now(), v."AdminNote", 'seed'
            FROM (VALUES
                ('lodging.guest-portal',3,100.0,0,'ราคาตั้งต้นจากแผน — แก้ได้ที่ /admin/addons.html'),
                ('lodging.promo',3,200.0,0,'ราคาตั้งต้นจากแผน'),
                ('lodging.channel-manager',3,1200.0,0,'ราคาตั้งต้นจากแผน (ยังไม่เปิดขาย)'),
                ('lodging.pos-folio',3,400.0,0,'ราคาตั้งต้นจากแผน (ยังไม่เปิดขาย)'),
                ('lodging.analytics',3,250.0,0,'ราคาตั้งต้นจากแผน (ยังไม่เปิดขาย)'),
                ('lodging.multi-property',3,300.0,0,'ต่อที่พักที่เพิ่มจากแห่งแรก'),
                ('lodging.i18n',3,250.0,0,'ราคาตั้งต้นจากแผน (ยังไม่เปิดขาย)'),
                ('lodging.early-late-fee',3,99.0,0,'ราคาตั้งต้นจากแผน (ยังไม่เปิดขาย)'),
                ('lodging.loyalty',3,300.0,0,'ราคาตั้งต้นจากแผน (ยังไม่เปิดขาย)'),
                ('documents.overage',1,5.0,0,'ต่อเอกสารที่เกินโควตาแพ็กเกจ'),
                ('lodging.stay',1,0.0,0,'มิเตอร์นับอย่างเดียว — โควตาอยู่ที่แพ็กเกจบัญชี'),
                ('lodging.email.overage',1,0.2,200,'ฟรี 200 ฉบับ/เดือน เกินคิดฉบับละ 0.20'),
                ('lodging.notify.sms',1,0.8,0,'ต้นทุน SMS ต่อข้อความ'),
                ('documents.topup',1,300.0,0,'แพ็ก 100 ฉบับ')
            ) AS v("FeatureCode","Method","UnitPrice","FreeQuota","AdminNote")
            WHERE NOT EXISTS (SELECT 1 FROM "ApiPricingPlans" p WHERE p."FeatureCode" = v."FeatureCode" AND p."IsDeleted" = false);
            """,

            // ===== ApiKey — ขยายให้รองรับ /api/v1 (ACCOUNT_STRUCTURE.md §7) =====
            // ต่อยอดตารางเดิมแทนการสร้าง ApiClient ใหม่แข่งกัน — key ที่ลูกค้าใช้อยู่
            // ทำงานเหมือนเดิมทุกประการ. Scopes เป็น NULL สำหรับคีย์เก่าทุกใบ
            // = **ถูกกันออกจาก /api/v1 โดยอัตโนมัติ** ต้องตั้งใจให้สิทธิ์เท่านั้น
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "BillingAccountId" uuid NULL;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "Scopes" varchar(300) NULL;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "WebhookUrl" varchar(500) NULL;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "WebhookSecret" text NULL;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "ConnectorType" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "ConnectorConfigJson" text NULL;""",
            """ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "IsSandbox" boolean NOT NULL DEFAULT false;""",

            // ===== Chatbot: public FAQ + tenant assistant (CHATBOT_PLAN.md) =====
            """
            CREATE TABLE IF NOT EXISTS "ChatConversations" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "Channel" varchar(20) NOT NULL DEFAULT 'Public',
                "CompanyId" uuid NULL,
                "UserId" uuid NULL,
                "SessionToken" varchar(64) NULL,
                "VisitorName" varchar(200) NULL,
                "VisitorEmail" varchar(200) NULL,
                "IpHash" varchar(64) NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'AiHandling',
                "Title" varchar(300) NULL,
                "MessageCount" integer NOT NULL DEFAULT 0,
                "LastMessageAt" timestamp NOT NULL DEFAULT now(),
                "AgentJoinedAt" timestamp NULL,
                "AgentName" varchar(200) NULL,
                "SatisfactionScore" integer NULL,
                "PurgeAfter" timestamp NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChatConversations_Session" ON "ChatConversations" ("SessionToken") WHERE "SessionToken" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_ChatConversations_Status" ON "ChatConversations" ("Status", "LastMessageAt") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_ChatConversations_Company" ON "ChatConversations" ("CompanyId", "UserId") WHERE "CompanyId" IS NOT NULL;""",
            """
            CREATE TABLE IF NOT EXISTS "ChatMessages" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "ConversationId" uuid NOT NULL,
                "Role" varchar(20) NOT NULL DEFAULT 'User',
                "Content" text NOT NULL DEFAULT '',
                "UsedAi" boolean NOT NULL DEFAULT false,
                "AiConfidence" decimal(5,4) NULL,
                "AiFeedbackId" uuid NULL,
                "RetrievedChunksJson" text NULL,
                "FlaggedForReview" boolean NOT NULL DEFAULT false,
                "HelpfulVote" integer NULL,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE INDEX IF NOT EXISTS "IX_ChatMessages_Conversation" ON "ChatMessages" ("ConversationId", "CreatedAt");""",
            """
            CREATE TABLE IF NOT EXISTS "KnowledgeChunks" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CompanyId" uuid NULL,
                "SourceType" varchar(30) NOT NULL DEFAULT 'File',
                "SourceKey" varchar(300) NOT NULL DEFAULT '',
                "Title" varchar(400) NOT NULL DEFAULT '',
                "Content" text NOT NULL DEFAULT '',
                "Audience" varchar(20) NOT NULL DEFAULT 'Internal',
                "EmbeddingJson" text NULL,
                "ContentHash" varchar(64) NOT NULL DEFAULT '',
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp NOT NULL DEFAULT now(),
                "CreatedBy" varchar(200) NULL,
                "UpdatedAt" timestamp NULL,
                "UpdatedBy" varchar(200) NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgeChunks_Key" ON "KnowledgeChunks" ("SourceKey") WHERE "CompanyId" IS NULL;""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_KnowledgeChunks_TenantKey" ON "KnowledgeChunks" ("CompanyId", "SourceKey") WHERE "CompanyId" IS NOT NULL;""",
            """CREATE INDEX IF NOT EXISTS "IX_KnowledgeChunks_Audience" ON "KnowledgeChunks" ("Audience", "IsActive") WHERE "IsDeleted" = false;""",
            // ตัวนับ rate limit ที่ใช้ร่วมกันข้าม instance (atomic upsert)
            """
            CREATE TABLE IF NOT EXISTS "ChatRateBuckets" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "BucketKey" varchar(120) NOT NULL,
                "WindowStart" timestamp NOT NULL,
                "Count" integer NOT NULL DEFAULT 0
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChatRateBuckets_Key" ON "ChatRateBuckets" ("BucketKey", "WindowStart");""",
            """CREATE INDEX IF NOT EXISTS "IX_ChatRateBuckets_Window" ON "ChatRateBuckets" ("WindowStart");""",
            // challenge (กันบอทยิงรัวซ้ำ) + คำถามที่ตอบไม่ได้ (feed หา KB gap)
            """ALTER TABLE "ChatConversations" ADD COLUMN IF NOT EXISTS "PendingChallenge" varchar(20) NULL;""",
            """ALTER TABLE "ChatMessages" ADD COLUMN IF NOT EXISTS "NoContextFound" boolean NOT NULL DEFAULT false;""",
            // Idempotency-Key ที่ใช้ร่วมกันข้าม instance — เดิมเก็บใน IMemoryCache
            // (in-process) ⇒ deploy 2 node แล้ว partner retry ไปโดนคนละ node =
            // ลงเอกสาร/รับเงินซ้ำเงียบ ๆ. "Status" = InFlight/Done ใช้กัน request
            // ที่เข้าพร้อมกันด้วยคีย์เดียวกัน (ตัวที่สองได้ 409 ไม่ใช่ยิงซ้ำ)
            """
            CREATE TABLE IF NOT EXISTS "IdempotencyRecords" (
                "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
                "CacheKey" varchar(400) NOT NULL,
                "Status" varchar(16) NOT NULL DEFAULT 'InFlight',
                "StatusCode" integer NOT NULL DEFAULT 0,
                "ContentType" varchar(200) NULL,
                "Body" bytea NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                "CompletedAt" timestamptz NULL
            );
            """,
            // tax point การนำเข้า §78/2 — วันชำระอากรขาเข้า (เดิมไม่มีที่เก็บเลย
            // ทำให้ VAT นำเข้าตกไปใช้ issueDate = เข้า ภ.พ.30 ผิดงวดได้)
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CustomsDutyPaidDate" timestamptz NULL;""",
            // ภาษีเงินได้นิติบุคคล แยกออกจาก TotalTaxWithheld (ซึ่งชื่อคือ "หัก ณ
            // ที่จ่าย") — เดิม ภ.ง.ด.50/51 ยัด CIT ลง field นั้นทำให้ยอด CIT ปนกับ
            // WHT ทุกครั้งที่รวมข้ามชนิดรายงาน
            """ALTER TABLE "TaxReports" ADD COLUMN IF NOT EXISTS "CitAmount" numeric(18,2) NULL;""",
            // backfill ข้อมูลเดิม: รายงาน CIT ที่มีอยู่ให้ CitAmount = ค่าที่เคย
            // เก็บไว้ใน TotalTaxWithheld (idempotent — รันซ้ำได้)
            // TaxType 6 = CorporateIncomeTax (ภ.ง.ด.50/51) เก็บเป็น int ในฐาน
            """
            UPDATE "TaxReports" SET "CitAmount" = "TotalTaxWithheld"
             WHERE "CitAmount" IS NULL AND "TaxType" = 6;
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_IdempotencyRecords_Key" ON "IdempotencyRecords" ("CacheKey");""",
            """CREATE INDEX IF NOT EXISTS "IX_IdempotencyRecords_CreatedAt" ON "IdempotencyRecords" ("CreatedAt");""",

            // ===== UserExternalLogins — บัญชี Google/Facebook/LINE ที่ผูกกับผู้ใช้ =====
            // เดิมเก็บใน Users.AuthProvider/AuthProviderId ซึ่งเป็นช่องเดี่ยว ⇒ ผูก
            // ได้ทีละราย (Google แล้ว LINE = ทับกันไปมา) และไม่มีที่เก็บสถานะ
            // "ยังไม่ยืนยัน" สำหรับ provider ที่ยืนยันอีเมลให้ไม่ได้ (Facebook)
            """
            CREATE TABLE IF NOT EXISTS "UserExternalLogins" (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL REFERENCES "Users"("Id") ON DELETE CASCADE,
                "Provider" varchar(32) NOT NULL,
                "ProviderUserId" varchar(256) NOT NULL,
                "ProviderEmail" varchar(256) NULL,
                "ConfirmedAt" timestamptz NULL,
                "ConfirmToken" text NULL,
                "ConfirmTokenExpiry" timestamptz NULL,
                "LinkedIp" varchar(45) NULL,
                "LastUsedAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                "UpdatedAt" timestamptz NULL,
                "CreatedBy" text NULL,
                "UpdatedBy" text NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            """,
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserExternalLogins_Provider_ProviderUserId" ON "UserExternalLogins" ("Provider", "ProviderUserId") WHERE "IsDeleted" = false;""",
            """CREATE INDEX IF NOT EXISTS "IX_UserExternalLogins_UserId" ON "UserExternalLogins" ("UserId");""",
            // ย้ายของเดิมเข้าตารางใหม่ **ครั้งเดียว** — ถือว่าที่ผูกไว้แล้วยืนยันแล้ว
            // ⚠️ DISTINCT ON + ON CONFLICT DO NOTHING: ฐานเดิมไม่เคยมี unique
            // constraint บน (AuthProvider, AuthProviderId) ⇒ อาจมีสองแถวซ้ำกัน
            // ถ้าปล่อยให้ชน partial unique index ทั้ง statement จะล้ม แล้ว catch
            // ของ loop จะกลืนทิ้ง ⇒ **ไม่มีใครถูก backfill เลยสักคน** ⇒ ผู้ใช้ SSO
            // เดิมทุกคนตกไปเส้น "หาด้วยอีเมล" (LINE ที่ไม่มีอีเมลถูกเด้งไปหน้าสมัคร)
            // และ IsDeleted=false: บัญชีที่ถูกลบ/anonymize ต้องไม่ได้ link row
            // (global query filter จะตัด User ทิ้ง ⇒ link.User = null ⇒ 500)
            // (ผู้ใช้เหล่านี้เข้าระบบด้วย provider นั้นได้อยู่ก่อนหน้า การบังคับให้
            // ยืนยันย้อนหลังคือการล็อกคนที่ใช้งานอยู่ออกจากระบบ) · idempotent:
            // NOT EXISTS กันแถวซ้ำเมื่อ migration รันทุกครั้งที่เปิดเซิร์ฟเวอร์
            """
            INSERT INTO "UserExternalLogins" ("Id","UserId","Provider","ProviderUserId","ProviderEmail","ConfirmedAt","CreatedAt","IsDeleted")
            SELECT DISTINCT ON (u."AuthProvider", u."AuthProviderId")
                   gen_random_uuid(), u."Id", u."AuthProvider", u."AuthProviderId", u."Email", now(), now(), false
              FROM "Users" u
             WHERE u."AuthProvider" IS NOT NULL AND u."AuthProviderId" IS NOT NULL
               AND u."IsDeleted" = false
             ORDER BY u."AuthProvider", u."AuthProviderId", u."CreatedAt"
            ON CONFLICT DO NOTHING;
            """,

            // หมายเหตุภายในของเอกสาร (ไม่พิมพ์ลงกระดาษ) — มีบน entity มานานแล้ว
            // แต่ไม่เคยมีบรรทัด ADD COLUMN ⇒ ฐานที่สร้างก่อนเพิ่มพร็อพเพอร์ตี้จะ
            // ไม่มีคอลัมน์นี้. ใช้เก็บ "คำเตือนที่ผู้ใช้กดรับทราบตอนอนุมัติ"
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "InternalNotes" text NULL;""",

            // โมดูลต้นทางของเอกสาร — ใช้กันนับโควตาซ้ำ (เอกสารจากโมดูลที่พักมีมิเตอร์
            // ของตัวเองคือ lodging.stay) ดู Helpers/DocumentQuotaPolicy
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "OriginModule" varchar(40) NULL;""",

            // รอบ 193 (เจ้าของข้อ 8): ผังผลต่างจากการปัดเศษ 54960 — ขา JE ของ Document.RoundingAdjustment (Helpers/DocumentRounding)
            // ใส่ให้ทุกบริษัทที่มีผังแล้ว (ไม่ผูกกับชุดผังสินค้าข้างบน — ใบบริการก็มีเศษปัดได้) · ไม่เคลมภาษีซื้อ ·
            // ON CONFLICT DO NOTHING (unique CompanyId+AccountCode) ⇒ รันซ้ำได้ ไม่ทับผังที่ลูกค้าตั้งรหัสเดียวกันไว้เอง
            """
            INSERT INTO "ChartOfAccounts"
                ("Id","CompanyId","AccountCode","AccountName","AccountNameEn","AccountType",
                 "ParentAccountId","Level","IsActive","IsSystemAccount","InputVatClaimable",
                 "CashFlowSection","CreatedAt","IsDeleted")
            SELECT gen_random_uuid(), p."CompanyId", '54960', 'ผลต่างจากการปัดเศษ', 'Rounding Difference', 5,
                   NULL, 4, true, true, false, 0, now() at time zone 'utc', false
            FROM (SELECT DISTINCT "CompanyId" FROM "ChartOfAccounts" WHERE "IsDeleted" = false) AS p
            ON CONFLICT DO NOTHING;
            """,

            // ── รอบ 193 (คำตัดสินเจ้าของข้อ 1/3/4/8) ──
            // ผลต่างจากการปัดเศษ (SubTotal = Σ บรรทัด + ค่านี้) — 0 = พฤติกรรมเดิมทุกแถว
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RoundingAdjustment" numeric(18,2) NOT NULL DEFAULT 0;""",
            // ยอดชำระจริงที่ต่างจากยอดเอกสาร (Shopee ใบกำกับ 536 · จ่าย 438) — NULL = จ่ายเต็มตามยอด (พฤติกรรมเดิม)
            """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "ActualPaidAmount" numeric(18,2) NULL;""",
            // ยอดหนี้ที่การชำระปิดด้วยบรรทัดปรับ (ไม่ใช่เงินสด) + บรรทัดปรับทั้งชุด — 0/NULL = ไม่มี (พฤติกรรมเดิม)
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "SettlementAdjustmentAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            """ALTER TABLE "Payments" ADD COLUMN IF NOT EXISTS "SettlementAdjustmentsJson" text NULL;""",
            // hash เนื้อหาเอกสารตอนลูกค้าเซ็น (R3-3) — NULL = แถวเก่า ⇒ ลายเซ็นนั้นใช้ซ้ำไม่ได้ ต้องเซ็นใหม่ (ทิศปลอดภัย)
            """ALTER TABLE "DocumentApprovals" ADD COLUMN IF NOT EXISTS "SignedContentHash" text NULL;""",

            // อัตรา/เพดานประกันสังคมมีผลเป็น "ช่วงเดือน" ไม่ใช่ทั้งปี — แถวเก่า
            // default 1–12 = ทั้งปี จึงให้ผลเหมือนเดิมทุกประการ
            """ALTER TABLE "SsoYearConfigs" ADD COLUMN IF NOT EXISTS "EffectiveFromMonth" integer NOT NULL DEFAULT 1;""",
            """ALTER TABLE "SsoYearConfigs" ADD COLUMN IF NOT EXISTS "EffectiveToMonth" integer NOT NULL DEFAULT 12;""",

            // ===== เลข placeholder รุ่นเก่าบนเอกสาร **Draft** =====
            // สองทางเข้าเคยออกเลขเองโดยไม่ผ่าน DocumentNumberGenerator
            // (CrossTenant "INV-yyyyMMdd-XXXXXX" · TimeBilling "TINV-…") ⇒ ใบพวกนี้
            // ถือเลขที่ไม่อยู่ในลำดับ gap-free §86/4 อยู่แล้ว. ต้นทางแก้เป็น
            // DRAFT-{guid} แล้ว แต่แถวเก่ายังค้าง ⇒ พออนุมัติจะได้เลขเดิมติดไป
            // (เพราะเครื่องออกเลขข้ามใบที่ "มีเลขแล้ว")
            //
            // ⚠️ แตะเฉพาะ Status = 0 (Draft) เท่านั้น — ใบที่อนุมัติแล้วห้ามเปลี่ยน
            // เลขย้อนหลังเด็ดขาด (§86/4 + เลขนั้นเข้ารายงานภาษีขายไปแล้ว)
            // ⚠️ ต้องมีตัวอักษร A-F อย่างน้อยหนึ่งตัวในหกหลักท้าย: เลขที่ระบบออกจริง
            // เป็นลำดับเลขศูนย์นำหน้า (ตัวเลขล้วน) ⇒ เงื่อนไขนี้กัน "INV-20260101-000042"
            // ของบริษัทที่ตั้งรูปแบบเลขคล้ายกันไม่ให้โดนแตะ
            """
            UPDATE "Documents"
               SET "DocumentNumber" = 'DRAFT-' || gen_random_uuid()
             WHERE "Status" = 0
               AND "IsDeleted" = false
               AND "DocumentNumber" ~ '^(INV|TINV)-[0-9]{8}-[0-9A-F]{6}$'
               AND "DocumentNumber" ~ '[A-F]{1}[0-9A-F]*$';
            """,

            // ===== Lodging — โมดูลธุรกิจที่พัก (โรงแรม/รีสอร์ท/บ้านพัก) =====
            // ตาราง Site* ในระบบเป็น EnsureCreated-only ⇒ ตารางใหม่ต้องมี CREATE TABLE
            // IF NOT EXISTS ที่นี่ ไม่งั้นฐานที่มีอยู่แล้วจะไม่มีตารางเลย (defect class
            // "entity กับ schema ไม่ sync อัตโนมัติ — ห้ามใช้ EF Migrations")
            // SQL บรรทัดเดียวจบทุก statement ตามกติกาของไฟล์นี้ (raw string หลายบรรทัด
            // เคยระเบิด CS8997)
            """CREATE TABLE IF NOT EXISTS "LodgingProperties" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "SiteId" uuid NULL, "BranchId" uuid NULL, "Name" varchar(256) NOT NULL, "NameEn" varchar(256) NULL, "Code" varchar(16) NOT NULL, "PropertyType" integer NOT NULL DEFAULT 1, "Description" text NULL, "Address" text NULL, "Phone" varchar(50) NULL, "Email" varchar(256) NULL, "LineId" varchar(100) NULL, "MapUrl" text NULL, "StarRating" integer NOT NULL DEFAULT 0, "ImagesJson" text NULL, "AmenitiesJson" text NULL, "CheckInTime" time NOT NULL DEFAULT '14:00', "CheckOutTime" time NOT NULL DEFAULT '12:00', "EarlyCheckInHours" integer NOT NULL DEFAULT 0, "EarlyCheckInFee" numeric(18,2) NOT NULL DEFAULT 0, "LateCheckOutHours" integer NOT NULL DEFAULT 0, "LateCheckOutFee" numeric(18,2) NOT NULL DEFAULT 0, "MinNights" integer NOT NULL DEFAULT 1, "MaxNights" integer NOT NULL DEFAULT 30, "MaxAdvanceDays" integer NOT NULL DEFAULT 365, "MinAdvanceHours" integer NOT NULL DEFAULT 0, "AutoConfirmOnDeposit" boolean NOT NULL DEFAULT true, "ConfirmWithoutDeposit" boolean NOT NULL DEFAULT false, "PaymentHoldMinutes" integer NOT NULL DEFAULT 1440, "OverbookingAllowance" integer NOT NULL DEFAULT 0, "ChildMaxAge" integer NOT NULL DEFAULT 11, "InfantMaxAge" integer NOT NULL DEFAULT 2, "OnlineBookingEnabled" boolean NOT NULL DEFAULT true, "RequireGuestIdNumber" boolean NOT NULL DEFAULT false, "DepositPercent" numeric(5,2) NOT NULL DEFAULT 50, "DepositFixedAmount" numeric(18,2) NULL, "DepositMinAmount" numeric(18,2) NOT NULL DEFAULT 0, "DepositMaxAmount" numeric(18,2) NULL, "DepositDeferredAccountCode" varchar(20) NULL, "DepositOutputVatDeferred" boolean NOT NULL DEFAULT false, "PricesIncludeVat" boolean NOT NULL DEFAULT true, "ChargeVat" boolean NULL, "ServiceChargePercent" numeric(5,2) NOT NULL DEFAULT 0, "RoomRevenueAccountCode" varchar(20) NULL, "ServiceChargeAccountCode" varchar(20) NULL, "CancellationFeeAccountCode" varchar(20) NULL, "WeekendMultiplier" numeric(6,3) NOT NULL DEFAULT 1, "WeekendDaysMask" integer NOT NULL DEFAULT 96, "ExtraGuestPrice" numeric(18,2) NOT NULL DEFAULT 0, "DefaultCancellationPolicyId" uuid NULL, "NoShowChargePercent" numeric(5,2) NOT NULL DEFAULT 100, "ConfirmationMessage" text NULL, "HouseRules" text NULL, "NotifyOwnerOnBooking" boolean NOT NULL DEFAULT true, "NotifyEmails" text NULL, "HousekeepingMinutesPerRoom" integer NOT NULL DEFAULT 30, "AutoCreateHousekeepingTaskOnCheckout" boolean NOT NULL DEFAULT true, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingProperties_CompanyId_SiteId" ON "LodgingProperties" ("CompanyId", "SiteId");""",
            // รอบ 158 L-02 — เว็บหนึ่งผูกที่พักได้แห่งเดียว (partial: เฉพาะแถวที่ยังอยู่และผูกเว็บ) · ถ้าฐานมี dup เดิม statement นี้ล้มแล้วถูก catch+log — ด่านในโค้ด (EnsureSiteNotBoundElsewhereAsync) ยังกันของใหม่
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_LodgingProperties_CompanyId_SiteId_Live" ON "LodgingProperties" ("CompanyId", "SiteId") WHERE "SiteId" IS NOT NULL AND "IsDeleted" = false;""",
            """CREATE TABLE IF NOT EXISTS "LodgingRoomTypes" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL REFERENCES "LodgingProperties"("Id") ON DELETE CASCADE, "Name" varchar(256) NOT NULL, "NameEn" varchar(256) NULL, "Code" varchar(32) NOT NULL, "Slug" varchar(128) NOT NULL, "Description" text NULL, "ImagesJson" text NULL, "AmenitiesJson" text NULL, "BedType" varchar(100) NULL, "SizeSqm" numeric(8,2) NULL, "ViewType" varchar(100) NULL, "StandardOccupancy" integer NOT NULL DEFAULT 2, "MaxAdults" integer NOT NULL DEFAULT 2, "MaxChildren" integer NOT NULL DEFAULT 1, "MaxOccupancy" integer NOT NULL DEFAULT 3, "AllowExtraBed" boolean NOT NULL DEFAULT false, "MaxExtraBeds" integer NOT NULL DEFAULT 0, "PricingMode" integer NOT NULL DEFAULT 1, "BaseRate" numeric(18,2) NOT NULL DEFAULT 0, "ExtraGuestPrice" numeric(18,2) NULL, "ExtraBedPrice" numeric(18,2) NULL, "MinNights" integer NULL, "IncludesBreakfast" boolean NOT NULL DEFAULT false, "ProductId" uuid NULL, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingRoomTypes_PropertyId" ON "LodgingRoomTypes" ("PropertyId");""",
            """CREATE TABLE IF NOT EXISTS "LodgingUnits" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "RoomTypeId" uuid NOT NULL REFERENCES "LodgingRoomTypes"("Id") ON DELETE CASCADE, "Number" varchar(50) NOT NULL, "Floor" varchar(50) NULL, "Building" varchar(100) NULL, "Notes" text NULL, "HousekeepingStatus" integer NOT NULL DEFAULT 1, "IsOutOfService" boolean NOT NULL DEFAULT false, "OutOfServiceUntil" timestamptz NULL, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingUnits_RoomTypeId" ON "LodgingUnits" ("RoomTypeId");""",
            """CREATE TABLE IF NOT EXISTS "LodgingRatePlans" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL REFERENCES "LodgingProperties"("Id") ON DELETE CASCADE, "RoomTypeId" uuid NULL, "Name" varchar(256) NOT NULL, "NameEn" varchar(256) NULL, "Code" varchar(32) NOT NULL, "Description" text NULL, "AdjustMode" integer NOT NULL DEFAULT 0, "AdjustValue" numeric(18,4) NOT NULL DEFAULT 0, "IncludesBreakfast" boolean NOT NULL DEFAULT false, "IsRefundable" boolean NOT NULL DEFAULT true, "CancellationPolicyId" uuid NULL, "MinNights" integer NULL, "MaxNights" integer NULL, "MinAdvanceDays" integer NULL, "MaxAdvanceDays" integer NULL, "ValidFrom" timestamptz NULL, "ValidTo" timestamptz NULL, "ApplicableDaysMask" integer NOT NULL DEFAULT 0, "DepositPercent" numeric(5,2) NULL, "IsDefault" boolean NOT NULL DEFAULT false, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "LodgingSeasons" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL REFERENCES "LodgingProperties"("Id") ON DELETE CASCADE, "Name" varchar(256) NOT NULL, "SeasonType" integer NOT NULL DEFAULT 3, "StartDate" timestamptz NOT NULL, "EndDate" timestamptz NOT NULL, "Multiplier" numeric(6,3) NOT NULL DEFAULT 1, "IsRecurringYearly" boolean NOT NULL DEFAULT false, "MinNights" integer NULL, "RoomTypeIdsJson" text NULL, "IsActive" boolean NOT NULL DEFAULT true, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "LodgingRateOverrides" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "RoomTypeId" uuid NOT NULL REFERENCES "LodgingRoomTypes"("Id") ON DELETE CASCADE, "Date" timestamptz NOT NULL, "Rate" numeric(18,2) NULL, "StopSell" boolean NOT NULL DEFAULT false, "Allotment" integer NULL, "MinNights" integer NULL, "Note" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LodgingRateOverrides_RoomType_Date" ON "LodgingRateOverrides" ("RoomTypeId", "Date") WHERE "IsDeleted" = false;""",
            """CREATE TABLE IF NOT EXISTS "LodgingCancellationPolicies" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL REFERENCES "LodgingProperties"("Id") ON DELETE CASCADE, "Name" varchar(256) NOT NULL, "Description" text NULL, "RulesJson" text NOT NULL DEFAULT '[]', "NonRefundable" boolean NOT NULL DEFAULT false, "IsDefault" boolean NOT NULL DEFAULT false, "IsActive" boolean NOT NULL DEFAULT true, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "LodgingExtras" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL REFERENCES "LodgingProperties"("Id") ON DELETE CASCADE, "Name" varchar(256) NOT NULL, "NameEn" varchar(256) NULL, "Description" text NULL, "Category" integer NOT NULL DEFAULT 99, "PriceMode" integer NOT NULL DEFAULT 1, "Price" numeric(18,2) NOT NULL DEFAULT 0, "MaxQuantity" integer NULL, "ProductId" uuid NULL, "ShowOnWebsite" boolean NOT NULL DEFAULT true, "IsActive" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "LodgingReservations" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL REFERENCES "LodgingProperties"("Id") ON DELETE RESTRICT, "SiteId" uuid NULL, "ReservationNumber" varchar(50) NOT NULL, "PublicToken" varchar(64) NOT NULL, "Status" integer NOT NULL DEFAULT 0, "Source" integer NOT NULL DEFAULT 1, "SourceReference" varchar(100) NULL, "CheckInDate" timestamptz NOT NULL, "CheckOutDate" timestamptz NOT NULL, "Nights" integer NOT NULL DEFAULT 1, "Adults" integer NOT NULL DEFAULT 2, "Children" integer NOT NULL DEFAULT 0, "Infants" integer NOT NULL DEFAULT 0, "ArrivalTime" varchar(20) NULL, "SpecialRequests" text NULL, "ContactId" uuid NULL, "SiteCustomerId" uuid NULL, "GuestName" varchar(256) NOT NULL, "GuestEmail" varchar(256) NULL, "GuestPhone" varchar(50) NULL, "GuestNationality" varchar(100) NULL, "GuestIdNumber" varchar(64) NULL, "GuestAddress" text NULL, "GuestTaxId" varchar(20) NULL, "GuestCompanyName" varchar(256) NULL, "RatePlanId" uuid NULL, "CancellationPolicyId" uuid NULL, "CancellationPolicySnapshotJson" text NULL, "PromoCode" varchar(50) NULL, "RoomSubtotal" numeric(18,2) NOT NULL DEFAULT 0, "ExtrasTotal" numeric(18,2) NOT NULL DEFAULT 0, "DiscountAmount" numeric(18,2) NOT NULL DEFAULT 0, "ServiceChargeAmount" numeric(18,2) NOT NULL DEFAULT 0, "VatAmount" numeric(18,2) NOT NULL DEFAULT 0, "TotalAmount" numeric(18,2) NOT NULL DEFAULT 0, "FolioTotal" numeric(18,2) NOT NULL DEFAULT 0, "Currency" varchar(3) NOT NULL DEFAULT 'THB', "PriceBreakdownJson" text NULL, "DepositRequired" numeric(18,2) NOT NULL DEFAULT 0, "DepositPaid" numeric(18,2) NOT NULL DEFAULT 0, "PaidAmount" numeric(18,2) NOT NULL DEFAULT 0, "HoldExpiresAt" timestamptz NULL, "DepositDocumentId" uuid NULL, "FinalDocumentId" uuid NULL, "PaymentSlipUrl" text NULL, "PaymentReference" varchar(256) NULL, "SlipUploadedAt" timestamptz NULL, "ConfirmedAt" timestamptz NULL, "CheckedInAt" timestamptz NULL, "CheckedOutAt" timestamptz NULL, "CancelledAt" timestamptz NULL, "CancellationReason" text NULL, "CancellationFee" numeric(18,2) NOT NULL DEFAULT 0, "RefundAmount" numeric(18,2) NOT NULL DEFAULT 0, "InternalNotes" text NULL, "ConfirmedBy" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LodgingReservations_Company_Number" ON "LodgingReservations" ("CompanyId", "ReservationNumber");""",
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_LodgingReservations_PublicToken" ON "LodgingReservations" ("PublicToken");""",

            // ตาข่ายชั้นสุดท้ายกันค่าเสื่อมงวดเดียวกันถูกโพสต์สองรอบ (E-04) —
            // advisory lock กันที่ชั้นตรรกะแล้ว แต่ตารางนี้ไม่เคยมี unique index
            // เลย ⇒ ถ้าล็อกพลาดด้วยเหตุใด ยอดจะเบิ้ลเงียบ ๆ โดยไม่มีอะไรฟ้อง
            """CREATE UNIQUE INDEX IF NOT EXISTS "UX_AssetDepreciations_Asset_Period" ON "AssetDepreciations" ("FixedAssetId", "Year", "Month");""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingReservations_Property_Dates" ON "LodgingReservations" ("PropertyId", "CheckInDate", "CheckOutDate", "Status");""",
            """CREATE TABLE IF NOT EXISTS "LodgingReservationRooms" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "ReservationId" uuid NOT NULL REFERENCES "LodgingReservations"("Id") ON DELETE CASCADE, "RoomTypeId" uuid NOT NULL REFERENCES "LodgingRoomTypes"("Id") ON DELETE RESTRICT, "UnitId" uuid NULL REFERENCES "LodgingUnits"("Id") ON DELETE SET NULL, "RoomTypeName" varchar(256) NOT NULL, "Adults" integer NOT NULL DEFAULT 2, "Children" integer NOT NULL DEFAULT 0, "ExtraBeds" integer NOT NULL DEFAULT 0, "NightlyRatesJson" text NULL, "Subtotal" numeric(18,2) NOT NULL DEFAULT 0, "GuestNames" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingReservationRooms_ReservationId" ON "LodgingReservationRooms" ("ReservationId");""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingReservationRooms_UnitId" ON "LodgingReservationRooms" ("UnitId");""",
            """CREATE TABLE IF NOT EXISTS "LodgingReservationExtras" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "ReservationId" uuid NOT NULL REFERENCES "LodgingReservations"("Id") ON DELETE CASCADE, "ExtraId" uuid NULL, "Name" varchar(256) NOT NULL, "PriceMode" integer NOT NULL DEFAULT 1, "UnitPrice" numeric(18,2) NOT NULL DEFAULT 0, "Quantity" integer NOT NULL DEFAULT 1, "Total" numeric(18,2) NOT NULL DEFAULT 0, "ProductId" uuid NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "LodgingFolioCharges" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "ReservationId" uuid NOT NULL REFERENCES "LodgingReservations"("Id") ON DELETE CASCADE, "ProductId" uuid NULL, "Description" varchar(500) NOT NULL, "Quantity" numeric(18,3) NOT NULL DEFAULT 1, "UnitPrice" numeric(18,2) NOT NULL DEFAULT 0, "Total" numeric(18,2) NOT NULL DEFAULT 0, "VatRate" numeric(5,2) NOT NULL DEFAULT 7, "Source" integer NOT NULL DEFAULT 1, "Status" integer NOT NULL DEFAULT 0, "ChargedAt" timestamptz NOT NULL DEFAULT now(), "PosOrderNumber" varchar(50) NULL, "Notes" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE TABLE IF NOT EXISTS "LodgingHousekeepingTasks" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL, "UnitId" uuid NOT NULL REFERENCES "LodgingUnits"("Id") ON DELETE CASCADE, "ReservationId" uuid NULL, "TaskType" integer NOT NULL DEFAULT 1, "Priority" integer NOT NULL DEFAULT 1, "Status" integer NOT NULL DEFAULT 0, "AssignedEmployeeId" uuid NULL, "AssignedToName" varchar(256) NULL, "DueAt" timestamptz NULL, "StartedAt" timestamptz NULL, "CompletedAt" timestamptz NULL, "VerifiedAt" timestamptz NULL, "EstimatedMinutes" integer NOT NULL DEFAULT 30, "Notes" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingHousekeepingTasks_Property_Status" ON "LodgingHousekeepingTasks" ("PropertyId", "Status");""",
            """CREATE TABLE IF NOT EXISTS "LodgingGuestRequests" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "PropertyId" uuid NOT NULL, "ReservationId" uuid NOT NULL REFERENCES "LodgingReservations"("Id") ON DELETE CASCADE, "RequestType" integer NOT NULL DEFAULT 99, "Details" text NOT NULL, "Status" integer NOT NULL DEFAULT 0, "ResolvedAt" timestamptz NULL, "ResolvedBy" text NULL, "ResponseNote" text NULL, "CreatedAt" timestamptz NOT NULL DEFAULT now(), "UpdatedAt" timestamptz NULL, "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false);""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingGuestRequests_Property_Status" ON "LodgingGuestRequests" ("PropertyId", "Status");""",

            // โหมดออกเอกสารของที่พัก + ธงมิเตอร์การเข้าพัก (LODGING_LICENSING_PLAN §13)
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "AccountingMode" integer NOT NULL DEFAULT 1;""",
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "AccountingModeAckAt" timestamptz NULL;""",
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "AccountingModeAckBy" text NULL;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "MeteredPeriod" varchar(7) NULL;""",
            // การตรวจสลิปของแขก (LDG-P0-02) — เดิมปฏิเสธสลิปไม่ได้เลย
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "SlipRejectedCount" integer NOT NULL DEFAULT 0;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "SlipRejectedReason" text NULL;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "SlipRejectedAt" timestamptz NULL;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "SlipUploadBlocked" boolean NOT NULL DEFAULT false;""",
            """CREATE INDEX IF NOT EXISTS "IX_LodgingReservations_Metered" ON "LodgingReservations" ("CompanyId", "MeteredPeriod");""",
            // รอบ 193 #34 — วิธีบันทึกมัดจำรายที่พัก (NULL = ตามบริษัท) + ย้ายค่าเดิม: ช่องเดิม
            // DepositOutputVatDeferred=true (พัก 21913) ⇒ VatPendingUndue (2) · **รันซ้ำได้**: แตะเฉพาะแถวที่
            // โหมดยังว่าง และหน้าตั้งค่าเขียนช่องเดิมตามโหมดทุกครั้งที่บันทึก (เลือก "ตามบริษัท" ⇒ ช่องเดิม=false)
            // ⇒ ไม่มีวันทับสิ่งที่ผู้ใช้เลือกทีหลัง · false (ค่าเดิมของทุกแถว) = NULL = พฤติกรรมเดิม (ออกใบกำกับทันที)
            """ALTER TABLE "LodgingProperties" ADD COLUMN IF NOT EXISTS "DepositVatTreatment" integer NULL;""",
            """UPDATE "LodgingProperties" SET "DepositVatTreatment" = 2 WHERE "DepositOutputVatDeferred" = true AND "DepositVatTreatment" IS NULL;""",
            // รอบ 193 F-03 — ยกเลิกแล้ว "ต้องคืน" ≠ "คืนแล้ว": ยอดที่ยืนยันว่าคืนจริง + หลักฐาน
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "RefundPaidAmount" numeric(18,2) NOT NULL DEFAULT 0;""",
            // รอบ 193 ฝ่ายค้านรอบสอง N3 — ยอดคืนบนใบมัดจำ ณ ตอนยกเลิก/เช็คเอาต์ (การคืนก่อนหน้านั้นไม่ใช่การคืนของยอดค้างนี้)
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "RefundBaselineGross" numeric(18,2) NOT NULL DEFAULT 0;""",
            // รอบ 193 ฝ่ายค้านรอบสาม R3-1 — ฐานมัดจำ "ออกใบกำกับแล้ว" ที่หักจากใบสุดท้าย แยกจากส่วนลดการค้า (เดิมรวมใน BillDiscountAmount)
            // ฝ่ายค้านรอบสี่ R4-3: สร้างคอลัมน์ + ย้ายค่าเดิม **ครั้งเดียวจริง** — ดู DepositBaseSplitMigrationSql (ห้ามมีคำสั่งย้ายค่าที่อื่น)
            DepositBaseSplitMigrationSql(),
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "RefundPaidAt" timestamptz NULL;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "RefundPaidBy" text NULL;""",
            """ALTER TABLE "LodgingReservations" ADD COLUMN IF NOT EXISTS "RefundReference" text NULL;""",
            // ข้อมูลเดิม: โค้ดก่อนรอบ 193 ลง JE คืนเงิน + ใบลดหนี้ + หัก PaidAmount **ตอนยกเลิก** โดยไม่มีหลักฐานว่าโอนคืนจริง ⇒
            // ติดป้าย legacy ใน RefundPaidBy **อย่างเดียว** — ห้ามประทับ RefundPaidAmount (= "คืนแล้ว" สถานะปลายทางที่ระบบแต่งเอง ·
            // ฝ่ายค้าน C10 · DECISION_DOCTRINE R1) ⇒ หน้าจอแสดง "ไม่มีข้อมูลการโอนคืน" (LodgingRefundState.Unknown) และห้ามกดคืนซ้ำ
            // (JE/ใบลดหนี้เดิมลงไปแล้ว) · ตัวแยก: เส้นใหม่ไม่หัก PaidAmount จนกว่าจะคืนจริง ⇒ แถวที่ PaidAmount ถูกหักแล้วคือแถวเดิม
            // · รันซ้ำได้ (RefundPaidBy IS NULL) · ป้ายต้องตรง LodgingDepositSettlement.LegacyRefundMarker
            """UPDATE "LodgingReservations" SET "RefundPaidAmount" = 0, "RefundPaidAt" = NULL WHERE "RefundPaidBy" LIKE 'legacy:posted-at-cancel%' AND "RefundPaidAmount" <> 0;""",
            """UPDATE "LodgingReservations" SET "RefundPaidBy" = 'legacy:posted-at-cancel (ก่อนรอบ 193 — ไม่มีข้อมูลการโอนคืน ระบบเดิมลงบัญชีคืนเงินตอนยกเลิก)' WHERE "Status" IN (4, 5) AND "RefundAmount" > 0 AND "RefundPaidAmount" = 0 AND "RefundPaidBy" IS NULL AND "PaidAmount" <= "DepositPaid" - "RefundAmount" + 0.005;""",

            // รอบ 194 — ประเภทเงินมัดจำ (ตาราง + คอลัมน์ใหม่ + seed idempotent) · ดู DepositKindMigrationStatements
            .. DepositKindMigrationStatements(),

            // C-T11 — ขยาย RetentionUntil ของแถวเดิมที่คำนวณจาก "วันที่เอกสาร + 5 ปี"
            // ให้เป็น "วันสิ้นรอบบัญชี + 5 ปี" (§87/3 นับจากวันยื่นแบบ · ม.10 นับจาก
            // วันสิ้นรอบ) — แถวเดิมสั้นไปเกือบ 12 เดือน (ถึง ~14 ถ้ารอบไม่ตรงปีปฏิทิน)
            // ⇒ เสี่ยงลบเอกสารที่สรรพากรยังเรียกดูได้
            //
            // ใช้ GREATEST เพื่อ **ไม่ย่นของใคร** — retention เป็น MAX ของทุกกฎที่ครอบ
            // แถวที่ยาวกว่าอยู่แล้ว (legal hold / กฎอื่น) ต้องคงไว้
            //
            // สูตรวันสิ้นรอบ: ถ้าเดือนของเอกสาร >= เดือนเริ่มรอบ → รอบจบปีถัดไป
            // (เขียนบรรทัดเดียวตามแบบของลิสต์นี้ — ห้าม raw string หลายบรรทัด CS8997)
            // ⚠️ คำสั่งนี้รัน**ทุกครั้งที่ boot** — เงื่อนไขท้าย (`RetentionUntil <` ค่าใหม่)
            // ทำให้รอบที่สองเป็นต้นไปไม่แตะแถวไหนเลย (0 rows) แทนที่จะเขียนทับทั้งตาราง
            // ซ้ำ ๆ · ถ้าไม่มีเงื่อนไขนี้ ฐานที่มีเอกสารหลักล้านใบจะเสียเวลา+WAL ทุกครั้ง
            // ที่ deploy โดยไม่ได้อะไรเพิ่ม (REV-08)
            """UPDATE "Documents" d SET "RetentionUntil" = GREATEST(d."RetentionUntil", (make_date(CASE WHEN EXTRACT(MONTH FROM d."DocumentDate") >= c."FiscalYearStartMonth" THEN EXTRACT(YEAR FROM d."DocumentDate")::int + 1 ELSE EXTRACT(YEAR FROM d."DocumentDate")::int END, c."FiscalYearStartMonth", 1) - INTERVAL '1 day' + INTERVAL '5 years')::timestamptz) FROM "Companies" c WHERE c."Id" = d."CompanyId" AND d."RetentionUntil" IS NOT NULL AND d."RetentionUntil" < (make_date(CASE WHEN EXTRACT(MONTH FROM d."DocumentDate") >= c."FiscalYearStartMonth" THEN EXTRACT(YEAR FROM d."DocumentDate")::int + 1 ELSE EXTRACT(YEAR FROM d."DocumentDate")::int END, c."FiscalYearStartMonth", 1) - INTERVAL '1 day' + INTERVAL '5 years')::timestamptz;""",

            // ── ซ่อมสแกน "สำเนา" ที่ข้อมูลหายไปแล้ว (บั๊กจริง ผู้ใช้รายงานเอง) ──
            // เส้นทาง cache ของใบซ้ำเคยคัดลอกแค่ 14 ช่องจาก 63 ช่อง ⇒ ข้อความดิบ ·
            // รายการสินค้า · ช่อง §86/4 ฝั่งผู้ซื้อ · TargetDocumentType · หมวดค่าใช้จ่าย ·
            // WHT · ค่าความมั่นใจรายช่อง หายทุกครั้งที่อัปโหลดไฟล์เดิมซ้ำ
            // (แก้ที่ Helpers/OcrScanSnapshot แล้ว — แต่ **แถวที่พังไปแล้วยังอยู่ในฐาน**
            //  และยังสร้างเอกสารที่ขาดรายการสินค้า/สกุลเงินผิดได้ต่อไป)
            //
            // COALESCE ทุกช่อง = **เติมเฉพาะที่ว่าง** ห้ามทับค่าที่ผู้ใช้แก้ไว้บนสำเนา
            // บูลีนใช้ `OR` = เปิดได้อย่างเดียว ไม่ปิดของใคร
            // ⚠️ คำสั่งนี้รันทุก boot — หลังรอบแรกทุกช่องมีค่าแล้ว UPDATE จึงเขียนค่าเดิม
            // ทับตัวเอง ไม่เปลี่ยนความหมาย และแถวเป้าหมายมีจำนวนน้อย (เฉพาะ engine='Cached')
            """UPDATE "OcrScanResults" d SET "RawTextContent" = COALESCE(d."RawTextContent", s."RawTextContent"), "ExtractedItemsJson" = COALESCE(d."ExtractedItemsJson", s."ExtractedItemsJson"), "FieldConfidenceJson" = COALESCE(d."FieldConfidenceJson", s."FieldConfidenceJson"), "ContentFingerprint" = COALESCE(d."ContentFingerprint", s."ContentFingerprint"), "ScannedDocumentType" = COALESCE(d."ScannedDocumentType", s."ScannedDocumentType"), "OurRole" = COALESCE(d."OurRole", s."OurRole"), "TargetDocumentType" = COALESCE(d."TargetDocumentType", s."TargetDocumentType"), "BuyerName" = COALESCE(d."BuyerName", s."BuyerName"), "BuyerTaxId" = COALESCE(d."BuyerTaxId", s."BuyerTaxId"), "BuyerAddress" = COALESCE(d."BuyerAddress", s."BuyerAddress"), "BuyerBranchCode" = COALESCE(d."BuyerBranchCode", s."BuyerBranchCode"), "VendorAddress" = COALESCE(d."VendorAddress", s."VendorAddress"), "VendorBranchCode" = COALESCE(d."VendorBranchCode", s."VendorBranchCode"), "ExtractedDiscountAmount" = COALESCE(d."ExtractedDiscountAmount", s."ExtractedDiscountAmount"), "ExpenseCategory" = COALESCE(d."ExpenseCategory", s."ExpenseCategory"), "SuggestedAccountsJson" = COALESCE(d."SuggestedAccountsJson", s."SuggestedAccountsJson"), "SuggestedEntryMode" = COALESCE(d."SuggestedEntryMode", s."SuggestedEntryMode"), "WhtRate" = COALESCE(d."WhtRate", s."WhtRate"), "PaymentTermsDays" = COALESCE(d."PaymentTermsDays", s."PaymentTermsDays"), "PotentialAssetLinesJson" = COALESCE(d."PotentialAssetLinesJson", s."PotentialAssetLinesJson"), "HandwritingConfidence" = COALESCE(d."HandwritingConfidence", s."HandwritingConfidence"), "GlAccountAiSuggestedCode" = COALESCE(d."GlAccountAiSuggestedCode", s."GlAccountAiSuggestedCode"), "GlAccountAiConfidence" = COALESCE(d."GlAccountAiConfidence", s."GlAccountAiConfidence"), "TargetDocTypeAiSuggested" = COALESCE(d."TargetDocTypeAiSuggested", s."TargetDocTypeAiSuggested"), "AiSuggestedContactId" = COALESCE(d."AiSuggestedContactId", s."AiSuggestedContactId"), "AiSuggestionFeedbackId" = COALESCE(d."AiSuggestionFeedbackId", s."AiSuggestionFeedbackId"), "GlAccountAiFeedbackId" = COALESCE(d."GlAccountAiFeedbackId", s."GlAccountAiFeedbackId"), "TargetDocTypeAiFeedbackId" = COALESCE(d."TargetDocTypeAiFeedbackId", s."TargetDocTypeAiFeedbackId"), "LineSplitAiFeedbackId" = COALESCE(d."LineSplitAiFeedbackId", s."LineSplitAiFeedbackId"), "OpenPoNumbersJson" = COALESCE(d."OpenPoNumbersJson", s."OpenPoNumbersJson"), "HasWht" = (d."HasWht" OR s."HasWht"), "HasPotentialFixedAsset" = (d."HasPotentialFixedAsset" OR s."HasPotentialFixedAsset"), "HasHandwriting" = (d."HasHandwriting" OR s."HasHandwriting"), "GlAccountUsedAi" = (d."GlAccountUsedAi" OR s."GlAccountUsedAi"), "TargetDocTypeUsedAi" = (d."TargetDocTypeUsedAi" OR s."TargetDocTypeUsedAi"), "LineSplitUsedAi" = (d."LineSplitUsedAi" OR s."LineSplitUsedAi") FROM "OcrScanResults" s WHERE d."OcrEngine" = 'Cached' AND d."DuplicateOfScanId" = s."Id" AND d."CompanyId" = s."CompanyId" AND ((d."RawTextContent" IS NULL AND s."RawTextContent" IS NOT NULL) OR (d."ExtractedItemsJson" IS NULL AND s."ExtractedItemsJson" IS NOT NULL) OR (d."FieldConfidenceJson" IS NULL AND s."FieldConfidenceJson" IS NOT NULL) OR (d."TargetDocumentType" IS NULL AND s."TargetDocumentType" IS NOT NULL) OR (d."BuyerTaxId" IS NULL AND s."BuyerTaxId" IS NOT NULL) OR (d."VendorBranchCode" IS NULL AND s."VendorBranchCode" IS NOT NULL) OR (d."ExpenseCategory" IS NULL AND s."ExpenseCategory" IS NOT NULL) OR (d."ContentFingerprint" IS NULL AND s."ContentFingerprint" IS NOT NULL));""",

            // ── ล้าง "โหมดออฟไลน์" ที่ประทับว่าส่งกรมสรรพากรแล้วทั้งที่ไม่เคยส่ง ──
            // เดิม SubmitToRevenueAsync ตั้ง Status=Submitted + SubmittedAt=UtcNow +
            // SubmissionId="OFFLINE-…" เมื่อยังไม่ได้ตั้งค่า RD API Key (ซึ่งเป็นค่า
            // เริ่มต้นของ appsettings ⇒ ลูกค้าทุกรายที่ยังไม่กรอกคีย์เดินเข้าเส้นนี้)
            // ⇒ `SubmittedAt != null` เป็นด่านล็อกเอกสาร 4 จุดใน DocumentService
            // ⇒ เอกสารถูกล็อก "ส่งสรรพากรแล้ว แก้ไม่ได้" ทั้งที่ RD ไม่เคยได้รับ
            // แก้โค้ดอย่างเดียวไม่พอ — แถวที่ประทับไว้แล้วยังล็อกเอกสารต่อไปตลอด
            // ลายเซ็นที่ชี้ชัดว่าไม่เคยส่งจริงคือ SubmissionId ขึ้นต้น "OFFLINE-"
            // (การส่งจริงใช้เลขที่ RD คืนมา) — คืนสถานะเป็น 1=Signed ซึ่งเป็นความจริง
            """UPDATE "EtaxInvoices" SET "Status" = 1, "SubmittedAt" = NULL, "SubmissionId" = NULL, "ErrorCode" = 'RD_API_NOT_CONFIGURED', "ErrorMessage" = 'ยังไม่ได้ตั้งค่าการเชื่อมต่อกรมสรรพากร — เอกสารลงนามแล้วแต่ยังไม่ได้นำส่ง (ล้างสถานะที่ระบบเคยประทับผิดโดยอัตโนมัติ)' WHERE "SubmissionId" LIKE 'OFFLINE-%' AND "Status" = 2;""",

            // ── ปลดคีย์ CMS ของเว็บไซต์ที่ถูกลบไปแล้ว (บั๊กจริง REF:F37BE341) ──
            // unique index ของ CMS ไม่มีตัวไหนกรอง IsDeleted แต่ Site มี global query
            // filter `!IsDeleted` ⇒ เว็บที่ลบแล้วยัง "จอง" slug/subdomain/domain ไว้
            // ทั้งที่ไม่มี query ไหนมองเห็น ⇒ ลูกค้าสร้างเว็บชื่อเดิมไม่ได้อีกเลย และ
            // ได้ 23505 เป็น 500 ที่อ่านไม่ออก (ผู้ใช้เจอกับเว็บชื่อ "b1")
            // DeleteSiteAsync ปลดให้ตั้งแต่รอบนี้แล้ว — แต่แถวที่ลบไปก่อนหน้ายังค้าง
            //
            // ต่อท้ายด้วย Id ของแถวเอง (ไม่ใช่เวลา) ⇒ ไม่ซ้ำแน่นอน และรันซ้ำได้
            // (รอบสองแถวเดิมติด NOT LIKE '%--retired-%' แล้ว จึงไม่ถูกแตะอีก)
            // left(...) ตัดหัวให้พอดีคอลัมน์: Slug 128 · Subdomain 63 · Domain 256
            // ลบด้วย 42 = ความยาวของ '--retired-' (10) + Id ที่ถอดขีดออก (32)
            """UPDATE "Sites" SET "Slug" = left("Slug", 86) || '--retired-' || replace("Id"::text, '-', '') WHERE "IsDeleted" = true AND "Slug" NOT LIKE '%--retired-%';""",
            """UPDATE "Sites" SET "Subdomain" = left("Subdomain", 21) || '--retired-' || replace("Id"::text, '-', '') WHERE "IsDeleted" = true AND "Subdomain" NOT LIKE '%--retired-%';""",
            """UPDATE "Sites" SET "CustomDomain" = left("CustomDomain", 214) || '--retired-' || replace("Id"::text, '-', '') WHERE "IsDeleted" = true AND "CustomDomain" IS NOT NULL AND "CustomDomain" NOT LIKE '%--retired-%';""",
            """UPDATE "SiteDomains" d SET "Domain" = left(d."Domain", 214) || '--retired-' || replace(d."Id"::text, '-', '') FROM "Sites" s WHERE s."Id" = d."SiteId" AND s."IsDeleted" = true AND d."Domain" NOT LIKE '%--retired-%';""",

            // ── ผังบัญชีที่ขาดหายของใบลดหนี้ฝั่งซื้อ (รอบ 185 · ผู้ใช้รายงาน) ──
            // **เพิ่มผังอย่างเดียว ไม่ย้ายยอดใด ๆ** — การย้ายยอดใน GL ต้องทำด้วย JE
            // ปรับปรุงผ่าน `ReclassifyDocumentLineAccountAsync` เท่านั้น ไม่ใช่ migration
            //
            // (1) `11520 วัสดุสิ้นเปลืองคงเหลือ` — ผังมาตรฐานไม่เคยมีผังวัสดุสิ้นเปลือง
            //     หมวดสินทรัพย์ ⇒ `InventoryControlAccount.DefaultAccountPrefix(Supplies)`
            //     (เดิม "118") ค้นเจอ **11810 เงินมัดจำ** ⇒ ซื้อวัสดุสิ้นเปลืองที่ตัดสต็อก
            //     Dr เข้าบัญชีเงินมัดจำ · ใส่ให้ทุกบริษัทที่ใช้ผังมาตรฐาน (มี 11500)
            // (2) `51150/51160` contra-purchase — เดิมอยู่เฉพาะเทมเพลตซื้อมาขายไป
            //     ⇒ บริษัทบริการ/ผลิต/ทั่วไปไม่มีผังให้ใบลดหนี้ฝั่งซื้อลงเลย จึงถูกบีบ
            //     ไปใช้ `43060 ส่วนลดรับ` ซึ่งอยู่หมวด **รายได้**
            //
            // `ON CONFLICT DO NOTHING` อาศัย unique index (CompanyId, AccountCode)
            // ⇒ รันซ้ำได้ และไม่ทับผังที่ลูกค้าสร้างรหัสเดียวกันไว้เอง
            """
            INSERT INTO "ChartOfAccounts"
                ("Id","CompanyId","AccountCode","AccountName","AccountNameEn","AccountType",
                 "ParentAccountId","Level","IsActive","IsSystemAccount","InputVatClaimable",
                 "CashFlowSection","CreatedAt","IsDeleted")
            SELECT gen_random_uuid(), p."CompanyId", v.code, v.name_th, v.name_en, v.acct_type,
                   NULL, 4, true, true, true, 0, now() at time zone 'utc', false
            FROM (VALUES
                    ('11520','วัสดุสิ้นเปลืองคงเหลือ','Supplies on Hand',1),
                    ('51150','ส่วนลดรับ (สินค้า)','Purchase Discount',5),
                    ('51160','ส่งคืนสินค้า','Purchase Returns',5)
                 ) AS v(code,name_th,name_en,acct_type)
            CROSS JOIN (
                SELECT DISTINCT "CompanyId" FROM "ChartOfAccounts"
                WHERE "AccountCode" = '11500' AND "IsDeleted" = false
            ) AS p
            ON CONFLICT DO NOTHING;
            """,
            // ชื่อ `43060` ระบุให้ชัดว่าเป็น **ส่วนลดเงินสด** เพื่อไม่ให้ถูกหยิบผิด
            // ความหมาย — แตะเฉพาะแถวที่ยังเป็นชื่อ default (ลูกค้าที่เปลี่ยนชื่อเองแล้ว
            // ไม่ถูกทับ) · **`AccountType` ไม่เปลี่ยน** เพราะการย้ายหมวดจะ re-sign
            // งบของงวดที่ปิด/ยื่นไปแล้วทั้งประวัติ
            """UPDATE "ChartOfAccounts" SET "AccountName" = 'ส่วนลดรับ (ส่วนลดเงินสด)', "AccountNameEn" = 'Cash Discounts Received' WHERE "AccountCode" = '43060' AND "AccountName" = 'ส่วนลดรับ' AND "IsDeleted" = false;""",
            // ── ซ่อมตัวนับโควตา OCR ที่สะสมข้ามเดือน (รอบ 190 · เจ้าของรายงาน "สะสมมา 3 เดือน
            //    จนเต็ม 200 แสกนต่อไม่ได้") — ต้นเหตุอยู่ที่ Helpers/SubscriptionUsageRollover ·
            //    แก้โค้ดอย่างเดียวไม่พอ (F2 ข้อ 9) เพราะวันรีเซ็ตถูกเลื่อนไป 1 ต.ค. แล้ว ⇒ ผู้ใช้จะ
            //    ติดต่ออีกหนึ่งสัปดาห์
            // เพดานที่พิสูจน์ได้จากของจริง: โควตาถูกคิด **1 ครั้งต่อการสแกน** (คืนเมื่อสแกนล้ม/ซ้ำ)
            //   ⇒ ตัวนับที่ถูกต้อง **ไม่มีทางเกิน** จำนวนแถวสแกนของเดือนนี้ (UTC — ขอบเดียวกับวันรีเซ็ต)
            //   ⇒ LEAST(ตัวนับ, จำนวนสแกนเดือนนี้) **ตัดเฉพาะส่วนเกินที่พิสูจน์ได้ว่ามาจากเดือนก่อน**
            //   ไม่เพิ่มค่าใคร ไม่แต่งตัวเลข (F2 ข้อ 3) · นับทุกแถวรวมที่ลบ/ล้มแล้ว เพื่อให้เป็นเพดานที่
            //   หลวมที่สุด (ไม่มีทางตัดต่ำกว่าการใช้จริง) · **idempotent**: ตัวนับที่ถูกอยู่แล้วไม่ขยับ
            //   จึงรันทุกครั้งที่เปิดเครื่องได้โดยไม่ต้องมีธง "รันแล้ว"
            """
            UPDATE "Subscriptions" s SET
                "CurrentMonthOcrPages"      = LEAST(s."CurrentMonthOcrPages", c.n),
                "CurrentMonthAzureOcrPages" = LEAST(s."CurrentMonthAzureOcrPages", c.n),
                "CurrentMonthLocalOcrPages" = LEAST(s."CurrentMonthLocalOcrPages", c.n)
            FROM (
                SELECT sub."Id" AS sid, COUNT(r."Id")::int AS n
                FROM "Subscriptions" sub
                LEFT JOIN "OcrScanResults" r
                       ON r."CompanyId" = sub."CompanyId"
                      AND r."CreatedAt" >= date_trunc('month', timezone('UTC', now()))
                GROUP BY sub."Id"
            ) c
            WHERE c.sid = s."Id"
              AND (s."CurrentMonthOcrPages" > c.n
                OR s."CurrentMonthAzureOcrPages" > c.n
                OR s."CurrentMonthLocalOcrPages" > c.n);
            """,
        };
    }
}
