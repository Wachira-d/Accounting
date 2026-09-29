# รอบ 200 · ฝ่ายค้าน ทีม V1 (ยกเลิกและออกใบแทน · ยกเลิกรับชำระ/เช็คเด้งกับ e-Tax)

> ผู้ตรวจ: subagent ฝ่ายค้าน (อ่านอย่างเดียว) · CLAUDE.md F3 ข้อ 11 · diff `5c1fe028..worktree-agent-adb697e0e30c6af71` (d2335b38 + b206aa74) ·
> ตรวจบนคอมมิต merge `3030dffb` (เลขบรรทัดทั้งหมดในไฟล์นี้อ้าง `3030dffb` · `DocumentService.Reissue.cs`/`SettlementPaidReissue.cs` เหมือนสาขาทีมทุกบรรทัด)
> คำถามหลัก: **เงิน/ภาษี/สต็อกผิด หรือเอกสารภาษีหายเงียบ** · ยังไม่ได้คอมไพล์ (ไม่มี .NET SDK)

## สรุป

| # | ระดับ | สถานะ | เรื่อง |
|---|---|---|---|
| V1-R1 | **P1** | CONFIRMED | ด่าน "รายงานภาษีล็อกแล้ว" ของออกใบแทนดูแค่ใบขาย — ใบเสร็จอัตโนมัติ §78/1 ที่ **เป็นเจ้าของแถว ภ.พ.30** ในงวดที่ล็อกแล้วถูกยกเลิก + ออกเลขใหม่ในงวดนั้น |
| V1-R2 | **P1** | CONFIRMED | เช็คเด้ง (ธง "ต้องยกเลิกทาง e-Tax"): ใบเสร็จ/ใบกำกับ §78/1 ยังมีผลที่กรมสรรพากร แต่ภาษีขายถูกถอยออกจาก ภ.พ.30 เงียบ ๆ |
| V1-R3 | P2 | CONFIRMED | ธง "ต้องยกเลิกทาง e-Tax" ไม่มีทางปิด — ใบเสร็จค้าง Paid ถาวร · ใบขายต้นทางยกเลิกไม่ได้ตลอดไป (รวมกรณี Submitted ที่ระบบยกเลิก e-Tax เองได้) |
| V1-R4 | P2 | CONFIRMED | ออกใบแทน: e-Tax ของ**ใบขาย**ที่ Submitted ถูกพลิกเป็น Voided ในฐานเรา (ขัดคำตัดสินข้อ 11 และขัดกับการตัดสินใบเสร็จในเมธอดเดียวกัน) แล้วส่ง e-Tax ใบใหม่ |
| V1-R5 | P2 | CONFIRMED | Integration `document.voided` ด้วย DocumentId/ExternalRef ของใบเดิม ⇒ ตอบ "already voided" สำเร็จ ขณะใบแทนยังมีรายได้/ภาษี (HTTP 200 โกหก) |
| V1-R6 | P2 | CONFIRMED (นโยบาย) | ออกใบแทนข้าม `ApproveDocumentAsync` ทั้งเส้น — SoD ห้ามอนุมัติเอง · วงเงินเซ็นหลายขั้น ไม่ถูกตรวจ (พี่น้อง `IssueFullTaxInvoiceForReceiptAsync` เดินผ่าน approve) |
| V1-R7 | P3 | CONFIRMED | `CopyScalars` พาหลักฐานของลูกค้าเดิมไปใบใหม่ (ลายเซ็นรับของ · ผู้รับ/เวลาเซ็น · RdCompliance ฯลฯ) |
| V1-R8 | P3 | CONFIRMED | ด่าน `ForbiddenChanges` ที่ call site เป็นสูตรตรวจตัวเอง (โคลนแล้วเทียบกับต้นฉบับ) + `AllowedChanges` ใส่ "หมายเหตุ" ทุกครั้ง |
| V1-R9 | P3 | CONFIRMED | ลิงก์ที่ไม่ถูกย้าย: `PaymentIntent.SourceId/ReceiptDocumentId` · `ProjectCostEntry.DocumentLineId` · JE `Reference/Description` ยังเป็นเลขใบเดิม |
| V1-R10 | P3 | CONFIRMED | กติกา "ผู้ซื้อครบ §86/4" สำเนาที่สอง — เข้มกว่าเส้นอนุมัติ (ผู้ซื้อบุคคลธรรมดา) |
| V1-P1 | P2 | PLAUSIBLE | e-Tax by Email (CC `csemail@etax.teda.th`) ไม่อยู่ใน `EtaxReachedRdStatuses` ⇒ ใบที่ "ส่งแล้ว" ทางอีเมลถูกยกเลิกเงียบ |
| V1-P2 | P3 | PLAUSIBLE | จุด throw ใหม่กลางลูปยกเลิกการลงบัญชี (สถานะ e-Tax เปลี่ยนหลังด่าน) = ครึ่งกลับครึ่งค้าง |
| V1-P3 | P3 | PLAUSIBLE | งวด "ประกาศว่ายื่นแล้วแต่ยังไม่ล็อก" (Q4) · ใบที่ถูก "ดึง" ไปงวดอื่น (`claimedElsewhere`) ⇒ ใบแทนตกงวดตามวันที่ |
| V1-P4 | P3 | PLAUSIBLE | execution strategy retry หลัง commit ⇒ รอบสองตอบ 409 "ถูกยกเลิกแล้ว" ทั้งที่รอบแรกสำเร็จ |

