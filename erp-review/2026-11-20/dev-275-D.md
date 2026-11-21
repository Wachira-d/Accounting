# รอบ 275 แพ็ก D — โต๊ะสลิปภาษี (ผลตรวจ T1 · คำตัดสินแทนเจ้าของ 4.3)

> branch `claude/booking-service-features-sdemob` · commit หลัก `2c247bd4` · ผลตรวจต้นทาง `audit-T1-line-slip-payment.md` · คำตัดสิน 275-D-1…4 ใน `erp-review/2026-09-29/DECISIONS.md` ·
> สถานะปัจจุบันอยู่ใน `DOCUMENT_FLOW.md` §TAX272 "รอบ 275 แพ็ก D" · เทสต์ T275D-01…08 ใน `TEST_PLAN.md`

## ตัวตัดสิน (Helpers/ ไฟล์เดียว `TaxSlipDeskRule.cs`)

| คลาส | หน้าที่ |
| --- | --- |
| `TaxSlipDeskRule` | `CandidateBlocker` (ผู้สมัครที่ล้มแน่นอน) · `ManualProblem` (บันทึกเอง: ภาษี+เงินเพิ่ม+เบี้ยปรับ = ยอดสลิปทุกสตางค์) · `LinkProblem` (ผูกกับรายการนำส่งที่มีอยู่) |
| `TaxSlipChannelRedirect` | ไฟล์เดิมจากกลุ่ม/ผู้ส่งอื่นที่แถวเดิมรอพนักงาน ⇒ ประมวลใหม่ |
| `TaxPaymentRequestLifecycle` | สถานะใบขอชำระ Open/Paid/Cancelled/Expired · ปิดเมื่อนำส่ง · หมดอายุ · หลังยกเลิกรายการ |
| `TaxSlipOpenAge` | แถวรอพนักงาน: เตือน 2 วัน (วันละครั้ง) · ปิด 90 วัน |

## สถานะทีละข้อ

