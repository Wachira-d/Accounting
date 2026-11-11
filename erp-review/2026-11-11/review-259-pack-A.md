# ฝ่ายค้าน รอบ 259 แพ็ก A — เงินรอจัดสรร: ขาออก (คืนเงิน · ย้ายเป็นมัดจำ · ตัดเศษ · จองให้ใบ)

ขอบเขต: คอมมิต `5a3b7fa1` · `7c3e1be2` · `dc0a69fc` บน `claude/booking-service-features-sdemob` · อ่านโค้ดอย่างเดียว (ยังไม่ได้คอมไพล์/รันในเครื่องนี้)
ทุกข้อตามโค้ดจริงถึงบรรทัด · ข้อที่ตามไม่ถึงตัดทิ้ง

สรุป: **HIGH 2 · MED 5 · LOW 6**

---

## HIGH

### H1 — ยกเลิก "เอกสาร" ข้ามด่าน 259-1-6: การรับชำระที่เงินส่วนเกินถูกคืน/ย้ายเป็นมัดจำแล้ว ถูกกลับทั้งก้อนแบบ cascade
- ด่าน `CustomerCreditLedger.VoidBlockAsync` ถูกเรียกที่ `VoidPaymentAsync` เท่านั้น (`DocumentService.cs:11748`)
- `VoidDocumentAsync` มีแค่ด่าน `CC-VOID-DOC-CREDIT` ที่ดู `p.UnappliedAmount > Tolerance` (`DocumentService.cs:10133-10142`) แล้ว cascade
  `ReversePaymentInternalAsync`/`ReverseMultiDocPaymentInternalAsync` (`:10195-10200`) ซึ่งกลับ JE ทุกตัวที่ขึ้นต้น `{เลข}/` และประทับ `payment.IsDeleted = true`
- **สถานการณ์:** INV-1 ยอด 1,000 · ลูกค้าโอน 1,500 → RV-1 (Cr 21630 500, Unapplied 500) → คืนเงิน 500 (`#R1`: Dr 21630 500 / Cr ธนาคาร 500) ⇒ Unapplied = 0
  → ยกเลิก INV-1: ด่าน CC-VOID-DOC-CREDIT ผ่าน (Unapplied = 0) → กลับ JE รับชำระ (Dr 21630 500 · Cr ธนาคาร 1,500 · Dr AR…) แต่ `#R1` ไม่ถูกกลับ
  ⇒ **21630 ติดเดบิต −500** · ขาออก Refund ยังมีผลบนการชำระที่ถูกลบ · `UndoMovementAsync` หาแถวการชำระไม่เจอ (query filter `IsDeleted`) ⇒ แก้จากระบบไม่ได้
  · กรณี ToDeposit เหมือนกัน: ใบมัดจำยัง Dr 21630 อยู่ ขณะที่ Cr 21630 ถูกกลับ ⇒ 21630 ติดลบเท่ายอดมัดจำ + หนี้มัดจำ 217xx ไม่มีเงินรองรับ
- ทางเข้าที่โดน: ปุ่มยกเลิกเอกสาร + ผู้เรียก `VoidDocumentAsync` อื่น (`CmsBookingService:1624,1848` · `CmsCommerceService:907` · `IntegrationService:1199,4888` · `PlatformBillingDocumentIssuer:372`)
- **แก้ขั้นต่ำ:** ในธุรกรรมของ `VoidDocumentAsync` ข้าง `creditHeldBy` เรียก `CustomerCreditLedger.VoidBlockAsync` กับทุก `cascadePaymentIds` → 409 `CC-VOID-MOVEMENT`
  (หรือย้ายด่านเข้าไปใน `ReversePaymentInternalAsync`/`ReverseMultiDocPaymentInternalAsync` ให้ทุกทางเข้าได้ในที่เดียว) + Db test ทิศ "คืนเงินแล้วยกเลิกใบ = 409 · ยกเลิกคืนเงินก่อน = ผ่าน"

### H2 — ToDepositAsync: ใบมัดจำอนุมัติแล้ว (JE Dr 21630 commit แล้ว) แต่ข้อยกเว้นหลัง commit ทำให้ "ปล่อยการจอง" ⇒ เงินก้อนเดียวนับสองที่
- `CustomerCreditService.Movements.cs:389` เรียก `ApproveDocumentAsync`; ฟังก์ชันนั้น commit ที่ `DocumentService.cs:7797` แล้วทำงานหลัง commit ที่ไม่ห่อ try
  (`RecordLineAccountFeedbackAsync` · `SyncScanToPostedDocumentAsync` · `_vendorIntel.TryTrainAsync` · `RouteSentDocumentAsync` · `_issuedHooks.RunAsync` · `_notify.DispatchAsync` · `GetDocumentAsync` · `FireWebhookAsync`)