NOT-A-BUG (ตรวจแล้ว — ห้ามรายงานซ้ำ): อยู่ท้ายไฟล์

---

## CONFIRMED

### V1-R1 (P1) — ใบเสร็จอัตโนมัติ §78/1 ที่อยู่ในรายงานภาษีที่ล็อกแล้วถูกยกเลิก-ออกใหม่

- ด่าน: `DocumentService.Reissue.cs:75` `filingLocked = IsDocumentFilingLockedAsync(companyId, doc.Id)` — ตรวจเฉพาะใบขาย
- ตัวตรวจล็อกดูตาม `DocumentId` ของแถวรายงาน: `TaxService.RdCompliance.cs:221-228`
- ใบแจ้งหนี้บริการ (VAT พัก 21913) ที่รับชำระครบด้วยใบเสร็จ "ถือ VAT" (`CreateSettlementReceiptAsync(carryVatFromSource)` · `DocumentService.cs:11856+`) —
  **ใบเสร็จเป็นเจ้าของแถว ภ.พ.30** และใบแจ้งหนี้ถูกข้าม: `TaxService.cs:438-452` (`invoiceIdsOwnedByVatReceipt`) · `:538` (`continue`) ·
  `:579-593` + `:664` (แถวใบเสร็จ `DocumentId = receipt.Id`)
- ฉาก: ใบบริการ INV-A (ส.ค.) รอบโอน Posted รับชำระ ก.ย. → ระบบออก RE-B (ใบกำกับ ณ วันรับเงิน §78/1) → ภ.พ.30 ก.ย. ยื่นและ **ล็อก** (แถว = RE-B)
  → ผู้ใช้กด "ยกเลิกและออกใบแทน" INV-A: `IsDocumentFilingLockedAsync(INV-A)` = false (INV-A ไม่มีแถว) → `Reissue.cs:287-311` ยกเลิก RE-B
  (ใบกำกับภาษีที่อยู่ในแบบที่ยื่นแล้ว) และออก RE-C เลขใหม่ลงวันที่ ก.ย. ⇒ แบบที่ยื่นอ้างใบที่ถูกยกเลิก · ใบกำกับที่ลูกค้าถือไม่อยู่ในแบบ —
  คือสิ่งที่ด่าน "ยกเลิกเอกสาร" (`DocumentService.cs:8050`) และคำตัดสินข้อ 9 ("เดือนภาษีของใบเดิมที่ยื่นแล้ว = บล็อก") ห้าม
