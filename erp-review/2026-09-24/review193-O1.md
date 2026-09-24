# ฝ่ายค้านรอบ 193 — ตรวจงานทีม O1 (8561d5e · merge be1c0d7) ด้านเงิน/ภาษี/OCR

> อ่านอย่างเดียว · ไม่มี .NET SDK ในเครื่องนี้ ตรวจจากการอ่านซอร์สเท่านั้น · เลขบรรทัดอิง HEAD `f8e32ae` (branch มีงานของทีมอื่นเข้ามาระหว่างตรวจ
> เลขบรรทัดจึงอาจเลื่อน ใช้ `grep` ตามข้อความที่อ้างได้) · ขอบเขต: คำตัดสินข้อ 1 3 4 5 8 9 10 12 14 ใน `DECISIONS.md`
> · หลังตรวจเสร็จ CI ได้จับ CS0128 (`plan` ชนกับตัวแปรเดิม) ไปแล้วหนึ่งตัวและแก้ใน `d2f8b2e`

---

## CONFIRMED (มี file:line + ตัวเลข)

### C1 — ใบสำคัญจ่ายที่แปลงมาจาก PI/Expense (settlement PV): ช่อง "ยอดชำระจริง" ไม่มีผลใด ๆ และไม่มีใครบอกผู้ใช้ (ข้อ 1/4 · กฎ #4 A ห้าม silent no-op)
- `DocumentService.cs:1216-1218` PV ที่มี `RelatedDocumentId` และไม่ส่ง `PaymentType` มา จะได้ `PaymentType.Credit` เป็นค่าเริ่มต้น เส้นแปลง
  `ConvertCoreAsync` (`:9646`) ไม่ส่ง `PaymentType` จึงได้ Credit เสมอ
- JE ของ settlement PV **ลงเงินสดไม่ว่า PaymentType จะเป็นอะไร** (`:15058` `pvCashThb = Conv(doc.TotalAmount)` · สาขา `RelatedDocumentId`)
- แต่ `PaymentSettlementAdjustment.PostsCashAtApproval` (`Helpers/PaymentSettlementAdjustment.cs:90`) ตอบ false เมื่อ `paymentType == Credit`
  ⇒ `DocumentCashDelta` = 0 ⇒ `ActualPaidAmount` ถูกข้ามเงียบ ๆ
- เดินตัวเลข: PI Shopee 536 → แปลงเป็น PV แล้วกรอกยอดชำระจริง 438
  - ไม่มีบรรทัดปรับ ⇒ JE ลง Cr เงินสด **536** ทั้งที่จ่ายจริง 438 และไม่มีข้อผิดพลาดขึ้นเลย
  - ถ้าผู้ใช้ใส่บรรทัดปรับ Dr 51120 37 / Cr 51150 135 ตามคำแนะนำ ⇒ `CheckDocumentLines(0, 37, 135)` ⇒ ขึ้นข้อความเดิม
    "Adjusting JE Lines ไม่ balance" ซึ่งไม่ได้บอกว่าช่องยอดชำระจริงถูกข้าม ⇒ ทางเข้าที่สามของการจ่าย PI ใช้กลไกใหม่ไม่ได้เลย
- ฟอร์มแสดงช่อง `fActualPaidAmount` ให้เอกสารฝั่งซื้อทุกชนิด (`documents.html:8025` `_toggleActualPaidRow(isExpense)`)
- เทสต์ `PaymentSettlementAdjustmentTests` `[InlineData(PaymentVoucher, Credit, false)]` เขียนกำกับไว้ว่า "PV เงินเชื่อ (legacy) ลงเจ้าหนี้"
  ซึ่งตั้งอยู่บนสมมติฐานที่ผิด: PV ชนิด Credit ที่มี `RelatedDocumentId` ลงเงินสด
- ทางแก้ที่ควรทำ: ให้ `PostsCashAtApproval` ตอบ true สำหรับ PV ที่ `RelatedDocumentId != null` หรือตัดสินจาก "สาขา JE ที่ลงเงินสดจริง"
  ไม่ใช่จาก PaymentType

