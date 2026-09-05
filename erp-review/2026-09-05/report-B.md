# ทีม B — ความต่อเนื่อง/ครบถ้วนของข้อมูลตลอดวงจรเอกสาร (data continuity & integrity)
(รอบ 2 · เขียน append ระหว่างตรวจ · HEAD 444d2cb)

## สรุป 5 บรรทัด
1. **P0 B-02**: อนุมัติผ่านมือถือตั้ง `Status=Approved` ตรง ๆ ข้าม ApproveDocumentAsync ⇒ ไม่มีเลข/JE/สต็อก/§86/4 แต่รับเงินได้ — DOCUMENT_FLOW บอกว่ามี 3 ทางและ "แก้แล้ว" ทั้งที่ทางที่ 4 ยังเปิด
2. **P1 การสืบทอด**: convert/clone/recurring ไม่ส่ง PaymentTerms/CreditDays/DimensionId (+ clone ไม่ส่ง BillDiscount) ⇒ ใบลูกได้ default ของคู่ค้า ยอด/เงื่อนไขไม่ตรงใบแม่ — ขัดกับ SYSTEM_REVIEW U6
3. **P1 round-trip**: แก้ไขแล้วล้างช่องข้อความ 13 ช่องไม่มีผล (client ส่ง null = คงเดิม) · Rejected เป็นทางตันจริง (แก้ได้แต่อนุมัติ/ส่งใหม่ไม่ได้)
4. **P1/P2 เงิน**: JE วงจรมัดจำ (realize/refund/apply) ไม่คูณ ExchangeRate ต่างจาก AutoPost · เส้นที่ออกเลขเองไม่ตรึงสาขา/tax point/retention และ null retention = purge ได้โดยไม่มีด่าน · void ข้ามสินทรัพย์ที่มีค่าเสื่อมแบบเงียบ
5. **ที่ผ่าน**: PaidAmount/BalanceDue recompute ครบทุกจุด · CN cap บังคับที่ approve ครอบทุกทางเข้า · Void cascade ครบ 12 ผลข้างเคียง · Approve transaction ครอบเลข→JE ไม่เกิด gap — แต่ threshold "จ่ายครบ" มี 3 ค่า และ Overdue ติดถาวร

## Findings (เรียง P0→P3 — จัดเรียงตอนสรุป)

### B-01 [P1][S] แก้ไขเอกสารแล้ว "ล้างช่องข้อความ" ไม่มีผล — payload ส่ง null (=คงเดิม) แทน "" (=ล้าง) ใน 13 ช่อง
- ไฟล์: Accounting/wwwroot/pages/documents.html:8367-8372, 8363(bookingNumber), 8380-8383(certificate*), 8443(paymentTerms) · Accounting/Services/Implementations/DocumentService.cs:2123-2127, 2130, 2134, 2147, 2159, 2297-2300 (ใน UpdateDocumentAsync)
- โค้ด (client): `notes: document.getElementById('fNotes').value || null,` · `reference: (…fCnExternalRef…).trim() || document.getElementById('fRef').value || null,` · `customAppendix: … || null` · `customFooterNotes: … || null` · `customTermsAndConditions: … || null` · `bookingNumber: …trim() || null` · `paymentTerms: …trim() || null` · `certificateReason/certifierName/certifierPosition/witnessName/witnessPosition: … || null`
- โค้ด (server): `if (request.Reference != null) doc.Reference = request.Reference;` · `if (request.Notes != null) doc.Notes = request.Notes;` · `if (request.CustomAppendix != null) …` · `if (request.PaymentTerms != null) doc.PaymentTerms = request.PaymentTerms;` · `if (request.BookingNumber != null) doc.BookingNumber = …` · `if (request.CertificateReason != null) …`
- ทำไมพัง: (1) ผู้ใช้เปิดแก้ใบร่าง ลบข้อความใน "หมายเหตุ"/"อ้างอิง"/"เงื่อนไขการชำระ"/"ภาคผนวก"/"เลขจอง" ทิ้ง → กดบันทึก (2) ตัวสร้าง payload ใช้ `|| null` ทั้งโหมดสร้างและแก้ไข ⇒ ค่าว่างถูกแปลงเป็น `null` (3) ฝั่ง server ใช้กติกา null = "ไม่แตะ" ⇒ ค่าเดิมคงอยู่ (4) ตอบ 200 + toast สำเร็จ แต่เปิดใบอีกครั้ง ข้อความเดิมกลับมา — ไม่มี error ไม่มีคำอธิบาย. เทียบกับช่องที่ทำถูกในไฟล์เดียวกัน: `_gsel()` (บรรทัด 8278-8282) ส่ง `CLEAR_GUID` เมื่อแก้ไขและช่องว่าง — คือรู้กติกานี้อยู่แล้วแต่ทำเฉพาะช่อง GUID
- ผลกระทบ: `Notes`/`PaymentTerms`/`CustomFooterNotes`/`CustomTermsAndConditions`/`CustomAppendix` **พิมพ์ลงกระดาษ** (PdfGenerationService อ่าน) ⇒ ข้อความที่ผู้ใช้ตั้งใจลบไปโผล่ในใบกำกับที่ส่งลูกค้า · `Reference` ใช้เป็นเลขอ้างอิงมัดจำ/ใบต้นทาง (บรรทัด 8500-8503 เขียน depRef ลง reference) ⇒ ล้างไม่ได้
- defect class (CLAUDE.md): กฎเหล็ก #4 A "ห้าม silent no-op" + เช็กลิสต์ B "payload (สร้าง: ว่าง=null · แก้ไข: ""=ล้างค่า)" — ละเมิดตรงตัว
- ทางแก้ที่เสนอ: helper เดียว `_txt(id)` = `this.editingId ? (value ?? '') : (value || null)` ใช้กับทุกช่องข้อความในตัวสร้าง payload (ทำแบบเดียวกับ `_gsel`)
- ความมั่นใจ: สูง (เห็นทั้งสองฝั่งของสาย)
- หมายเหตุ: `inputVatAccountCodeOverride` / `depositDeferredAccountCode` / `supplierInvoiceNumber` server แปลง whitespace→null แล้ว แต่ client ยังส่ง null เมื่อว่างเช่นกัน — ต้องเช็คบรรทัด 8391-8430 ว่าส่ง "" หรือ null (ยังไม่ได้เปิด)

