# LICENSE_USAGE_OVERAGE_AUDIT — License · หน้าการใช้งาน · ส่วนเกินโควตา · ราคาพิเศษรายบริษัท · วงจรชีวิตทดลอง/ฟรี/จ่าย (2026-11-03)

_ตรวจกับ HEAD `d9d264a7` (รวมรอบ 229–230) · อ่านอย่างเดียว ไม่แก้โค้ด · ไม่ได้คอมไพล์ · path ย่อจาก `Accounting/` · บรรทัดอ้างอิง ณ HEAD นี้_

> **สถานะการลงมือ รอบ 231 (2026-11-03 · คำตัดสินข้อ 231-8..231-11 · `ACCOUNT_STRUCTURE.md` §6.2f):** ✅ P1 ราคาพิเศษรายบริษัท (§C — 7 จุดเรียกผ่าน `PlatformPriceOverrideStore` · การ์ดแอดมิน customers/account-subscriptions · hub "★ ราคาพิเศษสำหรับคุณ") · ✅ P4 เปลี่ยนรอบบิลยาวขึ้นกลางรอบ (§B.8 — ค้างเฉพาะใบเสร็จ 2 บรรทัดใน issuer) · ✅ P5 `Subscription.Kind` + ต่อสาย 4 ค่าตั้ง `TrialConfig` (ด่านอ่าน · ทางส่งออก · งานปิดข้อมูลที่เคารพ legal hold — เลือก "ต่อสาย" แทน "ถอด" ตาม E7) · ✅ P0 (ค) License audit/เหตุผล + (ง) `AdminChangeLimits` audit + ย้ายไป `CompanyQuotaOverride` กลไกเดียว (migration) · ⏳ P0 (ก)(ข)(จ) · P2 ส่วนเกิน · P3 ประวัติ — ทีม overage/usage · ยังไม่แยก `FreeFeatures` (E7 ส่วนท้าย) · **ยังไม่ได้คอมไพล์ในเครื่องนี้**
_เอกสารตั้งต้นที่ต่อยอด: `erp-review/2026-11-02/QUOTA_ADDON_SUBSCRIPTION_AUDIT.md` (§A inventory 28 แถว · §B โมเดลโควตาเสริม · สถานะรอบ 229/230) ·
`erp-review/2026-10-30/PLATFORM_BILLING_AUDIT.md` · `ACCOUNT_STRUCTURE.md` §6.2b–6.2e · `DECISIONS.md` ข้อ 178 · 229-1..7 · 230-Q1..Q4_

สัญลักษณ์: ✅ มีและทำงาน · ⚠️ มีบางส่วน/มีแต่ไม่ครบ/กระจายหลายที่ · ❌ ไม่มี

---

## 0. คำตอบสั้น (8 คำถามของเจ้าของ)

| # | คำถาม | สถานะ | ประโยคเดียว |
|---|---|---|---|
| 1 | จัดการ License ครบไหม | ⚠️ | แพ็กเกจ/เพดาน/ผูก-ถอดบริษัท/ขยายอายุ/เปลี่ยนสถานะ **ทำได้** แต่ `ChangeStatus`/`Extend`/`ChangeLimits` ของ License **ไม่มีประวัติ ไม่มี audit chain ไม่บังคับเหตุผล** · เพดานรายบริษัทมี **2 กลไกซ้อน** (`Subscription.Max*` กับ `CompanyQuotaOverride`) |
| 2 | หน้าการใช้งาน เดือนนี้ + ย้อนหลัง ครบทุกทรัพยากร | ❌ | มีเฉพาะ **เดือนนี้** · ทรัพยากรแยก **2 หน้า 2 ชุด** (`usage.html` 4 หลอด vs hub `quota/summary` 7 รายการ) · `usage.html` อ่านเพดานจากช่องเดิม **ไม่รวม override/ที่ซื้อเพิ่ม** ⇒ ตัวเลขไม่ตรงกับ hub · "ไม่จำกัด" ยังตัดสิน **4 ที่** · ไม่มีย้อนหลังรายเดือน (ตัวนับถูกล้างตอนขึ้นเดือนโดยไม่เก็บ snapshot) |
| 3 | เกินโควตาตอนนี้เกิดอะไร | ✅ (เอกสารแล้ว) | ต่อทรัพยากรต่างกันโดยตั้งใจ — ตาราง §A.3 · **เอกสาร** = ใบที่กฎหมายบังคับออกต่อ + มิเตอร์ `documents.overage` เข้าใบแจ้งหนี้รายเดือน (ราคามักเป็น 0 เพราะไม่ได้ตั้ง) · OCR/ที่นั่ง/เว็บ/ที่พัก/สื่อ CMS = บล็อกพร้อมข้อความ+ลิงก์ซื้อ · ข้อความช่องกลาง = ไม่บล็อกงาน แค่ไม่ส่งทางช่องกลาง |
| 4 | เตือนใกล้เต็ม | ⚠️ | เอกสาร 80/100% และข้อความช่องกลาง 80/100% แจ้งเจ้าของผ่าน `NotificationEngine` (ในแอป+อีเมล ค่าตั้งต้น · LINE ตามค่าตั้ง) · **OCR/ที่นั่ง/พื้นที่/เว็บ/ที่พัก ไม่มีการแจ้ง** (เห็นเฉพาะตอนเปิดหน้า) · เกณฑ์เตือนฝัง 3 ชุด (80 · 70/90 · 80) แอดมินตั้งไม่ได้ · ลิงก์ในข้อความชี้ `/pages/addons.html` ไม่ใช่ hub |
| 5 | นโยบายส่วนเกิน (ผู้เช่าเลือกล็อก/ยอมจ่าย · ราคาต่อหน่วย · ผ่อนผัน · ล็อกเมื่อไม่จ่าย · ใบกำกับ · จ่ายใน hub) | ❌ | **ยังไม่มีเป็นนโยบาย** — มีชิ้นส่วนครบพอประกอบ: มิเตอร์ `UsageEvent` + `UsageInvoicingJob` (ใบแจ้งหนี้หลายบรรทัด §86/4) + `PlatformDunningJob` (ผ่อนผัน/ระงับ) + hub ชำระออนไลน์/สลิป + ตัดบัตรอัตโนมัติ · ขาด: ตัวเลือกของผู้เช่า · ตารางราคาส่วนเกินต่อทรัพยากร/แพ็กเกจ · เพดานค่าใช้จ่าย · ล็อกส่วนเกินเมื่อค้างชำระ · การแจ้ง · audit — ดีไซน์ §B · **proration รองรับทุกรอบ 1/3/6/12 แล้ว แต่เปลี่ยนรอบบิลกลางรอบ (รายเดือน→รายปี) ยังไม่ได้ — มีผลวันครบรอบ** ดีไซน์ §B.8 |
| 6 | หน้าเดียวเห็นครบ ทำได้ครบ | ⚠️ | hub 4 แท็บมีแล้ว (รอบ 230) · ยังขาด: ส่วนเกิน/นโยบาย · ประวัติรายเดือน · ตัวนับเอกสาร/ข้อความยังอยู่คนละหน้า (`usage.html`/`addons.html` ยังเปิดได้ · เมนู `inHub`) · License ของเจ้าของเป็นอีกหน้า · AI ไม่แสดง |
| 7 | แอดมินยืดหยุ่น · ราคาพิเศษรายบริษัทคงทน | ❌ | รอบ 230 ทำ **แก้ยอดครั้งเดียว** บนรายการที่ยังไม่อนุมัติ (`OverridePaymentAmountAsync`) · คูปองไม่ผูกบริษัท (`MaxUsesPerCompany` ตั้งต้น 1) · ส่วนลดแปลงทดลองใช้รายบริษัทใช้ครั้งเดียว · **ใบต่ออายุ/ส่วนต่างอัปเกรด/ดูราคา ใช้ราคาแม่แบบเสมอ** — ดีไซน์ `CompanyPlanPriceOverride` + resolver เดียว §C |
| 8 | ทดลอง vs ฟรี vs จ่าย แยกถูกไหม | ⚠️ | แยกได้ด้วย `IsPermanentFree` + `Status` แต่ **ใช้ enum เดียวกัน `SubscriptionPlan.FreeTrial`** ทั้ง "ทดลองใช้แพ็กเกจ X" และ "Free Edition" · ฟีเจอร์ฟรี = `FeatureFlags.TrialFeatures` ชุดเดียวกับทดลอง · หมดทดลอง ⇒ `Expired` อ่านได้/เขียน 402 หลังผ่อนผัน **เฉพาะโหมด Enforce** (ค่าตั้งต้น Shadow = ไม่บล็อกใคร) · ค่าตั้ง `TrialConfig.BlockAccessOnExpiry / AllowDataExportAfterExpiry / DeleteDataAfterGracePeriod / DataRetentionDays` **ไม่มีผู้อ่านเชิงธุรกิจ** (F2 ข้อ 2) · ไม่มีนโยบายข้อมูลหลังหมดอายุที่ระบบทำจริง |

---

## A. ตารางสถานะต่อคำถาม (หลักฐาน file:line)

### A.1 การจัดการ License (Account Plan) ฝั่งแอดมิน

| ความสามารถ | สถานะ | หลักฐาน | ช่องว่าง |
|---|---|---|---|
| แพ็กเกจ CRUD (ราคา 4 รอบ · เพดาน · ฟีเจอร์ · ทดลอง) | ✅ | `wwwroot/admin/plans.html` (ช่อง monthly/quarterly/semiAnnual/annualPrice · maxUsers/maxCompanies/maxStorageBytes/maxOcrPagesPerMonth/maxSharedMessagesPerMonth · trial* · isPermanentFree) · `SubscriptionService.CreatePlanTemplateAsync` `:1645` | `MaxJournalEntriesPerMonth` ยังเป็นช่องแม้ไม่บังคับ (229-7 ติดป้ายแล้ว) |
| เพดานต่อแพ็กเกจสำหรับทรัพยากรใหม่ (เว็บ/หน้า/สินค้า/ที่พัก) | ✅ | `AdminQuotaController.Allowances/SetAllowances` `:51` `:68` → `PlanQuotaAllowance` (`Models/Entities/QuotaAddOn.cs:35`) · ลำดับใน `Helpers/QuotaLimitPolicy.Effective` `:50-64` | เพดานอยู่ **2 ที่** (ช่องเดิมของ `PlanTemplate` สำหรับ users/storage/ocr · ตาราง allowance สำหรับ cms/lodging) — ตั้งใจ (229 "ไม่ทำสำเนาที่สอง") แต่หน้าแอดมินต้องเปิด 2 หน้า |
| override รายบริษัท | ✅ รอบ 231 (231-11 · กลไกเดียว `QuotaOverrideWriter` + migration) · เดิม ⚠️ | (ก) `AdminController.AdminChangeLimits` `:1721-1748` เขียน `Subscription.MaxUsers/MaxDocumentsPerMonth/MaxStorageBytes/…` — ประวัติ `AdminLimitsChanged` ไม่เก็บค่าเก่า/ใหม่ ไม่มี `AddChainedAuditLog` ไม่บังคับเหตุผล · (ข) `AdminQuotaController.SetOverride` `:206` → `CompanyQuotaOverride` (มี `Reason/SetBy/SetAt` `QuotaAddOn.cs:45-51`) | **สองกลไกซ้อนสำหรับ users/storage**: `CompanyQuotaOverride` ชนะ `Subscription.MaxUsers` (`QuotaLimitPolicy:55`) แต่ `GetUsageDetailAsync` อ่านเฉพาะ (ก) `SubscriptionService.cs:2047-2050` ⇒ แอดมินแก้ที่ (ข) แล้วหน้า `usage.html` ไม่เปลี่ยน |
| ผูก/ถอดบริษัทกับ License | ✅ | แอดมิน `AdminAccountSubscriptionController.AdminDetach/AdminAttach` `:224` `:261` (+ประวัติ `:239` `:296`) · เจ้าของ `AccountSubscriptionController.attach/detach` `:187` `:228` · auto-attach `CompanyService.cs:193` | — |
| ขยายอายุ License | ✅ รอบ 231 (เหตุผล+ประวัติ+audit+ล็อก) · เดิม ⚠️ | `AdminAccountSubscriptionController.Extend` `:172-187` — เซ็ต `EndDate` + คืน Active | **ไม่มี `SubscriptionHistory`/`AuditLog`/เหตุผล** · ไม่ตรวจว่ามีใบต่ออายุค้าง (ต่างจากเส้นบริษัทที่ผ่าน `SubscriptionPaymentLock`) |
| ระงับ/เปลี่ยนสถานะ License | ✅ รอบ 231 (เหตุผล+ประวัติทุกบริษัทลูก+audit) · เดิม ⚠️ | `ChangeStatus` `:156-167` — รับทุกค่า enum ไม่มีเหตุผล ไม่มีประวัติ · มีผลกับทุกบริษัทลูกผ่าน `GetEffectivePlanAsync` `:1207-1212` (ดู `acct.Status`) ✅ | ระงับ License ⇒ บริษัทลูกถูกบล็อกโดยที่ประวัติของบริษัทไม่มีร่องรอย · ไม่มี "ระงับชั่วคราวถึงวันที่" · ไม่แจ้งเจ้าของ |
| ประวัติ/audit ของ License | ✅ รอบ 231 (`WriteLicenseAuditAsync`) · เดิม ❌ | `History` `:314` อ่าน `SubscriptionHistories.AccountSubscriptionId` — มีเฉพาะแถวจากผูก/ถอด/อนุมัติสลิป (cascade) | 3 คำสั่งข้างบนไม่เขียนประวัติ · ไม่มี `AddChainedAuditLog` ในคอนโทรลเลอร์นี้เลย (กฎ M "audit append-only") |
| หน้าแอดมิน | ✅ | `admin/account-subscriptions.html:141-144` ปุ่ม ดู/ขยายอายุ/ปรับ Limits/เปลี่ยนสถานะ · สร้าง `:343` | ไม่แสดงบริษัทลูกพร้อมการใช้งานรวม · ไม่แสดงใบแจ้งหนี้/การชำระของ License |
| โหมดบังคับ (Off/Shadow/Enforce) | ✅ | `admin/subscription-enforcement.html` · `SubscriptionEnforcementResolver` (ไม่มีแถว = Shadow) | เจ้าของต้องรู้ว่า "ทุกด่านหมดอายุ" ไม่บล็อกจริงจนกว่าจะกด Enforce |

