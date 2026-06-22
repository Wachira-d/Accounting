# Document Flow — Reference

> **ขอบเขต**: ไล่ flow เอกสารทุกประเภทในระบบ ตั้งแต่ "นำเข้า"
> (สร้างเอง / OCR / integration / convert / recurring) จนถึง "นำออก"
> (PDF / e-tax XML / รายงานภาษี / integration)
> **เป้าหมาย**: ใช้เป็น single source of truth ของ behavior — ทุก commit
> ที่เปลี่ยน flow ต้องอัปเดตไฟล์นี้พร้อมกัน

อ้างอิงไฟล์หลัก:
- `Models/Enums/AllEnums.cs:305` — `DocumentType` enum
- `Models/Enums/AllEnums.cs:343` — `DocumentStatus` enum
- `Services/Implementations/DocumentService.cs` — core flow (5,700+ บรรทัด)
- `Services/Implementations/OcrService.cs` — OCR pipeline
- `Services/Implementations/TaxService.cs` + `.Export.cs` + `.EFiling.cs`
- `Services/Implementations/TaxFilingExportService.cs` — RD e-Filing
- `Services/Implementations/PdfGenerationService*.cs` — PDF/A-3
- `Services/Implementations/EtaxInvoiceService.cs` — XAdES-BES signing

---

## 1. ประเภทเอกสาร (DocumentType)

### 1.1 ฝั่งรายรับ (Sales / Revenue)
| Code | Type | ชื่อไทย | บทบาท |
| --- | --- | --- | --- |
| 1 | `Quotation` | ใบเสนอราคา | ก่อนขาย, ไม่กระทบ GL/VAT |
| 2 | `Invoice` | ใบแจ้งหนี้ | สร้าง AR + revenue (accrual) |
| 11 | `BillingNote` | ใบวางบิล | รวมใบแจ้งหนี้หลายใบมาวางบิล |
| 4 | `TaxInvoice` | ใบกำกับภาษี | output VAT เข้า ภ.พ.30 |
| 3 | `Receipt` | ใบเสร็จรับเงิน | รับเงิน (ปิด AR หรือ cash sale) |
| 14 | `ReceiptVoucher` | ใบสำคัญรับ | ใบเสร็จ-ใบกำกับฯ คู่เดียว / มัดจำ |
| 10 | `DeliveryNote` | ใบส่งของ | เคลื่อน stock ฝั่งส่ง (sales-side) |
| 5 | `DebitNote` | ใบเพิ่มหนี้ | +ฐาน + VAT ของใบเดิม (§86/9) |
| 6 | `CreditNote` | ใบลดหนี้ | −ฐาน − VAT (§86/10) — **บังคับเลือก `CreditNoteReason`** |

### 1.2 ฝั่งรายจ่าย (Purchase / Expense)
| Code | Type | ชื่อไทย | บทบาท |
| --- | --- | --- | --- |
| 12 | `PurchaseRequisition` | ใบขอซื้อ | internal, ไม่กระทบ GL/VAT |
| 7 | `PurchaseOrder` | ใบสั่งซื้อ | commitment, ไม่กระทบ GL/VAT |
| 16 | `GoodsReceiptNote` | ใบรับสินค้า (GRN) | รับของจริง, accrue GRNI, +stock |
| 8 | `PurchaseInvoice` | ใบแจ้งหนี้ซื้อ | input VAT + AP (3-way match) |
| 9 | `Expense` | ใบบันทึกค่าใช้จ่าย | input VAT + ค่าใช้จ่าย (ไม่ผูก PO) |
| 13 | `PaymentVoucher` | ใบสำคัญจ่าย | จ่ายเงิน — อ้างใบกำกับซื้อ (PV master switch) |
| 15 | `CertificateInLieu` | ใบรับรองแทนใบเสร็จ | จ่ายที่ผู้ขายไม่ออกใบเสร็จ (ขนส่ง/ตลาด/ฯลฯ) |

### 1.3 สถานะ (DocumentStatus)
```
Draft → WaitingApproval → Approved → Sent → PartiallyPaid → Paid
                            ↓
                          Voided / Rejected / Overdue
```
- **`Draft`** = `DocumentNumber = "DRAFT-{guid}"` placeholder (กัน gap §86/4)
- **`Approved`** = ออกเลขจริง + post JE + snapshot tax point + stock move
- **`Voided`** = reverse JE + reverse stock; เก็บไว้ดู audit (ห้าม hard delete)
- **`Sent`** = email ออกแล้ว (optionally e-tax-by-email + RD cc)

---

## 2. ทางเข้า (Entry Points)

### 2.1 สร้างมือ (ผู้ใช้)
- **Endpoint**: `POST /api/companies/{companyId}/documents`
- **Controller**: `DocumentController.cs:165` → `DocumentService.CreateDocumentAsync` (`DocumentService.cs:279`)
- **กฎ**:
  - ตั้ง `Status = Draft`, `DocumentNumber = "DRAFT-{guid}"`
  - resolve `AccountCode → AccountId` ต่อบรรทัด (รองรับ AI suggest)
  - validate VAT-claimability ต่อบัญชี (`ChartOfAccount.InputVatClaimable`)
  - compute line amounts (`ComputeLineAmounts`) รองรับ `PricesIncludeVat`
  - เก็บ `GlAccountAiFeedbackId` ต่อบรรทัด (ปิดลูปการสอน local model)
  - ยังไม่สร้าง JE / ยังไม่กระทบ stock จนกว่าจะ Approve

