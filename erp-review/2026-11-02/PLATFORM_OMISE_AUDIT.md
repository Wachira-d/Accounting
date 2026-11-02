# PLATFORM_OMISE_AUDIT — Omise ฝั่งแอดมินแพลตฟอร์ม (NextAcc เก็บเงินผู้เช่า) ครบ/ถูกต้องหรือยัง (รอบ 227)

> คำถามเจ้าของ (2026-11-02): "ตรวจสอบอย่างละเอียดอีกครั้ง ส่วนของ admin ระบบ — ออกแบบ พัฒนาการเชื่อมต่อกับ Omise ครบถ้วน ถูกต้องทุกส่วนแล้วรึยัง
> มีฟีเจอร์ปรับตั้งค่าต่างๆ ละเอียดครบถ้วนแล้วรึยัง รวมถึงการเชื่อมโยงข้อมูลที่เกี่ยวข้องทั้งหมด"
>
> ขอบเขต: Omise ของ **NextAcc** ที่เก็บค่าแพ็กเกจ/ส่วนเสริม/ใบแจ้งหนี้ (ศูนย์บิล · ลิงก์ชำระ) จากผู้เช่า (คำตัดสินข้อ 178–180) — ไม่ใช่ Omise ของร้าน
> (ร้านใช้ adapter/บริการตัวเดียวกัน ⇒ แก้ใน adapter/บริการกลางได้ผลทั้งสองฝั่ง ระบุไว้ในแต่ละข้อ) · ต่อจาก `erp-review/2026-10-30/PLATFORM_BILLING_AUDIT.md` ·
> `PLATFORM_GATEWAY_REFUND_POLICY_DESIGN.md` · ACCOUNT_STRUCTURE §6.2b/§6.2c · PAYMENT_GATEWAY_DESIGN.md
>
> สถานะ: ✅ ถูก/ครบ · ⚠️ ใช้ได้แต่มีช่องโหว่/ต้องยืนยัน · ❌ ผิด/ขาด · **(227)** = แก้ในรอบนี้ · **ยังไม่ได้คอมไพล์ในเครื่องนี้** (ไม่มี .NET SDK — CI เป็นตัวคอมไพล์แรก)

## 0. คำตอบสั้น

- **โครงหลักถูกต้อง**: คีย์คู่ test/live เข้ารหัส · ปักรุ่น API · Basic auth · สตางค์ปัดครึ่งขึ้น · webhook ยืนยันด้วยการ fetch event กลับ (ไม่เชื่อ body) +
  รหัสลับใน URL ต่อ config · ด่านเปิดรับเงินจริง (ทดสอบผ่าน + คีย์ live ตรวจแล้ว + นโยบายคืนเงิน + พิมพ์ยืนยัน) · ปิดฉุกเฉิน · ทุกเส้นเงินผ่าน `PaymentIntent` + `IPaymentProvider`
  (ไม่พบทางลัด) · งานพนักงานบนเงินแพลตฟอร์ม = SystemAdmin ใน tenant ผู้ให้บริการเท่านั้น
- **ไม่ครบ — พบ 4 เรื่องที่ทำให้เงิน/บัญชี/สิทธิ์ผิดจริง (แก้แล้วรอบนี้):**
  1. ❌ **เงินออนไลน์ค่าบริการลง Dr ธนาคาร** แทนบัญชีพักผู้ให้บริการรับชำระ (11340) ⇒ รอบโอนของบริษัทผู้ให้บริการ Cr 11340 ที่ไม่เคยถูก Dr ⇒ บัญชีพักติดลบ + ธนาคารเกินจริงสองเท่า (C-2)
  2. ❌ **ค่าส่วนเสริมที่จ่ายล่วงหน้า (ออนไลน์/สลิปที่อนุมัติ) ไม่มีใบกำกับภาษี/ใบเสร็จเลย** ⇒ รายได้ไม่ลง GL · ไม่เข้ารายงานภาษีขาย (§86/4 · §78/1) — ค้างจาก G-9 รอบ 219 (C-3)
  3. ❌ **สลับโหมด/ปิดฉุกเฉินขณะลูกค้าเพิ่งสแกน QR จริง ⇒ เงินเข้าแต่ระบบไม่รู้**: poll/ตรวจคืนเงินใช้คีย์ของโหมดปัจจุบัน · webhook ของอีกโหมดถูกทิ้ง (A-12)
  4. ❌ **ผู้เช่าเห็นวิธีชำระที่หน้าชำระทำไม่จบ**: บัตร (ไม่มีช่องกรอกบัตร/โทเคนในเบราว์เซอร์ — เซิร์ฟเวอร์โยน "ไม่พบโทเคนบัตร" ทุกครั้ง) · วอลเล็ท (ไม่ส่งเบอร์) ·
     แบงก์กิ้ง (ไม่รับกลับ) · ค่าตั้ง "ไม่ติ๊ก = ทุกวิธี" ⇒ ศูนย์บิลหยิบวิธีแรกของรายการ (A-3/A-4/B-2)
- **ตั้งค่าที่ขาด (เพิ่มแล้ว):** รายการตรวจความพร้อมการเชื่อมโยงบัญชี · ป้าย/เหตุผลต่อวิธีชำระ · วิธีที่ผู้เช่าเห็นจริง · ตรวจชื่อวิธีตอนบันทึก + audit ค่าก่อนแก้ ·
  ด่าน live ต้องตั้ง "URL ของระบบ" (ค่าที่มากับซอร์ส app.nextacc.com ไม่นับ — ยืนยันการแก้รอบ 221 ว่า **ยังพังได้ถ้าแอดมินไม่ตั้งค่า**) · รายการรับชำระค่าบริการล่าสุด (เทียบ Omise ↔ ระบบ)
- **ไม่สร้างในรอบนี้ (ออกแบบ + ข้อเสนอ §5):** ช่องกรอกบัตร + 3DS · ตัดบัตรอัตโนมัติ · ดึงรอบโอน (transfers) จาก Omise · ข้อพิพาท/chargeback · ใบลดหนี้คืนเงินอัตโนมัติ · proration · Idempotency-Key ของ Omise

## 1. ตารางตรวจ

### A. ความถูกต้องของการต่อ Omise (adapter `Services/Payments/Providers/OmisePaymentProvider.cs`)

