# QUOTA_ADDON_SUBSCRIPTION_AUDIT — โควตา · ส่วนเสริมที่ซื้อได้ · การต่ออายุ · UX หน้าสมัคร/บิล (2026-11-02)

> คำขอเจ้าของ (สรุป): (1) แอดมินตั้ง**จำนวนเว็บไซต์ CMS ต่อแพ็กเกจ**ได้ และผู้เช่า**ซื้อโควตาเว็บเพิ่ม**ได้ จ่ายผ่านระบบ (Omise/สลิป — `PaymentIntent` เดิม)
> (2) ไล่ทุกอย่างที่ "มีเพดาน/ซื้อได้" ว่าบังคับจริงไหม · แอดมินตั้งต่อแพ็กเกจได้ไหม · ซื้อเพิ่มได้ไหม · ซื้อแบบไหนได้ (ครั้งเดียว/เดือน · รายเดือนต่อเนื่อง · ถึงวันหมดอายุ License แบบคิดตามวัน)
> (3) ตอนต่ออายุ เลือกส่วนเสริมที่จะต่อพร้อมกันได้ (4) ตรวจ UX/UI ทุกหน้าที่เกี่ยวกับการสมัคร/บิล/โควตา ทั้งฝั่งผู้เช่าและแอดมิน
>
> ประเภทงาน: **ตรวจอย่างเดียว + ออกแบบ** — ไม่แก้โค้ด · ตรวจกับ HEAD `a138faaa` (มีทีมอื่นแก้โค้ดพร้อมกัน — เลขบรรทัดอาจเลื่อน ใช้ชื่อเมธอดเป็นหลัก)
> อ้างอิง: `ACCOUNT_STRUCTURE.md` §6.1b/§6.1c/§6.2b–d · `erp-review/2026-10-30/PLATFORM_BILLING_AUDIT.md` (ตำแหน่งจริงของไฟล์ — ไม่ใช่ 2026-11-01) ·
> `erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md` §5.8/§7.1 (ดีไซน์ proration) · CHANGELOG รอบ 212–213/219/227–228

---

## 0. คำตอบสั้น

1. **โควตาเว็บไซต์ CMS ไม่มีเลย** — `CmsSiteService.CreateSiteAsync` (`Services/Implementations/CmsSiteService.cs:33` → `_db.Sites.Add` `:96`) ตรวจแค่ชื่อซ้ำ/สาขา/คลัง
   ด่านเดียวคือบิต `CmsWebsiteBuilder` ของแพ็กเกจ (`Helpers/SubscriptionGatePolicy.cs:215`) ⇒ มีสิทธิ์ CMS = สร้างกี่เว็บก็ได้ · แอดมินไม่มีช่องตั้ง · ซื้อเพิ่มไม่ได้
2. **เพดานหน้า/สินค้าต่อเว็บ "ต่อสายด่านแล้วแต่ไม่มีค่า"** — ด่านถูกเรียกจริง (`CmsContentService.cs:42` · `CmsCommerceService.cs:70`) แต่ `Site.MaxPages/MaxProducts/MaxStorageBytes`
   (`Models/Entities/CmsSite.cs:104-108` คอมเมนต์ "override from subscription tier") **ไม่มีผู้เขียนเลยทั้งเรพ** ⇒ null = ไม่จำกัดเสมอ · `CmsQuotaService.CanUploadMediaAsync` (`:63`) ไม่มีผู้เรียก ·
   แบนด์วิดท์ไม่มีตัวนับ
3. **ของที่ซื้อเพิ่มได้จริงวันนี้มี 4 แบบ และไม่มีแบบไหนคิดตามวันถึงวันหมดอายุ License:**
   แพ็กเอกสาร (`documents.topup` · 100 ฉบับ/60 วัน · คิดเงินเข้าใบรายเดือน) · แพ็กข้อความ (`notify.topup`) · ส่วนเสริมสวิตช์ที่พัก 9 ตัว (เหมารายเดือน: จ่ายเดือนแรกล่วงหน้า หรือรวมใบรายเดือน) ·
   เครดิต OCR (**ไม่มีเส้นเงิน** — สร้างแถว Pending ให้แอดมินอนุมัติ ไม่มี `PaymentIntent` ไม่มีใบกำกับ ไม่มีหน้าจอผู้เช่า)
4. **ผู้ใช้ (seat) บังคับจริง แต่ซื้อเพิ่มไม่ได้** · **บริษัท/สาขา/คลัง/POS/โดเมน ไม่บังคับ** · **รายการบัญชี/เดือน ตั้งได้ + โชว์ แต่ไม่บังคับ** · **พื้นที่** บล็อกเฉพาะสื่อ CMS (ไฟล์แนบแค่เตือน)
5. **ใบต่ออายุมีบรรทัดเดียว (แพ็กเกจ)** — `SaasBillingDocumentService.GenerateRenewalInvoiceAsync` (`:389`, ออกที่ `:426`) ⇒ ยังไม่มีทางเลือกส่วนเสริมต่อพร้อมกัน
6. **บั๊กเงินที่พบระหว่างตรวจ (P1):** ค่าเหมาส่วนเสริมรายเดือน**ยังออกต่อหลังแพ็กเกจหมดอายุ/ยกเลิก** — `AddOnMonthlyBillingJob` (`Services/Background/AddOnMonthlyBillingJob.cs:85`)
   กรองแค่ `IsEnabled` ไม่ดูสถานะแพ็กเกจ ขณะที่ `EntitlementService.CheckAsync` ① ปิดการใช้ส่วนเสริมเมื่อแพ็กเกจไม่ active ⇒ **เก็บเงินโดยไม่มีบริการ** (ไม่มีจุดไหนแตะ `CompanyFeatures` ตอนยกเลิก — มีแค่ `SubscriptionService.cs:520` อ่าน)
7. **UX:** เรื่องเดียว (แพ็กเกจ/บิล/โควตา) กระจาย **5 เมนู** (`wwwroot/js/layout.js:1761-1777`) + ตัวเลขโควตาโชว์ 3 หน้าด้วยเกณฑ์ "ไม่จำกัด" 3 แบบ ·
   การซื้อแพ็กเอกสารกดยืนยัน**โดยไม่เห็นราคา** (`pages/addons.html:208`) · หน้าแพ็กเกจโชว์ผู้ใช้ "1/x" ตายตัว (`pages/subscription.html:287`)

ข้อเสนอหลัก: **โมเดล "โควตาเสริม" เดียว** — รายการทรัพยากรปิด (closed list ในโค้ด) · แพ็กเกจรวมให้เท่าไร (แอดมินตั้ง) · ซื้อเพิ่มได้ 3 โหมด (เดือนเดียว · รายเดือนต่อเนื่อง · ถึงวันหมดอายุแบบคิดตามวัน) ·
ด่านตัวเดียว `IQuotaGate` · เงินผ่าน `PlatformPaymentService` + ใบกำกับจาก `PlatformBillingDocumentIssuer` เดิม · ต่ออายุพร้อมส่วนเสริมในใบแจ้งหนี้หลายบรรทัด — §B · แผน 7 แพ็ก ≈ 13–16 วันทำงาน — §E

---

## A. Inventory — ทรัพยากรที่มีเพดาน / ซื้อได้ ทั้งระบบ

สัญลักษณ์: ✅ มีและทำงาน · ⚠️ มีบางส่วน/มีแต่ไม่ครบ · ❌ ไม่มี · "แอดมินตั้ง" = ตั้งต่อแพ็กเกจที่ `/admin/plans.html` (`PlanTemplate`) หรือแคตตาล็อก `/pages/admin-addons.html` (`ApiFeature`/`ApiPricingPlan`)