- ด่านงวดบัญชีปิด (`Reissue.cs:80-83`) ก็ดูแค่วันที่ใบขาย ไม่ดูวันรับเงิน/วันที่ใบเสร็จ
- แก้: ใน `EvaluateSettlementPaidReissueAsync` ตรวจ `IsDocumentFilingLockedAsync` ของ**ทุก** `liveReceiptIds` (เติมช่องใน `ReissueReceiptFact`) + เทสต์สองทิศ
  (ใบเสร็จอยู่ในรายงานล็อก = บล็อก · ใบเสร็จ VAT=0 / รายงานไม่ล็อก = ผ่าน) + แถว `required_call_site_check`

### V1-R2 (P1) — เช็คเด้งติดธงใบเสร็จ แต่ถอยภาษีขาย §78/1 ออกจาก ภ.พ.30 เงียบ ๆ

- `ReversePaymentInternalAsync`: ตัดสินธงที่ `DocumentService.cs:9745` แล้ว `ApplyAutoReceiptOnPaymentVoidAsync` คงใบเสร็จไว้ (`:9810`) แต่ต่อมา
  `:9830-9831` `if (doc.PaidAmount <= 0.005m && doc.OutputVatDueAt != null) TryUndoUndueOutputVatReclassAsync(...)` ยังรันเหมือนเดิม ⇒
  กลับ JE 21913→21911 + `OutputVatDueAt = null` (`:14039-14070`)
- ผลใน ภ.พ.30: ใบเสร็จที่ติดธง (VatAmount>0, ยัง Paid) ไม่เข้าเงื่อนไข `rcptSrcInfo.OutputVatDueAt != null` (`TaxService.cs:589-593`) และใบแจ้งหนี้ถูกข้าม
  เพราะ 21913 net > 0 (`:544-550`) ⇒ **ภาษีขายของใบกำกับที่กรมสรรพากร Accepted แล้วหายจากแบบ** — §78/1: การออกใบกำกับก่อนรับเงินทำให้ความรับผิดเกิด
  ณ วันออก จนกว่าจะยกเลิก/ลดหนี้อย่างเป็นทางการ
- ก่อนรอบนี้ตัวเลข VAT ก็หายแบบเดียวกัน แต่ใบเสร็จถูกยกเลิกคู่กัน (ผิดที่ RD แต่ฐานเราสอดคล้องกันเอง) · รอบนี้ฐานเราบอกสองอย่างขัดกัน:
  "ใบกำกับยังมีผล (ธง)" กับ "ภาษีขายไม่มี" — ตรงข้ามกับเจตนาของคำตัดสินข้อ 11 ("ไม่ประทับ Voided เงียบ")
- แก้: เมื่อ `receiptDecision.Action == FlagEtaxCancellation` และใบเสร็จถือ VAT ⇒ **ห้าม** undo reclass (ภาษีขายค้างที่ 21911/`OutputVatDueAt` จนกว่าจะ
  ยกเลิกทางกรมสรรพากร/ใบลดหนี้) + ข้อความธงบอกว่า "ภาษีขายยังนำส่ง" · เทสต์: ตัวตัดสิน pure "undo reclass ได้ไหม" (Void=ได้ · Flag+VAT=ไม่ได้)
- เส้นหลายใบ (`ReverseMultiDocPaymentInternalAsync`) ไม่ undo reclass อยู่แล้ว — ไม่กระทบ

### V1-R3 (P2) — ธง "ต้องยกเลิกทาง e-Tax" ไม่มีทางปิด

- ผู้เขียน `EtaxCancelRequiredAt` มีแค่ `DocumentService.cs:9579` (ตั้ง) และ `Reissue.cs:203` (ล้างบนใบใหม่) — **ไม่มีเส้นล้างธงของใบที่ติดธง**
- ใบเสร็จที่ติดธงคง `Status=Paid, IsDeleted=false` ขณะ `Payment.IsDeleted=true` · ยกเลิกตรงไม่ได้ (`DocumentService.cs:8031-8034` "ยกเลิกการชำระแทน"
  แต่การชำระไม่มีแล้ว) · ยังเป็นลูก active ของใบขาย ⇒ `DocumentVoidPreconditions.ChildFactsAsync` บล็อกการยกเลิกใบขายนั้น**ถาวร**