### C2 — `RoundingAdjustment` ไม่สืบทอดตอนแปลง/โคลน ทำให้เอกสารลูกเพี้ยน 0.01 (ข้อ 8 · กฎ #4 A "เอกสารลูกต้องสืบทอด")
- `ConvertCoreAsync` (`DocumentService.cs:9646`) คัดลอกบรรทัดแต่ไม่ส่ง `RoundingAdjustment` ซึ่ง `r193-O1.md` §5.6 บอกว่า "ตั้งใจ"
- เดินตัวเลขใบ Lazada: PI มีบรรทัด 4,695.34 ผลต่างปัดเศษ −0.01 ฐาน 4,695.33 VAT 328.67 รวม **5,024.00**
  - เมื่อแปลงเป็น PV/ใบลดหนี้: บรรทัด 4,695.34 ปัดเศษ 0 VAT 328.67 (คงค่าเดิมหรือคิดใหม่ก็ได้ค่าเดียวกัน) รวม **5,024.01**
  - เพดานการชำระ `:8968` `TotalAmount > BalanceDue + 0.01` ผ่านพอดี ⇒ `source.PaidAmount` 5,024.01 และ BalanceDue −0.01
  - JE: Dr เจ้าหนี้ 5,024.01 แต่ตอนตั้งหนี้ลง 5,024.00 ⇒ **เจ้าหนี้ค้างฝั่งเดบิต 0.01** · เงินสดออก 5,024.01 ทั้งที่กระดาษ 5,024.00
- เส้นขายได้รับผลแบบเดียวกัน (TaxInvoice ที่ใส่ผลต่างปัดเศษ → Receipt/CN) และ CN/DN จะอ้าง `OriginalInformationAmount` ผิด
- การแปลงเต็มใบต้องพาผลต่างปัดเศษไปด้วย ส่วนการแปลงบางส่วนต้องมีกติกาที่ตัดสินอย่างตั้งใจ

### C3 — เส้นที่สามของการสร้างบรรทัดจากสแกน ("แก้ในฟอร์มก่อน") ยังได้ 5,024.01 (§H "สามเส้นต้องเรียกตัวสร้างตัวเดียว")
- `OcrLinePreviewResponse` (`Models/DTOs/Ocr/OcrDtos.cs:181-190`) ไม่มีช่อง `RoundingAdjustment` และไม่มี `ActualPaidAmount`
- `document-scan.html` (ประมาณ `:4451-4475`) ส่งต่อ `unitPrice 1,228.04 × 4` กับ `discountAmount 216.82` ให้ฟอร์ม
  ⇒ เซิร์ฟเวอร์คิดใหม่ได้ 4,695.34 · ผลต่างปัดเศษ 0 ⇒ รวม **5,024.01 ≠ กระดาษ**
- O1 แก้ครบสองเส้น (สร้างเอกสาร และ repopulate) แต่เส้น handoff ยังไม่ได้แก้

### C4 — e-Tax XML ขาออกขัดกันเองเมื่อมีผลต่างปัดเศษ (ข้อ 8 ↔ กฎ #2 F)
- `EtaxInvoiceService.cs:855` ตั้ง `LineTotalAmount = doc.SubTotal` (ซึ่งรวมผลต่างปัดเศษแล้ว) ส่วน `TaxBasisTotalAmount = SubTotal`
- แต่ `:1323` ตั้ง `NetLineTotalAmount` = ยอดของแต่ละบรรทัด ⇒ Σ บรรทัดต่างจากหัวเอกสารเท่าผลต่างปัดเศษ
  (คอมเมนต์ `:846` เขียนเองว่าสองค่านี้ต้องเท่ากัน)
- ช่องผลต่างปัดเศษเปิดให้เอกสารขายด้วย: `documents.html:996-998` ไม่มีการแยกฝั่ง และ `CreateDocumentAsync`/`UpdateDocumentAsync` รับค่าได้ทุกชนิดเอกสาร
  สแกนฝั่งขายผ่าน `BuildScanLinesAsync` ก็ตั้งค่านี้ได้เช่นกัน ⇒ มีความเสี่ยงที่ RD จะตีกลับหรือได้ XML ที่ยอดขัดกัน
- ทางแก้คือจำกัดให้ใช้เฉพาะฝั่งซื้อ หรือให้ตัวสร้าง XML ใส่ผลต่างเป็น allowance/charge ระดับเอกสาร

### C5 — `[Σ-GAP]`: ระบบประทับ "รับทราบคำเตือน" แทนผู้ใช้ในสองทางเข้าของเว็บ (ข้อ 12 · R1)
- `ApprovalService.cs:432` (workflow อนุมัติหลายขั้นบนเว็บ) ส่ง `acknowledgeWarnings: true` ในนาม `approval-rule:{comments}`
- `SignatureApprovalService.cs:446` (ผู้เซ็นภายนอก) และ `:517` (ส่ง **userId จริง**) ก็ส่ง `acknowledgeWarnings: true`
- ผลคือ `ApproveDocumentAsync` เขียน audit `APPROVE-ACK-WARNINGS` และหมายเหตุ "— รับทราบคำเตือนตอนอนุมัติ — … ยืนยันโดย {userId}"
  ทั้งที่ไม่มีใครเคยเห็นข้อความ `[Σ-GAP]` ⇒ เป็นการประทับรับทราบแทนผู้ใช้จริง ทีมเขียนไว้เองว่า "ApprovalService ส่ง ack เอง"