| # | รายการ | สถานะ | หลักฐาน | แก้/ข้อเสนอ |
| --- | --- | --- | --- | --- |
| A-1 | ปักรุ่น API (`Omise-Version: 2019-05-29`) ทุกคำขอ · Basic auth (secret key + ":") | ✅ | `OmisePaymentProvider.cs:47,129-140` | — |
| A-2 | ยอดเป็นสตางค์ (`Math.Round(…, AwayFromZero)`) · สกุลเงิน `thb` · metadata `intentId`/`companyId` | ✅ | `:167-168,256-263` | — |
| A-3 | บัตร: โทเคนจาก Omise.js (ไม่มีเลขบัตรในเซิร์ฟเวอร์ — boundary check) · `return_uri` 3DS | ⚠️ เซิร์ฟเวอร์พร้อม แต่ **ไม่มีหน้าเว็บใดโหลด omise.js / ขอ public key** (`ClientScriptUrl` ไม่มีผู้อ่าน) ⇒ บัตรล้มทุกครั้ง | `:54,126,266-274` · `js/pay-widget.js` ไม่มีช่องบัตร | **(227)** ไม่ให้ผู้เช่าเห็นบัตรจนกว่าจะมีช่องกรอก (`Helpers/PlatformPayMethods`) · ดีไซน์ §5.1 |
| A-4 | PromptPay source · TrueMoney/Mobile/Internet banking | ⚠️ PromptPay ✅ · TrueMoney ไม่ส่ง `source[phone_number]` (ถูกปฏิเสธทุกครั้ง) · mobile = KBank เท่านั้น · internet = BAY เท่านั้น · ทั้งคู่เป็น redirect ที่หน้าเดิมไม่รับกลับ | `:143-150` | **(227)** ส่งเบอร์เมื่อมี (`:278-280`) · ไม่เปิดให้ผู้เช่า (`PlatformPayMethods`) · ดีไซน์ §5.1 |
| A-5 | ยืนยัน webhook: fetch `GET /events/{id}` ด้วยคีย์ของเรา · รูปเลข event กัน path injection · เชื่อเฉพาะข้อมูลที่ fetch | ✅ | `:353-418` · `PaymentGatewayController.cs` `Receive` | — |
| A-6 | ชนิด event: charge.* ผ่าน · refund/dispute ถูกข้าม (`object != "charge"`) | ⚠️ คืนเงินจากแดชบอร์ด Omise รู้ได้ทาง charge `reversed`/`refunded_amount` เท่านั้น · ข้อพิพาทไม่มีเส้น | `:399` | ดีไซน์ §5.4 (disputes) |
| A-7 | วันหมดอายุ QR | ❌ อ่าน `source.expires_at` ซึ่งไม่มี ⇒ `QrExpiresAt` null เสมอ (หน้าจ่ายไม่รู้เวลาหมด · หมดอายุเฉพาะเมื่อ Omise ตอบ expired) | `:176-189` | **(227)** อ่าน `expires_at` ระดับ charge เมื่อมี source (`:182-185`) · บัตรไม่ประทับ · เทสต์สองทิศ |
| A-8 | webhook ซ้ำ/มาไม่เรียง | ✅ เครื่องสถานะเดียว + advisory lock ต่อ intent · ซ้ำ = no-op · Failed/Expired → Succeeded ได้ (เงินเข้าช้า) · สถานะสุดท้ายไม่ถอย | `PaymentIntentService.ApplyChargeAsync` · `Helpers/PaymentIntentPolicy.Evaluate` | — |
| A-9 | poll สำรอง | ✅ job ทุก 5 นาที (`FOR UPDATE SKIP LOCKED`) + หน้าจ่าย poll ถอยห่าง · ค้าง > 30 นาทีโหมดจริงแจ้งครั้งเดียว | `Services/Background/PaymentIntentReconcileJob.cs` · `js/pay-widget.js` | — |
| A-10 | ข้อความล้มเหลวเป็นไทย | ❌ เก็บ `failure_message` ภาษาอังกฤษตรง ๆ · error API รู้จัก 4 รหัส | `:206-207,480-487` (ก่อนแก้) | **(227)** `Providers/OmiseFailureText` ตัวเดียว (บัตร + API) · ไม่รู้จัก = ข้อความต้นทาง+รหัส (ไม่เดา) |
| A-11 | timeout / retry | ✅ named client 20 วิ · สร้าง charge ล้ม = intent Failed (ลองใหม่ได้ · webhook ทีหลังพา Failed→Succeeded ถ้า Omise สร้างจริง) · คืนเงิน 5xx/408 = ผลไม่แน่ชัด (ล็อก) | `Program.cs:371-372` · `GatewayRefundService` | ⚠️ ไม่ส่ง `Idempotency-Key` ของ Omise ⇒ ดู §5.6 |
| A-12 | คีย์ตามโหมดของรายการ (สลับโหมด/ปิดฉุกเฉินขณะมีรายการค้าง) | ❌ adapter เลือกคีย์จาก `config.Mode` ⇒ poll/คืนเงิน/ตรวจผลคืนของรายการโหมดอื่น = 404 · webhook ของอีกแดชบอร์ด (URL เดียวกัน) ถูกทิ้ง | `PaymentIntentService.RefreshAsync` · `GatewayRefundService` · webhook | **(227)** `Services/Payments/PaymentConfigForIntent` (โหมดที่ประทับบน intent ชนะ · null = เดิม) ใช้ใน poll/สั่งคืน/ตรวจผล · webhook เส้นรหัสลับลองคีย์อีกโหมดเมื่อโหมดปัจจุบันไม่ผ่าน · `LastLiveWebhookAt` ตามโหมดที่ยืนยันได้จริง · ด่าน `PaymentLiveModeGuard` ยังตัดสินซ้ำ · ผลกับร้านด้วย |
| A-13 | แยกคีย์ test/live (`pkey_test_`/`skey_test_`) ต่อช่อง + `livemode` ของบัญชี/charge | ✅ | `Providers/OmiseKeyFormat.cs` · `TestConnectionAsync` · `PaymentLiveModeGuard` | — |
| A-14 | เก็บคีย์เข้ารหัส · แสดง 4 ตัวท้าย · เปลี่ยนคีย์ล้างผลทดสอบ · เปลี่ยนคีย์ live ขณะ live = ถอยโหมดทดสอบ | ✅ | `PaymentProviderConfigService.ApplyKeys` · `AdminPlatformGatewayController.Hint` | — |
| A-15 | ด่านเปิด live: ทดสอบผ่าน+webhook จริง · คีย์ live ตรวจ · นโยบายคืนเงินเผยแพร่ · พิมพ์ยืนยัน | ⚠️ ไม่ตรวจ "URL ของระบบ" — ไม่ตั้ง ⇒ ผู้จ่ายเด้งไป app.nextacc.com (ค่าใน appsettings) **การแก้รอบ 221 พึ่งให้แอดมินตั้ง AppBaseUrl** · ไม่ตรวจว่ามีวิธีที่ทำจบได้ | `Helpers/PaymentLiveGate.cs` · `SystemBaseUrl.cs` · `appsettings.json:73` | **(227)** ด่านเพิ่ม 2 ข้อ (`SystemBaseUrl.IsConfigured` — ค่าที่มากับซอร์สไม่นับ · `payableMethodCount`) |
| A-16 | ค่าธรรมเนียม + VAT ค่าธรรมเนียม | ⚠️ อ่าน `fee` (ก่อน VAT) · `fee_vat` ยังไม่ต่อสาย (ต้องผล sandbox — notice เดิมบนหน้า) · ค่าธรรมเนียม/VAT ลงตอน "บันทึกรอบโอน" ในบริษัทผู้ให้บริการ (`GatewayFeeVatMode`) | `:56-63,201` · `GatewaySettlementService` | คงเดิม — ยืนยัน sandbox (PAYMENT_GATEWAY_DESIGN §4.4) |
| A-17 | รอบโอน (transfers) ของ Omise | ⚠️ บันทึกรอบโอนด้วยมือ/นำเข้าไฟล์ในบริษัทผู้ให้บริการ (ด่านยอดไม่ตรง = บล็อก) · ไม่ดึง `/transfers` อัตโนมัติ | `GatewaySettlementService` · `Settlement/Adapters/PaymentIntentAdapter.cs` | ดีไซน์ §5.3 |
| A-18 | คืนเงิน | ✅ ตามคำตัดสิน "ไม่คืนเงินค่าบริการ" แต่แอดมิน (SystemAdmin ใน tenant ผู้ให้บริการ) คืนด้วยมือได้ผ่าน `GatewayRefundService` (ผลไม่แน่ชัด = ล็อก) · ⚠️ ไม่ออกใบลดหนี้ §86/10 อัตโนมัติ | `PaymentGatewayController.cs:421` · ACCOUNT_STRUCTURE §6.2b | ดีไซน์ §5.5 |
| A-19 | ข้อพิพาท/chargeback | ❌ ไม่มีเส้น (event ถูกข้าม · ไม่มีสถานะ/แจ้งเตือน/พักสิทธิ์) | `:399` | ดีไซน์ §5.4 |

### B. ตั้งค่าในหน้าแอดมิน (`/admin/platform-gateway.html` · `AdminPlatformGatewayController`)