### A.2 หน้าการใช้งานของผู้เช่า

| หัวข้อ | สถานะ | หลักฐาน | ช่องว่าง |
|---|---|---|---|
| เดือนนี้ — เอกสาร/OCR/ผู้ใช้/พื้นที่ | ✅ | `SubscriptionService.GetUsageDetailAsync` `:1997-2150` → `UsageBarRule` 4 หลอด (`:2104-2110`) · `pages/usage.html:64-70` | เพดานมาจาก `sub.MaxUsers/MaxStorageBytes/MaxDocumentsPerMonth` ดิบ (`:2047-2050`) — ไม่ผ่าน `QuotaLimitPolicy` ⇒ **ไม่รวม override แอดมิน + ที่นั่ง/GB ที่ซื้อเพิ่ม** (รอบ 229 แพ็ก 5) · เอกสารไม่บวก `DocumentBonusQuota` ขณะที่ด่านจริงบวก (`:1327-1329`) |
| เดือนนี้ — ข้อความช่องกลาง | ✅ | `usage.html:60` `notifyQuotaCard` · `MeteringController.GetNotifyQuota` `:361` | อยู่นอก `Bars` (การ์ดแยก) |
| เดือนนี้ — เว็บ CMS / ที่พัก / หน้าต่อเว็บ | ⚠️ | เฉพาะ hub `billing.html#tab=usage` ผ่าน `QuotaController.Summary` `:46` → `QuotaGateService.SummaryAsync` (7 รายการ `QuotaResources.All`) | `usage.html` ไม่มี · hub ไม่มีเอกสาร/ข้อความ ⇒ ผู้เช่าต้องเปิด 2 หน้าถึงเห็นครบ |
| การเรียก AI | ⚠️ | แอดมิน `pages/admin-ai-usage.html` + `AdminCompanyUsageController` (`AiCallsPaid/AiCostUsd` `:60`) | ไม่แสดงต่อผู้เช่า — **ถูกต้องตามกฎเหล็ก #1** (ไม่ขายเป็นโควตา) แต่ควรแสดง "ข้อมูลอย่างเดียว" ใน hub |
| ย้อนหลังรายเดือน | ❌ | ตัวนับ `CurrentMonth*` ถูกล้างใน `SubscriptionUsageRollover.RollIfDue` (`SubscriptionService.cs:1302`) **โดยไม่เก็บ snapshot** · `GET metering/usage?year&month` (`MeteringController.cs:421`) ดูย้อนหลังได้เฉพาะ `UsageEvent` (ส่วนเกิน/ส่วนเสริม/API) และไม่มีหน้าเรียก · แอดมินมี `pages/admin-company-usage.html` รายเดือนจากตารางต้นทาง (Documents/OcrScans/AiCalls) | ผู้เช่าไม่มีทางดู "เดือนที่แล้วใช้ไปเท่าไร" — ดีไซน์ §D |
| "ไม่จำกัด" ตัดสินที่เดียว | ⚠️ | เซิร์ฟเวอร์: `UsageBarRule` (`≥999` ผู้ใช้ · `≥99999` เอกสาร · `<0` OCR · `≥UnlimitedBytes`) `:15-16,43,49,56,65` · `QuotaLimitPolicy` (ค่าลบ) `:22,61` · `QuotaStorageLimit:14` · หน้าเว็บ: `js/plan-display.js:11` `UNLIMITED_AT {users:999, companies:99, docs:99999}` · `pages/subscription.html:295` literal `>= 99999` | เกณฑ์ 2 ตระกูล (ค่าลบ vs ค่าใหญ่) + สำเนาใน JS 2 ที่ (F2 ข้อ 5) · ต้องเหลือ `UsageBarRule`/`QuotaLimitPolicy` คู่เดียวและหน้าเว็บอ่าน `limitText/unlimited` เท่านั้น |

### A.3 เกินโควตาวันนี้ — ต่อทรัพยากร

| ทรัพยากร | เมื่อเกิน | ข้อความ/RuleCode | หลักฐาน | หมายเหตุ |
|---|---|---|---|---|
| เอกสาร/เดือน | **นุ่ม**: ใบที่กฎหมายบังคับ (ใบกำกับ/ใบเสร็จ/CN/DN) ออกต่อ + มิเตอร์ `documents.overage` 1 ฉบับ = 1 `UsageEvent` · ใบเสนอราคา/PO (`CountedBlockable`) ถูกปฏิเสธ | `DocumentQuotaPolicy.BlockedMessage` · `QUOTA-DOCUMENTS` | `DocumentService.cs:1082-1087` · `:4199-4215` · `Helpers/DocumentQuotaPolicy.cs:143-149` | ราคาส่วนเกินจาก `ApiPricingPlan` ของ `documents.overage` — ไม่ตั้ง = 0 (`UsageMeteringService.cs:83`) ⇒ **วันนี้เป็น "ยอมเกินฟรี" โดยผู้เช่าไม่ได้เลือก** · เข้าใบแจ้งหนี้รายเดือน `UsageInvoicingJob` (ขั้นต่ำ 50 บาท ยกยอด) |
| OCR หน้า/เดือน | **บล็อก** 429 | ข้อความ + `QUOTA-FULL:ocr.pages.month` + `buyUrl` | `Controllers/OcrController.cs:195-204` | Azure หมด → local ก่อน (`FallbackToLocalWhenAzureExhausted`) · เครดิตที่ซื้อ (โควตาเสริม) รวมในตัวนับ |
| ผู้ใช้ (ที่นั่ง) | **บล็อก** ตอนเชิญ/เพิ่ม | `QuotaLimitPolicy.Decide` "เพิ่มผู้ใช้ไม่ได้ — สิทธิ์ของคุณมี N … ของเดิมทั้งหมดยังใช้…" + `QUOTA-FULL:users` | `CompanyService.cs:406` · `QuotaLimitPolicy.cs:72-92` | นับสมาชิก + คำเชิญค้าง |
| พื้นที่ | **บล็อกเฉพาะสื่อ CMS** · ไฟล์แนบเอกสาร = เตือนเท่านั้น | — | `CmsContentService.cs:570` → `CanFitStorageAsync` `SubscriptionService.cs:1387` · คอมเมนต์ `:1396` | นโยบายไม่สมมาตร (FILE_STORAGE_AUDIT) |
| เว็บไซต์ CMS | **บล็อกสร้างใหม่** · เว็บเดิมออนไลน์/แก้ได้ (229-2) | `QUOTA-FULL:cms.sites` | `CmsSiteService.cs:23-27` + ด่านใน `CreateSiteAsync` | หน้า/สินค้าต่อเว็บ: `Site.MaxPages/MaxProducts` → allowance → null = ไม่จำกัด |
| ที่พัก | **บล็อกแห่งที่ 2+** เว้นมีสวิตช์ `lodging.multi-property` หรือซื้อจำนวน | `QUOTA-FULL:lodging.properties` | `Helpers/LodgingPropertyQuota.cs` · `QuotaGateService.cs:128-130` | — |
| ข้อความช่องกลาง (LINE OA/SMTP ของ NextAcc) | **ไม่บล็อกงาน** — ไม่ส่งทางช่องกลาง / ส่งทางช่องของร้านพร้อมป้าย | `NotifyQuotaPolicy.RoutedToTenantWarning` | `Helpers/NotifyQuotaPolicy.cs:84` · `NotifyQuotaService.cs:176-205` | ต้นแบบที่ดี: โชว์ราคา + 409 เมื่อข้อเสนอเปลี่ยน |
| รายการบัญชี/เดือน | ไม่บังคับ (229-7) | — | — | — |
| API ภายนอก (ConnectedApi) | มิเตอร์ต่อ call · postpaid เข้าใบรายเดือน / prepaid ตัดเครดิต | — | `UsageMeteringService.RecordAsync` `:33` | — |
| ส่วนเสริมสวิตช์ | ปิดเมื่อแพ็กเกจไม่ active / ไม่ได้เปิด | `EntitlementService.CheckAsync` ①–③ | `Services/Implementations/EntitlementService.cs` | — |

สรุป: พฤติกรรมเกินโควตา **ฝังต่อทรัพยากรในโค้ด** — ไม่มีจุดเดียวที่ผู้เช่า/แอดมินเลือก "ล็อก" หรือ "ยอมจ่ายส่วนเกิน" · เอกสารเป็นทรัพยากรเดียวที่มีมิเตอร์ส่วนเกิน และมักคิด 0 บาท

### A.4 เตือนใกล้เต็ม

| ทรัพยากร | ในแอป | อีเมล/LINE | เกณฑ์ | หลักฐาน |
|---|---|---|---|---|
| เอกสาร | ✅ แจ้งเตือนเจ้าของครั้งเดียวต่อระดับต่อเดือน (`quota.documents_level`) | ✅ ผ่าน `NotificationEngine.DispatchAsync` (ค่าตั้งต้น System+Email · LINE ตาม `NotificationPreferences`) | 80% / 100% (`DocumentQuotaPolicy.WarnLevel:205`) | `SubscriptionService.cs:1559-1633` · ส่งหลัง commit (`FlushPendingQuotaAlertsAsync:1605`) | ลิงก์ในข้อความ `/pages/addons.html` (`:1632`) — ควรเป็น `QuotaResources.BuyUrl` |
| ข้อความช่องกลาง | ✅ `NotifyQuota80/100` | ✅ (เหตุการณ์เดียวกัน) | 80/100 (`NotifyQuotaPolicy.WarnLevel:65`) | `Models/Constants/NotificationEvents.cs:131-132` |
| OCR | ⚠️ เฉพาะ `Alerts` ตอนเปิด `usage.html` (`:2127-2131`) | ❌ | 70/90 (`UsageBarRule:13-14`) | `OcrQuotaService.cs` ไม่มีจุดเตือน |
| ผู้ใช้ / พื้นที่ | ⚠️ `Alerts` ตอนเปิดหน้า (`:2112-2122`) | ❌ | 70/90 · ผู้ใช้ = "เหลืออีก 1" | — |
| เว็บ / ที่พัก / หน้า-สินค้าต่อเว็บ | ⚠️ ระดับสีใน `quota-ui.js` (`QuotaLimitPolicy.WarnLevel:104` 80%) | ❌ | 80 | — |
| แถบเตือนทั่วระบบ (layout) | ⚠️ มีเฉพาะหมดอายุ/ผ่อนผัน (`X-Subscription-Grace` `SubscriptionMiddleware.cs:194`) | — | — | ไม่มีแถบ "โควตา X ใกล้เต็ม" บนทุกหน้า |
| แอดมินตั้งเกณฑ์ | ❌ | — | ฝัง 3 ชุดต่างกัน (80 · 70/90 · 80) | ควรเหลือ `QuotaWarnPolicy` ตัวเดียว + ค่าตั้งใน `PlatformBillingSettings` |

