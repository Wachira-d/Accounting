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
                            ↓   ↑ (restore กู้เอกสารยกเลิกผิด)
                          Voided / Rejected / Overdue
```
- **`Draft`** = `DocumentNumber = "DRAFT-{guid}"` placeholder (กัน gap §86/4)
  — *ยกเว้น* เอกสารที่ restore จาก Voided: Draft แต่ถือ **เลขจริงเดิม**
  (re-approve คงเลข ไม่ regenerate)
- **`Approved`** = ออกเลขจริง + post JE + snapshot tax point + stock move
- **`Voided`** = reverse JE + reverse stock; เก็บไว้ดู audit (ห้าม hard delete).
  กู้คืนได้ด้วย `RestoreVoidedDocumentAsync` → กลับเป็น Draft (คงเลข) ถ้ายังไม่
  ยื่นภาษี/ไม่มี e-Tax accepted (ดู §3.6 Void/Restore)
- **`Sent`** = email ออกแล้ว (optionally e-tax-by-email + RD cc)

---

## 2. ทางเข้า (Entry Points)

### 2.1 สร้างมือ (ผู้ใช้)
- **Endpoint**: `POST /api/companies/{companyId}/documents`
- **Controller**: `DocumentController.cs:165` → `DocumentService.CreateDocumentAsync` (`DocumentService.cs:279`)
- **กฎ**:
  - ตั้ง `Status = Draft`, `DocumentNumber = "DRAFT-{guid}"`
  - resolve `AccountCode → AccountId` ต่อบรรทัด (รองรับ AI suggest) — **รหัสที่ resolve
    ไม่ได้ต้องล้มดัง** (`ValidateDocumentLinesAsync` → `BusinessRuleException` ระบุรหัส +
    บรรทัด) · เดิม resolver คืน null เงียบ ⇒ `AccountId=null` ⇒ JE ตกไปใช้ผังตามหมวด
    หัวเอกสาร ⇒ ผู้ใช้เห็นว่า “แก้ผังแล้วกดบันทึกอนุมัติ ระบบไม่เปลี่ยน” โดยไม่มี error
    (2026-09-10). ฝั่งฟอร์ม `documents.html`: เปลี่ยน "หมวดค่าใช้จ่าย" ระดับหัวเอกสาร
    → เติมผังให้บรรทัดที่ผู้ใช้**ยังไม่แตะ** (`dataset.userTouched`) · ก่อน save ถ้า
    หัวกับบรรทัดไม่ตรงกันจะถาม 1 ครั้ง (ใช้หัวทับทุกบรรทัด / คงรายบรรทัด) — JE ลงตาม
    **บรรทัด** เสมอ หัวเป็นแค่ตัวเติมค่าเริ่มต้น
  - validate VAT-claimability ต่อบัญชี (`ChartOfAccount.InputVatClaimable`)
  - compute line amounts (`ComputeLineAmounts`) รองรับ `PricesIncludeVat`
  - เก็บ `GlAccountAiFeedbackId` ต่อบรรทัด (ปิดลูปการสอน local model)
  - ยังไม่สร้าง JE / ยังไม่กระทบ stock จนกว่าจะ Approve

### 2.2 OCR (สแกนเอกสาร)
- **Flow**: อัปโหลด → OCR เก็บ `OcrScanResult` → review modal → "สร้างเอกสาร"
- **Endpoint**: `POST /api/companies/{id}/ocr/{scanId}/create-document`
  (`?targetType=` เลือกชนิด · `?approve=true` = **สร้าง + อนุมัติทันที**)
- **ผูกใบต้นทางทุกชนิด (2026-09-10 · `Helpers/OcrPredecessorMatcher`)** — ตอนสแกนเสร็จ
  `SuggestPredecessorLinkAsync` หา "เอกสารเปิดอยู่ของคู่ค้าเดียวกัน" ที่มีชนิดอยู่ใน
  `DocumentService.GetPredecessorTypes(target)` (ด้านกลับของ `ValidConversions` — PO/GRN → ใบซื้อ ·
  ใบเสนอราคา/ใบวางบิล/ใบส่งของ → ใบแจ้งหนี้ · ใบแจ้งหนี้/ใบกำกับ → ใบเสร็จ ฯลฯ; ฝั่งขายหาลูกค้าจาก
  `BuyerTaxId`) แล้วตัดสินด้วยตัวเดียว: **เลขที่เดิมพิมพ์บนกระดาษ** (ใบเดียว ⇒ ผูกอัตโนมัติ · หลายใบ
  ⇒ ให้เลือก) → **ยอดรวมตรง ±1 บาท** กับ Total หรือยอดค้าง (ใบเดียว ⇒ ผูกอัตโนมัติ · หลายใบ ⇒ ให้เลือก
  ห้ามเดา) → **รายการคล้าย** (เสนอเท่านั้น). ผลลง `OcrScanResult.LinkedPredecessor*` +
  `PredecessorCandidatesJson` (เซิร์ฟเวอร์ให้ Strength/Reason — หน้าเว็บแสดงอย่างเดียว) · ต้นทางเป็น PO
  ⇒ ตั้ง `LinkedPurchaseOrderId` ด้วย (เส้นสืบทอด GL รายบรรทัดเดิม). ตอน `CreateDocumentFromScanAsync`
  เอกสารใหม่ได้ `RelatedDocumentId = linkedPo ?? linkedPred` (ฝั่งขายใช้ `ContactId` ของใบต้นทางก่อน
  อ่านชื่อผู้ซื้อจากกระดาษ) · ถ้าผู้ใช้เปลี่ยนชนิดเป้าหมายจนต้นทางไม่ใช่ชนิดที่แปลงมาได้ → ไม่ผูก +
  `[LINK]` ใน Notes (ไม่ผูกผิดคู่เงียบ). Endpoint: `GET {scanId}/predecessor-candidates` (คำนวณสด) ·
  `POST/DELETE {scanId}/link-predecessor` (ตรวจชนิด + คู่ค้าเดียวกัน · PO เดินผ่าน `link-po` เดิม).
  เดิมมีแค่ PO→ใบซื้อ และผูกอัตโนมัติเฉพาะเมื่อเลข PO อยู่บนกระดาษ (ผู้ใช้: "PO บุญทรัพย์ 599 กับ
  ใบแจ้งหนี้ 599 ต้องผูกให้เอง")
- **โซนผู้ขาย/ผู้ซื้อ · วันที่ · สาขา · เล่มที่ (รอบ 190 — เพิ่มขั้น ไม่รื้อ · ตัวตัดสินทุกตัวเป็น pure helper มีเทสต์ข้อความกระดาษจริง)**:
  - **วันที่** — `Helpers/OcrDateReader.CrossCheck` เป็นขั้นตรวจท้ายสุดของ**ทุก engine** (ก่อนด่านคณิต · ข้าม e-Tax XML):
    วันที่ที่มีป้าย "วันที่/Date" ชนะวันที่ลอย · ป้ายชนิดอื่น (ครบกำหนด/Due) ไม่ใช่วันที่ใบ · ตีความแบบไทย (วัน/เดือน/ปี)
    ก่อน · ปีผ่าน `ThaiDate.NormalizeYear` · ค่าที่ทับ engine ได้คะแนน 0.80 (ไฮไลต์) ⇒ **วันที่เอกสารจากสแกน = ค่าหลังตรวจกับ
    กระดาษ** ซึ่งกำหนด tax point/งวด ภ.พ.30 ของใบที่สร้างจากสแกน
  - **ที่อยู่ผู้ซื้อ** — `Helpers/OcrBuyerAddressReader` (ใน `EnrichFromRawText` · ทุก engine · เติมเฉพาะช่องว่าง) ยึดป้ายฝั่งผู้ซื้อจาก
    `OcrPartyLabels` · invariant: **ที่อยู่ผู้ขายที่เป็นส่วนหนึ่งทั้งก้อนของบล็อกผู้ซื้อถูกล้าง** (เดิม regex หยิบ "Address" ตัวแรกของหน้า
    = ที่อยู่เราไปอยู่ช่องผู้ขาย และถูกสอนเข้าคลัง known-good)
  - **ชื่อฝั่งเรา** — `OcrPartyResolver.FillOurName`: รู้ฝั่งเรา ≥ 0.85 แล้วชื่อในบล็อกเราเป็น**รหัส/ว่าง** (+ เลขภาษีหรือชื่อเราบนกระดาษ)
    หรือเป็นชื่ออื่น**ขณะที่ชื่อเราพิมพ์อยู่บนกระดาษ** ⇒ ใช้ `Company.Name` · ชื่อเดิมลง Reasons · ป้ายสาขาท้ายชื่อ ("(สำนักงานใหญ่)" ·
    "Branch 00012") ถูกตัดด้วย `OcrPartyName.StripBranchSuffix` ทั้งสองฝั่ง
  - **ที่อยู่ผู้ขายขาดเลขบ้าน** — `OcrPartyAddress.StripLeakedNameFragment` ไม่ตัดเศษที่มีตัวเลขอีก (ตัวเลขไม่มี "กลางคำ") + migration ลบ
    known-good `VendorAddress` ที่ขึ้นต้น `/เลข`
  - **สาขาผู้ออกใบ** — `Helpers/OcrIssuerBranch`: ประโยคประกาศ "สาขาที่ออกใบกำกับภาษีคือ สาขาที่ N" ชนะ 00000/ว่าง (รหัสอื่น = Conflict
    ไม่ทับ + ไฮไลต์) · ที่อยู่ที่พิมพ์ต่อจากประโยคนั้น (ไทยก่อน) ใช้เป็นที่อยู่ผู้ขาย · Contact สำนักงานใหญ่ไม่รับที่อยู่สาขา ·
    `BranchCodeExtractor` ใช้ `OcrPartyLabels.FindBuyer` แยกบล็อกผู้ซื้อเมื่อรายการคำเดิมหาไม่เจอ
  - **เล่มที่/เลขที่** — `Helpers/OcrBookSerial.Combine` รวมเป็น `เล่ม/เลขที่` (เช่น `066/3267`) เฉพาะเมื่อเลขที่ของ engine ตรงคู่ที่พบ
  - **ผู้ติดต่อตามสาขา** — `Helpers/OcrVendorBranchContact` จับแถวผู้ติดต่อด้วย (เลขภาษี + สาขา) · ไม่มีแถวสาขานั้น ⇒ ผูกแถวเดิม
    (สาขาของใบอยู่ที่เอกสาร) **ไม่สร้างแถวใหม่เอง** (รอเจ้าของตัดสิน) · ที่อยู่สาขาจากทะเบียน VAT ผ่าน
    `IDbdLookupService.GetBranchAsync` + `Helpers/RdVatBranchRecords`
  - **วันที่ที่ระบบเดา/ทับ/สงสัย** (`OcrDateReader.NeedsHumanConfirm` — ความมั่นใจ < 0.85 และไม่ใช่ "ตรงกับป้าย") ⇒ แท็ก
    `[DATE-UNSURE]` ใน ProcessingNotes ⇒ `OcrPostingReadiness` ห้ามอนุมัติอัตโนมัติ (ทุกช่องทาง) — ฝ่ายค้านรอบ 190: ไม่งั้นวันที่
    ที่เติมจากตัวเลขลอย ๆ ทำให้ `[DATE-UNKNOWN]` ไม่เกิด แล้วใบลงงวด ภ.พ.30 ผิดเงียบ
  - **ยึดยอดรวมทั้งสิ้นก่อน (Total-first · รอบ 192)** — `Helpers/OcrTotalAnchor` รันก่อนด่านคณิต (ทุก engine ยกเว้น e-Tax XML ที่ลงนาม)
    และซ้ำที่ต้น `BuildScanLinesAsync` (สแกนเก่าได้ผลด้วย): ไล่ทุกตัวเลขที่อาจเป็นยอดรวม แล้วนับ**ชั้นหลักฐานอิสระ** (ป้ายยอดรวม ·
    จำนวนเงินตัวอักษร · แถวชำระ · ตารางสรุปตามรหัส ภ.พ. · ฐาน+VAT ที่พิมพ์ · VAT = 7/107) — **บรรทัดกระดาษเดียวนับชั้นเดียว** ·
    เขียนทับยอดของ engine **เฉพาะเมื่อพิสูจน์ได้** และค่าที่แพ้ต้องอธิบายบทบาทได้ (ยอดก่อนส่วนลด · ยอดที่จ่าย · หัก ณ ที่จ่าย ·
    ปัดเศษที่มีแถวพิมพ์) · **นิยาม `Document.TotalAmount` = ยอดใบกำกับ (ยอดที่ VAT บนกระดาษคิดมาจาก) ไม่ใช่ยอดที่จ่าย**
    (Makro ป้าย "TOTAL 24,110" = ก่อนส่วนลด ⇒ ยอดจริง 23,812.25) · ฐานที่คำนวณ (ไม่ได้พิมพ์) = Rule 0.80 ไม่ใช่ "พิมพ์บนกระดาษ" ·
    ใบสองสกุลเงิน/ตัวเลขของใบเดิม (ใบลดหนี้) = Unknown ไม่ใช่ขัดแย้ง
  - **แยกองค์ประกอบให้รวมได้ยอดนั้น** — `Helpers/OcrTotalDecomposer` ทดสอบสมมติฐานกับ VAT ที่พิมพ์: ส่วนลดก่อน VAT · ส่วนลดรวม VAT ·
    **ปรับตอนชำระหลังยอดใบกำกับ** (ใบ Shopee "ส่วนลดพิเศษ 98" หลัง "จำนวนเงินรวมทั้งสิ้น 536" ⇒ ไม่ลดฐาน/VAT · ไม่กระจายลงบรรทัด ·
    แท็ก `[PAY≠TOTAL]` **บล็อกอนุมัติเอง** จนกว่าส่วนต่างถูกลงที่ขั้นชำระ (รอบ 193 — ดูข้อ "ยอดชำระจริง" ข้างล่าง) — เขียนซ้ำตอนสร้าง/repopulate ให้สแกนเก่าด้วย) ·
    ตารางสรุปรหัส ภ.พ. แบบตัวเลข (`OcrLineVatMarks.ReadGroups` · รหัสอ่านจากคำอธิบายบนกระดาษ · ไม่มีคำอธิบาย = Unknown) ชนะการ
    เฉลี่ยส่วนลด · ไม่มีรายการ ⇒ บรรทัดสรุป**ต่อกลุ่มภาษี** + `[PAGES-PARTIAL]` (ไม่บล็อก) · back-calc 7/107 ทั้งสองชุดห้ามขัดกับ
    VAT ที่พิมพ์ (`VatBackCalcGuard.PrintedVatContradicts`) · ยอดบรรทัดที่พิมพ์ชนะราคาต่อหน่วย×จำนวน (±0.01) ·
    `[TOTAL-CONFLICT]` (บล็อก) เมื่อสองยอดพิสูจน์ได้แต่อธิบายกันไม่ได้ · แท็กตัดสินทุกตัวถูกคัดลอกไปแถวสำเนาเมื่ออัปโหลดซ้ำ
    (`OcrScanSnapshot.DecisionNoteTags` ⊇ `OcrPostingReadiness.BlockingTags`)
  - **ส่วนลดท้ายบิล / VAT ผสม (ทีม M)** — ตัวอ่านส่วนลดตัวเดียว `Helpers/OcrBillDiscount` (เดิมหยิบ "ส่วนลด" ตัวแรกของหน้า ⇒ ได้
    "ยอดหลังหักส่วนลด" เป็นส่วนลด) · ด่านคณิตรู้ส่วนลด (ส่วนลดที่อธิบายส่วนต่างพอดี = ใช้ฐานหลังลด) · `Document.SubTotal` จากสแกน =
    ยอด**หลัง**ส่วนลด (`OcrHeaderAmounts.NetSubTotal` ทั้งสร้าง/พรีวิว/repopulate — เดิมก่อนลด ⇒ ฐานภาษีซื้อ §87 เกิน) ·
    `Document.DiscountAmount` = Σ ส่วนลดที่ลงบรรทัดจริง · `OcrLineReconciler` เคส E `DiscountOnTotalInclVat` (ราคารวม VAT + ส่วนลด) ·
    อัตรา VAT รายบรรทัดจากสัญลักษณ์บนกระดาษ (V/N · ตารางสรุป VAT) **ก่อน**ตัวเดาจากชื่อสินค้า — ใช้เฉพาะเมื่อยอดบนกระดาษพิสูจน์ได้
    (`Helpers/OcrLineVatMarks`) · ด่าน `Helpers/OcrAmountIntegrity` ตรวจบรรทัดที่จะเขียนจริง (ติดลบ · Σ ยอด · Σ VAT · อัตรา×ยอด
    คำนวณอิสระ) ⇒ `[Σ-GAP]` พร้อมตัวเลข · ไม่แก้ตัวเลขใด ๆ · ปุ่ม LINE postback ตรวจ readiness ซ้ำตอนกด · หน้ารีวิวแบนเนอร์แดง
    พร้อมตัวเลขก่อนกดสร้าง (เดิมป้ายเขียว "ผลรวมตรง" โดยไม่ดูผลตรวจ)
  - **local เรียนจาก Azure** — ที่อยู่ผู้ขายที่ว่างเติมจากคลัง known-good ได้ (ด่าน `OcrKnownGoodAddressFill`) · ช่องอื่นยังไม่เรียน
    (สถานะจริงอยู่ใน `erp-review/2026-09-24/team-L.md` §0)
- **คำตัดสินเจ้าของ 37 ข้อ — ส่วน OCR (รอบ 193 · ทีม O1/O2 · `erp-review/2026-09-24/DECISIONS.md`)**:
  - **ยอดชำระจริง ≠ ยอดใบกำกับ (#1/#3/#4)** — `Helpers/OcrSettlementProposal.FromDecomposition` (PostInvoice เท่านั้น · ป้ายไม่รู้จัก = ไม่เดาผัง) → หมายเหตุ
    `[PAY-PLAN]` (คู่ `Note`/`Parse` ตัวเดียว · พาข้ามการอัปไฟล์ซ้ำ) · `ApplyScanSettlementPlanAsync` (ก่อน `_db.Documents.Add`): **PV จ่ายในตัว** ⇒ ตั้ง
    `ActualPaidAmount` + `DocumentAdjustingJournalLine` ตามข้อเสนอ + `[PAY-SETTLED]` (ปลด**เฉพาะ** `[PAY≠TOTAL]` · ไม่พาข้ามอัปซ้ำ) · **ใบตั้งหนี้** (PI/Expense) ⇒
    `ActualPaidAmount` + `[PAY-AT-PAYMENT]` (`DeferredNote`) **เฉพาะเมื่อ `FitsDocument`** (มีข้อเสนอ · ไม่มี WHT · ยอดใบกำกับในข้อเสนอ = ยอดเอกสาร ±
    `MatchTolerance`) — ไม่ผ่าน ⇒ คง `[PAY≠TOTAL]` + `[Σ]` จาก `UnverifiedReason` · ใบตั้งหนี้แบบจ่ายทันที ⇒ ไม่ตั้งค่า/ไม่เขียนแท็ก · มี WHT/ยอดไม่ตรง/ผังขาด ⇒
    ไม่ลง + `[Σ]` บอกเหตุ · การชำระ = §3.4
  - **ใบไม่มีรายการ (#5)** — บรรทัดสรุปต่อกลุ่มภาษี **ยอมรับ** + `[NO-ITEMS]` (`OcrTotalDecomposer.NoItemsTag/NoItemsNote` ถ้อยคำเจ้าของ · **ไม่อยู่ใน** `BlockingTags`) ·
    แสดงในการ์ดสแกนและคำเตือน LINE
  - **ราคาต่อหน่วยไม่ลงตัว (#8)** — คงราคาที่พิมพ์ + ผลต่างปัดเศษระดับเอกสาร (§6.2j)
  - **ผู้ซื้อบนกระดาษ (#9)** — `Helpers/OcrBuyerOnPaper` ⇒ §82/5(1) (§7)
  - **e-Tax XML ใน PDF = หลักฐานอันดับหนึ่ง (#10)** — `Services/Implementations/Ocr/EtaxPdfXmlExtractor.cs` แก้ 7 ช่องที่ทำให้ "ตกไป OCR เงียบ": Filespec ตัวแรก
    ไม่ใช่ .xml แล้วโยน · ชื่อไฟล์ hex UTF-16 (PDFKit) · regex ชี้ Filespec ไม่เคยแมตช์ · ตัดสตรีมผิดเมื่อ dict มี `octet-stream` · ยอดบรรทัดใน
    `NetLineTotalAmount` · BOM · `T03` = ใบเสร็จ/ใบกำกับ (เดิม map เป็น Receipt) — ตาราง ETDA ครบ · ยอด XML เป็นตัวตั้ง (ไม่ผ่านขั้นยึดยอด) · ข้อความหน้า PDF
    (`PdfTextLayerExtractor`) ใช้หา**เฉพาะ**การปรับตอนชำระที่ XML ไม่มี (`ApplyEtaxSettlement`) ⇒ ใบ Shopee XML ได้ `[PAY≠TOTAL]` + `[PAY-PLAN]` เหมือนใบ PDF ·
    T05/T06 ⇒ §82/5(2) (§7) · fixture จริง `erp-review/2026-09-24/fixtures/` (ยังไม่มี PDF จริงของ Shopee)
  - **`[Σ-GAP]` ตอนอนุมัติด้วยมือ (#12)** — §3.2 (คนกดรับทราบ · API ไม่ขัดจังหวะ)
  - **ข้อมูลเก่าที่ผิด (#14)** — **รายงานเท่านั้น ไม่แก้หลังบ้าน**: `GET /api/ocr/amount-audit` (`OcrService.GetStoredAmountAuditAsync` · take 1..1000 · ตัดแถวที่เปิดไม่ได้) +
    `pages/ocr-amount-audit.html` · ตัวตัดสิน `Helpers/OcrStoredAmountAudit` (ชุดเดียวกับไปป์ไลน์) 4 ชนิด: ยอดสแกน ≠ ยอดกระดาษที่พิสูจน์ได้ · ส่วนลดที่เป็นการปรับตอนชำระแต่
    เอกสาร**ลดยอด**ตามไปแล้ว · ฐานหัวเอกสาร ≠ Σ บรรทัด (+ปัดเศษ) · ยอดรวม ≠ บรรทัด + VAT − WHT · `undefined` = "ไม่ได้รับผลตรวจ" ≠ "ไม่พบปัญหา"
  - **วันที่ใบอังกฤษล้วน + สกุลต่างประเทศ (#22)** — `OcrDateReader.CrossCheck`: engine สมเหตุสมผล + `IsForeignEnglishPaper` (ไม่มีอักษรไทย · มีละติน ·
    `OcrPaperAmounts.HasForeignCurrency`) ⇒ **เชื่อลำดับของ engine** (Confirmed) · ใบมีไทยปน/เงินบาท = แบบไทยก่อนเหมือนเดิม · python fallback ใช้
    `ocr-service/app/date_reader.py` ใหม่ (#27 — ต้อง deploy image ใหม่)
  - **ที่อยู่ผู้ซื้อจากข้อมูลบริษัทเรา (#26)** — `Helpers/OcrOurAddressFill.ForBuyer`: ผู้ซื้อ = เรา ≥ 0.85 + ช่องว่าง (หลัง `OcrBuyerAddressReader`) ⇒
    `Company.Address` ความมั่นใจ **0.70** (สีเหลือง + เหตุผล §86/4) · ห้ามทับที่อยู่ที่พิมพ์ · สาขาผู้ซื้อบนกระดาษเป็นสาขาอื่นของเรา ⇒ ไม่เติม · ข้าม e-Tax XML
  - **ที่อยู่ผู้ขาย (#15)** — ตรงแล้ว ไม่แก้: `VendorAddress` = ที่พิมพ์ (หลักฐาน) · ทะเบียนลง `DbdAddress` แยกช่อง · `Contact.Address` ผ่าน `OcrIssuerBranch.ContactAddress` ·
    ค้นทะเบียน RD แยกสาขาทั้งแถว (#18 · §6.2i)
  - **คิวรอตรวจ (#31/#32 · ทีม U2)** — `GET ocr/review-queue` คืน `{ items, drafts, summary }`: **ร่างที่สร้างจากสแกน** (`ActiveLearningRanker.DraftsFromScansAsync` ·
    กรองด้วยสิทธิ์มองเห็นชุดเดียวกับลิสต์เอกสาร · กดแล้วไป `documents.html?openDoc=`) + **ทุกใบในงวดที่ยังไม่ปิด** (ไม่ตัดที่ 30 วัน · `Helpers/ClosedPeriodRanges` —
    `FiscalPeriod.Status ∈ {Closed, Locked}` · ไม่มีแถวงวด = เปิด · ช่วงครึ่งเปิด `[Start, EndDate+1วัน)`) · นิยาม "อยู่ในคิว"/"วันที่ทางบัญชีของสแกน" เป็น expression
    ตัวเดียว · เพดาน 1,000 ใบ · `QueueSummary` บอก `InClosedPeriods` (ไม่หายเงียบ) · ⚠️ กติกา "งวดปิด" ยังมี private copy 3 ชุด (Accounting/Bank/Integration)
- **ทางออกจาก review modal 4 ทาง**: `📝 ยืนยันในฟอร์ม` (แนะนำ — ผ่าน
  sessionStorage handoff เข้า `documents.html`) · `สร้างทันที` (Draft) ·
  `⚡ สร้าง + อนุมัติ` · `🧾 JE เท่านั้น`. ทุกทางที่สร้างสำเร็จ **ปิด modal +
  refresh รายการ** (เดิมค้างที่ modal เฉย ๆ) และการ์ดเปลี่ยนเป็น `สร้างแล้ว →`
  ที่ deep-link `documents.html?openDoc={id}` (เดิม `?id=` ซึ่งหน้านั้นไม่เคย
  อ่าน ⇒ พาไปหน้ารวมเปล่า ๆ). **อนุมัติไม่ผ่าน ≠ ล้มทั้งก้อน** — ใบ Draft ยังอยู่
  แล้วแนบเหตุผลกลับมาเป็น `[APPROVE-FAIL]` (ถ้า throw ทิ้ง ผู้ใช้จะเข้าใจว่า
  ไม่มีใบเกิดขึ้นแล้วสแกนซ้ำ = ใบซ้ำ)
- **จุด serialize `ExtractedItemsJson` มีจุดเดียว** (รอบ 183 · D3-1) —
  `OcrService.SerializeExtractedItems` เรียกจาก **จุดเดียวในไปป์ไลน์** ซึ่งอยู่
  **หลัง** ตัวเทียบสินค้าใน master + ตัวเรียนรายบรรทัด และ **ก่อน**
  `SuggestPredecessorLinkAsync`/`AutoCreateDocumentAsync`
  _เดิม serialize ที่ต้นไปป์ไลน์ด้วย projection 8 ช่อง ⇒ `SuggestedAccountCode`
  · `VatRate` · `ProjectAiFeedbackId` ที่ชั้นหลังเขียน **หายทุกใบ** ⇒ เอกสารได้
  ผังบัญชีระดับ**หัวใบ**ทั้งที่ trace บนจอเขียนว่า "[Product] รายการ → บัญชี"
  (ชั้นหลักฐานที่แข็งที่สุดไม่เคยถึงเอกสาร)_
  · ลำดับชั้น "บรรทัดนี้ลงบัญชีอะไร" อยู่ที่ **`Helpers/OcrLineAccountSource`**:
  บรรทัด PO ที่ผูกไว้ → บรรทัดสแกน → หัวใบ → ไม่มี (ห้ามเขียน `??` เรียงกันเอง)
  · ⚠️ แถวที่ persist ก่อนรอบนี้ยัง `VatRate = null` — อ่านได้ปกติ แต่ถ้าจะซ่อม
  ย้อนหลังต้องมี migration (ยังไม่ทำ)
- **`[MATH]` บล็อกการอนุมัติอัตโนมัติ** (รอบ 183 · D3-3) — `!MathConsistent`
  (ยอดหัวใบไม่ลงตัว / สามช่องขัดกัน / Σ บรรทัดไม่ตรงหัวใบ) เขียนแท็ก `[MATH]`
  ลง `ProcessingNotes` และแท็กนี้อยู่ใน `Helpers/OcrPostingReadiness.BlockingTags`
  ⇒ **ทุกช่องทาง** (เว็บ · LINE · มือถือ) ห้ามอนุมัติเองจนกว่าคนจะดู
  · และ `VendorPrediction` **เลิกยกคะแนนทั้งใบ** (`Confidence = Math.Max(…, prior)`)
  — prior ของผู้ขายประจำเคยดันใบที่ตัวเลขไม่ลงตัวกลับขึ้นเกณฑ์ auto-create
- **ด่านคุณภาพก่อน persist** (ทั้ง 4 ทางออกได้ผลเดียวกัน — ตรรกะอยู่ที่
  scan-time ก่อน serialize `ExtractedItemsJson`):
  1. **`DocumentNumberSanitizer`** — เลขที่เอกสารต้องปรากฏบนกระดาษจริง;
     ผ่าออกเป็น 2 เลขที่อยู่บนกระดาษทั้งคู่ = LLM ต่อเลขกัน → เลือกเลขที่หลัก
     (ป้าย "เลขที่/No." ชนะ "เลขที่ใบแจ้งหนี้/สัญญา/เครื่องวัด") · อ่านเพี้ยน
     (O↔0) = **ไม่เดาแก้** _(ที่มา: บิล กฟภ. ได้ `XH0712608004488` + `510504578045`
     ต่อกัน — เลขที่ผิด = ตามใบไม่เจอ + dedup ไม่มีวันจับใบซ้ำ)_
  2. **`SanitizeVatSplitArtifacts` ขั้น reconcile** — แยก 2 เคสที่เคยปนกัน:
     `UnitPrice ≈ Amount` ทั้งที่ `qty > 1` และ qty ไม่ใกล้ยอดเงิน = ช่องราคา
     ถือ "ยอดรวมบรรทัด" → **หารหาราคา/หน่วย เก็บจำนวนไว้** (ปริมาณจากมิเตอร์
     ตรวจย้อนได้ ทิ้งไม่ได้; หลักบัญชี: ราคาทุนต่อหน่วย = ยอดจ่ายจริง ÷ ปริมาณ)
     · `qty ≈ Amount` = จำนวนคือตัวหลงคอลัมน์ → แก้ qty (เดิม) · ไม่มีราคา/หน่วย
     แต่มีจำนวน+ยอด → หารหาให้ _(ที่มา: ค่าไฟ 3,611 × 16,351.48 = 59 ล้าน)_
  3. **`UnitInferrer`** — หน่วยว่าง → อนุมานจากคำอธิบาย (ค่าไฟ=`หน่วย` ·
     น้ำ=`ลบ.ม.` · น้ำมัน=`ลิตร` · รายเดือน/เช่า=`เดือน` · เหมา/บริการ=`งาน`)
     ก่อนตก `ชิ้น`; `ชิ้น` บนบรรทัดที่กฎรู้จัก = default ค้างมา → แทนที่
     _(handoff เดิม**ทิ้ง** field `unit` ⇒ ฟอร์ม default "ชิ้น" เสมอ ขณะที่
     "สร้างทันที" ได้หน่วยจริง = สองทางให้ผลต่างกันบนสแกนใบเดียวกัน)_
  4. **`ProhibitedInputVatScreener` (§82/5(4)(6))** — ต้องห้ามตาม **ชนิด
     รายจ่าย** ไม่ใช่แค่รูปแบบใบ: `OcrDocumentRoleInferrer` ตรวจได้เฉพาะ
     ใบย่อ §82/5(2) / ไม่ใช่ใบกำกับเต็มรูป §82/5(1) — แต่ใบกำกับเต็มรูปที่ถูก
     100% ของค่าน้ำมันรถเก๋ง/ค่ารับรองก็เคลมไม่ได้ **เดิมเปิดเคลมให้ทุกใบที่
     รูปแบบถูก = ยื่น ภ.พ.30 เกินสิทธิ์เงียบ ๆ**. ค่ารับรอง → ห้ามเสมอ ·
     ค่าน้ำมัน/เช่า/ซ่อมรถ **ไม่ระบุชนิดรถ → default ไม่เคลม** (เคลมเกินสิทธิ์
     = โดนประเมิน+เบี้ยปรับ · ไม่เคลมทั้งที่ได้ = ติ๊กคืนใน 6 เดือน §82/3 —
     สองทางผิดไม่เท่ากัน) · ระบุรถที่เคลมได้ (กระบะ/บรรทุก/เครื่องจักร) →
     เคลมได้ + เตือนให้ยืนยัน. Marker: `[VAT-CLAIM]` = ปิดเคลม (marker เดิมที่
     ทั้ง frontend+backend รู้จักอยู่แล้ว) · `[VAT-NOTE]` = เตือนอย่างเดียว.
     **ไม่ใส่ keyword "น้ำมัน" เดี่ยว ๆ** — จะโดนน้ำมันพืชของร้านอาหารซึ่ง
     เคลมได้ตามปกติ
- **ด่านก่อนสร้าง/อนุมัติ (รอบ 137 — ผลตรวจไปป์ไลน์ OCR · แก้เพิ่มรอบ 139)**:
  1. **สิทธิ์** — `OcrController.CreateDocument` เช็ค
     `DocumentPermissionHelper.CanCreateAsync(targetType)` ก่อนสร้าง และ
     `CanApproveAsync(createdType)` ก่อนอนุมัติ · `create-journal-entry` เช็ค
     `PermissionKeys.JournalManage`. **เดิมทั้งสองเส้นมีแค่ `[Authorize]`** ⇒
     เป็นทางอนุมัติทางที่ 4 ที่รอบก่อน (ลายเซ็น/LINE/มือถือ) ยังปิดไม่ครบ ·
     ไม่มีสิทธิ์อนุมัติ = ใบ Draft ยังถูกสร้าง แล้วแนบ `[APPROVE-SKIP]` กลับมา
     (ห้าม silent no-op)

     **ชนิดเป้าหมายของด่าน = `Helpers/OcrTargetDocumentType.Resolve()`** ตัวเดียว
     กับที่ `CreateDocumentFromScanAsync` ใช้จริง — ลำดับ: ผูก PO แล้ว →
     `PurchaseInvoice` · pseudo `"Deposit"` → `Receipt` + `IsDeposit` ·
     override ของผู้ใช้ · `TargetDocumentType` บนแถวสแกน · fallback ตามชนิด
     กระดาษ (`Invoice`/`TaxInvoice`→`PurchaseInvoice` · `Receipt`→`PaymentVoucher`
     · `CertificateInLieu` → เอง · อื่น ๆ → `Expense`). **ฟังก์ชันนี้ไม่มีทาง
     คืน "ไม่รู้"** เพราะเส้นสร้างเอกสารสร้างจริงเสมอ _(รอบ 139: ตัวแปลงเดิมใน
     controller คืน `null` เมื่อแปลงไม่ได้ ⇒ ด่านถูกข้ามเงียบ ๆ ในเคสที่พบบ่อย
     ที่สุด — สแกนใหม่ที่ยังไม่มี `TargetDocumentType` และใบมัดจำ)_
  2. **วันที่อ่านไม่ได้ → ห้าม auto-approve** — `ExtractedDate == null` ⇒
     `[APPROVE-SKIP]` + `FieldConfidence[DocumentDate]=0.30` +
     `[DATE-UNKNOWN]` ใน `ProcessingNotes`. เอกสารยังถูกเติม "วันนี้" ตาม
     กฎเหล็ก #3 (ห้ามปล่อยว่าง) แต่วันที่ = tax point/งวด ภ.พ.30 และเลขเอกสาร
     gap-free ออกตามวันนั้นแก้ย้อนหลังไม่ได้ ⇒ ต้องให้คนยืนยันก่อน
  3. **50 ทวิ ที่ "เราถูกหัก" ไม่สร้างเอกสารขาย** — `InferWhtCertWeAreWithheld`
     = true ⇒ ลงทะเบียน `WhtCreditReceived` (`EnsureWhtCreditFromCertAsync`)
     แล้ว **throw `OCR-WHTCERT-NO-SALE`** พร้อมบอกทางไปต่อ · auto-create ก็ถูก
     ปิดด้วย (`criticalFieldsOk && ocrWeAreWithheld != true`). เดิม role
     inferrer ตั้ง target = `ReceiptVoucher` แล้วเส้นนี้สร้าง RV **ไม่มีใบต้นทาง**
     ⇒ ตอนอนุมัติ JE เป็น "ขายสด" (Dr เงินสด / Cr รายได้ + ภาษีขาย) = รายได้
     เบิ้ล + VAT ขายเบิ้ล + AR ไม่ถูกล้าง
  4. **เลขใบกำกับของผู้ขาย ต้องมาจากใบกำกับจริง** — กระดาษที่ระบบอ่านได้ว่าเป็น
     `Invoice`/`Receipt`/`DeliveryNote`/`Quotation`/`BillingNote` **ไม่** set
     `HasTaxInvoiceReference` อีกต่อไป (เดิมเอาเลขใบแจ้งหนี้ไปลง
     `SupplierInvoiceNumber` ⇒ เคลมภาษีซื้องวดนี้ทั้งที่ใบกำกับยังไม่มา
     §82/5(1)) → แนบ `[TAX-INV-PENDING]` แทน แล้วพัก VAT ที่ 11640 รอเติมเลข
     ใบกำกับเมื่อได้รับ (flow §3.6 เดิม)
  5. **บันทึก JE ตรง: ภาษีหัก ณ ที่จ่ายต้องมีผังรองรับทั้งสองฝั่ง** — ฝั่งขาย
     (เราถูกหัก) ไม่มี `11910` → `OCR-JE-NO-WHT-ASSET` · ฝั่งซื้อ (เราหัก)
     ไม่มี `21916`/`21917` → `OCR-JE-NO-WHT-LIABILITY`. ทั้งสองด่านอยู่**ก่อน**
     สร้างบรรทัด JE _(รอบ 139: ฝั่งซื้อเคยปล่อยให้บรรทัด WHT หายเงียบ ⇒ เครดิต
     เต็มจำนวน = "จ่ายผู้ขายครบ" ทั้งที่ติ๊กว่าหัก ⇒ ไม่มีหนี้สินให้นำส่ง
     ภ.ง.ด.3/53 + เจ้าหนี้เกินจริง + 50 ทวิ ที่ออกไปแล้วไม่มีคู่ในบัญชี)_
  6. **รหัสสาขาไม่แต่ง** — ไม่รู้สาขาผู้ขาย = `SupplierBranchCode = null`
     (เดิม `?? "00000"` ⇒ รายงานภาษีซื้อ §87 พิมพ์ "สำนักงานใหญ่" ให้ใบสาขา);
     ด่าน §86/4 ตอนอนุมัติเป็นคนบังคับกรอก
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
  OCR engine cascade → local distillation model (**นักเรียน**) → historical
  lookup (vendor's last doc) → rule defaults (VAT 7%, vendor default GL) → AI
  ตอน last resort (ผ่าน `IAiOrchestrator.AskAsync` per กฎเหล็ก #1)
  - **นักเรียนตอบแทนครูได้จริงตั้งแต่รอบ 137** — orchestrator short-circuit คืน
    `UsedAi=false` พร้อมคำตอบของ local model; เดิม call site ทุกจุดบนเส้น OCR
    มี `UsedAi &&` เป็นด่านแรก ⇒ **คำตอบนักเรียนถูกทิ้งทุกครั้ง** (ยิ่ง local
    โต ระบบยิ่งได้แต่ heuristic เปล่า ๆ = "เรียนแล้วโง่ลง") → ตอนนี้ใช้
    `OcrAiAugmentationResult.HasModelAnswer` (ครูหรือนักเรียนก็ได้) ผ่าน
    anti-hallucination guard เดิมทุกด่าน · ป้าย `<Feature>UsedAi` ยังหมายถึง
    "provider ถูกเรียกจริง" เท่านั้น (ป้ายซื่อสัตย์ตามกฎเหล็ก #1 ข้อ 6)
  - **ไม่แต่งบรรทัดให้ยอดตรง** — การกระทบยอด "Σ บรรทัด ↔ หัวใบ" ย้ายไปที่
    `Helpers/OcrLineReconciler` (pure + เทสต์ด้วยตัวเลขจริง): เทียบกับ**ยอดก่อน
    VAT** ไม่ใช่ยอดรวม · ส่วนลดต้องมีบนกระดาษ (`ExtractedDiscountAmount`) ·
    ตัดสินไม่ได้/บรรทัดขาด = `[Σ-GAP]` ให้คนดู. เดิมเทียบผิดฝั่ง ⇒ ใบมีส่วนลด
    ถูกตีเป็น "ราคารวม VAT" แล้ว**แต่งบรรทัด "ค่าขนส่ง/บริการอื่น"** ที่ไม่มีบน
    กระดาษ · ส่วนลดถูกคิดเทียบยอดรวมแล้วบวก VAT ซ้ำ (เอกสาร ≠ กระดาษ)
- **แหล่งเงิน (Cr) auto-fill — 3 ชั้น priority** (`OcrService.cs` credit auto-fill
  + `ResolvePaymentSourceOverrideAsync`): สำหรับ PV/Receipt/Expense จ่ายสด —
  (1) partner metadata top-level `paymentAccountCode`/`bankCode`/`bankAccountCode`
  (match CoA code → เลขบัญชีธนาคาร→LinkedAccountId) → (2)
  `CompanySettings.DefaultPaymentAccountId` (admin ตั้งใน Settings) → (3)
  lowest-code fallback เดิม. เดิมมีแต่ชั้น 3 → หยิบบัญชีรหัสต่ำสุดมั่ว
  (กรุงไทย 11110 ชนะ กสิกร 11120). A/P-A/R ไม่มีแหล่งเงิน → ใช้ prefix เดิม.
- **แก้แหล่งเงินหลัง approve** — `DocumentService.ReclassifyPaymentSourceAsync`
  (`POST /documents/{id}/reclassify-payment-source`): คู่กับ reclassify-line
  (ฝั่ง Dr). post correcting-JE **Dr ผังเก่า / Cr ผังใหม่** ขนาด PaidAmount →
  เงินกลับเข้าบัญชีเก่า + ออกจากบัญชีใหม่. gate เดียวกับ reclassify-line
  (period open, ไม่มี downstream/Payment แยก/TaxReport submitted/e-Tax).
  UI: ปุ่ม ✏️ ข้าง "แหล่งเงิน (Cr)" ในหน้า detail (PV/Receipt/Expense ที่
  Approved/Paid).
- **Phantom split-VAT sanitizer**: `OcrService.SanitizeVatSplitArtifacts`
  ตัด suffix "(ส่วนมีภาษี)/(ส่วนไม่มีภาษี)/(VATable)/(non-VAT)/(VAT included)…"
  ที่ AI/OCR แปะมาจาก footer summary ของใบกำกับ (เคส OfficeMate) + ยุบบรรทัด
  ที่ description ตรงกัน + drop phantom remainder ≤ ฿1. รันก่อน serialize ลง
  `ExtractedItemsJson` → ทุก path (web UI + OCR API) ได้ไฟล์ items ที่สะอาด.
- **Inline line editing ในหน้า review (กฎเหล็ก #3)**: review modal ให้แก้
  `Description/Quantity/UnitPrice` ราย line ได้ในตัว (input ในตาราง, เดิม read-only
  โชว์แค่ project picker) → `OcrService.SetExtractedLineFieldsAsync`
  (`POST /ocr/{id}/line-fields`) recompute `Amount = round(qty×price,2)` + persist
  ลง `ExtractedItemsJson`. คู่กับ qty-guard (`SanitizeVatSplitArtifacts` reconcile
  จำนวน): guard แก้เคสที่ตรวจเจอ (Amount ถูก แต่ qty×price ระเบิด), inline edit
  ครอบเคสที่เหลือ — ผู้ใช้ไม่ต้องสร้างเอกสารก่อนแล้วเข้าไปแก้ทีหลัง.
- **Single create path (กฎ: ห้ามมี path คู่ขนาน)**: ทั้ง web UI ("สร้างเอกสาร")
  และ OCR API (`autoCreate=true`) สร้างเอกสารผ่าน **`CreateDocumentFromScanAsync`
  ตัวเดียวกัน**. `AutoCreateDocumentAsync` (เรียกตอน scan ผ่าน confidence gate)
  เป็น thin wrapper: `SaveChangesAsync()` (persist scan fields) → delegate ไป
  `CreateDocumentFromScanAsync(scan.Id, targetType=null)`. เดิมเป็น
  implementation คู่ขนานที่ "ง่ายกว่า" → OCR API ได้เอกสารไม่ตรงกับอัปโหลดผ่าน
  เว็บ (ขาด WHT base reconstruct, supplier-invoice ref ภพ.30, bank/payment
  account, sales-side contact, PO linkage, CertInLieu fields, line reconcile,
  GL feedback, RD-compliance). ตอนนี้ data point ทุกตัวตรงกัน.
- **DRAFT- placeholder (กฎ §86/4)**: `CreateDocumentFromScanAsync` ตั้งเลข
  เริ่มต้นเป็น `DRAFT-{guid14}` เหมือน `DocumentService.CreateDocumentAsync`
  → เลขจริงออกตอน Approve เท่านั้น (`ApproveDocumentAsync` line 1828 regen
  จาก `doc.DocumentDate` ที่ตอนนั้น). กัน:
  - **DocumentNumber↔DocumentDate desync** เคสที่ user แก้วันที่ตอน review
    แล้วเลขที่ออกไปคาวันเก่า (artifact ก่อน TZ fix หรือก่อนแก้ DocumentDate)
  - **Sequence gap** ตอนลบ Draft (เลขจริงไม่เคยออก → ลบได้ปลอดภัย)
- **Line reconcile (Case A/B/C/D)** ใน `CreateDocumentFromScanAsync` (ใช้ร่วม
  ทั้ง 2 path) — reconcile line amounts กับ header subtotal/total ก่อนสร้าง
  `DocumentLine`:
  - (A) ราคารวม VAT — `grossSum` อยู่ระหว่าง subtotal กับ total → ตั้ง
    `PricesIncludeVat=true` + เติม line "ค่าขนส่ง/บริการอื่น" ถ้ามี gap
  - (B) ราคาแยก VAT — `grossSum ≈ subtotal` → ไม่ปรับ
  - (C) ส่วนลด — `grossSum > total` → คำนวณ `docDiscountPercent` ลงทุกบรรทัด
  - (D) OCR ขาด — `grossSum < subtotal` → ปล่อยให้ user แก้
- **Quota refund**: ถ้า re-OCR (retry) ไม่ใช้ quota ใหม่ (`OcrService.cs`)
- **ไฟล์ซ้ำ (hash ตรง) → เส้น `Cached`** (`OcrService.ScanAsync`):
  - เกณฑ์เลือกต้นฉบับ: `CompanyId` เดียวกัน · `FileHash` ตรง · `ScanStatus =
    Completed` · **`!IsDuplicate`** (กันสำเนาของสำเนา) · `OrderByDescending
    (CreatedAt)` = ผลอ่านล่าสุดที่เป็นของจริง (deterministic)
  - คัดลอกผ่าน **`Helpers/OcrScanSnapshot.CopyExtractionFrom`** ตัวเดียว —
    **deny-list**: คัดลอกทุกช่องที่ประกาศบน `OcrScanResult` ยกเว้น 15 ช่องที่เป็น
    ตัวตนของแถว (ไฟล์แนบ · hash · สถานะ · engine · notes · RetryCount ·
    `ExternalMetadataJson` · `UserNotes` · `CreatedDocumentId` ·
    `CreatedJournalEntryId` · `StockImportedAt` · ธง duplicate) ⇒ ช่องใหม่
    ในอนาคตถูกคัดลอกโดยอัตโนมัติ · ล็อกด้วย `OcrScanSnapshotTests`
    (เติมค่าทุกช่องแล้วพิสูจน์ว่าไม่มีช่องไหนหลุด)
  - แถวสำเนาจึงมี `RawTextContent` + `ExtractedItemsJson` + `FieldConfidenceJson`
    + ช่อง §86/4 + `TargetDocumentType` ครบเท่าต้นฉบับ — สำคัญเพราะขั้นสร้าง
    เอกสารอ่าน raw text ไปตัดสิน **สกุลเงิน · เหตุผลใบลดหนี้ §86/10 ·
    ประเภทเงินได้ 50 ทวิ · เงินมัดจำ · คำเตือน RD compliance**
  - `OcrEngine = "Cached"`, `ProcessingNotes = "Duplicate of scan {id}"`,
    ไม่มี engine ตัวไหนทำงาน ⇒ `/upload` **คืนโควตา** (`result.IsDuplicate`)
  - **"สแกนใหม่" = `POST /ocr/{scanId}/retry`** → `ScanAsync(forceRescan: true)`
    ข้ามด่าน hash แล้วเดิน engine จริง · สร้าง **แถวใหม่** (แถวเดิมเก็บผลอ่านเดิม
    ไว้เปรียบเทียบ ไม่ถูกตั้งเป็น `Processing` ค้าง) และข้อความตอบกลับบอก
    **เลขสแกนปลายทาง** เสมอ
- **ด่านตัวเลขสามช่อง** (`OcrConfidenceGateway` ข้อ 2b): ฟ้องเมื่อ
  `|SubTotal+VAT−Total| > ฿0.02` **และ** `|VAT − 7%×SubTotal| > max(฿0.02,
  ฿0.01×จำนวนบรรทัด)` พร้อมกัน ⇒ ใบหลายอัตราภาษี (ผลรวมเป๊ะ) ไม่ถูกฟ้องผิด ·
  ปิดช่องที่ ฿0.44 เคยรอด `MathTolerance` ฿2.00 และ 7.37% เคยรอดกรอบ 6.5–7.5%

- **วงจร "ประวัติยืนยันประวัติ" เรื่องหัก ณ ที่จ่าย — ตัดแล้ว** (รอบ 183 · D3-2):
  - `VendorIntelligenceService` เคยเติม `HasWht`/`WhtRate` ให้สแกนจาก**ประวัติผู้ขาย**
    ⇒ ด่านอนุมัติ (`DocumentService.ScanPaperWhtEvidenceAsync`) อ่านช่องนั้นแล้วสรุปว่า
    "กระดาษประกาศเชิงบวก" ⇒ VendorIntel เรียนกลับจากผลนั้น = ความมั่นใจโตเองโดยไม่มี
    หลักฐานใหม่สักชิ้น
  - ตอนนี้ VendorIntel เขียนลง **`SuggestedWhtRate`** (ช่องข้อเสนอ) + `FieldConfidence`
    + โน้ต `[WHT-SUGGEST]` ที่บอกตรง ๆ ว่ามาจากประวัติ ไม่ใช่จากกระดาษ
  - **"กระดาษพูด" อ่านจาก `Helpers/PaperWhtReader` ตัวเดียว** ทั้งฝั่งด่านอนุมัติและ
    ฝั่งเรียนรู้ — ห้ามอ่าน `scan.HasWht`/`scan.WhtRate` ที่ไหนอีก (ทำให้แถวเก่าที่
    ปนเปื้อนหมดฤทธิ์เองโดยไม่ต้อง migration)
  - **`Helpers/OcrWhtLearningScope.Decide`** ตัดสินว่าใบนี้สอนประวัติได้ไหม:
    คีย์มือ / กระดาษพิมพ์ / ผู้ใช้แก้ช่อง WHT เอง ⇒ เรียนได้ · ระบบเสนอเองแล้วไม่มี
    ใครแตะ ⇒ **ไม่เรียน** (ทุกทางเข้ารวม backfill เดินด่านเดียวกัน)
  - ⚠️ **ผลที่ผู้ใช้เห็น**: ผู้ขายบริการประจำที่กระดาษไม่พิมพ์ส่วนหัก จะ**ไม่ถูกเติม
    WHT อัตโนมัติอีก** — ใบมี `WithholdingTaxAmount = 0` + โน้ต `[WHT-SUGGEST]` ให้คนกรอก

### 2.2b LINE bot — "โยนบิลเข้าไลน์" (Paypers-style)

- **Flow**: ผู้ใช้ส่ง **รูปถ่ายใบเสร็จ / ไฟล์ PDF** เข้า LINE bot →
  `LineWebhookController` (message type `image`/`file`) →
  `LineBotService.HandleImageAsync` → ดาวน์โหลดจาก LINE Content API
  (`api-data.line.me/v2/bot/message/{id}/content`) → **pipeline เดียวกับ
  อัปโหลดหน้าเว็บทุกขั้น**: `OcrPreprocessor.Check` (ตรวจคุณภาพก่อนตัดโควต้า)
  → SHA-256 dedup (10 นาที — LINE ชอบกดส่งซ้ำ) → `IOcrQuotaService.TryConsume`
  → `OcrService.ScanAsync(autoCreate: true)` → ตอบกลับทาง LINE
- **Reply**: สร้างเอกสารสำเร็จ → ประเภท+ผู้ขาย+ยอด+ลิงก์ตรวจ/อนุมัติ
  (`document-scan.html?reviewScan={id}`); safety gate ระงับ → สรุปที่อ่านได้
  + ลิงก์หน้า review ที่ pre-fill แล้ว; ซ้ำ/ล้มเหลว/โควต้าหมด → บอกเหตุผลตรง ๆ
- **กฎเหล็ก #3 ยังคุม**: เอกสารที่สร้างเป็น **ฉบับร่าง** เสมอ (เลขจริงออกตอน
  Approve) + safety gate ครบชุดของ `ScanAsync` (confidence ≥ 0.85, field สำคัญ
  ครบ, ระงับเมื่อเจอลายมือ/สินทรัพย์, กันใบซ้ำ) — LINE ไม่ได้ bypass อะไร
- **ผูกบัญชี**: ใช้กลไก bind เดิม ("ผูก {รหัส 6 หลัก}") + `LineUserState`
  active company (หลายบริษัท → ให้เลือกก่อนแล้วส่งรูปใหม่)
- **Quota refund**: กติกาเดียวกับหน้าเว็บ — duplicate / scan fail / e-Tax XML
  ฝังไฟล์ (ไม่ได้ใช้ OCR engine) คืนเครดิต
- **Flex card + อนุมัติในแชท**: สร้างเอกสารสำเร็จ → ส่ง Flex bubble (ประเภท/
  ร้าน/ยอด/VAT) พร้อมปุ่ม **"✅ อนุมัติเลย"** (postback `approve:{docId}`) และ
  **"🔍 ตรวจ/แก้ไขก่อน"** (ลิงก์ reviewScan); ส่ง Flex ไม่ผ่าน → fallback ข้อความ
  ธรรมดาเสมอ. postback → `HandlePostbackAsync`: **ตรวจ binding + tenant
  (สมาชิกบริษัทเจ้าของเอกสาร) + role (Owner/Accountant/ExternalAccountant/
  SystemAdmin เท่านั้น — data ปลอมได้)** → `ApproveDocumentAsync` (เลขจริงออก
  ที่นี่) → ตอบเลขเอกสาร; ซ้ำ/สถานะเลยแล้ว → บอกสถานะ ไม่ทำซ้ำ
- **แจ้งกลับผู้ส่ง**: intake ฝัง `{sourceChannel:"line", lineUserId}` ลง
  `ExternalMetadataJson` ของ scan → ตอนเอกสารถูกอนุมัติ (โดยคนอื่น เช่น
  นักบัญชีบนเว็บ) `ApproveDocumentAsync` push LINE กลับหาผู้ส่งบิล
  (best-effort, ใช้ token บอทเสมอ — companyId:null); ผู้อนุมัติ = ผู้ส่ง →
  ไม่แจ้งซ้ำ (ได้ reply จาก postback แล้ว)
- **อัลบั้มหลายรูป**: LINE ส่งเป็นคนละ event → loop เดิมประมวลผลครบทุกใบ
  (1 รูป = 1 scan = 1 เอกสาร/การ์ด)

### 2.2c ใบรับรองแทนใบเสร็จรับเงิน — routing บิลไม่เป็นทางการ (§65 ตรี)

- **กติกา** (`OcrService.ScanAsync` ก่อน re-sync block): ฝั่งซื้อ (`OurRole ==
  "Buyer"`) + กระดาษเป็น `Receipt` + **ไม่มี VAT** + target เดิมเป็น
  Expense/PaymentVoucher → เรียก **`Helpers/OcrPayeeEvidence.Evaluate`** (pure)
  แล้วตัดสินตามระดับหลักฐานผู้รับเงิน:

  | ระดับ | เงื่อนไข | ผล |
  | --- | --- | --- |
  | `Identified` | มีเลขภาษี 13 หลักที่ผ่าน checksum (และไม่ใช่บาร์โค้ดสินค้า) **หรือ** มีชื่อ + (ที่อยู่ ≥ 8 ตัวอักษร / เบอร์โทร ≥ 9 หลัก) | **คงชนิดเดิม** (ใบเสร็จ → ใบสำคัญจ่าย) · ลง ReasoningTrace บอกว่าทำไมไม่ออกใบรับรอง |
  | `Weak` | มีชื่อผู้รับเงินอย่างเดียว | **คงชนิดเดิม** + ลง trace ชวนให้ผู้ใช้เปลี่ยนเองถ้าพิสูจน์ผู้รับไม่ได้ |
  | `Unidentified` | ไม่มีชื่อผู้รับเงินที่อ่านได้ | `TargetDocumentType = CertificateInLieu` + trace อ้าง §65 ตรี(9)(18) |

  ⚠️ เดิมกติกานี้ตัดสินจาก **"ไม่มีเลขผู้เสียภาษี"** สัญญาณเดียว ⇒ บิลเงินสดของ
  ร้านที่มี **ชื่อ + ที่อยู่ครบบนกระดาษ** ถูกเปลี่ยนเป็นใบรับรองแทนใบเสร็จทั้งที่
  ผู้ขายออกบิลให้แล้ว (ผู้ใช้รายงาน 2026-09-11) — §65 ตรี(18) ถามว่า "พิสูจน์ผู้รับ
  เงินได้ไหม" ไม่ได้บังคับว่าต้องมีเลข 13 หลัก
- **เหตุผล**: บิลเงินสดแม่ค้า/วินฯ ที่**ไม่มีชื่อผู้รับเงิน**ระบุตัวไม่ได้ → เสี่ยงโดนบวกกลับ;
  แนวปฏิบัติกรมสรรพากรให้จัดทำ "ใบรับรองแทนใบเสร็จรับเงิน" ประกอบ — ฟอร์มมี
  ผู้รับรอง/เหตุผล (`CertificateReason`, `CertifierName` เติมอัตโนมัติใน
  `CreateDocumentFromScanAsync`) + รูปบิลเดิม relink เป็นไฟล์แนบเอกสาร
- **ใบมี VAT แต่ไม่มีเลขผู้เสียภาษี** (ใบกำกับอย่างย่อ) ไม่เข้ากติกานี้ —
  มีเส้นทาง §82/5(2) ของตัวเอง (เตือนเคลมภาษีซื้อไม่ได้)
- **Auto-create gate ผ่อนเลขที่เอกสาร**: target = CertificateInLieu ไม่บังคับ
  `DocumentNumber` จากกระดาษ (บิลพวกนี้มักไม่มีเลขที่ — เลขจริงคือเลขใบรับรอง
  ที่ระบบออกตอน Approve); ยังบังคับ วันที่ + ยอดรวม + contact match เหมือนเดิม

### 2.3 Integration ภายนอก
- **Controller**: `IntegrationController.cs` — manage config + API key issuance
- **เข้าทาง** `/api/companies/{id}/documents` (ทาง standard) พร้อม
  `X-Acting-User` header
- **คีย์ `int_` (รอบ 193 · คำตัดสิน #37 · ทีม S/W)** — ออก/แก้/ลบ/สร้างคีย์ใหม่ + ผูกผู้ใช้ที่คีย์สวมได้ = **เจ้าของ (Owner/SystemAdmin)
  ที่ล็อกอินเท่านั้น** (`RequireOwnerAsync` · คำขอจาก API key ถูกปฏิเสธเสมอ) · Account Mapping ต้อง `CompanySettings.Edit` ·
  สิทธิ์ต่อคีย์ `CanRead/CanWrite/CanDelete` (คีย์ใหม่ = **อ่านอย่างเดียว** จนเจ้าของติ๊กเพิ่ม · `IntegrationKeyPolicy.EffectiveScopes/RequiredScope`
  ตัวเดียวทั้ง `X-Api-Key` middleware และ `X-Integration-Key` ที่ `ExternalIntegrationController` · method ไม่รู้จัก = เขียน) · คีย์เดิม
  (TakeTime) = `IsLegacyKey` สิทธิ์เต็มถึง `LegacyDeprecatesAt` (deploy + 90 วัน · `LegacyGraceDays`) · `X-Acting-User`: คีย์ใหม่ = แถว
  `IntegrationUserMapping` เท่านั้น · คีย์ legacy = email match ได้แต่เขียน audit (`AddChainedAuditLog` · ครั้งเดียวต่อคีย์/ผู้ใช้/ชั่วโมง) ·
  header ตอบ `X-Acting-User-Resolved: mapping|legacy-email-match|unmapped` · regenerate คีย์รุ่นเก่า = ย้ายเข้านโยบายใหม่ (`IsLegacyKey=false`) —
  รายละเอียด `ACCOUNT_STRUCTURE.md` §3.1
- **จับผู้ติดต่อด้วย "เลขภาษี + สาขา"** ทุกทางเข้า (ลูกค้า/ใบขาย/ผู้จำหน่าย) — `ContactTaxBranchKey` · ดู §6.2i
- **Connected API `/api/v1/documents`** (รอบ 193 C3): รับ `contactBranchCode` (ผิดรูป = 400 ชี้ช่อง) · เลขมีแล้วคนละสาขา ⇒ **สร้างผู้ติดต่อสาขาใหม่** (ไม่เทียบชื่อ ·
  บทบาทลูกค้า/ผู้จำหน่ายตามแถว สนญ. + ฝั่งของเอกสาร `DocumentSide.IsSales`) · เลขจริง + ชื่อคล้าย (fuzzy) ⇒ ผู้ติดต่อใหม่ ไม่ผูกแถวเดิม · ใบฝั่งขายตอบ
  `contact.missingBuyerFields` (ตัวตรวจเดียวกับด่านอนุมัติ `TaxInvoiceCompletenessChecker.MissingBuyerFields`) + `branchCode` + `contact.taxIdWarning`
  (เลข checksum ผิด · แถวใหม่ติด `[TAXID-CHECKSUM]` — §6.2i) · approve = §3.2 ApiClient ·
  สัญญาเต็ม `ACCOUNT_STRUCTURE.md` §3.2
- **e-Tax อัตโนมัติ**: ทุกเมธอดที่ประทับ `Approved` เอง (TIV/CN/DN · Expense/PV/CIL) เรียก `IIssuedDocumentHooks.RunAsync` หลังบันทึก ·
  TIV/CN/DN ต่อข้อความเตือน (`EtaxHookSuffix`) ในคำตอบเมื่อออก e-Tax ไม่สำเร็จ · §3.2 ขั้น e-Tax
- **อัตรา VAT รายบรรทัดจากคู่ค้า** (`DocumentLineVatConvention.SplitLine`): `7` = 7% · `0` = อัตราศูนย์ §80/1 (ใบกำกับอัตรา 0 —
  `TaxInvoiceSeriesPolicy.IsZeroRatedFullTaxInvoice` ⇒ ธง `IsTaxInvoiceByLaw=true` + เลขชุด TIV) · `-1` = ยกเว้น §81 (ไม่ใช่ใบกำกับ) ·
  **อัตรา ≤ 0 ⇒ VAT 0** (เดิม `-1` ไหลเข้าสูตร = VAT ติดลบ 1%) · สัญญาเต็ม `INTEGRATION_RESYNC.md` §11
- **สถานะ VAT ของบริษัท** อ่านผ่าน `CompanyVatStatus` (`GetCompanyVatProfileAsync` — อัตราถอยไป `Company.VatRate` แทน 7) · §3.2 ธง VAT
- **Behavior พิเศษ**:
  - resolve external account/user IDs → company members (mapping table)
  - `AutoApprove` flag ตาม `IntegrationConfig` → ข้าม Draft state
  - **`IsCashSale` (B2B ขายเงินสด)** บน `InboundInvoiceRequest`: สร้าง TaxInvoice
    ที่ตั้ง `IssuedAsCashReceipt=true` + `PaymentAccountId` แล้ว **post JE แบบ
    "ขายเงินสด" ผ่าน `IDocumentService.PostCashSaleJournalAsync` (→ AutoPost
    branch cash-receipt ที่ขยายให้รับ TaxInvoice+IssuedAsCashReceipt)** — ไม่ใช่
    mapping-JE (Dr ลูกหนี้) เดิม. **GL: `Dr เงินสด(PaymentAccountId) + [Dr 217xx +
    Dr 21913 ถ้ามัดจำ drives] / Cr รายได้(ราย line) + Cr 21911` — ไม่มีลูกหนี้การค้า
    เลย** (reuse เส้น `driveDeposit` ที่ verified). สำเร็จ → `PaidAmount=Total,
    BalanceDue=0` → e-Tax **T03** + หัว "ใบเสร็จรับเงิน/ใบกำกับภาษี". ยุบ 3 ใบ
    (TIV+REC×2) เหลือใบเดียว (spec TakeTime).
    **fail-soft:** ลง JE เงินสดไม่สำเร็จ → degrade เป็นตั้งหนี้ (mapping JE Dr ลูกหนี้)
    + คง `BalanceDue=Total` + `IssuedAsCashReceipt=false` → TakeTime capability-
    detection (`balanceDue>0`) จะ fallback settle เอง (ไม่ settle ซ้ำ). PostCashSale
    detach JE ที่ค้างใน context ก่อน rethrow (กัน pollution).
  - **Deposit fields บน `InboundInvoiceRequest`** (`DepositAppliedAmount`,
    `DepositAppliedRef`, `DepositOutputVatDeferred`, `DepositAppliedDrivesJournal`)
    → persist ลง `Document` ตอนสร้าง (stamp `DepositAppliedAmount` ทุกกรณี).
    `DepositAppliedDrivesJournal=true` → `driveDeposit` ใน AutoPost อ่านยอดนี้กลับ
    217xx/21913 + Dr เงินสด "สุทธิ" (Total − ยอด). resolve `depositAppliedRef` เป็น
    ใบมัดจำจริง (IsDeposit) — เคส A doc / เคส B journal-ref. `DrivesJournal=false`
    = display-only (Dr เงินสดเต็ม, TakeTime กลับมัดจำเอง). void สมมาตรผ่าน 7c
    (`UnrealizeDrivesDepositAsync`).
    **รอบ 193 (L2 N1/N2)**: drives ที่อ้างมัดจำ**ออกใบกำกับแล้ว** (VAT ทันที) ⇒ `TaxedDrivesRejectionAsync` **ปฏิเสธก่อนออกเลข**
    (ทั้งสร้างใหม่และก่อน resync) พร้อมบอกรูปที่ต้องส่ง (หักมูลค่ามัดจำก่อน VAT จากราคาบรรทัด · หรือส่งมัดจำแบบ VAT รอเรียกเก็บ)
    — ไม่แปลงยอดของคู่ค้าเอง (#34 "ห้ามแก้ยอดคู่ค้า") · ตาข่าย: ถ้ายังหลุดถึงการลงบัญชีแล้วล้ม ⇒ **ยกเลิกใบ** (`VoidDocumentAsync` ·
    เลขคงอยู่ไม่มีช่องว่าง) + ธง `RD-86/4-DEPOSIT-TIV-DOUBLE` บนหมายเหตุ + log `Failed` + `success=false` · **ไม่ถอยไปตั้งหนี้เงียบ**
    (กติกา `required_call_site_check`) · **ตาข่ายของตาข่าย (ฝ่ายค้านรอบสาม B1 · 04ce362)**: `ProcessInvoiceAsync` **ยกเลิกก่อน** แล้วค่อยประทับหมายเหตุ
    "ยกเลิกอัตโนมัติ" · ยกเลิกล้ม (งวดยื่น/ปิด) ⇒ `ChangeTracker.Clear()` กันสภาพครึ่ง void ถูกบันทึกตาม → อ่านใบใหม่ → หมายเหตุจริง "ลงบัญชีไม่ได้และยกเลิก
    อัตโนมัติไม่สำเร็จ … ต้องยกเลิก/ออกใบลดหนี้ด้วยมือ" + LogError + log `Failed` + `success=false` · **คู่ค้ายิงซ้ำเจอใบนี้ = ล้มดังด้วยเหตุเดิม** (ไม่ตอบ
    "มีอยู่แล้ว" เงียบ) — ป้าย `IntegrationService.TaxedDrivesVoidFailedMarker` ตัวเดียวทั้งฝั่งเขียน/อ่าน · **idempotency ข้ามใบ Voided (R3-6 · 04ce362)**:
    คิวรีหา `Reference == ExternalRef` กรอง `Status != Voided` **ในคิวรี** + เรียงใหม่สุดก่อน ครบ **6 เมธอด** (ใบกำกับ · ใบลดหนี้ · ใบเพิ่มหนี้ · ค่าใช้จ่าย ·
    ใบสำคัญจ่าย · ใบแทนหนังสือรับรอง — รูปเดิมเหลือ 0 จุด) ⇒ ใบที่ตาข่ายยกเลิกแล้ว + คู่ค้าส่งใหม่ = เจอใบใหม่ ไม่สร้างซ้ำ · มีแต่ใบ Voided = สร้างใหม่ได้ตามเดิม · มัดจำ VAT พัก (`depositOutputVatDeferred`) ผ่านตามเดิม · payload ที่ขัดกับค่าตั้งมัดจำของบริษัท ⇒
    หมายเหตุภายใน (`DepositPolicyResolver.IntegrationMismatchNote`) ทั้งตอนสร้างและ resync — **รอบ 194 (ทีม C · spec S5)** คำนวณผ่าน
    `DepositKindCatalog.Decide` → `ResolveKind` (ประเภทที่คู่ค้าระบุ `depositKindCode` → ประเภทเริ่มต้น → ค่าตั้งบริษัท · ไม่ส่งรหัส = ข้อความเดิมทุกตัวอักษร) ·
    `DepositKindPayloadRejectionAsync` ตรวจก่อนทุกเส้นของ `ProcessInvoiceAsync`: รหัสที่ไม่รู้จัก/ปิดใช้ ⇒ 400 · เงินประกันที่ต้องคืน
    (ประเภทที่ระบุ หรือ `DepositNature` ที่ตรึงบนใบมัดจำที่อ้าง) + `depositAppliedDrivesJournal` ⇒ 400 `DEP-SEC-DEDUCT` (ไม่ออกเลข) · ⚠️ drives บนใบ**เครดิต** ของ integration
    ยังลง mapping JE เต็มโดย drives ไม่มีผล (audit-deposit P1-8 — ด่าน `DEPOSIT-DRIVES-UNSUPPORTED` อยู่เฉพาะเส้น Create/Update ของ DocumentService)
  - **ผังบัญชีรายบรรทัดที่ partner ส่งมา (`AccountCode`)**:
    `BuildDocumentLinesAsync` resolve → `DocumentLine.AccountId` และ
    **ตรวจฝั่งก่อนเสมอ** — ฝั่งจ่าย (expense / payment_voucher /
    certificate_in_lieu / resync expense) ห้ามเป็นบัญชี **Revenue**, ฝั่งรับ
    (CN/DN / resync invoice) ห้ามเป็น **Expense** → โยน error กลับพร้อมรหัส
    บัญชีที่ผิด (sync log เก็บเหตุผลให้ partner แก้ mapping). ไม่แตะ Asset/
    Liability เพราะมัดจำ/สินค้าคงเหลือใช้จริง. เหตุผล: JE ที่ Dr บัญชีรายได้
    **สมดุลเป๊ะและมีขาเจ้าหนี้ครบ** ⇒ `JournalPostingGuard` จับไม่ได้ และกำไร
    สุทธิไม่ขยับ (รายได้ต่ำไป = ค่าใช้จ่ายต่ำไป) — ไม่มีสัญญาณใดเลย.
    รหัสที่หาไม่เจอ/ปิดใช้งาน → log warning + ตกไปบัญชีทั่วไปตามเดิม
  - `/integration/journals` + `/integration/daily-summary` → JournalEntry ที่
    **ไม่มี SourceDocumentId** (รายงาน VAT มี fallback ใน `TaxService.cs:436+`
    สแกนหา JE ที่มี VAT account แล้วรวมเข้า ภ.พ.30 ให้)
  - **Idempotency (กัน retry สร้างเอกสารซ้ำ)**: ทุก inbound endpoint ที่สร้าง
    เอกสาร (invoice / creditnote / debitnote / **expense** / payment_voucher /
    certificate_in_lieu) เช็ค `ExternalRef` (→ `Document.Reference`) ก่อน; ถ้า
    partner **ไม่ส่ง ExternalRef** → fallback `TryFindDocumentByExternalIdAsync`
    ค้น sync log เดิม (`integrationId + eventType + ExternalId`, Success/Skipped,
    มี `CreatedDocumentId`) แล้วคืนเอกสารเดิมถ้ายังไม่ voided (`IntegrationService.cs`).
    `ProcessExpenseAsync`/`ProcessPaymentVoucherAsync` เดิม**ไม่มี** guard นี้ →
    เพิ่มแล้ว (เคยสร้าง expense ซ้ำเมื่อ retry)
  - **Resync update** (`ResyncUpdate=true` บน inbound invoice/expense):
    เจอ ExternalRef เดิม → แทน idempotent skip ระบบ "แก้เอกสาร + ปรับ JE"
    สองโหมดตามสถานะงวด (contract ระบบต้นทางเช่น TakeTime):
    • **งวดเปิด + JE เดิมใบเดียว → in-place**: แก้ JE ใบเดิม (เลข JE คงเดิม
      แทนที่บรรทัดทั้งชุด อัปเดต totals/วันที่) — audit ผ่าน Notes + sync log
    • **งวดปิด / มีหลาย JE → reversal**: กลับ JE เดิมทั้งชุด (คู่ Dr↔Cr,
      ลิงก์ Original/ReversedBy) + post JE ใหม่
    เลขเอกสารคงเดิมทั้งสองโหมด; response message ระบุโหมดชัด
    ("(in-place)" / "(reversal)") ให้ระบบต้นทางแสดงผลถูก.
    Guard: มีการชำระแล้ว / มี CN-DN ลูก / เดือนภาษียื่น ภ.พ.30 หรือ filing-lock
    แล้ว → คืน error ชัดเจน (ให้ void+ส่งใหม่ หรือออก CN แทน); sync log
    Status="Updated"
  - **CN/DN ผ่าน integration = ฝั่งขายเท่านั้น** (DTO มีแต่ field ลูกค้า) —
    `CreateCreditNoteJournalAsync`/`CreateDebitNoteJournalAsync` ลง AR/ภาษีขาย
    เสมอ; ใบลด/เพิ่มหนี้ฝั่งซื้อ sync ผ่าน expense reversal ไม่ผ่านช่องทางนี้
  - **ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี (ขายปลีก)** — 2 ทางเข้า:
    1. **Per-doc checkbox** `Document.BuyerDeclinedTaxInvoice=true` (UI ช่อง
       `#fBuyerDeclinedTaxInvoice` สำหรับ TaxInvoice/Receipt/ReceiptVoucher) —
       ผู้ใช้ติ๊กเองต่อใบ; ใช้กับลูกค้าที่ระบุตัวตนก็ได้ (มีชื่อ/ชื่อเล่น) ไม่บังคับ
       ผูก walk-in contact.
    2. **Walk-in contact** — เว้น customer fields ว่างทั้งหมด → ผูกผู้ติดต่อกลาง
       "ลูกค้าเงินสด (ไม่ประสงค์รับใบกำกับภาษี)" (`Contact.IsWalkInCustomer=true`,
       Address "-", สร้างครั้งเดียวต่อบริษัทผ่าน `GetOrCreateWalkInContactAsync`).
    ทั้งสองทางได้รับยกเว้น hard-block §86/4 ฝั่งผู้ซื้อตอน approve
    (`ApproveDocumentAsync` gate: `!doc.BuyerDeclinedTaxInvoice &&
    !Contact.IsWalkInCustomer`). นอกจากนี้ **branch code (§86/4 ประกาศ 199)
    บังคับเฉพาะผู้ซื้อนิติบุคคล** (`ContactType==JuristicPerson` หรือ TaxId 13 หลัก
    ขึ้นต้น "0") — บุคคลธรรมดาไม่บังคับสาขา.
    3. **Auto-downgrade (ไม่ block ทางตัน)** — เมื่อ §86/4 ผู้ซื้อไม่ครบตอน approve
       (`DocumentService.cs:~1991`): **แยกตามชนิดผู้ซื้อ** — (a) **นิติบุคคล** (ตั้งใจ
       เคลมภาษีซื้อ) → **block + ชี้ทางออก** ("เติมข้อมูล หรือ ติ๊กไม่ประสงค์รับใบกำกับ
       เพื่อออกเป็นใบเสร็จ") กัน downgrade เงียบ ๆ ที่ทำให้ผู้ซื้อเสียสิทธิ; (b)
       **บุคคลธรรมดา/ไม่มีเลขภาษี** (ขายปลีก) → ระบบ **auto ตั้ง
       `BuyerDeclinedTaxInvoice=true`** เอง → หัว downgrade เป็น "ใบเสร็จรับเงิน",
       **ไม่ block** (หลัก: เอกสาร §86/4 ไม่ครบ = ไม่ใช่ใบกำกับเต็มรูป จึงไม่ควรมีหัวว่า
       "ใบกำกับภาษี"). VAT ขายยังลง ภ.พ.30 ครบ.
    **เกณฑ์ "ข้อมูลผู้ซื้อครบ" — แหล่งเดียว**:
    `TaxInvoiceCompletenessChecker.MissingBuyerFields(buyer)` ใช้ร่วมทั้ง approve
    gate และการตัดสินหัวเอกสาร (เดิมเป็นโค้ดคนละชุด 2 ที่ → drift ได้)
    - **ผู้ซื้อทุกประเภท**: ชื่อ + ที่อยู่ (§86/4(3) บังคับแค่นี้)
    - **เฉพาะผู้ซื้อนิติบุคคล/จด VAT**: + เลขภาษี 13 หลัก + สาขา 5 หลัก
      (ประกาศอธิบดีฯ 194/199 — บังคับเฉพาะผู้ซื้อที่เป็นผู้ประกอบการจดทะเบียน)
    - นิติบุคคลตัดสินจาก `ContactType` **หรือ** เลขภาษีขึ้นต้น 0
    > ⚠️ เดิมบังคับเลขภาษี 13 หลักจากผู้ซื้อ **ทุกราย** → ขายให้บุคคลธรรมดาที่
    > ให้ชื่อ+ที่อยู่ครบก็ถูกตัดสินว่าไม่ครบ → auto `BuyerDeclinedTaxInvoice`
    > → หัวเป็น "ใบเสร็จรับเงิน" ทั้งที่ออกใบกำกับเต็มรูปได้ (ลูกค้าขอใบกำกับ
    > แล้วไม่ได้ = ขัด §86) และเกิดสภาพ "ใบเสร็จโผล่ในรายงานภาษีขาย"

    **หัวเอกสาร**: `ComputeDocumentTitle` เมื่อ `buyerDeclined` — per-doc flag,
    walk-in, **หรือ `Buyer864Incomplete(doc)`** (= `MissingBuyerFields` ไม่ว่าง,
    เฉพาะเอกสารมี VAT ไม่ใช่มัดจำพักรอ) → ไม่ upgrade เป็น "ใบกำกับภาษี/ใบเสร็จ
    รับเงิน"; ถ้า DocumentType=TaxInvoice → downgrade หัวเป็น "ใบเสร็จรับเงิน".
    VAT ขายลงรายงาน/ภ.พ.30 ครบตามปกติ (ภาระ VAT ไม่ขึ้นกับหัวเอกสาร),
    ผู้ซื้อเคลมภาษีซื้อไม่ได้

    **"ใบเดียว 2 หน้าที่" ไม่ใช่ "ออกใบกำกับคู่กับใบเสร็จ"** — เมื่อข้อมูลผู้ซื้อ
    ครบ หัวจะเป็น **"ใบกำกับภาษี/ใบเสร็จรับเงิน"** ใบเดียวทำหน้าที่ทั้งรับเงิน
    (ม.105) และใบกำกับ (§86/4) ไม่ต้องพิมพ์แยกสองใบ (พิมพ์แยก = เสี่ยงเคลมซ้ำ)
    - **§86 บังคับออกใบกำกับ "ทุกครั้ง" ที่ tax point เกิด** ไม่ออก = เบี้ยปรับ
      2 เท่าของภาษีตามใบ (§89(5)) + ปรับอาญา (§90(12))
    - **ท.ป.4/2528 ข้อ 12 — คำเตือน "ยังไม่ได้หัก ณ ที่จ่าย" เป็น 3 สถานะ (รอบ 176)**
      เดิมเงื่อนไขมีแค่ ฝั่งซื้อ · มีคู่ค้า · ยังไม่กรอก WHT · ยอดสะสม ≥ 1,000 —
      **ไม่มีข้อไหนถามว่าเป็นค่าสินค้าหรือค่าบริการ** ทั้งที่การซื้อสินค้าไม่อยู่ในข่ายหัก
      ⇒ ใบซื้อของทุกใบที่เกินพันเด้งหมด (ผู้ใช้รายงาน 2026-09-18 · ใบร้านค้าปลีก 5,682.24)
      **ชั้นที่ 0 (คำตัดสินเจ้าของ 2026-09-18 · ปรับรอบ 178): กระดาษพูดก่อน —
      แต่ "ความเงียบ" นับเฉพาะใบที่สมบูรณ์** ("ถ้าเป็นใบกำกับภาษีที่สมบูรณ์ กระดาษชนะ")
      สองทิศไม่เท่ากันโดยตั้งใจ:
      · กระดาษ **มี** ส่วน "หัก ณ ที่จ่าย" ⇒ เตือน **เสมอ** (คำประกาศเชิงบวกของผู้ขาย
        ไม่ขึ้นกับความสมบูรณ์ของใบ) — ตัวอ่านคือ `Helpers/PaperWhtReader` ตัวเดียว
      · กระดาษ **ไม่มี** ⇒ เงียบ **ก็ต่อเมื่อ** ใบนั้นครบ §86/4 ตาม
        `Helpers/PaperTaxInvoiceCompleteness` (คำว่า "ใบกำกับภาษี" ที่ไม่ใช่อย่างย่อ ·
        ชื่อ/ที่อยู่/เลขผู้เสียภาษีผู้ขายที่ผ่าน checksum · ชื่อ+ที่อยู่ผู้ซื้อ · เลขที่ ·
        วันที่ · ≥1 บรรทัด · VAT แยกบรรทัดและกระทบยอดได้) — ใบที่ไม่ครบ ความเงียบของมัน
        **ไม่มีน้ำหนัก** ไหลไปชั้นถัดไป ซึ่งชั้นที่ 1 คือ**คำประกาศของมนุษย์**
        (ประเภทเงินได้ ม.40 ที่ผู้ใช้ตั้งเอง) ที่เดิมถูกข้ามไปทั้งที่แข็งกว่า
        ⚠️ **ไม่**เอารหัสสาขาและเลขผู้เสียภาษีผู้ซื้อมาเป็นเงื่อนไข — ช่องแรกมีตัวเติม
        ค่าตั้งต้น `00000` (ด่านที่ตรวจค่าที่ตัวเองเติม = ผ่านตลอดกาล) ช่องหลังกฎหมาย
        บังคับตามสถานะผู้ซื้อ ไม่ใช่ตามกระดาษ
      · ไม่มีกระดาษให้ดู (คีย์มือ/สร้างจากใบอื่น/อ่านข้อความไม่ออก) ⇒ **ไม่ใช่หลักฐาน**
        ไหลไปชั้นถัดไป (เกรด `Unknown` ≠ `Incomplete`)
      ตอนนี้: `Helpers/WhtApplicabilityEvidence.Judge` (pure + เทสต์) ตอบ 3 สถานะ —
      **สินค้า** (ทุกบรรทัดผูกสินค้าในระบบ) ⇒ เงียบ · **บริการ** (บรรทัดมีประเภทเงินได้
      ม.40 แล้ว หรือผูกรายการชนิด Service) ⇒ เตือน · **ยังไม่รู้** (บรรทัดอิสระจาก OCR)
      ⇒ **เงียบ เว้นแต่มีเหตุให้สงสัย** (คำตัดสินเจ้าของ รอบ 179: "มีเหตุให้สงสัยว่าเป็น
      ค่าจ้าง หรือ ค่าบริการ ค่อยขึ้นเตือนหัก") — "เหตุ" นิยามที่ `Helpers/WhtServiceHints`
      ตัวเดียว: พบคำบ่งชี้ค่าจ้าง/ค่าบริการ/ค่าเช่า/โฆษณา/ขนส่ง/วิชาชีพ ในบรรทัด
      **ที่ไม่ได้ผูกสินค้าใน master** และบรรทัดเหล่านั้นมียอดรวม ≥ 20% ของยอดใบ
      (เกณฑ์สัดส่วนคือตัวแยก "บริการที่เป็นเนื้อของใบ" ออกจาก "ค่าส่ง 50 บาทบนใบซื้อของ")
      · **ผู้รับที่พิสูจน์ได้ว่าเป็นบุคคลธรรมดา ⇒ ลดเกณฑ์เหลือ 10%** (รอบ 180) — กองเงินได้
      ที่หักได้เฉพาะบุคคลธรรมดา (ค่าจ้างแรงงาน ม.40(1) · ค่าจ้างรายบุคคล ม.40(2)) ผูกกับ
      คู่ค้าชนิดนี้ฝ่ายเดียว และคนธรรมดาส่วนใหญ่ไม่จด VAT ⇒ ชั้นที่ 0 ไม่ทำงานกับใบของเขา
      ⚠️ เป็น**ตัวลดเกณฑ์ ไม่ใช่ "เหตุ" ในตัวเอง** (ร้านโชห่วยที่จดเป็นบุคคลธรรมดามีเต็มไปหมด
      — ถ้าใช้เป็นเหตุเดี่ยว ใบซื้อของจะเด้งทุกใบ) และต้องพิสูจน์ผ่าน
      `WhtPayeeKind.IsProvenIndividual` (เลขบัตร 13 หลักที่ผ่าน checksum ขึ้นต้น 1–8
      หรือคำนำหน้าชื่อที่มนุษย์กรอก) — **ห้ามใช้ `ContactType` ดิบ** เพราะ default =
      `Individual` และมี 12 จุดที่ `new Contact` โดยไม่เคยตั้งค่า
      · **ไม่มีเหตุ ⇒ เงียบโดยไม่ถามชั้นเรียนรู้ด้วยซ้ำ** (ประหยัด token + คำตอบไม่เปลี่ยน
      สิ่งที่ผู้ใช้เห็น) แล้วบันทึกลง `InternalNotes` + `AuditLog` ผ่าน `RecordWhtSilence`
      ตัวเดียว (`RuleCode = WHT-NO-SUSPICION`)
      ⚠️ **ข้อแลกเปลี่ยน**: ใบค่าบริการที่ไม่มีคำบ่งชี้เลยจะเงียบ ⇒ ความเสี่ยง §54
      (ผู้จ่ายรับผิด) ตกที่บริษัท — แลกกับการไม่เตือนจนผู้ใช้ชินแล้วกดข้ามทุกใบ
      · **มีเหตุ** ⇒ ถามชั้นเรียนรู้ผ่าน `AiFeatureKey.WhtCategoryInference` (นักเรียนก่อน ครูทีหลัง):
      ตอบ `None` (มั่นใจ ≥ 0.70) ⇒ **เงียบ** + ทิ้งร่องรอย **สองที่**: บรรทัด
      `[WHT-ADVICE]` ใน `InternalNotes` (ผู้ใช้เห็น แต่แก้ได้) **และ** แถว `AuditLog`
      `WhtWarningSuppressedByModel` พร้อม `RuleCode`/`LegalReference` จาก
      `Helpers/WhtAdviceNote` (append-only + hash chain = หลักฐานที่ยกไปอ้างได้ ·
      กฎเหล็ก #2 ข้อ M) — ข้อความไม่มีเวลาในตัว จึงเทียบซ้ำได้ ⇒ กดอนุมัติกี่รอบ
      ก็ได้บรรทัดเดียว (ด่านคำเตือนรันใหม่ทุกครั้งที่กดอนุมัติ) แต่คำตอบที่**เปลี่ยน**
      ยังได้บรรทัดใหม่เสมอ · ตอบ `Skip` ⇒ **ปฏิเสธ** (แปลว่า "ยอดสะสมยังไม่ถึง"
      ซึ่งขัดกับการคำนวณเชิงกำหนดของเราเองที่เพิ่งบอกว่าถึงแล้ว) ⇒ เตือนตามเดิม
      · ตอบรหัสประเภทเงินได้
      ที่**มีอยู่จริงใน `ThaiWhtRateTable`** ⇒ เตือนว่า "ระบบสงสัยว่าเข้าข่าย" ·
      ตอบไม่ได้/ตอบนอกชุด/ปิด AI ⇒ **ยังเตือน** เพราะ "เหตุ" ยังอยู่ (คำที่พบบนรายการ)
      เพียงแต่บอกประเภทเงินได้ไม่ได้
      และยอดสะสมนับผ่าน **`Helpers/WhtCumulativeScope`** — ชุด "เอกสารที่แทนการจ่าย"
      (`PurchaseInvoice · Expense · PaymentVoucher · CertificateInLieu` + `DebitNote`
      เฉพาะฝั่งซื้อ) พร้อมกันนับซ้ำด้วย `RelatedDocumentId`
      ⚠️ **ห้ามใช้ `ArApScope.PayableTypes`** เป็นนิยามของยอดจ่าย — ชุดนั้นคือ
      "เอกสารที่เพิ่มเจ้าหนี้" สำหรับ aging และตัด PV/CIL ออกโดยตั้งใจ ⇒ จ่ายงวดละ 800
      สามงวดด้วยใบสำคัญจ่ายจะได้ยอดสะสม 0 ทุกงวด = ไม่เตือนสักงวด ซึ่งเป็นเคสที่กฎนี้
      ถูกสร้างมาแก้โดยตรง (ฝ่ายค้านรอบ 177)
      · คำตอบ `Skip` จากชั้นเรียนรู้ **ใช้ที่จุดนี้ไม่ได้** (แปลว่า "ยังไม่ถึงเกณฑ์"
      ซึ่งขัดกับการคำนวณที่เพิ่งทำ) · คำตอบ "ไม่ต้องหัก" ต้องมั่นใจ ≥ 0.70 จึงจะปิดคำเตือน
    - **ด่านเก่า §50 ถูกถอดออกแล้ว (รอบ 183 · D1-B1)** — เดิมมีด่าน WHT **สองตัว**
      ทำงานต่อกันในเมธอดเดียว: ตัวใหม่ (ข้างบน) และบล็อกเก่าที่ใช้
      `IsPureGoodsPurchaseAsync` + `CheckWhtThresholdAsync` ซึ่ง (ก) ไม่ดูความสมบูรณ์
      ของกระดาษ ⇒ ใบซื้อของร้านค้าปลีกที่ใบกำกับครบ §86/4 ก็โดนเตือน (ข) เชื่อ
      **ผังบัญชี**ว่าเป็น "ของ" ⇒ ค่าจ้างติดตั้งที่ถูกลงผัง 51xxx เงียบสนิท
      (ค) ครอบแค่ PV/Expense/PI ⇒ ใบรับรองแทนใบเสร็จ (CIL) หลุดทั้งกอง
      · อัตราที่ใช้เตือนอ่านจาก **`ThaiWhtRateTable.StatutoryRates` ตัวเดียว**
      (ของเดิมมีชุด `knownWhtRates` ของตัวเองที่มี 1.5%) และข้อความเตือนสร้างจาก
      ตารางนั้น ไม่พิมพ์อัตราซ้ำ
    - **ขอบเขตของด่าน = `Helpers/WhtGateScope.Applies`** ซึ่ง delegate ตรงไป
      `WhtCumulativeScope.Counts` (ไม่มีชุดที่สอง) — เดิมถาม
      `DocumentSide.IsPurchase(type)` **โดยไม่ส่งบทบาท** ทั้งที่ helper ตัวนั้นเขียน
      doc ของตัวเองไว้ว่าชนิดกำกวมต้องส่ง `OurRole` ⇒ **ใบลดหนี้ฝั่งขาย** ตกเป็น
      "ฝั่งซื้อ" และ **ใบขอซื้อ/ใบสั่งซื้อ/ใบรับสินค้า** เด้งคำเตือนหัก ณ ที่จ่าย
      ทั้งที่ยังไม่มีการจ่ายเงิน · `cnDnPurchaseSide` คำนวณครั้งเดียวที่ต้นเมธอด
      แล้วใช้ร่วมกัน 3 ด่าน (§82/5 · WHT · §86/10)
      ⚠️ **ผลข้างเคียงที่ตั้งใจ**: ด่าน §82/5 แคบลงตามไปด้วย ⇒ PO/PR/GRN และใบลดหนี้
      ไม่ถูกสกรีนภาษีซื้อต้องห้ามอีก (PO/GRN ไม่ลงภาษีซื้อจริงอยู่แล้ว · ใบลดหนี้เป็นการ
      *ลด*การเคลมจากใบต้นทางที่ถูกสกรีนไปแล้ว)
    - เอกสารที่ VAT เข้ารายงานแต่หัวไม่มีคำว่าใบกำกับ → `CollectApprovalWarningsAsync`
      เตือนตอนอนุมัติ (ไม่ block — ขายปลีกที่ลูกค้าไม่ขอใบกำกับเป็นเคสปกติ) และ
      **เมื่อผู้ใช้กด "ยืนยันทั้งที่มีคำเตือน" (`acknowledgeWarnings=true`)
      ระบบบันทึกร่องรอย 2 ที่**: `Document.InternalNotes` (หมายเหตุ**ภายใน** —
      ไม่พิมพ์ลงกระดาษ ต่างจาก `Notes`) + `AuditLog` `APPROVE-ACK-WARNINGS`
      (มี hash chain) — เดิมคำเตือนที่ถูก acknowledge หายไปเฉย ๆ ⇒ ใบที่อนุมัติ
      ทั้งที่รู้ว่าผิด §86 หน้าตาเหมือนใบที่ไม่เคยมีคำเตือน ไม่มีอะไรตอบผู้สอบบัญชี
      (`DocumentService.ApproveDocumentAsync` · echo กลับผ่าน
      `DocumentResponse.InternalNotes` และแสดงเป็นการ์ดสีเหลืองในหน้ารายละเอียด)
      `TaxService.NotFullTaxInvoice` ติดธงบรรทัดในรายงานภาษีขายว่า
      "[ไม่ใช่ใบกำกับเต็มรูป — ลูกค้าเคลมภาษีซื้อไม่ได้]" เพื่อให้เห็นทั้งงวดในที่เดียว
    - ติ๊กขอเคลมภาษีซื้อแต่ §86/4 ไม่ครบ → เตือนยอด VAT ที่จะพัก 11640 (`InputVatParkingNotice` · รอบ 190 · ดู §6.2h)
    - แก้ย้อนหลังใบที่ออกไปแล้ว: เติมข้อมูลผู้ซื้อ → **พิมพ์ใหม่จากใบเดิม**
      (เลขที่/ยอด/วันที่ไม่เปลี่ยน หัวจะ upgrade เอง) ไม่ต้องออกใบใหม่/ใบลดหนี้

    **เงินมัดจำ (IsDeposit) กับ VAT — 3 โหมด (รอบ 193 · คำตัดสิน #34 + คำชี้แจงเจ้าของ "ขึ้นกับประเภทธุรกิจ · ตั้งค่าได้ทั้งหมด")**:
    ตัวตัดสินตัวเดียว **`Helpers/DepositPolicyResolver`** (enum `DepositVatTreatment` · API ส่งเป็นชื่อ) · ลำดับชั้น: ที่พักตั้งทับ
    (`LodgingProperty.DepositVatTreatment`) → บริษัท (`CompanySettings.DepositVatTreatment` · NULL = ไม่เคยตั้ง) → ประเภทธุรกิจ
    (`Company.IndustryType` → `NatureOf` บริการ/สินค้า/ไม่ทราบ) · ค่าเริ่มต้นเมื่อไม่มีใครตั้ง = `VatImmediate` ทุกประเภท (บริการ = กฎหมาย ·
    สินค้า/ไม่ทราบ = พฤติกรรมเดิมทุกทางเข้า ⇒ ไม่เปลี่ยนใครเงียบ) · ไม่ทราบประเภท = `NeedsOwnerChoice` (หน้าตั้งค่าแสดงเด่น) ·
    บริการเลือกโหมดที่ไม่ใช่ VAT ทันที ⇒ คำเตือน `RD-78/1-DEPOSIT-VAT` (หน้าตั้งค่าบริษัท/ที่พัก · หมายเหตุภายในใบมัดจำ/การจอง · audit) ·
    สินค้า ⇒ แจ้ง `RD-78-DEPOSIT-VAT` · **วิธีหักที่ใบสุดท้ายไม่ใช่ค่าตั้งแยก** — อ่านย้อนจากช่องที่ตรึงบนใบมัดจำ (`OfDocument`) ⇒ ไม่มีคู่ที่ผิดกฎหมายให้เลือก

    **รอบ 194 ทีม A (แกนกลาง — สัญญาพร้อม · ทีม B/C/D ต่อสายเข้าเส้นเอกสาร/ตั้งค่า/ที่พัก · spec `erp-review/2026-09-25/spec-194.md`)**:
    **ลักษณะเงิน** (`enum DepositNature` PartOfPrice=1 · RefundableSecurity=2 · NonVatSupply=3) = ตัวกำหนด VAT · โหมด = วิธีบันทึก ·
    ตาราง `DepositKinds` ต่อบริษัท (`Models/Entities/DepositKind.cs` · seed `Helpers/DepositKindSeed` ตารางเดียว: ADVANCE (ราคา · โหมด NULL = ตามค่าตั้งบริษัท ·
    เริ่มต้น) + SECURITY (เงินประกัน · เต็มยอด) · โรงแรมชื่อเฉพาะ · อสังหาฯ + RENT-ADV นอกระบบ VAT · migration ตอนบูต + จุดสร้างบริษัท 3 จุด
    `EnsureSeededAsync`) · ตัวตัดสิน 6 ชั้น `DepositPolicyResolver.ResolveKind` (บนใบ → ช่องทาง → ค่าเดิมช่องทาง → เริ่มต้นบริษัท → ค่าตั้งบริษัท →
    ประเภทธุรกิจ) · รูปใบ `Helpers/DepositDocumentShaping.Apply` (decision null = ไม่แตะอะไร · เต็มยอด/นอกระบบ VAT = VAT 0 + deferred + หมายเหตุเมื่อตั้งทับ ·
    ไม่บังคับ 7% ให้บรรทัด 0) · ด่าน `KindProblem` (ราคา + เลื่อน VAT ต้องมีเหตุผล) · `KindWarning` (สินค้าเตือนแรงเท่าบริการ `RD-78(1)(b)`/`RD-78/1` ·
    `RD-PO73-SEC` · `RD-81`) · `SecurityDeductionProblem` (`DEP-SEC-DEDUCT`) · ริบ `ForfeitVatDecision` (ตามลักษณะ ไม่ใช่โหมด · NULL + ไม่ระบุ = มี VAT ·
    ธง `[DEPOSIT-LATE-VAT]`) · ใบตรึง `Document.DepositKindId/DepositNature/DepositKindName/DepositPolicyNote` (ใบเดิม NULL = ไม่ทราบ · **migration ไม่ UPDATE
    "Documents"**) · `NatureOf(RealEstate)` = ไม่ทราบ (เดิมบริการ) · คำเตือนสินค้าของ `Resolve` เดิมแรงขึ้น (รหัสเดิม) ·
    **ทีม B ต่อสายเส้นเอกสารแล้ว** (สร้าง/แก้/โคลน/ริบ/ด่านหัก/คำเตือนอนุมัติ — §3.7) · ทางเข้าที่พัก/CMS/integration ยังเดินเส้นเดิมจนกว่าทีม C/D ต่อสาย ·
    payload ที่ไม่ระบุประเภท = พฤติกรรมรอบ 193 ทุกตัวอักษร
    | โหมด | ใบมัดจำ (1,000 รวม VAT) | JE | เข้า ภ.พ.30 | ใบสุดท้าย |
    | --- | --- | --- | --- | --- |
    | `FullDeposit` = 1 รับเต็มยอด ไม่แยก VAT (เงินประกัน/ต้องคืน) | ใบเสร็จ VAT บรรทัด 0 + ธงพัก | Cr 217xx 1,000 | ไม่เข้า | เต็มราคา + ตัดชำระ (`ApplyDepositToInvoiceAsync`) |
    | `VatPendingUndue` = 2 VAT รอเรียกเก็บ (`DepositOutputVatDeferred=true` เดิม) | ใบเสร็จ — **ห้ามมีคำว่าใบกำกับภาษี** | Cr 217xx 934.58 + Cr 21913 65.42 | ยังไม่ จนกว่า `DepositOutputVatRecognizedAt` | เต็มราคา + ตัดชำระ (Dr 217xx + Dr 21913 / Cr ลูกหนี้) |
    | `VatImmediate` = 3 ออกใบกำกับทันที §78/1 (`Deferred=false` เดิม) | ใบกำกับภาษี/ใบเสร็จ (เลขชุด TIV เมื่อผู้ซื้อครบ §86/4) | Cr 217xx 934.58 + Cr 21911 65.42 | **ใช่** เดือนที่รับเงิน | **หักฐานมัดจำออกจากฐานภาษี** — แถว "หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี {เลข}" (`DocumentLabels.TotalDepositTaxInvoiced` · ทั้งสอง renderer · ตัวตัดสิน `DepositPolicyResolver.TaxedDepositDeducted` บนช่อง `Document.DepositBaseDeducted`) + รับรู้ฐานมัดจำตอนอนุมัติ |
    - ตัวเลขใบสุดท้าย 7,450 − มัดจำ 2,000 (VAT ทันที): ฐาน 6,962.62 − 1,869.16 = 5,093.46 · VAT 356.54 · เก็บ 5,450 · VAT ทั้งเรื่อง
      130.84 + 356.54 = 487.38 ครั้งเดียว · VAT ใบสุดท้ายคิดบนฐานของใบเอง ⇒ ผลต่าง ±0.01 อยู่ที่ VAT (ฐานไม่เพี้ยน · `LodgingDepositSettlement.RoundingDelta` ≠ 0 ⇒ หมายเหตุ + audit)
    - **มัดจำที่ออกใบกำกับแล้วหักได้แบบเดียวทุกเส้น (L2 N1)** — ฟอร์ม "ขายเงินสดใบเดียว" ที่ส่ง drives ⇒ `CreateDocumentAsync`/`UpdateDocumentAsync`
      แปลงเป็นหักฐานตอนบันทึก (`ConvertTaxedDrivesAsync` · ใบร่างแสดงยอดใหม่ก่อนอนุมัติ · ผสมมัดจำสองแบบ/ส่วนลดท้ายบิล % ⇒ ปฏิเสธพร้อมทางไปต่อ) ·
      **การรับรู้ฐานมัดจำอยู่ในธุรกรรมอนุมัติ** (`RealizeTaxedDepositDeductionsAsync` ใน `AutoPostToJournalAsync` · ผูก
      `JournalEntries.DepositRealizedForDocumentId` · รับรู้แล้วไม่ทำซ้ำ · ฐานไม่พอ = ล้มดัง) · เส้นขับ JE ที่ยังอ้างมัดจำออกใบกำกับเต็มจำนวน
      (`GuardDrivesGrossApplyAsync`) = **บล็อกทุกงวด** เท่าปุ่ม · integration ปฏิเสธก่อนออกเลข (ข้างบน)
    - **ฐานมัดจำอยู่ช่องของตัวเอง `Document.DepositBaseDeducted` (ฝ่ายค้านรอบสาม R3-1 · 04ce362)** — `BillDiscountAmount` = **ส่วนลดการค้าอย่างเดียว** ·
      ยอดก่อนหักท้ายบิล = `SubTotal + BillDiscountAmount + DepositBaseDeducted` (`DepositPolicyResolver.BillDeductionTotal` ตัวเดียวของทั้งสอง renderer — ห้ามบวกเอง) ·
      ทั้งสองลดฐานภาษีเหมือนกันจึงเฉลี่ยลงบรรทัดครั้งเดียวผ่าน `DocumentService.AllocateBillDeductions` (ตัวเดียวของสร้าง/แก้/`PreviewTotals`) แล้วแยกกลับสองช่อง
      (`SplitBillDeduction` · ส่วนลด + มัดจำเกินยอดขาย = ล้มดัง ไม่ตัดช่องใดเงียบ) · ตัวแปลง `ConvertTaxedDrivesAsync` คืน**ฐานมัดจำอย่างเดียว** และ**แทนที่**ค่าเดิม
      ตามเลขอ้างอิงชุดใหม่ (ไม่บวกซ้ำเมื่อเลือกมัดจำเดิมซ้ำ) · อนุมัติรับรู้ `DepositBaseDeducted − ที่รับรู้แล้ว` · ด่านค่าที่บันทึก `TaxedDepositDeductionProblem`
      (ต้องมีเลขใบมัดจำ · ห้ามหักสองชั้นกับ `DepositAppliedAmount`/ขับ JE · ห้ามปนส่วนลด %) ทั้งสร้างและแก้ · **กระดาษสองแถวแยก** (ส่วนลดท้ายบิล / "หักมูลค่ามัดจำ
      (ก่อน VAT) ตามใบกำกับภาษี {เลข}") ทั้ง HTML + QuestPDF + หน้ารายละเอียด `documents.html` · `DocumentResponse.DepositBaseDeducted` (echo) · snapshot revision ·
      migration **ครั้งเดียวจริง** `DatabaseMigrationHelper.DepositBaseSplitMigrationSql` (ฝ่ายค้านรอบสี่ R4-3 · c3820dce — เดิมคำสั่งย้ายรันทุกบูตในชุดที่กลืน error):
      บล็อก `DO` เดียว `pg_advisory_xact_lock(AdvisoryLockKey.For("db-migration","Documents.DepositBaseDeducted"))` → ตรวจ `information_schema.columns` →
      **มีคอลัมน์แล้ว = ไม่ย้าย** → `ADD COLUMN` → ย้ายค่าในธุรกรรมเดียวกัน (อนุมัติแล้ว = ยอดที่รับรู้จริง · ร่าง = ทั้งก้อน) · ทุกใบที่ย้ายติดหมายเหตุ
      `[DEPOSIT-BASE-SPLIT] ย้าย X …` (อนุมัติแล้ว: ให้ตรวจมัดจำลูกค้าถ้าก้อนนั้นมีส่วนลดการค้า · ร่าง: หมายเหตุบอกว่าถ้ามีส่วนลดรวมอยู่ห้ามอนุมัติ — สร้างใบใหม่จากฟอร์มแล้วลบร่าง ·
      **เป็นข้อความเตือน ไม่ใช่ด่านที่บล็อก** · P4-1) · ⚠️ P4-2 ไม่แก้: ใบอนุมัติช่วง d788c2a→198fb5c ที่รับรู้จากหน้าเงินมัดจำ (ไม่มี JE ผูก) ไม่ถูกย้าย · ฐานที่เคยบูตด้วย 04ce362 มีคอลัมน์แล้ว ⇒ ตรวจมือ ·
      **ฝั่งขายเท่านั้น** `DepositPolicyResolver.TaxedDepositDeductionAllowed(type, cnDnPurchaseSide)` (`DocumentSide.IsSales` · CN/DN/ใบส่งของได้เมื่อไม่ได้ระบุฝั่งซื้อ)
      ใช้ทั้งด่านบันทึกและการรับรู้ตอนอนุมัติ (ใบซื้อที่ตั้งผ่าน API ก่อนมีด่าน ⇒ ล้มดัง ไม่ลงรายได้ · P4-4) · ข้อความ "ส่วนลด + ฐานมัดจำเกินยอดขาย" ชี้ทางที่ฟอร์มทำได้:
      ลดส่วนลดท้ายบิล หรือเลือกมัดจำใหม่ด้วยยอดที่น้อยลง (P4-3) · ที่พักส่งฐานมัดจำเข้าช่องนี้
      (สร้าง + เช็คเอาต์ต่อ) · ตัวเลข: ขายสด 7,450 ส่วนลด 100 + มัดจำ 2,000 ใช้เต็ม ⇒ 4,993.46 / 349.54 / 5,343.00 รับรู้ 1,869.16 (เดิมล้ม "ขาด 100") ·
      มัดจำใช้บางส่วน ⇒ รับรู้ 2,000 ไม่ใช่ 2,100 (เดิมเกิน 100 เงียบ)
    - **เอกสารลูก (R3-1)**: **convert** สืบทอดฐานมัดจำตามสัดส่วนที่ยก (สูตรเดียวกับส่วนลดบาท) + เลขใบมัดจำ ⇒ ยอดลูกตรงแม่ · **ไม่รับรู้ซ้ำ**: "รับรู้แล้ว" นับ JE ที่รับรู้
      เพื่อใบแม่ด้วย (`RelatedDocumentId`) · **clone** พ่วงส่วนลดการค้า (เดิมหาย — ใบโคลนแพงกว่า) แต่**ไม่พ่วงฐานมัดจำ/เลขใบมัดจำโดยเจตนา** (ใบโคลน = การขายใหม่
      มัดจำใช้แล้ว ⇒ พ่วง = หักซ้ำ) · recurring ไม่พ่วง · กติกา call-site ห้าม clone อ้าง `DepositBaseDeducted`/`DepositAppliedRef`
    - **ด่านรอบสาม (04ce362)**: ใบขับ JE เก่าที่อ้างมัดจำออกใบกำกับ ⇒ `DepositPolicyResolver.DrivesGuardMessage` **ทางเดียว** (เปิดแก้แล้วบันทึกจากฟอร์ม ระบบแปลงและรับรู้ให้
      ตอนอนุมัติ · บอกตรง ๆ ว่าไม่ต้องไปรับรู้ที่หน้าเงินมัดจำ · ใบถือเลขจริงแล้ว = ยกเลิกถาวรแล้วสร้างใหม่ — R3-5) · ปุ่ม/Integration ยังใช้ `GrossApplyBlockedMessage` ·
      รับรู้ตอนอนุมัติล็อกแถวใบมัดจำ `FOR UPDATE` เรียงตาม Id แล้ว reload ก่อนอ่านฐานคงเหลือ (`LoadTaxedDepositsByRefAsync(lockRows: true)` — B2) · "ปนกัน" ตัดสินจาก
      เลขที่ชี้ใบมัดจำ VAT พัก/เต็มยอดจริง · เลขที่ไม่ใช่มัดจำใช้งาน (JV/ร่าง/ยกเลิก/พิมพ์ผิด) ได้ข้อความของเหตุนั้น (B3) · `UpdateDocumentAsync` ตรวจสถานะก่อนตัวแปลง
      + "หักมูลค่ามัดจำ" อยู่ในรายการช่องที่ห้ามแก้ย้อนหลัง §86/4 (B4) · แถวมัดจำบนกระดาษดู `DepositBaseDeducted` ไม่ดู `DepositAppliedAmount` ⇒ ที่พักมัดจำสองแบบพิมพ์ถูก (B5)
      · ⚠️ ที่รู้: ร่างที่มีฐานมัดจำแล้วเลือกมัดจำ VAT พักแบบขับ JE เพิ่ม ⇒ ด่าน "ห้ามหักสองชั้น" ล้ม (ฟอร์มยังไม่มีปุ่มล้างฐานมัดจำ — ทางไปต่อ: สร้างใบใหม่) ·
      ป้ายแถวมัดจำพิมพ์ `DepositAppliedRef` ทั้งสตริง (รวมเลขมัดจำ VAT พักที่ `MergeDepositRef` ต่อท้าย)
    - **void/purge ใบสุดท้าย** ⇒ `ReverseDepositRealizationsForAsync` กลับ/ลบ JE รับรู้ที่ผูกใบนั้น + คืน `DepositRealizedAmount` (ล้าง RecognizedAt เมื่อ JE มีขา 21913) ·
      กู้ใบแล้วอนุมัติใหม่ = รับรู้อีกครั้ง (ไม่นับ JE ที่ถูกกลับ)
    - deferred → recognize (`RealizeDepositAsync`): JE ย้าย 21913 → 21911 +
      stamp `DepositOutputVatRecognizedAt` → เข้า ภ.พ.30 งวดที่รับรู้ + หัว upgrade
    - มัดจำที่ถูก**หักเข้าใบปลายทาง** (`DepositAppliedToDocumentId`) → ใบมัดจำ
      **ไม่**ขึ้นรายงานซ้ำ (ใบปลายทางรายงาน VAT เต็มใบแทน — กันนับซ้ำ/เคลมซ้ำ)
      **ใช้กับทั้งเคส deferred และ immediate** — เดิม immediate ที่ถูกหักแล้วยัง
      ถูกนับอยู่ ทั้งที่ JE กลับ Dr 21911 ไปแล้ว → ภ.พ.30 เกินจริงตามยอดมัดจำ

    **มัดจำหลายรอบ → ใบกำกับใบเดียว** (`ApplyDepositToInvoiceAsync` เรียกซ้ำได้
    ใบละครั้ง; `MergeDepositRef` เก็บเลขครบทุกใบแบบ comma-separated):
    | รูปแบบ | มัดจำแต่ละรอบ | ใบกำกับปลายทาง | ถูกต้อง? |
    | --- | --- | --- | --- |
    | **A — มัดจำไม่ใช่ใบกำกับ** (`Deferred=true`, VAT พัก 21913) | ใบเสร็จรับเงิน ไม่เข้า ภ.พ.30 | **ใบเดียวเต็มจำนวน** | ✅ ระบบรองรับครบ |
    | **B — มัดจำเป็นใบกำกับ** (`VatImmediate`, Cr 21911) | ใบกำกับภาษี เข้า ภ.พ.30 งวดที่รับ | **หักฐานมัดจำก่อน VAT** (ข้างบน) | ✅ ปุ่ม "หักมัดจำ" ถูกบล็อก |
    | **ผิด** | ใบกำกับ เข้า ภ.พ.30 แล้ว | ใบเดียว**เต็มจำนวน** | ❌ VAT ซ้ำ — บล็อกทุกเส้น |
    - **Guard (รอบ 193 · P0-3)**: `ApplyDepositToInvoiceCoreAsync` + เส้นขับ JE block มัดจำที่ออกใบกำกับแล้ว (VAT ทันที) ที่จะหัก
      **เต็มจำนวน**เข้าใบที่คิด VAT เต็ม **ไม่ว่างวดยื่นแล้วหรือยัง** (`DepositPolicyResolver.GrossApplyBlocked` · `RD-86/4-DEPOSIT-TIV-DOUBLE`)
      พร้อมทางไปต่อ ① ใบสุดท้ายหักฐานมัดจำ ② ใบลดหนี้ใบมัดจำก่อน — _เดิมกันเฉพาะงวดที่ยื่นแล้วและเฉพาะปุ่ม ⇒ แถวภาษีขายของมัดจำ
      ถูกข้ามในรายงาน (`DepositAppliedToDocumentId`) = VAT ย้ายเดือน · ผู้ซื้อได้ใบกำกับสองใบ_ · มัดจำ VAT พัก/เต็มยอดหักได้ทุกงวดตามเดิม
    - สร้าง/แก้เอกสารที่ตั้ง `DepositAppliedDrivesJournal=true` บนชนิดที่ AutoPost ไม่อ่าน (ใบเครดิต) ⇒ `DEPOSIT-DRIVES-UNSUPPORTED`
      (`DepositPolicyResolver.DrivesJournalSupported` · เดิม silent no-op)
    - ตัดสิน deferred แบบ **GL-first**: flag หรือมีขา Cr 21913 จริงใน JE ใบนั้น
      (กันเคสที่ flag ไม่ได้ตั้งแต่ GL ลง 21913 ไปแล้ว)

    **ตาข่ายกันพลาดของวิธี B (มัดจำ deferred)** — วิธีนี้เก็บ VAT จากลูกค้าแล้ว
    แต่ยังไม่นำส่ง จึงต้องมีตัวไล่ให้จบ ไม่งั้นกลายเป็น "เก็บแล้วไม่ส่ง":
    | ความเสี่ยง | ตัวกัน |
    | --- | --- |
    | VAT ค้าง 21913 ไม่มีใครตาม (ลูกค้าเงียบ/งานยืด/ลืม) | `GenerateVatReport` ใส่ `report.Notes` เตือนใบที่ค้างเกิน 90 วัน + ยอด VAT รวม + เลขใบ — เตือน**ตอนเปิดรายงานเพื่อยื่น** ซึ่งเป็นจังหวะที่แก้ได้ทัน |
    | tax point เกิดก่อนส่งมอบ (ลูกค้าขอใบกำกับกลางทาง §78) | `RecognizeDepositOutputVatAsync` — Dr 21913 / Cr 21911 **โดยไม่แตะรายได้** (TFRS 15 แยกจากภาระ VAT) → เข้า ภ.พ.30 งวดที่ระบุ + หัวเอกสาร upgrade เป็นใบกำกับทันที (`POST /document/{id}/recognize-deposit-vat`) · UI: ปุ่ม **"🧾 รับรู้ VAT"** ในหน้าจัดการมัดจำ (โชว์เฉพาะใบที่ยังพักรอ) |
    | มองไม่เห็นว่าใบไหนค้างนาน | ป้าย **"VAT พักรอ ⚠️"** ในหน้าจัดการมัดจำเมื่อค้างเกิน 90 วัน + modal บอกยอด/จำนวนวัน/คำเตือนเบี้ยปรับก่อนกดยืนยัน |
    | รับรู้เข้างวดที่ยื่น/ปิดไปแล้ว | block: งวดต้อง `Open` และยังไม่ Filed |
    | รับรู้ซ้ำ / รับรู้ทั้งที่หักเข้าใบปลายทางแล้ว | block (idempotent ผ่าน `DepositOutputVatRecognizedAt` + เช็ค `DepositAppliedToDocumentId`) |
    | ส่ง e-Tax จากใบมัดจำที่ยังไม่ใช่ใบกำกับ | block ที่ `EtaxInvoiceService.GenerateAsync` (§5.2 Receipt gate) |
    | หักมัดจำเกินยอดคงเหลือ/เกินยอดค้างใบปลายทาง | guard เดิมใน `ApplyDepositToInvoiceCoreAsync` (over-apply 3 ชั้น) |

### 2.3b Document revision (Rev.) — แก้เอกสาร operational ที่อนุมัติ/ส่งแล้ว

- **หลัก**: เอกสาร operational (ไม่มี JE / ไม่ขยับสต๊อก / ไม่เข้ารายงานภาษี —
  ไม่ใช่เอกสารภาษี §86/4) แก้หลังอนุมัติได้ตามธรรมเนียมการค้า: **เลขที่คงเดิม +
  Rev เพิ่มทีละ 1** (`Document.RevisionNumber`, 0 = ฉบับแรก) — ไม่ใช่ออกใบใหม่
  (การออกใบใหม่จะกินเลข running ที่ต้อง gap-free และทำให้คู่ค้าสับสนว่าใบไหนจริง)
- **ชนิดที่แก้ได้** — `DocumentService.RevisableTypes` (เกณฑ์เดียว: ไม่มี JE +
  ไม่ขยับสต๊อก + ไม่เข้ารายงานภาษี ครบทั้ง 3 ข้อ):

  | ชนิด | ทำไมต้องแก้ได้ | เหตุผลที่ปลอดภัย |
  | --- | --- | --- |
  | `Quotation` ใบเสนอราคา | ลูกค้าต่อราคา / เปลี่ยน spec | ยังไม่มีภาระผูกพันทางบัญชี |
  | `PurchaseOrder` ใบสั่งซื้อ | vendor แจ้งราคาใหม่ / ของขาด | ยอด commitment คำนวณสดจาก PO ที่เปิดอยู่ (`CheckBudgetCommitmentAsync`) → แก้แล้วยอดตามทันที ไม่มี ledger ให้กลับรายการ |
  | `PurchaseRequisition` ใบขอซื้อ | ผู้อนุมัติสั่งลดจำนวน/งบ | เอกสารภายใน ไม่แตะ GL |
  | `BillingNote` ใบวางบิล | ลูกค้าทักว่ามีใบที่ไม่ใช่ของตน | เป็นใบรวมยอดเรียกเก็บ ไม่ใช่เอกสารภาษี — ตัวหนี้จริงอยู่ที่ Invoice |
  | `DeliveryNote` ใบส่งของ | แก้รายการ/จำนวนก่อนส่งจริง | ไม่ขยับสต๊อก (สต๊อกขยับที่ Invoice/TaxInvoice — ดู §4.2) |

  **ห้ามใส่เพิ่ม** โดยไม่ตรวจ 3 เงื่อนไข: `Invoice`/`TaxInvoice`/`Receipt`/
  `CreditNote`/`DebitNote`/`PurchaseInvoice`/`Expense`/`PaymentVoucher`/
  `GoodsReceiptNote` — ทุกตัวมี JE และ/หรือเข้ารายงานภาษี/สต๊อก ต้องยกเลิก+ออกใบใหม่
  หรือออกใบลดหนี้/เพิ่มหนี้ตาม §86/9-10 เท่านั้น
- **เส้นทาง**: `UpdateDocumentAsync` เดิม (PUT /document/{id}) — เมื่อ doc อยู่ใน
  `RevisableTypes` + สถานะ Approved/Sent จะเข้าโหมด revision อัตโนมัติ:
  1. **Guard แปลงแล้ว** — มีเอกสารปลายทาง (RelatedDocumentId ชี้มา, ไม่ Voided)
     → block พร้อมบอกเลขใบปลายทาง (ดีลจบแล้ว เงื่อนไขใหม่ = เอกสารใบใหม่)
  2. **Guard หลักฐานผูกพัน** — `QuotationAcceptedAt` (ลูกค้ากดยอมรับ QT) **หรือ**
     `DeliverySignedAt` (ลูกค้าเซ็นรับของ POD บน DN) ตั้งแล้ว → ต้องส่ง
     `acknowledgeRevisionResetsAcceptance=true` ยืนยัน (ข้อตกลงเปลี่ยน =
     หลักฐานเดิมใช้ไม่ได้) — หลักฐานเดิมเก็บลง snapshot ก่อน reset เสมอ
  3. **Snapshot ก่อนแก้** → `DocumentRevisions` (header+lines+หลักฐานผูกพัน,
     JSON จาก `BuildDocumentSnapshot`) + `TotalAmount` denormalized — commit ใน
     SaveChanges เดียวกับการแก้
  4. Rev++ · reset ทั้ง acceptance token (QT) และ POD sign token/ลายเซ็น (DN)
     → ขอลิงก์ใหม่ให้คู่ค้ายืนยัน Rev ปัจจุบัน
  5. เส้นทาง revision **ข้าม** block "เลขจริงห้ามแก้" (เอกสาร operational ไม่ใช่
     เอกสารภาษี — เลขที่จึงคงเดิมได้)
- **PDF**: `DisplayDocNumber` (`PdfGenerationService.cs`) — Rev > 0 พิมพ์
  `<เลขที่> (Rev.N)` ทุก renderer (QuestPDF 3 จุด + HTML) ทุกชนิด
- **ประวัติ**: `GET /document/{id}/revisions` (list) + `/revisions/{n}` (snapshot
  เต็ม); UI: กล่องเหลืองในหน้า detail + ปุ่ม "ดู" เปิด snapshot
- **UI**: `DocumentResponse.CanRevise` / `CannotReviseReason` — server ตัดสินจาก
  ชนิด+สถานะ+เอกสารปลายทาง หน้าเว็บ **ไม่ hard-code รายชื่อชนิด** (เพิ่มชนิดใหม่ใน
  `RevisableTypes` แล้วปุ่มขึ้นเอง). ปุ่ม "✏️ แก้ไข (Rev ใหม่)" ใน detail →
  ถามเหตุผล (ลงประวัติ) + confirm ถ้าคู่ค้าเคยยืนยัน → ฟอร์มแก้ไขเดิม;
  รายการเอกสารมีป้าย `Rev.N` ให้เห็นตั้งแต่ list

### 2.4 Convert (แปลงเอกสาร)
- **Method**: `DocumentService.ConvertDocumentAsync` (full) / `ConvertDocumentPartialAsync`
  (partial — qty subset) — `DocumentService.cs:3082` / `:3122`
- **RelatedDocumentId ส่งเข้า CreateDocumentAsync ตั้งแต่ create** (ผ่าน
  CreateDocumentRequest) — ไม่ใช่เซ็ตทีหลัง เพราะ PaymentType inference /
  cash-settle / PV auto-approve ใช้ field นี้แยก "PV ตั้งต้น" กับ "PV settle
  ใบแจ้งหนี้ซื้อ" (บั๊กเดิม: PV แปลงจาก PI โดน auto-approve เป็น standalone
  cash ก่อนมีลิงก์ → PI ค้างชำระตลอด + JE ลงค่าใช้จ่ายซ้ำ)
- **กันสร้างซ้ำ**: `ComputeConsumptionAsync` — ตรวจ axis
  (`Delivery` / `Billing` / `None`) ที่ source line ถูกใช้ไปเท่าไหร่แล้ว
- **ข้อยกเว้น Invoice→TaxInvoice (ทั้งฉบับ)**: ถือเป็น "อัปเกรดชนิดเอกสารรับรู้
  รายได้ใบเดียวกัน" → **คัดลอกทุกบรรทัดเต็ม ข้าม consumption gate**
  (`wholeDocRevenueUpgrade` ใน `ConvertDocumentAsync`) — เดิม Invoice/TaxInvoice
  อยู่ Billing axis เดียวกัน gate ตัดบรรทัดที่มี child เก่าอ้าง → ใบกำกับ/ใบเสร็จ
  ยอดขาดไม่ตรงใบแจ้งหนี้. การแปลงซ้ำยังถูกกันด้วย ValidateConversionAsync
  (double revenue guard). partial ตั้งใจ → ConvertDocumentPartialAsync ตามเดิม
- **ด่านก่อนแปลง PO → PurchaseInvoice** (`ConvertCoreAsync` ต้นเมธอด · รอบ 135 · ERP_REVIEW E-05):
  ถ้า PO มีใบรับสินค้า (GRN) ที่ไม่ใช่ Voided/Rejected อ้างอยู่ → **บล็อก** พร้อมบอกให้แปลง
  จาก GRN แทน — เพราะ PI ที่ `RelatedDocumentId` เป็น PO จะไม่รู้จัก 21240 (GR-NI) ⇒ สต๊อกเข้า
  รอบที่สอง + Dr 11500 ซ้ำ + GR-NI ค้างตลอดกาล (แกน Delivery/Billing แยกกันจึงไม่มีด่านอื่นจับ)
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
  Receipt          → CreditNote / DebitNote   (เฉพาะใบเสร็จที่เป็นใบกำกับในตัว)
  ReceiptVoucher   → CreditNote / DebitNote   (เงื่อนไขเดียวกับ Receipt)

  PurchaseInvoice     → PaymentVoucher / CreditNote / DebitNote
  Expense             → PaymentVoucher / CreditNote / DebitNote / CertificateInLieu
  CertificateInLieu   → PaymentVoucher
  PaymentVoucher      → CreditNote / DebitNote   (เฉพาะ PV standalone จ่ายทันที)
  ```
  **Terminal** (no further conversion): `CreditNote`
- **Receipt/RV → CN/DN** (§86/9-10 อ้าง "กระดาษใบกำกับจริง"): เปิดเฉพาะใบเสร็จ
  ที่เป็นใบกำกับภาษีในตัว — ขายสด standalone (Cr 21911 เอง) หรือ settlement
  ถือ VAT §78/1 (เจ้าของแถว ภ.พ.30). guard: ใบมัดจำ → ใช้เมนู "คืนมัดจำ";
  ใบเสร็จหลักฐานรับเงินเปล่า (VAT=0 + อ้างต้นทาง) → ชี้ให้อ้างเอกสารตั้งหนี้แทน.
  JE: cash-refund mode (Cr เงินสด — ใบเสร็จ Paid เสมอ); stock **ไม่ขยับ**
  (Receipt/RV ไม่เคยตัดสต๊อกตอนขาย). + hard gate ตอน approve: CN/DN ที่มี VAT
  ต้องมี RelatedDocumentId หรือกรอกเลขใบกำกับเดิมในช่อง "อ้างอิง" (ใบเดิมนอก
  ระบบ/ก่อน migrate) ไม่งั้น block; warning เมื่ออ้าง "ใบแจ้งหนี้" (ไม่ใช่ใบกำกับ
  — ถ้าคู่ขายมี TIV/ใบเสร็จถือ VAT ต้องอ้างใบนั้น); DN ไม่กรอกหมายเหตุสาเหตุ → warn
- **ฟอร์มสร้าง CN/DN ตรง** (documents.html `cnSourceSection`): dropdown เลือก
  ใบกำกับ/เอกสารต้นฉบับของคู่ค้า (filter เงื่อนไขเดียวกับ ValidConversions —
  ตัดมัดจำ/ใบเสร็จเปล่า/PV settlement) → เลือกแล้วเติมบรรทัดจากใบเดิมให้แก้เป็น
  ยอดลด/เพิ่มจริง + ผูก `relatedDocumentId`; หรือช่องกรอกเลขใบเดิมนอกระบบ →
  `reference`. เส้นสร้างตรงมี guard ซ้ำตอน approve (AutoPost CN/DN block:
  มัดจำ/ใบเสร็จเปล่า/PV settlement) — กติกาเดียวกับ convert ทั้งสองทาง
- **PV → CN/DN**: PV standalone (จ่ายทันที = ตั้งหนี้+จ่ายในใบเดียว ไม่มี PI/Expense
  ให้อ้าง) — ผู้ขายส่งของพร้อมใบลดหนี้ทีหลังอ้าง PV ได้. PV แบบ settlement (อ้าง
  PI/Expense/CIL) → `ValidateConversionAsync` block พร้อมชี้ให้ออก CN อ้างเอกสาร
  ตั้งหนี้แทน (§86/10). จุดตรวจ "ฝั่งซื้อ" ทั้ง 4 รวม PV แล้ว: JE (`AutoPost` CN/DN
  branch), ภ.พ.30 (`TaxService` CN+DN), stock (PV → **ไม่ขยับ** เพราะ PV ไม่เคย
  รับของเข้าสต๊อก), e-Tax gate. + hard block คู่ค้าบน CN/DN ต้องตรงใบเดิม
  + approve warning §86/9-10 เมื่อลงวันที่ย้อนหลังเกิน 1 เดือนภาษีโดยไม่มี LateReason
- **Lineage**: ลูก carry `RelatedDocumentId = source.Id`, `SourceLineId` ต่อ
  บรรทัด (จำเป็นสำหรับ partial fulfillment + 3-way match)

#### 2.4b ใบกำกับภาษีเต็มรูป "แทน" ใบเสร็จ/ใบกำกับอย่างย่อ (§86/6 → §86/4) ✅ รอบ 130

**เคสจริง (ผู้ใช้ถาม 2026-09-03)**: ลูกค้ารับ "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"
ไปแล้ว ภายหลังกลับมาขอ "เต็มรูป" เพื่อเคลมภาษีซื้อ (อย่างย่อเคลมไม่ได้ §82/5(2))

**ทำไมไม่ใส่ `Receipt → TaxInvoice` ลง `ValidConversions`**: ใบเสร็จที่มี VAT
**นับเป็นภาษีขายเข้า ภ.พ.30 ไปแล้ว** (tax point = วันรับเงิน §78/1 — ดู
`TaxService.GenerateVatReport` branch "Receipt/ReceiptVoucher standalone")
⇒ การแปลงปกติจะได้ใบที่สองที่ AutoPost รายได้/ภาษีขายซ้ำ **และ** ถูกนับใน ภ.พ.30
อีกแถว. การขายครั้งเดียวมีใบกำกับได้ **ใบเดียว** (§86)

**จึงออกแบบเป็น "ใบแทน" ไม่ใช่ "ใบเพิ่ม"**:
- **Method**: `DocumentService.IssueFullTaxInvoiceForReceiptAsync`
  (endpoint `POST /api/{companyId}/document/{id}/issue-full-tax-invoice`
  — สิทธิ์ระดับเดียวกับ **Approve**)
- **ตัวตัดสินกลาง (pure)**: `Helpers/FullTaxInvoiceReplacement.Check` —
  บล็อกเมื่อ บริษัทไม่จด VAT · ใบต้นทางยังไม่อนุมัติ/ถูกยกเลิก · เป็นใบกำกับ
  เต็มรูปอยู่แล้ว · ไม่มี VAT · ออกใบแทนไปแล้ว · ผู้ซื้อไม่ครบ §86/4
  (ตัวเดียวกับที่ `DocumentResponse.CanIssueFullTaxInvoice` ใช้ ⇒ หน้าเว็บ
  **แสดง**อย่างเดียว ไม่มีสำเนากติกาฝั่ง JS)
- **ใบแทนสร้างผ่าน `ConvertCoreAsync`** (ยก `VatAmountOverride` รายบรรทัด ⇒
  ยอดตรงเป๊ะทุกสตางค์) แต่ใช้ **วันที่ของใบเดิม** — tax point เกิดไปแล้ว
  ย้ายงวดไม่ได้ (ย้าย = ภ.พ.30 ผิดสองเดือนพร้อมกัน)
- **ตอน approve ใบแทน (`ReplacesDocumentId != null`)** ข้ามทั้งหมด:
  `AutoPostToJournalAsync` · `ApplySourceDocumentAdjustmentsAsync` ·
  `ApplyProjectBillingAsync` · `ApplyStockMovementsAsync` ·
  `SupersedeSourceInvoiceAsync` — เศรษฐกิจของรายการไม่เปลี่ยน (เงิน/รายได้/
  ภาษีขายลงไปครบตั้งแต่ใบเดิม) เปลี่ยนแค่ **กระดาษที่ผู้ซื้อถือ**
  แล้วประทับ `ReplacedByDocumentId` + `ReplacedAt` + `InternalNotes` บนใบเดิม
  (ประทับตอน **approve** ไม่ใช่ตอนสร้าง — ไม่งั้นใบเดิมหลุดจากรายงานตั้งแต่
  ใบแทนยังเป็นร่าง = ภาษีขายนำส่งขาดโดยไม่มีอะไรเตือน)
- **รายงานภาษีขาย**: `GenerateVatReport` กรอง `d.ReplacedByDocumentId == null`
  (ทั้ง query หลักและ `deferredRecognized`) ⇒ ใบแทนเป็นเจ้าของแถว ยอด VAT
  รวมไม่ขยับ · `PullDocumentIntoReportAsync` block ใบที่ถูกแทนพร้อมชี้ให้ดึง
  ใบแทนแทน (เส้นดึงมือต้องมีด่านเดียวกับ loop หลัก)
- **โควตา**: `DocumentQuotaPolicy.Classify(..., isReplacement: true)` →
  `NotCounted` — การขายเดิมที่นับไปแล้ว (พารามิเตอร์ของ **เมธอด**
  `CreateDocumentAsync` ไม่ใช่ช่องใน DTO ด้วยเหตุผลเดียวกับ `originModule`)
- **Void ใบแทน** → ปลด `ReplacedByDocumentId` บนใบเดิม (คืนเข้ารายงานภาษีขาย)
  + ข้าม `ApplyProjectBillingAsync(-1)` / `ApplyStockMovementsAsync(-1)`
  (ไม่เคย +1 ตอน approve). **Void ใบเดิม**ถูก guard `activeChild` เดิมกันไว้
  แล้ว — ต้องยกเลิกใบแทนก่อน
- **หมายเหตุ**: `FullTaxInvoiceReplacement.ReplacementNote` ลง `Notes` ของใบแทน
  (**พิมพ์ลงกระดาษ** — ผู้ซื้อ/ผู้สอบบัญชีต้องเห็นว่าแทนใบไหน) ·
  `OriginalRecalledNote` ลง `InternalNotes` ของใบเดิม (ไม่พิมพ์)
- **เทสต์**: `Accounting.Tests/FullTaxInvoiceReplacementTests.cs`
- **Cascade**: `CustomAppendix / RevenueContractId / PerformanceObligationId /
  FileAttachment` (`CascadeAttachmentsAsync :3077`)
- **JE ของใบลูกดูประเภทต้นทาง (กันยอดเบิ้ล/ยอดหาย)**:
  - Receipt/ReceiptVoucher: **settlement mode (Cr AR) เฉพาะเมื่อ source ตั้ง
    ลูกหนี้จริง** (Invoice/TaxInvoice/DebitNote) — source เป็น
    Quotation/BillingNote (operational ไม่มี JE) → ลง **standalone**:
    Dr เงินสด / Cr รายได้ + VAT (เดิมเช็คแค่ `RelatedDocumentId.HasValue` →
    Cr ลูกหนี้ผี + รายได้ไม่ถูกบันทึก)
  - CertificateInLieu ที่อ้าง Expense/PI: **settlement เหมือน PV** — Dr AP /
    Cr เงินสด (+WHT ตาม basis) — เดิม Dr ค่าใช้จ่ายซ้ำเสมอ = ค่าใช้จ่ายเบิ้ล
    + เจ้าหนี้ค้างตลอดกาล; CIL เข้า settlementTypes (PaidAmount push + cap
    + revert ตอน void) แล้ว

### 2.5 CMS (เว็บไซต์ของฉัน) — Storefront commerce + booking
- **สร้าง/ลบเว็บไซต์** (`CmsSiteService.CreateSiteAsync` · `DeleteSiteAsync`) — คีย์ไม่ซ้ำ 4 ตัว
  (`IX_Sites_CompanyId_Slug` · `IX_Sites_CompanyId_Subdomain` · `IX_Sites_CustomDomain` ·
  `IX_SiteDomains_Domain`) **ไม่มีตัวไหนกรอง `IsDeleted`** แต่ `Site` มี global query filter
  `!IsDeleted` ⇒ ตรวจซ้ำด้วย query ปกติจะ "ว่าง" แล้วไปตายที่ 23505
  - ลบ = **ปลดคีย์ก่อนซ่อนแถว** — Slug/Subdomain/CustomDomain + ทุกแถว `SiteDomains` ของเว็บนั้น
    ย้ายผ่าน `Helpers/CmsRetiredSlug` (ความยาวคอลัมน์มาจาก `Helpers/CmsFieldLengths`)
  - สร้าง = อ่านคีย์ที่จองไว้ด้วย `IgnoreQueryFilters()` แล้วแยกทางตามที่มาของค่า:
    **Subdomain** ผู้ใช้พิมพ์เอง → `BusinessRuleException` (`CMS-SUBDOMAIN-TAKEN`) ให้เปลี่ยน ·
    **Slug** ระบบสร้างจากชื่อเว็บ ผู้ใช้ไม่มีช่องให้แก้ → `Helpers/CmsSlugUniquifier` เติม `-2`
    ให้เอง (โยน error = ทางตัน)
  - ไม่มีเส้นทางกู้คืนเว็บที่ลบแล้ว การปลดคีย์จึงไม่ต้องย้อนกลับ
- **Order flow** (`CmsCommerceService.cs`):
  - Customer checkout → `SiteOrder` + upload สลิป → `RecordPaymentSlipAsync`
    สร้าง `SiteOrderPayment` Status=`Pending`
  - Admin/webhook ยืนยันรับเงิน → `ConfirmPaymentAsync` (`POST /orders/{id}/confirm-payment`)
    ทำงาน 6 ขั้นรวด (idempotent):
    1. `SiteOrderPayment.Status = Confirmed` + `Order.PaidAmount/PaidAt`
    2. `SyncOrderToErpAsync` ถ้ายังไม่ sync — ใช้ `IDocumentService.CreateDocumentAsync`
       (เลข gap-free §86/4, tax point, VAT validation ครบ)
    3. `IDocumentService.ApproveDocumentAsync` → auto-post JE
       (Dr AR / Cr Revenue + Cr Output VAT 21911)
    4. `IDocumentService.CreatePaymentAsync` → Dr Cash/Bank / Cr AR (เคลียร์ลูกหนี้)
    5. `DeductStockAsync` — idempotent ตาม `line.StockDeducted` flag
    6. e-Tax: ขั้น 3 (`ApproveDocumentAsync`) ออกให้แล้วผ่าน hook — **ถอดการเรียก `GenerateAsync` ซ้ำ** (เดิมได้ "มี e-Tax แล้ว" เป็น LogWarning ทุกใบ
       และล้มเงียบ) · เรียก `IIssuedDocumentHooks.RunAsync` เฉพาะเมื่อ**ไม่ได้อนุมัติในรอบนี้** (ยืนยันซ้ำ/อนุมัติไว้ก่อน) (รอบ 193 S-02)
  - ทุก step fault-tolerant: ล้มเหลว → log + ไม่ rollback step ก่อนหน้า
- **Booking flow** (`CmsBookingService.SyncBookingToErpAsync`):
  - Map `BookingType` → DocumentType:
    - `Lead/Appointment` → `Quotation` (Draft, รอ admin confirm)
    - `Guaranteed` → `TaxInvoice` (Approved + JE auto)
    - `PrePayment` → `Receipt` ที่ `IsDeposit=true` → Cr 217xx ขายรอรับรู้
      + Cr 21911 VAT (§78/1 รับชำระแล้ว → เข้า ภพ.30 ทันที)
      ต่อมา realize ด้วย `RealizeDepositAsync` ตัด 217xx → 41000
    - **ประเภทเงินมัดจำ (รอบ 194 ทีม C · spec S4)**: ส่ง `DepositKindId:` = ประเภทเริ่มต้นบริษัท **เฉพาะเมื่อบริษัทตั้งค่ามัดจำเองแล้ว**
      (`DepositKindCatalog.CompanyConfigured` — ประเภทเริ่มต้นมีโหมดของตัวเอง หรือ `CompanySettings.DepositVatTreatment` ถูกตั้ง) ⇒ บริษัทที่ไม่เคยแตะ
      ได้ใบเดิมทุกตัวอักษร (VAT ทันที §78/1) · ตั้งแล้ว ⇒ DocumentService จัดรูปใบผ่าน `DepositDocumentShaping` ตัวเดียว
    - ⚠️ เส้นนี้ปิดด้วย Realize ไม่มีใบสุดท้าย ⇒ **ให้บริการเสร็จ** ตัดสินด้วย `Helpers/CmsBookingCancelPolicy.DecideOnComplete` ก่อนรับรู้:
      มัดจำ VAT ทันที/VAT รอเรียกเก็บ/บริษัทไม่จด VAT/นอกระบบ VAT ⇒ `RealizeDepositAsync` อัตโนมัติ (ไม่ส่ง `ForfeitAs` — นี่คือ "ใช้บริการแล้ว"
      ไม่ใช่ริบ) · **มัดจำเต็มยอด (VAT 0) ของบริษัทที่จด VAT ⇒ ไม่รับรู้อัตโนมัติ** (= รายได้ไม่มี VAT ไม่มีใบกำกับเลย) บอกผู้ใช้ให้ออกใบกำกับเต็ม
      แล้วหักมัดจำ · เงินประกันที่ต้องคืน ⇒ ไม่ใช่รายได้ · ส่วนที่เหลือของบริการไม่ถูกออกเอกสาร (audit-deposit P1-3)
  - **ยกเลิกการจอง (รอบ 194 · spec S6 C-1)** — `UpdateBookingStatusAsync` บันทึกสถานะการจองก่อน แล้ว `SettleErpDocumentOnCancelAsync` ตัดสินด้วย
    `CmsBookingCancelPolicy.DecideOnCancel`: **ใบมัดจำที่ออกแล้ว (`DocumentStatusRules.IsIssued`) ไม่ถูกยกเลิก** — คงเป็นหนี้สิน · ประทับ
    "ยกเลิกการจอง dd/MM/yyyy — รอตัดสินคืน (ใบลดหนี้) หรือริบ ที่ศูนย์มัดจำ" ลง `InternalNotes` ของใบและของการจอง · ผู้ใช้ตัดสินคืน (CN §86/10 ของเดือนที่คืน)
    หรือริบที่หน้า "เงินมัดจำ" _(เดิม `VoidDocumentAsync` ทั้งใบ = ลบภาษีขายเดือนที่รับเงินย้อนหลังทั้งที่ยังไม่ได้คืนเงิน · ขัด §86/4)_ ·
    ใบที่ไม่ใช่มัดจำ (ใบกำกับ Guaranteed / ใบเสนอราคา) และใบมัดจำที่ยังไม่ออก ⇒ `VoidDocumentAsync` เหมือนเดิม · **ล้มดัง**: ขั้น ERP ที่ล้ม
    (void/realize) ⇒ `ChangeTracker.Clear()` กันสภาพครึ่งทางถูกบันทึก · ข้อความ (`CmsBookingCancelPolicy.FailureNote`) ประทับบนการจอง +
    ตอบผู้กดใน `BookingResponse.ErpNotices` (หน้า `cms-edit.html` แสดงเป็นคำเตือน) — เดิมเป็นแค่ `LogWarning` · `required_call_site_check`
    ล็อก "ตัดสินก่อน void/realize" และห้ามเมธอดสถานะเรียก void/realize ตรง · ใบมัดจำที่ CMS void ไปแล้วก่อนรอบนี้ (ภาษีขายขาด) ยังไม่มี query
    ตามเก็บ — รอเจ้าของตัดสิน (L1 §7)
  - **อัตรา VAT ของ booking/lead/commerce** อ่านสถานะจด VAT ผ่าน `CompanyVatStatus` ตัวเดียวกับ §90/2 → `OutputVatRate.ForCompany` (รอบ 193 S-10 —
    เดิม `IsVatRegistered ? 7 : 0` / lead `VatRate: 7m` ⇒ บริษัทไม่จด VAT ได้ใบเสนอราคา VAT 7%) · lead: **บันทึกภายในไม่พิมพ์บนใบเสนอราคาอีก**
    (`Notes: null` + ต่อท้าย `InternalNotes` ของเอกสาร `[จาก lead LD-…]` — เดิม `Notes: lead.InternalNotes` หลุดถึงลูกค้า)
- **Gap (ยัง TODO)**:
  - Payment reconciliation (match `SiteOrderPayment.Reference` กับ bank statement)
  - Webhook gateway (Stripe/PromptPay) → ตอนนี้ admin กดยืนยันสลิปเอง

### 2.6 POS (Point of Sale)
- **Method**: `PosService.CompleteOrderAsync` (`Services/Implementations/PosService.Orders.cs:1286`) ·
  ออฟไลน์ `SyncOfflineOrderAsync` (`:816`) เดินลำดับเดียวกัน
- **ลำดับ (รอบ 193 · E-01)**: **ตัดสต็อกก่อน → ต้นทุน → JE** — `DeductSaleStockAsync` (`:1358`) ตัดผ่าน `IStockLedger`
  แล้วตรึงต้นทุนต่อบรรทัดที่ `PosOrderItem.CostOfGoodsSold` (`Helpers/PosCogsBooking.SaleLineCost`: เมนูสูตร = Σ ต้นทุน
  วัตถุดิบที่ ledger ตัดจริง **เฉพาะวัตถุดิบ `TrackStock`** (`RecipeCost` — ฝั่งซื้อ Dr 11500 เฉพาะ TrackStock) · สินค้า =
  `move.TotalCost` · อื่น ๆ = 0 · ต้นทุนติดลบ = `POS-COGS-NEGATIVE`) → `SaleTotal` ปัดครั้งเดียว → `CreateSalesJournalEntryAsync(…, saleCogs)`
  _เดิม JE ก่อนตัดสต็อก + COGS = ต้นทุน "ตัวแม่" ที่ TrackStock ⇒ เมนูชงสด COGS 0 ขณะวัตถุดิบออกจากคลังแล้ว_ ·
  `required_call_site_check` ล็อก "`DeductSaleStockAsync` ก่อน `CreateSalesJournalEntryAsync`" ทั้งสองเมธอด
- **ปุ่มสถานะ** `UpdateOrderStatusAsync` (`:150`) สลับได้เฉพาะในกลุ่มที่ยังเปิด (Open/InProgress/ReadyToServe/OnHold) ผ่าน
  `Helpers/PosOrderStatusTransition.Check` — เข้า/ออก Completed/Voided/Refunded ⇒ `POS-STATUS-TRANSITION` (ใช้ปุ่มปิดบิล/ยกเลิก/คืนเงิน)
  _เดิมเขียน `order.Status` ตรง ⇒ ปิดบิลโดยไม่ตัดสต็อก/JE หรือดึงบิลปิดแล้วกลับมาปิดซ้ำ_
- **Auto JE ทันที** ตอน complete order (ไม่ผ่าน Draft):
  - Dr Cash 1011 / Bank 1012 / Credit Card 1131 (ตาม PaymentMethod)
  - Cr Sales Revenue 41000 (net of VAT)
  - Cr Output VAT 21911 (7%)
  - Cr Tip Liability 21814/21819 (ถ้ามี) — **ไม่พบบัญชีทิป = `throw`
    `POS-NO-TIP-ACCOUNT` ปิดบิลไม่ได้** (รอบ 183 · D8-8) เดิมยัดเข้ารายได้ขาย
    + `LogWarning` ⇒ เงินที่ถือแทนพนักงานกลายเป็นรายได้ กำไรบวม หนี้หายจากงบ
    · ทิศเดียวกับฝั่งจ่ายทิป `TipPayoutService` ที่ throw อยู่แล้ว
    · บัญชีมาจาก `Helpers/TipAccountResolver` (21814 อยู่ใน `ChartOfAccountTemplates`
    ⇒ ผังมาตรฐานไม่มีทางเจอ throw นี้)
  - Dr COGS 51110 / Cr Inventory 11500 = ต้นทุนที่ออกจากคลังจริง (สูตร = วัตถุดิบ TrackStock) ที่ตรึงไว้บนบรรทัด ·
    ไม่พบผัง 51110/11500 = ข้าม COGS ทั้งขายและคืน (พฤติกรรมเดิม สมมาตร · backlog)
- **ใบกำกับภาษีอย่างย่อ (§86/6)** — เลขออกที่ `IssueAbbreviatedInvoiceNumberAsync`
  (`PosService.Orders.cs:1265`) โดย **`Helpers/AbbreviatedTaxInvoiceRule` เป็นตัวตัดสิน
  ตัวเดียว** (ใช้ร่วมกับเส้น PDF) + `Helpers/PosSlipHeader` ที่ถือกติกาเฉพาะของสลิป:
  1. ยังไม่จด VAT (§77/1) → ไม่ออกเลข หัวสลิป "ใบเสร็จรับเงิน"
  2. ยังไม่อนุมัติ ภ.พ.06 → ไม่ออก **เว้นแต่แอดมินแพลตฟอร์มปิดสวิตช์**
     (`SiteSettings.RequirePhoR06ForAbbreviatedTaxInvoice` — ตั้งต้น `true`)
  3. บิลไม่มี VAT → ไม่ออก
  4. **บิลผูกสาขาแต่สาขายังไม่มีรหัส 5 หลัก → ไม่ออก** (รอบ 183):
     `PosSlipHeader.BranchSeriesCode` คืน `null` แทนการเดา `"00000"` ซึ่งแปลว่า
     "สำนักงานใหญ่" ⇒ เดิมกระดาษของสาขาประกาศเท็จ **และ** เลขรันไปกินเล่ม
     สำนักงานใหญ่ (สองเล่มไม่ gap-free ตาม §86/4) · บริษัทที่ไม่มีสาขาเลย
     ยังได้ `00000` เหมือนเดิม
- **Tax Invoice เต็มรูป** (deferred): `IssueTaxInvoiceAsync` (`:578`) → สร้าง Document ใน
  Status=Approved (กัน JE ซ้อน) → **หลัง commit** เรียก `IIssuedDocumentHooks.RunAsync` (`:810` · รอบ 193 S-02 — e-Tax อัตโนมัติ
  ตามค่าตั้งเหมือนเส้นเว็บ · ล้ม = ป้าย `[ETAX-AUTO-FAILED]` บนใบ ไม่ทำให้ขายล้ม · ดู §3.2 ขั้น e-Tax) · ผู้ซื้อหาผู้ติดต่อด้วย
  `ContactTaxBranchKey` (เลขภาษี + `BuyerBranchCode` · §6.2i)
  - ด่านสิทธิ์ `[RequirePermission(PermissionKeys.DocumentRevenueApprove)]` —
    คีย์เดียวกับเส้นเอกสาร เพราะปุ่มนี้คือการอนุมัติเอกสารรายได้
  - ด่านกฎหมาย `Helpers/FullTaxInvoiceReplacement.Check` ตัวเดียวกับ
    `DocumentService` (ไม่จด VAT · บิลไม่มี VAT · ผู้ซื้อไม่ครบ §86/4 · ออกไปแล้ว)
  - **บรรทัดสร้างจาก `Helpers/PosTaxInvoiceLines` ตัวเดียว** (รอบ 183 · D8-1):
    Σ บรรทัด (รวม VAT) ต้องเท่า **เงินที่ลูกค้าจ่ายสำหรับสินค้า/บริการ**
    (`TotalAmount + RoundingAmount` = `NetAmount − TipAmount`) — ไม่ลงตัว =
    `throw RD-86/4-POS-LINE-SUM` ไม่ออกใบ · ส่วนลดท้ายบิล/คูปองเป็นบรรทัด**ติดลบ
    ที่แบ่ง VAT ติดลบไปด้วย** (§79 ฐานภาษีลดจริง — ไม่ใช่ VAT 0%) · บรรทัดปัดเศษ
    ไม่มี VAT · Σ VAT รายบรรทัด = `order.VatAmount` เป๊ะ (เศษไปบรรทัดใหญ่สุด)
    · **ทิปไม่อยู่บนใบกำกับ** (ถือแทนพนักงาน ไม่ใช่ค่าตอบแทนการขาย)
    _เดิมสร้างจาก `item.TotalAmount` = ยอด**ก่อน**ส่วนลด ⇒ ใบกำกับ 1,000 ทั้งที่
    ลูกค้าจ่าย 900 ⇒ ผู้ซื้อเคลมภาษีซื้อเกิน ผู้ขายรายงานภาษีขายไม่ตรง GL_
  - `BranchId`/`IssuerBranchCode` คัดจากบิล (snapshot ตอนปิดบิล) · `IsTaxInvoiceByLaw`
    ตัดสินด้วย `TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole` · อ้างใบย่อที่ลูกค้า
    ถือไปแล้วผ่าน `FullTaxInvoiceReplacement.ReplacementNote` + `ReplacementReason`
- **Refund/Void**: reverse JE + return stock
  - **COGS ที่กลับ = ส่วนของ COGS ที่บิลขายลงไว้** (`PosCogsBooking.RefundCogs` ตามสัดส่วนจำนวน · ปัด**สะสมระดับบิล**
    `round(after) − round(before)` ⇒ คืนครบกี่รอบ Σ = ยอดขายเป๊ะ) · ของเข้าคลังที่ต้นทุน ณ วันขาย (`RestockUnitCost` ·
    วัตถุดิบของสูตร: `LoadSaleUnitCostsAsync` `:1526` ถัวจาก movement ขาออกของบิล → `UnitCostOverride`) · บิลเก่า
    (`CostOfGoodsSold = null`) กลับตามสูตรขายเดิม (`LegacyUnitCost` — เมนูชงสดเก่ากลับ 0 ไม่ติดลบ) · ⚠️ จำนวนวัตถุดิบที่คืนยังตาม
    สูตรวันนี้ (movement ไม่มี `PosOrderItemId`) — ข้อจำกัดที่รู้
  - คำขอคืนที่ส่งบรรทัดเดียวกันหลายแถว = รวมต่อบรรทัดก่อนเทียบคงเหลือ · บรรทัด `IsDeleted` คืนไม่ได้
  - **คืนเงินบิลที่ JE ขายถูกกลับแล้ว** ⇒ `POS-REFUND-SALE-JE-REVERSED` (ใช้ "ยกเลิกบิล" แทน · `PosVoidSaleJournal.RefundBlockMessage`)
  - **Void บิล Completed** (`VoidOrderAsync` `:165` · `Helpers/PosVoidPlan` + `PosVoidSaleJournal`): สต็อกคืนเฉพาะ
    `Quantity − RefundedQuantity` · GL = กลับ JE ขาย **และ JE คืนเงินทุกใบ** (`Reference = "REFUND-{OrderNumber}"`) ⇒ สุทธิ = ส่วนที่ยังค้าง
    ทุกบัญชี · สาย JE ขาย (`LoadJournalChainAsync` เดิน `ReversedByEntryId` · กรอง `!IsDeleted`) ตัดสินด้วย `PosVoidSaleJournal.Decide`:
    ไม่มีใครแตะ = กลับใบเดิม · สุทธิศูนย์แล้ว (กลับด้วยมือ) = **ข้าม** + `AddChainedAuditLog` `PosOrder.VoidSaleJournalSkipped` ·
    กลับไม่ครบ/ร่าง/หาไม่เจอ = `POS-VOID-SALE-JE-PARTIAL` · มีการคืนแต่หา JE คืนเงินไม่เจอเลย = `POS-VOID-PARTIAL-REFUND` ·
    **กลับ JE ในธุรกรรมเดียวกับคืนสต็อก/เปลี่ยนสถานะ** — ล้ม = `POS-VOID-JE-REVERSAL` "บิลยังไม่ถูกยกเลิก" rollback ทั้งก้อน
    (เดิมกลับหลัง commit แล้ว `catch → LogError` ⇒ ยกเลิก "สำเร็จ" แต่ GL ไม่รู้) · ผลข้างเคียง: งวดปัจจุบันปิดอยู่ = ยกเลิกไม่ได้
  - **Refund คิดสัดส่วนหลังส่วนลดระดับบิล** — สูตรอยู่ที่ **`Helpers/PosRefundMath`
    ตัวเดียว** (`RefundOrderAsync` เรียก `Compute`): ยอดคืน =
    `Σ(LineGross × ratio) × discountFactor` โดย
    `discountFactor = (Σ LineGross − DiscountAmount) / Σ LineGross`
    - ⚠️ **`DiscountAmount` ตัวเดียว ห้ามบวก `CouponDiscountAmount` ซ้ำ** —
      `RecalculateOrder` รวมคูปองไว้ในค่านั้นแล้ว · สูตรเดิมบวกซ้ำ ⇒ **คืนเงิน
      ลูกค้าต่ำกว่าจริง** (บิล 1,000 คูปอง 100 จ่าย 900 แต่คืนได้แค่ 800) — รอบ 183 D8-2
    - ฐาน `Σ LineGross` นับเฉพาะบรรทัด `!IsDeleted` (`PosOrderItem` ไม่มี global
      query filter ⇒ บรรทัดที่ถูกลบเคยไหลมาเป็นฐาน ⇒ คืนเกิน)
    - ปัดทศนิยม **ครั้งเดียวที่ยอดรวม** · ServiceCharge/Tip เป็นรายการเสริมบนบิล
      ไม่คืนตามการคืนสินค้า (พฤติกรรมเดิม คงไว้โดยตั้งใจ)
- **Offline sync**: `SyncOfflineOrderAsync(ClientOrderId)` dedup
- **ขั้นตอนบริการ (pos-packages) · ประเภทคอมมิชชัน** (รอบ 193 M2): ฟอร์มส่งชื่อ enum (`Fixed`/`Percentage` — เดิม option `0/1`
  ⇒ เลือก "เปอร์เซ็นต์" แล้วเก็บเป็น Fixed) · ค่านอก enum = `POS-COMMISSION-TYPE` · `ServiceComponent.CommissionTypeConfirmedAt`
  ประทับเฉพาะเมื่อส่ง `commissionTypeConfirmed: true` · แถวเก่าค่า 0/ค่า 1 ที่ยังไม่ยืนยัน ⇒ ป้ายต้องตรวจ (`ServiceCommissionTypeReview.Judge`)
  + รายงาน `GET …/pos/packages/commission-review` (อ่านอย่างเดียว · **ไม่แปลงข้อมูลเก่า** — การขายยังคิดตามค่าที่เก็บ = คำถามเจ้าของ)
- **Gap (ยัง TODO)**:
  - Z-report consolidation (ปัจจุบัน 1 JE/order, ไม่มี shift-end batch)
  - มัดจำ/booking (217xx flow ยังไม่ enabled ฝั่ง POS)
  - WHT tip §50 ทวิ (เกิน 1,000/รอบ — niche)

### 2.7 Recurring
- **Service**: `RecurringTransactionService.cs:41`
- ความถี่: `Daily / Weekly / BiWeekly / Monthly / Quarterly / SemiAnnual / Annual`
- template เก็บใน `RecurringTransaction.TemplateData` (JSON)
- `AutoApprove=true` → ใบที่ generate ขึ้นจะถูก approve อัตโนมัติ (ทำตาม flow
  approve ปกติทุกขั้น — tax point, JE, stock, fixed asset)
- `AutoSendEmail=true` (`RecurringTransaction.AutoSendEmail`) → หลังสร้าง
  (+approve) ระบบ enqueue อีเมลแนบ PDF ถึง `Contact.Email` **ทันที**
  (`ScheduledFor = now` ไม่รอ 09:00) ผ่าน
  `EmailScheduleService.OnRecurringDocumentCreatedAsync:164`
  - ลำดับความสำคัญ: **มีกฎ `EmailScheduleRule.Trigger="RecurringInvoiceCreated"`
    อยู่แล้ว → ใช้กฎนั้น** (เวลา/เทมเพลต/BCC ตามกฎ) และ**ไม่**ส่งซ้ำจากธง;
    ไม่มีกฎเลยจึงใช้ธงบน recurring (rule เสมือน `Id=Guid.Empty` →
    `EmailQueue.RuleId=null`, idem key `doc:{id}:RecurringInvoiceCreated:autosend-{recurringId}`)
  - **invariant**: `AutoSendEmail=true ⇒ AutoApprove=true` บังคับทั้งใน
    `CreateAsync`/`UpdateAsync` และล็อกช่องบนฟอร์ม — ใบ Draft ยังเป็นเลข
    `DRAFT-{guid}` ตาม §86/4 ส่งออกหาลูกค้าไม่ได้ (guard เดิมใน
    `OnRecurringDocumentCreatedAsync` ตัด Draft/WaitingApproval/Rejected ทิ้ง)
  - **template สมุดรายวัน (`TemplateType="journal"`) ไม่รับธงนี้** — ปัดเป็น
    false ทั้งใน `CreateAsync`/`UpdateAsync` (JE ไม่มีคู่ค้า/PDF ให้ส่ง และ
    `AutoApprove` ฝั่ง journal แปลว่า "post JE อัตโนมัติ" คนละเรื่องกับที่
    ผู้ใช้ติ๊ก); ตัวตัดสินกลาง `IsJournalTemplate` ใช้ร่วมกับ dispatch ใน
    `ExecuteRecurringAsync`
  - ผู้ติดต่อไม่มีอีเมล → ข้าม + log warning (ไม่ throw); ฟอร์มเช็คให้ตั้งแต่
    ตอนบันทึก (`getContact`) แล้ว toast บอก · ผู้ส่งใช้ค่า
    `CompanySettings.Email*` ของบริษัท ถ้าไม่ได้ตั้ง → fallback อีเมลกลางระบบ
    (`EmailSenderFactory.GetGlobalFallbackSender`)
  - ฟอร์ม `pages/recurring.html` แสดงสถานะอีเมลบริษัทจริงจาก
    `GET /email-config` + ลิงก์ deep link `settings.html?tab=email`

### 2.7b ใบวางบิลรวมใบค้างชำระ (compose — ไม่ใช่ convert)
- **ทางเข้า**: ฟอร์มสร้างเอกสาร → ประเภท "ใบวางบิล" + เลือกลูกค้า → กล่อง
  "รวมใบค้างชำระ" (`documents.html #billingSourceBox`)
- **API**: `GET /document/billing-note/outstanding?contactId=` ·
  `POST /document/billing-note/from-invoices` (`DocumentController`)
- **Service**: `DocumentService.BillingNote.cs` — `CreateBillingNoteFromInvoicesAsync`
  · แหล่ง: Invoice / TaxInvoice / DebitNote สถานะ Approved/Sent/PartiallyPaid/
  Overdue + BalanceDue > 0 · ลูกค้าเดียวกันทั้งชุด · ใบละ 1 บรรทัด ยอด =
  BalanceDue, VatRate 0 (ยอดค้างรวม VAT ต้นทางแล้ว) · **ไม่ลง JE**
- **กันซ้ำ**: `DocumentLine.SourceDocumentId` → ใบเดียวอยู่ได้ในใบวางบิล
  active (ไม่ Voided/Rejected/ลบ) ใบเดียวเท่านั้น
- BN ที่ได้เป็น Draft — อนุมัติออกเลขจริง แล้วเดินสายเดิม (BN → Receipt เมื่อ
  รับเงิน / ตัวหนี้จริงยังตามที่ใบต้นทาง)

### 2.8 Quotation online accept (ลูกค้ากดยอมรับใบเสนอราคา)
- **สร้างลิงก์** (ต้อง login): `POST /document/{id}/quotation-accept-link` —
  เฉพาะ Quotation ที่อนุมัติแล้ว; token 64 hex อายุ 30 วัน เก็บบน
  `Document.QuotationAcceptToken(+ExpiresAt)`; เรียกซ้ำ = revoke ลิงก์เก่า
- **ฝั่งลูกค้า** (`PublicQuotationController`, AllowAnonymous):
  `GET /api/public/quotation/{token}` ดูรายการ+ยอด (sanitized) และ
  `POST .../accept` บันทึก `QuotationAcceptedAt/By` + stamp หลักฐานลง Notes
  (append-only) — **ไม่ auto-convert** เป็น invoice (ผู้ขายกดแปลงเองหลังเห็น
  การยอมรับ — กันเอกสารการเงินเกิดจาก anonymous click)
- **หน้า**: `pages/quotation-accept.html` (standalone, ไม่ใช้ Layout)
- ปุ่ม "🔗 ลิงก์ยอมรับ" ในหน้ารายการเอกสาร (Quotation Approved/Sent)
- **ลายเซ็นลูกค้าผ่านขั้นอนุมัติ** (`SignatureApprovalService.ExternalApproveQuotationAsync` · รอบ 193 O1 R3-3/B9): ถามคำเตือนก่อนบันทึกลายเซ็น (§3.2) ·
  ลายเซ็นเดิม**ใช้ซ้ำได้เฉพาะเนื้อหาเดิม** — ตัวตั้ง canonical ตัวเดียว `Helpers/DocumentSignedContent.Hash(doc, lines)` (**`v2:`** + SHA-256 · ครอบผู้ซื้อ ·
  สกุลเงิน/อัตรา · ราคารวม VAT · ส่วนลดท้ายบิล · ยอดหัวทุกช่อง · ครบกำหนด/เครดิต/เงื่อนไขชำระ/หมายเหตุ · ทุกบรรทัดที่ลูกค้าเห็น · **v2 (ฝ่ายค้านรอบสี่ R4-2 ·
  de5dc4cd) เพิ่ม**: วันที่เอกสาร · วันส่งมอบ · `CustomTermsAndConditions` · `CustomAppendix` · `CustomFooterNotes` · `BankAccountId` · `DocumentLanguage` · `BrandId` ·
  `IssuerBranchCode` · `Reference` · `BookingNumber` · `DepositAppliedRef` · `DepositAppliedAmount` · `DepositBaseDeducted` · `PaymentType` · ไม่ครอบ: แม่แบบหน้าตา ·
  `PreparerName` · ผังบัญชี/โปรเจกต์ · ช่องที่เกิดหลังอนุมัติ · ลายเซ็น v1 = แถวเก่า)
  เขียนตอนเซ็นลง `DocumentApproval.SignedContentHash` และคำนวณซ้ำตอนเรียกซ้ำด้วยฟังก์ชันเดียวกัน · `CanReuseSignature` = hash ตรง **และ** ลายเซ็น/ชื่อเดิม
  (hash ว่าง = แถวก่อนรอบนี้ = ใช้ซ้ำไม่ได้) · อย่างอื่น ⇒ ลายเซ็นเดิมถูก**แทนที่** (`SupersedeCustomerSignature` soft-delete ขั้น + `DocumentSignature` + เหตุผลใน
  `Comments` ⇒ ไม่ขึ้น PDF) แล้วบันทึกลายเซ็นใหม่ · ล็อกแถวเอกสาร `FOR UPDATE` ครอบ "ตรวจ → บันทึกลายเซ็น" (คำขอพร้อมกันไม่ซ้ำแถว · ไม่ใช้ unique index เพราะ
  ฐานมีแถวซ้ำเดิมแล้ว) · ⚠️ ลายเซ็นขั้นภายในยังไม่บันทึก hash (ไม่มีการใช้ซ้ำ)
- **ลายเซ็นลูกค้า "ยังนับไหม" — ตัวตัดสินตัวเดียว `DocumentSignedContent.IsSignatureCurrent(approval, doc, lines)`** (ฝ่ายค้านรอบสี่ R4-1 · de5dc4cd):
  ขั้นภายใน ⇒ ไม่อยู่ในกติกา · เอกสารออกแล้ว (`DocumentStatusRules.IsIssued` — เนื้อหาล็อก) ⇒ นับ (ใบเก่า/แถวไม่มี hash ยังพิมพ์ลายเซ็นเดิม — กฎ #4 H) ·
  ร่าง/รออนุมัติ/ถูกปฏิเสธ ⇒ นับเฉพาะ hash ตรง (ไม่มี hash / v1 = ไม่นับ) · ลายเซ็นคู่ค้า External อยู่ในกติกา · ข้อความเดียว `StaleReason` · ตัวแทนที่
  `DocumentSignedContent.Supersede` (soft-delete + เหตุผลต่อท้ายหมายเหตุเดิม) ใช้ร่วมสองเส้น · ผู้อ่านทุกเส้น: ① `ApproveDocumentAsync` คำนวณชุดที่ไม่นับ**ก่อน**ขั้นซ่อม
  บรรทัด · ด่านเซ็นครบ (`RequireApprovalForDocuments` เกินวงเงิน) ไม่นับ ⇒ 422 `SIGN-CUSTOMER-STALE` · ในธุรกรรมอนุมัติ ลายเซ็นที่ไม่นับถูกแทนที่ ⇒ PDF หลังอนุมัติ
  พิมพ์เฉพาะลายเซ็นที่ตรงเนื้อหา (ครอบกรณีปิด `RequireApprovalForDocuments` แล้วอนุมัติที่หน้า) ② `CheckAllApprovedAndProcessAsync` เซ็นครบแต่ลายเซ็นลูกค้าไม่นับ ⇒ 422
  พร้อมทางไปต่อ ③ PDF `ResolveSignersAsync` (HTML + QuestPDF ใช้ร่วม) ④ API `GetDocumentWithApprovalsAsync` (แถวไม่นับได้ `SignatureStaleReason`) +
  `GetDocumentSignaturesAsync` (ไม่ส่งภาพลายเซ็นที่ไม่นับ) ⑤ `CrossTenantWorkflowService.ApproveIncomingAsync` บทบาท Customer **บันทึก hash** (ทางเข้าที่สาม) ·
  ใบที่อนุมัติก่อนรอบนี้ด้วยลายเซ็นที่ไม่ตรงเนื้อหา ย้อนตรวจไม่ได้ (ไม่มี hash) — คงพิมพ์ตามเดิม

### 2.8b Delivery e-sign — ลูกค้าเซ็นรับสินค้าออนไลน์ (Proof of Delivery)
- **สร้างลิงก์**: `POST /document/{id}/delivery-sign-link` — เฉพาะ DeliveryNote
  ที่อนุมัติแล้ว; token 64 hex อายุ 14 วัน (`Document.DeliverySignToken`)
- **ฝั่งลูกค้า**: `GET/POST /api/public/delivery/{token}(/sign)` — วาดลายเซ็น
  บน canvas (มือถือ) + ชื่อผู้รับ → เก็บ `DeliverySignatureBase64/SignedAt/By`
  + stamp Notes; ลายเซ็น**ประทับลงช่อง "ผู้รับของ" (slot 1) บน PDF อัตโนมัติ**
  (`ResolveSignersAsync` override) พร้อมเวลาเซ็น (+07:00)
- **หน้า**: `pages/delivery-sign.html` (standalone signature pad)
- ปุ่ม "✍️ ลิงก์เซ็นรับ" ในหน้ารายการเอกสาร (DeliveryNote Approved/Sent)

### 2.9 Consignment (ฝากขาย)
- **Service**: `Services/Implementations/Consignment/ConsignmentService.cs`
- **Outbound dispatch** (`DispatchOutboundAsync`): ลด `CurrentStock` ทันที
  (ของอยู่ที่ลูกค้า กรรมสิทธิ์ยังเป็นเรา — ไม่มี GL) + **เขียน `StockMovement`
  คู่เสมอ** (เพิ่งแก้ — เดิมขยับ stock เปล่า ทำ stock card drift)
- **Consumption** (`RecordConsumptionAsync`): Inbound → สร้าง Draft
  `PurchaseInvoice`, Outbound → Draft `Invoice`; เอกสารใช้ **`DRAFT-{guid}`
  placeholder** ตาม convention กลาง (เดิมใช้เลข `CON-...` เองซึ่งหลุด series
  gap-free §86/4) + มี `Lines` + `SubTotal` ครบให้ approve ผ่าน gate ปกติ;
  ref consignment เก็บใน `Reference` (`CON-{id8}`)

---

## 3. Lifecycle — สิ่งที่เกิดในแต่ละ transition

### 3.1 Create / Update (Draft → Draft)
- **Update เฉพาะ Draft** — service guard: `doc.Status != Draft` throws
- **Field ที่ Update ได้** (เพิ่งเพิ่ม): `CreditNoteReason`, `IsForeignService`,
  `IsDeposit`, `DepositDeferredAccountCode`, `DepositOutputVatDeferred`
  (เดิม `UpdateDocumentRequest` ไม่มี → แก้ Draft แล้ว field เหล่านี้ "เงียบหาย")
- **เลขเอกสารยังเป็น `DRAFT-{guid}`** (ไม่ออกเลขจริง กัน gap §86/4)

### 3.2 Approve (Draft/WaitingApproval → Approved) — **ขั้นสำคัญที่สุด**

> **ทางเข้า approve มี 4 ทาง — ทุกทางวิ่งเข้า `ApproveDocumentAsync` เดียวกัน:**
> ① ปุ่มอนุมัติ/บันทึกและอนุมัติ (ตรง) ② กฎอนุมัติตามวงเงิน (ApprovalService
> gate — กฎ match แล้วปุ่มตรงถูกล็อคจน workflow ผ่าน) ③ ส่งเซ็นอนุมัติ
> (SignatureApprovalService — เซ็นครบทุกคน → เรียก ApproveDocumentAsync
> ให้อัตโนมัติ; **เดิมตั้ง Status ตรง ๆ ข้าม JE/สต๊อกทั้งหมด — แก้แล้ว**)
> ④ อนุมัติผ่านมือถือ (`MobileApiService.QuickApproveAsync` ขั้นสุดท้ายของ
> ApprovalRequest — **เดิมตั้ง `Status = Approved` ตรง ๆ เหมือน ③ ก่อนแก้ ⇒ เอกสาร
> "อนุมัติแล้ว" เลขยัง DRAFT-{guid} ไม่มี JE/สต๊อก แล้วรับชำระต่อได้ — แก้แล้ว รอบ 135
> (ERP_REVIEW B-02); ตีกลับผ่านมือถือเด้งกลับ Draft เหมือน ApprovalService ไม่ใช่ Rejected**)
> **ด่านสิทธิ์**: ทุกทางเข้าต้องผ่าน `DocumentPermissionHelper.CanApproveAsync` ก่อนถึง ApproveDocumentAsync —
> ① DenyDocAsync ② ApprovalService (ตามกฎ) ③ `SignatureApprovalService.RequireApproveAsync` (รอบ 135 · G-01)
> ④ `MobileApiService` (G-05) · LINE text/postback (G-03/G-04) — เดิม ③④+LINE มีแค่ [Authorize]
> กติกา: ห้ามเขียน `Document.Status` นอก `IDocumentService` — ทางเข้าใหม่ทุกทางต้องเรียก
> `ApproveDocumentAsync` (checker `document_status_writer_check` อยู่ในลิสต์ที่ควรมี §8 ของ ERP_REVIEW)
> RequireApprovalForDocuments (เกินวงเงิน) ยกเว้นให้เอกสารที่เซ็นครบแล้ว
> (กัน flow ที่ setting บังคับใช้โดน block ตัวเอง) — **"เซ็นครบ" ไม่นับลายเซ็นลูกค้าที่ไม่ตรงเนื้อหาปัจจุบัน** (`IsSignatureCurrent` · 422
> `SIGN-CUSTOMER-STALE` · รอบ 193 R4-1) และลายเซ็นที่ไม่นับถูกแทนที่ในธุรกรรมอนุมัติ (§2.8)

> **การรับทราบคำเตือน — แหล่ง 4 แบบ (รอบ 193 · คำตัดสิน #12 · `Helpers/ApprovalAcknowledgement` + enum
> `ApprovalAckSource {None, User, SystemWorkflow, ApiClient}` · overload ใหม่ของ `ApproveDocumentAsync`)**:
> - **User** (เว็บ/มือถือ — คนกด "รับทราบ"): `DocumentApprovalWarningsException` (422) → ส่งซ้ำพร้อม `acknowledgeWarnings=true` →
>   `[APPROVE-ACK]` บน `InternalNotes` + `AuditLog` `APPROVE-ACK-WARNINGS` · มือถือ `QuickApproveAsync` พรีวิวคำเตือน**ทุกชุด**
>   (`Warnings` + `RequiresAcknowledgement` · `MobileController ?acknowledgeWarnings=true`) และประทับผู้อนุมัติเป็น userId จริง
> - **workflow เว็บขั้นสุดท้าย** (`ApprovalService`) หยุด 422 ให้คนกดรับทราบ (`approval.html` ถามแล้วส่งซ้ำ · อนุมัติหลายรายการข้ามใบที่มีคำเตือน)
> - **ลายเซ็นขั้นสุดท้าย / ลายเซ็นลูกค้า** (`SignatureApprovalService`): ถามคำเตือน**ก่อนบันทึกลายเซ็น** → 422 + รายการ (ยังไม่มีอะไรถูกบันทึก)
>   → ส่งซ้ำพร้อม `acknowledgeWarnings` (ผู้เซ็น = ผู้รับทราบ) · ลายเซ็นครบแต่อนุมัติไม่ผ่านด่านอื่น ⇒ 422 "เซ็นครบ รออนุมัติด้วยมือ" (ไม่ใช่ 500)
> - **SystemWorkflow** (PV อนุมัติอัตโนมัติ · ใบแทน · ลายเซ็นที่ระบบประมวล): ผ่านคำเตือนทั่วไปได้พร้อมร่องรอย `APPROVE-SYSTEM-PASSED-WARNINGS`
>   (PV อัตโนมัติเขียนหมายเหตุภายในบนใบด้วย) · **`[Σ-GAP]` ผ่านไม่ได้**
> - **ApiClient** (`DocumentsV1Controller.Approve`): `PreviewApprovalWarningsAsync` (ไม่อนุมัติ · ไม่เรียก AI) → ถ้าคำเตือนเป็น gap ทั้งหมด
>   อนุมัติต่อด้วย ack ครั้งเดียว (`withAiHints:false`) แล้วคืน `scanAmountGap:true` + `warnings[]` ในโครงเดิม (**API ห้ามขัดจังหวะ**) ·
>   คำเตือนชุดอื่นยังทำตัวเดิม · ApiClient **ไม่ override** ด่านงบ/วงเงิน/วางบิลเกิน (เดิม ack:true ข้ามให้โดยบังเอิญ)
> - ⚠️ ผู้เรียกภายในที่ยังส่ง `acknowledgeWarnings:true` ตรง (ที่พัก ×2 · PlatformBilling ×3 · CMS ×2 ฯลฯ — เอกสารระบบสร้างเอง ไม่มี `[Σ-GAP]`)
>   ร่องรอยยังเขียนว่า "ยืนยันโดย" — ควรย้ายไป `SystemWorkflow` (backlog 7 จุด)
>
> **`[Σ-GAP]` ตอนอนุมัติด้วยมือ** (`Helpers/OcrApprovalGapWarning` ใน `CollectApprovalWarningsAsync`): ข้อความบอก "ตอนนี้รายการรวมเท่าไร ·
> กระดาษเท่าไร" จาก Σ บรรทัด (ไม่ใช่ `TotalAmount` ที่ตั้งตามกระดาษเสมอ) · ซ้ำ = ครั้งเดียว · แก้จนตรงแล้ว/ไม่รู้ยอดกระดาษ = ไม่เตือน

**`DocumentService.ApproveDocumentAsync` (`:4809`)** ทำตามลำดับ:

0. **ด่านงวดปิด** (`RequireOpenFiscalPeriodAsync` — `DocumentService.cs:12041`)
   — ทุกจุดที่ **จะลง JE จริง** ต้องผ่านก่อน: งวดของวันที่รายการต้องเป็น
   `FiscalPeriodStatus.Open` ไม่งั้น `BusinessRuleException` (HTTP 400 ข้อความไทย
   ที่บอกทางแก้ 2 ทาง: เปิดงวด หรือแก้วันที่). ครอบ **9 จุด**: ล้าง/คืนภาษีซื้อ ·
   รับรู้ภาษีขาย · รับรู้/คืน/ตัดชำระมัดจำ · รายการมัดจำ · รับ-จ่ายเงิน ·
   ตัดหนี้สูญ
   _(รอบ 125 · C-T04 — เดิมลงย้อนเข้างวดที่ยื่นแบบไปแล้วได้เงียบ ๆ ⇒ งบที่ยื่น_
   _กับบัญชีไม่ตรงกันโดยไม่มีอะไรเตือน · **เปลี่ยนพฤติกรรม**: เส้นที่เคยผ่านจะ_
   _เริ่ม 400 ถ้างวดปิดอยู่ — ดู "ผลกระทบก่อน deploy" ใน `SYSTEM_REVIEW_2026-09.md`)_
1. **Permission + workflow gate** (`:1638`) — ตรวจ ApprovalWorkflow
   (multi-level), credit limit ของลูกค้า (AR/AP advanced)
   - **§90/2 hard-block**: `CompanyVatStatus.IsRegistered(...)` = false (มีแถวค่าตั้ง = `CompanySettings.VatRegistered` · ไม่มี = ธงบริษัท —
     รอบ 193 S-01 · §7 แถว §90/2) → ห้ามอนุมัติ
     ใบกำกับภาษี (ทุกกรณี) และเอกสารขายที่ VatAmount > 0 (Invoice/Receipt/RV/
     BillingNote/CN/DN ฝั่งขาย — CN/DN ฝั่งซื้อที่ related เป็น PI/Expense/GRN
     ไม่ block); integration inbound invoice ก็ปฏิเสธด้วยเหตุผลเดียวกัน; ฟอร์ม
     สร้างเอกสาร (documents.html) ปิดตัวเลือกใบกำกับภาษี + ป้ายเตือน
   - **ภาษีซื้อฝั่งไม่จด VAT**: บริษัท `VatRegistered=false` → ทุกบรรทัดถูกบังคับ
     `IsVatClaimable=false` ตอน create/update (DocumentService) → posting รวม
     VAT เข้าต้นทุน/ค่าใช้จ่าย ไม่เข้า 11610/11640 (เคลมภาษีซื้อไม่ได้). เมนู
     ภ.พ.30/ภ.พ.30 ย้อนหลัง/ภาษีซื้อรอ (nav `vatOnly:true`) ถูกซ่อนใน layout.js
   - **Settlement doc self-paid**: PV/Receipt/RV/CIL ที่มี RelatedDocumentId
     (แปลงมาจากเอกสารตั้งหนี้) เมื่ออนุมัติ → ตัวมันเอง PaidAmount=Total,
     Status=Paid (เป็นเอกสารการจ่าย/รับเงินจริง ไม่ใช่ลูกหนี้/เจ้าหนี้ใหม่)
2. **AI warning collection** (`:1528`) — AI rule-based ตรวจหา anomaly
   (ราคาผิดปกติ, vendor ไม่ตรงประเภท ฯลฯ)
3. **ออกเลขจริง** — `DocumentNumberGenerator.NextAsync` — รูปแบบจริงในโค้ด =
   `{PREFIX}-{yyyyMMdd}-{NNNN}` (เลข running รีเซ็ต **รายวัน**, key ต่อ
   `(CompanyId, prefix)` ผ่าน `pg_advisory_xact_lock` กันเลขซ้ำใน transaction).
   วันที่ฝังในเลข → เลขไม่ซ้ำข้ามวัน; DB มี partial unique index
   `UX_Documents_CompanyId_DocumentNumber` เป็น backstop (ยกเว้น draft/soft-deleted).
   หมายเหตุ compliance: ยัง**ไม่**เป็น running ต่อปีภาษี/แยกสาขาแบบเต็มตาม §86/4
   (multi-branch) — ดู backlog "ปรับ scheme เลขเอกสาร" (ต้องมี migration path)
   · ตัวออกเลขใช้แค่ **prefix** ของ `NumberSeries` ⇒ API ลำดับเลข (`SettingsService.Create/UpdateNumberSeriesAsync`) ปฏิเสธ
   Suffix/Format/CurrentNumber/ResetPeriod/StartNumber ที่ต่างจากเดิม/ค่าเริ่มต้น — 400 `SET-NUMBER-SERIES-UNUSED-FIELD` ไทย "ไม่ได้บันทึกอะไร"
   (`Helpers/NumberSeriesFieldPolicy` · รอบ 193 S-20 — เดิมรับ-เก็บ-ตอบกลับเงียบ) · หน้าเว็บส่งแค่ `{documentType, prefix}`
4. **Snapshot Tax Point** (`:1760`) — `TaxPointResolver.Resolve(doc)` →
   `doc.TaxPointDate` = MIN(delivery / ownership transfer / payment received /
   invoice issue) ตาม §78 / §78/1 → ตัดสินงวด ภ.พ.30
5. **Retention** (`DocumentService.cs:5126`) — `doc.RetentionUntil ??=`
   **วันสิ้นรอบบัญชีที่เอกสารอยู่** `+ 5 ปี` (ไม่ใช่ `DocumentDate + 5y`)
   ตาม พ.ร.บ.บัญชี ม.10 + §87/3 · คำนวณผ่าน
   `Helpers.FiscalYear.FiscalYearOf(...)` → `RangeFor(...).EndInclusive`
   แล้วใช้ `MAX(fyEnd, DocumentDate)` เป็นฐาน
   _(รอบ 125 · C-T11 — สูตรเดิมสั้นไปเกือบ 12 เดือน และสั้นได้ถึง ~14 เดือน_
   _เมื่อรอบบัญชีไม่ตรงปีปฏิทิน · `DatabaseMigrationHelper` มี backfill_
   _`UPDATE "Documents" … GREATEST(…)` ให้แถวเก่า)_
6. **§65 ตรี** (`:1773`) — `ApplySection65TerAsync` → `doc.NonDeductibleAmount`
   + breakdown JSON (`NonDeductibleRuleJson`); ไหลเข้า ภ.ง.ด.50 ผ่าน
   `TaxService.GenerateCitReport` (บวกกลับ).
   **ครอบ**: `PurchaseInvoice` / `Expense` / `PaymentVoucher` / **`CertificateInLieu`
   ที่ไม่ได้แปลงมาจากใบตั้งหนี้** (`RelatedDocumentId == null`) — ตัว validator
   รองรับ CIL มาตลอด (`isPurchaseSide` ในเมธอด) แต่ call site เคยตกหล่น ทั้งที่
   CIL คือเคสที่ §65 ตรี(9)(18) เล็งตรงที่สุด (ผู้รับเงินออกใบเสร็จไม่ได้).
   CIL ที่แปลงมา = ใบต้นทางบวกกลับไปแล้ว เรียกซ้ำ = บวกกลับสองรอบ.
   **§65 ตรี(4) ค่ารับรอง cap = per fiscal year** (กฎกระทรวง 143) —
   `Section65TerValidator.Context.PriorYtdEntertainmentExpense` ส่ง YTD
   ของเอกสารฝั่งซื้อ/ค่าใช้จ่าย Approved ที่ description มี "รับรอง" →
   excess clamp ที่ใบปัจจุบันรับผิดชอบ. §82/5(6) vehicle warning bypass
   เมื่อ `CompanySettings.IsVehicleDealer=true`.
7. **Auto-post JE** (`:1789`) — `AutoPostToJournalAsync` แตกตาม `DocumentType`:
   - **Header JE สืบทอด `ProjectId` + `DimensionId` จากเอกสาร** — โครงการ
     (งานชั่วคราว วัดกำไรต่องาน) และ cost center/มิติ (สาขา/แผนกถาวร วัด
     ต้นทุนตามโครงสร้าง) เป็นคนละแกน เลือกได้อิสระทั้งคู่ในฟอร์มสร้างเอกสาร
     → รายงาน P&L ต่อมิติ (`getDimensionPnl`) มีข้อมูลจากเอกสารซื้อ-ขายจริง
   - sales: Dr AR / Cr Revenue + Cr Output VAT (21911 หรือ 21913 ถ้า
     deposit deferred)
   - **ผังบัญชีรายบรรทัดต้องอยู่ถูกฝั่ง** — create/update validate ผ่าน
     `EnsureLineAccountMatchesDocSide` (`DocumentService.cs:241`): เอกสาร
     ฝั่งขายล้วน (QT/INV/TaxInv/REC/RV/BN/DN-ส่งของ) ห้ามผูกผังหมวด
     ค่าใช้จ่าย, ฝั่งซื้อล้วนห้ามผูกผังหมวดรายได้ (CN/DN ยกเว้น — สองฝั่ง);
     ขา Cr รายได้ใน JE มี safety net `RevenueLegAccountId` — บรรทัดเก่า/
     บรรทัดที่ลอกมาจากการแปลงเอกสารซึ่งติดผังหมวดค่าใช้จ่าย ตกกลับบัญชี
     รายได้มาตรฐาน + log warning (กัน Cr รายได้เข้า 5xxxx). UI: per-line
     picker ใช้ datalist ตามฝั่ง (`coaList` ซื้อ / `coaListRevenue` ขาย —
     `documents.html _syncVatClaimColumn`)
   - **มัดจำ VAT พักรอ (21913) — การแสดงผล ≠ การลงบัญชี**: ใบเสร็จ/ใบสำคัญรับ
     ที่ `IsDeposit && DepositOutputVatDeferred` ยังไม่ใช่ใบกำกับภาษี (tax point
     ยังไม่เกิด §78) → PDF/HTML **ซ่อนบรรทัด "ยอดก่อน VAT" + "VAT 7%"**, หัวเรื่อง
     ไม่ขึ้น "ใบกำกับภาษี", บรรทัดรายการพิมพ์ยอดรวม VAT (Amount+VatAmount) ให้เท่า
     ยอดสุทธิ, ใส่หมายเหตุ "ไม่ใช่ใบกำกับภาษี" (`PdfGenerationService.IsDeferredVatDeposit`);
     **JE ยังแยก net/21913 ตามเดิม** (คนละเรื่อง) และ **ยกเว้น §86/4 gate** ตอน
     approve (ไม่บังคับ TaxId/ที่อยู่ผู้ซื้อ — ใบกำกับจริงออกตอนใช้บริการค่อยบังคับ).
     ตรงข้าม: มัดจำ tax point เกิดแล้ว (21911) = ใบกำกับจริง → โชว์ VAT ครบ
   - **sales COGS (perpetual — นโยบายเดียวกับ POS)**: Invoice/TaxInvoice
     ที่มีบรรทัดสินค้า TrackStock → Dr ต้นทุนขาย (51110/511) /
     Cr สินค้าคงเหลือ (11500/115) ที่ WAC ปัจจุบัน (`ComputeSalesCogsAsync`
     — ตรงกับ UnitCost ที่ stock movement stamp); ข้ามเมื่อ `IsDeposit`
     (ยังไม่ส่งมอบของ) หรือผัง 511/115 ไม่มี (log warning);
     COGS เป็น THB ไม่ผ่านการแปลง FX. ใบลดหนี้ฝั่งขายแบบ **Reason=Return**
     กลับ COGS ด้วย: Dr สินค้าคงเหลือ / Cr ต้นทุนขาย
   - purchase: Dr Expense + Dr Input VAT (11610 หรือ **11640** ถ้า §86/4
     ไม่ครบ) / Cr AP — **บรรทัดสินค้า TrackStock ที่ user ไม่ได้เลือกบัญชี
     เอง default เข้าสินค้าคงเหลือ** (`Product.InventoryAccountId` → 11500/115)
     แทนค่าใช้จ่าย (perpetual — สมมาตรกับ COGS ตอนขาย; ใช้กับ PI standalone,
     GRN, และ CN/DN ฝั่งซื้อ ผ่าน `BuildPurchaseLineAccountResolverAsync`)
   - cash receipt: Dr Cash/Bank / Cr AR (หรือ Cr 217xx ถ้า `IsDeposit`)
   - payment voucher: Dr AP/Expense / Cr Cash/Bank
   - WHT: Cr 21915/21916 ตามประเภทเงินได้
   - **ใบที่หักมูลค่ามัดจำ (ก่อน VAT) ตามใบกำกับภาษี** → `RealizeTaxedDepositDeductionsAsync` รับรู้ฐานมัดจำเป็นรายได้**ในธุรกรรมเดียวกัน**
     (ยอด = `DepositBaseDeducted` − ที่รับรู้แล้วเพื่อใบนี้/ใบแม่ · ล็อกแถวมัดจำ `FOR UPDATE` · ผังรายได้ = ผังของบรรทัดหลักของใบ · ผูก `DepositRealizedForDocumentId` ·
     รับรู้แล้วไม่ทำซ้ำ · ฐานไม่พอ = `RD-86/4-DEPOSIT-TIV-DOUBLE`) (§3.2 เงินมัดจำ)
   - **ใบสำคัญจ่ายที่จ่ายในตัว + `ActualPaidAmount` ≠ ยอดใบ** (คำตัดสิน #1/#4) → adjusting lines ต้องอธิบายส่วนต่าง**พอดี**
     (net = −ส่วนต่าง · `PaymentSettlementAdjustment.MatchTolerance` 0.005) + ขาเงินสดตามส่วนต่างที่ `moneyAccount` เดียวกับขาเงินสดหลัก ·
     ไม่มีบรรทัดปรับ = `BusinessRuleException` พร้อมทางไปต่อ · ใบที่ช่องนี้ไม่มีผล (ไม่จ่ายในตัว) = ปฏิเสธตั้งแต่บันทึก
     (`ActualPaidNotApplicableReason` · PV ที่มี RelatedDocumentId ลงเงินสดเสมอแม้ PaymentType=Credit — `PostsCashAtApproval(settlesSourceDocument)`) (§3.4)
   - **ผลต่างปัดเศษ** (`RoundingAdjustment` ≠ 0) → ขา **54960** เฉพาะเมื่อความไม่สมดุลเท่ากับผลต่างที่ประกาศ**พอดี** (ไม่งั้นด่านสมดุลเดิมฟ้อง) ·
     ทิศตามเครื่องหมายผลต่างและฝั่งเอกสาร (ใบซื้อ −0.01 = Cr · ใบขาย −0.01 = Dr) · ไม่มีผัง 54960 = `BusinessRuleException` บอกทางไปต่อ (§6.2j)
8. **Stock movements** (`:1794` → `ApplyStockMovementsAsync`) —
   switch ตัดสินตาม `DocumentType`:

   > ⚠️ **ตั้งแต่ POS เฟส 0**: เมธอดนี้ **ไม่เขียนสต็อกเอง** อีกแล้ว — ทุกการเคลื่อนไหว
   > เดินผ่าน `IStockLedger.MoveAsync` (`Services/Implementations/Inventory/StockLedger.cs`)
   > ซึ่งเป็น**ผู้เขียนสต็อกตัวเดียวของระบบ**: เขียน `WarehouseStock` (ความจริง) +
   > `StockMovement` (มี `WarehouseId` เสมอ) + ปรับ `Product.CurrentStock` ให้เท่าผลรวมทุกคลัง.
   > คลังของเอกสารมาจาก `ResolveWarehouseIdAsync(companyId, doc.BranchId)` — เอกสารผูก
   > **สาขา** ไม่ได้ผูกคลัง จึงต้องแปลงผ่านตัวกลางตัวเดียว. ขา void group ต่อ
   > (สินค้า, คลัง) เพื่อคืนของเข้าคลังที่มันออกไป. บังคับด้วย `tools/stock_writer_check.py`
   > (`StockMovements.Add` / `CurrentStock ±=` นอก `StockLedger.cs` = ฟ้อง)
   - **OUT (−1)**: `Invoice` / `TaxInvoice` (sale); **CN ฝั่งซื้อแบบ Return**
     (source = PI/Expense/CIL — เราคืนของให้ vendor = ของออกจากสต๊อกเรา)
   - **IN (+1)**: `GoodsReceiptNote` / `PurchaseInvoice` /
     `CreditNote when Reason==Return` (ฝั่งขาย — ลูกค้าคืนของ)
   - **มัดจำ (`IsDeposit`) → ไม่ขยับสต๊อก** (ยังไม่ส่งมอบ — ใบส่งมอบจริง
     เป็นผู้ตัด + ลง COGS)
   - **ขา void กลับตาม movement ที่เกิดจริง** (net ต่อ product ของ
     `DocumentId` เดิม, ต้นทุนเดิม) — ไม่ recompute จากกติกาปัจจุบัน →
     เอกสารเก่าที่ขยับด้วยกติกาเดิมกลับได้ถูก + void ซ้ำเป็น no-op
   - `PurchaseInvoice` ที่ผูก GRN accrual แล้ว → **ข้าม** (กันนับซ้ำ
     `:4633`)
   - **No-op (`_ => 0`)**: ทุกประเภทอื่น — รวมถึง `Receipt`,
     `ReceiptVoucher`, `DeliveryNote` (`DeliveryNote` ตั้งใจไม่ trigger
     เพราะ Invoice ที่ตามมาจะ trigger ให้ — กัน double-count `:4608`),
     `Quotation`, `PO`, `PR`, `BillingNote`, `DebitNote`, และ
     `CreditNote.Discount/Adjustment/Writeoff`
   - **UnitCost ของ movement ตาม CostingMethod** (TFRS NPAEs บทที่ 8):
     ซื้อเข้า (PI/GRN) = ต้นทุนจริง line net ต่อหน่วย + อัปเดต WAC running
     average; ขายออก = `AverageUnitCost` ปัจจุบัน (เมื่อ WeightedAverage,
     ไม่ใช่ `CostPrice` นิ่ง); void = ต้นทุนเดิมของ movement ต้นทาง
     (ให้กลับรายการหักล้างมูลค่าเท่ากัน). POS ใช้ helper `EffectiveUnitCost`
     เดียวกันทั้ง COGS JE / stock stamp / refund
9. **Fixed asset auto-register** — `AutoRegisterFixedAssetsAsync` (DocumentService)
   เป็นแค่ตัวห่อ: มอบต่อให้ **`FixedAssetService.RegisterFromDocumentAsync(companyId,
   doc, onlyLineId: null, actor, skipIfScanRegistered: true)`** ซึ่งเป็น**ตัวขึ้นทะเบียน
   จากเอกสารตัวเดียว**ของระบบ (รอบ 2026-09-10 — เดิมตรรกะทั้งก้อนอยู่ใน DocumentService
   และหน้าเอกสารไม่มีทางกดขึ้นทะเบียนย้อนหลังเลย ⇒ ใบที่อนุมัติไปก่อนมีผัง 12xxx
   หรือใบที่ตัวลงทะเบียนอัตโนมัติข้าม (ซ้ำ/ผิดพลาด) ค้างเป็น Dr 12xxx ที่ไม่มีคู่ในทะเบียน
   โดยผู้ใช้ไม่รู้). ทางเข้าตัวที่สอง: `POST /fixedasset/from-document/{docId}?lineId=`
   (สิทธิ์ `AssetManage`) — ขึ้นทะเบียน**เฉพาะบรรทัด** หรือทุกบรรทัดที่ยังไม่มีทะเบียน ·
   ด่าน `DocumentStatusRules.IsEffective` (ร่าง/รออนุมัติ/ยกเลิก → BusinessRuleException)
   · ชนิดที่รองรับ = `FixedAssetService.SupportsAutoRegister` (Expense · PurchaseInvoice ·
   PaymentVoucher — ตัวเดียวกับที่ขั้นอนุมัติใช้ ห้ามเขียนลิสต์ซ้ำ). ผลลัพธ์
   `RegisterFromDocumentResult(Created, Skipped, Assets, Notes)` — บรรทัดที่ข้ามต้องมี
   เหตุผลใน `Notes` เสมอ (ห้าม silent no-op).
   **ลิงก์สองทาง**: `GET /fixedasset/document-lines/{docId}` คืน
   `DocumentAssetLinesResponse` (รายบรรทัด: เป็นผังสินทรัพย์ไหม · เป็นค่าประกอบไหม ·
   `AssetId/AssetCode/AssetNeedsReview` ที่ผูกอยู่) → `documents.html`
   `renderDocAssetSection` วาดตาราง "🏭 ทะเบียนสินทรัพย์" ในหน้ารายละเอียด: บรรทัดที่มี
   ทะเบียนแล้วลิงก์ไป `/pages/fixed-assets.html?assetId=` · บรรทัดที่ยังไม่มีมีปุ่ม
   "➕ ขึ้นทะเบียนจากบรรทัดนี้" (เฉพาะใบที่ `IsIssued`) · ฝั่งทะเบียน
   (`fixed-assets.html` detail) มีช่อง "เอกสารต้นทาง" ลิงก์กลับ
   `/pages/documents.html?openDoc=<SourceDocumentId>` (`FixedAssetResponse` ส่ง
   `SourceDocumentLineId` ด้วย) — ทั้งสองหน้าอ่านจาก endpoint เดียวกัน ไม่คำนวณเอง.
   บรรทัดที่ลงผัง 12210 / 12220 / 12230 / 12240 / 12260 / 12270 / 12290 /
   12310 → group ตาม AccountId เพื่อหาค่าใช้จ่ายประกอบของผังนั้น แล้ววางแผน
   ด้วย **`Helpers/AssetRegistrationPlanner`** (pure ตัวเดียว — ห้ามเขียนกติกา
   นี้ซ้ำที่อื่น):
   - **บรรทัดจริง 1 บรรทัด = สินทรัพย์ 1 ตัว** ⇒ ใบเดียวซื้อหลายชิ้นในผังเดียวกัน
     (แอร์ 3 เครื่องบน 12210) ได้ทะเบียน **3 แถว** · เดิม group ตาม AccountId
     แล้วสร้างตัวเดียวราคารวม ⇒ จำหน่ายทีละเครื่องไม่ได้ · นับจำนวนทรัพย์สินผิด
     (ผู้ใช้รายงาน 2026-09-08)
   - **ค่าใช้จ่ายประกอบ** (ขนส่ง/จัดส่ง/ติดตั้ง/ฝึกอบรม/ค่าธรรมเนียม/ค่าบริการ/
     ค่าประกัน/shipping/delivery/freight/install/training/setup) = ต้นทุนที่ทำให้
     พร้อมใช้ตาม TFRS for NPAEs บทที่ 10 → **เฉลี่ยตามสัดส่วนราคา** เข้าทุกตัวใน
     กลุ่ม (บรรทัดสุดท้ายรับเศษ) ⇒ **Σ ต้นทุนที่ขึ้นทะเบียน = Σ ยอดบรรทัดในกลุ่ม
     เป๊ะเสมอ** (invariant · เทสต์ `AssetRegistrationPlannerTests`)
   - ทุกบรรทัดเป็นค่าใช้จ่ายประกอบ (ใบค่าติดตั้งเดี่ยวที่ผังลง PPE) → ยังขึ้น
     ทะเบียน 1 ตัว ไม่ปล่อยให้ Dr 12xxx ลอยโดยไม่มีคู่ในทะเบียน
   - `SourceDocumentLineId` = บรรทัดของสินทรัพย์ตัวนั้น (คีย์กันสร้างซ้ำตอน re-approve)
   - asset Description log auxiliary breakdown + ส่วนที่เฉลี่ยเข้าตัวนี้ ไว้ audit trail
   - **AssetCode = "DRAFT-{guid:14}"** placeholder (ไม่กิน counter)
     → user กดยืนยันใน `FixedAssetService.UpdateAsync` → generate
     `FA-yyyyMM-####` จริง (gap-free, ลำดับตามเวลายืนยัน)
   - `Status = Active, NeedsReview = true` พร้อม suggested `UsefulLifeMonths`
     + depreciation method (`StraightLine` default, ที่ดิน → `None`)
   - **Delete guard** (`FixedAssetService.DeleteAsync`):
     • block ถ้า AccumulatedDepreciation > 0 หรือมี posted depreciation
       → ต้อง Dispose/WriteOff
     • block ถ้า `SourceDocumentId.Status` ไม่ใช่ Voided/Rejected → ต้อง
       ยกเลิกเอกสารต้นทางก่อน (กัน orphan GL — Dr 12210 ใน JE ของใบยังอยู่
       แต่ asset register หาย)
   - **Void cascade** (`VoidDocumentAsync` step 7-asset): asset ของ doc ที่
     `NeedsReview=true` + ไม่มี posted dep → ลบ inline (ไม่เรียก DeleteAsync
     เพราะ source.Status ยังไม่ save), asset ที่ยืนยันแล้ว → log warning
     ไม่ลบ ผู้ใช้ต้อง dispose/write-off เอง
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
10. **Audit log** — append-only row + hash chain (`PrevHash + RowHash SHA-256` · สูตร v2 `AuditHashChain.Seal` · §6.1)
11. **Document number lock** — `IsDocumentNumberLocked = true` (กันแก้ภายหลัง)
12. **ผลข้างเคียงหลังออกเอกสาร — e-Tax อัตโนมัติ** (`:5631` → `IIssuedDocumentHooks.RunAsync` · รอบ 193 S-02 — เดิม private
    `TryAutoGenerateEtaxAsync` ที่มีแค่เส้นนี้) · อ่าน `EtaxEnabled` + `EtaxAutoSign` · idempotent (มี e-Tax ที่ไม่ใช่ Error = ข้าม) ·
    `GenerateAsync` ล็อกแถวเอกสาร `FOR UPDATE` + ตรวจซ้ำ (อีกตัวออกก่อน = ไม่ถือว่าล้ม) · ล้ม = **ไม่ throw** แต่ประทับ `[ETAX-AUTO-FAILED]`
    (`Helpers/EtaxAutoFailedNote` · `ExecuteUpdateAsync` แถวเดียว ไม่ save ทั้ง context) → `DocumentResponse.EtaxAutoFailed` = แถบแดงหน้าเอกสาร ·
    ล้างเมื่อ `GenerateAsync` สำเร็จ · **ข้ามโดยเจตนา (ไม่ติดป้าย)** ตาม `Helpers/EtaxAutoIssueScope.Judge` → `EtaxAutoSkip`: ไม่ใช่ชนิด e-Tax (T01–T04) ·
    ยังไม่ออก · Voided · มัดจำ VAT พักรอ · ใบเสร็จรับชำระใบกำกับ · CN/DN ฝั่งซื้อ (`AdjustmentNoteAccount.SourceIsPurchaseSide`) ·
    **ไม่ใช่ใบกำกับเต็มรูปโดยเจตนา** (`TaxService.NotFullTaxInvoiceByDesign`: ผู้ซื้อไม่ประสงค์รับ · walk-in · บุคคลธรรมดา — นิติบุคคลที่ §86/4 ไม่ครบ
    **ไม่ข้าม** ⇒ ไป `GenerateAsync` ที่ปฏิเสธพร้อมชื่อช่องที่ขาด = ป้ายล้ม) · `IsTaxInvoiceByLaw=false` (null = ใบเก่า ไม่ตีความ) ·
    ไม่รู้ใบต้นทาง = ไม่ข้าม (ให้ดัง) · **ทุกทางเข้าที่ประทับ `Approved` เอง เรียก hook เดียวกันหลัง commit**: POS ใบกำกับเต็มรูป ·
    Integration TIV/CN/DN(+Expense/PV/CIL — ข้ามในตัว) · CMS (เฉพาะเมื่อไม่ได้อนุมัติในรอบนี้) · ใบเสร็จ settlement (§78/1 ·
    `IssueReceiptForPaymentAsync`/`CreatePaymentAsync`) · **ไม่ออกโดยเจตนา** (baseline ของ `tools/approved_status_writer_check.py`):
    SampleData · PO ข้ามบริษัท · ยอดยกมา (`ImportOpeningSubledgerAsync` — ใบกำกับออกในระบบเดิมแล้ว) · CN คืนมัดจำ (`RefundDepositAsync` — ยังไม่ต่อ ·
    backlog) · ใบ **0%** จาก API = ใบกำกับ (`TaxInvoiceSeriesPolicy.IsZeroRatedFullTaxInvoice` · §2.3) · ⚠️ ใบ POS/API ที่ออกก่อนรอบนี้ไม่ถูกย้อนออก e-Tax ·
    e-Tax by Email **ยังไม่ส่งอัตโนมัติตอนอนุมัติ** (ช่อง `EtaxByEmailAutoSendOnApprove` ถูกล็อก + ป้าย "ยังไม่รองรับ" · ตัวส่งมีที่ปุ่มของแต่ละใบ)

### 3.3 Send (Approved → Sent)
- **Endpoint**: `POST /documents/{id}/send-email`
- **Controller**: `DocumentController.cs:43` → `IDocumentEmailService`
- **กระทำ**:
  - generate PDF (ถ้ายังไม่มี cache)
  - ถ้ามี EtaxInvoice + IsEtaxByEmail → cc `csemail@etax.teda.th` +
    subject = `{TaxId}.{DocNo}` (RD บังคับ)
  - log ลง `DocumentEmailLog` (Sent/Failed/Bounced)
- **หัวเรื่อง/ภาษาของทุกช่องทางที่ไม่ใช่ PDF = ตัวเดียวกับ PDF ที่แนบ** (รอบ 193 S-12): `PdfGenerationService.ResolveDocumentHeadingAsync(db, companyId, documentId)`
  (เดินขั้นเดียวกับ `GenerateDocumentPdfAsync`: เทมเพลต → `ResolveServedAsReceiptAsync` → `ResolveDocumentLanguage` → สิทธิ์ §86/6 → `ComputeDocumentTitle`
  · ส่วน pure `ComputeDocumentHeading` → `DocumentHeading(Language, Title)`) ใช้ใน อีเมลเอกสาร · e-Tax by Email (heading ของ `etax.DocumentId`) · LINE
  (`DocumentLineDeliveryService`) · อีเมลตั้งเวลา (`EmailScheduleService` · placeholder ใหม่ `{DocTitle}` · หัว default `{DocTitle} เลขที่ {DocNumber}`) —
  ตาราง `docTypeText`/`typeLabel` และ `coLang ??` ถูกลบ · **เนื้อ HTML ของอีเมล HtmlEncode** ชื่อลูกค้า/เลขเอกสาร/หัว (`Render(..., htmlEncodeValues: true)` ·
  หัวเรื่องคงข้อความล้วน) · ข้อจำกัด: e-Tax by Email ที่ render รวมล้มแล้ว fallback `GeneratePdfA3Async` (ไทยล้วน) อาจไม่ตรงหัว en
- **หน้าต่าง "ส่งอีเมล" ไม่ประกอบหัว/เนื้อเอง** — `GET documents/{id}/email-template?etax=` (`IDocumentEmailService.GetDefaultTemplateAsync` → resolver
  ข้างบน) · ค่าที่ยังเท่ากับที่ server เติม = ส่ง null (สลับ e-Tax หลังโหลดก็ยังตรง PDF) · ค่าที่ผู้ใช้พิมพ์ชนะ · ตั้ง checkbox e-Tax ก่อนโหลด ·
  ทิ้งคำตอบที่ไม่ใช่คำขอล่าสุด (seq) · ฟอร์มสร้างเอกสารปล่อยหัว/เนื้ออีเมลว่าง (server เติม)
- **ชั้นความลับ**: `email-template` และ `send-email` เดินด่าน `ISensitivityService.CanViewAsync` ตัวเดียวกับหน้าเอกสาร (ข้อความจาก `Helpers/SensitivityAccess`)
- **ไม่กระทบ JE / stock / VAT**

### 3.4 Pay / Partial Pay
- **Methods**: `CreatePaymentAsync` / `CreateMultiDocPaymentAsync`
  (`:534–541`)
- **Allowlist ประเภทที่ชำระตรงได้** (`PayableDocumentTypes`): Invoice /
  TaxInvoice / DebitNote / PurchaseInvoice / Expense เท่านั้น — ประเภทอื่น
  block พร้อมเหตุผล:
  - ใบวางบิล/ใบเสนอราคา/PO ฯลฯ (operational, ไม่มี JE ตอนอนุมัติ) → ชำระ
    แล้ว Cr AR ที่ไม่เคยถูก Dr / เข้า branch ผิดฝั่ง — ต้องแปลงเป็น
    Invoice/TaxInvoice/Receipt ก่อน
  - Receipt / ReceiptVoucher / PaymentVoucher / CertificateInLieu = เอกสาร
    เงินเข้า-ออก "จริงแล้ว" ตอนอนุมัติ → ชำระซ้ำ = เงินสดเบิ้ล
- **Receipt/ReceiptVoucher force `PaymentType=Cash` ตอน create** — ใบเสร็จ
  คือหลักฐานรับเงินแล้ว "เครดิต" ไม่มีความหมาย (เดิมปล่อย Credit ได้ →
  BalanceDue ค้างทั้งที่เงินเข้า GL แล้ว → โผล่ aging ผิด + ถูกชำระซ้ำได้)
- **DebitNote สองฝั่ง**: การชำระ + bank balance + void reversal ดูฝั่งจาก
  เอกสารต้นทาง (`IsCashInflowDocAsync`) — ฝั่งซื้อ (source = PI/Expense/CIL)
  = เงินออก Dr AP / Cr Cash (เดิมลงฝั่งเงินเข้าเสมอ — ผิดฝั่ง)
- คำนวณ `BalanceDue = TotalAmount − TotalPaid`
- → `PartiallyPaid` หรือ `Paid` อัตโนมัติ
- post JE: Dr Cash/Bank / Cr AR (sales) หรือ Dr AP / Cr Cash/Bank (purchase)
- **FX realized gain/loss**: เอกสารสกุลต่างประเทศใส่ `ExchangeRate` (rate วัน
  ชำระ) บน payment ได้ — เงินสดเข้า-ออกที่ rate วันชำระ, AR/AP ตัดที่ rate
  เอกสาร, ผลต่าง → 42600 กำไร / 54950 ขาดทุน (`ResolveFxGainLossAccountAsync`
  รองรับ 42600/4901 + 54950/5901 + ค้นชื่อ); เก็บ rate บน `Payments.ExchangeRate`
  เพื่อให้ void กลับยอดธนาคารด้วย rate เดิม; settlement Receipt/PV ข้ามใบที่
  rate ต่างกัน (ใบเสร็จ rate วันรับ vs invoice rate วันแจ้ง) ก็ post FX diff
  เช่นกัน; สิ้นงวด unrealized ใช้ `FxRevaluationService.PostAsync` (มีอยู่แล้ว)
  · **อัตรา ≤ 0 (รอบ 193 F/M2)**: บันทึกอัตรา `MidRate ≤ 0`/Buy·Sell ติดลบ/From==To = ปฏิเสธไทย · แถว 0 เก่าไม่ถูกลบแต่ตัวอ่านที่คำนวณ
  (`ConvertAsync` ตรง+กลับ · `GetLatestRateAsync` · กำไรขาดทุนยังไม่เกิดขึ้น) กรอง `MidRate > 0` ⇒ "ไม่มีอัตรา" ไม่ใช่ "อัตรา 0" · BOT sync
  **เขียนทับ**แถวที่ใช้ไม่ได้ของวันนั้น (`Helpers/CurrencyRateSync.Decide` · "ใช้ได้" = `IsUsableRow`: Mid > 0 หรือ Buy/Sell ครบ · ธปท. ส่ง 0 = ไม่เขียน) ·
  ตัวแนะนำอัตรา (`AiSuggestionController`) ไม่หยิบแถวที่ใช้ไม่ได้ · ⚠️ `CurrencyService` ยังนับ "มีอัตรา" ด้วย Mid > 0 อย่างเดียว
- **สกุลเงิน/อัตราของ *ตัวเอกสาร* ตั้งได้ตอนสร้างครั้งเดียว** —
  `CreateDocumentRequest.Currency/ExchangeRate` → `ResolveExchangeRateAsync`
  (THB→1 · override ชนะ · ไม่ระบุ = ดึงอัตรากลาง ธ.ปท. ของ `DocumentDate` ·
  ดึงไม่ได้ = **throw** ห้ามตกไปใช้ 1 เงียบ ๆ) แล้วทุกยอดที่ลง GL คูณผ่าน
  `ToGlAmount(doc, amount)` (`DocumentService.cs:12192`).
  `UpdateDocumentRequest` **ไม่มี**สองช่องนี้โดยเจตนา (อัตราถูกตรึงลง JE/AR-AP/
  ภ.พ.30 ไปแล้ว) ⇒ ฟอร์มตอนแก้ไขต้องแสดงค่าจริงแล้ว **ล็อกพร้อมบอกเหตุผล**
  (`documents.html` → `_hydrateCurrencyReadonly`) ห้ามโชว์ THB หลอกแล้วให้กด
  เปลี่ยนได้โดยไม่มีผล. _(A-D1: เดิม payload ของ `save()` ไม่ส่งสองช่องนี้เลย
  ⇒ ใบสกุลต่างประเทศทุกใบที่สร้างจากหน้าจอลงบัญชีเป็นบาทที่ยอดเดิม)_
- **ค่าธรรมเนียมหักจากยอดโอน** (marketplace Shopee/Lazada, gateway, ธนาคาร):
  `Payment.FeeAmount(+FeeAccountId)` — Amount คือเงินสุทธิที่เข้า, เอกสาร
  ถูกล้างที่ Amount+Fee: JE Dr เงินสด + Dr ค่าธรรมเนียม (53200/ค้นชื่อ) /
  Cr AR ยอดเต็ม; void คืน PaidAmount รวม fee; เฉพาะฝั่งขาย
- **ยอดชำระจริง ≠ ยอดใบกำกับ — ปรับที่ขั้นชำระ (รอบ 193 · คำตัดสิน #1/#3/#4)**: ใบกำกับ = เอกสารตั้งหนี้**ยอดเต็มตามใบ** (Shopee 536 · VAT 35.07
  เคลมเต็ม — #2 คงเดิม) · ส่วนต่างลงที่การชำระ: `Payment.SettlementAdjustmentAmount` + `SettlementAdjustmentsJson` (บรรทัดปรับ: ค่าส่ง +37 → **51120** ·
  คูปองแพลตฟอร์ม −135 → **51150** ส่วนลดรับ · "ส่วนลดพิเศษ X" ไม่มีรายละเอียด → 51150 ก้อนเดียว) · ตัวตัดสิน `Helpers/PaymentSettlementAdjustment.Check`
  (ยอดที่ปิด = เงินสด − Σ ปรับ · ห้ามจ่ายเกินผ่าน `DocumentSettlementState.WouldOverpay` · บรรทัดปรับห้ามผังเงินสด/เงินฝาก 111xx หรือผังที่ผูกบัญชีธนาคาร) ·
  `CreatePaymentAsync` (`:11227`) PI/Expense · THB เท่านั้น · ผังต้องมีและเปิดใช้ · `PaidAmount += เงิน + fee + ปรับ` · JE (`CreatePaymentJournalAsync` `:15975`):
  Dr เจ้าหนี้ 536 + Dr 51120 37 / Cr เงินสด 438 + Cr 51150 135 · **ชำระเงิน 0 + บรรทัดปรับ** = ปิดยอดค้างด้วยคูปองโดยไม่ต้อง void (ไม่มีขาเงินสด 0 ·
  ไม่หัก ณ ที่จ่าย/ไม่ออก 50 ทวิ · ส่ง WHT มาด้วย = ปฏิเสธ) · void คืนยอดรวมส่วนปรับ · **ชำระหลายใบพร้อมบรรทัดปรับ = throw** (ห้าม silent no-op) ·
  ช่อง `Document.ActualPaidAmount` (null = จ่ายเต็ม · echo ใน `DocumentResponse` · ฟอร์ม `fActualPaidAmount` ซ่อนฝั่งรายได้) — ใบตั้งหนี้แบบจ่ายทันที
  ช่องนี้ถูกปฏิเสธพร้อมทางไปต่อ · หน้าชำระเงินเติมข้อเสนอจากสแกน `GET /api/ocr/documents/{id}/settlement-proposal` (ด่านอ่านเอกสาร · แถว `rpSettleRow`
  DOM ล้วน) · ⚠️ ตัวจับคู่ statement ธนาคารยังเทียบยอดเอกสาร (536 ≠ 438) · พรีวิว JE ก่อนอนุมัติยังไม่รวมบรรทัดปรับ/ขาส่วนต่าง/ขาปัดเศษ (backlog) ·
  `RoundingAdjustment`/`ActualPaidAmount` ไม่สืบทอดไป PV ที่แปลงจาก PI (เป็นของเหตุการณ์จ่าย) · ผัง 51150 มีเฉพาะบริษัทที่มี 11500 + บริษัทใหม่
  (บริษัทเก่าที่ไม่มี = ข้อความ "ไม่พบผังบัญชี 51150")
- WHT cert auto-issue (`WithholdingTaxCertService` — ถ้ามี WHT บนใบ)

### 3.5 Void / Cancel
- **Method**: `VoidDocumentAsync` (`:1959`) — **cascade 7+ขั้น**:
  1. Reverse linked **Payments** → `ReversePaymentInternalAsync`
  2. Reverse posted JEs → `ReverseJournalEntryAsync` (สร้าง JE ใหม่ Dr↔Cr กลับ
     + link `OriginalEntryId/ReversedByEntryId`, ไม่ลบ JE เดิม)
  3. Void linked **e-Tax invoices** (soft — เก็บ XML ไว้ audit; รวมสถานะ
     `Submitted` ด้วย). **Guard ก่อน void**: block เฉพาะ e-Tax ที่
     `Accepted` (RD ตอบรับแล้ว = จุด no-return, ต้องยื่นขอยกเลิกที่ RD);
     `Submitted` (เซ็น/คิว ยังไม่ได้ตอบรับ) ยกเลิกได้ก่อนนำส่ง ภ.พ.30 —
     งวดที่ Filed แล้วถูกกันด้วย filing-lock guard แยกอยู่แล้ว
  4. Unlink **BankTransactions** (clear matched reference)
  5. Revert source-doc adjustments (ลูกของ CN/DN กลับ AR/AP ของต้นทาง)
  6. Reverse stock (`ApplyStockMovementsAsync(−1)`) + project cost entries
  7. Void linked **WHT certificates** (`_whtService.VoidAsync`)
  8. ตั้ง `Status = Voided`, `AgingDays = null`
  9. **Reset stateful posting flags** (เพิ่ม commit ล่าสุด): re-approve
     ไม่ข้ามขั้นที่ควรรัน:
     - `InputVatPostedAsUndue = false`, `InputVatBecameClaimableAt = null`
     - ถ้า `IsDeposit`: reset `DepositRealizedAmount/At`,
       `DepositOutputVatRecognizedAt`, `DepositRefundedAmount/At`,
       `DepositAppliedToDocumentId` → list ไม่โชว์ Partial/Realized ค้าง
  2b. **กลับ JV ตัดชำระด้วยมัดจำ** (`ApplyDepositToInvoiceAsync`): JV นั้น
      `SourceDocumentId=ใบมัดจำ` จึงหลุด step 2 → หา deposit ที่
      `DepositAppliedToDocumentId==ใบนี้` → reverse JV (คัดเฉพาะ JE มีขา Cr 113
      กันชน realize-JE) + คืน subledger (Realized/Recognized/AppliedTo). เคส
      drives (ขา reversal ฝังในใบ ไม่มี JV) ข้าม → step 7c จัดการ
- **Restore (กู้เอกสารที่ยกเลิกผิด)**: `RestoreVoidedDocumentAsync`
  (`DocumentService.cs`) — `Voided → Draft` **คงเลขเดิม** (re-approve ไม่
  regenerate เพราะเลขไม่ใช่ `DRAFT-`). ปลอดภัยเพราะ void เก็บ row/line ครบ +
  reset posting flags (9) เป็น approve-ready ไว้แล้ว; reversal JE เดิมคงไว้เป็น
  audit (คู่ net-zero) → re-approve post JE ใหม่ สุทธิถูก ไม่ double. **Gate
  compliance (block ทั้งหมด)**: (1) e-Tax `Accepted`; (2) เดือนภาษี (TaxPoint/
  DocDate) ยื่น ภ.พ.30/ล็อกแล้ว (period-based ไม่ใช่ line-ref เพราะ void ถอด
  doc ออกจาก report line แล้ว); (3) เลขถูกใช้กับใบ active อื่น. **ไม่คืน
  payment/ApplyDeposit อัตโนมัติ** — ผู้ใช้บันทึกใหม่หลังอนุมัติ. Endpoint
  `POST /document/{id}/restore` (สิทธิ์ = `Document.Void`); UI ปุ่ม "↩️ กู้คืน"
  โผล่เฉพาะสถานะ Voided ใน detail modal
  - **ด่าน §86/4 ของใบที่กู้คืนแล้ว** (`UpdateDocumentAsync`): ใบที่กู้คืนเป็น
    Draft แต่ **ถือเลขจริงเดิม** จึงห้ามแก้สิ่งที่พิมพ์บนใบย้อนหลัง. ด่านนี้ต้อง
    เทียบ **"ค่าที่เปลี่ยนจริง"** ไม่ใช่ **"ส่งฟิลด์มาไหม"** — ฟอร์มแก้ไขเป็น PUT
    ก้อนเดียวที่ส่ง `Lines` / `BillDiscount*` / `PricesIncludeVat` มาทุกครั้ง
    อยู่แล้ว ⇒ เช็ค `.HasValue`/`!= null` เท่ากับบล็อกทุกการกดบันทึกแม้ไม่ได้แก้
    อะไรเลย ⇒ ปุ่ม "บันทึกและอนุมัติ" ล้มที่ขั้น update ก่อนถึง approve ⇒
    **ใบที่กู้คืนมาอนุมัติไม่ได้เลยสักใบ** (ทางตัน: กู้คืนได้แต่ใช้งานต่อไม่ได้)
  - เทียบบรรทัดด้วย `PrintedLinesChanged` — เฉพาะสิ่งที่ **ปรากฏบนใบกำกับ**
    ตาม §86/4: รายการ · จำนวน · หน่วย · ราคา/หน่วย · ส่วนลด · อัตรา VAT ·
    อัตราหัก ณ ที่จ่าย. **ไม่นับ `AccountId`** เพราะรหัสผังไม่เคยพิมพ์บนเอกสาร
    (หลักเดียวกับที่เปิดให้ `ReclassifyLineAccountAsync` ทำได้บนใบที่อนุมัติแล้ว)
    — ไม่งั้นใบที่กู้คืนมาจะแก้ผังที่ลงผิดไม่ได้ตลอดกาล
  - **ทางลัดที่มีอยู่แล้ว**: detail modal ของสถานะ Draft มีปุ่ม "อนุมัติ" ตรง ๆ
    (ไม่ผ่าน update) — ข้อความ error ของด่านนี้ชี้ทางนั้นให้ด้วย
- **ห้าม hard delete** (ตาม §86/4 + พ.ร.บ.บัญชี)
- ⚠️ PDF footer "การลงบัญชี" (`PdfGenerationService.LoadGlPostingAsync`)
  query `OriginalEntryId == null` เพื่อแสดง **JE forward ต้นทาง** เสมอ
  ไม่ใช่ reversal — กัน footer ขึ้น Cr แทน Dr ตอน void
- ⚠️ **หา JE ไม่เจอ ≠ "ยังไม่อนุมัติ"** (รอบ 161) — เมื่อไม่มี JE ที่มีผลจริง
  footer ตกไปใช้ `BuildProjectedGlAsync` ซึ่งเดิมติดป้าย "(ประมาณการ — ก่อน
  อนุมัติ)" ให้ **ทุกกรณี** โดยไม่เคยตรวจสถานะเอกสาร. เลขรัน §86/4 ออกตอน
  อนุมัติเท่านั้น (ตอนสร้าง = `DRAFT-{guid}`) ⇒ ใบที่มีเลขจริงแต่ยังโชว์
  ประมาณการ แปลว่า **อนุมัติแล้วแต่สมุดรายวันว่าง** (ภ.พ.30 นับใบนี้แล้ว) —
  สาเหตุที่เป็นไปได้: ผู้ใช้ลบ JE จากหน้าสมุดรายวัน (`AccountingService`
  soft-delete ทั้งเดี่ยวและ bulk) · JE ถูกกลับรายการ · JE ค้าง Draft.
  ตอนนี้ป้ายอ่านสาเหตุจากฐานจริง (`DescribeMissingJournalAsync`,
  `IgnoreQueryFilters` เพื่อเห็นแถวที่ลบแล้ว + กรอง `CompanyId` เอง) และ
  ตัวตัดสินว่า "ใบนี้ควรมี JE ไหม" คือ **`Helpers/DocumentJournalExpectation`**
  ตัวเดียวที่ `ApproveDocumentAsync` ใช้เลือก post ด้วย — ใบเสนอราคา/ใบวางบิล/
  PR/PO/ใบส่งของ · ใบที่ยกเลิก · ใบเสร็จหลักฐานรับเงิน (`IsSettlementReceipt`)
  · ใบกำกับที่ออกแทนใบเดิม (`ReplacesDocumentId`) **ไม่มี JE คือถูกต้อง ห้ามเตือน**
- **Standalone void ปลอดภัยจาก void ซ้อน (row lock ใน tx)**:
  - `VoidPaymentAsync` (`DocumentService.cs`) — lock `Payments` row `FOR UPDATE`
    ในทรานแซกชัน + re-check `IsDeleted`; ถ้า void ไปแล้ว = no-op (กัน reverse
    bank balance/PaidAmount สองรอบ)
  - `VoidPayrollAsync` (`PayrollService.cs`) — lock `PayrollRuns` row `FOR UPDATE`
    + re-check `Status="Voided"`; กัน restore เงินทดรอง (SalaryAdvance
    OutstandingAmount) + reverse JE ซ้ำเมื่อกด void พร้อมกัน

### 3.6 §82/3 Undue VAT Reclassification (auto)
- เมื่อ approve PI/Expense ที่ใบกำกับยังไม่ครบ §86/4 (ขาดเลข/วันที่/สาขา
  ผู้ขาย) → input VAT ลง **11640 "ภาษีซื้อยังไม่ถึงกำหนด"** (ไม่เคลม)
- ผู้ใช้เติมข้อมูลภายหลังผ่าน `POST /documents/{id}/complete-tax-invoice`
  → `ReclassifyUndueInputVatAsync` (`:1137`): สร้าง JE Dr 11610 / Cr 11640
  + ตั้ง `InputVatBecameClaimableAt = now`
- รายงาน ภ.พ.30 ใช้ `InputVatBecameClaimableAt` เป็น tax point (ไม่ใช่
  `DocumentDate` ของใบเดิม) — เคลมในเดือนที่ใบครบ
- UI: `DocumentResponse.UndueInputVatBlockers` (populate ใน
  `MapDocumentToResponse` เมื่อค้าง 11640) = เหตุผลจริงที่ยังเคลมไม่ได้
  (missing fields จาก `TaxInvoiceCompletenessChecker` + override/ไม่มีบรรทัด
  เคลม VAT/§83/6) — กล่องเติมใบกำกับใน documents.html โชว์ checklist นี้
  + prefill รหัสสาขาจาก `Contact.BranchCode` (fallback 00000 สนญ.)
- **หมดอายุ 6 เดือนแล้วไม่มีใครเติมใบกำกับ** → `UndueInputVatExpiryJob`
  (รายวัน) เรียก `ReclassifyExpiredUndueInputVatAsync` ล้าง 11640 เป็น
  ค่าใช้จ่าย "ภาษีซื้อขอคืนไม่ได้" (Dr ค่าใช้จ่าย / Cr 11640) — เมธอดนี้เขียน
  ไว้ตั้งแต่ต้นแต่ **ไม่เคยมีใครเรียก** จนถึงรอบ audit 23 ⇒ ยอด 11640 ค้างเป็น
  สินทรัพย์ลอยในงบตลอดไป. job กันรันซ้ำข้าม instance ด้วย `pg_advisory_xact_lock`
  และเลือกเฉพาะบริษัทที่มีเอกสารค้างจริง (ไม่สแกนทั้งฐาน)
- UI badge "เคลมแล้วงวดไหน" — `DocumentResponse.InputVatPp30Month/Year/`
  `ReportStatus` (populate เฉพาะ `GetDocumentAsync` จาก `TaxReportLines`
  ฝั่งซื้อ !IsExcluded ของใบนั้น) = **ความจริงจากรายงาน ภ.พ.30** ไม่ใช่เดา
  จาก flag: modal โชว์ "✓ เคลมแล้ว · งวด MM/พ.ศ. (ยื่นแล้ว/ร่าง)"; งวด Filed
  → toggle เคลมถูกล็อก (ตรงกับ guard `UnclaimInputVatAsync`). list มี chip
  "🧾 เคลม ภ.พ.30 / ✕ พ้น 6 เดือน / ไม่มี VAT" จาก flag ฝั่งเบา. ใบฝั่งซื้อ
  ที่ `VatAmount = 0` ได้แผงอธิบาย + คำนวณ 7/107 แทนการซ่อนแผงเคลมเงียบ ๆ

### 3.6b ดึงเอกสารเข้ารายงาน ภ.พ.30 + ยกยอดข้ามงวด (§82/3)

เอกสารที่ยัง "ไม่ถูกใช้" ในรายงานงวดใด เข้ารายงานได้ **2 ทาง**:

1. **อัตโนมัติ (แนะนำ)** — `GenerateVatReport` กวาดบรรทัด `INPUT` ที่ถูกติ๊กออก
   (`IsExcluded=true`) จากรายงาน 6 เดือนย้อนหลัง แล้วยกมาเป็นบรรทัดใหม่ในงวดนี้
   ป้าย `[ยกมา §82/3 — ติ๊ก 'ใช้' เพื่อเคลมเดือนนี้]` **ติ๊กออกไว้ก่อน** ผู้ใช้ติ๊ก
   "ใช้" + บันทึกจึงนับเข้ายอด. dedup: ข้ามใบที่มีบรรทัด active อยู่แล้วในรายงานใด
   (`usedDocIds`), มีบรรทัดสดในงวดนี้ (`freshDocIds`), หรือยังพัก 11640 (`stillUndue`)
2. **ด้วยมือ** — `PullDocumentIntoReportAsync` (ปุ่ม "ดึงเอกสาร")

**ตัวตัดสินกลาง `TaxService.EvaluateClaimPeriod(basis, isInput, year, month)`** —
ใช้ทั้งการสร้างรายการที่แสดง (`GetPullableDocumentsAsync`) และตอนดึงจริง เพื่อให้
"สิ่งที่โชว์ = สิ่งที่กดได้" (เดิม list โชว์ใบที่กดแล้ว error):
- ห้ามดึงเข้างวดที่ **เก่ากว่าเดือนภาษีของเอกสาร** (ทั้งฝั่งซื้อ/ขาย)
- ฝั่งซื้อ: เคลมได้ในเดือนใบ + 6 เดือนถัดไป (§82/3) — เกินแล้วลงค่าใช้จ่ายแทน
- ฝั่งขาย: ไม่มีกรอบ 6 เดือน (นำส่งช้าได้)
- เดือนภาษีอ้างจาก `ClaimBasisDate` = `SupplierTaxInvoiceDate ?? TaxPointDate ?? DocumentDate`

**การกันซ้ำในรายการ "ดึงเอกสาร"** (`GetPullableDocumentsAsync` กรองออกทั้งหมด):
- `claimed` — มีบรรทัด active (`!IsExcluded`) ในรายงาน VAT ใดก็ตาม
- `alreadyInThisReport` — มีบรรทัดในงวดนี้แล้ว **รวมที่ติ๊กออก** (เช่น `[ยกมา]`)
  → กันบรรทัดซ้ำในงวดเดียวกัน; ทางที่ถูกคือติ๊ก "ใช้" ที่บรรทัดเดิม
- `IsPullable` — PV ต้องติ๊ก "ใช้งานใบกำกับภาษี", ไม่มี override ผังนอก 116,
  ไม่ใช่ VAT ที่ยังพัก 11640
- นอกกรอบเวลาตาม `EvaluateClaimPeriod`

**การเขียนตอนดึง** (สำคัญ — เคยพังด้วย `DbUpdateConcurrencyException`): อ่านทุกอย่าง
`AsNoTracking`, เขียนจริงแค่ INSERT บรรทัดใหม่ 1 แถว + `ExecuteUpdateAsync` ยอดรวม
รายงาน, จัดลำดับ §87 ทำท้ายสุดแบบ best-effort (อัปเดตเฉพาะแถวที่ลำดับเปลี่ยน)

### 3.7 มัดจำ (Deposit lifecycle)
- **รอบ 194 ทีม B — เส้นเอกสารต่อสายประเภทเงินมัดจำแล้ว** (spec `erp-review/2026-09-25/spec-194.md` S2–S4 · กติกาเส้นเอกสาร
  `Helpers/DepositKindDocumentRules` · เทสต์ `DepositKindDocumentRulesTests` สองครึ่ง · ล็อกจุดเรียก `tools/required_call_site_check.py` บล็อก "รอบ 194 ทีม B"):
  - **สร้าง/แก้ใบ** — `CreateDocumentRequest.DepositKindId`/`DepositKindCode` (คู่ค้า) · `UpdateDocumentRequest.DepositKindId` (null = คง · `Guid.Empty` = ล้าง ·
    ค่าอื่น = เปลี่ยน — เฉพาะร่าง/ถูกตีกลับ + ต้องส่งบรรทัด) ⇒ `ResolveDocumentDepositKindAsync` (บริษัทนี้ · เปิดใช้ · ไม่ลบ — ไม่พบ = 400
    `DEPOSIT-KIND-NOT-FOUND` · ประเภทกับใบที่ไม่ใช่มัดจำ = 400) → `DepositPolicyResolver.ResolveKind` → `DepositDocumentShaping.Apply` **ก่อน**
    `AllocateBillDeductions` ⇒ VAT บรรทัด · `DepositOutputVatDeferred` · `DepositDeferredAccountCode` (เงินประกัน 21530/21620 ไหลเข้า JE ตอนอนุมัติผ่าน
    ช่องเดิม `doc.DepositDeferredAccountCode ?? "21712"` ของ AutoPost — ไม่มีเส้นใหม่) + ตรึง `DepositKindId/DepositNature/DepositKindName/DepositPolicyNote` ·
    **payload ที่ไม่ระบุประเภท ⇒ ไม่แตะอะไร** (คู่ค้า/OCR/ที่พัก/CMS/ฟอร์มเก่า = พฤติกรรมรอบ 193 ทุกตัวอักษร) · `DocumentResponse` echo 4 ช่อง (ลักษณะเป็นชื่อ enum)
  - **ด่านเงินประกัน `DEP-SEC-DEDUCT`** (ตัวตัดสิน `SecurityDeductionProblem` ตัวเดียว) ทุกเส้นที่หักเป็นฐาน/ราคา: สร้าง/แก้ใบที่ตั้ง `DepositBaseDeducted`
    (`GuardSecurityDepositDeductionAsync`) · `LoadTaxedDepositsByRefAsync` (ผู้เรียก = แปลงขับ JE ตอนบันทึก `ConvertTaxedDrivesAsync` + รับรู้ฐานตอนอนุมัติ
    `RealizeTaxedDepositDeductionsAsync`) · เส้นขับ JE ทั้งใบเดียว/หลายใบ (`GuardDrivesGrossApplyAsync`) = **5 เส้น** · **ตัดชำระหนี้ (`ApplyDepositToInvoiceAsync`)
    ยังใช้กับเงินประกันได้** · ศูนย์มัดจำ/ตัวเลือกหักมัดจำได้ `DepositSummary.DeductAsBaseBlockedReason` (ฟอร์ม "ขายเงินสดใบเดียว" บอกก่อนส่ง)
  - **รับรู้/ริบโดยไม่มีใบสุดท้าย** (`RealizeDepositCoreAsync` · `FinalInvoiceId == null`) ⇒ `ForfeitVatDecision(doc.DepositNature, request.ForfeitAs, …)`:
    `KeepExistingVat`/`ReclassifyUndueToDue` = เส้นเดิม (+ ธง `[DEPOSIT-LATE-VAT]` บน `DepositPolicyNote` ของใบมัดจำเมื่อภาษีถึงกำหนดย้อนหลัง) ·
    **`IssueTaxInvoiceForForfeit`** (มัดจำเต็มยอดที่เป็นราคา/ใบเดิมไม่ระบุ) = `IssueForfeitTaxInvoiceAsync`: `CreateDocumentAsync` ใบกำกับ (ราคารวม VAT ·
    อัตราบริษัท · สืบทอดแบรนด์/สาขา/ภาษา/สกุลเงิน/เลขจอง) → `ApproveDocumentAsync(SystemWorkflow)` → `ApplyDepositToInvoiceAsync` (ตัดชำระด้วยมัดจำ ·
    ไม่ลง JE รับรู้ซ้ำ) · ใบกำกับติดธงในหมายเหตุภายใน · ล้มกลางทาง = ล้มดังพร้อมเลขใบ + ทางไปต่อ (หมายเหตุใบมัดจำ + คำตอบ) · เคยตัดชำระกับใบอื่นแล้ว =
    ปฏิเสธก่อนสร้างอะไร · **`CompensationNoVat`/`NonVatNoVat`** = รายได้ไม่มี VAT ลงบัญชี `DepositKind.ForfeitAccountCode` (ว่าง = บัญชีของเส้นเดิม) ·
    VAT พัก 21913 + ค่าเสียหาย = กลับ 21913 เข้ารายได้ตามสัดส่วน (`CompensationVatReversal` · ไม่ประทับ `DepositOutputVatRecognizedAt`) ·
    ขอ "ค่าเสียหาย" กับเงินที่เป็นราคา = ไม่มีผล + หมายเหตุบนใบ (ไม่เงียบ) · ศูนย์มัดจำถามผู้ใช้เฉพาะเมื่อคำตอบเปลี่ยนผล VAT
    (`DepositSummary.ForfeitOptions` · ค่าเริ่มต้น = มี VAT) + แสดง `RealizeVatNote` ก่อนกด
  - **คำเตือนตอนอนุมัติ** (`CollectApprovalWarningsAsync` → `DepositKindDocumentRules.ApprovalWarning` → `KindWarning`): ราคา × เลื่อน VAT
    (`RD-78(1)(b)`/`RD-78/1` + เหตุผลจาก `DepositPolicyNote`) · เงินประกัน × แยก VAT (`RD-PO73-SEC`) · ใบเดิม NULL/นอกระบบ VAT/บริษัทไม่จด = ไม่เตือน
  - **ฟอร์ม** (`documents.html`): `<select id="fDepositKind">` จาก `GET deposit-kinds` (`api.getDepositKinds`) · ใบใหม่ = `defaultKindId` · ใบเดิมไม่มีประเภท =
    "ตามข้อมูลบนใบ (ใบเดิม)" ส่งธงเดิม · ใบที่อนุมัติแล้ว = ล็อก + เหตุผล · พรีวิว VAT บรรทัด (เต็มยอด/นอกระบบ VAT = 0) ผ่าน `Layout.setLineVat` ·
    ลบข้อความ "ตั้ง VAT 0 เอง" · **โคลนใบมัดจำพา `IsDeposit` + ประเภท** (เดิมหาย ⇒ รายได้แทนหนี้สิน · ประเภทปิดใช้แล้ว = โคลนแบบไม่มีประเภท + ธงเดิม)
- เปิด Receipt/ReceiptVoucher ที่ `IsDeposit = true` — **โหมดมาจาก `DepositPolicyResolver`** (§2.3 ตาราง 3 โหมด ·
  ใบที่ระบุประเภทเงินมัดจำ = `ResolveKind` + `DepositDocumentShaping.Apply` ตามย่อหน้าข้างบน · ใบที่ไม่ระบุ = ธง/บรรทัดตาม payload):
  - **`VatImmediate`** (`DepositOutputVatDeferred = false`): Cr Output VAT 21911
    เข้า ภ.พ.30 ทันที (§78/1 รับชำระราคา = tax point) + Cr 217xx ขายรอรับรู้
  - **`VatPendingUndue`** (`DepositOutputVatDeferred = true`): Cr 21913 "ภาษีขายรอเรียกเก็บ"
    — ยังไม่เข้า ภ.พ.30 จนกว่าจะ realize
  - **`FullDeposit`**: VAT บรรทัด 0 + ธงพัก ⇒ Cr 217xx เต็มจำนวน
- **Realize**: `RealizeDepositAsync(amount, revenueAccountCode, FinalInvoiceId?)` (`DocumentService.cs:3422` → ตัวลงบัญชีไม่ SaveChanges
  `RealizeDepositCoreAsync` `:3390` ใช้ร่วมกับการรับรู้ตอนอนุมัติใบสุดท้าย) → `amount` = **ฐาน** (ไม่รวม VAT) ·
  Dr 217xx / Cr รายได้ (41xxx/42xxx); ถ้า deferred → ย้าย 21913 → 21911
  พร้อม `DepositOutputVatRecognizedAt = now` · `FinalInvoiceId` (ตรวจ tenant) ผูก JE ด้วย `JournalEntries.DepositRealizedForDocumentId` ⇒
  void/purge ใบสุดท้ายกลับได้ (`ReverseDepositRealizationsForAsync`) · ⚠️ audit-deposit P1-2 ยังเปิด: realize หลังคืนบางส่วนของโหมดรอเรียกเก็บ
  ย้าย 21913 ทั้งก้อน (เส้นที่พักเลี่ยงแล้ว — ริบก่อนคืนเสมอ)
- **Refund**: `RefundDepositAsync` (`:3940`) → reverse + ออกใบลดหนี้ภาษีขาย · `amount` = **gross** · VAT ของยอดคืนคิดจาก**ยอดคืนสะสม**
  (`DepositReversalMath.RefundSplit`: vat = round((คืนแล้ว+ยอดนี้)×VAT/รวม) − round(คืนแล้ว×VAT/รวม) ⇒ คืนหลายงวดไม่ค้าง 0.01 ·
  คืนครั้งเดียวจากศูนย์ = สูตรเดิม) · บัญชีเงินออก `RefundDepositRequest.MoneyAccountId` (ต้องเป็นผังของบริษัทนี้ · null = 111 เดิม)
- **Apply**: `ApplyDepositToInvoiceAsync` → `ApplyDepositToInvoiceCoreAsync` (`:4139`) → ใน 1 transaction:
  0. **FX guard**: ถ้า `invoice.Currency != deposit.Currency` หรือ
     `|invoice.ExchangeRate − deposit.ExchangeRate| > 0.0001` → throw
     (กัน FX silent corruption ตาม IAS 21 — ระบบยังไม่รองรับการบันทึก
     gain/loss FX อัตโนมัติ user ต้องทำ JE manual หรือใช้มัดจำสกุลเดียวกัน)
  0b. **ด่าน P0-3** (`:4113`): มัดจำออกใบกำกับแล้ว (VAT ทันที) ⇒ `RD-86/4-DEPOSIT-TIV-DOUBLE` ทุกงวด (§2.3)
  1. คำนวณ `vatPortion = deposit.VatAmount / deposit.TotalAmount`
  2. **ไม่เรียก `RealizeDepositAsync`** (แก้ doc รอบ 193 — doc เดิมเขียนว่าเรียก ซึ่งไม่ตรงโค้ด): ใบแจ้งหนี้รับรู้รายได้ + VAT แล้ว
     จึงแค่ล้างหนี้สินมัดจำไปตัดลูกหนี้ — Dr 217xx (ฐาน) + Dr [21913|21911] (VAT) / Cr ลูกหนี้ (gross) · `SourceDocumentId` = ใบมัดจำ
  3. ลด `invoice.BalanceDue` ตามยอด `amount` (gross — มัดจำจ่ายเงินจริงแล้ว
     ถือเป็น prepayment)
  4. ถ้า BalanceDue ≤ 0.005 → ตั้ง `Status = Paid`
  5. mark `deposit.DepositAppliedToDocumentId = invoiceId` (1 ใบมัดจำ → 1 ใบ
     ปลายทาง; ถ้า apply หลายใบต้องเรียกหลายครั้ง)
  6. **§ WHT ที่ค้างในลูกหนี้เมื่อปิดยอดครบ (S-2)** — เกณฑ์เงินสด (ค่า default)
     ตอนอนุมัติใบขาย GL ตั้ง `Dr ลูกหนี้ = TotalAmount + WHT` (gross) ส่วน
     `BalanceDue` ของ subledger ใช้ `TotalAmount` ที่สุทธิจาก WHT แล้ว ⇒ ปิดยอด
     ด้วยมัดจำจนครบ subledger ว่า "ชำระแล้ว" แต่ลูกหนี้ใน GL ค้างเท่ายอด WHT
     **ตลอดไป** และไม่มีใครลง 11910 ⇒ เสียเครดิตภาษีทั้งก้อนตอนยื่น ภ.ง.ด.50
     + งบดุลมีลูกหนี้ผี. `TryRecognizeSalesWhtOnSettlementAsync` ลง
     `Dr 11910 / Cr ลูกหนี้` เฉพาะส่วนที่ยังไม่เคยรับรู้ (อ่านยอด Dr 11910 จริง
     จาก GL ⇒ idempotent + ทยอยรับสด/หักมัดจำผสมกันได้) แล้วเรียก
     `SyncWhtCreditReceivedAsync`. เรียกทั้ง 2 เส้น (เอกสาร + JV) — เส้นรับเงินสด
     ทำถูกอยู่แล้วผ่าน `postPerPaymentWht`
  7. Fire webhook `deposit.applied`
- **หา "มัดจำคงเหลือ" จาก GL (family-net) — ต้องแยก JE ที่สร้างหนี้สินมัดจำ**:
  นับขา **Cr เฉพาะจาก JE ที่เครดิตบัญชีมัดจำ** (ใบเสร็จมัดจำ: Cr 217xx/215xx
  + 21913 หรือ 21911 กรณีไม่ deferred) ส่วนขา **Dr นับจากทุก JE** ที่ผูก
  `SourceDocumentId = ใบมัดจำ`. ที่มา (M-1): ตัวกรองเดิมตัดแค่ผัง `1xxxx` ⇒
  มัดจำที่ถูก **รับรู้รายได้บางส่วน** (`RealizeDepositAsync` ลง `Dr 217xx /
  Cr 41xxx` + `Dr 21913 / Cr 21911` ผูก SourceDocumentId เดียวกัน) ทำให้
  `Cr 41xxx` และ `Cr 21911` ถูกนับเป็นมัดจำคงเหลือ → ตอนเอาส่วนที่เหลือไปตัด
  ใบแจ้งหนี้ ระบบลง **Dr 41000 ล้างรายได้ที่รับรู้ถูกต้องไปแล้ว** (Dr=Cr ยัง
  สมดุล ไม่มี guard ตัวไหนจับ). แยกด้วย "JE ไหน" ไม่ใช่ "รหัสบัญชีอะไร" เพราะ
  21911 ยังต้องนับได้เมื่อมาจากใบเสร็จมัดจำที่รับรู้ VAT ทันที
- **Deposit-applied drives-journal** (integration self-contained JE) — ใบรับเงิน
  สุดท้ายส่ง `DepositAppliedAmount` + `DepositAppliedRef` + `DepositAppliedDrivesJournal=true`
  → JE ใบเดียวกลับ deferred ของมัดจำ (ไม่ต้องมี JV reverse แยก).
  `DepositAppliedRef` resolve 2 ทาง (`AutoPostToJournalAsync`, `DocumentService.cs:7499`):
  - **เคส A** — ตรงกับ **ใบมัดจำ (Document, `IsDeposit`)** ตาม `DocumentNumber` →
    Dr 217xx/21913 ของใบมัดจำ + mark `deposit.DepositAppliedToDocumentId`
  - **เคส B** — ไม่พบ Document → resolve เป็น **`JournalEntry.EntryNumber`** (มัดจำ
    ภายนอก เช่น JV-INT ที่ integration ลง Cr 217xx/21913 เอง) → อ่านบรรทัด Cr ของ
    journal หาบัญชี deferred (215xx/217xx) + VAT (21913/21911) → Dr กลับบัญชีเดิม
    ตามสัดส่วน; guard double-reverse ด้วย `JournalEntry.DepositAppliedToDocumentId`
    (void ใบ → un-mark ให้ resync ได้). ต้นเหตุ: TakeTime "drives รับ journal ref"
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
  `Reference`) populate ใน `GetDepositsAsync` (`DocumentService.cs:1581`)
- **การตรวจจับมัดจำใน dashboard** (`GetDepositsAsync`) — 3 ชั้น ไม่พึ่งแค่ธง
  `IsDeposit` เพื่อให้ KPI สะท้อนหนี้สินมัดจำจริงบนงบดุล:
  1. **Native** — `Documents.IsDeposit = true` (สร้างในระบบ) → ใช้
     `SubTotal − DepositRealizedAmount`
  2. **GL-detected (มีเอกสารผูก)** — เอกสารที่ยังไม่ติดธง `IsDeposit` แต่มี JE
     (Posted, ไม่ reverse) **Cr สุทธิ** บัญชีมัดจำ (`AccountCode` 215xx/217xx
     **หรือ** `AccountName` มี "มัดจำ/รับล่วงหน้า/รอรับรู้") — **ทุก doc type**
     (integration/POS/receipt/invoice) ไม่จำกัดแค่ Receipt; ฐาน/คงค้างใช้ยอด
     **Cr สุทธิใน GL** (ΣCr − ΣDr) แทน `SubTotal` เพราะ integration doc อาจ
     ไม่ตั้ง `SubTotal` → เดิมคำนวณ outstanding = 0
  3. **Doc-less (JE ล้วน ไม่มี `SourceDocumentId`)** — รวมยอด Cr สุทธิเป็น 1
     แถวสรุป (`Id = Guid.Empty`, ไม่มีปุ่มรับรู้/คืน) เพื่อ KPI ไม่ขึ้น 0
     ทั้งที่งบดุลมีหนี้สินมัดจำ (รับรู้/คืนต้องผ่านสมุดรายวันตรง)

### 3.8 รอบเงินเดือน (PayrollRun) — คำนวณใหม่ · แก้ยอด · ฐาน ปกส. · 50 ทวิ (รอบ 193 · คำตัดสิน #35 · ทีม P2 → M2)
- **ฐาน ปกส./กองทุนเงินทดแทน** — ตัวประกอบสูตรตัวเดียว `Helpers/SsoWageBase.ForPeriod(...)` → `SsoPeriodAmounts` (`PayrollService.CalculatePayrollAsync`
  `:2057` · `GrossWage/SalaryPaidThisPeriod/PeriodBase` เป็น internal ห้ามประกอบเอง): ค่าจ้าง = `SalaryPaidThisPeriod(prorated, leaveDeduction)` +
  เบี้ยเลี้ยงที่ติ๊กว่าเป็นค่าจ้าง · **ลาไม่รับค่าจ้างถูกหัก** (D-02 — เดิมฐานภาษีหักแต่ฐาน ปกส. ไม่หัก ⇒ รายได้ 0 · หัก ปกส. 875 · สุทธิ −875 · สปส.1-10
  ประกาศค่าจ้าง 17,500) · **ไม่ได้จ่ายอะไรเลย** (`statutoryWage ≤ 0 && grossIncome ≤ 0`) = ฐาน 0 · มีเงินได้อื่น (เช่นคอมมิชชันที่ยังไม่นับเป็นค่าจ้าง) =
  ขั้นต่ำ 1,650 เหมือนเดิม (⚠️ เบี้ยเลี้ยงที่ไม่ใช่ค่าจ้างก็ทำให้ได้ขั้นต่ำ — คำถามเจ้าของ) · กองทุนเงินทดแทนใช้ฐานเดียวกัน
- **คำนวณใหม่** (`POST runs/{id}/calculate` ทางเดียว · ปุ่ม "🔄 คำนวณใหม่" หรือ 🔒 + เหตุผลจาก `PayrollRunResponse.CanRecalculate/RecalculateBlockReason`) —
  `PayrollRunEditPolicy.CanRecalculate(status, externalSystem, reopenedAt, PayrollRunLockEvidence)` (หลักฐาน**บังคับ** · null = `ArgumentNullException`):
  Draft/Calculated/Approved ได้ (Approved → Calculated + ล้าง ApprovedBy/At · ยอดแก้มือรายคน + แหล่งจ่ายรายคนถูกแทน) · ปฏิเสธ Paid · Voided ·
  เคยจ่ายแล้วกลับรายการ (`ReopenedAt` → ใช้ ✏️ แก้ยอดรายคน) · นำเข้าจากระบบนอก (TakeTime) · **รอบ Approved ที่มีหลักฐาน "ยื่นแล้ว/นำส่งแล้ว"** ·
  **ปันต้นทุนโครงการแล้ว (ทุกสถานะ)** · Draft/Calculated ไม่ถูกหลักฐานของงวดล็อก (รอบที่สองของเดือนต้องคำนวณได้) · ด่านอยู่ก่อน `RemoveRange(run.Details)`
  (`required_call_site_check`) · **ไม่ทำ migration อัตโนมัติ** ให้รอบที่คำนวณก่อนแก้สูตร (HR กดเอง — เปลี่ยนตัวเลขที่อนุมัติแล้วเงียบ = สถานะที่ระบบประทับเอง)
- **หลักฐาน "ยื่นแล้ว"** (`PayrollService.LoadRecalculateLockEvidenceAsync` `:4354` · batch · กรอง `CompanyId` + `!IsDeleted`): **ปฏิทินภาษี** `TaxCalendarEvents`
  (`ภ.ง.ด.1`/`สปส.1-10` · `Status == "Filed"` · ปี/เดือนของรอบ — ทางเดียวบนจอ) + `ComplianceFiling` (PND1/SSO1-10 Filed/Accepted — ไม่มีหน้าจอ) + `TaxReport`
  เก่า (WHT1/SocialSecurity ที่ไม่ใช่ร่างหรือถูกล็อก) · **"นำส่งแล้ว"** = `SsoSettledAt` **ของรอบนั้น** (รอบโบนัสในเดือนที่รอบอื่นนำส่งแล้วไม่ล็อก) · ปันต้นทุน =
  `EmployeeProjectTimes.AllocatedPayrollRunId` · `EFilingExport` (PND.1) = **คำเตือนเท่านั้น** (`RecalculateWarning`) · การดาวน์โหลดไฟล์ยื่นไม่ทิ้งร่องรอย
  (ห้ามอนุมาน "ดาวน์โหลด = ยื่น" — confirm เตือนให้บันทึกการยื่นก่อน) · ข้อความทางไปต่อตามแหล่งที่บันทึกจริง (`PayrollFilingMark.UndoHint`)
- **✏️ แก้ยอดรายคน** `CanEditAmounts(status, evidence)` ด่านเดียวกับคำนวณใหม่ ⇒ `PAYROLL-EDIT-LOCKED` (`UpdatePayrollDetailAsync`) · ข้อความที่แนะนำ ✏️ ออกเฉพาะเมื่อ ✏️ ใช้ได้จริง ·
  **แหล่งจ่าย** แยก `CanSetPaymentAccount(status)` (ไม่อยู่ในแบบยื่น — ไม่ถูกล็อกด้วยหลักฐาน)
- **50 ทวิ ภ.ง.ด.1** (D-01): เลขผู้เสียภาษีพนักงาน = `Helpers/EmployeeTaxIdentity.Resolve(taxId, citizenId)` (TaxId ที่กรอก → เลขบัตร → null) ใช้ใน 50 ทวิ
  รายเดือน (ด่าน + ค้นใบมือ + ค้น/สร้าง Contact) · รายปี · ไฟล์ ภ.ง.ด.1/1ก/91 · รายงาน ภ.ง.ด.1ก · Contact จากใบเบิก/เงินทดรอง — _เดิม `Employee.TaxId` ไม่มี
  ผู้เขียนแต่เป็นด่านเดียว ⇒ "ออก 50 ทวิไม่ครบ" ทุกงวด + นำส่ง ภ.ง.ด.1 ติด `WHT-CERT-UNISSUED` ตลอดกาล_ · ไฟล์ สปส.1-10 ใช้ `CitizenId` ตรง (ถูกต้อง)
- **แก้ข้อมูลพนักงาน** (A05/D-07): `UpdateEmployeeRequest` +11 ช่อง (null = ไม่แตะ · "" = ล้าง) ผ่าน `Helpers/EmployeeRecordEdit` · Response +TaxId/ธนาคาร/
  `EmployeeCodeLocked`+เหตุผล · ทั้งสองหน้า hydrate = payload ชุดเดียว (`tools/employee_form_contract_sim.js`) · เลขบัตร checksum ผ่าน `ThaiTaxIdValidator`
  (เลขเดิมที่ไม่ได้แก้ไม่ถูกตรวจ) · วันเริ่มงานส่งเฉพาะเมื่อเปลี่ยนจริง · HRIS sync/นำเข้า CSV ยังไม่ตรวจ checksum (backlog)

---

## 4. ผลกระทบต่อระบบรายงาน (per document type)

| Type | JE on Approve | Stock | VAT report side | Tax point | Special |
| --- | --- | --- | --- | --- | --- |
| `Quotation` | ❌ | ❌ | – | – | – |
| `Invoice` | Dr AR / Cr Rev + Cr **[21913 บริการล้วน \| 21911 มีสินค้า TrackStock]** | ❌ (DN จัดการแยก) | output — บริการล้วน: เข้าเมื่อ `OutputVatDueAt` (รับเงิน §78/1); มีสินค้า/legacy (GL ลง 21911 ตรง): เข้าทันทีตาม tax point (§78 ส่งมอบ) | `TaxPointDate` snapshot; บริการ → `OutputVatDueAt` ตอนรับชำระ | รับชำระ (Payment/ใบเสร็จ settlement) → `TryReclassifyUndueOutputVatAsync`: JV Dr 21913 / Cr 21911 เต็มยอดคงเหลือ **รวม JE ของ CN/DN ที่อ้างใบนี้ (RelatedDocumentId) และ stamp `OutputVatDueAt` ให้ทั้งใบเดิมและ CN/DN ลูก** (รอบ 135 · ERP_REVIEW C-01 — เดิมรวมแค่ JE ของใบเดิม ⇒ VAT ของ DN ค้าง 21913 ถาวรและไม่เข้า ภ.พ.30; ยกเลิกการรับชำระ (`TryUndoUndueOutputVatReclassAsync`) ล้าง stamp ของลูกด้วย) (full-on-first-settlement, GL-driven, idempotent). แปลงเป็น TIV → supersede reverse JE ทั้งใบ (รวม 21913) ใบกำกับลง 21911 เอง |
| `TaxInvoice` | Dr AR / Cr Rev + Cr 21911 | ❌ | output | `TaxPointDate` snapshot | – |
| `BillingNote` | ❌ (รอ Receipt) | ❌ | – | – | **รวมใบค้างหลายใบได้**: `POST document/billing-note/from-invoices` — 1 บรรทัด/ใบ ยอด=BalanceDue, `DocumentLine.SourceDocumentId` ชี้ใบต้นทาง (กันวางบิลซ้ำใน BN active) |
| `Receipt` standalone | Dr Cash / Cr Rev + Cr 21911 **+ Dr 51110 / Cr 11500 (COGS) เมื่อ `CompanySettings.CashSaleStockPolicy = MoveStockAndCogs` (default)** | ตาม `CashSaleStockPolicy` (รอบ 136 · ERP_REVIEW A-06): `MoveStockAndCogs` (default) = OUT เหมือนใบกำกับ · `Ignore` = ไม่แตะ (พฤติกรรมเดิม) · `Block` = อนุมัติไม่ได้ให้ออกใบกำกับแทน — กติกาอยู่ที่ `Helpers/CashSaleStockRules` ใช้ทั้งทิศสต๊อก/COGS/ด่าน approve · ใบเสร็จที่อ้าง Invoice และใบมัดจำไม่กระทบ | output | DocumentDate | nullable `RelatedDocumentId` — ถ้ามีอ้าง Invoice → ไม่ count VAT ซ้ำ |
| `ReceiptVoucher` standalone | เหมือน Receipt | ❌ (same as Receipt) | output | DocumentDate | รองรับ `IsDeposit` (2 เคส VAT ดู §3.7) |
| `DeliveryNote` | ❌ | ❌ (ตั้งใจไม่ trigger — Invoice ที่ตามมาจะ OUT ให้, กัน double-count) | – | – | ใช้คู่กับ Invoice ใน Quotation→DN→Invoice chain |
| `DebitNote` | Dr AR / Cr Rev + Cr VAT | ❌ | output (หรือ input ถ้า `RelatedDocumentId` เป็น purchase: PI/Expense/CIL/**PV**) | DocumentDate | บังคับมี `RelatedDocumentId`; คู่ค้าต้องตรงใบเดิม (hard block) |
| `CreditNote` | Cr AR / Dr Rev + Dr VAT (ฝั่งซื้อ: กลับด้าน — source PI/Expense/CIL/**PV**) | ฝั่งขาย IN เฉพาะ `Reason = Return`; ฝั่งซื้อ OUT (คืนของ) ยกเว้น source=PV → ไม่ขยับ | output (หรือ input) | DocumentDate | บังคับ `CreditNoteReason`; คู่ค้าต้องตรงใบเดิม; วันที่ย้อนหลัง >1 เดือนภาษี → warning + LateReason (§86/9-10); PDF แสดงกล่อง มูลค่าเดิม/ที่ถูกต้อง/ผลต่าง |
| `PurchaseRequisition` | ❌ | ❌ | – | – | internal commitment |
| `PurchaseOrder` | ❌ | ❌ | – | – | – |
| `GoodsReceiptNote` | Dr Inv / Cr GRNI (accrual) | IN | – | – | 3-way match prep |
| `PurchaseInvoice` | Dr Exp + Dr Input VAT / Cr AP | IN (ถ้าไม่มี GRN ก่อน) | input (11610 หรือ 11640) | TaxPointDate; 11640 → `InputVatBecameClaimableAt` | §86/4 completeness gate |
| `Expense` | Dr Exp + Dr Input VAT / Cr Cash/AP | ❌ (ยกเว้นมี product code) | input | TaxPointDate | §65 ตรี ที่ approve |
| `PaymentVoucher` | Dr AP/Exp / Cr Cash/Bank | ❌ | input (เฉพาะถ้าเปิด PV master switch + มีใบกำกับ) | DocumentDate | `PaymentType` (Cash/Credit) |
| `CertificateInLieu` | **standalone**: Dr Exp (รวม VAT เคลมไม่ได้ **รายบรรทัด**) / Cr Cash · **แปลงจาก Expense/PI**: Dr AP / Cr Cash (settlement) | ❌ | **เคลมไม่ได้ §82/4** — VAT พับเข้าต้นทุนบรรทัดนั้น ๆ (เดิมกองรวมที่บัญชีค่าใช้จ่ายทั่วไป → ต้นทุนเพี้ยนทุกผัง และบริษัทที่ไม่มี default expense ได้ JE ไม่สมดุล) | DocumentDate | บังคับ `CertReason + CertifierName` |

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
- **ภาษาเอกสาร — resolver กลาง `ResolveDocumentLanguage`** (`PdfGenerationService.cs`)
  ลำดับ: `request.Language` → `Document.DocumentLanguage` → `template.Language`
  → `CompanySettings.DocumentLanguage` → `"th"`. ข้อความทั้งหมดมาจาก
  `Services/Implementations/Pdf/DocumentLabels.cs` (ไทย/อังกฤษ ชุดเดียว ใช้ทั้ง
  QuestPDF native + HTML renderer)
  - โหมด **en** พิมพ์หัวเอกสารที่มี VAT แบบ **สองภาษา** ("Tax Invoice /
    ใบกำกับภาษี") — §86/4 บังคับคำไทย ตัดทิ้ง = ผู้ซื้อเคลมภาษีซื้อไม่ได้ §82/5(1)
  - **แบบฟอร์มราชการคงไทยเสมอ**: 50 ทวิ, ภ.ง.ด.1/3/53/54, ภ.พ.30, ภ.พ.36
  - **ทางเข้าจาก UI ครบทั้ง 3 ชั้นแล้ว** (เดิมมีเฉพาะชั้นบริษัท — อีก 2 ชั้นมีใน
    DB/API แต่ไม่มีช่องให้กด ⇒ "ออกใบเดียวเป็นอังกฤษ" ทำได้ทางเดียวคือสลับค่า
    ทั้งบริษัท ซึ่งทำให้ **ใบเก่าทุกใบเปลี่ยนภาษาตามตอนพิมพ์ซ้ำ** เพราะใบที่
    `DocumentLanguage=null` ไปหยิบค่าบริษัท ณ เวลาพิมพ์):
    | ชั้น | ตั้งที่ไหน | ขอบเขต |
    | --- | --- | --- |
    | ทั้งบริษัท | ตั้งค่าบริษัท → "ภาษาของเอกสารที่ออก" (`settings.html`) | ทุกใบที่ไม่ได้ตรึงภาษาไว้ |
    | รายผู้ติดต่อ | หน้าผู้ติดต่อ → "ภาษาเอกสารของผู้ติดต่อรายนี้" (`Contact.DocumentLanguage`) | **ค่าตั้งต้นตอนสร้างใบ** — ประทับลง `Document.DocumentLanguage` ตอนสร้าง (ฟอร์ม `applyContactDocLanguage` + server fallback ใน `CreateDocumentAsync` กติกาเดียวกับเครดิตเทอม) **ไม่ใช่ชั้น resolve ตอนพิมพ์** ⇒ แก้ค่าผู้ติดต่อภายหลังไม่กระทบใบเก่า และผู้ใช้แก้ทับรายใบได้เสมอ (userTouched ชนะ) · ตัวเติมฝั่งฟอร์มจงใจไม่ใช้ `_canAutoFill` เพราะต้อง refresh/ล้างได้ตอนสลับผู้ติดต่อ (contact ทับ contact ซึ่ง rank เท่ากัน) |
    | รายใบ (ตรึงถาวร) | ฟอร์มสร้าง/แก้เอกสาร → "ภาษาของเอกสารใบนี้" (`fDocumentLanguage`) | ใบนั้นใบเดียว ทุกครั้งที่พิมพ์ |
    | ครั้งนี้ครั้งเดียว | modal ดูตัวอย่าง → dropdown ข้าง ต้นฉบับ/สำเนา (`pdfLangMode`) | เฉพาะการพิมพ์/ดาวน์โหลดรอบนั้น ไม่บันทึกลงใบ |
  - ชั้น "ครั้งนี้ครั้งเดียว" ส่งเป็น `GeneratePdfRequest.Language` → ใช้กับใบที่
    **อนุมัติ/ปิดแล้วแก้ไม่ได้** ได้ด้วย (เคสจริง: ลูกค้าโทรมาขอฉบับอังกฤษทีหลัง)
    ต่อครบทั้ง 4 ทางออกของหน้าเอกสาร: preview HTML · พิมพ์ · ดาวน์โหลด PDF ·
    ดาวน์โหลด HTML — และ reset ทุกครั้งที่เปิด modal ใบใหม่
  - `DocumentResponse.DocumentLanguage` echo กลับมาให้ฟอร์ม hydrate ตอนแก้ไข
    (เดิมไม่มีใน response ⇒ เปิดแก้ใบอังกฤษแล้วกดบันทึก ภาษาถูกล้างเงียบ ๆ)
  - **ชื่อ/ที่อยู่บริษัทบนใบโหมด en**: `Company.NameEn` เป็นชื่อหลัก (ไม่มี →
    คงชื่อไทย **ห้ามถอดอักษรชื่อบริษัท/บุคคลเอง** — การสะกดชื่อเฉพาะเป็นสิทธิ์
    ของเจ้าของชื่อ) · ที่อยู่ใช้ `Company.AddressEn` ก่อน ไม่มี → ถอดอักษร
    อัตโนมัติด้วย `ThaiRomanizer.ComposeEnglishAddress` (จังหวัด = ตารางสะกด
    ทางการ 77 ชื่อ; ตำบล/อำเภอ/ถนน = RTGS rule-based; ชิ้นที่เป็นละตินอยู่แล้ว
    ผ่านตามเดิม) · ที่อยู่**ลูกค้า**ถอดอัตโนมัติเช่นกัน (ไม่มี field ต่อ contact)
    · ตั้งค่า: ตั้งค่าบริษัท → "ที่อยู่ภาษาอังกฤษ" + ปุ่ม "✨ แปลงจากที่อยู่ไทย"
    (`POST /api/company/{cid}/romanize-address` — **route เอกพจน์** ต่างจาก
    controller อื่น) แปลงจากค่าบนฟอร์ม เติมให้ตรวจแก้ก่อนบันทึกเอง · แบบราชการ
    (50 ทวิ ฯลฯ) คงที่อยู่ไทยเสมอ ไม่แตะ
  - **ช่องทางส่งถึงลูกค้า ตามภาษาใบด้วยแล้ว** (audit 3 ทีม รอบ 22):
    เนื้ออีเมล default ทั้ง manual (`DocumentEmailService.BuildDefaultTemplate`)
    และ scheduled reminder (`EmailScheduleService.DefaultDocSubject/Body` —
    เฉพาะ fallback; template ที่ผู้ใช้เขียนเองไม่ถูกแตะ) + LINE flex
    (`DocumentLineDeliveryService`) — ทุกตัวใช้ชั้น `doc.DocumentLanguage ??
    CompanySettings.DocumentLanguage` และ `NameEn` เมื่อ en · **บั๊กที่แก้พ่วง**:
    ปุ่มส่งอีเมล manual ไม่เคยแนบ PDF จริง ("omit for now") ทั้งที่ UI ส่ง
    `attachPdf:true` → แนบผ่าน `GenerateDocumentPdfAsync` แล้ว (ล้ม = ไม่ส่ง
    อีเมล ห้ามส่งอีเมลที่บอกว่ามีไฟล์แนบแต่ไม่มี) · LINE ปุ่ม "ดูเอกสาร"
    hardcode `app.example.com` (ลิงก์ตาย) → ใช้ `App:BaseUrl` + portal จริง ·
    e-Tax by Email ตอนสร้างไฟล์ on-demand ใช้ renderer รวม (ตรงกับปุ่ม
    ดาวน์โหลดปกติ + ภาษาถูก) fallback ตัวเดิม; ไฟล์ที่ persist แล้วคงเดิม
  - **รู้แล้วแต่ยังไม่ทำ (จัดลำดับไว้)**: `portal.html` UI ไทยล้วน + ไม่ใช้
    NameEn (ต้องทำ portal สองภาษาเป็นงานแยก) · POS ใบเสร็จ client-side ไทย
    (ใบกำกับอย่างย่อหน้าร้าน — รับได้ แต่ไม่แชร์ label กลาง = drift risk) ·
    ปุ่ม PDF/A-3 ใน etax.html + artifact ที่ persist ยังเป็น renderer แยก
    layout ไทยตายตัว (`EtaxInvoiceService.PdfA3`) — เอกสารที่เคยออกต้องนิ่ง
    จึงไม่ย้อนแก้; งานถัดไปคือ unify ตอน generate ใหม่ ·
    `SendDunningLetterAsync` (AdvancedArAp) mark ว่าส่งแล้วโดยไม่ส่งจริง

- **หัวเรื่องเอกสาร — resolver กลาง `ComputeDocumentTitle`** (ใช้ทั้ง QuestPDF
  native + HTML กัน logic drift). ครอบทุกเคสจริงทางบัญชี:
  - หัวพื้นฐาน 16 ประเภท (`GetDocumentTitle`) — ทุกชนิดถูกต้องตามชื่อไทย
  - **เงื่อนไข** (auto): ใบกำกับ+รับเงินตอนออก (ServedAsReceipt) / ใบเสร็จมี
    VAT → "ใบกำกับภาษี/ใบเสร็จรับเงิน"; TaxInvoice+`CombinedInvoiceTaxInvoice`
    → "ใบแจ้งหนี้/ใบกำกับภาษี" และเมื่อ **จ่ายครบ+ไม่มีใบเสร็จแยก
    (ServedAsReceipt)** → upgrade เป็น **"ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน"
    (3-in-1)** — คำว่าใบเสร็จรับเงินโผล่เฉพาะเมื่อรับเงินจริง (ม.105);
    **ใบเสร็จ settlement ที่อ้าง TaxInvoice** (`SettlesTaxInvoiceSource` —
    resolve ตอน render) → คงหัว "ใบเสร็จรับเงิน" เปล่า ห้ามพิมพ์คำใบกำกับซ้ำ
    (กันลูกค้าถือกระดาษใบกำกับ 2 ใบจากขายครั้งเดียว = เคลมภาษีซื้อซ้ำ);
    settlement ที่อ้าง Invoice ยังพิมพ์ "ใบกำกับภาษี/ใบเสร็จรับเงิน" (มันคือ
    ใบกำกับที่ต้องออก ณ วันรับเงิน §78/1 — คู่กับโมเดล undue 21913);
    มัดจำ VAT พักรอ (21913) → คงเป็นใบเสร็จ (ไม่ upgrade);
    `IsDeposit` → ต่อท้าย "(เงินมัดจำ)"
  - **ตั้งเองได้ทุกหัว** (พื้นฐาน + เงื่อนไข) ผ่าน
    `CompanySettings.DocumentTitleOverridesJson` (คีย์ = ชื่อ enum +
    `TaxInvoiceReceipt`/`CombinedInvoice`/`CombinedInvoiceReceipt`/
    `DepositSuffix`) — หน้าตั้งค่า →
    เอกสาร → "หัวเรื่องเอกสาร"; per-template `CustomTitle` ยังชนะ base override
  - **หน้าเว็บไม่คิดหัวเอง — เซิร์ฟเวอร์ส่ง `DocumentResponse.DocumentTitle`
    มาให้** (`PdfGenerationService.ResolveDocumentTitleAsync` / `…TitlesAsync`
    → `DocumentService.GetDocumentAsync` + `GetDocumentsAsync`).
    `Layout.docHeaderLabel` **แสดงค่านั้นตรง ๆ** กฎเดิมในไฟล์เหลือไว้เป็น
    fallback ของ endpoint ที่ยังไม่ส่งค่ามาเท่านั้น
    _(เดิมเป็นสำเนามือที่รู้จักแค่ 3 ธง ⇒ จอกับกระดาษไม่ตรงกัน **6 เคส**:
    ชื่อหัวที่ผู้ใช้ตั้งเอง · `template.CustomTitle` · §86/4 ผู้ซื้อไม่ครบ →
    "ใบกำกับภาษีอย่างย่อ" · ใบเสร็จที่มี VAT → "ใบกำกับภาษี/ใบเสร็จรับเงิน" ·
    `IsDeposit` → "(เงินมัดจำ)" · `IssuedAsCashReceipt` ที่ JS สลับลำดับเป็น
    "ใบกำกับภาษี/ใบเสร็จรับเงิน" ทั้งที่กระดาษพิมพ์ "ใบเสร็จรับเงิน/ใบกำกับภาษี")_
    - **ตัวเลือกเทมเพลตมีชุดเดียว** — `PickTemplate` (pure) + `LoadTemplatePool
      Async`; `ResolveDocumentTemplateAsync` (ตอนพิมพ์) และ `ResolveDocumentTitles
      Async` (รายการเอกสาร) เรียกตัวเดียวกัน ⇒ หน้ารายการ query คงที่ **3 ครั้ง/หน้า**
      (เทมเพลตบริษัท · ตั้งค่าบริษัท · แบรนด์+ใบต้นทางของหน้านั้น) ไม่ใช่ N+1
    - เทสต์: `Accounting.Tests/DocumentTitleServerOwnedTests.cs` (ล็อกทั้ง 6 เคส
      ฝั่ง C#) + simulation ฝั่ง JS ที่รัน `docHeaderLabel` จริงจาก layout.js
    - **สิทธิ์ §86/6 ของผู้ออก เป็นด่านแรกของหัวเอกสาร** (รอบ 182 — DECISION_AUDIT D1-B4)
      `ComputeDocumentTitle(..., bool companyMayIssueAbbreviated)` และ
      `IsAbbreviatedTaxInvoiceDoc(doc, companyMayIssueAbbreviated)` **ไม่มีค่าตั้งต้น**
      ผู้เรียกต้องตอบเสมอ · คำตอบมาจาก `Helpers/AbbreviatedTaxInvoiceRule.CanIssue`
      ตัวเดียวของระบบ (ตัวเดียวกับสลิป POS ผ่าน `PosSlipHeader.Resolve`) ซึ่งรับ
      `IsVatRegistered` · `IsRetailApproved` · `PhoR06ApprovedDate` · วันที่บนเอกสาร ·
      และนโยบายแพลตฟอร์ม `SiteSettings.RequirePhoR06ForAbbreviatedTaxInvoice`
      _(เดิมเส้นเอกสาร/PDF พิมพ์ "ใบกำกับภาษีอย่างย่อ" **โดยไม่เคยตรวจ ภ.พ.06 เลย** —
      ธงนี้มีผู้อ่านแค่ `PosService` ⇒ บริษัทที่ยังไม่ได้รับอนุมัติออกใบกำกับโดยไม่มีสิทธิ์
      ผู้ซื้อเคลมภาษีซื้อไม่ได้ §82/5(5))_
      - **ไม่มีสิทธิ์ → หัวตกเป็น "ใบเสร็จรับเงิน"** (VAT ขายยังลง ภ.พ.30 ครบเหมือนเดิม —
        ภาระภาษีไม่ขึ้นกับหัวเอกสาร) · ข้อความ §86/6(6) "ยอดรวมทั้งสิ้นได้รวมภาษีมูลค่าเพิ่มแล้ว"
        หายตามไปด้วยทั้ง **สอง renderer** (ใช้ predicate ตัวเดียวกัน — กัน drift)
      - ⚠️ **ผลข้างเคียงที่สอง — เลขชุด**: หัวไม่มีคำว่า "ใบกำกับภาษี" ⇒ `IsTaxInvoiceByLaw=false` ⇒
        `TaxInvoiceSeriesPolicy` ให้ใบเสร็จที่มี VAT ได้เลข **REC** แทน **TIV** (ตรึงตอนอนุมัติ แก้ย้อนหลังไม่ได้ §86/4)
      - **ต้องบอกบนจอ** (รอบ 191 — ผู้ใช้เห็นใบเสร็จค่าห้องพักจากระบบจองกลับเป็น "ใบเสร็จรับเงิน" + เลข REC
        โดยไม่มีอะไรบอกเหตุ): `DocumentResponse.TaxInvoiceTitleNotice` ←
        `PdfGenerationService.AbbreviatedDowngradeNotice(doc, reason)` (รูปใบจาก `IsAbbreviatedTaxInvoiceDoc(doc, true)` ·
        เหตุผลจาก `AbbreviatedTaxInvoiceRule.Message`) → การ์ดแดงในหน้ารายละเอียดเอกสาร · คำอธิบายช่อง ภ.พ.06 ในหน้า
        ตั้งค่าบริษัทแก้จาก "สลิป POS" เป็น "ทุกเอกสารขายที่มี VAT แต่ผู้ซื้อไม่มีข้อมูลครบ" · **จงใจไม่ทำเป็นคำเตือนอนุมัติ**:
        ทางเข้า API v1 อนุมัติโดยไม่ยืนยันคำเตือน ⇒ คำเตือนใหม่จะทำให้ระบบภายนอกที่ส่งใบเสร็จมาล้มทั้งเส้น
      - **ใบกำกับเต็มรูป §86/4 ไม่เกี่ยวกับด่านนี้** — จด VAT แล้วออกได้เสมอ
      - ผู้เรียกที่ลืมส่งนโยบาย (`requirePhoR06` default `true` ใน `BuildDocumentHtml` /
        `RenderDocumentPdfNative`) ได้ทิศ**เข้มกว่า** ไม่ใช่ทิศที่ออกใบกำกับโดยไม่มีสิทธิ์
      - เทสต์: `AbbreviatedTaxInvoiceTitleTests` (3 เคสใหม่ รวมทิศตรงข้าม "ใบเต็มรูป
        ไม่ถูกแตะ") · `AbbreviatedTaxInvoiceRuleTests` (สวิตช์แพลตฟอร์มสองทิศ)

### 5.2 e-Tax XML (XAdES-BES, RSA-SHA256)
- **Service**: `EtaxInvoiceService.GenerateAsync` (`:87`)
- **Trigger**: เอกสาร approve แล้วผู้ใช้กด "ออกใบกำกับ e-Tax"
  หรือบริษัทเปิด auto-sign — อัตโนมัติผ่าน `IIssuedDocumentHooks.RunAsync` จาก**ทุกทางเข้าที่ออกเอกสาร** (§3.2 ขั้น 12 · รอบ 193 S-02) ·
  `GenerateAsync` ใช้ predicate ชุดเดียวกับ `EtaxAutoIssueScope` (ชนิด · มัดจำพักรอ · ฝั่งซื้อ `AdjustmentNoteAccount.SourceIsPurchaseSide`) ·
  ล็อกแถวเอกสาร `FOR UPDATE` + ตรวจซ้ำ (ออกซ้ำพร้อมกันไม่ได้) · สำเร็จ ⇒ ล้างป้าย `[ETAX-AUTO-FAILED]`
- **ขาเข้า (สแกน PDF ที่ฝัง XML)**: `EtaxPdfXmlExtractor.MapTypeCodeToInternal` ตาราง ETDA ครบ (T03 = ใบเสร็จ/ใบกำกับ) · §2.2 ข้อ #10
- **Types ที่ generate**: `Invoice` / `TaxInvoice` / `DebitNote` /
  `CreditNote` / `Receipt` (เฉพาะที่มี VAT)
- **Schema**: UBL 2.1 + ETDA profile (DocumentTypeCode T01–T04,
  ISO 8601 +07:00, CurrencyCode=THB)
- **Signing**: private key เก็บใน `Company.EtaxPrivateKeyBlobEncrypted`
  (AES-256 at rest); key ≥ 2048-bit; CA ที่ ETDA รับรอง
- **Storage**: `EtaxInvoice` entity → `XmlContent` + `EtaxRefNumber` +
  `CertificateSerialNumber` + `SignedAt`
- **Submission**: cron ส่ง batch ภายในวันที่ 15 ของเดือนถัดไป →
  `SubmittedToRdAt` — ⚠️ **ยังไม่มีในโค้ด** (ไม่มี hosted service ตัวไหนเรียก
  `SubmitToRevenueAsync`; `retry-failed` ก็ไม่มี UI เรียก) ดู `SYSTEM_AUDIT_2026-09-07.md` B-06
- **เมื่อยังไม่ได้ตั้งค่า RD API** (รอบ 147) — เดิม `SubmitToRevenueAsync` ตั้ง
  `Status=Submitted` + `SubmissionId="OFFLINE-…"` ทั้งที่**ไม่เคยยิงไปที่ไหนเลย**
  ⇒ เอกสารถูกประทับว่า "นำส่งแล้ว" และล็อกถาวร (void ไม่ได้ ส่งซ้ำไม่ได้).
  ตอนนี้ **ไม่ประทับ** — คงสถานะ `Signed` แล้ว throw
  `BusinessRuleException("ETAX-RD-NOT-CONFIGURED")` พร้อมบอกทางไปต่อ ·
  เส้น auto-submit จับ exception นี้แล้วคืนใบที่ลงนามแล้ว (ลงนามไม่ถือว่าล้ม) ·
  migration ล้างแถวเก่าที่ `SubmissionId LIKE 'OFFLINE-%'` กลับเป็น `Signed`
- **เลข `EtaxRefNumber`** (รอบ 147) — `ETAX-{TaxId}-{yyyyMMdd}-{seq:D6}` ออกผ่าน
  `Helpers/SequenceNumber.NextAsync` (advisory lock + integer-max + change tracker)
  ภายใน transaction เดียวกับการ insert · เดิมใช้ `CountAsync()+1` ซึ่งเลขวนกลับ
  ไปทับของเดิมเมื่อมีการลบแถว และสอง instance ได้เลขเดียวกัน
- **ด่านสิทธิ์** (รอบ 147) — `EtaxController` ทุก write endpoint ผ่าน
  `RequireEtaxAsync` → `PermissionKeys.EtaxIssue` (ออก/สร้าง PDF) ·
  `EtaxSubmit` (ลงนาม/นำส่ง/quick-submit/ส่งอีเมล/retry) · `EtaxVoid` (ยกเลิก) ·
  `CompanySettingsEdit` (แก้ config). ไฟล์นี้อยู่ใน `WATCHED` ของ
  `tools/write_permission_gate_check.py` แล้ว
- **ยอดเงิน (CII summation) — ต้อง ex-VAT ทั้งหมด** (`BuildLineItem` +
  summation `:667`):
  - per-line: `ChargeAmount` (unit price) + `ActualAmount` (discount) ถอด VAT
    เมื่อ `PricesIncludeVat=true` (`UnitPrice / (1+rate)`) → สอดคล้องกับ
    `BasisAmount`/`NetLineTotalAmount` (ex-VAT) ในบรรทัดเดียวกัน
  - `LineTotalAmount = doc.SubTotal` (net of discount = Σ NetLineTotalAmount)
  - `AllowanceTotalAmount = 0` (ส่วนลดเป็น line-level แสดงต่อบรรทัด ไม่ใช่ doc)
  - `TaxBasisTotalAmount = doc.SubTotal` (เดิม `SubTotal − Discount` หักซ้ำ
    เพราะ SubTotal net อยู่แล้ว → TaxBasis+Tax ≠ Grand → RD reject)
  - `GrandTotalAmount = SubTotal + VAT` (ไม่ใช่ `doc.TotalAmount` ที่หัก WHT —
    WHT แยกตอนจ่าย ไม่ใช่ face value ใบกำกับ)
  - invariant: `LineTotal − Allowance = TaxBasis` และ `TaxBasis + Tax = Grand` ✓
  - **ใบที่มีผลต่างปัดเศษ** (รอบ 193 · `DocumentRounding.EtaxSummation`): `LineTotalAmount = Σ บรรทัด` · ผลต่างเป็น Allowance/ChargeTotalAmount +
    `SpecifiedTradeAllowanceCharge` ระดับหัว · `TaxBasis = SubTotal` (invariant เดิมยังจริง · ยังไม่ได้ตรวจกับ XSD/Schematron ETDA จริง)
- **Receipt gate (`GenerateAsync`)** — e-Tax คือ "ใบกำกับภาษีในรูปอิเล็กทรอนิกส์"
  ใบที่**ตั้งใจไม่ให้เป็นใบกำกับ**จึงส่งไม่ได้ (Receipt/ReceiptVoucher ถูก map เป็น
  `T03 "ใบเสร็จรับเงิน/ใบกำกับภาษี"` เสมอ = ประกาศต่อ RD ว่าเป็นใบกำกับ):
  - **มัดจำ VAT รอเรียกเก็บ** (`DepositOutputVatDeferred` + ยังไม่ recognize) → block
    (ปล่อยผ่าน = XML บอก RD ว่าเป็นใบกำกับ ทั้งที่ ภ.พ.30 ยังไม่มียอดนี้ → ผู้ซื้อ
    เคลมภาษีซื้อจากใบที่ผู้ขายไม่เคยนำส่ง ทั้งสองฝั่งโดนประเมิน)
  - **ไม่ใช่ใบกำกับเต็มรูป** (`TaxService.NotFullTaxInvoice`: ข้อมูลผู้ซื้อไม่ครบ /
    walk-in / ติ๊กไม่ประสงค์รับใบกำกับ) → block พร้อมบอก field ที่ขาด · **รอบ 193 ขยายจากใบเสร็จไปใบกำกับ (TaxInvoice)** ·
    ธง `IsTaxInvoiceByLaw=false` → block (null = ใบเก่า ไม่ตีความ · ใช้เฉพาะใบกำกับ/ใบเสร็จ — CN/DN ธงตรึงจากหัว "ใบลดหนี้" จึงไม่ใช้กติกานี้)
  - ทั้งสองเคสข้อความบอกทางไปต่อ (ออก e-Tax ที่ใบกำกับตอนส่งมอบ / เติมข้อมูลผู้ซื้อ)
- **CN/DN gate (`GenerateAsync`)**: (1) ต้องมี `RelatedDocumentId` — Schematron
  DCN บังคับเลขที่+วันที่+มูลค่าใบเดิม; (2) **ฝั่งซื้อ block** — CN/DN ที่อ้าง
  PI/Expense/CIL/PV คือใบที่ผู้ขายออก เราเป็นผู้บันทึก ห้ามสร้าง/เซ็น e-Tax แทน.
  `DifferenceInformationAmount = doc.SubTotal` (ยอดลด/เพิ่มของใบเอง — สูตรเดิม
  `doc − ใบเดิม` ให้ค่าติดลบผิดความหมาย), `OriginalInformationAmount = ใบเดิม.SubTotal`

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
| **สปส.1-03** (ขึ้นทะเบียนเข้าใหม่) | `ExportSps103Async` | Employee.StartDate ในเดือน + IsSubjectToSocialSecurity |
| **สปส.6-09** (แจ้งออก) | `ExportSps609Async` | Employee.EndDate ในเดือน |

> **นำส่ง สปส. (สปส.1-10):** `PayrollService.SettleSocialSecurityAsync` post JE
> Dr 21815 / Cr Bank + เงินเพิ่ม §49 2%/เดือน. **Deadline tracker:** ตอนสร้าง/
> เลิกจ้างพนักงาน → `TrackSsoEmployeeFilingAsync` สร้าง `ComplianceFiling`
> (SSO_NewEmployee due+30วัน / SSO_Termination due วันที่15เดือนถัดไป).
> **หลักฐานการจ่าย:** โมดัลนำส่ง (`payroll.html`) + กล่อง "หลักฐานการจ่าย/นำส่ง"
> ในหน้ารายละเอียดรอบ แนบสลิป/ใบเสร็จผ่าน `FileAttachment` entityType
> `"PayrollRun"` (เก็บ 5 ปี ตาม พ.ร.บ.การบัญชี ม.10).
>
> **คู่ (ลูกจ้าง, นายจ้าง) ต้องสอดคล้องกันเสมอ** (ม.33 ใช้ฐานค่าจ้างเดียวกัน) —
> ตัวตัดสินตัวเดียวคือ `Helpers/SsoWageBase.Normalize` (ฝั่งลูกจ้างเป็นความจริง
> ฐาน+ฝั่งนายจ้างเป็นผลลัพธ์ · เกณฑ์ ±1 บาทเท่ากับด่านตอนนำส่ง) เรียกจาก **3 จุด**:
> 1. `ImportPayrollRunAsync` — ตอนนำเข้าจากระบบนอก + คืน **warning**
>    ใน `ImportPayrollRunResult.Warnings` ระบุชื่อพนักงาน/ยอดก่อน-หลัง
> 2. `ProcessPaymentAsync` — ตาข่ายรับสุดท้ายก่อนสร้าง JE
>    (`LastPaySsoAdjustedCount` → ข้อความตอบกลับของ `/pay`)
> 3. `ReopenPaidRunAsync` — ตอนกลับรายการจ่าย (`LastReopenSsoAdjustedCount`)
>
> _ที่มา: เส้น import ตรวจแค่ `net = gross − หักฝั่งลูกจ้าง` ส่วนฝั่งนายจ้างคัดมา
> ดิบ ๆ ⇒ TakeTime คิดนายจ้างจากค่าจ้างเต็มแต่คิดลูกจ้างจากฐานที่หักจริง ⇒ รวมทั้ง
> รอบต่างกัน 22 บาท (4,381 vs 4,403) ติดมาตั้งแต่วินาทีแรก และตรรกะซ่อมที่มีอยู่
> เขียนไว้ใน `ReopenPaidRunAsync` ที่เดียว ⇒ รอบที่ import แล้วเดินตรงไป "จ่าย"
> ไม่เคยผ่านการซ่อมเลย. **ห้ามซ่อมตอน Paid** — ตัวเลขจะไม่ตรงกับ JE ที่ลงไปแล้ว
> (ต้องกลับรายการจ่ายก่อน)_

#### 5.3a-1 ฐานยอด ภ.ง.ด.3/53/54 ของหน้า "นำส่งภาษี" + ปฏิทิน (แก้ 2026-09-16)

> **ที่มา**: ผู้ใช้พบว่า ภ.ง.ด.53 งวด ก.ค. หน้า "รายงานภาษี" ถูกต้อง แต่หน้า
> "นำส่งภาษี" แสดงคนละยอดคนละจำนวนราย. สองหน้านี้ mine ข้อมูลคนละชุดด้วย
> กติกาคนละแบบ — และสำเนาที่สองอยู่ใน **ไฟล์เดียวกัน** (`GetDashboardAsync`
> กับ `GetFilingCalendarAsync`) ⇒ คอมเมนต์/วินัยกันไม่ได้ ต้องยุบตัวตัดสิน

ทั้งสองเมธอดใช้กติกาชุดเดียวกันแล้ว (`StatutoryRemittanceService`):

| ข้อ | เดิม | ตอนนี้ |
| --- | --- | --- |
| ชนิดเอกสาร | **ไม่กรองเลย** ⇒ ใบขาย (`Invoice`/`TaxInvoice`/`DebitNote`) ที่ลูกค้าหักเราไว้ = **เครดิตภาษีของเรา** (Dr 11910 → ภ.ง.ด.50) ถูกนับเป็นเงินที่เราต้องนำส่ง | `Helpers/WhtRemitScope.PayerSideTypes` (PI · Expense · PV · CIL) — ลิสต์เดียวกับ `TaxService.GenerateWhtReport` |
| ใบตั้งหนี้ + ใบสำคัญจ่ายก้อนเดียวกัน | นับ **สองครั้ง** (และข้ามเดือนถ้าจ่ายคนละเดือน) | ตัดใบตั้งหนี้ที่มี PV คลุมแล้ว — ท.ป.4/2528 ภาระนำส่งเกิดที่ "การจ่าย" (กติกาเดียวกับ `settledSourceIds`) |
| แบ่ง 3 / 53 / 54 | `Contact.ContactType` **ดิบ** (default = Individual · 4 ทางเข้าไม่เคยตั้งค่า) และ ม.70 ตกไปอยู่ 53 | `Helpers/WhtPayeeKind.ResolveForm` — ตัวเดียวกับทะเบียน 50 ทวิ/รายงาน/ไฟล์ยื่น (เลขภาษี 13 หลัก → ContactType → คำในชื่อ; ต่างประเทศดู **ทั้ง** `IsForeignService` และ `CountryCode`) |
| "เลยกำหนด" | คิดจากวันที่ + หักกลบเฉพาะ **เงินที่จ่าย** ⇒ งวดที่ยื่นแบบไปแล้วยังขึ้น "เลยกำหนด N วัน" สีแดง | อ่าน `TaxReport.Status/FiledDate` ด้วย → `PendingRemittanceItem.ReportFiledAt`; ยื่นแล้ว = ไม่ overdue แต่ยังค้าง "รอบันทึกการนำส่งเงิน" |

> `Helpers/WhtPayeeKind` เป็นตัวตัดสินตัวเดียว — `WithholdingTaxCertService.DetectJuristic`
> และ `ResolveWhtFormType` delegate มาที่นี่ (เทสต์: `WhtPayeeKindTests` ล็อกสองทิศ
> — เคสที่เคยพังต้องถูก **และ** เคสปกติต้องได้ผลเดิม)

#### 5.3b ปฏิทินนำส่ง (Filing calendar) — วิดเจ็ต dashboard

- **Service**: `StatutoryRemittanceService.GetFilingCalendarAsync`
- **Endpoint**: `GET /api/companies/{id}/remittances/calendar?months=12`
- **UI**: `app.html` widget `filingCalendar` (ตาราง แบบ × เดือน) + action alert
  แถบแดงบนสุดเมื่อมีงวดเลยกำหนด; คลิกช่อง → deep-link
  `tax-remittance.html?type=&year=&month=` เปิดฟอร์มนำส่งงวดนั้นทันที

ต่างจาก `GetDashboardAsync` (ยอดค้าง) ตรงที่ปฏิทิน **ไม่ตัดงวดที่ยอด 0 ทิ้ง**
เพราะกฎหมายบังคับยื่นแม้ไม่มียอด — สถานะต่อช่อง:

| สถานะ | ความหมาย | ที่มา |
| --- | --- | --- |
| `Filed` | จ่ายครบ (หรือยอด 0 + ยื่นแบบแล้ว) | `StatutoryRemittance` / `PayrollRun.SsoSettledAt` / `TaxReport.Status=Filed` |
| `Partial` | ยื่นแล้วยังไม่จ่าย หรือจ่ายไม่ครบ | remitted < amount |
| `Pending` | ต้องยื่น ยังไม่ครบ (`Overdue=true` เมื่อเลยกำหนด e-Filing) | — |
| `Unknown` | ต้องยื่นแต่ระบบยังไม่ทราบยอด — **ยังไม่ได้สร้างรายงาน ภ.พ.30 / ยังไม่ได้รันเงินเดือน** | ไม่มี `TaxReport` / ไม่มี `PayrollRun` ของงวด |
| `NotRequired` | ไม่ต้องยื่น (ภ.ง.ด.3/53/ภ.พ.36 เดือนที่ไม่มีรายการ, ไม่ได้จดทะเบียน, งวดก่อนเริ่มใช้ระบบ) | — |

- แบบที่ **ต้องยื่นทุกเดือนแม้ยอด 0** (`FilingRule().Always`): ภ.พ.30 (§83),
  สปส.1-10 (§47), ภ.ง.ด.1 (ท.ป.4/2528) — ที่เหลือยื่นตามเหตุการณ์
  (รวม **ภ.ง.ด.54** ที่เดิมตกหล่นจากทั้ง `order` · `FilingRule` · `ReportTypeOf`
  ⇒ เดือนที่ต้องยื่น ม.70 ไม่มีอะไรเตือนเลย และยอดไปโผล่ปนใน ภ.ง.ด.53)
- กำหนดยื่น — **ตารางเดียวของทั้งระบบ** `Helpers/TaxFilingDeadline` (รอบ 164):
  ปกส. 15/15 · ภ.พ.30 กระดาษ 15 e-Filing 23 · **ภ.พ.36 กระดาษ 7 e-Filing 15**
  (§83/6 — ไม่ใช่ 23) · ภ.ง.ด.1/3/53/54 กระดาษ 7 e-Filing 15
  · เลื่อนพ้นเสาร์/อาทิตย์ตาม **ป.พ.พ. §193/8** (ยังไม่มีตารางวันหยุดราชการ — backlog)
  · e-Filing นับจากวันครบกำหนด **ก่อนเลื่อน** (เดิมปฏิทินเลื่อนกระดาษก่อนแล้ว +8
  ⇒ ช้ากว่ากฎหมาย 2 วันเมื่อวันที่ 7 ตรงเสาร์)
  · ผู้เรียก: `StatutoryRemittanceService.DueDates` · `TaxCalendarService` ·
  `TaxComplianceChecker.DeadlineFor` — **ห้ามเขียนเลขวันซ้ำที่อื่น**
- ใช้เวลาไทย (`UtcNow.AddHours(7)`) ตัดสินวันครบกำหนด ไม่ใช่ UTC
- งวดก่อน `Company.CreatedAt` และไม่มีร่องรอยในระบบ → `NotRequired` ไม่ขึ้นแดง
  (ระบบไม่มีข้อมูลจริง การเดาแล้วเตือนผิดทำให้ผู้ใช้เลิกเชื่อทั้งวิดเจ็ต)
- Unit test: `Accounting.Tests/FilingCalendarRulesTests.cs`

### 5.4 หนังสือรับรอง 50 ทวิ (WHT cert)
- **Service**: `WithholdingTaxCertService`
- **Trigger**: ตอน `PaymentVoucher` / `Expense` ที่มี WHT > 0 ถูก approve
  → auto-issue 2 ฉบับ ("สำหรับยื่นแบบ" + "เก็บไว้")
- **เกณฑ์เงินสด (ท.ป.4/2528) — ออกใบจริงเฉพาะตอน "จ่ายเงินแล้ว"**
  (`AutoGenerateFromDocumentAsync`):
  - **จ่ายแล้ว** (PV, ใบที่ `BalanceDue<=0 && PaidAmount>0` ตอน approve,
    ทุกเส้นรับ/จ่ายชำระ) → cert สถานะ **Issued** ผูก `SourcePaymentId` ของงวดนั้น
    ยอด = WHT ที่หักจริงงวดนั้น (`paymentWhtAmount` → `whtRatio`)
  - **ตั้งหนี้ยังไม่จ่าย** (integration `expense.created`, PaymentType.Credit)
    → cert สถานะ **Draft** เท่านั้น (`TryAutoGenerateWhtAsync(paid: false)`).
    Draft ไม่เข้าแบบยื่น — `TaxFilingExportService`/`TaxService` นับเฉพาะ
    Issued/Filed. ถ้าออก Issued ตั้งแต่ตอนตั้งหนี้: ภ.ง.ด.3/53 ของเดือนนั้น
    นำส่งภาษีที่ยังไม่ได้หักจริง **และ** guard "ออกใบเต็มจำนวนไปแล้ว" จะบล็อก
    ใบรายงวดตอนจ่ายจริง ⇒ ผู้ขายไม่ได้ใบที่ถูกต้องสักใบ
  - **Idempotency key** = (`SourcePaymentId`, `DocumentId`) — ไม่ใช่ payment
    ล้วน เพราะการโอนก้อนเดียวปิดหลายใบใช้ payment id ร่วมกันทุกใบ
  - เจอ cert ระดับเอกสาร (`SourcePaymentId == null`) ค้างอยู่ตอนจะออกรายงวด:
    **ทุกใบเป็น Draft → ยกเลิกอัตโนมัติแล้วออกรายงวดต่อ** (ใบร่างไม่เคยส่งมอบ
    และไม่เคยเข้าแบบยื่น); มีใบ Issued/Filed ปน → บล็อกเหมือนเดิม
  - ออกได้เฉพาะฝั่งซื้อ (`PurchaseInvoice`/`Expense`/`PaymentVoucher`/
    `CertificateInLieu`) — ฝั่งขายเราเป็น "ผู้ถูกหัก" ลูกค้าเป็นคนออกใบให้
    (เส้นรับชำระหลายใบเคยไม่กรองชนิดเอกสาร)
- **ข้อเสนอประเภทเงินได้/อัตราหักจากเส้น OCR ต้องมี "หลักฐานที่ผูกกับเงิน"** —
  `ExpenseCategoryResolver.Resolve` แบ่งหลักฐานเป็นสามชั้น: ชื่อผู้ขาย/หัวเรื่อง/
  **บรรทัดที่มียอด** = น้ำหนักเต็ม · **บรรทัดยอด 0** + `rawText` ทั้งใบ = ครึ่งเดียว ·
  และ `CategoryResult.MoneyBackedEvidence` เป็นเงื่อนไขบังคับของการเสนอ
  `SuggestedWhtRate` + `WhtIncomeTypeCode` (หมวดยังเสนอได้ตามปกติ). เหตุ: ใบจริง
  `TXE05202609T000434` มีแถว "ค่าจัดส่ง / Shipping Fee **0.00**" ที่แบบฟอร์มพิมพ์ไว้
  ทุกใบ ส่วนแถวที่มีเงินจริง 2,137.38 อ่านคำอธิบายไม่ออก ("0") ⇒ กฎ "ค่าขนส่ง" ชนะ
  จากคำบนแถวยอด 0 แล้วระบบเสนอ "40(8) ค่าขนส่ง · หัก 1%" ทั้งที่กระดาษไม่ได้บอกว่า
  จ่ายค่าขนส่ง — หมวดเดาผิดผู้ใช้แก้ได้ แต่ประเภทเงินได้ผิดไหลไป 50 ทวิ + ภ.ง.ด.3/53
  (เทสต์: `ExpenseCategoryResolverTests` ล็อกทั้งสองทิศ)
- **ประเภทเงินได้ ม.40 มาจาก `DocumentLine.IncomeTypeCode`** (ไม่ใช่หัวเอกสาร) —
  `AutoGenerateFromDocumentAsync` อ่าน `line.IncomeTypeCode ?? "8"` ต่อบรรทัด และ
  `TaxFilingExportService.MapIncomeTypeCode` ก็ map จากค่าเดียวกันลงไฟล์ ภ.ง.ด.3/53.
  **เส้น OCR เติมค่านี้ลงบรรทัดตอนสร้างเอกสารแล้ว** (`OcrService`:5626/5660 —
  ฝั่งซื้อ + `whtRate > 0` เท่านั้น) จากรหัส `ThaiWhtRateTable` ที่
  `ExpenseCategoryResolver` เดาไว้ (`8`/`5`/`8ad`/`8tr`/…); เดิมไม่เคยเซ็ตเลย ⇒
  ทุกใบจากเส้น OCR ตกเป็น `"8"` ค่าบริการอื่น ๆ เสมอ
- **PDF**: `PdfGenerationService.WhtCert.cs` (QuestPDF) **และ**
  `PdfGenerationService.cs:2237` (HTML) — **ทั้งสองตัวจัดบรรทัดลงแถว 1–6 ของแบบ
  ผ่าน `ThaiWhtRateTable.CertificateRow` ตัวเดียว** ห้ามเขียน allow-list ของรหัสเอง
  (แถว "อื่น ๆ" ของ HTML คือคีย์ `"other"` ซึ่งตรงกับ `"6"` ที่ helper คืน).
  เดิมฝั่ง HTML ยังเป็น allow-list พิมพ์มือ ⇒ รหัสที่ระบบเองสร้าง (`8ad` ค่าโฆษณา ·
  `8tr` ค่าขนส่ง · `4a`/`4b`) **หายจากทุกแถวแต่ยอดรวมท้ายตารางยังเต็ม**
  (D-03 แก้ฝั่ง QuestPDF ไปแล้ว เหลือฝั่ง HTML — "แก้ตัวเดียว เหลือที่เหลือ")
- **ประเภทแบบ guard (ภ.ง.ด.3 ↔ 53)**: `ResolveWhtFormType` + `DetectJuristic`
  บังคับที่ **ทุก create path** (`CreateAsync`, `AutoGenerateFromDocumentAsync`,
  `UpdateAsync`) — ประเภทแบบขึ้นกับ **ผู้ถูกหักภาษี**: นิติบุคคล → 53,
  บุคคลธรรมดา → 3. เดิมถ้า caller (เช่น integration/มังกร) ส่ง `TaxFormType` มา
  ระบบเชื่อทันที → บริษัทได้ใบ ภ.ง.ด.3 ผิด. guard ตรวจจาก 3 สัญญาณ (เลขภาษี 13
  หลักขึ้นต้น 0 = นิติบุคคล authoritative / ContactType / ชื่อ "บริษัท,หจก,Co.,Ltd")
  → override ค่าที่ส่งมาถ้าไม่ตรง + log correction. ภ.ง.ด.1 (เงินเดือน) / ภ.ง.ด.2
  (ดอกเบี้ย/ปันผล) ไม่แตะ (ขึ้นกับประเภทเงินได้). test: `WhtFormTypeGuardTests`
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
  (`NeedsReview = false`) → schedule depreciation ปกติ · ใบที่อนุมัติแล้วแต่บรรทัด
  ผัง 12xxx ยังไม่มีทะเบียน (อนุมัติก่อนฟีเจอร์ · ถูกข้าม) → ปุ่มขึ้นทะเบียนย้อนหลัง
  ในหน้ารายละเอียดเอกสาร (`POST /fixedasset/from-document/{docId}?lineId=`) ·
  สถานะรายบรรทัด "มีทะเบียนไหม" อ่านจาก `GET /fixedasset/document-lines/{docId}`
  ตัวเดียว ทั้งหน้าเอกสารและหน้าทะเบียน (ดู §3.2 ข้อ 9)

---

## 6. Cross-cutting

### 6.0a ไฟล์ที่ยื่นจริง ต้องเท่ากับรายงานบนจอ

| แบบ | ตัวสร้างไฟล์ | กติกาที่ต้องตรงกัน |
| --- | --- | --- |
| ภ.ง.ด.3 / 53 / 54 | `TaxFilingExportService.BuildPndRows` → `PndTextFileFormat` | นับเฉพาะ cert `Issued`/`Printed` (เท่ากับรายงาน) · **ใบที่ไม่มีบรรทัดย่อยต้องได้ 1 แถวจากยอดรวมของใบ** — เดิมวนเฉพาะ `cert.Lines` ⇒ ใบแบบนั้น**หายทั้งใบจากไฟล์** ทั้งที่หัวสรุปยังนับเข้า "จำนวนราย/ภาษีรวม" และจอแสดงปกติ ⇒ นำส่งขาดเงียบ ๆ (T-5) · รหัสประเภทเงินได้ผ่าน `MapIncomeTypeCode` เสมอ (default `"6"`) ห้ามปล่อย `"40(8)"` ดิบลงไฟล์ |
| ภ.พ.36 | `ExportPp36Async` → `TaxService.ComputePp36ReportAsync` | ใช้ **ตัวคำนวณเดียวกับรายงานบนจอ** (เดิม export คัดเอกสารเองคนละเงื่อนไข) |
| ภ.ง.ด.50 (สรุป xlsx) | `TaxService.Export.BuildSummarySheet` | `report.CitAmount` คือภาษี **ก่อน** หักเครดิต (`citPayable` ไม่ได้ถูกเก็บลง report) ⇒ ต้องแยก 3 บรรทัด: ก่อนเครดิต · หักเครดิต (อ่านจากบรรทัด `WHT_CREDIT` ของรายงานเอง ไม่คำนวณซ้ำ) · ต้องชำระเพิ่ม/ชำระเกิน. ป้ายเดิมเขียน "หลังเครดิต" บนยอดก่อนเครดิต ⇒ สรุปสูงเกินจริงเท่าเครดิตทั้งก้อน (T-6) |

### 6.0a-bis ปรับปรุงผังบัญชีของ JE (Adjust) — ข้อจำกัดที่ต้องรู้

`AdjustDocumentJournalEntryAsync` **ไม่แก้บรรทัดเอกสารเลย** — เจตนาผู้ใช้อยู่ใน
"ใบสำคัญปรับปรุง" ใบเดียว (description ขึ้นต้น `ปรับปรุงผังบัญชีของ`) ผลที่ตามมา:

- **ยกเลิก → คืนชีพ → อนุมัติใหม่** ⇒ ใบปรับปรุงถูกกลับพร้อม JE หลัก แล้ว
  AutoPost ลง JE ใหม่จาก `doc.Lines` (ผังเดิม) ⇒ ผังที่แก้ไว้หายเงียบ ๆ.
  `JournalAnomalyService` มีกฎ **`DOC-ADJUST-LOST`** (Warning) จับเคสนี้:
  ใบปรับปรุงถูกกลับหมด **และ** เอกสารมี JE หลักที่ active อยู่ → ขึ้นบนการ์ด 🩺
  พร้อมทางแก้ (ปรับปรุงใหม่ หรือใช้ ✏️ เปลี่ยนผัง ที่แก้ตัวบรรทัดจริงให้อยู่ถาวร)
  — กรอง `OriginalEntryId == null` เสมอ เพราะตัวกลับคัดลอกข้อความมาด้วย (X-6)
- **JE ที่มีบัญชีเดียวกันหลายบรรทัด** (ใบเสร็จ 2 รายการห้องพัก → Cr 41110
  สองบรรทัด) — ห้าม `ToDictionary(l => l.AccountId)` ตรง ๆ ใน Adjust:
  duplicate key ⇒ `ArgumentException` อังกฤษ ถูก middleware ปิดบังเป็น
  "ข้อมูลที่ส่งมาไม่ถูกต้อง" ⇒ กดบันทึกแผงไม่ได้ทั้งที่ตัวเลขถูกทุกอย่าง.
  ใช้ `GroupBy(AccountId)` ก่อนเสมอ (`NetBy` สะสมถูกอยู่แล้ว — จุดที่พลาดคือ
  map บัญชี→รหัสสำหรับข้อความ error)
- **วันที่ของตัวกลับ** — ใบปรับปรุงเลือกวันเองได้ (default = วันของ JE ต้นฉบับ)
  ตอน `VoidDocumentAsync` ตัวกลับต้องตกงวดของ **ใบที่มันกลับ** ไม่ใช่งวดของ
  เอกสารทั้งก้อน: ใบเดือน ก.ค. ที่ปรับปรุงลงเดือน ส.ค. ถ้ากลับที่วันเอกสาร ⇒
  ก.ค. มีตัวกลับที่ไม่มีคู่ (ต่ำไป) และ ส.ค. ยังมีใบปรับปรุงค้าง (สูงไป) —
  ผิดสองเดือนพร้อมกันโดยยอดรวมทั้งปียังตรง (X-7 · บทเรียนเดียวกับ X-1).
  ทุกวันยังผ่าน `ResolveReversalDateAsync` เพื่อ fallback เมื่องวดปลายทางปิด

### 6.0c-bis การจับคู่ธนาคาร — ให้คะแนน 1 ตัว · ตัดสิน 1 ตัว (รอบ 183 · D4-1/D4-2)

ก่อนรอบนี้สูตรให้คะแนนมี **5 สำเนาที่ให้อันดับต่างกัน** ⇒ อันดับที่ "คนเห็นบนจอ"
กับที่ "เครื่องประทับให้" ไม่ใช่อันเดียวกัน และทุกเส้นใช้ `score > bestScore` /
`FirstOrDefault` ⇒ **ใครมาก่อนชนะ** (รันสองครั้งได้คนละคำตอบ)

- **`Helpers/BankMatchScorer`** = สูตรให้คะแนนตัวเดียว (60 ยอดตรง / 30 วันเดียวกัน /
  +22 ชื่อผู้โอนมั่นใจ / +10 เลขอ้างอิง · ตัดที่ 100) — **ให้คะแนนอย่างเดียว ไม่ตัดสิน**
- **`Helpers/BankMatchArbiter`** = ตัวตัดสินตัวเดียว → `Apply` / `Suggest` / `None`
  · `ApplyMinScore = 80` · `SuggestMinScore = 60` · `ApplyMinMargin = 10`
  · **เสมอกันที่หัวตาราง = `Suggest` เสมอ** (ห้ามประทับ) · จัดอันดับ deterministic
  (`Score` → `HasIdentitySignal` → `Id`) เพื่อให้ลำดับแถวใน DB ไม่มีผล
  · ข้อยกเว้นเดียว `BANK-MATCH-IDENTITY`: ที่ 1 มีหลักฐานระบุตัวตนที่ที่ 2 ไม่มี
  = หลักฐานคนละชั้น (คะแนนถูกตัดที่ 100 ทำให้ระยะห่างที่วัดได้เล็กกว่าน้ำหนักจริง)
  · `ScreenAiProposals` ทิ้ง id ที่ AI แต่งขึ้น + conf < 0.70 ก่อนถึงการตัดสิน
  · `RankBySourceThenConfidence` — **เซิร์ฟเวอร์ชนะ AI เสมอ** แม้ conf ต่ำกว่า (G1)
- ผู้ใช้: `BankService.AutoMatchAsync` (Payment + JE) · `BankService.MatchCandidates`
  · `BankFeedService.TryAutoMatchAsync` · `OpenBankingService` · `BulkBankAiMatchService`
- **`Matched` ต้องมี id ของคู่เสมอ** (ราก R1) — BankFeed เคยประทับ `Matched` เมื่อเจอ
  *Document* ทั้งที่ตารางไม่มีคอลัมน์เก็บ document id ⇒ "เงินก้อนนี้มีที่มาที่ไปแล้ว"
  โดยไม่มีอะไรให้กดดู · ตอนนี้ไม่มีคู่ที่บันทึกได้ = ปล่อย `Unmatched` + log
  · migration คืนแถวเก่าที่ `Matched`/`Suggested` แบบไม่มีคู่ให้เป็น `Unmatched`
- **`Helpers/BankReconciliationTolerance`** — client เคยส่ง `Tolerance` เท่าไรก็ได้
  (ส่ง 1,000,000 แล้วกลุ่มที่ต่างกันเป็นแสนก็ "สมดุล") ⇒ ≤1 บ. ผ่าน · 1–20 บ.
  ต้องมีเหตุผลใน `Notes` · >20 บ. **ปฏิเสธเสมอ** (ต้องลงเป็นรายการจริง)
- **`Helpers/BankMatchAmountReconciler`** — แต่ละใบมี "ยอดเงินสดที่ผ่านธนาคาร"
  **ค่าเดียว** (`BankLineAmount` ถ้ารู้ ไม่งั้น `Recorded − Withheld − Fee`) —
  เดิมมีทั้ง net และ gross เป็น "สองโอกาสผ่าน" · ชนิดที่ระบบไม่รู้ทิศ = **ปฏิเสธ
  พร้อมบอกชื่อใบ** ไม่ใช่บวกเข้าไปเงียบ ๆ
- `UnmatchTransactionAsync` เขียน `BankMatchExclusion` ให้ทุก id ที่ถูกถอน
  (ปิดลูปเรียนรู้ — ผู้ใช้ยกเลิกได้ที่ `BankController.ClearMatchExclusion`)

**รอบ 184 — สกุลเงิน · ผู้กระทำ · คู่ที่เป็นเอกสาร · เช็ค/เงินสดย่อย**

- **`Helpers/BankMatchCurrency`** = ตัวแปลงสกุลตัวเดียวของระบบ · เดิม**ทุกเส้น**เทียบ
  `Payment.Amount` (สกุลเอกสาร) กับยอดบนบรรทัดธนาคารตรง ๆ ⇒ ใบ 30,780 USD กับเงินเข้า
  30,780 บาท ได้ "ยอดตรงเป๊ะ 60 คะแนน" ทั้งที่ต่างกัน 35 เท่า
  - **หักในสกุลเอกสารให้เสร็จ แล้วคูณอัตราครั้งเดียว** (ปัดทีละช่องคลาดได้ถึง 1.5 สตางค์ > tolerance)
  - ทิศต่างกันตามผู้ใช้: เส้น**อัตโนมัติ**ข้ามผู้สมัครที่แปลงไม่ได้ · หน้า**จับคู่ด้วยมือ**
    ยังโชว์แต่ตัดคะแนนยอดเป็น 0 + บอกเหตุผล · **ด่านตอนบันทึก** throw พร้อมบอกว่า
    ต้องกรอกอัตราที่ไหน (ด่านที่ไม่มีทางไปต่อ = ด่านที่ผู้ใช้ต้องหลบ)
- **`Helpers/BankMatchAttribution`** = เจ้าของค่า `ReconciledBy` + ป้ายบนจอ
  - `ReconcileAsync` (จับคู่ 1:1 ด้วยมือ) **ไม่เคยตั้ง `ReconciledBy` เลย** ⇒ ค้างค่าเดิม
    `"AutoMatch (เสนอ…)"` ⇒ จอบอกว่า "⚙️ ระบบ" ทั้งที่คนกด
  - Batch apply เคยเขียน `"AI-Batch"` **ทุกแถวแม้ `WasAiValidated = false`** ⇒ โกหกสองชั้น
    (บอกว่า AI ทำ · และทิ้งชื่อคนที่กด ทั้งที่ audit row รู้)
  - ตอนนี้: `👤 <ชื่อ>` · `👤 <ชื่อ> (ยืนยันคำแนะนำ AI)` · `🤖 AI` เฉพาะตอนเรียก provider จริง
    · ว่าง = "ไม่มีข้อมูล" ห้ามเดาเป็น "ระบบ" · migration คืนค่าที่ถูกจาก `BankMatchAuditLogs`
- **`BankTransactions.SuggestedDocumentId` + `MatchRuleCode` + `MatchReason`** —
  เส้น "AI เสนอเอกสาร" กลับมาใช้ได้ (รอบ 183 ปิดไว้เพราะไม่มีที่เก็บคู่) ·
  **ทุกสถานะต้องมีคู่ที่กดดูได้** และเหตุผลที่ arbiter ตัดสินต้องเดินทางถึงหน้าจอ
- **CAPTURE ปิดฝั่งยืนยันแล้ว** — จับคู่ 1:1 · Batch · กลุ่ม M:N ที่คนกดยืนยัน บันทึก
  แพตเทิร์นการเรียนรู้ · **`AutoMatchAsync` ไม่บันทึก** โดยตั้งใจ (คำตอบของเครื่องเอง
  ⇒ สอนตัวเอง = ราก R6) · ผู้ใช้ที่กลับมายืนยันคู่ที่เคยกด "ไม่ใช่" จะ**ถอนตัวอย่างลบ**เดิม
  · การบันทึกล้ม = **ล้มทั้งรายการ** (อยู่ใน transaction เดียวกัน) ไม่ใช่ `LogWarning`
- **`/api/v1/bank/matches/confirm` เดินผ่าน `BatchReconcileAsync`** — เดิมเขียนสถานะตรง
  ลงตาราง ไม่ผ่านด่านยอด/ด่านงวด/ไม่ล็อกแถว/ไม่มี audit (ราก R5)
- **บัญชีธนาคารต้องผูกผังบัญชี 111x** — ด่านนี้มีมาตั้งแต่รอบ 183 (กระทบยอด JE ต้องรู้ทิศ)
  แต่**ไม่มี UI ให้ผูกเลยทั้งหน้า** ⇒ ด่านที่ไม่มีทางไปต่อ · รอบนี้เพิ่มช่องในฟอร์ม
  บัญชีธนาคาร + แถบเตือนบนหน้ากระทบยอด + ด่านตรวจ tenant/111x ฝั่งเซิร์ฟเวอร์
- **เช็ค (`Helpers/ChequeBouncePlan`)** — เช็คเด้งที่ผูกกับการชำระเคยเปลี่ยนแค่สถานะ ·
  ใบยัง "ชำระแล้ว" · JE ยังอยู่ ⇒ ตอนนี้กลับรายการชำระทั้งชุด (JE/สถานะใบ/ยอดธนาคาร/
  ปลดกระทบยอด) · **เช็คที่ `Cleared` แล้วเด้งได้** (ธนาคารคืนเช็คหลังให้เครดิตชั่วคราว
  เป็นเรื่องปกติ — เดิมบันทึกเหตุการณ์นี้ไม่ได้เลย) · "ขึ้นเงินแล้ว" โดยไม่ระบุบัญชี
  = throw (เดิม `LogWarning` แล้วผ่าน: สถานะขึ้น ยอดไม่ขยับ)
- **เงินสดย่อย (`Helpers/PettyCashJePlan`)** — การเติมเงินเคย**ไม่มี JE เลย** (ยอดกองทุน
  ขึ้น GL ไม่ขยับ) · การเบิกที่ไม่เลือกผังก็ไม่มี JE ⇒ ตอนนี้ทั้งสองทางต้องมี JE หรือ throw
  · **ไม่มีใบเสร็จ = บันทึกได้ + ติดธง `IsNonDeductible` / `RD-65TER-9`** (ไหลเข้า worksheet
  บวกกลับเอง) — เลือกทิศที่ความเสียหายถูก**นับ** เพราะเงินออกจากลิ้นชักไปแล้วจริง
  ถ้าห้ามบันทึก ยอดในระบบจะไม่ตรงกับเงินในลิ้นชัก **ถาวรและมองไม่เห็น**
  ⚠️ ข้อนี้**ต่างจากตัวอักษรใน `CLAUDE.md` §L(9)** ที่เขียนว่า hard block — รอเจ้าของชี้ขาด
- **พยากรณ์เงินสด (`Helpers/CashForecastTiming`)** — เดิมใช้ `DueDate` ล้วน ⇒ ลูกค้าที่จ่าย
  ช้าประจำถูกนับว่าจ่ายตรงวัน · ตอนนี้เลื่อนตาม**มัธยฐาน**ความช้าจริง (ต้องมี ≥3 ใบ) ·
  ไม่มีประวัติ/จ่ายตรง/เคยจ่ายก่อนกำหนด = **ไม่ขยับเลย** (ห้ามมองโลกในแง่ดีโดยไม่มีสิทธิ์)

### 6.0a-ter เลขที่ JE ต้องนับ "ใบที่ Add ค้างยังไม่ save" ด้วย

ตัวขอเลข JE มี 2 ตัว (`DocumentService.GetNextJournalEntryNumberAsync` /
`AccountingService.GetNextEntryNumberAsync`) — ทั้งคู่ advisory-lock แล้ว query
MAX จาก **ฐานข้อมูล** ซึ่ง**มองไม่เห็น JE ที่ Add ค้างใน change tracker**

**เคสจริงที่ระเบิด (500 อ้างอิง BE996D32)**: อนุมัติใบกำกับภาษีที่แปลงจาก
ใบแจ้งหนี้ — `AutoPostToJournalAsync` เพิ่ม JE ใบกำกับ (SV-yyyymm-NNNN) แบบ
ยังไม่ save (ตัวมันไม่ SaveChanges เอง) → `SupersedeSourceInvoiceAsync` →
`ReverseJournalEntryAsync` ขอเลขให้ตัวกลับของใบแจ้งหนี้เดิม (Sales → SV
เดือนเดียวกัน) → ได้**เลขเดียวกัน** → unique (CompanyId, EntryNumber) ล้มตอน
SaveChanges → rollback ทั้งทรานแซกชัน = **อนุมัติใบกำกับแปลงไม่ได้เลยทั้งระบบ**
(deterministic — ไม่ขึ้นกับยอด/ข้อมูลบนใบ)

แก้ที่รากทั้งสอง generator: หลังคำนวณจาก DB ให้กวาด `_db.JournalEntries.Local`
หา pattern เดียวกันแล้วใช้ `max(DB, Local) + 1` — เส้นปกติ (ไม่มีใบค้าง)
พฤติกรรมเท่าเดิมเป๊ะ. เทสต์: `ConvertedTaxInvoiceApproveTests`

### 6.0b ด่านงวดบัญชีของสมุดรายวัน (X-9)

`AccountingService` ตรวจ **2 ชั้นเสมอ** ที่ Post / Update / Void:
`ValidateFiscalPeriodOpenAsync(FiscalPeriodId)` (FK ที่เก็บไว้) **และ**
`ValidateFiscalPeriodOpenForDateAsync(companyId, EntryDate)` (งวดของวันที่จริง)

- FK เป็น `null` ได้ปกติ (JE ที่ลงตอนบริษัทยังไม่ตั้งงวด / resolver หางวดไม่เจอ)
  ⇒ ด่านที่ดูแต่ FK ปล่อยผ่านทั้งที่งวดของวันนั้นถูกปิดย้อนหลังไปแล้ว
- **เปลี่ยนวันที่** → ตรวจงวดปลายทางด้วย + **re-resolve `FiscalPeriodId` ตามวันใหม่**
  (ไม่งั้นรายการค้างชี้งวดเดิม → รายงานรายงวดกับสมุดรายวันไม่ตรงกันถาวร)
- `PostJournalEntryAsync` เดิมปฏิเสธเฉพาะ `Closed` ⇒ งวด **`Locked`** (งวดที่ยื่น
  ภ.พ.30/ภ.ง.ด. แล้ว) ยัง post ทับได้ — ตอนนี้ใช้ด่านเดียวกับ Update/Void
- convention: **ไม่มีแถวงวดครอบวันนั้น = ถือว่าเปิด** (เหมือนที่อื่นทั้งระบบ)

### 6.0c การจับคู่ธนาคารที่ต้องถูกปลดเมื่อเงินเปลี่ยน

| เหตุการณ์ | สิ่งที่ต้องปลด | ที่มา |
| --- | --- | --- |
| ลบเอกสารถาวร (purge) | `MatchedPaymentId` ของ payment ที่ถูกลบ | มีอยู่เดิม |
| **ยกเลิกการชำระ** (`VoidPaymentAsync`) | `MatchedPaymentId` + สถานะ → Unmatched | **M-2** — `MatchAsync` แบบ 1:1 ไม่สร้าง `ReconciliationGroup` ⇒ `UnwindGroupsContainingItemAsync` ไม่แตะ; ผลคือรายการเดินบัญชีค้างสถานะ "กระทบยอดแล้ว" กับเงินที่กลับรายการไปแล้ว **และจับคู่ใหม่ไม่ได้เลย** (auto-match ตัด payment ที่ถูก match แล้ว + txn ไม่อยู่สถานะ Unmatched) |
| **เปลี่ยนแหล่งเงิน** (`ReclassifyPaymentSourceAsync`) | `MatchedJournalEntryId` ของ JE ที่ผูกเอกสารนี้ + สถานะ → Unmatched | **M-8** — GL บอกว่าเงินไม่เคยออกจากบัญชีเดิม แต่รายการเดินบัญชีของบัญชีเดิมยังกระทบยอดค้าง ⇒ บังคับให้จับคู่ใหม่กับบัญชีที่ถูก |

ทั้งสองเส้นเป็น best-effort หลัง commit (log warning เมื่อพลาด) — การชำระ/การแก้
ที่สำเร็จแล้วต้องไม่ถูก rollback เพราะงานกระทบยอด

### 6.1 Audit log (tamper-evident)
- **Entity**: `AuditLog(actorId, entityType, entityId, before, after, at, ip, reason, prevHash, rowHash)`
- **append-only** — ห้าม UPDATE/DELETE
- Hash chain — **`Helpers/AuditHashChain` ตัวเดียวทั้งเขียนและตรวจ** (รอบ 193 ทีม W): ฝั่งเขียน `Seal` (สูตร **v2**: normalize UTC + ตัดเหลือ
  ไมโครวินาที + format ตายตัว · `RowHash` ขึ้นต้น `v2:` · ตั้ง `row.Timestamp` = ค่าที่ hash) ผ่าน `AccountingDbContext.ApplyAuditHashChain`
  (`ResolveTip` ก่อน `Seal` — ปลาย chain = แถวที่รอบันทึกของบริษัทเดียวกันที่ไม่มีแถวอื่นผูกต่อ ก่อนค่าในฐาน) · `AddChainedAuditLog(row)` สำหรับจุดที่
  เพิ่มแถวเอง · ฝั่งตรวจ `Analyze` แยก **ถูกแก้** (เนื้อไม่ตรง RowHash) / **ขาดตอน** (PrevHash ชี้ hash ที่ไม่มีแถวไหนถือ) / **แตกกิ่ง** (หลายแถวชี้
  PrevHash เดียวกัน = คำขอพร้อมกัน · ไม่ใช่หลักฐานการแก้ · ไม่แจ้งลูกค้าแต่ log warning) · รายงานทุกแถว ไม่พึ่งลำดับ Id · แถว v1 เดิมตรวจแบบ
  legacy (สูตร v1 เป็น private · ลองคืนหลัก 100ns ที่ PostgreSQL ตัด) · แถวนอก chain (`RowHash=null`) นับแยก `unchainedCount` (ไม่ใช่ "ถูกแก้") ·
  `AuditChainVerifyJob` แจ้งด้วย `AlertMessage` ตามสาเหตุที่ตรวจพบจริง · **ขาดตอนไม่ฟันธงว่า "ถูกลบ"** (ฝ่ายค้านรอบสี่ P4-6 · 960e98cd): ผู้แก้ที่ประทับ RowHash
  ใหม่ (สูตร v2 ไม่มีกุญแจ) ทิ้งร่องรอยแบบเดียวกับการลบ ⇒ ข้อความ "แถวก่อนหน้า…ถูกลบ หรือถูกแก้แล้วประทับ hash ใหม่ (ระบบแยกสองกรณีนี้ไม่ได้) หรือแถวนี้ไม่ได้บันทึก
  โดยระบบ" · หัวแจ้งเตือน job "ขาดตอน (แถวก่อนหน้าถูกลบหรือถูกแก้)" · `verify-hash-chain` endpoint ต้อง `CompanySettings.Edit` + ปฏิเสธ API key ·
  _ที่มา: `Timestamp` เป็น `timestamp without time zone` ⇒ อ่านกลับ `Kind=Unspecified` ความละเอียดไมโครวินาที ⇒ สูตรเดิม (`"O"`) ตรวจไม่ผ่าน
  **ทุกแถวของทุกบริษัท** · `AuditTrailController` มี canonical สำเนาที่สองของตัวเอง_ · ⚠️ เทสต์ `AuditHashChainTests` **จำลอง** การอ่านกลับ ไม่ผ่าน
  PostgreSQL จริง · สองเครื่องเขียนพร้อมกันยังได้ fork (serialize การประทับ = คำถามเจ้าของ) · `AuditLogs.Add(...)` ตรงยังเหลือหลายจุด (นอก chain)
- write จุดสำคัญ: Create, Update (Draft), Approve, Void, Payment,
  WHT cert issue, e-Tax submission, DSR access
- **งานทำลายหลักฐานปฏิเสธ API key ทุกชนิด** (`[RejectApiKey]` → `OwnerActionGuard.DenyResult` · 403 `OWNER-ACTION-NO-API-KEY` · ต้องทำบนเว็บ):
  ลบเอกสารถาวร (`force` ข้ามช่วงเก็บ 5 ปี · `DocumentController.cs:953`) · ลบ 50 ทวิถาวร · DSR erase (PDPA)

### 6.2 PDPA (ม.26 / ม.37 / ม.39)
- **Sensitive PII** (เลขบัตร 13 หลัก, salary, sensitive contact data) →
  AES-256 at rest, KMS key
- Display mask `1-XXXX-XXXXX-XX-3` — full value เฉพาะ role `pii:view`
- `PiiAccessLog` ≥ 1 ปี
- DSR endpoints `/dsr/access | rectify | erase | portability` SLA 30 วัน
  (cascade-erase ยกเว้น legal_hold ของ พ.ร.บ.บัญชี/สรรพากร — MAX retention) · erase ปฏิเสธ API key (§6.1)
- **บันทึกความยินยอมแทนเจ้าของข้อมูล** (`POST …/pdpa/consent` · `GrantConsent`) ต้อง `Pii.View` (`RequireDpoAsync` — ด่านเดียวกับถอนความยินยอม ·
  รอบ 193 · เดิมไม่มีด่าน ⇒ ปลอมหลักฐานความยินยอมได้) · `Submit`/`ReportBreach` ยังเปิดให้ทุกคน (ม.37(4) นับ 72 ชม.)
- **ข้อมูลพนักงาน** (รอบ 193 P2): เลขผู้เสียภาษีพนักงานปิดบังแบบเลขบัตร (`1-XXXX-XXXXX-XX-8`) · ผู้ไม่มี `pii:view` เปิดแก้แล้วบันทึก ⇒ ค่าที่เท่ากับค่า
  ปิดบังของของเดิม = ไม่แตะ (`EmployeeRecordEdit.IsMaskedEcho` — เดิมเบอร์/อีเมลจริงถูกทับด้วยค่าปิดบัง)

**ความยินยอมตอนสมัครสมาชิก (ม.19) — ด่านก่อนเข้าระบบทุกทาง**
- ทุกทางที่ "สร้าง User ใหม่" ต้องผ่าน `AuthService.RequireSignupConsent()` +
  `RecordSignupConsentAsync()` — ไม่มีทางไหนสร้าง User โดยไม่เรียก 2 ตัวนี้
  | ทาง | ด่าน | ช่องทางที่บันทึก |
  | --- | --- | --- |
  | ฟอร์มสมัคร (`POST /api/auth/register`) | `AcceptedTerms` ต้อง true | `web-form` |
  | รับคำเชิญเข้าบริษัท (`?invite=` บนฟอร์มเดียวกัน) | เหมือนกัน | `web-form` |
  | Google / Facebook / LINE (`POST /api/auth/sso`) | บังคับ**เฉพาะตอนสร้าง user ใหม่** — ผู้ใช้เดิมเข้าระบบไม่ถูกขวาง | `sso-google` / `sso-facebook` / `sso-line` |
- แถวที่เขียน: `PdpaConsentRecord` scope **ระดับแพลตฟอร์ม**
  (`CompanyId = PdpaPolicy.PlatformScopeCompanyId` = `Guid.Empty` — ตอนสมัคร
  ผู้ควบคุมข้อมูลคือผู้ให้บริการ ไม่ใช่ tenant ที่ยังไม่เกิด; ทางรับคำเชิญไม่สร้าง
  บริษัทเลยด้วยซ้ำ) · `Purpose = PdpaPolicy.SignupPurpose`
- `EvidenceHash` = `PdpaConsentEvidence.ComputeHash(...)` — canonical **ตัวเดียว**
  ของทั้งฝั่งเขียนและฝั่งตรวจ (`Helpers/PdpaSignupConsent.cs`), ผูก
  `Email|Purpose|เวอร์ชันที่หน้าเว็บแสดง|GrantedAt|Channel|IP|UserAgent`
  → round-trip test: `Accounting.Tests/PdpaSignupConsentTests.cs`
- เวอร์ชันนโยบายอยู่ในโค้ด (`PdpaPolicy.CurrentVersion`) เพราะเนื้อความอยู่ใน
  `wwwroot/terms.html` + `privacy.html` ซึ่งเปลี่ยนได้ด้วย deploy เท่านั้น —
  **แก้เนื้อความเมื่อไรต้องขยับเวอร์ชันด้วย**
- ตัวตนผู้ควบคุมข้อมูลบนหน้าเอกสาร ← `GET /api/legal/policy`
  ← `SiteSettings.PlatformSeller*` (ชุดเดียวกับที่ใช้ออกใบกำกับค่าบริการ)

### 6.2b Convention ยอดรายบรรทัด — `DocumentLine.Amount` ต้องเป็น **net (ก่อน VAT)** เสมอ

จริงเสมอไม่ว่าราคาที่กรอกจะรวม VAT หรือไม่ (`ComputeLineAmounts` คืน `NetAmount`
ลงช่องนี้) เพราะ JE ฝั่งซื้อลง `Dr ค่าใช้จ่าย = Σ Line.Amount` แล้วบวก
`Dr ภาษีซื้อ` แยกอีกขา — ถ้า `Amount` รวม VAT มาแล้ว **เดบิตจะเกินเครดิตเท่ายอด
VAT พอดี**

| ชั้น | ที่อยู่ | ทำอะไร |
| --- | --- | --- |
| ต้นทาง | `OcrService` (9a0a882) | `Amount = PricesIncludeVat ? amount − lineVat : amount` |
| ซ่อมตอนใช้ | `ApproveDocumentAsync` (ก่อนเก็บ warning/JE) | เจอลายเซ็น "เก็บ gross" → หัก VAT รายบรรทัด + `SaveChanges` + log |
| ซ่อมย้อนหลัง | `DatabaseMigrationHelper` backfill | `UPDATE DocumentLines SET Amount = Amount − VatAmount` ตามเงื่อนไขเดียวกัน (idempotent) |
| ตัวตัดสิน | `Helpers/DocumentLineVatConvention.cs` | `LinesStoredGross` / `RepairedAmount` / `ExplainImbalance` — **ตัวเดียวทั้ง 3 ชั้น** |
| ข้อความ error | `ApproveDocumentAsync` + `ErrorMessageTranslator` | ส่วนต่าง = ยอด VAT พอดี → บอกสาเหตุ + ทางแก้ (ไทย **และ** อังกฤษ) |

ลายเซ็นที่ถือว่า "เก็บ gross" (ต้องครบทุกข้อ): `PricesIncludeVat` · `VatAmount > 0.02` ·
`Σ Line.Amount ≈ SubTotal + VatAmount` · `Σ Line.VatAmount ≈ VatAmount` ·
และ `Σ Line.Amount ≉ SubTotal` — ซ่อมแล้วเงื่อนไขเป็นเท็จเอง ⇒ รันซ้ำได้
**ยอดหัวเอกสาร (SubTotal/VatAmount/TotalAmount) ไม่ขยับ** เปลี่ยนแค่ส่วนแบ่งในบรรทัด
เทสต์: `Accounting.Tests/DocumentLineVatConventionTests.cs` (ตัวเลขจากใบจริง
1,304.68 + 91.32 = 1,396.00 → Dr 1,487.32)

### 6.2e บรรทัดเอกสาร **ห้ามติดลบ** — ยอดหักระดับบิลมีที่ของมันเอง (รอบ 184)

**ตัวตัดสินตัวเดียว: `Helpers/DocumentLineKind`** (`Judge` = ติดลบได้ไหม ·
`AllocateDeduction` = เฉลี่ยยอดหักระดับบิลลงบรรทัด)

เรพ**ประกาศกติกานี้ไว้ก่อนแล้ว** ที่ `DocumentDtos.cs:26-28` (เงินมัดจำที่หัก
เป็นช่องหัวเอกสาร + แถวสรุป "line ยังเป็นการขายเต็มจำนวน — ห้าม line ติดลบ")
แต่ CMS และ POS สร้างบรรทัดติดลบกันคนละทาง ⇒ รอบนี้ยุบเหลือกติกาเดียว

**หลักฐานว่าบรรทัดติดลบพังจริง (เปิดไฟล์ยืนยันทุกข้อ):**
| # | ที่ไหน | อาการ |
| --- | --- | --- |
| 1 | `EtaxInvoiceService.cs:1272-1325` | XML ปล่อย**ยอดติดลบ 5 จุด** (`ChargeAmount` · `BasisAmount` · `CalculatedAmount` · `NetLineTotalAmount` · `NetIncludingTaxes…`) ทั้งที่โปรไฟล์มีช่องของส่วนลดอยู่แล้วคือ `SpecifiedTradeAllowanceCharge` (`:1310-1315`) ซึ่งรับ `line.DiscountAmount` |
| 2 | `EtaxInvoiceService.cs:843-855` | หัวใบตรึง `AllowanceTotalAmount = "0.00"` **พร้อมเหตุผลที่เขียนไว้เอง** ว่าส่วนลดเป็น line-level ⇒ มีสองช่องทางสำหรับแนวคิดเดียวกัน |
| 3 | `DocumentService.cs:13417-13422` | JE ได้ **เครดิตติดลบ** (contra ที่ไม่มีใครประกาศ) · `AddLine` ไม่ตรวจเครื่องหมาย ขณะที่เส้น JV ที่คีย์มือตรวจ (`:5808` `:6541`) = กฎสองชุดในไฟล์เดียว |
| 4 | `Helpers/Pp30SalesClassifier.cs:68-70` | บรรทัดที่ `VatRate == 0` ตกเข้า **ช่อง 7 "ยอดขายอัตราร้อยละ 0 (§80/1 ส่งออก)"** ⇒ บรรทัดส่วนลดของ CMS ถูกรายงานเป็น**ยอดส่งออกติดลบ** |
| 5 | §86/4(5) | บังคับ "ปริมาณ + มูลค่า" ของสิ่งที่**ขายจริง** — บรรทัดติดลบไม่ใช่รายการที่ขาย |

> ⚠️ **สิ่งที่ตรวจแล้วไม่จริง**: ยอด**หัวใบ** XML ยังบาลานซ์ (`LineTotalAmount =
> TaxBasisTotalAmount = doc.SubTotal` และ `GrandTotal = SubTotal + VAT`) เพราะ
> `SubTotal` = Σ `line.Amount` อยู่แล้ว — ที่พังคือ**ระดับบรรทัด** · และในเรพ
> **ไม่มี Schematron assertion เรื่อง non-negative** จึงพิสูจน์ "RD reject" จากโค้ดไม่ได้

**ทางที่ถูก (มีอยู่ในระบบแล้ว)**: `DocumentLineRequest.DiscountAmount` รายบรรทัด
และ `CreateDocumentRequest.BillDiscountAmount` + `AllocateBillDiscount` ซึ่ง**ลดฐาน
VAT จริง** และ renderer พิมพ์ให้เห็น (คอลัมน์ส่วนลดต่อบรรทัด · แถว "รวมส่วนลด" ·
แถว "ส่วนลดท้ายบิล")

- **CMS** (`BuildOrderErpLines`) — ส่วนลดออเดอร์เฉลี่ยลง `DiscountAmount` รายบรรทัด
  (Σ เป๊ะ · เศษไปบรรทัดใหญ่สุด · clamp ที่ยอดบิล) แทนบรรทัด `UnitPrice` ติดลบ
  ⇒ **ออเดอร์ที่มีส่วนลดสร้างเอกสารได้แล้ว** (เดิม throw ⇒ ไม่มีเอกสาร/JE/AR/ภาษีขาย)
- **POS** (`PosTaxInvoiceLines`) — ทุกบรรทัดเป็นบวก · ส่วนลด/คูปอง/ปัดเศษลง/
  ค่าบริการติดลบ กลายเป็น "ยอดหักระดับบิล" ⇒ `Document.BillDiscountAmount`
  · **`VatRate` เป็นอัตราตามกฎหมาย (0/7/-1) เสมอ ไม่ใช่ค่าที่คำนวณได้**
- ⚠️ **ยอดหัวเอกสารไม่ขยับแม้แต่สตางค์เดียวในทุกเคส** — ย้ายแค่ "ที่อยู่" ของยอดหัก

**`VatRate` ต้องเป็นอัตราตามกฎหมายเท่านั้น** — `TaxService` ใช้
`Max(l.VatRate)` พิมพ์ลงรายงานภาษีขาย/แบบยื่น **11 จุด** และ `EtaxInvoiceService`
ใช้เป็นอัตราหัวใบ+รายบรรทัดใน XML ที่ยื่น RD · อัตราที่คำนวณได้เคยออกมาเป็น
`7.01` และ `-0.00` ⇒ ใบกำกับประกาศอัตรา 7.01% ต่อสรรพากร · migration ซ่อม
**เฉพาะอัตราที่ประกาศ** ไม่แตะยอด

### 6.2g ซื้อบริการจากต่างประเทศ (ภ.พ.36 §83/6) — แก้ใบที่ "ลืมติ๊ก" ย้อนหลังได้ (รอบ 186)

**ตัวแยกขาเครดิต: `Helpers/ForeignServiceVat`** · **ตัวตรวจว่าลืมติ๊ก:
`Helpers/ForeignServiceEvidence`** · **ตัวแก้ย้อนหลัง:
`DocumentService.ReclassifyForeignServiceAsync`**

ผู้ขายต่างประเทศ (Booking.com · Google Ads · Agoda) **ไม่เก็บ VAT ไทย** — ผู้รับ
บริการในไทยประเมิน 7% เองแล้วนำส่งด้วย ภ.พ.36 แทน ⇒ ขาเครดิตแตกสองก้อนเสมอ:

| | ติ๊กถูก (§83/6) | ลืมติ๊ก |
| --- | --- | --- |
| ค่าใช้จ่าย | Dr ฐาน | Dr ฐาน |
| ภาษีซื้อ | Dr **11640** (พักจนนำส่ง §77/2) | Dr 11640 หรือ 11610 ตามความครบของใบ |
| หนี้ ภ.พ.36 | **Cr 21912 = VAT** | **ไม่มี** |
| ผู้รับเงิน | Cr **ฐานเท่านั้น** | Cr **ฐาน + VAT** |

**ใบที่ลืมติ๊กพัง 3 ทางพร้อมกันและเงียบทั้งสามทาง**: (1) ไม่มีหนี้ ภ.พ.36 ⇒ ไม่โผล่
หน้านำส่งภาษี ⇒ ไม่เคยนำส่ง ⇒ เบี้ยปรับ/เงินเพิ่ม ม.27 (2) เครดิตผู้รับเงินเกินไป
เท่ากับ VAT ⇒ เจ้าหนี้/ธนาคารเกินจริง ⇒ กระทบยอดธนาคารไม่ลง (3) ภาษีซื้อค้างที่
11640 **โดยไม่มีทางเคลม** — เส้นปลดล็อก ภ.พ.36 อ่าน `IsForeignService` และเส้น
§86/4 ปิดอยู่เพราะผู้ขายต่างประเทศไม่มีเลขภาษีไทย ⇒ ครบ 6 เดือนงาน §82/3 โยนเป็น
ค่าใช้จ่าย ⇒ เสียสิทธิเคลมถาวร

**ก่อนอนุมัติ** — คำเตือน `RD-83/6-UNFLAGGED` ขึ้นเมื่อใบซื้อที่มี VAT ยังไม่ติ๊ก
และ (ประเทศคู่ค้าไม่ใช่ไทย) **หรือ** (เลขผู้เสียภาษีไม่ใช่รูปไทย 13 หลัก **และ**
ภาษีซื้อถูกพักที่ 11640) · **เป็นคำเตือน ไม่บล็อก** เพราะหลักฐานตอบไม่ได้ 100%
(สาขาบริษัทต่างชาติที่จด VAT ไทยแล้วออกใบกำกับไทยได้ ไม่ใช่ §83/6)

**หลังอนุมัติ** — ปุ่ม "↔ แก้เป็นบริการต่างประเทศ (ภ.พ.36)" บนหน้าเอกสาร →
`POST {documentId}/reclassify-foreign-service` (สิทธิ์เท่า Approve) ระบบ**กลับ JE
เดิมทั้งหมดแล้วลงใหม่**ผ่าน `AutoPostToJournalAsync` ตัวเดิม (ไม่ใช่ปะตัวเลขใน
รายงาน — ไม่งั้น GL กับแบบยื่นขัดกันเอง) · ตัวกลับลงวันที่ **ของเอกสารเอง**
· ล้าง `InputVatAccountCodeOverride` เมื่อเปลี่ยนเป็น §83/6 (กติกาบังคับ 11640)
· **gate ชุดเดียวกับย้ายฝั่ง CN/DN**: งวดต้องเปิด · ไม่มีเอกสารปลายทาง ·
**ไม่มีรายการจ่ายชำระแยก** · ไม่อยู่ในรายงานภาษีที่ยื่นแล้ว · ไม่ได้ส่ง e-Tax ·
เพิ่มเฉพาะของ §83/6: ต้องมี VAT · ห้ามโหมดราคารวมภาษี · และห้ามทำหลังภาษีซื้อ
ถูกย้ายออกจาก 11640 แล้ว (`InputVatBecameClaimableAt`/`InputVatExpiredAt`)

**ยังไม่ได้ทำ** — ใบที่ **ยื่นภาษีงวดนั้นไปแล้ว** ต้องยื่นแบบเพิ่มเติม ระบบกันไว้
ด้วย error ไม่ได้แก้ให้เงียบ ๆ

### 6.2h ไฟล์แนบหลักฐาน · ด่าน §86/4 ของใบกำกับซื้อ (รอบ 190)

- **ไฟล์แนบของเอกสาร** แนบ/ลบได้ทุกสถานะ (รวมใบที่อนุมัติแล้ว — ไฟล์หลักฐานไม่ใช่เนื้อใบกำกับ จึงไม่ขัด "ห้ามแก้ย้อนหลัง")
  · ต้องมีสิทธิ์**สร้างหรืออนุมัติ**ประเภทเอกสารนั้น (ด่านใน `FileAttachmentController` — เดิมแค่ `[Authorize]`) ·
  ชนิดไฟล์ตัดสินจาก**ไบต์** (`UploadFileType.SniffAttachment`) ไม่ใช่นามสกุล (HTML ที่ตั้งชื่อ `.csv` ถูกปฏิเสธ) ·
  **ลบ** = ถอดจากรายการ · ไฟล์จริงถูกลบเฉพาะใบร่างและข้อมูลหลัก (ผู้ติดต่อ/สินค้า) — นอกนั้นเก็บไฟล์ไว้ (soft-delete ·
  `Helpers/AttachmentRetention` · พ.ร.บ.การบัญชี ม.10 / §87/3 · ชนิดที่ไม่รู้จัก = เก็บ)
- **ด่านไฟล์แนบทุกชนิด — service ตัวเดียว `IAttachmentAccessGate`** (รอบ 193 · คำตัดสิน #29 · ทีม U2 → S2): ตาราง "ชนิด → วิธีตัดสิน + คีย์"
  `Helpers/AttachmentPermissionScope` (คีย์เดิมของโมดูลเจ้าของทุกตัว — ไม่สร้างคีย์ใหม่) ครอบ **แนบ/ลบ/ดูรายการ/ดาวน์โหลด**:
  Document/Expense = สร้างหรืออนุมัติชนิดนั้น + ฝั่งที่มองเห็น (`VisibleDirectionsAsync`) + ชั้นความลับ · Payment = ด่านของเอกสารที่ถูกชำระ ·
  PayrollRun = ความลับ Payroll (`Payroll.View` อ่าน · `Payroll.Run` เขียน) · JournalEntry = `Journal.Manage` + ความลับของ JE · Contact `Contact.Edit` ·
  Product `Product.Edit` · Project `Journal.Manage` · FixedAsset `Asset.Manage` · WhtCredit/StatutoryRemittance `Tax.File` · Subscription* `Billing.Manage` ·
  LodgingReservation `Lodging.Manage` (อ่านด้วย — สลิปแขก) · SiteOrder `CMS.OrderManage` (อ่านด้วย) · OcrScan ตามเจ้าของไฟล์ (ข้างล่าง) ·
  **ชนิดที่ไม่รู้จัก = ห้ามเขียน (403 ไทย + ทางไปต่อ)** · ฝั่งเขียนตรวจว่าเจ้าของมีอยู่จริงในบริษัท (404) · ชื่อที่เก็บเป็นชื่อมาตรฐาน (alias `contacts`/`products`) ·
  การ**อ่าน**ของ Contact/Product/FixedAsset/Project/StatutoryRemittance/WhtCredit/บิลลิ่ง = สมาชิก (เท่าตัวโมดูล — เข้มกว่านี้ต้องมีคีย์ "ดู" ใหม่ = คำถามเจ้าของ) ·
  WhtCredit แนบได้แล้ว (เดิมถูกตีกลับทุกครั้ง) · _เดิมด่านครอบเฉพาะ `"Document"` และเฉพาะแนบ/ลบ ⇒ ดาวน์โหลดสลิปเงินเดือน/ใบนำส่ง ปกส. ได้ถ้ารู้ id_
  - ผู้ใช้: `FileAttachmentController` (wrapper `DenyAttachmentAsync`) · **ใบเสร็จนำส่ง** (`StatutoryRemittanceController.UploadReceipt`: ด่าน `Tax.File` +
    รายการมีอยู่ในบริษัท (404) **ก่อน**อ่านไฟล์ · ไบต์ตัดสินชนิด `SniffAttachment` · 25MB) · รูป/ผลอ่าน/รายการ/คิว/ทุก action ของสแกน (ข้างล่าง) ·
    `DocumentController.GetLinkedScan` (ด่านอ่านเอกสาร) · นำเข้าจากโปรแกรมอื่น (`CompetitorImportController` preview+import → `ImportExportPermissionScope.ForImport`)
  - เส้นที่ระบบแนบเอง (OCR/LINE/V1/บิลลิ่ง/ที่พัก/CMS/50 ทวิ/นำส่ง) เขียนผ่าน service ตรง = การกระทำของระบบ · ⚠️ static `/uploads/` ยังเปิด (คำถามเจ้าของ)
  - checker `tools/attachment_gate_check.py` (ทุกเมธอดใน `Controllers/**` ที่แตะ `IFileAttachmentService`/`FileAttachments`/`IOcrService.ScanAsync…` ต้องอยู่ใน
    TARGETS หรือ EXEMPT · ทุก `[Http…]` ของ OcrController ที่รับ `scanId`/`fileAttachmentId`/`documentId` ต้องเรียกด่านคู่กัน · ต้อง "ใช้ผล" + ก่อน sink)
- **สแกน (OcrScan) ใช้ด่านของเจ้าของไฟล์** — `AttachmentPermissionScope.ScanOwner(..., fileOwnerExists)`: ① ไฟล์เป็นของรายการที่ไม่ใช่สแกน (ทุกชนิด) → ด่านรายการนั้น
  ② สแกนชี้เอกสารที่ยังอยู่ → ด่านเอกสาร ③ สแกนชี้ JE ที่ยังอยู่ → ความลับ JE ④ นอกนั้น → OCR · ดูสแกนพี่น้องที่ใช้ไฟล์เดียวกันด้วย (`PickLinkedOwner` —
  แถวตัวเองก่อน) · ลบร่างที่สร้างจากสแกน ⇒ สแกนกลับเป็น "ยังไม่ผูก" จริง · `OcrController` 24 action ที่รับ scanId ผ่าน `ScanGateAsync` (แก้สแกนที่**ยังไม่ผูก**
  จากหน้ารีวิว = ระดับสมาชิกเหมือนเดิม) · `POST ocr/scan/{fileAttachmentId}` → `DenyScanSourceAsync` (สแกนไฟล์ของรายการอื่นต้องอ่านได้ก่อน) ·
  `link-document` = ด่านเขียนของสแกน + เอกสารปลายทาง · `GET ocr`/`review-queue`/`amount-audit`/`open-pos`/`predecessor-candidates` ตัดแถวที่เปิดไม่ได้
  (`HiddenScanIdsAsync`/`HiddenDocumentIdsAsync` · คง `TotalCount` แบบลิสต์เอกสาร) · `link-po`/`link-predecessor` ห้ามผูกกับเอกสารที่มองไม่เห็น ·
  **สร้างผลบัญชี/สต็อกจากสแกนต้องมีคีย์โมดูล** (`Helpers/OcrScanPostingKeys` · `PostingGateAsync`): register-asset `Asset.Manage` · import-stock ตรวจทุกผล
  (สินทรัพย์ `Asset.Manage` · สินค้า/วัสดุ `Inventory.Receive` หรือ `Product.Edit`) · **ย้ายไฟล์ได้เฉพาะไฟล์ OcrScan หรือไฟล์ของเอกสารใบเดียวกัน**
  (`ScanFileRelinkable` ใน Relink · `LinkScanToExistingDocumentAsync` (400 ไทย) · relink-on-read ของ `GetByEntityAsync`) ⇒ สแกนที่สร้างจากไฟล์ของรายการอื่น
  แล้วกด "สร้างเอกสาร" = เอกสารใหม่ไม่มีไฟล์แนบ (ผู้ใช้อัปโหลดเอง)
- **ใบเบิก (ExpenseClaim)**: ด่านสิทธิ์ทุก action อยู่ใน **service** (`Helpers/ExpenseClaimActionPolicy` → `DenyClaimActionAsync`/`EnsureClaimActionAsync` ⇒ เว็บ +
  มือถือด่านเดียว) — ผู้ยื่นทำงานของตัวเองได้ (ดู/แก้/ส่ง/ถอน) · **อนุมัติ/ปฏิเสธ/จ่ายใบตัวเองไม่ได้แม้ถือคีย์** ยกเว้น role Owner ที่ไม่ได้เปิด
  `SodBlockSelfApproval` · คนอื่นต้องมีคีย์ (อนุมัติ `Expense.Approve`/`HR.Admin` · ปฏิเสธ +`Expense.Reject` · จ่าย `Expense.Pay` **และ** `CanApproveAsync(PaymentVoucher)`) ·
  PV จากการจ่ายอนุมัติในนาม**ผู้กดจ่าย** (ไม่ใช่ `"system:expense-claim"`) · มือถือเรียก `ApproveAsync/RejectAsync` ⇒ ได้ §65 ทวิ + **CertificateInLieu** เท่าเว็บ ·
  หลักฐาน (`ExpenseClaimEvidencePolicy.OwnerMayChange(status, isRemoval)`): Draft แนบ/ถอดได้ · Submitted เพิ่มได้ ถอดไม่ได้ · หลังอนุมัติผู้ยื่นแตะไม่ได้
  (`LockedMessage` ให้ขอผู้อนุมัติ) · SoD `ReviewerKeyApplies` · ส่ง/อนุมัติเว็บ/อนุมัติมือถือตรวจหลักฐานซ้ำ (`MissingEvidenceMessage`)
- **ถัง 50 ทวิ ก่อนบันทึก** (`WhtCredit` + id ว่าง): ดูทั้งถัง = 403 · ไฟล์ในถังเปิดได้เฉพาะผู้อัปโหลดหรือผู้ถือ `Tax.File` · ลบได้เฉพาะผู้อัปโหลด ·
  บันทึกรายการ ⇒ ผูกไฟล์ (`WhtCreditService.AdoptUnsavedAttachmentAsync` ตอน Create/Update/MarkReceived · ผู้อัปโหลดหรือผู้ถือ Tax.File เท่านั้น) ·
  ไฟล์ชนิดอื่น/ของรายการอื่น = `WHT-CREDIT-FOREIGN-FILE` · migration ย้ายไฟล์ในถังที่รายการชี้ถึงแล้ว (idempotent)
- **เพดานพื้นที่ = คำเตือน ไม่บล็อก** (คำตัดสิน #30): `ISubscriptionService.GetStorageStatusAsync` (Σ `FileAttachments.FileSize` + สื่อ CMS — สูตรเดียวกับ
  `CanFitStorageAsync`) · อัปโหลดบันทึกเสมอแล้วตอบ `data.storageWarning` (`Helpers/AttachmentStorageNotice`: Near ≥90%/Over พร้อม MB จริง · ไม่รู้เพดาน = ไม่เตือน) ·
  `API.upload` → `API.showStorageWarning` · CMS ยังบล็อก · ไฟล์ที่ soft-delete แต่เก็บไฟล์จริงไม่ถูกนับ (คำถามเจ้าของ)
- **ทางอนุมัติที่ไม่มีหน้าต่างยืนยันคำเตือน** (OCR อนุมัติอัตโนมัติ `[APPROVE-FAIL]` · ปุ่ม LINE) ได้ข้อความคำเตือนเต็มผ่าน
  `DocumentApprovalWarningsException.DescribeForUser` (เดิมได้แค่ "มีจุดที่ต้องตรวจ (1 รายการ)")
- **ด่าน §86/4 ของใบกำกับซื้อบนฟอร์ม = เซิร์ฟเวอร์ตัดสิน** — `GET /api/companies/{cid}/document/supplier-tax-invoice-check`
  (`DocumentService.CheckSupplierTaxInvoiceAsync`) เรียก `TaxInvoiceCompletenessChecker.Evaluate` **ตัวเดียวกับตัวลงบัญชี 11610/11640**
  กับผู้ติดต่อจากฐาน · คืน `IsClaimable` · `MissingFields` · `MissingContactFields` · `ContactTaxId` · `BranchCodeError` ⇒ กล่องแดง
  บนหน้าบอกตรงกับที่ JE จะลงจริง (เดิม JS ตรวจช่องบนฟอร์มคนละเวลา ⇒ "เลขผู้เสียภาษีขึ้นแล้วแต่บอกว่าขาด" และทิศกลับที่อันตรายกว่า:
  ที่อยู่ผู้ขายว่าง/เลข checksum ผิด ⇒ พัก 11640 เงียบ) · หน้าเรียกซ้ำทุกครั้งที่เลือกผู้ติดต่อ/ใบในระบบ/เปิดแก้/พิมพ์ (คำตอบล่าสุดชนะ)
- **คำเตือนก่อนอนุมัติ** — `Helpers/InputVatParkingNotice` ใน `CollectApprovalWarningsAsync`: ติ๊กขอเคลมภาษีซื้อแล้ว (`HasTaxInvoiceReference`)
  แต่ §86/4 ไม่ครบ ⇒ เตือนยอด VAT ที่จะพัก 11640 + สิ่งที่ขาด · จำกัดเฉพาะใบที่ประกาศเคลม เพื่อไม่ให้ทางอนุมัติอัตโนมัติ
  (ใบเบิก→PV · รายการประจำ) ล้มทั้งเส้น
- **ผู้ติดต่อหลายสาขา** — `ContactResponse.BranchLabel` (เซิร์ฟเวอร์คำนวณ) แสดงใน dropdown + ใต้ช่องผู้ติดต่อบนหน้าสร้างเอกสาร ·
  หน้าผู้ติดต่อวางรหัสสาขาติดเลขผู้เสียภาษี · ปุ่ม DBD ส่งรหัสสาขาไปค้นที่อยู่สาขา (`GetBranchAsync`)

### 6.2i คีย์ผู้ติดต่อ = เลขผู้เสียภาษี + สาขา ทุกทางเข้า (รอบ 193 · คำตัดสิน #20 · ทีม O2 → C3)

ตัวจับคู่ตัวเดียว **`Helpers/ContactTaxBranchKey`** (`Pick` pure · `FindAsync` · `PickContact`/`LoadByTaxIdsAsync` สำหรับนำเข้าเป็นชุด/change tracker ·
`AllBranchIdsAsync` = ทางที่**ประกาศว่าตั้งใจรวมทุกสาขา** เช่นประวัติ WHT/ค่าเฉลี่ยให้ AI · fuzzy-match ที่คืนทุกสาขาให้คนเลือก):
- payload **ระบุสาขา** → แถวสาขาตรง → ไม่มี: แถว**ไม่เคยระบุสาขา** (≡ 00000) อ้างได้**เฉพาะ payload สำนักงานใหญ่** → ไม่มีอีก: "ไม่พบ + `TaxIdExists`"
  ⇒ ผู้เรียกสร้างแถวของสาขานั้น (**ห้ามถอยไปจับด้วยชื่อ/อีเมล**) · payload **ไม่ระบุสาขา** = "ไม่รู้" → แถว สนญ./ไม่ระบุ → แถวเดียว → รหัสต่ำสุด (ไม่สร้างซ้ำ) ·
  รหัสผิดรูป ("8A") = ไม่รู้ · เลขที่ไม่มีตัวเลขเลย ("-"/"N/A") = **ไม่มีเลข** (`HasTaxId`) · แถวใหม่ไม่เก็บ "-" เป็นเลขภาษี
- **เขียนสาขา** ลงแถวที่ได้มาได้เฉพาะ `ContactKeyMatch.MayOverwriteBranch` (ตรงสาขา / ไม่ได้จับด้วยเลข / เลขไม่ใช่ 13 หลัก) — เดิม payload สาขา 00008
  ได้แถว สนญ. แล้ว `ProcessCustomerAsync`/`ImportContactAsync` เขียน 00008 + ที่อยู่ทับ
- **ถอยไปจับชื่อ/อีเมล/เบอร์** ได้เฉพาะในชุด `SoftScope(q, companyId, payloadTaxId, taxKey)` (null = ห้าม · เลขใหม่ = เฉพาะแถวที่ยังไม่มีเลข · ไม่มีเลข = ทุกแถว)
- **เติมเลขให้แถวที่จับได้** ด้วย `AdoptTaxId(row, taxId, branch, ContactMatchKind)` → `ContactAdoptOutcome {Keep, Adopted, Reject}` เท่านั้น
  (`MayWriteTaxId` เป็น private): ห้ามทับเลขนิติบุคคลอื่น/ล้างเลขจริงด้วย "-" · 13 หลักต้องผ่าน mod-11 (`ThaiTaxId.IsValid`) · ศูนย์ล้วนไม่รับ · แถว walk-in
  ไม่รับเลข (ผู้ซื้อมีเลขจริง ⇒ Reject) · **จับด้วยชื่อแบบ fuzzy/substring + payload มีเลขจริง ⇒ Reject** (ผู้เรียกสร้างแถวใหม่/ไม่ผูก) ·
  `NameMatchKind` = ExactName เมื่อ**ชื่อแกนเท่ากัน และรูปนิติบุคคลเท่ากัน** (ฝ่ายค้านรอบสี่ R4-4 · cb552889 — เดิมตัดคำบอกรูปทิ้ง ⇒ "บจก. เอ" = "บริษัท เอ จำกัด
  (มหาชน)") · `EntityFormOf` คลาส: บริษัทจำกัด (บจก./บจ./บริษัท…จำกัด/Co., Ltd./Company Limited/Ltd.) · มหาชน (บมจ./(มหาชน)/Public Company/PCL/PLC) · หจก.
  (ห้างหุ้นส่วนจำกัด/Limited Partnership) · หสน. — ตรวจตามลำดับ มหาชน → หจก. → หสน. → บริษัท · **คลาสต่างกัน / ฝั่งใดไม่รู้รูป (รวมบุคคลธรรมดาทั้งคู่ ·
  ป้ายร้าน) = Fuzzy** · ผู้เรียก: API v1 · นำเข้าเอกสาร (เส้นที่จับด้วย `==` ส่ง `ExactName` เอง) · superstring ไม่ใช่ exact
- **เลขที่ใช้ไม่ได้ได้ผลเดียวกันทุกทาง** (P4-5 · cb552889): `ContactTaxBranchKey.TaxIdChecksumWarning(taxId)` (13 หลักไม่ผ่าน mod-11 · ศูนย์ล้วน) +
  `StampTaxIdWarning(contact)` ต่อท้าย `InternalNotes` ด้วยป้าย `[TAXID-CHECKSUM]` (ไม่ติดซ้ำ · ไม่ทับ) — **แถวใหม่เก็บค่าที่คู่ค้าส่งไว้เป็นหลักฐานแต่ติดป้าย** ·
  แถวเดิมไม่ถูกเติมเลขนี้ (`AdoptTaxId` = Keep) · ผู้เรียก: Integration ลูกค้า (+ `InboundSyncResponse.Warnings`) / ใบขาย / ผู้จำหน่าย · API v1 `/documents`
  (`contact.taxIdWarning` + ป้ายบนแถวใหม่) · CMS lead · ยอดยกมา · ที่พัก (c3820dce) · นำเข้าผู้ติดต่อ / `ContactsV1` sync ปฏิเสธเลข checksum ผิดอยู่แล้ว ·
  แถวเก่าที่เก็บเลขผิดไม่ติดป้ายย้อนหลัง (ให้เจ้าของตัดสิน)
- ทางเข้าที่เดินตัวนี้: Integration ลูกค้า/ใบขาย/ผู้จำหน่าย · นำเข้าผู้ติดต่อ (คอลัมน์ BranchCode) / ยอดยกมา / เอกสาร (ชื่อตรงตัวก่อน แล้ว substring เรียงแน่นอน ·
  Reject ⇒ `KeyNotFoundException` ไทย "สร้าง/นำเข้าผู้ติดต่อก่อน") · พรีวิวนำเข้า + `CompetitorImportFramework` (เดิม `ToDictionary(TaxId)` **โยนทั้งไฟล์**เมื่อเลขเดียวมีสองสาขา) ·
  POS ใบกำกับเต็มรูป (`BuyerBranchCode`) · ที่พัก ("00000") · API v1 (`/documents` · `/contacts/resolve` · `/contacts/sync` — `ACCOUNT_STRUCTURE.md` §3.2) · CMS AutoLink
  (เลข + `SiteCustomer.BranchCode` ก่อนอีเมล · อีเมลว่างไม่จับ) · CMS lead · ข้ามบริษัท/ใบค่าบริการแพลตฟอร์ม (กุญแจแรก `ExternalSystem="NextAccTenant"` +
  `ExternalId = Id บริษัท` → เลข + `Company.BranchCode` → ชื่อบน SoftScope) · DuplicateDetector (คนละสาขา ≠ ซ้ำ) · ตัวแนะนำ AI
- checker `tools/contact_taxid_only_match_check.py` (กติกา 1 query ผู้ติดต่อด้วยเลขภาษีอย่างเดียว · กติกา 2 `#soft` จับชื่อ/อีเมลหลัง `FindAsync` นอก SoftScope) ·
  baseline 5 จุด (OCR ×2 · Payroll ×2 · POS `#soft` 1) + `required_call_site_check` ล็อกทุกผู้เรียก
- ⚠️ ข้อมูลที่ persist แล้ว: แถว สนญ. ที่เคยถูกเขียนทับเป็นสาขา / เลข "-" / แถวที่ถูกเติมเลขจาก fuzzy — แยกด้วยกลไกไม่ได้ ⇒ **ไม่ migrate** · รายงาน
  `GET /api/companies/{id}/contact-hygiene` + `pages/contact-hygiene.html` (อ่านอย่างเดียว · `Helpers/ContactDataHygiene`: ที่อยู่ขึ้นต้น `/เลข` · แถว สนญ.
  ที่ OCR สร้าง/แก้แล้วรหัสไปรษณีย์ไม่ตรงทะเบียน หรือมีใบสาขาอื่นพิมพ์ที่อยู่เดียวกัน · ตรวจทะเบียนทีละหน้าต่าง ≤50 · "ยังไม่ได้ตรวจ" ≠ "ไม่มีปัญหา") ·
  `InboundInvoiceRequest` ยังไม่มีช่องสาขาผู้ซื้อ (ส่ง null = สนญ.)
- **ทะเบียน VAT ของกรมสรรพากรแยกสาขาทั้งแถว** (คำตัดสิน #18): `Helpers/RdVatBranchRecords.PickForBranch` ตัวเลือกแถวตัวเดียวของ `DbdLookupService.LookupRdVatAsync`
  (สนญ.) · `GetBranchAsync` (สาขา N) · `ThaiGovIntegrationService.LookupBranchAsync` — `vBranchNumber` ตรง → ทั้งแถว · ถามสาขา N ไม่มีแถวตรง → null ·
  สนญ. + แถวเดียวไม่มีเลขสาขา → แถวนั้น (เดิม) · สนญ. ระบุแถวไม่ได้ → **ชื่ออย่างเดียว ที่อยู่ว่าง** (เดิมประกอบ "ค่าแรกที่ไม่ว่างทีละช่อง" ข้ามแถว) ·
  ⚠️ ยังไม่ได้ยืนยันกับคำตอบจริงของกรมสรรพากร (สังเคราะห์ตามรูป SOAP)

### 6.2j ผลต่างปัดเศษระดับเอกสาร (54960) — รอบ 193 · คำตัดสิน #8

- สัญญา `Helpers/DocumentRounding`: **`SubTotal = Σ Line.Amount + RoundingAdjustment`** · `TotalAmount = SubTotal + VAT − WHT` · `|RoundingAdjustment| < 1.00` ·
  ผัง **54960 ผลต่างจากการปัดเศษ** (`ChartOfAccountTemplates` + migration ใส่ทุกบริษัทที่มีผัง)
- สแกน: ราคาต่อหน่วยไม่ลงตัวกับยอดบรรทัด (Lazada 1,228.04 × 4) ⇒ บรรทัด = จำนวน × ราคาที่พิมพ์ (4,912.16 − ส่วนลด 216.82 = 4,695.34) · หัว `−0.01` · ฐาน 4,695.33 ·
  รวม 5,024.00 ตรงกระดาษ — **ห้ามทศนิยม 4 ตำแหน่ง · ห้ามบรรทัดติดลบ** (§6.2e) · `BuildScanLinesAsync` + repopulate สัญญาเดียว · ผลต่างรวม ≥ 1 บาท ⇒ ไม่ย้าย + `[Σ]` (`CapShifts`) ·
  บรรทัดที่กระดาษบอกไม่มี VAT ไม่ถูกย้าย (คอลัมน์ 7% ของรายงานถูก) · ฐาน WHT ระดับเอกสาร = Σ บรรทัด + ผลต่าง
- คีย์มือ/แก้ไข: `CreateDocumentAsync`/`UpdateDocumentAsync` (แก้เฉพาะผลต่างโดยไม่ส่งบรรทัดได้) · ฟอร์ม `fRoundingAdjustment` + `calcSum` · พรีวิวจากสแกนส่ง
  `OcrLinePreviewResponse.RoundingAdjustment/ActualPaidAmount` เข้าฟอร์มก่อน `calcSum`
- JE: §3.2 ขั้น 7 · พิมพ์: แถว `DocumentLabels.TotalRounding` ทั้งสอง renderer · e-Tax XML: `DocumentRounding.EtaxSummation` (LineTotal = Σ บรรทัด · ผลต่างเป็น
  `SpecifiedTradeAllowanceCharge` ระดับหัว · TaxBasis = SubTotal · ยังไม่ได้ตรวจกับ XSD/Schematron ETDA จริง)
- เอกสารลูก: `DocumentRounding.Inherit` — แปลง/โคลนที่ยก**ทุกบรรทัดครบจำนวน**พาผลต่างไปด้วย (บางส่วน = 0) · โคลนพาส่วนลดบาทรายบรรทัดไปด้วย (เดิมหาย)
- ⚠️ รายงานที่รวมจาก Σ `Line.Amount` จะต่าง 0.01 ต่อใบที่มีผลต่าง (`TaxService` ฐาน VAT ใช้ `SubTotal` — ถูกตามกระดาษ)

### 6.2f ผังบัญชีบนบรรทัด **ใบลดหนี้/ใบเพิ่มหนี้** — ต้องลงกลับผังเดิม (รอบ 185)

**ตัวตัดสินตัวเดียว: `Helpers/AdjustmentNoteAccount`**
(`SourceIsPurchaseSide` / `ResolveSide` / `SideViolation` / `StockValuationWarning`)

ใบลดหนี้ §86/10 คือ **การกลับรายการของธุรกรรมเดิม** ไม่ใช่ธุรกรรมใหม่ ⇒ ขา Cr
ต้องกลับไปที่ผังเดิมที่ใบซื้อ Dr ไว้ (ซื้อลงวัสดุสิ้นเปลือง = ลดวัสดุสิ้นเปลือง)

| | counter | บรรทัด | VAT | WHT |
| --- | --- | --- | --- | --- |
| CN ฝั่งขาย | Cr ลูกหนี้/เงินสด | **Dr รายได้** | Dr 21911 | Cr 11910 |
| DN ฝั่งขาย | Dr | **Cr รายได้** | Cr 21911 | Dr 11910 |
| CN ฝั่งซื้อ | Dr เจ้าหนี้/เงินสด | **Cr ค่าใช้จ่าย/สินค้าคงเหลือ** | Cr 11610 | Dr 2191x |
| DN ฝั่งซื้อ | Cr | **Dr ค่าใช้จ่าย/สินค้าคงเหลือ** | Dr 11610 | Cr 2191x |

**ด่านผังข้ามฝั่ง** (`RD-86/9-10-ACCT-SIDE`) — `EnsureLineAccountMatchesDocSide`
เดิม**ยกเว้น CN/DN ทั้งด่าน** (คอมเมนต์ที่ `PureSalesSideTypes` เขียนเองว่าจงใจ
เพราะเป็นเอกสารสองฝั่ง) ⇒ ฝั่งขายมีตาข่าย `RevenueLegAccountId` แต่**ฝั่งซื้อ
ไม่มีตาข่ายอะไรเลย** — ผังหมวดรายได้ (เช่น `43060 ส่วนลดรับ`) ถูก Cr เข้า 4xxxx
เงียบสนิท · ตอนนี้ด่านครอบ CN/DN แล้วโดยใช้ฝั่งที่ตัดสินได้:
- ฝั่งซื้อ **ห้าม** หมวด `Revenue` · ฝั่งขาย **ห้าม** หมวด `Expense`
- หมวด `Asset`/`Liability` ผ่านทั้งสองฝั่ง (มัดจำ · รับ-จ่ายล่วงหน้า · ภาษีซื้อ-ขาย)
- **ฝั่ง = `null` ("ยังไม่รู้") ⇒ ปล่อยผ่าน** โดยเจตนา — ใบที่ไม่ได้อ้างใบต้นทาง
  และไม่เคยส่งฝั่งมา (CSV · integration · API v1 · ใบก่อนมีช่องนี้) ต้องไม่ถูก
  ล้มด้วยด่านที่ระบบเองยังตอบไม่ได้ว่าอยู่ฝั่งไหน

**ฝั่งของใบตัดสินที่ `ResolveSide(sourceType, userOverride)`** — ใบต้นทางชนะ
เสมอ (ใบลดหนี้ของใบซื้อไม่มีทางเป็นฝั่งขาย) แล้วค่อยถึงตัวเลือกของผู้ใช้ ·
ชุด "ชนิดใบต้นทางฝั่งซื้อ" = `PurchaseSourceTypes` (PI · Expense · PV · CIL)
ซึ่งเคยพิมพ์มือซ้ำ 6 ที่

**ด่านบทบาทคู่ค้าตอนสร้าง** (`CreateDocumentAsync`) เดิมอ่านแต่
`CnDnPurchaseSideOverride` ⇒ การแปลง **ใบแจ้งหนี้ซื้อ → ใบลดหนี้** (เส้นเดียวที่
คัดลอกผังบัญชีรายบรรทัดมาให้ครบ) ถูกปฏิเสธทุกครั้งด้วยข้อความ "ไม่ได้ตั้งค่าเป็น
ลูกค้า" เพราะ `ConvertCoreAsync` ส่ง `RelatedDocumentId` แต่ไม่ส่ง override ·
ตอนนี้ด่านอ่านใบต้นทางด้วยผ่าน `ResolveSide` ⇒ เส้น convert ใช้ได้จริง

**คำเตือนตอนอนุมัติ** (`TFRS-NPAE-8-STOCKVAL`, soft — กดยืนยันผ่านได้):
ใบลดหนี้ฝั่งซื้อเหตุผล "รับคืนสินค้า" ที่บรรทัดผูกผังซึ่งไม่ใช่บัญชีคุมสต็อกของ
สินค้านั้น ⇒ จำนวนลดแต่มูลค่าใน GL ไม่ลด · ไม่บล็อกเพราะมีเคสที่ตั้งใจจริง
(ของขายออกไปแล้ว ส่วนลดเข้า COGS) ซึ่งระบบแยกจากข้อมูลบนใบไม่ได้

**บัญชีคู่ (counter) เคารพผังเฉพาะรายคู่ค้าแล้ว** — `FindAccountAsync` ของบล็อก
CN/DN เดิมไม่ส่ง `doc.Contact` ต่างจากทุกเส้นอื่นในไฟล์ ⇒ คู่ค้าที่ปัก
`DefaultApAccountId`/`DefaultArAccountId` ไว้ ถูกใบปรับปรุงลงผังกลางแทน ⇒ ยอด
คงเหลือรายคู่ค้าไม่มีวันเป็นศูนย์

**ผังที่ต้องมีในผังบัญชีกลาง** (เพิ่มรอบ 185 · migration ใส่ให้บริษัทเดิมแบบ
เพิ่มอย่างเดียว ไม่ย้ายยอด):
- `51150 ส่วนลดรับ (สินค้า)` · `51160 ส่งคืนสินค้า` — contra-purchase · **เดิมอยู่
  เฉพาะเทมเพลตธุรกิจซื้อมาขายไป** ⇒ บริษัทบริการ/ผลิต/ทั่วไปไม่มีผังให้ใบลดหนี้
  ฝั่งซื้อลงเลย จึงถูกบีบไปใช้ `43060` ซึ่งอยู่หมวด**รายได้**
- `11520 วัสดุสิ้นเปลืองคงเหลือ` — บัญชีคุมสต็อกของ `ProductType.Supplies` ·
  `InventoryControlAccount.DefaultAccountPrefix(Supplies)` เดิมคืน `"118"` แต่ผัง
  มาตรฐานไทย `118 = เงินมัดจำจ่ายล่วงหน้า` และไม่มีผังวัสดุสิ้นเปลืองหมวดสินทรัพย์เลย
  ⇒ ค้นเจอ **11810 เงินมัดจำ** ⇒ ซื้อวัสดุสิ้นเปลืองที่ตัดสต็อก Dr เข้าบัญชีเงินมัดจำ ·
  ตอนนี้ชี้ `11520` และ tenant ที่ยังไม่มีผังนี้ตกไปใช้บัญชีสินค้าคงเหลือแทน
  (ทั้งขาซื้อและขาเบิกใช้ถามตัวเดียวกัน จึงหักล้างกันได้เสมอ)
- `43060` เปลี่ยน**ชื่อ**เป็น "ส่วนลดรับ (ส่วนลดเงินสด)" · **`AccountType` ไม่เปลี่ยน**
  เพราะการย้ายหมวดจะ re-sign งบของงวดที่ปิด/ยื่นไปแล้วทั้งประวัติ

**ช่อง 7/8 ของ ภ.พ.30 (ยอดขาย 0% §80/1 · ยอดยกเว้น §81)** — แก้ 2 อย่างรอบ 185:
1. ฝั่งของ CN/DN เคยตัดสินด้วย `doc.CnDnPurchaseSideOverride ?? DocumentSide.IsPurchase(type)`
   ซึ่ง **ไม่ส่ง `ourRole`** ⇒ `IsPurchase(CreditNote)` = **true เสมอ** ⇒ ใบลดหนี้
   ฝั่งขายที่ยังไม่มี override ถูกนับยอดยกเว้นเข้าช่องฝั่ง**ซื้อ** · ตอนนี้ใช้
   `AdjustmentNoteAccount.ResolvePostedSide` ตัวเดียวกับรายงานภาษีซื้อ/ขาย
   (สั่งย้ายฝั่ง → ใบต้นทาง → GL → คู่ค้าเป็นผู้ขายอย่างเดียว → **ไม่รู้**)
2. **ใบลดหนี้เคยถูก "บวก" เข้าช่อง** เหมือนใบขายปกติ ⇒ ลดหนี้ยอดยกเว้น/0% ทำให้
   ช่องสูงกว่าความจริงเป็น **สองเท่าของยอดที่ลด** · ตอนนี้ใบลดหนี้ใช้เครื่องหมายลบ
   · ฝั่งที่ยัง**ไม่รู้ ไม่ถูกนับเข้าช่องใดเลย** (G3) และมีบรรทัดเตือน
   "⚠️ แยกฝั่งไม่ได้" ในรายงานอยู่แล้ว

**แถบเตือนบนฟอร์มสำหรับใบที่บันทึกไว้ก่อนมีด่าน** — `openEdit` สแกนแถวที่ hydrate
แล้วด้วยกติกาเดียวกัน ขึ้นแถบแดงบอกจำนวนบรรทัดและผังที่ผิดฝั่ง · **ระบบไม่แก้ให้เอง**
(ใบที่ post แล้วต้องผ่าน "เปลี่ยนผังบัญชี" ซึ่งลง JE ปรับปรุงในงวดเดิม — การแก้
คอลัมน์เฉย ๆ จะทำให้เอกสารกับ GL แยกทางถาวร)

**ฝั่งฟอร์ม** (`documents.html`) — ผู้ใช้ไม่ต้องเลือกผังเอง:
- เลือกใบต้นทาง ⇒ **ตั้งฝั่งก่อนสร้างแถว** แล้ว `_hydrateLineRow` (ตัวเดียวกับ
  ตอนแก้ไขเอกสาร) สืบทอด **ผังบัญชี · สินค้า · ธงเคลมภาษีซื้อ · ส่วนลด** จากบรรทัด
  ใบเดิม (เดิมคัดลอกแค่ 6 ช่อง และสร้างแถวก่อนตั้งฝั่ง ⇒ ทุกแถวค้างลิสต์ฝั่งขาย
  และ radio ถูกล็อกทันที ผู้ใช้แก้เองไม่ได้)
- `_syncLineAccountPickers()` ตั้ง datalist + ป้าย + placeholder + ชิป ตามฝั่ง ·
  ถูกเรียกจากทั้ง `_syncVatClaimColumn()` และ `onCnSideChange()`
- ชิปข้างช่องขึ้น **⚠ ผังคนละฝั่ง** (แดง) เมื่อหมวดผังขัดกับฝั่งเอกสาร แทนที่จะ
  โชว์ชื่อผังสีฟ้าเหมือนว่าระบบยืนยันแล้ว

### 6.2c ชื่อทางการค้า (แบรนด์) บนหัวเอกสาร

`DocumentBrand` (ต่อบริษัท มีได้หลายแบรนด์) + `Document.BrandId` (null = ใช้ชื่อบริษัท)
— ผลต่อ **หน้าตา** เท่านั้น ไม่แตะบัญชี/ภาษี/เลขที่เอกสาร

| ชนิดเอกสาร | ชื่อหลักบนหัว | แบรนด์ได้อะไร |
| --- | --- | --- |
| ใบเสนอราคา · ใบแจ้งหนี้ · ใบวางบิล · ใบส่งของ · ใบขอซื้อ · ใบสั่งซื้อ | **ชื่อทางการค้า** | ชื่อ + สโลแกน + โลโก้ + สี + ที่อยู่ + เว็บไซต์ |
| ใบกำกับภาษี (รวมอย่างย่อ/ใบรวม) · ใบเพิ่มหนี้ · ใบลดหนี้ · ใบเสร็จรับเงิน · ใบสำคัญรับ/จ่าย · ฝั่งซื้อทั้งหมด | **ชื่อนิติบุคคล** (§86/4(2)) | โลโก้ + สี + ที่อยู่ + ชื่อแบรนด์เป็นบรรทัดรอง |

- **ไม่ได้ตั้งแบรนด์ = ทุกอย่างเหมือนเดิมทุกประการ** — ฟอร์มไม่มีช่องให้เห็น,
  `Resolve` คืนค่าชุดเดิม (ชื่อ/โลโก้/สี/ที่อยู่ของบริษัท), ไม่มีบรรทัด
  "ดำเนินการโดย …" โผล่เพิ่ม (ล็อกด้วยเทสต์ 8 ชนิดเอกสาร)
- **ตั้งแบรนด์แล้ว ค่าเริ่มต้นตอนสร้างเอกสารยังเป็น "ใช้ชื่อบริษัท" เสมอ** —
  ตั้งใจไม่มี "แบรนด์ตั้งต้น" ที่เลือกให้อัตโนมัติ: ตั้งครั้งเดียวแล้วทุกใบ
  เปลี่ยนตามเงียบ ๆ = ใบที่ควรเป็นชื่อบริษัทหลุดเป็นชื่อร้านโดยไม่มีใครสั่ง
  และรู้ตัวตอนเอกสารถึงมือลูกค้าแล้ว. ผู้ใช้เลือกเป็นราย ๆ ไปบนฟอร์ม
- ด่าน + resolver = `Helpers/DocumentIssuerIdentity.cs` **ตัวเดียว**:
  `CanBrandBePrimary(type, renderedTitle)` + `Resolve(...)` → `IssuerIdentity`
- ตัดสินจาก **หัวเอกสารที่ render จริง** ด้วย ไม่ใช่ enum อย่างเดียว — ใบชนิด
  `Invoice` ที่หัวเป็น "ใบแจ้งหนี้/ใบกำกับภาษี" ถูกบังคับกลับไปใช้ชื่อนิติบุคคล
- **บรรทัดนิติบุคคลตัวเล็กปิดไม่ได้** เมื่อแบรนด์ขึ้นหัว (`LegalNamePlacement`
  = Header / Footer / Both — ค่านอกลิสต์ตกเป็น Footer) รูปแบบ:
  `ดำเนินการโดย {ชื่อ} · เลขประจำตัวผู้เสียภาษี {13 หลัก} · {สาขา}`
- renderer ทั้งสองตัวเรียก `PdfGenerationService.BuildIssuer(...)` จุดเดียว
  (ลบการคำนวณ `coPrimaryName` ที่เคยซ้ำอยู่คนละไฟล์) · โลโก้/สีแบรนด์เข้า
  `BuildBranding(..., brand)` ให้ QuestPDF เห็นตรงกับ HTML
- **ที่อยู่บนเอกสารภาษี = ที่อยู่สถานประกอบการที่ออกใบเสมอ** (§86/4(2) + ป.86/2542)
  — ที่อยู่หน้าร้านของแบรนด์ทับได้เฉพาะใบที่แบรนด์ขึ้นหัว. "สถานประกอบการที่ออกใบ"
  = สาขาบนเอกสาร (§6.2d) ถ้าไม่มีก็ที่อยู่บริษัท
- **ที่อยู่ของแบรนด์ "ผูก" กับทะเบียนได้** — `DocumentBrand.AddressSource`
  (`Helpers/BrandAddressSource.cs` = resolver กลาง):
  `Company` = ที่อยู่จดทะเบียนของบริษัท · `Branch` = ที่อยู่ของสาขาที่เลือก
  (`AddressSourceBranchId`) · `Custom` = พิมพ์เอง (ค่าของแถวเก่า = พฤติกรรมเดิม)
  · คืน null เมื่อไหร่ = ตกไปใช้ที่อยู่บริษัท (สาขายังไม่กรอกที่อยู่/ถูกลบ ก็ตกมาที่นี่)
  _(ที่มา: เดิมที่อยู่แบรนด์เป็นช่องพิมพ์เองล้วน ⇒ ย้ายออฟฟิศทีต้องไล่แก้ทุกแบรนด์
  และตัวที่ลืมแก้จะพิมพ์ที่อยู่เก่าออกไปหาลูกค้าเงียบ ๆ)_
- **พรีวิวในหน้าตั้งค่าต้องเดินลำดับเดียวกัน** — เอกสารภาษีโชว์ที่อยู่จดทะเบียน
  (ไม่ใช่ที่อยู่แบรนด์) · แบรนด์ที่ไม่มีโลโก้โชว์โลโก้บริษัท · ที่อยู่ที่ resolve แล้ว
  มาจาก server (`BrandResponse.EffectiveAddress` + `policy.companyAddress` ที่ประกอบ
  ด้วย `ThaiAddressFormatter` ตัวเดียวกับ renderer) — หน้าเว็บห้ามต่อสตริงที่อยู่เอง
- **ชื่อแบรนด์เป็นบรรทัดรองบนเอกสารภาษี พิมพ์เสมอ** (`SecondaryIsBrand`) ห้าม
  gate ด้วย `template.ShowCompanyNameEn` (default=false ⇒ สายเอกสารข้ามชื่อกัน)
- **`IsActive` ไม่ตัดแบรนด์ตอน render** — ใบเก่าที่ตรึง `BrandId` ไว้ต้องพิมพ์
  หน้าตาเดิมแม้แบรนด์ถูกปิดใช้งาน (สัญญาของปุ่มลบ) · `IsActive` คุมแค่รายการ
  ให้เลือกตอนออกใบใหม่
- **normalize หัวเอกสารก่อนเทียบ marker** — `CustomTitle` ที่ผู้ใช้พิมพ์เอง
  ("ใบกำกับ ภาษี" เว้นวรรค · `TAX-INVOICE` · สระอำแบบแยก) ต้องยังโดนด่านจับ
- **`BrandId`/`DocumentTemplateId` ต้องเป็นของบริษัทนั้น** —
  `ResolveOwnedBrandIdAsync` / `ResolveOwnedTemplateIdAsync` throw เมื่อข้าม tenant
  (invariant M) · `Guid.Empty` = "ไม่เลือก" normalize ทั้ง create และ update
- **pinned template ต้องตรงชนิดเอกสาร** — เทมเพลตใบเสนอราคาที่สืบทอดมากับใบที่
  convert เป็นใบแจ้งหนี้จะพา `CustomTitle`/flag ผิดชนิดมาทั้งใบ
- **เอกสารภาษีบังคับ `ShowCompanyName`/`TaxId`/`Address`** —
  `EnforceTaxDocTemplateInvariants` (เทมเพลตปิด flag เหล่านี้ไม่ได้ §86/4(2)-(3))
- เอกสารลูก (convert/clone/settlement receipt/CN มัดจำ/ใบวางบิล) สืบทอด
  `BrandId` + `DocumentTemplateId` จากต้นทาง (ใบวางบิลสืบทอดเมื่อทุกใบใช้แบรนด์
  เดียวกันเท่านั้น — ต่างกันเลือกแทนผู้ใช้ไม่ได้)
- **รูปแบบเอกสาร (เทมเพลต) เลือกได้รายใบ** — `Document.DocumentTemplateId`
  ลำดับการเลือกอยู่ที่ `PdfGenerationService.ResolveDocumentTemplateAsync`
  **ตัวเดียว** (เดิม if/else ชุดนี้ถูกก๊อปไว้ 2 ที่):
  คำขอ (พรีวิวชั่วคราว) → ที่เลือกไว้ตอนออกใบ → `Brand.DefaultTemplateId` →
  ตั้งต้นของชนิดเอกสาร → เทมเพลตในหน่วยความจำ
  · ข้อ 2-3 ที่ชี้ไปเทมเพลตที่ถูกลบ/ข้ามบริษัท **ตกลงข้อถัดไปเงียบ ๆ** (เอกสาร
  เก่าต้องพิมพ์ได้เสมอ) ต่างจากข้อ 1 ที่ผู้ใช้เพิ่งเลือกเอง → id ผิด = error จริง
  · เก็บกับใบเพราะพิมพ์ซ้ำปีหน้าต้องได้หน้าตาเดิม แม้ตั้งต้นจะเปลี่ยนไปแล้ว
- API: `/api/companies/{id}/document-brands` (CRUD + `/policy` + `/{id}/preview`
  + `POST /{id}/logo` อัปโหลดโลโก้แบรนด์ผ่าน pipeline เดียวกับโลโก้บริษัท)
  · หน้าตั้งค่า `/pages/document-brands.html`
- เทสต์: `Accounting.Tests/DocumentIssuerIdentityTests.cs`

### 6.2d สาขาผู้ออกเอกสาร (§86/4(2) · ป.86/2542 · ประกาศอธิบดีฯ 199)

`Document.BranchId` (null = กิจการสาขาเดียว) + `Document.IssuerBranchCode`
(snapshot 5 หลักที่ตรึงตอนอนุมัติ) — ต่างจาก `SupplierBranchCode` ที่เป็นสาขาของ
**คู่ค้า** บนใบที่เขาออกให้เรา ตัวนี้คือสาขาของ **เรา** ในฐานะผู้ออกใบ

**ข้อบังคับข้อแรก: ไม่มีแถวใน `Branches` ⇒ ทุกอย่างเหมือนเดิมทุกประการ**
(ใช้ `Company.BranchCode` / ที่อยู่บริษัท) — ฟอร์มไม่มีช่องให้เห็นด้วยซ้ำ
(ช่องสาขาโผล่เมื่อมีสาขาที่ใช้งานอยู่ **≥ 2 แห่ง** เท่านั้น)
และค่าเริ่มต้นตอนสร้างใบใหม่คือ "ตามค่าของบริษัท" เสมอ — **ห้ามเดาสาขาให้**
(เหตุผลเดียวกับที่ไม่มี "แบรนด์ตั้งต้น" ในข้อ 6.2c)

**resolver กลาง `Helpers/DocumentIssuerBranch.cs` — ตัวตัดสินเดียว**

| ลำดับ | แหล่งของ "รหัสสาขา" | ใช้เมื่อ |
| --- | --- | --- |
| 1 | `Document.IssuerBranchCode` (snapshot) | อนุมัติแล้ว — **ชนะทุกอย่างเสมอ** |
| 2 | `Branch.TaxBranchCode` ของสาขาที่ใบผูกอยู่ | ยังเป็น Draft |
| 3 | `Company.BranchCode` | กิจการสาขาเดียว (พฤติกรรมเดิม) |

- **ตรึง snapshot พร้อมเลขที่เอกสารตอน Approve** — แก้ทะเบียนสาขาวันนี้ต้องไม่
  ย้อนไปเปลี่ยนใบกำกับที่ออกไปแล้ว (พิมพ์ซ้ำปีหน้าต้องได้เลขสาขาเดิมเป๊ะ)
- **snapshot ไม่ตรงทะเบียนปัจจุบัน → ตัดชื่อสาขาออก เหลือแต่รหัส** — ชื่อในทะเบียน
  อาจย้ายไปเป็นของรหัสอื่นแล้ว พิมพ์ต่อท้ายจะกลายเป็นอ้างสถานประกอบการผิด
- **ที่อยู่ "ทั้งชุดหรือไม่ใช้เลย"** (`UseBranchAddress`) — สาขาที่ยังไม่กรอกที่อยู่
  ใช้ที่อยู่บริษัททั้งชุด; ห้ามผสม (ตำบลของสาขา + จังหวัดของสำนักงานใหญ่ =
  ที่อยู่ที่ไม่มีอยู่จริง)
- **ชื่อบนหัวยังเป็นชื่อนิติบุคคลเสมอ** — สาขาไม่ใช่นิติบุคคลแยก §86/4(2)
  เปลี่ยนแค่ "รหัสสาขา + ที่อยู่ + เบอร์/อีเมล"
- ป้ายบนกระดาษผ่าน `Helpers/TaxBranchCode.LabelWithName` (ดูตาราง resolver กลาง
  ในหัวข้อ Quick reference) → `IssuerIdentity.BranchLabel` — **renderer ทั้งสองตัว
  ต้องอ่านจากตรงนี้ ห้ามคำนวณจาก `company.BranchCode` เอง** (เดิม HTML renderer
  และ QuestPDF ต่างคำนวณเอง = ใบที่ออกจากสาขาย่อยพิมพ์รหัสสำนักงานใหญ่ผิด)
- **e-Tax**: `TXID = เลขภาษี 13 หลัก + รหัสสาขา 5 หลัก` และ `SellerTradeParty`
  (ที่อยู่/เบอร์/อีเมล) ใช้ของสาขาเดียวกับที่พิมพ์บนกระดาษ · `EtaxInvoice.SellerBranch`
  / `SellerAddress` snapshot ไว้ตอน generate XML แล้ว PDF/A-3 อ่านต่อจากนั้น
  (PDF กับ XML ของใบเดียวกันต้องไม่มีทางไม่ตรงกัน)
- **เอกสารลูกสืบทอด `BranchId`** ครบทุกทางเข้าเช่นเดียวกับ `BrandId`:
  convert (`ConvertCoreAsync`) · clone (`DocumentCloneController`) ·
  ใบเสร็จ settlement อัตโนมัติ · CN คืนมัดจำ · recurring (`branchId` ใน
  `TemplateData` — template เก่าไม่มี key = null = ตามค่าบริษัทเหมือนเดิม)
  _(ใบแจ้งหนี้ออกจากสาขาเชียงใหม่แล้วใบกำกับกลับเป็นสำนักงานใหญ่ = รายงานภาษีขาย
  เข้าผิดสถานประกอบการ §87)_
- `BranchId` ต้องเป็นสาขาของบริษัทนั้น (`ResolveOwnedBranchIdAsync` throw เมื่อ
  ข้าม tenant — invariant M) · `Guid.Empty` = "ปลดกลับไปใช้ค่าบริษัท"
  (ไม่งั้นผู้ใช้ถอดสาขาออกไม่ได้เลย — defect class silent no-op)
- **สาขาที่ปิดใช้งานยังผูกได้ตอนแก้ใบเก่า** — ปิดสาขาไม่ควรทำให้เอกสารที่ออกไปแล้ว
  แก้ไม่ได้ (รายการให้เลือกตอนออกใบใหม่กรองให้อยู่แล้ว)
- ยังไม่อยู่ในเฟสนี้: **เลขที่เอกสารแยกชุดต่อสาขา** (`NumberSeries` มี unique
  constraint ระดับฐาน) · JE stamp `BranchId` · รายงาน §87 / ภ.พ.30 แยกสาขา →
  เฟส 2 (ดู `ACCOUNT_STRUCTURE.md` §3.1a)
- เทสต์: `Accounting.Tests/DocumentIssuerBranchTests.cs` (เคสแรกคือ "ไม่มีสาขาเลย
  ต้องได้ค่าบริษัทเป๊ะ") + `TaxBranchCodeTests.cs`

### 6.3 §87/3 retention (5 ปี)
- ทุกเอกสารตั้ง `RetentionUntil = MAX(filingDate, reportDate, DocumentDate) + 5y`
- nightly job ห้ามลบจริง (soft-delete + flag `legal_hold`)
- e-Tax ที่ submitted แล้วยืดเป็น **7 ปี** (extended retention)
- **ไฟล์สแกน (OcrScan) — รอบ 193 (S2)**: ตัดสินการลบด้วย `Helpers/OcrScanFileDisposal` ตัวเดียว (`DeleteScanAsync` · มี `CompanyId` ในคิวรีไฟล์ · `catch {}` → LogWarning):
  ไฟล์ของรายการอื่น/ไฟล์ที่สแกนอื่นยังใช้ → ไม่แตะ · เอกสารที่ cascade → ตาม `AttachmentRetention.MustKeepPhysicalFile` (ร่าง = ลบได้ · ไม่ใช่ร่าง = เก็บไฟล์) ·
  **ลบสแกนที่ยังไม่ผูก = ลบไฟล์จริง** (`AttachmentRetention.ScanFilePurgeable`) · relink พลาดแต่เอกสารที่ cascade ต้องเก็บ ⇒ ผูกไฟล์เป็นของเอกสารนั้นแล้วเก็บ ·
  งานกลางคืน `OcrSelfCorrectionService.RunMaintenanceAsync`: **ข้ามสแกนที่ลง JE** และไฟล์ที่สแกนพี่น้องผูกรายการไว้ (ตัดที่ query ไม่ใช่ในหน่วยความจำ · เรียง
  วันที่ + id) · กวาดไฟล์ OcrScan ที่ถูกถอดแล้วไม่มีสแกนแถวใดชี้เมื่อครบ **30 วัน** (`DeletedScanFileSweepable` · ครอบข้อมูลเก่าด้วย) · ลบไฟล์จริงไม่สำเร็จ ⇒
  คงแถว + ประทับ `UpdatedAt` ใหม่ (watermark ต่อแถว ไม่ค้างหัวคิว `Take(500)`) · ⚠️ ยังไม่มี `JobLock` ข้าม instance ของงานนี้ (backlog)

### 6.4 AI distillation (กฎเหล็ก #1) ที่ฝังใน flow

**Feature enum**: `AiFeatureKey` (`Models/Enums/AllEnums.cs:1497`) —
**ตารางนี้ verified ตรงกับ enum จริงในโค้ด** — แถวที่ขีดฆ่าคือค่าที่ enum ยังมี
แต่**ไม่มีใครเรียกเลย** (E-AI-10) เดิมตารางนี้เขียนว่ามี student + round-trip
feedback ครบ ซึ่งไม่จริงเลยสักตัว — โค้ดเป็น ground truth จึงแก้ doc

| จุดเรียก AI | Feature key (enum) | Local model class | Round-trip feedback |
| --- | --- | --- | --- |
| OCR full review | `OcrFullReview = 22` | `GenericFeedbackDistillationModel` (register ใน Program.cs) | `SubmitCorrectionAsync` (OcrService) |
| ผังบัญชี GL ต่อบรรทัด | `GlAccountSuggestion = 2` | `GlAccountDistillationModel.cs` (4-tier: vendor+keyword exact → fuzzy → company-keyword ×0.85 → industry-keyword ×0.55) | `RecordLineAccountFeedbackAsync` ตอน approve |
| OCR document type label | `DocumentTypeClassification = 3` | generic | ตอน user แก้ในหน้า scan |
| OCR เราเป็นผู้ซื้อ/ผู้ขาย (ถามเฉพาะเมื่อ `OcrPartyResolver.ShouldAskAi`) | `DocumentRoleInference = 4` | generic (`Buyer`/`Seller`) | ตอน user แก้ `OurRole` ในหน้า scan (`OurRoleAiFeedbackId`) — รอบ 156 |
| OCR target doc to create | `DocumentConversionSuggestion = 23` | generic | ตอน user เปลี่ยน targetDocType |
| Vendor canonical match | `VendorCanonicalization = 1` | `VendorCanonDistillationModel.cs` | ตอน user เลือก contact |
| WHT category infer | `WhtCategoryInference = 5` | generic | ตอน user แก้ · **และตอนอนุมัติเอกสาร** (`RecordWhtDecisionFeedbackAsync` — อนุมัติโดยไม่หัก = คำตอบ `None` · หักและระบุประเภทเงินได้ = รหัสนั้น) รอบ 176 |
| **เข้าข่ายหัก ณ ที่จ่ายไหม (ตอนอนุมัติ)** — ถามเฉพาะเมื่อ `WhtApplicabilityEvidence.Judge` = `Unknown` | `WhtCategoryInference = 5` (**คลังเดียวกัน ห้ามตั้ง key ใหม่**) | generic | `WhtAdviceAiFeedbackId` บนเอกสาร → ปิดตอนอนุมัติ |
| Line item structured parse | `LineItemStructuredParse = 6` | – (ไม่มี student — heavy AI) | – |
| Approval warning fix | `ApprovalWarningFixSuggestion = 7` | `ApprovalWarningDistillationModel.cs` | – |
| Bank statement match | `BankStatementMatch = 8` | `BankMatchDistillationModel.cs` | ตอน user reconcile |
| Credit note reason | `CreditNoteReasonClassification = 9` | generic | ตอน user เลือก radio |
| Fuzzy duplicate doc | `FuzzyDuplicateDetection = 10` | `DuplicateDocumentDistillationModel.cs` | – |
| Anomaly explanation | `AnomalyExplanation = 11` | `AnomalyExplanationDistillationModel.cs` | – |
| Forecast narrative | `ForecastNarrative = 12` | – (essay) | – |
| ~~Product match~~ | ~~`ProductMatch = 13`~~ | **ตายแล้ว `[Obsolete(error)]`** | **ไม่มี call site เลยทั้งเรพ** — การจับคู่สินค้าเดินผ่าน `Ocr.ProductMatcher` (heuristic cascade ไม่ผ่าน AI) |
| ~~Contact match~~ | ~~`ContactMatch = 14`~~ | **ตายแล้ว `[Obsolete(error)]`** | ซ้ำกับ `ContactFuzzyMatch` ที่ใช้งานจริง |
| ~~Payment method suggest~~ | ~~`PaymentMethodSuggestion = 15`~~ | **ตายแล้ว `[Obsolete(error)]`** | ซ้ำกับ `PaymentChannelSuggestion` ที่ใช้งานจริง |
| ~~Currency + FX suggest~~ | ~~`CurrencyAndFxSuggestion = 16`~~ | **ตายแล้ว `[Obsolete(error)]`** | ซ้ำกับ `FxRateSuggestion` ที่ใช้งานจริง |
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
| OCR project match | `OcrProjectMatch = 29` | generic | `SetExtractedLineProjectAsync` / `SetAllExtractedLineProjectsAsync` (ตอน user override project ราย line — ปิดลูปด้วย `ProjectAiFeedbackId` ฝังใน line) |
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

### 6.5 โมดูลที่พัก (Lodging — โรงแรม/รีสอร์ท/บ้านพัก) ✅ รอบ 124 · ปลายทาง ✅ รอบ 126

> **รอบ 126 — เส้นที่แขกสัมผัสจริง** (`LODGING_BOOKING_AUDIT.md`):
> | สิ่งที่เพิ่ม | ไฟล์ | หมายเหตุ |
> | --- | --- | --- |
> | **จ่ายออนไลน์ได้จริง** | `PublicPaymentController` (`[AllowAnonymous]`) + `PublicPaymentResolver` | เดิม `PaymentGatewayController` เป็น `[Authorize]` และไม่มีทางเข้าอื่น ⇒ **ลูกค้าปลายทางจ่ายไม่ได้ทั้งระบบ** · ตัวตนพิสูจน์ด้วย `PublicToken`/`orderId` · **ไม่รับ `SourceId` และไม่รับ `Amount`** (ยอดมาจาก `LodgingAmounts.OnlinePayableAmount`) · เพดาน 20 intent/ชม./source |
> | **ปฏิเสธสลิปได้** | `LodgingService.RejectSlipAsync` · `POST reservations/{id}/reject-slip` | เหตุผล**บังคับ** → ล้างสลิปให้ส่งใหม่ · ต่อ hold 24 ชม. · แจ้งแขกทางอีเมล · `SlipUploadBlocked` ปิดรับสลิปเมื่อพบของปลอม · **ไม่ลบไฟล์เดิม** (หลักฐาน) |
> | **หลักฐานการจองให้โหลด** | `GET reservations/{token}/voucher.pdf` + `Helpers/LodgingVoucherBuilder` | **ไม่ใช่เอกสารภาษี** — มีเทสต์ล็อกว่าคำว่า "ใบกำกับภาษี"/"ใบเสร็จรับเงิน" ต้องไม่โผล่ · ใบเสร็จมัดจำ/ใบกำกับเช็คเอาต์ยังเป็นคนละใบผ่าน `IDocumentService` ตามเดิม |
> | **ค่าเช็คอินก่อนเวลา / เช็คเอาต์ช้า** | `CheckInAsync` / `CheckOutAsync` → `AddChargeCoreAsync` | `EarlyCheckInFee`/`LateCheckOutFee` มีคอลัมน์มาตั้งแต่รอบ 124 แต่**ไม่มีใครอ่าน** · **พนักงานติ๊กเอง** ไม่ใช่ระบบเก็บอัตโนมัติ |
> | **สลิปเป็น PII** | `Helpers/LodgingSlipPath` + `reservations/{id\|token}/slip` | ถอด `/uploads/lodging-slips` ออกจาก `publicUploadPrefixes` (เดิมใครได้ URL ก็เปิดได้ตลอดกาล) · ด่านสองชั้น: สิทธิ์ + พาธ (กัน traversal จากค่าที่ค้างใน DB) · `PaymentSlipUrl` ใน API คืน **endpoint ที่มีด่าน** ไม่ใช่ storage path · หน้าเว็บเปิดผ่าน `Layout.openAuthed` (`<a href>` เปล่าไม่ส่ง Bearer ⇒ 401) |
> | **LINE แจ้งเจ้าของ** | `ILineNotifyService.NotifyLodgingBookingAsync` ← `TryNotifyLineAsync` | จองใหม่ · แขกส่งสลิป → เข้ากลุ่ม LINE เดิมของบริษัท (ไม่ต้องตั้งค่าเพิ่ม) · ใช้ธง `NotifyOwnerOnBooking` ร่วมกับอีเมล · **ฝั่งแขกส่ง LINE ไม่ได้** (จองแบบไม่ล็อกอิน ไม่มี LINE user id) |
>
> **สูตรยอดคงเหลือย้ายมาอยู่ที่เดียว** — `Helpers/LodgingAmounts.BalanceDue`
> (เดิมคัดลอกไว้ 4 จุดใน `LodgingService`) เพราะกำลังจะมีผู้ใช้รายที่ห้าคือเส้นจ่ายเงินของแขก


> ที่มา/การตัดสินใจเทียบ TakeTime: `LODGING_TAKETIME_ANALYSIS.md` · entity: `Models/Entities/Lodging.cs` ·
> engine (pure): `Helpers/LodgingPricingEngine.cs` (+ `LodgingAvailability`) · service: `Services/Implementations/Lodging/LodgingService*.cs` ·
> API หลังบ้าน: `Controllers/LodgingController.cs` (`/api/companies/{cid}/lodging/**` · สิทธิ์ `Lodging.Manage` / `Lodging.Settings`) ·
> API สาธารณะ: `Controllers/LodgingPublicController.cs` (`/cms/sites/{siteId}/lodging/**` AllowAnonymous · scope siteId + token) ·
> seed: `Services/Implementations/Cms/LodgingSeeder.cs` (เรียกจาก `CmsSiteService.CreateSiteAsync` **และ** `ApplyTemplateAsync`
> เมื่อ `IndustryType.Hotel` — idempotent ต่อ SiteId) · ค่าตั้งต้นทุกตัว (ราคา/เวลาเข้า-ออก/นโยบายยกเลิก/บริการเสริม)
> มาจาก **`Helpers/LodgingSeedDefaults`** ที่เดียว ซึ่ง `CmsSiteTemplateSeeder.HotelPlan` ใช้พิมพ์หน้าเว็บด้วย
> (รอบ 158: เดิมสองที่ถือคนละชุด — หน้าเว็บโฆษณา "Junior Suite ฿3,800 · 4 ประเภท · เช็คอิน 15:00 · ยกเลิกฟรีก่อน 3 วัน"
> ขณะที่ที่พักที่จองได้จริงมี 3 ประเภท · 14:00 · 7 วัน ⇒ แขกเห็นราคา/กติกาบนหน้าแรกแล้วไปเจออีกอย่างตอนจอง) ·
> **เว็บหนึ่งผูกที่พักได้แห่งเดียว** — `EnsureSiteNotBoundElsewhereAsync` + unique index `UX_LodgingProperties_CompanyId_SiteId_Live`

**ทางเข้า** — (1) storefront `/booking` `/book` `/rooms` (เฉพาะเว็บที่มีที่พักผูก — `tryRouteSpecialSlug` probe `/lodging/info` ก่อน ไม่มีก็ปล่อยหน้า CMS ที่ seed ไว้;
เมื่อ hijack **จะวาด Hero ของหน้า CMS นั้นไว้บนสุด** (`lodgingCmsHero`) เพื่อให้เจ้าของแก้หัวเรื่องจาก cms-edit ได้จริง — เดิมหน้าถูกแทนทั้งหน้า แก้อะไรก็ไม่มีผล = silent no-op) และบล็อก `BookingCalendar` ที่กลายเป็นช่องค้นหาห้องว่างอัตโนมัติ · (2) front desk `pages/lodging.html` (walk-in/โทร/OTA · `ConfirmImmediately`) · (3) `/reservation/{token}` ให้แขกดู/อัปโหลดสลิป/ยกเลิก/ส่งคำขอ

**Lifecycle**: `Pending` (กันห้องถึง `HoldExpiresAt` = `PaymentHoldMinutes`; หมดเวลา+ไม่มีสลิป → `ExpireHoldsAsync` ตั้ง Cancelled อัตโนมัติ; อัปโหลดสลิปต่อเวลา 24 ชม.) → `Confirmed` (พนักงานกดยืนยัน/รับมัดจำ · หรือทันทีเมื่อ `ConfirmWithoutDeposit`/มัดจำ = 0/staff ConfirmImmediately) → `CheckedIn` (ต้อง assign `LodgingUnit` ครบทุกห้อง · unit → Occupied) → `CheckedOut` · ทางออก `Cancelled`/`NoShow` (เฉพาะก่อนเช็คอิน — เช็คอินแล้วต้องเช็คเอาต์/ออกบิล)

**เส้นเงิน (ทุกใบผ่าน `IDocumentService` — โมดูลไม่ออกเลขเอง)**

| เหตุการณ์ | เอกสาร | หมายเหตุ |
| --- | --- | --- |
| ยืนยัน + รับมัดจำ (`ConfirmAsync`) | `Receipt` `IsDeposit=true` `BookingNumber=RES-…` `PricesIncludeVat=true` · **โหมดมัดจำจาก `DepositPolicyResolver`** (ที่พักตั้งทับ → บริษัท → ประเภทธุรกิจ · §2.3 ตาราง 3 โหมด) → Approve(ack) | VAT ทันที: Cr 217xx + 21911 (§78/1) · รอเรียกเก็บ: Cr 217xx + 21913 · เต็มยอด: Cr 217xx เต็ม · บริการที่ไม่ใช่ VAT ทันที ⇒ หมายเหตุ `RD-78/1-DEPOSIT-VAT` · ตรวจห้องว่างซ้ำก่อนยืนยัน (`LODGING-OVERSOLD`) · สถานะหลังรับมัดจำ `LodgingDepositSettlement.StatusAfterDeposit`: พนักงานกด "ยืนยัน" = ยืนยันเสมอ · เงินเข้าจาก gateway (`LodgingReservationPaymentHandler` `fromOnlinePayment:true`) = ยืนยันเฉพาะเมื่อเปิด `AutoConfirmOnDeposit` (ปิด = รอพนักงาน + ล้าง hold กันยกเลิกอัตโนมัติ) · ปุ่ม "รับชำระเพิ่ม" ส่ง `confirmReservation:false` · รับชำระบนการจองที่เช็คอินแล้วไม่ถอยสถานะ · webhook ซ้ำไม่ออกใบมัดจำซ้ำ (S-06 · รอบ 193) |
| เช็คเอาต์ (`CheckOutAsync` → `SettleCheckOutAsync`) | `TaxInvoice` (บริษัทจด VAT) / `Invoice` ทั้งการเข้าพัก: บรรทัดค่าห้องต่อห้อง (AccountCode = `RoomRevenueAccountCode`, ProductCode ของประเภทห้อง) + บริการเสริม (1 บรรทัด/รายการ ยอดรวม — ห้ามหาร Total/Qty) + folio Pending (VatRate รายบรรทัดผ่าน `LodgingPricingEngine.ChargeVatRate` — บริษัทไม่จด VAT = 0 ไม่ว่าเก็บอะไรไว้) + service charge (`ServiceChargeAccountCode`) · **รอบ 193 (P0-1/P0-3/C2)**: `LodgingDepositSettlement.PlanCheckout(ใบมัดจำทุกใบของการจอง, ฐานใบสุดท้าย, DocumentService.PreviewTotals)` วางแผน**ก่อนออกเลข**: มัดจำ "ออกใบกำกับแล้ว" ⇒ หักฐานบนใบ (ส่วนหักท้ายบิล) + การอนุมัติรับรู้ฐาน (`RealizeTaxedDepositDeductionsAsync`) · มัดจำ "พัก/เต็มยอด" ⇒ `ApplyDepositToInvoiceAsync` หลังอนุมัติ · เก็บ `BalanceDue` หลังหักจริง · มัดจำเกินยอด ⇒ ใช้เท่าที่ใบรับได้ ส่วนเกินเป็น `RefundAmount` ค้างคืน → ถ้า `CollectBalanceNow` และ `BalanceDue>0` → `CreatePaymentAsync` | ประทับ `FinalDocumentId` ทันทีหลังอนุมัติ · ใบออกแล้วแต่ใช้มัดจำล้ม ⇒ กดเช็คเอาต์ซ้ำ = ทำต่อ (`ResumeCheckOutAsync` · ไม่ตัดซ้ำ) · ล้ม = หมายเหตุ + audit + ข้อความ · ด่าน `LODGING-DEPOSIT-VAT-MISMATCH`/`LODGING-DEPOSIT-CONTACT` + หาผู้ติดต่อ**ก่อน**สร้างรายการค่าเสียหาย/เช็คเอาต์ช้า (รายการใหม่ผูกหลังออกใบสำเร็จ) · `RoundingDelta` ≠ 0 ⇒ หมายเหตุ · _เดิมส่ง `DepositAppliedDrivesJournal=true` บนใบเครดิตที่ AutoPost ไม่อ่าน ⇒ ลูกหนี้เต็ม + เก็บเงินซ้ำมัดจำ + VAT มัดจำซ้ำ_ · ใบเช็คเอาต์ถูก void ⇒ `FinalDocumentNote` บอกทางออกใบใหม่ที่หน้าเอกสาร (ไม่มีปุ่มในโมดูล — คำถามเจ้าของ Q11) · เช็คเอาต์เครดิต = DueDate +30 วัน + บันทึกยอดค้างใน InternalNotes · unit → VacantDirty + งานแม่บ้าน CheckoutClean อัตโนมัติ |
| ยกเลิก / no-show (`CancelCoreAsync`) | ค่าปรับ = `LodgingPricingEngine.CancellationFee` จาก **snapshot** นโยบาย ณ วันจอง (no-show = `NoShowChargePercent`) · **รอบ 193 (F-03/P0-2)**: ด่านสถานะ (Terminal + เช็คอินแล้ว) อยู่ในตัวกลาง ⇒ เส้นแขกยกเลิกซ้ำ = "ยกเลิกไปแล้ว" · บันทึกสถานะยกเลิก + `RefundAmount` (ยอด**ต้องคืน**) ก่อน แล้วลงบัญชีส่วนริบ `RealizeDepositAsync(ฐาน)` (`LodgingDepositSettlement.PlanCancellation` — เดิมส่ง gross เข้าช่องฐาน ⇒ ล้มทุกครั้งเมื่อจด VAT) · **ไม่ลง JE คืนเงิน/ใบลดหนี้ตอนยกเลิกแล้ว** | ค่าปรับเกินมัดจำ = บันทึกส่วนต่างที่ยังไม่เรียกเก็บใน InternalNotes · แขกไม่เห็นข้อความภายในเมื่อยกเลิกสำเร็จแต่ลงรายได้ส่วนริบล้ม (หมายเหตุ + audit ฝั่งพนักงาน) · night audit บันทึก no-show แต่ไม่ริบ/คืน (backlog) |
| ยืนยันคืนเงินแล้ว (`POST /lodging/reservations/{id}/refund-paid` · สิทธิ์ `LodgingManage` · `RecordRefundPaidAsync`) | พนักงานยืนยันว่าโอนคืนจริง → `RefundDepositAsync` (JE + ใบลดหนี้) ทีละใบมัดจำ **จากบัญชีที่เงินเข้า** (ขา Dr ของ JE รับมัดจำ เช่น 11340 gateway) หรือบัญชีธนาคารที่เลือก · หาไม่ได้ = ปฏิเสธให้เลือก (ไม่เดา 111) | `RefundPaidAmount/RefundPaidAt/RefundPaidBy/RefundReference` · `RefundState` (None/Pending/Paid/Unknown) คำนวณจากสองตัวเลขเท่านั้น · แถวยกเลิกเดิมที่โค้ดเก่า "ลงคืนแล้ว" = ป้าย `legacy:posted-at-cancel` ⇒ `Unknown` "ไม่มีข้อมูลการโอนคืน" (กดคืนไม่ได้ · ไม่อยู่ในคิว) · ล็อกระดับการจอง (`JobLock` + `AdvisoryLockKey.LodgingRefundPaid`) · ตัวซ่อมยอด `RefundPaidCatchUp` นับเฉพาะการคืนหลัง `RefundBaselineGross` (ยอดคืนบนใบมัดจำ ณ จุดตั้งยอดค้าง) · ห้ามลงวันที่ย้อนเข้างวด ภ.พ.30 ที่ยื่น/ประกาศว่ายื่นแล้ว หรือวันที่อนาคต · หน้า front desk: ป้าย "ค้างคืน" · มุมมอง "ต้องคืนเงินแขก" · หน้าแขก "รอที่พักโอนคืน ฿X"/"ที่พักโอนคืนแล้ว" |
| เลื่อนวัน (`RescheduleAsync`) | ไม่ออกเอกสาร — คิดราคาใหม่ทั้งใบ (แผนราคาเดิม) · มัดจำที่รับแล้วคงเดิม · ปลด unit ให้จัดใหม่ | เฉพาะ Pending/Confirmed |

**อัตรา VAT ของที่พัก (S-10 · รอบ 193)**: `LodgingPricingEngine.PropertyVatRate(chargeVat, registered, companyRate)` ผ่าน
`OutputVatRate.ForCompany` ตัวเดียวกับ POS/TimeBilling · สถานะจด VAT อ่านผ่าน `CompanyVatStatus.ProfileAsync` · **เปลี่ยนพฤติกรรม**: ตั้ง
"คิด VAT เสมอ" บนบริษัทที่ไม่จด VAT = 0 (§90/2 — เดิม 7) · บริษัทที่ตั้งอัตราอื่นได้อัตรานั้น
**บริการเสริม (#36/F-01)**: `LodgingPricingEngine.ExtraTotal` ปฏิเสธวิธีคิดราคาที่ไม่มีในระบบ (เดิม `_ => 1` คิดครั้งเดียวเงียบ) · ตัวตัดสินเดียว
`ExtraConfigProblem` · `BuildQuote` ใส่ error ไทยแทนการคิด · หน้าแขกไม่แสดงบริการที่ตั้งค่าไม่ครบ · `SaveExtraAsync` ปฏิเสธ (`LODGING-EXTRA-PRICEMODE`) ·
หน้าตั้งค่า: ป้าย "ต้องเลือกใหม่" · ค่าที่ไม่ตรงตัวเลือก = "— ต้องเลือกใหม่ —" + required · รายงานข้ามที่พัก `GET /lodging/extras/needs-reselect` (ไม่ migrate ค่าทับ)
**หน้าแขก (C9)**: เซิร์ฟเวอร์ส่ง `StatusLabel` · `OnlinePayableAmount` (ตัวเดียวกับ gateway) · `OnlinePaymentNote` — หน้าแสดงอย่างเดียว (ถอดสูตรใน JS)
**ผู้ติดต่อแขก**: `FindOrCreateContactAsync` จับด้วยเลขภาษี + `"00000"` · เลขนี้มีแล้วคนละแถว ⇒ ไม่ถอยไปจับอีเมล/เบอร์ (de97a95) ·
แขกที่ส่งเลขภาษี/ชื่อบริษัท: แถวที่จับด้วยอีเมล/เบอร์ต้องไม่ใช่บุคคลธรรมดาและชื่อตรงชื่อบริษัท (`Helpers/LodgingGuestContact` · ไม่ fuzzy) ไม่งั้นสร้างแถวใหม่ ·
แถวที่ยังไม่มีเลข → เติมผ่าน `ContactTaxBranchKey.AdoptTaxId(row, taxId, branch, ContactMatchKind)` (§6.2i · 04ce362) ส่งชนิดการจับจริงต่อผู้สมัคร
(อีเมลก่อน แล้วเบอร์) · `Reject` ⇒ สร้างแถวใหม่ (เรียก `StampTaxIdWarning` ก่อน `Add` — เลข checksum ผิดติด `[TAXID-CHECKSUM]` · c3820dce) · แถว walk-in ไม่เข้าข่าย
ผู้สมัครเลย · รูปเดิม `[Obsolete]` ถูกลบแล้ว (ผู้เรียก 0)

**ราคา** (`LodgingPricingEngine.NightlyRate` ต่อคืน): ฐาน → แผนราคา (Absolute/Multiplier/Delta) → ฤดูกาล (ช่วงแคบกว่าชนะ · recurring ข้ามปีได้ · จำกัดประเภทห้องได้) → สุดสัปดาห์ (`WeekendMultiplier`×mask) → override รายวัน **แทนที่ทั้งหมด** · แขกเกิน `StandardOccupancy` × `ExtraGuestPrice` · เตียงเสริม · PerPerson = ราคา×คน · `Totals`: ราคารวม VAT → VAT = total×r/(100+r) (informational — ตัวจริงคำนวณอีกครั้งตอนออกเอกสารด้วยสูตรเดียวกัน) · มัดจำ = %/คงที่ + min/max · ขั้นต่ำคืน = max(ที่พัก, ห้อง, ฤดูกาล, override). ห้องว่าง (`LodgingAvailability.AvailableRooms`): ต่อคืน min(capacity(allotment) + overbooking − ที่กัน) · Pending กันเฉพาะที่ hold ยังไม่หมด · StopSell = 0 · unit ปิดซ่อมไม่นับ

**Invariants**: `Reservation.TotalAmount` = engine ณ วันจอง (snapshot ใน `PriceBreakdownJson`) · `FolioTotal` = Σ charges ที่ไม่ Cancelled · `PaidAmount` = มัดจำ + ยอดเก็บตอนเช็คเอาต์ − คืน · `BalanceDue` = Total+Folio−Paid · เลขจอง `RES-{Code}-{yyMM}-{####}` ต่อที่พัก ภายใต้ `AdvisoryLockKey.For(cid,"lodging-res",propertyId)` · `PublicToken` 32 hex ต่อการจอง (unique index) · `GuestIdNumber` ไม่เคยออกจากเซิร์ฟเวอร์เต็ม (`MaskId`) · ทุก query มี `CompanyId` + ฝั่งสาธารณะเพิ่ม `SiteId`

**ตาราง (CREATE TABLE IF NOT EXISTS ใน `DatabaseMigrationHelper`)**: LodgingProperties · LodgingRoomTypes · LodgingUnits · LodgingRatePlans · LodgingSeasons · LodgingRateOverrides · LodgingCancellationPolicies · LodgingExtras · LodgingReservations · LodgingReservationRooms · LodgingReservationExtras · LodgingFolioCharges · LodgingHousekeepingTasks · LodgingGuestRequests

## 7. Validation gates (compliance — กฎเหล็ก #2)

| Gate | Where | กระทำ |
| --- | --- | --- |
| **โควตาเอกสารของแพ็กเกจ** | `Helpers/DocumentQuotaPolicy.Classify` เรียกจาก `DocumentService.CreateDocumentAsync` | ตอบ **สองคำถามแยกกัน**: (1) *นับโควตาไหม* — นับเฉพาะใบที่แทน "การขาย 1 ครั้ง" (TaxInvoice/Invoice/Receipt); CN/DN/ใบสำคัญ/เอกสารฝั่งซื้อ/ใบส่งของ **ไม่นับ**; เอกสารที่โมดูลที่พักออกให้ (`OriginModule="Lodging"`) ไม่นับเพราะมีมิเตอร์ `lodging.stay` แล้ว (2) *บล็อกได้ไหม* — **ใบที่กฎหมายบังคับให้ออกห้ามบล็อกเด็ดขาด** (§86/4 tax point เกิดแล้ว · §86/9-10 ถ้าค้างจะทำให้ ภ.พ.30 เกินจริง) → เกินโควตาบันทึกเป็น `documents.overage` แทนการปฏิเสธ; บล็อกได้เฉพาะใบเสนอราคา/ใบสั่งซื้อ/ใบขอซื้อ (`BusinessRuleException` รหัส `QUOTA-DOCUMENTS`). เพดานจริง = โควตาแพ็กเกจ + โบนัสที่ยังไม่หมดอายุ (`EffectiveLimit`). **`originModule` เป็นพารามิเตอร์ของ `CreateDocumentAsync` ไม่ใช่ช่องใน `CreateDocumentRequest`** — เดิมอยู่ใน DTO ⇒ ผู้เรียก API ส่ง `"originModule":"Lodging"` เองแล้วเลี่ยงโควตาได้ทุกใบ (ค่าที่ client คุมได้ ห้ามใช้ตัดสินเรื่องเงิน) — ดู ACCOUNT_STRUCTURE.md §6.1b |
| §86/4 completeness (PI/Expense/PV) | `TaxInvoiceCompletenessChecker` | ถ้าไม่ครบ → input VAT ลง 11640 (undue) |
| §82/5 prohibited input VAT | `ChartOfAccount.InputVatClaimable` + per-line `IsVatClaimable` | flag claim=false, แยกออกจาก ภ.พ.30 + แสดง "🚫 §82/5" line |
| §82/5(1)(2) non-full-tax-invoice | `OcrDocumentRoleInferrer.Infer` → `InputVatClaimable/InputVatClaimWarning` | OCR ตรวจ "ใบกำกับภาษีอย่างย่อ §86/6" หรือ "ใบเสร็จ/บิลเงินสด ไม่ใช่ §86/4" + มี VAT → เขียน `[VAT-CLAIM]` ลง ProcessingNotes; review UI + form แสดง banner แดง "เคลม VAT ไม่ได้ — ขอใบกำกับเต็มรูป"; ไม่ auto-ติ๊ก "ขอเครดิตภาษีซื้อ". กัน false positive 2 ชั้น: `ContainsAnyNotNegated` (ข้ามข้อความปฏิเสธ "ไม่ใช่...อย่างย่อ" จาก vision model) + เลขภาษีผู้ซื้อ 13 หลักถูกสกัดได้ = ใบเต็มรูปเสมอ (§86/6 ใบอย่างย่อไม่มีข้อมูลผู้ซื้อ) override คำที่เจอบนกระดาษ |
| §82/3 6-month window | `TaxFilingExportService.ExportPp30Async` + `GenerateVatReport` | เกิน 6 เดือน → block claim หรือ require `LateReason` |
| §86/9–86/10 CN/DN | `CreditNote/DebitNote` flow | required `RelatedDocumentId` + `CreditNoteReason` (CN); cap ≤ original |
| §78 / §78/1 / **§78/2** tax point | `TaxPointResolver` | snapshot ตอน approve · นำเข้าใช้ `Document.CustomsDutyPaidDate` **ตรง ๆ ไม่ใช่ MIN** · `SupplyKind` ให้ caller ระบุชนิดแทนการเดา (Auto = พฤติกรรมเดิม) |
| §65 ตรี รายจ่ายต้องห้าม | `Section65TerValidator` | `NonDeductibleAmount + RuleJson` → ภ.ง.ด.50 · ครอบ **16 กลุ่มอนุมาตรา** (เดิม 8): เพิ่ม (8)(9)(10)(12)(13)(14)(15)(19) — context ใหม่ทุกตัว optional default = "ไม่ตรวจ" จึงไม่เดาแทนผู้ใช้ |
| ภ.ง.ด.50/51 ยอด CIT | `TaxReport.CitAmount` | **แยกจาก `TotalTaxWithheld`** (เดิม reuse field ผิดความหมาย ทำให้ยอด CIT ปนกับ WHT เวลารวมข้ามรายงาน) · ผู้อ่านใช้ `CitAmount ?? TotalTaxWithheld` รองรับรายงานเก่า |
| §65 ตรี(4) cap per fiscal year | `Section65TerValidator.Context.PriorYtdEntertainmentExpense` | sum YTD entertainment ของเอกสารที่ **ออกแล้วและยังมีผล** (`DocumentStatusRules.NotIssued` + ไม่ Voided — เดิม `== Approved` ทำให้ PV/PI ที่จ่ายแล้ว (Paid) หลุดทั้งปี · ERP_REVIEW A-01) → excess บวกกลับใบปัจจุบัน |
| §82/5(6) vehicle dealer override | `CompanySettings.IsVehicleDealer` → **`Helpers/InputVatVehicleRule.Judge(hay, vendor, isVehicleDealer)`** ตัวเดียว (รอบ 193 S-05) | ธงถึงทุกเส้น: OCR (`ProhibitedInputVatScreener.Screen(..., isVehicleDealer)` — required) · ด่านเตือนตอนอนุมัติ · ฟอร์มคีย์มือ (`POST /api/companies/{id}/input-vat/screen`) · dealer ⇒ `VehicleDealerExempt` ไม่ปิดเคลม (OCR ใส่ `[VAT-NOTE]` ไม่ใช่ `[VAT-CLAIM]`) + เตือน "รถยนต์นั่งที่ใช้เองยังต้องห้าม" · §82/5(4) ค่ารับรองไม่สนธง · ชั้น `UnclearCheck` = **เตือน ไม่ปิดเคลม** (ยี่ห้อ/คำว่ารถยนต์ + งานรถ · ปั๊ม/บัตรน้ำมัน · "fuel" ที่ไม่ใช่ surcharge) · **ลิสต์คำรถชุดที่สองในเมธอดอนุมัติถูกถอด** (เคยเตือนค่าซ่อมแอร์/น้ำมันพืช/fuel surcharge) · TaxService อ่านธงแล้วไม่ใช้ = ถอด (ตัวเลข ภ.พ.30 ไม่เปลี่ยน) |
| §82/5 กติกาฟอร์มคีย์มือ (ค่ารับรอง · บิลเงินสด) | `Helpers/ManualInputVatLineRule` (เรียกหลังตัวคัดกรองกลางใน `InputVatScreenController` · **ไม่ใช้กับเส้น OCR**) | ย้ายจาก JS (regex ใน `documents.html` = 0) · ความกว้างเท่า regex เดิม (baseline จาก `git show 7601891`) ลบบริบทที่ระบุชื่อ (หนังสือรับรอง/ตรวจรับรอง ISO/อาหารเลี้ยงสัตว์/เบี้ยเลี้ยง/เลี้ยงพนักงาน) · ปิดเคลมอัตโนมัติติด `dataset.autoSrc='screen'` — ผู้ใช้กดเปิดคืนได้ (ค่าผู้ใช้ชนะ) · endpoint เป็น `POST` body (กัน 414) |
| §82/5(1) ผู้ซื้อบนกระดาษ (คำตัดสิน #9) | `Helpers/OcrBuyerOnPaper` (OCR `OcrService.cs` บล็อก §82/5) | ใบหัว "ใบกำกับภาษี" ที่**ข้อความบนกระดาษ**ไม่มีชื่อ/เลขผู้ซื้อ (ชื่อเราที่ระบบเติมเอง/เลขที่ย้ายมา ไม่นับ · e-Tax XML ใช้ `FromStructured`) ⇒ `[VAT-CLAIM]` `RD-82/5(1)-BUYER` (ม.82/5(1) ประกอบ ม.86/4(3)) → บรรทัด `IsVatClaimable=false` + เหตุผลรายบรรทัดตามกฎที่ตรงสาเหตุ · `OcrPaperPresence.Unknown` = ไม่แตะ |
| §82/5(2) e-Tax XML T05/T06 | `EtaxPdfXmlExtractor.AbbreviatedClaimBlock` | XML ใบกำกับอย่างย่อ ⇒ `[VAT-CLAIM]` §82/5(2) ชนะการอนุมานจากข้อความ (ไม่ไปพัก 11640) |
| §90/2 ธง VAT สองตัว (stopgap S-01) | `Helpers/CompanyVatStatus.IsRegistered(companyFlag, settingsFlag?)` | มีแถวค่าตั้ง = `CompanySettings.VatRegistered` · ไม่มีแถว = `Company.IsVatRegistered` (เดิม `?? true` "ถือว่าจด") · ผู้อ่านฝั่งเอกสาร/integration `?? true` = 0 จุด · ลำดับ "ค่าตั้งก่อน" รอคำตัดสิน Q1 (TODO ในโค้ด) · แถวค่าตั้งเกิดพร้อมบริษัท (`CompanySettingsFactory`) · รายงาน `vat-flag-consistency` |
| มัดจำ: ใบกำกับซ้ำ | `DepositPolicyResolver.GrossApplyBlocked` | `RD-86/4-DEPOSIT-TIV-DOUBLE` ทุกเส้น/ทุกงวด · `DEPOSIT-DRIVES-UNSUPPORTED` · `RD-78/1-DEPOSIT-VAT`/`RD-78-DEPOSIT-VAT` (คำเตือนค่าตั้ง) (§2.3) |
| ที่พัก | `LodgingService` | `LODGING-EXTRA-PRICEMODE` · `LODGING-DEPOSIT-VAT-MISMATCH` · `LODGING-DEPOSIT-CONTACT` (§6.5) |
| [Σ-GAP] ตอนอนุมัติด้วยมือ (คำตัดสิน #12) | `Helpers/OcrApprovalGapWarning` + `ApprovalAcknowledgement` | §3.2 ขั้นคำเตือน — คนต้องกด "รับทราบ" · API ไม่ขัดจังหวะ |
| §87 ลำดับเวลาในรายงาน | `TaxService.NormalizeReportLineOrder` | เรียง + renumber `LineOrder` ท้ายการ generate ทุกครั้ง (ขาย → ซื้อ → บรรทัดสรุป; แต่ละกลุ่มตาม `TransactionDate`, ties = ลำดับเดิมเพื่อ deterministic). เรียกจาก GenerateVatReport / หลัง ApplyVatDeferrals / ComputeVatReport (ไฟล์ยื่น) / GenerateWhtReport / PullDocumentIntoReport / regenerate re-apply. `tax.html` sort ซ้ำฝั่ง client (จอ + แบบพิมพ์ §87) ให้รายงานเก่าถูกลำดับโดยไม่ต้อง regenerate — เดิม LineOrder ไล่ตามลำดับที่ query คืนเอกสาร = วันที่สลับไปมา |
| §82/5(6) นโยบาย "ดุลพินิจผู้กรอก" | keyword auto-cut ถูกถอดออกทั้งหมด | ระบบ**ไม่เดา**จากข้อความไปตัดสิทธิ (เดิม "ค่าน้ำมัน" คำเดียวโดนตัด = น้ำมันรถกระบะผู้รับเหมาหายจาก ภ.พ.30). การตัดใช้เฉพาะ (a) flag รายบรรทัด IsVatClaimable (b) ผังบัญชีต้องห้ามที่บริษัทตั้งเอง; คำเตือนกฎรถยนต์นั่ง (ประกาศ 42: กระบะตอนเดียว/แค็บ/บรรทุก/ตู้>10 เคลมได้; เก๋ง/กระบะ 4 ประตูไม่ได้) มี 2 จุด — approve warning + confirm ตอนติ๊กเคลมในหน้าเอกสาร · **รอบ 193: ทั้งสองจุดอ่านลิสต์คำชุดเดียว `InputVatVehicleRule`** (หน้าเอกสารถามเซิร์ฟเวอร์ — ลิสต์คำใน JS = 0) |
| ติ๊กเคลมภาษีซื้อเข้า/ออกหลังอนุมัติ | `CompleteSupplierTaxInvoiceAsync` + `ClaimInputVat` (Unclaim/ReclaimInputVatAsync) | แผงในหน้า detail ของ PV/Expense/PI (approved, VAT>0): เลิกเคลม → JE Dr ค่าใช้จ่าย "ภาษีซื้อขอคืนไม่ได้"/Cr 11610 หรือ 11640 + set override=ผังค่าใช้จ่าย (marker ที่ ภ.พ.30 exclude อยู่แล้ว) + ติ๊กบรรทัดงวด Draft ออก; block เมื่อเคลมในงวด Filed แล้ว (ต้องยื่นเพิ่มเติม). กลับมาเคลม → require §86/4 ครบ + กรอบ 6 เดือน §82/3 → JE ย้อน + BecameClaimableAt=now (เข้า ภ.พ.30 งวดปัจจุบัน) + PV เปิด HasTaxInvoiceReference. แก้เลขที่/วันที่/สาขาใบกำกับได้ทุกใบจากแผงเดียวกัน |
| F14 audit hash chain | `AuditTrailService.VerifyHashChainAsync` + `AuditChainVerifyJob` → **`Helpers/AuditHashChain`** (รอบ 193 · §6.1) | cron 7 วัน · สูตร v2 `Seal` ฝั่งเขียนตัวเดียว · `Analyze` แยก ถูกแก้/ขาดตอน/แตกกิ่ง · notify ตามสาเหตุจริง (พ.ร.บ.บัญชี ม.11 ทวิ) |
| Recurring template validate | `RecurringTransactionService.ValidateTemplateAsync` | fail-fast ตอน Create/Update ก่อนรอ midnight cron — accountId ต้องอยู่ใน CoA, journal balance |
| Reclassify line GL (post-approve) | `DocumentService.ReclassifyLineAccountAsync` | เปิดทั้งฝั่งซื้อและฝั่งขาย (Invoice/TaxInvoice/Receipt/CN/DN ด้วย — §86/4 บังคับ**สิ่งที่พิมพ์บนใบกำกับ** ซึ่งไม่มีรหัสผังบัญชีอยู่เลย); post JE คู่ใหม่ลงงวดเดิม โดย**อ่านขาที่ลงจริงใน GL** (`oldWasCredited`) แล้วกลับตามนั้น + update line.AccountId. **ยอดที่ย้าย = `ResolveLinePostedGlAmountAsync`** ไม่ใช่ `line.Amount` เสมอ: ภาษีซื้อต้องห้าม §82/5 (`IsVatClaimable=false`) → `Amount + VatAmount` เพราะ AutoPost รวม VAT เป็นต้นทุนบรรทัด (ย้ายแค่ฐาน = VAT ค้างผังเก่าถาวร โดย Dr=Cr ยังสมดุล) · ใบที่อ้างใบรับของ GRN → ฐานตัด GR-NI ไปแล้ว บรรทัดเหลือแค่ VAT ต้องห้าม · PV ที่แปลงจากใบตั้งหนี้ (settlement) → 0 (แค่เปลี่ยน AccountId ไม่ลง JE) · ต่างสกุล → คูณ `ExchangeRate` ผ่าน `ToGlAmount`. gate: period Open + no downstream (ไม่นับใบเสร็จ settlement) + not Draft/WaitingApproval/Voided/Rejected + ไม่ใช่ใบมัดจำ + ไม่ใช่บัญชีคุม + no submitted ภพ.30 + no e-Tax submitted |
| §87(3) chronological | ExportPp30Async summary | นับ doc ที่ tax point ย้อนกลับ → surface ใน Summary.csv |
| §87/3 retention 5 ปี | `RetentionUntil` | ห้าม hard delete; soft + legal_hold |
| §85/1 VAT threshold 1.8M | annual revenue check | warning "ต้องจด VAT ภายใน 30 วัน" |
| WHT 50 ทวิ ≤ threshold 1,000 | `Helpers/WhtCumulativeScope.SumDistinct` (ขอบเขตด่าน = `Helpers/WhtGateScope`) | ไม่หักถ้ายอดสะสมทั้งสัญญา < 1,000 · `CheckWhtThresholdAsync` ถูกลบรอบ 183 (ด่านซ้อน) |
| DTA override | WHT cert PDF | bilateral rate แทน ม.70 default |
| OCR auto-fill ครบ (กฎเหล็ก #3) | `OcrService.CreateDocumentFromScanAsync` | ทุก §86/4 field ต้อง pre-filled ก่อนเปิดฟอร์ม |

---

## 8. Quick reference — โค้ดอยู่ไหน

| ต้องการทำอะไร | ไปดูที่ |
| --- | --- |
| แก้การคิดส่วนลด/VAT ต่อบรรทัด | `DocumentService.ComputeLineAmounts :254` — รองรับ `DiscountPercent` + `DiscountAmount` (ยอดเงิน, มาตรฐานสากล: ใบระบุส่วนลดเป็นบาท). amount > 0 ชนะ % |
| เศษสตางค์ VAT/WHT (ปัดรายบรรทัดแล้วรวมเพี้ยน ±0.01) | `DocumentService.ReconcileTaxRounding` — หลังคิดทุกบรรทัด (create+update) กระทบยอดต่อกลุ่มอัตรา: ΣVAT/WHT ของกลุ่ม = round(Σฐาน × อัตรา) ตรงเครื่องคิดเลข; เศษเกลี่ยเข้าบรรทัดฐานสูงสุด; ข้าม `VatAmountOverride`; โหมดราคารวม VAT ขยับ net สวนทางคง gross. frontend mirror ใน `documents.html calcSum` (allocation+reconcile แบบเดียวกัน — ยอดก่อน/หลังบันทึกตรงกัน) |
| เพิ่ม `DocumentType` ใหม่ | `Models/Enums/AllEnums.cs:305` + `DocumentService.cs` หลายจุด (search by enum literal) |
| แก้ flow Approve | `DocumentService.ApproveDocumentAsync :4809` · คำเตือน `CollectApprovalWarningsAsync :17169` · แหล่งการรับทราบ `Helpers/ApprovalAcknowledgement` |
| แก้ flow JE per type | `DocumentService.AutoPostToJournalAsync :13843+` |
| **ผลข้างเคียงหลังออกเอกสาร (e-Tax อัตโนมัติ)** | `Services/Implementations/IssuedDocumentHooks.cs` (`IIssuedDocumentHooks.RunAsync`) + ขอบเขต `Helpers/EtaxAutoIssueScope` + ป้าย `Helpers/EtaxAutoFailedNote` · ทุกเส้นที่ประทับ `Approved` เอง ต้องเรียกหลัง commit (`tools/approved_status_writer_check.py`) |
| **วิธีบันทึกเงินมัดจำ (3 โหมด)** | `Helpers/DepositPolicyResolver.cs` (owner file) · ที่พัก `Helpers/LodgingDepositSettlement.cs` · คืนเงิน `Helpers/DepositReversalMath.RefundSplit` |
| **ยอดชำระจริง ≠ ยอดใบกำกับ (ค่าส่ง/คูปอง)** | `Helpers/PaymentSettlementAdjustment.cs` · `DocumentService.CreatePaymentAsync :11227` / `CreatePaymentJournalAsync :15975` · ข้อเสนอจากสแกน `Helpers/OcrSettlementProposal` (§3.4) |
| **ผลต่างปัดเศษ (54960)** | `Helpers/DocumentRounding.cs` (§6.2j) |
| **หาผู้ติดต่อด้วยเลขภาษี + สาขา** | `Helpers/ContactTaxBranchKey.cs` (§6.2i) · checker `tools/contact_taxid_only_match_check.py` |
| **ด่านไฟล์แนบ/สแกน** | `Services/Implementations/AttachmentAccessGate.cs` + `Helpers/AttachmentPermissionScope.cs` (§6.2h) · checker `tools/attachment_gate_check.py` |
| **ธง VAT บริษัท (stopgap)** | `Helpers/CompanyVatStatus.cs` + `Helpers/CompanySettingsFactory.cs` · รายงาน `Controllers/VatFlagConsistencyController.cs` |
| **รอบเงินเดือน: คำนวณใหม่/แก้ยอด/ฐาน ปกส.** | `Helpers/PayrollRunEditPolicy.cs` · `PayrollService.LoadRecalculateLockEvidenceAsync` · `Helpers/SsoWageBase.ForPeriod` (§3.8) |
| **ใบเบิก: ด่านสิทธิ์ + SoD + หลักฐาน** | `Helpers/ExpenseClaimActionPolicy.cs` · `Helpers/ExpenseClaimEvidencePolicy.cs` · `ExpenseClaimService.EnsureClaimActionAsync` (§6.2h) |
| แก้ stock movement (ทิศทางต่อชนิดเอกสาร) | `DocumentService.ApplyStockMovementsAsync` |
| **แก้การเขียนสต็อกเอง (ยอด/คลัง/ต้นทุน)** | `Services/Implementations/Inventory/StockLedger.cs` — **ที่เดียวของระบบ** |
| แก้สูตรต้นทุนถัวเฉลี่ย | `Helpers/WeightedAverageCost.cs` |
| แก้ §65 ตรี rule | `Services/Implementations/Tax/Section65TerValidator.cs` |
| แก้ tax point logic | `Services/Implementations/Tax/TaxPointResolver.cs` |
| แก้ §86/4 completeness | `Services/Implementations/Tax/TaxInvoiceCompletenessChecker.cs` |
| แก้ fixed asset auto-register / ขึ้นทะเบียนจากเอกสาร | `FixedAssetService.RegisterFromDocumentAsync` (ตัวเดียว — `DocumentService.AutoRegisterFixedAssetsAsync` แค่มอบต่อ) · ลิสต์ชนิดที่รองรับ `FixedAssetService.SupportsAutoRegister` |
| แก้ผัง 11640 ↔ 11610 reclassify | `DocumentService.ReclassifyUndueInputVatAsync :1137` |
| แก้ deposit Realize/Refund/Apply | `DocumentService.cs` — `RealizeDepositAsync :3422` / `RefundDepositAsync :3940` / `ApplyDepositToInvoiceCoreAsync :4139` · หักฐานมัดจำออกใบกำกับแล้ว `ConvertTaxedDrivesAsync :15783` / `RealizeTaxedDepositDeductionsAsync :15832` · กลับตอน void `ReverseDepositRealizationsForAsync :8100` · ฐานมัดจำช่องแยก `Document.DepositBaseDeducted` + `AllocateBillDeductions :650` + `DepositPolicyResolver.BillDeductionTotal/SplitBillDeduction/TaxedDepositDeductionProblem/DrivesGuardMessage` |
| แก้ราคาที่พัก/ห้องว่าง (ฤดูกาล/แผนราคา/override/มัดจำ/ค่าปรับยกเลิก) | `Helpers/LodgingPricingEngine.cs` (pure + `Accounting.Tests/LodgingPricingEngineTests.cs`) — service แค่โหลดข้อมูลส่งเข้า `BuildQuote` (`LodgingService.Reservations.cs`) |
| แก้เอกสารตอนยืนยันมัดจำ/เช็คเอาต์/ยกเลิกที่พัก | `Services/Implementations/Lodging/LodgingService.Lifecycle.cs` — `CreateDepositReceiptAsync` · `CheckOutAsync`/`SettleCheckOutAsync`/`ResumeCheckOutAsync` · `CancelCoreAsync` · `RecordRefundPaidAsync` (§6.5) · แผนเงิน `Helpers/LodgingDepositSettlement` |
| seed ที่พักตอนสร้างเว็บโรงแรม | `Services/Implementations/Cms/LodgingSeeder.cs` ← `CmsSiteService.CreateSiteAsync` (IndustryType.Hotel) |
| แก้รายงาน ภ.พ.30 (จอ) | `TaxService.GenerateVatReport :119` |
| ดูสายการแปลงทั้งเส้นของเอกสาร (chain stepper) | `DocumentService.GetDocumentChainAsync` — ขึ้นตาม RelatedDocumentId (กัน cycle, 15 ชั้น) แล้ว BFS ลง (เพดาน 60 ใบ); ใบ Voided คงอยู่ในสาย (UI ขีดฆ่า) / UI: `documents.html renderChainStepper` บนสุดของ detail modal |
| ค่าเริ่มต้นฟอร์มต่อชนิดเอกสาร (แหล่งเงิน/เงื่อนไขชำระ/วันเครดิต) | `DocumentTemplate.DefaultPaymentAccountId/DefaultPaymentTerms/DefaultCreditDays` — ตั้งใน template default ของชนิดนั้น (`document-templates.html` กล่อง "⚡ ค่าเริ่มต้น") / ฟอร์มดึงผ่าน `GET document-templates/default/{type}` เติมเฉพาะช่องว่าง+เฉพาะสร้างใหม่ (`applyDocTypeDefaults`) |
| **ลำดับค่าเริ่มต้นเทอมชำระเงิน/วันเครดิต** | **ผู้ใช้พิมพ์เอง > เครดิตของลูกค้า (`Contact.PaymentDueDays/PaymentTerms`) > เทมเพลตชนิดเอกสาร > AI (`/ai/payment-terms/suggest`)** — ติดตามที่มาผ่าน `dataset.autoSrc` ('template'\|'contact') + `dataset.userTouched` บน `#fCreditDays`/`#fPaymentTerms` (`documents.html`): ชั้นที่แคบกว่าทับชั้นที่กว้างกว่าได้ แต่ห้ามทับค่าที่ผู้ใช้แตะแล้ว; ตอนแก้เอกสาร (`openEdit`) ค่าที่บันทึกไว้ถือเป็น userTouched เสมอ. ช่องแสดงทุกชนิดเอกสาร (เดิมซ่อนใน `.supplier-invoice-only` → ใบเสนอราคาแก้เทอมไม่ได้) |
| เงื่อนไขการชำระเงินบนกระดาษ (`doc.PaymentTerms/CreditDays`) | render ทั้ง 2 ตัว (HTML `BuildDocumentHtml` + QuestPDF `DocumentRenderer`) เมื่อ `template.ShowPaymentTerms` — เดิม flag มีแต่ไม่มีใคร render = กรอกแล้วหายจากกระดาษเงียบ ๆ. **`PaymentTerms` เก็บได้หลายบรรทัด** (1 เงื่อนไข/บรรทัด — ฟอร์มเพิ่ม/ลบรายข้อผ่าน `#ptList`, sync ลง hidden `#fPaymentTerms`): HTML ใช้ `white-space:pre-line`, PDF แตกเป็น bullet เมื่อ >1 บรรทัด |
| **ชื่อ/ที่อยู่คู่สัญญาบนเอกสารภาษาอังกฤษ** | `ThaiAddressFormatter.ResolvePartyAddress` / `ResolvePartyName` — **resolver กลางตัวเดียว** ที่ทั้ง HTML และ QuestPDF เรียก (4 จุด: บริษัท×2 · ผู้ติดต่อ×2). ลำดับที่อยู่โหมด en: **ที่ผู้ใช้กรอกเอง (`AddressEn`) → ถอดอักษรอัตโนมัติ (`ThaiRomanizer`) → ที่อยู่ไทย**; โหมดไทยใช้ `Format` ตามปกติ. **ที่มา**: เดิมตรรกะนี้เขียนซ้ำ 4 จุด และ**สองจุดของผู้ติดต่อไม่มีชั้น "กรอกเอง" เลย** (ถอดอักษรเสมอ) ⇒ ที่อยู่ลูกค้า/ผู้ขายบนใบภาษาอังกฤษ **แก้ทับไม่ได้ตลอดกาล** ต่อให้รู้ว่าตัวถอดสะกดตำบล/อำเภอเพี้ยน (ThaiRomanizer เป็น RTGS แบบประมาณ ไม่มีพจนานุกรมเสียงอ่าน — ตำบล/อำเภอ/ถนน/ชื่ออาคารสะกดเพี้ยนได้เป็นปกติ ส่วนจังหวัด 77 ชื่อใช้ตารางทางการจึงแม่น). แก้โดยเพิ่ม `Contact.NameEn` + `Contact.AddressEn` คู่ขนานกับ `Company.NameEn`/`AddressEn` แล้วรวม 4 จุดเป็น resolver เดียว. **ชื่อไม่ถอดอักษรให้อัตโนมัติเด็ดขาด** — การสะกดชื่อเฉพาะเป็นสิทธิ์ของเจ้าของชื่อ (จดทะเบียนไว้อย่างไรต้องตามนั้น) เดาผิด = เอกสารระบุคู่สัญญาผิดคน ต่างจากที่อยู่ ที่ถอดผิดยังสื่อสารได้. ฟอร์มผู้ติดต่อมี section พับได้ "🌐 ข้อมูลภาษาอังกฤษ" (ไม่รกจอสำหรับลูกค้าไทยล้วน · เปิดอัตโนมัติเมื่อมีค่า) · payload/hydrate/reset ครบตาม checklist field ใหม่ · **ความแม่นของตัวถอดอักษร**: `ThaiRomanizer.PlaceNameEn` เป็นตารางสะกดทางการ ระดับ**อำเภอ/เขต** ที่ชนะตัวถอดเสมอ — (ก) **50 เขตกรุงเทพฯ** hand-curated (ที่อยู่ที่พบบ่อยที่สุดในเอกสารธุรกิจไทย + สะกดปรากฏบนป้าย/เอกสารราชการจึงยืนยันได้) (ข) **อำเภอเมือง 75 แห่งประกอบอัตโนมัติ** = `"Mueang "` + ชื่อจังหวัดทางการ — ไม่มีใครพิมพ์ด้วยมือจึงไม่มีทางสะกดผิด, มี guard ว่าส่วนหลังต้องเป็นชื่อจังหวัดจริงเพื่อกันเคส `เมืองจันทร์`(ศรีสะเกษ)/`เมืองปาน`(ลำปาง)/`เมืองยาง`/`เมืองสรวง` ที่ไม่ใช่อำเภอเมือง. รวมครอบ 126/928 อำเภอ + ช่วยแขวง กทม. อีก 35/178 (แขวงหลายแห่งชื่อเดียวกับเขต). **อำเภอต่างจังหวัดที่เหลือ ~800 ชื่อไม่ใส่มั่ว** — คำที่เดาเองในตารางที่ดู "ทางการ" อันตรายกว่าปล่อยตัวถอด เพราะผู้ใช้จะไม่เอะใจไปตรวจ; ทางออกคือ `AddressEn` ที่กรอกทับได้. เทสต์ `PartyEnglishTextTests` |
| **ที่อยู่บนเอกสารทุกชนิด (ต./อ./จ. · แขวง/เขต)** | `ThaiAddressFormatter.Format` — **ตัวประกอบที่อยู่ตัวเดียวของทั้งระบบ** ห้ามเขียน `string.Join` เอง. เดิมมี 5 ตัวแยกกัน (PdfGeneration / WithholdingTaxCert / DocumentService.ComposeAddress / CompanyService.ComposeAddress / EtaxInvoice PdfA3) และ 4 ตัวไม่ใส่คำนำหน้า → 50 ทวิ + ใบกำกับพิมพ์ "44 หมู่ 9 หนองเหียง พนัสนิคม ชลบุรี 20140" ผิดข้อกำหนดเอกสารราชการ. ตอนนี้ทุกตัว delegate มาที่นี่หมด. ความสามารถ: เติมคำนำหน้า · กทม.→แขวง/เขต + ไม่มี "จ." · parse free-text ที่ไม่มีคำนำหน้ากลับเป็น structured · **กู้จังหวัดจากรหัสไปรษณีย์** ผ่าน `ThaiAdminCodes` เมื่อที่อยู่ไม่ได้เขียนชื่อจังหวัดไว้เลย (ลอง token รองสุดท้าย = ตำบลก่อน — อำเภอที่มีตำบลชื่อเดียวกันจะไม่กลืนชื่อตำบลจริง) · กันพิมพ์ตำบล/จังหวัดซ้ำ · ยุบ "กทม กรุงเทพมหานคร". เทสต์: `Accounting.Tests/ThaiAddressFormatterTests.cs` |
| **รหัส/ป้ายสาขาบนเอกสาร (§86/4 · ประกาศอธิบดีฯ ฉบับที่ 199)** | `Helpers/TaxBranchCode.cs` — **resolver กลางตัวเดียว**: `TryNormalize` (เติมศูนย์ให้ครบ 5 หลัก · ปฏิเสธรูปแบบผิดพร้อมข้อความไทยที่อ้างประกาศฯ) · `Label`/`LabelWithName` → "สำนักงานใหญ่" / **"สาขาที่ 00008" (5 หลัก — คำตัดสินเจ้าของ #21 รอบ 193 · เดิมตัดศูนย์นำเป็น "สาขาที่ 8")** (ไทย) หรือ "Head Office" / "Branch 00008" · `DocumentLabels.Branch` ใช้ `TaxBranchCode.IsHeadOffice/Normalize` ตัวเดียวกัน (เทสต์ล็อกว่าคำตรงกับ resolver) · สอง renderer + PDF/A-3 + 50 ทวิ + `DocumentIssuerBranch` เดินตัวนี้ ⇒ เอกสารเก่าพิมพ์ใหม่เป็น 5 หลัก (ไฟล์ PDF/A-3 ที่ออกไปแล้วไม่เปลี่ยน) · `dimensions.html` (หน้าจอทะเบียนสาขา) ยังแสดง "00008 สาขาที่ 8". **ที่มา**: สูตร `code == "00000" ? ... : ...` เคยเขียนซ้ำ 3 ที่แล้วผลไม่ตรงกัน — `PdfGenerationService.PdfA3` (ทั้งฝั่งผู้ขายและผู้ซื้อ) พิมพ์ **เลขดิบ** `00003` แทนถ้อยคำที่ประกาศฯ กำหนด และ `DocumentBrandController.FormatBranchLabel` พิมพ์ "สาขาที่ 00003" (ศูนย์นำหน้าติดมา) ⇒ ใบกำกับภาษีระบุสถานประกอบการไม่ตรงแบบ. รหัสที่แปลงเป็นเลขไม่ได้ (ข้อมูลเก่าเพี้ยน) โชว์ตามที่เก็บไว้ — **ห้ามกลบเป็น "สำนักงานใหญ่"** เพราะจะรายงานภาษีผิดสาขา. ทะเบียนสาขา (`DimensionalAccountingService`) ใช้ตัวเดียวกันเป็นด่านตอนบันทึก. เทสต์: `Accounting.Tests/TaxBranchCodeTests.cs` |
| **ภาษีถูกหัก ณ ที่จ่าย → เครดิต ภ.ง.ด.50/51** | ✅ **สร้างแล้วครบ 5 เฟส** — ดู `WHT_CREDIT_PLAN.md`. ฝั่ง "เราหักคนอื่น" (ใบสำคัญจ่าย → 50 ทวิ ที่เราออก → ภ.ง.ด.3/53) ✅ เดิมมีอยู่แล้ว; ฝั่ง **"เราถูกหัก"** เพิ่มใหม่: ทะเบียน `WhtCreditReceived` (ตาราง `WhtCreditsReceived`) ← `DocumentService.SyncWhtCreditReceivedAsync` **อ่านจาก GL 11910 จริง** (ไม่ใช่จากช่องบนฟอร์ม — ยอดในทะเบียนกับสมุดบัญชีจึงไม่มีทางแตกกัน) · `WhtCreditService` + `WhtCreditController` (`/api/companies/{cid}/wht-credits`) + หน้า `wht-credit.html` แนบสแกนหนังสือรับรอง · `GenerateCitReport` **หักเครดิตจริงแล้ว** โดยนับเฉพาะ Received/Claimed — **Pending (ยังไม่ได้ใบ) แสดงแต่ `TaxAmount = 0`** ตาม L1 (ไม่มีใบ = เครดิตไม่ได้ตามกฎหมาย) · `SummaryAsync` กระทบทะเบียน↔ยอด 11910 ในงบ แล้วรายงานผลต่าง (ต่าง = มีรายการลงบัญชีแต่ไม่อยู่ในทะเบียน ต้องตามให้เจอก่อนยื่น) · `SettleYearEndAsync` (`POST /wht-credits/settle-year-end`) ลง JE ปิดปีล้าง 11910 ตามที่ใช้จริง (ใช้หักภาษี/ขอคืน/ตัดสูญ) มี guard ห้ามล้างเกินยอดคงเหลือ; **ยกไปปีหน้า = ไม่ต้องเรียก** (คงยอดไว้เฉย ๆ) · `CheckRate` เตือนเมื่อผู้จ่ายหักผิดอัตรา ท.ป.4/2528 (รวมอัตราพิเศษ: โฆษณา 2% · ขนส่ง 1% · เงินปันผล 40(4)(ข) 10% — ต้องตรวจ**ก่อน** 40(8) มิฉะนั้นถูกกลืนแล้วเตือนผิด) · **OCR สแกน 50 ทวิ** → `EnsureWhtCreditFromCertAsync` สร้างแถวทะเบียนสถานะ Received ให้เลย (มีใบจริงอยู่ในมือแล้ว) พร้อม `InferIncomeTypeCode` อ่านประเภทเงินได้จากกระดาษเพื่อป้อนให้ `CheckRate` — **คืน null เมื่อเจอหลายประเภทพร้อมกัน** เพราะแบบ 50 ทวิ พิมพ์สำเร็จมีหัวข้อ 1–6 ครบอยู่บนฟอร์มเปล่า (จับคำตรง ๆ = เจอทุกประเภท) และ **ห้ามเดาประเภทจากอัตราที่หัก** เพราะจะทำให้ CheckRate ตรวจกับตัวเองแล้วผ่านทุกครั้ง = ปิดตัวตรวจโดยไม่รู้ตัว |
| **เลขที่ใบกำกับที่อ้างอิง (ฝั่งซื้อ)** | ใบกำกับตัวจริงของฝั่งซื้อเป็น**เอกสารของผู้ขาย** ⇒ เลขที่/วันที่ที่มีผลทางภาษี คือ `SupplierInvoiceNumber`/`SupplierTaxInvoiceDate` **ไม่ใช่** `DocumentNumber` (เลขรันภายในเรา ที่สรรพากร/ผู้ขายไม่รู้จัก). รายงาน ภ.พ.30 ฝั่งซื้อใช้ถูกอยู่แล้ว (`TaxService` enrich) แต่ **กล่องอ้างอิง §86/9-10 บนกระดาษเคยใช้เลขภายในของเรา** — แก้แล้วให้ใช้เลขผู้ขาย เมื่อใบต้นทางเป็นชนิดฝั่งซื้อ (`IsPurchaseSideDocType`) และตกกลับเลขภายในเมื่อไม่มีเลขผู้ขาย (พฤติกรรมเดียวกับรายงาน — ไม่ให้ 2 ที่ขัดกัน). เพิ่มบรรทัด "เลขที่ใบกำกับภาษีของผู้ขาย" บนเอกสารฝั่งซื้อที่พิมพ์ออกมาด้วย (เดิมมีแต่ในหน้ารายละเอียด) |
| **เอกสารภาษาอังกฤษ — HTML renderer เคยฝังคำไทยตาย 20 จุด** | `DocumentLabels` มีคำครบทั้งไทย/อังกฤษ (75 key) และ **QuestPDF ใช้ครบ** แต่ **HTML renderer (เส้นหลัก: Chromium HTML→PDF + print preview) ฝังข้อความไทยไว้ตรง ๆ** ⇒ ตั้งภาษาอังกฤษแล้ว เอกสารออกมาปนไทย และ**หน้าตาต่างกันตามว่าเรนเดอร์ด้วยตัวไหน** (Chromium เปิด = ปนไทย · fallback QuestPDF = อังกฤษถูก) — defect class "สอง renderer drift" ที่โปรเจกต์เจอซ้ำ. จุดที่หลุด: ลายน้ำยกเลิก · ป้ายต้นฉบับ/สำเนา · เลขผู้เสียภาษี/โทร (ทั้งผู้ขายและผู้ซื้อ) · กล่องอ้างอิง CN/DN (มูลค่าเดิม/ที่ถูกต้อง/ผลต่าง/ลด/เพิ่ม) · หมายเหตุ "ไม่ใช่ใบกำกับภาษี" · บล็อกใบรับรองแทนใบเสร็จทั้งชุด · ข้อมูลชำระเงิน · เงื่อนไขการชำระเงิน + "เครดิต N วัน". แก้ให้ทุกจุดใช้ `L.*` แล้ว + เพิ่ม 2 label ที่ยังไม่มี (`payment_terms` และ `CreditDaysText(days)` ซึ่งต้องเป็นเมธอดเพราะรูปประโยคสลับที่: ไทย "เครดิต 30 วัน" / อังกฤษ "30 days credit"). **กันซ้ำ**: `DocumentLabels.Keys` + เทสต์ใน `DocumentLabelsTests` — key ไทย/อังกฤษต้องเท่ากัน · ฝั่งอังกฤษห้ามมีอักษรไทย (U+0E00–U+0E7F) · ห้ามมี key ที่ indexer คืนชื่อ key กลับมา · `LegalTitle` ต้องยังคงคำไทยไว้ตาม §86/4. **แบบฟอร์มราชการ (50 ทวิ ฯลฯ) คงเป็นไทยตามแบบ** ไม่แตะ. **รอบตรวจศัพท์ (audit ครั้งที่ 2 — "คำไหนแปลผิดบ้าง")**: เทสต์เดิมตรวจได้แค่ "ครบ + ไม่ปนภาษาไทย" ตรวจ**ความถูกต้องของศัพท์**ไม่ได้เลย. ไล่ทีละคำทั้ง 75 key + ชื่อเอกสาร 16 ชนิด + ป้ายลายเซ็น พบ 2 คำผิดจริง: `total_bill_discount` = "Bill discount" (ศัพท์การเงิน = **การขายลดตั๋วเงิน** คนละเรื่องกับส่วนลดท้ายบิล → "Invoice discount") และ `dn_reason_adjustment` = "under-calculated" (**ไม่ใช่คำอังกฤษจริง** → "Correction (amount undercharged)"); อีก 5 จุดแปลถูกแต่ตกสาระ: เหตุผล CN/DN ที่ย่อจนสรรพากรตรวจย้อนไม่ได้ ("Adjustment"/"Write-off" โดด ๆ ทั้งที่ไทยระบุ "ค่าสินค้าน้อยกว่าที่ตกลง"/"บางส่วน"), `our_doc_ref` "Our document" → "Our ref.", ป้ายลายเซ็น "Authorized" → "Authorized Signature" (6 จุด), หัวใบ Expense → "Expense Record". **กันซ้ำเพิ่ม**: `DocumentLabelsTests` มีตาราง `BannedEnglishTerms` (คำที่พิสูจน์แล้วว่าผิด + เหตุผลกำกับ) และเทสต์ว่าเหตุผล CN/DN ต้องไม่เป็นคำเดี่ยวกว้าง ๆ |
| **ออกเอกสารเป็นอังกฤษ "เฉพาะใบเดียว"** | resolver กลางรองรับ 4 ชั้นมาตั้งแต่ต้น แต่ **UI เปิดให้ตั้งได้ชั้นเดียว (ทั้งบริษัท)** ⇒ คำถามที่ผู้ใช้ถามจริง ("อยากออกใบเดียวเป็นอังกฤษ") ทำไม่ได้เลย นอกจากยิง API เอง. อันตรายกว่านั้นคือทางแก้ที่ผู้ใช้จะคิดเอง — สลับค่าบริษัทเป็น en แล้วสลับกลับ — **ทำให้ใบเก่าทุกใบที่ `DocumentLanguage=null` เปลี่ยนภาษาตามระหว่างนั้น** เพราะภาษาถูก resolve ตอนพิมพ์ ไม่ได้ freeze ไว้ตอนออกใบ. เติมทางเข้า 2 ชั้นที่ขาด: **รายใบ** (`#fDocumentLanguage` ในฟอร์มสร้าง/แก้ — ตรึงถาวรกับใบ) และ **ครั้งนี้ครั้งเดียว** (`#pdfLangMode` ใน modal พรีวิว → `GeneratePdfRequest.Language` — ใช้กับใบที่อนุมัติแล้วแก้ไม่ได้ ซึ่งเป็นเคสจริงที่พบบ่อยกว่า: ลูกค้าขอฉบับอังกฤษหลังออกใบไปแล้ว) ต่อครบทั้ง 4 ทางออกของหน้าเอกสาร + reset ทุกครั้งที่เปิด modal/ฟอร์มใหม่ (ไม่งั้น "en" ค้างข้ามใบ → ลูกค้าไทยรายถัดไปได้ใบอังกฤษเงียบ ๆ). **บั๊กที่เจอระหว่างต่อสาย**: `DocumentResponse` ไม่เคยคืน `DocumentLanguage` ⇒ เปิดแก้ใบอังกฤษแล้วกดบันทึก ภาษาถูกล้างกลับเป็นค่าบริษัท และ `GenerateDocumentPdfAsync` คำนวณภาษาของ **metadata Title ใน PDF/A-3** เอง (`request.Language ?? template.Language ?? "th"`) โดยข้ามทั้งภาษาที่ตรึงกับใบและค่าบริษัท ⇒ ไฟล์ e-Tax ใบเดียวมีเนื้อหาอังกฤษแต่ Title ไทย — แก้ให้เรียก `ResolveDocumentLanguage` ตัวเดียวกับ renderer (กฎ "resolver กลาง ห้ามคำนวณเอง"). **รอบตาม (audit ทางเข้าอนุพันธ์)**: ภาษาที่ตรึงกับใบต้องไหลตามใบลูกทุกทาง ไม่งั้นลูกค้าต่างชาติได้ QT อังกฤษแต่ INV/REC กลับเป็นไทยเงียบ ๆ — เติม 5 จุด: (1) `ConvertCoreAsync` propagation block (QT→INV→REC ทั้งสาย) (2) `DocumentCloneController` ทั้ง POST + preview (ลูกค้าประจำที่ออกบิลซ้ำคือกลุ่มที่ใช้ clone บ่อยสุด) (3) ใบเสร็จ settlement อัตโนมัติ (`IsSettlementReceipt`) ตามภาษาใบกำกับต้นทาง (4) CN คืนมัดจำ ตามภาษาใบเสร็จมัดจำ (5) recurring generator อ่าน `documentLanguage` จาก `TemplateData` (forward-compatible — template เก่าไม่มี key = null = ตามค่าบริษัท เหมือนเดิมเป๊ะ; ฟอร์ม recurring ยังไม่มีช่องให้ตั้ง). **ข้ามโดยตั้งใจ**: CrossTenantWorkflow (B2B ภายในระบบ ไทยทั้งคู่) · shadow doc ตรวจงบ PO (ไม่ persist) · เอกสารจาก OCR/Import/Integration/CMS/POS (ไม่มีใบต้นทางให้สืบภาษา → ตามค่าบริษัท). ทางออก server ตรวจแล้วส่ง `Language=null` ทุกตัว (อีเมลแนบ PDF อัตโนมัติ · portal ลูกค้า · บิล SaaS · mobile-receipt) = resolver หยิบภาษาที่ตรึงกับใบให้เองถูกอยู่แล้ว |
| **เครดิต NextAcc ท้ายเอกสาร (เฉพาะบัญชีแพ็กเกจฟรี)** | ข้อความจาง ๆ 2 บรรทัดมุมขวาล่างทุกหน้า: "จัดทำด้วย **NextAcc** · ระบบบัญชีออนไลน์ / เริ่มใช้ฟรีที่ www.nextacc.net" (7–7.5pt สีเทา ไม่แย่งสายตาจากเนื้อหา). **เกณฑ์ตัดสิน `PdfGenerationService.IsFreeTierAsync` — เกณฑ์เดียว: ราคารายเดือนของ*แพ็กเกจ* = 0** (ครอบทั้งทดลองใช้และแพ็กเกจฟรีตลอดชีพ). บริษัทใต้ License ของผู้ใช้ → ใช้แพ็กเกจของ License (`AccountSubscription.PlanTemplate.MonthlyPrice`); ไม่งั้นดู `PlanTemplate` ตาม `Subscription.Plan`. **ตั้งใจไม่ไปดูว่าบริษัทนี้เคยจ่ายเงินจริงไหม / ธง `IsPermanentFree` บนแถว subscription เป็นอะไร** — ราคาแพ็กเกจบอกครบแล้ว เป็นค่าที่ admin ตั้งเองในหน้าจัดการแพ็กเกจ และไม่เพี้ยนตามประวัติการจ่ายเงินรายบริษัท (ธงบนแถวเคยค้างผิดบนแพ็กเกจเสียเงินมาแล้ว — บั๊ก sync รอบ 10). หาแพ็กเกจไม่เจอ/อ่านข้อมูลไม่ได้ → **ไม่พิมพ์** (ยกเว้นแพ็กเกจ `FreeTrial` ที่ถือว่าฟรีเสมอแม้ไม่มี template) — พลาดฝั่ง "ไม่โฆษณา" ปลอดภัยกว่าพลาดฝั่ง "โฆษณาใส่คนจ่ายเงิน". **ต่อสายครบทุกทางออก** — คิดค่าครั้งเดียวใน `GenerateDocumentPdfAsync` แล้วส่งเข้า: e-Tax PDF/A-3 · Chromium HTML→PDF · QuestPDF · เส้นสำรอง HTML→QuestPDF · print preview (`GenerateDocumentHtmlAsync`) · พรีวิวเทมเพลตทั้ง 3 ทาง — ถ้าลืมเส้นใดเส้นหนึ่ง เอกสารจะมี/ไม่มีเครดิตไม่เหมือนกันแล้วแต่ว่าออกทางไหน. **ไม่แปะบนแบบฟอร์มราชการ** (50 ทวิ / ภ.พ.30 ฯลฯ) — เป็นแบบของกรมสรรพากร ห้ามเติมข้อความของเราเอง |
| **OCR ราคารวม VAT (Case A) — `Line.Amount` ต้องเป็นยอดก่อน VAT** | ใบที่ราคา/หน่วยรวม VAT แล้ว (IKEA, ห้างค้าปลีก) ตัวตรวจ Case A ตั้ง `PricesIncludeVat=true` ถูกแล้ว แต่ **เก็บ `Line.Amount` เป็นยอดรวม VAT ตรง ๆ** ขณะที่ `Document.SubTotal` เก็บยอด net จากหัวกระดาษ ⇒ สองค่าขัดกันในใบเดียว (Σ Line.Amount = 1,396 แต่ SubTotal = 1,304.68) และตอนอนุมัติ JE ฝั่งซื้อลง `Dr ค่าใช้จ่าย = Σ Line.Amount` (ซึ่งรวม VAT อยู่แล้ว) **+ `Dr ภาษีซื้อ` อีกรอบ** เทียบกับ `Cr เจ้าหนี้ = TotalAmount` ⇒ **เดบิตเกินเครดิตเท่ายอด VAT พอดี** → "การบันทึกบัญชีอัตโนมัติไม่สมดุล" (เคสจริง: Dr 1,487.32 ≠ Cr 1,396.00 · ผลต่าง 91.32 = VAT). แก้ให้เก็บ `Amount = ยอดบรรทัด − VAT ของบรรทัด` ตาม convention เดียวกับ `DocumentService.ComputeLineAmounts` (`Amount = NetAmount` · `UnitPrice` คงเป็นราคารวม VAT). **ยังยึด VAT บนกระดาษเป็นหลัก** (pro-rate ตามสัดส่วน บรรทัดท้ายรับเศษ) ไม่คิดใหม่จากอัตรา 7% รายบรรทัด เพราะยอด VAT ต้องกระทบกับที่ผู้ขายยื่นใน ภ.พ.30 ได้ — คิดจากอัตราจะต่างกระดาษได้ 0.01 บาท. **ใบเก่าที่สร้างไปแล้ว**: เปิดแก้ไขแล้วกดบันทึก → `UpdateDocumentAsync` คำนวณใหม่ผ่าน `ComputeLineAmounts` → ยอดกลับมาสมดุลเอง |
| **คำเตือนหัก ณ ที่จ่าย ไม่ควรยิงใส่ใบซื้อสินค้า** | §3 เตรส ใช้กับ **ค่าบริการ/ค่าเช่า/ขนส่ง/โฆษณา/จ้างทำของ** — **การซื้อสินค้าไม่ต้องหัก**. เดิมเตือนทุกใบฝั่งซื้อที่ยอด ≥ 1,000 โดยไม่ดูว่าซื้ออะไร ⇒ เตือนผิดแทบทุกใบซื้อของ (เคสจริง: ซื้อปลอกหมอนจาก IKEA) ⇒ ผู้ใช้ชินกับการกดข้ามคำเตือน แล้ววันที่เตือนถูกจริงก็ข้ามไปด้วย. เพิ่ม `IsPureGoodsPurchaseAsync` — เงียบเมื่อ **ทุกบรรทัดที่มียอด** ผูกสินค้าที่ตัดสต๊อก (`Product.TrackStock`) หรือลงผังสินค้าคงเหลือ/ต้นทุนสินค้า (115x / 51xxx); บรรทัดยอด 0 (ของแถม/บริการฟรี) ไม่นับ. **สัญญาณไม่พอ = เตือนตามเดิม** (ไม่เดาจากคำในรายการ) — พลาดฝั่งเตือนเกินดีกว่าพลาดฝั่งไม่เตือนตอนต้องหักจริง ซึ่งบริษัทต้องรับผิดภาษีแทนผู้รับเงิน. ข้อความแก้ให้บอกทั้งสองทาง (เป็นบริการ→ต้องหัก / ซื้อสินค้า→ข้ามได้) |
| **ใบเสร็จตัดลูกหนี้ ต้องตัดที่ยอด "ก่อนหักภาษี" (เกณฑ์ Cash)** | `CompanySettings.WhtRecognitionBasis` **ค่าเริ่มต้น = Cash** (ยังไม่เคยมี UI ให้ดู/เปลี่ยน — เพิ่มแล้วที่ ตั้งค่า → ภาษี). ใต้เกณฑ์นี้ใบแจ้งหนี้ตั้งลูกหนี้ไว้ **gross**: `Dr AR (TotalAmount + WHT) / Cr รายได้` โดย**ยังไม่แตะ 11910** (`arAmountAtInvoice` ใน `AutoPostToJournalAsync`) ⇒ ใบเสร็จต้องตัด AR ที่ยอด gross = `เงินสด + Dr 11910`. **บั๊กที่เจอ**: `onReceiptSourceSelect` เติมบรรทัดด้วย `balanceDue` ซึ่งเป็นยอด **net** (`TotalAmount = subTotal + VAT − WHT`) และ WHT 0% ⇒ JE ได้ `Dr เงินสด 3,492 / Cr AR 3,492` ขณะที่ AR ตั้งไว้ 3,600 — **เหลือลูกหนี้ค้าง 108 ถาวร** ทั้งที่เอกสารขึ้นว่าชำระครบ (`BalanceDue` คิดจาก TotalAmount ที่หัก WHT ไปแล้ว ⇒ งบดุลกับสถานะเอกสารขัดกันเงียบ ๆ) และ **11910 ไม่เคยถูก Dr** ⇒ ไม่มีแถวใน `WhtCreditReceived` (ตัวมันอ่านจาก GL) ⇒ ใช้เครดิตใน ภ.ง.ด.50 ไม่ได้ = เสียเงินจริง. guard เดิม (`WHT รวมเกินยอดต้นทาง`) จับไม่ได้เพราะกันแค่ "หักเกิน" ไม่ได้กัน "หักขาด". **แก้ 3 ชั้น**: (1) ตัวเติมอ่านเกณฑ์จาก settings — Cash → ยอด gross + อัตรา WHT ของใบต้นทาง (เฉลี่ยตามสัดส่วนที่ยังค้าง เหมือน `recordPayment` ที่ทำถูกอยู่แล้ว), Accrual → net + 0 (เดิมถูกอยู่แล้ว); (2) **guard ตอนอนุมัติ**: ใบเสร็จที่ปิดยอดต้นทางจนหมดแต่ WHT สะสมไม่ครบ → บล็อกพร้อมบอกยอดที่ขาดและวิธีแก้ (หรือให้แก้ WHT ที่ใบต้นทางเป็น 0 ถ้าลูกค้าไม่ได้หักจริง); (3) **ตรวจย้อนหลัง** `FindStrandedArFromMissingWhtAsync` + `GET /wht-credits/stranded-ar` → หน้าทะเบียน WHT ขึ้นแบนเนอร์รายใบที่ค้าง. หมายเหตุ: ทางเดิน `บันทึกชำระเงิน` (`recordPayment` → `CreatePaymentAsync`) คำนวณ WHT ต่องวดถูกมาตั้งแต่ต้น — เฉพาะทาง "แปลง/สร้างใบเสร็จอ้างใบค้าง" ที่พลาด |
| **ใบต้นทางที่ผูกไว้ ต้องแสดงตอนเปิดแก้ไข** | เปิดแก้ใบเสร็จที่แปลงมาจากใบแจ้งหนี้ แล้วช่อง "รับชำระใบค้างของลูกค้ารายนี้ (ตัดลูกหนี้)" โชว์ **"— ใบเสร็จอิสระ (ขายสด: ลงรายได้ + VAT) —"** ทั้งที่ใบนี้ตัดลูกหนี้อยู่ ⇒ หน้าจอ**บอกวิธีลงบัญชีผิดจากความจริง** (คนอ่านเข้าใจว่าลงรายได้ใหม่). สาเหตุ 3 ชั้นซ้อน: (1) `openEdit` ไม่เคย hydrate ค่านี้จาก `relatedDocumentId` (2) ตัวโหลดลิสต์กรองเฉพาะใบที่ **`balanceDue > 0`** — พอใบเสร็จถูกอนุมัติ ใบต้นทางกลายเป็นชำระครบจึงหลุดจากลิสต์ ต่อให้ set ค่าก็ไม่มี option ให้เลือก (3) ตัวโหลดเป็น async และล้าง `<select>` ทุกครั้ง ⇒ ค่าที่เซ็ตไว้ก่อนถูกล้างทิ้ง. แก้ด้วยการ **ปักหมุด** (`_pinnedSourceId`/`_pinnedSourceDoc` เซ็ตใน `openEdit` ก่อนตัวโหลดใดจะเริ่ม) แล้ว `_applyPinnedSource` เติม option กลับ + เลือกให้ **ท้ายตัวโหลดทั้งสองตัว นอก try** (ใบที่ผูกไว้ต้องแสดงแม้โหลดลิสต์ล้มเหลว) — ใช้ร่วมกันทั้งใบเสร็จ-ตัดลูกหนี้ และ CN/DN §86/9-10. **ล็อกไม่ให้เปลี่ยนตอนแก้ไข**: `UpdateDocumentAsync` ไม่รับ `RelatedDocumentId` (รับเฉพาะตอนสร้าง) และ `save()` ก็ส่งเฉพาะตอนสร้างอยู่แล้ว ⇒ เดิมเลือกเปลี่ยนได้ แต่กดบันทึกแล้ว**ไม่มีผลอะไรเลยแบบเงียบ ๆ** — ตอนนี้ล็อก + บอกเหตุผลและทางออก (ยกเลิกแล้วสร้างใหม่จากใบต้นทางที่ถูก). radio ฝั่ง CN/DN ก็ล็อกตามใบที่ผูกไว้ด้วย |
| **§90/2 บริษัทไม่จด VAT — ทุกทางที่ไปถึง "ใบกำกับภาษี" ต้องปิดครบ** | ผู้ไม่จดทะเบียนออกใบกำกับภาษีไม่ได้เลย (มีโทษ §90/2). เดิมบล็อกจริงแค่ **2 ทาง** คือ ตัวเลือกในฟอร์มสร้าง (`applyVatRegistrationLock`) + hard block ตอน `ApproveDocumentAsync` — ส่วนทางอื่นเปิดโล่ง: **แปลงเอกสาร** (กล่อง "แปลงเอกสาร" อ่านรายการจาก `ValidConversions` ซึ่งไม่รู้จักสถานะ VAT), **แปลงทั้งหมด (batch)**, และติ๊ก **"ออกเป็นใบแจ้งหนี้/ใบกำกับภาษี ใบเดียว"** (force type=TaxInvoice ตอนบันทึก) ⇒ ผู้ใช้แปลง/ติ๊กได้ กรอกจนครบ แล้วไปตายตอนกดอนุมัติ. ตอนนี้ปิดครบทุกทาง: `ValidateConversionAsync` บล็อก targetType=TaxInvoice (ชั้นบังคับจริง — ครอบ API/integration ด้วย) · `GetValidConversionTargetsAsync` กรองออกจากรายการที่ส่งให้ UI (`GET /conversion-targets`) · client กรองซ้ำในกล่องแปลง (เผื่อ backend เก่า) · batch dropdown ถอดตัวเลือกออก · ติ๊กใบเดียวซ่อนเมื่อไม่จด VAT. **กติกาค่าเริ่มต้น (แก้รอบ 193 · S-01)**: ~~`CompanySettings.VatRegistered` ไม่มีค่า = ถือว่าจด (`?? true`)~~ — ตอนนี้ **ไม่มีแถวค่าตั้ง = ธงบริษัท** (`CompanyVatStatus.IsRegistered`) · แถวค่าตั้งเกิดพร้อมบริษัทด้วยธงของบริษัท (`CompanySettingsFactory`) ⇒ บริษัทที่ไม่ติ๊กจด VAT ในวิซาร์ดไม่ถูกพลิกเป็น "จด" ตอนบันทึกหน้าตั้งค่าครั้งแรก · เข้มขึ้น: บริษัทไม่มีแถว + ธงบริษัท = ไม่จด ⇒ บล็อก (ทางไปต่อ: ติ๊ก "จด VAT") · ยังไม่เคยยืนยันสถานะ VAT (`VatStatusConfirmedAt` ว่าง) ⇒ แถบเตือนบน documents/dashboard. ข้อความ error บอกทางออกเสมอ (แปลงเป็นใบแจ้งหนี้/ใบเสร็จแทน · เปิดที่ ตั้งค่า → ภาษี) |
| **"เทมเพลตเริ่มต้นของชนิดนี้" ต้องเป็นใบเดียวกันทุกที่** | มี 3 ที่ที่ต้องชี้ไปใบเดียวกัน: ปุ่ม "⚡ ตั้งค่าเริ่มต้น" ในฟอร์มสร้างเอกสาร · หน้า `document-templates.html` · ตัวเรนเดอร์ PDF. ความจริงอยู่ที่ server — `DocumentTemplateService.GetDefaultTemplateAsync` = **`IsDefault && IsActive`** ไม่เจอ = สร้างใหม่. แต่หน้าเทมเพลตเคย**เดาเอง**ด้วย `list.find(isDefault) || list[0]` (ไม่ดู isActive + ตกมาที่ใบแรก) ⇒ เมื่อเทมเพลตเริ่มต้นถูกปิดใช้งาน หรือยังไม่มีใบ default เลย สองฝั่งไปคนละใบ: ตั้งเงื่อนไขชำระเงินจากหน้าสร้างเอกสารแล้วมาเปิดหน้าตั้งค่ากลับไม่เห็นค่า (แก้คนละใบกันอยู่). แก้: `openEditorForType` เรียก `GET /document-templates/default/{docType}` ให้ server ตัดสิน (สร้างให้ถ้าไม่มี) แล้ว sync ลิสต์ในหน้า; `_defaultTemplateFor` (ใช้โชว์การ์ด/พรีวิว) ใช้กติกาเดียวกับ server และคืน null เมื่อไม่มีจริง ๆ แทนการหยิบใบอื่นมาแทน. **ทิศทางบันทึกก็มีบั๊กคู่กัน**: หน้าเทมเพลตส่ง `defaultPaymentTerms: get(...) || null` — แต่ server ใช้ convention "null = ไม่ได้ส่งมา คงค่าเดิม" ⇒ ลบข้อความทิ้งแล้วกดบันทึก **ล้างค่าไม่ได้เลย** ค่าเก่ายังอยู่ (อ่านแล้วเหมือนบันทึกไม่ติด) → ส่ง `""` / `0` ตอนว่างแทน |
| **ลำดับที่มาของเทอมการชำระเงิน (4 ชั้น)** | ค่าวันเครดิต + ข้อความเงื่อนไข มาจาก 4 ชั้น: **ผู้ใช้พิมพ์เอง > คู่ค้า (`Contact.PaymentDueDays`/`PaymentTerms`) > เทมเพลตชนิดเอกสาร > AI แนะนำจาก history**. เดิมทุกชั้นใช้กติกา "เติมเฉพาะตอนช่องว่าง" ซึ่งกลายเป็น **ใครมาถึงก่อนชนะ** เพราะทุกชั้นเป็น async ⇒ (1) AI ตอบเร็วกว่า getContact ก็ชนะค่าที่ตั้งไว้กับลูกค้า (ผลไม่แน่นอน เปิดใบเดิม 2 ครั้งได้คนละค่า) (2) ข้อความเงื่อนไขของเทมเพลตถูกเติมตอนเปิดฟอร์ม จึงชนะของลูกค้าเสมอ. ตอนนี้ทุกชั้น**ประทับที่มาลง `dataset.autoSrc` แล้วเทียบลำดับจริง** (`_canAutoFill` + `_srcRank`) — ผลเหมือนกันเสมอไม่ว่าใครตอบก่อน; ค่าที่มีอยู่แต่**ไม่รู้ที่มา** (hydrate จากเอกสารเดิม / OCR handoff) ถือเป็นของผู้ใช้ ห้ามทับ. **ข้อความบนกระดาษต้องไม่ขัดกับวันครบกำหนด**: เทมเพลต "ชำระภายใน 30 วัน" + ลูกค้าเครดิต 45 เดิมได้ใบที่คิดครบกำหนด 45 วัน แต่พิมพ์ 30 วัน (ขัดกันเองในใบเดียว) → `_applyContactPaymentTermsText`: ลูกค้ามีข้อความของตัวเอง = ใช้ทั้งชุด · มีแต่จำนวนวัน = แก้เฉพาะ**ตัวเลขวัน**ในบรรทัดที่มีคำเกี่ยวกับการชำระ (เทียบก้อนตัวเลขเต็มค่า ไม่ใช้ lookbehind — Safari เก่า throw; "รับประกัน 30 วัน" และ "ปรับ 300 บาท" จึงไม่โดนแก้) · เทมเพลตไม่ได้อ้างจำนวนวัน = ไม่มีข้อขัดแย้ง ปล่อยไว้ครบ. **ครอบทั้ง 2 ฝั่ง**: เดิมเทอมของคู่ค้าถูกเติมเฉพาะฝั่งขาย (อยู่ใน `maybePreselectTaxInvoice`) ⇒ ฝั่งซื้อมีแต่ AI เดาให้ ทั้งที่เครดิตที่ผู้ขายให้ ถูกตั้งไว้ชัดเจน — แยกเป็น `applyContactCreditTerms` เรียกจาก onContactChange (ทุกชนิด) + onDocTypeChange (เคสเลือกคู่ค้าตอนอยู่บนชนิดที่ซ่อนช่องเครดิต แล้วค่อยสลับมาใบเสนอราคา — เดิมค่าลูกค้าหายไปเลย). **ชั้น server**: `CreateDocumentAsync` เติม `CreditDays`/`PaymentTerms` จาก Contact เมื่อ caller ไม่ได้ส่งมา (เดิมตรรกะอยู่บนหน้าจออย่างเดียว ⇒ เอกสารจาก API/integration/recurring ไม่ได้เทอมของคู่ค้าเลย — ใบเดียวกันสร้างคนละทางได้คนละเทอม) |
| **ช่องกรอกต่อชนิดเอกสาร (field profile)** | ฟอร์มสร้าง/แก้เอกสารเลิกเป็น "ฟอร์มเดียวใช้ทุกชนิด" — `documents.html _docFieldProfile(docType)` เป็น single source of truth ว่าชนิดไหนมีช่องอะไร: **วันเครดิต+เงื่อนไขการชำระ** เฉพาะใบที่ก่อหนี้ใหม่รอเก็บ/รอจ่าย (QT/INV/TaxInv/วางบิล/DN/PO/PI/Expense); ใบที่เงินเคลื่อนแล้ว (ใบเสร็จ/ใบสำคัญรับ/มัดจำ/PV/ใบแทนใบเสร็จ) และใบปรับหนี้เดิม (ใบลดหนี้) **ซ่อน+ล้างค่า** (เคสจริง: ใบลดหนี้โชว์ "วันเครดิต 30" เพราะค่าค้างจากฟอร์ม Invoice รั่วข้ามชนิด). **ช่องวันที่ตัวที่สอง** เปลี่ยนป้ายตามชนิด: ใบเสนอราคา = "ยืนราคาถึงวันที่" (ยังไม่มีหนี้ ไม่มีคำว่าครบกำหนดชำระ) · PO = "กำหนดส่งมอบ" · PR = "วันที่ต้องการรับของ" — ป้ายเดียวกันทั้ง 3 ชั้น (ฟอร์ม / detail modal / PDF ผ่าน `DocumentLabels.DueDateFor(docType)` ทั้ง HTML+QuestPDF 5 จุด). **เลขจอง (Booking)** เฉพาะสายขาย (มัดจำ→ใบสุดท้าย→ใบเสร็จ) — CN/DN ผูกผ่านใบต้นทาง §86/9-10 อยู่แล้ว, ฝั่งซื้อไม่มี. ตัวเติมอัตโนมัติทุกตัว (template default / เครดิตลูกค้า / AI suggest / OCR handoff) **ตรวจโปรไฟล์ก่อนเติม** — ตัวเติมเป็น async ถ้าไม่ตรวจจะยัดค่าลงช่องที่ถูกซ่อน+ล้างไปแล้ว แล้วรั่วเข้า payload. server ป้องกันซ้ำอีกชั้น: `DocumentService.NormalizeCreditTermFields` ล้างตอน create+update — จำเป็นตอน update เป็นพิเศษ เพราะ semantics "omit = คงค่าเดิม" ทำให้ client ที่ส่ง null ล้างค่าเก่าเองไม่ได้ (ใบลดหนี้เก่าที่มี CreditDays ค้าง แก้แล้วบันทึก = สะอาด). **รอบตรวจซ้ำทุกชนิด (audit ครั้งที่ 2)**: (a) **แหล่งเงิน/ช่องทางชำระ** (`pay` ใน profile) ซ่อนบนเอกสารที่ไม่มีเงินเคลื่อน (ใบเสนอราคา/PR/PO/ใบส่งของ/ใบวางบิล) — CN คงไว้เพราะเงินคืนควรออกช่องทางเดิมของใบต้นทาง (ถูกเติมอัตโนมัติ); (b) **CertInLieu ไม่มีใบกำกับโดยนิยาม** → ตัดออกจาก `supplierIssuedTypes` (ช่องเลขใบกำกับผู้ขาย+งวดเคลม), `inputVatTypes` (override ผัง VAT), tax point `vatBearing`, ปิดคอลัมน์เคลม VAT ทั้งตาราง (§82/5(1)) และบรรทัดใหม่ตั้งต้น VAT = ยกเว้น; (c) **บริการต่างประเทศ §83/6** (ภ.พ.36/ภ.ง.ด.54) เดิมโชว์ทุกชนิดรวมใบเสนอราคา → เหลือเฉพาะ PI/Expense/PV; (d) **ลำดับส่วนตามงานจริง**: เหตุผลใบลดหนี้ (§86/10 — คืนสินค้า = กระทบสต๊อก ต้องตัดสินใจทันทีหลังเลือกใบต้นทาง) และ ข้อมูลใบรับรองแทนใบเสร็จ (สาระหลักของใบ) ย้ายจากท้ายโซนการเงิน ขึ้นมาติดกล่องอ้างอิง ก่อนช่องวันที่. **เหตุผลใบเพิ่มหนี้ §86/9 (ทำแล้ว)**: เพิ่ม enum `DebitNoteReason` (ราคาเพิ่มขึ้น / ส่งสินค้าเกิน / ค่าใช้จ่ายเพิ่มเติม / ปรับยอด) คู่ขนานกับ `CreditNoteReason` §86/10 — บังคับเลือกก่อนอนุมัติ · พิมพ์ในกล่องอ้างอิงทั้ง HTML และ QuestPDF · **"ส่งสินค้าเกิน" เป็นเหตุผลเดียวที่กระทบสต๊อก** (ฝั่งขาย −1 ของออก · ฝั่งซื้อ +1 ของเข้า · อ้าง PV/ใบเสร็จ = ไม่แตะ เพราะไม่มีขาแรกให้ต่อ) สมมาตรกับ CN Return. ใบเก่าที่ยังไม่มีเหตุผล (NULL) สต๊อกนิ่งเหมือนเดิม — ไม่ย้อนกระทบข้อมูลที่ลงไปแล้ว |
| **ข้อมูลต้องแสดงครบทุกจุด (หน้าจอ ↔ กระดาษ)** | defect class ที่เจอซ้ำ: เก็บข้อมูลไว้แต่มีแค่บางหน้าจอเอามาแสดง. วิธีตรวจ: ไล่ทุก field ของ `class Document` เทียบ 4 ชั้น (Response DTO / detail modal / HTML+preview / QuestPDF). แก้แล้ว: **`CreditNoteReason`** (§86/10 บังคับระบุเหตุผลบนใบลดหนี้ — ไม่เคยพิมพ์เลยทั้งที่บังคับให้เลือกก่อนอนุมัติ) และ **`Currency`/`ExchangeRate`** (เอกสาร FX พิมพ์ตัวเลขเปล่าไม่บอกสกุลเงิน — TFRS บทที่ 19 ต้องเห็นอัตราด้วย); บรรทัดสกุลเงินวางเป็นบล็อกเดียวในสายหลัก QuestPDF (`ComposeCurrencyNote`) ไม่ยัดเข้า doc-info ที่มี 4 จุดตามเลย์เอาต์ |
| **อ้างอิงใบเดิมบน CN/DN (§86/9-10)** | กล่องอ้างอิงบนกระดาษมาจาก transient `AdjustmentOriginal*` (เซ็ตใน `ResolveServedAsReceiptAsync`): FK ใบเดิม → เลขที่+วันที่+มูลค่า; **ไม่มี FK แต่มี `Reference` → ใช้เลขนั้นเป็นเลขใบเดิม** (ใบเดิมอยู่นอกระบบ กฎหมายบังคับแค่ให้ระบุเลขที่ ไม่ได้บังคับว่าต้องอยู่ในระบบเรา) เดิมกล่องขึ้นเฉพาะตอนมี FK → ใบที่อ้างใบนอกระบบพิมพ์ออกมาไม่มีบรรทัดอ้างอิงเลย. เมื่อไม่รู้มูลค่าใบเดิม **ซ่อนแถวยอด** แทนการพิมพ์ 0.00 (ข้อมูลเท็จบนเอกสารภาษี) — ทำเหมือนกันทั้ง HTML และ QuestPDF. หน้ารายละเอียดแสดงสถานะเดียวกัน 3 แบบ (ผูกแล้ว / เลขนอกระบบ / ไม่มีเลย) |
| **กล่องอ้างอิงใบเดิม: 2 บรรทัด + เคสไม่มี VAT** | ฝั่งซื้อที่มีใบกำกับผู้ขาย พิมพ์ **2 เลข**: `AdjustmentOriginalNumber` = เลขใบกำกับผู้ขาย (ตามกฎหมาย §86/9-10) และ `AdjustmentOriginalOurNumber` = เลขเอกสารในระบบเรา (audit trail). ใบซื้อที่ **ไม่มี VAT** (ผู้ขายไม่จด VAT / ไม่มีใบกำกับ) ไม่มีเลขผู้ขายให้อ้าง → ใช้เลขในระบบ และหัวกล่องเปลี่ยนเป็น **"อ้างอิงเอกสารต้นฉบับ"** (`AdjustmentOriginalHasVat=false`) — พิมพ์ "ใบกำกับภาษีเดิม" ทั้งที่ไม่มีใบกำกับ = ข้อความเท็จบนเอกสาร. ทั้ง 3 field เป็น `[NotMapped]` (คำนวณตอน render ไม่ต้อง migration) |
| **ความสอดคล้องของฝั่ง CN/DN (3 ชั้น)** | (1) **ใบที่เลือกเป็นตัวกำหนดฝั่ง — ไม่ใช่ radio กรองลิสต์**: `_loadCnSourceDocs` โหลดเอกสารของคู่ค้า**ทั้งสองฝั่งเสมอ** แยก `optgroup` 🔵 ขาย (TaxInvoice/Invoice/Receipt-ใบกำกับในตัว) / 🟠 ซื้อ (PI/Expense/PV-standalone) โดยฝั่งของแท็บที่เปิดขึ้นก่อน; เลือกใบไหน ฝั่งใบลดหนี้/เพิ่มหนี้สลับตามใบนั้น + ล็อก radio. (ดีไซน์แรกกรองลิสต์ตาม radio → เปิดจากแท็บรายรับแล้ว "หาใบซื้อไม่เจอทั้งที่เคยเห็น" — บังคับผู้ใช้รู้จักฝั่งก่อนเห็นรายการ = ลำดับกลับหัวจากงานจริง). radio เลือกฝั่งเองใช้เฉพาะเคสใบเดิมอยู่นอกระบบ (กรอก `fCnExternalRef`); สลับ radio ไม่ reload (ลิสต์ไม่ได้กรองแล้ว). (2) **server guard ตอน create** — ฝั่ง≠ฝั่งของใบต้นทาง → ปฏิเสธ; ไม่ส่งฝั่งมาแต่มีใบต้นทาง → ยึดฝั่งใบต้นทาง (server ไม่ whitelist ชนิดใบต้นทาง — ใช้ derive ฝั่งเท่านั้น). (3) **ตรวจบทบาทคู่ค้าตามฝั่ง** — CN/DN ถูกถอดออกจาก `revenueDocTypes` ที่ตายตัวแล้ว (เดิมใบลดหนี้ซื้อกับผู้ขายที่ไม่ได้เป็นลูกค้าถูกปฏิเสธ). **ตำแหน่งในฟอร์ม**: กล่องใบอ้างอิง (ทั้ง CN/DN และใบเสร็จ-ตัดใบค้าง) ย้ายขึ้นมาต่อจาก ชนิด+ผู้ติดต่อ ทันที — เอกสารสายอ้างอิงต้องเลือกใบต้นทางก่อนช่องอื่น เพราะมันดึงค่ามาเติมเกือบทุกช่อง (บรรทัด/สกุลเงิน/แหล่งเงิน/เลขอ้างอิง/ฝั่งภาษี) |
| **CN/DN เลือกฝั่งตั้งแต่สร้าง** | เดิม CN/DN อยู่แต่ optgroup "ฝั่งรายรับ" ⇒ ใบลดหนี้ฝั่งซื้อถูกสร้างในแท็บรายรับ แล้วระบบต้องเดาฝั่ง = รากของปัญหายอดผิดฝั่งใน ภ.พ.30. ตอนนี้เลือก CN/DN ได้จากทั้ง 2 แท็บ + มี radio "ใบนี้เป็นฝั่งไหน" ในกล่องอ้างอิง (ตั้งต้นตามแท็บ, สลับอัตโนมัติเมื่อเลือกใบต้นทาง) ส่งเป็น `CnDnPurchaseSideOverride` ตั้งแต่ create (Update รับได้เฉพาะตอน Draft — หลังอนุมัติต้องใช้ปุ่ม "ย้ายฝั่ง" ที่กลับ JE ให้). เลือกใบต้นทางแล้วสืบทอด: สกุลเงิน+อัตรา · PricesIncludeVat · Project/Dimension/ExpenseCategory · เลข-วันที่-สาขาใบกำกับผู้ขาย (เติมเฉพาะช่องว่าง) |
| **ฝั่งภาษีของ CN/DN + การย้ายฝั่ง** | ลำดับตัดสินฝั่ง (ทั้ง `AutoPostToJournalAsync` และ `TaxService`): **`Document.CnDnPurchaseSideOverride` (ผู้ใช้สั่ง) → FK ใบต้นทาง → GL (ผังภาษีที่ JE ลงจริง) → บทบาทคู่ค้า (supplier-only)**. รายงาน ภ.พ.30 **ยึด GL เป็นความจริง** ⇒ ถ้า JE ลงผิดฝั่งตั้งแต่อนุมัติ การกด "สร้างรายงานใหม่" ไม่ช่วย. ทางแก้ = `ReclassifyCnDnSideAsync` (POST `{id}/reclassify-cn-side`) ซึ่ง **กลับ JE เดิมแล้วลงใหม่ให้ถูกฝั่ง** ไม่ใช่ย้ายแค่ตัวเลขในรายงาน (ย้ายแต่รายงาน = งบกับแบบยื่นขัดกันเอง). gate เดียวกับ reclassify อื่น (งวดเปิด · ไม่มีเอกสารปลายทาง · ยังไม่ยื่นภาษี · ยังไม่ส่ง e-Tax). UI: แบนเนอร์เตือนเมื่อ CN/DN ไม่ได้ผูก FK ใบเดิม (§86/9-10 บังคับ + PDF จะไม่มีบรรทัดอ้างอิง) + ปุ่ม "↔ ย้ายฝั่ง" |
| **ตั้งค่าเงื่อนไขชำระเงิน (3 ที่ ลำดับชัดเจน)** | (1) **รายใบ** — ฟอร์มสร้างเอกสาร ช่อง "เงื่อนไขการชำระเงิน" (เพิ่ม/ลบรายข้อ) + "วันเครดิต"; (2) **ต่อชนิดเอกสาร** — `DocumentTemplate.DefaultPaymentTerms/DefaultCreditDays` ตั้งได้ 2 ทางแต่**เก็บที่เดียว**: เมนู เทมเพลตเอกสาร PDF → ปรับแต่ง, หรือปุ่ม "⚡ ตั้งค่าเริ่มต้น" ที่โผล่ในหน้าสร้างเมื่อชนิดนั้นยังไม่เคยตั้ง (`Page.openPaymentTermsSetup` → GET `document-templates/default/{type}` → PUT partial); (3) **ต่อลูกค้า** — `Contact.PaymentDueDays/PaymentTerms`. ลำดับใครชนะใคร: **ที่พิมพ์ในใบ > ลูกค้า > เทมเพลตชนิดเอกสาร** — autofill เติมเฉพาะช่องว่างและเฉพาะตอนสร้างใหม่ (`dataset.userTouched` ล็อกไว้) |
| **เส้นทาง PDF จริง** | `GenerateDocumentPdfAsync`: เอกสาร **e-Tax → `RenderDocumentPdfNative` (QuestPDF) เสมอ**; เอกสารปกติ → Chromium เรนเดอร์ HTML ก่อน แล้ว fallback QuestPDF เมื่อปิด/ล้มเหลว. ⇒ ค่าที่ QuestPDF ไม่อ่าน **ไม่ใช่แค่เรื่อง preview** แต่ออกผิดจริงกับ e-Tax + เครื่องที่ไม่มี Chromium. แก้แล้ว: `ResolvePageSize` (PaperSize/Orientation เดิม hardcode A4 ทั้งไฟล์) และ `PickLangText` (ป้ายลายเซ็น/หัวข้อคู่ค้า/หมายเหตุท้าย/ข้อมูลธนาคาร ฉบับ EN ไม่เคยถูกอ่านเลย). ยังต่างโดยตั้งใจ: `LogoWidth` (QuestPDF คุมด้วยความสูง 8–16mm กันโลโก้ล้นหัว) และขอบกระดาษ (QuestPDF ใช้ค่าเฉลี่ย 4 ด้าน). ยังไม่มีใครใช้ (schema เปล่า ไม่มี UI): QR/PromptPay, DefaultCopies/CopyLabels, HeaderTextColor, ShowBilingual, IsEtaxTemplate/AutoGenerateEtaxXml/DigitalCertificate* |
| **ติ๊กในเทมเพลต → กระดาษ** | ทุกติ๊กต้องถูกอ่านโดย **ทั้งสอง** renderer (`PdfGenerationService.cs` = HTML/preview, `PdfGenerationService.DocumentRenderer.cs` = QuestPDF) และเรียงคอลัมน์เหมือนกันเป๊ะ — กฎ "ร่าง = ตัวจริง". เคยตายทั้งคู่ 4 ตัว: `ShowItemCode` / `ShowVatPerLine` / `ShowWithholdingTax` (ไม่มีคอลัมน์เลย) และ `ShowContactBranch` (สาขาขึ้นตลอดไม่ฟังติ๊ก). §86/4: ใบกำกับ/ใบเพิ่ม-ลดหนี้ บังคับแสดงสาขาผู้ซื้อเสมอ (`RequiresBuyerBranchOnPrint`) ติ๊กปิดไม่ได้. `ShowPaymentTerms` ทำงานก็ต่อเมื่อ **เอกสาร** มี `PaymentTerms`/`CreditDays` → `BuildPreviewHtml` จึงต้องหยิบ `DefaultPaymentTerms`/`DefaultCreditDays` ของ template มาใส่เอกสารตัวอย่าง ไม่งั้นตั้งค่าแล้ว preview นิ่ง. ข้อมูลตัวอย่างต้องมีส่วนลด/WHT/รหัสสินค้าครบเพื่อ "ออกกำลัง" ทุกติ๊ก |
| **การแบ่งหน้าเอกสารพิมพ์ — "ย้ายทั้งส่วน ไม่ใช่แค่บรรทัด"** | เอกสารยาวเกิน 1 หน้าเคยถูกผ่ากลางตรงไหนก็ได้: ลายเซ็นอยู่หน้า 1 แต่ชื่อผู้เซ็นตกไปหน้า 2 · กล่องข้อมูลธนาคาร/หมายเหตุถูกตัดครึ่ง · หน้า 2 มีแต่ตารางลอย ๆ ไม่มีหัวอะไรเลย. กติกาปัจจุบัน (ต้องตรงกันทั้งสอง renderer): **(ก) กล่องข้อมูลเป็นหน่วยเดียว** — `.contact-section .bank-details .footer-notes .custom-appendix .terms-conditions .cert-section .summary .amount-words` ตั้ง `page-break-inside: avoid` ฝั่ง HTML / `.ShowEntire()` ฝั่ง QuestPDF (กล่องคู่ค้า · สรุปยอด · ลายเซ็น); **(ข) แถวรายการห้ามผ่ากลาง** — `.items-table tr { break-inside: avoid }` คำอธิบายหลายบรรทัดต้องอยู่หน้าเดียวกับตัวเลขของมัน; **(ค) หัวตารางซ้ำทุกหน้า** — `thead { display: table-header-group }` + `table.Header(...)`; **(ง) บล็อกลายเซ็นเลิกใช้ flex** — Chromium **ไม่เคารพ `break-inside: avoid` บน flex container/item** (CSS มีอยู่แล้วแต่ไม่มีผลเลย) → เปลี่ยนเป็น `<table class='signatures'><tr><td class='sig-box'>` เพราะ `<tr>` เป็นสิ่งที่ print engine เคารพจริง |
| **หัวกระดาษ 3 ส่วนซ้ำทุกหน้า (`RepeatHeaderEveryPage`, default เปิด)** | หน้า 2+ ต้องมี **ข้อมูลบริษัทเรา · หัวเอกสาร (ชื่อ+เลขที่+วันที่) · กล่องข้อมูลคู่ค้า** ครบเหมือนหน้าแรก — หน้าที่หลุดจากชุดต้องสืบกลับได้ว่าเป็นเอกสารอะไรของใคร. **HTML**: ห่อทั้งเอกสารด้วย `<table class='doc-frame'>` โดย thead = 3 ส่วนหัว, tbody = เนื้อหาที่เหลือ — `display: table-header-group` เป็น**ทางเดียว**ที่ Chromium ทำ repeating header ให้ (`<div>` ทำไม่ได้ไม่ว่าเขียน CSS อย่างไร). ลายน้ำ/ป้ายมุม (`position:absolute`) และแถบเครดิต (`position:fixed`) อยู่**นอก**ตารางโดยตั้งใจ — ถ้าเอาเข้าไป `<td>` จะกลายเป็น containing block แล้วตำแหน่งเพี้ยน. **QuestPDF**: ย้าย `ComposeHeaderAndTitle` + `ComposeContact` + ป้ายสำเนา จาก `page.Content()` เข้า `page.Header()` (QuestPDF วาด Header ซ้ำทุกหน้าโดยกำเนิด) — ปิดธง = กลับไปไหลอยู่ใน Content หน้าแรกหน้าเดียวเหมือนเดิมเป๊ะ. ตั้งค่าที่ `document-templates.html` กล่อง "📐 หน้ากระดาษ" |
| **คอลัมน์ส่วนลดที่ว่างทั้งใบ (`HideEmptyDiscountColumn`, default เปิด)** | ใบส่วนใหญ่ไม่มีส่วนลด แต่คอลัมน์ "ส่วนลด" ยังกินความกว้างเป็น `0.00` ทุกแถว ทำให้ช่องคำอธิบายแคบจนตัดบรรทัด. resolver กลางตัวเดียว `PdfGenerationService.ShouldShowDiscountColumn(doc, template)` ที่ **renderer ทั้งสองตัวต้องเรียก ห้ามเช็ค `ShowDiscount` ตรง ๆ อีก** (ไม่งั้นจำนวนคอลัมน์ไม่เท่ากัน = หัวตารางกับเซลล์เหลื่อมกันทั้งใบ). ลำดับ: `ShowDiscount=false` → ไม่แสดงเสมอ (ผู้ใช้ปิดถาวร ชนะทุกกรณี) → `HideEmptyDiscountColumn=false` → แสดงเสมอ → นอกนั้นแสดงเฉพาะเมื่อมีบรรทัดใดมีส่วนลดจริง. ดูทั้ง `DiscountAmount > 0.005` (เศษต่ำกว่าครึ่งสตางค์ปัดเป็น 0.00 บนใบอยู่ดี) **และ `DiscountPercent > 0`** (OCR/integration บางเส้นทางกรอกมาแต่ % ยอดบาทคำนวณทีหลัง) และข้ามบรรทัด `IsDeleted`. เทสต์: `DocumentPrintLayoutTests` |
| หน้าปรับแต่งเทมเพลตติ๊กไม่ตรงค่าจริง | `DocumentTemplateResponse` ต้องส่ง **ทุก field ที่ editor ใช้** — เดิมขาดกลุ่มคู่ค้า/บริษัท/สรุปยอด/ลายเซ็น/ตราประทับ/ขอบกระดาษ → `_fillForm` อ่าน undefined → checkbox หลุดหมด และกดบันทึกทับ = ปิดข้อมูลบน PDF จริง. เพิ่ม field ใหม่ในเทมเพลต **ต้องเพิ่ม 4 ที่**: entity → Create/Update DTO → `ApplyRequestToTemplate`+`ApplyUpdateToTemplate` → `MapToResponse` (+`DuplicateAsync` ถ้าต้องคัดลอกด้วย) |
| เลือกงวดเคลมภาษีซื้อจากตัวเอกสาร (push) | ช่อง "งวดที่เคลมภาษีซื้อ" บนฟอร์มใบซื้อ → `ApplyInputVatClaimPeriodAsync` เขียนลง `InputVatBecameClaimableAt`. **ตารางตัดสินใจอยู่ที่ `InputVatClaimPeriodRules` (pure, มีเทสต์ครอบ)** — ทางเข้าทั้ง 3 ทาง (สร้าง · แก้ไข · แผงภาษีซื้อในหน้าดูเอกสาร) ต่างกันแค่ "เรียกกติกาไหม" ไม่ใช่ "ตัดสินยังไง" (สร้าง: ข้ามเมื่อค่าว่าง — ใบใหม่ไม่มีอะไรให้ล้าง · แผงภาษีซื้อ: ข้ามเมื่อ `ClaimInputVat=false`). รับปี พ.ศ. ด้วย — **ต้องแปลง −543 ก่อนตรวจช่วงปี** ไม่งั้นปี พ.ศ. ถูกปฏิเสธตั้งแต่บรรทัดแรก (บั๊กที่เคยหลุด: มีโค้ดแปลงอยู่แต่ไม่มีวันถูกเรียกถึง) (reuse กลไก "นับเฉพาะงวดที่กำหนด" เดิมของ GenerateVatReport ทั้ง query+skip). **ลำดับใครชนะใคร**: (1) บรรทัดรายงานจริง — **งวดร่าง (Draft) ย้ายได้เลย** ระบบติ๊กบรรทัดออกจากงวดเดิม (`IsExcluded=true` เก็บไว้เป็น audit + prefix เหตุผล) แล้ว `RecalcVatTotals` หัวรายงานให้ ผ่าน helper กลาง `ExcludeVatReportLinesAsync` ตัวเดียวกับที่ "เลิกเคลม" ใช้ (เดิม block แล้วสั่งให้ไปติ๊กเองที่หน้ารายงาน ทั้งที่ปุ่ม "เลิกเคลม" บนหน้าเดียวกันติ๊กออกให้เงียบ ๆ อยู่แล้ว = การกระทำเดียวกันได้คำตอบคนละอย่างแล้วแต่ทางเข้า); **Filed/Submitted ยัง block เด็ดขาด** (ตัวเลขถึงสรรพากรแล้ว — ต้องยื่นเพิ่มเติม) และ UI ล็อกช่องเฉพาะกรณีนี้. ลำดับสำคัญ: ติ๊กออกจากงวดเดิม **หลัง** validate §82/3 ครบ ไม่งั้นใบหลุดจากงวดเดิมโดยไม่ได้งวดใหม่ (2) flow 11640 ชนะเจตนา — post เป็น undue จะล้างเจตนาทิ้ง (3) เจตนา = ค่าเริ่มต้นให้ generation. §82/3 validate ด้วย `TaxService.EvaluateClaimPeriod` ตัวเดียวกับปุ่ม "ดึงเอกสาร" — ตั้งได้ = ดึงได้ ไม่มีวันขัดกัน. UI ล็อกช่อง+บอกงวดเมื่อถูกใช้แล้ว (`fVatClaimLocked`). **ตั้งจากหน้าดูเอกสารได้ด้วย** (ไม่ต้องเข้าโหมดแก้ไข/ไม่ต้องไปติ๊กในรายงาน): ช่อง "งวดที่เคลม ภ.พ.30" (`ctiClaimPeriod`) ในแผงเคลมภาษีซื้อของ detail modal → `POST complete-tax-invoice` รับ `InputVatClaimPeriod` แล้วเรียก resolver กลางตัวเดียวกัน (เรียก**หลัง** `ReclassifyUndueInputVatAsync` — ใบที่เพิ่งเติมใบกำกับครบในคลิกเดียวกันเลือกงวดต่อได้ทันที; ข้ามเมื่อ `ClaimInputVat=false`). ช่อง disabled เมื่อใบอยู่ในรายงานแล้ว (รายงานชนะ — badge บอกงวด+วิธีย้าย). **ช่องงวดโชว์งวดที่จะเคลมจริงเสมอ ไม่ปล่อยว่าง** (ทั้ง detail และฟอร์มแก้ไข): ไม่ตรึงงวดเอง → เติมเดือนของ `TaxPointDate ?? DocumentDate` ซึ่งเป็น**ฐานเดียวกับที่ `GenerateVatReport` ใช้คัดเอกสารเข้างวด** (ใช้ `documentDate` เฉย ๆ จะโชว์ผิดกับใบที่ tax point คนละเดือน) + ป้าย ⚙️ ค่าปกติ / 📌 ตรึงเอง. ⚠️ ค่าที่ pre-fill ไว้ **ห้ามส่งกลับ** — เทียบกับ `dataset.initial` แล้วส่งเฉพาะตอนผู้ใช้เปลี่ยนจริง ไม่งั้นแค่เปิดดูแล้วกดบันทึกจะตรึง `InputVatBecameClaimableAt` ถาวรทั้งที่ไม่ได้สั่ง แล้วใบไม่ขยับตาม tax point อีกเลย (เช่นแก้วันจ่ายทีหลัง). ฟอร์ม**สร้างใหม่**ไม่ pre-fill (วันที่ยังเปลี่ยนได้ระหว่างกรอก). กติกาเพิ่ม: ใบ undue ที่ย้ายเข้า 11610 แล้ว **ล้างงวดไม่ได้** (ย้ายงวดได้ แต่ต้องระบุงวดเสมอ) — `BecameClaimableAt=null` บนใบ `PostedAsUndue` จะทำให้รายงานเห็นเป็น "ยังพัก 11640" ทั้งที่ GL ย้ายออกแล้ว |
| แก้การแยกฝั่ง CN/DN ใน ภ.พ.30 (ซื้อ vs ขาย) | `TaxService.GenerateVatReport` — ลำดับ: `RelatedDocumentId` → **GL fallback** (`cnDnSideFromGl`: JE แตะ 116x = ซื้อ / 2191x = ขาย) → แยกไม่ได้ = ขึ้นบรรทัด ⚠️ ไม่เงียบ. CN/DN ที่ไม่มี FK เดิม**ตกไปฝั่งขายเสมอ** ทำให้ไม่หักภาษีซื้อ **และหักภาษีขายเกิน** (นำส่งขาด §89) |
| แก้รายงาน ภ.พ.30 (CSV ยื่น) | `TaxFilingExportService.ExportPp30Async :243` — ดึงจาก `ComputeVatReportAsync` |
| แก้ ภ.ง.ด.50 | `TaxService.GenerateCitReport :731` |
| แก้ปฏิทินนำส่ง (dashboard) | `StatutoryRemittanceService.GetFilingCalendarAsync` + `BuildCell` / UI: `app.html` widget `filingCalendar` |
| แก้ chatbot (public/tenant/admin) | `ChatbotService` + `KnowledgeBaseService` + `ChatAnswerDistillationModel` — สถาปัตยกรรม+แผนอยู่ `CHATBOT_PLAN.md` |
| แก้ rate limit / purge ของแชท | `ChatRateLimiter` (ตาราง `ChatRateBuckets`) / `ChatRetentionPurgeJob` |
| แก้คลังความรู้ chatbot (admin) | `AdminChatController` (kb/*, metrics) + UI `/admin/chat-kb.html` |
| แก้จับคู่ธนาคาร M:N (หลายโอน → เอกสารเดียว) | `BankService.CreateReconciliationGroupAsync` / UI: `bank.html` `GroupReconcile` (`applyPreselect`, `autoFillBankToMatch`, `signItemsToBankSide`) |
| แก้ยอด JE ที่ใช้กระทบยอด (ขาธนาคาร vs footing) | `BankService.GetUnmatchedItemsAsync` (`jeBankLeg`) + `ResolveItemAmountAsync` — ใช้ `BankAccount.LinkedAccountId` หาบรรทัดที่แตะธนาคารจริง |
| แก้เอกสาร operational แบบ Rev. (QT/PO/PR/BN/DN) | `DocumentService.RevisableTypes` + `UpdateDocumentAsync` (โหมด revision) + `BuildDocumentSnapshot` / PDF: `DisplayDocNumber` — ดู §2.3b |
| แก้กำหนดยื่น/กฎยื่นแบบเปล่า | `StatutoryRemittanceService.DueDates` / `FilingRule` (มี unit test) |
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

## 10. ประวัติการเปลี่ยนแปลงราย "รอบ" — ย้ายไป `CHANGELOG.md`

บล็อก `_Last verified …` และ `_รอบ NN …` ทุกรอบ (รวมตาราง "รายการที่ผ่านมาเรียงตามรอบ") ถูกย้ายไป `CHANGELOG.md`
เมื่อ 2026-09-18 (รอบ 170 — คำตัดสินเจ้าของ: doc ที่เป็น append-only log ขนาด 0.8 MB ทำให้ "สถานะปัจจุบัน" ผิดแล้วไม่มีใครเห็น).
ไฟล์นี้เหลือ **พฤติกรรมปัจจุบัน** (§1–§9) + บล็อกล่าสุดบล็อกเดียวด้านล่าง · กติกาการดูแลเดิมทุกข้อยังบังคับ:
คอมมิตที่เปลี่ยน flow ต้องแก้ §ที่เกี่ยวข้อง **และ** เติมบล็อกใหม่ใน `CHANGELOG.md` ในคอมมิตเดียวกัน แล้วแทนบล็อกล่าสุดข้างล่างนี้

_Last verified against codebase: 2026-09-24 (รอบ 193 — **คำตัดสินเจ้าของ 37 ข้อ + ผลตรวจการตั้งค่า/มัดจำ** · 13 ทีม + ฝ่ายค้าน 3 รอบ: มัดจำ 3 โหมด + หักฐานมัดจำก่อน VAT ทุกเส้น (§2.3/§3.7/§6.5) · ยอดชำระจริง/บรรทัดปรับ (§3.4) · ผลต่างปัดเศษ 54960 (§6.2j) · การรับทราบคำเตือน 4 แหล่ง + e-Tax hook ทุกทางเข้า (§3.2) · ธง VAT stopgap + §82/5 (§7) · คีย์ผู้ติดต่อเลขภาษี+สาขา (§6.2i) · ด่านไฟล์แนบ/สแกน/ใบเบิก (§6.2h) · retention สแกน (§6.3) · POS COGS/void (§2.6) · เงินเดือน (§3.8) · hash chain v2 (§6.1) · **หลังฝ่ายค้านรอบสาม**: ฐานมัดจำช่องแยก `DepositBaseDeducted` (R3-1) · ด่านทางเดียว (R3-5) · idempotency Integration ข้าม Voided 6 เมธอด (R3-6) · ตาข่าย void ล้มดัง (B1) · ล็อกมัดจำ (B2) · ที่พัก `AdoptTaxId` รูปใหม่ — 04ce362 · **หลังฝ่ายค้านรอบสี่**: ลายเซ็นลูกค้า `IsSignatureCurrent` + hash v2 (§2.8/§3.2 · de5dc4cd) · migration ฐานมัดจำครั้งเดียว + `[DEPOSIT-BASE-SPLIT]` + ฝั่งขายเท่านั้น (c3820dce) · ชื่อตรงตัว = ชื่อแกน + รูปนิติบุคคล · `[TAXID-CHECKSUM]` (§6.2i · cb552889) · ข้อความขาดตอน (§6.1 · 960e98cd) · รายละเอียดรอบ `CHANGELOG.md` — commit 7a16f097)_