### B-02 [P0][S] อนุมัติผ่านมือถือ (`QuickApproveAsync`) ตั้ง `Status = Approved` ตรง ๆ — ไม่ผ่าน `ApproveDocumentAsync` ⇒ ไม่มีเลขเอกสาร · ไม่มี JE · ไม่ตัดสต็อก · ไม่มี §86/4 gate
- ไฟล์: Accounting/Services/Implementations/MobileApiService.cs:388-396 (approve) · :410-416 (reject) · Accounting/Controllers/MobileController.cs:42-45 (`POST quick-approve`)
- โค้ด: `var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == entityId && !d.IsDeleted); if (document is not null) { document.Status = DocumentStatus.Approved; document.UpdatedAt = DateTime.UtcNow; … }`
- ทำไมพัง: (1) ผู้อนุมัติกดปุ่มในแอปมือถือ → `QuickApproveAsync` → ขั้นสุดท้ายของ workflow (2) แทนที่จะเรียก `IDocumentService.ApproveDocumentAsync` (ซึ่ง ApprovalService.cs:432 และ SignatureApprovalService.cs:397/469 ทำถูก) โค้ดเขียนสถานะลง entity ตรง ๆ (3) ⇒ เอกสาร `Status=Approved` แต่ `DocumentNumber` ยังเป็น `DRAFT-{guid}` · ไม่มี JournalEntry · ไม่ `ApplyStockMovementsAsync` · ไม่ตรึง `IssuerBranchCode` · ไม่ผ่าน §86/4/§82/5/งวดปิด · ไม่มี TaxReportLine (4) หลังจากนั้นทุกทางเข้าถือว่า "อนุมัติแล้ว": รับชำระเงินได้ (CreatePaymentAsync รับ Approved) → JE รับเงินอ้างถึงลูกหนี้ที่ไม่เคยถูกตั้ง · ภ.พ.30 ไม่มีใบนี้ · PDF พิมพ์เลข DRAFT-…
- ผลกระทบ: เงิน/ภาษี — GL กับสถานะเอกสารเล่าคนละเรื่องถาวร; ใบกำกับภาษี "อนุมัติแล้ว" ไม่มีเลขรัน (ผิด §86/4 gap-free) และไม่เข้ารายงานภาษีขาย §87
- defect class (CLAUDE.md): "โมดูลใหม่ที่มีเงินห้ามออกเอกสาร/เลขเอง — เดินผ่าน IDocumentService เสมอ" · "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" (ApproveDocumentAsync มี gate ครบ แต่ทางเข้านี้ข้ามทั้งหมด)
- ทางแก้ที่เสนอ: แทนสองบรรทัดด้วย `await _docService.ApproveDocumentAsync(companyId, entityId, actor, acknowledgeWarnings: true)` เหมือน ApprovalService.cs:432 · ฝั่ง reject ให้เดินทางเดียวกับ ApprovalService.cs:346-348 (bounce กลับ Draft) — ดู B-03
- ความมั่นใจ: สูง (อ่านทั้งเมธอด 360-420 แล้ว ไม่มีการเรียก docSvc ที่ไหนเลย; `grep ApproveDocumentAsync MobileApiService.cs` = 0)
- ไม่พบใน SYSTEM_REVIEW_2026-09.md (grep MobileApi = 0)

### B-03 [P1][S] เอกสาร `Rejected` เป็นทางตันจริง: แก้ไขได้ แต่ **อนุมัติ/ส่งขออนุมัติใหม่ไม่ได้** เพราะไม่มีใครรีเซ็ตกลับ Draft
- ไฟล์: DocumentService.cs:4475-4476 (ApproveDocumentAsync guard) · :2006 (UpdateDocumentAsync อนุญาต Rejected แต่ไม่เขียน Status) · ApprovalService.cs:253-255 (submit flip เฉพาะ `== Draft`) · SignatureApprovalService.cs:294 + MobileApiService.cs:415 (ผู้ตั้ง Rejected) · documents.html:9300-9310 (UI บอกว่า "แก้ตามเหตุผลที่ถูกตีกลับ แล้วส่งขออนุมัติใหม่")
- โค้ด: `if (doc.Status != DocumentStatus.Draft && doc.Status != DocumentStatus.WaitingApproval) throw new InvalidOperationException("อนุมัติได้เฉพาะเอกสาร Draft หรือ WaitingApproval เท่านั้น");` · `if (doc.Status is not (DocumentStatus.Draft or DocumentStatus.Rejected) && !isDocRevision) throw …` · `if (doc != null && doc.Status == DocumentStatus.Draft) doc.Status = DocumentStatus.WaitingApproval;` · `grep -n "Status = " ในช่วง UpdateDocumentAsync (1988-2487)` = 0 บรรทัด
- ทำไมพัง: (1) ใบถูกตีกลับผ่าน SignatureApproval หรือมือถือ → `Rejected` (2) UI โชว์ปุ่ม "แก้ไข" และ "ส่งขออนุมัติ" (Rejected อยู่ในลิสต์ 9310) (3) แก้ไขสำเร็จ แต่ Status ยัง Rejected (4) กด "อนุมัติ" → 400 "อนุมัติได้เฉพาะ Draft/WaitingApproval" · กด "ส่งขออนุมัติ" → ApprovalRequest ถูกสร้าง แต่ doc.Status ไม่ flip (เงื่อนไข `== Draft`) → ผู้อนุมัติกดอนุมัติ → ApprovalService.cs:432 เรียก ApproveDocumentAsync → **throw เดิม** ⇒ workflow ค้าง. ทางออกเดียวคือคัดลอกเป็นใบใหม่ (ApprovalService.cs:346 bounce เป็น Draft เฉพาะเส้นของตัวเอง — สองเส้นอื่นไม่ทำ)
- ผลกระทบ: ฟีเจอร์ตีกลับ-แก้-ส่งใหม่ ใช้ไม่ได้จริงกับใบที่ตีกลับผ่าน 2 ใน 3 ทาง; ผู้ใช้เห็นปุ่มที่กดแล้วพัง
- defect class: "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้ = ฟีเจอร์ที่ยังไม่จบ" · "UI ที่โชว์ปุ่มซึ่งจะ 4xx แน่ ๆ = silent no-op อีกทรง"
- ทางแก้ที่เสนอ: (ก) UpdateDocumentAsync: ถ้า `doc.Status == Rejected` หลังบันทึกสำเร็จ → `Draft` (บันทึก audit) (ข) หรือให้ Approve/Submit รับ Rejected ด้วย — เลือกทางเดียวแล้วใช้ทั้ง 3 ผู้ตั้ง Rejected
- ความมั่นใจ: สูง