### A.5 นโยบายส่วนเกิน · proration

| ชิ้น | สถานะ | หลักฐาน |
|---|---|---|
| ผู้เช่าเลือก "ล็อก" / "ยอมจ่ายส่วนเกิน" | ❌ | ไม่มี entity/หน้า/ด่านใดอ่านค่าเช่นนี้ (grep `Overage` พบเฉพาะ `AddOnCodes.DocumentsOverage/LodgingEmailOverage`) |
| ราคาส่วนเกินต่อทรัพยากร (และต่อแพ็กเกจ) | ⚠️ | เฉพาะ `documents.overage` ผ่านแคตตาล็อก `ApiFeature/ApiPricingPlan` (`admin-addons.html`) — ไม่มีต่อแพ็กเกจ · OCR/ข้อความ/พื้นที่ไม่มีรหัสส่วนเกิน |
| ออกใบแจ้งหนี้ส่วนเกินรายเดือน (§86/4 จาก tenant ผู้ให้บริการ) | ✅ (โครง) | `UsageInvoicingJob` (รอบ 6 ชม. · ปิดงวดที่ผ่านแล้ว · หลายบรรทัด · ขั้นต่ำ 50) → `PlatformBillingDocumentIssuer.IssueUsageInvoiceAsync` `:360` (reference `USAGE-`) |
| ผ่อนผัน · ล็อกเมื่อไม่จ่าย | ⚠️ | `PlatformDunningPolicy.Decide` `:79-105` (`SuspendGraceDays` ตั้งต้น 14 · ระงับทั้ง subscription เมื่อ Enforce) — ไม่แยก "ล็อกเฉพาะส่วนเกิน" ก่อนระงับ |
| จ่ายผ่าน hub (ออนไลน์/สลิป/บัตรบันทึก) | ✅ | `BillingCenterController` `:103` สลิป · `:170` ออนไลน์ · `PlatformAutoChargeService` + `PlatformAutoChargeMandate` (`PlatformCardBilling.cs:48`) |
| เพดานค่าใช้จ่ายส่วนเกินต่อเดือน (กันบิลช็อก) | ❌ | — |
| audit | ⚠️ | `UsageEvent` มี `RefEntity` · ไม่มี audit การเปลี่ยนนโยบาย/ราคา (เพราะยังไม่มี) |
| proration ทุกรอบ 1/3/6/12 | ✅ | `PlatformProration.Upgrade` `:32-54` (`cycleMonths` จาก `PlatformPlanPricing.MonthsOf` · C = EndDate − เดือนของรอบ) · `SubscriptionService.ProrationFor` `:898-902` |
| เปลี่ยนรอบบิลกลางรอบ (รายเดือน→รายปี) | ✅ รอบ 231 (231-9 `PlatformProration.CycleChange` · รอบยาวขึ้นเท่านั้น) · เดิม ❌ | `ChangeSubscriptionAsync` `:846-862`: อัปเกรดคิดส่วนต่าง **บนรอบเดิม** และบอก "รอบบิลคงเดิม — เปลี่ยนรอบบิลได้หลังอัปเกรด (มีผลวันครบรอบ)" · แพ็กเกจเดิม+เปลี่ยนรอบ = `Scheduled*` มีผลวันครบรอบ (`:869-881`) — ดีไซน์ §B.8 |

### A.6 หน้าเดียว (hub)

| มีแล้ว (รอบ 230) | ยังขาด |
|---|---|
| `pages/billing.html` 4 แท็บ `#tab=plan|usage|buy|docs` (`:53-57`) · `#buy=<resource>` เปิดหน้าต่างซื้อ · `plan-quote` ก่อนยืนยัน · `canPayNow` · ใบแจ้งหนี้/ใบเสร็จ PDF · สลิป/ออนไลน์ · บัตรบันทึก (`js/billing-saved-cards.js`) | (1) แท็บ "ใช้ไปเท่าไร" ไม่มีเอกสาร/ข้อความ/AI (อยู่ `usage.html`/`addons.html` — เมนูยัง `inHub` 3 รายการ `js/layout.js:1771-1777`) · (2) ไม่มีส่วน "ส่วนเกินเดือนนี้ · นโยบาย · ประมาณการบิล" · (3) ไม่มีย้อนหลัง · (4) License ของเจ้าของอยู่ `account-subscription.html` (`layout.js:1764`) · (5) ไม่มีตัวกรอง/ค้นหาใบแจ้งหนี้เก่า · (6) ประวัติการเปลี่ยนแพ็กเกจ (`SubscriptionHistory`) ไม่แสดงใน hub |

### A.7 แอดมินยืดหยุ่น · ราคาพิเศษ

| กลไกวันนี้ | ขอบเขต | หลักฐาน |
|---|---|---|
| แก้ยอดรายการชำระ (ครั้งเดียว) | เฉพาะรายการ Pending/UnderReview ที่ไม่ผูกใบแจ้งหนี้ ไม่มี intent ค้าง · เหตุผลบังคับ · audit `PLATFORM-ADMIN-PRICE-OVERRIDE` | `SubscriptionService.OverridePaymentAmountAsync` `:971-1024` · `admin/payments.html:255` |
| คูปอง | รหัสสาธารณะ · ไม่ผูกบริษัท · `MaxUsesPerCompany` ตั้งต้น 1 · `FirstPaymentOnly` | `Models/Entities/PlatformCoupon.cs:19-45` |
| ส่วนลดแปลงทดลองใช้รายบริษัท | `TrialConfig.DiscountPercentOnConversion/ConversionPromoCode/DiscountValidUntil` — ใช้ตอน Convert ครั้งเดียว | `SubscriptionService.cs:381-386` · `QuotePlanChangeAsync:905-910` |
| ต่ออายุฟรี/ยกเว้น | `SubscriptionPaymentKind.Waived` + `WaiveReason` | `AllEnums.cs:859-872` |
| **ราคาที่ใบต่ออายุใช้** | ✅ รอบ 231 ราคาพิเศษ (`PriceForAsync` → `PlatformPriceOverrideStore`) · เดิม **ราคาแม่แบบเสมอ** | `SaasBillingDocumentService.PriceFor` `:554` ← `:416` `:537` |
| **ราคาที่ส่วนต่างอัปเกรดใช้** | ✅ รอบ 231 ราคาพิเศษทั้งสองฝั่ง (`ProrationForAsync`) · เดิม **ราคาแม่แบบ** | `ProrationFor` `:898-902` |
| **ราคาที่ `plan-quote`/`RequestPlanPaymentAsync` ใช้** | `PlatformPlanPricing.AmountDue(template, cycle, discount%)` | `PlatformPlanPricing.cs:36-42` |
| ⇒ "บริษัท A จ่ายราคา Standard แต่ได้ Pro" แบบถาวร | ✅ รอบ 231 `CompanyPlanPriceOverrides` โหมด PriceOfPlan · เดิม ❌ | ไม่มี entity ใดเก็บ — ดีไซน์ §C |

### A.8 วงจรชีวิต: สมัคร → ทดลอง / ฟรี / จ่าย

| เรื่อง | สถานะ | หลักฐาน | ช่องว่าง |
|---|---|---|---|
| สมัครใหม่ได้อะไร | ✅ | `CompanyService.EnsureSubscriptionForNewCompanyAsync` `:135-161` → `StartTrialAsync(companyId, preferredPlan ?? FreeTrial)` · แม่แบบ `Free Edition` (`Data/SeedPlanTemplates.cs:144-168` `IsPermanentFree=true` · 1 ผู้ใช้ · 30 เอกสาร · 100 MB · `TrialFeatures`) ⇒ **ไม่เลือกแพ็กเกจ = ฟรีถาวร Active** · เลือก Basic/Pro = `Status=Trial` ใช้ `Trial*` ของแม่แบบนั้น | — |
| ทดลอง vs ฟรี แยกกันอย่างไร | ✅ รอบ 231 `Subscription.Kind` (`SubscriptionKindRule` + backfill) · ยังไม่แยก `FreeFeatures` · เดิม ⚠️ | enum `SubscriptionPlan.FreeTrial=0` ใช้ทั้งสองความหมาย · แยกด้วย `Subscription.IsPermanentFree` + `Status` (`SubscriptionService.cs:99-101`) | รายงาน/ตัวกรองแอดมิน/ตัวตัดสินต้องรู้ 2 ช่องเสมอ · ชุดฟีเจอร์ฟรี = `TrialFeatures` ของแม่แบบ FreeTrial (ไม่มี `FreeFeatures` แยก) — ถ้าอยากให้ฟรี "น้อยกว่า" ทดลอง ต้องแยกแม่แบบ · ฝ่ายค้าน B1: ไม่เคยจ่าย = LastPaymentDate ว่าง + ไม่มีรายการ Approved · แพ็กเกจจ่ายเงินหมดอายุไม่มีประวัติทดลอง = Paid · `AdminChangePlan` เขียน Kind |
| ขยายทดลอง | ✅ | `ExtendTrialAsync` `:293` · `TrialMaxExtensions/TrialExtensionDays` ต่อแม่แบบ · แอดมินตั้ง `TrialConfig` รายบริษัท `:1960-1980` | — |
| หมดทดลอง | ✅ | `ProcessExpiredTrialsAsync` `:2171-2196` (`BackgroundJobService.cs:83`) ⇒ `Status=Expired` + `TrialStatus.Expired` | ไม่แจ้งเจ้าของจากจุดนี้ (การแจ้งก่อน/หลังหมดอยู่ที่ `AccountPlanExpiryReminderJob` สำหรับ **License** · บริษัทใช้ `NotifyDaysBeforeExpiry` ใน `SubscriptionService` อีกเส้น) |
| หมดแล้วทำอะไรได้ | ✅ รอบ 231 ต่อสาย `BlockAccessOnExpiry` (ด่านอ่าน) + `AllowDataExportAfterExpiry` (ทางส่งออก) · เดิม ⚠️ | `Expired` ไม่ใช่ inactive (`SubscriptionGatePolicy.IsInactive` = Cancelled/Suspended) ⇒ อ่านได้ · เขียน 402 หลังผ่อนผัน (`GetEffectivePlanAsync` `:1244-1250` `TrialConfig.GracePeriodDays`) **เฉพาะโหมด Enforce** · Shadow (ค่าตั้งต้น) = ผ่านหมด | `TrialConfig.BlockAccessOnExpiry` (`Subscription.cs:182`) ถูกเก็บ/echo (`:213,282,1823,1974`) แต่ **ไม่มีผู้อ่านในด่าน** · `AllowDataExportAfterExpiry` `:183` ไม่มีผู้อ่าน · `DeleteDataAfterGracePeriod/DataRetentionDays` `:185-186` ไม่มีผู้อ่าน (ไม่มี job ลบ/anonymize) ⇒ 4 ค่าตั้งไร้ผล (F2 ข้อ 2) |
| แปลงเป็นจ่าย | ✅ | `ConvertTrialAsync` `:365` (ส่วนลด/คูปอง · `QuotedAmount`) · สิทธิ์เมื่อเงินเข้า | — |
| จ่าย → หมดอายุ → ระงับ | ✅ | `ProcessExpiredSubscriptionsAsync` `:3282`: Active→PastDue (`:3360-3372`) → Suspended หลัง `DeactivationDaysAfterExpiry` (ตั้งต้น 14) เมื่อ `AutoSuspendEnabled` (`:3375-3395`) · dunning ใบค้าง `PlatformDunningJob` | สองนาฬิกา (หมดอายุ 14 วัน vs ใบค้าง `SuspendGraceDays` 14 วัน) ตั้งคนละที่ (`Subscription.DeactivationDaysAfterExpiry` รายบริษัท vs `PlatformBillingSettings`) |
| ยกเลิก | ✅ | `CancelAtPeriodEnd` → `Cancelled` สิ้นรอบ (`:3316-3350`) · ใบต่ออายุถูก void | — |
| ดาวน์เกรดเป็นฟรี | ✅ | `Scheduled*` มีผลวันครบรอบ · ฟรีถาวรมีผลทันที (`ScheduledChangeEffectiveAt`) | ข้อมูลเกินเพดานคงอยู่ · บล็อกเฉพาะสร้างใหม่ (229-2) ✅ |
| เก็บ/ลบข้อมูลหลังหมดอายุ | ✅ รอบ 231 `TrialDataRetentionJob` + `TrialDataRetentionPolicy` (เฉพาะทดลองใช้ · legal hold ไม่ลบ · soft-delete · ค่าตั้งต้นปิด) · เดิม ❌ | ไม่มี job ใดลบ/anonymize/ส่งออก | ต้องประกาศนโยบายจริง: เก็บอ่านอย่างเดียว ≥ 5 ปี (พ.ร.บ.บัญชี ม.10) · ลบเฉพาะคำขอ DSR ที่ไม่ติด legal hold — และถอดช่องตั้งค่าที่ไม่มีผลออกจากหน้าแอดมิน · ฝ่ายค้าน B2: กรองในฐาน+paging ทั้งชุด · legal hold 6 เหตุ · แจ้งเจ้าของล่วงหน้า `PurgeNoticeDays` · ผู้ติดต่อผ่าน PDPA erase · `JobRunLog` dry-run · ผู้ใช้/พนักงาน/ไฟล์ไม่แตะ (DECISIONS 231-10) |
| ทดลอง License ครั้งเดียวต่อเจ้าของต่อระดับ | ✅ | `PlatformPlanPricing.MayStartAccountTrial` `:90` | บริษัท: 1 subscription ต่อบริษัท (`StartTrialAsync:76`) แต่เจ้าของสร้างบริษัทใหม่ได้ทดลองใหม่ (ตั้งใจ — §A inventory ข้อ 2 รอบ 229) |

