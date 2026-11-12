# รอบ 260-2 — ยืนยันสลิปโอน: เลือก "บัญชีที่เงินเข้า" ได้ (ผลวิเคราะห์ + สิ่งที่สร้าง)

> ที่มา: แพ็ก C รอบ 259 (คำตัดสิน 259-3-7) ตั้งใจ**ไม่ใส่**ช่องเลือกบัญชีใน `Layout.formDialog` ของ `payment-intents.html` เพราะเส้นยืนยัน
> (`PaymentGatewayController.ConfirmManually`) ไม่รับช่องนี้ — ใส่ไป = silent no-op (กฎ #4 A) · รอบนี้ทำให้เซิร์ฟเวอร์รับจริงก่อน แล้วค่อยวางช่องบนจอ

## 1. ทางเข้าที่ "ยืนยันสลิป/เงินโอน" ทั้งหมด (ตรวจ 2026-11-12 บนโค้ด `8d842def`)

| # | ทางเข้า | ไฟล์ | ใครเลือกบัญชีเงินเข้า (ก่อนรอบนี้) | ผล |
|---|---|---|---|---|
| 1 | คิวสลิปของพนักงาน "ยืนยันด้วยมือ" | `PaymentGatewayController.ConfirmManually` → `ApplyChargeAsync` → `DocumentPaymentHandler` → `CreatePaymentAsync` | **ไม่มีใคร** — handler ส่ง `OverrideBankAccountId: null` + `OverridePaymentAccountId: ResolveMoneyInAccountAsync()` ซึ่งคืน `null` สำหรับ manual-slip (`SettlesDirectlyToBank = true`) ⇒ `DocumentMoneyChannel.ForPayment` สืบทอดแหล่งเงินของ**ใบแจ้งหนี้** · ใบส่วนใหญ่ไม่ได้ตั้งแหล่งเงิน ⇒ **ลงเงินสด 111** | ❌ บั๊กหลักของรอบ |
| 2 | สลิปจากพอร์ทัล / หน้าชำระของเอกสาร / (รอบ 260-1) LINE OA | `SlipEvidenceService.SubmitAsync` | ไม่ยืนยัน — สร้าง `PaymentIntent(Document, ManualSlip)` รอคิว #1 | ส่งต่อ #1 |
| 3 | สลิปรุ่นเก่าที่ย้ายเป็นรายการรอ (รอบ 250) | `PortalSlipLegacyMigration` | ไม่ยืนยัน — รอคิว #1 | ส่งต่อ #1 |
| 4 | สลิปการจองนัดหมาย (เว็บ) | `BookingSlipService.VerifyAsync` → `SiteBookingPaymentHandler` | **ไม่มีใคร** — `RecordBookingPaymentRequest(…, BankAccountId: null)` · `moneyIn = null` ⇒ ใบรับมัดจำไม่มีแหล่งเงิน ⇒ เงินสด | ❌ คลาสเดียวกัน |
| 5 | สลิปมัดจำที่พัก | `lodging.html` → `LodgingService.ConfirmAsync(request.BankAccountId)` | พนักงานเลือกบัญชีในฟอร์มของหน้าที่พักอยู่แล้ว (`LodgingPaymentBank`) | ✅ ไม่แตะ |
| 6 | สลิปคำสั่งซื้อเว็บ | `CmsCommerceService.ConfirmPaymentAsync` (ไม่มี intent) | ไม่มีใคร — `moneyInAccountId: null` ⇒ ใบรับเงินไม่มีแหล่งเงิน | ⚠️ คลาสเดียวกัน · **ไม่อยู่ในรอบนี้** (ไม่มี PaymentIntent — ต้องต่อด่านที่หน้าคำสั่งซื้อ · §5) |
| 7 | ปิดหนี้อัตโนมัติจากรายการเดินบัญชี | `BankReceiptSettleService` → `CreatePaymentAsync(OverrideBankAccountId: line.BankAccountId)` | บัญชีของ**รายการเดินบัญชีเอง** (หลักฐานแรงที่สุด — เงินอยู่ในบัญชีนั้นจริง) | ✅ ถูกอยู่แล้ว |
| 8 | หน้าบันทึกรับชำระของเอกสาร (มือ) | `documents.html` → `CreatePaymentAsync(OverrideBankAccountId)` | พนักงานเลือก "บัญชีรับ-จ่ายเงิน" | ✅ ไม่ใช่เส้นสลิป (ไม่มีปุ่มยืนยันสลิปในหน้าเอกสาร — ยืนยันที่คิว #1 ตามคำตัดสิน 259-3-3) |
| 9 | LINE bot "อนุมัติ" | `LineBotService` | ไม่มีคำสั่งยืนยันสลิปในบอท (มีแต่ผูกสลิปเงินเดือน) · รอบ 260-1 ส่งสลิปเข้า #2 | — ไม่แตะ (เจ้าของคือ 260-1) |
| 10 | มือถือ | — | ไม่มี endpoint ยืนยันสลิปแยก (ใช้หน้าเว็บเดียวกัน) | — |
| 11 | เงินผ่านผู้ให้บริการ (Omise ฯลฯ) ที่ webhook หาย แล้วกดยืนยันมือ | #1 | `IGatewayAccountResolver` ⇒ บัญชีพัก 11340 (เงินยังอยู่กับผู้ให้บริการ) | ✅ ถูก — **ห้าม**ให้เลือกธนาคาร (จะลงธนาคารด้วยเงินที่ยังไม่เข้า) |

## 2. สลิปบอกอะไรได้บ้าง

- **ไม่มีตัวอ่าน "บัญชีผู้รับ" ของสลิปลูกค้า** — `SlipOcrAssistService` ใช้เฉพาะสลิปค่าบริการ SaaS (ยอด/วันที่/อ้างอิง) · สลิปเอกสารเก็บแค่ `SlipFileSha256` + `SlipBankRef` (เลขอ้างอิงที่ลูกค้าพิมพ์)
  ⇒ รอบนี้**ไม่แต่ง**ว่า "สลิปอ่านได้ว่าเข้าบัญชี X" (F2 ข้อ 3) · หลักฐานที่มีจริงคือ **"บัญชีที่ระบบแสดงให้ลูกค้าโอน"** ของใบนั้น:
  `PaymentQrService.PickDocumentAccountAsync` (แหล่งเงินของใบ → บัญชีของใบ → บัญชีของเทมเพลต → บัญชีเดียวที่ตั้ง QR) → ถ้าไม่มี = บัญชีรับโอนของเว็บแถวแรก
  (`SiteTransferAccount.Rows`) — ลำดับเดียวกับกล่องโอนของหน้าชำระ/พอร์ทัล (`DocumentPayLinkService.TransferBlockAsync`)
- การอ่านบัญชีผู้รับจาก QR ท้ายสลิป (mini-QR ของธนาคาร) = งานรอบถัดไป (ต้อง OCR/QR decode + ตารางรหัสธนาคาร) — ตัวตัดสินรอบนี้มีช่องรับ "หลักฐานจากสลิป" ไว้เป็นชั้นแรกแล้ว**ไม่ส่ง**ค่าที่แต่งขึ้น

## 3. ผังบัญชีถูกเลือกอย่างไรวันนี้

`CreatePaymentAsync` → `DocumentMoneyChannel.ForPayment(overrideBank, overrideGl, doc.BankAccountId, doc.PaymentAccountId)` → `ResolvePaymentCashAccountAsync`
(ผังตรง > ธนาคาร→`LinkedAccountId` > เงินสด 111) · ยอด `BankAccount.CurrentBalance` ขยับเฉพาะเมื่อ `payment.BankAccountId` มีค่า

## 4. อะไรพังวันนี้ (หลักฐานจากโค้ด)

1. สลิปเอกสารที่ใบไม่ได้ตั้งแหล่งเงิน ⇒ JE `Dr 111 เงินสด / Cr ลูกหนี้` ทั้งที่เงินเข้าธนาคาร · `Payment.BankAccountId = null` ⇒ ยอดคงเหลือธนาคารในระบบไม่ขยับ
2. กระทบยอดธนาคาร: `FindBestJournalMatch` นับเฉพาะ JE ที่มีบรรทัดบนผังของธนาคาร (`bankCoaId`) ⇒ JE ของสลิปที่ลง 111 **ไม่มีวันเป็นผู้สมัคร** · สรุปบัญชี (`BuildSummary`)
   นับ `p.BankAccountId == account.Id` ⇒ ไม่นับ · ตัวตรวจ `DetectCashBankDiscrepanciesAsync` ฟ้อง "ควรเป็นบัญชีธนาคาร" ทุกใบ (อาการเดียวกันที่ผู้ใช้เห็น)
3. ใบเสร็จอัตโนมัติสืบทอด `BankAccountId` ของการชำระ ⇒ "ชำระโดย" บนใบเสร็จไม่มีธนาคาร
4. บริษัทที่มีหลายธนาคาร: ใบที่ตั้งธนาคาร A แต่ลูกค้าโอนเข้า B ⇒ ลง A · พนักงานไม่มีทางแก้ที่คิว (ต้องยกเลิกการชำระแล้วบันทึกใหม่ที่หน้าเอกสาร)

## 5. สิ่งที่สร้าง (สรุป — รายละเอียดใน DECISIONS 260-2-x)

- **OWNER** `Helpers/SlipReceivingAccount.cs` — ตัวตัดสินตัวเดียว: ใช้กับรายการไหน (`Applies`: manual-slip เท่านั้น) · ตรวจบัญชีที่เลือก (`ChoiceProblem`: ของบริษัทนี้ · เปิดใช้ · สกุลตรง ·
  ไม่ใช่ฝากประจำ · มีเลขบัญชี/พร้อมเพย์ · ผูกผังที่ลงรายการได้ผ่าน `DocumentMoneyChannel.EvaluateAsync`) · ค่าแนะนำ (`Suggest`: แหล่งเงินของใบ (=เดิม) → บัญชีที่แสดงให้ลูกค้าโอน →
  บัญชีรับโอนบัญชีเดียวของบริษัท → ไม่แนะนำ) · ผลตัดสิน (`Decide`) · ตัวโหลด `ResolveAsync`
- `ConfirmManually` รับ `bankAccountId` (ว่าง = ค่าแนะนำ) · ตรวจใต้ล็อกต่อรายการ · ประทับ `PaymentIntent.ReceivingBankAccountId/ReceivingAccountOrigin` · audit (เลือก vs แนะนำ)
- `GET intents/{id}/receiving-accounts` — ตัวเลือก + ค่าแนะนำ + เหตุผล (สิทธิ์/นโยบาย API key เท่าปุ่มยืนยัน)
- `DocumentPaymentHandler` + `SiteBookingPaymentHandler` อ่านค่าที่ประทับ (ไม่มี = ค่าแนะนำจากตัวเดียวกัน) ⇒ JE · Payment · ใบเสร็จ · กระทบยอดธนาคาร ไปบัญชีเดียวกัน
- หน้า: ช่องเลือกใน `Layout.formDialog` ของ payment-intents · คอลัมน์แสดงบัญชีที่ใช้ (echo)

**ไม่อยู่ในรอบนี้ (ตั้งใจ):** #6 สลิปคำสั่งซื้อเว็บ (ไม่มี intent — แตะ `CmsCommerceService`) · อ่านบัญชีผู้รับจาก QR ของสลิป · หน้าเอกสาร (ไม่มีปุ่มยืนยันสลิป)
