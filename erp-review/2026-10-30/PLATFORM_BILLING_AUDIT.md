# PLATFORM_BILLING_AUDIT — เงินจากผู้เช่าเข้า NextAcc ครบหรือยัง (รอบ 219 ส่วน A)

> คำถามเจ้าของ: "ทุกฟังก์ชันที่เรียกเก็บเงิน หรือช่องทางที่ให้ลูกค้าจ่ายเงิน ซื้อโควตาต่าง ๆ เพิ่ม เชื่อมต่อครบถ้วนสมบูรณ์หรือยัง
> ในการเรียกเก็บเงินเข้า NEXTACC · ควรมีฟีเจอร์อะไรอีก · มีให้ออก manual link ดีไหม"
>
> **ขอบเขต:** เงินที่ไหลจาก **ผู้เช่า (ลูกค้าของเรา) → NextAcc** (platform billing) เท่านั้น · ไม่รวมเงินที่ลูกค้าปลายทางจ่ายให้ผู้เช่า
> (เว็บขายของ/ที่พัก/POS/portal) ยกเว้นจุดที่ซ้อนกัน (ดู G-1)
> **ฐานโค้ด:** `c18ca0ce` · งานนี้ **วิเคราะห์อย่างเดียว ไม่แก้โค้ด** · ยังไม่ได้คอมไพล์/รันในเครื่องนี้ — ทุกข้อเป็นการอ่านโค้ด (file:line อ้างอิงที่ sha นี้)

> **สถานะ (รอบ 219 เฟส 0 · คำตัดสินข้อ 178):** อุด G-0/G-1/G-2/G-3/G-4/G-5/G-6(ค่าตั้งต้น)/G-11 + part B (a)(b)(c) แล้ว — <sha ในคอมมิตถัดไป> (ยังไม่ได้คอมไพล์) ·
> G-1 เฟส 0 = **ปิด** ปุ่มชำระออนไลน์ของส่วนเสริม (ช่องที่ถูกต้องเป็นเฟส 1) · ของที่เกิดไปแล้วไม่ถูกแก้ — รายงานที่ `/admin/revenue.html` ·
> G-7/G-8/G-9/G-10/G-12..G-17 ยังเปิด (เฟส 1–3)

---

## 0. สรุปสั้น (คำตอบตรงคำถาม)

**ยังไม่ครบ และมีรูรั่วระดับ P0 หลายจุด** — ระบบ "ออกเอกสาร/นับการใช้งาน" ค่อนข้างดี (มิเตอร์ · ใบแจ้งหนี้ค่าใช้งาน · ใบกำกับใน tenant ผู้ให้บริการ)
แต่ **ด่าน "เงินเข้าจริงก่อนให้สิทธิ์" และ "ตามเก็บเงิน" แทบไม่มี**:

1. **อัปเกรดแพ็กเกจได้โดยไม่จ่าย** — ปุ่ม "อัปเกรด" ใน `subscription.html:464` เรียก `POST /api/subscription/{cid}/convert` →
   `SubscriptionService.ConvertTrialAsync` (`SubscriptionService.cs:355`) ตั้ง `Status=Active` + `EndDate=now+รอบบิล` + เปิดฟีเจอร์ทันที **ไม่มีขั้นชำระเงินเลย**
   (หน้าโชว์ "ยอดชำระ ฿…" แต่ไม่มีการเก็บ) · `ChangeSubscriptionAsync` (`:675`) ก็เปลี่ยนไปแพ็กเกจแพงกว่าได้ทันทีโดยไม่คิดส่วนต่าง
2. **เงิน "ค่าส่วนเสริม" ผ่าน gateway เข้าบัญชี Omise ของผู้เช่าเอง ไม่ใช่ของ NextAcc** — `MeteringController.StartAddOnPayment` (`:203`)
   เรียก `PaymentIntentService.StartAsync(companyId=ผู้เช่า, …)` ซึ่งเลือก `PaymentProviderConfigs` **ของผู้เช่า** (`PaymentIntentService.cs:224-231`)
   ⇒ ผู้เช่าที่ต่อ Omise ของตัวเองไว้ "จ่ายให้ตัวเอง" แล้ว handler ประทับ `Paid` เปิดสิทธิ์ให้ · ขัดกับ `PAYMENT_GATEWAY_DESIGN.md` §8 ข้อ 3
   ที่ออกแบบให้ใช้ config ของ **tenant ผู้ให้บริการ**
3. **ผู้เช่ายืนยันการจ่ายค่าบริการให้ตัวเองได้** — `POST /api/companies/{cid}/pay/intents/{id}/confirm-manually` (`PaymentGatewayController.cs:323`)
   ใช้สิทธิ์ `BankReconcile` ของบริษัทผู้เช่า (`PaymentGatewayPermissionScope.cs:26`) และ manual-slip ไม่ถูกบล็อก (`SettlesDirectlyToBank=true`)
   ⇒ intent ชนิด `AddOnPurchase`/`SubscriptionPayment` ที่ผู้เช่าสร้างเอง → กดยืนยันเอง → handler อนุมัติ/ต่ออายุแพ็กเกจ + **ออกใบกำกับภาษีว่ารับเงินแล้ว** ทั้งที่ไม่มีเงิน
4. **ยอด/ระยะเวลาของ `SubscriptionPayment` มาจากผู้เช่า** — `SubmitPaymentAsync` (`SubscriptionService.cs:1853`) รับ `Amount`,
   `RequestedPlan`, `RequestedPeriodMonths` จาก body ไม่ตรวจกับราคาแพ็กเกจ · ทาง gateway handler เทียบ intent กับ `pay.Amount` (ซึ่งผู้เช่าตั้งเอง) ⇒ ฿1 = Enterprise 120 เดือน ได้
5. **ใบแจ้งหนี้ค่าใช้งาน/ต่ออายุ ไม่มีทางจ่ายและไม่มีใครตามเก็บ** — `UsageInvoicingJob` ออกใบใน tenant ผู้ให้บริการแล้วจบ ไม่มีอีเมล ไม่มีหน้าให้ผู้เช่าเห็น
   ไม่มีลิงก์จ่าย ไม่มี dunning ไม่ผูกกับการระงับสิทธิ์
6. **กลุ่มบัญชี (BillingAccount) ถูกตั้งเป็น Prepaid ทั้งหมดแต่ไม่มีทางเติมเครดิต** ⇒ ค่าใช้งานของทุกบริษัทใต้แพลนกลุ่มถูก "ตัดเครดิต" จนติดลบ
   แล้ว `UsageInvoicingJob` ปิดรอบโดย **ไม่ออกใบ** — รายได้หายเงียบ