---

## B. ดีไซน์ "ส่วนเกินโควตา" (Overage) — ตัวตั้งตัวเดียว ต่อยอดของที่มี

### B.1 หลัก

1. **ค่าตั้งต้น = ล็อก** (พฤติกรรมวันนี้) · ส่วนเกิน = **opt-in ของผู้เช่า** โดย Owner/`BillingManage` ยอมรับข้อความราคา (เก็บหลักฐานใน `PolicyAcceptances` Scope=platform เหมือนโควตาเสริม)
2. ส่วนเกินคิดได้เฉพาะทรัพยากร **แบบใช้แล้วหมด/วัดเป็นปริมาณ** ที่มีมิเตอร์จริง: `documents` · `ocr.pages.month` · `notify.messages` · `storage.gb` (GB-เดือน) — ทรัพยากรแบบ Capacity ที่เป็น "จำนวนชิ้น" (ที่นั่ง · เว็บ · ที่พัก) **ไม่คิดส่วนเกิน** ให้ซื้อโควตาเสริมเหมือนเดิม (เหตุผล: มี 3 โหมดซื้ออยู่แล้ว · การ "ยอมให้มีที่นั่งเกิน" แล้วคิดรายวันย้อนหลัง = ค่าที่ระบบประทับเองซึ่งผู้เช่าโต้แย้งได้ · F2 ข้อ 3)
3. ราคาส่วนเกิน **เป็นตารางของแอดมิน** ต่อทรัพยากร (+ ต่อแพ็กเกจได้) · **ไม่มีราคา = เปิดส่วนเกินไม่ได้** สำหรับทรัพยากรนั้น (ไม่ใช่คิด 0 แบบ `documents.overage` วันนี้)
4. มิเตอร์ = `UsageEvent` เดิม (idempotent · มี `BilledPeriod`) · ใบแจ้งหนี้ = `UsageInvoicingJob` เดิม · ทวง/ระงับ = `PlatformDunningPolicy` เดิม · ชำระ = hub เดิม — **ไม่สร้างเส้นเงินที่สอง**
5. เพดานค่าใช้จ่ายต่อเดือน (ผู้เช่าตั้งเอง · ว่าง = ไม่จำกัด) — ถึงเพดาน ⇒ กลับเป็นล็อกจนสิ้นเดือน + แจ้ง
6. ค้างชำระใบส่วนเกินเกินผ่อนผัน ⇒ **ขั้นที่ 1 ปิดสิทธิ์ส่วนเกิน** (กลับเป็นล็อก · ของที่ใช้ไปแล้วไม่ถูกถอน) ⇒ **ขั้นที่ 2** ตาม dunning เดิม (ระงับเมื่อ Enforce) — ไม่ระงับทั้งบริษัทเพราะบิลส่วนเกิน 120 บาททันที

### B.2 ข้อมูล (ทั้งหมด `ADD COLUMN/CREATE TABLE IF NOT EXISTS` ผ่าน `DatabaseMigrationHelper` — ไฟล์ `Helpers/OverageMigration.cs`)

```
OverageRates  (ระดับแพลตฟอร์ม · ไม่ใช่ TenantEntity · มีประวัติด้วย EffectiveFrom/To ไม่แก้ทับ)
  ResourceKey        string   ∈ OverageResources (closed list — §B.3)
  PlanTemplateId     Guid?    null = ทุกแพ็กเกจ · แถวที่ระบุแพ็กเกจชนะ
  UnitSize           int      1 ฉบับ · 1 หน้า · 1 ข้อความ · 1 GB-เดือน
  UnitPrice          decimal  (รวม VAT ตาม SiteSettings.PlatformPriceIncludesVat — snapshot ตอนบันทึก event)
  EffectiveFromUtc   DateTime · EffectiveToUtc DateTime?
  IsActive           bool · SetBy · Reason
  unique (ResourceKey, PlanTemplateId, EffectiveFromUtc)

CompanyOveragePolicy  (TenantEntity · 1 แถวต่อบริษัท)
  Mode               enum {HardLock = 1, AllowOverage = 2}   ค่าตั้งต้น HardLock
  MonthlyCapAmount   decimal?  (บาท · null = ไม่จำกัด)
  AcceptedPolicyVersion string? · AcceptedBy · AcceptedAtUtc
  LockedByUnpaidAt   DateTime? (ระบบปิดสิทธิ์ส่วนเกินเพราะค้างชำระ — ผู้เช่าเปิดเองไม่ได้จนกว่าจะชำระ)
  ResourcesCsv       string?   (ว่าง = ทุกทรัพยากรที่มีราคา · ผู้เช่าเลือกบางตัวได้ เช่น ยอมเกินเอกสารแต่ไม่ยอมเกิน OCR)

PlatformBillingSettings (+คอลัมน์)
  OverageGraceDays         int  ตั้งต้น 7   (ครบกำหนดใบส่วนเกิน = วันออก + N)
  OverageUnpaidAction      enum {LockOverageOnly=1, LockThenDunning=2}  ตั้งต้น 2
  OverageMinInvoiceAmount  decimal ตั้งต้น 50 (ยกยอด — ใช้ค่าเดียวกับ UsageInvoicingJob.MinInvoiceAmount ย้ายมาเป็นค่าตั้ง)
  QuotaWarnPercents        string ตั้งต้น "80,100" (เกณฑ์เตือนเดียวทั้งระบบ — §B.6)
  OverageAutoChargeSavedCard bool ตั้งต้น true (ถ้ามี mandate)

UsageEvent (เดิม) — FeatureCode ใหม่: "overage.<resource>"  (คง documents.overage เดิมเป็น alias ตอนอ่าน · เขียนใหม่ใช้ overage.documents)
```

ทำไมไม่ใช้ `ApiFeature/ApiPricingPlan` สำหรับราคา: แคตตาล็อกนั้นไม่มีมิติ "ต่อแพ็กเกจ" และรหัสเปิดอิสระ (บทเรียน `lodging.promo`) — ตารางเล็กที่มี closed list ปลอดภัยกว่า · `UsageMeteringService.ResolvePlanAsync` เพิ่มทางแยก: รหัส `overage.*` ⇒ อ่าน `OverageRates` (ตัวตัดสิน `Helpers/OverageRateResolver.Pick(rates, resource, planTemplateId, nowUtc)` pure)

### B.3 Closed list + มิเตอร์ (ตัวเขียนเดียวต่อทรัพยากร)

| ResourceKey | หน่วย | จุดตัดสินวันนี้ (แก้ให้ถาม policy) | วิธีวัดส่วนเกิน |
|---|---|---|---|
| `documents` | ฉบับ | `DocumentService.cs:1083` — เมื่อ `AllowOverage` ⇒ `CountedBlockable` ก็ออกได้ และทุกฉบับที่เกินเพดาน (รวมที่กฎหมายบังคับ — เหมือนเดิม) ⇒ `RecordDocumentOverageAsync` `:4199` (เปลี่ยนรหัสเป็น `overage.documents`) · `HardLock` = พฤติกรรมเดิมทุกประการ | ต่อฉบับ ณ เวลาสร้าง |
| `ocr.pages.month` | หน้า | `OcrController.cs:195` — `TryConsumeAsync` คืน `Overage=true` แทน false เมื่อ policy อนุญาตและยังไม่ถึง cap ⇒ สแกนต่อ + `RecordAsync(overage.ocr.pages, 1, key ocr-overage:{scanId})` | ต่อหน้า ณ เวลาสแกน (Azure เท่านั้น — local ไม่คิด) |
| `notify.messages` | ข้อความ | `NotifyQuotaService.cs:~200` — เต็ม + policy อนุญาต ⇒ ส่งทางช่องกลางต่อ + `RecordAsync(overage.notify.messages)` · ไม่อนุญาต = เส้นเดิม (ไปช่องของร้าน) | ต่อข้อความ |
| `storage.gb` | GB-เดือน | ไม่มีจุดตัดสินแบบ event — ใช้ **snapshot รายวัน** (งาน §B.5) · `CanFitStorageAsync` เมื่อ policy อนุญาต ⇒ ผ่าน | สิ้นเดือน: `ceil(avg(max(usedGb − limitGb, 0)) รายวัน)` 1 event · คำอธิบาย "พื้นที่เกินเพดาน เฉลี่ย 2.3 GB × 30 วัน" |

ด่านเดียว: `IQuotaGate.ConsumeAsync(companyId, resource, qty, ct)` คืน `QuotaDecision` + ฟิลด์ใหม่ `OverageUnits int` และ `OverageBlockedReason` (ไม่มีราคา · ถึงเพดานเงิน · ถูกล็อกเพราะค้างชำระ · ไม่ได้เปิด) — ตัวตัดสิน pure `Helpers/OveragePolicy.Decide(limit, used, adding, policy, rate, spentThisMonth, nowUtc)` + เทสต์ทิศทั้งสอง (ล็อกยังล็อก · เปิดแล้วผ่านและนับ · ถึง cap กลับไปล็อก · ไม่มีราคา = ล็อกพร้อมเหตุ) · ล็อกจุดเรียกด้วย `tools/required_call_site_check.py` (3 จุดเขียน + 1 งาน)

### B.4 ตารางพฤติกรรมด่าน (ทรัพยากร × โหมด)

| ทรัพยากร | HardLock (ตั้งต้น) | AllowOverage | AllowOverage แต่ถึง cap / ไม่มีราคา / ล็อกเพราะค้าง |
|---|---|---|---|
| เอกสาร — ใบที่กฎหมายบังคับ | ออกได้ · **ไม่คิดเงิน** (คงนโยบาย "ห้ามขวางใบกำกับ") · มิเตอร์นับเป็นข้อมูล (ราคา 0 · `CoveredByFreeQuota=false, ChargedAmount=0`, flag `LegallyRequired`) | ออกได้ · คิดส่วนเกินต่อฉบับ | ออกได้ · ไม่คิดเงิน (เหมือน HardLock) |
| เอกสาร — ใบเสนอราคา/PO | 409 `QUOTA-DOCUMENTS` (เดิม) | ออกได้ · คิดส่วนเกิน | 409 พร้อมเหตุ `OVERAGE-CAP` / `OVERAGE-NO-RATE` / `OVERAGE-LOCKED` |
| OCR | 429 (เดิม) | สแกนต่อ · คิดต่อหน้า | 429 พร้อมเหตุ |
| ข้อความช่องกลาง | ส่งทางช่องของร้าน/ไม่ส่ง (เดิม) | ส่งทางช่องกลางต่อ · คิดต่อข้อความ | เส้นเดิม |
| พื้นที่ (สื่อ CMS) | บล็อก (เดิม) | อัปโหลดได้ · GB-เดือนเกิน | บล็อก |
| พื้นที่ (ไฟล์แนบ) | เตือน (เดิม) | เตือน + คิด | เตือน (ไม่คิด) |
| ที่นั่ง / เว็บ / ที่พัก / หน้า-สินค้าต่อเว็บ | บล็อกสร้างใหม่ + ลิงก์ซื้อ (เดิม) | **เหมือน HardLock** (ไม่อยู่ใน list) | — |

