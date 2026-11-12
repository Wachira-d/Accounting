# ฝ่ายค้านรอบ 260-2 — บัญชีที่เงินตามสลิปเข้า (abce9847 + 33289c92)

> ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านอย่างเดียว) · ตรวจบน `308ecb8c` (branch `claude/booking-service-features-sdemob`) · ยังไม่ได้คอมไพล์ในเครื่องนี้
> ทุกข้อตามถึงโค้ดจริงแล้ว · ข้อที่ตามไม่ถึงถูกตัดออก

## สรุป

| # | ระดับ | เรื่อง |
|---|---|---|
| M1 | MED | เลือกธนาคารที่ "ได้มาจากผังของเอกสาร" ⇒ ถูกยุบเป็น DocumentChannel ⇒ ลงผังตรง ไม่ขยับยอดธนาคาร/`Payment.BankAccountId` ทั้งที่จอแสดงว่าเลือกธนาคารนั้น |
| M2 | MED | `ShownDocumentAccountIdAsync` คืน `pick.ChosenId` ของบัญชีที่**ไม่ได้พิมพ์/ไม่ได้แสดง** (ไม่มี QR) ⇒ ข้ามบัญชีรับโอนของเว็บที่ลูกค้าเห็นจริงบนหน้าชำระ/พอร์ทัล ⇒ แนะนำผิดธนาคาร + ป้าย "บัญชีที่แสดงให้ลูกค้าโอน" ไม่จริง |
| M3 | MED | มีบัญชีรับโอนของเว็บ (ลูกค้าเห็น) แต่เลขไม่ตรงบัญชีไหนในระบบ ⇒ ตกไป "บัญชีรับโอนบัญชีเดียว" ⇒ ลงธนาคารที่หลักฐานบอกว่าไม่ใช่ |
| M4 | MED | ค่าที่ประทับไม่ถูกตรวจซ้ำตอน handler/ส่งต่อใหม่ + ยืนยันซ้ำ = 409 ⇒ แหล่งเงินของเอกสาร/บัญชีที่ประทับใช้ไม่ได้ (ปิดใช้ระหว่างนั้น) ⇒ รายการ "สำเร็จแต่ไม่ได้บันทึกรับชำระ" ค้าง ส่งต่อใหม่ล้มซ้ำทุกครั้ง ไม่มีทางเลือกบัญชีใหม่ |
| L1 | LOW | ล็อกต่อรายการไม่ครอบ `reject-slip` ⇒ ปฏิเสธ (Failed) กับยืนยันพร้อมกัน ⇒ ยืนยันชนะเงียบ (Failed→Succeeded ผ่านนโยบาย) |
| L2 | LOW | ยอมรับค่าแนะนำในหน้าต่าง (select ตั้งต้น = ค่าแนะนำ) ⇒ ถูกประทับเป็น `Chosen` "พนักงานเลือก" — ป้าย echo บนแถวกลบที่มา ShownToCustomer/SoleTransferAccount |
| L3 | LOW | stamp (`ExecuteUpdate`) กับ audit (`SaveChanges`) ไม่อยู่ธุรกรรมเดียว · audit ไม่มี `UserId`/`LegalReference` · audit ถูกเขียนแม้ `ApplyChargeAsync` ไม่เปลี่ยนสถานะ (โหมดไม่ตรง) แล้วคำตอบเป็น 200 |
| L4 | LOW | บัญชีรับโอนของเว็บจับด้วยตัวเลขล้วน ไม่เทียบธนาคาร (ผู้เขียนยกเอง) — ยืนยันว่าเป็นจริง แต่โอกาสชนต่ำ |
| L5 | LOW (เดิม · ไม่ใช่ของรอบนี้) | ใบรับมัดจำการจอง: บัญชีที่ส่งมีผลเฉพาะทางร่าง/สร้างใหม่ · รับบางส่วน = บัญชีของงวดสุดท้ายชนะทั้งก้อน |

ตรวจแล้วไม่ใช่บั๊ก (ห้ามรายงานซ้ำ): ดูท้ายไฟล์

---

