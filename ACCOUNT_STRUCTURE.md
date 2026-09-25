# ACCOUNT_STRUCTURE.md — โครงสร้างลูกค้า / กลุ่มบริษัท / สาขา / บิลลิ่ง / ผลิตภัณฑ์ API

> **Single source of truth** ของโครงสร้าง "ใครเป็นลูกค้า ใครจ่ายเงิน ข้อมูลอยู่ที่ไหน
> ตั้งค่าอะไรได้ที่ชั้นไหน" — ครอบทั้งลูกค้า NextAcc เต็มรูป (Full) และลูกค้าที่เชื่อม
> ผ่าน API จากระบบบัญชีอื่น (Connected เช่น Microsoft Dynamics)
>
> คู่กับ: `DOCUMENT_FLOW.md` (flow เอกสาร) · `CHATBOT_PLAN.md` (chatbot)

## กฎการดูแลไฟล์นี้ (hard requirement — แบบเดียวกับ DOCUMENT_FLOW.md)

- ทุก commit ที่แตะ entity/พฤติกรรมในไฟล์นี้ (BillingAccount, AccountSubscription,
  Subscription, Company, Branch, ApiClient, UsageEvent, pricing, portal `/connect`)
  **ต้องอัปเดตไฟล์นี้ในคอมมิตเดียวกัน** — ห้ามแยก, ห้าม TODO
- ทุก section มีป้ายสถานะ: ✅ มีจริงในโค้ด · 🔨 กำลังทำ · 📋 ออกแบบแล้วยังไม่ได้ทำ
  **เปลี่ยนสถานะทันทีที่โค้ดเปลี่ยน** — ไฟล์นี้ต้องบอกความจริงเสมอ ไม่ใช่ความตั้งใจ
- ถ้าโค้ดกับ doc ไม่ตรง = **doc ผิด** แก้ doc ในคอมมิตเดียวกับงานที่ทำอยู่
- อัปเดตบรรทัดท้ายไฟล์ `Last verified …` ทุกครั้ง

---

## 1. หลักการ 3 ชั้น

```
BillingAccount  ─ ใครจ่าย (สัญญา, บิล, โควตา pool, volume tier, เครดิต)
 └─ Company     ─ นิติบุคคล = tenant (สมุดบัญชี, ภ.พ.30, งบ, e-Tax cert, PDPA)
     └─ Branch  ─ จุดปฏิบัติการ (เลขเอกสาร series, รหัสสาขา §86/4, มิติรายงาน)
```

| ชั้น | เป็นหน่วยของ | **ไม่ใช่**หน่วยของ |
| --- | --- | --- |
| BillingAccount | เงิน/สัญญา/โควตารวม/สิทธิ์บริหารกลุ่ม | ข้อมูลบัญชี (ห้ามเห็นข้ามบริษัทอัตโนมัติ) |
| Company | ข้อมูลบัญชี + ภาษีทุกอย่าง (tenant isolation ต่อ `CompanyId` ทุก query) | การจ่ายเงิน (จ่ายเองได้เป็นข้อยกเว้น — ดู §6.3) |
| Branch | เลขเอกสาร, ใบกำกับ, การยื่น VAT รายสาขา, attribution การใช้งาน | เงิน/โควตา/สิทธิ์ (ไม่มีบิลระดับสาขา ไม่มีโควตาระดับสาขา) |

**เหตุผลทางกฎหมาย**: บริษัทย่อย = นิติบุคคลแยก (เลขภาษี 13 หลักของตัวเอง งบของตัวเอง)
ส่วนสาขา = นิติบุคคลเดียวกัน เลขภาษีเดียวกัน ต่างแค่รหัส 00000/00001 สมุดบัญชีชุดเดียว —
สองอย่างนี้ห้ามออกแบบเหมือนกันเด็ดขาด

---

## 2. รูปแบบการใช้งานที่ต้องรองรับ (ทุก shape → โครงเดียวกัน)

| # | รูปแบบ | mapping | หมายเหตุ |
| --- | --- | --- | --- |
| 1 | บริษัทเดี่ยว 1 เจ้าของ | 1 Account · 1 Company · 1 Branch (00000) | เคสเล็กสุด — Account ถูกสร้างห่ออัตโนมัติ ผู้ใช้ไม่รู้สึก |
| 2 | Holding + บริษัทย่อยแนวดิ่ง | 1 Account · N Company (`ParentCompanyId` ชี้ holding) | ผังบริษัทวาดจาก ParentCompanyId; งบรวมใช้ ConsolidationGroup (แยกเรื่องกัน) |
| 3 | บริษัทพี่น้องแนวขนาน (เจ้าของเดียว) | 1 Account · N Company (ไม่มี Parent) | billing เหมือน #2 เป๊ะ — ต้นไม้เป็นแค่การแสดงผล |
| 4 | เครือสาขา (1 นิติบุคคล N จุดขาย เช่นเครือรีสอร์ต/ร้านอาหาร) | 1 Account · 1 Company · N Branch | **ไม่ใช่** N Company! เลขเอกสาร/ภ.พ.30 แยกตามสาขา แต่สมุดเดียว |
| 5 | สำนักงานบัญชีดูแลลูกค้า N ราย | 1 Account (ของสำนักงาน) · N Company (ของลูกค้าแต่ละราย) | ลูกค้าย้ายสำนักงานบัญชี = detach ไป Account ใหม่ ข้อมูลติดบริษัทไป |
| 6 | Franchise | 1 Account · N Company (franchisee ที่จดนิติบุคคลแยก) หรือ Branch (สาขาของ franchisor เอง) | เลือกตามสถานะนิติบุคคลจริง |
| 7 | **Connected** — ใช้ ERP อื่น (Dynamics/SAP) ยิง API มาใช้ฟีเจอร์ | เหมือน #1–#6 ทุกประการ แต่ `Company.CompanyKind = Connected` | ไม่มี user login เข้าหน้าบัญชี, ไม่มี UI บัญชี — จัดการผ่าน portal `/connect` + API เท่านั้น |
| 8 | ผสม (บางบริษัทในเครือใช้ NextAcc เต็ม บางบริษัทใช้ Dynamics) | 1 Account · Company ปน Full/Connected | โครงรองรับโดยไม่ต้องทำอะไรเพิ่ม — CompanyKind เป็น per-company |

**Upsell path ในตัว**: Connected → Full = พลิก `CompanyKind` (ข้อมูลอยู่ครบแล้ว) —
นี่คือเหตุผลเชิงธุรกิจหลักที่ Connected ต้องเป็น tenant จริง ไม่ใช่ระบบแยก

---

## 3. Entity (สถานะปัจจุบัน + ที่ออกแบบ)

### 3.1 มีจริงแล้ว ✅