- workflow เดียวกันแต่อนุมัติผ่านมือถือ **ได้**คำเตือน (`MobileApiService.cs` ด่านขั้นสุดท้าย) ⇒ เว็บกับมือถือทำตัวไม่เหมือนกัน
  ทั้งที่สเปกระบุว่าต้องกดรับทราบทั้งเว็บและมือถือ
- `DocumentService.cs:1529` อนุมัติ PV เงินสดอัตโนมัติด้วย ack true ด้วย (ของเดิม แต่ครอบ `[Σ-GAP]` ใหม่ไปด้วย)

### C6 — API v1 เรียก AI ทุกครั้งแล้วทิ้งคำตอบ (ข้อ 12 ↔ กฎเหล็ก #1)
- `DocumentsV1Controller.cs:244` เรียก `ApproveDocumentAsync(ack:false)` ก่อน แล้วค่อยจับ exception
- แต่ก่อนจะ throw `ApproveDocumentAsync` ได้เรียก `_aiAugmenter.SuggestApprovalWarningFixesBulkAsync` ไปแล้ว (`DocumentService.cs:4729`
  มีงบเวลา 8 วินาที) ⇒ การอนุมัติทาง API ของใบที่มี gap ทุกครั้งเสียค่าเรียก AI 1 ครั้ง และสร้างแถว feedback ที่ไม่มีวันได้ค่าที่ผู้ใช้เลือก
  (loop ไม่ปิด) · response ช้าลงได้ถึง 8 วินาที · คำตอบของ AI ถูกทิ้ง
- ทางแก้คือคัดคำเตือนก่อนแล้วเรียกครั้งเดียว หรือเพิ่มพารามิเตอร์ "ไม่ต้องเสริม AI"

### C7 — endpoint ใหม่ทั้งสองตัวไม่ผ่านด่านไฟล์แนบ/สแกนที่ทีม S2 เพิ่มในรอบเดียวกัน (ข้อ 14 · R5 ทางเข้าอื่น)
- `OcrController.cs:423` `GET amount-audit` คืนชื่อผู้ขาย ชื่อไฟล์ เลขที่บนกระดาษ เลขเอกสาร และยอดเงิน ของสแกนได้สูงสุด **1,000** ใบ
  ให้สมาชิกทุกคนของบริษัท (มีแค่ `[Authorize]` ระดับคลาส ไม่มีด่านสิทธิ์)
- ในขณะที่รายการสแกน/ผลสแกน/คิวรีวิวกรองผ่าน `IAttachmentAccessGate.HiddenScanIdsAsync`/`DenyScanAsync` แล้ว (`:297`, `:311`, `:1191`)
  ⇒ endpoint นี้เปิดทางเลี่ยงด่านของ S2
- `OcrController.cs:416` `GET documents/{id}/settlement-proposal` ก็ไม่ตรวจว่าผู้ใช้เปิดเอกสารนั้นได้หรือไม่ (ข้อมูลรั่วน้อยกว่า)
- สิ่งที่ผ่าน: ทั้งสองตัวอ่านอย่างเดียวจริง · กรอง `CompanyId` ครบทุก query (`OcrService.GetStoredAmountAuditAsync` ทั้งตารางสแกนและเอกสาร,
  `GetSettlementProposalAsync`) · หน้า `ocr-amount-audit.html` escape ทุกค่าผ่าน `Layout.esc` · `dead_link_check` ได้ 0 ·
  เข้าหน้าได้จากหัวหน้า `document-scan.html` เท่านั้น (ไม่อยู่ในเมนูข้าง)

### C8 — PI ที่มี `[PAY≠TOTAL]` อนุมัติอัตโนมัติไม่ได้ตลอดไป (ตรรกะวน)
- `OcrPostingReadiness.Evaluate` ปลดล็อกได้ก็ต่อเมื่อมี `[PAY-SETTLED]` แต่ `ApplyScanSettlementPlanAsync` (`OcrService.cs:7361`) ไม่เขียน
  `[PAY-SETTLED]` ให้เอกสารตั้งหนี้เลย (ตั้ง `ActualPaidAmount` แล้ว return) และจะบันทึกการชำระได้ก็ต่อเมื่อเอกสารอนุมัติแล้วเท่านั้น