| # | รายการ | สถานะ | หลักฐาน | แก้/ข้อเสนอ |
| --- | --- | --- | --- | --- |
| B-1 | คีย์ test/live · ชื่อที่แสดง · ทดสอบสองชุด · ทดลองจ่าย ฿20 · URL webhook + หมุนรหัส · เปิด live/ถอยกลับ · ปิดฉุกเฉิน (บังคับเหตุผล) · หน้าทดลองจ่าย Off/AdminsOnly/Public | ✅ | controller ทั้งไฟล์ | — |
| B-2 | วิธีชำระที่เปิด | ❌ ชื่อ enum ล้วน · ไม่บอกว่าวิธีไหนหน้าชำระทำจบได้ · ไม่ติ๊ก = ทุกวิธี (รวมบัตร) | `platform-gateway.html:64` (ก่อนแก้) · `PlatformPaymentService.MethodsOf` | **(227)** ป้ายไทย + "ใช้ได้/ยังไม่เปิดให้ผู้เช่า — เหตุผล" + "ผู้เช่าเห็นตอนนี้" จากเซิร์ฟเวอร์ · ผู้เช่าเห็น = ติ๊ก ∩ รองรับ ∩ ทำจบได้ |
| B-3 | ตรวจค่าที่บันทึก + audit | ⚠️ เก็บชื่อวิธีอะไรก็ได้ · audit มีแต่ "ช่องไหนเปลี่ยน" ไม่มีค่าเดิม | `Save` | **(227)** ชื่อวิธีที่ผู้ให้บริการไม่รองรับ = 400 · audit `OldValues` (ชื่อ · วิธี · คีย์สาธารณะ · โหมด) |
| B-4 | ด่าน live ครบ | ⚠️ ดู A-15 | — | **(227)** |
| B-5 | รายการตรวจความพร้อม (บัญชี/URL/ภาษี/วิธีโอนสำรอง) | ❌ ไม่มี — แอดมินไม่รู้ว่าขาดผัง 11340/54710/รายได้/ธนาคาร | — | **(227)** การ์ด "ความพร้อมการเชื่อมโยงข้อมูล/บัญชี" (`Helpers/PlatformGatewayReadiness` · ข้อที่ด่านใช้ = ✗ · เตือน = ⚠️) |
| B-6 | ยอดขั้นต่ำ/สูงสุด · statement descriptor | — ไม่ต้องมี: ยอดมาจากเซิร์ฟเวอร์เสมอ (ใบแจ้งหนี้/ราคาแพ็กเกจ) · descriptor ตั้งที่บัญชี Omise | — | ไม่ทำ (ไม่มีผู้อ่าน = ค่าตั้งที่ไม่มีผล — F2 ข้อ 2) |
| B-7 | ผลิตภัณฑ์ที่ชำระออนไลน์ได้ | ✅ ค่าแพ็กเกจ · ส่วนเสริม (จ่ายล่วงหน้า) · ใบแจ้งหนี้ทุกชนิดผ่านศูนย์บิล/ลิงก์ชำระ (ต่ออายุ · ค่าใช้งาน = แพ็ก LINE/โควตา/เกินโควตา) — ตัวตัดสิน `PlatformRevenueSource` · ไม่มีปุ่มแยกต่อผลิตภัณฑ์ (ปิดทั้งช่องทาง = ปิดฉุกเฉิน) | `Helpers/PlatformRevenueSource.cs` · `BillingCenterController.PrepareOnline` · `PlatformPayLinkController` | ไม่ทำ — ความยืดหยุ่นต่อผลิตภัณฑ์ไม่มีความต้องการจริง (เส้นโอน+สลิปเปิดเสมอ) |
| B-8 | ใบเสร็จ/ใบกำกับเมื่อรับเงิน · อีเมลแจ้ง | ⚠️ ค่าแพ็กเกจ/ใบแจ้งหนี้ ✅ (ใบเสร็จจริง + อีเมล) · **ส่วนเสริม ❌ ไม่มีทั้งใบและอีเมลเมื่อจ่ายออนไลน์** | `SaasBillingDocumentService` · `AddOnPurchaseService` | **(227)** ดู C-3 |
| B-9 | ค่าตั้งการเรียกเก็บ (ใบต่ออายุล่วงหน้า · เตือน · ผ่อนผัน · ระงับ · อายุลิงก์ · อีเมล/LINE) | ✅ | `/admin/platform-billing.html` · `PlatformBillingSettingsService` (audit chain) | — |
| B-10 | ตัดบัตรอัตโนมัติ (บัตรที่บันทึกไว้) | ❌ ไม่มี | — | ดีไซน์ §5.2 |

### C. การเชื่อมโยงข้อมูล (เงิน → สิทธิ์ → เอกสาร → บัญชี → ผู้เช่า)

| # | รายการ | สถานะ | หลักฐาน | แก้/ข้อเสนอ |
| --- | --- | --- | --- | --- |
| C-1 | เงินเข้า → อนุมัติรายการชำระ/ตัดชำระใบ/ต่ออายุ · ส่วนเสริมเปิดสิทธิ์ · ยอดไม่ตรง = พักให้แอดมิน · จ่ายซ้อน = ล้มดัง | ✅ | `Handlers/SubscriptionPaymentHandler.cs` · `Handlers/AddOnPurchasePaymentHandler.cs` · `PlatformDoublePayment` | — |
| C-2 | ขาเงินเข้าใน GL ของ NextAcc | ❌ ใบเสร็จค่าแพ็กเกจ/ตัดชำระใบลง `PlatformCashAccountCode` (ธนาคาร) แม้จ่ายผ่าน Omise ⇒ รอบโอน (หยิบ intent เดียวกันเพราะอยู่ใต้บริษัทผู้ให้บริการ) Cr 11340 ซ้ำ ⇒ 11340 ติดลบ + ธนาคารเกินสองเท่า | `PlatformBillingDocumentIssuer` `IssuePaidReceiptAsync`/`SettleInvoiceAsync` (ก่อนแก้) · `GatewaySettlementService.SelectCandidatesAsync` | **(227)** `MoneyInAccountIdAsync` — มี intent ที่รับเงินของต้นทางนี้ ⇒ `IGatewayAccountResolver.ResolveMoneyInAccountAsync` (ตัวเดียวกับร้าน) · สลิป = ธนาคารเดิม · เทสต์ Db สองทิศ · ⚠️ ไม่ย้อนแก้ JE เดิม (ดู §4) |
| C-3 | ใบกำกับภาษี/ใบเสร็จของค่าส่วนเสริมจ่ายล่วงหน้า | ❌ ไม่มีเอกสาร (ออนไลน์และสลิป) — G-9 ค้างตั้งแต่รอบ 219 | `AddOnPurchaseService.ApproveLockedAsync`/`MarkPaidByGatewayLockedAsync` (ก่อนแก้) | **(227)** `IPlatformBillingDocumentIssuer.IssueAddOnReceiptAsync` (ขายเงินสด · VAT ตามค่าตั้งเดียวกับเส้นอื่น · กันซ้ำด้วยเลขอ้างอิงต่อ intent/ต่อไฟล์สลิป · ขาเงินเข้า = บัญชีพักเมื่อจ่ายออนไลน์) · เก็บ `CompanyFeature.PaymentReceiptDocumentId/Number` (echo ใน `AddOnPaymentState` เฉพาะสถานะชำระแล้ว) · ล้ม = audit `RD-86/4-ADDON-RECEIPT` + log · อีเมลแจ้งเจ้าของเมื่อจ่ายออนไลน์ (เดิมเงียบ) |
| C-4 | ค่าธรรมเนียม/VAT ค่าธรรมเนียม → GL | ✅ ตอนบันทึกรอบโอนในบริษัทผู้ให้บริการ (Dr ธนาคาร + Dr 54710 + Dr ภาษีซื้อ / Cr 11340) · intent ทดสอบ/ทดลองจ่ายไม่เข้า | `GatewaySettlementService` · `PaymentIntentAdapter` | ⚠️ ต้องมีผัง 11340/54710 ในบริษัทผู้ให้บริการ — รายการตรวจ B-5 บอกแล้ว |
| C-5 | ศูนย์บิลผู้เช่า: สถานะชำระ + ดาวน์โหลดใบเสร็จ · dunning หยุดเมื่อจ่าย · อีเมลใบเสร็จ | ✅ ค่าแพ็กเกจ/ใบแจ้งหนี้ · ⚠️ ใบเสร็จส่วนเสริม: เลขใบ echo แล้ว แต่ยังไม่มีปุ่มดาวน์โหลด PDF ในศูนย์บิล | `pages/billing.html` · `PlatformDunningService` | ข้อเสนอ §5.7 (endpoint ดาวน์โหลดเอกสารผู้ให้บริการที่ผู้เช่าเป็นผู้ซื้อ) |
| C-6 | กระทบยอด Omise ↔ ระบบ | ⚠️ มีหน้ารายการรับชำระออนไลน์ + รอบโอนในบริษัทผู้ให้บริการ (ต้องล็อกอินเป็นบริษัทนั้น) · หน้าแอดมินไม่มีมุมมองรายการ | — | **(227)** `GET api/admin/platform-gateway/intents` + ตารางบนหน้าแอดมิน (สถานะ · เลข Omise · ค่าธรรมเนียม · ผู้จ่าย · ต้นทาง · ใบเสร็จ · เข้ารอบโอน · ⚠️ เงินเข้าแต่ต้นทางไม่บันทึก) |
| C-7 | ทุกเส้นเงินผ่าน `PaymentIntent` + `IPaymentProvider` | ✅ เริ่ม: `PlatformPaymentService.StartAsync` → `PaymentIntentService.StartAsync` (ด่าน `PlatformRevenueSource`) · ศูนย์บิล/ลิงก์ชำระ/ส่วนเสริม/แพ็กเกจ/ทดลองจ่ายผ่านตัวนี้ · `RecordExternalSuccessAsync` ปฏิเสธเงินแพลตฟอร์ม · ไม่พบ HTTP ไป Omise นอก adapter (`payment_provider_boundary_check`) | — | — |
| C-8 | เส้นล้ม/หมดอายุ | ✅ intent Failed/Expired · รายการชำระยังเปิด (จ่ายใหม่/แนบสลิปได้) · ข้อความล้มเหลวไทย (227) | `PaymentIntentService` | — |