## M1 (MED) — ธนาคารจากผังของเอกสารถูกยุบเป็น "ตามแหล่งเงินเอกสาร" ⇒ ลงผังตรง ยอดธนาคารไม่ขยับ

- `Accounting/Helpers/SlipReceivingAccount.cs:186-191` — เอกสารตั้งแหล่งเงินเป็น**ผังตรง** (`doc.PaymentAccountId = gl`) แล้วมีธนาคารผูกผังนั้นบัญชีเดียว ⇒ `docBank = linked[0]`
- `:100` `Suggest` ⇒ `DisplayBankAccountId = docBank` · หน้า (`payment-intents.html` select `value: ra.suggestedBankAccountId`) ตั้งค่าเป็นธนาคาร B
- `:130` `Decide`: `chosen == DisplayBankAccountId` ⇒ `Origin.DocumentChannel`, `BankAccountId = null` (ไม่ส่ง override)
- `DocumentService.cs:15488` `ForPayment(null,null,doc.BankAccountId,doc.PaymentAccountId)` ⇒ `Pick` เลือกผังตรงก่อน (`DocumentMoneyChannel.cs:141`) ⇒ `Payment.BankAccountId = null`, `OverridePaymentAccountId = gl`
- `DocumentService.cs:15683` ยอด `BankAccount.CurrentBalance` ขยับเฉพาะเมื่อ `payment.BankAccountId.HasValue` ⇒ **ไม่ขยับ** · `BankReceiptSettleService.cs:335` และตัวกรองผู้สมัครกระทบยอดที่อ่าน `Payment.BankAccountId` ไม่เห็นการชำระนี้

สถานการณ์: ใบแจ้งหนี้ตั้งแหล่งเงิน = ผัง 1112 (ผูกกับ "กสิกร 111-1") · ลูกค้าโอนเข้ากสิกร · พนักงานเปิดหน้าต่างเห็น "กสิกร 111-1" เลือกอยู่แล้ว กดยืนยัน ⇒ JE Dr 1112 ถูก แต่ยอดธนาคารกสิกรในระบบไม่ขยับ และแถวการชำระไม่มีบัญชีธนาคาร ⇒ กระทบยอด/สรุปบัญชีธนาคารตกหล่น — คลาสเดียวกับบั๊กที่รอบนี้ตั้งใจแก้ (ยอดธนาคารไม่ขยับ) แต่ซ่อนอยู่ใต้จอที่บอกว่าเลือกธนาคารแล้ว · Db test มีแค่กรณี `doc.BankAccountId` (`SlipReceivingAccountRound260DbTests` "ไม่เลือก_เอกสารตั้งธนาคารไว้") ไม่มีกรณีผังที่ผูกธนาคาร

แก้ขั้นต่ำ: ให้ `Suggestion` แยก "แหล่งเงินเป็นธนาคารจริง" (`channel.BankAccountId != null`) กับ "ธนาคารที่อนุมานจากผัง" · ยุบเป็น DocumentChannel (`:130`) เฉพาะกรณีแรก · กรณีผังที่ผูกธนาคาร ⇒ ส่ง `OverrideBankAccountId = docBank` (GL เท่าเดิม เพราะผังของธนาคารนั้นคือผังเดียวกัน + ยอดธนาคารขยับ) หรืออย่างน้อยไม่แสดงธนาคารเป็นค่าตั้งต้น · เทสต์ Db: ใบผังตรงที่ผูกธนาคาร ⇒ `pay.BankAccountId == bank` + `CurrentBalance` ขยับ (ทิศตรงข้าม: ผังตรงที่ไม่ใช่ธนาคาร เช่น เงินสดย่อย ⇒ ไม่แตะ)

## M2 (MED) — "บัญชีที่แสดงให้ลูกค้าโอน" รวมบัญชีที่ไม่ได้แสดง (ChosenId ไม่มี QR)