- กรณี **Submitted**: ข้อความธง (`DocumentVoidPreconditions.cs` `AutoReceiptOnPaymentVoid` สาขาเช็คเด้ง) บอกให้ "กดยกเลิก e-Tax ที่หน้า e-Tax" —
  `EtaxInvoiceService.VoidAsync` (`:752-771`) ทำได้จริง (พลิกสถานะในฐานเหมือนกัน) แต่หลังจากนั้นไม่มีอะไรยกเลิกใบเสร็จ/ล้างธง ⇒ ทางไปต่อที่ข้อความสัญญาไว้ไม่จบ
  (ผิดหลัก DECISIONS "ทุกการบล็อกต้องมีทางไปต่อ") · ทีมบันทึกเป็น Q1 เฉพาะกรณี Accepted
- แก้ขั้นต่ำ: เช็คเด้ง + Submitted ⇒ ยกเลิก e-Tax ที่ยังไม่ตอบรับตามพฤติกรรม `VoidAsync` เดิมแล้วยกเลิกใบเสร็จ (ไม่ต้องติดธง) · Accepted ⇒ ปุ่ม
  "ยืนยันยกเลิกที่กรมสรรพากรแล้ว (เลขอ้างอิง)" ที่ยกเลิกใบเสร็จ + ล้างธง + audit (รอเจ้าของ = Q1)

### V1-R4 (P2) — ออกใบแทน: e-Tax ของใบขายที่ Submitted ถูกยกเลิกในฐานเราเงียบ ๆ

- ด่าน: `Reissue.cs:73-74` บล็อกเฉพาะ `Accepted` · ขั้นทำ: `:264-274` พลิกทุกสถานะ `!= Voided && != Accepted` (รวม **Submitted**) เป็น Voided ·
  แล้ว `:352` `_issuedHooks.RunAsync(neo)` ส่ง e-Tax ใบใหม่
- เมธอดเดียวกันถือว่าใบเสร็จ Submitted "ถึงกรมสรรพากรแล้ว" (`:297-300` + `SettlementPaidReissue.Decide` ลูป Receipts `:148-158`) — สองมาตรฐานในธุรกรรมเดียว
- ฉาก: ใบกำกับ e-Tax Submitted (รอผล RD) → ออกใบแทน → แถวเดิม Voided (poller ไม่ติดตามแถว Voided) → RD ตอบรับทั้งใบเดิมและใบใหม่ ⇒ ภาษีขายซ้ำฝั่ง RD
- พฤติกรรมเดียวกันมีอยู่แล้วใน `VoidDocumentAsync` (`DocumentService.cs:8063`, `:8304`) — แต่คำตัดสินข้อ 11 นิยาม "ถึงกรมสรรพากร = Submitted/Accepted" และโค้ดใหม่ควรใช้
  `DocumentVoidPreconditions.EtaxReachedRdStatuses` ชุดเดียว · แก้: `etaxAccepted` → `StrongestEtax(...)` ∈ `EtaxReachedRdStatuses` พร้อมข้อความทางไปต่อแบบใบเสร็จ

### V1-R5 (P2) — Integration ยกเลิกด้วยอ้างอิงใบเดิม ⇒ ตอบสำเร็จทั้งที่ใบแทนยังมีผล

- `IntegrationService.cs:4355-4389`: หาใบด้วย `request.DocumentId` ก่อน (คู่ค้าเก็บ id ที่ได้ตอนสร้าง = **ใบเดิม**) → `Status == Voided` →
  `"Document already voided (idempotent skip)"` + `InboundSyncResponse(true, …)`