### D. ความปลอดภัย

| # | รายการ | สถานะ | หลักฐาน | แก้/ข้อเสนอ |
| --- | --- | --- | --- | --- |
| D-1 | ความลับของ webhook token (≥32 ตัว · ต่อ config · หมุนได้ · log ปิดบัง path) · รหัสผิด = ไม่ยิงคำขอออก | ✅ | `Helpers/GatewayWebhookRoute.cs` · `AuditMiddleware` | — |
| D-2 | replay | ✅ event ซ้ำ = no-op (เครื่องสถานะ) · เนื้อปลอมทำอะไรไม่ได้ (fetch event จริงเท่านั้น) · `PaymentWebhookOwnership` ตรวจเจ้าของ+ยอด | `PaymentGatewayController.cs` `Receive` | — |
| D-3 | สิทธิ์แอดมิน | ✅ `[Authorize(Roles = "SystemAdmin")]` ทั้ง controller · CSRF ไม่เกี่ยว (Bearer ใน header ไม่ใช่ cookie) | `AdminPlatformGatewayController.cs:24` | — |
| D-4 | สิทธิ์ผู้เช่า | ⚠️ เริ่มจ่าย = `Billing.Manage` · สถานะค่าแพ็กเกจตรวจสิทธิ์ แต่**สถานะส่วนเสริมไม่ตรวจ** | `PlatformPaymentController` | **(227)** เพิ่ม `RequirePermission(BillingManage)` ให้สถานะส่วนเสริม (สมมาตร) |
| D-5 | rate limit | ✅ ลิงก์ชำระ (ต่อ IP + ต่อโทเคน) · หน้าทดลองจ่าย (ต่อ IP + รวม) · เส้นล็อกอินใช้คีย์กันซ้ำ (`SubmitOnce`) | `PlatformPayLinkController` · `PlatformPayTestController:74-75` | — |
| D-6 | open redirect หลังจ่าย | ⚠️ `SafeReturnUrl` ✅ แต่ allow-list ยังเติมโฮสต์ของ appsettings (app.nextacc.com — โดเมนที่ไม่ใช่ของเรา) แม้แอดมินตั้ง URL ระบบแล้ว | `PaymentIntentService.SafeReturnUrlAsync` | **(227)** เลิกเติม (ยังไม่ตั้ง ⇒ `SystemBaseUrl` คืนค่านั้นอยู่แล้ว — พฤติกรรมเดิมคงไว้) |
| D-7 | PCI (ไม่แตะเลขบัตร) · ไม่ log ความลับ · ประวัติไม่เก็บ payload (PII) | ✅ | `payment_provider_boundary_check` · `PaymentIntentService.AddEvent` | — |

## 2. สิ่งที่แก้ในรอบนี้ (ไฟล์)

| ข้อ | ไฟล์ |
| --- | --- |
| A-4 · A-7 · A-10 | `Services/Payments/Providers/OmisePaymentProvider.cs` · ใหม่ `Services/Payments/Providers/OmiseFailureText.cs` |
| A-12 | ใหม่ `Services/Payments/PaymentConfigForIntent.cs` · `PaymentIntentService.RefreshAsync` · `GatewayRefundService` (สั่งคืน/ตรวจผล) · `PaymentProviderConfigService.Probe` (ใช้สำเนาตัวเดียวกัน) · `PaymentGatewayController.cs` (`PaymentWebhookController.Receive`) |
| A-15 · B-4 | `Helpers/PaymentLiveGate.cs` (พารามิเตอร์ใหม่ค่าตั้งต้น = ผ่าน) · `Helpers/SystemBaseUrl.cs` (`IsConfigured` · `ShippedDefault`) · `PlatformLiveExtras` |
| B-2 · B-3 · B-5 · C-6 | ใหม่ `Helpers/PlatformPayMethods.cs` · ใหม่ `Helpers/PlatformGatewayReadiness.cs` · `PlatformPaymentService.MethodsOf` · `AdminPlatformGatewayController` (GET · Save · SetMode · `intents`) · `wwwroot/admin/platform-gateway.html` |
| C-2 | `PlatformBillingDocumentIssuer` (`MoneyInAccountIdAsync` · `SettleInvoiceAsync(subscriptionPaymentId)`) · `SaasBillingDocumentService.SettlePlatformInvoiceAsync` · ใหม่ `Helpers/PlatformMoneyInAccount.cs` |
| C-3 | `IssueAddOnReceiptAsync` · `AddOnPurchaseService` · `CompanyFeature.PaymentReceiptDocumentId/Number` (migration ใน `PlatformBillingPhase2Migration`) |
| D-4 · D-6 | `PlatformPaymentController.AddOnStatus` · `PaymentIntentService.SafeReturnUrlAsync` |
| เทสต์ | `Accounting.Tests/PlatformOmiseRound227Tests.cs` (pure + adapter ผ่าน handler ปลอม) · `Accounting.Tests/Db/PlatformOmiseRound227DbTests.cs` (ขาเงินเข้า + กันซ้ำใบเสร็จ) |
| ด่าน | `tools/required_call_site_check.py` กลุ่ม `_R227_WHY` (13 กติกา) |

ทิศตรงข้ามที่ล็อกด้วยเทสต์: บัตรไม่มี source ไม่ประทับวันหมดอายุ · charge สำเร็จไม่มีข้อความล้มเหลว · รายการทดสอบยังใช้คีย์ทดสอบ · แถวเดิม (IsTestMode null) ใช้โหมดช่องทาง ·
อีกโหมดไม่มีคีย์ = ไม่ลอง · ผู้เรียกด่าน live เดิมผลไม่เปลี่ยน · สลิป/intent ที่ยังไม่สำเร็จลงธนาคารเดิม · อนุมัติสลิปซ้ำ = ใบเดิม · สลิปงวดใหม่ = ใบใหม่

## 3. คำถาม 6 ข้อ (F3 ข.)

7. **รูปแบบเดิมทั้งเรพ:** adapter เลือกคีย์จาก `config.Mode` สำหรับรายการเดิม — เรียก provider ด้วย config ของรายการ 4 จุด (poll 1 · สั่งคืน 1 · ตรวจผลคืน 1 · webhook 1) → แก้ครบ 4 เหลือ 0 ·
   `PaymentAccountId/OverridePaymentAccountId: await ResolveCashAccountIdAsync(` ในเส้นที่รับเงินผ่าน gateway 2 จุด → 0 (เหลือเฉพาะเส้นสลิป/ไม่มีรายการชำระ)
8. **ทางเข้าอื่น:** ศูนย์บิล · ลิงก์ชำระ · หน้าแพ็กเกจ · หน้าส่วนเสริม เดินผ่าน `PlatformPaymentService` ตัวเดียว ⇒ ได้ตัวกรองวิธีชำระ/ด่านเดียวกัน · คีย์ตามโหมดรายการอยู่ในบริการกลาง ⇒ ร้านได้ด้วย ·
   ส่วนเสริมสองทางรับเงิน (ออนไลน์ · สลิปที่อนุมัติ) ออกใบทั้งคู่
9. **เข้มขึ้น:** (ก) ด่าน live เพิ่ม 2 ข้อ — ผู้ถูกกัน: ตั้ง URL ระบบที่ ตั้งค่าเว็บไซต์ / ติ๊กพร้อมเพย์ (ข้อความบอกทาง) — เทสต์ `B4_…` ทั้งสองทิศ (ข) วิธีที่ทำไม่จบหายจากผู้เช่า —
   ทางไปต่อ = พร้อมเพย์/โอน+สลิป (เปิดเสมอ) · `B2_…` (ค) ชื่อวิธีไม่รู้จัก = 400 — `B3_…` (ง) สถานะส่วนเสริมต้อง Billing.Manage — ผู้เริ่มจ่ายต้องมีสิทธิ์นี้อยู่แล้ว
10. **ค่าที่ persist ก่อนแก้:** JE ใบเสร็จ/ตัดชำระที่ลงธนาคารจากเงินออนไลน์ก่อนรอบนี้ — **ไม่ migrate อัตโนมัติ** (ห้ามแก้ JE ย้อนหลังเงียบ) ·
    เจ้าของยังไม่เปิด live (มีแต่โหมดทดสอบ — intent ทดสอบไม่ให้สิทธิ์/ไม่ออกใบ) ⇒ คาดว่า **ไม่มีแถวเงินจริงที่ผิด** · ถ้ามี: รายการ ⚠️ ในตาราง C-6 + กลับรายการ Dr 11340 / Cr ธนาคาร ด้วยมือ ·
    ส่วนเสริมที่จ่ายก่อนรอบนี้ไม่มีใบ — ออกด้วยมือในบริษัทผู้ให้บริการ (ไม่ออกย้อนหลังอัตโนมัติ: วันที่ใบ/งวดภาษีต้องให้ผู้ทำบัญชีตัดสิน)