**manual link: แนะนำ "ทำ" แต่ต้องผูกกับใบแจ้งหนี้จริงใน tenant ผู้ให้บริการเท่านั้น (ไม่ใช่ลิงก์ยอดอิสระ)** — ดู §4

---

## 1. Inventory — ทุกจุดที่ผู้เช่าถูกเรียกเก็บ / จ่ายเงินให้ NextAcc ได้

สัญลักษณ์: ✅ ครบ · ⚠️ บางส่วน · ❌ ไม่มี/พัง

### 1.1 ตารางสรุป

| # | รายการ | ทางเข้า | จ่ายจริงอย่างไรวันนี้ | ผ่าน PaymentIntent/IPaymentProvider | เอกสารที่ออก (ผู้ขาย = NextAcc) | ไม่จ่ายแล้วเกิดอะไร | คืนเงิน/ยกเลิก | สถานะ |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| S1 | ทดลองใช้ → แพ็กเกจจ่ายเงิน (convert) | `subscription.html:464` → `SubscriptionController.cs:159` → `ConvertTrialAsync` `SubscriptionService.cs:355` | **ไม่มีการจ่าย** — Active ทันที | ❌ | ❌ ไม่มี | ใช้ฟรีครบรอบ แล้ว PastDue→Suspended (แต่ด่านบังคับค่าตั้งต้น = Shadow) | ❌ | ❌ |
| S2 | เปลี่ยนแพ็กเกจ (อัป/ดาวน์) | `PUT /api/subscription/{cid}/plan` `:171` → `ChangeSubscriptionAsync` `:675` | ไม่มี — มีผลทันที ไม่ prorate | ❌ | ❌ | อัปเกรดฟรีจนถึงรอบต่ออายุ | ดาวน์เกรดไม่คืนส่วนต่าง | ❌ |
| S3 | ต่ออายุแพ็กเกจ (โอน+สลิป) | API `POST …/{cid}/payments` `:241` + `payments/{id}/slip` `:253` · **ไม่มี UI ฝั่งผู้เช่า** (มีแต่ประวัติ `subscription.html:220`) | โอนเข้าบัญชีไหนก็ไม่รู้ (ไม่มีช่องเลขบัญชีแพลตฟอร์มใน `SiteSettings`) → แอดมินอนุมัติ `AdminController.cs:1881` | ⚠️ handler มี (`SubscriptionPaymentHandler`) แต่ UI ไม่ใช้ และ intent วิ่งด้วย config ผู้เช่า | ✅ อนุมัติ → `IssuePaidReceiptAsync` (`PlatformBillingDocumentIssuer.cs:90`) ใบกำกับ/ใบเสร็จขายสด · ⚠️ ถ้าไม่ได้ตั้ง tenant ผู้ให้บริการ ตกไป PDF เลขรันเอง (`SaasBillingDocumentService.cs:35+`) ไม่อยู่ในชุด gap-free | แจ้งเตือนตามวัน (`ProcessSubscriptionNotificationsAsync` `:2249`) → PastDue → Suspended (`:2336`) | ❌ ไม่มีใบลดหนี้/คืนเงิน | ⚠️ |
| S4 | บันทึกรับเงิน/ยกเว้นโดยแอดมิน | `POST /api/admin/companies/{cid}/subscription-payments/record` `AdminController.cs:1900` → `RecordManualPaymentAsync` `:2168` | แอดมินกรอกเอง (รองรับ WHT · waived ต้องมีเหตุผล) | ❌ (นอก intent) | ✅ ใบเสร็จ/ใบกำกับ | — | ❌ | ✅ (สำหรับงานแอดมิน) |
| S5 | ใบแจ้งหนี้ต่ออายุล่วงหน้า | job `ProcessSubscriptionNotificationsAsync` → `GenerateRenewalInvoiceAsync` → `IssueRenewalInvoiceAsync` `:164` | **ไม่มีทางจ่ายใบนี้** — ลูกค้าดาวน์โหลด PDF ได้ (`SubscriptionController.cs:330`) | ❌ | ✅ ใบแจ้งหนี้ (Dr ลูกหนี้/Cr รายได้) · ⚠️ ตอนอนุมัติสลิปออกใบขายสดอีกใบ **ไม่ล้างลูกหนี้ใบนี้** ⇒ รายได้ซ้ำ + ลูกหนี้ค้างตลอดกาล (ACCOUNT_STRUCTURE §6.1 ตารางจังหวะยืนยันว่าเป็นสองใบคนละบัญชี) | ไม่มี dunning บนใบนี้ | ❌ | ⚠️ |
| S6 | แพลนระดับบัญชี (AccountSubscription / group) | `account-subscription.html` · `AccountSubscriptionController.cs:122 start-trial` · แอดมิน `AdminAccountSubscriptionController` | ไม่มีทางจ่ายของตัวเอง — ต่ออายุได้ทางเดียวคือ cascade จาก S3 (`ReviewPaymentAsync` ส่วน AccountSubscription) หรือแอดมิน extend | ❌ | ไม่มีใบของแพลนกลุ่ม (ราคา `MonthlyPrice/AnnualPrice` เก็บไว้เฉย ๆ) | `AccountPlanExpiryReminderJob` เตือน 7/3/1 วัน → Expired | ❌ | ⚠️ |
| A1 | เปิดส่วนเสริมเหมารายเดือน (lodging.* ฯลฯ) — จ่ายก่อนใช้ | `addons.html` → `POST metering/features/{code}` `MeteringController.cs:135` → `SetFeatureEnabledAsync` (`AddOnPaymentPolicy.RequiresPayment`) | (ก) "ชำระออนไลน์" `:203` — **เงินเข้า Omise ของผู้เช่า** (ปุ่มขึ้นเฉพาะเมื่อ `public-pay/methods` ของผู้เช่าเปิด `addons.html:296`) (ข) แนบสลิป `:244` → แอดมินอนุมัติ `MeteringAdminController.cs:74` | ⚠️ ผ่าน intent แต่ **ผิดบริษัท** (G-1) | ❌ **ไม่มีใบเสร็จ/ใบกำกับ** ตอนจ่ายล่วงหน้า (`AddOnPurchaseService.ApproveAsync/MarkPaidByGatewayAsync` ไม่เรียก issuer) | `AwaitingPayment` ใช้ได้ไปเรื่อย ๆ — ตัดสิทธิ์เฉพาะเมื่อแอดมิน "ปฏิเสธ" · `AddOnPaymentPolicy.UsableWhilePending` (`:57`) **ไม่มีผู้เรียก** | ปิดเองได้ ไม่มี proration/คืนเงิน | ❌ |
| A2 | ค่าเหมารายเดือนของ add-on (รอบบิล) | `AddOnMonthlyBillingJob` → `RecordFlatMonthlyAsync` → `UsageEvent` → `UsageInvoicingJob` | เข้าใบแจ้งหนี้ค่าใช้งาน (S5/M1) | ❌ | ✅ ใบแจ้งหนี้หลายบรรทัด | ดู M1 | ❌ | ⚠️ · **เก็บซ้อนกับ A1** (G-5) |
| M1 | ค่าใช้งานตามจริง/overage (documents.overage · lodging.stay · lodging.email.overage · lodging.notify.sms · Connected API PerCall) | `IUsageMeteringService.RecordAsync` → `UsageInvoicingJob` (ทุก 6 ชม. · ปิดงวดที่จบแล้ว · ขั้นต่ำ ฿50 ยกยอด) → `IssueUsageInvoiceAsync` `:217` | **ไม่มีทางจ่าย** — ใบอยู่ใน tenant ผู้ให้บริการ ผู้เช่าไม่เห็น ไม่ได้อีเมล | ❌ | ✅ ใบแจ้งหนี้ (หลายบรรทัด · หน่วย snapshot) · ✅ idempotent (unique `CompanyId+IdempotencyKey` · advisory lock) | ไม่มีผล (ไม่มี dunning · ไม่ผูก suspension) | ❌ ไม่มีใบลดหนี้ | ⚠️ |
| M2 | ซื้อโควตาเอกสาร (documents.topup) | `addons.html:191` → `MeteringController.cs:295` → `QuotaService.PurchaseTopUpAsync` `:94` | **ให้โควตาทันที** แล้ว "เข้าบิลรอบถัดไป" (postpaid ผ่าน M1) · Idempotency-Key ✅ | ❌ | ผ่านใบ M1 | เหมือน M1 — ได้ของแล้วไม่จ่ายก็ไม่มีอะไรเกิด | ❌ | ⚠️ |
| M3 | ซื้อแพ็กข้อความแจ้งเตือน (notify.topup) | `MeteringController.cs:322` → `NotifyQuotaService.PurchaseTopUpAsync` `:408` | เหมือน M2 (ตรวจราคา/ขนาดที่ผู้ซื้อเห็น 409 ✅) | ❌ | ผ่านใบ M1 | เหมือน M1 | ❌ | ⚠️ |
| O1 | ซื้อเครดิต OCR | `POST api/companies/{cid}/ocr/credits/purchase` `OcrController.cs:1619` → `OcrQuotaService.PurchaseCreditsAsync` `:416` · แอดมินอนุมัติ `AdminController.cs:3321` · **ไม่มี UI ผู้เช่า** (หน้าสแกนบอก "กรุณาซื้อเครดิตเพิ่ม" แต่ไม่มีปุ่ม) | ไม่มีช่องจ่าย/สลิปเลย — แอดมินกดอนุมัติจากความเชื่อ · ช่อง `AmountPaid` ตั้งตอนสร้าง (ชื่อผิดความหมาย — เป็นยอดที่ "ควรจ่าย") | ❌ | ❌ ไม่มีเอกสารใด ๆ · ไม่เข้า UsageEvent | — | ปฏิเสธ = PagesRemaining 0 | ❌ |
| Q1 | แลกโควตาจากภารกิจ (quota rewards) | `MeteringController.cs:361` | ไม่มีเงิน (โฆษณา/ภารกิจ) | — | — | เพดานต่อวัน/เดือน `QuotaRewardPolicy` | — | ✅ (ไม่ใช่รายได้เงินสด) |
| P1 | กลุ่ม Prepaid (BillingAccount.CreditBalance) | ตัดเครดิตใน `UsageMeteringService.cs:134-137, 364-365` | **ไม่มีทางเติมเครดิตเลย** (ไม่มีโค้ดใดทำ `CreditBalance +=`) | ❌ | ❌ ไม่มีใบเสร็จเติมเครดิต | `UsageInvoicingJob.cs:107` ปิดรอบ prepaid **โดยไม่ออกใบ** · `PostpaidCreditLimit/GracePeriodDays` ไม่มีผู้อ่านเชิงธุรกิจ | ❌ | ❌ |