ข้อความปฏิเสธ (ต่อท้าย `QuotaLimitPolicy.Decide`): "…หรือเปิด "ใช้ต่อได้ คิดค่าส่วนเกิน" ที่หน้าแพ็กเกจและการชำระเงิน (เอกสารละ ฿x)" เฉพาะเมื่อมีราคาและไม่ถูกล็อก

### B.5 งานเบื้องหลัง (ทุกตัวผ่าน `Helpers/JobLock` · F4 ข้อ 6)

| งาน | ทำอะไร | ของเดิมที่ใช้ |
|---|---|---|
| `StorageOverageSnapshotJob` (รายวัน) | ต่อบริษัทที่ `AllowOverage` และมีราคา storage: บันทึก `UsageDailySnapshot(companyId, date, resource=storage.gb, used, limit)` · สิ้นเดือนไทย ⇒ 1 `UsageEvent overage.storage.gb` | `GetStorageStatusAsync` (เพดานรวม override/ซื้อ) |
| `UsageInvoicingJob` (เดิม · 6 ชม.) | ปิดงวด: รวม `overage.*` + ค่ารายเดือนโควตาเสริม + API เป็นใบเดียว · **ครบกำหนด = วันออก + `OverageGraceDays`** (เดิมรับ `dueDate` อยู่แล้ว `IssueUsageInvoiceAsync:361`) · ขั้นต่ำจาก `OverageMinInvoiceAmount` · บรรทัด: "ส่วนเกินโควตาเอกสาร เดือน พ.ย. 2569 · 37 ฉบับ × ฿5.00" (Qty × UnitPrice = LineAmount — §86/4) | `IssueUsageInvoiceAsync` · reference `USAGE-{companyId}-{yyyyMM}` idempotent |
| `PlatformDunningJob` (เดิม) | ใบ `USAGE-` ค้างเกิน `OverageGraceDays` ⇒ **ขั้น 1** `CompanyOveragePolicy.LockedByUnpaidAt = now` + แจ้ง "ปิดสิทธิ์ส่วนเกินชั่วคราว" (audit `PLATFORM-OVERAGE-LOCKED`) · เกิน `SuspendGraceDays` ⇒ ขั้น 2 เส้นเดิม (`WouldSuspend`/`Suspend`) · ชำระครบ ⇒ ปลดล็อก (`Restore` เดิม) | `PlatformDunningPolicy.Decide` + `PlatformInvoiceRules.KindOf(reference)` แยก USAGE/RENEW อยู่แล้ว |
| `PlatformCardOpsJobs` (เดิม) | ใบส่วนเกินเข้าคิวตัดบัตรอัตโนมัติเมื่อ `OverageAutoChargeSavedCard` + mandate | `PlatformAutoChargePolicy` |

### B.6 การแจ้งเตือน (เกณฑ์เดียว `Helpers/QuotaWarnPolicy` — แทน 3 ชุดวันนี้)

| เหตุการณ์ | ผู้รับ | ช่อง | key (ครั้งเดียวต่อ บริษัท·ทรัพยากร·ระดับ·เดือน) |
|---|---|---|---|
| ใกล้เต็ม (เกณฑ์จาก `QuotaWarnPercents`) ทุกทรัพยากรใน `QuotaResources` + เอกสาร + ข้อความ | Owner + ผู้มี `BillingManage` | `NotificationEngine` (ในแอป + อีเมล ค่าตั้งต้น · LINE ตาม pref) | `quota.<resource>_level` (ขยายจาก `quota.documents_level` เดิม) |
| เริ่มคิดส่วนเกินครั้งแรกของเดือน | 〃 | 〃 | `overage.<resource>_started` — ข้อความบอกราคา + ประมาณการ |
| ถึงเพดานเงิน / ไม่มีราคา / ถูกล็อก | 〃 | 〃 | `overage.capped` |
| ออกใบแจ้งหนี้ส่วนเกิน · ก่อน/ถึง/หลังครบกำหนด | 〃 | `PlatformBillingNotifier` (เดิม) | เดิม |
| ปิดสิทธิ์ส่วนเกินเพราะค้างชำระ | 〃 | 〃 | `co:{id}:overage-locked:{date}` |
| แถบบนทุกหน้า (layout) | ผู้ใช้ทุกคนของบริษัท | header `X-Quota-Warn: <resource>:<level>` จาก middleware (แบบ `X-Subscription-Grace`) ⇒ `layout.js` วาดแถบเหลือง/แดงพร้อมลิงก์ hub | — |

### B.7 UX (hub `billing.html`) + ข้อความไทย

**แท็บ "แพ็กเกจ" การ์ดใหม่ "เมื่อโควตาเต็ม"**
```
เมื่อใช้ครบโควตาของแพ็กเกจ
  ( • ) หยุดส่วนที่เกิน (ค่าตั้งต้น) — ระบบจะไม่สร้างรายการเพิ่มจนกว่าจะขึ้นเดือนใหม่ ซื้อโควตาเพิ่ม หรืออัปเกรด
        ใบกำกับภาษี · ใบเสร็จ · ใบลดหนี้ ยังออกได้เสมอโดยไม่คิดเงิน
  (   ) ใช้ต่อได้ คิดค่าส่วนเกินตามจริง
        เอกสาร ฿5.00/ฉบับ · OCR ฿2.00/หน้า · ข้อความแจ้งเตือน ฿0.50/ข้อความ · พื้นที่ ฿20.00/GB/เดือน  (ราคารวม VAT)
        ออกใบแจ้งหนี้ทุกต้นเดือน ชำระภายใน 7 วัน — ค้างชำระ ระบบจะปิดสิทธิ์ส่วนเกินชั่วคราว
        [ ] เพดานค่าส่วนเกินต่อเดือน  [ 500 ] บาท  (ถึงเพดานแล้วกลับไปหยุดส่วนที่เกิน · ว่าง = ไม่จำกัด)
        ☐ ยอมรับเงื่อนไขค่าส่วนเกิน (ฉบับ 2026-11)
  [บันทึก]
```
สถานะเมื่อถูกล็อก: "ปิดสิทธิ์ส่วนเกินชั่วคราว — ใบแจ้งหนี้ USAGE-… ค้างชำระ 9 วัน · [ชำระตอนนี้]"

**แท็บ "ใช้ไปเท่าไร"** เพิ่มแถว "ส่วนเกินเดือนนี้: เอกสาร 37 ฉบับ = ฿185.00 · OCR 0 · รวม ฿185.00 (ประมาณการบิลวันที่ 1 ธ.ค.)" + ตัวเลือกเดือน (§D)
**แท็บ "ใบแจ้งหนี้&ใบเสร็จ"** ป้ายชนิด "ค่าส่วนเกิน" (จาก `PlatformInvoiceRules.KindLabel`) + ปุ่มชำระเดิม
**แอดมิน** `admin/platform-billing.html` การ์ด "ราคาส่วนเกิน" (ตาราง ทรัพยากร × แพ็กเกจ · ประวัติราคา) + ค่าตั้ง `OverageGraceDays/UnpaidAction/MinInvoice/WarnPercents` · `pages/admin-quota.html` แท็บ "นโยบายส่วนเกินของบริษัท" (ดู/ปลดล็อก/ตั้งแทนผู้เช่า พร้อมเหตุผล + audit)

### B.8 ทางเลือก: อัปเกรด/เปลี่ยนรอบกลางรอบ (เติมที่ `PlatformProration`)

ที่มีแล้ว: อัปเกรดแพ็กเกจกลางรอบ ทุกรอบ 1/3/6/12 ✅ (วันหมดอายุคงเดิม) · ที่ขาด: **เปลี่ยนรอบบิล** กลางรอบ (รายเดือน→6 เดือน/รายปี, หรือพร้อมอัปเกรด)

ดีไซน์ `PlatformProration.CycleChange(oldPricePerCycle, oldCycleMonths, newPricePerCycle, newCycleMonths, cycleEndUtc, nowUtc)` (pure):
```
D = วันที่เหลือของรอบเดิม · C = วันของรอบเดิม
credit    = round(oldPricePerCycle × D / C, 2)          มูลค่าที่ยังไม่ได้ใช้ของรอบเดิม
amount    = max(0, round(newPricePerCycle − credit, 2))  จ่ายรอบใหม่เต็ม หักเครดิต
newStart  = วันนี้ · newEnd = วันนี้ + newCycleMonths     ⇐ ต่างจากอัปเกรดแพ็กเกจ (ซึ่งคงวันหมดอายุ) — รอบใหม่ต้อง "เริ่มใหม่" เพราะความยาวรอบเปลี่ยน
```
เงื่อนไข: ใช้เมื่อ `MonthsOf(new) > MonthsOf(old)` (ยาวขึ้น) · สั้นลง = `Scheduled*` วันครบรอบตามเดิม (ไม่คืนเงิน — ข้อ 178) · ทดลอง/ฟรีถาวร/หมดอายุ = เส้น "เริ่มรอบใหม่" เดิม · ใบต่ออายุรอบเดิมที่ออกแล้ว ⇒ `RenewalInvoiceClaims` เดิม · รายการเป็น `SubscriptionPayment` `IsProration=true` + ฟิลด์ใหม่ `ProrationNewCycle` · `QuotedAmount` ประทับ · อนุมัติ ⇒ `ApplyTemplate(sub, tpl, newCycle)` + `EndDate = newEnd` · ราคาทั้งสองฝั่งผ่าน resolver ราคาพิเศษ §C · บนใบเสร็จ: 2 บรรทัด "ค่าแพ็กเกจ Pro รายปี 12 เดือน" และ "หักมูลค่าคงเหลือรอบเดิม (D/C วัน)" เป็น `DiscountAmount` ของบรรทัดแรก (ฐานหลังลด + VAT = เงินรับ) · เทสต์: รายเดือน→รายปีกลางเดือน · รายเดือน→รายปีพร้อมอัปเกรด · วันสุดท้ายของรอบ · credit > ราคาใหม่ (= 0 ห้ามติดลบ ห้ามคืน)

---

## C. ดีไซน์ราคาพิเศษรายบริษัท (คงทน · ใช้กับต่ออายุ + อัปเกรด + proration + ดูราคา)

### C.1 ข้อมูล

```
CompanyPlanPriceOverrides  (ระดับแพลตฟอร์ม · อ้าง CompanyId หรือ AccountSubscriptionId อย่างใดอย่างหนึ่ง)
  CompanyId Guid? · AccountSubscriptionId Guid?      (License: ใช้กับใบต่ออายุ License)
  PlanTemplateId Guid                                  แพ็กเกจที่ได้สิทธิ์ (เช่น Pro)
  Mode enum { PriceOfPlan = 1, FixedPerCycle = 2, PercentOff = 3 }
  ReferencePlanTemplateId Guid?   (PriceOfPlan: "จ่ายราคา Standard")
  FixedMonthly/FixedQuarterly/FixedSemiAnnual/FixedAnnual decimal?  (FixedPerCycle: ว่าง = ใช้ราคาแม่แบบรอบนั้น)
  PercentOff decimal?             (PercentOff)
  EffectiveFromUtc · EffectiveToUtc?  (ว่าง = ตลอดไป · ต่อสัญญา = แถวใหม่)
  AppliesToUpgrades bool = true · AppliesToRenewals bool = true
  Reason string (บังคับ) · ApprovedBy · IsActive
  unique active (CompanyId|AccountSubscriptionId, PlanTemplateId) ณ เวลาหนึ่ง (ตรวจใต้ล็อกตอนบันทึก)
```

### C.2 ตัวตัดสินเดียว (pure) — `Helpers/PlatformPlanPricing`