- catch ที่ `Movements.cs:392-411` ลบเฉพาะร่าง (`st is Draft or WaitingApproval`) แต่ **ปล่อยการจองเสมอ** (`:407`)
- **สถานการณ์:** ย้าย 5,000 เป็นมัดจำ → ใบมัดจำ RE-0012 อนุมัติ (Dr 21630 5,000 / Cr 217xx 4,672.90 + VAT 327.10) → hook หลัง commit โยน → ปล่อยการจอง ⇒ Unapplied กลับเป็น 5,000
  ขณะที่ GL 21630 = 0 ⇒ ผู้ใช้กด "คืนเงิน 5,000" ได้อีก (เงินสดออกจริงสองต่อ: มัดจำ 5,000 + คืน 5,000) · หน้า "เงินรอจัดสรร" แสดง GL difference 5,000 แต่ไม่บล็อก ·
  ใบมัดจำไม่มี movement ผูก ⇒ ยกเลิกใบมัดจำภายหลังก็ไม่คืนอะไร (Cr 21630 อีก 5,000)
- ข้อความถึงผู้ใช้ "เงินยังอยู่ในเงินรอจัดสรรเท่าเดิม" (`:410`) เป็นสถานะที่ระบบประทับเองทั้งที่ใบมัดจำออกไปแล้ว
- **แก้ขั้นต่ำ:** ใน catch อ่านสถานะใบ — ถ้าไม่ใช่ Draft/WaitingApproval (อนุมัติแล้ว) **ห้าม release**; ให้ stamp `TargetDocumentId` แล้วคืนผลสำเร็จพร้อม notice
  "อนุมัติแล้วแต่ขั้นหลังอนุมัติล้ม: …" · เทสต์: hook ล้มหลัง commit ⇒ Unapplied ยังถูกตัด + TargetDocumentId ถูกประทับ

---

## MED

### M1 — ToDeposit: ช่องว่างระหว่าง "จอง" กับ "ประทับใบปลายทาง" ทิ้งขาออกกำพร้าที่ไม่มีทางกลับ (ข้อ 1 ของผู้เขียน)
- ขั้น (1) commit การจองที่ตัดยอดทันที (`Movements.cs:325-357`) · ขั้น (2)-(3) อยู่นอกธุรกรรม · `TargetDocumentId` ประทับด้วย `ExecuteUpdateAsync` แยก (`:418-419`)
- ทางที่ทิ้งกำพร้า: (ก) ผู้ใช้ปิดแท็บ — `ct` = `RequestAborted` จาก controller ⇒ `FirstOrDefaultAsync(ct)` ที่ `:365` โยน `OperationCanceledException` ซึ่ง **ถูกยกเว้นจาก catch** (`:392`) ⇒ ไม่ปล่อย ·
  (ข) process ตาย/ deploy ระหว่างขั้น · (ค) ขั้น (3) ล้มหลังใบอนุมัติแล้ว
- ผล: ขาออก ToDeposit มีผล (Unapplied ลด) แต่ `TargetDocumentId = null` · `UndoMovementAsync` ปฏิเสธ ToDeposit เสมอ (`:276-279`) · ยกเลิกใบมัดจำไม่ปล่อย (หาโดย `TargetDocumentId`) ·
  `VoidPaymentAsync` ถูกบล็อกตลอดไปด้วย `CC-VOID-MOVEMENT` ⇒ **สถานะปลายทางที่ระบบประทับเองและไม่มีทางออก** · ตัวอย่าง: ย้าย 3,000 แล้วปิดแท็บ ⇒ เงิน 3,000 หายจากบัญชีย่อย (GL 21630 ยังมี 3,000) ตลอดไป
- **แก้ขั้นต่ำ:** ส่ง `CancellationToken.None` ตั้งแต่หลังขั้น (1) จนจบ (ขั้นชดเชยห้ามถูกยกเลิก) + ให้ `UndoMovementAsync` ปล่อย ToDeposit ที่ `TargetDocumentId == null` และไม่มีใบมัดจำที่ `Reference == PaymentNumber` ยังมีผล (หรือหาใบด้วย Reference แล้วประทับ)