11. **ฝ่ายค้าน:** ยังไม่ได้ส่ง (ผู้ประสานงานส่ง diff ให้ subagent ฝ่ายค้าน 1 รอบก่อน push ตาม F3 ข้อ 11 — แตะเงิน/ภาษี/สิทธิ์)
12. **เอกสาร:** ACCOUNT_STRUCTURE §6.2d · DOCUMENT_FLOW (ใบเสร็จส่วนเสริม) · TEST_PLAN · CHANGELOG รอบ 227

## 4. ความเสี่ยงที่เหลือ / ต้องยืนยันกับ sandbox

- `expires_at` ระดับ charge และตารางรหัสล้มเหลว มาจากเอกสารสาธารณะของ Omise — ยืนยันใน sandbox ตอนทดลองจ่าย ฿20 ครั้งถัดไป (ดูช่อง "หมดอายุ" บนหน้าจ่าย + ลองบัตรทดสอบที่ถูกปฏิเสธเมื่อมีช่องบัตร)
- webhook ลองคีย์อีกโหมด = คำขอออกเพิ่ม 1 ครั้งต่อ webhook ที่ยืนยันไม่ผ่าน (เฉพาะ URL รหัสลับ — รหัสลับคือด่านแรก)
- ใบเสร็จส่วนเสริมใช้ `PlatformPriceIncludesVat` ตัวเดียวกับแพ็กเกจ — ถ้าราคาส่วนเสริมตั้งแบบไม่รวม VAT แต่แพ็กเกจรวม จะต้องแยกค่าตั้ง (ยังไม่พบกรณีนี้)

## 5. ดีไซน์ + ข้อเสนอ (ไม่สร้างในรอบนี้)

### 5.1 ช่องกรอกบัตร + 3-D Secure + แบงก์กิ้ง (ทำให้ `PlatformPayMethods` เปิดเพิ่มได้) — ✅ บัตรทำแล้วรอบ 228 · วอลเล็ท/แบงก์กิ้ง ⚠️ ยังปิด
`PayWidget` ขอ `{ publicKey, clientScriptUrl }` จาก endpoint ของช่องทาง (ค่าจาก `IPaymentProvider.ClientScriptUrl` + `OmisePaymentProvider.PublicKey` — ไม่ hard-code) → โหลด omise.js →
`Omise.createToken('card', …)` ในเบราว์เซอร์ → ส่ง `cardToken` (เส้นเดิมรับอยู่แล้ว) → ได้ `authorizeUrl` → ไปหน้าธนาคาร → กลับมาที่ `returnUrl` ที่มี `?intent=` →
หน้าเดิม (ศูนย์บิล/ลิงก์ชำระ/แพ็กเกจ/ส่วนเสริม) เปิด widget ต่อในโหมด "ตรวจสถานะ" (ถามเซิร์ฟเวอร์ ไม่เชื่อ query) · CSP มาจาก `CspNeeds` แล้ว ·
แบงก์กิ้ง/วอลเล็ทใช้ "รับกลับ" ตัวเดียวกัน + ช่องเบอร์วอลเล็ท · เปิดใน `PlatformPayMethods.NotPayableReason` เมื่อครบ · ประมาณ 2–3 วัน + ทดสอบ sandbox

### 5.2 ตัดบัตรอัตโนมัติ (saved card) — ดีไซน์ + ข้อความยินยอม (รอบ 228 · ยังไม่สร้าง)
Omise Customer ต่อบริษัทผู้จ่าย (เก็บ `cust_…` ใน tenant ผู้ให้บริการ · ไม่เก็บเลขบัตร) · ยินยอมชัดแจ้ง (PDPA `ConsentRecord` + ข้อความในนโยบาย) · 3DS ครั้งแรก ·
ตัดเมื่อออกใบต่ออายุ/ใบค่าใช้งานถึงกำหนด ผ่าน `PaymentIntentService.StartAsync` ตัวเดิม (ชนิดต้นทางเดิม) · ล้ม ⇒ dunning เดิม · แอดมินปิดได้ต่อบริษัท · 4–5 วัน · **แนะนำทำหลัง 5.1**

### 5.3 ดึงรอบโอน (transfers) อัตโนมัติ — ดีไซน์ละเอียดรอบ 228 (ยังไม่สร้าง)
adapter เพิ่ม `ListSettlementsAsync` (ไม่มีชื่อเจ้าในชั้นกลาง) → ร่างรอบโอนใน `GatewaySettlementService` (ด่านยอดไม่ตรงเดิม) ให้ผู้ทำบัญชีกดยืนยัน — ไม่ลง JE อัตโนมัติ · 2 วัน

### 5.4 ข้อพิพาท/chargeback — ✅ รอบ 228 (บันทึก + เตือน + แสดง · JE ค่าเสียหายยังทำมือ)
adapter ยืนยัน event `charge.dispute.*` (fetch แบบเดิม) → `PaymentIntent.DisputeStatus` + ประวัติ + แจ้ง SystemAdmin · แพ้ข้อพิพาท ⇒ เรื่องค้างบนรายการชำระ (`PlatformPaymentAttention`) ให้แอดมินตัดสินพักสิทธิ์/ออกใบลดหนี้ · JE Dr ค่าเสียหาย / Cr 11340 ตอนรอบโอนหัก · 2–3 วัน

### 5.5 คืนเงินค่าบริการ + ใบลดหนี้ §86/10 — ✅ รอบ 228
ตามคำตัดสิน "ไม่คืนเงิน" — ข้อยกเว้น (เก็บซ้อน/ผิดพลาด) ใช้ปุ่มคืนเงินเดิม (SystemAdmin) แล้ว**ออกใบลดหนี้อ้างใบเดิม**ใน tenant ผู้ให้บริการ (ด่าน SUM(CN) ≤ ใบเดิม ที่มีอยู่) — ต่อสายจาก `GatewayRefundService` เมื่อ intent เป็นเงินแพลตฟอร์ม · 1.5 วัน

### 5.6 Idempotency-Key ของ Omise
ส่ง `PaymentIntent.IdempotencyKey` เป็นหัว idempotency ของคำขอสร้าง charge (ยืนยันชื่อหัว/พฤติกรรมกับ sandbox ก่อน) ⇒ timeout ตอนสร้าง charge แล้วลองใหม่ไม่ได้ charge ซ้อน · 0.5 วัน

### 5.7 ดาวน์โหลดใบเสร็จส่วนเสริมในศูนย์บิล — ✅ รอบ 228
endpoint ศูนย์บิล "เอกสารที่บริษัทนี้เป็นผู้ซื้อในบริษัทผู้ให้บริการ" (ตรวจ `ContactId` ผูก tenant ด้วย `ExternalId`) ใช้กับใบเสร็จส่วนเสริม (และใบกำกับขายสดอื่น) · 0.5–1 วัน

### 5.8 Proration + คูปอง/โค้ดส่วนลด — ดีไซน์รอบ 228 (ยังไม่สร้าง)
คงคำตัดสินเดิม (อัปเกรดทันที = รอบใหม่เริ่มวันอนุมัติ ส่วนที่เหลือสละ) — ทำเมื่อมีคำขอจริง (pure `PlatformProration` + เครดิตรอบหน้า) · 2 วัน

## 6. ฝ่ายค้านรอบ 227 — ผลและการแก้ (คำตัดสินผู้ประสานงาน)