```
EffectiveCyclePrice(PlanTemplate target, BillingCycle cycle, PriceOverride? ov, PlanTemplate? reference, DateTime nowUtc)
  → (Price, Source: "list" | "override:PriceOfPlan" | "override:Fixed" | "override:Percent", Note)
ลำดับ: override ที่ active และอยู่ในช่วงวัน ?? ราคาแม่แบบ  ·  ผลลัพธ์ปัด 2 ตำแหน่ง AwayFromZero · ห้ามติดลบ
ซ้อน: ราคาพิเศษ (แทนราคาตั้ง) → คูปอง (ลดจากราคาพิเศษ) → แอดมินแก้ยอดครั้งเดียว (ทับสุดท้าย · มีอยู่แล้ว)
```
จุดเรียกที่ **ต้องเปลี่ยนให้ผ่านตัวนี้ทั้งหมด** (ล็อกด้วย `required_call_site_check` · "แก้ที่หนึ่ง grep ทั้งเรพ" ต้องตอบเป็นตัวเลขในคอมมิต):

| จุด | วันนี้ | ต้องเป็น |
|---|---|---|
| `RequestPlanPaymentAsync` (Convert/Change) `SubscriptionService.cs:~400` | `PlatformPlanPricing.AmountDue(template, cycle, discount%)` | `AmountDue(EffectiveCyclePrice(...), discount%)` + ประทับ `ListAmount` = ราคาแม่แบบ, `QuotedAmount` = ราคาพิเศษ(−คูปอง) · บันทึก `PriceOverrideId` บนรายการ |
| `ProrationFor` `:898-902` | `CyclePrice(current)` / `CyclePrice(target)` | ราคาพิเศษ**ทั้งสองฝั่ง** (ของเดิมที่บริษัทจ่ายจริง = override ของแพ็กเกจเดิมถ้ามี · ไม่งั้นราคาแม่แบบ) — ถ้าใช้ราคาตั้งฝั่งเดิมจะคิดส่วนต่างน้อยเกิน/มากเกิน |
| `QuotePlanChangeAsync` `:905+` | ราคาแม่แบบ | ตัวเดียวกับตอนยืนยัน (กฎเดิมของ 230) + แสดง "ราคาตามข้อตกลง" |
| `SaasBillingDocumentService.PriceFor` `:554` ← `:416` `:537` (ใบต่ออายุบริษัท/License) | ราคาแม่แบบ | `EffectiveCyclePrice` (License ⇒ แถว `AccountSubscriptionId`) |
| `PlatformPlanPricing.AutoApproveExpected` ผู้เรียก (`SubscriptionPaymentHandler` · `StartSubscriptionPaymentAsync`) | `listPrice` = แม่แบบ | `listPrice` = ราคาพิเศษ (ไม่งั้นรายการราคาพิเศษที่ไม่มี `QuotedAmount` ถูกพักให้แอดมินทุกใบ) |
| `AdminController` เปลี่ยนแพ็กเกจรายบริษัท (เส้นแอดมิน) | ราคาแม่แบบ | 〃 |
| `AccountPlanExpiryReminderJob`/อีเมลต่ออายุ ที่พิมพ์ราคา | — | 〃 |
| ส่วนเสริม/โควตาเสริม | — | **นอกขอบเขต** (ราคาพิเศษเฉพาะแพ็กเกจ · ถ้าต้องการ ค่อยทำ `CompanyOfferPriceOverride` แยก) |

### C.3 ใบกำกับ · audit · หน้าจอ

- ใบกำกับ/ใบเสร็จ: บรรทัดเดียว `UnitPrice = ราคาพิเศษ` คำอธิบายต่อท้าย "(ราคาตามข้อตกลง)" — **ไม่แสดงเป็นราคาตั้ง−ส่วนลด** (ราคาที่ตกลงคือราคาขายจริง §86/4 · คูปองยังแสดงเป็นส่วนลดของบรรทัดตาม 230-Q2)
- audit chain ทุกครั้งที่สร้าง/ปิด override: `PLATFORM-PRICE-OVERRIDE-SET/END` (ค่าเก่า/ใหม่ · เหตุผล · ผู้อนุมัติ) + `SubscriptionHistory` `PriceOverrideSet`
- แอดมิน: ในหน้า company detail / `admin/account-subscriptions.html` การ์ด "ราคาพิเศษ" (แพ็กเกจ · โหมด · ค่า · ช่วง · เหตุผล · ประวัติ) · ตัวอย่างสด "ต่ออายุรายปีถัดไปจะคิด ฿9,900 (ราคาตั้ง ฿14,900)"
- ผู้เช่า: hub แท็บแพ็กเกจแสดง "ราคาของคุณ: ฿… /ปี (ราคาตามข้อตกลง)" และ `plan-quote` คืน `listPrice` + `yourPrice`
- เทสต์ pure: ลำดับ/ช่วงวัน/โหมด 3 แบบ · proration ด้วยราคาพิเศษสองฝั่ง · ใบต่ออายุใช้ราคาพิเศษ · หมดช่วง ⇒ กลับราคาตั้ง (ทิศตรงข้าม) · Db: ต่ออายุ License ใช้แถว `AccountSubscriptionId`

---

## D. ดีไซน์หน้าประวัติการใช้งาน (เดือนนี้ + ย้อนหลัง · ทุกทรัพยากร · "ไม่จำกัด" ที่เดียว)

### D.1 ข้อมูล — `UsageMonthlySnapshots` (TenantEntity)

```
CompanyId · Period "yyyy-MM" (เดือนไทย) · ResourceKey · Used long · Limit long? (null = ไม่จำกัด) · Purchased int · OverageUnits int · OverageAmount decimal
Source enum {Rollover, Daily, Backfill} · SnapshotAtUtc · unique (CompanyId, Period, ResourceKey)
```
ผู้เขียน (ตัวเดียวต่อแหล่ง):
1. **ตอนขึ้นเดือน** `SubscriptionUsageRollover.RollIfDue` (`SubscriptionService.cs:1302`) — ก่อนล้างตัวนับ เขียน snapshot เอกสาร/OCR/ข้อความ (เพิ่ม hook `IUsageSnapshotWriter.WriteBeforeResetAsync(sub)` ใน scope เดียวกัน · ล้มดัง: ไม่ snapshot = ไม่ล้าง)
2. **รายวัน** (งาน `UsageSnapshotJob` · JobLock · watermark ต่อบริษัท) — ทรัพยากร Capacity (ผู้ใช้ · พื้นที่ · เว็บ · ที่พัก) เก็บค่าสูงสุดของเดือน (`Used = max`) ผ่าน `IQuotaGate.UsedAsync` + `LimitAsync` (ตัวตัดสินเดิม — ไม่คำนวณเอง)
3. **Backfill ครั้งเดียว** 12 เดือนย้อนหลังจากตารางต้นทาง (วิธีเดียวกับ `AdminCompanyUsageController` — Documents/OcrScans/UsageEvents) · `Source=Backfill` · ไม่ทับแถว Rollover

### D.2 API

`GET api/companies/{cid}/quota/history?months=12` (เจ้าของ/`BillingManage`) → `{ months: [ { period, bars: [UsageBarRule.Bar + overageUnits + overageAmount + invoice {number, status, url}?] } ], resources: [...] }` — ทุก `Bar` คิดด้วย `UsageBarRule` (เดือนนี้ = สด · เดือนก่อน = snapshot) · ไม่มี snapshot = `null` ("ยังไม่มีข้อมูล" ≠ 0 · F2 ข้อ 5)

### D.3 หน้าจอ (hub แท็บ "ใช้ไปเท่าไร" — ยุบ `usage.html` เข้ามา · URL เดิม redirect `#tab=usage`)

- แถวบน: ตัวเลือกเดือน (12 เดือน) · ค่าตั้งต้น เดือนนี้ · ป้าย "ข้อมูล ณ …" สำหรับเดือนเก่า
- ตาราง/หลอดครบ **ทุกทรัพยากร**: เอกสาร · OCR · ข้อความช่องกลาง · ผู้ใช้ · พื้นที่ · เว็บ · หน้า/สินค้าต่อเว็บ (ต่อเว็บย่อย) · ที่พัก · **AI (ข้อมูลอย่างเดียว · ไม่มีเพดาน · ป้าย "ไม่คิดเงิน")** · API ภายนอก (ถ้ามี)
- ต่อแถว: ใช้ไป / เพดาน (`usedText` / `limitText` จากเซิร์ฟเวอร์เท่านั้น) · ที่ซื้อเพิ่ม · ส่วนเกิน (หน่วย·บาท) · ปุ่ม "ซื้อเพิ่ม" (`BuyUrl`) · sparkline 12 เดือน
- การ์ดสรุป: "ส่วนเกินเดือนนี้ ฿… · บิลถัดไปวันที่ … · นโยบาย: หยุดส่วนที่เกิน [เปลี่ยน]"
- พื้นที่แยกประเภท / ไฟล์ใหญ่สุด / รายชื่อผู้ใช้ (ของ `usage.html` เดิม) ย้ายเป็นส่วนพับได้ใต้แถวนั้น
- ลบสำเนาเกณฑ์: `js/plan-display.js:11 UNLIMITED_AT` · `pages/subscription.html:295` · `usage.html` ที่เหลือ ⇒ ทุกหน้าอ่าน `unlimited/limitText` · `UsageBarRule` เลิกเกณฑ์ `≥999/≥99999` ได้เมื่อ migration แปลงค่าตัวแทนในแม่แบบ/subscription เป็น -1 (F2 ข้อ 9 — persist แล้วต้องมี migration) · ระหว่างย้าย `UsageBarRule` ยังรับทั้งสองแบบ
- แอดมิน: `pages/admin-company-usage.html` อ่าน snapshot ชุดเดียวกัน (เลิก query ต้นทางซ้ำ) + export CSV (MiniExcel)

---

## E. คำตัดสินที่ต้องขอเจ้าของ (≤ 8 · พร้อมคำแนะนำ)

| # | คำถาม | แนะนำ | เหตุผล |
|---|---|---|---|
| E1 | ค่าตั้งต้นของทุกบริษัท = "หยุดส่วนที่เกิน" และผู้เช่าเปิดส่วนเกินเองเท่านั้น? | **ใช่** | ไม่เปลี่ยนพฤติกรรมเดิมหลัง deploy · ไม่มีใครโดนบิลที่ไม่ได้ตกลง · เอกสารที่กฎหมายบังคับยังออกฟรีเหมือนเดิม |
| E2 | ทรัพยากรที่คิดส่วนเกินรุ่นแรก = เอกสาร · OCR · ข้อความช่องกลาง · พื้นที่ (GB-เดือน) — ที่นั่ง/เว็บ/ที่พักยังเป็นซื้อโควตาเสริม? | **ใช่** | ของนับชิ้นมี 3 โหมดซื้อแล้ว · ส่วนเกินรายวันของ "ชิ้น" โต้แย้งง่าย |
| E3 | ราคาส่วนเกิน: ต่อทรัพยากรค่าเดียว หรือ ต่อแพ็กเกจด้วย? | **ทั้งสอง** (แถวแพ็กเกจชนะแถวทั่วไป) | ตารางเดียว ไม่ซับซ้อน · Enterprise มักถูกกว่า |
| E4 | ค้างชำระใบส่วนเกิน: ปิดสิทธิ์ส่วนเกินก่อน แล้วค่อยเข้า dunning ปกติ? | **ใช่ (2 ขั้น)** ผ่อนผัน 7 วัน → ล็อกส่วนเกิน · เกิน `SuspendGraceDays` → เส้นเดิม | ไม่ระงับทั้งระบบเพราะบิลเล็ก · ของที่ใช้แล้วไม่ถอน |
| E5 | ราคาพิเศษรายบริษัท: รองรับ 3 โหมด (ราคาของแพ็กเกจอื่น / กำหนดเอง / ลด %) และใช้กับ ต่ออายุ+อัปเกรด+ส่วนต่าง ทั้งหมด? คูปองซ้อนได้? | **ใช่ · คูปองซ้อนได้** (ลดจากราคาพิเศษ) | ตรงคำขอ "A จ่ายราคา Standard ได้ Pro" · resolver เดียวตัดปัญหาราคาต่างกันคนละจุด |
| E6 | เปลี่ยนรอบบิลกลางรอบ (รายเดือน→รายปี): เริ่มรอบใหม่วันนี้ + หักมูลค่าคงเหลือ (§B.8) — ยอมรับว่าวันหมดอายุ "เลื่อน" ไม่ใช่คงเดิม? | **ใช่** · สั้นลง = วันครบรอบ | ความยาวรอบเปลี่ยน วันหมดอายุเดิมไม่มีความหมาย · ไม่คืนเงิน (ข้อ 178) |
| E7 | ทดลอง vs ฟรี: คงใช้ `SubscriptionPlan.FreeTrial` ร่วม แต่เพิ่มคอลัมน์ `Subscription.Kind {Trial, Free, Paid}` (คำนวณจาก IsPermanentFree/Status ตอนเขียน · migration backfill) + แม่แบบ "Free Edition" มี `EnabledFeatures` ของตัวเอง (ไม่ผูก `TrialFeatures`)? และ **ถอด** 4 ค่าตั้ง `TrialConfig` ที่ไม่มีผู้อ่านออกจากหน้าแอดมิน (หรือ ต่อสาย `BlockAccessOnExpiry` เข้าด่าน)? | **เพิ่ม Kind · แยกฟีเจอร์ฟรี · ต่อสาย `BlockAccessOnExpiry` เข้า `SubscriptionGatePolicy` (อ่านเมื่อ Expired+พ้นผ่อนผัน ⇒ บล็อกอ่านด้วย) · ถอด `DeleteDataAfterGracePeriod/DataRetentionDays/AllowDataExportAfterExpiry`** | ไม่แตะ enum ที่ persist ทั่วระบบ · ค่าตั้งที่ไม่มีผลหลอกแอดมิน (F2 ข้อ 2) · การลบข้อมูลขัด พ.ร.บ.บัญชี ม.10 — ทำผ่าน DSR เท่านั้น |
| E8 | ประวัติการใช้งานเก็บกี่เดือน และโชว์ AI ต่อผู้เช่าไหม? | **24 เดือน · โชว์ AI เป็นข้อมูล ไม่มีเพดาน ไม่คิดเงิน** | เพียงพอเทียบปีต่อปี · กฎเหล็ก #1 |