### M2 — ToDeposit ล้มแล้วกดซ้ำในหน้าต่างเดิม = "สำเร็จ" ปลอม (silent no-op)
- หน้า `customer-credits.html:428` สร้างคีย์ครั้งเดียวต่อการเปิดหน้าต่าง; error ไม่ปิดหน้าต่าง (`:493`) ⇒ กดใหม่ใช้คีย์เดิม
- ToDeposit commit แถวพร้อมคีย์ก่อนออกใบ แล้ว release เมื่อล้ม · `ExistingByKeyAsync` (`Movements.cs:111-127`) **ไม่ดู `ReleasedAt`** ⇒ คืน 200 "รายการนี้บันทึกไปแล้ว (กดซ้ำ)" + `TargetDocumentNumber = null`
- **สถานการณ์:** ย้าย 2,000 ล้มเพราะเลือกชนิดมัดจำผิด → แก้แล้วกดใหม่ → toast เขียว "ออกใบมัดจำ  ยอด 2,000.00 บาทแล้ว" แต่ไม่มีใบ ไม่มีอะไรเกิด
- **แก้ขั้นต่ำ:** ใน `ReleaseMovementAsync` ล้าง `IdempotencyKey = null` (เส้นล้ม) หรือ `ExistingByKeyAsync` ถือแถวที่ released + ไม่มีเป้าหมายเป็น "ไม่มี" และหน้าเว็บสร้างคีย์ใหม่เมื่อ error

### M3 — JE คืนเงิน/ตัดเศษไม่มี `SourceDocumentId` ⇒ กลับรายการเองที่หน้าสมุดรายวันได้ แล้วบัญชีย่อยค้าง/ยกเลิกไม่ได้
- `MoveWithJournalAsync` (`Movements.cs:183-193`) ลง JE ผ่าน `JournalEntryBuilder` โดยไม่ผูกเอกสาร · `AccountingService.ReverseJournalEntryAsync` บล็อกการกลับโดยผู้ใช้เฉพาะ JE ที่มี `SourceDocumentId` (`AccountingService.cs:~1059`)
- **สถานการณ์:** ผู้ใช้กลับ `RV-1#R1` (คืน 500) ที่หน้าสมุดรายวัน ⇒ GL 21630 กลับมา 500 แต่ขาออกยังมีผล (Unapplied 0) · กด "ยกเลิก" ที่หน้าเงินรอจัดสรร ⇒ `ReverseJournalEntryAsync` โยน "ถูกกลับรายการไปแล้ว" ⇒ ติดถาวร + `VoidPaymentAsync` ถูกบล็อก
- **แก้ขั้นต่ำ:** ด่านใน `ReverseJournalEntryAsync` (non-systemTriggered) ปฏิเสธ JE ที่ `PaymentCreditMovements.JournalEntryId` ชี้และยังมีผล พร้อมทางไปต่อ "ยกเลิกที่หน้าเงินรอจัดสรร" · หรือ Undo ยอมรับ JE ที่ถูกกลับแล้ว (ไม่กลับซ้ำ แค่ปล่อย)

### M4 — การจองให้ใบ: ทางอนุมัติอื่นไม่จัดสรร แต่ป้าย `[CC-HOLD]` บนใบยังบอกว่าจะจัดสรรให้อัตโนมัติ (ข้อ 2 ของผู้เขียน)
- ผู้เรียก `AfterDocumentApprovedAsync` มีที่เดียว `DocumentController.cs:835` · ไม่มีใน bulk-approve (`DocumentController.cs:1869`) · `ApprovalService.cs:637` · `SignatureApprovalService.cs:551,679` · `LineBotService.cs:384,887`
- ไม่นับซ้ำ (ToInvoice ไม่ตัดยอด · `HoldsUsable` หมดผลเมื่อใบพ้นร่าง) แต่: ใบอนุมัติแล้วค้างชำระเต็มทั้งที่ลูกค้าจ่ายแล้ว · หมายเหตุ `InvoiceHoldNote` "อนุมัติแล้วระบบจัดสรร … ให้อัตโนมัติ" เป็นเท็จ ·
  แถวจองค้าง `ReleasedAt = null` ตลอดไป · ไม่มีคำถาม "ใช้เงินรอจัดสรร" (มีเฉพาะปุ่มอนุมัติเดี่ยวบนเว็บ) ⇒ ขัดกฎ #4A "ห้าม silent no-op"