| # | ข้อ | แก้ | ที่อยู่ / เทสต์ |
| --- | --- | --- | --- |
| B1 (blocking) | อ่านวันหมดอายุได้แล้ว ⇒ `RefreshAsync` ปิดใบ "หมดอายุ" เองโดยไม่ถามผู้ให้บริการ (สถานะปลายทางที่ระบบประทับเอง — เงินอาจเพิ่งเข้า) + ถือห้องที่พัก/รอเงินการจองยาวขึ้นเงียบ ๆ (พร้อมเพย์อาจ 24 ชม.) | มีเลขอ้างอิงผู้ให้บริการ ⇒ ถามก่อนเสมอแล้วใช้สถานะของเขา · ถามไม่ได้ ⇒ ปิดเองหลังวันหมดอายุ + 15 นาทีเท่านั้น · หน้าต่างถือ/รอ = min(วันหมดอายุ, ค่าตั้งต้นเดิม 30 นาที) — ค่าเดิมเป็นเพดาน · ใช้ QR เดิมซ้ำได้ไม่เกินวันหมดอายุ (เดิมไม่จำกัด) | ใหม่ `Helpers/GatewayExpiryPolicy` · `PaymentIntentService.RefreshAsync` · `LodgingHoldRule.PaymentHoldUntil` · `BookingPaymentPolicy.GatewayPaymentInProgress` · `B1_*` · แก้ `LodgingHoldRuleTests` (24 ชม. ⇒ 30 นาที + ทิศตรงข้าม 10 นาที) |
| S1 | ตัวหาขาเงินเข้านับ intent คืนเต็ม/โหมดทดสอบ | เฉพาะสำเร็จ/คืนบางส่วน + ไม่ใช่โหมดทดสอบ | `GatewayMoneyInAccountIdAsync` · Db `S1_*` |
| S2 | ใบเสร็จ ≠ เงินที่รับ: ฐาน × 7% ปัดเหลือเศษ (0.99 ⇒ 1.00 · 1.76 ⇒ 1.75) · "ราคาไม่รวม VAT" ออกใบ = เงิน + VAT | **เงินที่รับแล้วถือว่ารวม VAT เสมอ** — ฐาน = round(A×100/107) · VAT บังคับ = A − ฐาน (`VatAmountOverride`) ทั้งใบเสร็จค่าส่วนเสริม ค่าแพ็กเกจ และ PDF สำรอง · ไม่บวกยอดเรียกเก็บเงียบ ๆ — จด VAT + "ราคาไม่รวม VAT" = ด่านเปิดรับเงินจริง (ข้อ `priceIncludesVat`) | `PlatformMoneyInAccount.SplitReceived` · `IssueAddOnReceiptAsync`/`IssuePaidReceiptAsync` · `SaasBillingDocumentService` · `S2_*` (Theory 0.99/1.76/300) · Db `S2_S6_*` |
| S3 | เงินจริงเข้าแต่ไม่มีใบ มองไม่เห็น/กดซ้ำไม่ได้ | ตารางรายการติดธง + เหตุผล (`PlatformReceiptGap`) · ปุ่ม "ออกใบเสร็จอีกครั้ง" `POST api/admin/platform-gateway/intents/{id}/reissue-receipt` (SystemAdmin · audit chain · ตัวออกเอกสารเดิม idempotent · ค่าแพ็กเกจผูกใบ = ตัดชำระใบเดิม) | `AdminPlatformGatewayController.ReissueReceipt` · `IAddOnPurchaseService.ReissueGatewayReceiptAsync` · `S3_*` |
| S4 | ผังบัญชีพักหาย = คำเตือน | เป็นข้อของด่านเปิดรับเงินจริง (ด่านใช้รายการตรวจชุดเดียวกับการ์ด) | `PlatformGatewayReadiness` · `PaymentLiveGate.PlatformRefusal(readinessMissing)` · `C1_*` |
| S5 | เลขอ้างอิงสลิปไม่มีงวด | + `UpfrontPeriod` | `AddOnPurchaseService.ReceiptReference` · `C3_*` |
| S6 | ใบร่างค้างถูกนับเป็น "ใบเดิม" | กรองร่างออก · เรียงตามเวลาสร้าง | `IssueAddOnReceiptAsync` · Db `S2_S6_*` |
| S7 | ตัดโฮสต์ appsettings ทั้งหมด | คงไว้เมื่อ deployment ตั้งเอง (ไม่ใช่ค่าที่มากับซอร์ส) | `SafeReturnUrlAsync` |
| N1 | event อีกโหมดประทับ "เคยได้รับ webhook" ของโหมดปัจจุบัน | ประทับเฉพาะโหมดเดียวกัน | `PaymentWebhookController.Receive` |
| N2 | ใช้ QR เดิมซ้ำข้ามโหมด | ต้องโหมดเดียวกับช่องทางตอนนี้ | `PaymentIntentService.StartAsync` |
| N3 | ตัวตรวจเลขภาษีผู้ขายนับแค่ 13 หลัก | `ThaiTaxId.IsValid` (mod-11) ตัวเดียว — ทั้งการ์ดและตัวออกเอกสาร/PDF สำรอง | `PlatformBillingDocumentIssuer` · `SaasBillingDocumentService` |
| N4 | วันที่ใบเสร็จส่วนเสริมเป็นเวลา UTC | วันที่ตามปฏิทินไทย (`ThaiDate.CalendarDateUtc`) | `IssueAddOnReceiptAsync` |
| N5 | หาเวลารับเงินด้วย Id อย่างเดียว | + `PayerCompanyId` | `MarkPaidByGatewayLockedAsync` |
| N6 | เบอร์วอลเล็ทมีขีด/ช่องว่าง | ตัวเลขล้วน | `OmisePaymentProvider.PhoneDigits` · `A4_*` |

ผลต่อพฤติกรรมเดิม: ใบเสร็จค่าแพ็กเกจในโหมด "ราคาไม่รวม VAT" เปลี่ยนจาก "เงิน + VAT" เป็น "เงินที่รับ (ถอด VAT)" — ใบเดิมไม่ถูกแก้ · เลขภาษีผู้ขายที่ checksum ไม่ผ่าน ⇒ ออกใบเสร็จธรรมดาแทนใบกำกับ (การ์ดความพร้อมบอก)

## 7. รอบ 228 — ทำของค้าง §5 (ทีม PLATFORM BILLING + OMISE · ผู้ประสานงานตัดสินเอง ตามคำเจ้าของ "ตัดสินเอง") · **ยังไม่ได้คอมไพล์ในเครื่องนี้**