### B-04 [P3][S] `DocumentStatus.Sent` ไม่มีใครเขียนเลยทั้งเรพ — สถานะ "ส่งแล้ว" เป็นสถานะตาย แต่ด่านชำระเงิน/Overdue/UI ยังอ้างถึง
- ไฟล์: grep `Status = [^;]*DocumentStatus\.Sent` ทั้ง Accounting/**/*.cs = 0 · ผู้อ่าน: DocumentService.cs:10515, 10920 (payment gate), BackgroundJobService.cs:173 (overdue scan), UpdateDocumentAsync:2001
- ทำไมพัง: อีเมล/LINE ส่งเอกสารแล้วบันทึกลง `DocumentEmailLog` แต่ไม่เคย flip เอกสารเป็น Sent ⇒ รายงาน/ตัวกรอง "ส่งแล้ว" ว่างตลอด; ไม่ใช่บั๊กเงิน แต่เป็นสถานะที่ทีม I ควรตัด/ต่อสาย (ตัดสินใจโดยเจ้าของ)
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" · ต้องเลือก "ต่อสาย หรือ ลบ"
- ความมั่นใจ: สูงว่าไม่มีใครเขียน · กลางว่าเป็นปัญหา (อาจตั้งใจให้ Sent เป็น legacy)

### B-05 [P2][S] Void ใบซื้อที่ขึ้นทะเบียนสินทรัพย์แล้ว: ถ้าสินทรัพย์ยืนยันแล้ว/มีค่าเสื่อม posted → `LogWarning` + `continue` แล้ว void สำเร็จเงียบ ๆ — สินทรัพย์ค้างอยู่ในทะเบียนโดยไม่มีใบซื้อ
- ไฟล์: DocumentService.cs:7174-7187 (ใน VoidDocumentAsync ขั้น 7-asset)
- โค้ด: `_logger.LogWarning("Doc {Doc} void: asset {Code} ยืนยันแล้ว — ไม่ลบ ผู้ใช้ต้อง dispose/write-off ด้วยตนเอง", …); continue;` และ `_logger.LogWarning("… มีค่าเสื่อม posted — ไม่ลบ", …); continue;`
- ทำไมพัง: (1) ใบซื้อทรัพย์สิน approve → AutoRegister FixedAsset (2) ผ่านไปเดือนหนึ่ง ค่าเสื่อม posted (3) ผู้ใช้ void ใบซื้อ (เช่นออกใบใหม่แทน) → JE ซื้อถูกกลับ · สต็อกกลับ · แต่สินทรัพย์ + ค่าเสื่อมสะสมยังอยู่ (4) ผลตอบกลับ 200 ไม่มีข้อความบอกว่า "ทรัพย์สิน X ยังค้าง ต้อง dispose เอง" — เหตุผลอยู่ในไฟล์ log ที่ไม่มีใครเปิด (5) งบ: ค่าเสื่อมและสินทรัพย์ในงบดุลอิงใบซื้อที่ไม่มีอยู่แล้ว
- ผลกระทบ: งบดุล/ค่าเสื่อม (TFRS NPAEs บทที่ 10) ค้างจากเอกสารที่ถูกยกเลิก โดยไม่มีสัญญาณถึงผู้ใช้
- defect class: "`LogWarning` แล้วเดินต่อ = กลืน error — ดังพอต้องดังในที่ที่คนดู (3 ที่: ข้อมูล · สถานะงาน · คำตอบ)"
- ทางแก้ที่เสนอ: ต่อข้อความเข้า `doc.InternalNotes` + คืน warning ใน response ของ void endpoint (หรือบล็อกพร้อมทางไปต่อ "dispose ก่อน") — เลือกอย่างตั้งใจ แต่ห้ามเงียบ
- ความมั่นใจ: สูง (quote ตรง) · ควรเช็คต่อว่า `VoidDocumentAsync` คืน `Task` (ไม่มี payload) ⇒ ต้องเพิ่มช่องส่งคำเตือนกลับ

### B-06 [P1][S] Convert (ทุกคู่ผ่าน `ConvertCoreAsync`) ไม่สืบทอด `PaymentTerms`/`CreditDays`/`DimensionId`/`IsForeignService`/`PaymentType`/tax-point dates — ใบลูกได้ค่า **default ของคู่ค้า** แทนค่าที่ตกลงบนใบแม่
- ไฟล์: DocumentService.cs:9255-9300 (`ConvertCoreAsync` → `new CreateDocumentRequest(…)`) · :1186-1198 (CreateDocumentAsync fallback จาก Contact) · :1141-1146 (PaymentType default)
- โค้ด: request ที่ส่งมีเฉพาะ `ProjectId, BankAccountId, PaymentAccountId, ExpenseCategoryId, Currency, ExchangeRate, PricesIncludeVat, BrandId, BranchId, DocumentTemplateId, BillDiscountPercent/Amount, RelatedDocumentId` + หลังสร้างเซ็ตเพิ่ม `DocumentLanguage, CustomAppendix/FooterNotes/Terms, RevenueContractId, PerformanceObligationId, Certificate*/Witness*, PaymentDate` — **ไม่มี** `PaymentTerms`, `CreditDays`, `DimensionId`, `IsForeignService`, `PaymentType`, `DeliveryDate/OwnershipTransferDate/ServiceUsedDate`, `BookingNumber`, `SupplierInvoiceNumber/SupplierTaxInvoiceDate/SupplierBranchCode/HasTaxInvoiceReference/InputVatAccountCodeOverride`, `Sensitivity`
  ฝั่ง Create: `if ((!doc.CreditDays.HasValue || doc.DocumentLanguage == null) && doc.ContactId != Guid.Empty) { … if (partner?.PaymentDueDays is > 0) doc.CreditDays = partner.PaymentDueDays; if (string.IsNullOrWhiteSpace(doc.PaymentTerms) && …) doc.PaymentTerms = partner!.PaymentTerms; }`