- ข้อความที่ขึ้น "ยังไม่ได้บันทึกบรรทัดปรับส่วนต่าง" จึงชี้ไปทางที่ทำไม่ได้ก่อนอนุมัติ · ไม่ใช่การถดถอยจากรอบ 192 แต่ข้อความเหตุผลไม่ตรงความจริง
  (F2 ข้อ 7) · ทีมเปิดเป็นคำถามข้อ 2 ไว้แล้ว

### C9 — หนี้ค้าง 98 ปิดด้วยบรรทัดปรับอย่างเดียวไม่ได้
- ทางเข้าชำระที่ไม่รู้จักบรรทัดปรับ ได้แก่ API integration (`IntegrationService.cs:1183` `PaidAmount += request.Amount`) และนำเข้าไฟล์
  (`ImportExportService.cs:1329`) ⇒ ชำระ 438 แล้วเหลือหนี้ 98 (ไม่ติดลบ และมองเห็นได้)
- จะปิด 98 ที่เหลือด้วยบรรทัดปรับอย่างเดียวไม่ได้ เพราะ `CreatePaymentAsync` บังคับ `Amount > 0` (`:11010`) และ `Check` บังคับ
  ยอดที่ปิด ≤ ยอดค้าง ⇒ Amount ต้อง ≤ 0 · ทางเดียวที่เหลือคือ void แล้วบันทึกใหม่ ⇒ ไม่มีทางไปต่อสำหรับผู้ใช้ (F2 ข้อ 8)
- การชำระหลายใบ (`:11467`) throw พร้อมข้อความ ✅ · การจับคู่ธนาคารไม่ได้สร้าง Payment จึงไม่เกี่ยว

### C10 — เทสต์ของ O1 เกือบทั้งหมดทดสอบแค่ helper (ตอบข้อ 10)
- 7 ใน 8 ไฟล์เรียกเฉพาะ helper แบบ pure · `EtaxPdfXmlExtractorTests` เป็นไฟล์เดียวที่เรียกโค้ดจริง (`TryExtract`/`ParseEtaxXml`)
- `PaymentSettlementAdjustmentTests` ส่วนที่ว่า "JE สมดุลทั้งสองทางเข้า" คำนวณ JE ด้วยมือในไฟล์เทสต์เอง (สำเนาสูตรของ service)
  ⇒ เทสต์ยังเขียวแม้ `CreatePaymentJournalAsync` จะพัง
- ดูรายการเต็มในหมวด "เทสต์ที่ยังเขียวแม้ถอดการแก้" ท้ายไฟล์

### C11 — ความเสี่ยงคอมไพล์ (ตอบข้อ 11)
- CS0128 (`plan`) หลุดไปถึง CI จริง และแก้แล้วใน `d2f8b2e`
- ไล่ตัวแปรใหม่ทั้งหมดใน `AutoPostToJournalAsync`, `CreatePaymentAsync`, `CreatePaymentJournalAsync`, `ProcessScan` และ `BuildScanLinesAsync`
  แล้วไม่พบชื่อชนกันอีก
- ผลรัน checker: `namespace_shadow` `record_arg` `nullable_arg` `undeclared_local` `service_interface` `arg_type` `tuple_name_merge`
  `dead_link` `html_attr_escape` ได้ 0 ทุกตัว · `dead_helper` ไม่มีตัวใหม่
- `check_all` ล้มสองจุด แต่ไม่ใช่ของ O1: `using_check` ล้มใน `.claude/worktrees/agent-aa9…` (worktree ของ agent อื่น) และ
  `test_inventory` ล้มเพราะงานที่ merge ภายหลัง
- `DocumentLabels` หลัง merge กับ L2: มีทั้ง `total_deposit_tax_invoiced` และ `total_rounding` ครบทั้ง th และ en · renderer ทั้งสองใช้ทั้งสองคีย์
  ลำดับและเงื่อนไข (`!hideVatBreakdown`) ตรงกัน (`PdfGenerationService.cs` ประมาณ `:2125` ↔ `.DocumentRenderer.cs` ประมาณ `:912`)

---

## PLAUSIBLE (เห็นทางเกิดจากโค้ด แต่ต้องมีข้อมูลจริงยืนยัน)