---

## F. แพ็กลงมือ (ไฟล์ · ลำดับ · ขนาด) — รวม ≈ 17–21 วันทำงาน

| แพ็ก | เนื้อหา | ไฟล์หลัก | เทสต์ | ขนาด |
|---|---|---|---|---|
| **P0 อุดรั่วเล็ก (ทำก่อน · ไม่รอคำตัดสิน)** (ค)(ง) ✅ รอบ 231 · (ก)(ข)(จ) ทีม overage/usage | (ก) `GetUsageDetailAsync` ใช้ `IQuotaGate.LimitAsync` สำหรับ users/storage + `DocumentQuotaPolicy.EffectiveLimit` สำหรับเอกสาร (ให้ `usage.html` ตรงกับ hub) · (ข) ลิงก์แจ้งเตือน `:1632` → `QuotaResources.BuyUrl` · (ค) License `ChangeStatus/Extend/ChangeLimits` เขียน `SubscriptionHistory` + `AddChainedAuditLog` + `Reason` บังคับ (ขยายอายุผ่าน `SubscriptionPaymentLock`) · (ง) `AdminChangeLimits` บันทึกค่าเก่า/ใหม่ + audit · (จ) ลบ `UNLIMITED_AT` ใน `plan-display.js` และ literal `subscription.html:295` → อ่าน `limitText` | `SubscriptionService.cs` · `AdminAccountSubscriptionController.cs` · `AdminController.cs` · `js/plan-display.js` · `pages/subscription.html` · `TEST_PLAN.md` | pure `UsageBarRule` เดิม + Db `AccountSubscriptionAuditDbTests` (ใหม่) | 1.5 วัน |
| **P1 ราคาพิเศษรายบริษัท (§C)** ✅ รอบ 231 | ตาราง + migration · `PlatformPlanPricing.EffectiveCyclePrice` · เปลี่ยน 7 จุดเรียก · `AdminPlatformPriceOverrideController` + การ์ดแอดมิน · hub แสดง "ราคาของคุณ" · `required_call_site_check` | `Helpers/PlatformPlanPricing.cs` · `Helpers/PlatformPriceOverrideMigration.cs` · `Models/Entities/PlatformPriceOverride.cs` · `SubscriptionService.cs` (Request/Proration/Quote) · `SaasBillingDocumentService.cs` · `Services/Payments/SubscriptionPaymentHandler.cs` · `PlatformPaymentService.cs` · `admin/account-subscriptions.html` · `pages/billing.html` · `ACCOUNT_STRUCTURE.md` §6.2f | `PlatformPriceOverrideTests` (pure 8 เคส) · Db ต่ออายุ/proration | 3 วัน |
| **P2a ส่วนเกิน — ฐาน (§B.2–B.4)** | `OverageResources` closed list · `OverageRates` + `CompanyOveragePolicy` + คอลัมน์ settings · `OveragePolicy.Decide` · `OverageRateResolver` · `IQuotaGate.ConsumeAsync` · `UsageMeteringService.ResolvePlanAsync` ทางแยก `overage.*` · แก้ 3 จุดเขียน (DocumentService/OcrController+OcrQuotaService/NotifyQuotaService) · `CanFitStorageAsync` | `Helpers/OveragePolicy.cs` · `Helpers/OverageRateResolver.cs` · `Helpers/OverageMigration.cs` · `Models/Constants/OverageResources.cs` · `Services/Implementations/QuotaGateService.cs` · `DocumentService.cs` · `OcrController.cs` · `OcrQuotaService.cs` · `NotifyQuotaService.cs` · `UsageMeteringService.cs` | `OveragePolicyTests` (ทิศสองทาง · cap · no-rate · locked) · Db `OverageMeterDbTests` (ซ้ำ idempotent · ธุรกรรมถอย) | 4 วัน |
| **P2b ส่วนเกิน — เงิน/งาน (§B.5)** | `StorageOverageSnapshotJob` · `UsageInvoicingJob` อ่านค่าตั้ง + due date · `PlatformDunningService` ขั้น 1 ล็อกส่วนเกิน/ปลด · auto-charge ใบส่วนเกิน · audit | `Services/Background/StorageOverageSnapshotJob.cs` · `UsageInvoicingJob.cs` · `PlatformDunningService.cs` · `Helpers/PlatformDunningPolicy.cs` (`OverageLockDecision`) · `PlatformAutoChargeService.cs` · `Program.cs` | `PlatformDunningOverageTests` · Db ใบ USAGE- ครบกำหนด→ล็อก→ชำระ→ปลด | 3 วัน |
| **P2c ส่วนเกิน — แจ้งเตือน + UX (§B.6–B.7)** | `QuotaWarnPolicy` แทน 3 เกณฑ์ · event keys · middleware header + แถบ layout · การ์ดนโยบายใน hub · ตารางราคาแอดมิน · แท็บแอดมิน-quota | `Helpers/QuotaWarnPolicy.cs` · `NotificationEvents.cs` · `SubscriptionMiddleware.cs` · `js/layout.js` · `pages/billing.html` · `js/quota-ui.js` · `admin/platform-billing.html` · `pages/admin-quota.html` · `AdminPlatformBillingController.cs` · `QuotaController.cs` (`GET/PUT overage-policy`) | `QuotaWarnPolicyTests` · `node --check` | 2.5 วัน |
| **P3 ประวัติการใช้งาน (§D)** | `UsageMonthlySnapshots` + writer ใน rollover + งานรายวัน + backfill · `GET quota/history` · ยุบ `usage.html` เข้า hub · admin อ่าน snapshot · migration ค่าตัวแทน ≥999/≥99999 → -1 | `Helpers/UsageSnapshotMigration.cs` · `Services/Implementations/UsageSnapshotWriter.cs` · `Services/Background/UsageSnapshotJob.cs` · `Helpers/SubscriptionUsageRollover.cs` · `QuotaController.cs` · `pages/billing.html` · `pages/usage.html` (redirect) · `AdminCompanyUsageController.cs` | `SubscriptionUsageRolloverTests` (ไม่ snapshot = ไม่ล้าง) · Db `UsageHistoryDbTests` | 3 วัน |
| **P4 เปลี่ยนรอบบิลกลางรอบ (§B.8)** ✅ รอบ 231 (ค้างใบเสร็จ 2 บรรทัด) | `PlatformProration.CycleChange` · `ChangeSubscriptionAsync` ทางแยก · `QuotePlanChangeAsync` · handler อนุมัติตั้ง EndDate ใหม่ · ใบเสร็จ 2 บรรทัด · hub ข้อความ | `Helpers/PlatformProration.cs` · `SubscriptionService.cs` · `PlatformBillingDocumentIssuer.cs` · `pages/billing.html` · `ACCOUNT_STRUCTURE.md` §6.2e | `PlatformProrationCycleChangeTests` (4 เคส §B.8) · Db อนุมัติ | 2 วัน |
| **P5 วงจรชีวิต (E7)** ✅ รอบ 231 (ต่อสาย 4 ค่าตั้งแทนถอด · ยังไม่แยก FreeFeatures) | `Subscription.Kind` + backfill · แม่แบบ Free มี `EnabledFeatures` แยก · ต่อสาย `BlockAccessOnExpiry` เข้า `SubscriptionGatePolicy.Decide` (facts ใหม่ `BlockReadsWhenExpired`) · ถอด 3 ค่าตั้งไร้ผลจากหน้าแอดมิน + DTO (คงคอลัมน์) · รายงานแอดมินกรองตาม Kind · แจ้งเจ้าของตอน `ProcessExpiredTrialsAsync` | `Models/Entities/Subscription.cs` · `Helpers/SubscriptionGatePolicy.cs` · `Middleware/SubscriptionMiddleware.cs` · `SubscriptionService.cs` · `admin/plans.html` · `admin/companies*.html` · `tools/settings_reader_check.py` baseline | `SubscriptionGatePolicyTests` (+Expired/BlockReads ทิศสองทาง) | 2 วัน |

ลำดับ: P0 → P1 → P2a → P2b → P2c → P3 → P4 → P5 (P3/P4/P5 สลับกันได้) · ทุกแพ็ก: `bash tools/check_all.sh` · doc (`ACCOUNT_STRUCTURE.md` §6 · `TEST_PLAN.md` · `CHANGELOG.md`) ในคอมมิตเดียว · ฝ่ายค้าน 1 รอบสำหรับ P1/P2 (เงิน) ตาม F3 ข้อ 11 · CI บน `claude/**` เป็น compiler ตัวแรก

### สิ่งที่ doc ต้องแก้ตามผลตรวจนี้ (เมื่อมีคอมมิตโค้ด)
- `ACCOUNT_STRUCTURE.md` §6.1/§6.2b: เพิ่มว่าเพดานรายบริษัทมี 2 กลไก (`Subscription.Max*` vs `CompanyQuotaOverride`) และลำดับชนะ — วันนี้ doc กล่าวถึงเฉพาะอันแรก
- `QUOTA_ADDON_SUBSCRIPTION_AUDIT.md` §E แพ็ก 6: "`usage.html` ย้ายเข้า renderer เดียวรอบถัดไป" — รอบ 230 ย้ายหลอดแล้วแต่**เพดานยังอ่านช่องเดิม** (A.2) ต้องระบุ
- `PLATFORM_BILLING_AUDIT.md` §2 G-16 (ราคา `documents.overage` = 0) ยังเปิด — ปิดเมื่อ P2a ลง

---

## สถานะการลงมือ รอบ 231 (2026-11-03 · คำตัดสินข้อ 231-1..7 · ยังไม่ได้คอมไพล์ในเครื่องนี้ — CI บน `claude/**` เป็น compiler ตัวแรก)

