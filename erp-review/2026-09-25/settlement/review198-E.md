# รอบ 198 — ฝ่ายค้านทีม E (settlement เฟส 0) · main agent ตรวจเอง

> subagent ติดโควตารายสัปดาห์ถึง 1 ต.ค. 13:00 UTC ⇒ main agent เปิดไฟล์ตรวจเอง (F3 ข้อ 11 · 3 คำถาม: ทางเข้าอื่น? ทิศตรงข้าม? สถานะปลายทางประทับเองไหม?)
> ขอบเขต: `ef3d97b5` (merge `b8104136`) — เน้นเส้นเงิน: `GatewayRefundService` · `IntegrationService` I-1 · `PosService.Orders` P-1

## CONFIRMED — แก้แล้วในคอมมิตนี้

- **E-1 (P1) คืนเงินที่ผู้ให้บริการสำเร็จแล้ว แต่ exception ชนิดอื่นหลุด ⇒ กดคืนซ้ำได้ (เงินออกสองรอบ)**
  `GatewayRefundService.RefundCoreAsync` จับเฉพาะ `InvalidOperationException or DbUpdateException` หลัง `provider.RefundAsync` สำเร็จ ·
  `NpgsqlException` (เครือข่าย DB หลุดตอน SaveChanges นอก DbUpdate) · `OperationCanceledException` (ผู้ใช้ปิดหน้า/ct ถูกยกเลิก) ·
  exception อื่นจาก `JournalEntryBuilder` ⇒ หลุดออกไป: ไม่มีประวัติ · สถานะยัง `Succeeded` · `RefundedAmount` ไม่ขยับ ⇒
  `GatewayRefundMath.Check` ยอมให้คืนเต็มยอดอีกครั้ง.
  แก้: จับทุก exception ในเส้นหลังเงินออก · งานเขียนกู้คืน (rollback · event · สถานะผ่าน `ApplyChargeAsync`) ใช้ `CancellationToken.None`.
  (หลังแก้: สถานะเป็น `Refunded`/`PartiallyRefunded` + ยอดสะสม 0 ⇒ `Check` บล็อกการคืนเพิ่มผ่านระบบ — ทางเดิมที่ทีม E ตั้งใจไว้)

## PLAUSIBLE — backlog (ยังไม่แก้)

- ✅ cbd50b37 **E-2 (P2) `provider.RefundAsync` โยน exception (timeout) ⇒ ไม่รู้ว่าเงินออกหรือยัง แต่ระบบไม่บันทึกอะไรเลย** — แก้แล้ว (ทีม E2):
  จับ exception ของการเรียกผู้ให้บริการ → ประทับ `PaymentIntent.RefundOutcomeUnknownSince` + เหตุการณ์ "⚠️ ผลไม่แน่ชัด" ในธุรกรรมเดิมที่ยังถือล็อก
  แล้ว commit ⇒ `GatewayRefundMath.Check(..., refundOutcomeUnknown)` ปฏิเสธการคืนเพิ่ม · ปุ่ม "ตรวจผลการคืนเงิน" (`POST pay/intents/{id}/refund/verify`)
  อ่านยอดคืนสะสมจากผู้ให้บริการ (`ProviderCharge.RefundedTotal` ← Omise `refunded_amount` — ใช้ `GetChargeAsync` เดิม ไม่ต้องรอ `GetRefundStatus`)
  แล้วตัดสินด้วย `GatewayRefundMath.Verify` ตัวเดียว: เท่าที่บันทึก = ไม่ได้เกิด ⇒ ปลดล็อก · มากกว่า = เงินออกแล้ว ⇒ ลงบัญชีส่วนต่างด้วยตัวลงบัญชีคืนเงินตัวเดียว
  (`BookRefundAsync` · เวลาเงินออก = เวลาที่พยายามคืน) + ปลดล็อก · ไม่ส่งยอด/ขัดกัน ⇒ ล็อกต่อ (ไม่ประทับผลเอง) · รอบโอนที่มีรายการนี้บล็อก `RefundOutcomeUnknown` (ไม่ใช่ "ยอดไม่ตรง" ที่ชี้ไปแก้ค่าธรรมเนียม) · เทสต์ `E2_*` · ⚠️ ต้องยืนยันใน sandbox
  ว่า Omise charge ส่ง `refunded_amount` จริง (ถ้าไม่ส่ง = ล็อกค้างพร้อมข้อความ — ทิศปลอดภัย) · เดิม: tx rollback เงียบ ·
  ผู้ใช้เห็น error แล้วกดใหม่ได้ · ถ้าผู้ให้บริการประมวลผลไปแล้ว = คืนซ้ำ (ขึ้นกับ idempotency ฝั่งผู้ให้บริการ) ·
  เสนอ: จับ exception ของการเรียกผู้ให้บริการ → บันทึก event "ผลไม่แน่ชัด" + ล็อกการคืนเพิ่มจนกว่าจะ re-fetch สถานะจากผู้ให้บริการ
  (ต้องมี `IPaymentProvider.GetRefundStatus` — งานเฟส 2)