- **P1 — ข้อ 9 `OcrBuyerOnPaper` ปิดเคลมผิดใบเมื่อรูปเบลอ (ทิศที่ความเสียหายเงียบหลังอนุมัติ)**
  - ช่อง TaxId มีแค่ Printed/Missing · จะได้ Unknown ก็ต่อเมื่อไม่มีข้อความเลย (`Helpers/OcrBuyerOnPaper.cs` `Read`)
  - ถ้าเลขผู้ซื้อบนกระดาษอ่านเพี้ยน ≥ 2 หลัก หรือมีตัวอักษรปน ⇒ checksum ไม่ผ่านและไม่ใช่ near-miss ⇒ Missing ⇒ **บล็อก §82/5(1)**
    แม้ชื่อผู้ซื้อจะพิมพ์อยู่
  - `[VAT-CLAIM]` ไม่อยู่ใน `OcrPostingReadiness.BlockingTags` ⇒ ปุ่ม "สร้าง+อนุมัติ" บนเว็บ และปุ่ม "อนุมัติเลย" ใน LINE ลง VAT เป็นต้นทุนได้โดยไม่มีคนดู
    หลังอนุมัติแล้วบรรทัดถูกล็อก ⇒ ต้องกลับรายการเท่านั้น
  - DOCTRINE G5 กำหนดทิศปลอดภัยเป็น "มองเห็น**และแก้ทัน**" ซึ่งเป็นจริงเฉพาะเมื่อมีคนดูก่อนอนุมัติ
  - ข้อเสนอ: กรณี "ชื่อพิมพ์อยู่แต่ไม่พบเลข" จากข้อความ OCR (ไม่ใช่ XML) ให้เป็น Unknown/เตือน หรือทำให้ `[VAT-CLAIM]` ตัวใหม่ (RD-82/5(1)-BUYER)
    เป็นตัวหยุดการอนุมัติอัตโนมัติ
  - ใบอย่างย่อและใบเสร็จไม่ถูกแตะ ✅ (ทำงานเฉพาะเมื่อ `role.InputVatClaimable == true && OurRole=="Buyer" && docVat>0`)
- **P2 — ผลต่างปัดเศษจากสแกนไม่มีเพดาน**
  - `BuildScanLinesAsync` สะสม shift ได้ถึง n × 0.01 โดยไม่เรียก `DocumentRounding.Validate` (`OcrService.cs:6910`)
  - ใบ ≥ 100 บรรทัดที่เศษไปทางเดียวกันจะได้ |x| ≥ 1 ⇒ พอเปิดแก้แล้วบันทึก `Validate` จะ throw "ไม่ใช่เศษสตางค์" ⇒ ผู้ใช้บันทึกไม่ได้
- **P3 — ค่าเผื่อไม่ตรงกันระหว่างข้อเสนอกับ AutoPost**
  - ข้อเสนอรับได้ถึง `OcrPaperAmounts.ExactTol` = 0.02 (`FromDecomposition` และด่าน `ApplyScanSettlementPlanAsync`)
  - แต่ AutoPost ตรวจที่ 0.005 (`DocumentSettlementState.Tolerance`)
  - ถ้ายอดเอกสารกับข้อเสนอต่างกัน 0.01–0.02 ⇒ `[PAY-SETTLED]` ปลดการอนุมัติเอง แล้วการอนุมัติล้มเป็น `[APPROVE-FAIL]` (มองเห็นได้ ไม่เงียบ)
- **P4 — รายงานข้อ 14 น่าจะรายงานสแกนใหม่ที่ถูกต้องไปด้วย**
  - `ScanDiscountIsSettlement` ฟ้องทุกสแกนที่ `ExtractedDiscountAmount` เป็นการปรับตอนชำระ
  - แต่ `ApplyTotalFirst` ไม่ล้าง `data.DiscountAmount` ⇒ ใบ Shopee ที่สแกนหลังรอบ 192 (ซึ่งสร้างเอกสารถูกแล้ว) ก็จะอยู่ใน "รายงานข้อมูลเก่าที่ผิด"
  - ควรเทียบกับเอกสารที่สร้างจริงก่อนจะฟ้อง
- **P5 — การกระจายผลต่างปัดเศษลงคอลัมน์รายงาน**
  - `TaxService.VatableBase` (`:3107`) = SubTotal − บรรทัดยกเว้น ⇒ ผลต่างปัดเศษทั้งก้อนไปอยู่คอลัมน์ 7%
  - ใบผสม V/E ที่เศษมาจากบรรทัดยกเว้นจะทำให้คอลัมน์ 7% กับคอลัมน์ยกเว้นคลาดกัน 0.01
  - รายงาน WHT (`TaxService.cs` ประมาณ `:1842`) ใช้ Σ บรรทัดแทน SubTotal ⇒ ฐานต่างกัน 0.01
  - VAT (35.07/328.67) ไม่ถูกแตะ ✅