| แพ็ก | ทำแล้ว | ไฟล์ | ยังไม่ทำ / หมายเหตุ |
|---|---|---|---|
| **P0 (ก)** `usage.html` เพดานไม่รวม override/ที่ซื้อ | ✅ `GetUsageDetailAsync` ผู้ใช้ = `QuotaLimitPolicy.Effective` (override·allowance·ช่องเดิม·ที่ซื้อ ผ่าน `QuotaStore`) · พื้นที่ = `GetStorageStatusAsync` · เอกสาร = `EffectiveLimit`+`DisplayLimit` (+หมายเหตุโบนัส) · หลอดทุกตัวใช้เกณฑ์ของแอดมิน | `SubscriptionService.cs` | — |
| **P0 (ข)** ลิงก์แจ้งเตือน `/pages/addons.html` | ✅ `QuotaResources.UsageUrl` (hub `#tab=usage`) · title/ข้อความตาม % ที่ข้าม | `SubscriptionService.cs` · `QuotaResources.cs` | ✅ ข้อความช่องกลาง (`NotifyQuotaService.AlertIfCrossedAsync`) ชี้ `QuotaResources.UsageUrl` แล้ว (รอบเก็บงาน) |
| **P0 (ค)(ง)** audit License/`AdminChangeLimits` | — | — | **ทีม License audit (AdminAccountSubscriptionController/AdminController) ทำ** — นอกพื้นที่ของรอบนี้ |
| **P0 (จ)** สำเนาเกณฑ์ "ไม่จำกัด" ใน JS | ✅ `UsageBarRule.LimitTexts/UnlimitedFlags` → `PlanTemplateResponse.LimitTexts/Unlimited` + `SubscriptionResponse.DocumentsBar/LimitTexts/Unlimited` · `plan-display.js` เลิก `UNLIMITED_AT` (7 จุดเรียก: index 6 · register 1 · account-subscription 1 แก้เป็นอ่านจากแพ็กเกจ) · `subscription.html` อ่าน `documentsBar` | `Helpers/UsageBarRule.cs` · `SubscriptionDtos.cs` · `js/plan-display.js` · `index.html` · `register.html` · `pages/account-subscription.html` · `pages/subscription.html` | migration ค่าตัวแทน ≥999/≥99999 → -1 ในแม่แบบ/subscription **ยังไม่ทำ** (`UsageBarRule` ยังรับทั้งสองเกณฑ์ — คำตัดสิน 231-6) |
| **P2a ฐาน (§B.2–B.4)** | ✅ `OverageResources` (closed list) · `OverageRate`/`CompanyOveragePolicy`/`UsageMonthlySnapshot`/`UsageDailySnapshot` + `OverageMigration` (+5 คอลัมน์ `PlatformBillingSettings`) · `OveragePolicy.Decide`/`OverageRateResolver.Pick` (pure) · `IOverageService` (Decide/Record/Policy/Rates/UnpaidLock · audit ทุกการเปลี่ยน) · `UsageMeteringService` รับ `UnitPriceOverride` (overage.* ไม่อยู่ใน ApiPricingPlan) · `AddOnCodes.IsSystemMeter(overage.*)` · ด่าน 4 จุด: `DocumentService` (สร้าง+อนุมัติใบเลื่อนนับ · `RecordDocumentOverageAsync` ⇒ `overage.documents` คิดเงินเฉพาะเมื่อด่านอนุญาต · HardLock = ราคา 0) · `OcrQuotaService.TryConsumeAsync` (เครดิตหมด ⇒ ถามด่าน ⇒ สแกนต่อ + มิเตอร์ต่อหน้า) + `OcrController` ต่อท้ายเหตุ · `NotifyQuotaService.TryReserveAsync` ขั้น 4 (`KindOverage` · `ReleaseAsync` ถอน UsageEvent เมื่อส่งล้ม) · `CanFitStorageAsync` | `Models/Constants/OverageResources.cs` · `Models/Entities/Overage.cs` · `Helpers/OverageMigration.cs` · `Helpers/OveragePolicy.cs` · `Services/Implementations/OverageService.cs` · `DocumentService.cs` · `OcrQuotaService.cs` · `OcrController.cs` · `NotifyQuotaService.cs` · `SubscriptionService.cs` · `UsageMeteringService.cs` · `IUsageMeteringService.cs` · `AddOnCodes.cs` | ✅ `required_call_site_check` `_R231OV_WHY` 19 กติกา (OverageService 5 · DocumentService 3 · OcrQuotaService 2 · SubscriptionService 2 · NotifyQuotaService 2 · UsageSnapshotService 2 · UsageInvoicingJob · PlatformDunningService · PlatformAutoChargeService) · `IQuotaGate.ConsumeAsync` ตาม §B.3 ไม่ได้สร้าง — ด่านตัดสินผ่าน `IOverageService.DecideAsync` ตัวเดียวแทน |
| **P2b เงิน/งาน (§B.5)** | ✅ `UsageInvoicingJob` อ่าน `OverageMinInvoiceAmount` · ครบกำหนด = วันออก + `OverageGraceDays` · บรรทัด `OverageResources.InvoiceName` + งวดเดือนไทย · แถว 0 บาท = ปิดงวดโดยไม่มีใบ (เดิมวนยกยอดตลอดไป) · `PlatformDunningService` ขั้น 1 ล็อก/ปลด + `LockOverageOnly` ตัดใบ USAGE- ออกจากขั้นระงับ (เตือนยังส่ง) + ข้อความ overage-locked/unlocked · `UsageSnapshotJob` ปิดส่วนเกินพื้นที่ GB-เดือน (`SettleStorageOverageAsync` idempotent ต่องวด) | `UsageInvoicingJob.cs` · `PlatformDunningService.cs` · `Helpers/PlatformBillingSettingsRules.cs` · `PlatformBillingSettingsService.cs` · `Services/Background/UsageSnapshotJob.cs` | ตัดบัตรอัตโนมัติใบส่วนเกิน: ค่าตั้ง `OverageAutoChargeSavedCard` เก็บ+echo แล้ว ✅ ผู้อ่าน `PlatformAutoChargePolicy.AutoChargeAllowedForKind` (pure) ใน `PlatformAutoChargeService.ProcessPayerAsync` — ปิด ⇒ ใบ USAGE- ไม่เข้าคิวตัดบัตร (ใบต่ออายุไม่ขึ้นกับค่าตั้งนี้) · บรรทัดใบ/ขั้นต่ำ/ครบกำหนดย้ายเป็น pure `Helpers/UsageInvoiceLines` (Dispose/Build/DueDate) |
| **P2c แจ้งเตือน + UX (§B.6–B.7)** | ✅ `QuotaWarnPolicy` (เกณฑ์ตั้งได้ · `DocumentQuotaPolicy.WarnLevel`/`QuotaLimitPolicy.WarnLevel`/`UsageBarRule.Level` มอบให้ตัวนี้) · `NotificationEvents` `quota.resource_level`/`overage.started`/`overage.capped` · งานรายวันเตือน OCR/ที่นั่ง/พื้นที่/เว็บ/ที่พัก ครั้งเดียวต่อเกณฑ์ต่อเดือน · การ์ด "เมื่อใช้ครบโควตา" ใน hub (ผู้เช่าตั้งเอง: โหมด · ทรัพยากร · เพดานเงิน · ยอมรับฉบับ) + สถานะล็อก/ชำระตอนนี้ · แอดมิน: `admin-quota.html` ข้อ 5 ตารางราคา (ทรัพยากร×แพ็กเกจ·ประวัติ) + บล็อกนโยบายรายบริษัท (ตั้งแทน/ปลดล็อก+เหตุผล) · `platform-billing.html` ค่าตั้ง 5 ช่อง | `Helpers/QuotaWarnPolicy.cs` · `NotificationEvents.cs` · `UsageSnapshotService.cs` · `Controllers/QuotaOverageController.cs` · `Controllers/AdminQuotaOverageController.cs` · `pages/billing.html` · `pages/admin-quota.html` · `admin/platform-billing.html` | แถบ `X-Quota-Warn` บนทุกหน้า (middleware + layout.js) **ยังไม่ทำ** (เห็นจากกระดิ่ง/อีเมล + การ์ด hub) · ✅ `overage.capped` ส่งจาก `OverageService.DecideAsync` เมื่อผู้เช่าเปิดไว้แต่ถูกหยุด (ถึงเพดาน/ราคาถูกปิด) ครั้งเดียวต่อเดือน (`CompanyOveragePolicy.CapNotifiedPeriod` ประทับก่อนส่ง) · ✅ ผู้รับทุกการแจ้งโควตา/ส่วนเกิน = เจ้าของ + ผู้มีสิทธิ์ `BillingManage` (`OverageService.BillingRecipientsAsync` ตัวเดียว) |
| **P3 ประวัติ (§D)** | ✅ `UsageMonthlySnapshots` + 3 ผู้เขียน (Rollover ใน 3 จุดขึ้นเดือน · Daily job · Backfill ตอนอ่าน) · `GET quota/history` (เดือนนี้สด · เดือนก่อน snapshot · ส่วนเกิน/ใบจาก UsageEvents · AI ข้อมูลอย่างเดียว) · hub แท็บ "ใช้ไปเท่าไร" = ส่วนเกิน/นโยบาย + หลอด 4 ตัว (เดิม usage.html) + โควตาเสริม + ข้อความช่องกลาง + ย้อนหลัง (เลือกเดือน/6-12-24 · sparkline · ใบแจ้งหนี้) + รายละเอียดพับได้ (พื้นที่แยกประเภท/ผู้ใช้/ไฟล์ใหญ่สุด) · `usage.html` = redirect | `Helpers/UsageHistoryRule.cs` · `Services/Implementations/UsageSnapshotService.cs` · `Services/Background/UsageSnapshotJob.cs` · `pages/billing.html` · `pages/usage.html` · `js/layout.js` | แอดมิน `admin-company-usage.html` ยังอ่านตารางต้นทาง (ไม่ได้ย้ายมาอ่าน snapshot) · export CSV ไม่ทำ |
| **เทสต์** | ✅ `OverageRound231Tests` (pure 29 เคส — ทิศสองทางทุกด่าน + ใบส่วนเกิน `UsageInvoiceLines` บรรทัด/ครบกำหนด/ขั้นต่ำ/ปิดงวด + ผู้อ่าน `OverageAutoChargeSavedCard`) · `Db/OverageRound231DbTests` (7 เคส: ล็อก/เปิด/cap/ล็อกเพราะค้าง/ปลด/ไม่มีราคา/ราคาปิดแถวเดิม/snapshot ก่อนขึ้นเดือน/`overage.capped` ครั้งเดียว + ผู้รับ Owner+BillingManage) | `Accounting.Tests/` | ยังไม่ได้รันในเครื่องนี้ (ไม่มี SDK) |
| **ฝ่ายค้านรอบ 231** | ✅ B1 ปลดล็อก (`DueCompaniesAsync` ∪ บริษัทที่ล็อก) · B2 พื้นที่คิดจากตัวอย่างรายวัน+ราคา snapshot ไม่ถามโหมด · B3 ใบรวมหลายงวดต่อบริษัท (30+30 ⇒ 1 ใบ 60 · 2 บรรทัด) · S1–S8 · N1–N6 (รายละเอียด ACCOUNT_STRUCTURE §6.2g) | `OveragePolicy` · `OverageService` · `UsageSnapshotService/Job` · `UsageInvoiceLines` · `UsageInvoicingJob` · `PlatformDunningJob/Service` · `NotifyQuotaService` · `SubscriptionService` · `billing.html` · `admin-quota.html` | N7 = ข้อความ TermsNote/hub บอกว่าล็อกเมื่อเกินกำหนด + ตัดบัตรเมื่อเปิด · ยังไม่ได้คอมไพล์ |
| **doc** | ✅ ACCOUNT_STRUCTURE §6.2g · DOCUMENT_FLOW รอบ 231 · TEST_PLAN OV231 · CHANGELOG · DECISIONS 231-1..7 | | ปิด G-16 ของ `PLATFORM_BILLING_AUDIT.md` §2 (ราคา documents.overage = 0) — ตอนนี้ราคาจาก `OverageRates` และ HardLock = 0 โดยตั้งใจ |

**P1 (ราคาพิเศษรายบริษัท) · P4 (เปลี่ยนรอบบิลกลางรอบ) · P5 (วงจรชีวิต Kind/BlockAccessOnExpiry) · P0 (ค)(ง) License audit — ทีมอื่นในรอบ 231 (DECISIONS 231-8..)**

_ตรวจกับ HEAD d9d264a7 · 2026-11-03 · ลงมือ P0(ก)(ข)(จ)/P2a/P2b/P2c/P3 บน working tree รอบ 231 · รอบเก็บงาน (agent ที่สอง): checker 19 กติกา · ผู้อ่าน `OverageAutoChargeSavedCard` · `overage.capped` · ผู้รับ BillingManage · `UsageInvoiceLines` pure + เทสต์ · ลิงก์ข้อความช่องกลาง → hub · ยังไม่ได้คอมไพล์_