### 1.2 หลักฐานรายจุดที่สำคัญ

**S1/S2 — ไม่มีขั้นจ่ายก่อนให้สิทธิ์**
`ConvertTrialAsync` คำนวณ `price` (รวมโค้ดส่วนลด) แล้ว **แค่เก็บลง `sub.PricePerCycle`** จากนั้น `sub.Status = Active`, `EndDate = now.AddMonths(months)`,
ลอก `EnabledFeatures`/โควตาจาก template — ไม่มี `SubscriptionPayment`, ไม่มี intent, ไม่มีใบแจ้งหนี้ · หน้า `subscription.html:440-467` แสดง "ยอดชำระ" แล้วกด "อัปเกรด" ได้เลย ·
ผลข้างเคียง: MRR ในหน้าแอดมิน (`AdminController.cs:111`) นับจาก `PricePerCycle` ของ sub ที่ Active ⇒ **MRR รวมลูกค้าที่ไม่เคยจ่าย**

**G-1 — intent ค่าบริการแพลตฟอร์มวิ่งด้วย config ของผู้เช่า**
`PaymentIntentService.StartAsync(companyId, …)`: `_db.PaymentProviderConfigs.Where(c => c.CompanyId == companyId && c.IsActive …)` → ไม่มี = `manual-slip`
— ไม่มีกรณีพิเศษสำหรับ `SubscriptionPayment`/`AddOnPurchase` · `SiteSettings.PlatformCompanyId` มีอยู่แล้ว (`SiteSettings.cs:298`) แต่ใช้เฉพาะออกเอกสาร
ไม่ได้ใช้เลือก gateway · ผลคือ (ก) ผู้เช่าที่มี Omise เก็บเงินค่าส่วนเสริมเข้าบัญชีตัวเอง (ข) intent/ค่าธรรมเนียม/settlement ของเงินก้อนนี้ไปปนใน
รายงานกระทบยอด gateway และบัญชีพัก 11340 ของผู้เช่า (คืนเงินผ่าน `GatewayRefundService` จะลง JE ในสมุดของผู้เช่า)

