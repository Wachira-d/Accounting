# ทีม A — ความสอดคล้องของ "ประเภทเอกสาร" ทั่วระบบ

(เขียนแบบ append ตั้งแต่ finding แรก · branch claude/erp-system-review-team-660mev · HEAD 444d2cb · 2026-09-05)
(อ้าง enum: DocumentType 16 ค่า AllEnums.cs:460-483 · DocumentStatus 9 ค่า :498-509)

## สรุป 5 บรรทัด
1. โครง "ประเภทเอกสาร" ฝั่ง backend ที่ครบ 16/16 จริงมีแค่ 3 จุด (prefix · หัวเอกสาร th/en · e-Tax gate) — ที่เหลือเป็น if-chain + default กลืนเงียบ (`_ => 0` สต๊อก · `_ => "เอกสาร"` เทมเพลต) และมีชุด "ฝั่งซื้อ/ขาย" เขียนมือ ≥ 13 จุดนอก `DocumentSide` (A-13)
2. บั๊กเงิน/ภาษีจริง 2 ตัว: §65ตรี(4) YTD ค่ารับรองนับเฉพาะ `Status == Approved` ⇒ ใบที่จ่ายแล้วหลุด cap ทั้งปี (A-01) · ขายสดด้วยใบเสร็จ standalone ลงรายได้แต่ไม่ตัดสต๊อก/COGS ขณะที่ TIV cash sale ตัดครบ (A-06)
3. Portal ลูกค้าเห็น+ดาวน์โหลด PDF ของเอกสารทุกสถานะ (Draft/รออนุมัติ/ตีกลับ/ยกเลิก) และทุกชนิดรวมใบภายในฝั่งซื้อ — ไม่มี `Status` ในเงื่อนไขเลยทั้ง 3 เมธอด (A-02)
4. `== DocumentStatus.Approved` เป๊ะ 26 จุด: ผิดจริง 4 กลุ่ม (A-01/A-03 bank-AI+aging/A-04 DOC_NO_JE false-positive/A-05 อัตราแปลงใบเสนอราคา) ที่เหลือถูกตามบริบท (ระบุไว้ในส่วน "ไม่ใช่บั๊ก")
5. UI ไม่ตรง backend: โมดัลแปลงซ่อน option ที่มี ⇒ PO→GRN แปลงไม่ได้เลย (A-07) · กฎอนุมัติเลือกได้ 5/16 ชนิด (A-08) · risk.html ส่ง `JournalEntry` เข้า enum → 400 (A-09) · ชื่อไทยของชนิด drift ใน 8 map C# + 3 JS (A-10) · สถานะกรอง/ป้ายไม่ครบ 9 (A-11)

## Findings (เรียง P0→P3)

