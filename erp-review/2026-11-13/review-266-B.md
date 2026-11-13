# ฝ่ายค้าน รอบ 266 แพ็ก B — ย้ายการรับชำระระหว่างใบ (46ac5463 + cf0314b9)

ผู้ตรวจ: adversarial reviewer (อ่านโค้ดอย่างเดียว · ไม่ได้คอมไพล์/รันเทสต์) · วันที่ 2026-11-13
ขอบเขต: `DocumentService.ReceiptLink.cs` · `ReceiptLinkPolicy` · `ReceiptLinkService` · `ReceiptLinkController` · ผู้อ่าน `Payment.DocumentId` ที่ถูก/ไม่ถูกกวาด · renderer ×2

## สรุป

| # | ระดับ | เรื่อง |
|---|---|---|
| B-1 | **HIGH** | ย้ายเงินออกจากใบกำกับที่ "ทำหน้าที่ใบเสร็จในตัว" (ไม่มี REC แยก) — หัวกระดาษของใบเดิมเปลี่ยนย้อนหลังเงียบ ๆ และใบปลายทางไม่ได้ใบรับ · พรีวิวบอกว่า "ไม่มีกระดาษใดถูกแก้" |
| B-2 | MED | สิทธิ์ยกเลิกตรวจกับชนิดของ **ใบปลายทาง** ไม่ใช่ใบที่ถูกถอนเงินออก · เส้นถูกตัดสินจากพรีวิวนอกล็อก (TOCTOU) · ถอด (undo) ไม่ตรวจสิทธิ์อนุมัติ/สิทธิ์ใบเสร็จ |
| B-3 | MED | ค่าคอมมิชชันแบบ "ยอดที่เก็บได้" ยังผูกเงินกับผู้สร้างใบเดิม (`p.Document.CreatedBy`) |
| B-4 | MED | งานคิดค่าปรับล่าช้าของใบจากรายการประจำ คิดค่าปรับทั้งช่วงย้อนหลังให้ใบเดิมที่กลับมาค้าง (พักแค่หนังสือทวง 7 วัน) |
| B-5 | LOW | `VoidPaymentAsync` ล็อกชุดใบจากแถวจัดสรรที่อ่านก่อนล็อก — ถ้าการย้าย commit ระหว่างนั้น ใบปลายทางไม่ถูกล็อก (lost update ของ `PaidAmount`) |
| B-6 | LOW | งานหลังรับชำระ (PostPaymentAutomation) ที่ยังค้างในคิวตอนย้าย ยังรันกับใบเดิม (`run.DocumentId`) |
| B-7 | LOW | PDF ใบรับรายการชำระ (`GenerateReceiptPdfAsync`, API) พิมพ์ "เอกสารอ้างอิง" เป็นใบเดิม |
| B-8 | LOW | ถอดการย้ายหลังเส้น "ใบปลายทางเป็นใบเสร็จในตัว" (ไม่ออกใบแทน) — ใบเดิมกลับมาชำระครบแต่ใบเสร็จเดิมถูกยกเลิกไปแล้วและไม่ได้ออกใหม่ |
| B-9 | LOW | หน้าที่แสดงประวัติ/รายการเท่านั้นที่ยังชี้ใบเดิม (ArAp contact detail · จับคู่ธนาคาร · CMS cancel plan) + purge ใบปลายทางชนแถวจัดสรรที่ถูกปล่อย |