- `Accounting/Services/Implementations/PaymentQrService.cs:121` `return acct?.Id ?? pick.ChosenId;` — `ChosenId` = "บัญชีที่ถูกระบุชัด (มีแม้ไม่มี QR)" (`PaymentQrPolicy.cs:84`) ซึ่งกระดาษ**ไม่พิมพ์** (`AccountId` null = ไม่พิมพ์, `ProblemChosenNoQr`)
- กล่องโอนที่ลูกค้าเห็นจริง `DocumentPayLinkService.TransferBlockAsync` (`:543-548`): `DocumentAccountTransferAsync` คืน null เมื่อ `acct == null` ⇒ **แสดงบัญชีรับโอนของเว็บแทน**
- `SlipReceivingAccount.cs:194-196`: `shown = ChosenId` ⇒ `shown ??= SiteTransferMatchAsync` ไม่ถูกเรียก

สถานการณ์: เทมเพลตตั้ง "บัญชีที่ใช้ QR" = ไทยพาณิชย์ A (ยังไม่ตั้งพร้อมเพย์/รูป QR) · บัญชีรับโอนของเว็บ = กสิกร B (ลงทะเบียนเป็นบัญชีธนาคารด้วย) · ลูกค้าเปิดลิงก์ชำระ เห็นกสิกร B โอนเข้า B แนบสลิป · หน้าต่างยืนยันแนะนำ A พร้อมป้าย "บัญชีที่แสดงให้ลูกค้าโอน" ⇒ พนักงานเชื่อป้าย ⇒ JE/ยอดธนาคารลง A · กระทบยอดทั้งสองบัญชีเพี้ยน (F2 ข้อ 3: ป้ายที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง)

แก้ขั้นต่ำ: `ShownDocumentAccountIdAsync` คืน `acct?.Id` เท่านั้น (ให้ตรงกับ `DocumentAccountTransferAsync` ที่เป็นผู้แสดงจริง) · ถ้าอยากใช้ `ChosenId` เป็นหลักฐานอ่อน ให้เป็น origin แยก ("บัญชีที่ตั้งบนเทมเพลต") ไว้**หลัง**บัญชีรับโอนของเว็บ · เทสต์: เทมเพลตชี้บัญชีไม่มี QR + เว็บชี้ B ⇒ แนะนำ B

หมายเหตุเกี่ยวกัน: handler เรียก `ResolveAsync` ด้วย `attachmentsRoot` null ⇒ `""` (`PaymentQrService.cs:120`) ⇒ บัญชีที่มีแต่รูป QR ที่อัปโหลดไม่นับ ⇒ ค่าแนะนำทางเส้นไม่ประทับ (`SiteBookingPaymentHandler` ส่ง null เสมอ · `DocumentPaymentHandler` เมื่อ `_storage` null) ต่างจากที่ลูกค้าเห็น — สำหรับเส้นเอกสารหลังรอบนี้ทุกการยืนยันผ่านคิวถูกประทับแล้ว จึงกระทบเฉพาะการส่งต่อใหม่ของรายการเก่า (LOW)

## M3 (MED) — บัญชีรับโอนของเว็บที่ไม่ตรงบัญชีใดในระบบ ⇒ ตกไปบัญชีเดียวของบริษัท

- `SlipReceivingAccount.cs:238-247` `SiteTransferMatchAsync` คืน null ทั้งกรณี "ไม่มีแถวรับโอน" และ "มีแถว (ลูกค้าเห็น) แต่เลขไม่ตรง/ตรงหลายบัญชี"
- `:104-107` ⇒ `usable.Count == 1` ⇒ `SoleTransferAccount`

สถานการณ์: ร้านตั้งบัญชีรับโอนของเว็บ/พอร์ทัลเป็นพร้อมเพย์หรือบัญชีที่ยังไม่ได้ลงในหน้า "บัญชีธนาคาร" · ในระบบมีธนาคารบัญชีเดียว (บัญชีจ่ายเงินเดือน) · ลูกค้าโอนตามที่เห็น ⇒ สลิปการจอง (`SiteBookingPaymentHandler` — หน้าการจองเลือกไม่ได้เลย) และสลิปเอกสาร (ค่าตั้งต้น) ลงธนาคารที่หลักฐานชี้ว่า**ไม่ใช่** · เดิมลงเงินสด (ผิดเหมือนกัน แต่ไม่ทำให้ยอดธนาคารที่กระทบยอดได้เพี้ยน) · ขัด 260-2-2 ข้อ 4 "หลายบัญชี/ไม่มีหลักฐาน = ไม่เดา" — ตรงนี้มีหลักฐานขัดแย้งแต่ยังเดา