- เส้น ExternalRef (`:4361-4366`): `CopyScalars` คัดลอก `Reference` (external ref) ไปใบใหม่ ⇒ มีสองใบ `Reference` เดียวกัน · คิวรี `FirstOrDefaultAsync`
  ไม่มีลำดับ ไม่กรอง Voided ⇒ ผลขึ้นกับลำดับแถว (ต่างจากเส้นสร้าง `:726-732` และรับชำระ `:1203-1209` ที่กรอง Voided + เรียงแล้ว)
- ผล: คู่ค้าเชื่อว่ายกเลิกแล้ว · ใบแทนยังถือรายได้/ภาษีขาย/การรับชำระ (การยกเลิกจริงของใบแทนจะได้ 409 จากด่านรอบโอน — ซึ่งคู่ค้าควรได้เห็น) — F2 ข้อ 7 "HTTP 200 กับของว่างคือการโกหก"
- แก้: ใบที่ `ReplacedByDocumentId` + `ReplacementCarriesPostings` ของใบแทน ⇒ ตามไปใบแทน (หรือ 409 บอกเลขใบแทน) · เส้น ExternalRef กรอง Voided + เรียงเหมือนเส้นอื่น

### V1-R6 (P2 · นโยบาย) — ออกใบแทนไม่ผ่านด่านของการอนุมัติ

- ใบใหม่เกิดเป็น Paid/Approved ตรงใน `Reissue.cs:188-248` ไม่ผ่าน `ApproveDocumentAsync` ⇒ ข้าม SoD `SodBlockSelfApproval` (`DocumentService.cs:5963-5968`) ·
  วงเงินที่ต้องเซ็นหลายขั้น (`~:5940-5956`) · คำเตือนอนุมัติอื่น
- พี่น้องที่ทำงานแบบเดียวกัน (`IssueFullTaxInvoiceForReceiptAsync`) เดินผ่าน `ApproveDocumentAsync` (`DocumentService.cs:10956`)
- ยอดเท่าเดิม แต่ **ผู้ซื้อเปลี่ยนได้** — คนเดียวที่ถือ Void+Approve ออกใบกำกับให้นิติบุคคลอื่นได้โดยไม่มีคนที่สองเห็น เมื่อบริษัทเปิด SoD ไว้
- ทีมอธิบายว่าต้องอยู่ในธุรกรรมเดียว (approve เปิดธุรกรรมเอง) — ข้อเสนอขั้นต่ำ: เรียกตัวตัดสิน SoD/วงเงินเดียวกัน (แยกเป็น helper) ก่อนแตะ · ต้องให้เจ้าของตัดสินว่า "ใบแทนต้องมีผู้อนุมัติคนที่สองไหม"

### V1-R7 (P3) — `CopyScalars` พาค่าที่เป็นหลักฐานของใบเดิมไปใบใหม่

`SettlementPaidReissue.CopyScalars` (`SettlementPaidReissue.cs:270-280`) คัดลอกทุกช่องค่า · `Reissue.cs:190-215` ล้างแค่ Id/เวลา/ผู้ซื้อ/ตราใบแทน/ธง e-Tax/โทเคน/revision/aging/dunning ·
ที่ยังตามมา: `DeliverySignedAt/DeliverySignedBy/DeliverySignatureBase64` (`Document.cs:197-201` — ใบใหม่ของ**ผู้ซื้อใหม่**แสดงว่าลูกค้าเซ็นรับแล้ว) ·
`QuotationAcceptedAt/By` · `RdComplianceStatus/RdComplianceIssuesJson` (ผลตรวจของผู้ซื้อเดิม) · `WhtAdviceAiFeedbackId` (สองใบชี้ feedback แถวเดียว) ·
`SettlementOrphanAck*` · `Reference` (ดู V1-R5). ไม่กระทบยอดเงิน/ภาษี · แก้: รายการ "ห้ามตามมา" ใน helper (pure + เทสต์) แทนการจำใน service

### V1-R8 (P3) — ด่าน "ใบใหม่เท่าใบเดิม" ตรวจผลของสูตรตัวเอง