ตรวจแล้วไม่ใช่บั๊ก (ไม่ต้องรายงานซ้ำ): JE สองขาสุทธิ 0 ที่ 21630 + ขาเงินสดไม่ถูกแตะ · ยกเลิกการรับชำระหลังย้ายกลับครบ (เส้นหลายใบเลือก JE ด้วย prefix `{เลข}/` ⇒ จับ M1-OUT/M1-IN + JE เงินสดเดิม · คืนยอดเฉพาะใบปลายทาง · ยกเลิกใบแทน) ·
ยกเลิกใบปลายทาง = cascade ยกเลิกการรับชำระทั้งก้อน (`MovedToDocumentId ?? DocumentId`) · ยกเลิกใบเดิมถูกกัน (`RL-MOVE-VOID-SOURCE`) · ยกเลิกและออกใบแทน (SettlementPaidReissue) ของทั้งสองใบถูกด่าน "ชำระร่วมใบอื่น" กันจริง ·
ใบเสร็จอัตโนมัติยกเลิกตรงไม่ได้ (ไม่มี deadlock กับ VoidDocument ของใบเสร็จ) · เลขใบแทนเป็นชุดรายวัน `{prefix}-{yyyyMMdd}-` ตามวันรับเงิน ⇒ ลำดับตามวันที่ (§87) ไม่สลับ · renderer ×2 ใช้ `ReplacementReferenceText` + `DocumentLabels` ตัวเดียว (th/en) + HtmlEncode ·
statement ลูกค้า (`AdvancedArApService`) ไม่แสดงเลขใบของการชำระ ⇒ ไม่ผิด · ใบกำกับ e-Tax by Email อยู่ใน `EffectiveEtaxAsync` · tenant filter ครบทุก query ใหม่ · audit ผ่าน `AddChainedAuditLog` ในธุรกรรมเดียว

---

## B-1 HIGH — ใบกำกับที่เป็นใบเสร็จในตัว: กระดาษเปลี่ยนเงียบ + ใบปลายทางไม่มีใบรับ

**ที่:** `DocumentService.ReceiptLink.cs:50-57` (หาใบเสร็จเฉพาะ `IsSettlementReceipt`) · `:377` (`if (oldReceipt != null)` — ทั้งบล็อกกระดาษ) · `ReceiptLinkPolicy.cs:503-506` (ข้อความพรีวิว "ไม่มีกระดาษใดถูกแก้หรือออกใหม่") ·
ตัวตัดสินหัว `DocumentService.cs:21505-21523` `ComputeServedAsReceipt` (`BalanceDue > 0.01m ⇒ false`) + `LoadSettlementDatesAsync` (ถูกกวาดเป็นใบที่มีผลแล้ว `:21456`)

**สถานการณ์:** INV002 = TaxInvoice โหมด Combined ยอด 10,700 ลงวันที่ 5 ส.ค. · รับเงินเต็ม 5 ส.ค. ⇒ `CreatePaymentAsync` ไม่ออก REC (`combinedSelfReceipt`, `:15712-15724`) — INV002 พิมพ์หัว "ใบกำกับภาษี/ใบเสร็จรับเงิน" ส่งลูกค้าแล้ว ·
เงินจริงเป็นของ INV001 (10,700 · Invoice บริการ VAT พัก 21913) · ผู้ใช้กดย้าย ⇒ `LiveReceiptNumber = null` ⇒ route `MoveSettlement` (ไม่มีด่านใดดูว่าใบเดิมทำหน้าที่ใบรับของเงินก้อนนี้) ⇒
1. INV002 ยอดค้าง 10,700 ⇒ `ComputeServedAsReceipt` = false ⇒ พิมพ์ซ้ำได้หัว "ใบกำกับภาษี" — ใบรับที่ลูกค้าถือ **ถูกยกเลิกโดยไม่มีแถว Voided/ใบยกเลิก** (§86/4 ห้ามแก้ย้อนหลัง · ม.105 · F2 ข้อ 3 "สถานะที่ระบบประทับเอง")
2. INV001 ชำระครบ · `ApplyCreditAllocationTaxStepsAsync` ย้ายภาษีขาย 700 เข้างวด ส.ค. (หรืองวดปัจจุบัน) แต่ **ไม่มีใบกำกับภาษี ณ วันรับเงิน/ใบรับออกให้ลูกค้าเลย** (ม.78/1 + ม.86 ต้องออกใบกำกับเมื่อจุดความรับผิดเกิด) — VAT ถูกรายงานโดยไม่มีเอกสารที่มันเป็นเจ้าของ
3. พรีวิวบอกผู้ใช้ว่า "การรับชำระนี้ไม่มีใบเสร็จแยก — ไม่มีกระดาษใดถูกแก้หรือออกใหม่" = ข้อความเท็จ