| # | ทรัพยากร | บังคับเพดานจริงไหม (file:line) | แอดมินตั้งต่อแพ็กเกจ | ซื้อเพิ่มได้ | โหมดที่รองรับ | ช่องโหว่ / หมายเหตุ |
|---|---|---|---|---|---|---|
| 1 | **ผู้ใช้ (seat) ต่อบริษัท** | ✅ `CompanyService.AddUserAsync` `:401-421` (สมาชิก + คำเชิญค้าง ≥ เพดาน ⇒ โยน) · License ชนะ (`MaxUsersPerCompany`) | ✅ `PlanTemplate.MaxUsers` (`admin/plans.html:273`) · รายบริษัท `AdminController.cs:1730` · License `AdminAccountSubscriptionController.cs:205` | ❌ | — | โยน `InvalidOperationException` ("กรุณาอัปเกรดแพ็กเกจ") — ไม่มีทางซื้อ seat · หน้าแพ็กเกจโชว์ใช้ไป `1` ตายตัว (`subscription.html:287`) · `team.html:168` โหลดพลาด ⇒ `999` (โชว์ "ไม่จำกัด") |
| 2 | **บริษัท** | ❌ ตอนสร้าง (`CompanyService.CreateAsync:44` ตรวจแค่ `RegistrationPolicy`) — ทุกบริษัทได้ trial ของตัวเอง (`EnsureSubscriptionForNewCompanyAsync:129`) · ⚠️ License จำกัดแค่**การผูก** (`AccountSubscriptionController.cs:213` · auto-attach `CompanyService.cs:193`) | ⚠️ `PlanTemplate.MaxCompanies` (`plans.html:278`) — บน sub รายบริษัท `Subscription.MaxCompanies` (`Subscription.cs:67`) **ไม่มีผู้อ่านเชิงธุรกิจ** | ❌ | — | ค่าตั้งที่ไม่มีผล (F2 ข้อ 2) · ถ้าจะขาย "บริษัทเพิ่ม" ต้องขายผ่าน License เท่านั้น |
| 3 | **สาขา** | ❌ `DimensionalAccountingService.CreateBranchAsync:307` | ❌ | ❌ | — | — |
| 4 | **คลัง / เครื่อง POS** | ❌ `WarehouseService.cs:56` · `PosService.cs:64` | ❌ | ❌ | — | POS หลายสาขาเป็นของขายได้ในอนาคต (POS_MULTI_BRANCH_ANALYSIS) |
| 5 | **เอกสาร/เดือน** | ✅ แบบนุ่ม: `DocumentService.cs:1082` (`CheckUsageLimitAsync("document")` `SubscriptionService.cs:989`) — ใบที่กฎหมายบังคับไม่บล็อก ⇒ มิเตอร์ `documents.overage` (`DocumentService.cs:4205`) | ✅ `MaxDocumentsPerMonth` (`plans.html:283`) + Trial | ✅ `documents.topup` `QuotaService.PurchaseTopUpAsync:94` | แพ็ก 100 ฉบับ อายุ 60 วัน (ครั้งเดียว) · คิดเงินเข้าใบแจ้งหนี้ค่าใช้งานงวดถัดไป (postpaid) | ขนาด/อายุแพ็ก**ฝังในโค้ด** (`AddOnCodes.DocumentsPerTopUpPack`/`TopUpValidDays`) ต่างจาก `notify.topup` ที่ตั้งได้ · หน้าเว็บไม่โชว์ราคา ไม่ส่ง `Idempotency-Key` ไม่ส่งข้อเสนอที่เห็น (`addons.html:208-210`) · ไม่ตั้งราคา ⇒ ฿0 (`UsageMeteringService.cs:83` · G-16 เดิมยังเปิด) |
| 6 | **รายการบัญชี/เดือน** | ❌ `CheckUsageLimitAsync(…,"journal")` ไม่มีผู้เรียก (บันทึกไว้เองที่ `SubscriptionService.cs:652`) | ⚠️ ตั้งได้ (`plans.html:287`) + โชว์ในหน้าแพ็กเกจ (`subscription.html:286`) | ❌ | — | โชว์เพดานที่ไม่บังคับ = ทำให้ผู้ใช้กังวลเปล่า ๆ |
| 7 | **พื้นที่จัดเก็บ (ทั้งบริษัท/License)** | ⚠️ บล็อกเฉพาะสื่อ CMS (`CmsContentService.cs:570` → `CanFitStorageAsync` `SubscriptionService.cs:1109`) · ไฟล์แนบ = เตือนอย่างเดียว | ✅ `MaxStorageBytes` (`plans.html:296`) | ❌ | — | ดู `erp-review/2026-11-02/FILE_STORAGE_AUDIT.md` |
| 8 | **OCR หน้า/เดือน** | ✅ `OcrController.cs:195-199` (`TryConsumeAsync` ⇒ 429) | ✅ รวม/Azure/Local/Trial (`plans.html:309-327`) | ⚠️ `OcrQuotaService.PurchaseCreditsAsync:416` (`OcrController.cs:1619`) | ครั้งเดียว อายุ 12 เดือนหลังแอดมินอนุมัติ (`ReviewCreditPurchaseAsync` `:446`) | **ไม่มีเส้นเงิน**: ไม่มี `PaymentIntent`/สลิป/ใบกำกับ (G-9/O1 ของ PLATFORM_BILLING_AUDIT ยังเปิด) · ไม่มีหน้าจอผู้เช่า แต่ข้อความ 429 บอก "กรุณาซื้อเครดิตเพิ่ม" = ทางตัน · ราคา `SiteSettings.OcrCreditPricePerPage` (ไม่ผ่านแคตตาล็อก) |
| 9 | **ข้อความแจ้งเตือนผ่านช่องกลาง** (LINE OA + SMTP ของ NextAcc) | ✅ `NotifyQuotaPolicy` / `NotifyQuotaService.cs:176-205` — เต็มแล้วไม่บล็อกงาน แค่ไม่ส่งทางช่องกลาง | ✅ `MaxSharedMessagesPerMonth` (+Trial) (`plans.html:291`) | ✅ `notify.topup` (`NotifyQuotaService.PurchaseTopUpAsync`) | แพ็ก (ขนาด/อายุตั้งได้ `admin-addons.html:226-230`) · postpaid | **ต้นแบบที่ดีที่สุดตอนนี้**: โชว์ราคา + ส่งข้อเสนอที่เห็น ⇒ 409 ถ้าเปลี่ยน (`js/customer-notify.js:164-180`) |
| 10 | LINE OA / SMTP ของร้านเอง | — ไม่นับโดยตั้งใจ (`NotifyQuotaPolicy.IsMetered`) | — | — | — | ถูกต้อง |
| 11 | อีเมล/SMS ของโมดูลที่พัก | ❌ `AddOnCodes.LodgingEmailOverage` · `LodgingSms` **ไม่มีผู้เขียน UsageEvent เลย** | ⚠️ ราคาในแคตตาล็อกตั้งได้แต่ไม่มีผล | ❌ | — | รหัสที่ประกาศขายแต่ไม่มีมิเตอร์ (ลบ หรือต่อสาย) |
| 12 | **การเรียก AI** | ⚠️ ระดับแพลตฟอร์มเท่านั้น (`Services/Ai/AiBudgetGuard.cs` — DailyCallCap/MonthlyBudgetUsd ต่อ provider) · ผู้เช่าเปิด/ปิดด้วยบิต `AI_Features` | ❌ ต่อแพ็กเกจ | ❌ | — | **ไม่แนะนำให้ขายเป็นโควตา** — กฎเหล็ก #1: local ต้องแทนได้ 100% ผู้ใช้ไม่ควรรู้สึกต่าง · ถ้าต้องการรายได้ ให้ขาย "ฟีเจอร์" ไม่ใช่ "จำนวน call" |
| 13 | **e-Tax** (`EtaxByEmail`/`EtaxDirect`) | ❌ จำนวน — เป็นบิตแพ็กเกจอย่างเดียว | ⚠️ บิต | ❌ | — | นับรวมในโควตาเอกสาร (#5) ไม่ต้องแยก |
| 14 | **เว็บไซต์ CMS (จำนวนเว็บ)** | ❌ `CmsSiteService.CreateSiteAsync:33` (ไม่มีด่านจำนวน) · บิต `CmsWebsiteBuilder` อย่างเดียว | ❌ | ❌ | — | **คำขอข้อ (1) ของเจ้าของ** — §C |
| 15 | หน้าเว็บต่อเว็บ | ⚠️ ด่านต่อสายแล้ว `CmsContentService.cs:42` · เติมเทมเพลต `CmsSiteService.cs:612` | ❌ `Site.MaxPages` ไม่มีผู้เขียน | ❌ | — | null ตลอด ⇒ ไม่จำกัดเสมอ |
| 16 | สินค้าบนเว็บต่อเว็บ | ⚠️ ด่านต่อสายแล้ว `CmsCommerceService.cs:70` · `:338` | ❌ `Site.MaxProducts` ไม่มีผู้เขียน | ❌ | — | เหมือน #15 |
| 17 | พื้นที่สื่อต่อเว็บ | ❌ `CmsQuotaService.CanUploadMediaAsync:63` **ไม่มีผู้เรียก** · `UpdateStorageUsageAsync` ไม่มีผู้เรียก (ตัวนับจริงเขียนตรงใน `CmsContentService.cs:604/695`) | ❌ | ❌ | — | dead helper 2 ตัว (ใช้เพดานรวมบริษัท #7 แทน) |
| 18 | แบนด์วิดท์เว็บ | ❌ `Site.CurrentBandwidthUsed` ไม่มีผู้เพิ่ม · `MaxBandwidthBytesPerMonth` ไม่มีผู้ตั้ง | ❌ | ❌ | — | แสดงผลอย่างเดียว (`CmsQuotaService.GetQuotaStatusAsync`) — ค่า 0 ตลอด |
| 19 | โดเมนของตัวเอง | ❌ `CmsSiteService.cs:756` | ❌ | ❌ | — | — |
| 20 | การจอง / ออเดอร์เว็บ | ❌ จำนวน (บิต `CmsBooking`/`CmsEcommerce`) | ⚠️ บิต | ❌ | — | ไม่ควรจำกัด (เป็นรายได้ของร้าน) |
| 21 | **ที่พัก (property)** | ✅ แห่งแรกฟรี แห่งที่ 2+ ต้องมี `lodging.multi-property` (`Helpers/LodgingPropertyQuota.cs` · เรียกจาก `LodgingService.cs:160` + `LodgingSeeder.cs:34`) | ⚠️ ราคาสวิตช์ | ✅ (สวิตช์) | เหมารายเดือน — แต่เป็น**สวิตช์ ไม่ใช่จำนวน** (เปิดแล้วไม่จำกัด) | รูปแบบด่านที่ดี (ตัวเดียว + RuleCode พาไปหน้าส่วนเสริม) — ควรย้ายเป็น "จำนวนที่พัก" ใน §B |
| 22 | การเข้าพัก (`lodging.stay`) | — มิเตอร์นับอย่างเดียว (ราคา 0) `LodgingService.Lifecycle.cs:48` | ⚠️ | — | — | — |
| 23 | **ส่วนเสริมสวิตช์** (`AddOnCodes.Switchable` 9 ตัว) | ✅ `EntitlementService.CheckAsync` ③ | ✅ ราคา/ทดลอง/แพ็กเกจขั้นต่ำ (`admin-addons.html:206-262`) | ✅ `MeteringController.cs:149` | (ก) จ่ายเดือนแรกล่วงหน้า (`UpfrontPeriod` = เดือนเดียว) แล้วเข้าใบรายเดือน (ข) รวมใบรายเดือน (`AddOnBillingChoice` `AllEnums.cs:647`) | ❌ รายปี/ถึงวันหมดอายุ · ❌ คิดตามวันเดือนแรก (เปิดวันที่ 28 = เต็มเดือน `AddOnBilling.FlatAmount`) · ❌ หยุดคิดเมื่อแพ็กเกจหมด (ข้อ 0.6) |
| 24 | API ภายนอก (`ApiFeature.Kind=ConnectedApi`) | ✅ มิเตอร์ต่อ call (`UsageEvent`) | ✅ ราคา/โควตาฟรีต่อเดือน (`ApiPricingPlan.FreeQuotaPerMonth`) | postpaid | ต่อหน่วย/ขั้นบันได | — |
| 25 | ภารกิจแลกโควตา (ฟรี) | ✅ `QuotaRewardPolicy` · `MeteringController.cs:398` | ✅ 3 ชั้น | — | — | — |
| 26 | ขยายทดลองใช้ | ✅ | ✅ "ต่อครั้งละ (วัน)" (`plans.html:343`) | — | — | `subscription.html:340` ยังพิมพ์ `+7` ตายตัว (ACCOUNT_STRUCTURE §6.1c บอกว่าแก้แล้ว — **doc ผิด/ถดถอย**) |
| 27 | **ตัวแพ็กเกจเอง** | ✅ | ✅ ราคา 4 รอบ (`plans.html:253-265`) | ✅ `SubscriptionController.cs:160/199` → `PlatformPlanPricing` | ราย 1/3/6/12 เดือน · อัปเกรด = รอบใหม่จากวันอนุมัติ ส่วนที่เหลือสละ (`PlatformPlanPricing.PeriodStart`) · ดาวน์เกรดมีผลวันครบรอบ | ❌ proration (ดีไซน์ค้าง PLATFORM_OMISE_AUDIT §7.1) |
| 28 | License (AccountSubscription) | ✅ บริษัท/ผู้ใช้ต่อบริษัท | ✅ `admin/account-subscriptions.html` | ⚠️ ไม่มีเส้นซื้อของตัวเอง — ต่ออายุตามการจ่ายของบริษัทที่แพ็กเกจตรงกัน (`PlatformPlanPricing.MayCascadeToAccountPlan`) | — | ผู้ใช้สับสน "License ของฉัน" vs "แพ็กเกจของบริษัทนี้" (§D) |

**สรุปเชิงตัวเลข:** 28 แถว · บังคับจริงครบ 8 · บังคับบางส่วน 6 · ไม่บังคับ 12 · ซื้อเพิ่มได้ผ่านระบบเงินจริง 4 (เอกสาร · ข้อความ · สวิตช์ · แพ็กเกจ) + 1 ที่ไม่มีเส้นเงิน (OCR) · **ไม่มีรายการใดซื้อแบบ "ถึงวันหมดอายุ License คิดตามวัน" ได้**

---

## B. ดีไซน์ "โควตาเสริม" (Quota Add-on) แบบเดียวทั้งระบบ

### B.1 หลัก (ตัวตั้งตัวเดียว · กฎ F2)

| เรื่อง | OWNER เดียว | เหตุผล |
|---|---|---|
| รายชื่อทรัพยากรที่ขายเป็นจำนวนได้ | `Models/Constants/QuotaResources.cs` (closed list) | แอดมิน**เลือก**จากรายการ ไม่พิมพ์รหัสเอง — วันนี้ `MeteringAdminController.UpsertFeature` (`:163`) สร้างรหัสอะไรก็ได้ ⇒ ขายของที่ไม่มีผู้อ่าน (บทเรียน `lodging.promo`) |
| เพดานที่มีผล = รวม + ซื้อ + override | `Helpers/QuotaLimitPolicy` (pure) | คำนวณที่เดียว ทั้งด่าน · หน้าจอ · ข้อความปฏิเสธ (บทเรียน `CmsQuotaUsage`) |
| ด่าน "เพิ่มได้ไหม" | `IQuotaGate.CheckAsync(companyId, resource, adding)` | ทุกทางเข้าเรียกตัวนี้ (ล็อกด้วย `tools/required_call_site_check.py`) |
| ราคา/ยอดที่ต้องจ่ายต่อโหมด | `Helpers/QuotaAddOnPricing` (pure) | ลอก `PlatformPlanPricing` — เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดงอย่างเดียว |
| เงินเข้า → สิทธิ์ | handler ใหม่ผ่าน `PlatformPaymentService` + `PlatformRevenueSource` | กติกา §6.2b: เงินแพลตฟอร์มต้องอยู่ใต้บริษัทผู้ให้บริการ |
| ใบกำกับ/ใบเสร็จ | `IPlatformBillingDocumentIssuer` เดิม | ไม่มีเส้นเอกสารใหม่ (§86/4 · gap-free · เข้ารายงานภาษีขาย) |

### B.2 ทรัพยากร (รุ่นแรก)

สองชนิด — ต่างกันที่ "หมดแล้วเกิดอะไร":

| ชนิด | ความหมาย | ตัวอย่าง `ResourceKey` | หน่วยที่ขาย |
|---|---|---|---|
| **Capacity** (ของที่มีอยู่พร้อมกัน) | นับของที่มีอยู่ตอนนี้ — ลบแล้วคืนช่อง | `cms.sites` · `cms.pages-per-site` · `cms.products-per-site` · `cms.domains` · `users` · `storage` (GB) · `lodging.properties` · `branches` · `pos.terminals` | +1 เว็บ · +1 ที่นั่ง · +10 GB |
| **Consumable** (ใช้แล้วหมดต่อเดือน) | ตัวนับรายเดือน | `documents.month` · `ocr.pages.month` · `notify.messages.month` | +100 ฉบับ · +500 หน้า |

**ไม่อยู่ในรายการ (ตัดสินแล้ว):** การเรียก AI (#12 — กฎเหล็ก #1) · การจอง/ออเดอร์ (#20 — รายได้ของร้าน ห้ามตั้งเพดาน) · e-Tax (#13 — นับในเอกสาร) · รายการบัญชี (#6 — แนะนำ**เลิกโชว์เพดาน**จนกว่าจะตัดสินว่าจะบังคับ)

### B.3 ข้อมูล (ADD COLUMN/TABLE IF NOT EXISTS ผ่าน `DatabaseMigrationHelper`)

```
PlanQuotaAllowance            (ระดับแพลตฟอร์ม · ไม่ใช่ TenantEntity)
  PlanTemplateId, ResourceKey, Included int (-1 = ไม่จำกัด), TrialIncluded int?
  unique (PlanTemplateId, ResourceKey)

CompanyQuotaOverride          (TenantEntity · แอดมินแพลตฟอร์มตั้งรายบริษัท + audit chain)
  CompanyId, ResourceKey, Limit int (-1 = ไม่จำกัด), Reason, SetBy, SetAt

QuotaAddOnOffer               → ใช้ ApiFeature เดิม (Kind ใหม่ = QuotaAddOn) + คอลัมน์เพิ่ม:
  ResourceKey (ต้องอยู่ใน QuotaResources), UnitSize int (1 แพ็ก = กี่หน่วย),
  MaxUnitsPerCompany int?, AllowedModes (flags: OneOffMonth|RecurringMonthly|Coterm),
  MinChargeAmount decimal?      ราคา → ApiPricingPlan เดิม (มีอายุ EffectiveFrom/To — ตั้งราคาใหม่ไม่แก้ราคาเก่า)
                                 + คอลัมน์ AnnualUnitPrice decimal? (null = ใช้ราคาเดือน × 12)

CompanyQuotaAddOn             (TenantEntity — "การซื้อ 1 ครั้ง")
  CompanyId, ResourceKey, OfferCode, Units, Mode {OneOffMonth, RecurringMonthly, Coterm},
  PeriodStartUtc, PeriodEndUtc (null = รายเดือนจนยกเลิก),
  Status {AwaitingPayment, PendingReview, Active, Expired, Cancelled, Rejected},
  CancelAtPeriodEnd bool, RenewWithPlan bool (เฉพาะ Coterm),
  UnitPriceSnapshot, AmountDue, VatIncluded bool (snapshot ของ PlatformPriceIncludesVat),
  PaymentIntentId?, SlipPath?, ReceiptDocumentId?, ReceiptNumber?,
  PolicyVersionAccepted, CreatedBy
```

ทำไมใช้ `ApiFeature`/`ApiPricingPlan` เดิมแทนตารางแคตตาล็อกใหม่: แอดมินมีหน้าเดียว (`admin-addons.html`) · ราคามีประวัติอยู่แล้ว · มิเตอร์รายเดือนใช้ `UsageEvent` → `UsageInvoicingJob` เดิมได้ทันที
ทำไม**ไม่**ใช้ `CompanyFeature` เก็บการซื้อ: `CompanyFeature` = สวิตช์ 1 แถวต่อรหัส (unique ต่อบริษัท+รหัส) — การซื้อจำนวนต้องมีหลายแถว หลายงวด หลายโหมด (กฎ E "ห้าม reuse field ผิดความหมาย")

### B.4 เพดานที่มีผล (`QuotaLimitPolicy.Effective`)

```
included   = CompanyQuotaOverride.Limit            ถ้ามี (แอดมินชนะ)
           ?? PlanQuotaAllowance(plan ปัจจุบัน, resource).Included (หรือ TrialIncluded ระหว่างทดลอง)
           ?? LegacyColumn(resource)                (ช่วงย้าย: PlanTemplate.MaxUsers/MaxStorageBytes/… — §B.9)
           ?? Unlimited                            (ยังไม่ตั้ง = ไม่จำกัด ⇒ deploy แล้วไม่มีใครถูกบล็อก)
purchased  = Σ Units × UnitSize ของ CompanyQuotaAddOn ที่ Status=Active และ now ∈ [PeriodStart, PeriodEnd)
effective  = included == Unlimited ? Unlimited : included + purchased
pool       = แพ็กเกจกลุ่ม (License) ⇒ นับรวมทุกบริษัทใต้ License และเพดานของ License (ฐานเดียวกับเอกสาร — DocumentQuotaPolicy.Basis)
```

ด่าน: `CheckAsync` คืน `QuotaDecision(Allowed, Used, Limit, Remaining, BlockReason, RuleCode="QUOTA-FULL:<resource>", BuyAction)` —
ข้อความไทยพร้อมทางไปต่อสองทาง: "ลบ…ที่ไม่ใช้" + "ซื้อโควตาเพิ่ม" (ลิงก์ `/pages/billing.html#buy=<resource>`) · Capacity ตรวจใต้ `pg_advisory_xact_lock` ต่อ (บริษัท, resource) ภายใน transaction เดียวกับ insert
(กันสองคำขอผ่านพร้อมกัน — pattern `QuotaService.PurchaseTopUpAsync:108`)

### B.5 โหมดการซื้อ + สูตร (`QuotaAddOnPricing` · decimal · `MidpointRounding.AwayFromZero`)

ให้ `P` = ราคาต่อหน่วยต่อเดือน (ราคาที่มีผล ณ วันซื้อ) · `Pa` = ราคาต่อหน่วยต่อปี (`AnnualUnitPrice ?? P×12`) · `u` = จำนวนหน่วย · วันนับตามปฏิทินไทย (UTC+7) แบบ `[วันเริ่ม, วันสิ้นสุด)`

| โหมด | ช่วงสิทธิ์ | ยอด | เอกสาร | เหมาะกับ |
|---|---|---|---|---|
| **OneOffMonth** (เดือนนี้เดือนเดียว) | วันนี้ → สิ้นเดือนไทยนี้ (Consumable: หรือ N วันตามค่าตั้งแพ็ก — คงพฤติกรรม `TopUpValidDays`) | `round(u × P, 2)` (ไม่คิดตามวัน — ระบุชัดในหน้าซื้อ) หรือแบบคิดตามวันถ้าแอดมินติ๊ก `ProrateOneOff` | จ่ายก่อน ⇒ ใบกำกับภาษี/ใบเสร็จ (ขายเงินสด) `IssueAddOnReceiptAsync` | แพ็กเอกสาร/OCR/ข้อความ · เว็บชั่วคราว |
| **RecurringMonthly** (ทุกเดือนจนยกเลิก) | วันนี้ → ไม่มีกำหนด · ยกเลิก = สิ้นเดือน | เดือนแรก: `round(u × P × วันที่เหลือในเดือน / วันในเดือน, 2)` · เดือนถัดไป `round(u × P, 2)` | เดือนแรกจ่ายก่อน (ใบเสร็จ) · เดือนถัดไปเป็นบรรทัดใน**ใบแจ้งหนี้ค่าใช้งานรายเดือน** (`AddOnMonthlyBillingJob` → `UsageEvent` → `IssueUsageInvoiceAsync`) | ร้านที่ยังไม่แน่ใจ |
| **Coterm** (ถึงวันหมดอายุแพ็กเกจ/License) | วันนี้ → `Subscription.EndDate` (หรือ `AccountSubscription.EndDate` เมื่อผูก License) | `perUnit = round(R × D / C, 2)` · `amount = round(u × perUnit, 2)` โดย `R` = ราคาต่อหน่วยต่อรอบ (`Pa` ถ้ารอบ 12 เดือน ไม่งั้น `P × เดือนของรอบ`) · `D` = วันที่เหลือ · `C` = จำนวนวันของรอบปัจจุบัน | จ่ายก่อน ⇒ ใบเสร็จ · ต่ออายุ ⇒ บรรทัดในใบแจ้งหนี้ต่ออายุ (§B.6) | ร้านที่จ่ายรายปี — "ให้หมดพร้อมแพ็กเกจ" |

**ทำไมคิดราคาต่อหน่วยก่อนแล้วคูณจำนวน:** บรรทัดในใบกำกับต้องได้ `LineAmount = round(Qty × UnitPrice, 2)` (กฎ §86/4 ใน CLAUDE.md A) — ถ้าคิดยอดรวมก่อนแล้วหารกลับเป็นราคาต่อหน่วย จะได้เศษที่ไม่ลงตัว

**ตัวอย่าง** (เจ้าของตรวจได้): แพ็กเกจรายปี 31/03/2569 → 31/03/2570 (`C` = 365) · ซื้อวันที่ 15/11/2569 (`D` = 136) · เว็บเพิ่มราคา ฿199/เดือน ฿1,990/ปี · ซื้อ 2 เว็บ
⇒ `perUnit = round(1,990 × 136 / 365, 2) = 741.48` · `amount = 1,482.96` · ถ้าไม่ตั้งราคารายปี `R = 2,388` ⇒ `perUnit = round(889.775…) = 889.78` · `amount = 1,779.56`
· ราคาตั้งรวม VAT (`SiteSettings.PlatformPriceIncludesVat` ค่าตั้งต้น true) ⇒ ฐาน/VAT แยกด้วย `PlatformMoneyInAccount.SplitReceived` เดิม

**ด่านของโหมด Coterm:** แพ็กเกจฟรีถาวร (EndDate +100 ปี) / ทดลองใช้ ⇒ ไม่เปิดโหมดนี้ (เหลือ OneOff/Recurring) · `D < 1` ⇒ ปฏิเสธ "ซื้อพร้อมการต่ออายุแทน" ·
ยอด < `MinChargeAmount` ⇒ คิด `MinChargeAmount` (บอกบนหน้าซื้อ) · มีใบแจ้งหนี้ต่ออายุออกแล้ว (`RenewalInvoiceForEndDate == EndDate`) ⇒ ซื้อได้ แต่ระบบถาม "รวมในรอบหน้าด้วยไหม" (ตั้ง `RenewWithPlan` + ออกใบต่ออายุใหม่ผ่านเส้น supersede เดิม)

**กันจ่ายซ้ำ/ราคาเปลี่ยน:** request ส่ง `ExpectedUnitPrice` + `ExpectedAmount` (ลอก `notify.topup` — ไม่ตรง ⇒ 409 + ข้อเสนอใหม่) · header `Idempotency-Key` (`AddOnBilling.TopUpRequestKey` เดิม) · `MaxUnitsPerCompany` ตรวจใต้ล็อก

### B.6 ต่ออายุพร้อมเลือกส่วนเสริม

1. ก่อนออกใบต่ออายุ (`RenewalInvoiceLeadDays` ตั้งต้น 15 วัน — `PlatformBillingSettings`) ระบบส่งอีเมล/LINE "รายการที่จะต่ออายุ" + ลิงก์หน้า **ต่ออายุ** (ศูนย์บิล)
2. หน้า**ต่ออายุ**: แพ็กเกจ (+รอบ) และรายการโควตาเสริม Coterm ทุกตัวพร้อมช่องติ๊ก "ต่อพร้อมกัน" + จำนวน (ลดได้ เพิ่มได้ ≤ Max) · ส่วนเสริมสวิตช์รายเดือนแสดงเป็นข้อมูล ("คิดรายเดือนต่อตามเดิม")
   — บันทึก `CompanyQuotaAddOn.RenewWithPlan`/`RenewUnits`
3. `SaasBillingDocumentService.GenerateRenewalInvoiceAsync` (`:389`) ออก**ใบแจ้งหนี้หลายบรรทัด**: บรรทัดแพ็กเกจ + บรรทัดละโควตาเสริม (`u × R` เต็มรอบ) — เพิ่ม overload
   `IssueRenewalInvoiceAsync(buyer, IReadOnlyList<PlatformInvoiceLine> lines, …)` (ใช้ `PlatformInvoiceLine` ที่มีอยู่ `PlatformBillingDocumentIssuer.cs:87`) · reference เดิม `RenewalReferencePrefix`
4. เปลี่ยนรายการหลังออกใบแล้ว ⇒ เส้นเดิม `RenewalInvoiceSupersede` (ยังไม่จ่าย = ยกเลิกใบเดิมออกใหม่ · จ่ายบางส่วน = งานใบลดหนี้ให้แอดมิน — §6.2c ขั้น 3)
5. ตัดชำระใบต่ออายุ (`SettleInvoiceAsync`) ⇒ ต่อแพ็กเกจ **และ** สร้าง `CompanyQuotaAddOn` แถวใหม่ (Mode=Coterm, Period = รอบใหม่) ต่อทุกบรรทัดที่ติ๊ก — ในธุรกรรมเดียว ใต้ `SubscriptionPaymentLock` เดิม ·
   บรรทัดที่ไม่ได้ติ๊ก ⇒ หมดอายุพร้อมรอบเดิม (ไม่ลบข้อมูล — §B.8)
6. ไม่ติ๊กอะไรเลยและไม่เข้าหน้า ⇒ ค่าตั้งต้น = **ต่อทุกตัวที่ใช้อยู่** (ร้านส่วนใหญ่ไม่อยากให้เว็บหาย) — ข้อความแจ้งเตือนบอกชัด · คำตัดสินเจ้าของ Q3

### B.7 เงิน · เอกสาร · บัญชี (กฎเหล็ก #2)

- `PaymentSourceKind.QuotaAddOnPurchase = 9` (ใหม่) — **ต้องเพิ่มใน `Helpers/PlatformRevenueSource`** (`:26` วันนี้รู้จักแค่ `SubscriptionPayment`/`AddOnPurchase`) ไม่งั้น intent ไปตก gateway ของผู้เช่า (G-1 ซ้ำ) ·
  handler `QuotaAddOnPaymentHandler` กรอง `CompanyId` (บทเรียน G-3) · โหมดทดสอบไม่ให้สิทธิ์ (`PaymentIntentTestMode`)
- สลิป: คิวตรวจเดียวกับ `MeteringAdminController` `addon-payments` (`:31`) — เพิ่มชนิดแถว ไม่ทำหน้าที่สอง
- รับเงินแล้ว ⇒ `IssueAddOnReceiptAsync` (`PlatformBillingDocumentIssuer.cs:569`) คำอธิบายบรรทัด: "เพิ่มโควตาเว็บไซต์ 2 เว็บ · 15/11/2569–30/03/2570 (136/365 วัน)" · reference `QADD-{id}` (idempotent) ·
  ล้ม ⇒ audit `RD-86/4-ADDON-RECEIPT` + ธงในหน้าแอดมิน (เหมือนรอบ 227)
- รายเดือน ⇒ `UsageEvent` (`IdempotencyKey = AddOnBilling.FlatKey(offer, period)` + id ของการซื้อ) ⇒ ใบแจ้งหนี้ค่าใช้งานเดิม
- คืนเงิน: นโยบายเดิม "ค่าบริการไม่คืนเงิน" (§6.2c) · ลดจำนวนมีผลรอบหน้า · คืนเงินโดยแอดมิน ⇒ เส้นใบลดหนี้รอบ 228
- audit chain ทุกการซื้อ/ยกเลิก/override (`AddChainedAuditLog`) · หลักฐานยอมรับนโยบาย `PolicyAcceptances` (Scope=platform)

### B.8 ดาวน์เกรด · หมดอายุ · ยกเลิก — "บล็อกการสร้าง ไม่ลบข้อมูล"

| เหตุการณ์ | ผล |
|---|---|
| ใช้อยู่ > เพดานใหม่ (ดาวน์เกรด · โควตาเสริมหมดอายุ · แอดมินลด override) | **ของเดิมใช้ต่อได้ทั้งหมด** (เว็บยังออนไลน์ · ผู้ใช้ยังล็อกอินได้ · ไฟล์ไม่ถูกลบ) · สร้าง/เชิญ/อัปโหลดเพิ่ม **ไม่ได้** จนกว่าจะลดลงต่ำกว่าเพดานหรือซื้อเพิ่ม · แถบเตือนบนหน้าที่เกี่ยวข้อง + อีเมลครั้งเดียว |
| เว็บไซต์เกินโควตา | ไม่ปิดเว็บสาธารณะ (ลูกค้าของร้านไม่ควรรับผล) · ห้าม**เปิดเผยแพร่เว็บใหม่**ที่ยังเป็นร่าง · แก้ไขเว็บเดิมได้ (คำตัดสินเจ้าของ Q2: จะให้แก้ได้ทุกเว็บ หรือเฉพาะ N เว็บที่เลือก) |
| แพ็กเกจหมดอายุ/ถูกระงับ | โควตาเสริม Coterm หมดพร้อมรอบ · รายเดือน**หยุดออกค่าเดือน** (แก้ `AddOnMonthlyBillingJob` ด้วย — ข้อ 0.6) · ต่ออายุแล้ว ⇒ กลับมาตามที่ติ๊ก |
| ยกเลิกโควตาเสริมรายเดือน | `CancelAtPeriodEnd` — ใช้ได้ถึงสิ้นเดือน ไม่คืนเงิน |
| สลิปถูกปฏิเสธ | สิทธิ์ที่เปิดชั่วคราวระหว่างรอตรวจถูกถอน (เหมือน `AddOnPurchaseService.RejectAsync`) — ของที่สร้างไปแล้วไม่ถูกลบ เข้ากติกา "เกินเพดาน" ข้างบน |

### B.9 ย้ายของเดิม (persist แล้วต้องมี migration — F2 ข้อ 9)

1. `PlanQuotaAllowance` backfill จาก `PlanTemplate.MaxUsers/MaxStorageBytes/MaxDocumentsPerMonth/MaxOcrPagesPerMonth/MaxSharedMessagesPerMonth` (+Trial*) — คอลัมน์เดิม**ยังเป็นผู้อ่านสำรอง**จนแพ็กสุดท้าย แล้วค่อยเลิกเขียน (ไม่ DROP)
2. override รายบริษัทเดิม (`Subscription.MaxUsers` ที่แอดมินแก้ — `AdminController.cs:1730`) ⇒ `CompanyQuotaOverride` เฉพาะแถวที่ต่างจากแม่แบบ
3. `cms.*` ไม่มีค่าเดิม ⇒ ไม่ backfill = ไม่จำกัด (ไม่มีใครถูกบล็อกหลัง deploy) · `Site.MaxPages/MaxProducts` คงเป็น **override รายเว็บ** (null = ตามแพ็กเกจ)
4. แพ็กเอกสาร/ข้อความที่ซื้อแล้ว (`Subscription.DocumentBonusQuota`/`NotifyBonusQuota`) คงไว้ — รุ่นแรก**ไม่ย้าย** Consumable เข้าตารางใหม่ (ใช้ได้อยู่ · ย้ายเมื่อทำแพ็ก 5)
5. `OcrCreditPurchase` ที่ Pending ค้าง ⇒ รายงานให้แอดมิน (ไม่อนุมัติ/ยกเลิกอัตโนมัติ)

---

## C. โควตาเว็บไซต์ CMS — รายละเอียด

| เรื่อง | ข้อเสนอ |
|---|---|
| นับอะไร | `Sites` ของบริษัทที่ `!IsDeleted` (global filter มีอยู่แล้ว) **ทุกสถานะ** รวม Draft/Maintenance/Suspended — ไม่นับ Draft = สร้างร่างได้ไม่จำกัดแล้วค่อยเปิดทีละเว็บ |
| pool | ผูก License ⇒ รวมทุกบริษัทใต้ License (ฐานเดียวกับเอกสาร) · ไม่ผูก ⇒ ต่อบริษัท (คำตัดสินเจ้าของ Q1) |
| จุดเรียกด่าน | `CmsSiteService.CreateSiteAsync` ก่อน `new Site` (`:78`) ใต้ล็อก `(companyId, "cms.sites")` ในธุรกรรมเดียวกับ `SaveChangesAsync` แรก (`:137`) · grep วันนี้: ทางสร้าง `Site` มีที่เดียว (ไม่มี clone/import) — ล็อกด้วย `required_call_site_check` ให้ทางใหม่ในอนาคตต้องผ่าน |
| ต่อเว็บ | `CmsQuotaService.PageUsageAsync/ProductUsageAsync` เปลี่ยนเพดานเป็น `site.MaxPages ?? allowance("cms.pages-per-site")` — ด่านเดิมต่อสายแล้ว ได้ผลทันที · ลบ dead `CanUploadMediaAsync`/`UpdateStorageUsageAsync` หรือต่อสาย (เลือกอย่างตั้งใจ) · แบนด์วิดท์: **ไม่ขาย** จนกว่าจะมีตัวนับ (ซ่อนจากหน้าสถานะโควตา) |
| ข้อความปฏิเสธ | "สร้างเว็บไซต์เพิ่มไม่ได้ — แพ็กเกจของคุณมี 3 เว็บ ใช้ไป 3 · ลบเว็บที่ไม่ใช้ หรือซื้อเว็บเพิ่ม (เว็บละ ฿199/เดือน)" + `RuleCode = QUOTA-FULL:cms.sites` ให้หน้าเว็บเปิดหน้าต่างซื้อ |
| แอดมิน | `admin/plans.html` กลุ่มใหม่ "เว็บไซต์ (CMS)": จำนวนเว็บ · หน้า/เว็บ · สินค้า/เว็บ · โดเมนของตัวเอง (−1 = ไม่จำกัด) · `admin-addons.html`: ข้อเสนอ `quota.cms.sites` ราคา/เดือน · ราคา/ปี · ซื้อได้สูงสุด · โหมดที่เปิด |
| หน้า `cms-sites.html` | แถบ "ใช้ไป 2/3 เว็บ" ข้างปุ่ม "สร้างเว็บไซต์" (`:135`) · เต็ม ⇒ ปุ่มกลายเป็น "เพิ่มโควตาเว็บไซต์" เปิดหน้าต่างซื้อ (ไม่ต้องกรอกฟอร์มสร้างเว็บแล้วค่อยเจอ error) · ตัวเลขมาจาก API (ไม่นับใน JS) |
| เทสต์ (ทั้งสองทิศ) | เต็มแล้วสร้างไม่ได้ · ลบแล้วสร้างได้ · ซื้อแล้วสร้างได้ทันที · โควตาหมดอายุแล้ว**เว็บเดิมยังเปิดได้/แก้ได้** · ไม่ตั้งค่า = ไม่จำกัด · สองคำขอพร้อมกันได้แค่หนึ่ง (Db test) · License pool รวมข้ามบริษัท · บริษัทอื่นไม่นับ (tenant) |

---

## D. UX/UI — มุมมองเจ้าของร้านที่ไม่ใช่สายเทคนิค

### D.1 ฝั่งผู้เช่า

| # | หน้า | ความรุนแรง | ที่เห็น | แก้ |
|---|---|---|---|---|
| D-1 | `pages/addons.html:191,208-210` | **สูง (เงิน)** | ปุ่ม "ซื้อโควตาเพิ่ม 100 ฉบับ" → `confirm()` บอกแค่ "ค่าใช้จ่ายจะเข้าบิลรอบถัดไป" **ไม่บอกราคา** · ไม่ส่ง `Idempotency-Key` · ไม่ส่งข้อเสนอที่เห็น | ใช้คอมโพเนนต์เดียวกับแพ็กข้อความ (`js/customer-notify.js:155-180`): ปุ่มมีราคา · ส่ง expected · 409 วาดข้อเสนอใหม่ |
| D-2 | `js/layout.js:1761-1777` | **สูง** | เรื่องเดียวแตก 5 เมนู: "License ของฉัน" · "แพ็กเกจของบริษัทนี้" · "ศูนย์บิลค่าบริการ" · "ส่วนเสริมของฉัน" · "สถานะการใช้งาน" — เจ้าของร้านไม่รู้ว่า License ต่างจากแพ็กเกจยังไง ต้องเปิด 3 หน้าเพื่อตอบ "ฉันจ่ายเท่าไร ใช้ไปเท่าไร ซื้อเพิ่มที่ไหน" | เมนูเดียว **"แพ็กเกจและการชำระเงิน"** 4 แท็บ (§D.3) · License ซ่อนเป็นแท็บย่อยเฉพาะคนที่มีหลายบริษัท |
| D-3 | `pages/subscription.html:287` | **สูง (ข้อมูลผิด)** | การ์ด "การใช้งานเดือนนี้" ผู้ใช้งาน = `used: 1` ตายตัว | ใช้ตัวนับจากเซิร์ฟเวอร์ (`GetUsageCurrentAsync`) |
| D-4 | `subscription.html:284-295` · `usage.html:224,246` · `addons.html:171` · `team.html:168` | **สูง** | ตัวเลขโควตาโชว์ 3 หน้า ชุดไม่เท่ากัน และเกณฑ์ "ไม่จำกัด" 3 แบบ (`≥99999` · `≥999` · `≤0`) + `team.html` โหลดพลาดตกเป็น 999 — ขัด §6.1c ("เกณฑ์อยู่ที่ `PlanDisplay` ที่เดียว") | endpoint เดียว `GET …/quota-summary` คืนรายการ `{resource, label, used, limit, unlimited, warnLevel, buyAction}` · ทุกหน้าใช้ renderer เดียว · โหลดพลาด = "โหลดไม่ได้" ไม่ใช่ "ไม่จำกัด" |
| D-5 | `subscription.html:286` | กลาง | โชว์เพดาน "รายการบัญชี" ที่ไม่ถูกบังคับ (#6) — ผู้ใช้กลัวบันทึกบัญชีไม่ได้ | ซ่อนจนกว่าจะบังคับ (หรือป้าย "ไม่จำกัดในทางปฏิบัติ") |
| D-6 | `subscription.html:340` | กลาง | ปุ่ม "ขยายเวลา (+7 วัน)" ตายตัว ขณะที่แอดมินตั้ง "ต่อครั้งละ (วัน)" ได้ (`admin/plans.html:343`) — §6.1c บอกว่าแก้แล้ว (ถดถอย/doc ผิด) | อ่านจาก `TrialExtensionDays` ของแม่แบบ · แก้ §6.1c |
| D-7 | `subscription.html:263-264,349` · `usage.html:212` | กลาง | ป้ายสถานะ/รอบบิลแปลในหน้าเว็บ (สำเนากติกา — F2 ข้อ 5) | เซิร์ฟเวอร์ส่ง `statusLabel`/`cycleLabel` (ศูนย์บิลทำแล้ว) |
| D-8 | `subscription.html:494` | กลาง (ความเชื่อใจ) | อัปเกรดกลางรอบ "เริ่มรอบใหม่จากวันที่ยืนยัน" ตัวหนังสือเทาเล็ก — เจ้าของร้านรู้ทีหลังว่าวันที่เหลือหายไป | บอกเป็นตัวเลข: "วันที่เหลือของแพ็กเกจเดิม 120 วัน (≈ ฿1,600) จะไม่ถูกนำมาหัก" หรือทำ proration (`PlatformProration` ดีไซน์ค้าง) |
| D-9 | `pages/billing.html:96-108` | กลาง | ศูนย์บิลไม่มีปุ่ม "ต่ออายุ/เปลี่ยนแพ็กเกจ/ซื้อเพิ่ม" และไม่มีโควตา · คำอธิบายเมนู "ชำระด้วยการโอน+สลิป" (`layout.js:1773`) ล้าสมัย (มีชำระออนไลน์แล้ว) | เป็นแท็บ "ภาพรวม" ของ hub (§D.3) · แก้คำอธิบายเมนู |
| D-10 | `addons.html:335-336` | กลาง | หน้าเว็บกรองมิเตอร์ระบบด้วยรายชื่อที่พิมพ์ซ้ำจาก `AddOnCodes.SystemMeters` | เซิร์ฟเวอร์ไม่ส่งแถว `Kind=SystemMeter` มาในรายการสวิตช์ |
| D-11 | `addons.html:191-192` | กลาง | "100 ฉบับ · ใช้ได้ 60 วัน" พิมพ์ในหน้า | มาจากข้อเสนอของเซิร์ฟเวอร์ (packSize/packValidDays เหมือนแพ็กข้อความ) |
| D-12 | `OcrController.cs:199` | กลาง (ทางตัน) | OCR หมด ⇒ "กรุณาซื้อเครดิตเพิ่ม" แต่ไม่มีหน้าซื้อ | ปุ่ม "ซื้อหน้า OCR เพิ่ม" → หน้าต่างซื้อ `ocr.pages.month` (แพ็ก 5) · ระหว่างรอ: บอก "ติดต่อผู้ดูแลระบบ" ตามจริง |
| D-13 | `CmsQuotaService.CmsQuotaUsage.BlockReason` | กลาง | เต็มแล้วบอกแค่ "อัปเกรดแพ็กเกจ" — ไม่มีทางซื้อเพิ่ม · `cms-sites.html` ไม่บอกจำนวนที่เหลือ | §C |
| D-14 | `addons.html` การ์ดสวิตช์ | กลาง | กดเปิด → การ์ดขึ้น "เปิดอยู่" พร้อม "ยอดที่ต้องชำระ" — สถานะ "เปิดแต่ยังไม่จ่าย" งง (ผู้ใช้ไม่รู้ว่าต้องทำอะไรต่อ/มีเวลากี่วัน) | ขั้นตอนซื้อแบบ checkout: สรุป → จ่าย → เปิดใช้ (ผ่อนผัน 7 วันเฉพาะเลือกรวมใบรายเดือน และบอกวันสิ้นสุดเป็นวันที่) |
| D-15 | สวิตช์รายเดือน | กลาง (เงิน) | เปิดวันที่ 28 = คิดเต็มเดือน (`AddOnBilling.FlatAmount`) ไม่มีที่บอก | คิดตามวันเดือนแรก (§B.5 RecurringMonthly) หรือบอกชัดในหน้าซื้อ |
| D-16 | ทุกหน้า | **สูง (เงิน)** | แพ็กเกจยกเลิก/หมดอายุแล้ว ใบรายเดือนยังมีค่าส่วนเสริม (ข้อ 0.6) | แพ็ก 0 |
| D-17 | `register.html:22` | ต่ำ | "ทดลอง Pro 14 วันก็ได้" พิมพ์ตายตัว | อ่านจากแม่แบบ (การ์ดด้านล่างทำแล้ว `:609`) |
| D-18 | `team.html:168` | ต่ำ | เชิญได้/ไม่ได้ไม่บอกเหตุผลล่วงหน้า (ไปเจอ error ตอนกดเชิญ) | แถบ "ที่นั่ง 3/3 — ซื้อที่นั่งเพิ่ม" เหนือปุ่มเชิญ |

### D.2 ฝั่งแอดมินแพลตฟอร์ม

| # | หน้า | ความรุนแรง | ที่เห็น | แก้ |
|---|---|---|---|---|
| A-1 | `pages/admin-addons.html` + `MeteringAdminController.UpsertFeature:163` | สูง | ฟอร์มสร้างส่วนเสริมรับรหัสอะไรก็ได้ — สร้าง `cms.sites` แล้ว**ไม่มีผลอะไร** (ไม่มีผู้อ่าน) | รหัสต้องเลือกจากรายการในโค้ด (`AddOnCodes` + `QuotaResources`) · รหัสนอกรายการ = 400 |
| A-2 | `admin-addons.html:247-262` | กลาง | ราคามีแค่ต่อเดือน/ต่อหน่วย · ไม่มีราคาต่อปี · ไม่มีเพดานซื้อ · ไม่มีโหมดที่เปิด · ขนาดแพ็กตั้งได้เฉพาะ `notify.topup` (`:226`) | ฟิลด์ใหม่ตาม §B.3 · ขนาด/อายุแพ็กเปิดให้ทุก Consumable |
| A-3 | `admin-addons.html` | กลาง (เงิน) | ของที่เปิดขายแต่ยังไม่ตั้งราคา ⇒ ขายฟรีเงียบ ๆ (G-16) — ไม่มีป้ายเตือน | แถวแดง "ยังไม่ตั้งราคา — ลูกค้าได้ฟรี" + นับบนการ์ดสรุป |
| A-4 | `admin/plans.html:273-296` | กลาง | ลิมิตกระจายเป็นช่องเฉพาะ · ไม่มีกลุ่ม CMS/สาขา/โดเมน · "บริษัทสูงสุด" ตั้งได้แต่ไม่มีผล (#2) | ตาราง allowance เดียวจาก `QuotaResources` (แถว = ทรัพยากร · คอลัมน์ = ปกติ/ทดลอง/ไม่จำกัด) · ช่องที่ไม่มีผลติดป้าย "ยังไม่บังคับ" |
| A-5 | OCR credit (`AdminController` review · `admin/ocr-config.html`) | สูง (เงิน/ภาษี) | อนุมัติเครดิตโดยไม่มีหลักฐานเงินและไม่มีใบกำกับ | ย้ายเข้าโควตาเสริม (แพ็ก 5) · ระหว่างนี้ ซ่อนปุ่มอนุมัติหรือบังคับแนบหลักฐาน |
| A-6 | `admin/platform-billing.html` | ต่ำ | ไม่มีมุมมอง "โควตาเสริมที่ขายได้/ใกล้หมดอายุ/ต่ออายุรอบหน้า" | เพิ่มในรายงาน MRR เดิม (`revenue-report` · `PlatformRevenueMetrics`) |

### D.3 Flow ที่เสนอ (ง่ายพอสำหรับเจ้าของร้าน)

```
เมนู: 💎 แพ็กเกจและการชำระเงิน  (/pages/billing.html — hub เดียว)
 ├─ แท็บ "ภาพรวม"      แพ็กเกจ · ใช้ได้ถึง · ยอดค้าง · [ต่ออายุ] [เปลี่ยนแพ็กเกจ]
 ├─ แท็บ "ใช้ไปเท่าไร"  หลอดทุกทรัพยากร (จาก quota-summary) · ทุกหลอดมีปุ่ม [ซื้อเพิ่ม]
 ├─ แท็บ "ซื้อเพิ่ม"     โควตาเสริม + ส่วนเสริมสวิตช์ (การ์ดเดียวกัน)
 └─ แท็บ "เอกสาร"      ใบแจ้งหนี้ · ใบเสร็จ/ใบกำกับ · ประวัติการชำระ · 50 ทวิ

[ซื้อเพิ่ม] → หน้าต่างเดียว 3 ขั้น:
 1) จำนวน  [−] 1 [+]  (สูงสุด N)
 2) ใช้แบบไหน (ปุ่มวิทยุ — เซิร์ฟเวอร์คิดราคาให้ทุกตัวเลือก)
    ○ เดือนนี้เดือนเดียว            ฿199
    ○ ทุกเดือน (ยกเลิกได้)           ฿103 เดือนนี้ · จากนั้น ฿199/เดือน
    ● ถึงวันหมดอายุแพ็กเกจ 31/03/2570 ฿741.48 (คิด 136 จาก 365 วัน) · ต่ออายุพร้อมแพ็กเกจได้
 3) สรุป (ก่อน VAT · VAT · รวม) + ติ๊กนโยบาย → [พร้อมเพย์] [บัตร] [โอน+แนบสลิป]
→ จ่ายสำเร็จ: ใช้ได้ทันที + ลิงก์ใบกำกับภาษี/ใบเสร็จ (PDF) + หลอดอัปเดต

[ต่ออายุ] → รายการ: แพ็กเกจ ✔ · เว็บเพิ่ม 2 เว็บ ✔ (แก้จำนวนได้) · ที่นั่งเพิ่ม 1 ☐ → ยอดรวม → จ่าย/รอใบแจ้งหนี้
```

หลักที่ใช้: ราคาทุกตัวเลขมาจากเซิร์ฟเวอร์ · ไม่มีขั้น "เปิดแล้วค่อยจ่าย" สำหรับการจ่ายก่อน · ทุกข้อความปฏิเสธมีปุ่มพาไปซื้อ · ไม่มีหน้าไหนต้องรู้ว่า License คืออะไรถ้ามีบริษัทเดียว

---

## E. แผนลงมือ (แพ็ก · ลำดับ · ไฟล์ · เทสต์ · ขนาด)

> ทุกแพ็ก: `bash tools/check_all.sh` · CI บน `claude/**` · ฝ่ายค้าน 1 รอบ (แตะเงิน/ภาษี) · ACCOUNT_STRUCTURE §6.1b + DOCUMENT_FLOW (ใบเสร็จ/ใบแจ้งหนี้ค่าบริการ) + TEST_PLAN + CHANGELOG ในคอมมิตเดียวกัน

| แพ็ก | เนื้องาน | ไฟล์หลัก | เทสต์ | ขนาด |
|---|---|---|---|---|
| **0 — บั๊กที่พบ (ทำก่อน แยกได้)** | (ก) `AddOnMonthlyBillingJob` ข้ามบริษัทที่แพ็กเกจไม่ active/ยกเลิกมีผลแล้ว (ตัดสินด้วยตัวเดียวกับ `EntitlementService` ①) (ข) ซื้อแพ็กเอกสาร: โชว์ราคา + expected + Idempotency-Key (ลอก notify) (ค) `subscription.html:287` ผู้ใช้จริง · `:340` วันขยายจากแม่แบบ · ซ่อนเพดานรายการบัญชี (ง) ข้อความ OCR 429 ไม่ชี้ทางที่ไม่มี (จ) `team.html` โหลดพลาด ≠ ไม่จำกัด | `Services/Background/AddOnMonthlyBillingJob.cs` · `Helpers/AddOnBilling.cs` (เพิ่มพารามิเตอร์สถานะแพ็กเกจให้ `FlatAmount` หรือ helper ใหม่) · `Controllers/MeteringController.cs:332` · `QuotaService.cs:94` · หน้า 3 หน้า | `AddOnBillingTests` เพิ่ม: แพ็กเกจหมด ⇒ ฿0 "plan-inactive" · แพ็กเกจปกติ ⇒ คิดตามเดิม (ทิศตรงข้าม) · topup ราคาเปลี่ยน ⇒ 409 | 1 วัน |
| **1 — แกนกลาง** | `QuotaResources` · ตาราง §B.3 · migration/backfill §B.9 · `Helpers/QuotaLimitPolicy` (pure) · `IQuotaGate` + ล็อก · endpoint `GET …/quota-summary` | `Models/Constants/QuotaResources.cs` · `Models/Entities/Quota.cs` · `Data/DatabaseMigrationHelper.cs` · `Helpers/QuotaLimitPolicy.cs` · `Services/Implementations/QuotaGateService.cs` · `Program.cs` | `QuotaLimitPolicyTests` (override ชนะ · ไม่ตั้ง=ไม่จำกัด · หมดอายุไม่นับ · pool License) · Db test ล็อกพร้อมกัน · tenant isolation | 2.5 วัน |
| **2 — โควตาเว็บไซต์ CMS** | §C ทั้งหมด · `plans.html` กลุ่ม CMS · `cms-sites.html` แถบ + ปุ่มซื้อ · ต่อเว็บอ่าน allowance · จัดการ dead helper | `CmsSiteService.cs` · `CmsQuotaService.cs` · `ICmsQuotaService.cs` · `admin/plans.html` · `pages/cms-sites.html` · `tools/required_call_site_check.py` | เทสต์ 8 ข้อใน §C (รวมทิศตรงข้าม: เว็บเดิมไม่ถูกแตะ) | 1.5 วัน |
| **3 — ซื้อโควตาเสริม (เงิน)** | `QuotaAddOnPricing` 3 โหมด · `PaymentSourceKind.QuotaAddOnPurchase` + `PlatformRevenueSource` + handler · คิวสลิป · ใบเสร็จ `IssueAddOnReceiptAsync` · รายเดือนเข้า `UsageEvent` · admin-addons ฟิลด์ใหม่ + รหัสปิด (A-1/A-2/A-3) | `Helpers/QuotaAddOnPricing.cs` · `Models/Enums/AllEnums.cs` · `Helpers/PlatformRevenueSource.cs` · `Services/Payments/Handlers/QuotaAddOnPaymentHandler.cs` · `Services/Payments/QuotaAddOnPurchaseService.cs` · `Controllers/MeteringController.cs` · `MeteringAdminController.cs` · `pages/admin-addons.html` | `QuotaAddOnPricingTests` (ตัวอย่าง §B.5 ตรงเป๊ะ · ปัด AwayFromZero · `LineAmount = round(qty×unit)` · D<1 ปฏิเสธ · ฟรีถาวรไม่มี Coterm) · handler ไม่ให้สิทธิ์โหมดทดสอบ/ข้ามบริษัท · ใบเสร็จ idempotent | 3 วัน |
| **4 — ต่ออายุพร้อมส่วนเสริม** | §B.6 · overload ใบแจ้งหนี้หลายบรรทัด · ตัดชำระแล้วต่อโควตาเสริม · supersede เมื่อเปลี่ยนรายการ · หน้า "ต่ออายุ" | `SaasBillingDocumentService.cs:389` · `PlatformBillingDocumentIssuer.cs:207` · `SubscriptionService.cs` (เส้นอนุมัติ/ตัดชำระ) · `pages/billing.html` | ใบต่ออายุมีบรรทัดตามที่ติ๊ก · ไม่ติ๊ก ⇒ หมดพร้อมรอบ (ข้อมูลไม่หาย) · จ่ายแล้วต่อครบในธุรกรรมเดียว · เปลี่ยนหลังออกใบ ⇒ supersede | 2 วัน |
| **5 — ย้ายทรัพยากรอื่นเข้าด่านเดียว** | ที่นั่ง (`CompanyService.AddUserAsync`) · พื้นที่ · ที่พัก (`LodgingPropertyQuota` → จำนวน) · OCR เครดิต → `ocr.pages.month` (เลิกเส้น `OcrCreditPurchase` ที่ไม่มีเงิน) · แพ็กเอกสาร/ข้อความใช้หน้าต่างซื้อเดียวกัน | `CompanyService.cs` · `SubscriptionService.CanFitStorageAsync` · `Helpers/LodgingPropertyQuota.cs` · `OcrQuotaService.cs` · `OcrController.cs` | ด่านเดิมทุกตัวยังให้ผลเดิมเมื่อไม่ได้ซื้อ (regression) · ซื้อแล้วผ่าน | 2.5 วัน |
| **6 — UX รวมศูนย์** | hub 4 แท็บ (§D.3) · เมนูเหลือ 1 (+License เฉพาะคนหลายบริษัท) · renderer โควตาเดียว · ป้ายจากเซิร์ฟเวอร์ (D-4/D-7/D-9/D-10/D-11) · ป้ายเตือน "ยังไม่ตั้งราคา" | `pages/billing.html` · `js/layout.js` · `pages/subscription.html` · `pages/usage.html` · `pages/addons.html` · `js/plan-display.js` | `node --check` · ตรวจ checker สำเนาป้าย | 2.5 วัน |

**รวม ≈ 15 วันทำงาน** (แพ็ก 0 ทำได้ทันทีแยกจากที่เหลือ · แพ็ก 1→2 ตอบคำขอข้อ (1) ส่วน "แอดมินตั้งได้" ภายใน ~4 วัน · ส่วน "ซื้อเพิ่มได้" ต้องมีแพ็ก 3)

### คำถามที่ต้องให้เจ้าของตัดสินก่อนเริ่ม

1. **Q1 pool** — โควตาเว็บ/ที่นั่งนับ "ต่อบริษัท" หรือ "รวมทั้ง License" เมื่อบริษัทผูก License? (แนะนำ: รวม — ตรงกับเอกสาร/พื้นที่วันนี้)
2. **Q2 เกินเพดาน** — หลังดาวน์เกรด เว็บที่เกินยังแก้ไขได้ทุกเว็บ หรือให้เลือก N เว็บที่แก้ได้ (ที่เหลืออ่านอย่างเดียว · **ไม่ปิดเว็บสาธารณะในทุกกรณี**)?
3. **Q3 ค่าตั้งต้นตอนต่ออายุ** — ไม่เข้าไปเลือก = ต่อโควตาเสริมทุกตัว (แนะนำ) หรือไม่ต่อ?
4. **Q4 Coterm** — ใช้ราคารายปีที่ตั้งแยก (ถูกกว่า) หรือ ราคาเดือน × 12 · และยอดขั้นต่ำต่อการซื้อเท่าไร?
5. **Q5 RecurringMonthly เดือนแรก** — คิดตามวัน (แนะนำ · แก้ D-15 ไปด้วยสำหรับสวิตช์) หรือเต็มเดือน?
6. **Q6 OCR เครดิตเดิม** — เลิกเส้น `OcrCreditPurchase` (ไม่มีเงิน/ใบกำกับ) ทันทีที่มีแพ็ก 5 ใช่ไหม · รายการ Pending ค้างจะจัดการอย่างไร?
7. **Q7 รายการบัญชี/เดือน** — บังคับจริง หรือเลิกโชว์เพดาน?

### คำตัดสิน (ผู้ประสานงานภายใต้อำนาจเจ้าของ · 2026-11-02 · บันทึกใน `erp-review/2026-09-29/DECISIONS.md` ข้อ 229-1..229-7)

| # | คำตัดสิน |
|---|---|
| Q1 (229-1) | นับ**ต่อบริษัท** — ไม่รวมข้าม License (ช่องเดิมที่ pool อยู่แล้ว — เอกสาร/พื้นที่ของ License — คงเดิม) |
| Q2 (229-2) | เกินเพดาน ⇒ ของเดิม**ออนไลน์และแก้ไขได้ทั้งหมด** · บล็อกเฉพาะการสร้างใหม่ · ไม่ลบ/ไม่ปิดเผยแพร่เด็ดขาด |
| Q3 (229-3) | ต่ออายุ: ส่วนเสริมที่ใช้อยู่**ต่อเป็นค่าตั้งต้น** · หน้าต่ออายุแสดงรายการพร้อมราคาให้เอาออกได้ |
| Q4 (229-4) | ถึงวันหมดอายุ: ราคาปีของแอดมิน (ไม่ตั้ง = เดือน × 12) · `perUnit = round(R×D/C,2)` · `amount = round(units×perUnit,2)` · ขั้นต่ำ = หนึ่งเดือน (แอดมินปิดได้ · ค่าตั้งต้นเปิด) |
| Q5 (229-5) | รายเดือนต่อเนื่อง: เดือนแรกคิดตามวันที่เหลือของเดือน |
| Q6 (229-6) | เลิกเส้นขอเครดิต OCR แบบไม่มีเงิน เมื่อแพ็ก 5 ลง · รายการ Pending เดิมแอดมินยังอนุมัติด้วยมือได้ · คำขอใหม่ตอบข้อความพาไปหน้าซื้อ |
| Q7 (229-7) | รายการบัญชี/เดือน: เลิกแสดงต่อผู้เช่า · แอดมินติดป้าย "ไม่บังคับใช้ (เลิกใช้)" · ไม่บังคับ |

### สถานะการลงมือ (รอบ 229 · working tree — ผู้ประสานงานคอมมิต · **ยังไม่ได้คอมไพล์ในเครื่องนี้** CI เป็น compiler ตัวแรก)

| แพ็ก | ทำแล้ว | ยังค้าง / เบี่ยงจากดีไซน์ (ตั้งใจ) |
|---|---|---|
| 0 | (ก) `AddOnBilling.FlatAmount(planActive)` + `RecordFlatMonthlyAsync(planActive)` + งานรายเดือนถาม `GetEffectivePlanAsync().IsActive` (ตัวเดียวกับ EntitlementService ①) — ไม่ active ⇒ ไม่บันทึก/ไม่ประทับงวด · (ข) แพ็กเอกสาร: ราคา/ขนาด/อายุจากเซิร์ฟเวอร์ (`DocumentQuotaStatus.TopUp*`) · expected ⇒ 409 · Idempotency-Key · (ค) ผู้ใช้จริงจากตัวสรุปโควตา · `+7` ⇒ `extensionDays` · ซ่อนเพดานรายการบัญชี (subscription/usage/License) · (ง) ข้อความ OCR 429 ชี้หน้าซื้อ + ruleCode · (จ) team.html โหลดพลาด = "ดูไม่ได้" · A-1 รหัสส่วนเสริมใหม่ต้องอยู่ใน `AddOnCodes.IsKnown` · ACCOUNT_STRUCTURE §6.1c แก้ | — |
| 1 | `QuotaResources` (closed list 7 รายการ — เฉพาะที่มีด่านจริง) · ตาราง 4 ตาราง (`Helpers/QuotaAddOnMigration`) · `QuotaLimitPolicy` (pure) · `IQuotaGate`/`QuotaGateService` + `QuotaStore` · `GET quota/summary` · `GET quota/check/{resource}` | **ไม่ backfill** allowance จากช่องเดิม — ผู้ใช้/พื้นที่/OCR อ่านช่องเดิมเป็นตัวตั้งต่อ (ไม่ทำสำเนาที่สอง) · ตาราง allowance ใช้กับทรัพยากรที่ไม่มีช่องเดิมเท่านั้น · แคตตาล็อกเป็นตาราง `QuotaAddOnOffers` แยก (ไม่ใช้ ApiFeature Kind ใหม่ — ผู้อ่าน ApiFeature เดิมไม่รู้จักชนิดใหม่) |
| 2 | ด่านจำนวนเว็บใน `CmsSiteService.CreateSiteAsync` (ล็อกธุรกรรม + ล็อกจุดเรียก) · เพดานหน้า/สินค้าต่อเว็บอ่าน allowance เมื่อเว็บไม่ตั้งเอง · `cms-sites.html` แถบ + ตรวจก่อนเปิดฟอร์ม + ปุ่มซื้อ · plans.html ลิงก์ไปหน้าตั้ง | dead helper `CanUploadMediaAsync`/`UpdateStorageUsageAsync` ไม่แตะ (อยู่ใน baseline เดิม) · แบนด์วิดท์/โดเมน/สาขา/POS ไม่อยู่ในรายการ (ไม่มีด่าน) |
| 3 | `QuotaAddOnPricing` 3 โหมด · `PaymentSourceKind.QuotaAddOnPurchase = 9` ใน `PlatformRevenueSource` · `QuotaAddOnPaymentHandler` · `QuotaAddOnPurchaseService` (สร้าง/ออนไลน์/สลิป/อนุมัติ/ปฏิเสธ/ยกเลิก/รายเดือน) · ใบเสร็จผ่าน `IssueAddOnReceiptAsync` (ขาเงินเข้าตามชนิด intent จริง) · ใบลดหนี้หาใบเดิมของโควตาเสริมได้ · หน้าแอดมิน `pages/admin-quota.html` | คิวสลิปอยู่หน้าแอดมินใหม่ (ไม่รวมกับคิวส่วนเสริมสวิตช์) · การซื้อที่จ่ายช้าหลังสร้าง: ช่วงสิทธิ์เริ่มวันจ่าย สิ้นสุดตามที่คิดไว้ |
| 4 | ใบต่ออายุหลายบรรทัด (ผ่าน `IssueUsageInvoiceAsync` reference ต่ออายุเดิม) · ตัดชำระใบต่ออายุ ⇒ `QuotaRenewalLines.ActivateForInvoiceAsync` · หน้าเลือกต่ออายุ (การ์ดในศูนย์บิล) | เปลี่ยนรายการหลังออกใบ = ล็อก (409) ไม่ใช่ supersede อัตโนมัติ · บริษัทใต้ License: ยังไม่รวมบรรทัดโควตาเสริมในการต่ออายุ License (หมดพร้อมรอบ — ซื้อใหม่) · โหมด PDF เดี่ยว (ไม่ตั้ง tenant ผู้ให้บริการ) ไม่รวมบรรทัดโควตาเสริม |
| 5 | ที่นั่ง (`CompanyService.AddUserAsync` ใต้ล็อก) · พื้นที่ (`QuotaStorageLimit` ใน `GetStorageStatusAsync`) · ที่พัก (`LodgingPropertyQuota` นับจำนวน + สวิตช์เดิม) · OCR = เครดิต Approved ในตัวนับเดิม · เลิกเส้นขอเครดิต OCR | แพ็กเอกสาร/ข้อความยังเป็นเส้น postpaid เดิม (`documents.topup`/`notify.topup`) — ไม่ย้ายเข้าตารางใหม่ (§B.9 ข้อ 4) |
| 6 | เมนูเหลือ "แพ็กเกจและการชำระเงิน" (หน้าย่อยเปิดจาก hub · id เมนูคงเดิม) · `js/quota-ui.js` ตัวแสดง/หน้าต่างซื้อ/ต่ออายุตัวเดียว · การ์ดโควตา + การซื้อ + ต่ออายุในศูนย์บิล | ยังไม่แบ่งเป็น 4 แท็บ (ไม่แตะส่วนใบเสร็จของศูนย์บิล — ทีมรอบ 228 กำลังตรวจ) · `usage.html` หลอดเดิมยังคำนวณ % เอง (ข้อมูลจากเซิร์ฟเวอร์) — ย้ายเข้า renderer เดียวรอบถัดไป · D-7/D-10/D-11 ยังไม่ทำ |

### ภาคผนวกฝ่ายค้านรอบ 229 (คำตัดสินผู้ประสานงาน — แก้แล้วใน working tree)

| # | ปัญหา | แก้ |
|---|---|---|
| B1 | วง DI `QuotaAddOnPurchaseService → IPlatformPaymentService → IPaymentIntentService → IEnumerable<IPaymentCompletionHandler> → QuotaAddOnPaymentHandler → …` ⇒ start ไม่ขึ้น | resolve `IPlatformPaymentService` ตอนใช้ (IServiceProvider) · `tools/di_cycle_check.py` เดิมมองไม่เห็น (เก็บ register แค่ตัวสุดท้าย + ตัด `IEnumerable<T>`) ⇒ แก้ให้เห็น + negative test ในตัว (วงแบบ B1 ต้องถูกจับ) |
| B2 | ข้อเสนอเก่าจ่ายได้ (ข้ามเดือน/หมดช่วง/วันหมดอายุแพ็กเกจเปลี่ยน) ⇒ เปิดสิทธิ์ช่วงศูนย์วัน/ยอดผิดฐาน | `QuotaAddOnPricing.StaleCheck` ตัวเดียว: เริ่มชำระ/แนบสลิป/อนุมัติ = 409 `QUOTA-STALE` "สร้างรายการซื้อใหม่" · เงินเข้าช้า: เดือนเดียวแบบกำหนดวัน = นับจากวันจ่าย (`LatePaymentEnd`) · อื่น ๆ = ไม่เปิด + เรื่องค้าง `AttentionRule=QUOTA-STALE` + audit + ล้มดัง · รายเดือน: เดือนแรก = เดือนที่คิดราคา |
| S1 | ยกเลิกการต่อ (Coterm) หลังออกใบต่ออายุ = no-op เงียบ | 409 `QUOTA-RENEW-LOCKED` |
| S2 | อนุมัติรายการไม่มีสลิปได้ | อนุมัติปกติเฉพาะสลิปรอตรวจ · ไม่มีสลิป = ต้องมีหลักฐานรับเงินนอกระบบ + เหตุผล (audit) · มี intent Created/Pending = ห้าม |
| S3 | ต่อโควตาเสริมหลัง commit การต่อแพ็กเกจ (ล้มกลางทาง = ต่อแพ็กเกจแต่ไม่ต่อโควตา) · ใบต่ออายุที่ถูกแทนที่เงียบ | สร้างรอบใหม่ใน context ก่อน SaveChanges ของการอนุมัติ (ธุรกรรมที่ล็อกเดียวกัน · `save:false`) · อ่านล้ม = เรื่องค้าง `PLATFORM-QUOTA-RENEWAL-FAILED` บนรายการชำระ · ใบที่ถูกแทนที่ ⇒ ปลดบรรทัด + แจ้งเจ้าของ + กระดิ่งแอดมิน + audit |
| S4 | แถวหนึ่งชน unique ⇒ PostgreSQL abort ทั้งงวด | savepoint ต่อแถว |
| S5 | หน้าแอดมินช่องทางรับชำระไม่เห็นรายการโควตาเสริม/ออกใบเสร็จซ้ำไม่ได้ | `PlatformRevenueSource.Kinds` ชุดเดียว (ด่าน + query) · ออกใบอีกครั้งของโควตาเสริม |
| S6 | ที่พักไม่ผ่านด่านเดียว/ไม่ล็อก/ไม่ดูทดลองจริง · สวิตช์หลายแห่งไม่สะท้อนในหน้าสรุป | `LodgingPropertyQuota` ใช้ `IQuotaGate` (ล็อกเมื่ออยู่ในธุรกรรม · `LodgingService.CreatePropertyAsync` เปิดธุรกรรม) · `LimitAsync` สวิตช์ = ไม่จำกัด |
| S7 | ใบเสร็จใช้ยอดที่ต้องจ่าย/วันอนุมัติ | ยอด = เงินที่รับจริง · วันที่ = วันโอน (สลิป/แอดมินระบุ) §78/1 |
| S8 | ไม่มีทางถอนสิทธิ์หลังคืนเงิน | `RevokeAsync` (แอดมิน · เหตุผลบังคับ · audit · ไม่ลบข้อมูล) ข้างสถานะ "คืนเงินแล้ว" ในคิว |
| S9 | เทสต์ขาด | `QuotaAddOnPaymentDbTests` (เงินซ้ำ · สลิป+ออนไลน์ · เงินขาด · จ่ายซ้อน · จ่ายช้า · หลักฐานอนุมัติ · S1 · ต่ออายุซ้ำ · เพิกถอน · ที่นั่งสองทิศ) + pure (StaleCheck · LatePaymentEnd · ยอดใบหลายบรรทัด `PlatformInvoiceLineMath`) |
| S10 | ส่งมอบบริษัทเพิ่มสมาชิกไม่ผ่านด่านที่นั่ง | ผ่านด่านเมื่อเพิ่มสุทธิ (ไม่ถอดผู้ส่งมอบ) |
| เพิ่ม | ค้างไม่จ่าย 14 วัน (รอชำระ/ถูกปฏิเสธ/สลิปรอตรวจ) ⇒ ยกเลิก + audit (สลิปรอตรวจ = เรื่องค้างของแอดมิน) · คิว OCR เดิมติดป้าย "ไม่มีหลักฐานการชำระเงิน" | — |

ยังไม่ทำจาก "nice": ส่งราคาต่อหน่วยของบรรทัดต่ออายุตรง ๆ (ยังถอดจากยอด 4 ตำแหน่ง — ผลต่าง ≤ 1 สตางค์/บรรทัด มีเทสต์) · เตือนแม่แบบซ้ำระดับเดียวกัน · VAT snapshot ของค่าเดือนรายเดือน

### สิ่งที่ doc ต้องแก้ตามผลตรวจนี้ (เมื่อมีคอมมิตโค้ด)
- ACCOUNT_STRUCTURE §6.1c บอกว่า "`subscription.html` พิมพ์ +7 วัน" ถูกแก้แล้ว — โค้ดยังพิมพ์อยู่ (`subscription.html:340`) ⇒ doc ผิด แก้พร้อมแพ็ก 0
- ACCOUNT_STRUCTURE §6.1b ตาราง "แคตตาล็อก + ราคา" ต้องเพิ่มแถวโควตาเสริม (แพ็ก 1/3)
- ข้อ 0.6 (ค่าส่วนเสริมหลังแพ็กเกจหมด) ควรขึ้นทะเบียนเป็น G-ใหม่ใน PLATFORM_BILLING_AUDIT §2

_ตรวจกับ HEAD a138faaa · 2026-11-02 · ไม่ได้แก้โค้ด · ไม่ได้คอมไพล์ (ไม่มีโค้ดใหม่)_