| ข้อ | สถานะ | ที่แก้ |
| --- | --- | --- |
| H1 ไม่มีทางบันทึกสลิปที่ไม่มีผู้สมัคร | ✅ แก้แล้ว | "บันทึกเอง" `StaffManualRemitAsync` (คำตัดสิน 4.3 · ไม่มีผู้อนุมัติคนที่สอง · OCR ล่มกรอกยอดที่อ่านเองได้) · "ผูกกับรายการนำส่งที่มีอยู่" `LinkAsync` (การ์ด ✅ ตามจริง) · "ไม่บันทึก" ไม่ลบไฟล์ทันที · ตัวเลือกจาก `GET slips/{id}/manual-options` |
| H2 สถานะใต้ล็อกจาก tracker · ทางล้มเขียนทับ Remitted | ✅ แก้แล้ว | `LockedSlipAsync` (FOR UPDATE + Reload · ปฏิเสธทุกสถานะที่ไม่ใช่รอตัดสิน = L9) · `ApplyAndReplyAsync` อ่านแถวใหม่ ไม่แตะแถวที่ปิด · claim atomic ใน `HandleChoicePostbackAsync` + `StaffRejectAsync` · ปิดปุ่มระหว่างรอ · Db test สองการเชื่อมต่อยืนยันสถานการณ์ 2 (เดิมได้ 2 แถวนำส่ง) |
| M1 ยกเลิกรายการนำส่งไม่ปล่อยสลิป/ใบขอชำระ | ✅ แก้แล้ว | `VoidRemittanceAsync` → `TaxSlipRemitService.ReleaseOnVoidAsync` (สลิป ToStaff `TAX-SLIP-VOIDED` · ใบขอชำระ Open/Expired · audit) · บันทึกใหม่อ่านไฟล์จากไฟล์แนบเดิม |
| M2 ปุ่มที่ล้มแน่นอนส่งการ์ดทุกครั้ง | ✅ แก้แล้ว | `CandidateBlocker` บนคิว (option disabled + เหตุ) และใน `StaffRemitAsync` · พนักงานกดล้ม = 409 ไม่ส่งการ์ด ไม่เปลี่ยนเหตุเดิม |
| M3 กันซ้ำด้วยแฮชไฟล์ล็อกกับช่องทางแรก | ✅ แก้แล้ว | `TaxSlipChannelRedirect` + `ReprocessFromNewChannelAsync` (audit `TaxSlipResentFromOtherChannel`) |
| M4 งานลบ 90 วันไม่มีแถว · แถวรอตัดสินไม่มีกำหนด | ✅ แก้แล้ว | ไม่ลบตอนปฏิเสธ · `PurgeClosedFilesAsync` ครอบ Rejected+Expired · `TaxSlipOpenAge` (คำตัดสิน 275-D-3) · OCR-OFF อ่านใหม่เมื่อ OCR กลับ · คอลัมน์ `StaffRemindedAt` |
| M5 สลิปค่าบริการใน OA กลางไม่มีเส้น | ◐ ขั้นต่ำ | การ์ด `TransferNotTax` ตามจริง + กระดิ่งพนักงาน (ไม่เก็บรูป) · **เลื่อน:** ส่งต่อเข้าเส้นจับคู่ใบแจ้งหนี้ของผู้ให้บริการ (ต้องออกแบบ — 275-D-4) · **รอบ 282 ปิดแล้ว** (คำตัดสิน 282-1 · `TaxSlipIntakeService.IntakeAsync` ⇒ `LineChatSlipService.HandleForwardedAsync` · `erp-review/2026-11-21/dev-282-invoice-slip-group-email.md`) |
| M6 เส้น 260 ไม่รู้จักสลิปรัฐ | ✅ แก้แล้ว | `LineChatSlipRule.Decide` → `GovPaymentSlip.Identify` ก่อนจับคู่ ⇒ `Reasons.GovernmentSlip` + คำตอบ `Replies.GovernmentSlip` |
| M7 วงจรใบขอชำระไม่ปิด | ✅ แก้แล้ว | `CloseOnRemitAsync` ใน `RemitAsync` (ทุกเส้น) และ `LinkAsync` · `ExpireDueAsync` ใน `TaxSlipJob` + การ์ด · ยกเลิกแจ้งกลุ่ม · ทีละใบต่อ (บริษัท·แบบ·งวด) |
| M8 ไม่พบเงินออกประทับถาวร | ✅ แก้แล้ว | ตรวจซ้ำ 60 วัน · ไม่มีรายการเดินบัญชีหลังช่วง ⇒ ยังไม่ตรวจ · ผลเดิมไม่กระดิ่งซ้ำ |
| L1 การ์ดอีเมลรอบหลังไปอีเมลหลักของผู้ติดต่อ | ⏸ เลื่อน | ต้องเก็บที่อยู่ตอบกลับแบบเข้ารหัสบนแถว (คอลัมน์ใหม่ + PII) — รวมกับแพ็ก A (คีย์) จะไม่ชนกัน |
| L2 ข้อความ exception ถึงกลุ่ม | ✅ | `cardReason` — เฉพาะ `TaxSlipGateException`/`BusinessRuleException`/23505 ที่เหลือข้อความกลาง (เหตุจริงอยู่บนแถว) |
| L3 เหตุ "ไม่พบรายการ" ทั้งที่นำส่งแล้ว | ✅ | `TaxSlipMatch.Decide(remittedTotals)` ⇒ `AlreadyRemitted` + ชี้ทาง "ผูก" |
| L4 ตัวเลือกลูกค้าหายหลังรอความยินยอม | ✅ | `DecideAndActAsync`: Many + ผู้สมัครเดิมตัวเดียวที่ยังอยู่ในชุด ⇒ ใช้ตัวที่เลือก |
| L5 แถวค้าง Received | ✅ | `RereadAsync` ประมวล Received ค้าง > 10 นาที |
| L6 อนุญาตแบบไม่รวมสลิปที่พัก ⇒ ย้ายเงียบ | ✅ | กระดิ่งผู้ให้บริการ |
| L7 สลิปภาษีส่ง 1:1 กับ OA กลาง | ⏸ เลื่อน | ต้องยืนยันผล `ScanAsync` กับสลิปธนาคารจริงก่อน (ผลตรวจเองติด "ต้องยืนยัน") · ไม่แตะเส้น OCR โยนบิล |
| L8 "สมทบ" | ✅ | นับเป็นคำของรัฐเฉพาะเมื่อคู่ป้ายจ่ายบิล · สลิป สปส. จริง (Biller ID) ยังเป็นสลิปรัฐ |
| L9 Rejected ที่ไฟล์ยังไม่ลบบันทึกได้ | ✅ | ปิดพร้อม H2 (`LockedSlipAsync`) |