ทิศกลับ (LOW ในข้อเดียวกัน): ใบปลายทางเป็น TaxInvoice Combined ลงวันเดียวกับวันรับเงินและรับครบ ⇒ หลังย้ายหัวใบปลายทางพลิกเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" ย้อนหลังเงียบเช่นกัน (เส้นมีใบแทนบอกผู้ใช้แล้วที่ `:385-388` แต่เส้นไม่มีใบแทนไม่บอก)

**แก้ขั้นต่ำ:** ใน `BuildSettlementMoveFactsAsync` คำนวณ `ComputeServedAsReceipt(settles, …)` + `ReceiptIssuePolicy.CoversPayment(served, settles.DocumentDate, payment.PaymentDate)` (ตัวตัดสินเดิม) → ใส่ข้อเท็จจริงใหม่ `FromServedAsReceipt` →
`MoveRefusal` ปฏิเสธด้วย `ServedAsReceiptCode` + ทางไปต่อ "ยกเลิกการรับชำระแล้วบันทึกใหม่ที่ใบถูก (ระบบออกใบรับให้)" · เส้นไม่มีใบแทนที่ใบปลายทางจะถือ VAT (`CarriesTaxInvoiceRole`) ⇒ ออกใบด้วย `CreateSettlementReceiptAsync` หรือปฏิเสธ — ห้ามย้ายภาษีโดยไม่มีกระดาษ ·
เทสต์: `Move_FromCombinedSelfReceipt_Refused` + ทิศตรงข้าม (ใบที่มี REC แยกยังย้ายได้)

## B-2 MED — สิทธิ์ผิดใบ · TOCTOU ของเส้น · ถอดข้ามด่านสิทธิ์

**ที่:** `ReceiptLinkController.cs:76-93` · `:108-119` · `ReceiptLinkService.cs:578-609`

1. `CanApproveAsync`/`CanVoidAsync` ตรวจกับ `invType` = ชนิดของ **ใบปลายทาง** (`request.InvoiceId`) เท่านั้น · การย้ายคือ "ถอนการรับชำระออกจากใบเดิม" แต่ชนิดของใบเดิมไม่ถูกตรวจ —
   ผู้มี `Document.Void` ของ Invoice แต่ไม่มีของ TaxInvoice ย้ายเงินออกจาก TaxInvoice (ใบเดิม) เข้า Invoice ได้ · แก้: ตรวจ `CanVoidAsync` กับ `TypeAsync(preview.FromDocumentId)` ด้วย (ข้อเท็จจริงมีใน `snap.Move.FromDocumentId`)
2. เส้น (`plannedRoute`) มาจาก `PreviewAsync` **นอกล็อก** · ระหว่างพรีวิวกับการย้าย มีคนออกใบเสร็จย้อนหลังให้การรับชำระนี้ (`IssueReceiptForPaymentAsync`) ⇒ ใต้ล็อกกลายเป็นเส้นออกใบแทน
   แต่ผู้กดผ่านแค่ด่าน `CanVoid(invType)` — ด่าน Create/Void ของ Receipt ไม่ถูกตรวจ (ใต้ล็อกตรวจแค่ `CanApprove(Receipt)` ที่ `ReceiptLink.cs:88-90`) · แก้: ย้ายด่านสิทธิ์ใบเสร็จทั้งสามเข้า `BuildSettlementMoveFactsAsync` (→ `ReplacementReceiptBlock`) ให้ตัดสินใต้ล็อก
3. `Undo` ของเส้นย้าย ตรวจแค่ `CanVoid` ของใบที่เงินอยู่ตอนนี้ — ไม่ตรวจ `CanApprove` ของใบที่เงินจะกลับเข้า (ที่ Link บังคับ) และไม่ส่ง preview token · แก้: ตรวจสิทธิ์อนุมัติของใบเดิม + ใช้ด่านข้อ 2 เดียวกัน