**G-2 — ยืนยันเอง (self-confirm)**
`ConfirmManually` ตรวจแค่ `RequirePermission(BankReconcile)` ของบริษัทใน route + `ManualConfirmBlockReason` (บล็อกเฉพาะ provider ที่ถือเงินแล้วไม่มี charge)
→ `ApplyChargeAsync(Succeeded)` → `DispatchSucceededAsync` → `AddOnPurchasePaymentHandler`/`SubscriptionPaymentHandler` ·
Owner มีทุกสิทธิ์ ⇒ เปิด add-on → สร้าง intent `ManualSlip` → กดยืนยันเอง = `Paid` โดยไม่มีแอดมินแพลตฟอร์มเห็น ·
ทาง subscription: `POST …/payments` (ยอดเอง) → `POST /pay/intents` (`SourceKind=SubscriptionPayment`, ยอดเท่ากัน) → confirm-manually → `ReviewPaymentAsync(systemConfirmed:true)` ต่ออายุ + ออกใบกำกับภาษี

**G-3 — handler ไม่ตรวจเจ้าของ**
`SubscriptionPaymentHandler.cs:42` หา `SubscriptionPayments.Where(p => p.Id == intent.SourceId)` **ไม่กรองบริษัท** (ต่างจาก `AddOnPurchasePaymentHandler` ที่กรอง `CompanyId`) ·
intent ของบริษัท A ชี้ไปที่รายการของบริษัท B ได้ถ้ารู้ id + ยอด

**G-4 — ราคาไม่ถูกตรวจ**
`SubmitPaymentAsync` เก็บ `request.Amount/RequestedPlan/RequestedPeriodMonths` ตรง ๆ (ไม่มีแม้ `> 0`) และไม่มี `RequireOwner` (สมาชิกใดก็ได้) ·
`ReviewPaymentAsync` ต่ออายุ `RequestedPeriodMonths` ตามที่ขอ และตั้ง `PricePerCycle` จาก template — ไม่เทียบกับ `payment.Amount` ·
เส้นแอดมินพอรับได้ (คนดู) แต่เส้น `systemConfirmed` (gateway) อนุมัติอัตโนมัติด้วยตัวเลขที่ผู้เช่าตั้งเอง

**G-5 — add-on เก็บซ้อน**
A1 เก็บ `AcceptedUnitPrice` ล่วงหน้า (`AddOnPaymentPolicy.AmountDue`) แล้ว A2 `AddOnMonthlyBillingJob` ออกค่าเหมาเดือนเดียวกันอีก (`AddOnBilling.FlatAmount` `:53`
ไม่รู้จักสถานะ `Paid`/`PaymentIntentId`) ⇒ งวดแรก **จ่ายสองครั้ง** (ล่วงหน้า + ใบแจ้งหนี้) · งวดถัดไป `PaymentStatus` ค้าง `Paid` ตลอด ⇒ การจ่ายล่วงหน้าเป็นแค่ด่านครั้งเดียว
ไม่ใช่ "ค่าเดือน"

**G-6 — Prepaid ที่ไม่มีทางเติม**
`BillingAccount.PaymentModel` ค่าตั้งต้น `Prepaid` (`BillingAccount.cs:46` · คอลัมน์ `DEFAULT 1` `DatabaseMigrationHelper.cs:6112`) ·
migration backfill สร้าง BillingAccount ให้ทุก AccountSubscription และผูกทุกบริษัทใต้แพลน (`:6193+`) ⇒ บริษัทเหล่านี้ทุก `UsageEvent` ไป `CreditBalance -= …`
แล้ว `UsageInvoicingJob` ประทับ `BilledPeriod` โดยไม่ออกใบ · ไม่มี endpoint/หน้า/handler เติมเครดิต ⇒ **ค่าใช้งานของลูกค้าแพลนกลุ่มไม่เคยถูกเรียกเก็บ** (ยอดติดลบสะสม)

**G-7 — ใบแจ้งหนี้ที่ไม่มีใครเห็น**
`IssueUsageInvoiceAsync`/`IssueRenewalInvoiceAsync` สร้าง `Document(Invoice)` ใน tenant ผู้ให้บริการและ approve — ไม่มีอีเมลถึงผู้เช่า (ใบเสร็จมี `SendEmailAsync` แต่ใบแจ้งหนี้ค่าใช้งานไม่มี) ·
ผู้เช่าไม่มีหน้าดู/ดาวน์โหลดใบค่าใช้งาน (`BilledDocumentId` ไม่มีผู้อ่านฝั่งผู้เช่า) · `OverdueDunningJob` ของ tenant ผู้ให้บริการแจ้ง **พนักงานของเรา** ไม่ใช่ผู้เช่า

**G-8 — ด่านบังคับเมื่อไม่จ่าย**
`SubscriptionEnforcementResolver`: ไม่มีแถวค่าตั้ง/อ่านไม่ได้ = **Shadow** (ตัดสินแต่ไม่บล็อก) — PastDue/Suspended จึงไม่บล็อกจนกว่าแอดมินจะกด Enforce ·
ไม่มีเส้นใดผูก "ใบแจ้งหนี้ค้าง X วัน" เข้ากับสถานะแพ็กเกจ/ส่วนเสริม