แก้ขั้นต่ำ: ให้ `SiteTransferMatchAsync` คืนสามสถานะ (ไม่มีแถว / ตรง id / มีแถวแต่ไม่ตรง) · "มีแถวแต่ไม่ตรง" ⇒ `Origin.None` + เหตุ "บัญชีที่แสดงให้ลูกค้า (เลข …) ไม่ตรงบัญชีธนาคารในระบบ" ไม่ตกไป Sole · เทสต์ทิศตรงข้าม: ไม่มีแถวเว็บ + บัญชีเดียว ⇒ Sole เหมือนเดิม

## M4 (MED) — ค่าที่ประทับไม่ถูกตรวจซ้ำ + ยืนยันซ้ำ 409 ⇒ รายการค้างแก้ไม่ได้

- `ResolveAsync` ตรวจนอกล็อก (`PaymentGatewayController.cs:428`) แล้วประทับใต้ล็อก (`:449`) · handler อ่านค่าที่ประทับโดยไม่ตรวจซ้ำ (`SlipReceivingAccount.cs:215`)
- `Suggest` คืน `DocumentChannel` แม้ธนาคารของเอกสารใช้ไม่ได้ (`:99-101` — `usable` ใช้แค่ตัดสิน display) ⇒ `ConfirmManually` ไม่บล็อก
- `CreatePaymentAsync` → `ResolvePaymentCashAccountAsync` (`DocumentService.cs:21106`) ล้มดังเมื่อบัญชีปิดใช้/ผังใช้ไม่ได้ ⇒ `DispatchSucceededAsync` จับ ⇒ ⚠️ "ดำเนินการต่อไม่สำเร็จ"
- ส่งต่อใหม่ใช้ค่าที่ประทับเดิม ⇒ ล้มซ้ำ · กดยืนยันใหม่เพื่อเลือกบัญชีอื่น = 409 (`:442`) · ไม่มีปลายทางให้เปลี่ยนบัญชี

สถานการณ์: (ก) ใบตั้งแหล่งเงินเป็นธนาคารที่ปิดใช้ไปแล้ว ⇒ ยืนยันสลิป "สำเร็จ" แต่ไม่มีการรับชำระ และทางแก้เดียวคือเปิดบัญชีเก่ากลับ หรือบันทึกรับชำระมือแยก (ซึ่งรายการชำระยังค้าง ⚠️ ส่งต่อใหม่ได้ ⇒ เสี่ยงรับซ้ำภายหลัง) · (ข) ปิดใช้บัญชีระหว่างโหลดหน้าต่างกับกดยืนยัน (ผู้เขียนยกเอง) — ผลเดียวกัน

แก้ขั้นต่ำ: (1) `ConfirmManually` ตรวจผลสุดท้ายก่อนประทับ: `Decision.BankAccountId ?? (DocumentChannel ? docChannel : null)` ผ่าน `DocumentMoneyChannel.EvaluateAsync` ใต้ล็อก (ใช้ไม่ได้ = 400 พร้อมเหตุ ให้เลือกบัญชีอื่น) · (2) ให้ `Suggest` ที่แหล่งเงินเอกสารใช้ไม่ได้คืน `None` + เหตุ แทน DocumentChannel · (3) ส่งต่อใหม่ของรายการที่ค้าง ⚠️ รับ `bankAccountId` ใหม่ได้ (ประทับทับ + audit) หรือ handler ตรวจค่าที่ประทับแล้วล้มพร้อมข้อความชี้ทางไปต่อ

## L1 (LOW) — reject-slip ไม่อยู่ใต้ล็อกเดียวกัน