- ทำไมพัง: (1) ใบเสนอราคาตกลงเทอม "มัดจำ 50% · ส่วนที่เหลือ 60 วัน" (PaymentTerms + CreditDays=60) (2) แปลงเป็นใบแจ้งหนี้ → request ไม่มีสองช่องนี้ → Create ตกไปใช้ `Contact.PaymentTerms/PaymentDueDays` (เช่น 30 วัน) (3) ใบแจ้งหนี้พิมพ์เงื่อนไขคนละอย่างกับใบเสนอราคาที่ลูกค้าเซ็นรับ · `DueDate` ส่งมาจากแม่ (`dueDate ?? source.DueDate`) แต่ `CreditDays` เป็น 30 ⇒ สองค่าบนใบเดียวขัดกันเอง. `DimensionId` (cost center) หลุด ⇒ JE header ของใบลูกไม่มีมิติ (DOCUMENT_FLOW §3.2 ข้อ 7 บอกว่า JE สืบทอด DimensionId จากเอกสาร) ⇒ P&L ต่อมิติเห็นแค่ใบต้นทาง. `IsForeignService` หลุด ⇒ PI บริการต่างประเทศ → PV ไม่ถือธง ภ.พ.36
- ผลกระทบ: เอกสารที่ลูกค้าเห็นบอกเงื่อนไขผิด (ข้อพิพาทการเก็บเงิน) · รายงานต้นทุนตามมิติขาด · เส้น reverse-charge หลุด
- defect class: กฎเหล็ก #4 A "เอกสารลูกต้องสืบทอด … (สืบทอด null เป็น null — ห้ามแปลงเป็นค่า default ตอนสืบทอด)" · "Resolver กลาง ห้ามคำนวณเอง" (เทอมชำระเงินคือตัวอย่างในกฎ)
- ทางแก้ที่เสนอ: ส่ง `PaymentTerms: source.PaymentTerms, CreditDays: source.CreditDays, DimensionId: source.DimensionId, IsForeignService: source.IsForeignService, PaymentType: source.PaymentType` (+ tax-point dates เมื่อ target เป็นเอกสารภาษี) และเขียนเทสต์ reflection "ทุก field ที่ลูกค้าเห็นบน DocumentResponse ต้องอยู่ในลิสต์สืบทอดหรือลิสต์ยกเว้นที่ระบุชื่อ" (กลไกเดียวกับ `OcrScanSnapshot`)
- ความมั่นใจ: สูงสำหรับ PaymentTerms/CreditDays/DimensionId (เห็น fallback จริง) · กลางสำหรับ IsForeignService/tax-point (ต้องเช็คว่าคู่ convert ที่เกี่ยวข้องมีจริงใน ValidateConversionAsync)

### B-07 [P2][M] เส้นที่สร้างเอกสาร **Approved/Paid ตรง ๆ โดยไม่ผ่าน ApproveDocumentAsync** (settlement receipt · CN คืนมัดจำ) ไม่ตรึง `IssuerBranchCode` · ไม่เซ็ต `TaxPointDate` · ไม่เซ็ต `RetentionUntil`
- ไฟล์: DocumentService.cs:10350-10485 `CreateSettlementReceiptAsync` (คอมเมนต์บรรทัด 10373: "เส้นนี้ออกเลขเองไม่ผ่าน ApproveDocumentAsync ⇒ ต้องใช้กติกาเดียว" — ทำเฉพาะเรื่อง series) · :3752-3770 `RefundDepositAsync` (คอมเมนต์ 3752: "CN สร้างแบบ Approved ตรง ๆ ไม่ผ่าน ApproveDocumentAsync") · เทียบ ApproveDocumentAsync :5097 (`doc.IssuerBranchCode = DocumentIssuerBranch.ResolveCode(…)`), :5115 (`doc.TaxPointDate = TaxPointResolver.Resolve(doc)`), :5127-5135 (RetentionUntil)
- โค้ด: field list ของ `new Document {…}` ที่ 10395 มี `DocumentLanguage/BrandId/BranchId/DocumentTemplateId/ProjectId/Currency/ExchangeRate` แต่ `grep IssuerBranchCode|TaxPointDate|RetentionUntil` ในช่วง 10350-10485 = 0 · เช่นเดียวกับ 3659-3830
- ทำไมพัง: (1) ใบเสร็จ settlement เป็นใบที่มีบทบาท "ใบกำกับภาษี ณ วันรับเงิน" (§78/1 — `IsTaxInvoiceByLaw = carriesTaxInvoiceRole`) พิมพ์ส่งลูกค้า (2) `IssuerBranchCode = null` ⇒ renderer ตกไปใช้ค่า**สด**ของ Branch (`DocumentIssuerBranch.ResolveCode(snapshot=null, view, company…)` PdfGenerationService.cs:1284) ⇒ ถ้าสาขาถูกแก้/ย้ายทีหลัง กระดาษของใบที่ออกเลขแล้วเปลี่ยนตาม (กฎ "ค่าที่ใช้ตัดสินเลข ต้องถูกตรึงพร้อมเลข") (3) `RetentionUntil = null` ⇒ ใบชุดนี้ไม่มีอายุเก็บ — งาน purge ต้องตีความ null (ยังไม่ได้เช็คว่า null = ข้าม หรือ = หมดอายุ; ดู "ยังไม่ได้อ่าน") (4) `TaxPointDate = null` ⇒ รายงานภาษีใช้ `?? DocumentDate` (=PaymentDate) ผลถูกโดยบังเอิญ แต่ `DocumentResponse.TaxPointDate` ว่างให้ UI
- ผลกระทบ: §86/4 รหัสสาขาบนใบกำกับที่ออกแล้วไม่นิ่ง · retention ตาม พ.ร.บ.บัญชี ม.10 ไม่ครอบใบเสร็จ settlement ทั้งชุด
- defect class: "ด่านที่ครอบแค่ทางเดียว คือด่านที่ไม่มี" · "ของแบบนี้มีจุดออกเลขมากกว่าหนึ่งที่เสมอ — grep DocumentNumberGenerator.NextAsync ทุกจุด"
- ทางแก้ที่เสนอ: แยก "ขั้นตรึงค่าตอนออกเลข" (IssuerBranchCode/TaxPointDate/RetentionUntil) เป็น helper ตัวเดียว เรียกจากทุกจุดที่เรียก `DocumentNumberGenerator.NextAsync`
- ความมั่นใจ: สูงว่าไม่ได้เซ็ต · กลางเรื่องผลกระทบ retention (ต้องเปิด job)