## B-3 MED — คอมมิชชัน "ยอดที่เก็บได้" ยังนับให้ผู้สร้างใบเดิม

**ที่:** `CommissionService.cs:318-328` — `paymentQuery.Where(p => p.Document.CreatedBy == assignment.EmployeeId)` · ไม่อยู่ในรายการกวาด (DOCUMENT_FLOW §3.4c-B)
**สถานการณ์:** INV002 (พนักงาน ก) · INV001 (พนักงาน ข) · ย้าย 10,700 จาก INV002 → INV001 ⇒ ค่าคอมของเดือนยังให้ ก 10,700 ข 0 — เงินจ่ายพนักงานผิดคน (และยกเลิกใบเดิมไม่ได้ จึงแก้ทางอื่นไม่ได้)
**แก้:** join ใบที่มีผล (`p.MovedToDocumentId ?? p.DocumentId`) แทน navigation `p.Document` · เทสต์ทิศคู่ (ไม่ย้าย = ค่าเดิม)

## B-4 MED — ค่าปรับล่าช้าย้อนหลังของใบที่กลับมาค้าง

**ที่:** `RecurringLateFeeAccrualJob.cs:8-20, 94` — คิด `balanceDue × rate × (today − DueDate − grace)` แล้ว post Dr ลูกหนี้ / Cr รายได้อื่น · `MoveSettlementAsync:420-426` พักเฉพาะ `LastDunningSentAt` (หนังสือทวง)
**สถานการณ์:** INV002 จากรายการประจำ (LateFeeEnabled) ครบกำหนด 31 ส.ค. · ระบบบอกลูกค้าว่าชำระแล้ว (ใบรับ/สถานะ Paid) · 13 พ.ย. ย้ายเงินออก ⇒ คืน 03:00 งานคิดค่าปรับ ~74 วันทันที และลงรายได้ —
ลูกค้าโดนค่าปรับของช่วงที่บริษัทเองบอกว่าจ่ายแล้ว · พรีวิว "การทวงเงิน" ไม่ได้บอกเรื่องนี้
**แก้:** นับวันล่าช้าจาก `max(DueDate, วันที่ย้าย)` สำหรับใบที่มีการรับชำระถูกย้ายออก (ข้อเท็จจริงจาก `PaymentAllocation.ReleasedAt`) หรือให้พรีวิวเตือน + ปฏิเสธเมื่อใบเดิมเปิดค่าปรับอยู่ · เทสต์ทิศคู่

## B-5 LOW — `VoidPaymentAsync` ล็อกจากแถวจัดสรรที่อ่านก่อนล็อก

**ที่:** `DocumentService.cs:11853-11858` — `lockDocIds` = แถวจัดสรร (อ่านในธุรกรรมแต่ก่อนล็อก) + `payment.DocumentId` · ไม่มี `payment.MovedToDocumentId`
**สถานการณ์:** ยกเลิกการรับชำระพร้อมกับการย้ายที่กำลัง commit ⇒ ชุดล็อก = {INV002} · หลังได้ล็อกแถว Payment เส้นหลายใบอ่านแถวจัดสรรใหม่ (INV001) แล้วลด `PaidAmount` ของ INV001 โดย **ไม่ถือล็อก INV001** —
การรับชำระใหม่ของ INV001 ที่ commit พร้อมกันถูกทับ (lost update ที่ V1I-O3 เคยปิด) · แก้: หลัง `FOR UPDATE` แถว Payment ให้อ่านแถวจัดสรร + `MovedToDocumentId` ใหม่แล้ว `LockDocumentsForPaymentVoidAsync` ชุดที่ขาด (หรือรวม `PaymentEffectiveDocument.Of(payment)` ตั้งแต่ต้น)

## B-6 LOW — คิวงานหลังรับชำระที่ค้างอยู่ตอนย้าย