## ชุดแก้ฝ่ายค้าน (ไม่พบ HIGH)

| ข้อ | ที่แก้ |
| --- | --- |
| MED-1 ผูก/บันทึกเองตรวจแค่ยอด | `TaxSlipDeskRule.SlipTaxIdOther` (ตัวเดียว · ย้ายจาก intake) + `AgencyTypeProblem` (สลิปกรมสรรพากร ⇒ แบบของกรมเท่านั้น · สลิป สปส. ⇒ สปส.1-10 เท่านั้น) ใน `LinkProblem`/`ManualProblem` · หน่วยงานของสลิปที่ไม่มีแถวผู้รับชำระมาจาก `GovPaymentSlip.AgencyHint` (ประทับตอนรับ/อ่านใหม่) · เทสต์สองทิศ pure + Db `MED1_*` |
| MED-2 ปิดแถว/ใบหมดอายุเขียนทับสถานะที่เพิ่ง commit | `AgeOpenRowsAsync` · `ExpireDueForAsync` · `CancelAsync` = `UPDATE … WHERE Status = expected` ทีละแถว · การ์ดเฉพาะแถวที่เปลี่ยน · Db test แข่งเวลา (ถือล็อกแถวในอีกธุรกรรม) `MED2_*` ×2 |
| MED-3 สลิปที่ปล่อยหลัง void นับอายุจาก CreatedAt | คอลัมน์ `TaxSlipEvidences.ReleasedAt` · อายุ = `ReleasedAt ?? CreatedAt` |
| LOW-1 การ์ดรอบแรกหลัง deploy | การ์ดสรุปใบเดียวต่อกลุ่ม/ผู้ส่ง (สลิป) และต่อบริษัท (ใบขอชำระ) ต่อรอบ · เพดาน 20 ต่อกลุ่ม/บริษัทต่อรอบ |
| LOW-2 หมดอายุทั้งที่มีสลิปเปิดอ้าง | ข้ามใบที่มีแถวสลิปสถานะรอตัดสินอ้าง `TaxPaymentRequestId` |
| LOW-3 ปิดใบด้วยการนำส่งบางส่วน · void แล้วไม่แจ้ง | `TaxPaymentRequestLifecycle.PaidBy` (ยอดรวมเท่าใบ + Control Code ถ้ารู้) · void ⇒ `ReopenedAt` ⇒ `TaxSlipJob` ส่งการ์ด `PaymentRequestReopened` ครั้งเดียว (`ReopenNoticeAt`) |
| (ผู้ประสานงาน) เลขผู้เสียภาษีเทียบได้แค่ 7 หลักแรก | `TaxSlipEvidences.TaxIdHash` (HMAC `TaxSlipRefHash.TaxId` — กุญแจเมล็ดถาวรของแพ็ก A MED-4 ซึ่งตั้งผ่าน `HmacSeedBootstrap.Configure` → `TaxSlipRefHash.ConfigureKey` แล้ว จึงไม่ต้องย้ายตาม) · `TaxIdVerdict` · แถวเก่า ⇒ พนักงานยืนยันจากรูป (audit) · ไม่ backfill (เลขเต็มไม่เคยถูกเก็บ) · ลบบรรทัดซ้ำจาก resolve conflict 3 บรรทัด (CHANGELOG ×2 · DOCUMENT_FLOW ×1) · `f32c38ed` |
| LOW-4 ทางล้มของเส้นอัตโนมัติ | `MoveToStaffIfOpenAsync` (conditional) ใช้ที่ ApplyAndReplyAsync · ProcessAsync catch · postback catch |

