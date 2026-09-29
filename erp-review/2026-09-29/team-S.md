# รอบ 200 ทีม S — แพ็กเกจ/สิทธิ์: ความพร้อมก่อนเปิดบังคับแพ็กเกจบนหน้าเว็บ

> ขอบเขต: `BRIEF.md` แถว S · คำตัดสิน `DECISIONS.md` ข้อ 14 (+ ข้อ 5 ของ `2026-09-25/settlement/DECISIONS.md`) · `review198-D.md` D-P2/D-P3/D-P5
> ฐาน: `5c1fe028` (worktree เริ่มที่ `69fd88e8` ซึ่งเป็นประวัติคนละสาย — แตก branch `team-S-r200` จาก `5c1fe028` โดยไม่ลบอะไร)
> **ยังไม่ได้คอมไพล์** (ไม่มี .NET SDK ในเครื่องนี้) — ดู "ความเสี่ยงคอมไพล์" ท้ายไฟล์ · **ค่าตั้งต้นยังเป็น Shadow — ไม่ได้พลิกเป็น Enforce**

## สรุปหนึ่งย่อหน้า

ก่อนรอบนี้ "บังคับแพ็กเกจ" มีสองสวิตช์ที่ขัดกัน: สวิตช์แอดมินในฐานข้อมูลคุมการตัดสินฟีเจอร์ของหน้าเว็บ แต่ด่าน "บริษัทถูกระงับ/หมดอายุ"
อ่าน config `Subscription:Enforcement:Mode` = `LogOnly` ⇒ แอดมินกดบังคับแล้วบริษัทที่ถูกระงับยังสร้าง/แก้ไขได้ **โดยไม่มีอะไรบอก** และรายงานเงา
ติดป้าย "log อย่างเดียว" ทำให้อ่านว่าไม่มีผลกระทบ. ตอนนี้โหมดที่มีผลจริงมีตัวตัดสินตัวเดียว (`SubscriptionEnforcementResolver`) ที่ middleware และหน้าแอดมิน
ใช้ร่วมกัน · config เหลือบทบาท override ฉุกเฉิน (ชื่อใหม่ `Subscription:Enforcement:EmergencyOverride`) · หน้าแอดมินบอก "โหมดที่มีผลจริง + เพราะอะไร" ·
ลูกค้าทดลอง/ฟรีที่สำเนาฟีเจอร์ว่างใช้ข้อมูลแพ็กเกจ · รายงานเงาบอกครบ "บริษัท · endpoint · เหตุ · กี่ครั้ง (จะบล็อก/บล็อกจริง)" และตัดแถวเก่า ·
D-P3/D-P5 ปิด · D-P2 verify แล้ว. **ที่ต้องให้เจ้าของดูก่อนกดบังคับ** คือเส้น `/bank/accounts` ที่หน้าเอกสาร/ชำระเงินเรียกเบื้องหลัง
(Free Edition ไม่มี BankReconciliation ⇒ เปิดบังคับแล้ว api.js จะพาผู้ใช้ออกไปหน้าแพ็กเกจ) — ดู checklist ข้อ 3

## ตารางรายการ