### B-08 [P1][M] JE ของวงจรมัดจำ (Realize / Refund / Apply) ลงด้วย **ยอดสกุลต่างประเทศดิบ** ไม่คูณ `ExchangeRate` — ขณะที่ JE ตอนอนุมัติมัดจำคูณแล้ว ⇒ GL มัดจำสกุลต่างประเทศไม่ล้าง
- ไฟล์: DocumentService.cs:3158-3300 `RealizeDepositAsync` · :3659-3760 `RefundDepositAsync` (JE บรรทัด 3729-3741: `DebitAmount = refundBase`, `DebitAmount = refundVat`, `CreditAmount = request.Amount`) · :3812-4100 `ApplyDepositToInvoiceAsync` (มีแค่ guard 3896-3900 ว่ามัดจำกับใบต้องสกุล/เรตเดียวกัน) · เทียบ `AutoPostToJournalAsync` :12843+111 `var fx = doc.ExchangeRate;` (คูณทุกบรรทัด) และ :12848-12853 บังคับว่าเอกสารต่างสกุลต้องมี rate ≠ 1
- โค้ด: `grep -n "ExchangeRate\|fx" ในช่วง 3158-3300` = 0 · ช่วง 3659-3760 = 0 · ช่วง 3812-4100 พบเฉพาะ `Math.Abs(invoice.ExchangeRate - deposit.ExchangeRate) > 0.0001m` (guard ไม่ใช่การแปลง)
- ทำไมพัง: (1) รับมัดจำ USD 1,000 @ 35 → approve → JE Dr เงินฝาก 35,000 / Cr มัดจำรับ 32,710 + VAT 2,290 (คูณ fx) (2) รับรู้/ตัดชำระ/คืนมัดจำ → JE Dr มัดจำรับ **934.58** (ยอด USD ดิบ) ⇒ บัญชี 217xx เหลือค้าง 31,775 ตลอดกาล · คืนมัดจำ Cr เงินฝาก 1,000 แทน 35,000 (3) เงียบ เพราะแต่ละ JE "สมดุล" ในตัวเอง
- ผลกระทบ: งบดุล (มัดจำรับ/จ่าย) และ ภ.พ.30 (VAT ที่รับรู้ตอน realize) ผิดสำหรับทุกเคสมัดจำต่างสกุล
- defect class: "แก้ตัวเดียว เหลือที่เหลือ" (คอมมิต 327e879 แก้ AutoPost ให้คูณ fx แต่ 3 เส้นมัดจำที่สร้าง JE เองไม่ได้ตาม) · "JE ทุกจุดเดินผ่าน JournalEntryBuilder"
- ทางแก้ที่เสนอ: ทั้ง 3 เมธอดใช้ `doc.ExchangeRate` เป็นตัวคูณ (บันทึก `ForeignCurrency/OriginalRate` บนบรรทัดตาม TFRS บทที่ 19) หรือบล็อกมัดจำต่างสกุลตั้งแต่ Create พร้อมข้อความ — เลือกอย่างตั้งใจ
- ความมั่นใจ: กลาง — ต้องยืนยันว่า Deposit (Receipt IsDeposit) สร้างเป็น non-THB ได้จริงจากฟอร์ม (`fCurrency` มีในฟอร์ม Receipt ไหม) ถ้า UI บล็อกอยู่แล้วให้ลดเป็น P2 (API ยังทำได้)
- **เพิ่มเติม B-07 (ยืนยันแล้ว)**: ผู้อ่าน `RetentionUntil` มีที่เดียวคือ `PurgeDocumentAsync` (DocumentService.cs:7666-7672) `var inRetention = doc.RetentionUntil.HasValue && DateTime.UtcNow.Date < doc.RetentionUntil.Value.Date && doc.Status != DocumentStatus.Draft;` ⇒ **null = ไม่อยู่ในช่วงเก็บ** ⇒ Owner purge ใบ Paid/Approved ที่ RetentionUntil ว่างได้โดยไม่ต้องใส่เหตุผลและไม่มี log `RETENTION-OVERRIDE` (purge ลบ JE + payment จริง — ดู `purgedJournalIds` :7690) · backfill ใน DatabaseMigrationHelper.cs:6244 มี `WHERE … d."RetentionUntil" IS NOT NULL` ⇒ แถวที่ null (settlement receipt · refund CN · ใบที่อนุมัติก่อนมีคอลัมน์ · ใบจาก QuickApprove B-02) **ไม่เคยถูกเติมและจะไม่ถูกเติม** ⇒ ยกระดับผลกระทบ retention เป็นจริง ไม่ใช่สงสัย. (soft `DeleteDocumentAsync` :7454 มี guard Draft-only ครบ — ไม่ใช่ปัญหา)