### A-01 [P1][S] ด่าน §65 ตรี(4) ค่ารับรอง YTD นับเฉพาะใบ `Status == Approved` — ใบที่จ่ายแล้ว (Paid) หลุดทั้งหมด
- ไฟล์: Accounting/Services/Implementations/DocumentService.cs:11831-11842 · เทียบ :5183-5192 (PV/Receipt/RV/CIL ที่อ้างใบตั้งหนี้ → `Status = Paid` ทันทีตอน approve) · CreatePaymentAsync ตั้ง PI/Expense เป็น Paid/PartiallyPaid หลังจ่าย (:4095-4100 รูปเดียวกันฝั่ง AR)
- โค้ด: `.Where(l => l.Document.CompanyId == companyId && l.Document.Id != doc.Id && l.Document.Status == DocumentStatus.Approved && … (PurchaseInvoice || Expense || PaymentVoucher || CertificateInLieu) && (l.Description.Contains("รับรอง") …))` → `priorEntertainment`
- ทำไมพัง: (1) คอมเมนต์เหนือ query บอกเจตนาชัด "เพื่อให้ excess คำนวณตาม YTD จริง" (2) แต่ PV ที่แปลงมาจาก Expense/PI ถูกตั้ง `Paid` ตั้งแต่ approve (:5191) และ PI/Expense ที่จ่ายผ่าน Payment ก็ไป Paid/PartiallyPaid (3) ⇒ ยอดค่ารับรองที่ "จ่ายไปแล้วจริง" ทั้งปีไม่ถูกนับ เหลือแต่ใบที่ยังค้างจ่าย ⇒ `priorEntertainment` ต่ำกว่าจริงเสมอ ⇒ cap `MAX(0.3% revenue, 0.3% capital)` ถูกใช้ซ้ำหลายรอบ
- ผลกระทบ: ค่ารับรองส่วนเกิน cap ไม่ถูก mark `nonDeductible` → worksheet "บวกกลับ" ภ.ง.ด.50 ขาด → เสียภาษีต่ำกว่าจริง (สรรพากรประเมินย้อนหลัง + เบี้ยปรับ) — ผิดกฎเหล็ก #2 L(4) ที่บอกว่า "ห้ามให้ user ลืม"
- defect class: "ด่านสถานะที่เขียนว่า `== Approved` เป๊ะ ๆ มักผิด เพราะสถานะเดินต่อได้เอง" (CLAUDE.md #4 A/บทเรียน e-Tax gate)
- ทางแก้ที่เสนอ: เปลี่ยนเป็น "ห้ามสถานะไหน" `Status is not (Draft or WaitingApproval or Rejected or Voided)` — และ pure test ที่ใส่ PV Paid 1 ใบแล้วยืนยันว่า YTD รวม
- ความมั่นใจ: สูง (อ่านทั้งสองฝั่งแล้ว) — ควรเช็คต่อว่า `Section65TerValidator` รับ `priorEntertainment` ไปใช้เป็น YTD จริง (ชื่อตัวแปรบอกอย่างนั้น)

### A-02 [P1][S] Portal ลูกค้าเห็นเอกสาร **ทุกสถานะ** (Draft/WaitingApproval/Rejected/Voided) และ **ทุกประเภท** (รวมใบภายในฝั่งซื้อ) ของ contact ตัวเอง — ดาวน์โหลด PDF ได้ด้วย
- ไฟล์: Accounting/Services/Implementations/PortalService.cs:200-214 (`GetMyDocumentsAsync`) · :216-227 (`GetDocumentAsync`) · :229-262 (`DownloadDocumentPdfAsync`) · Controllers/PortalController.cs:66,75 · wwwroot/portal.html:316-330 (ไม่มีการกรองสถานะฝั่ง client เช่นกัน)
- โค้ด: `var query = _db.Documents.Where(d => d.CompanyId == companyId && d.ContactId == contactId); if (… Enum.TryParse<DocumentType>(documentType, out var docType)) query = query.Where(d => d.DocumentType == docType);` — **ไม่มี `Status` ในเงื่อนไขเลยทั้งสามเมธอด** (grep `DocumentStatus\.` ใน PortalService.cs = 0 บรรทัด)
- ทำไมพัง: (1) เอกสาร Draft มีเลข `DRAFT-{guid}` และยอด/BalanceDue เต็ม (2) portal.html:319 คำนวณป้ายจาก `balanceDue` อย่างเดียว ⇒ ใบ Draft/รออนุมัติ/ถูกตีกลับ ขึ้นเป็น **"รอชำระ"** สีเหลืองต่อหน้าลูกค้า · ใบ Voided ที่ BalanceDue=0 ขึ้น "ชำระแล้ว" (3) ปุ่ม PDF (:330) เรียก `DownloadDocumentPdfAsync` ซึ่งไม่กันสถานะ ⇒ ลูกค้าพิมพ์ใบร่างที่ยังไม่มีเลขจริง/ยังไม่อนุมัติออกไปใช้ได้ (4) ถ้า contact เป็นทั้งลูกค้าและผู้ขาย (หรือได้ portal access ในฐานะ vendor — `CreateAccessAsync` :35-60 ไม่เช็ค ContactType) ก็เห็น `PurchaseRequisition`/`Expense`/`CertificateInLieu`/`PaymentVoucher` ที่เราบันทึกภายในเกี่ยวกับเขา รวมหมายเหตุยอด
- ผลกระทบ: ลูกค้าโอนเงินตามใบร่างที่ยอดยังไม่ final / ใบที่ถูก Reject · เอกสารที่ไม่มีผลทางกฎหมาย (ไม่มีเลข §86/4) ไหลออกไปนอกบริษัท · ใบภายใน (ราคาซื้อ/ค่าใช้จ่าย) รั่วถึงคู่ค้า
- defect class: "ด่านที่อ่อนกว่าแต่คืนข้อมูลมากกว่าคือช่องที่ใหญ่ที่สุด" + "ค่า default ที่แปลว่า 'ยังไม่ระบุ' ต้องแยกจากค่าที่มีความหมายจริง" (portal ตีทุกใบเป็นใบจริง)
- ทางแก้ที่เสนอ: helper เดียว `PortalVisibleDocuments(query)` = `Status ∉ {Draft, WaitingApproval, Rejected}` (Voided โชว์ได้แต่ต้องติดป้าย "ยกเลิก") + จำกัดชนิดด้วย `DocumentSide.IsSales(type)` (หรือ AR set เดียวกับ CustomerStatementController:56-61) ใช้ทั้ง 3 เมธอด · portal.html แสดง `status` จาก server ไม่ใช่เดาจาก balanceDue
- ความมั่นใจ: สูง (API) · ผลบนจอ = สูง (อ่าน portal.html:316-330 แล้ว) · เรื่อง vendor เห็นใบภายใน = กลาง (ต้องดูว่าใครสร้าง portal access ให้ vendor ได้ในทางปฏิบัติ — vendor-portal แยกไหม)

### A-03 [P2][S] ตัวช่วย AI จับคู่ธนาคาร/aging นับเฉพาะ `Approved || PartiallyPaid` — ใบที่ **Sent** และ **Overdue** (สถานะที่ job ตั้งให้เองเมื่อเลยกำหนด) หายจากผู้สมัครทั้งหมด
- ไฟล์: Accounting/Services/Ai/BankAiAugmenter.cs:54-56,66-74 · Accounting/Services/Ai/AdvancedAiAugmenter.cs:434,443,710 · เทียบ BackgroundJobService.cs:157-165 (flip Approved/Sent/PartiallyPaid → `Overdue` เมื่อ DueDate < today) · BankService.MatchCandidates.cs:157 (เส้นหลักใช้ `!= Voided` ถูกต้อง)
- โค้ด: `&& direction.Contains(d.DocumentType) … && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.PartiallyPaid)` · และ `direction = isDeposit ? {Invoice, TaxInvoice, Receipt} : {PurchaseInvoice, PaymentVoucher}`
- ทำไมพัง: (1) ใบแจ้งหนี้ที่ส่งอีเมลแล้ว = `Sent` · ใบที่เลยกำหนด = `Overdue` — สองสถานะนี้คือใบที่ "รอเงินเข้า" มากที่สุด (2) ทั้งสองถูกตัดจากผู้สมัคร ⇒ AI เสนอคู่ผิด/ไม่เสนอ (3) `AdvancedAiAugmenter` AR/AP aging ใช้เงื่อนไขเดียวกัน ⇒ ยอดค้างที่เลยกำหนด (ซึ่งเป็นแก่นของ aging) **ถูกตัดออกจากตาราง aging** (4) ฝั่งถอน `direction` ไม่มี `Expense`/`CertificateInLieu` ⇒ เงินออกจ่ายเจ้าหนี้อื่นไม่มีวันถูกเสนอคู่
- ผลกระทบ: ฟีเจอร์ AI แนะนำ (ไม่ใช่เส้นบัญชีหลัก) จึง P2 — แต่รายงาน aging ที่ AI สรุปให้ผู้บริหารต่ำกว่าจริงเชิงระบบ และ CLAUDE.md #1 ข้อ 6 วัด `UsedAi` จากเส้นนี้
- defect class: "`== Approved` เป๊ะ" + "รายการที่คัดลอกมาด้วยมือ" (ชุด DocumentType ฝั่งซื้อ/ขายควรมาจาก `DocumentSide` — วันนี้มี call site แค่ 5 จุดทั้งเรพ)
- ทางแก้ที่เสนอ: `Status is not (Draft or WaitingApproval or Rejected or Voided or Paid)` และชนิดจาก `DocumentSide.IsSales/IsPurchase`
- ความมั่นใจ: สูง

### A-04 [P2][S] ตัวตรวจก่อนปิดงวด `DOC_NO_JE` ฟ้อง "High" กับใบเสนอราคา/PO/PR/ใบส่งของ/ใบวางบิลที่อนุมัติทุกใบ (ไม่มี JE โดยออกแบบ) — และพลาดใบ Paid/Sent ที่ไม่มี JE จริง
- ไฟล์: Accounting/Controllers/AiSuggestionController.cs:2100-2110 · เทียบ DocumentService.cs:5144-5161 (ชนิดที่ AutoPost ทำงาน: Invoice/TIV/DN/CN/PI/Expense/Receipt/RV/PV/CIL/GRN — **ไม่มี** Quotation/PR/PO/DeliveryNote/BillingNote)
- โค้ด: `.Where(d => … && d.Status == DocumentStatus.Approved && !_db.JournalEntries.Any(j => j.SourceDocumentId == d.Id …))` → `issues.Add(new { severity = "High", code = "DOC_NO_JE", title = $"เอกสารอนุมัติ {docsNoJe} ใบยังไม่ลงบัญชี" })`
- ทำไมพัง: (1) ไม่กรองชนิด ⇒ ทุกใบเสนอราคา/PO ที่ approve ในงวดถูกนับ (2) บริษัทที่ออกใบเสนอราคาเดือนละ 30 ใบเห็น "30 ใบยังไม่ลงบัญชี" ทุกเดือน ⇒ ผู้ใช้เลิกเชื่อตัวตรวจ (3) กลับกัน ใบขายที่ไป Sent/Paid แล้วแต่ AutoPost ล้มเงียบ (SYSTEM_REVIEW §JE เตือน LogWarning เดินต่อ) ไม่ถูกนับเพราะสถานะไม่ใช่ Approved พอดี
- ผลกระทบ: ด่านที่ควรจับ "อนุมัติแล้วไม่มี JE" (บั๊กคลาส IntegrationService ที่เพิ่งแก้) ใช้งานจริงไม่ได้ — เสียง false positive กลบของจริง
- defect class: "`== Approved` เป๊ะ" + "กฎสองข้อที่มองข้อมูลคนละชุดจะเถียงกันเอง" (ชุดชนิดที่ต้องมี JE อยู่ที่ :5144-5161 แต่ตัวตรวจไม่ใช้)
- ทางแก้ที่เสนอ: expose ชุด "ชนิดที่ต้องมี JE" เป็น static บน DocumentService (หรือ Helpers) แล้วให้ตัวตรวจใช้ตัวเดียวกัน · สถานะ = `not in {Draft, WaitingApproval, Rejected, Voided}`
- ความมั่นใจ: สูง

### A-05 [P2][S] อัตราแปลงใบเสนอราคา→ใบแจ้งหนี้ในรายงานผู้บริหาร = "ใบเสนอราคาที่อนุมัติ" ไม่ใช่ "ที่ถูกแปลง"
- ไฟล์: Accounting/Services/Implementations/ExecutiveReportService.Sales.cs:42-44 · เทียบ DocumentService.cs:9326-9700 (ConvertDocumentAsync ไม่เปลี่ยน `src.Status` — awk หา `src.Status =` ใน range = 0 · ใบลูกถือ `SourceDocumentId` Document.cs:723)
- โค้ด: `var convertedQuotes = docs.Count(d => d.DocumentType == DocumentType.Quotation && d.Status == DocumentStatus.Approved); var qiRatio = Pct(convertedQuotes, quotes);`
- ทำไมพัง: approve ใบเสนอราคา = แค่ออกเลข ยังไม่ได้แปลง · แปลงแล้วสถานะต้นทางก็ยังเป็น Approved/Sent ⇒ ตัวเศษคือ "ทุกใบที่ไม่ใช่ร่าง" ⇒ อัตราแปลงเกือบ 100% เสมอ
- ผลกระทบ: KPI ผู้บริหารผิดทิศ (ตัวเลขดูดีเกินจริง)
- defect class: "ค่าที่คงที่ต่อ X กับค่าที่เปลี่ยนทุกใบห้ามอยู่ในถังเดียวกัน" — ใช้สถานะ (lifecycle) แทนความสัมพันธ์ (SourceDocumentId)
- ทางแก้ที่เสนอ: `convertedQuotes = quotes.Count(q => docs.Any(c => c.SourceDocumentId == q.Id && c.Status != Voided))`
- ความมั่นใจ: สูง

### A-06 [P1][M] ขายสดด้วย "ใบเสร็จรับเงิน" standalone (หรือแปลงจากใบเสนอราคา/ใบวางบิล) → ลงรายได้ แต่ **ไม่ตัดสต๊อก ไม่ลง COGS**
- ไฟล์: DocumentService.cs:12490-12510 (stock direction: `Invoice or TaxInvoice => -1 … _ => 0` — Receipt/ReceiptVoucher ตก 0) · :12546 คอมเมนต์ "PV/Receipt/RV ไม่เคยขยับสต๊อกตอนซื้อ/ขาย" · :13684-13712 (Receipt standalone → `Dr เงินสด / Cr รายได้ + VAT`) · COGS perpetual อยู่ **เฉพาะ** สาขา Invoice/TaxInvoice :13039-13062 · ValidConversions :8841-8848 อนุญาต `Quotation → Receipt`, `BillingNote → Receipt` · grep `TrackInventory|IsStockItem|ProductType.Product` ใน DocumentService = 0 (ไม่มีด่านห้ามใส่สินค้าคงคลังลงใบเสร็จ)
- โค้ด: `int direction = doc.DocumentType switch { DocumentType.Invoice or DocumentType.TaxInvoice => -1, … _ => 0 };` กับ `else if (doc.DocumentType == DocumentType.Receipt || … ReceiptVoucher …) { journalType = CashReceipts; … receiptSettlesAr = false → standalone: Dr เงินสด / Cr รายได้ }`
- ทำไมพัง: (1) ร้านค้าที่ขายสดแล้วออก "ใบเสร็จรับเงิน" (ไม่จด VAT หรือลูกค้าไม่ขอใบกำกับ) เลือกแท็บ Receipt ใส่บรรทัดสินค้า (2) approve → JE รายได้ลงครบ (3) แต่ `ApplyStockMovementsAsync` คืน 0 → สต๊อกไม่ลด · COGS ไม่ลง (4) ขณะที่ใบเดียวกันถ้าเลือก `TaxInvoice + IssuedAsCashReceipt` จะตัดสต๊อก + COGS ครบ ⇒ **ชนิดเอกสารที่เลือกบนหน้าจอเปลี่ยนความจริงของสต๊อก/กำไรขั้นต้น** โดยผู้ใช้ไม่รู้
- ผลกระทบ: กำไรขั้นต้นสูงเกินจริง · สินค้าคงเหลือในงบไม่ตรงคลัง · TFRS NPAEs บทที่ 8 (LCNRV) วัดจากยอดที่ผิด · เกี่ยวข้อง SYSTEM_REVIEW "Product.CurrentStock vs WarehouseStock สองความจริง"
- defect class: "'ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้' อันตรายกว่าการไม่ตอบ" (`_ => 0` กลืนเงียบ) + "เลขที่ กับ ชื่อบนกระดาษเป็นคนละแกน — อย่าให้ผู้ใช้เดาความสัมพันธ์เอง"
- ทางแก้ที่เสนอ: Receipt/RV ที่ **standalone** (ไม่มี RelatedDocumentId ที่ตั้งลูกหนี้) และมีบรรทัดสินค้าคงคลัง → เดินสาย -1 + COGS เหมือน TIV cash sale (หรือ block พร้อมบอกให้ใช้ใบกำกับ/ใบแจ้งหนี้) · เทสต์: Quotation→Receipt ที่มี product 2 ชิ้น → stock ลด 2 + JE มี 511xx
- ความมั่นใจ: กลาง-สูง — ต้องเช็คต่อว่าหน้าฟอร์ม Receipt ให้เลือก Product ที่ track stock ได้จริง (ผมไม่พบด่านฝั่ง server; ฝั่งฟอร์มยังไม่ได้ไล่) และ POS ใช้เส้นของตัวเอง (ไม่กระทบ)

### A-07 [P2][S] โมดัลแปลงเอกสารเป็น allow-list แบบ "ซ่อน option ที่มีอยู่" — target ที่ server อนุญาตแต่ **ไม่มี `<option>`** จึงไม่มีวันโชว์: `PO → GoodsReceiptNote` แปลงจากหน้าจอไม่ได้เลย
- ไฟล์: wwwroot/pages/documents.html:1432-1456 (`#convertTarget` static 12 option — ไม่มี GoodsReceiptNote/CreditNote/DebitNote/PurchaseRequisition) · :11239-11262 (`Array.from(select.options).forEach(opt => opt.hidden = !allowed.includes(opt.value) …)`) · :11151-11170 สำเนา offline `_validConversions` (ตรงกับ server 100% วันนี้ แต่เป็นสำเนามือ) · :232-241 `#batchConvertTarget` ไม่มี ReceiptVoucher/GoodsReceiptNote · DocumentService.cs:8879-8895 (`PurchaseOrder → {GoodsReceiptNote, PurchaseInvoice}`, `Invoice → {…, ReceiptVoucher}`) · grep `convertTo('GoodsReceiptNote'|receiveGoods|สร้างใบรับสินค้า` ใน documents.html = 0
- โค้ด: `<select id="convertTarget">` มี Invoice/TaxInvoice/TaxInvoicePaid/Receipt/DeliveryNote/BillingNote/ReceiptVoucher/PurchaseOrder/PurchaseInvoice/Expense/PaymentVoucher/CertificateInLieu เท่านั้น
- ทำไมพัง: (1) server ส่ง `["GoodsReceiptNote","PurchaseInvoice"]` สำหรับ PO (2) JS แค่ซ่อน/โชว์ option ที่**มีอยู่แล้ว** ⇒ GRN ไม่มี option ให้โชว์ (3) ผู้ใช้เห็นแค่ "ใบแจ้งหนี้ซื้อ" ⇒ สาย PO→GRN→PI (3-way match ที่ backend ลงทุนไว้: `SourceLineId` → "billed qty ≤ received qty") เดินจาก PO ไม่ได้ · สร้าง GRN จากแท็บตรง ๆ (:185) ได้แต่ไม่มี SourceLineId ผูก PO ⇒ 3-way match ไม่ทำงาน
- ผลกระทบ: รับของบางส่วน/เทียบยอดบิลกับของที่รับจริง (แก่นของ ERP จัดซื้อ) ใช้ไม่ได้จริง แม้ backend รองรับ · batch convert ใบแจ้งหนี้ → ใบสำคัญรับ ทำไม่ได้
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้ — มีทุกอย่างยกเว้น call site" + "รายการที่คัดลอกมาด้วยมือจากรายการหลัก" (ต่อจาก A-D5 ที่แก้ `_expenseDocTypes` ไปแล้วแต่เหลือจุดนี้)
- ทางแก้ที่เสนอ: สร้าง `<option>` จาก `allowed` ที่ server คืน (label ผ่าน `Layout.docTypeLabel`) แทน static list — สำเนา `_validConversions` เหลือไว้เป็น fallback หรือลบ
- ความมั่นใจ: สูง (GRN) · CN/DN ผ่านโมดัลนี้ไม่ได้เช่นกัน แต่ฟอร์มมี "เหตุผลใบลดหนี้/ใบเพิ่มหนี้" (:543,:574) ให้สร้างตรงพร้อมอ้างใบเดิม จึงถือว่ามีทางอื่น

### A-08 [P2][S] กฎอนุมัติ (sme-config) ให้เลือกประเภทเอกสารได้แค่ 5 จาก 16 และเป็นชื่ออังกฤษ — ตั้งกฎ "PO เกิน X ต้องผ่าน CFO" ไม่ได้ ทั้งที่ backend รับทุกชนิด
- ไฟล์: wwwroot/pages/sme-config.html:73-79 · ApprovalService.cs:66,109 (`rule.DocumentType = request.DocumentType` nullable enum) · :152-165 (resolve `entityDocType` จาก Document ทุกชนิด) · :494-505 (`ScoreRule`: `if (r.DocumentType != entityDocType) return -1`)
- โค้ด: `<option value="PurchaseInvoice">PurchaseInvoice</option> … <option value="TaxInvoice">TaxInvoice</option>` (5 ตัว) 
- ทำไมพัง: PurchaseOrder/PurchaseRequisition/Quotation/CreditNote — เอกสารที่องค์กรตั้งสายอนุมัติบ่อยที่สุด — ไม่มีให้เลือก ⇒ ต้องใช้ "ทุกประเภท" ซึ่งไปครอบใบเสร็จ/ใบส่งของด้วย
- ผลกระทบ: workflow อนุมัติแบบ ERP ตั้งค่าไม่ได้ตามจริง · label อังกฤษล้วนขัดกับกฎ "UI string ใช้ไทย"
- defect class: "รายการที่คัดลอกมาด้วยมือ" (ควรสร้างจาก `Layout.docTypeLabel` keys)
- ความมั่นใจ: สูง

### A-09 [P3][S] risk.html เสนอ `JournalEntry` เป็น "ประเภทเอกสาร" แต่ endpoint bind เป็น `DocumentType` enum → เลือกแล้วได้ 400 ทุกครั้ง
- ไฟล์: wwwroot/pages/risk.html:97-104 (`<option value="JournalEntry">`) · Controllers/RiskController.cs:57-61 (`[FromQuery] DocumentType docType`)
- ทำไมพัง: `"JournalEntry"` ไม่ใช่ค่าใน enum ⇒ model binding ล้ม ⇒ `[ApiController]` ตอบ 400 ก่อนถึง action ⇒ ปุ่ม "แนะนำสายอนุมัติ" กับ JE ไม่เคยทำงาน
- defect class: "UI ที่โชว์ปุ่มซึ่งจะ 4xx แน่ ๆ = silent no-op อีกทรง"
- ความมั่นใจ: สูง (ไม่ได้รัน แต่ default enum binding ของ ASP.NET Core ไม่รับชื่อที่ไม่มี)

### A-10 [P3][M] ชื่อไทยของประเภทเอกสารมี **แผนที่มือ 8 ชุดฝั่ง C# + 3 ชุดฝั่ง JS + option ตายตัวอีก ~12 select** และ drift แล้วจริง
- ไฟล์/ค่าที่ต่าง (เทียบกับ `PdfGenerationService.GetDocumentTitle` :~1240 ซึ่งพิมพ์บนกระดาษ = ground truth):
  - `CertificateInLieu`: กระดาษ/layout.js:2590/LineBot:717/portal DOC_TYPE = "ใบรับรองแทนใบเสร็จ**รับเงิน**" · ExecutiveReportService.Sales.cs:104 + option ใน documents.html(10 จุด)/email-schedule:108/document-scan(2)/admin-ocr/ocr-config/document-templates/expense.html = "ใบรับรองแทนใบเสร็จ"
  - `Receipt`: กระดาษ "ใบเสร็จรับเงิน" · ExecutiveReportService:94, CustomerStatementController:191 (**เอกสารส่งลูกค้า**), portal.html:303 (`<option>` ขัดกับ DOC_TYPE ในไฟล์เดียวกัน :573), document-scan:2116 = "ใบเสร็จ"
  - `ReceiptVoucher`: LineBotService:721 "ใบสำคัญรับ**เงิน**" vs ที่อื่น "ใบสำคัญรับ"
  - `Expense`: LineBot:715 "บันทึกค่าใช้จ่าย" · CustomerStatement:194 + recurring.html:50 "ค่าใช้จ่าย" vs "ใบบันทึกค่าใช้จ่าย"
  - `DocumentTemplateService.CreateDefaultTemplate` :265-292 ไม่มี GoodsReceiptNote/CertificateInLieu → เทมเพลตเริ่มต้นชื่อ "เอกสาร" · CustomerStatementController map ไม่มี Quotation/DeliveryNote/PR/PO/GRN/CIL → `_ => t.ToString()` อังกฤษ (วันนี้ statement เลือกชนิดเฉพาะ :56-61 จึงยังไม่โผล่)
- ที่มีอยู่แล้ว: `Layout.docTypeLabel` (layout.js:2582) ครบ 16 และตรงกระดาษ — ใช้แค่ 7 หน้า · documents.html:4852 มี `_docTypeLabel` ตัวที่สาม (อ่านจาก `<option>` text แล้ว split '(') · portal.html มีสำเนาที่ **ต้องมี** (ไม่โหลด layout.js — หมายเหตุ :568) แต่ต้องตรงกัน
- ผลกระทบ: ผู้ใช้เห็นชื่อชนิดคนละแบบระหว่างตาราง/LINE/statement/กระดาษ · ชื่อบน statement ที่ส่งลูกค้าไม่ตรงหัวใบ
- defect class: "รายการที่คัดลอกมาด้วยมือจากรายการหลัก = drift แน่นอน แค่รอเวลา" · "สำเนาที่ตามหลังอยู่ไม่กี่ธง อันตรายกว่าสำเนาที่ผิดชัด"
- ทางแก้ที่เสนอ: `Helpers/DocumentTypeNames.Th(type)/En(type)` ตัวเดียว (ย้าย GetDocumentTitle มา) ให้ 8 จุด C# เรียก · expose ผ่าน endpoint หรือฝังใน layout.js เป็นแหล่งเดียว · หน้าที่มี `<select>` สร้าง option จาก `Layout.docTypeLabel` แบบเดียวกับที่ทำกับ `MENU_SECTIONS` · เพิ่ม checker "สตริงไทยของ enum ที่ต่างจากแหล่งกลาง"
- ความมั่นใจ: สูง (grep ครบ)

### A-11 [P3][S] DocumentStatus: ตัวกรอง/ป้ายไม่ครบ 9 ค่าในหลายจุด
- documents.html:213-221 `#statusFilter` ไม่มี `WaitingApproval`/`Rejected` — ผู้อนุมัติกรอง "รออนุมัติ" จากหน้าเอกสารไม่ได้ (approval.html ไม่มีคำ WaitingApproval เลย = ใช้คนละแกน) · purchases.html:70-76 ไม่มี `WaitingApproval`/`Overdue`/`Rejected` — ใบซื้อเกินกำหนดจ่ายกรองไม่ได้
- documents.html:7616 `stLabel` ใน chain stepper ไม่มี `Overdue` → โชว์ "Overdue" อังกฤษ สีเทา (ขณะที่ `Layout.statusBadge` :2477 + translations.js:101-102 ครบ 9)
- api-developer.html:534 เอกสาร API บอกสถานะมี 5 ค่า (`"Draft" | "Approved" | "Paid" | "PartiallyPaid" | "Voided"`) จาก 9 → คู่ค้าเขียน switch ไม่ครอบ Sent/Overdue/WaitingApproval/Rejected
- defect class: สำเนามือ / doc drift
- ทางแก้: ตัวกรองสถานะสร้างจาก `translations.status.*` keys ชุดเดียว · stepper ใช้ `Layout.statusBadge`
- ความมั่นใจ: สูง

### A-12 [P3][S] portal.html `#docFilter` มีแค่ 6 ชนิด — ลูกค้ากรอง "ใบส่งของ/ใบวางบิล/ใบสำคัญรับ" ที่ backend ส่งมาให้จริงไม่ได้ (ต่อจาก A-02: หลังกรองสถานะ/ฝั่งขายแล้ว ตัวกรองควรสร้างจากชุด AR เดียวกับ CustomerStatementController:58,61)
- ไฟล์: wwwroot/portal.html:299-307 · PortalService.cs:203-204 (รับทุกชนิดที่ parse ได้)
- ความมั่นใจ: สูง · ขนาด S

### A-13 [P3][S] ชุด "ชนิดฝั่งซื้อ/ฝั่งขาย" ยังถูกเขียนมืออีกอย่างน้อย 9 ที่ นอก `Helpers/DocumentSide` (ซึ่งมี call site แค่ 5 จุด)
- C#: TaxService.cs:1520-1521 และ :1595-1596 (`purchaseSide = {PI, Expense, PV, CIL}` ×2) · :2953-2963 `PullableVatTypes` (ตั้งใจตัด CN/DN — มีเหตุผลเขียนไว้) · EmailScheduleService.cs:263-264 `arTypes` (ไม่มี ReceiptVoucher ⇒ ทวงหนี้/แจ้งเตือนไม่นับ RV) · BankAiAugmenter.cs:54-56 (A-03) · CustomerStatementController.cs:56-61 · AdvancedAiAugmenter.cs:431-441 (AP aging = PurchaseInvoice อย่างเดียว ไม่นับ Expense/CIL ⇒ เจ้าหนี้อื่นหายจาก aging)
- JS (documents.html ไฟล์เดียว 4 ชุดไม่เท่ากัน): :2264 `buySide` 4 ชนิด (ไม่มี PR/PO/CIL) · :4092-4093 `INBOUND` 5 ชนิด (ไม่มี PR/PO) · :4332-4333 / :5647-5649 / :6529 / :7729 `expenseTypes` 7 ชนิด (บางชุดรวม CN/DN บางชุดไม่)
- ผลกระทบ: แต่ละจุดถูกในบริบทตัวเอง "วันนี้" แต่เพิ่มชนิดใหม่ (เช่น GRN เมื่อวาน) ต้องไล่ 13 จุด — ตัวที่พลาดเงียบ (A-D5 คือตัวอย่างที่เพิ่งเกิด)
- ทางแก้: expose `DocumentSide` เป็น endpoint/ฝังใน layout.js (`Layout.docSide(type)`) และให้ทุกลิสต์ C# derive จาก `DocumentSide` + filter เพิ่มตามบริบท
- ความมั่นใจ: สูงว่าเป็นสำเนา · ผลกระทบจริงวันนี้ = เฉพาะที่ระบุ (RV ใน arTypes, Expense/CIL ใน AP aging) ความมั่นใจกลาง

## ตรวจแล้วไม่ใช่บั๊ก (เพื่อทีมอื่นไม่เสียเวลาซ้ำ)
- **prefix เลขที่ 16/16 ตรงกัน**: `DocumentNumberGenerator.GetPrefix` (Helpers/DocumentNumberGenerator.cs:22-41) ↔ `settings.html:2259-2265 NS_DEFAULT_PREFIX` เทียบทีละตัวเท่ากันทุกตัว (QT/INV/REC/TIV/DN/CN/DLV/BN/RV/PR/PO/GR/PI/EXP/PV/CIL) · `_ => "DOC"` unreachable · NumberSeries override เฉพาะ prefix ผ่าน `ResolvePrefixAsync` ที่ `NextAsync` เรียกเสมอ · ไม่พบ literal prefix ที่ออกเลขเองนอก generator (ที่เจอเป็น sample/ExpenseClaim ซึ่งคนละตาราง)
- **หัวเอกสาร**: `GetDocumentTitle` ครบ 16 ทั้ง th/en; `ComputeDocumentTitle` เป็นตัวเดียวและ JS `docHeaderLabel` ใช้ `documentTitle` จาก server ก่อน (แก้ไปแล้วรอบก่อน)
- **e-Tax**: gate ที่ EtaxInvoiceService.cs:100-102 รับแค่ TIV/Receipt/DN/CN ตรง ETDA; `EtaxDocumentTypeMap._ => "388"` unreachable; สาขา `ReceiptVoucher` ที่ :128 เป็น dead branch (gate กันก่อน) ไม่อันตราย
- **pseudo-type**: `Deposit` (documents.html:8312 → Receipt+IsDeposit · document-scan:3980 · OcrService:4486-4497) · `TaxInvoiceCombined` (recurring.html:454 → TaxInvoice+flag) · `TaxInvoicePaid` — ทุกตัวถูก map ก่อนส่ง server ไม่ใช่ค่า enum จริง ⇒ "extra" ใน options.md ไม่ใช่บั๊ก
- **missing ที่ตั้งใจ**: `batchConvertTarget` ไม่มี Quotation/PR (ไม่มีอะไรแปลงไปหา) · CN/DN ไม่อยู่ใน convert (ต้องกรอกเหตุผล §86/9-10 ผ่านฟอร์ม) · admin-ocr/ocr-config `trainDocType` เฉพาะฝั่งซื้อ (สอน OCR บิลรับเข้า) · `document-scan #revDocType` มี WHT/Other เพราะ `OcrScan.DocumentType` เป็น string (Intelligence.cs:120) ไม่ใช่ enum
- **สต๊อก**: DeliveryNote ไม่ตัดสต๊อก (คอมเมนต์ :12485-12489 ตั้งใจ — Invoice เป็นจุดรับรู้) · PI ที่อ้าง GRN ไม่ตัดซ้ำ (:12577) · CN/DN ขยับตามเหตุผลเท่านั้น — ถูกต้อง
- **DocumentSide**: `OcrScanComplianceEvaluator.IsPurchaseSide` (:32-41) delegate ไป `DocumentSide` แล้ว · document-scan.html salesTypes ถูกยุบแล้ว (:3976)
- `== DocumentStatus.Approved` ที่ **ถูกต้องตามบริบท**: SignatureApprovalService:330 (กันลูกค้าเซ็นซ้ำใบที่อนุมัติแล้ว — จากนั้นเรียก `ApproveDocumentAsync` เต็ม :393) · DocumentService:4099/4350 (Approved→PartiallyPaid transition) · :15265 (Done ทั้งสองทาง) · :12702 PO commitments Approved||Sent (PO ไม่ไป Paid) · BankService.Reconciliation:844 — Receipt/PV/RV ที่ standalone ยังเป็น Approved จริง (Paid เฉพาะที่อ้างใบตั้งหนี้ :5183) ครอบเคสหลัก แต่ **ควรเช็คต่อ**ว่าใบ settle (Paid) ต้องเข้าคู่ธนาคารด้วยไหม — ผมไม่ยกเป็น finding เพราะยังไม่แน่ใจเจตนา · BackgroundJobService:159-175 / AccountantWorkspace:58 / VendorConsolidatedPayment:47 / JournalAnomaly:119 / DocumentCompleteness:85 ใช้ชุด Approved/Sent/PartiallyPaid/Overdue ครบ

## ซ้ำกับ SYSTEM_REVIEW (ID เดิม + ยังเปิดอยู่จริงไหม)
- A-D5 ✅ (GRN หายจากฝั่งจ่าย) — ปิดแล้วที่ 48186d1 แต่ **A-07 คือส่วนที่เหลือของคลาสเดียวกัน** (โมดัลแปลงยังไม่มี GRN)
- D8 (renderer สำรอง client ใช้ `docTypeLabel`) — ยังเปิด; A-10 เกี่ยวข้องแต่คนละจุด
- §10 "Portal/signatures อ่านตื้น" — A-02 คือสิ่งที่พบเมื่ออ่านลึก · ไม่พบ ID เดิมเรื่อง portal status
- ไม่พบ ID เดิมสำหรับ A-01/A-03/A-04/A-05/A-06/A-08/A-09/A-11/A-13

## ยังไม่ได้อ่าน
- ฟอร์ม Receipt ใน documents.html ว่าเลือกสินค้าคงคลังได้จริงไหม (A-06 ขั้นสุดท้าย) · POS/lodging ออก Receipt ผ่าน `IDocumentService` ด้วยเส้นไหน (กระทบ A-06 ไหม)
- `Section65TerValidator` ว่ารับ `priorEntertainment` เป็น YTD ตรง ๆ หรือมี re-query (A-01)
- ใครสร้าง PortalAccess ให้ vendor ได้จริง (vendor-portal.html แยกหรือใช้ portal.html เดียวกัน) — กำหนดความรุนแรงส่วน "ใบภายในรั่ว" ของ A-02
- purchases.html / recurring / email-schedule เชิง runtime · `EmailScheduleService` rule ฝั่ง AP (rDocType มี PI/Expense/PV แต่ arTypes ไม่มี) · LineBotService flow เลือกชนิด
- AutoPostToJournalAsync สาขา PV (:14203-14372) และ GRN (:14373+) ทีละบรรทัด (SYSTEM_REVIEW ก็ยังไม่ได้อ่าน)

## ข้อเสนอเชิง ERP (แยกจากบั๊ก)
1. **Registry ประเภทเอกสารตัวเดียว** (`DocumentTypeRegistry`: enum → prefix · ชื่อ th/en · side · ต้องมี JE ไหม · ขยับสต๊อกทิศไหน · e-Tax code · ชนิดที่แปลงไปได้ · สถานะที่ถือว่า "issued") แล้วให้ทุกตาราง/select/switch derive จากมัน — วันนี้ความรู้ชุดเดียวกระจายอยู่ ≥ 25 จุด (ตาราง 16×9 ที่ผมไล่: ครอบครบเฉพาะ prefix/title/e-Tax gate; ส่วนอื่นเป็น if-chain + default กลืน)
2. **สถานะเอกสารต้องมี "issued predicate" กลาง** (`DocumentStatusRules.IsIssued/IsOpen/IsSettled`) — `== Approved` 26 จุดในเรพ แต่ละจุดเดาเอง (A-01/A-03/A-04/A-05 คือ 4 จุดที่เดาผิด)
3. **Sales Order / Purchase chain ที่มองเห็น**: ValidConversions มี PR→PO→GRN→PI→PV ครบ แต่ UI ไม่มี "เอกสารลูก/สถานะการรับของ/ยอดค้างรับ" ต่อ PO — 3-way match ที่มีใน backend ต้องมีหน้าจอ (A-07)
4. Portal ลูกค้า/ผู้ขายควรแยก view ตาม `DocumentSide` + สถานะ (A-02) และรองรับ Statement/aging เดียวกับ CustomerStatementController
5. ชนิดที่ ERP มักต้องมีแต่ยังไม่มีใน enum: Sales Order (ยืนยันคำสั่งซื้อจากลูกค้า — วันนี้ใช้ Quotation แทน) · Return (ใบรับคืนสินค้าแยกจาก CN) · Stock Transfer/Adjustment เป็นเอกสารมีเลข — ควรตัดสินก่อนขยาย ไม่ใช่ยัดเข้า flag บน type เดิม (บทเรียน "ห้าม reuse field ผิดความหมาย")