`PaymentGatewayController.cs:437` ล็อก `{id}:confirm` แต่ `reject-slip` (`:531`→`ApplyChargeAsync(Failed)`) ไม่ถือคีย์นี้ · ลำดับ: ยืนยันอ่านสถานะ Pending → ปฏิเสธเขียน Failed → ยืนยัน `ApplyChargeAsync(Succeeded)` ซึ่ง `Evaluate(Failed→Succeeded).Apply = true` (ตั้งใจให้รายการล้มยืนยันได้) ⇒ สลิปที่เพิ่งถูกปฏิเสธว่าปลอมกลายเป็นรับเงินแล้ว คำตอบ 200 · มีมาก่อนรอบนี้ แต่รอบนี้อ้างว่า "สองคนกดพร้อมกัน serialize แล้ว" · แก้: ให้ reject-slip ถือ `JobLock` คีย์เดียวกัน (หรือส่ง "สถานะที่คาด" เข้า `ApplyChargeAsync` ตรวจใต้ xact lock)

## L2 (LOW) — ยอมรับค่าแนะนำ = "พนักงานเลือก"

หน้า (`payment-intents.html` `value: ra.suggestedBankAccountId || ''`) ส่ง id ของค่าแนะนำกลับมาเป็น `bankAccountId` ⇒ `Decide` `:132` ⇒ `Origin.Chosen` · `Overridden` = false ถูก แต่ `ReceivingAccountOrigin` ที่ echo บนแถว = "พนักงานเลือก" ทั้งที่ที่มาคือ ShownToCustomer/Sole · แก้: `Decide` — chosen == `Suggested.BankAccountId` ⇒ คงที่มาของค่าแนะนำ (หรือหน้าส่งค่าว่างเมื่อไม่แตะ)

## L3 (LOW) — audit/stamp ไม่อะตอมิก · ขาดฟิลด์

`:448-469` `ExecuteUpdateAsync` commit ทันที · `AddChainedAuditLog`+`SaveChangesAsync` แยก ⇒ audit ล้ม = stamp ค้างไม่มี audit (ยืนยันใหม่ประทับทับได้ จึงไม่เสียหายถาวร) · audit ไม่มี `UserId` (มีแค่ `UserEmail = "manual:{guid}"`) และไม่มี `LegalReference` (กฎ M) · `ApplyChargeAsync` คืน intent โดยไม่เปลี่ยนสถานะได้ (โหมดไม่ตรง — `PaymentIntentService.cs:416-423`) ⇒ `updated != null` ⇒ 200 "ยืนยันแล้ว" + audit "Decided" ทั้งที่ไม่ได้ยืนยัน (คำตอบผิดมีมาก่อน) · แก้: ห่อ stamp+audit ใน tx เดียว · ตรวจ `updated.Status == Succeeded` ก่อนตอบ 200

## L4 (LOW) — จับเลขบัญชีด้วยตัวเลขล้วน

`SlipReceivingAccount.cs:152-158` ไม่เทียบ `BankName` · เลขซ้ำข้ามธนาคาร (≥6 หลักตรงกันทุกหลัก) โอกาสต่ำ และซ้ำสองบัญชี = ไม่เดาอยู่แล้ว · แก้ (ถ้าจะทำ): ถ้าทั้งสองฝั่งมีชื่อธนาคาร ให้ต้องตรงด้วย

## L5 (LOW · มีอยู่เดิม) — ใบรับมัดจำการจอง

`CmsBookingService.Payment.cs:158-229`: `bankAccountId` มีผลเฉพาะ (ก) ยังไม่มีใบ ⇒ `SyncBookingToErpAsync` (ข) ใบเป็นร่าง ⇒ `doc.BankAccountId = …` · ใบออกแล้ว/ชนิด Guaranteed-Appointment ⇒ ไม่ใช้ (ไม่มี JE เงินเข้าที่เส้นนี้ — ถูกแล้ว) · รับบางส่วน (`:165`) ⇒ คืนก่อน — งวดสุดท้ายที่ครบมัดจำกำหนดแหล่งเงินของ**ทั้งก้อน** (สลิปงวดแรกเข้าธนาคาร + งวดสองเงินสด ⇒ ทั้งก้อนตามแหล่งเงินเดิมของร่าง) · และถ้าร่างมี `PaymentAccountId` อยู่แล้ว ผังตรงชนะ `BankAccountId` (`DocumentMoneyChannel.Pick`) · ไม่ใช่ของรอบนี้ — บันทึกไว้ให้รอบจองนัดหมาย