### B-09 [P1][S] Clone (`DocumentCloneController`) ไม่คัดลอก `BillDiscountPercent/Amount` · `PaymentTerms` · `CreditDays` · `DimensionId` · `IsForeignService` · `BankAccountId/PaymentAccountId/ExpenseCategoryId` — ใบคัดลอกยอดรวมไม่เท่าต้นฉบับ
- ไฟล์: Accounting/Controllers/DocumentCloneController.cs:124-166 (`new CreateDocumentRequest(…)`)
- โค้ด: request มี `DueDate: src.CreditDays.HasValue ? docDate.AddDays(src.CreditDays.Value) : null, Reference, Notes, ProjectId, Currency, ExchangeRate, PaymentType, PricesIncludeVat, BrandId, BranchId, DocumentTemplateId, DocumentLanguage, CombinedInvoiceTaxInvoice, IssuedAsCashReceipt, CustomAppendix/FooterNotes/Terms` — `grep BillDiscount|PaymentTerms|DimensionId|IsForeignService|ExpenseCategoryId DocumentCloneController.cs` = 0
- ทำไมพัง: (1) ใบต้นฉบับมีส่วนลดท้ายบิล 10% (2) กด "คัดลอก" → ใบใหม่ไม่มีส่วนลด ⇒ SubTotal/VAT/Total สูงกว่าต้นฉบับ (3) ผู้ใช้เห็นใบที่ "เหมือน" แต่ยอดไม่ตรง — ถ้าไม่สังเกตแล้วอนุมัติ = ออกใบกำกับเกินราคาที่ตกลง (4) `CreditDays` ถูกใช้คำนวณ DueDate แต่ตัวมันเองไม่ส่ง ⇒ Create เติมจาก Contact ⇒ ใบใหม่แสดง "เครดิต 30 วัน" ทั้งที่ DueDate คิดจาก 45 (สองค่าขัดกัน — เหมือน B-06)
- ผลกระทบ: ยอดผิดบนใบกำกับ · เงื่อนไขชำระผิด · cost center หลุด
- defect class: "เอกสารลูกต้องสืบทอด" · "แยกของออกเป็นหลายชิ้น = ต้องถามทุกฟิลด์ว่าเป็นเปอร์เซ็นต์หรือจำนวนเงิน" (ที่นี่หายทั้งคู่)
- ทางแก้ที่เสนอ: ใช้ตัวสร้าง request ตัวเดียวร่วมกับ `ConvertCoreAsync` (`Helpers/DocumentHeaderInheritance`) แล้วเทสต์ reflection ครอบทั้ง convert/clone/recurring/settlement
- ความมั่นใจ: สูง · ขัดกับ SYSTEM_REVIEW U6 ("เอกสารลูกสืบทอดครบ") — U6 ตรวจเฉพาะ BrandId/Branch/Language ไม่ได้ไล่ทุก field