`Reissue.cs:187-241`: `after` มาจาก `CopyScalars(old)` แล้วแก้แค่คำบรรยาย/ผู้ซื้อ/หมายเหตุ ⇒ `ForbiddenChanges` จับได้แค่ "คำบรรยายว่าง" ตลอดกาล (F2 ข้อ 6) ·
ด่านจริงคือรูปร่าง `ReissueSettlementPaidRequest` · เทสต์ `…ยอด_อัตราVAT_taxpoint…_ด่านปฏิเสธ` เรียก helper ตรงจึงเขียวแม้ service ไม่เรียก — ไม่อันตราย แต่ doc/เทสต์เรียกว่า "ด่าน" ·
และ `AllowedChanges` ใส่ "หมายเหตุ" ทุกครั้งเพราะ `ComposeNotes` ต่อท้ายเสมอ (audit `changed` มีเสียงรบกวน) · ใบแทนของใบแทนสะสมบรรทัด "ยกเลิกและออกฉบับใหม่แทน…" สองบรรทัด

### V1-R9 (P3) — ลิงก์ที่ `RepointDocumentLinksAsync` ไม่ย้าย

- `PaymentIntent.SourceId` (SourceKind=Document) + `PaymentIntent.ReceiptDocumentId` (`Payments.cs:146`) — `GatewayRefundService.CreditNoteStatesAsync` (`:564-575`)
  นับใบลดหนี้ที่อ้าง**ใบเดิม** (ใบลดหนี้ใหม่ต้องอ้างใบแทน ⇒ ขึ้น "ต้องออกใบลดหนี้" ค้าง) · `ContactArPinAsync` (`:593-600`) อ่านผู้ซื้อเดิม · เกิดเมื่อใบนั้นมี intent จาก gateway
- `ProjectCostEntry.DocumentLineId` ชี้ id บรรทัดเดิม (ย้ายแค่ `DocumentId`)
- JE ที่ย้าย `SourceDocumentId` ยังมี `Reference`/`Description` = เลขใบเดิม (+ ชื่อผู้ซื้อเดิม) — ไม่มีตัวเลือก JE ตาม Reference ที่ถูกกระทบ (มัดจำถูกบล็อก ·
  `TryUndoUndueOutputVatReclassAsync` เลือกด้วย `SourceDocumentId` `:14045-14053`) แต่ผู้สอบบัญชีเห็น JE อ้างใบที่ยกเลิกโดยไม่มีรายการกลับ ⇒ ควรเติมหมายเหตุ JE

### V1-R10 (P3) — กติกาผู้ซื้อครบ §86/4 สำเนาที่สอง

`Reissue.cs:166-174` บล็อกทุกช่องที่ `MissingBuyerFields` คืน · เส้นอนุมัติ (`DocumentService.cs:5885-5925`) บล็อกเฉพาะผู้ซื้อนิติบุคคล ส่วนบุคคลธรรมดาลดหัวเป็นอย่างย่อ ·
เข้มกว่าอย่างเดียว (มีทางไปต่อ: แก้ผู้ติดต่อ) แต่เป็นกติกาสองชุด (F2 ข้อ 4) · และ `!old.BuyerDeclinedTaxInvoice` ข้ามการตรวจแม้เปลี่ยนเป็นผู้ซื้อคนใหม่

---

## PLAUSIBLE

- **V1-P1 (P2)** e-Tax by Email: การส่งพร้อม CC ที่อยู่ประทับเวลา RD บันทึกแค่ `DocumentEmailLog.IncludedRdTimestamp` (`DocumentEmailLog.cs:34`) ไม่เปลี่ยน `EtaxStatus` ⇒
  `EtaxReachedRdStatuses` (`DocumentVoidPreconditions.cs` บรรทัดประกาศ) มองไม่เห็น "ส่งแล้ว" ของบริษัท SME ที่ใช้ช่องทางนี้ ⇒ ยกเลิกการชำระ/ออกใบแทนยกเลิกใบที่ส่งกรมสรรพากรแล้วเงียบ ๆ
  (คำตัดสินข้อ 11 ใช้คำว่า "Accepted/ส่งแล้ว") — ต้องตรวจว่าเส้นอีเมลนับเป็นการนำส่งจริงไหม