**ที่:** `PostPaymentAutomationService.cs:246-251` — `ExecuteAsync` ใช้ `run.DocumentId` (ใบเดิม) · `MoveSettlementAsync` ไม่ตรวจ/ยกเลิกแถว `PostPaymentAutomationRuns` ที่ยัง Pending
**ผล:** ย้ายภายในไม่กี่นาทีหลังรับเงิน ⇒ งานส่งอีเมล "ขอบคุณ/ใบเสร็จ" ด้วยบริบทใบเดิม (ขัด 266-1-30 "ไม่ยิงงานหลังรับชำระซ้ำ") · แก้: ในธุรกรรมย้าย ตั้งแถวที่ยังไม่เสร็จของการรับชำระนี้เป็น Skipped พร้อมเหตุ

## B-7 LOW — PDF ใบรับรายการชำระอ้างใบผิด

**ที่:** `PdfGenerationService.cs:284-298, 2894-2906` (`BuildReceiptHtml`: "เอกสารอ้างอิง: {payment.Document.DocumentNumber}") · endpoint `POST document-templates/receipt/{paymentId}/pdf` (ไม่มีหน้าเว็บเรียก — API/integration)
**แก้:** โหลดใบที่มีผลด้วย `PaymentEffectiveDocument.Of` · หรือปิด endpoint เมื่อ `MovedToDocumentId != null`

## B-8 LOW — ถอดการย้ายหลังเส้น "ใบปลายทางเป็นใบเสร็จในตัว"

**ที่:** `ReceiptLink.cs:383-389` (ยกเลิกใบเดิม · `payment.ReceiptDocumentId = null` · ไม่ออกใบแทน) + `ReceiptLinkService.cs:594-602` (ถอดถูกกันเฉพาะเมื่อมีใบแทน `ReplacesDocumentId != null`)
**ผล:** ถอด = ย้ายกลับ ⇒ ไม่มีใบเสร็จที่ยังมีผล ⇒ เส้นไม่มีใบแทน ⇒ INV002 กลับมาชำระครบ แต่ REC เดิมของมันเป็น Voided และไม่มีใบใหม่ (ลูกค้าไม่มีใบรับ) · แก้: กันถอดเมื่อการย้ายนั้นยกเลิกใบเสร็จไปแล้ว (ดูจาก `[RCPT-MOVE]`/audit `VoidedReceipt`) หรือให้เส้นย้ายกลับออกใบรับเมื่อใบเดิมเคยมี REC

## B-9 LOW — ผู้อ่านที่เหลือ (แสดงผล) + purge

- `ArApAnalysisService.cs:228-238` (รายละเอียดผู้ติดต่อ: การชำระแสดงเลขใบเดิม) · `BankService.MatchCandidates` (ชื่อใบของผู้สมัคร) · `CmsCommerceService.Fulfilment.cs:210` (`hasPayments` ของใบออเดอร์ที่รับเงินย้ายเข้ามา = false ⇒ แผนยกเลิกออเดอร์เลือกผิดทาง) — ไม่อยู่ในรายการ "จงใจคงไว้" ของ §3.4c-B ยกเว้น CMS
- purge ใบปลายทาง (หลังยกเลิก) — `DocumentService.cs:11699` ลบ Payments ตาม `DocumentId` = ใบนั้นเท่านั้น ⇒ แถวจัดสรร (IsDeleted) ของการรับชำระที่ย้ายเข้ามายังชี้ใบนี้ ⇒ FK ชนตอนลบใบ · แก้: `DeletePaymentChildrenAsync` ให้รวมแถวจัดสรรตาม `DocumentId`

---

### สิ่งที่ไม่ได้ทำ
ไม่ได้คอมไพล์/รันเทสต์ (env ไม่มี .NET SDK) · ไม่ได้ตรวจ XML e-Tax ของใบแทน (ไม่มี `PurposeCode`/อ้างใบเดิมสำหรับใบที่ยกเลิกแล้วออกใหม่ — เหมือนเส้น Reissue เดิม จึงไม่นับเป็นข้อของแพ็กนี้)