| # | เรื่อง | สถานะ | file:line | เทสต์ / ด่าน |
|---|---|---|---|---|
| S-1 | สองสวิตช์ขัดกัน (config LogOnly กลบสวิตช์แอดมิน Enforce) | ✅ | `Helpers/SubscriptionEnforcementResolver.cs:88` (Resolve) · `Helpers/SubscriptionGatePolicy.cs:206` (WriteGateModeFor) · `Middleware/SubscriptionMiddleware.cs:94,118` · `appsettings.json` (`EmergencyOverride: ""` แทน `Mode: LogOnly`) · ลบ `ParseWriteMode` | `SubscriptionEnforcementResolverTests` (ทุกคู่ค่า 16 แถว · ไม่มีแถว/อ่านไม่ได้ · config เดิมไม่มีผล 3×3 · บั๊กต้นทาง "กดบังคับ + LogOnly ⇒ บล็อกจริงทั้งเว็บและ partner" · ค่าตั้งต้นไม่เข้มขึ้น · โหมดเงาไม่ใช่ log อย่างเดียว · override Off ไม่หลวม partner · ตาราง WriteGateModeFor 6 แถว) · required_call_site (InvokeAsync forbid `_config[`/`Subscription:Enforcement`) |
| S-2 | หน้าแอดมินแสดง "โหมดที่มีผลจริง + เพราะอะไร" · ห้าม silent no-op | ✅ | `Controllers/AdminSubscriptionEnforcementController.cs` (`EffectiveAsync` · DTO `SubscriptionEffectiveModeDto` · `SetMode` ตอบ "บันทึกแล้วแต่ยังไม่มีผล" เมื่อ override ทับ · อ่านชื่อโหมดด้วย `SubscriptionEnforcementResolver.ParseMode` ตัวเดียวกับ override — ตัวเลข/ว่าง/ไม่รู้จัก = 400 เหมือนเดิม) · `wwwroot/admin/subscription-enforcement.html` (กรอบสีเหลืองเมื่อ override · toast แดง) | required (EffectiveAsync · SetMode `AdminSwitchHasEffect`) · TEST_PLAN SUB-G13..G15 |
| S-3 | FreeTrial ได้ฟีเจอร์ตามข้อมูลแพ็กเกจ (สำเนาว่าง ⇒ ถูกบล็อกทุกเส้นทาง) | ✅ | `Helpers/SubscriptionTrialReadiness.cs` (ResolveFeatures · CheckTemplate) · `SubscriptionService.ResolveGateOverlayAsync` :523 (ตัวเดียวของหน้าเว็บ `hasFeature` + gate) · precheck "ความพร้อมของแพ็กเกจทดลอง/ฟรี" (`TrialReadinessAsync` :252) | `SubscriptionTrialReadinessTests` (สำเนาว่าง→TrialFeatures · ฟรีถาวร→EnabledFeatures · สำเนาไม่ว่างไม่แตะ · แพ็กเกจเสียเงินไม่เติม · แพ็กเกจว่างไม่แต่ง · seeder Free Edition ไม่ว่าง/ภาษีไม่ถูกปิด · แพ็กเกจว่าง = ปัญหา) · required (ResolveGateOverlayAsync ก่อน mask เจ้าของ) · SUB-G17 |
| S-4 | seeder: แพ็กเกจทดลองตั้งฟีเจอร์ครบไหม | ✅ NOT-A-BUG | `Data/SeedPlanTemplates.cs:50,90,130,165-171` — ทุกแพ็กเกจ seed `TrialFeatures` ไม่ว่าง (Starter=Basic · Pro=Pro · Enterprise=Enterprise · Free Edition=TrialFeatures) | ความเสี่ยงอยู่ที่ข้อมูลที่แอดมินแก้ภายหลัง (บันทึกรายการว่างได้ `FeatureFlagsHelper.FromNameList([])`) + คอลัมน์ `DEFAULT 0` ⇒ precheck แสดงแทน |
| S-5 | รายงานเงา "บริษัทไหน · endpoint ไหน · เพราะอะไร · กี่ครั้ง" | ✅ | คีย์แถว + `Endpoint` (route template ไม่มี id — `SubscriptionGatePolicy.EndpointKey` :225) · `BlockedCount` (ถูกบล็อกจริงหลังบังคับ · middleware :149) · `SubscriptionStatus` · migration `Data/DatabaseMigrationHelper.cs:6205-6212` (3 คอลัมน์ + ถอด unique เดิม → `IX_…_KeyV2`) | `endpoint_ใช้templateไม่มีid` ×7 · เพดาน 200 · required (RecordShadowAsync ห้าม QueryString) · SUB-G16/G18 |
| S-6 | "ทดลองใช้ถูกบล็อกกี่ครั้งเพราะอะไร" | ✅ | `SubscriptionTrialReadiness.SummarizeTrialBlocks` · การ์ด 🧪 บนหน้าแอดมิน (เซิร์ฟเวอร์สรุป · JS แสดงอย่างเดียว) | `สรุปลูกค้าทดลอง_…` ×2 |
| S-7 | ตารางไม่โตไม่จำกัด (multi-instance) | ✅ | `ISubscriptionGateShadowLog.PruneAsync` — DELETE แถวที่ไม่ถูกพบซ้ำ > 90 วัน ทุกครั้งที่เปิดหน้ารายงาน (idempotent ข้าม instance · ไม่ใช่ BackgroundService ⇒ ไม่ต้อง JobLock) · cardinality ถูกจำกัดโดย บริษัท×เหตุ×ฟีเจอร์×endpoint template (ไม่มี id) · upsert อะตอมมิกเดิม | required (Get: PruneAsync) · SUB-G19 |
| S-8 | เส้นเว็บที่ไม่ส่ง `X-Company-Id` ตัดสินด้วย `TenantCompanyId` ทุกเส้น? | ✅ verify + 📋 | เส้น `/api/companies/{companyId}/…` + `/api/mobile/companies/{companyId}/…` = ✅ (route) · **ไม่ครอบ**: `/api/v1/*` (บริษัทจากคีย์ API — มีด่าน scope/feature ของ Connected เอง แต่ไม่ดูสถานะระงับ/หมดอายุ) · `/api/signatures/*` = ลายเซ็นระดับผู้ใช้ (ไม่มีบริษัท — ถูกต้อง) · `/api/portal`, `/api/public/*` = ภายนอก | ACCOUNT_STRUCTURE §5.2 "ยังไม่ครอบ" · คำถามค้าง Q2 |
| D-P3 | settlement ผูก feature key เดียวกับกระทบยอดธนาคาร | ✅ | `SubscriptionGatePolicy.cs:129` `("/settlement", BankReconciliation)` · `wwwroot/js/layout.js:1259,1261` | `เส้นทางที่ถูกgate_…` +3 · ทิศตรงข้าม `/pay/settlements/…` · `…/orphaned-settlement-receipts` · `…/settlement-proposal` ไม่ถูกผูก |
| D-P5 | `Settlement.View` อย่างเดียวไม่เห็นผู้สมัคร/ยอดค้าง | ✅ | `Helpers/SettlementPermissionScope.cs:69,77` · `Controllers/SettlementController.cs:254` (สิทธิ์ชุดเดียวกับปุ่ม) · `candidatesHiddenReason` + แบนเนอร์ 🔒 `settlements.html` | `มีสิทธิ์นำเข้าหรือลงบัญชี_เห็นผู้สมัคร` ×3 · `ดูอย่างเดียว_…ซ่อน_พร้อมเหตุผล_สถานะยังอยู่` · required (BatchDetailAsync) · SUB-G20 |
| D-P2 | จำการจับคู่คอลัมน์ผ่าน import ด้วยด่านต่างกัน | ✅ verify `266acad2` | `SettlementPermissionScope.ColumnMapMemory` · `SettlementController.ImportFile` · หน้าเว็บ `reference.columnMapMemory` | `DP2_…` (SettlementReview198DTests) · required (ImportFile) · ติ๊ก `review198-D.md` (D-P1 ด้วย — sha เดียวกัน) |
| F-1 | ตาราง `RouteFeatureMap` มีคีย์ที่ไม่ตรง endpoint จริง | 📋 คำถามค้าง Q1 | ดูหัวข้อ "สิ่งที่พบระหว่างทาง" | — |