- **แก้ขั้นต่ำ:** เรียก `AfterDocumentApprovedAsync` จาก bulk-approve + ApprovalService + SignatureApprovalService + LineBot (หรือย้ายเข้า hook หลัง commit ของ `ApproveDocumentAsync` ตัวเดียว) · ไม่งั้นอย่างน้อยให้ `ReceiptPending`/Autopilot แสดง "ใบอนุมัติแล้วที่ยังมีการจองไม่ถูกจัดสรร"

### M5 — "ใบเก่าก่อน 258 ที่ไม่มีใบเสร็จ" ไม่ดูใบเสร็จที่ออกเองด้วยมือ ⇒ "ออกใบเสร็จทั้งหมด" ออกเอกสารซ้ำได้ (ข้อ 3 ของผู้เขียน)
- `CustomerCreditWorkQueue.PairsWithoutReceiptAsync` (`:94-111`) นับ "มีแล้ว" เฉพาะใบเสร็จที่ `SettlementPaymentId ∈ payIds` หรือ `Payment.ReceiptDocumentId` · ใบเสร็จที่ผู้ใช้ออกเอง (`RelatedDocumentId = ใบ`, `SettlementPaymentId = null`) ไม่ถูกนับ
- `IssueReceiptForPaymentAsync` ด่านกันซ้ำ (`DocumentService.cs:~15135-15145`) ก็ดูแค่สองทางนั้น
- ก่อนรอบ 258 ใบที่ 2..n ของการรับหลายใบไม่มีใบเสร็จอัตโนมัติ — ผู้ใช้ที่ออกใบเสร็จ/ใบกำกับเองไปแล้วจะเห็นใบนั้นในรายการ แล้วปุ่มรวมออกใบเสร็จ/ใบกำกับภาษีใบที่สองให้รายการเดียวกัน (§86/4 · ภาษีขายนับซ้ำถ้าใบเสร็จถือ VAT)
- cutover: `Round258CutoverAsync` ใช้เวลา marker จริง (ถูก) · fallback `2026-11-11` ใช้เฉพาะฐานไม่มีตาราง marker · `catch` กลืนทุก exception (ไม่ log) — ถ้า query ล้มด้วยเหตุอื่น จะใช้วันตั้งต้นเงียบ ๆ (LOW ในตัวมันเอง)
- **แก้ขั้นต่ำ:** ตัดใบที่มีใบเสร็จ/ใบกำกับภาษีที่มีผล `RelatedDocumentId = ใบ` (ชนิด Receipt/ReceiptVoucher/TaxInvoice · ไม่ Voided/Rejected) ออกจากรายการ และใส่เงื่อนไขเดียวกันในด่านกันซ้ำของ `IssueReceiptForPaymentAsync`

---

## LOW

- **L1 — สิทธิ์ยกเลิกคืนเงินต่ำกว่าสิทธิ์คืนเงิน** — คืนเงินต้อง `Revenue.Approve + Bank.Reconcile` (`CustomerCreditController` Refund) แต่ยกเลิก (กลับ JE + เพิ่มยอดธนาคาร + เงินกลับมาใช้ได้) ต้องแค่ `Revenue.Void` · ผู้ไม่มีสิทธิ์ธนาคารปลด "เงินคืนที่จ่ายไปจริง" กลับมาเป็นเครดิตได้ ⇒ บังคับ `Bank.Reconcile` เมื่อ kind = Refund + บังคับเหตุผล
- **L2 — การปล่อยการจองหลังอนุมัติไม่ลง audit** — `AfterDocumentApprovedAsync` (`Movements.cs:608-611`) และการประทับ `TargetDocumentId` ของ ToDeposit (`:418-419`) เป็น `ExecuteUpdateAsync` ไม่มี `AddChainedAuditLog` (เส้นอื่นทุกเส้นมี)
- **L3 — `AfterDocumentApprovedAsync` จับเฉพาะ 3 ชนิด exception** (`:594`) — `DbUpdateException`/timeout หลุดไปถึง controller ⇒ ตอบ 500 ทั้งที่ใบอนุมัติ commit แล้ว และไม่มีป้าย `[CC-HOLD-FAILED]`
- **L4 — เส้นล้มของ ToDeposit/IssueInvoice ลบร่างไม่สำเร็จ = แค่ LogError** (`:402-404`, `:554-556`) — ร่างใบมัดจำ `PaymentAccountId = 21630` ค้าง; ถ้าคนอนุมัติภายหลัง ⇒ Dr 21630 ซ้ำโดยไม่มีขาออก · ควรติดป้ายบนร่าง/ปฏิเสธการอนุมัติร่างที่มีป้ายนี้
- **L5 — คีย์กันกดซ้ำยาว > 80 ตัว** — `NormKey` ตัด 80 แต่ `ExistingByKeyAsync` ค้นด้วยคีย์เต็ม (`:115-117` vs `:129`) ⇒ กดซ้ำได้ 500 (unique violation) แทนผลเดิม (ไม่เกิดเงินซ้ำ)
- **L6 — ลิงก์ข้อเสนอธนาคาร** (`PayHref` `:673-682`) เปิดหน้ารับชำระของใบแรกพร้อม "ยอดทั้งบรรทัด" แม้ข้อเสนอเป็นชุดหลายใบ ⇒ ถ้าผู้ใช้กดบันทึกตามที่เติม จะเกิดเงินเกินที่ใบแรกแทนการแบ่ง · และ `BankLineSuggestionsAsync` ดึงใบเก่าสุด 2,000 ใบ (`:634-638`) ⇒ บริษัทใบเยอะไม่เห็นใบใหม่