- **P6 — e-Tax T05/T06 (ใบกำกับอย่างย่อ) ถูก map เป็น "Receipt"**
  - เส้นสร้างเอกสารถือว่า "ไม่ใช่ใบกำกับ" ⇒ อาจพักภาษีไว้ที่ 11640 และขึ้น `[TAX-INV-PENDING]` "รอใบกำกับจริง"
    แทนที่จะปิดเคลมตาม §82/5(2) (ขึ้นกับว่าตัวอนุมานบทบาทจับได้ก่อนหรือไม่)
  - เดิมตกไป fallback ตามชื่อ root · ต้องตรวจต่อด้วย XML T05 จริง
- **P7 — ความปลอดภัยของ XML/zip (ของเดิม ไม่ได้แย่ลง)**
  - `EtaxPdfXmlExtractor.Inflate` (`:370`) ไม่มีเพดานขนาด ⇒ zip bomb ใน FlateDecode ทำให้หน่วยความจำหมดได้
  - `XDocument.Parse` ใช้ DtdProcessing.Parse พร้อมเพดาน entity 10M และไม่มี resolver ⇒ XXE ภายนอกไม่ resolve
  - O1 ไม่ได้แตะสองจุดนี้ แต่ตอนนี้เส้น e-Tax เรียก `PdfTextLayerExtractor` เพิ่มอีกหนึ่งรอบ (มีเพดานจำนวนหน้าเดิมอยู่)
- **P8 — การกระทบยอดธนาคารของ PV จ่ายในตัว**
  - ถ้าจับคู่แบบ Document จะเห็น `TotalAmount` **536** (`BankService.Reconciliation.cs` ประมาณ `:612`, `:872`) ไม่ตรงกับ statement 438
  - ถ้าจับคู่แบบ JE ใช้ยอดสุทธิของขาบัญชีธนาคาร = **438** ✅
  - PI ที่ชำระผ่าน Payment ใช้ `Payment.Amount` 438 ✅
- **P9 — การถดถอยตาม §H ที่ไม่มีเทสต์คุม**
  - `FromPrintedLine` ถูกใช้กับทุกบรรทัดของทุกสแกน
  - ใบที่เคยถูก (IKEA ราคารวม VAT, Makro ฯลฯ) ถ้า จำนวน × ราคา ต่างจากยอดพิมพ์ 0.01 จะได้แถว "ผลต่างจากการปัดเศษ ±0.01" บนกระดาษพิมพ์
    และการอนุมัติจะต้องมีผัง 54960 · ยอดรวมไม่เปลี่ยน
  - ไม่มีเทสต์ระดับ `BuildScanLinesAsync` ยืนยันเรื่องนี้

---

## ตรวจแล้วไม่มีปัญหา

- **ข้อ 1/3/4 — ใบตั้งหนี้ (PI) Shopee**
  - `Check(438, 536, [+37, −135])` ได้ settled **536** และ net **98**
  - `PaidAmount += 438 + 0 + 98` (`:11235`) ⇒ BalanceDue **0**
  - JE (`CreatePaymentJournalAsync` `:15744`): Dr เจ้าหนี้ 438 + 98 = 536, Dr 51120 37 / Cr เงินสด 438, Cr 51150 135
    ⇒ **573 = 573** ✅
- **ข้อ 1/3/4 — PV จ่ายในตัว (PaymentType Cash และไม่มี RelatedDocumentId)**
  - Dr ค่าใช้จ่าย 500.93 + VAT 35.07 + Dr เงินสด 98 + Dr 51120 37 = **671**
  - Cr เงินสด 536 + Cr 51150 135 = **671**
  - เงินสดออกสุทธิ **438** ✅
- **ภาษีซื้อ 35.07** ไม่ถูกแตะทั้งสองทาง (บรรทัด/หัวเอกสาร VAT ไม่เปลี่ยน · รายงานภาษีซื้ออ่าน `Lines.VatAmount`) ✅ ตามข้อ 2 ที่ให้คงเดิม
- **WHT**
  - แบ่งตามสัดส่วนจาก `(Amount + settleNet) × WHT / Total` (`:11168`) = ฐานหนี้ที่ปิด (ตามใบ 536) ไม่ใช่เงินสด 438 · ค่าส่ง 37 ไม่เข้าฐาน ✅
  - OCR ไม่ลงข้อเสนออัตโนมัติเมื่อมี WHT ✅
- **ทิศตรงข้าม (ไม่มีบรรทัดปรับ · ไม่ระบุยอดชำระจริง · ผลต่างปัดเศษ 0)** — เดินเส้นเดิมทุกจุด
  - ด่านจ่ายเกินเดิมทำงานเมื่อ `settleLines.Count == 0`
  - `settleNet = 0` ⇒ `apClear` ค่าเดิม
  - `DocumentCashDelta = 0`
  - สูตร `SubTotal`/`TotalAmount` เหมือนเดิม
  - renderer พิมพ์แถวผลต่างปัดเศษเฉพาะเมื่อ ≠ 0
  - ใบที่ลงตัวอยู่แล้วไม่มีบรรทัดปัดเศษ ✅