## สิ่งที่พบระหว่างทาง (verify ด้วยการเปิดไฟล์จริง · ไม่ได้แก้ — เหตุผลอยู่ท้ายแต่ละข้อ)

**F-1 คีย์ `RouteFeatureMap` ที่ไม่เคยตรง endpoint ⇒ ฟีเจอร์นั้นไม่ถูก gate เลยแม้เปิดบังคับ** (ทิศหลวม — ไม่บล็อกใคร แต่ขายแพ็กเกจไม่ได้ผล):

| คีย์ในตาราง | route จริง | ผล |
|---|---|---|
| `/fixed-assets` | `FixedAssetController` `[controller]` ⇒ `/fixedasset` | FixedAssets ไม่ถูก gate |
| `/multi-currency` | `CurrencyController` ⇒ `/currency` | MultiCurrency ไม่ถูก gate |
| `/warehouse` | `/warehouses` | WarehouseManagement ไม่ถูก gate |
| `/commission` | `/commissions` | Commission ไม่ถูก gate |
| `/approval` | `/approvals` (`DocumentApprovalController`) | ApprovalWorkflow ไม่ถูก gate |
| `/ai/` | `/ai` (ตัวจับคู่ต่อ `/` ท้ายคีย์ ⇒ ต้องเจอ `/ai//`) | AI_Features ไม่ถูก gate · `…/ai/bank/…` ตกไป `/bank` · `…/ai/ocr/…` ตกไป `/ocr` |
| `/commerce` · `/booking` | อยู่ใต้ `/cms/sites/{id}/…` — คีย์ `/cms/sites` ยาวกว่าจึงชนะ | CmsEcommerce/CmsBooking ไม่ถูก gate (ได้ CmsWebsiteBuilder แทน) |

