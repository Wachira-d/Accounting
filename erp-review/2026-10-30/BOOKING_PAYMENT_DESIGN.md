# รับชำระล่วงหน้าของการจองนัดหมาย (CMS booking) — วิเคราะห์ + ออกแบบ (รอบ 218 · คำตัดสินข้อ 177)

> ที่มา: เจ้าของ (2026-10-30) "ติ๊ก **รับชำระมัดจำออนไลน์ (บริการชำระล่วงหน้า)** แล้ว แต่ตอนกดจองก็ไม่มีให้ระบุเกี่ยวกับการโอนมัดจำ ·
> ต้องตั้งค่าได้ว่าจะบังคับมัดจำ / จ่ายเต็มก่อน / ปิด · ตั้งเลขบัญชีหรือแนบ QR โอนเงินได้"
> สถานะ: **ทำแล้ว** (โค้ด + เทสต์ในรอบนี้) · ยังไม่ได้คอมไพล์ในเครื่องนี้ (CI คอมไพล์)

## 1. ทำไมเจ้าของไม่เห็นอะไร (reproduce)

เส้นทางค่าตั้งเดิม: `cms-edit.html` แท็บการจอง → "แจ้งเตือนลูกค้า (อีเมล / LINE)" → ☑ `#bssOnlineDeposit` → `PUT …/booking/site-settings`
(`OnlineDepositEnabled = true`). ค่านี้มีผู้อ่านสองตัวเท่านั้น (`CmsBookingService.FillCustomerFacingAsync` · `PublicPaymentResolver.ResolveBookingAsync`)
และทั้งคู่ผ่าน `CmsBookingOnlinePayment.DepositDue(type, status, deposit, paid)` ซึ่งคืน **0** เมื่อ:

| เงื่อนไข | ผลเดิม | เจ้าของเห็น |
| --- | --- | --- |
| บริการไม่ใช่ชนิด `PrePayment` (ค่าเริ่มต้นบริการใหม่ = `Lead`/แม่แบบ = `Appointment`) | ยอด 0 | ไม่มีกล่องจ่ายเลย |
| บริการ `PrePayment` แต่ไม่ได้กรอก "มัดจำ (บาท/%)" | `DepositAmount` = 0 | ไม่มีกล่องจ่ายเลย |
| มีมัดจำแต่บริษัทยังไม่ตั้ง gateway (`PaymentProviderConfig`) | `_payMethods()` ว่าง | "มัดจำ ฿x — โอนแล้วแจ้งร้าน" (ไม่มีเลขบัญชี/QR/ช่องแนบสลิป) |
| ทุกกรณี | — | **ก่อนกดจอง** หน้า modal ไม่บอกยอดต้องจ่าย/วิธีจ่าย (มีแค่ `policySummary` "มัดจำ ฿x" ตัวเล็ก) |