- ✅ <pending> (รอบ 200 ทีม G · `erp-review/2026-09-29/team-G.md`) **E-3 (P2 · พฤติกรรมเปลี่ยน — เจ้าของควรรู้) POS/Integration รับโอน/พร้อมเพย์ เมื่อบริษัทมีบัญชีธนาคารที่ผูกผัง 0 หรือ ≥ 2 บัญชี
  และไม่ได้ปักบัญชีที่เครื่อง ⇒ ปิดบิลไม่ได้** (เดิมลง prefix "112" = เงินลงทุนชั่วคราว ผิดเงียบ ๆ) · ถูกทิศตามหลัก "ล้มดัง" และมีทางไปต่อ
  (ปักบัญชีที่เครื่อง / ส่ง `bankAccountName`) · แต่บริษัทหลายบัญชีที่ใช้ QR ทุกบิลจะติดทันทีหลัง deploy — รวมบิล offline ที่ sync เข้ามา
  (เงินรับไปแล้ว แต่ sync ล้มจนกว่าจะตั้งค่า) · เสนอ: แจ้งเตือนหน้าตั้งค่า POS ล่วงหน้า / หน้าตรวจความพร้อมก่อน deploy
- ✅ <pending> (รอบ 200 ทีม G — ข้อความถึงผู้ใช้จริง 3 ทาง + บอกผังที่ต้องแก้) **E-4 (P3) integration ใบแจ้งหนี้: ผังลูกหนี้เปลี่ยนจาก "113 ตัวไหนก็ได้" เป็น 11310 ตรงตัว (หรือผังที่ปักบนผู้ติดต่อ)** —
  ผังที่ปรับแต่งเองโดยไม่มี 11310 ⇒ JE ใบแจ้งหนี้ถูก `Skip` (มี onSkip แจ้ง) · ถูกทิศ (เดิมอาจได้หัวกลุ่ม/ลูกหนี้อื่น) แต่เป็นการเข้มขึ้น

## NOT-A-BUG (เปิดไฟล์ยืนยันแล้ว)

- คืนเกินยอดจากเส้นลงบัญชีล้ม: `Check` บล็อก `PartiallyRefunded` + ยอดสะสม 0 และ `Refunded` ⇒ ไม่คืนซ้ำ (เมื่อสถานะถูกบันทึก — ดู E-1)
- POS: resolver โยนภายใน transaction ของ `CompleteOrderAsync`/`SyncOfflineOrderAsync`/`RefundOrderAsync` ⇒ rollback ทั้งบิล (ไม่มีครึ่งทาง)
- Integration `ProcessPaymentAsync`: หาผังก่อน `Payment` ถูกเพิ่ม · `BusinessRuleException` → `HandleSyncError` (log = Failed + ตอบ success=false)
- tenant: query ใหม่ทุกตัวมี `CompanyId == companyId` · สถานะ Refunded เดินผ่าน `ApplyChargeAsync` (เจ้าของสถานะตัวเดียว) หลังผู้ให้บริการตอบจริง (ไม่ประทับเอง)

## ยังไม่ได้ตรวจ (ค้างให้ฝ่ายค้าน subagent หลังโควตากลับ)
> ✅ <pending> ตรวจแล้วในรอบ 200 ทีม G — ผลอยู่ที่ `erp-review/2026-09-29/team-G.md` (G-2/G-3/G-4 · G-8 ทุก endpoint · หน้าเว็บ gateway)
`GatewaySettlementService` G-2/G-3/G-4 (สูตรรอบโอน · VAT/WHT ค่าธรรมเนียม) · `PaymentGatewayController` G-8 ครบทุก endpoint · หน้าเว็บ 4 หน้า