ไม่แก้เพราะ: ตารางเดียวกันใช้กับคำขอ partner ที่ส่ง `X-Company-Id` ซึ่ง**บังคับเสมอ** ⇒ แก้คีย์ = บล็อก partner ทันที (ไม่ผ่านโหมดเงา) และเป็นการ
ตัดสินเชิงผลิตภัณฑ์ (ฟีเจอร์ไหนขาย) — เลือกทิศมองเห็นได้: รายงานไว้ที่นี่ + คำถาม Q1

**F-2 `/bank` ครอบ "รายการบัญชีธนาคาร" ที่หน้าหลักใช้** — `GET /api/companies/{id}/bank/accounts` ถูกเรียกจาก `documents.html` · `payments.html` ·
`cheques.html` · `petty-cash.html` · `tax-remittance.html` · `payroll.html` · `recurring.html` · `lodging.html` · และ `api.js:420-424` เมื่อได้ 403
`FEATURE_NOT_AVAILABLE` จะ **พาทั้งหน้าไป `/pages/subscription.html` หลัง 1.5 วินาที** ⇒ ถ้าเปิดบังคับวันนี้ ลูกค้า Free Edition/Starter ที่ไม่มี
BankReconciliation เปิดหน้าเอกสารขายแล้วถูกดีดออก. ไม่แก้เพราะเป็นทางแยกเชิงผลิตภัณฑ์ใหม่ (ยกเว้นทะเบียนบัญชีธนาคารแบบ `/dimensions/branches`
หรือเพิ่ม BankReconciliation ในแพ็กเกจเล็ก) — **รายงานเงาจะแสดงแถว endpoint `api/companies/{companyid}/bank/accounts` ให้เห็นก่อน** · คำถาม Q3

## ขั้นตอนที่เจ้าของต้องทำก่อนกด Enforce (checklist)

> ปรับโดยทีม S2 (รอบ 200 · `team-S2.md`) หลังคำตัดสินข้อ 21–24 + ฝ่ายค้าน `review200-S.md` — **ข้อที่ต้องทำก่อน deploy อยู่หัวรายการ** ·
> ข้อ 3 (F-2 · Q3) และข้อ 8 (F-1 · Q1) เดิม **ตัดสินแล้ว** (ข้อ 21/22) และลงโค้ดแล้ว · ค่าตั้งต้นยัง Shadow

### ก. ก่อน deploy (ทำครั้งเดียว)