สรุป: ช่องติ๊กเดิมเป็น "สวิตช์ช่องทาง gateway" ที่วางผิดหมวด (อยู่ใต้แจ้งเตือน) ไม่ใช่นโยบายการชำระ · ไม่มีช่องทางโอน/QR/สลิปของการจองเลย
(คำสั่งซื้อ/ที่พักมี แต่การจองไม่มี) ⇒ ติ๊กแล้วไม่มีผล = **silent no-op** (กฎเหล็ก #4 A)

## 2. ค่าตั้งเรื่องเงินที่มีอยู่แล้ว (ก่อนรอบนี้)

| ที่ | ค่าตั้ง | ผู้อ่าน |
| --- | --- | --- |
| บริการจอง (`SiteBookingService`) | `BookingType` (Lead/Appointment/Guaranteed/PrePayment) · `DepositAmount` · `DepositPercent` · `AutoConfirm` · `FreeCancellationHours` · `CancellationFeePercent` | `CreateBookingAsync` (`BookingSlotEngine.Deposit`) · `SyncBookingToErpAsync` · `BookingCancellationFee` |
| เว็บ (`SiteBookingSettings`) | `OnlineDepositEnabled` · `GuaranteedInvoiceTiming` · `CompletionDocument` · `WaitlistHoldMinutes` | ดูข้อ 1 · `BookingInvoiceTimingPolicy` · `BookingReminderJob` |
| เว็บ (`SitePaymentGateway` — หน้า cms-orders "ตั้งค่าการรับเงิน") | `PromptPayId` · `PromptPayQrUrl` · `BankName/AccountNumber/AccountName` | หน้าคำสั่งซื้อสำเร็จ (`payment-options`) · `commerce/promptpay-qr` · portal |
| บริษัท (`PaymentProviderConfig`) | gateway (Omise ฯลฯ) + โหมดทดสอบ | `PaymentIntentService.StartAsync` · `public-pay/methods` |
| ที่พัก (`LodgingProperty`) | มัดจำ %/คงที่/min/max · `GuestConfirmMode` · `SlipDeadlineMinutes` · `PaymentHoldMinutes` | เส้นที่พักของตัวเอง (`LodgingHoldRule` · สลิป `LodgingSlipGallery`) |
| MTO (`SiteCommerceConfig`) | `MtoPaymentTermMode` · `MtoDepositPercent/FixedAmount` | `MtoPaymentTerms` |

## 3. เอกสารตามโหมด (DOCUMENT_FLOW §6.4 · ไม่เพิ่ม posting ใหม่)

เงินล่วงหน้าค่า **บริการ** = จุดเกิดภาษี §78/1 (วันที่รับชำระ) ⇒ ต้องออก **ใบรับเงินมัดจำ/ใบกำกับภาษี ลงวันที่รับเงิน** —
ใช้เส้นเดิมของ `PrePayment` ทุกตัวอักษร: จอง ⇒ ใบมัดจำ **ร่าง** (`AwaitingPaymentNote`) · รับครบ ⇒ `IssueDepositReceiptAfterPaymentAsync`
อนุมัติ **ลงวันที่รับเงิน** · ให้บริการเสร็จ ⇒ ใบส่วนที่เหลือหักฐานมัดจำ (R-09) · ยกเลิก/ไม่มา ⇒ ริบตามนโยบาย (R-10/R-11)

| โหมด | ยอดตอนจอง | สถานะหลังจอง | เอกสาร |
| --- | --- | --- | --- |
| ปิด (`Off`) | 0 | ตาม `AutoConfirm` | ไม่มีใบมัดจำ (`NoDepositNote`) |
| มัดจำบังคับ (`DepositRequired`) | ตามสูตร (คงที่ หรือ % · min/max) | **รอยืนยัน** จนรับครบ · มีกำหนดเวลา ⇒ ไม่จ่ายในเวลา = ยกเลิกอัตโนมัติ | ใบมัดจำร่าง → อนุมัติวันรับเงิน |
| มัดจำไม่บังคับ (`DepositOptional`) | ลูกค้าเลือก "จ่ายตอนนี้" ⇒ ตามสูตร · ไม่เลือก ⇒ 0 | ตาม `AutoConfirm` · ไม่มีการยกเลิกอัตโนมัติ | เลือกจ่าย = เหมือนบังคับ · ไม่เลือก = ไม่มีใบ |
| ชำระเต็มก่อน (`FullRequired`) | ราคาเต็ม | เหมือนมัดจำบังคับ | ใบมัดจำเต็มยอด → ตอนเสร็จ `RealizeDepositOnCompleteAsync` (เส้นเดิม "มัดจำ = ราคาเต็ม") |

**คำตัดสิน D1 — เก็บเงินล่วงหน้าได้เฉพาะบริการชนิด `PrePayment`**: ชนิดอื่นออกเอกสารคนละเส้น (Lead ไม่มีเอกสาร · Appointment = ใบเสนอราคา ·
Guaranteed = ใบกำกับตอนจอง/ตอนรับเงิน) — ถ้าเก็บเงินบนเส้นเหล่านั้นจะได้เงินที่ไม่มีใบ ณ tax point (ผิด §78/1) ⇒ ตัวตัดสินคืนโหมดที่มีผล = ปิด
พร้อม **เหตุผล** (`Problem`) · บันทึกบริการที่ตั้งโหมดเก็บเงินเองแต่ชนิดไม่ใช่ PrePayment = **ปฏิเสธพร้อมข้อความ** (ไม่ silent) ·
บริการที่รับค่าเว็บแต่ชนิดไม่ตรง ⇒ หน้าตั้งค่าแสดงรายชื่อ "ไม่มีผลกับบริการเหล่านี้" + ปุ่มเปิดฟอร์มบริการ

**คำตัดสิน D2 — ค่าเดิมไม่เปลี่ยนพฤติกรรม**: เว็บ/บริการที่ไม่เคยตั้งโหมด (`PaymentMode` NULL ทั้งสองชั้น) ⇒ `Legacy`:
PrePayment = มัดจำตามช่องเดิมของบริการ (ไม่มีกำหนดเวลา · `AutoConfirm` ตามเดิม) · ชนิดอื่น = ยอดมัดจำบนการจองคำนวณเหมือนเดิม (แสดงอย่างเดียว)

## 4. ออกแบบ (สิ่งที่สร้างในรอบนี้)

### 4.1 ตัวตัดสินเดียว `Helpers/BookingPaymentPolicy`
* ลำดับ: **โหมด** = บริการ (`SiteBookingService.PaymentMode`) ?? เว็บ (`SiteBookingSettings.PaymentMode`) ?? Legacy ·
  **สูตรยอด** = ช่องมัดจำของบริการ (`DepositAmount`/`DepositPercent` — มีค่าใดค่าหนึ่ง) ?? ของเว็บ · min/max/กำหนดเวลา แยกช่องถอยทีละช่อง
* `AmountDue(total, optIn)` — decimal · `Math.Round(…, 2, AwayFromZero)` · clamp [min, max] แล้ว [0, total] · เต็มยอด = total
* `HoldUntil(now, startUtc, minutes)` = min(now + N, เวลาเริ่มนัด) · เฉพาะโหมดบังคับ + ยอด > 0 + ลูกค้าจองเอง (พนักงานจองแทนไม่ถือ)
* ข้อความลูกค้า/ป้ายสถานะ (`รอชำระ` · `รอตรวจสลิป` · `ชำระแล้ว`) · ตัวเลือกโหมดสำหรับหน้าตั้งค่า (server ส่ง label + คำอธิบาย)

### 4.2 ช่องทางชำระ — `Helpers/BookingPaymentChannels`
* (ก) gateway: เว็บเปิด `OnlineDepositEnabled` (ช่องเดิม — ย้ายไปอยู่หมวด "การชำระเงินของการจอง") + บริษัทมี `PaymentProviderConfig` ที่ใช้งาน ⇒ `PaymentIntent` เดิม (`public-pay/bookings/…`)
* (ข) โอน/QR/สลิป: **เก็บที่ `SitePaymentGateway` ของเว็บ** (ตารางเดียวกับคำสั่งซื้อ · ตั้งได้จากหน้าตั้งค่าการจองหรือ cms-orders — แถวเดียวกัน ไม่มีสำเนา) + เพิ่ม `BankBranch` ·
  เลขบัญชีตรวจรูปแบบ (ตัวเลข 10–15 หลักหลังตัดขีด) · พร้อมเพย์ตรวจด้วย `PromptPayService.ValidatePromptPayId` · QR มียอด = สร้างที่เซิร์ฟเวอร์จาก PromptPay ID
  (`PromptPayService` EMVCo + CRC16 ตัวเดียวของระบบ — ไม่สร้าง helper ใหม่ · เพิ่มเทสต์ CRC) · ไม่มี PromptPay ID แต่อัปโหลดรูป QR ⇒ แสดงรูป (ยอดคงที่ไม่ได้ฝัง — ลูกค้ากรอกยอดเอง)
* **ไม่ตั้งระดับบริษัท**: บริษัทไม่มีที่เก็บบัญชีรับเงินสำหรับลูกค้า (มีแต่ `BankAccount` ฝั่งบัญชี) และคำสั่งซื้อใช้รายเว็บอยู่แล้ว ⇒ รายเว็บตัวเดียว (ไม่สร้างความจริงที่สอง)
* โหมดบังคับ/เต็มยอด แต่ไม่มีช่องทางใดพร้อม ⇒ **บันทึกค่าตั้งไม่ได้** (ข้อความ + ลิงก์ไปตั้งช่องทาง) — ไม่ใช่บันทึกแล้วไม่มีผล

### 4.3 สลิป (ทุกทางเข้าผ่าน `PaymentIntent` + `IPaymentProvider`)
* ลูกค้าแนบสลิปด้วยโทเคน (`POST …/booking/bookings/by-token/{token}/slip`) ⇒ `PaymentIntentService.StartAsync(providerCode: manual-slip)`
  (ยอดจากเซิร์ฟเวอร์ = มัดจำค้าง) + ไฟล์ผ่าน `IFileAttachmentService` (EntityType `SiteBooking` · ไม่ใช่ static สาธารณะ · ชนิดจากไบต์จริง · ≤ 10 MB) ·
  บนการจอง: `SlipAttachmentId` · `SlipUploadedAt` · `SlipIntentId` · มีสลิปรอตรวจ ⇒ ปิดช่องจ่าย gateway ชั่วคราว + job ไม่ยกเลิกอัตโนมัติ
* ร้านตรวจ (cms-bookings): **ยืนยัน** ⇒ `ApplyChargeAsync(Succeeded, Manual)` ⇒ `SiteBookingPaymentHandler` ⇒ `RecordDepositPaymentAsync` (ผู้เขียนเงินคนเดียว —
  ยืนยันการจอง + ใบมัดจำลงวันที่รับเงิน) · **ปฏิเสธ** (ต้องมีเหตุผล) ⇒ intent `Failed` + ล้างสลิป + เหตุผลให้ลูกค้าเห็น + แจ้งลูกค้า (`booking.slip-rejected`) + ต่อเวลาถือ 24 ชม.
* PDPA: สลิปมีชื่อ/เลขบัญชีผู้โอน ⇒ เปิดได้เฉพาะเจ้าของโทเคน หรือพนักงานที่มีสิทธิ์จัดการคำสั่งซื้อ/การจอง · เก็บตามอายุเอกสารบัญชี (หลักฐานรับเงิน ม.10 พ.ร.บ.การบัญชี 5 ปี) — ไม่ลบเมื่อปฏิเสธ (หลักฐานข้อพิพาท)

### 4.4 หน้าเว็บ
* ก่อนกดจอง: `POST …/booking/payment-preview` (บริการ + จำนวนช่วง) ⇒ โหมด · ยอดต้องจ่ายตอนนี้ · กำหนดเวลา · ช่องทาง · ข้อความยกเลิก/คืนเงิน (`BookingCancellationFee`) — หน้าแสดงอย่างเดียว
* หลังจอง/หน้า my-booking: กล่องชำระ (gateway + เลขบัญชี + QR + แนบสลิป) จาก `BookingResponse.Payment` (server ประกอบ) · สถานะ รอชำระ/รอตรวจสลิป/ชำระแล้ว
* "การจองของฉัน" (สมาชิก) แสดงป้ายสถานะชำระจากเซิร์ฟเวอร์ · ข้อความยืนยันการจอง (อีเมล/LINE) ต่อท้ายวิธีชำระ + กำหนดเวลา
* หลังบ้าน: หมวด "💳 การชำระเงินของการจอง" ใน cms-edit (บล็อกแยก) · ฟอร์มบริการมี "การชำระล่วงหน้า: ตามค่าเว็บ/กำหนดเอง" · cms-bookings มีกล่องตรวจสลิป

## 5. ความเสี่ยงที่ยังเปิด
* Guaranteed + เก็บเงินล่วงหน้า (ใบกำกับตอนรับเงิน) ยังไม่รองรับในโหมดนี้ — ใช้ปุ่ม "รับเงินแล้ว — ออกใบกำกับ" เดิม
* การเปลี่ยนชนิดบริการมีผลกับการจองเดิมของบริการนั้น (โค้ดเดิมอ่านชนิดสดจากบริการ) — ไม่แตะในรอบนี้
* รูป QR ที่อัปโหลด (ไม่มี PromptPay ID) ไม่ฝังยอด — ลูกค้าต้องกรอกยอดเอง · ร้านตรวจยอดตอนยืนยันสลิป