### 2.2 OCR (สแกนเอกสาร)
- **Flow**: อัปโหลด → OCR เก็บ `OcrScanResult` → review modal → "สร้างเอกสาร"
- **Endpoint**: `POST /api/companies/{id}/ocr/{scanId}/create-document`
- **Service**: `OcrService.CreateDocumentFromScanAsync` (`OcrService.cs:3245`)
- **กฎเหล็ก #3**: OCR ต้อง pre-fill **ครบทุก field §86/4** — ผู้ใช้แค่ "ยืนยัน"
- **Field ที่ OCR map**:
  - Header: contact (match TaxId → contact / สร้างใหม่ถ้าไม่เจอ), invoice no/date,
    supplier branch, VAT base/amount/grand
  - Lines: description, qty, unit price, vat rate, **`GlAccountAiFeedbackId`**
    (เดิมหาย — ตอนนี้แนบให้บรรทัดแรกที่ใช้ผัง AI ตอน `Approve` จะเรียก
    `RecordLineAccountFeedbackAsync` ปิดลูป)
  - Payment: `scanDebitAccountId` → `ExpenseCategoryId`; `scanCreditAccountId` →
    `BankAccountId / PaymentAccountId` (lookup `LinkedAccountId`)
  - PV: `bookSupplierInvoice` flag → `HasTaxInvoiceReference = true` +
    `SupplierInvoiceNumber / Date / BranchCode`