---

## ตรวจแล้วไม่ใช่บั๊ก
- **ลำดับปล่อยใน `VoidDocumentAsync` (ข้อ 4)** — `ReleaseForVoidedDocumentAsync` อยู่หลัง SaveChanges สุดท้าย ก่อน `CommitAsync` ในธุรกรรมเดียวกับการกลับ JE (`DocumentService.cs:10602-10604`) ⇒ atomic · ล็อก ใบ → การชำระ ลำดับเดียวกับเส้นจัดสรร
- **JE ใบมัดจำ** — `PaymentAccountId` = 21630 ผ่าน `DocumentMoneyChannel` → `UseMoneyAccount()` ใบเสร็จ standalone Dr 21630 = TotalAmount (`DocumentService.cs:~19767`) · `PricesIncludeVat` + ตรวจ `TotalAmount == amount` (`Movements.cs:385`) · ไม่มีแถว Payment/ยอดธนาคารเกิดซ้ำตอนอนุมัติ
- **แข่งกันใช้เงินก้อนเดียว** — ทุกเส้นเขียน (Refund/WriteOff/ToDeposit จอง/IssueInvoice จอง/Allocate/Undo) ล็อก `Payments FOR UPDATE` แล้วคิด `AvailableFactsAsync` ใต้ล็อก ⇒ ไม่ double-spend
- **tenant isolation** — query ใหม่ทุกตัวใน Movements/Ledger/WorkQueue/HeldForDrafts มี `CompanyId == companyId` (รวม join ทั้งสองฝั่ง)
- **ปัดเศษ** — `Available`/`Compute`/`AfterDocumentApprovedAsync` ใช้ `AwayFromZero` · ยอดขาเข้าถูกบังคับ 2 ตำแหน่ง
- **งวดปิด** — Refund/WriteOff ตรวจวันที่ JE · ToDeposit ตรวจวันนี้ + VAT lock · Undo ตรวจวันนี้
- **อนุมัติใน controller ไม่เคยค้าง WaitingApproval** — ไม่มีการตั้ง `WaitingApproval` ใน `ApproveDocumentAsync` (มีแค่ ApprovalService/Signature) ⇒ ไม่เกิด "ปล่อยการจองก่อนอนุมัติจริง"
- **Reissue ใบมัดจำ** — `SettlementPaidReissue.Decide` บล็อก `IsDeposit` แล้ว ⇒ ไม่มีเส้นยกเลิกใบมัดจำที่ข้าม `VoidDocumentAsync`

## ทิศตรงข้าม / เทสต์ที่ขาด
- ไม่มี Db test ToDeposit เต็มเส้น (ผู้เขียนยอมรับ) — ต้องมีอย่างน้อย: สำเร็จ · ล้มก่อนอนุมัติ (ปล่อย) · ล้มหลัง commit (H2: ห้ามปล่อย) · ยกเลิกใบมัดจำ (ปล่อย) · ยกเลิกใบแจ้งหนี้ต้นทางหลังย้าย (H1: 409)
- ไม่มีเทสต์ "ยกเลิกเอกสาร" หลังคืนเงิน/ตัดเศษ (H1) · ไม่มีเทสต์กลับ JE `#R` ด้วยมือ (M3)