- **Void**
  - `ReversePaymentInternalAsync` กลับ JE ทั้งใบตาม Reference (รวมขาปรับ)
  - `PaidAmount −= Amount + Fee + SettlementAdjustmentAmount` (`:8717`)
  - ยอดธนาคารกลับด้วย `Amount` (438) ตรงกับตอนบันทึก
  - void ตัว PV ก็กลับ JE ทั้งใบ ✅
- **ข้อ 8 — ผัง 54960**
  - ไม่เคยมีในผังมาตรฐาน · `gl_code_check` ได้ 0
  - migration เป็น idempotent ผ่าน unique `(CompanyId, AccountCode)` (`AccountingDbContext.cs:731`) + `ON CONFLICT DO NOTHING`
    ใส่ต่อบริษัทจากแถวผังที่มีอยู่ ✅ · `InputVatClaimable = false`
  - `FindAccountAsync("54960")` เป็น exact-match ก่อน prefix ✅
  - ไม่มีบรรทัดติดลบ (ผลต่างเก็บที่หัวเอกสาร · `FromPrintedLine` ทำงานเฉพาะเมื่อ calc > 0) ✅
  - ขา JE ลงเฉพาะเมื่อยอดที่ไม่สมดุลเท่ากับผลต่างที่ประกาศไว้ ✅
- **ข้อ 5 — `[NO-ITEMS]`**
  - ไม่อยู่ใน `BlockingTags` (`OcrNoItemsNoteTests:32-34`) · แสดงบนเว็บและ LINE อย่างเดียว · ไม่ใส่ซ้ำเมื่อสร้างซ้ำ ✅
- **ข้อ 12 — ตัวตัดสินและทางเข้าอื่น**
  - `OcrPostingReadiness` ยังเป็นตัวตัดสินการอนุมัติเองตัวเดียว (`OcrController:336`, `LineBot:666/811`)
  - `[PAY-SETTLED]` ปลดเฉพาะ `[PAY≠TOTAL]` และไม่ติดไปกับสแกนซ้ำ ✅
  - เว็บหน้าเอกสาร (`DocumentController:611`) ขึ้นกล่อง 422 แล้วต้องกดรับทราบ ✅
  - มือถือตรวจที่ขั้นสุดท้ายแล้วคืน `RequiresAcknowledgement` ✅
  - API ไม่หยุดเฉพาะเมื่อ**ทุก**คำเตือนเป็น gap และคืน `scanAmountGap`/`warnings` ✅
  - LINE ถูกกันด้วย readiness อยู่แล้ว
- **ข้อ 10 — e-Tax extractor**
  - การ map ETDA (388/T02/T03/T04 → TaxInvoice · T01/T05/T06 → Receipt · 380 → Invoice · T07 → null) ตรงตาม ขมธอ.3-2560
  - PDF ที่ `PdfAttachmentInjector` ของเราเองสร้าง (`>> >>\nstream\n`) ยังแมตช์ regex `StreamKeyword` ตัวใหม่ ✅
  - ข้อควรรู้: ก่อน O1 ไม่มีเทสต์หรือ fixture ของ extractor เลย ⇒ "รูปแบบอื่นที่เคยอ่านได้" ไม่มี baseline ให้เทียบ
    มีเพียงการอ่านโค้ดยืนยันว่าเส้นเดิม (`/Subtype /text#2Fxml`, `/F (x.xml)`) ยังใช้ได้
  - PDF ที่เขียน `stream\r` โดยไม่มี LF (ไม่ตรงสเปก) จะอ่านไม่ได้แล้ว (เดิมอ่านได้) — ความเสี่ยงต่ำ
- **ข้อ 9** — ใช้ `FromStructured` กับ XML · ชื่อที่ระบบเติมเองไม่ถูกนับเป็นหลักฐาน · เหตุผลรายบรรทัดใช้ข้อความ `[VAT-CLAIM]` ตัวแรกที่ตรงสาเหตุ ✅
- **ข้อ 9 (checker)** — การแก้ `undeclared_local_check.py` แค่เพิ่มชื่อที่ตามหลัง `is not T` เข้าชุด "ประกาศแล้ว"
  ซึ่งตำแหน่งนั้นเป็นการประกาศตัวแปรเสมอ ⇒ ไม่บังของจริง · รันแล้วได้ 0 ✅