### B-10 [P2][M] Recurring (`RecurringTransactionService`) สร้างเอกสารได้เฉพาะ THB · ไม่มี `PaymentTerms`/`BrandId`/`DimensionId`/`DocumentTemplateId`/`IsForeignService` — และหน้า recurring.html ไม่มีช่องให้ตั้ง
- ไฟล์: Accounting/Services/Implementations/RecurringTransactionService.cs:455-512 (request fields: `DocumentType DocumentDate DueDate CreditDays ContactId Reference Notes Lines ProjectId BankAccountId PaymentAccountId ExpenseCategoryId PricesIncludeVat BillDiscountPercent BillDiscountAmount DocumentLanguage BranchId CombinedInvoiceTaxInvoice BuyerDeclinedTaxInvoice`) · recurring.html: `grep paymentTerms|currency|brandId|dimensionId|documentTemplateId` = 0
- ทำไมพัง: สัญญาเช่ารายเดือนสกุล USD / ลูกค้าที่ต้องออกใบภายใต้แบรนด์ที่สอง / ค่าใช้จ่ายประจำที่ต้องลง cost center — ตั้ง recurring ไม่ได้เลย ต้องออกใบมือทุกเดือน (ฟีเจอร์ "ประจำ" ครอบไม่ถึงเคสที่ทำซ้ำจริง)
- ผลกระทบ: ใช้งานไม่ได้กับลูกค้ากลุ่มนั้น · ใบที่ออกใช้แบรนด์/เทมเพลต default ผิดตัว
- defect class: "เอกสารลูกต้องสืบทอด … recurring" (กฎเหล็ก #4 A ระบุ recurring เป็นทางเข้าที่ต้องไหลตาม)
- ความมั่นใจ: สูงว่าขาด · P2 เพราะเป็น feature gap ไม่ใช่ข้อมูลเสีย

### B-11 [P2][S] `Overdue` เป็นสถานะติดถาวร: ขยาย `DueDate` ออกไปในอนาคต หรือหนี้ลดจนไม่ค้าง (CN) แล้วสถานะไม่กลับ — ไม่มี code path ใดเขียน Overdue → Approved/Sent
- ไฟล์: Accounting/Services/Implementations/BackgroundJobService.cs:171-190 (ตัวเดียวที่ flip → Overdue; ไม่มี flip กลับ) · DocumentService.cs:2118-2121 (UpdateDocumentAsync แก้ DueDate ได้ใน revision แต่ `grep "Status = " ในเมธอด` = 0) · :4094-4100 (payment: `else if (BalanceDue > 0.005m && Status == Approved) → PartiallyPaid` — Overdue ที่ชำระบางส่วนคง Overdue ซึ่งถูก) · repo-wide `Status = …Approved` มีแค่ ternary ตอน void payment (:7232, :8354, :8504, :8782) ซึ่งกลับเป็น Approved **ทับ** Sent/Overdue เดิม
- ทำไมพัง: (1) ใบเกินกำหนด → job ตั้ง Overdue + แจ้ง LINE (2) ตกลงเลื่อนนัดชำระ ผู้ใช้ทำ revision แก้ DueDate เป็นเดือนหน้า (3) สถานะยัง Overdue ⇒ OverdueDunningJob ยังทวงลูกค้าต่อ · รายงานลูกหนี้ค้างชำระนับผิด · ทางกลับด้าน: void การชำระบน**ใบ Overdue** ทำให้สถานะกลายเป็น Approved ⇒ job รอบหน้าตั้ง Overdue ใหม่และ **แจ้งเตือน "เกินกำหนดครั้งแรก" ซ้ำ** (`firstTimeOverdue = doc.Status != Overdue`)
- ผลกระทบ: ทวงหนี้ลูกค้าผิด (ความสัมพันธ์ลูกค้า) · aging/รายงาน AR ผิด
- defect class: "สถานะ lifecycle ที่ไหลไปข้างหน้า — ด่านที่เขียน `== Approved` เป๊ะ ๆ มักผิด" · "แก้ field แล้วไม่ recalc ค่าที่ derive จากมัน"
- ทางแก้ที่เสนอ: ตัวคำนวณสถานะการเงินกลางตัวเดียว `DocumentPaymentState.Resolve(doc, today)` (Approved/PartiallyPaid/Paid/Overdue จาก PaidAmount+BalanceDue+DueDate) เรียกจากทุกจุดที่แตะ PaidAmount/DueDate — แทน ternary 8 ชุดที่ threshold ไม่ตรงกัน (0 · 0.005 · 0.01 — ดู B-14)
- ความมั่นใจ: สูง (ไม่มี write path กลับ) · ควรเช็ค OverdueDunningJob ว่าคัดด้วย Status หรือ DueDate

### B-12 [P3][S] DOCUMENT_FLOW.md drift 3 จุดที่ทีมนี้ชน
- §3.2 :729 "ทางเข้า approve มี 3 ทาง — ทุกทางวิ่งเข้า ApproveDocumentAsync เดียวกัน" — มีทางที่ 4 (`MobileApiService.QuickApproveAsync`) ที่ไม่วิ่งเข้า (B-02) และ doc เขียนไว้เองว่า "เดิมตั้ง Status ตรง ๆ ข้าม JE — แก้แล้ว" (แก้เฉพาะ SignatureApprovalService)
- line refs เก่าทั้งหมด: §3.2 `ApproveDocumentAsync (:1512)` จริง :4467 · gates `:1638/:1528/:1760/:1773/:1789` จริง :4858/…/5115/5150/5177 · §3.5 `VoidDocumentAsync (:1959)` จริง :6745 — "Quick reference" ตายตามกฎในไฟล์เอง
- §3.5 ไม่มีขั้น "7-asset" (ลบ FixedAsset ที่ AutoRegister · ข้ามเมื่อยืนยัน/มีค่าเสื่อม posted) ทั้งที่โค้ดมี (:7160-7190) และเป็นจุดที่เงียบ (B-05)
- ลำดับขั้น approve **ตรงกับโค้ด** (งวดปิด → transaction → เลข → สาขา → tax point → retention → §65 ตรี → JE → undue reclass → stock → supersede → asset → commit) และ transaction ครอบตั้งแต่ออกเลข (:4869) ถึง commit (:5293) ⇒ partial failure = rollback ทั้งก้อน **ไม่เกิด gap** (`pg_advisory_xact_lock` ปล่อยพร้อม rollback) — ไม่ใช่บั๊ก
- defect class: "doc drift เกิดกับตัวเองได้"

### B-13 [P3][S] แก้ไขใบร่างที่มี `DepositAppliedAmount/Ref` บันทึกไว้ — openEdit ไม่ hydrate `_pendingDepositApplies` จาก `d.depositApplied*` ⇒ ผู้ใช้ไม่เห็นว่าใบนี้จะหักมัดจำ และถอดออกไม่ได้ (server null=คงเดิม :2183-2185) ถ้าเลือกมัดจำใบใหม่ payload ทับ `depositAppliedRef` ทั้งชุด (documents.html:8485-8498)
- ความมั่นใจ: กลาง (เส้นนี้เปิดเฉพาะ `IssuedAsCashReceipt` + ร่าง — ต้องทดลอง runtime)

### B-14 [P3][S] เกณฑ์ "จ่ายครบ" ไม่ตรงกัน 3 ค่าใน 8 จุด: `<= 0.005m` (:4094, :4345) · `<= 0.01m` (:7232, :8727, :8785) · `<= 0` (:8354, :8504, :10668, :11028) ⇒ ใบเดียวกันจ่ายด้วยช่องทาง A เป็น Paid แต่กลับรายการแล้วจ่ายใหม่ผ่านช่องทาง B เป็น PartiallyPaid เมื่อเศษสตางค์ 0.003-0.01 · ตัวตัดสินควรเป็นฟังก์ชันเดียว (รวมกับ B-11)

## ตรวจแล้วไม่ใช่บั๊ก (ทีมอื่นไม่ต้องซ้ำ)
- **PaidAmount ↔ BalanceDue** ทุกจุดที่ +=/−= (12 จุด: 4090, 4344, 6997, 7011, 7230, 8350, 8500, 8674/8725, 8782, 10666, 11025) recompute BalanceDue ในบรรทัดถัดไปครบ — ไม่มี "update ฝั่งเดียว" (ต่างกันแค่ threshold — B-14)
- **CN รวม ≤ ใบต้นทาง** บังคับที่ `ApplySourceDocumentAdjustmentsAsync` :8687 (`cnSiblingSum + doc.TotalAmount > source.TotalAmount + 0.01m` → throw; คัด Voided/Rejected/Draft ออก) ซึ่งเรียกจาก approve :5180 — ครอบทั้ง CN มือและ CN จาก convert เพราะทุกใบต้องผ่าน approve · ร่างเกินยอดสร้างได้แต่อนุมัติไม่ได้ = ถูกต้อง · เคส "ใบต้นทางจ่ายครบ" CN เป็น cash-refund ไม่แตะ PaidAmount (:8712-8714) ตั้งใจ
- **ชำระเงินบนใบ Voided/Draft/Rejected/WaitingApproval** — `CreatePaymentAsync` :10515-10516 และ `CreateMultiDocPaymentAsync` :10920-10922 ปฏิเสธ (รับเฉพาะ Approved/PartiallyPaid/Sent/Overdue) · payment reversal มี `if (Status != Voided)` ครอบ (:8352, :8502, :8784)
- **VoidDocumentAsync** ครอบ: payments → JE → JV มัดจำ (2b) → e-Tax (block Accepted, void อื่น) → BankTransactions unlink → source adjustments → stock(−1) → project cost → WHT cert → supersede → asset → undue VAT flags → deposit flags → drives deposit · **บล็อก**: Voided ซ้ำ · settlement receipt · รายงานภาษี Filed · e-Tax Accepted · เอกสารลูก active (RelatedDocumentId, คัด Voided/Rejected) · CN/DN legacy ที่อ้างด้วยเลขที่ — ครบตาม DOCUMENT_FLOW §3.5 ยกเว้นข้อ B-05 (asset skip เงียบ) และ FX มัดจำ (B-08)
- **ApproveDocumentAsync transaction** ครอบเลข→JE→stock→asset→commit; rollback ปล่อย advisory lock ⇒ ไม่เกิด gap เลข (B-12 ยืนยัน)
- **Update null-vs-empty สำหรับ GUID** ทำถูกผ่าน `_gsel()`/`CLEAR_GUID` (documents.html:8278-8296) — ปัญหาเหลือเฉพาะช่องข้อความ (B-01)
- **Currency/ExchangeRate/RelatedDocumentId ตอนแก้ไข** — ไม่อยู่ใน UpdateDocumentRequest โดยตั้งใจ และ UI ล็อกพร้อมบอกเหตุผล (`_hydrateCurrencyReadonly` :7720, `_pinnedSourceId` :7712) — ผ่านกฎ "ห้าม silent no-op"
- **hydrate ตอน openEdit** ครอบ field ส่วนใหญ่แล้ว (brand/branch/template/currency/creditDays/paymentTerms/language/deposit/CN reason/billDiscount/cash-receipt/paidOnIssue/foreignService/tax-point dates/VAT claim period) — fields.py ฟ้อง NO_HYDRATE ผิดกับ BranchId/BrandId/DocumentTemplateId (hydrate ผ่าน helper `_hydrateBrand/_hydrateBranch/_hydrateTemplate` ที่ regex ไม่เห็น)
- **DeleteDocumentAsync (soft)** :7454 มี guard Draft-only + ไม่มี JE/payment/e-Tax — ถูก (ปัญหาอยู่ที่ Purge — B-07)
- **settlement receipt** สืบทอด Currency/ExchangeRate/DocumentLanguage/Brand/Branch/Template/Project ครบ (ต่างจาก convert/clone)

## ซ้ำกับ SYSTEM_REVIEW (ID เดิม + ยังเปิดอยู่จริงไหม)
- **D9** (P2, ยังเปิด — ยืนยันแล้ว): `PricesIncludeVat/BrandId/BranchId/DocumentTemplateId` ยังอยู่ใน `if (request.Lines != null)` (DocumentService.cs:2263-2280) — PUT field เดียวไม่มีผล
- **U6** ("เอกสารลูกสืบทอดครบ") — **ผลตรวจรอบนี้ขัดแย้ง**: B-06/B-09/B-10 พบ field ที่ไม่สืบทอดใน convert/clone/recurring หลายตัว (PaymentTerms/CreditDays/DimensionId/BillDiscount ใน clone) — ควรถอด U6 ออกจากรายการ "ไม่ใช่บั๊ก"
- **T-11 ✅** (RetentionUntil สูตร) แก้แล้ว แต่ backfill `IS NOT NULL` ทิ้งแถว null (B-07 เพิ่มเติม) — ไม่ซ้ำ เป็นของใหม่
- **D1 ✅** (currency ใน save) แก้แล้วจริง — ยืนยันจาก payload :8344 และ ConvertCoreAsync ส่ง Currency/ExchangeRate

## ยังไม่ได้อ่าน
- `ConvertDocumentPartialAsync` (:9488-9700) รายละเอียด partial allocation ของ BillDiscountAmount (ดูสูตร :9276-9283 แล้วแต่ไม่ได้ simulate)
- `UpdateDocumentAsync` ช่วง revision (Approved/Sent) 2000-2110 — ผลต่อ PaidAmount/BalanceDue เมื่อยอดใบเปลี่ยนหลังชำระบางส่วน (D9 ครอบบางส่วน)
- `OverdueDunningJob` — คัดด้วย Status หรือ DueDate (กระทบความรุนแรง B-11)
- `AutoPostToJournalAsync` :12843+ รายชนิดเอกสาร (ทีม C/บัญชี) · `ApplyStockMovementsAsync`
- ทางเข้า integration/OCR → CreateDocument (IntegrationService สร้าง `Status = Approved` ใน object initializer 7 จุด :889/1172/1284/3121/3248/3490 — **ควรตรวจว่าเดินผ่าน ApproveDocumentAsync หลังจากนั้นหรือเปล่า** เหมือน B-02; ไม่ทัน)
- `PosService.Orders.cs:459` และ `CrossTenantWorkflowService.cs:405` `Status = Approved` ใน initializer — คำถามเดียวกัน
- runtime test ใด ๆ (ไม่มี SDK)

## ข้อเสนอเชิง ERP (แยกจากบั๊ก)
1. **Header-inheritance contract ตัวเดียว** — `DocumentHeaderInheritance.Copy(source, target, mode)` + เทสต์ reflection ที่บังคับว่าทุก property บน `Document` ต้องอยู่ในลิสต์ "สืบทอด" หรือ "ตัวตนของแถว/ไม่สืบทอด" (กลไกเดียวกับ `OcrScanSnapshot`) — ปิด B-06/B-09/B-10 พร้อมกันและกันตัวถัดไปเมื่อเพิ่ม field
2. **State machine ของ DocumentStatus เป็นตารางเดียว** (`DocumentStatusMachine.CanTransition(from,to)` + `ResolvePaymentState`) แทน ternary/if กระจาย 20+ จุด 3 threshold — ปิด B-03/B-04/B-11/B-14 และทำให้ "Sent" ถูกตัดสินว่าจะมีจริงหรือลบ
3. **จุดออกเลข = จุดตรึงค่า** — helper `IssueDocumentNumberAsync` ที่ทำ number + IssuerBranchCode + TaxPointDate + RetentionUntil ด้วยกัน เรียกจากทุก `DocumentNumberGenerator.NextAsync` (approve · settlement receipt · refund CN · supersede) — ปิด B-07
4. **Update semantics ชัดเจนต่อ field**: PATCH-style DTO ที่แยก "ไม่ส่ง" จาก "ส่ง null" (เช่น `Optional<T>`) — ปิด B-01/B-13/D9 โดยโครงสร้าง ไม่ต้องจำกติกาต่อช่อง
5. **ทุก writer ของ `Document.Status` ที่ไม่ใช่ DocumentService** (MobileApi · Integration · POS · CrossTenant · SignatureApproval · ApprovalService) ควรถูกบังคับด้วย checker (`document_status_writer_check.py` — รูปทรงของโค้ด เหมือน `stock_writer_check`) ให้เขียนได้เฉพาะผ่าน `IDocumentService` — ปิด B-02 และเคสที่ยังไม่ได้อ่าน