- **V1-P2 (P3)** `SettlementPostingService.UnpostCoreAsync` ยกเลิกเอกสารก่อน แล้ววน `VoidPaymentAsync(..., SettlementUnpost)` (แต่ละตัวเปิดธุรกรรมเอง) — เดิม `VoidPaymentAsync`
  ไม่เคย throw เรื่อง e-Tax · ตอนนี้ throw ได้ถ้าใบเสร็จถูกส่ง e-Tax **หลัง**ด่าน `LoadUnpostFactsAsync` ⇒ ครึ่งกลับครึ่งค้าง · หน้าต่างแคบ (ต้องมีคนกดส่ง e-Tax พร้อมกัน)
- **V1-P3 (P3)** ด่านเดือนภาษีใช้ "ล็อก" อย่างเดียว (ทีมถาม Q4) ขณะที่ด่านยกเลิกการลงบัญชีใช้ `DeclaredOrFiled` · และใบเดิมที่ถูก "ดึง" ไปรายงานงวดอื่น (`TaxService.cs:188-199`
  `claimedElsewhere`) — ใบแทนไม่มีแถวอ้างจึงกลับไปตกงวดตามวันที่เดิม ⇒ ภาษีขายย้ายงวดเมื่อสร้างรายงานใหม่ · รายงานภาษีขายตัดใบ Voided ทิ้งทั้งแถว (ของเดิม ไม่ใช่ของรอบนี้)
  ⇒ รายงาน §87 ไม่แสดง "ยกเลิก" ของเลขเดิม
- **V1-P4 (P3)** `Reissue.cs:140-356` ใส่ `_issuedHooks.RunAsync` + `GetDocumentAsync` ไว้**ใน** lambda ของ execution strategy หลัง commit — ถ้าส่วนหลัง commit ล้มแบบ transient
  แล้ว strategy retry ⇒ รอบสองเห็นใบเดิม Voided → 409 "ใช้ได้เฉพาะเอกสารที่ออกแล้ว…" ทั้งที่ออกใบแทนสำเร็จแล้ว

---

## NOT-A-BUG (ตรวจแล้ว)

1. **ภ.พ.30 ไม่ซ้ำ/ไม่หายในเคสปกติ** — ใบเดิม Voided ถูกตัด (`TaxService.cs:154`) · ใบแทนวันที่/tax point/`OutputVatDueAt` เท่าเดิม · JE ถูกย้าย `SourceDocumentId` ⇒ ตัวอ่าน GL ราย
   ใบ (21913 net `:455-467`, 21911 มัดจำ, `TryUndoUndueOutputVatReclassAsync`) เห็นใบแทนถูกตัว · ใบเสร็จถือ VAT ใหม่อ้างใบแทน ⇒ `invoiceIdsOwnedByVatReceipt` ทำงานเหมือนเดิม
2. **เลขที่ gap-free** — `DocumentNumberGenerator.NextAsync` ในธุรกรรม · ชุดเลขผ่าน `ResolveNumberSeriesTypeAsync` ตัวเดียวกับ approve (`DocumentService.cs:6208`) · วันที่เดิมตรง
   คำตัดสินข้อ 9 และตรงเส้นใบแทนกระดาษเดิม (ประกาศเรื่องวันที่บนใบแทนตาม ป.86/2542 ควรให้นักบัญชียืนยันอีกครั้ง — ไม่ใช่บั๊กของรอบนี้)