- [ ] **A1. ตรวจ config ทุกเครื่อง** — `Subscription:Enforcement:Mode` / env `Subscription__Enforcement__Mode`:
      ถ้าเป็น `Enforce` ⇒ หลัง deploy **partner ที่ส่ง X-Company-Id ยังถูกบล็อกการเขียนเมื่อบริษัทถูกระงับ/หมดอายุแบบเดิม** (S200-1 · ไม่หลวมลง) +
      log Warning ตอนบูต + กรอบแดงบนหน้าแอดมิน — ลบคีย์เมื่อพร้อมให้ partner เดินตามสวิตช์ · ค่าอื่น (`LogOnly`/`Off`) ไม่มีผล ลบได้เลย ·
      `Subscription:Enforcement:EmergencyOverride` ต้อง**ว่าง**ทุกเครื่อง
- [ ] **A2. migration** — ตาราง `SubscriptionGateShadowHits`: สร้าง unique `…_KeyV2` **ก่อน** ถอด `…_Key` เดิม (S200-7 · rolling deploy ปลอดภัย) +
      คอลัมน์ `PartnerHitCount`/`PartnerBlockedCount` · ดู log migration ว่าไม่มี error ของตารางนี้

### ข. หลัง deploy — ใช้งานในโหมดเงา

- [ ] **1. ยืนยันหน้าแอดมิน** `/admin/subscription-enforcement.html` ไม่ขึ้น "อ่านตารางผลโหมดเงาไม่ได้" · กรอบ "โหมดที่มีผลจริง" =
      `โหมดเงา (Shadow) — ตามสวิตช์แอดมิน` (ไม่ใช่ "อ่านสวิตช์ไม่ได้" / "ค่าตั้งต้น") · กรอบแดง config เดิมหายเมื่อลบคีย์แล้ว (A1)
- [ ] **2. ใช้งานจริงในโหมดเงาอย่างน้อย 1–2 สัปดาห์** (ครอบรอบปิดเดือน/ยื่น ภ.พ.30 — วันที่ 1–15) แล้วอ่านรายงานเงา: ทุกแถว "จะถูกบล็อก"
      (ทั้งคอลัมน์หน้าเว็บ **และคอลัมน์ partner/API** — S200-3) ต้องตอบได้ว่า "ตั้งใจให้บล็อก" หรือ "แก้แพ็กเกจ/เปิดฟีเจอร์ให้ลูกค้า"
- [ ] **3. แถวป้าย "คีย์ใหม่รอบ 200"** (`/settlement` · `/fixedasset` · `/currency` · `/warehouses` · `/commissions` · `/approvals` · `/ai` ·
      `/cms/sites/*/commerce|booking` — ข้อ 22): ฟีเจอร์เหล่านี้ไม่เคยถูก gate จริงมาก่อน ⇒ ดูเป็นพิเศษ · โดยเฉพาะ **`/ai`**: ตัวแนะนำหลายตัว
      (`ai/gl-account/suggest` · `ai/payment-terms/suggest` · `ai/vat/infer-type` …) ถูกเรียกเบื้องหลังจากหน้าเอกสาร/สมุดรายวัน/เงินสดย่อย/ผู้ติดต่อ ⇒
      เมื่อบังคับ ลูกค้าที่ไม่มี AI_Features จะเสียตัวแนะนำ (หน้าไม่ถูกดีด — ข้อ 24 · เตือนครั้งเดียว) — ตัดสินว่าจะใส่ AI_Features ในแพ็กเกจเล็ก
      หรือยอมให้ตัวแนะนำหายไป
- [ ] **4. การ์ด 🧪 ลูกค้าทดลองใช้** — ดูเหตุ × ฟีเจอร์ที่ลูกค้าทดลอง/ฟรีจะโดน · ตัดสินว่าชุดฟีเจอร์ของแพ็กเกจทดลองพอสำหรับ "ลองใช้จริง" ไหม
- [ ] **5. กด "คำนวณ" (ตรวจล่วงหน้า)** ทุกหน้า (200 บริษัท/หน้า) — ส่วน "ความพร้อมของแพ็กเกจทดลอง / ฟรี" ต้อง**ไม่มีป้ายแดง "ว่าง"** และต้องมีแพ็กเกจ
      FreeTrial ที่เปิดใช้ · "ถูกบล็อกทุกคำขอ" (subscription Cancelled/Suspended) ต้องเป็นบริษัทที่ตั้งใจระงับจริงเท่านั้น · subscription ทดลองที่แอดมิน
      ตั้ง TrialFeatures รายบริษัทเป็น "ว่าง" จะไม่ถูกเติมจากแพ็กเกจ (S200-6 เคารพค่าที่ตั้งใจ)