| Entity | ไฟล์ | หมายเหตุ |
| --- | --- | --- |
| `Company` | `Models/Entities/Company.cs` | tenant; มี `BranchCode` (สำนักงานใหญ่ 00000) |
| **`Company` ↔ `CompanySettings` (แถวค่าตั้ง)** | `Helpers/CompanySettingsFactory.cs` · `Helpers/CompanyVatStatus.cs` | ✅ รอบ 193 (S-01 stopgap): แถวค่าตั้งเกิด**พร้อมบริษัท**ทุกทาง (สร้าง `CompanyService.CreateAsync` · สมัคร · SSO) + lazy-create 6 จุดเรียก `AddNewAsync` ตัวเดียว (seed `VatRegistered`/`DefaultVatRate` จาก `Company` · `new CompanySettings` นอก factory = 0 จุด — `tools/company_settings_factory_check.py`) · ธง VAT อ่านผ่าน `CompanyVatStatus.IsRegistered(companyFlag, settingsFlag?)` (มีแถว = ค่าตั้ง · ไม่มี = บริษัท · ลำดับรอ Q1) · `CompanySettings.VatStatusConfirmedAt` (วิซาร์ด = ยืนยัน · สมัคร/SSO/lazy = ยังไม่ · ประทับเฉพาะ payload มีช่อง VAT + `confirmVatStatus:true`) → แถบเตือน `layout.js` · รายงานอ่านอย่างเดียว `GET /api/companies/{id}/vat-flag-consistency` (+ `/zero-rated-tax-invoices`) สิทธิ์ `CompanySettings.Edit` · `GET /api/admin/vat-flag-consistency` (SystemAdmin · เฉพาะบริษัทที่ขัดกัน + `companiesWithoutSettingsRow`) · **ไม่ migrate** ธงที่ขัดกัน (คำตัดสิน #14 — ให้คนตัดสิน) · ไม่มีหน้าเว็บของรายงาน |
| `Branch` | `Models/Entities/DimensionalAccounting.cs:37` | `TaxBranchCode`, `IsHeadOffice`, ที่อยู่ครบ; `BranchId` ใช้บน JournalEntry/Payroll แล้ว — ทะเบียน + API + หน้าตั้งค่า ✅ (§3.1a) |
| `TaxBranchCode` (resolver) | `Helpers/TaxBranchCode.cs` | ✅ ตัวเดียวของระบบ: `TryNormalize` (เติม 0 ให้ครบ 5 หลัก) · `Label`/`LabelWithName` ("สำนักงานใหญ่" / **"สาขาที่ 00008" — 5 หลัก ตั้งแต่รอบ 193 คำตัดสิน #21**) ตามประกาศอธิบดีฯ ฉบับที่ 199 |
| `AccountSubscription` | `Models/Entities/AccountSubscription.cs` | แพลนครอบหลายบริษัท **แต่ผูก `OwnerUserId` (คน)** — จุดอ่อนที่ §8 แก้ |
| `Subscription` (ต่อบริษัท) | `Models/Entities/Subscription.cs` | ชนะ AccountSubscription เมื่อบริษัทมีของตัวเอง (resolution order §6.3) |
| `ConsolidationGroup/Member` | `DimensionalAccounting.cs:122` | งบรวม + %ถือหุ้น — **เรื่องการเงิน แยกจาก billing เด็ดขาด** |
| `ExternalIntegration` + `ApiKeyMiddleware` | `Models/Entities/`, `Middleware/` | ต้นแบบของ ApiClient (TakeTime ใช้อยู่) · ✅ **รอบ 193 (คำตัดสิน #37)**: สิทธิ์แยก `CanRead/CanWrite/CanDelete` + `IsLegacyKey`/`LegacyDeprecatesAt` · ออก/แก้/ลบคีย์ + ผูกผู้ใช้เฉพาะ Owner — §3.1c |
| `CompanyUser` | `Models/Entities/User.cs:75` | สิทธิ์ราย user รายบริษัท |
| **`BillingAccount`** | `Models/Entities/BillingAccount.cs` | ✅ องค์กรผู้จ่ายเงิน — Name/TaxId/BillingMode/PaymentModel/CreditBalance/PostpaidCreditLimit/GracePeriodDays/IsSandbox/Status |
| **`BillingAccountAdmin`** | `Models/Entities/BillingAccount.cs` | ✅ M:N account↔user + `IsPrimary`; unique ต่อ (account,user) |
| **`Company.BillingAccountId`** | `Models/Entities/Company.cs` | ✅ nullable FK (`ON DELETE SET NULL` — ลบกลุ่มแล้วบริษัทต้องไม่หาย) |
| **`Company.ParentCompanyId`** | `Models/Entities/Company.cs` | ✅ nullable self-FK (`RESTRICT`) — ผังเครือ/แสดงผลเท่านั้น ไม่มีผลต่อสิทธิ์ |
| **`Company.CompanyKind`** | `Models/Enums/AllEnums.cs` | ✅ `Full`(1, default = พฤติกรรมเดิม) \| `Connected`(2) |
| **`AccountSubscription.BillingAccountId`** | `Models/Entities/AccountSubscription.cs` | ✅ nullable — backfill 1:1 แล้ว; `OwnerUserId` เดิมยังอยู่ (เส้นทาง resolve เดิมไม่ถูกแตะ) |
| enum `BillingMode`/`PaymentModel`/`BillingAccountStatus` | `Models/Enums/AllEnums.cs` | ✅ |
| **`ApiFeature`** | `Models/Entities/Metering.cs` | ✅ แคตตาล็อกฟีเจอร์ + `UnitLabel`/`RequiredScopes`/`IsPublished`; seed 5 ฟีเจอร์ตั้งต้น |
| **`CompanyFeature`** | `Models/Entities/Metering.cs` | ✅ ลูกค้า opt-in รายบริษัท + audit `EnabledBy/At` + `AcceptedUnitPrice` (ราคาที่เห็นตอนกดเปิด) |
| **`ApiPricingPlan`** | `Models/Entities/Metering.cs` | ✅ `PricingMethod` 4 แบบ + free quota + `TierJson` + `EffectiveFrom/To` + ราคาเฉพาะกลุ่ม (`BillingAccountId`) |
| **`UsageEvent`** | `Models/Entities/Metering.cs` | ✅ append-only + snapshot ราคา + `BranchId`/`ApiClientId` + idempotency (unique index) + `BilledPeriod` |
| **`IUsageMeteringService`** | `Services/Implementations/UsageMeteringService.cs` | ✅ record/คิดราคา/หักโควตาฟรี/ตัดเครดิต · toggle ฟีเจอร์ · สรุปราย บริษัท+กลุ่ม |
| **API ฝั่ง admin** | `Controllers/MeteringAdminController.cs` | ✅ `/api/admin/metering/features`, `/plans`, `/plans/{code}` (ประวัติราคา), `/usage` |
| **API ฝั่งลูกค้า** | `Controllers/MeteringController.cs` | ✅ `/api/companies/{id}/metering/features` (เห็นราคาก่อนเปิด), toggle, `/usage`, `/usage/account` (ต้องเป็น AccountAdmin) |
| enum `PricingMethod`/`ErpConnectorType` | `Models/Enums/AllEnums.cs` | ✅ |
| **`ApiKey` ขยาย** | `Models/Entities/ApiKey.cs` | ✅ `Scopes`/`BranchId`/`BillingAccountId`/`WebhookUrl`/`WebhookSecret`/`ConnectorType`/`ConnectorConfigJson`/`IsSandbox` — **ต่อยอดตารางเดิม ไม่สร้าง `ApiClient` แข่ง** |
| **`/api/v1` base** | `Controllers/V1/PublicApiControllerBase.cs` | ✅ ด่าน 3 ชั้นรวมเมธอดเดียว (key → scope → ฟีเจอร์เปิด) + `MeterAsync` อ่าน `Idempotency-Key` header อัตโนมัติ |
| **`/api/v1/ocr`** | `Controllers/V1/OcrV1Controller.cs` | ✅ `scan` (คิดเงินหลังสำเร็จเท่านั้น) + `confirm` (ปิดลูปเรียนรู้ §7.3) |
| **`/api/v1/bank`** | `Controllers/V1/BankV1Controller.cs` | ✅ `statements` (dedupe ด้วย ExternalId, คิดตามบรรทัดที่ประมวลผล) + `matches/confirm` |
| **Portal `/connect`** | `wwwroot/connect/index.html` | ✅ ภาพรวม+ขั้นตอนเริ่มต้น · เลือกฟีเจอร์ (ยืนยันพร้อมราคา) · usage รายเดือน · ยอดรวมกลุ่ม — **ยิงเฉพาะ API สาธารณะ** |
| **Workbench** | `wwwroot/connect/workbench.html` | ✅ โต๊ะทำงานจริง: โยนเอกสาร→OCR→ตรวจ→สร้างใบสำคัญจ่าย · วาง statement (รับ พ.ศ.)→จับคู่→ยืนยัน · ผูกรหัสผู้ติดต่อ |
| **`/api/v1/contacts`** | `Controllers/V1/ContactsV1Controller.cs` | ✅ `sync` (upsert ด้วย ExternalId · ด่าน `CONTACT-TAXID-OWNED` เทียบ **เลข + สาขา** ผ่าน `ContactTaxBranchKey.Pick` — สาขา 8 ของนิติบุคคลเดียวกับ สนญ. ไม่ถูกบล็อก) · `unmapped` · `map` · `resolve` (รับ `branchCode` optional · รหัสผิดรูป = 400 · ตอบ `branchCode` + `taxIdExists` · เลขมีแต่สาขาไม่ตรง = `matched:false` + เหตุผล ไม่ถอยไปเทียบชื่อ · ผู้สมัครเทียบชื่อข้ามภาษามาจากชุด `SoftScope` เท่านั้น — เลขใหม่ + ชื่อคล้ายไม่ตอบ matched กับนิติบุคคลที่ถือเลขอื่น) — รอบ 193 |
| **`/api/v1/documents`** | `Controllers/V1/DocumentsV1Controller.cs` | ✅ สร้างเอกสาร (resolve ผู้ติดต่อ 4 ชั้น) + `approve` (ออกเลข gap-free) + คืน `contact.needsMapping` · **รอบ 193**: รับ `contactBranchCode` (ผิดรูป = 400) · เลขมีแล้วคนละสาขา ⇒ สร้างผู้ติดต่อสาขาใหม่ (ไม่เทียบชื่อ · บทบาทตามฝั่งเอกสาร) · เลขจริง + ชื่อคล้าย ⇒ ผู้ติดต่อใหม่ · ใบฝั่งขายตอบ `contact.missingBuyerFields` + `branchCode` (ตัวตรวจเดียวกับด่านอนุมัติ) · `contact.taxIdWarning` เมื่อเลข checksum ผิด (แถวใหม่ติด `[TAXID-CHECKSUM]` · ชื่อตรงตัว = ชื่อแกน + รูปนิติบุคคลเดียวกัน — ฝ่ายค้านรอบสี่ cb552889) · `approve` = พรีวิวคำเตือนไม่เรียก AI (`PreviewApprovalWarningsAsync`) → คำเตือนเป็น `[Σ-GAP]` ทั้งหมด = อนุมัติต่อแล้วคืน `scanAmountGap:true` + `warnings[]` (API ไม่ขัดจังหวะ · คำตัดสิน #12) · **รอบ 195**: VAT จากสแกนที่ไม่ได้พิมพ์บนกระดาษ ⇒ คำเตือนชุดเดียวกัน + ธงแยก `scanVatNotOnPaper:true` (`scanAmountGap` นับเฉพาะคำเตือนยอด) · ไม่ override ด่านงบ/วงเงิน/วางบิลเกิน |
| **`/api/v1/ocr/confirm`** กรองบริษัท | `Controllers/V1/OcrV1Controller.cs` | ✅ รอบ 193: กรอง `feedbackId` ให้เป็นของบริษัทผู้เรียกก่อนบันทึก (208f44d) |

**Migration + backfill** (`DatabaseMigrationHelper.cs` บล็อก "BillingAccount"): additive
ล้วน — `CREATE TABLE IF NOT EXISTS` + `ADD COLUMN IF NOT EXISTS` (nullable/มี default
ตรงพฤติกรรมเดิม) + FK ห่อ `DO $$ … pg_constraint guard` (Postgres ไม่มี
`ADD CONSTRAINT IF NOT EXISTS`) + **backfill 1:1**: ทุก `AccountSubscription` ที่ยัง
ไม่มีกลุ่ม → สร้าง `BillingAccount` (ชื่อ/อีเมลจาก owner) + ตั้ง owner เป็นผู้ดูแลหลัก
+ ผูกบริษัททุกใบใต้แพลนนั้นเข้ากลุ่ม — **ลูกค้าเก่าไม่ต้องทำอะไรและไม่รู้สึกอะไร**
รันซ้ำได้ (ทุก statement มี `IS NULL`/`NOT EXISTS` guard)

**Metering** (`Metering.cs` + `UsageMeteringService` + 2 controller): แยกจากระบบ
subscription เดิมโดยสิ้นเชิง — โควตารายเดือนของแพลน (docs/journals/OCR pages) ยังวิ่ง
ผ่าน `SubscriptionService` เส้นเดิม ส่วน `UsageEvent` เป็นการนับ **รายหน่วยเพื่อคิดเงิน
ตามการใช้จริง** ของผลิตภัณฑ์ API คนละเรื่องกัน ไม่ทับกัน

> **สถานะพฤติกรรม**: ระบบเดิมยังไม่ถูกแตะเลย — resolve โควตายังผ่าน
> `Subscription.AccountSubscriptionId` เหมือนเดิม 100% และยังไม่มี call site ไหน
> เรียก `RecordAsync` (จะต่อพร้อม `/api/v1` ในขั้นถัดไป). ฟีเจอร์ทุกตัว default
> **ปิด** → ต่อให้ต่อ endpoint แล้วก็ยังไม่มีใครถูกคิดเงินจนกว่าจะกดเปิดเอง

### 3.1c การเข้าถึงด้วย API key · งานระดับเจ้าของ · สมัครสมาชิก ✅ รอบ 193 (คำตัดสิน #37 · ทีม S/W + ฝ่ายค้าน 2 รอบ)

**คีย์ integration (`int_` — `ExternalIntegration`)**
| เรื่อง | พฤติกรรม | โค้ด |
| --- | --- | --- |
| ออก/แก้/ลบ/สร้างคีย์ใหม่ · ผูก/ลบผู้ใช้ที่คีย์สวมได้ (`user-mappings`) | **Owner/SystemAdmin ที่ล็อกอินเท่านั้น** · คำขอจาก API key ถูกปฏิเสธเสมอ (คีย์ออกคีย์ไม่ได้) · user-mapping ตรวจ `ExternalIntegrations.CompanyId` | `IntegrationController.RequireOwnerAsync` → `OwnerActionGuard.IsApiKeyRequest` |
| Account Mapping (`mappings` POST/PUT/DELETE) | `CompanySettings.Edit` (ไม่ใช่การให้สิทธิ์คีย์ จึงไม่บังคับ Owner) | `IntegrationController` |
| สิทธิ์ของคีย์ | คอลัมน์ `CanRead/CanWrite/CanDelete` · **คีย์ใหม่ = อ่านอย่างเดียว** (`ScopesForNewKey`: ไม่ส่ง ≠ "ทั้งหมด") · method → สิทธิ์ `IntegrationKeyPolicy.RequiredScope` ตัวเดียว (ฟิลเตอร์ `acc_` ใช้ตัวเดียวกัน · method ไม่รู้จัก = เขียน · fail closed) · ทั้งทาง `X-Api-Key` (middleware) และ `X-Integration-Key` (`ExternalIntegrationController : IAsyncActionFilter` → `Allows`) เดิน `EffectiveScopes` ตัวเดียว | `Helpers/IntegrationKeyPolicy.cs` |
| คีย์เดิม (legacy · TakeTime) | migration ติด `IsLegacyKey=true` + `LegacyDeprecatesAt = deploy + 90 วัน` (`LegacyGraceDays` · idempotent) · ในช่วงผ่อนผัน = สิทธิ์เต็มเหมือนเดิม · หลังวันนั้น = ค่าที่เก็บ (อ่านอย่างเดียว) ⇒ เขียน 403 พร้อมข้อความ · `LegacyDaysRemaining` (server คำนวณ · null ≠ 0) บนหน้า integrations (แดงเมื่อ ≤ 14 วัน) · **regenerate = ย้ายเข้านโยบายใหม่** (`IsLegacyKey=false` · หน้าเตือนก่อนกด) | `IntegrationService.RegenerateApiKeyAsync` |
| `X-Acting-User` | คีย์ใหม่ = แถว `IntegrationUserMapping` เท่านั้น · legacy ในช่วงผ่อนผัน = email match ได้แต่ `LogWarning` + `AuditLog` (`IntegrationActingUser` · `AddChainedAuditLog` · ครั้งเดียวต่อ (คีย์, ผู้ใช้) ต่อชั่วโมง) · response header `X-Acting-User-Resolved: mapping\|legacy-email-match\|unmapped` | `ApiKeyMiddleware` |
| หน้า `integrations.html` | ช่องสิทธิ์ในฟอร์มสร้าง/แก้ (แก้: ส่งสิทธิ์เฉพาะเมื่อแตะ) · แผงสิทธิ์ (ค่าจาก server) · ประกาศคีย์รุ่นเก่า + วันเลิกใช้ · แผงผู้ใช้ที่คีย์สวมได้ | — |

**คีย์บัญชี (`acc_` — `ApiKey`)**: สวิตช์ `CompanySettings.EnableApiAccess` **มีผลกับคีย์ที่ออกแล้ว** — ปิด = 403 ทันที (อ่าน DB ทุกคำขอ ·
ไม่มีแถว = ไม่ผ่าน · ข้อความไทยบอกทางไปต่อ) · ออกคีย์ใช้สวิตช์เดียวกัน (`ApiAccessPolicy.EvaluateAccountKey`/`CanIssueKey` ใน `ApiKeyMiddleware` +
`SettingsService.CreateApiKeyAsync`) · คีย์ `int_` ไม่ขึ้นกับสวิตช์นี้ (S-04)

**งานระดับเจ้าของ — ต้องทำโดยคนที่ล็อกอิน (ปฏิเสธ API key ทุกชนิด)**: ตัวบอก "คำขอจากคีย์" ตัวเดียว `Helpers/OwnerActionGuard`
(`Items["IsApiKeyAuth"]` **หรือ** claim `AuthMethod ∈ {ApiKey, IntegrationKey}` · `EnsureNotApiKey` → `BusinessRuleException` 403 `OWNER-ACTION-NO-API-KEY`)
- ใน service: `CompanyService.EnsureOwnerAccessAsync` (ผู้เรียก 17 จุด: ออก/เพิกถอน api-key · webhook ลงทะเบียน/แก้/ลบ/test/retry · soft-close/reopen/ปิดปี/ยอดยกมา ·
  แก้ข้อมูลบริษัท · เชิญ · เปลี่ยนบทบาท/ถอด/โอนเจ้าของ) · `RolePermissionService.EnsureOwnerAccessAsync` (role) · `OwnerConfigController` · `PermissionCatalogController`
  (template apply) · `SampleDataController` · `BulkCleanupController` (เส้นที่ตั้งใจให้คีย์เรียกยังตรวจ `IsCompanyScopedApiKey` แยก)
- attribute **`[RejectApiKey("…")]`** (`Filters/RejectApiKeyAttribute.cs` → `OwnerActionGuard.DenyResult`): `PUT settings` · โลโก้/ตรายาง POST/DELETE · number-series POST/PUT ·
  `PUT email-config` · `PUT line-config` · `PUT etax/config` · `PUT/DELETE payroll/tax-rule-config` · `PUT/DELETE payroll/sso-config` · Payment gateway Save/Test/SetMode ·
  Sensitivity `SetRule` · กฎการอนุมัติ POST/PUT/DELETE · Account Plan start-trial/attach/detach (ปิดเส้น "คีย์บริษัท A ผูก/ถอดบริษัท B ผ่าน `CompanyId` ใน body") ·
  `verify-hash-chain` · **ทำลายหลักฐาน**: ลบเอกสารถาวร (`force`) · ลบ 50 ทวิถาวร · DSR erase
- attribute **`[RequireOwner]`** (`Filters/RequireOwnerAttribute.cs` → `Helpers/OwnerGateDecision` ตัวตัดสินเดียว · คีย์/บทบาทอื่น/ไม่ใช่สมาชิก = ปฏิเสธ ·
  Owner/SystemAdmin/แอดมินแพลตฟอร์ม = ผ่าน): Payment gateway Save/Test/SetMode (บันทึกการเปิด live → `AddChainedAuditLog`) · Sensitivity `SetRule` ·
  `SubscriptionController.RequireOwnerAsync` ใช้ตัวเดียวกัน
- ด่านสิทธิ์ที่เพิ่ม: `PUT settings`/logo/stamp/number-series + **`PUT email-config`/`PUT line-config` และปุ่มทดสอบ (เดิมไม่มีด่านเลย — สมาชิกทุกบทบาทเปลี่ยน
  ผู้ส่งอีเมล/LINE ได้)** + กฎการอนุมัติ ⇒ `RequirePermission(CompanySettings.Edit)` · ข้อความ 403 ตัวเดียว `PermissionKeys.DeniedMessage(key)` (ชื่อสิทธิ์ไทย +
  คีย์ + ให้ขอเจ้าของที่ `/pages/roles.html`) · `GET settings` ส่ง `canEdit`/`editDeniedMessage` (server ตัดสิน · null = ไม่ได้ตรวจ ⇒ ไม่ล็อก)
- ล็อกจุดเรียก: `tools/owner_action_wiring_check.py` (64 แถว · ต้อง "ใช้ผล" · `--self-test` ถอดทีละแถวจากไฟล์จริง) · `tools/write_permission_gate_check.py` WATCHED
  (+Integration/Tax/Settings/Webhook/PaymentSettings/Sensitivity/Approval/EmailConfig/LineConfig/Pdpa/StatutoryRemittance/CompetitorImport/ExpenseClaim)
- ⚠️ เปลี่ยนพฤติกรรม: Accountant ที่ไม่มี `CompanySettings.Edit` บันทึกหน้าตั้งค่าไม่ได้แล้ว (คำถามเจ้าของ: ให้โดยปริยายไหม) · สคริปต์ `acc_` ที่เรียกปิดงวด/ปิดปี/
  webhook/แก้บริษัท ได้ 403 · `CompanyService.EnsureOwnerAccessAsync` ยังโยน 401 (ไม่ใช่ 403) ให้สมาชิกที่ไม่ใช่เจ้าของ (คำถามเจ้าของ) · ยังไม่ครอบ:
  `SubscriptionController` `IncrementUsage/UpdateNotificationSettings/SubmitPayment/UploadSlip/StartTrial` · `DocumentTemplateController` · `NotificationConfigController` ฯลฯ (backlog)

**Subscription (ต่อบริษัท)**: trial/extend · convert · `PUT plan` · cancel → **Owner เท่านั้น** (`RequireOwnerAsync` · คีย์ = 403 · SystemAdmin แพลตฟอร์มผ่าน) ·
เพดานวันขยาย trial ที่ server `Helpers/TrialExtensionPolicy.ResolveDays` (ขอเกิน `TrialConfig.ExtensionDays` = ปฏิเสธพร้อมบอกเพดาน ไม่ตัดเงียบ · แอดมินแพลตฟอร์ม
กำหนดเองได้ด้วย `allowCustomDays: true`)

**ปิดรับสมัคร (`SiteSettings.RegistrationEnabled`) กันที่ server** (S-08 · `Helpers/RegistrationPolicy`): `IsOpen` (ไม่มีแถว = เปิด) · `IsInvitationUsable`
(predicate เดียวกับ `AuthService.ConsumeInvitationAsync`) · `EvaluateNewAccount` (ปิด + มีคำเชิญของอีเมลนี้ = สมัครได้แต่ `MayCreateCompany=false`) ·
`EvaluateNewCompany` (แอดมินแพลตฟอร์มยังเปิดบริษัทให้ลูกค้าได้) · ทางเข้า: `AuthService.RegisterAsync` · SSO บัญชีใหม่ (ตรวจก่อนพาไปหน้าสมัคร) ·
`CompanyService.CreateAsync` (ผู้ใช้เดิมเปิดบริษัทเพิ่มไม่ได้เมื่อปิด) · `register.html` ไม่ซ่อนฟอร์มเมื่อมี `?invite=`

**ภาษีรายงานข้ามบริษัท (B-03/B-04)**: `ITaxComplianceChecker.CheckAsync(companyId, taxReportId)` ค้นผ่าน `Helpers/TaxReportTenantScope.ById` (ไม่พบ = 404) ·
`TaxController` endpoint เขียน 12 จุด ⇒ `Tax.File` (+ `Tax.Export` สำหรับ e-filing · `Tax.File` **และ** `Journal.Manage` สำหรับ unlock-filing/reject-reverse)

**อัตรา VAT รายบรรทัดของ integration** (สัญญา — `INTEGRATION_RESYNC.md` §11): `null` = ตามบริษัท · `7` · **`0` = อัตราศูนย์ §80/1 (ใบกำกับอัตรา 0 · ห้ามส่ง 0 แทนยกเว้น)** ·
**`-1` = ยกเว้น §81 (ไม่ใช่ใบกำกับ)** · อัตรา ≤ 0 ⇒ VAT 0 (`DocumentLineVatConvention.SplitLine`) · ใบ 0% เดิมที่ธงบอกไม่ใช่ใบกำกับ → รายงาน `zero-rated-tax-invoices`

### 3.1b ที่พัก (Lodging) ✅ รอบ 124 — ชั้น Company → Site/Branch → LodgingProperty

- `LodgingProperty` (`Models/Entities/Lodging.cs`) = "ที่พัก 1 แห่ง" ถือการตั้งค่าทั้งหมด (เวลาเข้า-ออก · กติกาจอง ·
  มัดจำ/VAT/service charge/ผังบัญชี · ฤดูกาล/สุดสัปดาห์ · นโยบายยกเลิก · แจ้งเตือน · แม่บ้าน) — ผูก `SiteId` (1 เว็บ : 1 ที่พัก
  ที่ active — `ResolvePropertyIdForSiteAsync` เลือกตัวแรกตาม SortOrder) และ `BranchId` (เอกสารทุกใบของที่พักออกจากสาขานั้น)
- บริษัทเดียวมีหลายที่พักได้ (`Code` ไม่ซ้ำต่อบริษัท — ใช้ในเลขจอง `RES-{Code}-…`); ที่พักไม่ผูกเว็บ = รับจองผ่าน front desk อย่างเดียว
- สร้างอัตโนมัติเมื่อ `CreateSiteAsync(IndustryType.Hotel)` (`LodgingSeeder`) — ไม่ขึ้นกับ `SeedTemplate`; idempotent ต่อ SiteId
  · **รอบ 158**: `Site.IndustryType` ถูก **เก็บลงคอลัมน์** แล้ว (เดิมรับมาใน request เพื่อ seed แล้วทิ้ง ⇒ ไม่มีใครรู้ว่าเว็บไหนเป็นที่พัก)
  และเว็บที่สร้างผิดประเภทซ่อมได้ด้วย `POST cms/sites/{siteId}/apply-template` (เติมหน้าเทมเพลต · ตัวเลือก "แทนที่หน้าที่ slug ซ้ำ"
  ซึ่งย้าย slug เดิมผ่าน `Helpers/CmsRetiredSlug` เพราะ unique index `(SiteId, Slug)` ไม่กรอง `IsDeleted` · seed ที่พัก/บริการจองให้ด้วย)
  · **เว็บ 1 : ที่พัก 1** บังคับจริงแล้ว (`EnsureSiteNotBoundElsewhereAsync` + partial unique index) — เดิมผูกซ้ำได้และ storefront หยิบตัวแรกเงียบ ๆ
- **โมดูลของเว็บ** — `Helpers/CmsModuleResolver` เป็นตัวตัดสินตัวเดียวว่าบริษัท/เว็บนี้ใช้โมดูลไหน (`orders` · `bookings` · `lodging` · `leads`)
  จากข้อเท็จจริง (ชนิดเว็บ · มีแถว order/booking-service/booking · มี `LodgingProperty` · มีเว็บ `IndustryType.Hotel`) —
  **ห้ามตัดสินจาก `SiteType` ตรง ๆ** (เว็บที่พักก็เป็น `SiteType.Booking` แต่ใช้ระบบจองห้อง ไม่ใช่จองคิวแบบ slot) ·
  ส่งออกทาง `CompanySettingsResponse.CmsModules` (แถบเมนูซ้าย: ธง `cmsModule:` ใน `Layout.navItems` — กติกาเดียวกับ `vatOnly`/`etaxOnly`
  คือ **ซ่อนเมื่อเซิร์ฟเวอร์บอกแล้วเท่านั้น** ยังไม่โหลด = แสดงไว้ก่อน) และ `SiteResponse.Modules` (แท็บ/ลิงก์ใน `cms-edit.html`)
- สิทธิ์ใหม่ใน `PermissionKeys`: `Lodging.Manage` (front desk) · `Lodging.Settings` (ตั้งค่า) — Owner/SystemAdmin ผ่านอัตโนมัติ
- ฝั่งสาธารณะ scope `CompanyId + SiteId` เสมอ · การจองเข้าถึงด้วย `PublicToken` (ไม่มี id เดาได้) · เมนู `lodging`/`lodging-settings`
  อยู่ใต้ feature `CmsWebsiteBuilder` เหมือน CMS **+ ธง `cmsModule: 'lodging'`** ⇒ ร้านอาหาร/คลินิกไม่เห็น "ตั้งค่าที่พัก" รกแถบเมนู
  (ซ่อน ≠ ห้าม — เปิดหน้าจาก URL ได้ และหน้ามี empty-state พาไปสร้าง; `roles.html` ยังติ๊กสิทธิ์ได้ครบพร้อมป้ายบอกเงื่อนไขการแสดงผล)
- **license/การคิดเงินของส่วนเสริม** 📋 ออกแบบแล้วใน `LODGING_LICENSING_PLAN.md` — ใช้ catalog `ApiFeature`/`ApiPricingPlan`/
  `CompanyFeature`/`UsageEvent` (§7) เป็น add-on catalog ทั่วไป · มิเตอร์หลัก = โควตาเอกสารของแพ็กเกจบัญชี (§5) ·
  ข้อเท็จจริงที่ต้องแก้ก่อน: `FlatMonthly` ยังไม่ถูกเก็บเงินจริง · โควตาเอกสาร hard-block เอกสารตามกฎหมาย · `/lodging` ไม่มี gate

### 3.1a ทะเบียนสาขา — เฟส 0 ✅ (ตั้งค่าเท่านั้น ยังไม่แตะเอกสาร)

**หลักการที่ห้ามหลุด: กิจการสาขาเดียวต้องไม่รู้สึกถึงความเปลี่ยนแปลงใด ๆ**
ไม่มีแถวใน `Branches` = ระบบใช้ชื่อ/ที่อยู่/`Company.BranchCode` เหมือนเดิมทุกจุด
ไม่มีที่ไหนบังคับให้สร้างสาขาก่อนถึงจะทำงานได้

| ของที่ลง | ที่อยู่ | หมายเหตุ |
| --- | --- | --- |
| CRUD ครบ | `Controllers/DimensionController.cs` (`/dimensions/branches`) | เพิ่ม `DELETE` + `?includeInactive=` |
| `BranchResponse` echo ครบทุกฟิลด์ | `Models/DTOs/Dimension/DimensionDtos.cs` | เดิมคืนแค่ 10 ช่อง ตกตำบล/อำเภอ/ไปรษณีย์/โทร/อีเมล ⇒ ฟอร์มแก้ไข prefill ไม่ได้ |
| `UpdateBranchRequest` ครอบทุกช่อง | ไฟล์เดียวกัน | เดิมแก้ที่อยู่แยกส่วน/รหัสภายใน/สถานะสำนักงานใหญ่ **ไม่ได้เลย** (กดบันทึกแล้วเงียบ) |
| ด่านความถูกต้อง | `Services/Implementations/DimensionalAccountingService.cs` | รหัสสรรพากรไม่ซ้ำ · `00000` สงวนให้สำนักงานใหญ่ · สำนักงานใหญ่มีได้แห่งเดียว (ตั้งใหม่ปลดของเดิมอัตโนมัติ) · ปิดสาขาสุดท้าย/สำนักงานใหญ่ไม่ได้ · ลบได้เฉพาะสาขาที่ยังไม่มี JE อ้างถึง (พ.ร.บ.การบัญชี ม.10) |
| ปลด gate แพ็กเกจ | `Middleware/SubscriptionMiddleware.cs` (`FeatureExemptRoutes`) | `/dimensions/branches` เป็นข้อบังคับ §86/4 ไม่ใช่ของขายเพิ่ม — ส่วน `/dimensions` (มิติ/ศูนย์ต้นทุน) ยัง gate ด้วย `CostCenter` เหมือนเดิม |
| หน้าตั้งค่า | `wwwroot/pages/dimensions.html` (แท็บสาขา) | ฟอร์มครบตาม §86/4 · ปุ่มแก้ไขดึงค่าเดิมมาเติมทุกช่อง · เห็นสาขาที่ปิดใช้งานเพื่อเปิดกลับได้ |
| เมนู | `wwwroot/js/layout.js` | เพิ่ม `branches` ในหมวด "ตั้งค่า & ผู้ใช้" → `/pages/dimensions.html?tab=branches` (**ไฟล์เดิม** — ลิงก์/บุ๊กมาร์กเก่าใช้ได้ทั้งหมด); เมนู `dimensions` เดิมเหลือเฉพาะมิติ |
| ตัวกรองสาขาในสมุดรายวัน | `Services/Implementations/AccountingService.cs` | เดิมกรองผ่าน `Branch.DimensionId` ที่ **ไม่มีโค้ดตรงไหนเซ็ตเลย** ⇒ ข้ามเงื่อนไขทั้งก้อน คืนทุกแถว (silent no-op ตั้งแต่เขียนมา); เปลี่ยนมากรอง `BranchId` บนหัว JE/บรรทัด ให้ตรงกับ GL/งบทดลอง/งบกำไรขาดทุน |

> ผลข้างเคียงที่ตั้งใจ: ตอนนี้ยังไม่มีจุดไหน **stamp** `BranchId` ลง JE (มาในเฟส 2)
> ⇒ เลือกสาขาในตัวกรองแล้วได้ผลว่าง = ถูกต้องตามข้อมูลจริง (ดีกว่าคืนทุกแถวแบบเดิม
> ซึ่งอ่านว่า "สาขานี้มีรายการทุกใบ")

### 3.1b เอกสารออกจากสาขาไหน — เฟส 1 ✅

`Document.BranchId` + `Document.IssuerBranchCode` (snapshot 5 หลักตอนอนุมัติ)
+ resolver กลาง `Helpers/DocumentIssuerBranch.cs`
→ **รายละเอียดทั้งหมดอยู่ที่ `DOCUMENT_FLOW.md` §6.2d** (flow เอกสารเป็น ground truth)

สรุปที่กระทบชั้นโครงสร้างบัญชี:

| ผลกระทบ | สถานะ |
| --- | --- |
| รหัสสาขา + ที่อยู่บนกระดาษ (HTML + QuestPDF) | ✅ ผ่าน `IssuerIdentity.BranchLabel` ตัวเดียว |
| TXID + `SellerTradeParty` ของ e-Tax XML | ✅ `ComposeTxId(taxId, สาขาที่ resolve, …)` |
| ตัวเลือกสาขาบนฟอร์มออกเอกสาร | ✅ โผล่เมื่อมีสาขาใช้งาน ≥ 2 · ค่าเริ่มต้น = ตามค่าบริษัท |
| เอกสารลูกสืบทอดสาขา (convert/clone/settlement/CN/recurring) | ✅ ครบ 5 ทาง |
| **เลขที่เอกสารแยกชุดต่อสาขา** | 📋 เฟส 2 — ติด unique constraint ระดับฐานบน `NumberSeries` |
| **JE stamp `BranchId`** → งบ/รายงานต่อสาขา | 📋 เฟส 2 |
| **รายงานภาษีซื้อ/ขาย §87 + ภ.พ.30 แยกสาขา** | 📋 เฟส 2 (`TaxReport` ก็มี unique constraint ที่ต้องขยาย) |
| ผู้ใช้ผูกสาขา · POS/API key ต่อสาขา · คลังต่อสาขา | 📋 เฟส 3 |

### 3.2 ออกแบบใหม่ (ยังไม่ทำ) 📋

```csharp
// Company เพิ่ม (ยังไม่ทำ):
//   bool VatFilingConsolidated — ยื่น ภ.พ.30 รวมสาขา (ต้องมีอนุมัติสรรพากร)

// API key ราย client — ยกระดับจาก ExternalIntegration
public class ApiClient : TenantEntity      // CompanyId = บริษัทที่ key นี้เขียน/อ่าน
{
    public Guid? BillingAccountId;         // รวมบิล/รวม dashboard ที่ account
    public Guid? BranchId;                 // scope ระดับสาขา (POS/PMS ต่อสาขา) — null = ทั้งบริษัท
    public string KeyHash;                 // เก็บ hash ไม่เก็บ plaintext
    public string Scopes;                  // "ocr:write bank:write documents:write" (space-sep)
    public int RateLimitPerMinute = 60;
    public string? IpAllowlistCsv;
    public string? WebhookUrl; public string? WebhookSecret;  // HMAC-SHA256 sign
    // ระบบบัญชีปลายทางที่ลูกค้าเลือกตอน onboard — กำหนดว่า connector ปลั๊กไหน
    // แปลง payload (GenericRest = ยิง contract กลางของเราเองตรง ๆ ไม่ต้องมีปลั๊ก)
    public ErpConnectorType ConnectorType; // GenericRest | Dynamics365 | SapB1 | Xero | Odoo | ...
    public string? ConnectorConfigJson;    // ค่าเชื่อมต่อเฉพาะ ERP นั้น (endpoint/tenant id — ไม่เก็บ secret ตรง ๆ)
    public bool IsSandbox; public bool IsActive;
}

```

หลักที่ฝังในดีไซน์:
- `UsageEvent.BillingAccountId` **stamp ตอนเกิด** ไม่ join สด — บริษัท detach ออกจากเครือแล้ว
  บิลเดือนเก่าไม่เปลี่ยน (append-only เหมือน AuditLog)
- ราคา snapshot บน event + แพลนมี `EffectiveFrom/To` — โต้แย้งบิลได้เสมอว่า ณ วันนั้นราคาเท่าไร
- นับเงินตาม **"งานที่สำเร็จ"** (ต่อเอกสาร/ต่อบรรทัด) ไม่ใช่ต่อ AI token — local model
  เก่งขึ้น → ต้นทุนเราลด → margin โตเอง (สอดคล้องกฎเหล็ก #1)

---

## 4. ตารางการตั้งค่า — อะไรตั้งได้ที่ชั้นไหน (ทุกช่องตั้งอิสระ)

| การตั้งค่า | Account | Company | Branch | ApiClient | default |
| --- | :-: | :-: | :-: | :-: | --- |
| โหมดบิล (รวมศูนย์/แยกใบ) | ✔ | override ได้ (§6.3) | — | — | Centralized |
| Prepaid/Postpaid | ✔ | — | — | — | Prepaid |
| โควตารวม (pool) | ✔ | — | — | — | ตามแพลน |
| เพดานย่อยกันกินโควตากลุ่ม | — | ✔ optional | — | — | ไม่จำกัด |
| Volume tier | ✔ (นับรวมทุกบริษัท) | — | — | — | ตามแพลน |
| Grace period | ✔ | — | — | — | 7 วัน |
| CompanyKind (Full/Connected) | — | ✔ | — | — | Full |
| ยื่น ภ.พ.30 รวม/แยกสาขา | — | ✔ (`VatFilingConsolidated`) | — | — | แยกรายสาขา (ตามกฎหมาย ยื่นรวมต้องขออนุมัติ) |
| เลขเอกสาร series | — | — | ✔ (ต่อ `CompanyId+BranchCode+TaxYear` gap-free) | — | 00000 |
| Scopes / สิทธิ์ API | — | — | — | ✔ | แคบสุด |
| Rate limit / IP allowlist | — | — | — | ✔ | 60/นาที |
| ผูก key กับสาขา | — | — | — | ✔ optional | ทั้งบริษัท |
| Webhook + secret | — | — | — | ✔ | ปิด |
| Sandbox | ✔ ทั้ง account | — | — | ✔ ราย key | ปิด |
| AI budget cap | ✔ (ต่อยอด AiBudgetGuard จาก global → ราย account) 📋 | ✔ optional | — | — | ตามแพลน |
| เปิด/ปิดฟีเจอร์ (`CompanyFeature`) | — | ✔ **ลูกค้ากดเองใน portal** | — | — | ปิดทุกตัว (opt-in) |
| วิธีคิดเงิน+ราคาต่อฟีเจอร์ (`ApiPricingPlan`) | — | — | — | — | **admin เท่านั้น** (หน้า `/admin`) |
| ระบบบัญชีที่เชื่อม (`ConnectorType`) | — | — | — | ✔ ลูกค้าเลือกตอนสร้าง key | GenericRest |
| Subdomain (`{slug}.nextacc.app`) | ✔ ตั้งเองทันที | — | — | — | auto จากชื่อ account |
| Custom domain (โดเมนตัวเอง) | ✔ + ต้อง verify DNS TXT | — | — | — | ไม่มี |
| วิธี login ที่อนุญาต (`AllowedAuthMethods`) | ✔ (เช่นบังคับ O365 อย่างเดียว) | — | — | — | ทุกวิธี |
| เปิด API Access (`EnableApiAccess` — คุมคีย์ `acc_` ที่ออกแล้วด้วย) ✅ | — | ✔ (เจ้าของ · ปฏิเสธ API key) | — | — | ไม่มีแถว = ปิด |
| สิทธิ์คีย์ integration (`CanRead/CanWrite/CanDelete`) ✅ | — | ✔ Owner เท่านั้น | — | — | อ่านอย่างเดียว (legacy = เต็มจนถึงวันเลิกใช้) |
| วิธีบันทึกเงินมัดจำ (`DepositVatTreatment`) ✅ รอบ 193 | — | ✔ (`CompanySettings` · NULL = ตามประเภทธุรกิจ) | ที่พักตั้งทับได้ (`LodgingProperty`) | — | VAT ทันที (ทุกประเภท — ไม่เปลี่ยนพฤติกรรมเดิม) · รอบ 194: = โหมดของประเภทเงินมัดจำที่ "ไม่ได้ตั้งโหมด" |
| ประเภทเงินมัดจำ (`DepositKind` — ลักษณะเงิน + วิธีบันทึก + บัญชี) ✅ รอบ 194 | — | ✔ ตาราง (ตั้งค่า → ภาษี · `GET/POST/PUT/DELETE /api/companies/{id}/deposit-kinds` + `POST …/{kindId}/default` · เขียน = สิทธิ์ `CompanySettings.Edit` + ปฏิเสธ API key · ลบประเภทที่ใบ/ที่พักใช้อยู่ = ปิดใช้แทน · ประเภทเริ่มต้นตัวเดียว (unique index) · ลักษณะ "ราคา" + เลื่อน VAT ต้องมีเหตุผล `KindProblem`) | ที่พักเลือกประเภทต่อที่พัก (`RoomDepositKindId`/`SecurityDepositKindId` — ทีม D) | คู่ค้าอ้างด้วยรหัส `depositKindCode` (integration ใบกำกับ · ไม่รู้จัก/ปิดใช้ = 400) | seed ต่อประเภทธุรกิจ (`DepositKindSeed`: ADVANCE เริ่มต้น + SECURITY · อสังหาฯ + RENT-ADV) · บริษัทเก่าที่ยังไม่มีแถว = seed ตอนเปิดรายการ |
| สถานะจด VAT (`VatRegistered` — stopgap สองธง) ✅ | — | ✔ (`Company` + `CompanySettings` · `CompanyVatStatus`) | — | — | ตามที่เลือกในวิซาร์ด (แถวค่าตั้งเกิดพร้อมบริษัท) |

---

## 5. โควตา — ลำดับ resolve (ต่อยอดของเดิม เปลี่ยนสมอจากคนเป็นองค์กร)

```
1) Company มี Subscription ของตัวเอง (Active)     → ใช้อันนั้น (ชนะเสมอ — ของเดิม)
2) Company.BillingAccountId → AccountSubscription  → โควตา pool ของกลุ่ม
3) ไม่มีทั้งคู่                                     → auto-trial ต่อบริษัท (ของเดิม)
```

- Pool นับ**รวมทุกบริษัทใต้ account** (ปรัชญาเดิมของ AccountSubscription — ถูกแล้ว)
- เพดานย่อยต่อบริษัท (optional): เตือนที่ 80% → block ที่ 100% → AccountAdmin ปลดได้เอง
- สาขา**ไม่มีโควตา** — คุมพฤติกรรมด้วย rate limit ของ ApiClient ที่ผูกสาขาแทน
- **พื้นที่เก็บไฟล์ (รอบ 193 · คำตัดสิน #30 · ทีม U2)** ✅ — คิดจาก**ของจริง** Σ `FileAttachments.FileSize` + สื่อ CMS ทุกจุด ผ่าน
  `ISubscriptionService.GetStorageStatusAsync` (สูตรเดียวกับ `CanFitStorageAsync`) / `GetStorageBytesByCompanyAsync`: aggregate ของกลุ่ม · trial ·
  รายบริษัท (`AccountSubscriptionController` · `AdminAccountSubscriptionController`) · `CheckUsageLimitAsync("storage")` — ชื่อช่อง JSON `currentStorageUsed` คงเดิม ·
  **`Subscription.CurrentStorageUsed` ไม่มีผู้เขียนและไม่มีผู้อ่านแล้ว** (เดิมผู้อ่าน 5 จุดได้ 0 เงียบ ⇒ หน้าแพ็กเกจโชว์ 0 และด่าน storage ผ่านเสมอ) ·
  **ไฟล์แนบเกินเพดาน = บันทึกและเตือน** (`storageWarning` · `Helpers/AttachmentStorageNotice` Near ≥90%/Over พร้อม MB · ไม่รู้เพดาน = ไม่เตือน) · **CMS ยังบล็อก** ·
  ไฟล์ที่ soft-delete แต่เก็บไฟล์จริง (`AttachmentRetention`) ไม่ถูกนับ (คำถามเจ้าของ)

### 5.1 โควตาของเว็บ CMS (หน้า · สินค้า · พื้นที่) ✅ *(บังคับจริงตั้งแต่รอบ 159)*

เพดานของ **เว็บ** อยู่บน `Site` เอง (`MaxPages` · `MaxProducts` · `MaxStorageBytes`)
แยกจากโควตาเอกสารของแพ็กเกจบัญชี (§5 ข้างบน) — คนละมิเตอร์ คนละตัวนับ

| เพดาน | ตัวนับ/ด่าน | บังคับที่ |
| --- | --- | --- |
| `MaxPages` | `ICmsQuotaService.PageUsageAsync` → `CmsQuotaUsage.BlockReason()` | `CmsContentService.CreatePageAsync` · `CmsSiteService.ApplyTemplateCoreAsync` (นับทีละหลายหน้า) |
| `MaxProducts` | `ProductUsageAsync` | `CmsCommerceService.AddProductAsync` (โยน) · auto-publish ตอนสร้างเว็บ (**ตัดให้พอดี ไม่โยน**) |
| `MaxStorageBytes` | `ISubscriptionService.CanFitStorageAsync` (pool ของ License · รอบ 193 เรียก `GetStorageStatusAsync` = Σ ไฟล์แนบ + สื่อ CMS) | `CmsContentService.UploadMediaAsync` — ยังบล็อก (ไฟล์แนบบัญชีแค่เตือน §5) |

- **ตัวนับตัวเดียว**: `GetQuotaStatusAsync` (ตัวเลขบนหน้าจอ) และด่านทั้งหมดอ่านจาก
  `PageUsageAsync`/`ProductUsageAsync` ตัวเดียวกัน — ห้ามจุดไหนนับเอง ไม่งั้นจอบอก
  "ยังเหลือ" แต่กดแล้วถูกปฏิเสธ
- หน้าที่ถูก soft-delete **ไม่กินโควตา** (global query filter `!IsDeleted` ของ `SitePage`)
- ข้อความปฏิเสธต้องบอก **ใช้ไป/เพดาน/จำนวนที่ต้องการ/ทางไปต่อ** เสมอ (ลบของที่ไม่ใช้ หรืออัปเกรด)
- *ก่อนรอบ 159*: เมธอด `CanAddPageAsync`/`CanAddProductAsync` มีอยู่แต่ **ไม่มีใครเรียก** ⇒
  เพดานทั้งสองเป็นแค่ตัวเลขบนหน้าจอ ลูกค้าสร้างเกินได้ไม่จำกัด

---

## 6. Billing

### 6.1 สองโหมด (ตั้งที่ Account)

**Centralized (default)** — ใบแจ้งหนี้ + ใบกำกับภาษี **1 ใบ/เดือน** ออกให้นิติบุคคลตาม
`BillingAccount.TaxId` โดย**แยกบรรทัดต่อบริษัท** (นักบัญชีกลุ่มใช้ charge back ภายใน):

```
ใบกำกับภาษี → บจก.โฮลดิ้ง
  ค่าบริการ OCR — บจก.ย่อย B (120 เอกสาร)   1,200.-
  ค่าบริการ OCR — บจก.ย่อย C (45 เอกสาร)      450.-
  Bank Recon — บจก.ย่อย B (300 บรรทัด)        300.-
  รวม + VAT 7%                              2,086.50
```
- สัญญาต้องระบุชัดว่า holding เป็นคู่สัญญาผู้ซื้อบริการแทนกลุ่ม
- หัก ณ ที่จ่าย 3% มาจากผู้จ่ายใบเดียว → 50 ทวิใบเดียว

**PerCompany** — ออกใบกำกับ N ใบให้แต่ละนิติบุคคลตามการใช้จริง (ผู้สอบบัญชีบางเจ้าขอ
ให้ต้นทุนลงตรงนิติบุคคล) — portal ยังเห็น roll-up รวม; แลกกับตามเก็บ N ทาง + 50 ทวิ N ใบ

ทั้งสองโหมด gen ผ่าน **pipeline เอกสารของ NextAcc เอง** (บริษัทผู้ให้บริการ = tenant
ของตัวเอง) → ได้เลข gap-free + e-Tax + VAT 7% ครบโดยไม่เขียนระบบบิลใหม่

**สถานะ: ✅ ลงโค้ดแล้ว** (`PlatformBillingDocumentIssuer`) — ตั้งค่าที่
**Admin → ตั้งค่าเว็บไซต์ → แท็บ "ข้อมูลผู้ขาย" → เลือก "บริษัทผู้ให้บริการ"**
(เว้นว่าง = โหมดเดิม PDF ใบเดี่ยว ซึ่ง **ไม่ลงบัญชีและไม่เข้ารายงานภาษีขาย**)

| จังหวะ | เอกสารที่ออกใน tenant ผู้ให้บริการ | ผลทางบัญชี |
| --- | --- | --- |
| อนุมัติการชำระ / บันทึกรับเงิน manual | จด VAT → **ใบกำกับภาษี/ใบเสร็จรับเงิน** (ขายเงินสด `IssuedAsCashReceipt`, e-Tax T03) · ไม่จด VAT → **ใบเสร็จรับเงิน** | `Dr เงินสด/ธนาคาร` + `Dr 11910` (ถ้าถูกหัก ณ ที่จ่าย) / `Cr รายได้ 41000` + `Cr ภาษีขาย 21911` → เข้ารายงานภาษีขาย/ภ.พ.30 |
| ออกใบแจ้งหนี้ต่ออายุล่วงหน้า (WP-B1) | **ใบแจ้งหนี้** (มีวันครบกำหนด = วันหมดอายุรอบเดิม) | `Dr ลูกหนี้` / `Cr รายได้` — ตามเก็บได้จริงในงบของเรา |

**รายละเอียดที่ต้องรู้:**
- **ยอดบนเอกสาร = เงินที่รับจริง + ภาษีที่ลูกค้าหัก ณ ที่จ่าย** — ช่อง
  "ภาษีถูกหัก ณ ที่จ่าย" บนฟอร์มบันทึกรับเงิน (`SubscriptionPayment.WithholdingTaxAmount`).
  ไม่กรอก = เอกสารจะมีแต่ยอดสุทธิ ⇒ รายได้ต่ำไป + เสียเครดิตภาษี 3% (บั๊กแบบเดียว
  กับที่เจอฝั่งเอกสารลูกค้า). อัตรา WHT บนบรรทัดคำนวณกลับจากยอดจริง → ยอดหักตรงเป๊ะ
  ไม่เพี้ยนจากการปัด 3.00%
- **ราคารวม VAT แล้ว** (`PlatformPriceIncludesVat`) → ถอด VAT ออกก่อนเป็นฐาน ex-VAT
  (บรรทัดเอกสารเก็บราคา ex-VAT)
- **ผู้ติดต่อ** ถูกสร้าง/จับคู่ใน tenant ผู้ให้บริการโดยยึด **เลขผู้เสียภาษี** ก่อนชื่อ
  (`ExternalSystem=NextAccTenant`, `ExternalId=<CompanyId>` ตามรอยกลับได้) — กันผู้ติดต่อ
  ซ้ำทุกครั้งที่ลูกค้าจ่ายเงิน ซึ่งจะทำให้รายงานยอดขายรายลูกค้าของเราแตกเป็นหลายราย
- **ล้มเหลว = ตกกลับโหมด PDF เดิมเสมอ ไม่ทำให้การอนุมัติเงินพัง** (เงินเข้าแล้วต้อง
  บันทึกได้) — ดู log `ออกเอกสารจริงไม่สำเร็จ … ใช้ PDF เดี่ยวแทน`
- **ดาวน์โหลดใบเสร็จ** เรนเดอร์จากเอกสารจริงสด ๆ (ได้เทมเพลต/ลายเซ็น/ตราประทับชุด
  เดียวกับเอกสารอื่น และสะท้อนการแก้ไขล่าสุด) ไม่ใช่สำเนาที่แนบค้างไว้
- **ยกเว้นค่าบริการ (0 บาท)** ไม่ออกเอกสาร — ไม่มีรายได้ให้บันทึก

#### 6.1a ปิดรอบบิลค่าใช้งาน — `UsageInvoicingJob` ✅

`UsageEvent` มีช่อง `BilledPeriod` / `BilledDocumentId` มาตั้งแต่ต้น แต่ **ไม่เคยมีใคร
เขียนสองช่องนั้น** ⇒ ค่าใช้งานที่คิดได้ค้างในตารางตลอดกาล ไม่เคยกลายเป็นใบแจ้งหนี้
(defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"). งาน `UsageInvoicingJob`
(`Services/Background/UsageInvoicingJob.cs`, รอบ 6 ชม.) ปิดช่องนี้:

| ขั้น | พฤติกรรม |
| --- | --- |
| ขอบเขต | `BilledPeriod IS NULL` · ไม่ใช่ sandbox · `OccurredAt` อยู่ใน **งวดที่ปิดแล้ว** (ก่อนต้นเดือนนี้ตามปฏิทินไทย) |
| จัดกลุ่ม | ต่อ (บริษัท, งวด) → 1 ใบแจ้งหนี้ · **แยกบรรทัดต่อ `FeatureCode`** (ชื่อจาก `ApiFeature.Name` ไม่ใช่รหัสดิบ) |
| ออกเอกสาร | `IPlatformBillingDocumentIssuer.IssueUsageInvoiceAsync` (ใหม่) — ใบแจ้งหนี้หลายบรรทัด ครบกำหนด +7 วัน อ้างอิง `USAGE-{งวด}-{8 ตัวแรกของ CompanyId}` |
| Prepaid | **ไม่ออกใบ** (เครดิตถูกตัดตอน `RecordAsync` แล้ว — ออกอีกใบ = เก็บสองรอบ) แต่ยัง **ตีตรา `BilledPeriod`** เพื่อไม่ให้วนอ่านแถวเดิมตลอดไป · `BilledDocumentId = null` แปลว่า "ปิดรอบแล้วโดยไม่มีใบ" |
| ยอดต่ำกว่า ฿50 | **ไม่ตีตรา** — ยกยอดไปรวมงวดถัดไป (ไม่ใช่การยกเลิกหนี้) |
| ออกใบไม่สำเร็จ | **ไม่ตีตรา** ปล่อยให้รอบหน้าลองใหม่ — ตีตราแล้วรายได้หายถาวรโดยไม่มีใครรู้ |
| ยังไม่ตั้ง tenant ผู้ให้บริการ | ข้ามทั้งรอบ ไม่แตะข้อมูล (ตั้งค่าเสร็จเมื่อไรก็เก็บย้อนหลังได้) |

ล็อกด้วย `AdvisoryLockKey.UsageInvoicing` ระดับระบบ (ไม่ผูกบริษัท) ใน transaction จริง
— งานเดินทีเดียวทุก tenant จึงต้องกันหลาย instance ปิดรอบเดียวกันพร้อมกัน

#### 6.1b ส่วนเสริม (add-on) และโควตาเอกสาร ✅

ความสามารถที่ขายแยกจากแพ็กเกจเดินผ่าน `CompanyFeature` (string code ใน
`Models/Constants/AddOnCodes.cs`) ไม่ใช่ `FeatureFlags` bitmask (ใช้ไปถึงบิต 47/64 แล้ว)

| ชั้น | ที่อยู่ | หน้าที่ |
| --- | --- | --- |
| แคตตาล็อก + ราคา | `ApiFeature` / `ApiPricingPlan` · หน้า `/pages/admin-addons.html` (SystemAdmin) | ตั้งราคา · วันทดลองใช้ · แพ็กเกจขั้นต่ำ · เปิด/ปิดการขาย — **ตั้งราคาใหม่ไม่แก้ราคาเดิม** (ปิดแผนเก่า เปิดแผนใหม่) |
| สวิตช์ของลูกค้า | `CompanyFeature` · หน้า `/pages/addons.html` | เปิด/ปิดเอง · ติ๊กยอมรับค่าใช้จ่ายแยกจากปุ่มเปิด · ปิดแล้วหยุดคิดเงินทันที |
| ตัวตัดสินสิทธิ์ | `IEntitlementService.CheckAsync` (เซิร์ฟเวอร์) · `Layout.hasFeature` (หน้าเว็บ) | ชื่อที่มีจุด = add-on code · ไม่มีจุด = ความสามารถของแพ็กเกจ — **ตัวเดียวตอบทั้งสองแกน** |
| ค่าเหมารายเดือน | `AddOnMonthlyBillingJob` + `AddOnBilling.FlatAmount` | ทดลองใช้/ของแถม = ฿0 **แต่ยังบันทึกแถว** ให้เห็นในบิล |

`SubscriptionResponse.EnabledAddOnCodes` เดินทางมากับแพ็กเกจ เพื่อไม่ให้หน้าเว็บต้อง
ยิง endpoint ที่สองแล้วตัดสินสิทธิ์เอง (= resolver ตัวที่สอง)

**โควตาเอกสาร** (`IQuotaService` + `Helpers/DocumentQuotaPolicy.cs`):
- `GET /api/companies/{id}/metering/quota` — used / planLimit / bonus / **warnLevel** /
  วันที่คาดว่าจะเต็ม / ยอดส่วนเกินเดือนนี้ · **เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดงอย่างเดียว**
  (`Layout.showQuotaBanner()` ใช้ในหน้าเอกสาร · ที่พัก · POS)
- `POST …/quota/topup` — ซื้อโควตาเพิ่ม 100 ฉบับ/แพ็ก (อายุ 60 วัน) ผ่าน `documents.topup`
- `POST …/quota/rewards/{id}/claim` — ภารกิจแลกโควตา (§12 ของ LODGING_LICENSING_PLAN),
  สวิตช์ 3 ชั้น: ไม่มี option ที่ `IsActive` · `PlanTemplate.AllowQuotaReward` ·
  `Subscription.QuotaRewardBlocked`
- โบนัสถูก **หักทีละใบ** ตอนออกเอกสารที่เกินโควตาแพ็กเกจ (`IncrementUsageAsync`) —
  ถ้าไม่หัก ผู้ที่ซื้อครั้งเดียวจะได้โควตาเพิ่มทุกเดือนจนหมดอายุ
- มิเตอร์ของระบบ (`AddOnCodes.SystemMeters`) **ไม่ต้องให้ลูกค้าเปิด** — `RecordAsync`
  ข้ามด่าน "ฟีเจอร์เปิดอยู่ไหม" ให้รหัสกลุ่มนี้ ไม่งั้นค่าส่วนเกินจะบันทึกไม่ได้เลย

**สิ่งที่ตัดสินโควตา ห้ามให้ client ส่งมาเอง** — `OriginModule` (โมดูลต้นทางของเอกสาร)
เคยอยู่ใน `CreateDocumentRequest` ⇒ ผู้เรียก REST API ส่ง `"originModule":"Lodging"`
มาทุกใบก็ไม่กินโควตาเลยตลอดกาล. ตอนนี้เป็น **พารามิเตอร์ของเมธอด**
`IDocumentService.CreateDocumentAsync(..., originModule)` ซึ่ง model binding เอื้อมไม่ถึง
โดยโครงสร้าง (ปลอดภัยกว่าให้ controller ล้างเอง ซึ่งวันหนึ่งจะมีตัวใหม่ที่ลืมล้าง)

**สวิตช์ของภารกิจแลกโควตา (§12) ตั้งได้จริงแล้วทั้ง 3 ชั้น** — `/api/admin/metering/quota-rewards`
(สร้าง/แก้/เปิด-ปิดภารกิจ + ยอด 30 วันล่าสุดไว้เทียบว่าคุ้ม lead ไหม) ·
`quota-rewards/plan` (ต่อแพ็กเกจ) · `quota-rewards/block-company` (ระงับรายบริษัท + AuditLog)
ทั้งหมดอยู่ในหน้า `/pages/admin-addons.html` — ก่อนหน้านี้กลไกครบแต่**ไม่มีทางเปิดใช้เลย**

**ขายเฉพาะของที่มีจริง** — `lodging.promo` ถูก unpublish แล้ว: `LodgingReservation.PromoCode`
เก็บเป็นข้อความเฉย ๆ `LodgingPricingEngine` ไม่เคยอ่านมาคิดส่วนลด ⇒ เปิดขายไปก็ไม่ได้อะไร

#### 6.1c แคตตาล็อกฟีเจอร์ + หน้าที่แสดงแพ็กเกจ — อ่านจากที่แอดมินตั้งเท่านั้น ✅ (2026-09-10)

| ชั้น | ตัวเดียวของระบบ | หมายเหตุ |
| --- | --- | --- |
| ชื่อ/ป้าย/หมวด/คำอธิบาย/ชุดสำเร็จรูปของฟีเจอร์ | `Helpers/FeatureCatalog.cs` → `GET /api/subscription/feature-catalog` (สาธารณะ) | ทุกบิตใน `FeatureFlags` ต้องมี metadata (เทสต์ `FeatureCatalogTests`) · preset คำนวณจาก enum combo ไม่พิมพ์ซ้ำ · `FeatureFlagsHelper` แค่มอบต่อ |
| แพ็กเกจไหนเปิดอะไร · ลิมิต · เงื่อนไขทดลอง | `PlanTemplate` ที่แอดมินตั้ง → `GET /api/subscription/plans` (`PlanTemplateResponse` ส่ง `TrialGracePeriodDays`/`TrialBlockOnExpiry`/`TrialMax*` ด้วยแล้ว) | หน้าเว็บ**แสดง**อย่างเดียว |
| วิธีแสดง (ไม่จำกัด · พื้นที่ · ขยายทดลอง · บรรทัดฟีเจอร์บนการ์ด) | `wwwroot/js/plan-display.js` (`PlanDisplay`) | ใช้ร่วมกันโดย `index.html` · `register.html` · `pages/subscription.html` — เกณฑ์ "ไม่จำกัด" (999/99/99999) อยู่ที่นี่ที่เดียว |
| ชื่อแพ็กเกจที่ผู้ใช้เห็น | `SubscriptionResponse.PlanName` · `DashboardSubscriptionSummary.PlanName` · `AdminLayout.loadPlanNames()` | เดิม map `Basic→"Starter"` พิมพ์ซ้ำใน 5 หน้า และ `app.html` ใช้คีย์ที่ไม่ตรง enum เลย |

ที่มา: ผู้ใช้รายงานว่าตารางเปรียบเทียบฟีเจอร์บนหน้าแรกไม่ตรงกับที่ติ๊กในหน้าแพ็กเกจของแอดมิน —
ตารางเป็น HTML ตายตัว 40 แถว · การ์ดสมัครพิมพ์ "ทดลอง 14 วัน" · `subscription.html` พิมพ์ "+7 วัน"
3 จุดและ**ส่ง `additionalDays: 7` ทับค่าแพ็กเกจ** · `settings-features.html` มีตารางป้าย 45 แถวที่ตก
`CmsBooking` · `admin/plans.html` มี preset มือที่ตก `EtaxByEmail` · `admin-addons.html` มีรายชื่อแพ็กเกจ
`['FreeTrial','Starter','Standard','Professional','Enterprise']` ซึ่ง 3 ใน 5 ไม่ใช่ชื่อ enum ⇒
"แพ็กเกจขั้นต่ำ" ของ add-on **ไม่เคยบล็อก** (`MinPlanBlocked` parse ไม่ได้ = ขายทุกแพ็กเกจ) และตั้ง
โบนัสโควตาต่อแพ็กเกจแล้ว 404. กติกา: ค่าที่มีที่ให้ตั้งในระบบ ห้ามมีสำเนาบนหน้าใด ๆ · API ไม่ตอบ =
ซ่อนส่วนนั้น/บอกว่าโหลดไม่ได้ ไม่ตกไปใช้ตารางราคาสำรองที่พิมพ์ไว้ (เดิม `renderFallback` โชว์ราคาเก่า)

### 6.2 Prepaid (default) / Postpaid

- **Prepaid**: ซื้อแพ็กเครดิตล่วงหน้า → `CreditBalance` ตัดตาม UsageEvent → เตือน 20%/หมด
  → หมดแล้ว block งานใหม่ (งานค้างคิวทำต่อจนจบ) — เหมาะตลาดไทย ตัดปัญหาตามเก็บเงิน
- **Postpaid**: ใช้ก่อนจ่ายทีหลัง สิ้นเดือนออกใบแจ้งหนี้ — เฉพาะลูกค้าใหญ่ที่มีสัญญา + วงเงิน
- ซื้อเครดิต = ออก "ใบเสร็จรับเงิน (มัดจำ)" ตาม flow เงินมัดจำที่มีอยู่ (deferred VAT
  ตามเคสที่ระบบรองรับแล้ว — ดู DOCUMENT_FLOW.md §2.3a)

### 6.3 Edge cases (ตัดสินใจแล้ว)

| เคส | นโยบาย |
| --- | --- |
| ขายบริษัทออกจากเครือ | detach: `BillingAccountId = null` → ได้ subscription ตัวเอง; **usage เดือนเก่าอยู่บิลกลุ่มเดิม** (stamp ไว้แล้ว ไม่ย้ายย้อน) |
| เข้ากลุ่มกลางเดือน | usage นับเข้าบิลกลุ่มตั้งแต่วัน attach — ไม่ prorate ย้อน |
| กลุ่มค้างจ่าย | ระงับทั้งกลุ่มหลัง grace 7 วัน — แจ้งเตือน**ทุกบริษัทย่อย** ไม่ใช่แค่ holding (คนทำงานจริงอยู่ที่ย่อย) |
| ย่อยขอจ่ายเอง | มีอยู่แล้วใน resolution order — company subscription ชนะ account |
| บริษัทอยู่ 2 กลุ่ม | **ไม่อนุญาต** — FK เดี่ยว (หลายกลุ่ม = โควตา/บิลกำกวม) |
| retry ยิง API ซ้ำ | `IdempotencyKey` unique ต่อ client → event เดียว เก็บเงินครั้งเดียว |

---

## 7. ผลิตภัณฑ์ Connected (API) — สรุปสถาปัตยกรรม

- **ไม่แยกระบบ** — เครื่องยนต์ (OCR/matching/tax/AI) + ข้อมูล + billing อยู่ใน NextAcc;
  แยกเมื่อไรได้ที่ระดับ **deployment** (โค้ดเดิม อีก instance เปิดเฉพาะ `/api/v1`)
  ไม่ใช่ระดับ codebase. ต้นแบบที่พิสูจน์แล้ว: TakeTime integration
- **`/api/v1/*`** — versioned public API: `ocr/scan`, `bank/statements`, `documents`,
  webhooks; งานช้าเป็น async (รับงานคืน `jobId` → แจ้งผลทาง webhook) ห้ามให้ลูกค้า polling
- **Connector ราย ERP** (`IErpConnector`) เป็นปลั๊กแปลง format เท่านั้น — แกนรู้จักแค่ DTO
  ของเรา; เริ่มจาก "ลูกค้ายิงหาเราเอง" ก่อน ค่อยสร้าง connector สำเร็จรูปเมื่อมี demand จริง
- **เอกสารจริงอยู่ระบบไหน** — ต่อ 1 Company เลือกได้อย่างเดียว: NextAcc เป็นตัวจริง
  (เราออกเลข gap-free/ยื่นภาษี) หรือ ERP เขาเป็นหลัก (เราเป็นเงา ประมวลผลอย่างเดียว)
  — ปนกัน = เลขเอกสาร/ภ.พ.30 ชนแน่นอน
- **PDPA**: ลูกค้า Connected ฝากข้อมูลมาประมวลผล → เราเป็น **Data Processor** ต้องมี DPA,
  retention ของ payload ที่ฝาก (เช่นรูป OCR ลบใน 90 วัน), DSR ครอบข้อมูลชุดนี้
- **กฎเหล็ก #1 ยังบังคับเต็ม** — ทุก endpoint ที่แตะ AI ผ่าน orchestrator + kill-switch;
  ลูกค้า API ได้ local-first อัตโนมัติ = ต้นทุนต่อ transaction ลดเรื่อย ๆ ราคาขายคงที่

### 7.1 Self-service onboarding 📋 — ลูกค้าสร้างการเข้าถึงเองครบวงจร ไม่ต้องรอ admin

```
สมัคร (User เดิมของระบบ)
 → สร้าง BillingAccount (ชื่อกลุ่ม, ข้อมูลออกใบกำกับ)
 → เพิ่ม Company (Connected) + สาขาถ้ามี
 → เลือกฟีเจอร์ (ติ๊กจาก ApiFeature ที่ IsPublished — เห็นราคา/วิธีคิดเงินก่อนเปิด)
 → เลือกระบบบัญชีที่จะเชื่อม (ConnectorType + คู่มือเฉพาะ ERP นั้น)
 → ได้ API key (sandbox ก่อนเสมอ) → ทดสอบ → เติมเครดิต → สลับ production
```

- ทุกขั้นทำเองใน portal `/connect` — admin เข้ามาเกี่ยวเฉพาะ (ก) กำหนดราคา/วิธี
  คิดเงินใน `ApiPricingPlan` (ข) อนุมัติ Postpaid/วงเงิน (ค) ปิด `ApiFeature`
  ทั้งระบบเมื่อจำเป็น
- **ราคาที่โชว์ตอนลูกค้ากดเปิดฟีเจอร์ = แผนที่ active ณ วันนั้น** และการกดเปิดถูก
  audit (`CompanyFeature.EnabledBy/At`) — กันข้อพิพาท "ไม่เคยเปิด/ไม่รู้ราคา"
- ฟีเจอร์ที่ปิดอยู่ = endpoint ตอบ 403 + **ไม่เกิด UsageEvent** — เปิด/ปิดคือ
  สวิตช์เงินจริง ไม่ใช่แค่ซ่อนเมนู

### 7.2 ฟีเจอร์กลาง = ระบบเก่งขึ้นเรื่อย ๆ (สองชั้น — ห้ามสับสนกัน)

- **ชั้น per-tenant**: feedback จากทุก call (OCR แก้ field, ยืนยัน bank match)
  เทรน distillation model **ของบริษัทนั้น** — ข้อมูลลูกค้าไม่ข้าม tenant (PDPA;
  `AiResponseCache`/`AiSuggestionFeedback` tenant-isolated อยู่แล้ว)
- **ชั้นกลางที่แชร์ได้**: seed corpus, `SystemOcr*` mappings, rule resolver,
  prompt/โค้ดที่แก้จาก edge case ที่ลูกค้าเจอ — ยิ่งมีลูกค้า Connected มาก
  ชั้นนี้ยิ่งแข็งให้ทุกคนโดยไม่แตะข้อมูลใคร
- ผลเชิงธุรกิจ: local ตอบแทน AI มากขึ้น → ต้นทุน/transaction ลด → ราคาขายคงที่
  → margin โตเอง (เหตุผลที่คิดเงินตาม "งานสำเร็จ" ไม่ใช่ token)

### 7.3 AI mandate สำหรับผลิตภัณฑ์ API — ปิดลูปโดยดีไซน์ (hard requirement) 📋

> **กับดัก**: ผู้ใช้ UI กดยืนยัน/แก้บนหน้าจอเรา → `RecordUserChoiceAsync` เก็บ
> feedback อัตโนมัติ แต่ลูกค้า API แก้ผลลัพธ์**ในระบบเขา** (Dynamics) — เราไม่เห็น
> ถ้าไม่บังคับใน contract ทุก call ของ Connected = "ยิงทิ้ง" ผิดกฎเหล็ก #1
> ทั้งผลิตภัณฑ์: จ่าย token ฟรี local model ของ tenant นั้นไม่มีวันโต

**กฎ 4 ข้อ ทุก endpoint ใน `/api/v1` ที่มี AI เกี่ยวข้อง:**

1. **Response แนบ `feedbackId`** — ทุก field ที่ AI/local เสนอ (DTO ใช้ pattern
   `<Feature>AiFeedbackId` เดิมของระบบ) + `usedAi`/`confidence` ให้ลูกค้าโชว์
   ป้ายซื่อสัตย์ฝั่งเขาได้ด้วย
2. **การใช้งานปกติของลูกค้า = feedback โดยธรรมชาติ — ห้ามพึ่งความสมัครใจ**:
   ลูกค้าไม่มีแรงจูงใจส่ง feedback แยก ดังนั้นออกแบบให้ workflow ปกติปิดลูปเอง —
   • OCR: ขั้น "ยืนยันสร้างเอกสาร" ลูกค้า POST ค่าสุดท้ายกลับมาอยู่แล้ว →
     ระบบ diff ค่าสุดท้าย vs ที่เสนอ = feedback ครบทุก field อัตโนมัติ
   • Bank recon: การ POST ยืนยันจับคู่ (เลือกชุดไหน) คือ feedback ในตัว
   → ลูกค้าไม่ต้องทำอะไรพิเศษ แต่ลูปปิดทุก transaction
3. **Endpoint feedback เสริม** (`POST /api/v1/feedback/{feedbackId}`) — สำหรับ
   เคสที่แก้ทีหลังในระบบเขา (นักบัญชีแก้เลขบัญชีอีก 3 วันถัดมา) — connector
   สำเร็จรูป (Dynamics) ต้อง sync การแก้กลับมาทางนี้อัตโนมัติ
4. **วัดความพร้อมราย tenant** ✅ — `<Feature>UsedAi` rate ต่อบริษัทลดลงเรื่อย ๆ
   = local โตจริง. **ทำแล้ว** — ดู §7.4 (รายงานการใช้งาน AI แยกรายลูกค้า)
   ใช้เป็นตัวพิสูจน์ margin ที่โตขึ้นต่อ investor/ตัวเอง

**Kill-switch ยังบังคับเต็ม**: provider ดับ → ทุก endpoint API ตอบจาก local
ครบ 100% เงียบ ๆ — SLA ของลูกค้า Connected ต้องไม่ผูกกับ uptime ของ DeepSeek

### 7.4 รายงานการใช้งาน AI แยกรายลูกค้า ✅

> ตอบคำถามธุรกิจ: **ลูกค้ารายไหนใช้ AI เท่าไร ผ่านหน้าเว็บหรือผ่าน API
> จ่ายไปเท่าไร และ local โตพอจะลดการเรียกลงหรือยัง**

**ป้ายกำกับ "ใครเรียก" บนทุก call** (`AiSuggestionFeedback`) — นิยาม
**ชุดเดียวกับ `UsageEvent`** เป๊ะ ๆ เพื่อให้รายงานกับบิลกระทบยอดกันได้:

| ฟิลด์ | ความหมาย |
| --- | --- |
| `Channel` | `Web` (หน้า NextAcc) · `ApiKey` (ลูกค้า Connected) · `Background` (งานระบบ) · `Unknown` (แถวก่อนมีฟีเจอร์นี้) |
| `ApiClientId` | คีย์ที่ยิงเข้ามา — **null = ใช้ผ่านหน้าเว็บ** (นิยามเดียวกับ `UsageEvent.ApiClientId`) |
| `BillingAccountId` | กลุ่มผู้จ่าย **snapshot ณ ขณะเรียก** — ไม่ join สด เพราะบริษัทถูกขายออกจากเครือแล้วประวัติต้องไม่ย้ายตาม |
| `BranchId` · `UserId` | สาขา (จากคีย์) · ผู้กด (ช่องทาง Web) — สำหรับ charge-back ภายในและไล่ที่มาเวลาโต้แย้งบิล |
| `IsSandbox` | คีย์ทดสอบ — นับเพื่อดูพฤติกรรม แต่ **ไม่เข้าบิล** (รายงานตัดออกโดยค่าเริ่มต้น) |

resolve โดย `AiUsageAttributionResolver` (scoped) อ่าน claim จาก
`ApiKeyMiddleware` (`AuthMethod=ApiKey` + `ApiKeyId`) — ไม่มี HttpContext =
`Background`. พลาด resolve ต้องไม่ทำให้ AI call ล้ม (เสียแค่มิติในรายงาน)

**สรุปรายวัน** `AiUsageDailyTenant` unique ที่
(วัน, บริษัท, provider, feature, ช่องทาง):
- **แยกตารางจาก `AiUsageDaily` โดยตั้งใจ** — ตารางเดิม unique ที่
  (วัน, provider, feature) และวิดเจ็ตแอดมินอ่านอยู่ ถ้าเอาแถวแยกบริษัทไปปน
  ผลรวมจะ**นับซ้ำ**ทันที
- เก็บ `LatencySumMs` + `LatencySamples` **ไม่ใช่ค่าเฉลี่ย** — รายงานรวมข้าม
  feature/ช่องทาง/วันตลอดเวลา และ "เฉลี่ยของเฉลี่ย" ผิดเมื่อจำนวน call ต่างกัน
  (เคสจริงในเทสต์: 500 ms vs 108 ms — ผิด 4.6 เท่า)
- `CallsLocalServed` = ครั้งที่ **ประหยัดไป** (Skipped + มีคำตอบ local จริง);
  Skipped ที่ไม่มีคำตอบ local ไม่นับ ไม่งั้นตัวเลข sovereignty สวยเกินจริง
- `UserReviewed`/`UserAcceptedAi` อัปเดตตอน `RecordUserChoiceAsync` โดยลง
  **วันที่เกิด call** ไม่ใช่วันที่กดยืนยัน (ไม่งั้นอัตรายอมรับของเดือนหนึ่ง
  ไปโผล่อีกเดือน) และนับ "ตัดสินใจแล้ว" ครั้งแรกครั้งเดียว — เปลี่ยนใจซ้ำ
  ปรับเฉพาะตัวเศษ ตัวหารต้องไม่โต

**Service/API/หน้าจอ**
- `AiUsageReportService` — สรุปแพลตฟอร์ม · เจาะรายลูกค้า · รายคีย์ API ·
  แนวโน้มรายวัน · ตัวอย่าง call ล่าสุด. ยอดรวมอ่านจาก rollup (เบา)
  ส่วนรายคีย์/ตัวอย่าง อ่านจากแถวระดับ call (มีดัชนี + จำกัดช่วงวันที่ ≤ 400 วัน)
- `GET /api/admin/ai-usage/summary|customers/{id}|export` — **SystemAdmin**
- `GET /api/companies/{id}/ai-usage` (+ `/group` รวมทั้งเครือ) — **ฝั่งลูกค้า
  ดูของตัวเอง**, แยก controller ให้ route ผูก `companyId` เพื่อให้
  `TenantAccessMiddleware` กันข้ามบริษัทให้เหมือน endpoint อื่น
- หน้า `/pages/admin-ai-usage.html` (เมนู adminOnly)
- **PDPA**: คืนเฉพาะ metadata — **ห้ามคืน `PromptJson`/`ResponseJson`**
  ซึ่งมีเนื้อหาเอกสารของลูกค้า (กติกาเดียวกับ `MeteringController`)

**ป้าย "ประเภทลูกค้า"** คำนวณจากช่องทางที่พบจริงในช่วงที่ดู:
`API อย่างเดียว` (มี ApiKey ไม่มี Web) · `เว็บอย่างเดียว` · `ผสม` ·
`งานระบบเท่านั้น` — คอลัมน์เดียวตอบคำถามแรกที่ทีมขาย/ซัพพอร์ตถามเสมอ

**อ่านตัวเลขให้ถูกทาง**: `AiUsageRate` (= ถึง provider จริง ÷ ทั้งหมด)
**ยิ่งต่ำยิ่งดี** ตามกฎเหล็ก #1 — รายงานที่โชว์แต่ยอดเงินจะทำให้สรุปกลับด้าน
ว่า "ใช้น้อย = ไม่มีใครใช้ระบบ" ทั้งที่จริงคือ local เก่งจนไม่ต้องถามครูแล้ว

## 8. Portal `/connect` 📋

- โฟลเดอร์ใหม่ `wwwroot/connect/` — **อยู่ใน deployment เดียวกัน** (แบบ `/admin`)
- **กฎเหล็ก: ยิงได้เฉพาะ `/api/v1` สาธารณะ ห้ามแตะ endpoint ภายใน ห้ามมีข้อมูลของตัวเอง**
  → dogfooding (เราคือลูกค้ารายแรกของ API ตัวเอง) + วันหลังยกไปโฮสต์แยกได้ทั้งโฟลเดอร์
- โครงหน้า = โครงข้อมูล:
  ```
  /connect (login User เดิม → role AccountAdmin)
   ├─ ภาพรวมกลุ่ม: usage ทุกบริษัท · บิลค้าง · เครดิตคงเหลือ · สถานะ key
   ├─ [บริษัท] usage รายสาขา/ฟีเจอร์ · API keys · webhook · log · เอกสารที่สร้างผ่าน API
   │           · **เลือกฟีเจอร์** (เปิด/ปิด CompanyFeature เห็นราคาก่อนเปิด)
   │           · **เลือก connector** (ConnectorType + config + คู่มือ ERP)
   ├─ onboarding wizard (§7.1) — สร้าง account/บริษัท/key ครบวงจรด้วยตัวเอง
   └─ บิล & ชำระเงิน (ระดับ account) · ซื้อเครดิต · ประวัติ
  ```
- ฝั่ง admin (`/admin`): หน้าใหม่ "ฟีเจอร์ & ราคา API" — CRUD `ApiFeature` +
  `ApiPricingPlan` (วิธีคิดเงิน 4 แบบ, ราคา, free quota, tier, วันมีผล) 📋

### 8.1 การเข้าถึงผ่านโดเมน 📋 — subdomain ให้ฟรี / โดเมนตัวเองก็ชี้เข้าได้

ยกแบบมาจากของที่มีจริงแล้วใน CMS (`CmsSite.Subdomain`/`CustomDomain` +
`SiteDomain` + `DomainType` — `CmsSite.cs:17,171`) — pattern พิสูจน์แล้ว ไม่ออกแบบใหม่:

```csharp
public class AccountDomain : BaseEntity          // ผูกระดับ BillingAccount
{
    public Guid BillingAccountId;
    public string Domain;                        // "abcgroup.nextacc.app" | "erp.abcgroup.co.th"
    public DomainType DomainType;                // Subdomain | CustomDomain
    // CustomDomain ต้องพิสูจน์ความเป็นเจ้าของก่อนใช้ — กัน host-header hijack:
    public string VerificationToken;             // ลูกค้าตั้ง DNS TXT ตาม token นี้
    public DateTime? VerifiedAt;                 // null = ยังใช้ไม่ได้
    public bool IsPrimary;
}
```

- **Subdomain**: ตั้งเองใน portal ได้ทันที (`{slug}.nextacc.app` — ตรวจ slug ซ้ำ/คำสงวน)
- **Custom domain**: ลูกค้าเพิ่มโดเมน → ระบบให้ TXT record → ตั้ง DNS → กด verify →
  ใช้ได้ (TLS อัตโนมัติผ่าน reverse proxy/Let's Encrypt — งาน infra ไม่ใช่งานโค้ด)
- **Middleware resolve host → BillingAccountId** → portal ขึ้นแบรนด์ของ account นั้น
  (โลโก้/ชื่อ/สี — white-label ให้ partner ได้ในตัว); host ที่ไม่รู้จัก → หน้า
  กลาง `/connect` ปกติ. Cookie/JWT audience ผูกต่อ host กัน token ข้ามโดเมน

### 8.2 Authentication — ในระบบเอง + LINE + Microsoft 365

| วิธี | สถานะ | หมายเหตุ |
| --- | --- | --- |
| Email + password (ในระบบ) | ✅ | `AuthController` login/refresh/forgot/reset ครบ |
| **Google OAuth** | ✅ | `POST /auth/sso` — เส้นหลักคือ **authorization-code redirect** เหมือน LINE (`ValidateGoogleTokenAsync` แลก code ที่ `oauth2.googleapis.com/token` ด้วย `SiteSettings.GoogleClientSecret` แล้ว verify id_token ที่ tokeninfo); One Tap (`gsi/client`) เหลือเป็นทางสำรองเมื่อยังไม่ได้ตั้ง secret |
| Facebook OAuth | ✅ | `POST /auth/sso` + `User.AuthProvider/AuthProviderId` (generic รองรับ provider เพิ่มโดยไม่แก้ schema) — ยังใช้ JS SDK |
| **LINE Login** (OAuth2) | ✅ | authorization-code flow → `AuthService.ValidateLineTokenAsync` (channel secret ไม่ออกจาก server); คนละอย่างกับ LINE bot binding ที่มีแล้ว (`User.LineUserId` + `LineBindCode`) — แต่ login แล้ว map เข้า `LineUserId` เดิมได้เลย บัญชีเดียวทั้ง login และ bot |
| **Microsoft Entra ID (Office 365)** | 📋 | OIDC มาตรฐาน; ตลาดเดียวกับลูกค้า Dynamics พอดี — บริษัทที่ใช้ Dynamics มี M365 อยู่แล้วเกือบ 100% |

- `AuthProvider` เพิ่มค่า `"Line"` (✅ ใช้งานแล้ว), `"Microsoft"` (📋) — โครงเดิมรองรับอยู่แล้ว
- 🔐 **การผูกบัญชีภายนอก (✅ รอบ 119)** — ตัวตัดสินคือตาราง
  **`UserExternalLogins`** (`Provider` + `ProviderUserId` unique) ไม่ใช่คอลัมน์เดี่ยว
  `Users.AuthProvider/AuthProviderId` ซึ่งเหลือความหมายเป็น "ตัวล่าสุดที่ใช้เข้าระบบ"
  - **ผูกเข้าบัญชีเดิมได้ก็ต่อเมื่อ provider ยืนยันอีเมลแล้ว** (`Helpers/SsoIdentityPolicy`):
    Google ต้องมี `email_verified=true` · LINE คืน email = ยืนยันแล้ว ·
    **Facebook ยืนยันไม่ได้เลย** ⇒ ต้องผ่านลิงก์ยืนยันทางอีเมลเสมอ
    _(เดิมผูกด้วย "อีเมลตรงกัน" อย่างเดียว = ใครสร้างบัญชี provider ให้อีเมลตรงกับ
    ผู้ใช้ของเรา ก็เข้าถึงข้อมูลทั้ง tenant ได้ — account pre-hijacking)_
  - ยืนยันไม่ได้ → ไม่ตัน: แถวสถานะ `ConfirmedAt=null` + ส่งลิงก์อายุ 1 ชม. →
    `GET /api/auth/sso/confirm-link?token=` → redirect `/login.html?ssoLinked=…`
  - 🆕 **ไม่มีอีเมลจาก provider ก็ใช้งานได้ (รอบ 120)** — LINE คืนอีเมลเฉพาะ
    channel ที่ผ่านอนุมัติสิทธิ์ email ซึ่งส่วนใหญ่ยังไม่ผ่าน. ตัวระบุตัวตนหลัก
    คือ `ProviderUserId` (LINE userId) ไม่ใช่อีเมล:
    - **ผูกไว้แล้ว** → ล็อกอินได้เลย ไม่แตะอีเมล
    - **ยังไม่เคยผูก** → `SsoSignupRequiredException` → API ตอบ 200 พร้อม
      `needsSignup + ssoTicket + suggestedName + pictureUrl` → หน้า login พาไป
      `/register.html` เติมชื่อ/รูปให้ ผู้ใช้กรอกแค่อีเมล+รหัสผ่าน →
      `RegisterRequest.SsoTicket` ผูกบัญชีให้อัตโนมัติ
    - ตั๋วเซ็นด้วย **กุญแจผูกวัตถุประสงค์** (`JwtHelper.GenerateSsoSignupTicket`,
      อายุ 20 นาที) ⇒ access token เอามาสวมเป็นตั๋วไม่ได้ (`SsoSignupTicketTests`)
    - **ผูกตอนล็อกอินอยู่แล้ว**: `POST /api/auth/external-logins/link` —
      ไม่ต้องใช้อีเมลเลย (JWT + OAuth สด = พิสูจน์ครบทั้งสองฝั่ง);
      หน้าตั้งค่ามีปุ่ม "ผูกบัญชี LINE/Google" ที่เดินผ่าน `/login.html`
      ด้วย `ssoMode='link'` เพราะ provider ตั้ง callback URL ได้ชุดเดียว
  - **ห้ามผูกเงียบ** — ทุกครั้งที่ผูก/ถอด เขียน `AuditLog` (EntityType
    `UserExternalLogin`) + ส่งอีเมลแจ้งเจ้าของ; ผู้ใช้ดู/ถอดเองได้ที่
    **ตั้งค่า → 🔐 ความปลอดภัยบัญชี** (`GET/DELETE /api/auth/external-logins`)
    — ถอดอันสุดท้ายของบัญชีที่ไม่มีรหัสผ่านถูกบล็อกพร้อมบอกทางแก้
- 🚦 **ด่านสถานะบัญชี (✅ รอบ 119)** `Helpers/UserLoginPolicy.Evaluate` ใช้ร่วม
  **ทั้งสามทางเข้า** (`LoginAsync` · `SsoLoginAsync` · `RefreshTokenAsync`):
  Inactive/Suspended = เข้าไม่ได้ + เหตุผลเป็นข้อความ · PendingVerification เข้าได้
  เฉพาะผ่าน SSO ที่ยืนยันอีเมลแล้ว (แล้วเลื่อนเป็น Active)
  _(เดิมไม่มีทางเข้าไหนอ่าน `User.Status` เลย ทั้งที่ `PayrollService` ตั้ง
  Inactive ให้อัตโนมัติเมื่อพนักงานลาออก พร้อมคอมเมนต์ว่า "login is revoked" ⇒
  พนักงานที่ลาออกแล้วยังล็อกอินได้ — control ที่ไม่มีใครเรียก = ไม่มี control)_
- บัญชีที่สร้างผ่าน SSO มี `PasswordHash=""` → `LoginAsync` ตรวจก่อนเรียก BCrypt
  แล้วตอบ 401 พร้อมชื่อ provider ที่ผูกไว้ (เดิม BCrypt โยน `SaltParseException`
  ⇒ ผู้ใช้เห็น **500** และ ErrorLogs รกด้วยรายการที่ไม่ใช่บั๊ก)
- คำเชิญเข้าบริษัททำงานบนเส้น SSO ด้วยแล้ว — `ConsumeInvitationAsync` ตัวเดียว
  ที่ทั้ง `RegisterAsync` และ `SsoLoginAsync` เรียก (เดิมเส้น SSO ไม่มีตรรกะนี้เลย
  ⇒ ผู้ถูกเชิญที่เลือกสมัครด้วย Google/LINE ไม่ได้เข้าบริษัทที่เชิญ)
- **ตั้งค่าคีย์จากหน้าเว็บ** `/admin/sso-config.html` → `SiteSettings.{Google,Facebook,Line}*`
  (DB ชนะ `appsettings.json`); `GET /api/auth/sso-config` คืนเฉพาะ provider ที่
  **เปิดสวิตช์ + มีคีย์ครบ** → หน้า `login.html`/`register.html` ซ่อนปุ่มที่เหลือ
  (ปุ่มที่กดแล้วพัง = ปุ่มหลอก ห้ามมี) และแสดงข้อความเมื่อ SDK ของ Facebook
  โหลดไม่ขึ้น แทนที่จะเงียบ — ⚠️ ข้อความนั้นยังโทษ "ตัวบล็อกโฆษณา" อยู่ ทั้งที่
  ต้นเหตุที่พบจริงคือ CSP/การแข่งกันของ `async defer` (หนี้ที่รู้ตัว: Facebook
  ยังไม่ได้ย้ายไป redirect flow เหมือน Google/LINE)
- ⚠️ **CSP ต้องอนุญาต origin ของ SDK ด้วย** — `script-src` ใน
  `Middleware/SecurityMiddleware.cs` เดิมไม่มี `https://accounts.google.com`
  (และ `https://connect.facebook.net`) ⇒ เบราว์เซอร์บล็อกสคริปต์เงียบ ⇒ ปุ่ม Google
  ขึ้น "โหลดบริการไม่สำเร็จ — ปิดตัวบล็อกโฆษณา" ตลอด ทั้งที่ผู้ใช้ไม่มีตัวบล็อกเลย.
  บังคับด้วย `tools/csp_external_ref_check.py`
- **redirect flow ตัวกลางตัวเดียว** `wwwroot/js/sso.js` (`Sso.begin` / `Sso.readCallback`)
  ใช้ร่วมกันทั้ง `login.html` และ `register.html` สำหรับ **LINE และ Google** —
  เก็บ `ssoState` (กัน CSRF) + `ssoProvider` (รู้ว่าจะส่ง provider ไหนให้ backend)
  + `ssoSignup` (พก companyName/แพ็กเกจ/หลักฐานยินยอมข้าม redirect) ใน
  sessionStorage; callback URL มาจากเซิร์ฟเวอร์ที่เดียว (`SsoSettings.LoginCallbackUrl`)
  ห้ามคำนวณจาก `location.origin` เอง
- **สมัครผ่าน SSO**: ปุ่มอยู่**นอก** `<form>` ⇒ เบราว์เซอร์ไม่ตรวจ `required` ให้
  → `register.html._ssoPreflight()` บังคับติ๊ก "ยอมรับข้อกำหนด + นโยบายความเป็น
  ส่วนตัว" เอง ก่อนพาออกไป IdP; `companyName` + แพ็กเกจที่เลือกถูกส่งเข้า
  `POST /auth/sso` ด้วย (LINE/Google พกข้าม redirect ผ่าน `sessionStorage.ssoSignup`)
  — มิฉะนั้นผู้สมัครผ่าน SSO จะได้ `FreeTrial` เสมอและบริษัทไม่มีชื่อ
- **ความยินยอม PDPA ม.19 (✅ ลงโค้ดแล้ว)** — ทุกทางที่สร้าง `User` ใหม่ผ่าน
  `AuthService.RequireSignupConsent()` + `RecordSignupConsentAsync()`:
  ฟอร์มสมัคร · รับคำเชิญ · Google/Facebook/LINE. `POST /auth/sso` ทำหน้าที่ทั้ง
  login และ signup จึงบังคับ**เฉพาะตอนสร้าง user ใหม่** — ผู้ใช้เดิมกดเข้าระบบ
  ต้องไม่ถูกขวาง (ยินยอมซ้ำทุกครั้งไม่ใช่ความยินยอม); ผู้ใช้ใหม่ที่กด SSO ที่หน้า
  login จะได้ข้อความ + ปุ่มพาไปหน้าสมัคร ซึ่งเป็นที่เดียวที่มีข้อความให้อ่านจริง
- แถวที่บันทึก: `PdpaConsentRecord` scope ระดับ**แพลตฟอร์ม**
  (`CompanyId = Guid.Empty` — ตอนสมัคร ผู้ควบคุมข้อมูลคือผู้ให้บริการ ไม่ใช่
  tenant ที่ยังไม่เกิด) + `EvidenceHash` จาก canonical ตัวเดียวใน
  `Helpers/PdpaSignupConsent.cs` (round-trip test มีแล้ว)
- **หน้าเอกสารทางกฎหมายจริง** `/terms.html` + `/privacy.html` (เดิม `href="#"`)
  — ตัวตนผู้ควบคุมข้อมูล + เวอร์ชันนโยบายมาจาก `GET /api/legal/policy`
  ← `SiteSettings.PlatformSeller*` ที่แอดมินกรอกไว้แล้ว (ไม่ฝัง literal ซ้ำ)
- **ตั้งค่าที่ระดับ BillingAccount**: `AllowedAuthMethodsCsv` — องค์กรบังคับได้ว่า
  user ใต้ account ต้อง login วิธีไหน (เช่น enterprise บังคับ O365 เท่านั้น
  ปิด password login) + `EnforceSsoForAccountUsers`
- **JIT provisioning**: login ผ่าน IdP ครั้งแรก → สร้าง User อัตโนมัติ →
  เข้ากลุ่มด้วย invite เท่านั้น (**ห้าม auto-join จาก email domain** — โดเมน
  อีเมลปลอมง่าย ไม่ใช่หลักฐานความเป็นสมาชิกองค์กร)
- ทุกวิธี login จบที่ JWT เดิมของระบบ → ชั้นสิทธิ์ (`CompanyUser`/`AccountAdmin`)
  ไม่ต้องรู้ว่า login มาจากไหน
- Governance: `AccountAdmin` จัดการกลุ่ม/บิล/key ได้ แต่**เปิดสมุดบัญชีบริษัทใดต้องมี
  `CompanyUser` ของบริษัทนั้น** — งบรวมไปทาง ConsolidationGroup (opt-in) เท่านั้น

---

## 9. แผน migrate จากปัจจุบัน (ลำดับทำจริง)

1. ✅ **เสร็จแล้ว** — `BillingAccount` + `BillingAccountAdmin` +
   `Company.BillingAccountId/ParentCompanyId/CompanyKind` + migration + backfill 1:1
   (owner เดิมกลายเป็นผู้ดูแลหลัก) **ลูกค้าเก่าไม่รู้สึกอะไรเลย · zero behavior change**
2. เปลี่ยนสมอ `AccountSubscription` → `BillingAccountId` + จุดเดียวใน `CheckUsageLimitAsync`
3. ✅ **เสร็จแล้ว** — `UsageEvent` + `ApiFeature`/`CompanyFeature`/`ApiPricingPlan`
   + `IUsageMeteringService` + API admin (`/api/admin/metering`) และลูกค้า
   (`/api/companies/{id}/metering`). เหลือ: หน้าเว็บ admin + rollup รายวัน
4. 🔨 **บางส่วนแล้ว** — ขยาย `ApiKey` เดิม (scopes/branch/sandbox/ConnectorType/
   webhook) + `/api/v1/ocr` + `/api/v1/bank` พร้อมด่าน scope + metering ครบ.
   เหลือ: ส่ง webhook จริง (HMAC), async job/batch, `/api/v1/documents`,
   onboarding wizard (§7.1)
5. Billing สิ้นเดือน → ใบแจ้งหนี้/ใบกำกับอัตโนมัติผ่าน pipeline เอกสารเดิม (2 โหมด)
6. 🔨 Portal `/connect` (หน้าหลักเสร็จแล้ว) + `AccountDomain` (subdomain → custom domain + verify) +
   LINE Login / Microsoft Entra ID (§8.1–8.2)
7. เปิด 2 ฟีเจอร์แรก: OCR→DTO, statement→matching; Connector Dynamics เมื่อมีลูกค้าจริง

## 10. การบ้านที่งอกจากดีไซน์ (ยังไม่ทำ)

- [ ] ตรวจ `TaxService` ว่ารายงาน ภ.พ.30 กรอง/แยกตามสาขาได้จริงครบไหม + gate
      `VatFilingConsolidated` (ยื่นรวมต้องมีอนุมัติ)
- [ ] `AiBudgetGuard` จาก global → ราย BillingAccount/Company (ผูกตาราง §4)
- [ ] `AiUsageDaily` เพิ่ม `CompanyId` (จากการวิเคราะห์ token accounting รอบก่อน)
- [ ] `AiSuggestionFeedback` เพิ่ม `UserId`/`ApiClientId`/`RequestSource` (ตรวจสอบ "ใครทำ")
- [ ] SLA + status page ก่อนเซ็นลูกค้า Connected รายแรก

---

_Last verified against codebase: 2026-09-25 (rev 30 · รอบ 195 ทีม I3 — `/api/v1/documents/{id}/approve` คืน `scanVatNotOnPaper` แยกจาก `scanAmountGap` — commit <pending>)_

_ก่อนหน้า: 2026-09-25 (rev 29 · รอบ 194 ทีม C — **§4 ประเภทเงินมัดจำ** (`DepositKind` ต่อบริษัท · API `/deposit-kinds` ·_
_สิทธิ์ `CompanySettings.Edit` + ปฏิเสธ API key · integration `depositKindCode` ไม่รู้จัก = 400 · เงินประกัน + ขับ JE = 400 `DEP-SEC-DEDUCT` ·_
_หมายเหตุ mismatch ผ่าน `ResolveKind`) — commit <pending>)_

_Last verified against codebase: 2026-09-24 (rev 28 · รอบ 193 — **§3.1c การเข้าถึงด้วย API key**: คีย์ `int_` สิทธิ์แยก อ่าน/เขียน/ลบ + legacy 90 วัน ·_
_ออก/ผูกผู้ใช้เฉพาะเจ้าของ · `EnableApiAccess` คุมคีย์ `acc_` ที่ออกแล้ว · งานเจ้าของ/ทำลายหลักฐาน/ตั้งนโยบายปฏิเสธ API key (`OwnerActionGuard` ·_
_`[RejectApiKey]` · `[RequireOwner]`) · Subscription เจ้าของเท่านั้น + `TrialExtensionPolicy` · ปิดรับสมัครกันที่ server (`RegistrationPolicy`) ·_
_§3.1 `/api/v1` contactBranchCode/taxIdExists/missingBuyerFields/scanAmountGap · แถวค่าตั้งเกิดพร้อมบริษัท + `CompanyVatStatus` ·_
_§4 ค่าตั้งใหม่ · §5 พื้นที่เก็บไฟล์คิดจากของจริง + เกินเพดาน = เตือน · หลังฝ่ายค้านรอบสี่: `/api/v1/documents` `contact.taxIdWarning` + integration `Warnings` — commit 7a16f097)_

_Last verified against codebase: 2026-09-11 (rev 27 — **§5.1 โควตาเว็บ CMS บังคับจริง**:_
_`CmsQuotaUsage` เป็นตัวนับตัวเดียวของทั้งด่านและตัวเลขบนหน้าจอ · ต่อสายที่ `CreatePageAsync` ·_
_`ApplyTemplateCoreAsync` (นับทีละหลายหน้า) · `AddProductAsync` · auto-publish ตัดให้พอดีโควตา —_
_เดิม `CanAddPageAsync`/`CanAddProductAsync` ไม่มีใครเรียก เพดานจึงไม่เคยกั้นอะไร)_

_Last verified against codebase: 2026-09-10 (rev 26 — **§6.1c แคตตาล็อกฟีเจอร์ + หน้าแสดง_
_แพ็กเกจอ่านจากที่แอดมินตั้ง**: `Helpers/FeatureCatalog` + `GET /api/subscription/feature-catalog` ·_
_`js/plan-display.js` · ตารางเปรียบเทียบ/การ์ดราคา/การ์ดสมัคร/หน้าแพ็กเกจลูกค้า/ตั้งค่าฟีเจอร์/preset_
_แอดมิน วาดจาก API ทั้งหมด · `PlanName` บน SubscriptionResponse/Dashboard · add-on min-plan ใช้ชื่อ enum จริง)_

_Last verified against codebase: 2026-09-03 (rev 25 — **รอบผู้ใช้รายงาน 6 ข้อ**:_
_(1) หน้า "ติดต่อเรา" ของเว็บ CMS โชว์เบอร์/อีเมลตัวอย่าง (`02-XXX-XXXX`) ที่ seed_
_ฝังเป็นข้อความตายตัว → เปลี่ยนเป็นโทเคน `{{company.phone}}` (`Helpers/CmsContentTokens`)_
_แทนค่าตอนเรนเดอร์ + `Site.ContactPhone/ContactEmail/LineId/FacebookUrl/InstagramUrl`_
_ให้ override ระดับเว็บ (ว่าง = ใช้ของบริษัท) + migration ล้าง placeholder ที่ค้างในฐาน_
_(2) `Layout.jsArg()` — ค่าที่ฝังใน JS string ของ onclick ต้องหนีแบบ JS ไม่ใช่ HTML_
_(3) ศูนย์ช่วยเหลือ `HelpResource` (ระดับแพลตฟอร์ม ไม่มี CompanyId): วิดีโอ/คู่มือ_
_อัปโหลดเองหรือฝัง YouTube/Facebook/TikTok · แยกสองแกน หมวด (สอนเรื่องอะไร) กับ_
_ModuleCode (ของธุรกิจไหน) · `/api/help` + หน้า `help.html`/`admin-help.html` ·_
_CSP `frame-src` เพิ่มโดเมนวิดีโอ มิฉะนั้นเบราว์เซอร์บล็อกเงียบ)_
_ก่อนหน้า: 2026-09-03 (rev 24 — **เก็บงานค้างจากการตรวจซ้ำ**:_
_(1) ปิดช่องเลี่ยงโควตา — `OriginModule` ย้ายจาก request DTO ไปเป็นพารามิเตอร์ของเมธอด_
_(2) `lodging.promo` unpublish (ขายฟีเจอร์ที่ยังไม่มี) (3) สวิตช์ภารกิจแลกโควตา 3 ชั้น_
_มี endpoint + หน้าจอแอดมินจริงแล้ว (4) ลบ `GetEnabledAddOnCodesAsync` ที่ไม่มีใครเรียก_
_(5) ยุบสำเนา resolver ราคาใน `EntitlementService` → เรียก `ResolveEffectivePlanAsync`_
_ตัวเดียวกับเส้นคิดเงิน (doc-comment เดิมอ้างเทสต์ที่ไม่มีไฟล์อยู่จริง)_
_(6) ล็อก §82/3 ผูก `companyId` แล้ว — เลิกบล็อกข้ามบริษัท + job เลิกถือล็อกทั้งรอบ_
_(7) ตั้ง `Db:MaxPoolSize`/`MinThreads` + ให้เส้นที่เปิด connection เองใช้สตริงเดียวกับ EF)_
_ก่อนหน้า: 2026-09-03 (rev 23 — **License ส่วนเสริม + โควตา**:_
_§6.1a ปิดรอบบิลค่าใช้งาน (`UsageInvoicingJob` เขียน `BilledPeriod`/`BilledDocumentId`_
_ที่ไม่เคยมีใครเขียน · ใบแจ้งหนี้หลายบรรทัดผ่าน `IssueUsageInvoiceAsync` · Prepaid_
_ปิดรอบโดยไม่ออกใบ · ต่ำกว่า ฿50 ยกยอด · ออกใบไม่สำเร็จห้ามตีตรา) ·_
_§6.1b ชั้น add-on (`AddOnCodes`/`CompanyFeature`/`IEntitlementService`) + โควตาเอกสาร_
_(`IQuotaService`: สถานะ/top-up/ภารกิจแลกโควตา · `DocumentQuotaPolicy` ตัดสิน "นับไหม"_
_กับ "บล็อกได้ไหม" แยกกัน — เอกสารที่กฎหมายบังคับออกได้เสมอ) · หน้า `/pages/addons.html`_
_(ลูกค้า) + `/pages/admin-addons.html` (SystemAdmin) · `SubscriptionResponse.EnabledAddOnCodes`_
_→ `Layout.hasFeature` ตัวเดียวตอบทั้ง bitmask และ add-on code)_
_ก่อนหน้า: 2026-09-03 (rev 22 — **ที่พัก (Lodging)** §3.1b: LodgingProperty ผูก Site/Branch ·_
_seed ตอนสร้างเว็บโรงแรม · สิทธิ์ Lodging.Manage/Settings · scope สาธารณะ SiteId+token)_
_ก่อนหน้า: 2026-09-02 (rev 21 — **เก็บงานค้างของชั้น_
_ผู้ใช้/บัญชีภายนอก**: (ก) คำเชิญเข้าบริษัท (`CompanyInvitation`) ถูก consume_
_บนเส้น SSO ของ **บัญชีเดิม** ด้วย — เดิมทำเฉพาะตอนสมัครใหม่ ⇒ ผู้ใช้ที่มีบัญชี_
_อยู่แล้วแล้วถูกเชิญเข้าอีกบริษัท กดลิงก์คำเชิญ → เลือก "เข้าด้วย Google" จะเข้า_
_ระบบได้แต่คำเชิญค้าง `Pending` ตลอดไป (แอดมินเห็น "รอตอบรับ" ทั้งที่คนนั้นเข้า_
_มาแล้ว = silent no-op) · เพิ่มด่านกัน `CompanyUser` ซ้ำ (สองสิทธิ์ในบริษัท_
_เดียว = บทบาทไหนชนะขึ้นกับลำดับแถว) (ข) PDPA erasure ถอด `UserExternalLogins`_
_+ `AuthProvider`/`AuthProviderId` + refresh token ด้วย — บัญชีที่ anonymise_
_แล้วแต่ยังผูก Google/LINE ไว้ กดปุ่ม SSO ก็เข้าได้ตามปกติ (เส้น SSO ค้นด้วย_
_`ProviderUserId` ไม่เคยดูอีเมล) และรายงาน DSR access คืนรายการบัญชีภายนอก_
_ที่ผูกไว้ตาม ม.30 (ค) `ExternalLoginResponse` แยก `LinkedAt` (วันที่ผูก) ออกจาก_
_`ConfirmedAt` (วันที่ยืนยัน) + เปิด `LinkedFromIp` ให้เจ้าของบัญชีเห็นเอง —_
_เดิมยืมช่องเดียวเก็บสองความหมาย ⇒ การผูกที่ยังไม่ยืนยันกลายเป็น "ไม่มีวันที่ผูก");_
_ก่อนหน้า 2026-09-01 (rev 20 — **LINE ที่ไม่มีสิทธิ์_
_email ใช้งานไม่ได้เลยแม้แต่คนที่ผูกบัญชีไว้แล้ว**: เลิกบังคับอีเมล ใช้_
_`ProviderUserId` เป็นตัวระบุตัวตนหลัก · ไม่มีบัญชี → พาไปหน้าสมัครพร้อมตั๋ว_
_ที่เซ็นแล้ว + เติมชื่อ/รูปให้ · เพิ่มเส้น "ผูกบัญชีตอนล็อกอินอยู่แล้ว");_
_ก่อนหน้า 2026-09-01 (rev 19 — **SSO ผูกบัญชีด้วยอีเมล_
_อย่างเดียวมาตลอด + ไม่มีทางเข้าไหนอ่าน `User.Status` เลย**: เพิ่ม_
_`Helpers/SsoIdentityPolicy` (ผูกได้ต่อเมื่อ provider ยืนยันอีเมล — Facebook ต้อง_
_ผ่านลิงก์ยืนยันเสมอ) · `Helpers/UserLoginPolicy` (ด่านสถานะร่วมสามทางเข้า) ·_
_ตาราง `UserExternalLogins` (ผูกได้หลาย provider + สถานะรอยืนยัน) · audit +_
_อีเมลแจ้งทุกครั้งที่ผูก/ถอด · หน้าตั้งค่า → ความปลอดภัยบัญชี · invitation ทำงาน_
_บนเส้น SSO · บัญชี SSO ล็อกอินด้วยรหัสผ่านได้ 401 แทน 500);_
_ก่อนหน้า 2026-09-01 (rev 18 — **ปุ่ม Google login กดแล้ว_
_ไม่ขึ้นอะไร**: CSP ของระบบเอง (`script-src`) ไม่มี `accounts.google.com` ⇒ สคริปต์_
_One Tap ถูกบล็อกเงียบทุกครั้งตั้งแต่วันแรก แล้วหน้า login ขึ้นข้อความโทษตัวบล็อก_
_โฆษณา — แก้ CSP + ย้าย Google มาใช้ **authorization-code redirect** เหมือน LINE_
_(ต่อสาย `GoogleClientSecret` ที่มีช่องกรอกในหน้าแอดมินมาตลอดแต่ไม่มีใครเรียกใช้) +_
_ยุบโค้ด redirect ของสองหน้าเข้า `wwwroot/js/sso.js` ตัวเดียว + `tools/csp_external_ref_check.py`);_
_ก่อนหน้า 2026-08-28 (rev 17 — **VAT ค่าบริการปัดเศษไม่ตรงกัน_
_ระหว่างสองตัวออกบิล**: `PlatformBillingDocumentIssuer` คิด 7/107 พร้อม_
_`MidpointRounding.AwayFromZero` ถูกต้อง แต่ `SaasBillingDocumentService` ที่คิด_
_**สูตรเดียวกัน** ลืมทั้ง 4 จุด (inclusive/exclusive × 2 เส้นทาง) และ_
_`SampleDataController` อีก 1 จุด — แก้ให้ทั้ง 5 จุดใช้ `AwayFromZero` เหมือนกัน_
_**ขอบเขตที่แท้จริง (ตรวจย้ำรอบ 100)**: ต่างกันจริงเฉพาะสูตร `x × 0.07`_
_(VAT บวกเพิ่ม) ซึ่งตกจุดกึ่งกลางราว 0.5% ของยอด เช่น ฿1.50 → 0.105 ⇒_
_AwayFromZero 0.11 เทียบ banker's 0.10 = **2 ใน 5 จุด**. อีก 3 จุดที่ใช้_
_`x × 7 / 107` และ `x / 1.07` พิสูจน์ได้ว่า**ไม่มีค่าใดตกจุดกึ่งกลางเลย**_
_จึงเป็นการทำให้สม่ำเสมอ ไม่ใช่การแก้ยอดที่เคยผิด (ล็อกไว้ด้วย_
_`VatRoundingModeTests`) · ไม่มีการเปลี่ยนโครงสร้าง billing/quota ใด ๆ);_
_rev 16 — **§3.1b เอกสารออกจากสาขาไหน_
_เฟส 1 ✅**: Document.BranchId + IssuerBranchCode snapshot + resolver กลาง →_
_รหัสสาขา/ที่อยู่บน renderer ทั้งสองตัว + TXID e-Tax + สืบทอดเอกสารลูก 5 ทาง_
_(รายละเอียดที่ DOCUMENT_FLOW.md §6.2d));_
_rev 15 — **§3.1a ทะเบียนสาขา เฟส 0 ✅_
_ลงโค้ดจริง**: CRUD ครบ + echo ทุกฟิลด์ + ด่านรหัสสรรพากร/สำนักงานใหญ่ + ปลด gate_
_แพ็กเกจบน `/dimensions/branches` + เมนูในหมวดตั้งค่า + `Helpers/TaxBranchCode.cs`_
_resolver กลาง (แทนสูตรที่กระจาย 3 ที่) + แก้ตัวกรองสาขาในสมุดรายวันที่ไม่เคยกรอง);_
_rev 14 — **§8.2 ความยินยอม PDPA ม.19_
_ตอนสมัคร ✅ ลงโค้ดจริง**: ด่าน + PdpaConsentRecord ทุกทางสมัคร · หน้า_
_terms.html/privacy.html + GET /api/legal/policy);_
_rev 13 — **§8.2 auth: LINE Login ✅_
_(authorization-code) · ตั้งคีย์ SSO จาก /admin/sso-config.html · ทางสมัครผ่าน_
_Google/Facebook/LINE บังคับติ๊กยอมรับข้อกำหนด + ส่ง companyName/แพ็กเกจไปด้วย)**;_
_rev 12 — **§7.4 รายงานการใช้งาน AI_
_แยกรายลูกค้า ✅ ลงโค้ดจริง**: ป้ายกำกับ Channel/ApiClientId/BillingAccountId/_
_BranchId/UserId/IsSandbox บนทุกแถว `AiSuggestionFeedback` (นิยามชุดเดียวกับ_
_`UsageEvent` เพื่อกระทบยอดกับบิลได้) · resolver อ่าน claim จาก ApiKeyMiddleware ·_
_ตารางสรุป `AiUsageDailyTenant` (เก็บผลรวม latency ไม่ใช่ค่าเฉลี่ย) ·_
_`AiUsageReportService` + `/api/admin/ai-usage/*` (SystemAdmin) +_
_`/api/companies/{id}/ai-usage` (ลูกค้าดูของตัวเอง + ทั้งเครือ) +_
_หน้า `/pages/admin-ai-usage.html` + export Excel · เทสต์ `AiUsageReportTests` ·_
_ทำให้ §7.3 ข้อ 4 จาก 📋 → ✅; rev 11 — §6.1 ลงโค้ดจริง:_
_`PlatformBillingDocumentIssuer` ออกใบกำกับ/ใบเสร็จ/ใบแจ้งหนี้ค่าบริการผ่าน tenant_
_ของผู้ให้บริการ (เลือกบริษัทที่ ตั้งค่าเว็บไซต์ → ข้อมูลผู้ขาย) ⇒ รายได้ค่าบริการ_
_ลง GL + เข้ารายงานภาษีขาย/ภ.พ.30 + ออก e-Tax ได้ · เพิ่มช่อง "ภาษีถูกหัก ณ ที่จ่าย"_
_ตอนบันทึกรับเงิน (ยอดใบ = รับจริง + ที่ถูกหัก) · ล้มเหลวตกกลับ PDF เดิมเสมอ)_
_· rev 10 — audit จอ Admin License:_
_(1) modal 🎫 ใน users.html เคยส่ง accountPlan เป็น JSON ผ่าน onclick attribute —_
_browser decode &quot; กลับเป็น " ทำให้ argument เป็น object แล้ว .replace โยน_
_TypeError → catch ตีความว่า "ไม่มี License" ทั้งที่มี ⇒ ต่ออายุ/แก้วันหมดของเดิม_
_ไม่ได้เลย; แก้เป็นส่ง userId แล้วอ่านจาก cache + เพิ่มกล่องอธิบายโครงสร้าง 3 ชั้น_
_ในตัว modal. (2) "บันทึกรับเงิน manual" อนุมัติอัตโนมัติ (วิ่งเข้าเส้น approve)_
_จึงไม่โผล่ในแท็บ "รอตรวจสอบ" ที่ค้างอยู่ — ผู้ใช้เข้าใจว่ากดแล้วไม่เกิดอะไร;_
_แก้ให้สลับไปแท็บ "ทั้งหมด" + toast บอกเลขที่. (3) `ReviewPaymentAsync` sync_
_โควตาจาก template ครบทุกตัวแต่ลืมธง `IsPermanentFree` — บริษัทที่เคยอยู่แพ็กเกจ_
_ฟรีถาวรแล้วจ่ายอัปเกรด Enterprise จะติดป้าย "ไม่หมดอายุ (ฟรีถาวร)" ค้าง และงาน_
_ตัดหมดอายุไม่เคยตัด; แก้ให้ sync ตาม template ใหม่ — แถวเก่าที่ติดค้างแล้วล้างได้_
_ด้วยปุ่ม resync ของแต่ละแพ็กเกจในหน้า plans. เส้น cascade ต่ออายุ Subscription →_
_AccountSubscription ตรวจแล้วถูกต้องอยู่แล้ว)_
_· rev 9 — contact sync + /api/v1/documents +_
_workbench (เอกสาร→PV, statement→จับคู่, ผูกรหัสผู้ติดต่อ); rev 8 — portal /connect หน้าแรกใช้งานได้; rev 7 — §9.4 บางส่วน: ApiKey ขยาย +_
_/api/v1 ocr/bank + ด่าน scope + ผูก UsageEvent ทุก call; rev 6 — §9.3 ลงโค้ด: Metering ครบชุด_
_(ApiFeature/CompanyFeature/ApiPricingPlan/UsageEvent + service + admin/tenant API); rev 5 — §9.1 ลงโค้ดจริงแล้ว: BillingAccount/_
_BillingAccountAdmin + Company FK 3 ตัว + migration backfill 1:1 → §3.1 ✅; rev 4 — §7.3 AI mandate ฝั่ง API: ปิดลูป_
_โดยดีไซน์ (feedbackId ทุก response, workflow ปกติ=feedback, endpoint แก้ย้อนหลัง,_
_UsedAi rate ราย tenant); rev 3 — เพิ่ม §8.1 โดเมน (subdomain/custom_
_domain ยกแบบจาก CmsSite ที่มีจริง) + §8.2 auth (local/Google/FB ✅ · LINE/Microsoft 365 📋);_
_rev 2 — self-service onboarding §7.1,_
_ฟีเจอร์กลาง/การเรียนรู้ 2 ชั้น §7.2, ApiFeature/CompanyFeature/PricingMethod/ConnectorType) —_
_สถานะ: §3.1 ✅ ตรวจกับโค้ดแล้ว (รวม BillingAccount/BillingAccountAdmin/Company FK ที่เพิ่งลง) ·_
_§3.2 (ApiClient/UsageEvent/ApiFeature/Pricing), §4 คอลัมน์ ApiClient, §6, §7, §8, §9 ข้อ 2-7 = 📋_