- **§H — ตัวอย่างที่ต้องยังถูก**
  - O1 ไม่ได้แก้ `OcrPaperSamples.cs` หรือเทสต์เดิมตัวใด (diff มีแต่ไฟล์เทสต์ใหม่)
  - helper เดิม (`OcrTotalDecomposer` `OcrPostingReadiness` `OcrScanSnapshot`) มีแต่การเพิ่ม ⇒ Makro 951/49/1,000, ลักกี้เวย์, IKEA และใบ M/U/S
    ของรอบ 192 ในระดับ helper ให้ผลเดิม
  - ส่วนที่เปลี่ยนในระดับ service ดู P9

---

## เทสต์ที่ยังเขียวแม้ถอดการแก้ใน service

| การแก้ที่ถอดออก | เทสต์ของ O1 ที่จับได้ |
|---|---|
| `CreatePaymentAsync` ไม่บวก `settleNet` เข้า `PaidAmount` / ไม่เก็บ `SettlementAdjustmentAmount` | ไม่มี |
| `CreatePaymentJournalAsync` ไม่บวก `payment.SettlementAdjustmentAmount` เข้า `apClear` / ไม่ลงขาปรับ | ไม่มี (`PaymentSettlementAdjustmentTests` คำนวณ JE ด้วยมือในเทสต์) |
| void ไม่หัก `SettlementAdjustmentAmount` | ไม่มี |
| AutoPost ไม่ลงขา `settlementCashDelta` / ขา 54960 | ไม่มี |
| `BuildScanLinesAsync` ไม่ shift บรรทัด / ไม่ตั้ง `RoundingAdjustment` · repopulate ไม่บวกผลต่าง | ไม่มี (`DocumentRoundingTests` ทดสอบแค่ `FromPrintedLine`/`JournalLine`) |
| `OcrService` ไม่เรียก `OcrBuyerOnPaper` (หรือเงื่อนไข `else if` ผิด) | ไม่มี |
| `ApplyScanSettlementPlanAsync` / `ApplyEtaxSettlement` ไม่ถูกเรียก | ไม่มี |
| `CollectApprovalWarningsAsync` ไม่เติม `[Σ-GAP]` · มือถือไม่ตรวจ · V1 ไม่จับ exception | ไม่มี |
| `GetStoredAmountAuditAsync` ไม่กรอง `CompanyId` | ไม่มี |
| renderer HTML/QuestPDF ไม่พิมพ์แถวผลต่างปัดเศษ | ไม่มี |
| การแก้ใน `EtaxPdfXmlExtractor` | **จับได้** (`EtaxPdfXmlExtractorTests` เรียก `TryExtract`/`ParseEtaxXml` จริง) |

---

## สิ่งที่ควรทำต่อ (เรียงตามความเสี่ยงเงิน)

1. **C1**: ให้ `PostsCashAtApproval` ครอบ settlement PV (มี `RelatedDocumentId`) หรือล็อก/ซ่อนช่องยอดชำระจริงเมื่อไม่มีผล · เพิ่มเทสต์ทิศตรงข้าม
2. **C2/C3**: ให้ convert/clone (เต็มใบ) และ `line-preview` พาผลต่างปัดเศษไปด้วย · ถ้าตัดสินใจไม่พาไป ต้องพิสูจน์ว่ายอดลูกเท่ายอดแม่
3. **C4**: จำกัดผลต่างปัดเศษให้เฉพาะฝั่งซื้อ หรือให้ตัวสร้าง e-Tax XML รองรับ
4. **C5/C6**: เส้นเว็บ workflow/ลายเซ็นต้องแสดง `[Σ-GAP]` ก่อนกดอนุมัติ (ห้ามประทับรับทราบแทนผู้ใช้) · ให้ V1 คัดคำเตือนก่อนเรียก AI
5. **C7**: ให้ `amount-audit` และ `settlement-proposal` เดินผ่าน `IAttachmentAccessGate` และใส่ด่านสิทธิ์อ่านเอกสาร
6. **P1**: เจ้าของหรือทีมภาษีต้องตัดสินว่า "ชื่อพิมพ์อยู่แต่อ่านเลขไม่ออก" = Unknown หรือ = บล็อก · และ `[VAT-CLAIM]` ของ §82/5(1)-BUYER
   ควรหยุดการอนุมัติอัตโนมัติหรือไม่
7. เพิ่มเทสต์ระดับ service (หรือแยกออกมาเป็น pure builder) ให้ `CreatePaymentJournalAsync`, AutoPost ขาส่วนต่าง/ปัดเศษ และ `BuildScanLinesAsync`