- [ ] **6. บริษัท "ถูกบล็อกการสร้าง/แก้ไข"** (ระงับ/หมดอายุเกินผ่อนผัน) — เมื่อกดบังคับ ด่านนี้บล็อกหน้าเว็บ · partner · **และ `/api/v1`** (ข้อ 23 ·
      ดูคอลัมน์ partner/API endpoint `api/v1/…`) — แจ้งลูกค้าที่หมดอายุ/ต่ออายุให้ก่อน
- [ ] **7. "ล้างผล / เริ่มนับใหม่"** หลังแก้แพ็กเกจแล้ว ใช้งานต่อ 2–3 วัน · "บริษัทที่จะถูกบล็อก" (ทั้งสองคอลัมน์) ต้องเหลือเฉพาะที่ตั้งใจ

### ค. กดบังคับ

- [ ] **8. กดบังคับ** (confirm บอกจำนวนบริษัท) → สิ่งที่เปลี่ยนพร้อมกัน: หน้าเว็บถูกบล็อกตามแพ็กเกจ · คีย์ใหม่รอบ 200 บังคับ partner ด้วย · `/api/v1`
      บล็อกเมื่อระงับ/หมดอายุ · เมนู settlements/settlement-channels ขึ้น 🔒 (เดิมป้าย "แพ็กเกจไม่รวม") · มีผลทุกเครื่องภายใน ≤5 วินาที (แคช S200-8)
      → ดูคอลัมน์ "ถูกบล็อกจริง" + "partner/API ถูกบล็อกจริง" ใน 24 ชม. แรก · **ย้อนได้ทันที**: กดกลับเป็นโหมดเงา · ถ้าหน้าแอดมิน/ฐานข้อมูลใช้ไม่ได้
      ตั้ง env `Subscription__Enforcement__EmergencyOverride=Shadow` (หรือ `Off`) แล้วเริ่มระบบใหม่ — หน้าแอดมินจะขึ้นกรอบสีเหลืองจนกว่าจะลบ

## คำถามค้าง (ทางแยกใหม่นอก DECISIONS — เลือกทิศมองเห็น/ย้อนได้แล้ว)

> ทั้ง 4 ข้อ **ตัดสินแล้ว** (DECISIONS ข้อ 21–24) และลงโค้ดโดยทีม S2 (`team-S2.md`) — ติ๊กไว้ ไม่ลบแถว

- ✅ (ข้อ 22 · S2) **Q1** แก้คีย์ `RouteFeatureMap` ที่ไม่ตรง endpoint (F-1) ไหม — ผลคือฟีเจอร์ FixedAssets/MultiCurrency/Warehouse/Commission/Approval/AI/CMS-commerce/booking
  ถูก gate จริง (partner ที่ส่ง header ถูกบังคับทันที) · ตอนนี้: ไม่แก้ · รายงาน
- ✅ (ข้อ 23 · S2) **Q2** `/api/v1/*` (Connected) ควรเคารพสถานะระงับ/หมดอายุของบริษัทไหม (วันนี้ไม่ดู) · ตอนนี้: ไม่แตะ · บันทึกใน ACCOUNT_STRUCTURE §5.2
- ✅ (ข้อ 21 · S2) **Q3** `GET …/bank/accounts` (ทะเบียนบัญชีธนาคารที่หน้าเอกสาร/ชำระเงินใช้) ควร: (ก) ยกเว้นจาก gate แบบ `/dimensions/branches` หรือ
  (ข) ใส่ BankReconciliation ในแพ็กเกจเล็ก หรือ (ค) คงไว้ (ลูกค้าแพ็กเกจเล็กใช้หน้าเอกสารไม่ได้เมื่อบังคับ) · ตอนนี้: ไม่แก้ (ค่าตั้งต้น Shadow ไม่บล็อกใคร) — ต้องตัดสินก่อนกดบังคับ