| ข้อ | สถานะ | ทำอะไร | ไฟล์ / เทสต์ |
| --- | --- | --- | --- |
| 5.1 บัตร + 3DS | ✅ | `IPaymentProvider.ClientPublicKey` (adapter ประกาศ — คีย์สาธารณะของ**โหมดปัจจุบัน**) + `Services/Payments/PaymentClientSetup` (สคริปต์ + คีย์ · ไม่มีชื่อเจ้า) · `PlatformPayInfo` คืน `card` + `methodOptions` (ป้ายจาก `PlatformPayMethods.Label`) · ร้าน `public-pay/methods` คืน `card` และ**ตัดปุ่มบัตรเมื่อไม่มีคีย์สาธารณะ** (เดิมขึ้นแล้วล้ม "ไม่พบโทเคนบัตร") · `js/pay-widget.js`: ตัวเลือกวิธีในหน้าต่าง · ช่องกรอกบัตร (ไม่มี name/ไม่อยู่ใน form · ล้างหลังแลกโทเคน) → โหลดสคริปต์จาก URL ที่เซิร์ฟเวอร์ให้ (CSP มาจาก `CspNeeds` แล้ว) → โทเคน → `cardToken` · ก่อนพาไปหน้าธนาคารจดรายการใน sessionStorage + URL กลับมี `payReturn=1` → `PayWidget.resume()` รันเองตอนโหลด ถามสถานะเซิร์ฟเวอร์ (ไม่เชื่อ query) · เส้นค่าบริการต้องส่ง `card` ของช่องทาง NextAcc เอง (ไม่หยิบคีย์ของร้าน) · `PlatformPayMethods.Effective(…, cardClientReady)` (ไม่มีค่าตั้งต้น — ลืมส่ง = คอมไพล์ไม่ผ่าน) | `PaymentClientSetup.cs` · `PlatformPaymentService.MethodsOf/CardSetupOf` · `PublicPaymentController.Methods` · `pay-widget.js` · `billing.html`/`addons.html`/`subscription.html` · `Card1–3` |
| แบงก์กิ้ง/วอลเล็ท | ⚠️ ปิดต่อ | การรับกลับใช้ได้แล้ว (เส้นเดียวกับ 3DS — หน้าร้านได้ผลทันที) แต่ adapter ผูกธนาคารเดียว (`mobile_banking_kbank`/`internet_banking_bay`) และเส้นค่าบริการยังไม่ส่งเบอร์วอลเล็ท ⇒ เหตุผลใน `PlatformPayMethods.NotPayableReason` เปลี่ยนตามจริง · ทำต่อ = ตัวเลือกธนาคารใน widget + `source[type]` ต่อธนาคาร + ช่องเบอร์ใน `PlatformPaymentController.StartRequest` | — |
| 5.7 ใบเสร็จในศูนย์บิล | ✅ | `PlatformInvoiceQueries.PaidDocumentsForBuyer` (ใบกำกับ/ใบเสร็จ/ใบลดหนี้ที่บริษัทเป็นผู้ซื้อ — ผู้ติดต่อผูก tenant ตัวเดียวกับใบแจ้งหนี้) · `GET billing-center/receipts/{id}/pdf` (Billing.Manage · ของบริษัทอื่น = 404) · ศูนย์บิลมีตาราง "ใบเสร็จ / ใบกำกับภาษี / ใบลดหนี้" + ปุ่มใบเสร็จบนแถวส่วนเสริม (`BillingCenterAddOn.ReceiptDocumentId`) | `BillingCenterController.ReceiptPdf` · `PlatformBillingCenterService` |
| 5.5 ใบลดหนี้คืนเงิน | ✅ (แก้ตามฝ่ายค้าน B1 §7.2) | intent ค่าบริการ**ใต้บริษัทผู้ให้บริการ** (`PlatformRevenueSource.RefundBooksInPlatformTenant`) — ~~คืนเงินลง PV Dr ลูกหนี้ / Cr บัญชีพัก~~ (ฝ่ายค้าน B1: ไม่ลง PV แล้ว — ใบลดหนี้ Cr บัญชีพักคือรายการเดียว) หลัง commit `IssueRefundCreditNoteAsync` ออกใบลดหนี้ (อนุมัติทันที) อ้างใบเดิม · ยอด = เงินที่คืนรวม VAT (`SplitReceived` ถ้าใบเดิมมี VAT) · ด่าน `Helpers/PlatformRefundCreditNote.Decide` (Σ ≤ ใบเดิม · ใบเดิมยกเลิก/ไม่พบ = ไม่ออก + ทางไปต่อ) · อ้างอิง `REFUND-{เลขคืน}` กันซ้ำ · audit `RD-86/10-PLATFORM-REFUND` · ครอบคืนเงินปกติ/ตรวจผลไม่แน่ชัด/บันทึกผลมือ · รายการเก่าใต้ร้าน = ไม่ลง (เดิม) | `GatewayRefundService.IssuePlatformCreditNoteAsync` · `PlatformBillingDocumentIssuer.IssueRefundCreditNoteAsync` · `Cn1–5` |
| 5.4 ข้อพิพาท | ✅ | adapter: event ที่ `data.object = dispute` ⇒ อ่านเลข charge (ตรวจรูป `chrg_`) → fetch charge ด้วยคีย์ของเรา → intent จาก metadata → `VerifiedWebhookEvent.Dispute` · webhook ผ่านด่านเจ้าของรายการเดิมแล้วส่ง `PaymentDisputeService.RecordAsync` (ไม่ `ApplyChargeAsync`) · คอลัมน์ `PaymentIntent.DisputeState/ProviderRef/Amount/Reason/UpdatedAt` (ADD COLUMN IF NOT EXISTS) · ประวัติ + audit `GW-DISPUTE` + กระดิ่งแอดมินแพลตฟอร์ม (เปิด/พิจารณา/แพ้/ปิด) · รายการบนหน้าแอดมินติดธง + ป้าย/ทางไปต่อ (`Helpers/PaymentDisputeRules`) · **ไม่เปลี่ยนสถานะเงิน/สิทธิ์เอง** | `PaymentDisputeService` · `OmisePaymentProvider.VerifyDisputeAsync/ParseDispute` · `Dp1–3` |
| 6 รายงาน MRR/AR | ✅ | `GET api/admin/platform-billing/revenue-report` — MRR ตามแพ็กเกจ (Active/PastDue · ไม่ฟรี · ราคา ÷ เดือนของรอบ) · นับยกเลิกสิ้นรอบ/ค้างชำระ · ส่วนเสริมที่ต้องจ่าย (MRR) · รับค่าส่วนเสริม 30 วัน · ลูกหนี้ค้าง 5 ช่วงอายุ · หน้า `/admin/platform-billing.html` การ์ด 📈 | `Helpers/PlatformRevenueMetrics` · `Mrr1–3` |

### 7.1 ดีไซน์ที่ยังไม่สร้าง (ตัดสินแล้ว — ทำเมื่อมีคำขอจริง/เวลา)

**5.3 รอบโอน (transfers) → ร่างรอบโอน:** adapter เพิ่ม `Task<IReadOnlyList<ProviderSettlement>> ListSettlementsAsync(config, since, ct)` (default ว่าง · Omise = `GET /transfers?from=` แล้วต่อ `GET /transactions?…` เพื่อหา charge ในรอบ —
ต้องยืนยันใน sandbox ว่า transfer ระบุ charge ได้ ถ้าไม่ได้ จับคู่ด้วยช่วงวันที่ + ยอดสุทธิ) → job ต่อบริษัท (JobLock + watermark `LastSettlementPullAt`) สร้าง **ร่าง** ใน `GatewaySettlementService` ด้วย intent ที่จับได้ ·
ด่านยอดไม่ตรงเดิมบล็อก · ผู้ทำบัญชีกดยืนยัน (ไม่ลง JE อัตโนมัติ — สถานะ "ระบบภายนอกรับแล้ว" ต้องมาจากผู้ให้บริการจริง แต่การลงบัญชีเป็นของคน) · แพ้ข้อพิพาท = บรรทัดหักในรอบ ⇒ Dr ค่าเสียหายจากข้อพิพาท / Cr 11340 ·
**ไม่สร้างรอบนี้** เพราะรูปข้อมูล transfer↔charge ของผู้ให้บริการยังไม่ยืนยันกับ sandbox (เสี่ยงร่างผิดที่ดูถูก)

**5.8 Proration (อัปเกรดกลางรอบ):** pure `Helpers/PlatformProration.Charge(oldPricePerCycle, newPricePerCycle, cycleStart, cycleEnd, today)` = round((ใหม่ − เก่า) × วันที่เหลือ ÷ วันในรอบ, 2, AwayFromZero) ·
อัปเกรด ⇒ สร้าง `SubscriptionPayment` ยอดส่วนต่าง (ชนิด `Proration` · ไม่เปลี่ยน EndDate) จ่ายผ่านศูนย์บิล/widget เดิม · จ่ายแล้วเปลี่ยนแพ็กเกจทันทีโดย**รอบเดิมคงที่** (แทนนโยบายชั่วคราว "เริ่มรอบใหม่วันอนุมัติ") ·
ดาวน์เกรด = `Scheduled*` เดิม (มีผลวันครบรอบ · ไม่คืนส่วนต่าง — ข้อ 178 ⑥/⑦) · ไม่เก็บย้อนหลัง (ข้อ 178 ②) · แอดมินยังตั้งราคา/ยกเว้นได้ (ข้อ 178 ①)

**คูปอง/โค้ดส่วนลด:** ตาราง `PlatformCoupon(Code unique ไม่สนตัวพิมพ์, Kind{Percent,Fixed}, Value, ValidFrom, ValidTo, MaxUses, UsedCount, PlansJson, AppliesTo{Subscription,AddOn,Both}, IsActive)` +
`PlatformCouponRedemption(CouponId, CompanyId, SubscriptionPaymentId/CompanyFeatureId, Amount, At)` (unique CouponId+CompanyId) · pure `Helpers/PlatformCouponRule.Apply(price, coupon, now, plan, usedCount)` ⇒ ยอดหลังส่วนลด (ไม่ต่ำกว่า 0 · ปัดครึ่งขึ้น) หรือเหตุผลปฏิเสธ ·
ใช้ตอน**สร้าง**รายการชำระ (ยอดยังมาจากเซิร์ฟเวอร์เสมอ) · ใช้สิทธิ์นับเมื่อเงินเข้า (handler) ใต้ advisory lock · ใบเสร็จแสดงส่วนลดเป็นบรรทัดส่วนลด (ฐานภาษีหลังส่วนลด) · **ผลต่อด่านเดิม:** อนุมัติอัตโนมัติเฉพาะ "ยอด = ราคาเต็ม" ต้องเปลี่ยนเป็น "ยอด = ราคาหลังคูปองที่บันทึกบนรายการ" (`PlatformPlanPricing.ClaimedAmountAcceptable`) ·
แอดมินสร้าง/ปิดโค้ดที่ `/admin/platform-billing.html` + audit chain · ประมาณ 2–3 วัน