## ตรวจแล้วไม่ใช่บั๊ก / ปรับจากผลตรวจ

- M2 ผลตรวจรวม `BooksMismatch`/`BooksChanged` ไว้ใน "ล้มทุกครั้งโดยโครงสร้าง" — **ไม่จริงทั้งหมด**: ด่านคือยอดค้างในสมุด ณ ตอนกด (`ExpectedAmount`) ถ้าผู้ทำบัญชีแก้สมุดแล้วกดได้ ⇒ ไม่บล็อกปุ่ม (บล็อกเฉพาะ ยอดสลิปไม่ตรง · ใบขอชำระปิด · งวดนำส่งแล้ว)
- H2 สถานการณ์ 2 ยืนยันแล้วว่าเป็นบั๊กจริงด้วย Db test (ไม่ใช่ข้อสันนิษฐาน) — ก่อนแก้ได้ 2 แถวนำส่ง

## การเชื่อมต่อ (ทุกทางเข้าเดินด่านเดียวกัน)

- LINE webhook (OA กลาง) / อีเมลขาเข้า → `IntakeAsync` → `ApplyAsync` (LockedSlipAsync) · ปุ่มเลือก → claim atomic → `ApplyAsync` · โต๊ะพนักงาน (บันทึก 1 คลิก/บันทึกเอง/ผูก/ไม่บันทึก) → `ApplyAsync`/`LinkAsync` (LockedSlipAsync) ·
  `TaxSlipJob` → `RunHeldAsync` → `ProcessAsync` → `ApplyAsync` · ทางนำส่งมือเดิม `StatutoryRemittanceService.RemitAsync` ปิดใบขอชำระของงวด + `VoidRemittanceAsync` ปล่อยสลิป
- ผู้อ่านสถานะ Remitted ที่เขียนทับได้: เหลือ 0 จุด (`ProcessAsync`/postback catch/`ApplyAndReplyAsync` ตรวจ `Statuses.Open` ก่อนตั้ง ToStaff · `StaffRejectAsync` atomic)
- ไม่แตะรายงาน ภ.ง.ด. (แพ็ก B) และใบเสร็จ (แพ็ก C)

## สิ่งที่เจ้าของต้องทำหลัง deploy

1. ไม่มีขั้นตอนบังคับ — migration เพิ่มคอลัมน์ `TaxSlipEvidences.StaffRemindedAt` อัตโนมัติตอนบูต
2. ครั้งแรกที่ `TaxSlipJob` รัน: แถว "รอพนักงานตรวจ" ที่ค้างเกิน 2 วันจะได้กระดิ่งรวม 1 ครั้ง · แถวที่ค้างเกิน 90 วัน (ถ้ามี) จะถูกปิดและลูกค้าได้การ์ด "ยังไม่ได้บันทึก · ปิดรายการแล้ว" — ถ้าไม่ต้องการ ให้เคลียร์คิวก่อน deploy
3. ใบขอชำระที่ Control Code หมดอายุแล้วจะเปลี่ยนเป็น "หมดอายุ" + การ์ดเข้ากลุ่ม (ครั้งแรกอาจหลายใบ)
4. สปส.1-10: แบบนี้ยังให้ความยินยอมบนเว็บไม่ได้ (`ReaderWired=false`) ⇒ "บันทึกเอง" ของ สปส. ใช้ได้เมื่อมีหนังสือมอบอำนาจที่อนุมัติแล้ว หรือผู้กดเป็นผู้ยื่นภาษีของบริษัทลูกค้า — ถ้าต้องการให้ความยินยอมบนเว็บครอบ สปส. สำหรับการบันทึกโดยพนักงาน ต้องตัดสินเพิ่ม