- ✅ (ข้อ 24 · S2) **Q4** `api.js` ดีดทั้งหน้าไปหน้าแพ็กเกจเมื่อคำขอ "เบื้องหลัง" ใดก็ได้ได้ 403 ฟีเจอร์ — ควรดีดเฉพาะคำขอหลักของหน้าไหม · ตอนนี้: ไม่แตะ (นอกขอบเขต · ผูกกับ Q3)

## ไฟล์ที่แตะ (นอกขอบเขตทีมอื่น: แตะ `SettlementController.BatchDetailAsync` 5 บรรทัด + `settlements.html` 1 บรรทัด + `SettlementPermissionScope` เพิ่ม 2 เมธอด — D-P5)

`Helpers/SubscriptionEnforcementResolver.cs` (ใหม่) · `Helpers/SubscriptionTrialReadiness.cs` (ใหม่) · `Helpers/SubscriptionGatePolicy.cs` · `Helpers/SettlementPermissionScope.cs` ·
`Middleware/SubscriptionMiddleware.cs` · `Services/Interfaces/ISubscriptionGateShadowLog.cs` · `Services/Implementations/SubscriptionGateShadowLog.cs` ·
`Services/Implementations/SubscriptionService.cs` (overlay) · `Controllers/AdminSubscriptionEnforcementController.cs` · `Controllers/SettlementController.cs` ·
`Data/DatabaseMigrationHelper.cs` · `Models/Entities/SiteSettings.cs` (doc) · `appsettings.json` · `wwwroot/admin/subscription-enforcement.html` ·
`wwwroot/js/layout.js` · `wwwroot/pages/settlements.html` · เทสต์ใหม่ 2 ไฟล์ + แก้ `SubscriptionGatePolicyTests` · `tools/required_call_site_check.py` · ACCOUNT_STRUCTURE §5.2 ·
TEST_PLAN §0 + SUB-G07/G13–G20 · CHANGELOG · `review198-D.md` (ติ๊ก D-P1/D-P2/D-P3/D-P5)

## ความเสี่ยงคอมไพล์ที่เหลือ (ไม่มี .NET SDK — CI บน `claude/**` คือ compiler ตัวแรก)

1. `context.GetEndpoint() as RouteEndpoint` + `RoutePattern.RawText` ใน middleware — ต้องการ `using Microsoft.AspNetCore.Routing` (ใส่แล้ว) · `GetEndpoint` อยู่ใน `Microsoft.AspNetCore.Http` (implicit using)
2. `SettlementLineView`/`SettlementBatchView` ใช้ `with { … }` ใน `SettlementPermissionScope.HideCandidates` — เป็น positional `sealed record` (with ได้) · `Lines` รับ `List<>` เข้า `IReadOnlyList<>`
3. `ExecuteSqlRawAsync(sql, object[] { cutoff }, ct)` ใน `PruneAsync` — overload `IEnumerable<object>` + `CancellationToken` (แบบเดียวกับ `RecordAsync` เดิม)
4. named argument กลางรายการ (`IsWrite: true, writeMode, …`, `WouldBlock: true, endpoint, …`) — ถูกตำแหน่ง (C# 7.2+) · แบบเดียวกับโค้ดเดิมใน controller
5. `Assert.Equal(new[] {…}, v.LogOnly)` (array vs `IReadOnlyList`) และ `Assert.Equal(10, long)` ในเทสต์ — ใช้ overload `IEnumerable<T>`/อนุมาน `long`
6. cref `SubscriptionEnforcementResolver.Resolve(SubscriptionAdminSwitchRead, Microsoft.Extensions.Configuration.IConfiguration)` ใน doc-comment — ผิดได้แค่ warning (ไม่มี TreatWarningsAsErrors)

## commit

- โค้ด + เอกสาร: `2bff9ece` (sha นี้เติมในคอมมิตตามหลัง — ไม่ amend)