**เอกสาร/ภาษี (กฎเหล็ก #2)**
- ✅ เส้นหลัก (`PlatformBillingDocumentIssuer`) ออกใน tenant ผู้ให้บริการ ⇒ เลข gap-free · JE · รายงานภาษีขาย · e-Tax ได้ · ถอด VAT รวมใน · WHT ย้อนจากยอดจริง
- ⚠️ โหมดสำรอง PDF (`SaasBillingDocumentService`) ออก "ใบกำกับภาษี" เลขรันของตัวเอง (`NextReceiptNumberAsync`) เมื่อยังไม่ตั้ง tenant ผู้ให้บริการ — ถ้าแพลตฟอร์มจด VAT
  ใบพวกนี้ **อยู่นอกชุด §86/4 และไม่เข้ารายงานภาษีขาย** (ACCOUNT_STRUCTURE ยอมรับไว้แล้ว) ⇒ ควรห้ามออกใบกำกับในโหมดนี้
- ❌ A1 (จ่ายค่าส่วนเสริมล่วงหน้า) และ O1 (เครดิต OCR) **ไม่มีเอกสารใดเลย** — รับเงินแล้วไม่ออกใบกำกับ/ใบเสร็จ = ผิด §86 (ถ้าจด VAT) และไม่มีรายได้ลง GL
- ⚠️ S5 + S3: ใบแจ้งหนี้ (Dr ลูกหนี้/Cr รายได้) + ใบขายสดตอนรับเงิน (Cr รายได้อีกรอบ) ⇒ **รายได้ซ้ำ** และลูกหนี้ค้าง — ต้องเปลี่ยนให้ "รับชำระใบแจ้งหนี้เดิม" ไม่ใช่ขายสดใบใหม่
- ❌ ไม่มีใบลดหนี้ §86/10 บนเส้นแพลตฟอร์มเลย (ยกเลิก/ดาวน์เกรด/คืนเงิน/ปฏิเสธหลังออกใบ)

**Idempotency / audit**
- ✅ UsageEvent unique key · advisory lock รอบบิล · top-up `Idempotency-Key` · intent reuse ต่อ source · handler กันซ้ำด้วยสถานะ
- ⚠️ `SubscriptionHistory` เป็น log ธรรมดา ไม่ใช่ `AddChainedAuditLog` · `ConvertTrialAsync/ChangeSubscriptionAsync` ไม่ลง audit chain · การอนุมัติ OCR credit ไม่มี audit

---

## 2. Gaps & Risks (เรียงตามความรุนแรง)

| ID | ประเภท | รายละเอียด | ผลกระทบ | ระดับ |
| --- | --- | --- | --- | --- |
| G-0 | ใช้ได้โดยไม่จ่าย | S1/S2 อัปเกรด/เปลี่ยนแพ็กเกจไม่มีขั้นชำระ | ลูกค้าทุกรายใช้แพ็กเกจจ่ายเงินฟรีได้หนึ่งรอบเต็ม (สูงสุดรายปี) · MRR ปลอม | **P0** |
| G-1 | เงินผิดกระเป๋า | intent ค่าส่วนเสริม/ค่าบริการใช้ gateway ของผู้เช่า | เงินไม่เข้า NextAcc · ผู้เช่าได้สิทธิ์ · ปนบัญชีพักของผู้เช่า | **P0** |
| G-2 | ยืนยันเอง | confirm-manually บน intent ชนิดแพลตฟอร์มโดยผู้เช่า | ต่ออายุ/เปิด add-on ฟรี + ออกใบกำกับภาษีเท็จในชื่อเรา | **P0** |
| G-3 | ข้ามบริษัท | `SubscriptionPaymentHandler` ไม่กรองบริษัท | อนุมัติรายการของบริษัทอื่น | **P0** (แก้ 1 บรรทัด) |
| G-4 | ยอดตั้งเอง | `SubscriptionPayment.Amount/PeriodMonths` จากผู้เช่า | ฿1 ได้แพ็กเกจหลายปีบนเส้น gateway | **P0** |
| G-6 | รายได้หายเงียบ | BillingAccount Prepaid ไม่มีทางเติม | ค่าใช้งานของทุกบริษัทใต้แพลนกลุ่มไม่เคยออกบิล | **P0** |
| G-5 | เก็บซ้อน | add-on จ่ายล่วงหน้า + ค่าเหมาในใบแจ้งหนี้งวดเดียวกัน | ลูกค้าโดนเก็บสองครั้ง → ร้องเรียน/คืนเงิน | **P1** |
| G-7 | ออกบิลแล้วไม่เก็บ | ใบค่าใช้งาน/ต่ออายุไม่มีช่องจ่าย ไม่ส่ง ไม่ทวง | ลูกหนี้ค้างไม่รู้จบ · ได้ของก่อน (top-up) แล้วไม่จ่าย | **P1** |
| G-8 | ไม่มี dunning/suspension | ไม่ผูกหนี้ค้างกับสิทธิ์ · ด่านค่าตั้งต้น Shadow | ไม่มีแรงจูงใจให้จ่าย | **P1** |
| G-9 | ไม่มีเอกสาร | A1/O1 รับเงินแล้วไม่ออกใบ | ผิด §86 · รายได้ไม่ลง GL | **P1** |
| G-10 | รายได้ซ้ำ | ใบแจ้งหนี้ต่ออายุ + ใบขายสดตอนรับเงิน | งบ/ภ.พ.30 ของ NextAcc สูงเกินจริง · ลูกหนี้ค้าง | **P1** |
| G-11 | ใช้ระหว่างค้างจ่ายไม่จำกัด | add-on `AwaitingPayment` ใช้ได้ตลอด (`UsableWhilePending` ไม่ถูกเรียก — dead helper) | เปิดแล้วไม่จ่ายก็ใช้ได้ | **P1** |
| G-12 | OCR credit ไม่มีวงจรเงิน | ไม่มี UI ซื้อ · ไม่มีจ่าย · ไม่มีใบ · รายได้ในหน้าแอดมินคำนวณจาก `AmountPaid` ที่ไม่ได้จ่ายจริง | ขายไม่ได้ / รายงานรายได้ปลอม | **P1** |
| G-13 | ไม่มีช่องทางโอนของแพลตฟอร์ม | ไม่มีเลขบัญชี/PromptPay ของ NextAcc ใน `SiteSettings` ที่ผู้เช่าเห็น | ลูกค้าไม่รู้จะโอนที่ไหน | **P1** |
| G-14 | ไม่มีคืนเงิน/ใบลดหนี้ | ยกเลิก/ดาวน์เกรด/ปฏิเสธหลังออกใบ | ผิด §86/10 เมื่อจำเป็นต้องลดหนี้ · ทำมือนอกระบบ | **P2** |
| G-15 | โหมด PDF สำรองออก "ใบกำกับภาษี" นอกชุด | ไม่ตั้ง tenant ผู้ให้บริการ | ใบกำกับไม่เข้ารายงานภาษีขาย | **P2** (บังคับตั้งค่าก่อน) |
| G-16 | top-up/meter ราคา 0 | ไม่ตั้ง `ApiPricingPlan` ⇒ `unitPrice=0` ให้โควตาฟรี (`UsageMeteringService` "ไม่มีแผนราคา = ราคา 0") | ขายฟรีเงียบ ๆ ถ้าลืมตั้งราคา | **P2** (เตือนในหน้าแอดมิน) |
| G-17 | audit | เปลี่ยนแพ็กเกจ/อนุมัติเครดิตไม่ลง audit chain | ตรวจย้อนไม่ได้ | **P2** |

---

## 3. ข้อเสนอฟีเจอร์ (P0/P1/P2 + ประมาณการ)

> ประมาณการเป็นวันทำงานของ dev 1 คน (รวมเทสต์ + doc) · ทุกข้อแตะเงิน/ภาษี/สิทธิ์ ⇒ ต้องผ่านฝ่ายค้าน 1 รอบ (F3 ข้อ 11)

### 3.1 หลักออกแบบ (ตัวตั้งตัวเดียว)

1. **ทุกบาทที่เข้า NextAcc = `PaymentIntent` ใน tenant ผู้ให้บริการ (`PlatformCompanyId`)** — ไม่ใช่ใน tenant ผู้เช่า · ใช้ `PaymentProviderConfig` ของ tenant ผู้ให้บริการ
   (Omise ของ NextAcc) ตาม PAYMENT_GATEWAY_DESIGN §8 ข้อ 3 · ผู้เช่าเริ่ม intent ผ่าน endpoint ใหม่ `api/companies/{cid}/platform-billing/…` ที่ **เซิร์ฟเวอร์กำหนดยอด** เสมอ
2. **ทุกการเรียกเก็บ = ใบแจ้งหนี้ใน tenant ผู้ให้บริการก่อน แล้วจ่าย "ใบนั้น"** — ใช้ `PaymentSourceKind.Document` + `DocumentPaymentHandler` ที่มีอยู่
   (ตัดลูกหนี้ใบเดิม ไม่ออกขายสดใบใหม่ ⇒ ปิด G-10) · ขายสดใช้เฉพาะการจ่ายทันทีที่ไม่มีใบแจ้งหนี้มาก่อน
3. **สิทธิ์ตามหลังเงิน ผ่านตัวตัดสินเดียว** `Helpers/PlatformEntitlementPolicy` (pure) — "ใบ X ชำระแล้ว ⇒ ให้สิทธิ์ Y" (ต่ออายุ/เปิด add-on/เติมเครดิต/เครดิต OCR) ·
   handler ใหม่ `PlatformInvoicePaidHandler` เรียกหลัง `DocumentPaymentHandler` สำเร็จ (ผูกด้วยตาราง `PlatformCharge`)
4. **ยืนยันด้วยมือบนเงินแพลตฟอร์ม = เฉพาะแอดมินแพลตฟอร์ม** (`SystemAdmin` ของ tenant ผู้ให้บริการ) — ผู้เช่าไม่มีทางกด

### 3.2 รายการ

| P | ฟีเจอร์ | สาระ | วัน |
| --- | --- | --- | --- |
| **P0** | ปิดรูยืนยันเอง (G-2/G-3) | `ConfirmManually`/`Redispatch`/`Create` ปฏิเสธ `SourceKind ∈ {SubscriptionPayment, AddOnPurchase}` จากทุก tenant ที่ไม่ใช่ tenant ผู้ให้บริการ · `SubscriptionPaymentHandler` กรองบริษัท + ปฏิเสธ intent ที่ไม่ได้อยู่ใน tenant ผู้ให้บริการ · เทสต์ทิศตรงข้าม: tenant ผู้ให้บริการยังยืนยันได้ | 1–1.5 |
| **P0** | ราคาจากเซิร์ฟเวอร์ (G-4) | `SubmitPaymentAsync` คำนวณยอดจาก `PlanTemplate × รอบ` (ตัวตั้งเดียวกับ Convert) · `RequestedPeriodMonths ∈ {1,3,6,12}` · `RequireOwner`/BillingManage · ยอดโอนจริงต่างจากยอดที่ต้องจ่าย = แอดมินต้องตัดสิน (ไม่อนุมัติอัตโนมัติ) | 1 |
| **P0** | อัปเกรด = ใบแจ้งหนี้ + จ่ายก่อน (G-0) | `convert`/`plan` สร้าง "คำสั่งอัปเกรด" + ใบแจ้งหนี้ใน tenant ผู้ให้บริการ → จ่าย → `PlatformEntitlementPolicy` เปลี่ยนแพ็กเกจ · ทางเลือกนโยบาย (ต้องให้เจ้าของตัดสิน): (ก) จ่ายก่อนใช้ หรือ (ข) ใช้ได้ทันทีพร้อม grace N วัน แล้ว revert ถ้าไม่จ่าย · ดาวน์เกรดมีผลรอบถัดไป · **migration**: ไล่ sub ที่ Convert โดยไม่มี `SubscriptionPayment` Approved → รายงานให้แอดมินตัดสิน (ไม่ revert อัตโนมัติ) | 3–4 |
| **P0** | gateway ของแพลตฟอร์ม (G-1) | `PlatformPaymentService.StartAsync(buyerCompanyId, chargeId)` สร้าง intent ใน `PlatformCompanyId` · `addons.html`/หน้าแพ็กเกจเลิกถาม `public-pay/methods` ของผู้เช่า ถามของแพลตฟอร์มแทน · ไม่ตั้ง `PlatformCompanyId` = ซ่อนปุ่มออนไลน์ (สลิปอย่างเดียว) · migration: ไล่ intent `AddOnPurchase/SubscriptionPayment` เดิมที่อยู่ใน tenant ผู้เช่า → รายงาน | 2–3 |
| **P0** | Prepaid ที่ไม่มีทางเติม (G-6) | ตัดสินนโยบาย: เปลี่ยนค่าตั้งต้นเป็น `Postpaid` + migration แถวที่ backfill (`CreatedBy='migration:billing-account-backfill'` และ `CreditBalance<=0`) → Postpaid **และ** ออกใบย้อนหลังของ UsageEvent ที่ถูกปิดรอบแบบ prepaid (ต้องให้เจ้าของตัดสินว่าจะเก็บย้อนหลังไหม) · หรือทำ "เติมเครดิต" จริง (P1 ด้านล่าง) | 1.5 (+ย้อนหลัง 1) |
| **P1** | ศูนย์บิลของผู้เช่า (Billing Center) | หน้าเดียว `pages/billing.html`: ยอดค้าง · ใบแจ้งหนี้ทั้งหมด (ต่ออายุ/ค่าใช้งาน/add-on) ดึงจากเอกสารใน tenant ผู้ให้บริการที่ `ContactId` = บริษัทนี้ · "ชำระเลย" (QR PromptPay/บัตร ผ่าน Omise ของแพลตฟอร์ม · หรือแนบสลิป) · ประวัติการชำระ · ดาวน์โหลดใบกำกับ/ใบเสร็จ/50 ทวิที่เราต้องได้ · ช่องกรอก WHT ที่ลูกค้าหัก (แนบ 50 ทวิ) | 4–5 |
| **P1** | ส่งใบ + ทวงหนี้ (dunning) แพลตฟอร์ม | อีเมล/LINE ตอนออกใบ · D-3/D0/D+3/D+7 · grace (`GracePeriodDays` ที่มีอยู่แต่ไม่มีผู้อ่าน) → จำกัดการเขียน (ใช้ `SubscriptionMiddleware` 402 ที่มีอยู่) → ระงับ · job ต้องผ่าน `Helpers/JobLock` + watermark ต่อบริษัท | 3 |
| **P1** | add-on: เลิกเก็บซ้อน (G-5/G-11) | เลือกทางเดียว: **(แนะนำ)** ไม่เก็บล่วงหน้า — ค่าเหมาเข้าใบแจ้งหนี้รายเดือนอย่างเดียว + ใช้ dunning คุม · หรือเก็บล่วงหน้าแล้วให้ `FlatAmount` เป็น 0 ในงวดที่จ่ายแล้ว · ต่อสาย/ลบ `UsableWhilePending` · migration: หา add-on ที่จ่ายล่วงหน้าและถูกออกค่าเหมางวดเดียวกัน → ใบลดหนี้ | 2 |
| **P1** | เอกสารสำหรับเงินที่รับแล้วทุกเส้น (G-9/G-10) | add-on/OCR/เติมเครดิต → ใบกำกับ/ใบเสร็จผ่าน `PlatformBillingDocumentIssuer` · ใบแจ้งหนี้ต่ออายุที่จ่ายแล้ว → **รับชำระใบเดิม** (ไม่ใช่ขายสดใหม่) + migration กระทบลูกหนี้ค้างในสมุดของ NextAcc | 2–3 |
| **P1** | OCR credit ให้ครบวง (G-12) | ย้ายเข้าเส้นเดียวกับ `documents.topup` (UsageEvent + ใบแจ้งหนี้) หรือจ่ายก่อนผ่าน Billing Center · ปุ่มซื้อในหน้าสแกน · เลิกนับรายได้จาก `AmountPaid` ที่ยังไม่จ่าย | 1.5 |
| **P1** | ช่องทางโอนของแพลตฟอร์ม + ตรวจสลิป (G-13) | `SiteSettings.PlatformBankAccounts` + PromptPay ID (แสดงบน Billing Center/ใบแจ้งหนี้) · สลิปใช้ `SlipOcrAssistService` เดิม (เทียบยอด/วันที่/บัญชีปลายทาง) · กันสลิปซ้ำด้วย ref | 1.5 |
| **P1** | Manual payment link | ดู §4 | 2–3 |
| **P2** | ตัดบัตรอัตโนมัติ (auto-charge) | Omise Customer + card token เก็บใน tenant ผู้ให้บริการ (ไม่เก็บเลขบัตร) · ต่ออายุ/ใบค่าใช้งานตัดอัตโนมัติวันครบกำหนด · ล้ม → dunning · ต้องมี consent ของผู้ถือบัตร (PDPA `ConsentRecord`) · 3DS ครั้งแรก | 4–5 |
| **P2** | Proration | อัปเกรดกลางรอบ = เก็บส่วนต่างตามวัน (pure `PlatformProration`) · ดาวน์เกรด = เครดิตเข้ารอบหน้า (ไม่คืนเงินสด) | 2 |
| **P2** | คูปอง/เครดิตบัญชี | ย้าย promo จาก `TrialConfig.ConversionPromoCode` ไปตาราง `PlatformCoupon` (ใช้ได้กี่ครั้ง/หมดอายุ/แพ็กเกจไหน) · เครดิตบัญชี (ledger) ใช้ร่วมกับ Prepaid | 2–3 |
| **P2** | ใบลดหนี้/คืนเงินแพลตฟอร์ม §86/10 | ยกเลิก/ปฏิเสธหลังออกใบ/ดาวน์เกรด → CN ใน tenant ผู้ให้บริการ (ด่าน SUM(CN) ≤ ใบเดิม ที่มีอยู่) · คืนผ่าน Omise ของแพลตฟอร์ม | 2 |
| **P2** | รายงานแอดมิน | MRR จากใบที่ **ชำระจริง** · AR aging ของผู้เช่า · failed payments · ลูกค้าใช้เกินแต่ไม่จ่าย · churn · ส่วนใหญ่ได้จากรายงานลูกหนี้ของ tenant ผู้ให้บริการอยู่แล้ว — ทำหน้ารวมใน admin | 2 |
| **P2** | ห้ามออก "ใบกำกับภาษี" ในโหมด PDF สำรอง (G-15) | บังคับตั้ง `PlatformCompanyId` เมื่อ `PlatformIsVatRegistered=true` | 0.5 |
| **P2** | เตือนฟีเจอร์ขายที่ยังไม่มีราคา (G-16) | หน้า admin-addons แดงเมื่อ meter/top-up ไม่มี `ApiPricingPlan` | 0.5 |

---

## 4. Manual payment link — ควรมีไหม

**คำแนะนำ: มี (P1)** — แต่เป็น "ลิงก์จ่ายใบแจ้งหนี้" ไม่ใช่ "ลิงก์ยอดอิสระ"

**ข้อดี**
- ลูกค้า B2B ส่วนใหญ่ให้ฝ่ายบัญชี/ผู้อนุมัติที่ **ไม่มีบัญชีในระบบเรา** เป็นคนจ่าย — ส่งลิงก์ทางอีเมล/LINE แล้วจ่ายได้เลย
- ฝ่ายขาย/support ปิดดีลพิเศษ (ราคาตามสัญญา · ค่าติดตั้ง · ย้ายข้อมูล) ได้โดยไม่ต้องรอฟีเจอร์ในหน้าแพ็กเกจ
- ใช้ซ้ำกับ dunning (ทุกอีเมลทวงหนี้แนบลิงก์) — เพิ่มอัตราเก็บเงินได้ชัดที่สุดในบรรดาข้อเสนอทั้งหมด

**ข้อเสีย/ความเสี่ยง**
- phishing: ลิงก์จ่ายเงินที่มีแบรนด์เราถูกปลอมได้ ⇒ โดเมนเดียวคงที่ + ไม่ขอรหัสผ่าน/ข้อมูลบัตรนอก Omise + หน้าแสดงเลขใบที่ตรวจในศูนย์บิลได้
- ลิงก์หลุด = เปิดเผยชื่อบริษัท/ยอด ⇒ แสดงข้อมูลขั้นต่ำ (ชื่อผู้ซื้อ · เลขใบ · ยอด · รายการย่อ) ไม่มีที่อยู่/เลขภาษีเต็ม
- ลิงก์ยอดอิสระ = ทางออกเงินที่ไม่มีเอกสาร ⇒ **ห้าม** (ต้องมีใบแจ้งหนี้ก่อนเสมอ — แอดมินกด "เรียกเก็บพิเศษ" = สร้างใบแจ้งหนี้ + ลิงก์ในคำสั่งเดียว)

**ออกแบบ**
- ตาราง `PlatformPaymentLink(Id, PlatformCompanyId, BuyerCompanyId, DocumentId, TokenHash, AmountSnapshot, ExpiresAt, UsedAt, RevokedAt, CreatedBy, CreatedAt)` ·
  token 128-bit แบบสุ่ม เก็บเฉพาะ SHA-256 (canonical function เดียว) · อายุตั้งต้น 14 วัน · ปิดเองเมื่อใบถูกชำระครบ/ยกเลิก (single-use เชิงธุรกิจ) · เพิกถอนได้ · rate-limit ต่อ IP (pattern `ChatRateLimiter`)
- หน้า public `/pay/l/{token}` (`[AllowAnonymous]` controller ใหม่ แยกจาก `PublicPaymentController` ที่เป็นของผู้เช่า) → intent `SourceKind=Document`, `SourceId=DocumentId`
  ใน **tenant ผู้ให้บริการ** · ยอด = ยอดค้างของใบ ณ ตอนกด (เซิร์ฟเวอร์คำนวณ) · วิธีจ่าย: PromptPay QR / บัตร (Omise ของแพลตฟอร์ม) / แนบสลิป (รอแอดมินแพลตฟอร์มตรวจ)
- เงินเข้า → `DocumentPaymentHandler` (ตัดลูกหนี้ใบเดิม · ออกใบเสร็จ/ใบกำกับ) → `PlatformInvoicePaidHandler` → `PlatformEntitlementPolicy` (ต่ออายุ/เปิดสิทธิ์)
- audit chain ทุกการสร้าง/เปิดดู/เพิกถอน/ชำระ · ลิงก์ที่หมดอายุแสดงทางไปต่อ ("ขอลิงก์ใหม่" / เข้าศูนย์บิล)
- ใช้ร่วม: ปุ่ม "คัดลอกลิงก์จ่าย" ในหน้าแอดมินลูกค้า · ใส่ในอีเมลใบแจ้งหนี้และ dunning ทุกฉบับ

**ข้อกฎหมาย:** NextAcc รับเงินค่าบริการ **ของตัวเอง** เข้าบัญชี Omise ของตัวเอง ⇒ ไม่ใช่การรับชำระแทน (ไม่ติด พ.ร.บ.ระบบการชำระเงิน 2560 ที่ §8 ข้อ 1 ของ PAYMENT_GATEWAY_DESIGN กังวล)

---

## 5. แผนแบ่งเฟส

| เฟส | เนื้องาน | ID ที่ปิด | วัน |
| --- | --- | --- | --- |
| **เฟส 0 — อุดรั่ว (ทำทันที)** | ปิด self-confirm + handler กรองบริษัท · ราคาจากเซิร์ฟเวอร์ · ซ่อนปุ่ม "ชำระออนไลน์" ของ add-on จนกว่าจะมีเฟส 1 · ตัดสินนโยบาย Prepaid (เปลี่ยนเป็น Postpaid) · รายงานรายชื่อ sub ที่ convert โดยไม่จ่าย | G-1(ชั่วคราว) G-2 G-3 G-4 G-6 | 4–5 |
| **เฟส 1 — เงินเข้าถูกกระเป๋า + จ่ายก่อนได้สิทธิ์** | `PlatformPaymentService` (intent ใน tenant ผู้ให้บริการ) · อัปเกรด/ต่ออายุ = ใบแจ้งหนี้ → จ่าย → สิทธิ์ · ช่องทางโอน/PromptPay ของแพลตฟอร์ม · ใบเอกสารทุกเส้นที่รับเงิน · แก้รายได้ซ้ำ | G-0 G-1 G-9 G-10 G-13 | 8–10 |
| **เฟส 2 — เก็บเงินได้จริง** | Billing Center · manual payment link · อีเมลใบแจ้งหนี้ + dunning + grace + ระงับ · add-on เลิกเก็บซ้อน · OCR credit ครบวง | G-5 G-7 G-8 G-11 G-12 | 12–15 |
| **เฟส 3 — ลดงานคน + ความครบถ้วนเชิงภาษี** | auto-charge บัตร · proration · คูปอง/เครดิต · ใบลดหนี้/คืนเงิน · รายงาน MRR(จ่ายจริง)/AR aging/failed payments · บังคับตั้ง tenant ผู้ให้บริการเมื่อจด VAT | G-14 G-15 G-16 G-17 | 12–15 |

**คำถามที่เจ้าของต้องตัดสินก่อนเริ่มเฟส 1**
1. อัปเกรด: จ่ายก่อนใช้ หรือใช้ได้ทันทีพร้อม grace (กี่วัน)?
2. ลูกค้าที่ convert โดยไม่จ่ายไปแล้ว: เก็บย้อนหลัง / ให้ฟรีถึงสิ้นรอบ / revert?
3. แพลนกลุ่ม (BillingAccount): Postpaid เป็นค่าตั้งต้น? ค่าใช้งานที่ถูกปิดรอบแบบ prepaid ไปแล้ว ออกใบย้อนหลังไหม?
4. add-on เหมารายเดือน: เก็บล่วงหน้า หรือรวมในใบรายเดือนอย่างเดียว (แนะนำอย่างหลัง)?
5. Omise ของ NextAcc พร้อม (คีย์ live · PromptPay เปิดแล้ว) และตั้ง `PlatformCompanyId` บน production แล้วหรือยัง?

---

## 6. ข้อจำกัดของการตรวจรอบนี้

- อ่านโค้ดอย่างเดียว ไม่ได้รัน/คอมไพล์ · ข้อ G-2: Owner/SystemAdmin ผ่านทุกสิทธิ์ (`PermissionService.cs:23`) ⇒ ถือ `BankReconcile` เสมอ · ควร reproduce เป็นเทสต์ก่อนแก้ (กฎเหล็ก #4 G)
- ไม่ได้ตรวจ e-Tax/ภ.พ.30 ของ tenant ผู้ให้บริการเชิงลึก (อยู่ในเส้นเอกสารปกติที่ตรวจไว้ในรอบอื่น)
- ไม่ได้ตรวจข้อมูล production — ขนาดผลกระทบของ G-0/G-6 ต้องดูจาก DB จริง (จำนวน sub ที่ Active โดยไม่มี payment Approved · ยอด `CreditBalance` ติดลบรวม)