3. **สิทธิ์ endpoint** — `CanVoidAsync && CanApproveAsync` (`DocumentController.cs` `ReissueSettlementPaid`) · ไม่มี `[RejectApiKey]` เหมือน `void`/`approve`/`issue-full-tax-invoice` · tenant:
   ทุกคิวรี/ExecuteUpdate มี `CompanyId` (ReconciliationGroupItems ผ่าน subquery ของกลุ่ม) · ล็อก: ใบเดิม `FOR UPDATE` · การรับชำระ `FOR UPDATE` · รอบโอน `FOR SHARE` · กดซ้ำ = รอบสองได้ NotRelevant
4. **ผู้เรียก `VoidPaymentAsync`** มี 3 จุดทั้งเรพ (`DocumentController.cs:1361` · `ChequeService.cs:285` · `SettlementPostingService.cs:1099`) — ธงเกิดได้เฉพาะ `ChequeBounce` ซึ่งอ่านผล ·
   ไม่มี fake/mock ของ `IDocumentService` ใน `Accounting.Tests` · cascade ใน `VoidDocumentAsync` (`:8159/8163`) ไปไม่ถึงใบเสร็จ active เพราะ `ChildFactsAsync` บล็อกก่อน
5. **ทิศตรงข้าม** — ใบขายที่ไม่เกี่ยวรอบโอน: `QuickRelevance`/`CheckDocumentPaymentsAsync` = null ⇒ ไม่มีปุ่ม · `VoidDocumentAsync` เปลี่ยนแค่ข้อความ 409 (เงื่อนไขเดิม `VoidBlockedReason`) ·
   ใบเสร็จที่ยังไม่ถึง RD ยกเลิกได้เหมือนเดิม + e-Tax ค้างของใบนั้นถูกยกเลิกตาม (ดีขึ้น) · ใบแทนกระดาษเดิม (`ReplacementCarriesPostings=false`) พฤติกรรมเดิมทุกจุด (`:6302` · `:8341` · `:8422`)
6. **กู้คืนใบเดิม** ถูกกัน (`DocumentService.cs:8664-8676` Gate 0) ⇒ ไม่มีรายได้ซ้ำ · ยกเลิกใบแทนภายหลังกลับ JE/สต็อก/ยอดโครงการตามปกติ
7. **S3-7 / ข้อ 17** มีอยู่แล้วจริง (`SettlementPostingGuards.cs:326-331` `OutputVatDueAt` + ยอดรับสะสม)
8. **คอมไพล์ (อ่านโค้ด)** — DbSet ทุกตัวใน `RepointDocumentLinksAsync` มีจริง · `ReconciliationGroupItem : BaseEntity` จึงต้องกรองผ่าน subquery (EF8 แปลเป็น EXISTS ได้) ·
   tuple conditional ใน `BatchStateAsync` target-typed ได้ (C# 12) · `using` ครบ (`BusinessRuleException` ใน CmsCommerceService มี `using Accounting.Helpers`) · `Tax.TaxInvoiceCompletenessChecker`
   resolve เป็น namespace ลูกแบบเดียวกับ `DocumentService.cs:5891` · เทสต์ใช้ internal ผ่าน `InternalsVisibleTo` (`Accounting.csproj:82`) · checker ที่รันบน snapshot `3030dffb` ผ่านทั้งหมด:
   using · record_arg · namespace_shadow · service_interface · undeclared_local · tuple_name_merge · nullable_arg · arg_type · comment_line_break · string_quote_close · verbatim_string ·
   dto_nullable_contract · identifier_space · html_attr_escape · onclick_js_string · accessibility · write_permission_gate · dead_helper (**`required_call_site_check` รันไม่จบใน 8 นาที — ไม่ได้ยืนยัน**)
9. หน้าเว็บ — ทุกค่าที่ผู้ใช้/เซิร์ฟเวอร์คุมผ่าน `Layout.esc` · ปุ่มตัดสินจาก `canReissueSettlementPaid` ของเซิร์ฟเวอร์ · ผู้ซื้อเดิมที่ไม่อยู่ใน 500 รายแรกถูกเติมเป็นตัวเลือกที่เลือกไว้ (ไม่หล่นไปรายแรก)