- **Fallback chain (กฎเหล็ก #3)**:
  Vision/OCR → local distillation model → historical lookup (vendor's last doc)
  → rule defaults (VAT 7%, branch 00000, vendor default GL) → AI ตอน last resort
  (ผ่าน `IAiOrchestrator.AskAsync` per กฎเหล็ก #1)
- **Quota refund**: ถ้า re-OCR (retry) ไม่ใช้ quota ใหม่ (`OcrService.cs`)

### 2.3 Integration ภายนอก
- **Controller**: `IntegrationController.cs` — manage config + API key issuance
- **เข้าทาง** `/api/companies/{id}/documents` (ทาง standard) พร้อม
  `X-Acting-User` header
- **Behavior พิเศษ**:
  - resolve external account/user IDs → company members (mapping table)
  - `AutoApprove` flag ตาม `IntegrationConfig` → ข้าม Draft state
  - `/integration/journals` + `/integration/daily-summary` → JournalEntry ที่
    **ไม่มี SourceDocumentId** (รายงาน VAT มี fallback ใน `TaxService.cs:436+`
    สแกนหา JE ที่มี VAT account แล้วรวมเข้า ภ.พ.30 ให้)

### 2.4 Convert (แปลงเอกสาร)
- **Method**: `DocumentService.ConvertDocumentAsync` (full) / `ConvertDocumentPartialAsync`
  (partial — qty subset) — `DocumentService.cs:3082` / `:3122`
- **กันสร้างซ้ำ**: `ComputeConsumptionAsync` (`:3104`) — ตรวจ axis
  (`Delivery` / `Billing` / `None`) ที่ source line ถูกใช้ไปเท่าไหร่แล้ว
- **คู่ที่แปลงได้** (`DocumentService.ValidConversions :2808`) — exact:
  ```
  Quotation        → Invoice / TaxInvoice / BillingNote / DeliveryNote / Receipt
  BillingNote      → Invoice / TaxInvoice / Receipt
  DeliveryNote     → Invoice / TaxInvoice
  Invoice          → TaxInvoice / Receipt / ReceiptVoucher
  TaxInvoice       → Receipt / ReceiptVoucher / CreditNote / DebitNote
  DebitNote        → Receipt / ReceiptVoucher

  PurchaseRequisition → PurchaseOrder
  PurchaseOrder       → GoodsReceiptNote / PurchaseInvoice
  GoodsReceiptNote    → PurchaseInvoice
  PurchaseInvoice     → PaymentVoucher / CreditNote / DebitNote
  Expense             → PaymentVoucher / CreditNote / DebitNote / CertificateInLieu
  CertificateInLieu   → PaymentVoucher
  ```
  **Terminal** (no further conversion): `Receipt`, `ReceiptVoucher`, `CreditNote`, `PaymentVoucher`
- **Lineage**: ลูก carry `RelatedDocumentId = source.Id`, `SourceLineId` ต่อ
  บรรทัด (จำเป็นสำหรับ partial fulfillment + 3-way match)
- **Cascade**: `CustomAppendix / RevenueContractId / PerformanceObligationId /
  FileAttachment` (`CascadeAttachmentsAsync :3077`)

### 2.5 Recurring
- **Service**: `RecurringTransactionService.cs:41`
- ความถี่: `Daily / Weekly / BiWeekly / Monthly / Quarterly / SemiAnnual / Annual`
- template เก็บใน `RecurringTransaction.TemplateData` (JSON)
- `AutoApprove=true` → ใบที่ generate ขึ้นจะถูก approve อัตโนมัติ (ทำตาม flow
  approve ปกติทุกขั้น — tax point, JE, stock, fixed asset)

---

## 3. Lifecycle — สิ่งที่เกิดในแต่ละ transition

### 3.1 Create / Update (Draft → Draft)
- **Update เฉพาะ Draft** — service guard: `doc.Status != Draft` throws
- **Field ที่ Update ได้** (เพิ่งเพิ่ม): `CreditNoteReason`, `IsForeignService`,
  `IsDeposit`, `DepositDeferredAccountCode`, `DepositOutputVatDeferred`
  (เดิม `UpdateDocumentRequest` ไม่มี → แก้ Draft แล้ว field เหล่านี้ "เงียบหาย")
- **เลขเอกสารยังเป็น `DRAFT-{guid}`** (ไม่ออกเลขจริง กัน gap §86/4)

### 3.2 Approve (Draft/WaitingApproval → Approved) — **ขั้นสำคัญที่สุด**
**`DocumentService.ApproveDocumentAsync` (`:1512`)** ทำตามลำดับ:

1. **Permission + workflow gate** (`:1638`) — ตรวจ ApprovalWorkflow
   (multi-level), credit limit ของลูกค้า (AR/AP advanced)
2. **AI warning collection** (`:1528`) — AI rule-based ตรวจหา anomaly
   (ราคาผิดปกติ, vendor ไม่ตรงประเภท ฯลฯ)
3. **ออกเลขจริง** (`:1742`) — `DocumentNumberGenerator.NextAsync` — gap-free
   running per (CompanyId, BranchCode, TaxYear) ตาม §86/4
4. **Snapshot Tax Point** (`:1760`) — `TaxPointResolver.Resolve(doc)` →
   `doc.TaxPointDate` = MIN(delivery / ownership transfer / payment received /
   invoice issue) ตาม §78 / §78/1 → ตัดสินงวด ภ.พ.30
5. **Retention** (`:1766`) — `doc.RetentionUntil ??= DocumentDate + 5 years`
   ตาม พ.ร.บ.บัญชี ม.10 (ห้ามลบจริงก่อนหมดอายุ)
6. **§65 ตรี** (`:1773`) — `ApplySection65TerAsync` → `doc.NonDeductibleAmount`
   + breakdown JSON (`NonDeductibleRuleJson`); ไหลเข้า ภ.ง.ด.50 ผ่าน
   `TaxService.GenerateCitReport` (บวกกลับ)
7. **Auto-post JE** (`:1789`) — `AutoPostToJournalAsync` แตกตาม `DocumentType`:
   - sales: Dr AR / Cr Revenue + Cr Output VAT (21911 หรือ 21913 ถ้า
     deposit deferred)
   - purchase: Dr Expense + Dr Input VAT (11610 หรือ **11640** ถ้า §86/4
     ไม่ครบ) / Cr AP
   - cash receipt: Dr Cash/Bank / Cr AR (หรือ Cr 217xx ถ้า `IsDeposit`)
   - payment voucher: Dr AP/Expense / Cr Cash/Bank
   - WHT: Cr 21915/21916 ตามประเภทเงินได้
8. **Stock movements** (`:1794` → `ApplyStockMovementsAsync :4605`) —
   switch ตัดสินตาม `DocumentType` (`:4612`):
   - **OUT (−1)**: `Invoice` / `TaxInvoice` (sale)
   - **IN (+1)**: `GoodsReceiptNote` / `PurchaseInvoice` /
     `CreditNote when Reason==Return`
   - `PurchaseInvoice` ที่ผูก GRN accrual แล้ว → **ข้าม** (กันนับซ้ำ
     `:4633`)
   - **No-op (`_ => 0`)**: ทุกประเภทอื่น — รวมถึง `Receipt`,
     `ReceiptVoucher`, `DeliveryNote` (`DeliveryNote` ตั้งใจไม่ trigger
     เพราะ Invoice ที่ตามมาจะ trigger ให้ — กัน double-count `:4608`),
     `Quotation`, `PO`, `PR`, `BillingNote`, `DebitNote`, และ
     `CreditNote.Discount/Adjustment/Writeoff`
9. **Fixed asset auto-register** (`:1799`) — `AutoRegisterFixedAssetsAsync`:
   บรรทัดที่ลงผัง 12210 / 12220 / 12230 / 12240 / 12260 / 12270 / 12290 /
   12310 → สร้าง `FixedAsset` ที่ `Status = Active, NeedsReview = true` พร้อม
   suggested `UsefulLifeMonths` + depreciation method (`StraightLine` default,
   ที่ดิน → `None`)
   - **UX force-review** (ครบใน commit หลัง audit): หลัง approve
     `documents.html` เรียก `_maybePromptFixedAssetReview` → fetch
     `/fixedasset/needs-review` → ถ้ามีรายการ → toast เด่นพร้อมปุ่มลัด
     "ไปยืนยันสินทรัพย์" (auto-dismiss 12s)
   - **Banner ที่หน้าทะเบียนสินทรัพย์** (`fixed-assets.html`): list สินทรัพย์
     `NeedsReview=true` ติด badge "⚠️ รอตรวจ" + row สีเหลือง + ปุ่ม
     "ตรวจสอบและยืนยัน" (primary) — เปิดแก้เลย
   - **Auto-clear**: เมื่อผู้ใช้กด save ใน edit-asset modal → `Update` ปลด
     `NeedsReview = false` อัตโนมัติ (FixedAssetService.cs Update +
     implicit confirmation semantics — ไม่ใช่ field ใน DTO กัน client เผลอเซ็ตกลับ)
10. **Audit log** — append-only row + hash chain (`PrevHash + RowHash SHA-256`)
11. **Document number lock** — `IsDocumentNumberLocked = true` (กันแก้ภายหลัง)

### 3.3 Send (Approved → Sent)
- **Endpoint**: `POST /documents/{id}/send-email`
- **Controller**: `DocumentController.cs:43` → `IDocumentEmailService`
- **กระทำ**:
  - generate PDF (ถ้ายังไม่มี cache)
  - ถ้ามี EtaxInvoice + IsEtaxByEmail → cc `csemail@etax.teda.th` +
    subject = `{TaxId}.{DocNo}` (RD บังคับ)
  - log ลง `DocumentEmailLog` (Sent/Failed/Bounced)
- **ไม่กระทบ JE / stock / VAT**

### 3.4 Pay / Partial Pay
- **Methods**: `CreatePaymentAsync` / `CreateMultiDocPaymentAsync`
  (`:534–541`)
- คำนวณ `BalanceDue = TotalAmount − TotalPaid`
- → `PartiallyPaid` หรือ `Paid` อัตโนมัติ
- post JE: Dr Cash/Bank / Cr AR (sales) หรือ Dr AP / Cr Cash/Bank (purchase)
- WHT cert auto-issue (`WithholdingTaxCertService` — ถ้ามี WHT บนใบ)

### 3.5 Void / Cancel
- **Method**: `VoidDocumentAsync` (`:1954`)
- reverse JE (gen JE ใหม่ Cr/Dr กลับ — ไม่ลบ JE เดิม)
- reverse stock (`ApplyStockMovementsAsync(−1)`)
- reverse project cost entries
- ตั้ง `Status = Voided`, `VoidedAt`, `VoidedBy`
- **ห้าม hard delete** (ตาม §86/4 + พ.ร.บ.บัญชี)

### 3.6 §82/3 Undue VAT Reclassification (auto)
- เมื่อ approve PI/Expense ที่ใบกำกับยังไม่ครบ §86/4 (ขาดเลข/วันที่/สาขา
  ผู้ขาย) → input VAT ลง **11640 "ภาษีซื้อยังไม่ถึงกำหนด"** (ไม่เคลม)
- ผู้ใช้เติมข้อมูลภายหลังผ่าน `POST /documents/{id}/complete-tax-invoice`
  → `ReclassifyUndueInputVatAsync` (`:1137`): สร้าง JE Dr 11610 / Cr 11640
  + ตั้ง `InputVatBecameClaimableAt = now`
- รายงาน ภ.พ.30 ใช้ `InputVatBecameClaimableAt` เป็น tax point (ไม่ใช่
  `DocumentDate` ของใบเดิม) — เคลมในเดือนที่ใบครบ

### 3.7 มัดจำ (Deposit lifecycle)
- เปิด Receipt/ReceiptVoucher ที่ `IsDeposit = true`:
  - **`DepositOutputVatDeferred = false`** (default): Cr Output VAT 21911
    เข้า ภ.พ.30 ทันที (§78 รับชำระราคา = tax point) + Cr 217xx ขายรอรับรู้
  - **`DepositOutputVatDeferred = true`**: Cr 21913 "ภาษีขายรอเรียกเก็บ"
    — ยังไม่เข้า ภ.พ.30 จนกว่าจะ realize
- **Realize**: `RealizeDepositAsync(amount, revenueAccountCode)` →
  Dr 217xx / Cr รายได้ (41xxx/42xxx); ถ้า deferred → ย้าย 21913 → 21911
  พร้อม `DepositOutputVatRecognizedAt = now`
- **Refund**: `RefundDepositAsync` → reverse + ออกใบลดหนี้ภาษีขาย
- **Apply**: `ApplyDepositToInvoiceAsync` (`:1389`) → ใน 1 transaction:
  1. คำนวณ `vatPortion = deposit.VatAmount / deposit.TotalAmount`
  2. เรียก `RealizeDepositAsync` ด้วยฐานไม่รวม VAT (`amount × (1−vatPortion)`)
     → Dr 217xx ขายรอรับรู้ / Cr รายได้ + (ถ้า deferred) ย้าย 21913→21911
  3. ลด `invoice.BalanceDue` ตามยอด `amount` (gross — มัดจำจ่ายเงินจริงแล้ว
     ถือเป็น prepayment)
  4. ถ้า BalanceDue ≤ 0.005 → ตั้ง `Status = Paid`
  5. mark `deposit.DepositAppliedToDocumentId = invoiceId` (1 ใบมัดจำ → 1 ใบ
     ปลายทาง; ถ้า apply หลายใบต้องเรียกหลายครั้ง)
  6. Fire webhook `deposit.applied`
- **UX**: ตอนผู้ใช้เลือก contact ในฟอร์ม Invoice/TaxInvoice/Receipt/Quotation/
  BillingNote → `onContactChange` เรียก `checkContactDeposits(contactId)` →
  ถ้า `GetContactDepositSummaryAsync` คืน outstanding > 0 → โชว์ banner เขียว
  "ลูกค้านี้มีมัดจำคงค้าง XXX" + ปุ่ม "หักมัดจำจากใบนี้" (เฉพาะ
  `editingId != null` — ต้องบันทึกใบก่อนถึงจะ apply ได้) → เปิด picker modal
  เลือกใบมัดจำ + ยอด → `applyDeposit` endpoint
- **Booking-match auto-suggest**: เมื่อใบปลายทางและใบมัดจำมี `BookingNumber`
  เดียวกัน (เคส PMS/POS/CRM: ลูกค้าจอง BK-2026-001 → จ่ายมัดจำ → ออกใบกำกับ) →
  - banner โชว์ badge เพิ่ม "✓ N ใบ booking ตรงกัน (XXX บาท)"
  - ปุ่มเปลี่ยน label เป็น "หักมัดจำที่ booking ตรงกัน"
  - picker modal sort booking-match ขึ้นบนสุด + prefix "✓" + option label
    มี `· booking BK-XXX`
  - ถ้าตรง 1 ใบ → **auto-select** + เติมยอดสูงสุดให้ → ผู้ใช้กดบันทึกได้เลย
    (กฎเหล็ก #3 spirit: ระบบเติมให้ครบ ผู้ใช้แค่ยืนยัน)
  - `fBookingNumber` มี `onchange/onblur` → re-render banner live เมื่อพิมพ์
- **DTO field**: `DepositSummary.BookingNumber` (เพิ่มล่าสุด — เดิมมีแต่
  `Reference`) populate ใน `GetDepositsAsync` (`DocumentService.cs:1278`)

---

## 4. ผลกระทบต่อระบบรายงาน (per document type)

| Type | JE on Approve | Stock | VAT report side | Tax point | Special |
| --- | --- | --- | --- | --- | --- |
| `Quotation` | ❌ | ❌ | – | – | – |
| `Invoice` | Dr AR / Cr Rev + Cr 21911 | ❌ (DN จัดการแยก) | output (เฉพาะถ้าไม่ใช่ "Cash basis" company) | `TaxPointDate` snapshot | – |
| `TaxInvoice` | Dr AR / Cr Rev + Cr 21911 | ❌ | output | `TaxPointDate` snapshot | – |
| `BillingNote` | ❌ (รอ Receipt) | ❌ | – | – | – |
| `Receipt` standalone | Dr Cash / Cr Rev + Cr 21911 | ❌ (Receipt **ไม่อยู่** ใน `ApplyStockMovementsAsync` switch — ถ้าต้อง OUT ต้อง issue Invoice/TaxInvoice ก่อน) | output | DocumentDate | nullable `RelatedDocumentId` — ถ้ามีอ้าง Invoice → ไม่ count VAT ซ้ำ |
| `ReceiptVoucher` standalone | เหมือน Receipt | ❌ (same as Receipt) | output | DocumentDate | รองรับ `IsDeposit` (2 เคส VAT ดู §3.7) |
| `DeliveryNote` | ❌ | ❌ (ตั้งใจไม่ trigger — Invoice ที่ตามมาจะ OUT ให้, กัน double-count) | – | – | ใช้คู่กับ Invoice ใน Quotation→DN→Invoice chain |
| `DebitNote` | Dr AR / Cr Rev + Cr VAT | ❌ | output (หรือ input ถ้า `RelatedDocumentId` เป็น purchase) | DocumentDate | บังคับมี `RelatedDocumentId` |
| `CreditNote` | Cr AR / Dr Rev + Dr VAT | IN เฉพาะ `Reason = Return` | output (หรือ input) | DocumentDate | บังคับ `CreditNoteReason` |
| `PurchaseRequisition` | ❌ | ❌ | – | – | internal commitment |
| `PurchaseOrder` | ❌ | ❌ | – | – | – |
| `GoodsReceiptNote` | Dr Inv / Cr GRNI (accrual) | IN | – | – | 3-way match prep |
| `PurchaseInvoice` | Dr Exp + Dr Input VAT / Cr AP | IN (ถ้าไม่มี GRN ก่อน) | input (11610 หรือ 11640) | TaxPointDate; 11640 → `InputVatBecameClaimableAt` | §86/4 completeness gate |
| `Expense` | Dr Exp + Dr Input VAT / Cr Cash/AP | ❌ (ยกเว้นมี product code) | input | TaxPointDate | §65 ตรี ที่ approve |
| `PaymentVoucher` | Dr AP/Exp / Cr Cash/Bank | ❌ | input (เฉพาะถ้าเปิด PV master switch + มีใบกำกับ) | DocumentDate | `PaymentType` (Cash/Credit) |
| `CertificateInLieu` | Dr Exp / Cr Cash | ❌ | input (§86/4 ถ้าครบ) | DocumentDate | บังคับ `CertReason + CertifierName` |

---

## 5. ทางออก (Output Channels)

### 5.1 PDF (มาตรฐาน + e-Tax)
- **Service**: `PdfGenerationService.GenerateDocumentPdfAsync` (`:39`)
- **Detection chain**:
  1. มี `EtaxInvoice` + `XmlContent` → **PDF/A-3 + embed XML** (ตาม
     ETDA ขมธอ.3-2560 — ใช้ยื่นภาษีได้)
  2. มี HTML template + Chromium headless → render HTML → PDF
  3. fallback: QuestPDF native (Thai-safe layout)
- รองรับ template per `DocumentType + IsDefault` flag
- ลายเซ็น/ลายน้ำ/QR/รหัส GL footer (toggle ต่อบริษัท)

### 5.2 e-Tax XML (XAdES-BES, RSA-SHA256)
- **Service**: `EtaxInvoiceService.GenerateAsync` (`:87`)
- **Trigger**: เอกสาร approve แล้วผู้ใช้กด "ออกใบกำกับ e-Tax"
  หรือบริษัทเปิด auto-sign
- **Types ที่ generate**: `Invoice` / `TaxInvoice` / `DebitNote` /
  `CreditNote` / `Receipt` (เฉพาะที่มี VAT)
- **Schema**: UBL 2.1 + ETDA profile (DocumentTypeCode T01–T04,
  ISO 8601 +07:00, CurrencyCode=THB)
- **Signing**: private key เก็บใน `Company.EtaxPrivateKeyBlobEncrypted`
  (AES-256 at rest); key ≥ 2048-bit; CA ที่ ETDA รับรอง
- **Storage**: `EtaxInvoice` entity → `XmlContent` + `EtaxRefNumber` +
  `CertificateSerialNumber` + `SignedAt`
- **Submission**: cron ส่ง batch ภายในวันที่ 15 ของเดือนถัดไป →
  `SubmittedToRdAt`

### 5.3 รายงานภาษี
| รายงาน | Service / Method | Source data |
| --- | --- | --- |
| **ภ.พ.30 (จอ + Excel)** | `TaxService.GenerateVatReport` (`:119`), export ผ่าน `TaxService.Export.cs` | `TaxReport.Lines` |
| **ภ.พ.30 (CSV ยื่น RD)** | `TaxFilingExportService.ExportPp30Async` (`:243`) — **ใช้ `ComputeVatReportAsync` ตัวเดียวกับจอ** (รวมแล้วใน commit ล่าสุด) | `ComputeVatReportAsync` → TaxReport ชั่วคราว |
| **ภ.พ.36** (ซื้อบริการ ตปท. self-assess VAT) | `TaxFilingExportService.ExportPp36Async` (`:566`) | Documents.IsForeignService |
| **ภ.ง.ด.1** (เงินเดือน WHT) | `ExportPnd1Async` (`:42`) | PayrollDetail |
| **ภ.ง.ด.3 / 53 / 54** | `ExportPnd3 / 53 / 54Async` | WithholdingTaxCert + DocumentLine.WHT |
| **ภ.ง.ด.50** (CIT รายปี) | `TaxService.GenerateCitReport` (`:731`) — **บวกกลับ §65 ตรี อัตโนมัติ** (commit ล่าสุด: ตัด rule (4) กัน double count) | JE Revenue/Expense + Document.NonDeductibleAmount |
| **ภ.ง.ด.51** (ครึ่งปี) | `ExportPnd51Async` | half-year P&L |
| **สปส.1-10** (ประกันสังคม) | `ExportSso110Async` (`:412`) | PayrollRun + Employee |

### 5.4 หนังสือรับรอง 50 ทวิ (WHT cert)
- **Service**: `WithholdingTaxCertService`
- **Trigger**: ตอน `PaymentVoucher` / `Expense` ที่มี WHT > 0 ถูก approve
  → auto-issue 2 ฉบับ ("สำหรับยื่นแบบ" + "เก็บไว้")
- **PDF**: `PdfGenerationService.WhtCert.cs`
- **DTA override**: ถ้า payee ต่างประเทศ + มี DTA → ใช้อัตรา bilateral
  แทน default 15%/10% (ม.70)

### 5.5 Integration ภายนอก (เอาออก)
- `GET /api/companies/{id}/documents/{id}` คืน `DocumentResponse`
  (รวม `IsForeignService` + ทุก field — เพิ่มล่าสุดให้ครบ)
- มี **`SensitivityKind`** gate: ถ้า caller ไม่มีสิทธิ์ดูเงินเดือน → คืน
  redacted stub (ไม่ 404) ผ่าน `RedactDocumentResponse` (`:5726`)

### 5.6 Stock / Fixed Asset register
- Stock: `StockMovement` row ที่ post ตอน approve — query ผ่าน
  `/inventory/movements`
- Fixed Asset: `FixedAsset` row สร้างอัตโนมัติ → ผู้ใช้กรอก
  `UsefulLifeMonths + DepreciationMethod` ในหน้า fixed-assets แล้วยืนยัน
  (`NeedsReview = false`) → schedule depreciation ปกติ

---

## 6. Cross-cutting

### 6.1 Audit log (tamper-evident)
- **Entity**: `AuditLog(actorId, entityType, entityId, before, after, at, ip, reason, prevHash, rowHash)`
- **append-only** — ห้าม UPDATE/DELETE
- Hash chain: `rowHash = SHA-256(prevHash + actor + entity + before + after + at)`
- write จุดสำคัญ: Create, Update (Draft), Approve, Void, Payment,
  WHT cert issue, e-Tax submission, DSR access

### 6.2 PDPA (ม.26 / ม.37 / ม.39)
- **Sensitive PII** (เลขบัตร 13 หลัก, salary, sensitive contact data) →
  AES-256 at rest, KMS key
- Display mask `1-XXXX-XXXXX-XX-3` — full value เฉพาะ role `pii:view`
- `PiiAccessLog` ≥ 1 ปี
- DSR endpoints `/dsr/access | rectify | erase | portability` SLA 30 วัน
  (cascade-erase ยกเว้น legal_hold ของ พ.ร.บ.บัญชี/สรรพากร — MAX retention)

### 6.3 §87/3 retention (5 ปี)
- ทุกเอกสารตั้ง `RetentionUntil = MAX(filingDate, reportDate, DocumentDate) + 5y`
- nightly job ห้ามลบจริง (soft-delete + flag `legal_hold`)
- e-Tax ที่ submitted แล้วยืดเป็น **7 ปี** (extended retention)

### 6.4 AI distillation (กฎเหล็ก #1) ที่ฝังใน flow

**Feature enum**: `AiFeatureKey` (`Models/Enums/AllEnums.cs:1188`) —
**ตารางนี้ verified ตรงกับ enum จริงในโค้ด**

| จุดเรียก AI | Feature key (enum) | Local model class | Round-trip feedback |
| --- | --- | --- | --- |
| OCR full review | `OcrFullReview = 22` | `GenericFeedbackDistillationModel` (register ใน Program.cs) | `SubmitCorrectionAsync` (OcrService) |
| ผังบัญชี GL ต่อบรรทัด | `GlAccountSuggestion = 2` | `GlAccountDistillationModel.cs` (4-tier: vendor+keyword exact → fuzzy → company-keyword ×0.85 → industry-keyword ×0.55) | `RecordLineAccountFeedbackAsync` ตอน approve |
| OCR document type label | `DocumentTypeClassification = 3` | generic | ตอน user แก้ในหน้า scan |
| OCR target doc to create | `DocumentConversionSuggestion = 23` | generic | ตอน user เปลี่ยน targetDocType |
| Vendor canonical match | `VendorCanonicalization = 1` | `VendorCanonDistillationModel.cs` | ตอน user เลือก contact |
| Buyer/Seller role infer | `DocumentRoleInference = 4` | generic | – |
| WHT category infer | `WhtCategoryInference = 5` | generic | ตอน user แก้ |
| Line item structured parse | `LineItemStructuredParse = 6` | – (ไม่มี student — heavy AI) | – |
| Approval warning fix | `ApprovalWarningFixSuggestion = 7` | `ApprovalWarningDistillationModel.cs` | – |
| Bank statement match | `BankStatementMatch = 8` | `BankMatchDistillationModel.cs` | ตอน user reconcile |
| Credit note reason | `CreditNoteReasonClassification = 9` | generic | ตอน user เลือก radio |
| Fuzzy duplicate doc | `FuzzyDuplicateDetection = 10` | `DuplicateDocumentDistillationModel.cs` | – |
| Anomaly explanation | `AnomalyExplanation = 11` | `AnomalyExplanationDistillationModel.cs` | – |
| Forecast narrative | `ForecastNarrative = 12` | – (essay) | – |
| Product match | `ProductMatch = 13` | generic | ตอน user เลือก product |
| Contact match | `ContactMatch = 14` | generic | ตอน user เลือก |
| Payment method suggest | `PaymentMethodSuggestion = 15` | generic | ตอน user แก้ |
| Currency + FX suggest | `CurrencyAndFxSuggestion = 16` | generic | ตอน user แก้ rate |
| Aging explanation | `AgingExplanation = 17` | – (essay) | – |
| Tax filing pre-check | `TaxFilingPreCheck = 18` | – (essay) | – |
| Stock movement validation | `StockMovementValidation = 19` | generic | – |
| **Bulk PV accounting** (ใบสำคัญจ่าย) | `PaymentVoucherAccountingSuggestion = 20` | bespoke (ใน prompts) | ตอน user save PV |
| Manual JE line suggest | `ManualJournalSuggestion = 21` | generic | ตอน user save JE |
| Reorder forecast | `ReorderForecast = 24` | local Croston/Holt-Winters | – |
| Bulk bank statement match | `BulkBankStatementMatch = 25` | bespoke | – |
| Import column match | `ImportColumnMatch = 26` | bespoke | ตอน user map |
| Import data review | `ImportDataReview = 27` | – (essay) | – |
| **Payment type** (Cash/Credit) | `PaymentTypeSuggestion = 28` | `PaymentTypeDistillationModel.cs` | ตอน user เปลี่ยน select |
| OCR project match | `OcrProjectMatch = 29` | generic | – |
| VAT type per line | `VatTypeInference = 30` | generic | ตอน user แก้ |
| Payment terms / credit days | `PaymentTermsSuggestion = 31` | – (pure lookup, ทุกครั้งผ่าน orchestrator) | ตอน user แก้ |
| Payment channel (แหล่งเงิน) | `PaymentChannelSuggestion = 32` | generic | ตอน user เปลี่ยน select |
| Project allocation per line | `ProjectAllocationSuggestion = 33` | generic | ตอน user เลือก project |
| Contact fuzzy match | `ContactFuzzyMatch = 34` | generic | – |
| Manual JE account suggest | `ManualJeAccountSuggestion = 35` | reuse `GlAccountDistillationModel` | – |
| Dimension allocation | `DimensionAllocationSuggestion = 36` | generic | – |
| Asset category suggest | `AssetCategorySuggestion = 37` | rule-based keyword (no AI by default) | ตอน user แก้ใน asset modal |

**Litmus test ก่อน commit**: ปิด provider ทุกตัว → feature ยังทำงานครบ 100%
(`AiProviderConfig.IsActive = false`)

---

## 7. Validation gates (compliance — กฎเหล็ก #2)

| Gate | Where | กระทำ |
| --- | --- | --- |
| §86/4 completeness (PI/Expense/PV) | `TaxInvoiceCompletenessChecker` | ถ้าไม่ครบ → input VAT ลง 11640 (undue) |
| §82/5 prohibited input VAT | `ChartOfAccount.InputVatClaimable` + per-line `IsVatClaimable` | flag claim=false, แยกออกจาก ภ.พ.30 + แสดง "🚫 §82/5" line |
| §82/3 6-month window | `TaxFilingExportService.ExportPp30Async` + `GenerateVatReport` | เกิน 6 เดือน → block claim หรือ require `LateReason` |
| §86/9–86/10 CN/DN | `CreditNote/DebitNote` flow | required `RelatedDocumentId` + `CreditNoteReason` (CN); cap ≤ original |
| §78 / §78/1 tax point | `TaxPointResolver` | snapshot ตอน approve |
| §65 ตรี รายจ่ายต้องห้าม | `Section65TerValidator` | `NonDeductibleAmount + RuleJson` → ภ.ง.ด.50 |
| §87(3) chronological | ExportPp30Async summary | นับ doc ที่ tax point ย้อนกลับ → surface ใน Summary.csv |
| §87/3 retention 5 ปี | `RetentionUntil` | ห้าม hard delete; soft + legal_hold |
| §85/1 VAT threshold 1.8M | annual revenue check | warning "ต้องจด VAT ภายใน 30 วัน" |
| WHT 50 ทวิ ≤ threshold 1,000 | `CheckWhtThresholdAsync` | ไม่หักถ้ารวมสัญญา < 1,000 |
| DTA override | WHT cert PDF | bilateral rate แทน ม.70 default |
| OCR auto-fill ครบ (กฎเหล็ก #3) | `OcrService.CreateDocumentFromScanAsync` | ทุก §86/4 field ต้อง pre-filled ก่อนเปิดฟอร์ม |

---

## 8. Quick reference — โค้ดอยู่ไหน

| ต้องการทำอะไร | ไปดูที่ |
| --- | --- |
| เพิ่ม `DocumentType` ใหม่ | `Models/Enums/AllEnums.cs:305` + `DocumentService.cs` หลายจุด (search by enum literal) |
| แก้ flow Approve | `DocumentService.ApproveDocumentAsync :1512` |
| แก้ flow JE per type | `DocumentService.AutoPostToJournalAsync :4684+` |
| แก้ stock movement | `DocumentService.ApplyStockMovementsAsync :4605–4682` |
| แก้ §65 ตรี rule | `Services/Implementations/Tax/Section65TerValidator.cs` |
| แก้ tax point logic | `Services/Implementations/Tax/TaxPointResolver.cs` |
| แก้ §86/4 completeness | `Services/Implementations/Tax/TaxInvoiceCompletenessChecker.cs` |
| แก้ fixed asset auto-register | `DocumentService.AutoRegisterFixedAssetsAsync :4477` |
| แก้ผัง 11640 ↔ 11610 reclassify | `DocumentService.ReclassifyUndueInputVatAsync :1137` |
| แก้ deposit Realize/Refund/Apply | `DocumentService.cs` ค้นหา `RealizeDepositAsync` / `RefundDepositAsync` / `ApplyDepositToInvoiceAsync` |
| แก้รายงาน ภ.พ.30 (จอ) | `TaxService.GenerateVatReport :119` |
| แก้รายงาน ภ.พ.30 (CSV ยื่น) | `TaxFilingExportService.ExportPp30Async :243` — ดึงจาก `ComputeVatReportAsync` |
| แก้ ภ.ง.ด.50 | `TaxService.GenerateCitReport :731` |
| แก้ WHT cert auto-issue | `WithholdingTaxCertService` |
| แก้ PDF template | `PdfGenerationService.DocumentRenderer.cs` / `HtmlRenderer.cs` |
| แก้ e-Tax XML | `EtaxInvoiceService.GenerateAsync :87` |
| แก้ OCR pipeline | `OcrService.cs` (3,800+ บรรทัด) — `ProcessScanAsync`, `CreateDocumentFromScanAsync` |
| แก้ AI orchestration | `Services/Ai/AiOrchestrator.cs` |
| แก้ local distillation | `Services/Ai/Distillation/*` (per feature) |

---

## 9. หลักการสำคัญที่ต้องไม่ลืม

1. **DocumentNumber ออกตอน Approve เท่านั้น** — Draft = `DRAFT-{guid}` กัน gap (§86/4)
2. **ห้ามแก้ Approved เอกสาร** — ใช้ Void แล้วออกใหม่ (§86/4 ห้ามแก้ย้อนหลัง)
3. **TaxPointDate snapshot ตอน approve** — ห้ามคำนวณ on-the-fly (กัน period leak)
4. **Tenant isolation** — ทุก query ต้องมี `CompanyId == @companyId` — ไม่มีข้อยกเว้น
5. **Soft delete + legal_hold** — ใน 5 ปีห้าม hard delete (§87/3 + ม.10)
6. **Audit log append-only + hash chain** — tamper-evident
7. **กฎเหล็ก #1 Distillation Mandate** — ทุก AI call ผ่าน `IAiOrchestrator.AskAsync`,
   capture FeedbackId, ปิดลูปด้วย `RecordUserChoiceAsync`
8. **กฎเหล็ก #2 Thai compliance** — ทุก feature ผ่าน checklist §86/4, §82/5,
   §65 ตรี, PDPA ก่อน commit
9. **กฎเหล็ก #3 OCR auto-fill** — ทุก §86/4 field ต้อง pre-filled ก่อนผู้ใช้
   เห็นฟอร์ม — เป้าหมาย = 1-click approve
10. **Single source of truth for ภ.พ.30** — `ComputeVatReportAsync`
    ตัวเดียวสำหรับจอ + Excel + CSV ยื่น (รวมแล้วล่าสุด — กัน drift)

---

_Last verified against codebase: 2026-06-22 — รอบ 3 (เพิ่ม UX force-review_
_fixed asset หลังอนุมัติ PV/PI/Expense + UX apply deposit จาก TaxInvoice/Invoice)._
_Files referenced are accurate; if behavior diverges, this doc is wrong —_
_update it in the same PR (CLAUDE.md §"DOCUMENT_FLOW.md" hard requirement)._