---

## ตรวจแล้วไม่ใช่บั๊ก

- **ข้อมูลค้างใน tracker หลัง ExecuteUpdate**: `FindAsync` เป็น `AsNoTracking` และ `ApplyChargeAsync` `ReloadAsync` หลังได้ xact lock (`PaymentIntentService.cs:408-413`) ⇒ handler เห็นค่าที่ประทับจริง
- **ล็อกซ้อน/ธุรกรรมซ้อน**: `JobLock.RunExclusiveAsync` = session lock บน connection ที่เปิดเอง ไม่เปิดธุรกรรม ⇒ `BeginTransactionAsync` ข้างใน `ApplyChargeAsync` ไม่ชน · คีย์ต่างกัน (`{id}:confirm` กับ `{id:N}`)
- **JE / ยอดธนาคารซ้ำสอง**: `CreatePaymentAsync` ตั้ง `BankAccountId = OverrideBankAccountId` และขยับ `CurrentBalance` ครั้งเดียว (`:15683`) · ขา WHT ไม่เปลี่ยน · Dr ผังของธนาคาร / Cr ลูกหนี้ (Db test ยืนยัน)
- **กระทบยอดอัตโนมัติจับคู่ซ้ำ**: `BankReceiptSettleService` ดูเฉพาะใบ `BalanceDue > 0` · สลิปยืนยันก่อน ⇒ ใบปิดแล้วไม่เป็นผู้สมัคร · รายการเดินบัญชีปิดก่อน ⇒ `ConfirmManually` บล็อก `ExceedsBalance` (ยอดค้าง 0) · ไม่เกิดการชำระสองแถวจากเงินก้อนเดียวในเส้นที่รอบนี้แตะ
- **จ่ายเกินจากรายการหมดอายุ/ล้ม**: `ExceedsBalance` (`:418`) + `CreatePaymentAsync` ปฏิเสธจ่ายเกิน ⇒ ไม่ไปเงินรอจัดสรร · ปุ่มยืนยันบนหน้ามีเฉพาะแถวเปิด (`canCheckLive`) ⇒ `canChooseReceivingAccount` (IsOpen) สอดคล้อง
- **สกุลเงิน**: สลิปเอกสารเป็น THB (`DocumentPayLinkPolicy.IsThb`) · `UsableProblem` เทียบสกุลกับ `intent.Currency`
- **สิทธิ์/ทางเข้า**: `confirm-manually` + `receiving-accounts` = `BankReconcile` + `RejectApiKey` · สลิป LINE (`LineChatSlipController.Assign`) เข้าคิวเดียวกัน ไม่มีทางยืนยันเอง · สลิปการจองถูกบล็อกที่ `ConfirmManually` (`:403-405`) และยืนยันผ่าน `BookingSlipService` ⇒ handler ค่าแนะนำ · ไม่พบทางยืนยันสลิปเอกสารทางอื่น (มือถือ/LINE bot)
- **Tenant isolation**: ทุก query ใน `SlipReceivingAccount` กรอง `CompanyId` · บัญชีที่เลือกต้องอยู่ใน `LoadAccountsAsync(companyId)` · ป้ายบนรายการ `IgnoreQueryFilters` + `CompanyId`
- **409 กับการกดซ้ำ**: กดซ้ำระหว่างทำงาน ⇒ `ran=false` 409 · ลองใหม่หลังสำเร็จ ⇒ 409 พร้อมข้อความให้โหลดหน้าใหม่ (ไม่มีผลเงิน) — ยอมรับได้
- **migration**: `varchar(32)` พอสำหรับชื่อ enum ยาวสุด (`SoleTransferAccount`)