**5.2 ตัดบัตรอัตโนมัติ — ข้อความยินยอม (ใช้ตอนสร้าง):**
> "ข้าพเจ้ายินยอมให้ NextAcc บันทึกบัตรนี้ไว้กับผู้ให้บริการรับชำระเงิน (NextAcc ไม่เก็บเลขบัตร) และตัดเงินค่าแพ็กเกจ/ค่าส่วนเสริม/ค่าใช้งานตามใบแจ้งหนี้ที่ถึงกำหนดโดยอัตโนมัติ
> ตามราคาที่แสดงในศูนย์บิล · ยกเลิกการตัดอัตโนมัติได้ทุกเมื่อที่ศูนย์บิล (มีผลกับรอบถัดไป) · ไม่มีการคืนเงินตามนโยบายการยกเลิกและคืนเงิน ฉบับ {version}"
>
ขั้น: ติ๊กยินยอม (บังคับ · `ConsentRecord` PDPA + ฉบับนโยบาย) → บัตรครั้งแรกผ่าน 3DS (widget เดิม) → adapter สร้าง customer + ผูกบัตร (เก็บเฉพาะ `cust_`/`card_` id ใน `PlatformSavedCard` ต่อบริษัทผู้จ่ายใน tenant ผู้ให้บริการ) →
job ใบถึงกำหนด (JobLock) เริ่ม `PlatformPaymentService.StartAsync` ชนิดต้นทางเดิม + `ChargeRequest.CustomerRef` (Idempotency-Key = §5.6) → ล้ม ⇒ dunning เดิม + อีเมล · แอดมินปิดได้ต่อบริษัท · ทำหลัง 5.6

### 7.2 ฝ่ายค้านรอบ 228 — ผลและการแก้ (คำตัดสินผู้ประสานงาน)

| # | ข้อ | แก้ | ที่อยู่ / เทสต์ |
| --- | --- | --- | --- |
| B1 (blocking) | คืนเงินลงซ้ำ: PV Dr ลูกหนี้/Cr 11340 + ใบลดหนี้ของใบที่ชำระครบ (`AdjustmentNoteAccount.CounterLegIsMoney`) ลง Cr **เงินสด** ⇒ เงินออกสองขา + ลูกหนี้ค้าง | ทางเลือก (a): ธุรกรรมคืนเงินไม่ลง PV สำหรับค่าบริการใต้บริษัทผู้ให้บริการ · ใบลดหนี้ใส่ `PaymentAccountId` = บัญชีพัก ⇒ Dr รายได้+VAT / Cr 11340 · ใบลดหนี้ล้ม ⇒ ใบสำคัญสำรอง Dr ลูกหนี้/Cr 11340 (ดังบนงบ) + เรื่องค้าง | `GatewayRefundService.BookRefundAsync/IssuePlatformCreditNoteAsync/PostPlatformFallbackVoucherAsync` · `IssueRefundCreditNoteAsync(counterAccountId)` · Db `PlatformRefundCreditNoteDbTests.B1_*` (11340 −107 · 111 = 0 · ลูกหนี้ 0 · รายได้ −100 · VAT −7) |
| B1 เส้นร้าน | **ยืนยันแล้วว่าเกิดแบบเดียวกัน**: คืนเงินผ่าน gateway (PV Dr ลูกหนี้/Cr บัญชีพัก) แล้วผู้ใช้ออกใบลดหนี้อ้างใบที่ชำระครบ ⇒ ขา counter = ช่องทางเงินของใบ (เงินสด/ธนาคาร) | ✅ แก้แล้ว (คำตัดสินผู้ประสานงาน "แก้เลย"): ตอนลงบัญชีใบลดหนี้ขายโหมดขาเงิน ถ้าใบต้นทางมีเงินคืน gateway (`PaymentIntent` ชนิด Document · `LastRefundJournalEntryId`) ที่ยังไม่มีใบลดหนี้ครอบ ≥ ยอดใบนี้ และไม่มี WHT ⇒ ขา counter = ผังลูกหนี้ (ขา Dr) ของ PV นั้น · อื่น ๆ = เดิม · การ "ครอบแล้ว" ใช้ชุดเดียวกับ `CreditNoteStatesAsync` (ใบลดหนี้ที่ออกแล้วอ้างใบต้นทาง) · ข้อจำกัด: คืนน้อยกว่าใบลดหนี้ (ไม่แบ่งขา) และใบที่มี WHT คงเส้นเดิม | `Helpers/GatewayRefundCreditNoteCounter` · hunk เดียวใน `DocumentService.AutoPostToJournalAsync` · Db `GatewayRefundCreditNoteCounterDbTests` (ลูกหนี้ 0 · เงินสด 0 · บัญชีพัก −107 / ทิศตรงข้าม เงินสด −107) · `Cn7_*` |
| S1 | บันทึกยอดคืนย้อนหลัง (ลงบัญชี) ไม่ผ่านใบลดหนี้ | เลือก "ให้ระบบลงบัญชี" ⇒ `IssuePlatformCreditNoteAsync` เส้นเดียวกัน · "ลงด้วยมือแล้ว" ⇒ ไม่ออก | `RecordLegacyRefundAsync` · Db B1 ใช้ทางเข้านี้ |
| S2 | ใบลดหนี้ล้มแล้วหายเงียบ | ประทับ `PaymentIntent.RefundCreditNotePending*/Problem/CounterAccountId/FallbackJournalEntryId` + audit `platform-refund-credit-note-failed` · รายการบนหน้าแอดมินติดธง (`PlatformReceiptGap.CreditNoteReason`) + ปุ่ม "ออกใบลดหนี้อีกครั้ง" (SystemAdmin · audit · เลขกันซ้ำเดิม · counter = ลูกหนี้ของใบสำคัญสำรอง) | `RetryPlatformCreditNoteAsync` · `AdminPlatformGatewayController.ReissueCreditNote` · `Cn6_*` |
| S3 | ใบเดิมที่ยกเลิกถูกเลือกก่อน | ใบที่ยังมีผลก่อน | `IssueRefundCreditNoteAsync` |
| S4 | ข้อพิพาทถอยสถานะเมื่อ event มาช้า · webhook พร้อมกัน | `PaymentDisputeRules.ShouldApply` (ผลปลายทางไม่ถอยเว้นแต่เลขข้อพิพาทใหม่ · เลขว่าง = เดิม · ส่งซ้ำไม่เขียน) + `FOR UPDATE` ในธุรกรรม | `PaymentDisputeService.RecordAsync` · `Dp3_*` `Dp4_*` |
| S5 | ไม่มีทางปิดธงข้อพิพาท · ร้านไม่เห็น | "รับทราบข้อพิพาท" (แอดมิน: SystemAdmin · ร้าน: สิทธิ์คืนเงิน · ค่าบริการ NextAcc = SystemAdmin) + audit · ธงกลับมาเมื่อข้อพิพาทเปลี่ยน · หน้า `payment-intents.html` แสดงข้อพิพาท · กระดิ่งเจ้าของบริษัทสำหรับรายการของร้าน | `AcknowledgeAsync` · `PaymentGatewayController.AcknowledgeDispute` · `Dp5_*` |
| S6 | MRR ส่วนเสริมนับรอชำระ/ทดลอง/บริษัทหมดอายุ | นับเฉพาะจ่ายแล้ว + พ้นทดลอง + บริษัทใช้งาน (`AddOnCountsTowardMrr`) · แยกคอลัมน์รอชำระ/ทดลอง | `revenue-report` · `Mrr4_*` |
| N1–N4 | resume ล้างเครื่องหมายก่อนตรวจ customAuth · โทเคนบัตรค้างหลังเริ่มไม่สำเร็จ · ตัวหาบริษัทผู้ให้บริการซ้ำ · เหตุผลแอดมินไม่อยู่บนใบ | ตรวจก่อนล้าง · ล้างโทเคน · `PlatformInvoiceQueries.PlatformTenantIdAsync` · บรรทัดใบลดหนี้พกเหตุผล (`LineDescription`) | `pay-widget.js` · `GatewayRefundService` · `Cn6_*` |

ข้อจำกัดที่รู้: เรื่องค้างใบลดหนี้เก็บได้ 1 รายการต่อรายการชำระ — ล้มซ้อนอีกครั้งก่อนออกรายการแรก ข้อความบอกให้ดูบันทึกตรวจสอบ (audit เก็บทั้งสองรายการ) ·
ใบลดหนี้อาจถูกด่าน §86/10 ของ DocumentService (วันที่ใบเทียบเดือนเหตุการณ์) ⇒ ล้ม ⇒ ใบสำคัญสำรอง + เรื่องค้าง (ไม่เงียบ) · ยังไม่ได้คอมไพล์ในเครื่องนี้

