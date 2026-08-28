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
  - resolve `AccountCode → AccountId` ต่อบรรทัด (รองรับ AI suggest)
  - validate VAT-claimability ต่อบัญชี (`ChartOfAccount.InputVatClaimable`)
  - compute line amounts (`ComputeLineAmounts`) รองรับ `PricesIncludeVat`
  - เก็บ `GlAccountAiFeedbackId` ต่อบรรทัด (ปิดลูปการสอน local model)
  - ยังไม่สร้าง JE / ยังไม่กระทบ stock จนกว่าจะ Approve

### 2.2 OCR (สแกนเอกสาร)
- **Flow**: อัปโหลด → OCR เก็บ `OcrScanResult` → review modal → "สร้างเอกสาร"
- **Endpoint**: `POST /api/companies/{id}/ocr/{scanId}/create-document`
  (`?targetType=` เลือกชนิด · `?approve=true` = **สร้าง + อนุมัติทันที**)
- **ทางออกจาก review modal 4 ทาง**: `📝 ยืนยันในฟอร์ม` (แนะนำ — ผ่าน
  sessionStorage handoff เข้า `documents.html`) · `สร้างทันที` (Draft) ·
  `⚡ สร้าง + อนุมัติ` · `🧾 JE เท่านั้น`. ทุกทางที่สร้างสำเร็จ **ปิด modal +
  refresh รายการ** (เดิมค้างที่ modal เฉย ๆ) และการ์ดเปลี่ยนเป็น `สร้างแล้ว →`
  ที่ deep-link `documents.html?openDoc={id}` (เดิม `?id=` ซึ่งหน้านั้นไม่เคย
  อ่าน ⇒ พาไปหน้ารวมเปล่า ๆ). **อนุมัติไม่ผ่าน ≠ ล้มทั้งก้อน** — ใบ Draft ยังอยู่
  แล้วแนบเหตุผลกลับมาเป็น `[APPROVE-FAIL]` (ถ้า throw ทิ้ง ผู้ใช้จะเข้าใจว่า
  ไม่มีใบเกิดขึ้นแล้วสแกนซ้ำ = ใบซ้ำ)
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
  "Buyer"`) + กระดาษเป็น `Receipt` + **ไม่มีเลขผู้เสียภาษี 13 หลัก** + **ไม่มี
  VAT** + target เดิมเป็น Expense/PaymentVoucher → เปลี่ยน
  `TargetDocumentType = CertificateInLieu` + ลง ReasoningTrace อ้าง §65 ตรี(9)(18)
- **เหตุผล**: บิลเงินสดแม่ค้า/วินฯ ระบุตัวผู้รับเงินไม่ได้ → เสี่ยงโดนบวกกลับ;
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
    - เอกสารที่ VAT เข้ารายงานแต่หัวไม่มีคำว่าใบกำกับ → `CollectApprovalWarningsAsync`
      เตือนตอนอนุมัติ (ไม่ block — ขายปลีกที่ลูกค้าไม่ขอใบกำกับเป็นเคสปกติ) และ
      `TaxService.NotFullTaxInvoice` ติดธงบรรทัดในรายงานภาษีขายว่า
      "[ไม่ใช่ใบกำกับเต็มรูป — ลูกค้าเคลมภาษีซื้อไม่ได้]" เพื่อให้เห็นทั้งงวดในที่เดียว
    - แก้ย้อนหลังใบที่ออกไปแล้ว: เติมข้อมูลผู้ซื้อ → **พิมพ์ใหม่จากใบเดิม**
      (เลขที่/ยอด/วันที่ไม่เปลี่ยน หัวจะ upgrade เอง) ไม่ต้องออกใบใหม่/ใบลดหนี้

    **เงินมัดจำ (IsDeposit) กับ VAT — 2 เคสตามจังหวะ tax point**:
    | เคส | `DepositOutputVatDeferred` | ผังภาษีขาย | เข้า ภ.พ.30 | หัวเอกสาร |
    | --- | --- | --- | --- | --- |
    | มัดจำที่เป็น**ส่วนหนึ่งของราคา** (เงินจอง/ดาวน์/ล่วงหน้า) — §78/§78/1 "ได้รับชำระราคา" = tax point เกิดแล้ว | `false` | 21911 | **ใช่** งวดที่รับเงิน | ใบกำกับภาษี/ใบเสร็จรับเงิน (เงินมัดจำ) |
    | **เงินประกัน/มัดจำที่ต้องคืน** (ประกันความเสียหาย/เช่า/ภาชนะ) — ไม่ใช่ค่าตอบแทน ยังไม่เกิด tax point | `true` | 21913 (พักรอ) | ยังไม่เข้า จนกว่า `DepositOutputVatRecognizedAt` | ใบเสร็จรับเงิน (เงินมัดจำ) — **ห้ามมีคำว่าใบกำกับภาษี** |
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
    | **B — มัดจำเป็นใบกำกับ** (`Deferred=false`, Cr 21911) | ใบกำกับภาษี เข้า ภ.พ.30 งวดที่รับ | ต้องออก**เฉพาะยอดคงเหลือ** | ✅ แต่ห้ามกด "หักมัดจำ" |
    | **ผิด** | ใบกำกับ เข้า ภ.พ.30 แล้ว | ใบเดียว**เต็มจำนวน** | ❌ VAT ซ้ำ |
    - **Guard**: `ApplyDepositToInvoiceCoreAsync` block เมื่อมัดจำเป็น immediate VAT
      และ**งวดของใบมัดจำถูกยื่น (Filed) ไปแล้ว** — กลับ Dr 21911 ไม่ได้เพราะภาษี
      ก้อนนั้นนำส่งแล้ว (GL จะไม่ตรงกับแบบที่ยื่น) พร้อมชี้ทางออก 2 ทาง
      (ออกใบกำกับเฉพาะยอดคงเหลือ / ออกใบลดหนี้ยกเลิกใบกำกับมัดจำก่อน)
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
    6. `IEtaxInvoiceService.GenerateAsync` ถ้า `RequestTaxInvoice + EtaxEnabled`
  - ทุก step fault-tolerant: ล้มเหลว → log + ไม่ rollback step ก่อนหน้า
- **Booking flow** (`CmsBookingService.SyncBookingToErpAsync`):
  - Map `BookingType` → DocumentType:
    - `Lead/Appointment` → `Quotation` (Draft, รอ admin confirm)
    - `Guaranteed` → `TaxInvoice` (Approved + JE auto)
    - `PrePayment` → `Receipt` ที่ `IsDeposit=true` → Cr 217xx ขายรอรับรู้
      + Cr 21911 VAT (§78/1 รับชำระแล้ว → เข้า ภพ.30 ทันที)
      ต่อมา realize ด้วย `RealizeDepositAsync` ตัด 217xx → 41000
- **Gap (ยัง TODO)**:
  - Payment reconciliation (match `SiteOrderPayment.Reference` กับ bank statement)
  - Webhook gateway (Stripe/PromptPay) → ตอนนี้ admin กดยืนยันสลิปเอง

### 2.6 POS (Point of Sale)
- **Method**: `PosService.CompleteOrderAsync` (`Services/Implementations/PosService.Orders.cs:908`)
- **Auto JE ทันที** ตอน complete order (ไม่ผ่าน Draft):
  - Dr Cash 1011 / Bank 1012 / Credit Card 1131 (ตาม PaymentMethod)
  - Cr Sales Revenue 41000 (net of VAT)
  - Cr Output VAT 21911 (7%)
  - Cr Tip Liability 2160 (ถ้ามี)
  - Dr COGS / Cr Inventory (สินค้าที่ track stock)
- **Tax Invoice** (deferred): `IssueTaxInvoiceAsync` → สร้าง Document ใน
  Status=Approved (กัน JE ซ้อน) + เรียก `EtaxInvoiceService`
- **Refund/Void**: reverse JE + return stock
  - **Refund คิดสัดส่วนหลังส่วนลดระดับบิล** (`RefundOrderAsync`,
    `PosService.Orders.cs`): ยอดคืน = `Σ(item.TotalAmount × ratio) × discountFactor`
    โดย `discountFactor = (Σ item.TotalAmount − (DiscountAmount + CouponDiscountAmount))
    / Σ item.TotalAmount` — กันคืนเกินเมื่อบิลมีส่วนลด/คูปองระดับออเดอร์ (เช่น
    สินค้า 1000 ลดทั้งบิล 10% ลูกค้าจ่าย 900 → คืนเต็มต้องได้ 900 ไม่ใช่ 1000).
    ServiceCharge/Tip เป็นรายการเสริมบนบิล ไม่คืนตามการคืนสินค้า
- **Offline sync**: `SyncOfflineOrderAsync(ClientOrderId)` dedup
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

> **ทางเข้า approve มี 3 ทาง — ทุกทางวิ่งเข้า `ApproveDocumentAsync` เดียวกัน:**
> ① ปุ่มอนุมัติ/บันทึกและอนุมัติ (ตรง) ② กฎอนุมัติตามวงเงิน (ApprovalService
> gate — กฎ match แล้วปุ่มตรงถูกล็อคจน workflow ผ่าน) ③ ส่งเซ็นอนุมัติ
> (SignatureApprovalService — เซ็นครบทุกคน → เรียก ApproveDocumentAsync
> ให้อัตโนมัติ; **เดิมตั้ง Status ตรง ๆ ข้าม JE/สต๊อกทั้งหมด — แก้แล้ว**)
> RequireApprovalForDocuments (เกินวงเงิน) ยกเว้นให้เอกสารที่เซ็นครบแล้ว
> (กัน flow ที่ setting บังคับใช้โดน block ตัวเอง)
**`DocumentService.ApproveDocumentAsync` (`:1512`)** ทำตามลำดับ:

1. **Permission + workflow gate** (`:1638`) — ตรวจ ApprovalWorkflow
   (multi-level), credit limit ของลูกค้า (AR/AP advanced)
   - **§90/2 hard-block**: `CompanySettings.VatRegistered=false` → ห้ามอนุมัติ
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
4. **Snapshot Tax Point** (`:1760`) — `TaxPointResolver.Resolve(doc)` →
   `doc.TaxPointDate` = MIN(delivery / ownership transfer / payment received /
   invoice issue) ตาม §78 / §78/1 → ตัดสินงวด ภ.พ.30
5. **Retention** (`:1766`) — `doc.RetentionUntil ??= DocumentDate + 5 years`
   ตาม พ.ร.บ.บัญชี ม.10 (ห้ามลบจริงก่อนหมดอายุ)
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
8. **Stock movements** (`:1794` → `ApplyStockMovementsAsync :4605`) —
   switch ตัดสินตาม `DocumentType` (`:4612`):
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
9. **Fixed asset auto-register** (`:1799`) — `AutoRegisterFixedAssetsAsync`:
   บรรทัดที่ลงผัง 12210 / 12220 / 12230 / 12240 / 12260 / 12270 / 12290 /
   12310 → **group ตาม AccountId** → 1 group = 1 `FixedAsset` (TFRS for NPAEs
   บทที่ 10: ค่าขนส่ง/ติดตั้ง/ฝึกอบรม/ค่าธรรมเนียม/setup ฯลฯ = ต้นทุนที่ทำ
   ให้พร้อมใช้ — รวมเป็น cost ของ asset หลัก ไม่แยก asset)
   - main line = บรรทัดแรกใน group ที่ description ไม่ใช่ auxiliary keyword
   - cost = sum ของทุก line ใน group (รวม aux)
   - asset Description log auxiliary breakdown ไว้ audit trail
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
- **ค่าธรรมเนียมหักจากยอดโอน** (marketplace Shopee/Lazada, gateway, ธนาคาร):
  `Payment.FeeAmount(+FeeAccountId)` — Amount คือเงินสุทธิที่เข้า, เอกสาร
  ถูกล้างที่ Amount+Fee: JE Dr เงินสด + Dr ค่าธรรมเนียม (53200/ค้นชื่อ) /
  Cr AR ยอดเต็ม; void คืน PaidAmount รวม fee; เฉพาะฝั่งขาย
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
  0. **FX guard**: ถ้า `invoice.Currency != deposit.Currency` หรือ
     `|invoice.ExchangeRate − deposit.ExchangeRate| > 0.0001` → throw
     (กัน FX silent corruption ตาม IAS 21 — ระบบยังไม่รองรับการบันทึก
     gain/loss FX อัตโนมัติ user ต้องทำ JE manual หรือใช้มัดจำสกุลเดียวกัน)
  1. คำนวณ `vatPortion = deposit.VatAmount / deposit.TotalAmount`
  2. เรียก `RealizeDepositAsync` ด้วยฐานไม่รวม VAT (`amount × (1−vatPortion)`)
     → Dr 217xx ขายรอรับรู้ / Cr รายได้ + (ถ้า deferred) ย้าย 21913→21911
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

---

## 4. ผลกระทบต่อระบบรายงาน (per document type)

| Type | JE on Approve | Stock | VAT report side | Tax point | Special |
| --- | --- | --- | --- | --- | --- |
| `Quotation` | ❌ | ❌ | – | – | – |
| `Invoice` | Dr AR / Cr Rev + Cr **[21913 บริการล้วน \| 21911 มีสินค้า TrackStock]** | ❌ (DN จัดการแยก) | output — บริการล้วน: เข้าเมื่อ `OutputVatDueAt` (รับเงิน §78/1); มีสินค้า/legacy (GL ลง 21911 ตรง): เข้าทันทีตาม tax point (§78 ส่งมอบ) | `TaxPointDate` snapshot; บริการ → `OutputVatDueAt` ตอนรับชำระ | รับชำระ (Payment/ใบเสร็จ settlement) → `TryReclassifyUndueOutputVatAsync`: JV Dr 21913 / Cr 21911 เต็มยอดคงเหลือ + stamp `OutputVatDueAt` (full-on-first-settlement, GL-driven, idempotent). แปลงเป็น TIV → supersede reverse JE ทั้งใบ (รวม 21913) ใบกำกับลง 21911 เอง |
| `TaxInvoice` | Dr AR / Cr Rev + Cr 21911 | ❌ | output | `TaxPointDate` snapshot | – |
| `BillingNote` | ❌ (รอ Receipt) | ❌ | – | – | **รวมใบค้างหลายใบได้**: `POST document/billing-note/from-invoices` — 1 บรรทัด/ใบ ยอด=BalanceDue, `DocumentLine.SourceDocumentId` ชี้ใบต้นทาง (กันวางบิลซ้ำใน BN active) |
| `Receipt` standalone | Dr Cash / Cr Rev + Cr 21911 | ❌ (Receipt **ไม่อยู่** ใน `ApplyStockMovementsAsync` switch — ถ้าต้อง OUT ต้อง issue Invoice/TaxInvoice ก่อน) | output | DocumentDate | nullable `RelatedDocumentId` — ถ้ามีอ้าง Invoice → ไม่ count VAT ซ้ำ |
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
- **Receipt gate (`GenerateAsync`)** — e-Tax คือ "ใบกำกับภาษีในรูปอิเล็กทรอนิกส์"
  ใบที่**ตั้งใจไม่ให้เป็นใบกำกับ**จึงส่งไม่ได้ (Receipt/ReceiptVoucher ถูก map เป็น
  `T03 "ใบเสร็จรับเงิน/ใบกำกับภาษี"` เสมอ = ประกาศต่อ RD ว่าเป็นใบกำกับ):
  - **มัดจำ VAT รอเรียกเก็บ** (`DepositOutputVatDeferred` + ยังไม่ recognize) → block
    (ปล่อยผ่าน = XML บอก RD ว่าเป็นใบกำกับ ทั้งที่ ภ.พ.30 ยังไม่มียอดนี้ → ผู้ซื้อ
    เคลมภาษีซื้อจากใบที่ผู้ขายไม่เคยนำส่ง ทั้งสองฝั่งโดนประเมิน)
  - **ไม่ใช่ใบกำกับเต็มรูป** (`TaxService.NotFullTaxInvoice`: ข้อมูลผู้ซื้อไม่ครบ /
    walk-in / ติ๊กไม่ประสงค์รับใบกำกับ) → block พร้อมบอก field ที่ขาด
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
- กำหนดยื่น (`DueDates`): ปกส. 15/15 · ภ.พ.30 กระดาษ 15 e-Filing 23 ·
  ภ.ง.ด. กระดาษ 7 e-Filing 15 (ของเดือนถัดจากงวด)
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
- **PDF**: `PdfGenerationService.WhtCert.cs`
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
  (`NeedsReview = false`) → schedule depreciation ปกติ

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

## 7. Validation gates (compliance — กฎเหล็ก #2)

| Gate | Where | กระทำ |
| --- | --- | --- |
| §86/4 completeness (PI/Expense/PV) | `TaxInvoiceCompletenessChecker` | ถ้าไม่ครบ → input VAT ลง 11640 (undue) |
| §82/5 prohibited input VAT | `ChartOfAccount.InputVatClaimable` + per-line `IsVatClaimable` | flag claim=false, แยกออกจาก ภ.พ.30 + แสดง "🚫 §82/5" line |
| §82/5(1)(2) non-full-tax-invoice | `OcrDocumentRoleInferrer.Infer` → `InputVatClaimable/InputVatClaimWarning` | OCR ตรวจ "ใบกำกับภาษีอย่างย่อ §86/6" หรือ "ใบเสร็จ/บิลเงินสด ไม่ใช่ §86/4" + มี VAT → เขียน `[VAT-CLAIM]` ลง ProcessingNotes; review UI + form แสดง banner แดง "เคลม VAT ไม่ได้ — ขอใบกำกับเต็มรูป"; ไม่ auto-ติ๊ก "ขอเครดิตภาษีซื้อ". กัน false positive 2 ชั้น: `ContainsAnyNotNegated` (ข้ามข้อความปฏิเสธ "ไม่ใช่...อย่างย่อ" จาก vision model) + เลขภาษีผู้ซื้อ 13 หลักถูกสกัดได้ = ใบเต็มรูปเสมอ (§86/6 ใบอย่างย่อไม่มีข้อมูลผู้ซื้อ) override คำที่เจอบนกระดาษ |
| §82/3 6-month window | `TaxFilingExportService.ExportPp30Async` + `GenerateVatReport` | เกิน 6 เดือน → block claim หรือ require `LateReason` |
| §86/9–86/10 CN/DN | `CreditNote/DebitNote` flow | required `RelatedDocumentId` + `CreditNoteReason` (CN); cap ≤ original |
| §78 / §78/1 / **§78/2** tax point | `TaxPointResolver` | snapshot ตอน approve · นำเข้าใช้ `Document.CustomsDutyPaidDate` **ตรง ๆ ไม่ใช่ MIN** · `SupplyKind` ให้ caller ระบุชนิดแทนการเดา (Auto = พฤติกรรมเดิม) |
| §65 ตรี รายจ่ายต้องห้าม | `Section65TerValidator` | `NonDeductibleAmount + RuleJson` → ภ.ง.ด.50 · ครอบ **16 กลุ่มอนุมาตรา** (เดิม 8): เพิ่ม (8)(9)(10)(12)(13)(14)(15)(19) — context ใหม่ทุกตัว optional default = "ไม่ตรวจ" จึงไม่เดาแทนผู้ใช้ |
| ภ.ง.ด.50/51 ยอด CIT | `TaxReport.CitAmount` | **แยกจาก `TotalTaxWithheld`** (เดิม reuse field ผิดความหมาย ทำให้ยอด CIT ปนกับ WHT เวลารวมข้ามรายงาน) · ผู้อ่านใช้ `CitAmount ?? TotalTaxWithheld` รองรับรายงานเก่า |
| §65 ตรี(4) cap per fiscal year | `Section65TerValidator.Context.PriorYtdEntertainmentExpense` | sum YTD entertainment of Approved docs → excess บวกกลับใบปัจจุบัน |
| §82/5(6) vehicle dealer override | `CompanySettings.IsVehicleDealer` | bypass warning เมื่อรถเป็น inventory (ประกาศอธิบดี 42) |
| §87 ลำดับเวลาในรายงาน | `TaxService.NormalizeReportLineOrder` | เรียง + renumber `LineOrder` ท้ายการ generate ทุกครั้ง (ขาย → ซื้อ → บรรทัดสรุป; แต่ละกลุ่มตาม `TransactionDate`, ties = ลำดับเดิมเพื่อ deterministic). เรียกจาก GenerateVatReport / หลัง ApplyVatDeferrals / ComputeVatReport (ไฟล์ยื่น) / GenerateWhtReport / PullDocumentIntoReport / regenerate re-apply. `tax.html` sort ซ้ำฝั่ง client (จอ + แบบพิมพ์ §87) ให้รายงานเก่าถูกลำดับโดยไม่ต้อง regenerate — เดิม LineOrder ไล่ตามลำดับที่ query คืนเอกสาร = วันที่สลับไปมา |
| §82/5(6) นโยบาย "ดุลพินิจผู้กรอก" | keyword auto-cut ถูกถอดออกทั้งหมด | ระบบ**ไม่เดา**จากข้อความไปตัดสิทธิ (เดิม "ค่าน้ำมัน" คำเดียวโดนตัด = น้ำมันรถกระบะผู้รับเหมาหายจาก ภ.พ.30). การตัดใช้เฉพาะ (a) flag รายบรรทัด IsVatClaimable (b) ผังบัญชีต้องห้ามที่บริษัทตั้งเอง; คำเตือนกฎรถยนต์นั่ง (ประกาศ 42: กระบะตอนเดียว/แค็บ/บรรทุก/ตู้>10 เคลมได้; เก๋ง/กระบะ 4 ประตูไม่ได้) มี 2 จุด — approve warning + confirm ตอนติ๊กเคลมในหน้าเอกสาร |
| ติ๊กเคลมภาษีซื้อเข้า/ออกหลังอนุมัติ | `CompleteSupplierTaxInvoiceAsync` + `ClaimInputVat` (Unclaim/ReclaimInputVatAsync) | แผงในหน้า detail ของ PV/Expense/PI (approved, VAT>0): เลิกเคลม → JE Dr ค่าใช้จ่าย "ภาษีซื้อขอคืนไม่ได้"/Cr 11610 หรือ 11640 + set override=ผังค่าใช้จ่าย (marker ที่ ภ.พ.30 exclude อยู่แล้ว) + ติ๊กบรรทัดงวด Draft ออก; block เมื่อเคลมในงวด Filed แล้ว (ต้องยื่นเพิ่มเติม). กลับมาเคลม → require §86/4 ครบ + กรอบ 6 เดือน §82/3 → JE ย้อน + BecameClaimableAt=now (เข้า ภ.พ.30 งวดปัจจุบัน) + PV เปิด HasTaxInvoiceReference. แก้เลขที่/วันที่/สาขาใบกำกับได้ทุกใบจากแผงเดียวกัน |
| F14 audit hash chain | `AuditTrailService.VerifyHashChainAsync` + `AuditChainVerifyJob` | cron 7 วัน re-compute SHA-256 → notify ถ้า tamper (พ.ร.บ.บัญชี ม.11 ทวิ) |
| Recurring template validate | `RecurringTransactionService.ValidateTemplateAsync` | fail-fast ตอน Create/Update ก่อนรอ midnight cron — accountId ต้องอยู่ใน CoA, journal balance |
| Reclassify line GL (post-approve) | `DocumentService.ReclassifyLineAccountAsync` | เปิดทั้งฝั่งซื้อและฝั่งขาย (Invoice/TaxInvoice/Receipt/CN/DN ด้วย — §86/4 บังคับ**สิ่งที่พิมพ์บนใบกำกับ** ซึ่งไม่มีรหัสผังบัญชีอยู่เลย); post JE คู่ใหม่ลงงวดเดิม โดย**อ่านขาที่ลงจริงใน GL** (`oldWasCredited`) แล้วกลับตามนั้น + update line.AccountId. **ยอดที่ย้าย = `ResolveLinePostedGlAmountAsync`** ไม่ใช่ `line.Amount` เสมอ: ภาษีซื้อต้องห้าม §82/5 (`IsVatClaimable=false`) → `Amount + VatAmount` เพราะ AutoPost รวม VAT เป็นต้นทุนบรรทัด (ย้ายแค่ฐาน = VAT ค้างผังเก่าถาวร โดย Dr=Cr ยังสมดุล) · ใบที่อ้างใบรับของ GRN → ฐานตัด GR-NI ไปแล้ว บรรทัดเหลือแค่ VAT ต้องห้าม · PV ที่แปลงจากใบตั้งหนี้ (settlement) → 0 (แค่เปลี่ยน AccountId ไม่ลง JE) · ต่างสกุล → คูณ `ExchangeRate` ผ่าน `ToGlAmount`. gate: period Open + no downstream (ไม่นับใบเสร็จ settlement) + not Draft/WaitingApproval/Voided/Rejected + ไม่ใช่ใบมัดจำ + ไม่ใช่บัญชีคุม + no submitted ภพ.30 + no e-Tax submitted |
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
| แก้การคิดส่วนลด/VAT ต่อบรรทัด | `DocumentService.ComputeLineAmounts :254` — รองรับ `DiscountPercent` + `DiscountAmount` (ยอดเงิน, มาตรฐานสากล: ใบระบุส่วนลดเป็นบาท). amount > 0 ชนะ % |
| เศษสตางค์ VAT/WHT (ปัดรายบรรทัดแล้วรวมเพี้ยน ±0.01) | `DocumentService.ReconcileTaxRounding` — หลังคิดทุกบรรทัด (create+update) กระทบยอดต่อกลุ่มอัตรา: ΣVAT/WHT ของกลุ่ม = round(Σฐาน × อัตรา) ตรงเครื่องคิดเลข; เศษเกลี่ยเข้าบรรทัดฐานสูงสุด; ข้าม `VatAmountOverride`; โหมดราคารวม VAT ขยับ net สวนทางคง gross. frontend mirror ใน `documents.html calcSum` (allocation+reconcile แบบเดียวกัน — ยอดก่อน/หลังบันทึกตรงกัน) |
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
| ดูสายการแปลงทั้งเส้นของเอกสาร (chain stepper) | `DocumentService.GetDocumentChainAsync` — ขึ้นตาม RelatedDocumentId (กัน cycle, 15 ชั้น) แล้ว BFS ลง (เพดาน 60 ใบ); ใบ Voided คงอยู่ในสาย (UI ขีดฆ่า) / UI: `documents.html renderChainStepper` บนสุดของ detail modal |
| ค่าเริ่มต้นฟอร์มต่อชนิดเอกสาร (แหล่งเงิน/เงื่อนไขชำระ/วันเครดิต) | `DocumentTemplate.DefaultPaymentAccountId/DefaultPaymentTerms/DefaultCreditDays` — ตั้งใน template default ของชนิดนั้น (`document-templates.html` กล่อง "⚡ ค่าเริ่มต้น") / ฟอร์มดึงผ่าน `GET document-templates/default/{type}` เติมเฉพาะช่องว่าง+เฉพาะสร้างใหม่ (`applyDocTypeDefaults`) |
| **ลำดับค่าเริ่มต้นเทอมชำระเงิน/วันเครดิต** | **ผู้ใช้พิมพ์เอง > เครดิตของลูกค้า (`Contact.PaymentDueDays/PaymentTerms`) > เทมเพลตชนิดเอกสาร > AI (`/ai/payment-terms/suggest`)** — ติดตามที่มาผ่าน `dataset.autoSrc` ('template'\|'contact') + `dataset.userTouched` บน `#fCreditDays`/`#fPaymentTerms` (`documents.html`): ชั้นที่แคบกว่าทับชั้นที่กว้างกว่าได้ แต่ห้ามทับค่าที่ผู้ใช้แตะแล้ว; ตอนแก้เอกสาร (`openEdit`) ค่าที่บันทึกไว้ถือเป็น userTouched เสมอ. ช่องแสดงทุกชนิดเอกสาร (เดิมซ่อนใน `.supplier-invoice-only` → ใบเสนอราคาแก้เทอมไม่ได้) |
| เงื่อนไขการชำระเงินบนกระดาษ (`doc.PaymentTerms/CreditDays`) | render ทั้ง 2 ตัว (HTML `BuildDocumentHtml` + QuestPDF `DocumentRenderer`) เมื่อ `template.ShowPaymentTerms` — เดิม flag มีแต่ไม่มีใคร render = กรอกแล้วหายจากกระดาษเงียบ ๆ. **`PaymentTerms` เก็บได้หลายบรรทัด** (1 เงื่อนไข/บรรทัด — ฟอร์มเพิ่ม/ลบรายข้อผ่าน `#ptList`, sync ลง hidden `#fPaymentTerms`): HTML ใช้ `white-space:pre-line`, PDF แตกเป็น bullet เมื่อ >1 บรรทัด |
| **ชื่อ/ที่อยู่คู่สัญญาบนเอกสารภาษาอังกฤษ** | `ThaiAddressFormatter.ResolvePartyAddress` / `ResolvePartyName` — **resolver กลางตัวเดียว** ที่ทั้ง HTML และ QuestPDF เรียก (4 จุด: บริษัท×2 · ผู้ติดต่อ×2). ลำดับที่อยู่โหมด en: **ที่ผู้ใช้กรอกเอง (`AddressEn`) → ถอดอักษรอัตโนมัติ (`ThaiRomanizer`) → ที่อยู่ไทย**; โหมดไทยใช้ `Format` ตามปกติ. **ที่มา**: เดิมตรรกะนี้เขียนซ้ำ 4 จุด และ**สองจุดของผู้ติดต่อไม่มีชั้น "กรอกเอง" เลย** (ถอดอักษรเสมอ) ⇒ ที่อยู่ลูกค้า/ผู้ขายบนใบภาษาอังกฤษ **แก้ทับไม่ได้ตลอดกาล** ต่อให้รู้ว่าตัวถอดสะกดตำบล/อำเภอเพี้ยน (ThaiRomanizer เป็น RTGS แบบประมาณ ไม่มีพจนานุกรมเสียงอ่าน — ตำบล/อำเภอ/ถนน/ชื่ออาคารสะกดเพี้ยนได้เป็นปกติ ส่วนจังหวัด 77 ชื่อใช้ตารางทางการจึงแม่น). แก้โดยเพิ่ม `Contact.NameEn` + `Contact.AddressEn` คู่ขนานกับ `Company.NameEn`/`AddressEn` แล้วรวม 4 จุดเป็น resolver เดียว. **ชื่อไม่ถอดอักษรให้อัตโนมัติเด็ดขาด** — การสะกดชื่อเฉพาะเป็นสิทธิ์ของเจ้าของชื่อ (จดทะเบียนไว้อย่างไรต้องตามนั้น) เดาผิด = เอกสารระบุคู่สัญญาผิดคน ต่างจากที่อยู่ ที่ถอดผิดยังสื่อสารได้. ฟอร์มผู้ติดต่อมี section พับได้ "🌐 ข้อมูลภาษาอังกฤษ" (ไม่รกจอสำหรับลูกค้าไทยล้วน · เปิดอัตโนมัติเมื่อมีค่า) · payload/hydrate/reset ครบตาม checklist field ใหม่ · **ความแม่นของตัวถอดอักษร**: `ThaiRomanizer.PlaceNameEn` เป็นตารางสะกดทางการ ระดับ**อำเภอ/เขต** ที่ชนะตัวถอดเสมอ — (ก) **50 เขตกรุงเทพฯ** hand-curated (ที่อยู่ที่พบบ่อยที่สุดในเอกสารธุรกิจไทย + สะกดปรากฏบนป้าย/เอกสารราชการจึงยืนยันได้) (ข) **อำเภอเมือง 75 แห่งประกอบอัตโนมัติ** = `"Mueang "` + ชื่อจังหวัดทางการ — ไม่มีใครพิมพ์ด้วยมือจึงไม่มีทางสะกดผิด, มี guard ว่าส่วนหลังต้องเป็นชื่อจังหวัดจริงเพื่อกันเคส `เมืองจันทร์`(ศรีสะเกษ)/`เมืองปาน`(ลำปาง)/`เมืองยาง`/`เมืองสรวง` ที่ไม่ใช่อำเภอเมือง. รวมครอบ 126/928 อำเภอ + ช่วยแขวง กทม. อีก 35/178 (แขวงหลายแห่งชื่อเดียวกับเขต). **อำเภอต่างจังหวัดที่เหลือ ~800 ชื่อไม่ใส่มั่ว** — คำที่เดาเองในตารางที่ดู "ทางการ" อันตรายกว่าปล่อยตัวถอด เพราะผู้ใช้จะไม่เอะใจไปตรวจ; ทางออกคือ `AddressEn` ที่กรอกทับได้. เทสต์ `PartyEnglishTextTests` |
| **ที่อยู่บนเอกสารทุกชนิด (ต./อ./จ. · แขวง/เขต)** | `ThaiAddressFormatter.Format` — **ตัวประกอบที่อยู่ตัวเดียวของทั้งระบบ** ห้ามเขียน `string.Join` เอง. เดิมมี 5 ตัวแยกกัน (PdfGeneration / WithholdingTaxCert / DocumentService.ComposeAddress / CompanyService.ComposeAddress / EtaxInvoice PdfA3) และ 4 ตัวไม่ใส่คำนำหน้า → 50 ทวิ + ใบกำกับพิมพ์ "44 หมู่ 9 หนองเหียง พนัสนิคม ชลบุรี 20140" ผิดข้อกำหนดเอกสารราชการ. ตอนนี้ทุกตัว delegate มาที่นี่หมด. ความสามารถ: เติมคำนำหน้า · กทม.→แขวง/เขต + ไม่มี "จ." · parse free-text ที่ไม่มีคำนำหน้ากลับเป็น structured · **กู้จังหวัดจากรหัสไปรษณีย์** ผ่าน `ThaiAdminCodes` เมื่อที่อยู่ไม่ได้เขียนชื่อจังหวัดไว้เลย (ลอง token รองสุดท้าย = ตำบลก่อน — อำเภอที่มีตำบลชื่อเดียวกันจะไม่กลืนชื่อตำบลจริง) · กันพิมพ์ตำบล/จังหวัดซ้ำ · ยุบ "กทม กรุงเทพมหานคร". เทสต์: `Accounting.Tests/ThaiAddressFormatterTests.cs` |
| **รหัส/ป้ายสาขาบนเอกสาร (§86/4 · ประกาศอธิบดีฯ ฉบับที่ 199)** | `Helpers/TaxBranchCode.cs` — **resolver กลางตัวเดียว**: `TryNormalize` (เติมศูนย์ให้ครบ 5 หลัก · ปฏิเสธรูปแบบผิดพร้อมข้อความไทยที่อ้างประกาศฯ) · `Label`/`LabelWithName` → "สำนักงานใหญ่" / "สาขาที่ 3" (ไทย) หรือ "Head Office" / "Branch 3". **ที่มา**: สูตร `code == "00000" ? ... : ...` เคยเขียนซ้ำ 3 ที่แล้วผลไม่ตรงกัน — `PdfGenerationService.PdfA3` (ทั้งฝั่งผู้ขายและผู้ซื้อ) พิมพ์ **เลขดิบ** `00003` แทนถ้อยคำที่ประกาศฯ กำหนด และ `DocumentBrandController.FormatBranchLabel` พิมพ์ "สาขาที่ 00003" (ศูนย์นำหน้าติดมา) ⇒ ใบกำกับภาษีระบุสถานประกอบการไม่ตรงแบบ. รหัสที่แปลงเป็นเลขไม่ได้ (ข้อมูลเก่าเพี้ยน) โชว์ตามที่เก็บไว้ — **ห้ามกลบเป็น "สำนักงานใหญ่"** เพราะจะรายงานภาษีผิดสาขา. ทะเบียนสาขา (`DimensionalAccountingService`) ใช้ตัวเดียวกันเป็นด่านตอนบันทึก. เทสต์: `Accounting.Tests/TaxBranchCodeTests.cs` |
| **ภาษีถูกหัก ณ ที่จ่าย → เครดิต ภ.ง.ด.50/51** | ✅ **สร้างแล้วครบ 5 เฟส** — ดู `WHT_CREDIT_PLAN.md`. ฝั่ง "เราหักคนอื่น" (ใบสำคัญจ่าย → 50 ทวิ ที่เราออก → ภ.ง.ด.3/53) ✅ เดิมมีอยู่แล้ว; ฝั่ง **"เราถูกหัก"** เพิ่มใหม่: ทะเบียน `WhtCreditReceived` (ตาราง `WhtCreditsReceived`) ← `DocumentService.SyncWhtCreditReceivedAsync` **อ่านจาก GL 11910 จริง** (ไม่ใช่จากช่องบนฟอร์ม — ยอดในทะเบียนกับสมุดบัญชีจึงไม่มีทางแตกกัน) · `WhtCreditService` + `WhtCreditController` (`/api/companies/{cid}/wht-credits`) + หน้า `wht-credit.html` แนบสแกนหนังสือรับรอง · `GenerateCitReport` **หักเครดิตจริงแล้ว** โดยนับเฉพาะ Received/Claimed — **Pending (ยังไม่ได้ใบ) แสดงแต่ `TaxAmount = 0`** ตาม L1 (ไม่มีใบ = เครดิตไม่ได้ตามกฎหมาย) · `SummaryAsync` กระทบทะเบียน↔ยอด 11910 ในงบ แล้วรายงานผลต่าง (ต่าง = มีรายการลงบัญชีแต่ไม่อยู่ในทะเบียน ต้องตามให้เจอก่อนยื่น) · `SettleYearEndAsync` (`POST /wht-credits/settle-year-end`) ลง JE ปิดปีล้าง 11910 ตามที่ใช้จริง (ใช้หักภาษี/ขอคืน/ตัดสูญ) มี guard ห้ามล้างเกินยอดคงเหลือ; **ยกไปปีหน้า = ไม่ต้องเรียก** (คงยอดไว้เฉย ๆ) · `CheckRate` เตือนเมื่อผู้จ่ายหักผิดอัตรา ท.ป.4/2528 (รวมอัตราพิเศษ: โฆษณา 2% · ขนส่ง 1% · เงินปันผล 40(4)(ข) 10% — ต้องตรวจ**ก่อน** 40(8) มิฉะนั้นถูกกลืนแล้วเตือนผิด) · **OCR สแกน 50 ทวิ** → `EnsureWhtCreditFromCertAsync` สร้างแถวทะเบียนสถานะ Received ให้เลย (มีใบจริงอยู่ในมือแล้ว) พร้อม `InferIncomeTypeCode` อ่านประเภทเงินได้จากกระดาษเพื่อป้อนให้ `CheckRate` — **คืน null เมื่อเจอหลายประเภทพร้อมกัน** เพราะแบบ 50 ทวิ พิมพ์สำเร็จมีหัวข้อ 1–6 ครบอยู่บนฟอร์มเปล่า (จับคำตรง ๆ = เจอทุกประเภท) และ **ห้ามเดาประเภทจากอัตราที่หัก** เพราะจะทำให้ CheckRate ตรวจกับตัวเองแล้วผ่านทุกครั้ง = ปิดตัวตรวจโดยไม่รู้ตัว |
| **เลขที่ใบกำกับที่อ้างอิง (ฝั่งซื้อ)** | ใบกำกับตัวจริงของฝั่งซื้อเป็น**เอกสารของผู้ขาย** ⇒ เลขที่/วันที่ที่มีผลทางภาษี คือ `SupplierInvoiceNumber`/`SupplierTaxInvoiceDate` **ไม่ใช่** `DocumentNumber` (เลขรันภายในเรา ที่สรรพากร/ผู้ขายไม่รู้จัก). รายงาน ภ.พ.30 ฝั่งซื้อใช้ถูกอยู่แล้ว (`TaxService` enrich) แต่ **กล่องอ้างอิง §86/9-10 บนกระดาษเคยใช้เลขภายในของเรา** — แก้แล้วให้ใช้เลขผู้ขาย เมื่อใบต้นทางเป็นชนิดฝั่งซื้อ (`IsPurchaseSideDocType`) และตกกลับเลขภายในเมื่อไม่มีเลขผู้ขาย (พฤติกรรมเดียวกับรายงาน — ไม่ให้ 2 ที่ขัดกัน). เพิ่มบรรทัด "เลขที่ใบกำกับภาษีของผู้ขาย" บนเอกสารฝั่งซื้อที่พิมพ์ออกมาด้วย (เดิมมีแต่ในหน้ารายละเอียด) |
| **เอกสารภาษาอังกฤษ — HTML renderer เคยฝังคำไทยตาย 20 จุด** | `DocumentLabels` มีคำครบทั้งไทย/อังกฤษ (75 key) และ **QuestPDF ใช้ครบ** แต่ **HTML renderer (เส้นหลัก: Chromium HTML→PDF + print preview) ฝังข้อความไทยไว้ตรง ๆ** ⇒ ตั้งภาษาอังกฤษแล้ว เอกสารออกมาปนไทย และ**หน้าตาต่างกันตามว่าเรนเดอร์ด้วยตัวไหน** (Chromium เปิด = ปนไทย · fallback QuestPDF = อังกฤษถูก) — defect class "สอง renderer drift" ที่โปรเจกต์เจอซ้ำ. จุดที่หลุด: ลายน้ำยกเลิก · ป้ายต้นฉบับ/สำเนา · เลขผู้เสียภาษี/โทร (ทั้งผู้ขายและผู้ซื้อ) · กล่องอ้างอิง CN/DN (มูลค่าเดิม/ที่ถูกต้อง/ผลต่าง/ลด/เพิ่ม) · หมายเหตุ "ไม่ใช่ใบกำกับภาษี" · บล็อกใบรับรองแทนใบเสร็จทั้งชุด · ข้อมูลชำระเงิน · เงื่อนไขการชำระเงิน + "เครดิต N วัน". แก้ให้ทุกจุดใช้ `L.*` แล้ว + เพิ่ม 2 label ที่ยังไม่มี (`payment_terms` และ `CreditDaysText(days)` ซึ่งต้องเป็นเมธอดเพราะรูปประโยคสลับที่: ไทย "เครดิต 30 วัน" / อังกฤษ "30 days credit"). **กันซ้ำ**: `DocumentLabels.Keys` + เทสต์ใน `DocumentLabelsTests` — key ไทย/อังกฤษต้องเท่ากัน · ฝั่งอังกฤษห้ามมีอักษรไทย (U+0E00–U+0E7F) · ห้ามมี key ที่ indexer คืนชื่อ key กลับมา · `LegalTitle` ต้องยังคงคำไทยไว้ตาม §86/4. **แบบฟอร์มราชการ (50 ทวิ ฯลฯ) คงเป็นไทยตามแบบ** ไม่แตะ. **รอบตรวจศัพท์ (audit ครั้งที่ 2 — "คำไหนแปลผิดบ้าง")**: เทสต์เดิมตรวจได้แค่ "ครบ + ไม่ปนภาษาไทย" ตรวจ**ความถูกต้องของศัพท์**ไม่ได้เลย. ไล่ทีละคำทั้ง 75 key + ชื่อเอกสาร 16 ชนิด + ป้ายลายเซ็น พบ 2 คำผิดจริง: `total_bill_discount` = "Bill discount" (ศัพท์การเงิน = **การขายลดตั๋วเงิน** คนละเรื่องกับส่วนลดท้ายบิล → "Invoice discount") และ `dn_reason_adjustment` = "under-calculated" (**ไม่ใช่คำอังกฤษจริง** → "Correction (amount undercharged)"); อีก 5 จุดแปลถูกแต่ตกสาระ: เหตุผล CN/DN ที่ย่อจนสรรพากรตรวจย้อนไม่ได้ ("Adjustment"/"Write-off" โดด ๆ ทั้งที่ไทยระบุ "ค่าสินค้าน้อยกว่าที่ตกลง"/"บางส่วน"), `our_doc_ref` "Our document" → "Our ref.", ป้ายลายเซ็น "Authorized" → "Authorized Signature" (6 จุด), หัวใบ Expense → "Expense Record". **กันซ้ำเพิ่ม**: `DocumentLabelsTests` มีตาราง `BannedEnglishTerms` (คำที่พิสูจน์แล้วว่าผิด + เหตุผลกำกับ) และเทสต์ว่าเหตุผล CN/DN ต้องไม่เป็นคำเดี่ยวกว้าง ๆ |
| **ออกเอกสารเป็นอังกฤษ "เฉพาะใบเดียว"** | resolver กลางรองรับ 4 ชั้นมาตั้งแต่ต้น แต่ **UI เปิดให้ตั้งได้ชั้นเดียว (ทั้งบริษัท)** ⇒ คำถามที่ผู้ใช้ถามจริง ("อยากออกใบเดียวเป็นอังกฤษ") ทำไม่ได้เลย นอกจากยิง API เอง. อันตรายกว่านั้นคือทางแก้ที่ผู้ใช้จะคิดเอง — สลับค่าบริษัทเป็น en แล้วสลับกลับ — **ทำให้ใบเก่าทุกใบที่ `DocumentLanguage=null` เปลี่ยนภาษาตามระหว่างนั้น** เพราะภาษาถูก resolve ตอนพิมพ์ ไม่ได้ freeze ไว้ตอนออกใบ. เติมทางเข้า 2 ชั้นที่ขาด: **รายใบ** (`#fDocumentLanguage` ในฟอร์มสร้าง/แก้ — ตรึงถาวรกับใบ) และ **ครั้งนี้ครั้งเดียว** (`#pdfLangMode` ใน modal พรีวิว → `GeneratePdfRequest.Language` — ใช้กับใบที่อนุมัติแล้วแก้ไม่ได้ ซึ่งเป็นเคสจริงที่พบบ่อยกว่า: ลูกค้าขอฉบับอังกฤษหลังออกใบไปแล้ว) ต่อครบทั้ง 4 ทางออกของหน้าเอกสาร + reset ทุกครั้งที่เปิด modal/ฟอร์มใหม่ (ไม่งั้น "en" ค้างข้ามใบ → ลูกค้าไทยรายถัดไปได้ใบอังกฤษเงียบ ๆ). **บั๊กที่เจอระหว่างต่อสาย**: `DocumentResponse` ไม่เคยคืน `DocumentLanguage` ⇒ เปิดแก้ใบอังกฤษแล้วกดบันทึก ภาษาถูกล้างกลับเป็นค่าบริษัท และ `GenerateDocumentPdfAsync` คำนวณภาษาของ **metadata Title ใน PDF/A-3** เอง (`request.Language ?? template.Language ?? "th"`) โดยข้ามทั้งภาษาที่ตรึงกับใบและค่าบริษัท ⇒ ไฟล์ e-Tax ใบเดียวมีเนื้อหาอังกฤษแต่ Title ไทย — แก้ให้เรียก `ResolveDocumentLanguage` ตัวเดียวกับ renderer (กฎ "resolver กลาง ห้ามคำนวณเอง"). **รอบตาม (audit ทางเข้าอนุพันธ์)**: ภาษาที่ตรึงกับใบต้องไหลตามใบลูกทุกทาง ไม่งั้นลูกค้าต่างชาติได้ QT อังกฤษแต่ INV/REC กลับเป็นไทยเงียบ ๆ — เติม 5 จุด: (1) `ConvertCoreAsync` propagation block (QT→INV→REC ทั้งสาย) (2) `DocumentCloneController` ทั้ง POST + preview (ลูกค้าประจำที่ออกบิลซ้ำคือกลุ่มที่ใช้ clone บ่อยสุด) (3) ใบเสร็จ settlement อัตโนมัติ (`IsSettlementReceipt`) ตามภาษาใบกำกับต้นทาง (4) CN คืนมัดจำ ตามภาษาใบเสร็จมัดจำ (5) recurring generator อ่าน `documentLanguage` จาก `TemplateData` (forward-compatible — template เก่าไม่มี key = null = ตามค่าบริษัท เหมือนเดิมเป๊ะ; ฟอร์ม recurring ยังไม่มีช่องให้ตั้ง). **ข้ามโดยตั้งใจ**: CrossTenantWorkflow (B2B ภายในระบบ ไทยทั้งคู่) · shadow doc ตรวจงบ PO (ไม่ persist) · เอกสารจาก OCR/Import/Integration/CMS/POS (ไม่มีใบต้นทางให้สืบภาษา → ตามค่าบริษัท). ทางออก server ตรวจแล้วส่ง `Language=null` ทุกตัว (อีเมลแนบ PDF อัตโนมัติ · portal ลูกค้า · บิล SaaS · mobile-receipt) = resolver หยิบภาษาที่ตรึงกับใบให้เองถูกอยู่แล้ว |
| **เครดิต NextAcc ท้ายเอกสาร (เฉพาะบัญชีแพ็กเกจฟรี)** | ข้อความจาง ๆ 2 บรรทัดมุมขวาล่างทุกหน้า: "จัดทำด้วย **NextAcc** · ระบบบัญชีออนไลน์ / เริ่มใช้ฟรีที่ www.nextacc.net" (7–7.5pt สีเทา ไม่แย่งสายตาจากเนื้อหา). **เกณฑ์ตัดสิน `PdfGenerationService.IsFreeTierAsync` — เกณฑ์เดียว: ราคารายเดือนของ*แพ็กเกจ* = 0** (ครอบทั้งทดลองใช้และแพ็กเกจฟรีตลอดชีพ). บริษัทใต้ License ของผู้ใช้ → ใช้แพ็กเกจของ License (`AccountSubscription.PlanTemplate.MonthlyPrice`); ไม่งั้นดู `PlanTemplate` ตาม `Subscription.Plan`. **ตั้งใจไม่ไปดูว่าบริษัทนี้เคยจ่ายเงินจริงไหม / ธง `IsPermanentFree` บนแถว subscription เป็นอะไร** — ราคาแพ็กเกจบอกครบแล้ว เป็นค่าที่ admin ตั้งเองในหน้าจัดการแพ็กเกจ และไม่เพี้ยนตามประวัติการจ่ายเงินรายบริษัท (ธงบนแถวเคยค้างผิดบนแพ็กเกจเสียเงินมาแล้ว — บั๊ก sync รอบ 10). หาแพ็กเกจไม่เจอ/อ่านข้อมูลไม่ได้ → **ไม่พิมพ์** (ยกเว้นแพ็กเกจ `FreeTrial` ที่ถือว่าฟรีเสมอแม้ไม่มี template) — พลาดฝั่ง "ไม่โฆษณา" ปลอดภัยกว่าพลาดฝั่ง "โฆษณาใส่คนจ่ายเงิน". **ต่อสายครบทุกทางออก** — คิดค่าครั้งเดียวใน `GenerateDocumentPdfAsync` แล้วส่งเข้า: e-Tax PDF/A-3 · Chromium HTML→PDF · QuestPDF · เส้นสำรอง HTML→QuestPDF · print preview (`GenerateDocumentHtmlAsync`) · พรีวิวเทมเพลตทั้ง 3 ทาง — ถ้าลืมเส้นใดเส้นหนึ่ง เอกสารจะมี/ไม่มีเครดิตไม่เหมือนกันแล้วแต่ว่าออกทางไหน. **ไม่แปะบนแบบฟอร์มราชการ** (50 ทวิ / ภ.พ.30 ฯลฯ) — เป็นแบบของกรมสรรพากร ห้ามเติมข้อความของเราเอง |
| **OCR ราคารวม VAT (Case A) — `Line.Amount` ต้องเป็นยอดก่อน VAT** | ใบที่ราคา/หน่วยรวม VAT แล้ว (IKEA, ห้างค้าปลีก) ตัวตรวจ Case A ตั้ง `PricesIncludeVat=true` ถูกแล้ว แต่ **เก็บ `Line.Amount` เป็นยอดรวม VAT ตรง ๆ** ขณะที่ `Document.SubTotal` เก็บยอด net จากหัวกระดาษ ⇒ สองค่าขัดกันในใบเดียว (Σ Line.Amount = 1,396 แต่ SubTotal = 1,304.68) และตอนอนุมัติ JE ฝั่งซื้อลง `Dr ค่าใช้จ่าย = Σ Line.Amount` (ซึ่งรวม VAT อยู่แล้ว) **+ `Dr ภาษีซื้อ` อีกรอบ** เทียบกับ `Cr เจ้าหนี้ = TotalAmount` ⇒ **เดบิตเกินเครดิตเท่ายอด VAT พอดี** → "การบันทึกบัญชีอัตโนมัติไม่สมดุล" (เคสจริง: Dr 1,487.32 ≠ Cr 1,396.00 · ผลต่าง 91.32 = VAT). แก้ให้เก็บ `Amount = ยอดบรรทัด − VAT ของบรรทัด` ตาม convention เดียวกับ `DocumentService.ComputeLineAmounts` (`Amount = NetAmount` · `UnitPrice` คงเป็นราคารวม VAT). **ยังยึด VAT บนกระดาษเป็นหลัก** (pro-rate ตามสัดส่วน บรรทัดท้ายรับเศษ) ไม่คิดใหม่จากอัตรา 7% รายบรรทัด เพราะยอด VAT ต้องกระทบกับที่ผู้ขายยื่นใน ภ.พ.30 ได้ — คิดจากอัตราจะต่างกระดาษได้ 0.01 บาท. **ใบเก่าที่สร้างไปแล้ว**: เปิดแก้ไขแล้วกดบันทึก → `UpdateDocumentAsync` คำนวณใหม่ผ่าน `ComputeLineAmounts` → ยอดกลับมาสมดุลเอง |
| **คำเตือนหัก ณ ที่จ่าย ไม่ควรยิงใส่ใบซื้อสินค้า** | §3 เตรส ใช้กับ **ค่าบริการ/ค่าเช่า/ขนส่ง/โฆษณา/จ้างทำของ** — **การซื้อสินค้าไม่ต้องหัก**. เดิมเตือนทุกใบฝั่งซื้อที่ยอด ≥ 1,000 โดยไม่ดูว่าซื้ออะไร ⇒ เตือนผิดแทบทุกใบซื้อของ (เคสจริง: ซื้อปลอกหมอนจาก IKEA) ⇒ ผู้ใช้ชินกับการกดข้ามคำเตือน แล้ววันที่เตือนถูกจริงก็ข้ามไปด้วย. เพิ่ม `IsPureGoodsPurchaseAsync` — เงียบเมื่อ **ทุกบรรทัดที่มียอด** ผูกสินค้าที่ตัดสต๊อก (`Product.TrackStock`) หรือลงผังสินค้าคงเหลือ/ต้นทุนสินค้า (115x / 51xxx); บรรทัดยอด 0 (ของแถม/บริการฟรี) ไม่นับ. **สัญญาณไม่พอ = เตือนตามเดิม** (ไม่เดาจากคำในรายการ) — พลาดฝั่งเตือนเกินดีกว่าพลาดฝั่งไม่เตือนตอนต้องหักจริง ซึ่งบริษัทต้องรับผิดภาษีแทนผู้รับเงิน. ข้อความแก้ให้บอกทั้งสองทาง (เป็นบริการ→ต้องหัก / ซื้อสินค้า→ข้ามได้) |
| **ใบเสร็จตัดลูกหนี้ ต้องตัดที่ยอด "ก่อนหักภาษี" (เกณฑ์ Cash)** | `CompanySettings.WhtRecognitionBasis` **ค่าเริ่มต้น = Cash** (ยังไม่เคยมี UI ให้ดู/เปลี่ยน — เพิ่มแล้วที่ ตั้งค่า → ภาษี). ใต้เกณฑ์นี้ใบแจ้งหนี้ตั้งลูกหนี้ไว้ **gross**: `Dr AR (TotalAmount + WHT) / Cr รายได้` โดย**ยังไม่แตะ 11910** (`arAmountAtInvoice` ใน `AutoPostToJournalAsync`) ⇒ ใบเสร็จต้องตัด AR ที่ยอด gross = `เงินสด + Dr 11910`. **บั๊กที่เจอ**: `onReceiptSourceSelect` เติมบรรทัดด้วย `balanceDue` ซึ่งเป็นยอด **net** (`TotalAmount = subTotal + VAT − WHT`) และ WHT 0% ⇒ JE ได้ `Dr เงินสด 3,492 / Cr AR 3,492` ขณะที่ AR ตั้งไว้ 3,600 — **เหลือลูกหนี้ค้าง 108 ถาวร** ทั้งที่เอกสารขึ้นว่าชำระครบ (`BalanceDue` คิดจาก TotalAmount ที่หัก WHT ไปแล้ว ⇒ งบดุลกับสถานะเอกสารขัดกันเงียบ ๆ) และ **11910 ไม่เคยถูก Dr** ⇒ ไม่มีแถวใน `WhtCreditReceived` (ตัวมันอ่านจาก GL) ⇒ ใช้เครดิตใน ภ.ง.ด.50 ไม่ได้ = เสียเงินจริง. guard เดิม (`WHT รวมเกินยอดต้นทาง`) จับไม่ได้เพราะกันแค่ "หักเกิน" ไม่ได้กัน "หักขาด". **แก้ 3 ชั้น**: (1) ตัวเติมอ่านเกณฑ์จาก settings — Cash → ยอด gross + อัตรา WHT ของใบต้นทาง (เฉลี่ยตามสัดส่วนที่ยังค้าง เหมือน `recordPayment` ที่ทำถูกอยู่แล้ว), Accrual → net + 0 (เดิมถูกอยู่แล้ว); (2) **guard ตอนอนุมัติ**: ใบเสร็จที่ปิดยอดต้นทางจนหมดแต่ WHT สะสมไม่ครบ → บล็อกพร้อมบอกยอดที่ขาดและวิธีแก้ (หรือให้แก้ WHT ที่ใบต้นทางเป็น 0 ถ้าลูกค้าไม่ได้หักจริง); (3) **ตรวจย้อนหลัง** `FindStrandedArFromMissingWhtAsync` + `GET /wht-credits/stranded-ar` → หน้าทะเบียน WHT ขึ้นแบนเนอร์รายใบที่ค้าง. หมายเหตุ: ทางเดิน `บันทึกชำระเงิน` (`recordPayment` → `CreatePaymentAsync`) คำนวณ WHT ต่องวดถูกมาตั้งแต่ต้น — เฉพาะทาง "แปลง/สร้างใบเสร็จอ้างใบค้าง" ที่พลาด |
| **ใบต้นทางที่ผูกไว้ ต้องแสดงตอนเปิดแก้ไข** | เปิดแก้ใบเสร็จที่แปลงมาจากใบแจ้งหนี้ แล้วช่อง "รับชำระใบค้างของลูกค้ารายนี้ (ตัดลูกหนี้)" โชว์ **"— ใบเสร็จอิสระ (ขายสด: ลงรายได้ + VAT) —"** ทั้งที่ใบนี้ตัดลูกหนี้อยู่ ⇒ หน้าจอ**บอกวิธีลงบัญชีผิดจากความจริง** (คนอ่านเข้าใจว่าลงรายได้ใหม่). สาเหตุ 3 ชั้นซ้อน: (1) `openEdit` ไม่เคย hydrate ค่านี้จาก `relatedDocumentId` (2) ตัวโหลดลิสต์กรองเฉพาะใบที่ **`balanceDue > 0`** — พอใบเสร็จถูกอนุมัติ ใบต้นทางกลายเป็นชำระครบจึงหลุดจากลิสต์ ต่อให้ set ค่าก็ไม่มี option ให้เลือก (3) ตัวโหลดเป็น async และล้าง `<select>` ทุกครั้ง ⇒ ค่าที่เซ็ตไว้ก่อนถูกล้างทิ้ง. แก้ด้วยการ **ปักหมุด** (`_pinnedSourceId`/`_pinnedSourceDoc` เซ็ตใน `openEdit` ก่อนตัวโหลดใดจะเริ่ม) แล้ว `_applyPinnedSource` เติม option กลับ + เลือกให้ **ท้ายตัวโหลดทั้งสองตัว นอก try** (ใบที่ผูกไว้ต้องแสดงแม้โหลดลิสต์ล้มเหลว) — ใช้ร่วมกันทั้งใบเสร็จ-ตัดลูกหนี้ และ CN/DN §86/9-10. **ล็อกไม่ให้เปลี่ยนตอนแก้ไข**: `UpdateDocumentAsync` ไม่รับ `RelatedDocumentId` (รับเฉพาะตอนสร้าง) และ `save()` ก็ส่งเฉพาะตอนสร้างอยู่แล้ว ⇒ เดิมเลือกเปลี่ยนได้ แต่กดบันทึกแล้ว**ไม่มีผลอะไรเลยแบบเงียบ ๆ** — ตอนนี้ล็อก + บอกเหตุผลและทางออก (ยกเลิกแล้วสร้างใหม่จากใบต้นทางที่ถูก). radio ฝั่ง CN/DN ก็ล็อกตามใบที่ผูกไว้ด้วย |
| **§90/2 บริษัทไม่จด VAT — ทุกทางที่ไปถึง "ใบกำกับภาษี" ต้องปิดครบ** | ผู้ไม่จดทะเบียนออกใบกำกับภาษีไม่ได้เลย (มีโทษ §90/2). เดิมบล็อกจริงแค่ **2 ทาง** คือ ตัวเลือกในฟอร์มสร้าง (`applyVatRegistrationLock`) + hard block ตอน `ApproveDocumentAsync` — ส่วนทางอื่นเปิดโล่ง: **แปลงเอกสาร** (กล่อง "แปลงเอกสาร" อ่านรายการจาก `ValidConversions` ซึ่งไม่รู้จักสถานะ VAT), **แปลงทั้งหมด (batch)**, และติ๊ก **"ออกเป็นใบแจ้งหนี้/ใบกำกับภาษี ใบเดียว"** (force type=TaxInvoice ตอนบันทึก) ⇒ ผู้ใช้แปลง/ติ๊กได้ กรอกจนครบ แล้วไปตายตอนกดอนุมัติ. ตอนนี้ปิดครบทุกทาง: `ValidateConversionAsync` บล็อก targetType=TaxInvoice (ชั้นบังคับจริง — ครอบ API/integration ด้วย) · `GetValidConversionTargetsAsync` กรองออกจากรายการที่ส่งให้ UI (`GET /conversion-targets`) · client กรองซ้ำในกล่องแปลง (เผื่อ backend เก่า) · batch dropdown ถอดตัวเลือกออก · ติ๊กใบเดียวซ่อนเมื่อไม่จด VAT. **กติกาค่าเริ่มต้น**: `CompanySettings.VatRegistered` ไม่มีค่า = ถือว่าจด (`?? true`) — บริษัทเดิมที่ยังไม่เคยแตะหน้าตั้งค่าจะไม่ถูกบล็อกโดยไม่รู้ตัว. ข้อความ error บอกทางออกเสมอ (แปลงเป็นใบแจ้งหนี้/ใบเสร็จแทน · เปิดที่ ตั้งค่า → ภาษี) |
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

_รอบ 17: Payroll import endpoint (TakeTime) — POST /payroll/runs/import รับ
ยอดสำเร็จรูปต่อพนักงาน → run สถานะ Calculated (ไม่ recalc) → approve/pay/
exports เดิมออก GL+ภงด.1+สปส.1-10+50ทวิ+payslip จากยอดที่ส่งมา. idempotent
(ExternalRunRef + unique index), validate net=gross−หักลูกจ้าง, account override
(salary/payment code) ลง JE. + integration outbound document attachments[]._

_รอบ 18: OCR contact address ครบ + กทม. แสดง แขวง/เขต ถูกต้อง. ปัญหา: OCR ผ่าน
API → เอกสารที่อยู่ กทม. ขึ้น "ตำบล/อำเภอ" (ผิด ต้องเป็น "แขวง/เขต") + ผู้ติดต่อ
ไม่มีที่อยู่จนกด "ดึงข้อมูล" เอง. แก้ 4 ชั้น:
(1) `OcrService.EnrichContactAddress` — contact ที่ match จากของเดิม เติมทั้ง
free-text + structured (เดิมเติมแค่ free-text) จาก DbdAddress ก่อน VendorAddress;
ทับเฉพาะเมื่อ DBD ยืนยัน + contact OCR-managed.
(2) `PdfGenerationService.FormatThaiAddress` — เมื่อ structured locality ว่าง
parse free-text ผ่าน `ThaiAddressParser` ตอน render → กทม. ได้ แขวง/เขต ครบทุก
เอกสารโดยไม่ต้อง migrate; แก้ token-strip เป็น word-aware (เดิม substring replace
ทำ "บางนาตราด"→"ตราด"); 50ทวิ payee/company address route ผ่าน FormatThaiAddress.
(3) `ThaiAddressParser` — StreetRegex/BuildingNameRegex หยุดที่ marker เขตปกครอง
(ไม่กลืนชื่อตำบล) + `ExtractStreetHead` รักษาส่วนหัวเต็ม (ห้อง/ชั้น/อาคาร/ซอย/ถนน).
(4) `DocumentService.GetContactAsync` — lazy backfill structured จาก free-text
ตอนเปิดฟอร์ม (self-heal contact เก่า ไม่ต้องกด "ดึงข้อมูล")._

_รอบ 19: OCR header subtotal ผูกกับ grand total — แก้ "รายงานยอด 530 แต่ใบพิมพ์/
JE = 630". เคส: OCR แกะ subtotal (630) ไม่ตรง grand total (530) โดยไม่มี VAT/ส่วนลด
อธิบาย → `CreateDocumentFromScanAsync` fallback line (items==0) ใช้ headerSubTotal
(630) แต่ document.TotalAmount = headerTotal (530) → report (อ่าน TotalAmount) ≠
print/JE (อ่าน line). แก้: headerSubTotal ใช้ ExtractedSubTotal เฉพาะตอน tie กับ
grand total (subtotal+VAT−discount=total); ไม่งั้น derive จาก grand total (ตัวเลข
ที่พาร์ทเนอร์/ใบส่งมา = ตัวตั้งต้นเชื่อถือได้สุด) → line/subtotal/total แตกกันไม่ได้.
หมายเหตุ: แก้เฉพาะ doc ที่สร้างใหม่ — เอกสารเดิมที่ผิดต้องลบแล้ว re-OCR._

_รอบ 20: OCR เชื่อค่าเงินจากระบบภายนอก (override OCR vision). `ScanAsync` →
`ApplyExternalAmountOverrides(extractedData, metadata)` หลัง EnrichFromRawText:
อ่านยอดที่พาร์ทเนอร์กรอกมาใน metadata (top-level หรือ nested "amounts") —
total/totalAmount/grandTotal/amount, subTotal, vat/vatAmount, wht/whtAmount|whtRate,
และ "lineItems":[{description,quantity,unitPrice,amount}] — เขียนทับค่าที่ OCR แกะ
จากรูป (กันอ่านเลขผิด 530↔630) + ตั้ง FieldConfidence=1.0 + re-sync เข้า scanResult
ก่อน dup-check/serialize/auto-create. เพิ่ม body metadata ให้ endpoint POST
/ocr/scan/{fileId} (flow 2 ขั้น) ด้วย (`OcrScanMetadataRequest{Metadata,Engine}`).
fallback chain กฎเหล็ก #3: partner-provided > OCR vision. Fail-safe: metadata
เพี้ยน → คงค่า OCR._

_รอบ 21: ภ.พ.30 ภาษีซื้อ = 0 ทั้งที่มียอด — `GenerateVatReport`
(`TaxService.cs`) loop จัด input VAT จาก PurchaseInvoice/Expense/CertificateInLieu
เท่านั้น **ไม่มี branch ของ PaymentVoucher** → ใบสำคัญจ่ายที่ติ๊ก "ใช้งานใบกำกับ
ภาษี" (HasTaxInvoiceReference=true) ภาษีซื้อตกหล่นทั้งหมด (JE ของ PV มี
SourceDocumentId → JE-only fallback ก็ข้าม). แก้: เพิ่มเงื่อนไข
`|| (DocumentType==PaymentVoucher && HasTaxInvoiceReference)` เข้า branch ภาษีซื้อ
(ใช้ §82/5 prohibited + §82/3 window เดิม). PV ที่ไม่ติ๊ก = ไม่เคลม (§82/5(1)).
มีผลทั้งจอ + CSV ยื่น (ComputeVatReportAsync → GenerateVatReport ตัวเดียวกัน)._

_รอบ 22: หน้านำส่งภาษี/ประกันสังคมรวม (StatutoryRemittance) — สปส.1-10 + ภงด.1/3/53
+ ภพ.30 ในที่เดียว (pattern QuickBooks Pay Liabilities). `StatutoryRemittanceService`
.GetDashboardAsync รวมยอดค้าง (SSO=PayrollRun Paid ที่ยังไม่ settle; ภงด.1=Total
WithholdingTax; ภงด.3/53=เอกสาร WHT แยกชนิดผู้ติดต่อ; ภพ.30=ComputeVatReportAsync
.NetVat) + กำหนดยื่น/overdue/เงินเพิ่ม §49. .RemitAsync post JE ล้างหนี้ค้างจ่าย/Cr
ธนาคาร (VAT: Dr 21911/Cr 11610/Cr ธนาคาร net), บันทึก remittance (unique/งวด กัน
จ่ายซ้ำ), stamp PayrollRun.SsoSettledAt, แนบใบเสร็จ (FileAttachment "StatutoryRemittance").
รองรับทั้งบริษัทรันเงินเดือนในระบบ (ตั้งค้างจ่าย 21815 อัตโนมัติ) + ทำข้างนอก.
หน้า /pages/tax-remittance.html. Endpoints GET/POST /companies/{id}/remittances._

_รอบ 23 (ชุดแก้ + ปรับปรุง): (a) รายงานภาษีซื้อ/ขายบนจอ + พิมพ์ → ฟอร์มราชการ §87
(ฉบับที่ 104): GetTaxReportAsync เติม InvoiceNumber/BranchCode/CompanyName-TaxId ต่อ
บรรทัด; tax.html ตารางคอลัมน์ราชการ + ปุ่ม "พิมพ์ฟอร์มราชการ". (b) WHT cert 50ทวิ:
GenerateWithholdingTaxCertPdfAsync ลอง render HTML (BuildWithholdingTaxCertHtml +
ลายเซ็น) ผ่าน IHtmlPdfRenderer/Puppeteer ก่อน → fallback QuestPDF (font Sarabun:
ThaiFontCandidatePaths เพิ่ม Windows/macOS/Fonts bundle); auto-attach เข้า PV ปิด
default ผ่าน CompanySettings.AutoAttachWhtCertPdf. (c) StatutoryRemittance ภพ.30
เปลี่ยนเป็นอ่าน TaxReports ที่ generate แล้ว (เลิกคำนวณสด — กันหน้าค้าง) + per-section
try/catch. (d) documents list: server-side types[] filter + DocumentPermissionHelper
Other=rev&&pur (กัน paging หายสำหรับ owner) + count bar. (e) OCR amount: external
metadata override (เชื่อยอดที่ partner ส่ง) + headerSubTotal ผูก grand total._

_รอบ 24 (UX หน้านำส่งภาษี/ประกันสังคม): (a) ถอด Floating Action Button "＋ Quick"
ออกทั้งระบบ (layout.js — ปุ่มลอยมุมขวาล่างบังเนื้อหา; ทางลัดยังอยู่ใน sidebar +
mobile bottom-nav). (b) "แหล่งเงิน (บัญชีจ่าย)" ในโมดัลนำส่ง: เพิ่ม payment channels
ครบ — bank accounts (optgroup, value `bank:<id>` → LinkedAccountId) + GL เงินสด/
ช่องจ่ายอื่น (getPaymentChannels, value `account:<id>` → ใช้เป็นผัง Cr ตรง ๆ).
RemitRequest เพิ่ม `BankGlAccountId`; ResolveBankGlAsync validate GL เป็นผังบริษัทนี้
+ active + level≥4 ก่อนใช้. (c) แนบเอกสารที่จ่าย/ใบเสร็จได้ในโมดัลนำส่งเลย (input
`rmDoc`) → หลัง RemitAsync สำเร็จ auto-upload เข้า FileAttachment "StatutoryRemittance"
ใน flow เดียว (ไม่เลือกไฟล์ → แสดง step แนบภายหลังเหมือนเดิม)._

_รอบ 25 (payroll import loop — เห็นรายคน): POST /payroll/runs/import (ระบบนอก เช่น
TakeTime ส่งยอดสำเร็จรูป recalculate=false → run สถานะ Calculated) เดิมหน้า "ดู" รอบ
เงินเดือนตารางรายคนว่าง เพราะ GetPayrollRunAsync (PayrollRunResponse) ไม่คืน detail
lines. แก้: PayrollRunResponse เพิ่ม `Details` (List<PayrollRunLineDto>) — เติมเฉพาะตอน
ดึง run เดี่ยว (list ปล่อย null) + `ExternalSystem`/`ExternalRunRef`. payroll.html
viewRun แสดงรายคน + ปุ่มดูสลิป/50ทวิ ได้ครบทั้ง run ที่สร้างในระบบและ import; runs
list ติด badge "↧ <ระบบนอก>". ลูปต่อ (approve → pay → settle-sso → payslip → ภงด.1 →
50ทวิรายปี) ครบเหมือน run ปกติ — import ไม่ auto-post ต้องกด approve/pay ในระบบเอง._

_รอบ 26 (แหล่งจ่ายเงินรายคน): เดิม ProcessPaymentAsync ลง Cr เงินสด/ธนาคารบรรทัด
เดียวรวมทั้ง run (run.NetPaymentAccountCode → default 11122/111x) — จ่ายทุกคนจากบัญชี
เดียว. แก้ให้แยกรายคน: PayrollDetail เพิ่ม `NetPaymentAccountCode` (migration). import
เก็บ PaymentAccountCode รายคนลง detail (เลิกยุบเป็นค่าเดียว). Pay → group ยอดสุทธิ
(NetPay − AdvanceRecovered) ตามบัญชีจ่ายของแต่ละคน (fallback detail → run → default)
→ ลง Cr หลายบรรทัดตามบัญชี. ผู้ใช้แก้แหล่งจ่ายรายคนได้ก่อนจ่าย (Calculated/Approved)
ผ่าน PUT /payroll/runs/{id}/employees/{empId}/payment-account (validate 111x/1133/2123)
→ หน้า run detail dropdown ราย row. PayrollRunLineDto เพิ่ม NetPaymentAccountCode._

_รอบ 27 (แก้ 2 จุดหน้า run detail): (a) dropdown แหล่งจ่ายรายคน "ไม่มีบัญชีธนาคาร" —
GetPaymentChannelAccountsAsync ตัด 1112x (ธนาคาร) ออกโดยตั้งใจ (ธนาคารมาจาก
getBankAccounts แยก). payroll.html viewRun โหลด getBankAccounts ด้วย → optgroup
"บัญชีธนาคาร" (value = LinkedAccountCode) + "เงินสด/ช่องทางอื่น" (payment channels).
(b) ดูสลิปไม่ขึ้น — iframe payslip ส่ง ?token= แต่ JWT รับ query-token เฉพาะ /hubs +
OCR image และอ่านคีย์ access_token เท่านั้น. Program.cs OnMessageReceived: รับทั้ง
access_token+token และ allow path ที่ลงท้าย /payslip._

_รอบ 28 (สลิปเงินเดือน — แสดง inline + ดีไซน์ใหม่): (a) เดิม GetPayslip ส่ง
File(bytes,ct,fileName) → Content-Disposition: attachment → เบราว์เซอร์ดาวน์โหลด
แทนที่จะ render. แก้: download=false (default) → set inline + File ไม่มีชื่อไฟล์ →
iframe โชว์; download=true → attachment ชื่อไฟล์มีชื่อพนักงาน (สลิปเงินเดือน_<ชื่อ>_
MM-YYYY.pdf). หน้า payslip modal เพิ่มปุ่ม "⬇️ ดาวน์โหลด". (b) GeneratePayslipAsync
สร้าง HTML ดีไซน์ใหม่ (หัวแถบสีธีม PrimaryColor + โลโก้ data-URI, การ์ดข้อมูล,
ตารางรายได้/หัก, กล่อง Net Pay เด่น). **render ด้วย QuestPDF โดยตรง**
(PdfGenerationService.Payslip.cs → GeneratePayslipPdfAsync) ไม่ผ่าน HTML→Chromium
จึงสวยคงที่ทุก server แม้ไม่เปิด Puppeteer; สีธีมดึงจากเทมเพลตใบกำกับ
(AccentColor/TableHeaderColor) + โลโก้จาก CompanySettings.LogoPath._

_รอบ 29 (ดู JE ของรอบเงินเดือน): การจ่ายเงินเดือนลงเป็น JournalEntry 1 ใบ/รอบ
(ProcessPaymentAsync, ref "HR-PR-{year}-{month}", sensitivity=Payroll) ไม่ออกเอกสาร
ใบสำคัญจ่ายแยก. เพิ่ม JournalEntryId ใน PayrollRunResponse + MapToPayrollRunResponse
→ payroll.html run detail ปุ่ม "🧾 ดูรายการบัญชี (JE)" deep-link
journals.html?entryId={id} (เปิด JE detail ตรง). สลิป = หลักฐานพนักงาน (HR), JE =
บันทึกบัญชีการจ่าย — แยกหน้าที่กัน._

_รอบ 30 (แก้ยอดรายคนก่อนจ่าย): เดิมไม่มีทางแก้ยอดรายคน (Calculate ทำเฉพาะ Draft +
ลบ detail คำนวณใหม่; import เป็น Calculated). เพิ่ม UpdatePayrollDetailAsync +
PUT /payroll/runs/{id}/employees/{empId}/detail (UpdatePayrollDetailRequest, field
nullable แก้เฉพาะที่ส่ง) — อนุญาตเฉพาะ Calculated/Approved, รวม Gross/หัก/สุทธิ +
run totals ใหม่, กันสุทธิติดลบ, ปัดค่าติดลบเป็น 0. PayrollRunLineDto ขยายเป็น raw
fields ครบ (commission/otherIncome/PVD/loan/SSO นายจ้าง) เพื่อ pre-fill ตัวแก้.
payroll.html run detail: ปุ่ม "✏️ แก้ยอด" ราย row → โมดัลแก้ทีละช่อง + รวมสุทธิ live._

_รอบ 31 (กดจ่ายแล้ว "เชื่อมต่อเซิร์ฟเวอร์ไม่ได้"): ProcessPaymentAsync commit JE แล้ว
แต่ยัง await งานหนักใน request — IssueMonthlyPnd1Certs + AutoGenerateFilings (สร้าง
PDF ภงด.1/สปส. + สลิป QuestPDF ทุกคน + upload) + email enqueue → ใช้เวลานาน proxy
reset connection (client เห็น "Failed to fetch" ทั้งที่จ่ายสำเร็จแล้ว). แก้: ย้ายงาน
สร้างเอกสารไป background DI scope ใหม่ (IServiceScopeFactory) ผ่าน
DispatchPostPaymentArtifactsAsync → GeneratePostPaymentArtifactsAsync (IPayrollService);
response กลับทันทีหลัง commit + notifications. ไม่มี scopeFactory (test) → inline เดิม.
เอกสาร best-effort + สร้าง on-demand ได้._

_รอบ 32 (กดจ่ายแล้ว error จริง — nested transaction): log ชี้ "The connection is
already in a transaction and cannot participate in another transaction". ต้นเหตุ:
ProcessPaymentAsync/SettleSocialSecurityAsync เปิด tx เอง แล้วเรียก
AccountingService.CreateJournalEntryAsync ที่ก็เปิด tx ใหม่แบบ unconditional →
Npgsql ห้าม nested tx. แก้: CreateJournalEntryAsync ใช้ ambient-tx pattern เดียวกับ
ReverseJournalEntryAsync (เช็ค _db.Database.CurrentTransaction — เปิด/commit เฉพาะ
ตอนไม่มี ambient tx) → JE creation เข้าร่วม tx ของ caller. PayrollController.Pay
ครอบ try/catch คืน 400 + ข้อความจริง (เดิม propagate ดิบ). แก้ทั้ง payroll pay +
settle SSO + ทุก caller ที่ครอบ JE ด้วย tx._

_รอบ 33 (post JE ซ้ำ — "post ได้เฉพาะ Draft"): หลังแก้ nested-tx (รอบ 32) โผล่บั๊ก
ถัดมา — CreateJournalEntryAsync สร้าง JE เป็น Posted ตั้งแต่แรก (ไม่มีขั้น Draft) แต่
ผู้เรียก 4 ที่ (payroll pay / settle SSO / severance / RemitAsync นำส่งภาษี) เรียก
PostJournalEntryAsync ตามหลัง Create → post ใบที่ Posted แล้ว → throw. แก้:
PostJournalEntryAsync เป็น idempotent — entry Posted อยู่แล้ว → no-op สำเร็จ (คืน
response เดิม); Draft → post ปกติ; สถานะอื่น (Voided/Reversed) → ยัง throw._

_รอบ 34 (แหล่งจ่ายโมดัลนำส่ง สปส. ไม่ครบ): settleSsoBank โหลดแค่ getBankAccounts —
เพิ่ม getPaymentChannels (เงินสด/เงินทดรองกรรมการ 1133/ช่องจ่าย 2123) แบบ optgroup
(value bank:<id> / account:<id>). SettleSsoRequest + SettleSocialSecurityAsync เพิ่ม
BankGlAccountId (validate ผังบริษัท+active+level≥4 ใช้เป็น Cr ตรง ๆ). pattern เดียวกับ
รอบ 24 (หน้านำส่งภาษี) + payment-source รายคน._

_รอบ 35 (reclassify ผังบัญชี "กดแล้วไม่เปลี่ยน"): ReclassifyLineAccountAsync ทำงาน
ถูกต้อง (update line.AccountId + post JE คู่ Dr ใหม่/Cr เก่า ผ่าน JournalEntryBuilder
status=Posted, ไม่มี nested-tx). บั๊กอยู่ที่ frontend: submitReclassifyLine สำเร็จแล้ว
เรียก this.openDetail?.() ที่ "ไม่มี method นี้จริง" (ชื่อจริง detail()) → optional-chaining
no-op เงียบ → detail ไม่ refresh → ดูเหมือนข้อมูลไม่เปลี่ยน. แก้: เรียก
await this.detail(ctx.docId). (retry ไม่สร้าง JE ซ้ำ — backend guard line.AccountId==new → no-op)._

_รอบ 36 (PDF footer "การบันทึกบัญชี" สะท้อน reclassify): เดิม LoadGlPostingAsync หยิบ
JE ต้นทางใบเดียว (FirstOrDefault) → footer ยังโชว์ผังเดิม (516) แม้ reclassify แล้ว.
แก้: รวม JE forward ทั้งหมดของเอกสาร (SourceDocumentId เดียวกัน + OriginalEntryId==null
+ Posted + ReversedByEntryId==null = JE ต้นทาง + คู่แก้ไข reclassify) → NetGlLinesByAccount
net Dr−Cr ต่อผัง (ผังที่ reclassify หักล้างเป็น 0 หายไป เหลือผังใหม่) → footer แสดงยอด
สุทธิ Dr ผังใหม่ / Cr เงินสด ตรงกับที่แก้. label เพิ่ม "(สุทธิรวมแก้ไข N)" เมื่อมี >1 JE.
footer นี้ opt-in ผ่าน CompanySettings.ShowGlEntryOnDocument (default ปิด); เอกสารปกติ
ไม่แสดงผังบัญชีบนหน้า (ไม่ใช่ field §86/4)._

_รอบ 37 (แก้ "แหล่งเงิน" ไม่ได้ — แต่แก้ผังบัญชีได้): backend ReclassifyPaymentSourceAsync
ทำงานถูก (JournalEntryBuilder Dr เก่า/Cr ใหม่, guards ผ่าน). บั๊กที่ frontend:
openReclassifyPaymentSource หา doc จาก this.docs (list projection ที่อาจไม่มี doc
นี้/ไม่มี bankAccountId·paidAmount) แล้ว hard-return "ไม่พบเอกสาร" — ต่างจาก
openReclassifyLine ที่รับ args inline จึงไม่กระทบ. แก้: ใช้ this._currentDoc (เอกสารที่
detail() เพิ่ง fetch มี field ครบ) ก่อน fallback this.docs._

_รอบ 38 (ตั้งค่าต่อลูกค้า "ออกใบกำกับภาษีเสมอ"): Contact เพิ่ม
DefaultIssueTaxInvoice (bool, migration) + Create/Update/ContactResponse DTO +
MapContactToResponse. contacts.html เพิ่ม checkbox ในส่วนตั้งค่าบันทึกบัญชี (save/load/
reset). documents.html onContactChange → maybePreselectTaxInvoice: ลูกค้าที่ flag=true
+ ชนิดปัจจุบัน Invoice → เปลี่ยนเป็น TaxInvoice อัตโนมัติ + เตือน §86/4 (TaxId/สาขา/
ที่อยู่) ไม่ครบ. ไม่บังคับ — ยังเลือกชนิดเองได้/convert ได้เหมือนเดิม._

_รอบ 39 (หมายเหตุขึ้น PDF + รายละเอียดหลายบรรทัด): (a) doc.Notes (หมายเหตุที่กรอกตอน
สร้าง) เดิมไม่ถูก render บน PDF (โชว์แต่ CustomFooterNotes) — เพิ่ม render ทั้ง 2 path:
RenderDocumentPdfNative (QuestPDF Text รองรับ \n) + BuildDocumentHtml (white-space:
pre-line). (b) รายละเอียดรายการรองรับหลายบรรทัด: line desc input เปลี่ยนจาก <input>
เป็น <textarea rows=1 auto-grow> (Enter=เว้นบรรทัด; ProductLookup ยัง select ด้วย Enter
เมื่อ arrow-highlight เท่านั้น idx≥0 จึงไม่ชน); PDF cell + on-screen td ใช้ pre-line/Td
.Text() render \n ครบ._

_รอบ 40 (ชุด invoice/tax-invoice ครบวงจร): (a) เครดิตเทอมต่อลูกค้า —
Contact.PaymentDueDays/PaymentTerms → เติมวันครบกำหนดอัตโนมัติตอนสร้างเอกสารขาย.
(b) §86/4 บังคับตอนอนุมัติ — enforce864 default true; TaxInvoice บังคับ field ผู้ซื้อ
(เลขภาษี13/ที่อยู่) → ใบไม่ครบ block. **ยกเว้น**: (i) `BuyerDeclinedTaxInvoice`/walk-in
→ ข้าม gate ทั้งชุด; (ii) branch code (สาขา5) บังคับเฉพาะผู้ซื้อนิติบุคคล
(ประกาศ 199) บุคคลธรรมดาไม่บังคับ (ดู §2.3 ผู้ซื้อไม่ประสงค์รับใบกำกับ). (c) ป้าย "ต้นฉบับ" บน PDF
— ใบกำกับ/ใบเสร็จภาษี/CN/DN เติม "(ต้นฉบับ)" (สำเนา=WatermarkOverride) ทั้ง
QuestPDF+HTML. (d) หัว PDF ต่อชนิด GetDocumentTitle ถูกต้องอยู่แล้ว. (e) auto-receipt:
ชำระครบบน Invoice/TaxInvoice → prompt "ออกใบเสร็จรับเงิน" → convertDocument→Receipt
(VAT รับรู้ที่ใบเดิม ไม่คิดซ้ำ). (f) e-Tax email 1-คลิก: ปุ่ม sendEtaxEmailOneClick =
/etax/generate → sendEtaxByEmail (PDF/A-3+XML+CC สรรพากร)._

_รอบ 41 (ดาวน์โหลดสำเนา): pdfModal เพิ่ม dropdown "ต้นฉบับ/สำเนา" (pdfCopyMode) →
_refreshPdfPreview re-render + printPdf/downloadServerPdf/generate-html ส่ง
watermarkOverride="สำเนา (COPY)". server พิมพ์ลายน้ำ "สำเนา" (HTML div.watermark +
QuestPDF background) + isCopyPrint ตัดป้าย "(ต้นฉบับ)" ออก. ต้นฉบับ=ให้ลูกค้า,
สำเนา=ผู้ขายเก็บ (retention 5 ปี §87/3). จำเป็นเฉพาะเอกสารภาษี (ใบกำกับ/ใบเสร็จ
VAT/CN/DN); เอกสารทั่วไป (ใบแจ้งหนี้/เสนอราคา/ส่งของ) ไม่บังคับ._

_รอบ 42 (audit เชิงลึก convert/void/CN — verify แล้วแก้ 5 จุด): (a) ConvertCoreAsync
คงสกุลเงิน+เรตต้นทาง (เดิม default THB). (b) ValidateConversionAsync กันแปลงซ้ำเป็น
Invoice/TaxInvoice (1 ต้นทาง=1 ใบรับรู้รายได้ กัน double VAT/ภพ.30). (c) VoidDocumentAsync
block เมื่อมีเอกสารลูก active อ้างอยู่ (กัน orphan + ครอบเคสลูกมี e-Tax ยื่น RD).
(d) Void เพิ่ม FOR UPDATE lock + re-read สถานะ (กัน double-void race → reverse JE ซ้ำ).
(e) §86/10 CN cumulative cap: SUM(CN)≤source.TotalAmount (โหมดคืนเงินสดเดิมไม่ cap).
หมายเหตุ: ภพ.30 สร้างแบบ on-demand จาก documents (อ่านสด ตาม TaxPointDate) —
สะท้อน CN/DN/void ถูกต้องอยู่แล้ว ไม่ต้องมี TaxReportLine incremental._

_รอบ 43 (supersede + หัวเอกสารรวม): (a) แปลง Invoice→TaxInvoice: เมื่ออนุมัติ
TaxInvoice ที่แปลงจากใบแจ้งหนี้ (approved/ยังไม่ชำระ/ไม่มีลูกอื่น) →
SupersedeSourceInvoiceAsync ล้างใบแจ้งหนี้เดิม (reverse JE + stock -1 + project -1
+ Voided) กัน GL/รายได้/สต๊อกซ้ำ (ภพ.30 นับ TaxInvoice ใบเดียวอยู่แล้ว). (b) หัว
PDF ต่อชนิด GetDocumentTitle ถูกต้อง (Invoice→ใบแจ้งหนี้, TaxInvoice→ใบกำกับภาษี+
ต้นฉบับ, Receipt+VAT→ใบกำกับภาษี/ใบเสร็จรับเงิน) — แต่ไม่มี "ใบแจ้งหนี้/ใบกำกับภาษี"
รวม. เปิดช่อง CustomTitle/CustomTitleEn ในหน้า document-templates (เดิมมี field
แต่ UI ไม่โชว์) → ตั้งหัวเอกสารเองต่อเทมเพลตได้ (เช่น "ใบแจ้งหนี้/ใบกำกับภาษี").
เมื่อตั้ง CustomTitle → "(ต้นฉบับ)" auto ไม่ต่อท้าย (ใส่เองในหัวได้)._

_รอบ 44 (ใบแจ้งหนี้/ใบกำกับภาษี ใบเดียว — checkbox บนฟอร์ม): เพิ่ม flag ระดับ
เอกสาร `Document.CombinedInvoiceTaxInvoice` (bool, migration ALTER ADD COLUMN
IF NOT EXISTS). หน้า create-doc (documents.html) มี checkbox `fCombinedTaxInvoice`
โผล่เฉพาะฝั่งขาย Invoice/TaxInvoice (คุมโดย onDocTypeChange). ติ๊กแล้ว save →
frontend บังคับ `documentType='TaxInvoice'` + `combinedInvoiceTaxInvoice=true`
(ผ่าน `_effectiveDocType`). เอกสารทำงานเป็นใบกำกับภาษีเต็มรูป (post VAT 21911→
ภพ.30, บังคับ §86/4 ตอน approve, ออก e-Tax T03/T01 ได้ตามปกติ) แต่หัวกระดาษ PDF
พิมพ์ "ใบแจ้งหนี้/ใบกำกับภาษี" (Invoice / Tax Invoice) แทน "ใบกำกับภาษี" — override
ทั้ง QuestPDF (PdfGenerationService.DocumentRenderer.cs) + HTML path
(PdfGenerationService.cs) เมื่อ `type==TaxInvoice && CombinedInvoiceTaxInvoice
&& CustomTitle==null`. ยังคงต่อท้าย "(ต้นฉบับ)"/สำเนา ตามเดิม. เครดิตเทอมดึงจาก
contact.paymentDueDays → fDueDate + fPaymentTerms อัตโนมัติ (maybePreselectTaxInvoice
เดิม). Service กันเฉพาะ type=TaxInvoice จริงเท่านั้นถึงรับ flag (กันหัวเพี้ยน).
DTO: CreateDocumentRequest + DocumentResponse echo flag; hydrate checkbox ตอน edit._

_รอบ 45 (ส่งสลิปเงินเดือนทาง LINE): พนักงานผูก LINE เองผ่าน LINE OA บริษัท —
HR สร้างรหัส 6 หลัก (`EmployeeLineBindCode`, หมดอายุ 24 ชม.), พนักงานเพิ่มเพื่อน
OA แล้วส่ง "สลิป {รหัส}" → LineBotService.TryBindFromLineAsync เขียน Employee.LineId
(= push userId เดียวกับ NotificationEngine). ส่งสลิป: PayslipLineDeliveryService
สร้าง `PayslipShareToken` (สุ่ม 32 bytes base64url, หมดอายุ 7 วัน, เพิกถอน token
เก่าของงวด+คนเดียวกัน) แล้ว push flex card **ซ่อนยอดเงิน** (โชว์แค่ชื่อ/งวด + ปุ่ม)
ผ่าน ILineNotifyService.PushFlexToUserAsync (channel ต่อบริษัท). ปุ่มลิงก์ไป
`GET /api/public/payslip/{token}` ([AllowAnonymous]) → validate token → reuse
GeneratePayslipAsync → stream PDF inline + log **PdpaPiiAccessLog** (ม.37(4),
Operation=Read, SubjectType=Employee) + increment AccessCount. UI payroll.html:
ปุ่ม "📤 LINE" รายคน + "ส่งสลิปทั้งงวดทาง LINE" + modal รหัสผูก (NotBound →
เสนอสร้างรหัส). Settings: LINE OA Basic ID (`CompanySettings.LineOaBasicId`)
ทำลิงก์เพิ่มเพื่อน. Endpoints (HR-authed): POST runs/{r}/employees/{e}/payslip/
send-line · POST runs/{r}/payslip/send-line-all · POST employees/{e}/line-bind-code
· GET employees/{e}/line-status._

_รอบ 46 (กันส่งอีเมลเอกสาร Draft): เดิมกด "ส่งอีเมลหลังบันทึก" ตอนสร้าง →
ส่ง PDF เลข DRAFT-xxx ให้ลูกค้าทันทีโดยไม่อนุมัติ (ผิด §86/4 — เลขจริงออกตอน
Approve). แก้ 2 ชั้น: (a) backend DocumentEmailService.SendDocumentEmailAsync
บล็อกเอกสาร Draft/WaitingApproval/Rejected/Voided (throw) — กันทุกทาง
(create-flow, ปุ่มส่งซ้ำ, integration). e-Tax path บล็อก Draft อยู่แล้ว
(EtaxInvoiceService). (b) frontend create-flow: ติ๊กส่งอีเมล → อนุมัติให้ก่อน
(ออกเลขจริง) แล้วค่อยส่ง (1-click); อนุมัติไม่ผ่าน (§86/4 ไม่ครบ) → ไม่ส่ง +
แจ้งเหตุ. แชร์ savedStatus กับ paid-on-issue chain กัน approve ซ้ำ (ApproveDocument
throw ถ้าไม่ใช่ Draft/WaitingApproval). เพิ่มปุ่ม "📧 ส่งอีเมล" (PDF ปกติ) บน
เอกสารฝั่งขายที่อนุมัติแล้ว นอกเหนือจาก "ส่ง e-Tax อีเมล" (CC สรรพากร+XML) เดิม.
e-Tax by Email checkbox แสดงกับ TaxInvoice (รวม combined) อยู่แล้ว.

_รอบ 47 (รายงานภาษี — สะท้อน GL + PDF + ภ.พ.30): (a) รายงานภาษีซื้อไม่ดึง
เอกสารที่ไม่ได้เคลม VAT — เดิม §82/5/ไม่เคลม (IsVatClaimable=false, VAT กลบ
ค่าใช้จ่ายไม่ลง 11610) ถูกใส่เป็น audit line IsExcluded → เลิก emit; รายงานมี
เฉพาะภาษีซื้อที่เคลมจริง (claimableVat>0) สะท้อน GL. ต้นเหตุ: OCR ตั้ง
HasTaxInvoiceReference=true อัตโนมัติเมื่อมี VAT+เลขใบ แต่ผังบัญชีบังคับ
ไม่เคลมตอน approve. (b) วันที่ export วว/ดด/ปปปป (พ.ศ.) ตรงหัวคอลัมน์ (เดิม
สลับ ปปปป-ดด-วว). (c) เพิ่ม PDF: GET tax/{id}/export-pdf?kind=purchase|sales|
pp30 → PdfGenerationService.GenerateVatReportPdfAsync (QuestPDF) — รายงาน
ภาษีซื้อ/ขาย ตาราง §87 ประกาศ 104 + แบบสรุป ภ.พ.30 (ช่อง 1-9); ปุ่มในหน้า tax._

(c) UX: maker ที่ไม่มีสิทธิ์อนุมัติ (เช็คจาก my-permissions allowedMenuIds:
perm:Document.Approve / .Revenue.Approve / .Purchase.Approve) → กล่องส่งอีเมล
ขึ้นหมายเหตุล่วงหน้าว่าเอกสารจะเป็นร่างรออนุมัติ + ตอนบันทึกไม่ยิง approve
(กัน 403) แจ้งแบบเป็นมิตร. Owner/Admin หรือ role ที่มี perm → ส่งได้ปกติ._

_รอบ 97: แก้ภาษีซื้อที่ "ถึงกำหนดทีหลัง" (§83/6 ภ.พ.36 รับรู้ / §86/4 เติมใบกำกับ)
_หายจากรายงาน — GenerateVatReport เดิมโหลดเอกสารด้วย (TaxPointDate ?? DocumentDate)
_ในงวดเท่านั้น → ใบเดือน พ.ค. ที่ BecameClaimableAt=ก.ค. ไม่ถูกโหลดในรายงาน ก.ค.
_= "กดเคลมแล้วหาไม่เจอ". แก้ docs query: OR (InputVatBecameClaimableAt ในงวด) +
_ในลูป input เพิ่ม guard "เคลมเฉพาะเดือนที่ถึงกำหนด" (skip ถ้า BecameClaimableAt
_นอกงวด) → นับเฉพาะงวดรับรู้ กันเคลมผิดเดือน/เบิ้ล 2 งวด. + recognize ภ.พ.36 ให้
_ผู้ใช้กรอก "วันที่ใบเสร็จ RD" (เดิม hardcode วันนี้) → JE + BecameClaimableAt +
_เดือนที่เข้า ภ.พ.30 = วันที่นั้น (§77/2)._
_รอบ 96: แก้ §83/6 โชว์ผิด flow "รอใบกำกับ §86/4" — เอกสารบริการต่างประเทศ post
_11640 (InputVatPostedAsUndue) เหมือนกัน แต่เคลมผ่าน ภ.พ.36 (นำส่ง+รับรู้) ไม่ใช่
_§86/4 completeness. ผู้ขาย ตปท. ไม่มีเลขภาษีไทย → CompleteSupplierTaxInvoice
_(completeness) fail เงียบ ๆ แต่ toast บอก "สำเร็จ ย้ายเข้า ภ.พ.30" ทั้งที่ไม่ย้าย.
_แก้: (1) ReclassifyUndueInputVatAsync guard IsForeignService → return false (ชัด).
_(2) banner detail + badge list แยก §83/6 → โชว์ "🌐 ภ.พ.36 รอรับรู้" + ลิงก์หน้า
_นำส่งภาษี (ไม่โชว์ฟอร์ม §86/4). (3) toast completeTaxInvoice ซื่อสัตย์ — เช็ค
_inputVatBecameClaimableAt จริง: ย้ายแล้ว/ยังเคลมไม่ได้ (ชี้ ภ.พ.36 ถ้า ตปท.)._
_รอบ 95: หน้ารายงานภาษี (tax.html) — (1) ติ๊ก "ใช้" ไม่มี onchange → ยอดไม่ recalc
_+ ต้องกดปุ่มบันทึกเอง. เพิ่ม onVatLineToggle: recalc footer ตารางนั้นทันที
_(client) + auto-save debounce 700ms (indicator ● กำลังบันทึก → ✓ บันทึกแล้ว) +
_อัปเดต KPI จาก server response — ไม่ต้องกดปุ่มบันทึกอีก. (2) §82/3 carry-forward:
_GenerateVatReport ดึงบรรทัด INPUT ที่ IsExcluded ในรายงานเดือนก่อน (ภายใน 6 เดือน,
_ยังไม่ถูกใช้/ยังไม่ undue/ไม่ voided) มาเป็นบรรทัด "ยกมา §82/3" (default ติ๊กออก) →
_ผู้ใช้ติ๊กใช้เดือนไหนก็เคลมเดือนนั้น (RecalcVatTotals นับตอนบันทึก). กันเครดิต
_ภาษีซื้อที่เลื่อนไว้หายถาวร._
_รอบ 96 (เอกสารมาช้าหลังปิดงวด): carry-forward รอบ 95 ครอบเฉพาะใบที่ "เคยมี
_บรรทัดแล้วถูกติ๊กออก" — ใบกำกับ มิ.ย. ที่เพิ่งบันทึกตอน ก.ค. (งวด มิ.ย. Filed
_แล้ว regenerate ไม่ได้) ไม่เคยอยู่ในรายงานไหนเลย → tax point = มิ.ย. หลุดทั้ง
_query งวด ก.ค. และ carry-forward = ภาษีซื้อหายเงียบ. GenerateVatReport เพิ่ม
_late-arrival sweep: ใบ tax point ในงวดก่อน (≤6 เดือน) ที่ไม่มีบรรทัดในรายงาน
_VAT ใดเลย + (งวดนั้น Filed **หรือ** CreatedAt หลังเดือน tax point จบ) →
_ฝั่งซื้อใส่บรรทัด opt-in "[ใบกำกับซื้อมาช้า]" (IsExcluded, ยอด = เฉพาะส่วน
_เคลมได้หลังหัก §82/5) / ฝั่งขายใส่บรรทัดเตือน "ต้องยื่น ภ.พ.30 เพิ่มเติมงวดนั้น"
_(ภาษีขายเลื่อนงวดไม่ได้). + PullableVatTypes เดิมขาด **PaymentVoucher** (loop
_หลักนับเป็นภาษีซื้อเมื่อ HasTaxInvoiceReference) + ใบขายที่ไม่ใช่ TaxInvoice →
_ปุ่ม "ดึงเอกสาร" ดึง PV ไม่ได้เลย; เพิ่มแล้ว + guard PV ที่ไม่ติ๊กใช้ใบกำกับ +
_hard block §82/3 เกิน 6 เดือนตามงวดปลายทาง (เดิมเช็คจากวันนี้ = เตือนอย่างเดียว)._
_รอบ 94 (ภ.พ.36 ครบวงจร §83/6): (A) AutoPost แก้ JE บริการต่างประเทศ — เดิม Cr
_เจ้าหนี้/เงินสด "รวม VAT" (จ่ายผู้ขาย ตปท. เกิน 7% + งบไม่มีหนี้ ภ.พ.36). ใหม่:
_Cr ผู้ขาย/เงินสด = ฐาน + Cr 21912 เจ้าหนี้ ภ.พ.36 = VAT ประเมินเอง + Dr 11640
_บังคับเสมอ (§77/2 เคลมได้หลังนำส่ง) — ทั้ง branch Expense/PI accrual และ PV
_standalone (cash + legacy credit). (B) StatutoryRemittance เพิ่ม VatPp36:
_dashboard pending จากเอกสาร IsForeignService, RemitAsync generic → Dr 21912/
_Cr ธนาคาร, due 7/15. (C) RecognizePp36InputVatAsync — หลังได้ใบเสร็จ RD:
_Dr 11610/Cr 11640 + stamp InputVatBecameClaimableAt → เข้า ภ.พ.30 เดือนรับรู้;
_idempotent (JE Ref ภ.พ.36R-YYYYMM) + ต้องนำส่งก่อน. UI: หน้า tax-remittance
_แถว ภ.พ.36 ขึ้น pending อัตโนมัติ + ปุ่ม "รับรู้ภาษีซื้อ" บนประวัติ._
_รอบ 93: เปิดฟอร์มออก "ใบกำกับภาษี/ใบเสร็จรับเงิน ใบเดียว" (ขายเงินสด) — เดิม
_IssuedAsCashReceipt set ได้เฉพาะ integration (TakeTime IsCashSale); ฟอร์มสร้างเอง
_มีแค่ "จ่ายแล้ว (2 ใบ + REC แยก)". เพิ่ม CreateDocumentRequest.IssuedAsCashReceipt
_(guard TaxInvoice), approve ปิด Paid, checkbox ในฟอร์ม (mutually exclusive กับ
_paidOnIssue). มัดจำที่เลือก → ส่ง drives (DepositAppliedDrivesJournal) reuse เส้น
_AutoPost cash-sale ที่ verified (JE เดียว Dr เงินสดสุทธิ+กลับมัดจำ/Cr รายได้+VAT,
_ไม่ตั้งลูกหนี้, ไม่ออก REC แยก, e-Tax T03).
_รอบ 92: หักมัดจำหลายใบโชว์ครบบน PDF — apply สะสมทุกเลขใน DepositAppliedRef
_(MergeDepositRef comma-sep+dedup, เดิมเก็บใบแรก → label โชว์เลขเดียว) + PDF
_แตกบรรทัดต่อใบ (LoadDepositApplyBreakdownAsync อ่าน gross ต่อใบจาก apply JE:
_Cr 113; เลข = Document มัดจำ/JV จาก description) ผ่าน param depositApplies._
_รอบ 91: void/purge คืน "JV มัดจำ raw (non-drives)" — เดิม void 2b/purge 0c วน_
_เฉพาะ Document deposits (d.IsDeposit) → JV apply (raw JE, SourceDocumentId=null,_
_Reference=docNo) ไม่ถูก reverse/delete + JV mark ไม่ถูกล้าง → ลบใบแล้ว 3 JV apply_
_(Dr 21510/Cr 113) ค้าง orphan + JV นำไป apply ใบใหม่ไม่ได้. เพิ่ม helper_
_ReverseOrphanJvDepositAppliesAsync (void→reverse / purge→delete ทุก apply JE +_
_un-mark JV ต้นทางจาก description "JV XXX"). ปรับ PaidAmount ตาม gross ที่คืน._
_รอบ 90: JV apply strictly one-shot — เดิม guard บล็อกเฉพาะ apply ไปใบอื่น_
_(DepositAppliedToDocumentId != invoiceId) แต่ "ยอมหักซ้ำใบเดิม" (== invoiceId)._
_JV เป็น one-shot เต็มจำนวน ไม่มี remaining tracking แบบมัดจำเอกสาร → หักซ้ำ =_
_โพสต์ Dr 21510/Cr ลูกหนี้ ซ้ำเต็มจำนวน. เคสจริง JV 3,000 ถูกหัก 3 ครั้ง →_
_Dr 21510 9,467.29 (467.29+3×3,000) + Cr ลูกหนี้ค้าง 6,000. แก้: == invoiceId_
_→ throw idempotent (แก้ยอด = void/ลบใบแล้วสร้างใหม่). มัดจำเอกสารปลอดภัยอยู่_
_แล้ว (availableBase guard). ใบที่พังไปแล้วต้อง void+recreate บน build ใหม่._
_รอบ 89: ปิด field-driven 21712 ที่เหลือ — Realize + Refund มัดจำ. เพิ่ม helper_
_ResolveDepositBaseAccountAsync (หาผัง 215/217 ยอด Cr สูงสุดจาก JE จริงของใบมัดจำ)_
_→ Dr ผังจริง (เช่น 21510) แทนเดา 21712 (ไม่งั้น 21510 ค้าง Cr + 21712 ติดลบ)._
_ครบทุกเส้นแล้ว: apply(doc/JV), realize, refund, drives(cash-sale), void/purge_
_= GL-driven อ่านขาจริงหมด. 21510 = ผังมัดจำจริงของ tenant (ที่ผู้ใช้ส่งมา)._
_รอบ 88: ApplyDepositToInvoiceAsync เปลี่ยนเป็น GL-driven (แบบเดียวกับ JV apply)_
_— เดิม field-driven (DepositDeferredAccountCode ?? 21712 + flag เดา VAT) → ใบ_
_มัดจำที่ JE จริงลง Cr ผังอื่น (integration ลง 21510/21610) ถูก Dr 21712 ผิดผัง:_
_ผังเดิมค้าง Cr ถาวร + 21712 ติดลบ. ใหม่: family-net (Cr−Dr ต่อผัง) จากทุก JE_
_forward ของใบมัดจำ (ต้นทาง+apply ก่อนหน้า, ตัดผัง 1xxxx) → Dr ตามขาจริงตาม_
_สัดส่วน, ฐาน/VAT แยกตามผังจริง (21913/21911), guard เกิน grossRemaining._
_field-driven เหลือเป็น fallback เมื่อไม่มี JE forward เท่านั้น._
_รอบ 87: footer "การบันทึกบัญชี" รวม JE ตัดมัดจำ — เดิมดึงเฉพาะ SourceDocumentId_
_== ใบนี้ แต่ JE ตัดมัดจำผูกกับ "ใบมัดจำ" (doc apply) / null (JV apply) → net view_
_โชว์ Dr ลูกหนี้ "ค้าง" เท่ายอดมัดจำทั้งที่ GL จริงล้างครบ (ผู้ใช้เข้าใจผิดว่าลงผิด)._
_เพิ่มเงื่อนไข OR (IsAutoGenerated && Reference == เลขใบ) — apply ทั้ง 2 path_
_stamp Reference = เลขใบปลายทาง; JE ของใบอื่น Reference = เลขตัวเอง ไม่ปน._
_รอบ 86 (audit ซ้ำ): (1) chain "จ่ายแล้ว" รันเฉพาะปุ่ม "บันทึกและอนุมัติ" — เดิม_
_กด "บันทึกร่าง" ก็ approve+บันทึกชำระเลย (ร่างขยับเงินจริง) (2) วันที่ JE ตัด_
_ชำระมัดจำ = วันที่เอกสาร (เดิม UtcNow → backdate ข้ามเดือน VAT recognition_
_หลุดเดือน ภ.พ.30): ApplyJournalDepositRequest + ApplyJournalDepositToInvoiceAsync_
_รับ ApplyDate, frontend ส่ง fDate ทุกจุด (pending applies + pickJvDeposit)._
_ตรวจแล้วถูกอยู่: PDF โชว์ "หักเงินมัดจำ→ยอดชำระสุทธิ" (ทั้ง 2 apply path stamp_
_DepositAppliedAmount/Ref), JV apply แบบ GL-driven รองรับทั้ง JV มี/ไม่มี VAT leg._
_รอบ 85: แก้ "จ่ายเงินแล้ว (cash sale) + หักมัดจำ" ชนกัน — เดิม paidNow branch_
_approve แล้วจ่าย "เต็ม balanceDue" ทันที แล้ว skip บล็อกหักมัดจำ (อยู่ใน_
_approveAfter ที่ข้ามเพราะ Approved แล้ว) → เงินเข้าธนาคารเต็มใบทั้งที่รับจริงแค่_
_ส่วนต่าง + มัดจำค้างไม่ถูกหักเงียบ ๆ. แก้: แยก _applyPendingDeposits (consume_
_list กันหักซ้ำ) เรียกทั้ง 2 branch — ลำดับใหม่: approve → หักมัดจำ (ลด_
_BalanceDue) → จ่ายเฉพาะยอดคงเหลือจริง (re-fetch หลัง apply)._
_รอบ 84: หักมัดจำในฟอร์ม — (1) totals box เพิ่มแถว "หักมัดจำที่เลือก / คงเหลือรับ_
_ชำระ" (updateDepositSummary — ยอดสุทธิใบไม่เปลี่ยนตาม §86/4, มัดจำลดยอดค้างหลัง_
_อนุมัติ) (2) แก้หน่วยยอด: DepositSummary.outstandingAmount เป็น "ฐานไม่รวม VAT"_
_แต่ ApplyDeposit.amount เป็น gross → เดิม UI ส่งฐานเป็น gross = มัดจำมี VAT หัก_
_ขาด (เศษ VAT ค้าง AR). เพิ่ม _depGrossOut แปลงฐาน→gross ใช้ทุกจุด (banner/_
_checkbox rows/quick-apply/modal) + label "(รวม VAT)" (3) แก้ modal picker ใช้_
_d.id (เดิม d.depositDocumentId ที่ไม่มีจริง → option value undefined หักไม่ได้)._
_รอบ 83: purge ครอบผลข้างเคียงนอก GL ครบ (mirror void) — เดิม PurgeDocumentAsync_
_ลบ JE/Payment/WHT/e-Tax แต่ "ไม่กลับ" สต๊อก, ยอดใบต้นทางที่ถูกตัดชำระ, project_
_billed/cost, FixedAsset auto-register, bank match (MatchedPaymentId + สถานะ_
_Matched ค้าง), ReconciliationGroup, OcrScanResult.CreatedDocumentId → resync =_
_ตัดสต๊อกซ้ำ/ยอดเบิ้ล/กระทบยอดค้างผี. เพิ่ม step 0d (Revert+Billing−1+Stock−1+_
_PCE reverse — ข้าม Draft/Voided กันคืนเกิน), ลบแถว StockMovement ของใบ, 0e_
_asset cascade (NeedsReview+ไม่มี dep → ลบ; อื่น ๆ ตัด link), reset bank txn เป็น_
_Unmatched ทั้งขา JE และ Payment, 7b ตัด link OCR scan, unwind groups หลัง_
_commit. + JE คู่กลับรายการ: ลบ REV → คืนใบเดิม Posted; ลบใบเดิม → ลาก REV ตาม_
_(AccountingService). batch-delete แจ้งรายการที่ข้าม (ผูกเอกสาร) แทนเงียบ._
_รอบ 82: หักมัดจำ "หลายใบ" ในฟอร์มสร้างเอกสาร — เดิม dropdown เดียว = หักได้ใบเดียว/_
_บันทึก. เปลี่ยนเป็น checkbox rows (แต่ละใบมียอดของตัวเอง, booking-match pre-tick),_
_`_pendingDepositApply` → array `_pendingDepositApplies` (ปนมัดจำเอกสาร+JV ได้),_
_save() วน applyDeposit/applyJournalDeposit ทีละใบหลัง approve, fail-soft ต่อใบ +_
_สรุปผลรวม. ติ๊กใบมัดจำ → autofill เลขจอง (fBookingNumber) + อ้างอิง (fRef) จากใบ_
_มัดจำ (เฉพาะตอนช่องว่าง ไม่ทับที่ผู้ใช้พิมพ์; ไม่แตะบรรทัดสินค้า) ผูกใบเข้า booking_
_เดียวกัน. frontend เท่านั้น (backend applyDeposit เรียกซ้ำสะสมได้อยู่แล้ว)._
_รอบ 81: WHT cert ประเภทแบบ guard (ภ.ง.ด.3↔53 ตามผู้ถูกหัก) — ResolveWhtFormType_
_+ DetectJuristic บังคับทุก create path, override ค่าที่ integration ส่งผิด._
_รอบ 80: OCR review inline line editing (Description/Quantity/UnitPrice แก้ในตาราง_
_→ SetExtractedLineFieldsAsync recompute Amount + persist, คู่กับ qty-guard); ปิดลูป_
_project-match feedback (SetExtractedLineProject → RecordUserChoiceAsync)._
_รอบ 81 (2026-07-24): Invoice undue output VAT — ใบแจ้งหนี้บริการล้วน Cr 21913_
_(ไม่เข้า ภ.พ.30 จนรับเงิน §78/1) → reclass 21913→21911 + OutputVatDueAt เมื่อ_
_รับชำระ (Payment/ใบเสร็จ settlement); ใบมีสินค้า TrackStock = ส่งมอบ (§78) ลง_
_21911 ทันทีเหมือนเดิม; report ตัดสิน GL-driven (ใบเก่า net 21913=0 → พฤติกรรมเดิม)._
_รอบ 82 (2026-07-24): กันใบกำกับซ้อน/ยอดหาย (เคส INV 97,500 + TIV 75,000 ไม่ผูกกัน):_
_(1) Supersede guard — อนุมัติ TIV ที่แปลงจาก INV ยอดต้องเท่าใบต้นทาง (±0.01)_
_ไม่งั้น block (กัน partial/ราคาหลุด 0 ทำส่วนต่างหายจาก GL เงียบ);_
_(2) integration invoice.created: ถ้ามี INV active อ้างอิง (WO) เดียวกัน — ยอดตรง_
_→ ออกใบกำกับผ่าน ConvertDocumentAsync+Approve (copy บรรทัดจากใบจริง + supersede_
_อัตโนมัติ), ยอดไม่ตรง/ชำระแล้ว/มีมัดจำ → Failed ดัง ๆ ไม่ mint TIV แยกใบ;_
_(3) payment.received lookup ข้ามใบ Voided/Rejected + เลือก TIV ก่อน INV._
_รอบ 83 (2026-07-24): settlement receipt v2 — (A) ใบรวม (Combined) รับครบงวดเดียว_
_ผ่าน modal/paidOnIssue → ไม่ออกใบเสร็จแยก ตัวใบรวม ServedAsReceipt → หัว 3-in-1_
_"ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน" (ผ่อนหลายงวด = ใบเสร็จแยกต่องวด + หัวคง_
_2 หน้าที่เดิม); (B) ใบแจ้งหนี้ VAT รับครบงวดเดียว → settlement receipt ถือ VAT/_
_บรรทัดจากใบแจ้งหนี้ (carryVatFromSource) = ใบกำกับภาษี ณ วันรับเงิน §78/1 หัว_
_"ใบกำกับภาษี/ใบเสร็จรับเงิน" — ไม่ post JE/ไม่เข้า ภ.พ.30 ที่ใบนี้ (VAT รายงานที่_
_INV ผ่าน OutputVatDueAt, settlement ถูก exclude เดิม); งวดแรกบางส่วน/TIV source_
_= ใบเสร็จเปล่า VAT=0 เหมือนเดิม._
_รอบ 84 (2026-07-24): invariant "หัวมีคำใบกำกับภาษี ⇔ อยู่ใน ภ.พ.30" — (1) ใบเสร็จ_
_ถือ VAT ที่ settle ใบแจ้งหนี้ undue เป็น "เจ้าของแถว ภ.พ.30" แทน INV (เลข/วันที่ตรง_
_กระดาษใบกำกับจริง; INV skip กันซ้ำ; ไม่มีใบเสร็จถือ VAT → fallback INV ตาม_
_OutputVatDueAt เดิม — VAT ไม่หลุดรายงาน; legacy invoice (OutputVatDueAt null)_
_ใบเสร็จ convert ไม่ผ่านเงื่อนไข → ไม่ซ้ำ); (2) มัดจำ deferred ที่ recognize แล้ว_
_(เข้า ภ.พ.30) หัว upgrade เป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน (เงินมัดจำ)" — ยกเว้น_
_ถูก apply เข้าใบปลายทาง (ใบปลายทางคือใบกำกับ กันกระดาษซ้ำ). ข้อยกเว้น invariant_
_ที่ตั้งใจ: ใบแจ้งหนี้สินค้า/legacy + ผู้ซื้อปฏิเสธใบกำกับ (ขายปลีก) = อยู่ในรายงาน_
_โดยกระดาษไม่มีหัวใบกำกับ (นำส่งครบตามกฎหมาย มี approval warning ชี้ทางแล้ว)._
_รอบ 16 (multi-team audit 8 โดเมน — ดู DEVELOPMENT_PHASES.md): void ใบที่ชำระ_
_ด้วย multi-doc payment คืนยอดธนาคารครบ (ReverseMultiDocPaymentInternalAsync;_
_ยกเลิกใบเดียวในกลุ่ม → block ให้ยกเลิกใบชำระก่อน); RealizeDeposit หัก_
_DepositRefundedAmount; ApplyDepositToInvoice ห่อ transaction + FOR UPDATE;_
_UpdateDocument บล็อกการแก้ใบที่ restore แล้วถือเลขจริง (§86/4); approve_
_idempotency guard ไม่นับ reversal (void→restore→approve ต้อง post JE ใหม่);_
_FX reval เฉพาะ monetary types; ค่าเสื่อม/ตีราคาเช็คงวดปิด + FiscalPeriodId;_
_ภ.ง.ด.3/53 แยกผู้ถูกหักด้วย DetectJuristic + กรองเฉพาะเอกสารฝั่งซื้อ + ภ.ง.ด.1_
_ไม่ดึงจากเอกสาร; BuildPnd กรอง SUMMARY/IsExcluded; void ติ๊กบรรทัดรายงานทุกแบบ_
_(RecalcWhtTotals); 50 ทวิ ออกตามงวดจ่าย (SourcePaymentId + pro-rate);_
_claimedElsewhere ยกเว้นงวดเดียวกัน; Sale.csv กรอง IsExcluded + ปี พ.ศ. ต่อแถว;_
_InputVat ไม่รวมเครดิตยกมา; ฐานรายงาน = VatableBase หักบรรทัดยกเว้น; e-Tax_
_BasisAmount/PDF ไม่หักส่วนลดซ้ำ + TaxId ผู้ซื้อบังคับ 13 หลัก + ISO8601 +07:00 +_
_ชื่อเอกสาร PDF=XML; AI: fingerprint ตรงกันเมื่อ sanitize PII, ImportDataReview_
_มี local heuristics, DailyCallCap นับเฉพาะ provider call, OCR เก็บสาขา/ที่อยู่;_
_tenant: CMS cart scope, POS ProductId, payroll includeSalary; XSS 4 หน้า;_
_import: พ.ศ.→ค.ศ. ทุกจุด + JE/bank dedup. **ใหม่: ภาษาเอกสาร th/en**)_

_Last updated: 2026-08-04 — Chatbot Phase 1-5.1 (public FAQ + tenant assistant_
_+ admin console + คลังความรู้/metrics + PDPA purge + rate limit ข้าม instance_
_+ ถามผ่าน LINE — รายละเอียด/งานที่เหลืออยู่ CHATBOT_PLAN.md); ก่อนหน้า:_
_LINE bot รับรูปใบเสร็จ → OCR → เอกสารทันที (§2.2b:_
_รวม Flex ปุ่มอนุมัติในแชท + postback guard + แจ้งกลับผู้ส่งเมื่ออนุมัติ)_
_+ routing บิลไม่เป็นทางการ → ใบรับรองแทนใบเสร็จ (§2.2c); ก่อนหน้า: ปฏิทินนำส่ง_
_ภาษี/ประกันสังคมบน dashboard (§5.3b) + แนบสลิปนำส่ง สปส. เข้ารอบเงินเดือน_

_รอบ 107 — **แอดมิน: กรอง/เรียงผู้ใช้ · คัดลอก secret · SSO ตั้งจากหน้าเว็บ**:
(1) **จัดการผู้ใช้งาน** เดิมมีแค่ค้นหา+แบ่งหน้า เรียงตายตัวตามวันสมัคร ⇒ หา
"ใครยังไม่ยืนยันอีเมล" / "ใครไม่ได้เข้านานแล้ว" ไม่ได้ → เพิ่มตัวกรอง สถานะ/
แอดมิน/ยืนยันอีเมล/ถือ License + เรียง 7 แบบ + จำนวนต่อหน้า (20/50/100) +
ตัวนับผลรวม + ล้างตัวกรอง (`GET /api/admin/users` รับ status/isAdmin/
emailVerified/hasLicense/sort · NULL ของ "เข้าใช้ล่าสุด" ไปท้ายเสมอทั้ง
asc/desc — คนไม่เคยเข้าไม่ควรลอยขึ้นหัวตอนเรียง "ล่าสุด")
(2) **คัดลอก secret** — ช่องรหัส SMTP/MS secret เพิ่มปุ่ม 👁 แสดง + 📋 คัดลอก
**ของค่าที่พิมพ์อยู่** (ค่าที่บันทึกแล้วยังไม่ส่งกลับมาแสดงตามเดิม — API คืนแค่
`hasPassword:true`; เขียนกำกับบน UI ว่าคัดลอกได้เฉพาะค่าที่เพิ่งพิมพ์)
(3) **SSO/OAuth ตั้งจากหน้าแอดมินได้แล้ว** — เดิมมีแต่ `appsettings.json`
(แก้ทีต้อง deploy+restart และแอดมินมองไม่เห็นว่าตั้งไว้ไหม). ย้ายมาเก็บใน
`SiteSettings` (+migration 9 คอลัมน์, secret เข้ารหัสด้วย SecretProtector) +
หน้า `/admin/sso-config.html` มีขั้นตอนตั้งค่าทีละข้อ + Callback URL ที่ต้องใส่
ใน console ของแต่ละเจ้า (คัดลอกได้) · `GET/PUT /api/admin/sso-config` ·
**เปิดใช้ไม่ได้ถ้ายังไม่มี Client ID** (กันปุ่มหลอก) · appsettings ยังเป็น
fallback ให้ deployment เดิม
(4) **หน้า login ซ่อนปุ่มที่ยังใช้ไม่ได้** — `/api/auth/sso-config` คืนเฉพาะ
provider ที่ "เปิดสวิตช์ + มีคีย์" (ตัวตัดสินเดียวกับ `SsoLoginAsync` ที่ปฏิเสธ
provider ที่ปิดอยู่) ⇒ ไม่มีปุ่มที่กดแล้วเจอ "ยังไม่ได้ตั้งค่า OAuth" อีก ·
ไม่มี provider ไหนเปิดเลย = ซ่อนทั้งบล็อก "หรือเข้าสู่ระบบด้วย"
(5) **LINE Login** (ไทยใช้เยอะสุด) — web OAuth2 authorization-code:
หน้า login ส่งไป `access.line.me/oauth2/v2.1/authorize` (state กัน CSRF, ล้าง
query หลังกลับกันยิง code ซ้ำ) → `AuthService.ValidateLineTokenAsync` แลก code
เป็น id_token ด้วย channel secret (ไม่ออกจาก server) แล้ว verify ที่
`api.line.me/oauth2/v2.1/verify` + เช็ค audience · รองรับ id_token ตรงด้วย
(นับจุดใน JWT แยกสองกรณี) · ต้องขอ scope `openid email` — ไม่มีอีเมล = เข้าไม่ได้
เพราะระบบผูกบัญชีด้วยอีเมล (แจ้งไว้ในขั้นตอนบนหน้าแอดมิน);_

_รอบ 106 — **แอดมิน: การใช้งานรายบริษัทต่อเดือน (เอกสาร/OCR/AI/อีเมล/e-Tax)**:
เดิมมีแต่หน้า "รายงานการใช้งาน AI" (เจาะ AI อย่างเดียว) — ตอบไม่ได้ว่าบริษัท
ไหนออกเอกสารอะไรไปกี่ใบในเดือนนั้น. เพิ่ม `GET /api/admin/company-usage?year=&month=`
(`AdminCompanyUsageController`, `[Authorize(Roles="SystemAdmin")]`) รวม 5 แหล่ง
เป็นแถวต่อบริษัท: **เอกสาร** group ที่ DB ตาม (CompanyId, DocumentType) — นับ
ทั้งหมด/อนุมัติแล้ว/มูลค่า (ตัดร่าง+รอดำเนินการ+ปฏิเสธ ออกจากยอดเงิน และตัด
Voided ออกอีกชั้นเฉพาะยอดเงิน — ใบยกเลิกเคยออกเลขจริงจึงยังนับเป็น "อนุมัติแล้ว")
· **OCR** `OcrScanResults` (ทั้งหมด/สำเร็จ) · **AI** `AiUsageDailyTenants`
rollup ชุดเดียวกับหน้า AI (ตัด sandbox — ยอดต้องตรงกัน) แยก "เรียกทั้งหมด" กับ
"จ่ายจริง" ตามกฎเหล็ก #1 · **อีเมล** `EmailQueues` ที่ Status=Sent ·
**e-Tax** `EtaxInvoices`. นับตาม **CreatedAt** (มิเตอร์การใช้งาน) ไม่ใช่เดือน
ภาษีของเอกสาร — คนละมุมกับรายงานบัญชี ระบุไว้บนหน้าจอชัด. หน้า
`pages/admin-company-usage.html` (adminOnly): KPI 4 ตัว + ตารางเรียงตามจำนวน
เอกสาร + คลิกแถวกางชิปแยกชนิดเอกสาร + ค้นหา + CSV (BOM ให้ Excel ไทยอ่านออก);_

_รอบ 105 — **"กดอนุมัติแล้วเด้งถามเลิกเคลม?" — method ชื่อซ้ำทับกันเงียบ**:
เคสจริงจาก screenshot: ใบไทวัสดุเป็น**ใบกำกับเต็มรูป** (ผู้ขายยกเลิกใบอย่างย่อ
ออกใบเต็มรูปแทนเพื่อให้เคลมได้) ผู้ใช้ติ๊ก "มีใบกำกับภาษีซื้อ — ขอเครดิต
ภ.พ.30" ครบ แต่บรรทัดค้าง 🚫 จาก OCR แล้วกดสลับไม่ได้ — ต้นเหตุ:
`toggleVatClaim` มี **2 ตัวชื่อซ้ำใน `Page` เดียวกัน** (ตัวฟอร์มรับ `(el)` /
ตัวหน้า detail รับ `(id, claim, el)`) JS เอาตัวหลังทับตัวแรกเงียบ ๆ ⇒ กดไอคอน
บนฟอร์มไปเรียกตัวหลังด้วย claim=undefined → เด้ง confirm "เลิกเคลมภาษีซื้อ
ใบนี้?" กลางฟอร์ม + ยิง API ด้วย DOM element แทน id. แก้ 3 ชั้น:
(1) เปลี่ยนชื่อตัวฟอร์มเป็น `toggleLineVatClaim` — ไอคอน ✓/🚫 กลับมาทำงาน
(2) ติ๊ก "มีใบกำกับภาษีซื้อ" → `_reclaimLinesForFullTaxInvoice` ปลด 🚫 ของ
บรรทัดที่เหตุผล "ใบเต็มรูปรักษาได้" (§82/5(1) ใบไม่สมบูรณ์/อย่างย่อ/บิลเงินสด)
อัตโนมัติ + toast — เหตุถาวร (§82/5(3)(4)(6) ค่ารับรอง/น้ำมันรถนั่ง) ไม่แตะ ·
ผู้ใช้ override เองไม่แตะ (3) ด่านก่อน save: header บอกเคลมแต่ทุกบรรทัด 🚫 →
confirm ภาษาคนให้เลือก เคลม (เปิด ✓ ให้) / คงไม่เคลม — ไม่ปล่อยกระดาษกับบัญชี
ขัดกันเงียบ. **checker ตัวที่ 11** `js_dup_method_check.py` จับ class นี้ทั้งเรพ
→ เจอตัวที่สองทันที: `projects.html openEdit` ซ้ำ (rename ด้วย prompt ทับฟอร์ม
แก้ไขเต็ม — ปุ่มแก้ไขโครงการแก้ได้แค่ชื่อมาตลอด) ลบตัวทับ ฟอร์มเต็มกลับมา;_

_รอบ 104 — **ตัวเลือกกระดาษ: ทีมนักบัญชี + ทีม UX ตรวจแล้วปรับตาม**:
เคสจริงจากผู้ใช้ — กลุ่ม "รับเงินแล้ว" ขึ้นหัวข้อแต่**ว่างเปล่า** และ
"ใบแจ้งหนี้/ใบกำกับภาษี" หายทั้งชุด เพราะ facade ซ่อนหัวใบกำกับตามธง
`vatRegistered === false` ซึ่งอาจผิด/ยังไม่ได้ติ๊กทั้งที่บริษัทจดจริง. แก้:
(1) **เลิกซ่อนตามธง** — แสดงครบเสมอ เลือกแล้วขึ้นกล่องเตือนแดง §90/2 พร้อม
ลิงก์ `settings.html?tab=tax` (ด่านจริงคือ approve ฝั่ง server) (2) หัวกลุ่มที่
option ถูกซ่อนหมดต้องหายทั้งกลุ่ม (`_hideEmptyPaperGroups`) (3) resolve
กระดาษจาก **flag จริง** ผ่านตัวตัดสินกลาง `_resolveModeFromFlags` ใช้ร่วมกับ
`_syncIssueModeUi` — เลิกผูกกับธง VAT ตอน hydrate.
**ผลตรวจ 2 ทีม (subagent)**: ทีมนักบัญชีไล่ 16 เคสธุรกิจ — mapping/JE ถูกเกือบ
หมด แต่พบ (ก) hint ใบสำคัญรับ **drift จาก JE จริง** (เขียน "Cr รายได้รับ
ล่วงหน้า" แต่ JE จริง = Cr รายได้+VAT) + เงินรับที่ไม่ใช่รายได้ควรไป JV —
แก้ title แล้ว (ข) hint มัดจำ/ใบส่งของไม่เตือน tax point §78 — เติมแล้ว
(ค) เคสรับเงินบางส่วน ณ วันออก — เติมคำแนะนำใน hint 3-in-1 แล้ว.
ทีม UX เดิน 4 persona — แม่ค้ารับโอนเลือกผิดเพราะ "ขายสด"/"ตั้งลูกหนี้"
แอดมินร้านวัสดุจบที่ใบวางบิลเพราะหาคำว่า "บิล": **เรียงกลุ่มใหม่** (รับเงินแล้ว
ขึ้นก่อน — งานที่ทำบ่อยสุด) ชื่อกลุ่ม = คำตอบ "รับเงินหรือยัง?" · ย้ายมัดจำ/
ใบเสร็จเข้ากลุ่มรับเงินแล้ว · เลิกคำ "ขายสด→ลูกค้าจ่ายแล้ว (เงินสด/โอน)",
"3-in-1→ใบเดียวครบ 3 อย่าง", ตัด Dr/Cr/e-Tax T03/IsDeposit/ชื่อ flag ออกจาก
ทุก hint (ภาษาคนก่อน มาตราตามหลัง) · "ใบกำกับภาษี" เดี่ยวติดป้าย "เหมือนข้อบน
ต่างแค่หัวกระดาษ" กันเลือกผิด · ตัดคำ "มัดจำ" ออกจากใบสำคัญรับ (ทับกับใบมัดจำ).
งานที่จดไว้ทำต่อ (ยังไม่ทำ): กระดาษ "ใบส่งของ/ใบกำกับภาษี" (e-Tax T04) ·
คำถามนำ 2 ข้อเหนือ dropdown · gate ใบกำกับอย่างย่อด้วย IsRetailApproved;_

_รอบ 103 — **ใบวางบิลรวมใบแจ้งหนี้หลายใบ — จาก doc ที่โกหกให้เป็นของจริง**:
tooltip กับ doc เขียนว่า "ใบรวมยอด invoice หลายใบไปวางบิลครั้งเดียว" มานานแต่
โค้ดไม่มีทางทำ (ConvertDocumentAsync รับใบเดียว · ไม่มี Invoice→BillingNote ใน
convert map) — ผู้ใช้ต้องพิมพ์บรรทัดเอง. เพิ่มเส้นทาง compose จริง:
`GET document/billing-note/outstanding?contactId=` (ใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้
ที่ Approved/Sent/PartiallyPaid/Overdue + BalanceDue>0 ของลูกค้า พร้อมบอกใบที่
ถูกวางบิลแล้วอยู่ใบไหน) + `POST document/billing-note/from-invoices` →
`CreateBillingNoteFromInvoicesAsync` (ไฟล์ใหม่ `DocumentService.BillingNote.cs`,
class เปลี่ยนเป็น partial): ตรวจ ชนิด/สถานะ/ยอดค้าง/ลูกค้าเดียวกันทั้งชุด/
ห้ามซ้ำใบวางบิล active → สร้าง BN ร่างผ่าน `CreateDocumentAsync` ปกติ (เลขจริง
ออกตอนอนุมัติ · **ไม่ลง JE** — ตัวหนี้อยู่ที่ใบต้นทาง) 1 บรรทัด = 1 ใบ ยอด =
BalanceDue (รวม VAT ของใบต้นทางแล้ว → VatRate 0) เรียงตามวันที่ · ลิงก์ต้นทาง
ต่อบรรทัดเก็บใน **`DocumentLine.SourceDocumentId` (คอลัมน์ใหม่ + migration)**
ใช้กันรวมใบเดิมซ้ำ (ทวงลูกค้าซ้ำสองทาง = เสียเครดิต). UI: ฟอร์มใบวางบิล +
เลือกลูกค้า → กล่องฟ้าแสดงใบค้างให้ติ๊ก (ใบที่วางบิลแล้ว disable + โชว์เลข BN)
+ ยอดรวมสด → ปุ่มสร้าง → ปิดฟอร์ม เปิดใบที่สร้าง. ตรวจด้วย simulation
validation matrix 116 เคส (ชนิด×สถานะครบ + dedup/คนละลูกค้า/ใบลบ/เรียงลำดับ)
— ผ่านหมด. **audit ครบทุก DocumentType ในรอบเดียวกัน**: convert map ครบถ้วนดี
(PR→PO→GRN→PI→PV · Expense→PV/CIL · Receipt/PV→CN/DN · CN terminal) · พบ+แก้
อีกจุด: ใบมัดจำเป็น pseudo-type (DB = Receipt+IsDeposit) เปิดแก้แล้ว facade
เดิมชี้ "ใบเสร็จ" — ตอนนี้ชี้ "ใบมัดจำ" ถูกต้อง;_

_รอบ 102 — **ฟอร์มสร้างเอกสาร: "ประเภทเอกสาร" ชั้นเดียว = กระดาษที่จะออก**:
ผู้ใช้ยังงง "ใบแจ้งหนี้/ใบกำกับภาษี" กับ "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จ" สร้าง
ต่างกันยังไง ต้องติ๊กจ่ายไหม — เพราะการเลือกเป็น 2 ชั้น (ชนิดดิบ → dropdown
"หัวกระดาษ" ที่โผล่ทีหลัง) + ติ๊กจ่าย. รวมเป็น **ตัวเลือกเดียว `fPaper`**:
รายการคือกระดาษปลายทางตรง ๆ จัดกลุ่มตาม "เงิน" (ก่อนขาย/เรียกเก็บ · ขายเครดิต
ยังไม่รับเงิน · รับเงินแล้วจบในใบเดียว — ระบบบันทึกรับเงินให้ · รับเงินอื่น ๆ ·
ปรับปรุงหนี้ · ฝั่งรายจ่าย) เลือกแล้วระบบตั้ง `fDocType` (ยังเป็น source of
truth เดิม — payload/แปลง/JE ไม่แตะ) + issue mode + flag ทุกตัวให้เอง.
กลไก: `_PAPERS` map กระดาษ→(type, mode) · `onPaperChange` → ตั้ง type (เรียก
`onDocTypeChange` เฉพาะตอนชนิดดิบเปลี่ยนจริง) แล้วตั้งโหมดหลังรอบ deferred ของ
`_syncIssueModeUi` (คิว FIFO — กันโดน resolve จาก flag เก่าทับ) ·
`_syncPaperFromState` (เรียกท้าย `_syncIssueModeUi` ทั้งสองทางออก) ซิงก์ย้อน
ตอน hydrate ใบเดิม/ใบแปลง/โค้ดตั้งชนิดเอง + copy `disabled` (แก้ไขใบเดิม
เปลี่ยนชนิดไม่ได้เหมือนเดิม) · `_syncPaperOptionVisibility` ใช้กติกาชุดเดียว
กับของเดิม: ฝั่งรายรับ/จ่าย (CN/DN อยู่ทั้งสองฝั่ง) · ไม่จด VAT ซ่อนหัวใบกำกับ
ทั้งชุด · ใบแปลงจากขายเครดิตซ่อน ขายสด/หัวรวม/3-in-1 และเปิด `tax_paid` แทน ·
โหมดที่ใบเดิมใช้แต่บริบทซ่อน — เปิด option ให้เลือกเห็น ไม่เด้งไปค่าอื่นเงียบ.
select เดิม (`fDocType`, `fIssueMode`+label) ซ่อนใน DOM — hint ฟ้า (ชนิด)/เขียว
(โหมด) ยังแสดง โดยกระดาษที่มีโหมดโชว์เฉพาะ hint เขียวกันข้อความตีกัน ·
"ตัวเลือกขั้นสูง" (checkbox จริง) ยังอยู่เป็นทางหนีไฟ. ยืนยันด้วย harness รัน
Page จริงทั้ง object กับ DOM จำลอง (select สร้างจาก markup จริง) 40+ assertion:
เลือกกระดาษ→flag ครบ 13 แบบ · hydrate ย้อน 8 แบบ · ใบแปลง/ไม่จด VAT/ฝั่งจ่าย/
ล็อกตอนแก้ไข — ผ่านหมด · negative test (ตัดบรรทัดตั้งโหมด) ฟ้อง 9 เคส;_

_รอบ 101 — **แท็บ ภ.พ.36 ในหน้ารายงานภาษี: งวดที่นำส่งแล้วหายไปทั้งงวด**:
แถวเทา "ยังไม่สร้าง" (รอบ 36) ดึงจาก `dashboard.pending` อย่างเดียว — พอกด
นำส่ง งวดนั้น**หลุดจาก pending ไปอยู่ recentHistory** แถวเทาจึงหายตาม และถ้า
ไม่เคยกดสร้างรายงาน งวดนั้นก็ไม่โผล่ที่ไหนเลยทั้งที่จ่ายเงินไปจริง ⇒ ผู้ใช้
เข้าใจว่า "ไม่มียอด". แก้ 2 ชั้น: (1) **server** `RemitAsync` ของ VatPp36 เรียก
`TryEnsurePp36ReportAsync` สร้างรายงานงวดนั้นให้อัตโนมัติ — idempotent, อยู่
**นอก** transaction ของการนำส่ง และห้าม throw (นำส่ง commit ไปแล้ว ห้ามล้มย้อน
หลังเพราะสร้างรายงานพลาด) (2) **UI** รวมงวดจาก `recentHistory` เข้าแถวเทาด้วย
ป้าย "นำส่งแล้ว · ยังไม่มีรายงาน" (พื้นเหลือง) — ครอบข้อมูลเก่าที่นำส่งไปก่อน
มี auto-generate. **ความสอดคล้องของปุ่ม/สถานะ**: เดิมเช็ค
`taxType.toLowerCase().includes('vat')` ซึ่ง `'VatPp36'` ก็ผ่าน ⇒ แถว ภ.พ.36 มี
ปุ่ม **ภ.ซื้อ · ภ.ขาย · ภ.พ.30** ทั้งที่ §87 รายงานซื้อ-ขาย และแบบ ภ.พ.30 เป็น
ของ VAT ปกติ ภ.พ.36 (§83/6) ไม่มีของตัวเอง — จำกัดเป็น `VAT` จริงเท่านั้น แล้ว
ใส่ปุ่ม "💸 หน้านำส่ง/ใบเสร็จ" แทน; ฝั่ง Excel export ก็เลิกแตกชีต
"รายงานภาษีขาย/ซื้อ" ให้ ภ.พ.36 (ใช้ชีต "รายการ" + สรุป) ให้ตรงกัน;
เพิ่มป้ายใต้สถานะรายงานบอก **สถานะการนำส่งเงิน** (นำส่งแล้ววันไหน / รับรู้ภาษี
ซื้อเข้า ภ.พ.30 แล้วหรือยัง) เพราะ "ร่าง" ของรายงานคนละเรื่องกับการจ่ายเงิน
ผู้ใช้เห็นแล้วเข้าใจว่ายังไม่ได้นำส่ง · ตัวเทียบชนิดภาษีรวมเป็น `_isType()`
ตัวเดียว รองรับทั้งชื่อ enum และตัวเลข (serializer ส่งได้ทั้ง 2 แบบ);_

_รอบ 100 — **"เมนู e-Tax กดแล้วขึ้นหน้าตั้งค่า" — ที่แท้คือหน้าเข้าไม่ได้เลย**:
ไม่ใช่ดีไซน์ แต่เป็นบั๊ก: `pages/etax.html` อ่าน `localStorage['companyId']`
ซึ่ง**มีแต่ portal `/connect` เท่านั้นที่เขียน** แอปหลักไม่เคยเขียนคีย์นี้เลย
⇒ ได้ null ทุกครั้ง → `window.location.href='/pages/settings.html'` ทันที
= หน้า e-Tax Invoice เข้าไม่ได้สักครั้งตั้งแต่เขียนมา. แก้ให้ใช้ resolver กลาง
`Layout.getCompanyId()`. **defect class เดียวกันอีก 2 จุด**: `mobile-expense.html`
(ขึ้น "ต้อง login + เลือกบริษัทก่อน" ตลอด ส่งเบิกไม่ได้) และ
`pages/signatures-logic.js` อ่าน `'selectedCompanyId'` ที่ไม่มีใครเขียนเลยทั้งเรพ
⇒ แท็บรออนุมัติว่าง + ปุ่มอนุมัติ/ปฏิเสธ `return` เงียบ ๆ (กดแล้วไม่มีอะไร
เกิดขึ้น ไม่มี error) — แก้ทั้งหมด + เปลี่ยน guard ให้ดังแทนที่จะเงียบ.
**คำถาม "เมนูไหนไม่ได้ใช้ก็เอาออก"**: e-Tax เป็นฟีเจอร์จริงตามกฎเหล็ก #2 F
(ETDA ขมธอ.3-2560) ไม่ควรลบทิ้ง แต่เป็น opt-in ⇒ เพิ่มธง `etaxOnly` บน nav item
คู่กับ `_etaxEnabled` ที่อ่านจาก `/settings` **ครั้งเดียวกับที่ดึง vatRegistered
อยู่แล้ว (ไม่มี request เพิ่ม)** → บริษัทที่ยังไม่เปิดใช้ e-Tax ไม่เห็นเมนูนี้
เลย; ถ้าเปิดหน้ามาแล้วยังไม่ได้เปิดใช้ แบนเนอร์อธิบายว่าเมนูนี้ทำอะไร + ปุ่ม
"ซ่อนเมนูนี้" (ผ่านกลไกซ่อนเมนูกลาง เปิดกลับได้ที่ ตั้งค่า > ทั่วไป).
ตรวจ nav ทั้ง 107 รายการ — ปลายทางมีไฟล์จริงครบทุกอัน ไม่มีเมนูตายอื่น;_

_รอบ 99 — **หน้านำส่งภาษี/ประกันสังคม: กรองตามประเภทแบบได้ + ลด noise**:
หน้าจอจริงมี 13 รายการค้างจาก 5 แบบ × 5 งวด เรียงปนกัน ไม่มีตัวกรองเลยสักตัว
และทุกแถวขึ้น "เลยกำหนด" แดง + ปุ่ม primary น้ำเงิน ⇒ ทุกอย่างเด่นเท่ากัน =
ไม่มีอะไรเด่น (alarm fatigue) ผู้ใช้ที่จะยื่น "ภ.พ.30 เม.ย." ต้องไล่สายตาเอง.
เพิ่ม: **ชิปกรองตามแบบ** (เลือกได้หลายประเภท พร้อมจำนวน+ยอดในชิป) · ตัวกรอง
สถานะ/งวด/ค้นหา · **3 มุมมอง** (ตามงวด/ตามประเภท/รายการ) พับกลุ่มได้ · KPI
กดเป็นตัวกรองลัด · ป้ายบอก **"เลยกำหนด N วัน"** แทนคำลอย ๆ + แถบสีความด่วน
หน้าแถว (ปุ่มลดเป็น outline เท่ากันหมด ให้สีสื่อความด่วนแทน) · ประวัติกรอง
ประเภท/ค้นหา + สรุปยอด · มือถือแปลงตารางเป็นการ์ด · จำตัวกรองต่อบริษัทใน
localStorage · แถบ ภ.พ.36 "รอรับรู้ภาษีซื้อ" ย้ายออกนอกการ์ดที่ถูกกรอง (ตัว
กรองต้องไม่ซ่อนงานที่ค้างอยู่). ตัวตัดสินความด่วน `_urgency` เป็นตัวเดียว ใช้
ร่วมทั้ง badge/แถบสี/ตัวกรอง. **แถมแก้บั๊กร่วมทั้งระบบ**: `Layout.toast` ไม่เคย
รับพารามิเตอร์ที่ 3 (ระยะเวลา) แต่มีคนเรียกส่งมาแล้ว **40 จุด** → ข้อความสอน
ขั้นตอนยาว ๆ หายใน 3.5 วิ; และ `.toast-warning` ไม่มีสีพื้นเลย (`.toast` ตั้ง
`color:#fff`) = ตัวอักษรขาวบนขาว มองไม่เห็น — แก้ทั้งคู่;_

_รอบ 98 — **รายการประจำ: ออกเอกสารอนุมัติ + ส่งอีเมลได้จบในฟอร์มเดียว**:
เดิมมี hook `OnRecurringDocumentCreatedAsync` อยู่แล้ว แต่มันส่งเฉพาะเมื่อ
tenant ไป**สร้างกฎเองที่หน้า "ตารางส่งอีเมล"** (trigger `RecurringInvoiceCreated`)
⇒ ผู้ใช้ที่ตั้ง SMTP บริษัทไว้แล้วยังไม่มีอะไรถึงลูกค้าเลยและไม่มีที่ไหนบอก
(silent no-op เต็มรูป). เพิ่มธง `RecurringTransaction.AutoSendEmail` +
ช่องติ๊กในฟอร์ม: ไม่มีกฎ → ระบบ enqueue เองด้วย **rule เสมือน** (`Id=Guid.Empty`
→ `EmailQueue.RuleId=null`) ส่ง**ทันที** ไม่รอ 09:00; มีกฎอยู่แล้ว → กฎชนะ
ไม่ส่งซ้ำ. invariant `AutoSendEmail ⇒ AutoApprove` บังคับทั้ง service
(`CreateAsync`/`UpdateAsync`) และ UI (ล็อกช่อง + บอกเหตุผล §86/4 ใบร่างเป็น
`DRAFT-{guid}` ส่งไม่ได้) — ไม่ใช่ปล่อยติ๊กแล้วเงียบ. ฟอร์มดึงสถานะอีเมลจริง
จาก `GET /email-config` มาแสดง (พร้อม/ยังไม่ทดสอบ—ครอบทั้ง SMTP/MS Graph/Gmail
ไม่ใช่ดูแค่ `smtp.host`/ยังไม่ตั้ง→ใช้อีเมลกลาง) + เช็คว่าผู้ติดต่อมีอีเมลไหม
ตั้งแต่ตอนบันทึก + เตือนเมื่อชนิดเอกสารเป็นฝั่งซื้อ (จะส่งไปหาผู้ขาย) +
`settings.html` รับ deep link `?tab=email` ได้แล้ว (เดิมลิงก์ไปตกแท็บแรก).
ธงนี้ไม่มีผลกับ template สมุดรายวัน — ปัดทิ้งที่ service (ตรวจด้วย simulation
102 เคส ผ่าน invariant `AutoSendEmail ⇒ AutoApprove` + `⇒ ไม่ใช่ journal`);_

_รอบ 93 — **single source of truth: เดือนเคลม = อยู่ในรายงานจริง**: ผู้ใช้
ไม่ยอมกด "สร้างใหม่" (ล้างการติ๊ก/แก้ยอดของบรรทัดอื่นทั้งงวด — ถูกต้อง) →
เพิ่ม `TaxService.TryPullIntoDraftReportAsync(companyId, docId)`: หา**รายงาน
ร่างของงวดเคลม**แล้วดึงใบเดียวเข้าโดยใช้ `PullDocumentIntoReportAsync` เดิม
(INSERT บรรทัดเดียว + recalc — บรรทัดอื่นไม่ถูกแตะ) best-effort คืนข้อความ
ไม่ throw. ผู้เรียก: (1) `RecognizePp36InputVatAsync` หลัง commit — ทุกใบที่
รับรู้ (2) `POST document/{id}/vat-claim-period` หลังตั้งงวด — toast โชว์ผล
จริงจาก backend. + backfill เลข/วันที่ใบเสร็จ RD ใบเก่าจาก FilingNumber ของ
การนำส่งงวดเดียวกัน (idempotent) + ตาราง modal scroll แนวนอน;_
_รอบ 92 — **ภ.พ.36 ไม่โผล่ใน "ดึงเอกสาร"/ภ.พ.30 — ต้นเหตุจริง**: ทั้ง
`GenerateVatReport` และ `GetPullableDocumentsAsync` รับ PV เข้าฝั่งภาษีซื้อ
**เฉพาะที่ `HasTaxInvoiceReference=true`** (นิยามเดิม = อ้างใบกำกับซื้อเพื่อขอ
เครดิต) — ใบ ภ.พ.36 ไม่มีใบกำกับไทยจึงไม่เคยติ๊ก และ `RecognizePp36InputVatAsync`
ก็ไม่เคยตั้งให้ (ต่างจากเส้น §86/4 `ReclassifyUndueInputVatAsync` ที่ตั้งอยู่แล้ว)
⇒ GL มี Dr 11610 แต่รายงานไม่มีแถว + ปุ่มดึงเอกสารไม่เห็นใบเลยทุกกรณี. แก้:
recognition ตั้งธงให้ PV (ใบเสร็จ RD = ใบกำกับ §86/14 สิทธิ์เครดิตสมบูรณ์) +
**migration backfill** ใบที่รับรู้ไปแล้วก่อนหน้า (idempotent) + `ClaimBasisDate`
ของใบ ภ.พ.36 ใช้ `Pp36RdReceiptDate` เป็นฐาน §82/3 แทนวันจ่าย (เดิมหน้าต่าง
สั้นกว่าสิทธิ์จริง ~1 เดือน และงวดที่ดึงได้เพี้ยน);_
_รอบ 91 — **ภ.พ.36 หลังรับรู้ (เข้า ภ.พ.30 แล้ว...แต่หาไม่เจอ)**: สาเหตุจริง
2 ชั้น — ป้าย ✓ ไม่บอกงวดเคลม และ**รายงาน ภ.พ.30 เป็น snapshot**: สร้างไว้ก่อน
กดรับรู้ = บรรทัดใบ ภ.พ.36 ยังไม่อยู่จนกด "สร้างใหม่". แก้: ป้ายกลายเป็นปุ่ม
เปิด modal รายใบ (`GET remittances/pp36/recognized`) โชว์เดือนเคลม + สถานะใน
ภ.พ.30 ต่อใบ (ไม่มีรายงาน/สร้างก่อนรับรู้—บอกให้กดสร้างใหม่/อยู่ในรายงาน/
ยื่นแล้ว) + **แก้เดือนเคลมต่อใบจาก modal ได้เลย** ผ่าน endpoint ใหม่
`POST document/{id}/vat-claim-period` → `SetInputVatClaimPeriodAsync` ซึ่งห่อ
ตัวตรวจกลาง `ApplyInputVatClaimPeriodAsync` เดิมทั้งชุด (งวดร่างย้ายบรรทัด
อัตโนมัติ · §82/3 · งวดยื่นแล้ว block) — ไม่มีกติกาใหม่;_
_รอบ 90 — **ภ.พ.36 รอบสาม (VAT/WHT บนใบเดียวกัน)**: (0) **ฐานภาษี = ยอดจ่าย
จริงเสมอ ห้ามโหมดราคารวมภาษี** — ผู้ขาย ตปท. ไม่เก็บ VAT ไทย ยอดจ่ายไม่มี VAT
ปน การติ๊ก "ราคารวมภาษี" จะถอด 7/107 ออกจากยอดจ่าย ⇒ ฐานหด นำส่งขาด (จ่าย
11,009.25 → นำส่ง 720.23 แทน 770.65) + เจ้าหนี้ตั้งขาดเท่า VAT: ฟอร์มปลด+ล็อก
ช่องเมื่อติ๊ก 🌐 (ปลดล็อกเมื่อเลิกติ๊ก/เปลี่ยนชนิด) + guard ตอนอนุมัติกันทาง
API (JE ฝั่ง Cr ถูกอยู่แล้ว: เจ้าหนี้ = ฐาน · 21912 = VAT แยก); (1) guard อนุมัติ —
IsForeignService + VAT=0 → block (self-assess คือหัวใจ ภ.พ.36; ฟอร์มตั้ง 7%
ให้แล้วแต่ API ยิงข้ามได้ ปล่อยผ่าน = ใบหายทั้งวงจรเงียบ ๆ) (2) WHT ม.70:
`ResolveWhtPayableAccountAsync` รับ isForeignService → **21918** (เดิมตกไป
21916/17 ทุกใบ) · dashboard นำส่งเพิ่ม lane **WhtPnd54** (ภงด.54 · 21918 ·
กำหนด 7/15) + ตัดใบต่างประเทศออกจากการนับ ภงด.3/53 (3) รายงาน ภงด.3/53/54:
`PayeeInScope` เพิ่มสัญญาณ `doc.IsForeignService` (เดิมดูแค่ CountryCode ที่
มักไม่ได้กรอก → ใบเข้า ภงด.53 ผิดแบบ) (4) banner ฟอร์ม: อัตรา ม.70 15%/10%/
DTA + 40(8) ไม่เข้า ม.70 · ยอดเคลม ภ.พ.30 = ยอดนำส่ง (ยกเว้นส่วนต้องห้าม
§82/5 ที่นำส่งเต็มแต่เคลมไม่ได้ — ถูกต้องตามกฎหมาย);_
_รอบ 89 — **ภ.พ.36 รอบสอง (4 คำถามผู้ใช้)**: (1) แท็บ ภ.พ.36 เติมแถว "ยังไม่
สร้าง" จากงวดที่มียอดจริง (remittance dashboard — แหล่งเดียวกับหน้านำส่ง) +
ปุ่มสร้างคลิกเดียว (2) 📎 ประวัติกดดูไฟล์ได้ (fetch+blob JWT) (3) หน้า 11640
แยกใบ ภ.พ.36 เป็น section ต่างหาก — ใบพวกนี้ไม่มีวันเติมใบกำกับไทยได้
(CompleteSupplierTaxInvoiceAsync ก็ throw อยู่แล้ว) CTA ชี้ไปหน้านำส่งภาษี
(4) **§86/14**: `Document.Pp36RdReceiptNumber/Date` stamp ตอนรับรู้ (จาก
FilingNumber ของ remittance หรือ prompt ใหม่) → เลขที่ใบกำกับในรายงานภาษีซื้อ
ภ.พ.30 + CSV ยื่น = เลขใบเสร็จ RD ไม่ใช่ invoice ผู้ขาย ตปท. + **ตัด JE
ภ.พ.36R- ออกจาก scan JE ของ ภ.พ.30** (เดิมขึ้นบรรทัด JV ซ้อนกับใบจริงที่เข้า
ทาง BecameClaimableAt — ที่ผู้ใช้เห็น "ขึ้นเป็น JV" + เสี่ยงนับซ้ำ);_
_รอบ 88 — **วงจร ภ.พ.36 ครบถึง ภ.พ.30**: ผู้ใช้นำส่งแล้วหาใบใน ภ.พ.30 ไม่เจอ
— เพราะขั้นที่ 2 "รับรู้ภาษีซื้อ" (RecognizePp36InputVatAsync: 11640→11610 +
stamp BecameClaimableAt เมื่อได้ใบเสร็จ RD §77/2) ซ่อนอยู่ในแท็บประวัติโดยไม่มี
สถานะ. ปิดช่องว่าง: (1) dashboard เพิ่ม `Pp36AwaitingRecognition` (งวดที่นำส่ง
แล้วแต่ยังมีใบพัก 11640 — ตรวจจาก JE Reference ภ.พ.36R-YYYYMM ชุดเดียวกับ
กติกา idempotent เดิม) → แถบฟ้าค้างบนสุดหน้านำส่งภาษีพร้อมปุ่มรับรู้ (2)
history ต่อแถวมี `Pp36Recognized` → ✓ เข้า ภ.พ.30 แล้ว / ปุ่มรับรู้ (3) หน้า
success หลังนำส่งบอกขั้นถัดไป (4) แถวประวัติไม่มีไฟล์ → ปุ่ม ＋แนบ ย้อนหลัง
(5) ฟอร์ม PV: ติ๊ก 🌐 → banner วงจร 4 ขั้น + ตั้ง VAT 7% self-assess ให้
บรรทัดที่เป็น 0 (ปล่อย 0 = ใบไม่เข้า dashboard นำส่งเลย) + hint เมื่อคู่ค้า
ไม่มีเลขภาษีไทย (ชี้ ไม่ auto-ติ๊ก);_
_รอบ 87 — **ใบกำกับภาษีอย่างย่อ §86/6**: ขายมี VAT + ผู้ซื้อไม่รับใบกำกับ/
walk-in/ข้อมูล §86/4 ไม่ครบ (บุคคลธรรมดาที่ระบบ auto-downgrade ตอนอนุมัติ) →
หัวเปลี่ยนจาก "ใบเสร็จรับเงิน" เปล่า (ซึ่งผิดหลัก §86 — ผู้จด VAT ต้องออก
ใบกำกับบางรูปแบบทุกการขาย) เป็น **"ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"**
(ใบกำกับขายเชื่อ → "ใบกำกับภาษีอย่างย่อ" เดี่ยว — ห้ามคำใบเสร็จ ม.105) +
บรรทัด "ยอดรวมทั้งสิ้นได้รวมภาษีมูลค่าเพิ่มแล้ว" (§86/6(6)) ใต้หัวทั้ง 2
renderer · ตัวตัดสินกลาง `IsAbbreviatedTaxInvoiceDoc` ใช้ร่วม title+โน้ต ·
ยกเว้น: ใบเสร็จ settlement ของใบกำกับ (ใบเสร็จเปล่า — กันใบกำกับซ้ำ) และมัดจำ
VAT พักรอ · ผู้ซื้อเคลมภาษีซื้อไม่ได้ §82/5(2) · VAT ขายเข้า ภ.พ.30 ครบ ·
เทสต์ `AbbreviatedTaxInvoiceTitleTests` เรียก resolver ตัวจริง;_
_รอบ 86 — **เปิดแก้ JE ของเอกสารทั้งใบ**: แผงปรับปรุง JE
(`AdjustDocumentJournalEntryAsync`) เลิกบล็อกบัญชีคุม — แก้ได้ทุกบรรทัดรวม
ลูกหนี้/เจ้าหนี้/ภาษี/มัดจำ/WHT และยอดต่อบรรทัด ตามคำขอผู้ใช้ hard rule
เหลือ 2 ข้อ: Dr = Cr และ **ยอดรวมแต่ละฝั่งต้องเท่าเอกสาร** (Σ Dr ปลายทาง =
Σ Dr ใบเดิม) · การขยับบัญชีคุมทุกตัวถูกจดลง audit เป็น `controlAccountsMoved`
(ก่อน→หลัง) + UI เตือน 2 ชั้น (ป้าย ⚠️ บนแถว + confirm ตอนบันทึกเมื่อยอดคุม
เปลี่ยนจริง) · gate ระดับเอกสารเดิมยังครบ (แบบยื่นแล้ว/e-Tax/งวดปิด/ใบมัดจำ/
ปรับซ้อน) — ป้าย 🔒 + "บัญชีคุมห้ามขยับ" ใน log รอบเก่าคือพฤติกรรมก่อนรอบนี้;_
_รอบ 85 — **OCR ส่วนลดซ้อน + คอลัมน์ยอด JE**: ใบ Scommerce ส่วนลด 2 ชั้น —
map บรรทัดเก็บส่วนลดรายบรรทัดจากกระดาษ (qty×unitPrice − amount) + reconcile
เขียนใหม่ยึด**ยอดรวมทั้งสิ้น**เป็นหลัก: บรรทัด ex-VAT เทียบกับฐานภาษี (ยอดรวม
− VAT) เท่านั้น ส่วนเกิน → `billDiscountAmount` ลงช่อง "ส่วนลดท้ายบิล"
ที่เดียว ห้ามกระจายใส่บรรทัด (เดิมเทียบข้ามฐาน incl/excl VAT แล้วกดบรรทัดลง
จนฐานภาษี = ยอดรวมทั้งบิล → VAT ถูกบวกซ้ำ) · ยอด Dr ใน "การบันทึกบัญชี"
ท้ายเอกสารเยื้องซ้ายจากยอด Cr 16px แบบบัญชีแยกประเภท (ทั้ง 2 renderer);_
_Last verified against codebase: 2026-08-28 (รอบ 98 — **ตารางกฎหมายที่ถูกคัดลอกไปเขียนใหม่**:_
_ต่อจากรอบ 97 — สาม "ตารางความรู้เชิงกฎหมาย" ที่มีสำเนามือมากกว่าหนึ่งชุด และ_
_ทุกชุดตอบไม่ตรงกัน (defect class "รายการที่คัดลอกมาด้วยมือ = drift แน่นอน"):_
_(1) **อัตราหัก ณ ที่จ่าย มี 2 ตาราง ไม่ตรงกันเอง และไม่ตรงกฎหมายทั้งคู่** —_
_`WithholdingTaxCertController.GetIncomeTypes` ใส่ 40(3) ค่าสิทธิ = **5%** (ท.ป.4/2528_
_คือ 3%) และ 40(1) เงินเดือน = **3% คงที่** ทั้งที่กฎหมายใช้อัตราขั้นบันได ·_
_`wht-credit.html` dropdown ยุบ "ดอกเบี้ย/เงินปันผล" เป็นตัวเลือกเดียวที่ **1%**_
_ทั้งที่ดอกเบี้ยบุคคล 15% · ดอกเบี้ยนิติบุคคล 1% · ปันผล 10%. dropdown คือ_
_คำแนะนำเดียวที่ผู้ใช้เห็นตอนกรอกอัตรา ⇒ ป้ายผิด = ยอดหักผิดตั้งแต่ต้นทาง_
_แล้วไหลไป ภ.ง.ด.3/53 และเครดิต CIT ทั้งสาย → ยุบเป็น `Helpers/ThaiWhtRateTable.cs`_
_(แยกอัตราบุคคล/นิติบุคคล · เงินเดือนเก็บ null แทนใส่ตัวเลขปลอม) แล้วให้_
_หน้าเว็บ**สร้าง dropdown จาก `/api/reference/income-types`** — drift เป็นศูนย์_
_โดยโครงสร้าง + `ShouldWithhold()` ที่บังคับด่าน ฿1,000 แบบสะสมตามข้อ 12_
_(2) **checksum เลขผู้เสียภาษีฝั่ง JS มี 2 ชุดที่ตอบไม่ตรงกันบนหน้าเดียวกัน** —_
_`layout.js validateTaxId` **ไม่มีกติกาหลักแรก 0-8** ⇒ เลขขึ้นต้น 9 ผ่านด่านฝั่ง_
_ผู้ใช้แล้วไปตายที่เซิร์ฟเวอร์โดยไม่บอกว่าผิดตรงไหน · `smart-hooks.js` มีสำเนา_
_ที่ตรวจหลักแรก → เหลือตัวเดียว `Layout.taxIdCheck` (ตรงกับ `Helpers/ThaiTaxId`)_
_และ smart-hooks เรียกตัวนั้น_
_(3) **แปลง พ.ศ.→ค.ศ. มี 4 เกณฑ์ที่ต่างกัน** (`> 2500` · `>= 2400` · `> 2400` ·_
_`> currentYear + 10`) ⇒ ปีย่อ "69" เส้นทาง `ParseThaiDocument` ได้ **2069**_
_ขณะที่ `EnrichFromRawText` ในไฟล์เดียวกันได้ 2026 — ใบเดียวกันลงคนละปีตาม_
_เส้นทาง OCR → `ThaiDate.NormalizeYear()` ตัวเดียว + เทสต์ที่ reproduce 2069);_
_รอบ 97 — **ไล่หา defect class ที่แก้แล้วแต่ยังเหลือที่อื่น**:_
_ตั้งทีมไล่ตรวจ 8 defect class ที่เคยแก้ไปรอบเดียวว่ายังเหลือตัวที่สองที่ไหนบ้าง_
_ผลคือเจอของจริงทุก class — ที่กระทบเงิน/ภาษี/ความเป็นส่วนตัวถูกแก้ในรอบนี้:_
_(1) **ข้อมูลการค้าข้ามผู้เช่ารั่ว** — `GlobalVendorIntelLearner.CanShareMoneyAggregates`_
_(ด่าน k-anonymity k=3) ถูกเขียนไว้ + มี doc-comment ระบุว่า "guarded by the k=3_
_read-time gate inside VendorIntelligenceService.PredictAsync" แต่ **ไม่มีใครเรียกเลย**_
_⇒ ถ้ามีผู้เช่ารายเดียวที่เคยทำธุรกรรมกับผู้ขายรายนั้น ผู้เช่ารายอื่นเห็น_
_ยอดเฉลี่ย/ต่ำสุด/สูงสุด/มัธยฐาน ของรายนั้นตรง ๆ บนแบนเนอร์ "ยอด X นอกช่วง min–max"_
_→ ใส่ด่านแล้ว: ต่ำกว่า k=3 ส่ง null ทุกช่องที่เป็นจำนวนเงิน (ช่องพฤติกรรมเปิดตามเดิม)_
_(2) **เครดิตภาษีถูกหักคิดจากยอดรวมทั้งใบ** — `EnsureWhtCreditFromCertAsync` เขียนว่า_
_`whtAmount = derived > 0 ? derived : (ExtractedTotalAmount ?? 0)` ⇒ ใบ 50 ทวิ ยอด 1,070_
_หัก 3% ถูกบันทึกเป็นเครดิต CIT **1,070 บาท** แทน 30 และ `IncomeAmount` เขียน 0 ทำให้_
_อัตราย้อนกลับเป็นอนันต์ ไม่มีด่านไหนจับได้ แถวนั้นเป็น `Received` ⇒ หักภาษีจริงใน_
_ภ.ง.ด.50 ทันที → แยกเป็น `Helpers/WhtCertAmountResolver.cs` (pure + เทสต์):_
_ฐาน×อัตรา → จำนวนเงินตัวอักษรบนกระดาษ (มีเพดาน "ภาษี < ยอดจ่าย") → **ไม่รู้**_
_(บันทึกเป็น Pending ที่ TaxService ไม่นับเป็นเครดิต ไม่ใช่เดายอด) · เพิ่ม_
_`MidpointRounding.AwayFromZero` · `PayerFormType` ตัดสินจาก `Company.BusinessType`_
_(ผู้ถูกหัก = เรา) แทน hardcode ภ.ง.ด.53_
_(3) **6 ช่องที่รับตอน Create/Update แต่ไม่มีใน `DocumentResponse`** —_
_`BuyerDeclinedTaxInvoice` (ธง §86/4 ที่คุมทั้งหัวกระดาษและหมายเหตุ e-Tax) ·_
_`PreparerName` · `PreparerSignatureBase64` · `DepositAppliedRef` ·_
_`DepositAppliedDrivesJournal` · `InputVatClaimPeriod` ⇒ ติ๊กแล้วเปิดแก้ใบ_
_ค่าเด้งกลับเงียบ ๆ ทุกครั้ง (บล็อกเดียวกับ `IssuedAsCashReceipt` ที่แก้ไปแล้ว —_
_ตกค้าง 6 ช่อง) · `InputVatClaimPeriod` ไม่มีคอลัมน์ของตัวเอง จึงคำนวณที่_
_`ResolveInputVatClaimPeriod` ตัวเดียวแล้วส่งเป็น "yyyy-MM" แทนให้หน้าเว็บประกอบเอง_
_(4) **สำเนา `salesTypes` ใน JS ที่ `DocumentSide.cs` ระบุชื่อไว้เองว่าเป็นตัวที่สาม**_
_ยังไม่เคยถูกแตะ ⇒ ใบลดหนี้**ขาย** (OurRole=Seller) เปิดฟอร์มรายจ่าย และใบส่งของ_
_จากผู้ขายเปิดฟอร์มรายได้ → เพิ่ม `DocumentSide.BuildSideMap()` ส่งมากับผลสแกน_
_(`OcrResultResponse.DocumentSideMap`) หน้าเว็บอ่านอย่างเดียว + เทสต์บังคับว่า_
_ทุก `DocumentType` ต้องถูกจัดฝั่ง (เพิ่มชนิดใหม่แล้วลืม = เทสต์แดง)_
_(5) **ครึ่งเซิร์ฟเวอร์ของการแก้ ship ไปคนเดียว** — `OcrCorrectionRequest` เปิดรับ_
_`VendorBranchCode`/`BuyerTaxId`/`BuyerBranchCode` และ persist แล้ว แต่ payload builder_
_ตัวเดียวของหน้า review ไม่เคยส่งมาเลย และ modal โชว์ผู้ซื้อเป็นข้อความอ่านอย่างเดียว_
_⇒ ผู้ใช้ยัง "ไม่มีทางแก้" และ `?? "00000"` ยังเป็นตัวเติมสาขาตัวจริง → เพิ่มช่องกรอก_
_3 ช่อง + ส่งใน correction + handoff อ่านค่าที่ผู้ใช้แก้ก่อน `'00000'`_
_(6) **XSS ค้างในโมดัลเดียวกับที่เพิ่งแก้** — ตาราง raw/debug ของ `document-scan.html`_
_ต่อค่าที่ OCR/กระดาษคุมได้เข้า `innerHTML` โดยไม่ผ่าน `Layout.esc` 6 จุด (ชื่อผู้ขาย/_
_เลขภาษี/เลขเอกสาร/วันที่/หมวด/engine) ทั้งที่ทุกจุดอื่นบนหน้าเดียวกัน escape ครบ ·_
_แก้พร้อมกับ `Layout.esc(...).substring()` ที่ตัดกลาง entity_
_(7) **VAT ปัดแบบ banker's rounding** ใน `SaasBillingDocumentService` 4 จุด +_
_`SampleDataController` ทั้งที่ `PlatformBillingDocumentIssuer` ที่คิดสูตร 7/107_
_เดียวกันใส่ `AwayFromZero` ไว้ถูกแล้ว (แก้ไม่ทั่วรอบก่อน)_
_(8) **ทะเบียนสินค้าถูกเขียนด้วยค่าที่แต่งขึ้น** — `OcrController` นำเข้าสต็อกตั้ง_
_`Unit: "ชิ้น"` (ทั้งที่ `UnitInferrer` ถูกสร้างมาเพื่อเรื่องนี้และเส้นทางเอกสารแก้ไปแล้ว)_
_และ `VatRate: 7m` ⇒ สินค้ายกเว้น §81/อัตรา 0 ถูกตั้ง 7% ใน**ทะเบียน** แล้วใบขาย_
_ทุกใบในอนาคตคิด VAT ผิดตาม → ใช้ `UnitInferrer` + อิงจากใบว่ามี VAT จริงไหม_
_(9) **`catch {}` กลืน error 4 จุด** — `ExtractedItemsJson` เพี้ยน = ตารางรายการว่าง_
_โดยไม่มี error (ผู้ใช้อนุมัติเอกสารที่ไม่มีบรรทัด) · บัญชีเครดิตของ JE ตั้งสินทรัพย์_
_ตกไปใช้ "บัญชี 21xx ตัวแรก" เงียบ ๆ · ยอดรายการประจำโชว์ ฿0 เมื่อ JSON มีตัวเลข_
_เป็นสตริง → ใส่ log + parse ให้ทน_
_(10) **`VendorClusteringService` รายงานว่าสำเร็จทั้งที่ไม่ได้เก็บอะไรเลย** —_
_รัน K-means ทั้งระบบแล้วทิ้งผล (โค้ดเขียนหมายเหตุว่า "will return assignments to_
_caller" แต่ `ClusterResult` ไม่มีช่อง assignment) ขณะที่หน้าแอดมินขึ้น "จัดกลุ่ม_
_สำเร็จ (N vendors)" → คืนสรุปกลุ่มจริง + ธง `Persisted=false` + ข้อความบอกตามจริง);_
_รอบ 96 — **prompt ที่มีช่องแต่ไม่มีใครเติม**:_
_ผลตรวจ "AI โยนข้อมูลไปพอให้คิดครบไหม" รอบสอง — พบรูปแบบเดิมซ้ำอีก 3 จุด คือ_
_prompt เตรียมช่องบริบทไว้ครบแต่ **ผู้เรียกไม่เคยส่งค่าลงไปเลยตั้งแต่วันแรก**_
_(defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้"):_
_(1) **`WhtCategoryPrompt`** มีพารามิเตอร์ 4 ตัว (ประวัติหักภาษีของผู้ขาย ·_
_ยอดสะสมปีนี้ · ค่าเฉลี่ย 6 เดือน · ธุรกิจของผู้ขาย) แต่ `DocumentAiAugmenter`_
_เรียกด้วย 8 ตัวแรกเท่านั้น ⇒ AI เดารหัสเงินได้จากคำอธิบายบรรทัดล้วน ๆ ทั้งที่_
_ระบบมี 50 ทวิ ของผู้ขายรายเดิมอยู่ในมือ → เพิ่ม `LoadWhtVendorContextAsync`_
_(join `WithholdingTaxCertLines`→`WithholdingTaxCerts` ย้อน 24 เดือน ตัดใบ Voided,_
_ทุก query มี `CompanyId`) + `vendorDominantGlAccount` จาก `OcrCategoryMapping`._
_แถมแก้กติกาด่าน **฿1,000 ที่เดิมเขียนผิดกฎหมาย** — prompt สั่งว่า_
_"amount < 1000 → Skip" ซึ่งเป็นการดูยอด**ต่อบรรทัด** แต่ ท.ป.4/2528 ข้อ 12_
_เป็นเกณฑ์**สะสมต่อคู่สัญญา** ⇒ จ่ายงวดละ 800 สามงวด = ต้องหักตั้งแต่งวดแรก_
_ที่ยอดรวมถึง 1,000 (ระบบเดิมตอบ Skip ทุกงวด = ลูกค้าไม่ได้หักภาษีเลย)_
_(2) **`StockDecisionPrompt`** ขอ `suggested_account_code` มาตลอดแต่ payload_
_**ไม่เคยมีผังบัญชีของ tenant อยู่เลย** ⇒ AI ต้องเดารหัสจากผังมาตรฐานไทยซึ่ง_
_อาจไม่มีในผังลูกค้ารายนั้น → ส่ง `candidate_accounts` (GlCandidateBuilder 120 รหัส)_
_+ HARD CONSTRAINTS ว่าต้องคัดลอกรหัสจากลิสต์เท่านั้น + **anti-hallucination guard_
_ฝั่งรับ** (`ScrubStockDecisionJson`) ที่ล้างรหัสบัญชี/GUID สินค้าที่ไม่มีจริงทิ้ง_
_แล้วลด action เป็น `MatchUncertain` พร้อมติด compliance flag บอกผู้ใช้_
_(เดิมคำตอบดิบถูกส่งไปหน้าเว็บเป็นปุ่ม "ใช้ค่านี้" ให้กด → ลงบัญชีผิด)_
_(3) **ผังสินค้าเรียงตามรหัส `.Take(200)`** = ตัดตามตัวอักษร ⇒ ร้านที่มีสินค้า_
_เกิน 200 รายการ ตัวที่ตรงกับใบนี้แทบไม่เคยติดลิสต์ AI จึงตอบ CreateNew ทุกบรรทัด_
_→ สร้างสินค้าซ้ำ. เปลี่ยนเป็นจัดอันดับด้วย token overlap กับคำอธิบายบรรทัด_
_(`LoadRelevantProductsAsync`, pool 3,000 แถว, เสมอกันเรียงตามรหัสเพื่อ deterministic)_
_+ บอก AI ตรง ๆ ว่า `product_catalog` เป็น subset — "ไม่เจอ ≠ ไม่มี"_
_(4) **`company_industry` ค่า default `"general"` ชนะเสมอ** เพราะผู้เรียกไม่เคยส่ง_
_→ ส่ง `Company.BusinessType / IndustryType` จริง (ตัดสิน Inventory vs Supply)_
_(5) **GL prompt ส่งแต่ผังฝั่งรายจ่าย** ทั้งที่ `IntegrationService.ProcessInvoiceAsync`_
_เรียกเส้นเดียวกันเพื่อสร้าง**ใบกำกับภาษีขาย** ⇒ ผังรายได้ 4xxxx ไม่เคยอยู่ใน_
_candidate เลย → `expenseAssetOnly` ผูกกับ `lineContext.OurRole` + rule 6 ของ_
_`GlAccountPrompt` อ่าน `our_role` ก่อนแทนที่จะยืนยันว่า "นี่คือใบสำคัญจ่าย" เสมอ_
_(6) **`localConfidence: 0.50m` บน "Acknowledge"** = ตัวเลขที่แต่งขึ้นให้ดูน่าเชื่อ_
_บนคำตอบที่แปลว่า "ไม่รู้" → ลดเป็น 0.20 ให้ตรงความจริง (ไม่กระทบ routing_
_เพราะยังต่ำกว่า short-circuit 0.85 เหมือนเดิม);_
_รอบ 95 — **ผลตรวจทีม 4 ด้าน: ไล่ OCR ทั้งสาย**:_
_ตั้งทีมผู้เชี่ยวชาญ 4 คน (อนุมานชนิดเอกสาร · วิเคราะห์ข้อมูลบนกระดาษ · UX/UI ·_
_สถาปัตยกรรม AI) ไล่ตรวจตั้งแต่อัปโหลดถึงเอกสารเสร็จ ข้อสรุปใหญ่: **ปัญหาส่วนใหญ่_
_ไม่ใช่ขาดความสามารถ แต่คือของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้** —_
_(1) **ตัวอนุมาน**: 50 ทวิ กลับทิศทั้งสองทาง (ตัวอ่านทิศที่ถูกต้องมีอยู่แล้วแต่ถูก_
_เรียกหลังสร้างเอกสาร) · เอกสาร 4 ชนิดตกหลุม Expense (ใบเสนอราคา/ใบวางบิล/ใบส่งของ/_
_สลิปโอนเงินซึ่งไม่มี marker เลย) · ใบกำกับซื้อไม่มีเครดิตเทอม → PV = บันทึกจ่ายที่_
_ไม่เคยเกิด · ตัวเรียนรู้ federated เขียนมาตลอดแต่**เงื่อนไขอ่านเป็นจริงไม่ได้เลย** ·_
_prior ของ vendor ข้ามฝั่งซื้อ/ขายได้ · สแกนใบขายตัวเองซ้ำ = ออกเลขซ้ำ ·_
_ตัวจำแนกฝั่งซื้อ/ขาย 3 ชุดตอบไม่ตรงกัน → ยุบเป็น `Helpers/DocumentSide.cs`_
_(2) **สกัดข้อมูล**: รหัสสาขาถูกอ่านเฉพาะ Tesseract — เส้นทาง Azure (เส้นหลัก)_
_ไม่เคยอ่านเลยแล้ว default 00000 เงียบ ๆ · กฎที่ 4 เป็น stub hard-code "ผ่าน" ·_
_FieldConfidence ใช้ชื่อ key 3 ชุดไม่ตรงกัน (ด่านลด confidence ไม่ทำงาน 3/4 เส้นทาง ·_
_ไฮไลต์เหลืองแทบไม่เคยขึ้น · ไม่ persist) → `Helpers/OcrFieldKeys.cs` + คอลัมน์ใหม่ ·_
_**ไม่เคยเทียบจำนวนเงินตัวอักษร** ทั้งที่เป็น check ที่แรงที่สุดบนใบไทย →_
_`Helpers/ThaiAmountInWords.cs` (round-trip 2,000 ค่า) · เล่มที่/เลขที่ · อย่างย่อ_
_เชิงโครงสร้าง (§82/5(2)) · ต้นฉบับ/สำเนา_
_(3) **UX**: ปุ่มที่ระบบติดป้าย "แนะนำ" ทิ้งการแก้ไขทั้งหมด + ไม่เรียนรู้เลย ·_
_strict-products บล็อกการเซฟใบ OCR ทุกใบ · แท็บ "สร้างแล้ว" ว่างถาวร ·_
_ลำดับปุ่มกลับด้านกับกฎเหล็ก #3 ข้อ 4 · mobile-expense ไม่เคยเรียก OCR + POST ไป_
_route ที่ไม่มีอยู่จริง (404 ทุกครั้ง) · ผู้ใช้แก้ "บทบาทเรา" ไม่ได้เลย_
_(4) **AI**: DocumentTypeClassification มี enum+prompt+student ครบแต่**ไม่มี call_
_site** · OcrFullReview ไม่มี student = ปิด provider แล้วตายเงียบ (ผิดกฎเหล็ก #1) ·_
_feedbackId ถูกทิ้ง = จ่าย token ฟรี → ต่อสายครบทั้งสามจุด);_
_รอบ 94 — **บาร์โค้ดสินค้าถูกอ่านเป็นเลขผู้เสียภาษี**:_
_ใบ PI-20260820-0005 ถูกต้องครบแต่ขึ้นเตือน 3 ข้อ รวม "อาจอัพโหลดผิดบริษัท" —_
_เลข `8885009199627` ที่ระบบอ้างว่าเป็นเลขผู้ซื้อไม่เคยมีบนกระดาษ เป็นบาร์โค้ด_
_ในตารางสินค้าที่ผ่าน mod-11 ไทยโดยบังเอิญ · แก้ 5 ชั้น: (1) resolver กลาง_
_`Helpers/ThaiTaxId.cs` (checksum + EAN-13/GS1 + first-digit) แทน checksum ที่_
_กระจาย 5 ที่ · (2) `SmartFieldExtractor` คัดบาร์โค้ด + ให้เลขที่มีป้าย_
_"เลขประจำตัวผู้เสียภาษี" ชนะ + regex ตัวคั่นไม่รวม `\n` (เดิมต่อเลขข้ามบรรทัด_
_เป็นเลข 13 หลักที่ไม่มีบนกระดาษ) + **เลิกปลอม `buyerPos = text.Length`**_
_(เดิม = เลขท้ายหน้ากลายเป็นเลขผู้ซื้อทุกใบ) · (3) Rule 1 ย่อข้อความด้วย_
_`ThaiTextNormalizer.SquashForKeywordMatch` ก่อนค้น "ใบกำกับภาษี" · (4) Rule 7_
_ต้องดู raw text ก่อนฟันธง — เพิ่ม `BUYER_TAX_ID_MISREAD` (Warning) สำหรับ_
_"อ่านผิดช่อง" และจำกัด `TENANT_BUYER_MISMATCH` (Error) ไว้เฉพาะใบฝั่งซื้อที่_
_เลขเราไม่อยู่บนกระดาษเลย · (5) `OcrService` เติมเลขผู้ซื้อ = บริษัทเราเมื่อ_
_พิสูจน์ได้ว่าเราไม่ใช่ผู้ขายและเลขเราอยู่บนกระดาษ (กฎเหล็ก #3 ห้ามปล่อยช่องว่าง)) ·_
_**กวาดทั้งเรพต่อ**: pattern เดียวกันถูกคัดลอกไปวางอีก 4 ไฟล์ และรูปแบบ `[-\s]?`_
_เดียวกันยังใช้กับเบอร์โทร/เลขบัญชี/เลขที่เอกสารในสเตทเมนต์ธนาคาร/ตัวกรอง PII_
_ก่อนส่งเข้า AI รวม 13 จุด 7 ไฟล์ → ยุบเข้า `ThaiTaxId.Pattern` + เปลี่ยนตัวคั่น_
_ทุกที่เป็น `[- \t]?` + migration ล้าง `OcrLearnedPatterns.ExtractionRegex` ที่ค้าง_
_ในฐาน + checker ตัวที่ 18 `tools/regex_line_span_check.py`) ·_
_**รอบสอง (การ์ดผลสแกน)**: ใบเสร็จ/ใบกำกับ หจก.สหกลชลบุรี เลขที่ 0339 พิมพ์เลข_
_ผู้ซื้อไว้เต็ม ๆ แต่การ์ดยังเตือน "ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" — สองต้นเหตุ:_
_(ก) แบบฟอร์มพิมพ์สำเร็จวาง**ป้ายไว้ใต้เส้นประ** ⇒ ค่าอยู่บรรทัดก่อนป้าย แต่ตัว_
_ตรวจป้ายมองย้อนหลังอย่างเดียว → มองสองทิศ (look-ahead 45) + ด่านคัดบาร์โค้ด_
_เลิกรับ "มีป้าย" เป็นข้อยกเว้น · (ข) `document-scan.html` มีสำเนามือของกฎ_
_`RdComplianceValidator` เขียนด้วย JS ที่ไม่เคยแก้ตาม → ย้ายมาคำนวณที่เซิร์ฟเวอร์_
_ตัวเดียว `Ocr/OcrScanComplianceEvaluator.cs` ส่งเป็น `OcrResultResponse_
_.ComplianceIssues` ให้หน้าเว็บแสดงอย่างเดียว (แถมปิด XSS จากค่าที่ OCR อ่านมา));_
_รอบ 93 — **ที่อยู่แบรนด์ผูกกับทะเบียนได้**:_
_`DocumentBrand.AddressSource` (Company/Branch/Custom) + resolver กลาง_
_`Helpers/BrandAddressSource.cs` · ย้ำด่าน "เอกสารภาษีใช้ที่อยู่สถานประกอบการเสมอ"_
_· พรีวิวเดินลำดับเดียวกับ renderer (ที่อยู่/โลโก้) — เดิมพรีวิวโชว์ที่อยู่หน้าร้าน_
_บนใบกำกับภาษีทั้งที่กระดาษจริงพิมพ์ที่อยู่จดทะเบียน);_
_รอบ 92 — **§6.2d สาขาผู้ออกเอกสาร**:_
_`Document.BranchId` + `IssuerBranchCode` (snapshot ตอนอนุมัติ) + resolver กลาง_
_`Helpers/DocumentIssuerBranch.cs` → รหัสสาขา/ที่อยู่บน renderer ทั้งสองตัว +_
_TXID/SellerTradeParty ของ e-Tax + สืบทอดเอกสารลูกครบ 5 ทาง + ตัวเลือกสาขาบนฟอร์ม_
_(โผล่เมื่อมี ≥ 2 สาขา · ค่าเริ่มต้น = ตามค่าบริษัทเสมอ) — **กิจการสาขาเดียวไม่กระทบ**);_
_รอบ 91 — **resolver กลางรหัสสาขา**:_
_`Helpers/TaxBranchCode.cs` แทนสูตรที่กระจาย 3 ที่ (PdfA3 ผู้ขาย/ผู้ซื้อพิมพ์เลขดิบ_
_"00003" · DocumentBrandController พิมพ์ "สาขาที่ 00003") → "สาขาที่ 3" ตาม_
_ประกาศอธิบดีฯ ฉบับที่ 199 ทุกจุด; ทะเบียนสาขาเฟส 0 ดู ACCOUNT_STRUCTURE.md §3.1a —_
_**ยังไม่แตะ flow เอกสาร** (Document.BranchId มาเฟส 1));_
_รอบ 90 — **§6.2c ผลตรวจ 3 ทีม**:_
_LegalLine ท้ายกระดาษใน QuestPDF · ที่อยู่จดทะเบียนบนเอกสารภาษี · บรรทัดรอง_
_แบรนด์ไม่ gate · IsActive ไม่ตัดตอน render · tenant guard · pinned template_
_ตรงชนิด · สืบทอดครบทุกเอกสารลูก · normalize marker);_
_รอบ 89 — **§6.2c ต่อของที่ค้าง**:_
_อัปโหลดโลโก้แบรนด์ + เลือกรูปแบบเอกสารรายใบ (Document.DocumentTemplateId) +_
_resolver กลาง ResolveDocumentTemplateAsync แทน if/else ที่ซ้ำ 2 ที่);_
_รอบ 88 — **§6.2c ปรับตามที่ผู้ใช้สั่ง**:_
_ไม่ตั้งแบรนด์ = เหมือนเดิมทุกอย่าง · ตั้งแล้วค่าเริ่มต้นยังเป็นชื่อบริษัท_
_(ตัด IsDefault ที่เลือกให้อัตโนมัติทิ้ง));_
_รอบ 87 — **§6.2c ชื่อทางการค้าบนหัว_
_เอกสาร**: DocumentBrand + Document.BrandId · ด่าน §86/4 + resolver กลาง_
_DocumentIssuerIdentity ที่ renderer ทั้งสองตัวใช้ร่วม · หน้าตั้งค่า document-brands);_
_รอบ 86 — **§6.2b convention ยอด_
_รายบรรทัดต้องเป็น net**: ซ่อมอัตโนมัติตอนอนุมัติ + backfill + ข้อความ error_
_ที่บอกทางแก้ (เคสจริง OCR ใบราคารวม VAT → Dr เกิน Cr เท่ายอด VAT));_
_รอบ 85 — **§6.2 ความยินยอมตอนสมัคร_
_(PDPA ม.19)**: ด่าน `RequireSignupConsent` + `RecordSignupConsentAsync` ทุกทางสมัคร ·_
_`PdpaConsentRecord` scope แพลตฟอร์ม + evidence hash canonical ตัวเดียว ·_
_หน้า terms.html/privacy.html จริง + `GET /api/legal/policy`);_
_รอบ 84 — **คุณภาพ OCR → สร้างเอกสาร**:_
_(1) `DocumentNumberSanitizer` กันเลข 2 ชุดถูกต่อกัน (บิล กฟภ.) โดยเทียบกับ_
_token บนกระดาษจริง; (2) reconcile บรรทัดแยกเคส "ราคาถือยอดรวม" (หารหาราคา_
_เก็บจำนวน) ออกจาก "จำนวนหลงคอลัมน์" (แก้จำนวน) — ค่าไฟ 59 ล้านหาย;_
_(3) `UnitInferrer` หน่วยตามชนิดรายการ + handoff เลิกทิ้ง field `unit`;_
_(4) `ProhibitedInputVatScreener` §82/5(4)(6) เลิก default เคลมให้ค่าน้ำมัน/_
_ค่ารับรอง + banner บอกรถประเภทที่เคลมได้/ไม่ได้; (5) `?approve=true` +_
_ปุ่ม "⚡ สร้าง + อนุมัติ" + ปิด modal/เด้งเปิดใบหลังสร้าง + แก้ deep-link_
_`?id=`→`?openDoc=`; เทสต์ `DocumentNumberSanitizerTests`,_
_`ProhibitedInputVatScreenerTests`, `OcrLineReconcileTests` (+3 เคส);_
_รอบ 83 — **การจัดหน้าเอกสารพิมพ์**:_
_(1) คอลัมน์ส่วนลดหายไปเองเมื่อทั้งใบไม่มีส่วนลด — resolver กลาง_
_`ShouldShowDiscountColumn` ที่ renderer ทั้งสองเรียก (ดู % ด้วย ไม่ใช่แค่ยอดบาท)_
_ตั้งค่า `HideEmptyDiscountColumn` ปิดได้; (2) ทุกกล่องข้อมูลย้ายทั้งก้อน_
_ไม่ผ่ากลาง (`break-inside: avoid` / `.ShowEntire()`); (3) บล็อกลายเซ็นเลิกใช้_
_flex → `<table><tr><td>` เพราะ Chromium ไม่เคารพ `break-inside` บน flex_
_(ลายเซ็นกับชื่อผู้เซ็นเคยแยกคนละหน้า); (4) หัวกระดาษ 3 ส่วนซ้ำทุกหน้าผ่าน_
_`.doc-frame thead` (HTML) + `page.Header()` (QuestPDF) ตั้งค่า_
_`RepeatHeaderEveryPage`; เทสต์ `DocumentPrintLayoutTests`;_
_รอบ 82 — **ตารางสะกดทางการระดับ_
_อำเภอ/เขต**: 50 เขต กทม. ยืนยันมือ + อำเภอเมือง 75 แห่งประกอบจากตารางจังหวัด_
_อัตโนมัติ (กันเคส เมืองจันทร์/เมืองปาน ที่ไม่ใช่ชื่อจังหวัด) — ครอบ 126/928 อำเภอ_
_+ แขวง กทม. 35/178 · อำเภอที่เหลือไม่ใส่มั่ว ใช้ AddressEn กรอกทับแทน;_
_รอบ 81 — **ที่อยู่คู่สัญญาบนเอกสาร_
_ภาษาอังกฤษแก้ทับได้แล้ว**: เพิ่ม `Contact.NameEn`/`AddressEn` คู่ขนานกับฝั่งบริษัท_
_(เดิมมีแต่ฝั่งเรา ที่อยู่ลูกค้าถูกถอดอักษรอัตโนมัติเสมอและแก้ไม่ได้เลย) +_
_รวมตรรกะที่เคยเขียนซ้ำ 4 จุดเป็น `ResolvePartyAddress`/`ResolvePartyName` ตัวเดียว_
_ที่ทั้ง HTML และ QuestPDF เรียก · เทสต์ `PartyEnglishTextTests`;_
_รอบ 80 — **ตรวจศัพท์อังกฤษบนเอกสาร_
_ทีละคำ**: แก้ 2 คำที่ผิดจริง ("bill discount" = ขายลดตั๋วเงินในภาษาการเงิน ·_
_"under-calculated" ไม่ใช่คำอังกฤษ) + 5 จุดที่ตกสาระ (เหตุผล CN/DN ย่อเกิน ·_
_Our document · Authorized · Expense) · เพิ่มตารางคำต้องห้ามใน DocumentLabelsTests_
_กันแปลตรงตัวกลับเข้ามา; รอบ 79 — **อนุมัติใบกำกับแปลงล้ม 500 +_
_แผงปรับปรุง JE ล้มเมื่อบัญชีซ้ำ**: ตัวขอเลข JE ทั้งสองตัวนับ change tracker ด้วย_
_(§6.0a-ter — AutoPost ค้าง SV ยังไม่ save แล้ว supersede ขอเลขตัวกลับ SV เดือน_
_เดียวกัน → เลขซ้ำ → unique ล้ม → อนุมัติใบแปลงไม่ได้ทั้งระบบ) · Adjust ใช้_
_GroupBy ก่อนทำ map บัญชี (JE ปกติมีบัญชีซ้ำหลายบรรทัดได้เสมอ) ·_
_เทสต์ `ConvertedTaxInvoiceApproveTests`; รอบ 78 — **ใบที่กู้คืนจากการยกเลิก_
_อนุมัติกลับเข้าบัญชีไม่ได้**: ด่าน §86/4 ใน UpdateDocumentAsync เช็ค "ส่งฟิลด์_
_มาไหม" แทน "แก้ค่าจริงไหม" — ฟอร์มส่ง Lines/ส่วนลดท้ายบิล/PricesIncludeVat_
_มาทุกครั้งอยู่แล้ว จึงบล็อกทุกการกดบันทึกแม้ไม่ได้แก้อะไร ⇒ "บันทึกและอนุมัติ"_
_ล้มที่ขั้น update ก่อนถึง approve. แก้เป็นเทียบค่าจริง + เพิ่ม PrintedLinesChanged_
_ที่ไม่นับผังบัญชี (ไม่ได้พิมพ์บนใบ) · เทสต์ `RestoredDocumentApproveTests`;_
_รอบ 77 — **จำลองเหตุการณ์ชุด 8:_
_ใบสำคัญปรับปรุงผังบัญชีตลอดวงจรชีวิต** (§6.0a-bis): ตัวกลับตกงวดของใบที่มัน_
_กลับ ไม่ใช่งวดเอกสารทั้งก้อน (ใบ ก.ค. ที่ปรับปรุงลง ส.ค. เคยผิดสองเดือน_
_พร้อมกันโดยยอดรวมทั้งปียังตรง) · กฎ scanner `DOC-ADJUST-LOST` เตือนเมื่อ_
_ยกเลิก→คืนชีพ→อนุมัติใหม่ ทำให้ผังที่แก้ไว้หายเงียบ ๆ ·_
_เทสต์ `SimulationRound8Tests`; รอบ 76 — **จำลองเหตุการณ์ชุด 7:_
_ใบขายที่ถูกหัก ณ ที่จ่ายแล้วปิดยอดด้วยมัดจำ**: ลูกหนี้ใน GL ตั้ง gross ตาม_
_เกณฑ์เงินสด แต่ BalanceDue สุทธิ ⇒ ปิดครบแล้วลูกหนี้ค้างเท่ายอด WHT ตลอดไป_
_และไม่ได้เครดิตภาษีตอนยื่น ภ.ง.ด.50 — เพิ่ม hook รับรู้ Dr 11910 / Cr ลูกหนี้_
_แบบ GL-first (ลงเฉพาะส่วนต่าง) ทั้งเส้นเอกสารและเส้น JV ·_
_เทสต์ `SimulationRound7Tests`; รอบ 75 — **จำลองเหตุการณ์ชุด 6:_
_ไฟล์ที่ยื่นจริงต้องเท่ากับรายงานบนจอ** (§6.0a): ใบ 50 ทวิ ที่ไม่มีบรรทัดย่อย_
_เคยหายทั้งใบจากไฟล์ ภ.ง.ด.3/53 ทั้งที่หัวสรุปยังนับภาษีของใบนั้น (นำส่งขาด) ·_
_สรุป ภ.ง.ด.50 แยก "ก่อนเครดิต / หักเครดิต / ต้องชำระเพิ่ม" (ป้ายเดิมเขียน_
_"หลังเครดิต" บนยอดก่อนเครดิต) · เทสต์ `SimulationRound6Tests`;_
_รอบ 74 — **จำลองเหตุการณ์ชุด 5:_
_มัดจำที่รับรู้บางส่วน · การจับคู่ธนาคารค้าง · ด่านงวดของสมุดรายวัน**:_
_family-net ของมัดจำแยก "JE ที่สร้างหนี้สินมัดจำ" ออกจาก JE รับรู้รายได้_
_(เดิมเอาส่วนที่เหลือไปตัดใบแจ้งหนี้แล้วลง Dr 41000 ล้างรายได้ที่รับรู้แล้ว) ·_
_ยกเลิกการชำระ/เปลี่ยนแหล่งเงินปลดการจับคู่ธนาคารที่ค้าง (§6.0c) ·_
_ด่านงวดยึดวันที่ ไม่ใช่แค่ FK + re-resolve เมื่อเปลี่ยนวัน + Post ปฏิเสธงวด_
_Locked (§6.0b) · เทสต์ `SimulationRound5Tests`; รอบ 73 — **จำลองเหตุการณ์ชุด 4:_
_เส้น integration + รายจ่ายที่เคลมภาษีซื้อไม่ได้**: ผังบัญชีที่ partner ส่งมาต้อง_
_อยู่ถูกฝั่งเอกสาร (ฝั่งจ่ายห้ามบัญชีรายได้ — JE แบบนี้สมดุลเป๊ะ guard จับไม่ได้_
_และกำไรสุทธิไม่ขยับ) · ย้ายผังบัญชีย้าย "ยอดที่ลงจริง" ผ่าน_
_`ResolveLinePostedGlAmountAsync` (VAT ต้องห้ามรวมเป็นต้นทุน / ใบอ้าง GRN /_
_PV settlement / ต่างสกุล) · CIL พับ VAT เคลมไม่ได้เข้าบรรทัดของตัวเองแทนกอง_
_รวม + §65 ตรี ครอบ CIL แล้ว · 50 ทวิ ยึดเกณฑ์เงินสด ท.ป.4/2528 (ตั้งหนี้ =_
_ฉบับร่าง, จ่ายจริง = ออกใบ; idempotency key = payment+document) ·_
_เทสต์ `SimulationRound4Tests`; รอบ 70-72 — **จำลองเหตุการณ์ชุด 1-3**:_
_guard เทียบยอดข้ามสกุล · ทุกทางปิดยอดยิง tax point §78/1 · 50 ทวิ ผูกงวดจ่าย ·_
_void ใบกำกับที่แทนที่ใบแจ้งหนี้แล้วคืนใบเดิมเป็นร่าง; รอบ 69 — **ปิดช่องว่าง OCR D1-D3**:_
_`BranchCodeExtractor` อ่านสาขาผู้ขาย/ผู้ซื้อแยกกัน (เดิมผู้ซื้อไม่เคยถูกอ่าน) ·_
_pseudo-target `Deposit` สแกนใบมัดจำได้ตรง (ทั้ง UI และ auto-create) ·_
_`duplicate-check` เตือนก่อนสร้างเมื่อใบเดิมถูกสแกนซ้ำ; รอบ 68 — **PaidOnIssue persist**:_
_เจตนา "รับเงินครบแล้ว" เก็บลง Document + Draft พิมพ์หัวรวมตามเจตนา / หลังอนุมัติ_
_ตามชำระจริง (mirror คู่ ComputeServedAsReceipt/ResolveServedAsReceiptAsync) ·_
_500 มีรหัสอ้างอิงผูก ErrorLogs; รอบ 67 — **JournalPostingGuard**:_
_ด่านตรวจโครงสร้าง JE ก่อนบันทึก (WHT ≤ 15% ของฐาน · ขาเจ้าหนี้/เงินครบยอด ·_
_VAT ไม่เกินเอกสาร) wire เข้า AutoPost + integration 3 จุด · scanner_
_`journal-anomalies` + การ์ด 🩺 หา JE เสียเก่าและเอกสารที่ไม่มี JE ·_
_เทสต์ `JournalPostingGuardTests` จากเคสจริง; รอบ 66 — **ตัวเลือกผังบัญชีบนมือถือ**:_
_เลิกใช้ `<datalist>` (Android Chrome ไม่เด้งรายการ) เปลี่ยนเป็น `<select>` +_
_`optgroup` ทั้งโมดัลแก้ JE และเปลี่ยนผัง · `_ensureAllAccounts()` โหลดครบ 5 หมวด_
_รวมส่วนของเจ้าของ · ถือ `accountId` ตรง ๆ แทนการ parse โค้ดจากข้อความ ·_
_แถวเป็นการ์ดต่อบรรทัดให้อ่านได้บนจอแคบ; รอบ 65 — **เลขที่/วันที่ใบลดหนี้จาก_
_ผู้ขาย**: เลิกยกเลขใบกำกับที่อ้างถึงมาเติมให้ CN/DN (ผู้ใช้สับสน + รายงานภาษีซื้อ_
_โชว์เลขผิด) และเพิ่ม `bookSupplierCreditNote` ให้ OCR ที่สแกนใบลดหนี้ของผู้ขาย_
_เติมเลขที่/วันที่จริงให้ โดยไม่ผูกกับเงื่อนไข VAT · เทสต์_
_`SupplierCreditNoteRefTests`; รอบ 64 — **แผง "📒 รายการบัญชี" บน_
_หน้าเอกสาร**: กางบรรทัด Dr/Cr จริงจาก GL + ปุ่ม "✏️ แก้ผังบัญชี" ต่อใบ →_
_`AdjustDocumentJournalEntryAsync` รับสถานะปลายทาง (แก้ผัง/เพิ่ม/ลดบรรทัด/เลือก_
_วันที่) แล้วลงใบปรับปรุงใหม่ตามผลต่าง · ค่าคงที่: ยอดรวมห้ามเปลี่ยน + บัญชีคุม_
_ห้ามขยับ · เทสต์ `AdjustDocumentJournalTests`; รอบ 63 — **แปลงเอกสารแล้วยอดขยับ_
_1 สตางค์**: `ConvertCoreAsync` ยก `VatAmountOverride` จากบรรทัดต้นทางเมื่อยกทั้ง_
_บรรทัด ⇒ ใบกำกับยอดตรงใบแจ้งหนี้เป๊ะ · ฟอร์มเอกสาร round-trip VAT ที่บันทึกไว้_
_ผ่าน `_vatBasisKey`/`_keptVat` (ไม่แตะฐาน = ไม่คิดใหม่) + ป้ายบอกส่วนต่าง ·_
_เทสต์ `ConvertTaxRoundingTests`; รอบ 62 — **แก้วันที่กลับบัญชีรายใบ**:_
_ทุกแถวในตารางมีช่องวันที่ของตัวเอง + ปุ่มตั้งทั้งชุด (วันที่ใบแรก / วันที่เอกสาร /_
_ตามใบต้นฉบับ / วันที่ที่เลือก) · API รับ `RedateVoidReversalRequest{Entries[]}` ·_
_กล่องอธิบาย "ทำไมใบเดียวมีหลายรายการ" (ลงบัญชีตามเหตุการณ์ ไม่ใช่ตามใบ);_
_รอบ 61 — **เปลี่ยนผังบัญชีรายบรรทัด_
_ของใบฝั่งขาย**: `ReclassifyLineAccountAsync` เปิดให้ Invoice/TaxInvoice/Receipt/_
_CN/DN · ทิศ JE อ่านขาที่ลงจริงใน GL แล้วกลับตามนั้น (รายได้ = Dr เก่า/Cr ใหม่) ·_
_เลิกบล็อกด้วยการมีรับ-จ่ายชำระ · ใบเสร็จหลักฐานไม่นับเป็นเอกสารปลายทาง ·_
_`ReclassifyProtectedCodes` เทียบตรงรหัสกันบัญชีคุม (ภาษี/ลูกหนี้-เจ้าหนี้/มัดจำ) ·_
_เทสต์ `ReclassifyLineAccountTests`; รอบ 60 — **แก้วันที่กลับบัญชีกับ_
_เอกสารที่มีตัวกลับหลายใบ**: ค่าเริ่มต้นต่อใบ = วันที่ใบต้นฉบับที่ตัวเองกลับ_
_(`ResolveRedateTarget`) ไม่ใช่ยัดรวมวันเดียว · endpoint preview_
_`GET /document/{id}/redate-void-reversal/preview` + โมดัลโชว์ตารางว่าใบไหนย้าย_
_จากวันไหนไปวันไหน/ย้ายไม่ได้เพราะอะไร ก่อนกดยืนยัน; รอบ 59 — **แก้ผังบัญชีของ JE ที่ระบบ_
_ลงจากเอกสาร**: หน้าสมุดรายวันโชว์เฉพาะปุ่มที่ server ยอมให้ทำ (ใบผูกเอกสาร =_
_"ดู" + "📄 เปิดเอกสารต้นทาง") · banner ชี้ทางแก้จริง (Expense/PI/PV → ปุ่ม_
_"✏️ เปลี่ยนผัง" ท้ายบรรทัด `ReclassifyLineAccountAsync`; ชนิดอื่น → ยกเลิกแล้ว_
_ออกใหม่) · `documents.html` อ่าน `?search=` (ลิงก์ตาย 4 หน้า) และ `?openDoc=`_
_(เปิดหน้ารายละเอียด ไม่ใช่ฟอร์มแก้ไข) · `CorrectJournalEntryAsync` guard_
_`SourceDocumentId` พร้อมข้อความชี้ทาง · แผงช่วย "❓ ปุ่มไหนใช้ตอนไหน" ·_
_เทสต์ `JournalEntryActionGuardTests` ตรึงตารางสิทธิ์; รอบ 40 — **วันที่ JE กลับรายการตอน_
_ยกเลิกเอกสาร**: `VoidDocumentAsync(companyId, documentId, reversalDate = null)` —_
_ค่าเริ่มต้นเปลี่ยนจาก `DateTime.UtcNow.Date` เป็น **วันที่ของเอกสารเอง** (ยกเลิก_
_ใบเดือนก่อนแล้วรายการกลับตกเดือนปัจจุบัน = ผิดสองเดือนพร้อมกัน: เดือนเก่าค้างยอด_
_ที่ไม่มีอยู่จริง เดือนใหม่มียอดติดลบไม่มีที่มา) · ส่งวันที่เดียวกันต่อไปทุกขา_
_(JE ของเอกสาร, JV หักมัดจำ, การกลับรับชำระเดี่ยว/หลายใบ, JV มัดจำ orphan) ·_
_`ResolveReversalDateAsync` ตกกลับเป็นวันนี้เมื่องวดปลายทางปิด **พร้อม log เหตุผล**_
_(ไม่บล็อกการยกเลิก ไม่เงียบ) · Supersede (INV→TIV) ใช้วันที่ของใบกำกับที่มาแทน ·_
_UI ถามวันที่ก่อนยืนยัน (default = วันที่เอกสาร) · **เครื่องมือแก้ย้อนหลัง**_
_`RedateVoidReversalAsync` + ปุ่ม "📅 แก้วันที่กลับบัญชี" บนใบที่ยกเลิกแล้ว —_
_ย้ายเฉพาะ JE ตัวกลับ (OriginalEntryId != null) ไม่แตะต้นฉบับ ⇒ ยอดสุทธิเท่าเดิม_
_เปลี่ยนแค่งวด; งวดต้นทาง+ปลายทางต้องเปิดทั้งคู่; รอบ 39 — Task Force P5-P7 (N+1):_
_ตรวจรหัสบัญชีของบรรทัดเอกสาร (`ValidateLines`) เดิมยิง AnyAsync **ต่อบรรทัด** —_
_ใบ 50 บรรทัด = 50 query ทุกครั้งที่สร้าง/แก้ · import เงินเดือนเช็คผัง 2 query/_
_พนักงาน (200 คน = 400 query) · โพสต์เงินเดือน resolve แหล่งจ่าย 1 query/โค้ด —_
_ทั้งหมดเปลี่ยนเป็นดึงชุดเดียวก่อนลูปแล้วเทียบใน RAM (query คงที่ 0-1 ครั้ง); รอบ 38 — Task Force P4/Q3: (P4)_
_`GetGeneralLedgerAsync` รวมยอดยกมาใน SQL (GroupBy+Sum) แทนดึง JournalEntryLine_
_ทั้งหมดตั้งแต่เปิดบริษัทเข้า RAM เมื่อไม่ได้เลือกบัญชีเจาะจง · (Q3) AuditMiddleware:_
_ข้าม path ที่เขียนถี่แต่ไม่มีคุณค่าเชิงตรวจสอบ (chat/telemetry/autosave/preview),_
_`Guid.TryParse` แทน `Parse` (subject ที่ไม่ใช่ GUID เคย throw **หลังงานสำเร็จ**_
_⇒ ผู้ใช้เห็น 500 แล้วกดซ้ำ = เอกสารซ้ำ), ตัด UserAgent ที่ 512 ตัว, และ audit ที่_
_บันทึกไม่สำเร็จ log เป็น Error แทนทำให้ request 500; รอบ 37 — เครื่องมือนักบัญชีใหม่:_
_**กระทบยอด GL ↔ รายงานภาษี** (`TaxGlReconciliationService` +_
_`GET /accountant/tax-gl-recon` + การ์ดในหน้า accountant.html): เทียบยอดเคลื่อนไหว_
_ใน GL ของงวด (21911 ภาษีขาย / 11610 ภาษีซื้อ / 21916-21917-21918 WHT ค้างจ่าย /_
_21912 ภ.พ.36) กับยอดในแบบที่จะยื่น — **พร้อมไล่หาสาเหตุจริงเมื่อไม่ตรง**:_
_บรรทัดที่ติ๊กออกจากรายงาน · ภาษีซื้อพัก 11640 (§86/4 ไม่ครบ) · เอกสารที่บันทึก_
_หลังสร้างรายงาน (snapshot ไม่อัปเดตเอง) · เอกสารที่ถูกยกเลิกแต่ยังอยู่ในแบบ ·_
_50 ทวิ ที่ยังเป็นร่าง/ยังไม่ออก · cert ที่ออกคนละเดือนกับเอกสาร (จ่ายข้ามเดือน) —_
_แต่ละสาเหตุมียอดที่อธิบายได้ + รายการเอกสาร + สิ่งที่ต้องทำ; รอบ 36 — S8: `/uploads/**` เปลี่ยนเป็น_
_**allow-list** และย้าย guard ไปอยู่**ก่อน static handler ทุกตัว** — ของเดิมบล็อกแค่_
_`/uploads/attachments` ซึ่งไม่ตรงที่ไฟล์เก็บจริง (เอกสารแนบอยู่_
_`/uploads/{companyId}/{entityType}/…`, สแกน OCR อยู่ `wwwroot/uploads/ocr/…` ที่_
_static handler ตัวแรกเสิร์ฟก่อน guard เดิมเสียอีก, e-Tax XML/PDF อยู่ uploads/etax)_
_⇒ ไฟล์การเงิน/PII โหลดได้ทาง URL ตรงโดยไม่ต้อง login. เปิดเฉพาะ logos/banners/_
_products/stamps/cms/signatures/order-slips/portal-slips; รอบ 35 — Task Force P3 บางส่วน: (Q5)_
_`CmsCommerceService.ConfirmPaymentAsync` เลิกกลืน error ใน money/stock path —_
_สะสมความล้มเหลว (อนุมัติเอกสาร/บันทึกชำระ/ตัดสต๊อก), log เป็น Error, และปักหมุด_
_ลง `Order.InternalNotes` ให้แอดมินเห็นว่า "เงินเข้าแล้วแต่บัญชี/สต๊อกยังไม่ครบ"_
_(throw ไม่ได้เพราะเป็น webhook — gateway จะ retry วนไม่จบ) · (S9)_
_`UseForwardedHeaders` เป็น middleware ตัวแรกสุด: rate limit เคยนับทุกคนเป็น IP_
_เดียว (IP ของ proxy) และ audit/PiiAccessLog/ลายเซ็นอนุมัติบันทึก IP ผิดคน —_
_ปิดได้ด้วย `Security:TrustProxyHeaders=false`; รอบ 34 — backlog ภาษีครบทั้ง 14 ข้อ:_
_(B6) ภ.ง.ด.51 ใช้ฐานเดียวกับ 50 — `ComputeSection65TerAddBackAsync` เป็น helper_
_ร่วม (เดิม 51 คำนวณจาก JE ล้วนไม่มีบวกกลับ ⇒ ประมาณการต่ำ เสี่ยงเงินเพิ่ม 20%_
_§67 ตรี) · (B7) **ผลขาดทุนสุทธิยกมา 5 รอบบัญชี §65 ตรี(12)** หักจากฐานกำไรก่อน_
_คิด CIT (FIFO + หมดอายุปี Y+5) — เดิมโค้ด "เครดิตภาษีปีก่อน" เป็น dead code_
_(เช็ค NetVat<0 = ปีขาดทุน ซึ่ง CitAmount = 0 เสมอ) ผลขาดทุนจึงไม่เคยถูกหักเลย ·_
_(B8) ค่าเสื่อม CIT ตามรอบบัญชี (Year+Month vs ช่วง fiscal) — เดิมกรองปีปฏิทิน ⇒_
_บริษัทรอบไม่ตรงปีได้ค่าเสื่อมผิดรอบ · (B9) ภ.ง.ด.50/91 unique ต่อ **ปี** (เดิม_
_ปี+เดือน ⇒ สร้างได้ 12 ใบ/ปี แล้ว e-Filing หยิบใบไหนก็ได้) · (B10) ไฟล์ ภ.ง.ด.91_
_ใช้ประชากร payroll Approved/Paid ตรงกับจอ (เดิม "ไม่ใช่ Draft/Voided"); รอบ 33 — backlog ภาษี B1-B4/B11-B14:_
_(B1) หนังสือรับรองที่คีย์มือผูก `DocumentId` ได้ (DTO+guard tenant+กันออกซ้ำ) ⇒ เลิก_
_นับซ้ำกับแถวเตือน · (B2) ออกใบรายงวดทับใบเต็มจำนวนไม่ได้แล้ว (idempotency สองสาขา_
_เคยเช็คคนละ key จึงลอดกัน) · (B3) **เดือนนำส่ง ภ.ง.ด. = เดือนที่จ่ายเสมอ** ไม่ขึ้นกับ_
_WhtRecognitionBasis (เกณฑ์บัญชีคนละเรื่องกับเดือนยื่น — Accrual เคยทำให้เงินก้อนเดียว_
_โผล่ 2 เดือน) · (B4) regenerate: snapshot ติ๊กของทุกชนิดรายงาน (เดิมเฉพาะ VAT) +_
_คืนฟิลด์ audit/RD/การยื่น (EFilingExportedAt, RdAck*, Rejection*, ReversalJournalEntryId)_
_— เส้น ปลดล็อก→แก้→สร้างใหม่ เคยลบร่องรอยการยื่นทิ้ง · (B11) RecalcVatTotals เป็น_
_allow-list สองฝั่ง (เดิม default-to-output: code ใหม่/สะกดผิดไหลเข้าภาษีขายเงียบ) ·_
_(B12) ฐาน §82/3 ใช้ `ClaimBasisDate` (วันที่ใบกำกับผู้ขาย) ทั้ง generate และ pull —_
_เดิมคนละฐาน ใบเดียวกันตอบต่างกันแล้วแต่ทางเข้า · (B13) ใบต้นทางที่ soft-delete แล้ว_
_resolve ด้วย IgnoreQueryFilters (output VAT ของใบเสร็จเคยหายเงียบ) · (B14) UI ล็อก_
_ติ๊กบรรทัด 🚫/⚠️/[รอใบกำกับ ที่ server ปฏิเสธอยู่แล้ว + error ระบุลำดับบรรทัด_
_(auto-save ส่งทั้งหน้า ติ๊กผิด 1 บรรทัดเคย rollback ทั้งชุดโดยไม่บอกว่าบรรทัดไหน); รอบ 32 — TODO_OPUS A3/A6: ใบลดหนี้/_
_ใบเพิ่มหนี้ **ฝั่งซื้อ** มีช่อง "เลขที่/วันที่ใบจากผู้ขาย" (reuse SupplierInvoice_
_Number — enrichment รายงานภาษีซื้อหยิบให้อยู่แล้วเพราะบรรทัด CN ฝั่งซื้อ =_
_IncomeTypeCode "INPUT") + soft warning §86/10 ตอนอนุมัติเมื่อมี VAT แต่เลขว่าง;_
_`_syncSupplierInvoiceFields` แยกจาก onDocTypeChange เพื่อให้สลับฝั่ง CN เรียกได้_
_โดยไม่วนซ้ำ · ไฟล์แนบในหน้ารายละเอียดพับไว้เป็นค่าเริ่มต้น + โหลดรูปย่อเฉพาะตอน_
_กางครั้งแรก (เดิม auto-โหลดทุกไฟล์ ≤8MB ทุกครั้งที่เปิดใบ); รอบ 31 — TODO_OPUS A4/A5 "ระบบพูดภาษา_
_หัวกระดาษ": `DocumentResponse.ServedAsReceipt` (read-only, batch query 1 ครั้ง/หน้า_
_ไม่ใช่ N+1; mirror `ResolveServedAsReceiptAsync` ผ่าน `ComputeServedAsReceipt`) →_
_`Layout.docHeaderLabel/docHeaderBadge` ตั้งป้ายตามหัวจริงบน list + หัว detail modal_
_(ใบ combined เห็นได้ทันทีโดยไม่ต้องเปิดแก้/พิมพ์) · A5 "สองประตู ห้องเดียว":_
_เอกสารตั้งหนี้แล้ว (INV/TIV/DN) เลือก "ใบเสร็จ/ใบสำคัญรับ" ในหน้าแปลง → เปิด modal_
_บันทึกชำระเงิน prefilled แทนการเรียก Convert API (ผลบัญชี+เอกสารเหมือนกัน 100% และ_
_ทำได้มากกว่า: จ่ายบางส่วน/WHT/มัดจำ/FX); QT/BN ยัง convert จริง (ขายสด ไม่มี AR) ·_
_ข้อความติ๊กใบเสร็จเขียนใหม่ให้ตอบ "จะมีเอกสารใหม่ไหม" + ใช้หัวจริงของใบนั้น_
_(ใบ combined จ่ายครบ = 3-in-1 — เดิมเขียนตายตัวผิด); รอบ 30 — TODO_OPUS A1/A2/B5: ไฟล์ยื่น_
_ภ.ง.ด.3/53/54 (.txt) เปลี่ยนเป็น **detail rows ล้วน ไม่มี H|/T|** ตามหน้า import_
_ของสรรพากร (Helpers/PndTextFileFormat.cs = format กลางตัวเดียว ใช้ทั้งเมนูส่งออก_
_และปุ่ม e-Filing ในหน้ารายงาน — กันสองปุ่มได้ไฟล์คนละหน้าตา): 11 คอลัมน์ Col1 ลำดับ /_
_Col2 เลขภาษี 13 / Col3 สาขา 5 / Col4 ชื่อ / Col5 ชื่อสกุล (นิติ=ว่าง) / Col6_
_ddMMyyyy ค.ศ. / Col7 ประเภทเงินได้ / Col8 เงินได้ / Col9 อัตรา / Col10 ภาษี /_
_Col11 เงื่อนไขการหัก · ภ.ง.ด.54 ย้ายเป็น cert-primary (เลิก mine เอกสาร — เดิม_
_ต่างจากจอ 13 จุด + ซ้ำกับ 3/53) · แถว "⚠️ ยังไม่ออกหนังสือรับรอง" เกิดมาแบบ_
_IsExcluded (ยอดจอ = ยอดไฟล์ยื่น = ทะเบียน 50 ทวิ เสมอ; ติ๊กกลับเองได้); รอบ 29 — audit 3 ทีมรายงานภาษีทุกชนิด,_
_แก้ 20 จุด: **ภ.พ.30** ApplyVatDeferrals ใช้ RecalcVatTotals จริง (เดิม scalar ทิ้ง_
_เครดิตยกมา = ยอดยื่นเกิน + เปลี่ยนเองหลังติ๊ก) · JE-fallback matcher แคบ (ตัด 21912/_
_21913/21914/21915/21918, prefix 114/115 เงินกู้-สต๊อก, 11620/11630, ผัง 5xxxx ชื่อ_
_ภาษีซื้อ) · ทั้งสอง fallback ข้าม JE ที่ถูกกลับรายการ · ExportPp30 ใช้รายงาน persisted_
_ที่ผู้ใช้ติ๊กแล้ว (เดิม recompute ทิ้งติ๊กทั้งหมด) · endStampExclusive กัน timestamp_
_วันสุดท้ายหลุดทุกงวด (5 จุด) · PV settlement/CIL ห้ามเคลม-ห้ามดึง + pull เคารพ_
_§82/5 · PDF ภ.พ.30 JE_INPUT จัดฝั่งถูก · **ภงด.** fallback ฐาน=ΣDr (เดิมลบ WHT_
_ซ้ำ อัตราเพี้ยนทุกแถว) + ExtractTaxpayer + 50 ทวิ payee ตปท. → ภงด.54 (เดิมได้แค่_
_3/53 = นำส่งซ้ำสองแบบ) + cert query เลิก Include INNER-JOIN (payee ถูกลบแล้วจอขาด)_
_· **CIT** กด "บันทึก" ไม่ทับหัวรายงานแล้ว + detail/Excel โชว์ รายได้/กำไร/CIT จริง ·_
_**ภ.พ.36** ฐาน header-only ใช้ SubTotal (เดิม dead-fallback = 0) + update ยอด Output/_
_Net ตามติ๊ก + BuildPp36 กรอง IsExcluded + recompute header · **ภงด.1/สปส.** block_
_การสร้างรายงานว่าง (ชี้ไปหน้า e-Filing payroll) · **ภงด.91** โชว์ยอดถูก (typeMap/_
_list/detail) · AutoRefresh ปลด VatDeferral ก่อนลบ · tax.html หัวคอลัมน์ per-tab +_
_kpi ids (หัวรายงานอัปเดตหลังติ๊ก) + คำเตือน 🚫/⚠️ ไม่ถูกเลขใบกำกับทับ._
_**Backlog (ยังไม่แก้ — บันทึกไว้):** manual cert ไม่มี DocumentId นับซ้ำกับแถวเตือน;_
_cert เต็มใบ+รายงวด ซ้ำ (idempotency ข้ามสาขา); Accrual PI/cert ข้ามเดือนซ้ำ;_
_Regenerate ภงด. ไม่ snapshot ติ๊ก + ล้าง audit fields + ไม่ atomic; ExportPnd54_
_ยัง doc-mined (13 ความต่าง); ภงด.51 คนละฐานกับ 50; ขาดทุนยกมา 5 ปีไม่หักใน CIT;_
_ค่าเสื่อม CIT ปีปฏิทิน vs รอบบัญชี; CIT unique รายเดือน (สร้างได้ 12 ใบ/ปี); ภงด.91_
_สร้างจาก UI ไม่ได้ + ประชากร payroll จอ≠ไฟล์; RecalcVatTotals default-to-output;_
_§82/3 ฐานวันที่ generate≠pull; Receipt ที่ต้นทาง soft-deleted VAT หาย; auto-save_
_ภงด. all-or-nothing; รอบ 28d: fallback "สแกน JE ไร้เอกสาร"_
_ของรายงานภาษี เลิกจับด้วย prefix หลวม — ภงด.3/53: เดิม prefix "2191" กวาดบัญชี VAT_
_21911/21912/21913 (JV auto-reconcile มัดจำโผล่เป็นแถว "REC..." VAT 7% ใน ภงด.53)_
_+ "11910 ถูกหัก" คือเครดิตเราไม่ใช่ยอดนำส่ง + ไม่แยกแบบ (แถวเดียวเข้าทั้ง 3 และ 53)_
_→ จับเฉพาะบัญชีของแบบ: 21916=ภงด.3 / 21917=ภงด.53 (+ชื่อที่ระบุแบบ, ไม่ใช่ "ถูกหัก");_
_ภ.พ.30 (กระจกเงา): IsOutputVat ตัด 21916/21917 ออก (JV WHT manual เคยโผล่เป็นภาษีขาย)_
_+ IsInputVat ตัด 11640 undue (เคลมก่อนถึงกำหนดไม่ได้); รอบ 28c: ไล่ตรวจ pipeline รายงานภาษี_
_ทั้งเส้น (generator → MapToResponse → list → detail → e-Filing → Excel) — จุดที่แก้:_
_(1) สูตรยอดหัวตอน generate ภงด. กรอง SUMMARY+IsExcluded ให้ตรง RecalcPndTotals_
_(เดิมคนละสูตร); (2) cert "ร่าง" ของงวดขึ้นเป็นบรรทัด IsExcluded "⚠️ ยังเป็นร่าง —_
_ออกใบก่อนยื่น" (เห็นแต่ไม่นับ) + บรรทัด [สรุป] ต่อผู้ขายไม่รวมแถว excluded;_
_(3) e-Filing ภงด.3/53 export เฉพาะ cert Issued/Printed (เดิม != Voided ⇒ ใบร่าง_
_หลุดเข้าไฟล์ยื่น + ไม่ตรงจอ); (4) e-Filing ภ.พ.36 เปลี่ยนเป็น single source_
_ComputePp36ReportAsync (pattern เดียวกับ ภ.พ.30) — เดิม mine เอกสารเองคนละเงื่อนไข;_
_ที่ตรวจแล้วถูก: MapToResponse ส่ง totalIncome/totalTaxWithheld/citAmount/lines ครบ,_
_ภ.พ.30 export single-source อยู่แล้ว, Excel อ่าน report lines, ตาราง detail มีติ๊ก_
_ใช้/ไม่ใช้; ภงด.54 export ยัง doc-mined (ติด ⚠️ ไว้ — เคสน้อย); รอบ 28b: ภงด.3/53/54 ใช้ทะเบียน_
_หนังสือรับรอง 50 ทวิ (Issued/Printed งวดนั้น) เป็นแหล่งหลักของรายงาน — เดิม mine_
_จากบรรทัดเอกสารเท่านั้น: ใบที่ WHT อยู่ระดับเอกสาร/งวดจ่าย (บรรทัดไม่มียอดราย_
_บรรทัด) ผ่าน filter นอกแต่ inner loop ว่าง ⇒ รายงาน 0 ทั้งที่ cert ออกครบ; เอกสาร_
_ที่ cert ครอบแล้วไม่ mine ซ้ำ + เอกสารยังไม่ออก cert ขึ้นเป็นแถว "⚠️ ยังไม่ออก_
_หนังสือรับรอง" (รวมเคส WHT ระดับเอกสาร — เดิมหายทั้งใบ) · หน้า tax.html: แถว ภงด._
_โชว์ totalIncome/totalTaxWithheld (เดิม bind outputVat/netVat ของ VAT ⇒ 0.00_
_เสมอ) + แท็บ/ตัวเลือกสร้าง "ภ.พ.36" (เดิมไม่มีที่ดูเลย) + detail modal แยก layout_
_ตามชนิดแบบ; รอบ 28: ภาษีซื้อไม่เคลม "ตามก้อนเงิน" —_
_UnclaimInputVatAsync + expiry job §82/3 เลิก fallback "บัญชี 53xx ตัวแรก" (เคยได้_
_53120 ค่าโฆษณา ทั้งที่ก้อนเงินคือ 54123 สวัสดิการ) → ResolveNonClaimableVatExpense_
_AccountAsync: บัญชีของบรรทัดที่ VAT เกาะ (VAT มากสุดชนะ; Expense/Asset — capitalize_
_เข้าต้นทุนได้) > ผังชื่อ "ภาษีซื้อ/ขอคืนไม่ได้" > (job) ผังค่าใช้จ่ายใดก็ได้ ·_
_ภ.พ.36 มี generator เฉพาะ (GeneratePp36Report): เอกสาร IsForeignService ฝั่งซื้อ_
_ตามงวด tax point, ยอดนำส่ง = VAT ประเมินเอง, dedup เฉพาะกับ ภ.พ.36 งวดอื่น —_
_เดิม reuse ตัว ภ.พ.30 แล้วโดน cross-report dedup จนว่าง/0 ตลอด · ภงด.3/53 กันนับ_
_ซ้ำสายตั้งหนี้→PV ตาม WhtRecognitionBasis (Cash: PV คือแถวจริง ใบตั้งหนี้ที่มี PV_
_active ข้าม; Accrual: กลับกัน) — เดิมเข้าทั้งสองใบ = นำส่ง 2 เท่า; รอบ 27: พรีวิว GL "ประมาณการ — ก่อน_
_อนุมัติ" (PdfGenerationService.BuildProjectedGlAsync) เลิก drift จาก JE จริง —_
_(1) WHT ฝั่งซื้อเคย hardcode 21510 ("เงินมัดจำรับล่วงหน้าค่าห้องพัก" ในผังมาตรฐาน!)_
_→ mirror ResolveWhtPayableAccountAsync: 21917 (นิติ ภ.ง.ด.53) / 21916 (บุคคล ภ.ง.ด.3);_
_(2) PV ที่ผูกใบตั้งหนี้ (แปลง/ดึงใบค้าง) เคยพรีวิวแบบ standalone (Dr ค่าใช้จ่ายซ้ำ)_
_→ mirror settlement branch: Dr เจ้าหนี้ gross (ตามชนิดใบต้นทาง + pinned AP) / Cr_
_เงิน / Cr WHT (Cash basis) — ไม่มีขาค่าใช้จ่าย/VAT; (3) Expense credit contra →_
_21220 เจ้าหนี้อื่น (เดิมโชว์ 21210 ทุกชนิด); (4) fallback เงินสด 11111 (11110 ไม่มี_
_ในผัง). ป้องกัน 3 ชั้น: mirror comment สองฝั่ง + tools/gl_code_check.py (จับ code/_
_ชื่อไม่ตรงผังมาตรฐาน — negative test จับ 21510 ที่บรรทัดจริง) +_
_GlAccountTemplateSanityTests (pin ความหมาย code ที่ JE engine พึ่ง); รอบ 26d: ไล่ปิด "ทางเข้าที่หลุด policy_
_หัวกระดาษ" ครบทุกทาง — (1) Clone (`DocumentCloneController`): สืบทอด Combined/_
_Declined/IssuedAsCashReceipt + CustomAppendix/Footer/Terms (เดิมสืบทอดแค่ภาษา ⇒_
_clone ใบหัวรวมได้ใบหัวเดี่ยวเงียบ ๆ) — ส่ง flag เฉพาะเมื่อไม่ override ชนิด; (2)_
_Recurring: template UI มีตัวเลือก "ใบแจ้งหนี้/ใบกำกับภาษี (ใบเดียว หัวรวม)"_
_(pseudo → TaxInvoice + templateData.combinedInvoiceTaxInvoice) + backend อ่าน_
_combined/declined จาก template (ไม่รองรับ cash โดยเจตนา — auto-gen รายเดือน_
_โดยไม่มีเงินเข้าจริง = เงินสดปลอม) + hydrate/detail แสดงหัวรวมถูก; (3) convert_
_เปิดฟอร์มร่างหลังแปลงครอบ QT→INV/TIV ด้วย (เดิมเฉพาะ INV/BN→TIV) ให้เลือก_
_หัวกระดาษต่อทันทีทุกเส้น; รอบ 26c: convert modal พูดภาษา "หัวกระดาษ"_
_— เพิ่ม pseudo-target `TaxInvoicePaid` "ใบกำกับภาษี/ใบเสร็จรับเงิน — รับเงินครบแล้ว"_
_ใน dropdown แปลงเอกสาร (เฉพาะแหล่ง INV/BN ที่แปลงเป็น TIV ได้; backend ไม่รู้จัก —_
_frontend แปลงเป็น TaxInvoice ทั้งฉบับ + เปิดฟอร์มพร้อม preset โหมด tax_paid ให้เลย);_
_pseudo ไม่มี axis โดยตั้งใจ = ห้ามเลือกบางบรรทัด (supersede ต้องยอดเท่าต้นทาง);_
_hint ของ target "ใบกำกับภาษี" ชี้ทางไปตัวเลือกหัวรวม; รอบ 26b: dropdown เป็นตัวควบคุมเดี่ยว —_
_ซ่อนกล่องติ๊กซ้ำ "💰 ลูกค้าจ่ายเงินแล้ว" + "🧾 ขายเงินสด" เมื่อ dropdown แสดง (โหมด_
_ตั้ง flag ให้เอง; บริษัทไม่จด VAT ที่ไม่มี dropdown ยังใช้กล่องเขียวเดิม; reset ปลดติ๊ก_
_เฉพาะ "ชนิดเอกสารใช้ไม่ได้จริง" ไม่อิง display — กันล้าง flag ที่โหมดเพิ่งตั้ง) ·_
_`receipt_only` เป็นโหมดรับเงินในตัว (paid:true — ใบเสร็จ = หลักฐานรับเงิน ม.105;_
_เดิมเลือกหัวใบเสร็จได้โดยไม่บันทึกรับเงิน = กระดาษ/บัญชีขัดกัน) + hydrate ใบ declined_
_เดิม sync จะ arm paid ให้ตรง hint (กัน silent no-op) · hint โชว์บรรทัด "💰 เงินเข้า:_
_<ช่องทาง>" สำหรับโหมด paid/cash และ refresh เมื่อเปลี่ยนแหล่งเงิน; รอบ 26: โหมด `tax_paid` "ใบกำกับภาษี/_
_ใบเสร็จรับเงิน — รับเงินครบแล้ว" สำหรับใบที่แปลงจากขายเครดิต (INV/BN → TIV เท่านั้น;_
_standalone ใช้ tax_receipt/3-in-1 เดิม): ตั้ง fPaidOnIssue → chain อนุมัติ (Supersede_
_กลับ JE ใบต้นทาง) + บันทึกชำระเต็มยอด (backend หัก WHT งวดปิดยอดอัตโนมัติ =_
_remainingCap; ไม่ออกใบเสร็จแยก → ServedAsReceipt จัดหัวรวม) · ปลดล็อก paid-chain_
_ให้ทำงานตอน "แก้ไขร่าง/ใบถูกปฏิเสธ" ด้วย (เดิม !editingId เท่านั้น ⇒ ใบแปลงติ๊กจ่าย_
_แล้วกดอนุมัติ = silent no-op ไม่บันทึกชำระ; revision ใบอนุมัติแล้ว/สถานะไม่รู้ ไม่ยิง_
_fail-safe) · สลับออกจากโหมด paid → ปลดติ๊กที่โหมดตั้งให้ (ติ๊กมือผู้ใช้ไม่แตะ) ·_
_doConvert INV/BN→TIV เปิดฟอร์มร่างทันที + hint ใน convert modal บอกทางเลือก 2 แบบ ·_
_ยืนยันพฤติกรรมลบร่างใบแปลง: DeleteDocument (Draft, no JE/payment/e-Tax) = hard delete_
_→ ใบต้นทางไม่ถูกแตะ (supersede เกิดตอน approve เท่านั้น) + guard กันแปลงซ้ำ/consumption_
_มองไม่เห็นแถวที่ลบ → แปลงใหม่ได้ทันที; รอบ 25: dropdown "เอกสารที่จะออกให้ลูกค้า"_
_6 ตัวเลือกแทน 4 checkbox (รวม 3-in-1 ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน — ตั้ง_
_combined+paid ให้ chain อนุมัติ+ชำระรันเอง) · sync `_validConversions` frontend ให้ตรง_
_backend ValidConversions (เดิม drift หลายรุ่น: PO→Expense เลิกแล้ว, ขาด GRN/PV/Receipt/_
_CertInLieu ทั้งชุด ⇒ ปุ่มแปลงหาย/ตัวเลือกผี) + comment ชี้ mirror สองทิศ · openConvert_
_ใช้ API getConversionTargets เป็นหลัก map เป็น offline fallback · รอบ 24: UX audit ปุ่ม/ป้าย/ฟอร์มทุกชนิด×สถานะ —_
_ปุ่มรับเงิน/ตัดหนี้สูญ เพิ่ม type gate (เดิมโผล่บน QT/DN/PO/PR/CN) · แปลงเอกสารกรองชนิด_
_ที่มีปลายทางจริง + เปิดตอน PartiallyPaid/Paid/Overdue · Rejected แก้ไขได้ (backend ปลด_
_พร้อมกัน — ใบตีกลับยังไม่ posted) · e-Tax/อีเมล รวม Overdue + ส่ง PO ให้ vendor ได้ ·_
_AgingDays นับจาก DueDate ไม่ใช่ DocumentDate (ป้าย "ค้างชำระ" เดิมโกหกใบเครดิตยาว) ·_
_banner ค้างชำระมีปุ่มส่งทวง · confirm รับเงิน INV มี VAT เป็น 2 ขั้น (dismiss = ยกเลิกจริง_
_ไม่ใช่เลือกไม่ออกใบกำกับ) · detail: escape description (XSS), แถวส่วนลดท้ายบิล/หักมัดจำ,_
_ซ่อน WHT -0.00, ส่วนลดบาทไม่โชว์ 0% · badge เคลม ภ.พ.30 ไม่ขึ้นคู่ "รอใบกำกับ" ·_
_stale เงียบเมื่อ aging โชว์ · decline-TIV เช็คจด VAT · PR/PO ปิดคอลัมน์เคลม VAT ·_
_dialog void ไม่พูดถึง Reversal JE เมื่อใบยังไม่ posted) ·_
_รอบ 23: ลงมือตาม roadmap task force —_
_P0: audit hash canonical เดียว + เทสต์ round-trip · WHT GL นับ reversal ถูก · lockout key ·_
_prod config (ลบ placeholder JWT, PG VerifyFull). P1: CMS XSS allowlist sanitizer +_
_ถอด unsafe-eval · idempotency ย้ายขึ้น PostgreSQL + กัน race ด้วย InFlight ·_
_BusinessRuleException หยุด framework error รั่วถึง client. P2: §65 ตรี 8→16 กลุ่ม_
_อนุมาตรา + เลิก over-add-back (cap=0, VAT เคลมได้) · tax point §78/2 นำเข้า ·_
_แยก CitAmount ออกจาก TotalTaxWithheld · เปิด job §82/3 ที่เขียนไว้แต่ไม่มีใครเรียก) ·_
_รอบ 22: audit 3 ทีม (คำแปล/adversarial/_
_process) — เนื้ออีเมล+LINE ตามภาษาใบ · แนบ PDF อีเมล manual ที่หายไป · ลิงก์ LINE_
_จริงแทน example.com · e-Tax by Email on-demand ใช้ renderer รวม · RTGS fuzz 29 เคส_
_+ แก้ เเ/ไทย/ฤๅ · รอบ 21: ออกเอกสารเป็นอังกฤษ "เฉพาะใบเดียว" —_
_เติมทางเข้า UI 2 ชั้นที่ resolver รองรับอยู่แล้วแต่กดไม่ได้ (รายใบ `#fDocumentLanguage` +_
_ครั้งเดียว `#pdfLangMode`) · แก้ `DocumentResponse` ไม่คืน `DocumentLanguage` (เปิดแก้แล้ว_
_ภาษาหาย) · แก้ metadata Title ของ PDF/A-3 คำนวณภาษาเองข้าม resolver กลาง ·_
_รอบตาม: สืบทอดภาษาใบต้นทาง 5 ทาง — convert/clone/settlement receipt/CN คืนมัดจำ/_
_recurring generator · รอบตาม 2: ภาษาเริ่มต้นรายผู้ติดต่อ `Contact.DocumentLanguage` —_
_ประทับตอนสร้างใบ ไม่ resolve ตอนพิมพ์ · ฟอร์ม+server fallback คู่ขนานแบบเครดิตเทอม ·_
_รอบตาม 3: ชื่อ/ที่อยู่บริษัทภาษาอังกฤษ — NameEn เป็นชื่อหลักโหมด en, AddressEn ใหม่_
_+ ThaiRomanizer ถอด RTGS อัตโนมัติเมื่อไม่ได้กรอก, ที่อยู่ลูกค้าถอดอัตโนมัติ,_
_ปุ่มแปลงในตั้งค่า) ·_
_รอบ 20: เอกสารภาษาอังกฤษ — HTML renderer_
_ฝังคำไทยตาย 20 จุดทั้งที่ label มีครบและ QuestPDF ใช้อยู่ (เอกสารปนไทย + หน้าตาต่างกัน_
_ตาม renderer) แก้ครบ + เทสต์กันซ้ำ · เปิด ValidateOnBuild ทุก environment + CI ตรวจ_
_วงกลม DI) · รอบ 19: เหตุผลใบเพิ่มหนี้ §86/9 ครบวงจร_
_(enum+บังคับก่อนอนุมัติ+พิมพ์บนกระดาษ+สต๊อกเฉพาะ "ส่งสินค้าเกิน") · แก้วงกลม DI ที่_
_ทำให้ start ไม่ขึ้น + เพิ่ม tools/di_cycle_check.py) · รอบ 18: เครดิต NextAcc ท้ายเอกสารเฉพาะ_
_แพ็กเกจที่ราคารายเดือน = 0 (เกณฑ์เดียว ครอบทดลองใช้+ฟรีตลอดชีพ) — ไม่ดูธง_
_IsPermanentFree/ประวัติการจ่ายเงินเลย · ต่อสายครบทุก renderer · ไม่แปะแบบฟอร์มราชการ)_
_· รอบ 17: OCR ใบราคารวม VAT เก็บ Line.Amount_
_เป็นยอดรวม VAT ⇒ JE เดบิตเกินเครดิตเท่ายอด VAT ("บันทึกบัญชีไม่สมดุล") · คำเตือน_
_หัก ณ ที่จ่ายเลิกยิงใส่ใบซื้อสินค้า) · รอบ 16: ใบเสร็จตัดลูกหนี้ต้องตัดยอดก่อนหักภาษี_
_(เกณฑ์ Cash) — เดิมตัดยอดสุทธิ เหลือลูกหนี้ค้าง = WHT และเครดิต 11910 หายไป;_
_+ guard ตอนอนุมัติ + ตัวตรวจย้อนหลัง + UI เลือกเกณฑ์ที่ ตั้งค่า → ภาษี)_
_· รอบ 15: ใบต้นทางที่ผูกไว้แสดงตอนเปิดแก้ไข —_
_ปักหมุดก่อนตัวโหลด async, เติม option กลับเมื่อใบต้นทางชำระครบจนหลุดลิสต์, ล็อก+บอกเหตุ)_
_· รอบ 14: §90/2 ปิดทุกทางที่ไปถึงใบกำกับภาษี_
_เมื่อบริษัทไม่จด VAT — แปลงเอกสาร/แปลงทั้งหมด/ติ๊กใบเดียว เดิมเปิดโล่งจนไปตายตอนอนุมัติ)_
_· รอบ 13: "เทมเพลตเริ่มต้นของชนิดนี้"_
_ให้ server ตัดสินที่เดียว — หน้าเทมเพลตเคยเดาเอง (ไม่ดู isActive + ตกมาที่ใบแรก)_
_จึงแก้คนละใบกับที่ปุ่มตั้งค่าเขียนลง + ส่ง ""/0 เพื่อให้ล้างค่าได้จริง)_
_· รอบ 12: ลำดับที่มาเทอมชำระเงิน 4 ชั้น —_
_ประทับ autoSrc + เทียบลำดับจริง (เดิมใครมาถึงก่อนชนะ: AI แซงค่าของลูกค้าได้),_
_ข้อความบนกระดาษปรับตามวันเครดิตที่ใช้จริง, ครอบฝั่งซื้อด้วย, + fallback ฝั่ง server)_
_· รอบ 11: `RecheckRdComplianceAsync` +_
_`POST /ocr/documents/{id}/recheck-compliance` — ผลตรวจ RD เคย persist ตอนสแกน_
_ครั้งเดียว คำเตือนจาก validator รุ่นเก่าจึงค้างถาวรแม้กระดาษครบ (เคส: เลขผู้ซื้อ_
_อยู่ใน raw text แต่ OCR แยกช่องไม่ได้) → เปิด detail ของใบ OCR ที่ยังติดเตือน_
_ระบบตรวจซ้ำเบื้องหลังหนึ่งครั้งแล้วรีเฟรชเมื่อผลเปลี่ยน)_
_· รอบ 10: audit ช่องกรอกครั้งที่ 2 — แหล่งเงิน_
_เฉพาะใบที่เงินเคลื่อน · CertInLieu ตัดทุกอย่างที่อิงใบกำกับ (ไม่มีใบกำกับโดยนิยาม) ·_
_บริการตปท. §83/6 เฉพาะฝั่งซื้อ · เหตุผล CN + ข้อมูลใบรับรองย้ายขึ้นโซนบน)_
_· รอบ 9: ลิสต์ใบต้นทาง CN/DN โหลดทั้ง_
_สองฝั่งเป็น optgroup — ใบที่เลือกกำหนดฝั่ง ไม่ใช่ radio กรองลิสต์; กล่องใบอ้างอิง_
_(CN/DN + ใบเสร็จตัดใบค้าง) ย้ายขึ้นต่อจากชนิด+ผู้ติดต่อตามลำดับงานจริง)_
_· รอบ 8: ช่องกรอกต่อชนิดเอกสาร —_
_`_docFieldProfile` คุมวันเครดิต/เงื่อนไข/วันครบกำหนด/เลขจอง ตามชนิด + ป้ายวันที่_
_ตามบริบท (ยืนราคาถึง/กำหนดส่งมอบ) ตรงกัน ฟอร์ม-detail-PDF + server normalize)_
_· รอบ 7: WHT credit ครบ 5 เฟส (แถวในตาราง_
_อ้างอิงเคยค้างป้าย 📋 ทั้งที่สร้างแล้ว) + OCR 50 ทวิ อ่านประเภทเงินได้ป้อน `CheckRate`_
_+ อัตราพิเศษ โฆษณา/ขนส่ง/ปันผล ต้องตรวจก่อน 40(8) · ซ่อมตารางที่ถูกยุบเป็นบรรทัดเดียว_
_ด้วย literal `\\n`) · รอบ 6: ย้ายฝั่ง CN/DN ด้วยการกลับ JE,_
_กฎ RD ตามชนิดเอกสาร, CSP เปิด blob: ให้ iframe ดู PDF แนบ) · รอบ 5: audit เทมเพลตครบทุกชั้น —_
_ขนาด/แนวกระดาษเข้า QuestPDF, ข้อความชุด EN ถูกใช้จริง, สรุป schema ที่ยังไม่มีใครใช้)_
_· รอบ 4: ติ๊กเทมเพลต → กระดาษ —_
_payment terms ขึ้น preview, ปลุก 4 ติ๊กที่ตายทั้งสอง renderer, §86/4 สาขาบังคับ)_
_· รอบ 3: ThaiAddressFormatter —_
_ตัวประกอบที่อยู่ตัวเดียวของระบบ ที่อยู่บนเอกสารราชการมี ต./อ./จ. ครบทุกเส้นทาง)_
_· รอบ 2: เทมเพลตเอกสาร response_
_ครบทุก field — ติ๊กในหน้าปรับแต่งตรงกับ PDF จริง; เทอมชำระเงินหลายข้อ +_
_ลำดับ default ผู้ใช้>ลูกค้า>เทมเพลต) · รอบ 1: per-line account side guard:_
_`EnsureLineAccountMatchesDocSide` create/update + `RevenueLegAccountId` JE_
_safety net + datalist แยกฝั่งใน documents.html — แก้ "ทำใบเสนอราคาแล้วเจอ_
_ผังค่าใช้จ่ายตอนเพิ่ม item / JE Cr รายได้เข้า 5xxxx") ก่อนหน้า: 2026-07-31_
_(audit ทีมคิดเคส/ทีมทดสอบ 65 เคส →_
_แก้ 43 บั๊ก 3 ชุด: CN/DN text-ref resolve+undue VAT accounts+GRN block+qty cap+_
_FX rate+refund txn; ภ.พ.30 regen snapshot ticks+double-tick guard+warn-line_
_guard+CF นอกลูป+pastWindow ตามงวด+void→exclude+credit CF on file+deferral เป็น_
_บรรทัด+override exclude; e-Tax purpose code ตาม reason+original หัก CN ก่อนหน้า;_
_rounding: exempt -1 passthrough, r2 midpoint, billdisc clamp, partial-convert_
_ratio ex-VAT; subscription: trial expiry scheduler, soft-delete restore,_
_aggregate stale counters; OCR: checksum guard, negation suffix, abbrev→ปิด_
_IsVatClaimable, สาขาผู้ขายจาก contact; branch label เฉพาะนิติบุคคล) —_
_รอบ 13-14: OCR API=web UI,_
_DRAFT- placeholder, แหล่งเงิน 3-layer + Reclassify, ประกันสังคมครบวงจร,_
_floor 1,650, กท.20ก, สปส.1-03/6-09._
_รอบ 15: §82/3 block+reclassify, §82/5(6) car/fuel, §81/1 VAT-reg warning,_
_PII encrypt+ (Bank/SSN), audit-log DB trigger, §86/4 hard-block opt-in,_
_ภ.ง.ด.51 SME bracket, PDPA Wave 3 (RoPA/Consent/PiiAccessLog/Breach)._
_รอบ 42: ผู้ซื้อไม่ประสงค์รับใบกำกับภาษี — per-doc checkbox_
_`BuyerDeclinedTaxInvoice` (นอกจาก walk-in contact) ยกเว้น §86/4 gate +_
_downgrade หัว TaxInvoice → "ใบเสร็จรับเงิน"; branch code (§86/4) บังคับเฉพาะ_
_ผู้ซื้อนิติบุคคล บุคคลธรรมดาไม่บังคับ (ประกาศอธิบดีฯ 199). VAT → ภ.พ.30 ครบ._
_รอบ 43: §86/4 smart-lock — ไม่ block ทางตัน: บุคคลธรรมดา/ไม่มีเลขภาษีที่ข้อมูล_
_ไม่ครบ → auto ตั้ง BuyerDeclinedTaxInvoice (ออกเป็นใบเสร็จ, VAT ครบ); นิติบุคคล_
_→ block + ชี้ทางออก (เติม/ติ๊กไม่รับใบกำกับ). + ตราประทับบริษัท (company seal):_
_อัปโหลด `/settings/stamp` → `CompanySettings.StampPath` + ขนาด/ตำแหน่ง_
_(StampWidthMm/HeightMm/Align); ประทับในโซนลายเซ็น **เฉพาะเอกสารที่อนุมัติแล้ว**_
_(เงื่อนไขเดียวกับช่องผู้อนุมัติ) ทั้ง PDF native + HTML preview._
_รอบ 77: integration Expense/PV รองรับ `AutoApprove` (default true = เดิม). false =_
_สร้าง Draft: ไม่ลง GL + ไม่ออก 50 ทวิ ตอน create (เดิม hardcode Approved + JE +_
_50 ทวิ เสมอ). อนุมัติภายหลังผ่าน `ApproveDocumentAsync` → post JE + ออก 50 ทวิ._
_เพิ่มเงื่อนไข 50 ทวิ ตอน approve ให้ครอบ "จ่ายเต็มแล้ว" (BalanceDue<=0+PaidAmount>0)_
_ไม่ใช่แค่ Status Paid — กันใบ PV Draft ที่จ่ายแล้วมาอนุมัติทีหลังไม่ออก 50 ทวิ._
_backward-compat: ผู้เรียกเดิมไม่ส่ง AutoApprove = true เหมือนเดิม._
_รอบ 76: gate ภาษีซื้อ (§82/5) — ใบที่ "ขอเครดิตภาษีซื้อ" (HasTaxInvoiceReference)_
_ต้องให้ Contact ผู้ขายมีเลขภาษี 13 หลัก มิฉะนั้น **block อนุมัติ (hard, ไม่มี_
_acknowledge bypass)** ใน `ApproveDocumentAsync` — เคลมภาษีซื้อโดยผู้ขายไม่มีเลข_
_ภาษี/ไม่ใช่ใบกำกับเต็มรูป = §82/5(1)(5) โดนประเมินคืน. + frontend guard: เลขผู้เสีย_
_ภาษีผู้ขายต้อง 13 หลัก + ตรงกับ contact ที่เลือกก่อนบันทึก (เตือน+บล็อก). หมายเหตุ:_
_backend เคลมด้วย Contact.TaxId จริง (field ผู้ขายบนฟอร์มเป็น display อ่านจาก contact)._
_รอบ 75: FormatBranch ทนทานขึ้น — (1) รหัสสาขาจริง + ชื่อสาขา default "สำนักงานใหญ่"_
_ไม่ต่อท้าย "(สำนักงานใหญ่)" ที่ขัดกัน; (2) รหัสสาขา=00000 แต่ "ชื่อสาขา" เป็นเลขล้วน_
_(เช่น 00001 — กรอกผิดช่อง) → แสดง "สาขาที่ 00001" (regex เลขล้วนกัน false-positive_
_เช่น "สำนักงานใหญ่ ชั้น 5"). หมายเหตุ: ถ้า รหัสสาขา=00000 จริง (ชื่อ="สำนักงานใหญ่")_
_เอกสารโชว์ "สำนักงานใหญ่" ถูกต้องตามกฎหมาย — ต้องตั้ง รหัสสาขา=00001 ที่ entity._
_รอบ 74: เลขที่ 50 ทวิ เพิ่มเดือน — `WHT-{ปี}-{run}` → `WHT-{ปี}{เดือน2หลัก}-{run}`._
_เดิม manual (`CreateAsync`) รันต่อปี (WHT-2026-0006) แต่ auto (`AutoGenerate`)_
_รันต่อเดือน (WHT-202607-0006) — ไม่สอดคล้อง. แก้ manual ให้ใส่เดือน (TaxMonth ที่_
_ผู้ใช้ระบุ = เดือนภาษี) รันต่อเดือน; auto เปลี่ยนฐานเดือนจาก UtcNow → เดือนของ_
_paymentDate (tax month) ให้ตรง TaxMonth ในใบ. เลขเก่าไม่ชนกัน (prefix ต่างกัน)._
_รอบ 73: 50 ทวิ — แก้ที่ **client-side renderer** ด้วย (wht.html สร้าง HTML print_
_เองใน JS ไม่ผ่าน C# BuildWithholdingTaxCertHtml). รอบ 72 แก้แค่ 2 renderer ฝั่ง_
_C# (HTML+native) → ปุ่ม "พิมพ์/บันทึก PDF" ที่ผู้ใช้เห็นยังขึ้น 2 ฉบับ+ป้ายเดิม._
_แก้ buildCopy 1,2 → 1,2,3 + copy-label บรรทัดที่ 3 + ป้ายลงชื่อ → "ผู้มีหน้าที่_
_หักภาษี ณ ที่จ่าย" ใน wht.html. **บทเรียน: 50 ทวิ มี 3 renderer** (C# HTML,_
_C# native, JS ใน wht.html) — แก้ต้องครบทั้งสาม._
_รอบ 72: 50 ทวิ — (1) ป้ายลงชื่อเปลี่ยน "ผู้จ่ายเงิน" → "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย"_
_(ตรงช่องลงชื่อ ทั้ง HTML + native; ป้าย "ผู้จ่ายเงิน" ในแถวเงื่อนไข (1)หัก/(2)ออกให้_
_คงเดิม—เป็นฟิลด์ทางการ). (2) เพิ่ม "ฉบับที่ 3 (สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็น_
_หลักฐาน)" — จากเดิม 2 ฉบับ (ผู้ถูกหัก ใช้แนบ/เก็บ) เป็น 3 ฉบับ; loop native 1→3,_
_BuildCopy(3) ฝั่ง HTML, copy-header เพิ่มบรรทัดที่ 3 ทั้งสอง renderer._
_รอบ 71: 50 ทวิ — เพิ่มสาขาต่อท้ายชื่อผู้จ่าย/ผู้รับ (inline, ไม่กระทบ layout ฟอร์ม)._
_เฉพาะนิติบุคคล (เลขภาษี 13 หลักขึ้นต้น 0) ผ่าน `CertBranchSuffix` — บุคคลธรรมดา_
_(ภ.ง.ด.3) ไม่มีสาขา คืนค่าว่าง. 50 ทวิ ไม่บังคับช่องสาขาตามกฎหมาย (คนละกรณี §86/4)_
_เพิ่มเพื่อความครบถ้วนในการระบุตัว (ช่วย ภ.ง.ด.53). ครบทั้ง HTML + native cert renderer._
_รอบ 70: แสดง "สาขา/สำนักงานใหญ่" บนเอกสาร (§86/4 + ประกาศฯ 199). เดิมหัวเอกสาร_
_ไม่แสดงรหัสสาขาเลย. เพิ่ม `FormatBranch(code, name, lang)`: 00000/ว่าง = "สำนักงานใหญ่",_
_อื่น = "สาขาที่ {code}" (+ชื่อสาขา). แสดงต่อท้ายเลขผู้เสียภาษีทั้งบริษัท (ผู้ออก) +_
_คู่ค้า ทั้ง HTML + native renderer. ตอบคำถามผู้ใช้: 00000 ต้องเป็น "สำนักงานใหญ่"_
_(ถูกต้องตามกฎหมาย) ไม่ใช่ "สาขา 00000"._
_รอบ 79 (UX สร้างมัดจำ — discoverability + กันพลาด): (1) เพิ่ม pseudo-type
"💰 ใบมัดจำ / รับเงินล่วงหน้า" ใน dropdown ประเภทเอกสาร (ฝั่งขาย) → save map เป็น
`Receipt` + `isDeposit=true` อัตโนมัติ (pattern เดียวกับ CombinedInvoiceTaxInvoice
ที่ map → TaxInvoice); onDocTypeChange ติ๊ก IsDeposit + โชว์ depositOptions ให้เลย
(เดิมต้องรู้เองว่า "เลือกใบเสร็จ → ติ๊ก checkbox"). (2) guard ตอน save: บรรทัดใด
เลือกผัง 215xx/217xx (ขายรอรับรู้/รับล่วงหน้า) แต่ไม่ได้ตั้งเป็นเอกสารมัดจำ → เตือน
(GL เข้า 217xx แต่ subledger มัดจำไม่รู้จัก = "มัดจำไร้เอกสาร" ที่ Realize/หัก/drives
ไม่เจอ) แนะนำเลือกประเภทใบมัดจำ. flag `IsDeposit` (ไม่ใช่ผังบัญชี) คือตัวคุมทุกกลไก
มัดจำ. frontend เท่านั้น._
_รอบ 78 (หัก "JV มัดจำที่ไม่มีเอกสาร" ได้): มัดจำที่ integration post ตรงผ่าน
`/integration/journals` (ไม่มี SourceDocumentId) เดิมหักเข้าใบแจ้งหนี้ไม่ได้ผ่าน UI
(ApplyDepositToInvoiceAsync ต้องมีใบมัดจำ Document). เพิ่ม:
(1) `SearchJournalDepositsAsync` — ค้น JE Posted, ไม่มี source doc, ยังไม่ apply,
มีขา Cr 215/217, filter ด้วย query (EntryNumber/Reference/Description contains) →
`GET /document/journal-deposits?q=`. (2) `ApplyJournalDepositToInvoiceAsync` —
อ่านขา Cr จริงของ JV (215/217 + 21913/21911) → post JV ตัดชำระ: Dr บัญชีเดิม +
Dr VAT / Cr ลูกหนี้ (gross) + ลด BalanceDue + mark `JV.DepositAppliedToDocumentId`
(one-shot, หักเต็ม JV; gross>ยอดใบ → block) → `POST /document/{id}/apply-journal-deposit`.
(3) frontend: กล่องค้น JV ในฟอร์ม (booking auto-fill) → editing หักทันที / creating
หักหลัง approve. GL-critical v1 — verify Windows._
_รอบ 77 (หักมัดจำได้ในฟอร์มสร้างเอกสารเลย): เดิมตอนสร้างใหม่ banner มัดจำคงค้าง
บอกแค่ "บันทึกใบก่อน แล้วเปิดแก้เพื่อหักมัดจำ" (2 ขั้น). เพิ่ม selector ในฟอร์ม
(checkbox + เลือกใบมัดจำ + ยอด, auto-select ใบ booking ตรงกัน) → เก็บ
`_pendingDepositApply` → `save()` หลัง approve สำเร็จเรียก `applyDeposit` อัตโนมัติ
(ApplyDepositToInvoiceAsync ที่ verified) = create+approve+หักมัดจำ ใน action เดียว.
cap ยอดไม่เกินคงเหลือ (UI guard). ต้อง "บันทึกและอนุมัติ" (บันทึกร่าง → หักไม่ได้
เพราะ ApplyDeposit ต้องเอกสาร approved). fail-soft: หักไม่ผ่าน → ใบยังอยู่ หักเอง
ได้. + แก้บั๊กเดิม: banner ใช้ `deposit.id` (DepositSummary) แทน `.depositDocumentId`
ที่ไม่มีจริง (quick-apply เคย pass undefined). frontend เท่านั้น ไม่แตะ GL logic._
_รอบ 76 (display "อ้างอิง" = เลขจอง ไม่ใช่ dedup key): integration เก็บ externalRef
(REC260718006 = dedup key ภายใน) ลง `Document.Reference` → PDF/หน้าเอกสารโชว์เป็น
"อ้างอิง" ทำให้ลูกค้า/บัญชีเห็นเลขใบเสร็จ TakeTime แทนรหัสจอง. เพิ่ม
`Document.DisplayReference` (NotMapped: `BookingNumber ?? Reference`) → PDF (5 จุด
DocumentRenderer + HTML) + frontend detail (2 จุด, ใช้ `bookingNumber || reference`)
render จากนี้. RES-id (BookingNumber) มีความหมายกับคน → โชว์แทน; company doc ไม่มี
BookingNumber → คืน Reference เดิม. **display เท่านั้น — Reference field ยังเก็บ
externalRef สำหรับ dedup/idempotency + resolve มัดจำ (รอบ 75) ไม่กระทบ**._
_รอบ 75 (depositAppliedRef รับ external ref — root cause ที่ทำ degrade เสมอ):
หลักฐานจากใบทดสอบจริง (TIV-20260718-0001): TakeTime ส่ง `depositAppliedRef` เป็น
**เลขใบเสร็จของเขาเอง** (REC260713008) แต่ resolver จับคู่เฉพาะ `DocumentNumber`
ของ NextAcc (REC-20260713-xxxx) → หาไม่เจอ → PostCashSale throw → **degrade เป็น
ตั้งหนี้เสมอ** แม้ deploy แล้ว → TakeTime fallback settle (มัดจำเป็น payment ใหม่
= เงินสด/มัดจำเบิ้ล + REC settlement งอก). แก้ 3 จุด (single lock-lookup / multi
loop / UnrealizeDrivesDeposit): จับคู่ `DocumentNumber` ก่อน → ไม่เจอ → จับคู่
`Reference` (= externalRef ที่ integration stamp ตอนสร้างใบมัดจำ). เลขจอง
(BookingNumber) ไม่เกี่ยว — `อ้างอิง` บนใบ = externalRef ตาม contract idempotency._
_รอบ 74 (purge สมมาตร void — กัน 21510 สะสมติดลบจาก recreate ทับ): TakeTime เจอ
21510 ติดลบ −934.58 จากการ resync (ลบ+สร้างใหม่) ซ้ำหลายรอบ. VoidDocumentAsync
มี step 2b (กลับ JV ตัดชำระด้วยมัดจำ + คืน subledger ใบมัดจำ) แต่ **PurgeDocumentAsync
ไม่มี** → hard-delete ใบกำกับแล้ว JV ApplyDeposit (SourceDocumentId=ใบมัดจำ, หลุด
step 1 ที่ลบเฉพาะ JE sourced จากใบนี้) ค้าง → มัดจำถูกตัด 217xx/21913 ถาวร +
subledger ไม่คืน → recreate ทับ → Dr 21510 สะสม. เพิ่ม purge step 0c (mirror 2b
แบบ delete): ลบ JV ApplyDeposit ที่ Reference=เลขใบนี้ (คัด Cr 113) + คืน subledger
มัดจำ (RealizedAmount/RecognizedAt/AppliedTo). **หมายเหตุ: root cause ที่ TakeTime
เจอคือ env ยังไม่ deploy branch นี้ — ทั้ง isCashSale + step 2b/0c ยังไม่ทำงานที่นั่น
→ ทุก resync สะสม. deploy = หยุด churn + delete สมมาตร**._
_รอบ 73 (มัดจำหลายใบ/ใบกำกับ — blocker โรงแรม): `driveDeposit` เดิม resolve
`depositAppliedRef` เป็นเลขเดียว (exact match) → comma-separated หาไม่เจอ →
degrade เป็น AR. เพิ่ม: split `depositAppliedRef` ด้วยจุลภาค — ถ้า >1 เลข →
loop **reverse ทุกใบเต็มยอดคงเหลือ** (GL-driven ต่อใบ: Dr 215xx/217xx + 21913/
21911 ที่แต่ละใบ Cr ไว้จริง, mark ใบมัดจำ realized เต็ม + one-shot guard ต่อใบ +
row-lock). ผลรวม Dr = `depositAppliedAmount` (ยอดรวมที่ส่งมา) → cashAmt (Total −
รวม) สมดุลพอดี; ไม่ตรง → AutoPost balance check throw → degrade (ปลอดภัย).
**เลขเดียว → else = logic เดิมไม่แตะ (zero regression)**. contract TakeTime: คง
comma-separated `depositAppliedRef` + `depositAppliedAmount`=ผลรวม, แต่ละใบถูก
consume เต็ม (semantic checkout โรงแรม). ⚠️ GL-critical — Windows GL test เคส
2+ ใบก่อนเปิด._
_รอบ 72 (ลบ+resync ให้สะอาด — ใบเสร็จ REC ลอยค้าง): ผู้ใช้ลบใบกำกับเก่าที่มี
ปัญหาเพื่อ resync ใหม่ แต่ **PurgeDocumentAsync เดิม step 7 แค่ NULL
RelatedDocumentId ไม่ได้ลบใบเสร็จ settlement (REC)** → REC ลอยค้างใน list. แก้:
(1) purge เพิ่ม step 6c — cascade ลบ settlement receipt (IsSettlementReceipt +
RelatedDocumentId==ใบนี้) พร้อม e-Tax/line ก่อน NULL ref; (2)
`BulkCleanupController`: `GET /cleanup/orphaned-settlement-receipts` (diagnostic,
API key อ่านได้) + `POST .../purge` (Owner soft-delete) — ล้าง REC ที่ orphan
อยู่แล้วจากการลบก่อนหน้า (RelatedDocumentId NULL หรือต้นทาง Voided). settlement
receipt ไม่มี JE ของตัวเอง (payment ถือ JE, ถูกลบไปกับใบกำกับ) → ลบปลอดภัย
ไม่กระทบ GL. วิธี resync สะอาด: ลบทั้ง group (มัดจำ+ใบกำกับ+REC) → resync มัดจำ
fresh + ใบกำกับ isCashSale อ้าง depositAppliedRef ใหม่._
_รอบ 71 (กันยอดเบิ้ลจากชำระซ้ำ — root cause ที่ผู้ใช้เจอ): `ProcessPaymentAsync`
(integration payment endpoint) เดิมมีแค่ idempotency-by-reference — **ไม่มี**
status guard/over-pay cap → ยิง payment ส่วนมัดจำแยก = Dr เงินสด/Cr ลูกหนี้ ซ้ำ
กับที่มัดจำ+ใบกำกับลงไปแล้ว → เงินสด/มัดจำนับซ้ำ + สร้าง REC settlement เยอะ.
เพิ่ม guard: เอกสาร `IssuedAsCashReceipt` (ขายเงินสด settle ในตัวแล้ว) หรือ
`Status=Paid`/`BalanceDue≤0` → skip ไม่รับชำระภายนอก; over-pay (Amount>คงค้าง)
→ throw พร้อมชี้ให้ใช้ `depositAppliedRef` (drives) แทนการยิง payment แยก.
(`DocumentService.CreatePaymentAsync` มี guard นี้อยู่แล้ว — เติมให้ครบฝั่ง
integration). วิธีถูก: ออกใบกำกับ isCashSale + depositAppliedRef → driveDeposit
**ดึงใบมัดจำเดิม** (กลับ 21510/21913) ใบเดียวจบ ไม่สร้าง receipt ใหม่/ไม่นับซ้ำ._
_รอบ 70 (TakeTime cash-sale — GL สะอาด ไม่มีลูกหนี้): แก้ตามที่ผู้ใช้ทัก — ขายเงินสด
B2B ต้องไม่มีลูกหนี้การค้าในการลงบัญชี. เดิม integration `isCashSale` ลงผ่าน
mapping-JE (Dr ลูกหนี้) + ApplyDeposit + settle → **AR-transit** (สุทธิ 0 แต่ footer
JE โชว์ Dr ลูกหนี้). เปลี่ยนเป็น: **(1)** ขยาย branch cash-receipt ใน
`AutoPostToJournalAsync` ให้รับ `TaxInvoice && IssuedAsCashReceipt` (sales branch
เพิ่ม `&& !IssuedAsCashReceipt` เพื่อ exclude; บังคับ `receiptSettlesAr=false`) →
reuse เส้น `driveDeposit` ที่ verified. **(2)** `IDocumentService.PostCashSaleJournalAsync`
(public wrapper: AutoPost + SaveChanges + detach-on-error + คืน JE id). **(3)**
integration isCashSale ตั้ง `IssuedAsCashReceipt=true` + `PaymentAccountId` + stamp
`DepositAppliedAmount` ทุกกรณี → post ผ่าน PostCashSale (ไม่ใช่ mapping+settle).
ผล GL: `Dr เงินสด(+217xx+21913 ถ้ามัดจำ) / Cr รายได้+21911` **ไม่มี 113 เลย** ใบเดียว.
fail-soft → degrade เป็นตั้งหนี้ (mapping JE) ให้ TakeTime fallback. ลบ
`TrySettleCashSaleAsync`/ApplyDeposit-transit path ทิ้ง. void สมมาตรผ่าน 7c เดิม.
⚠️ GL-critical + env นี้ test ไม่ได้ → **ต้อง verify GL บน Windows ก่อนเปิด**._
_รอบ 69 (ทุกเอกสาร): OWNER fallback ไม่ทับผู้อนุมัติตัวจริงอีกต่อไป. เดิม fallback_
_ทำงานทุกครั้งที่ slot 1 ไม่มี "รูปลายเซ็น" → ผู้อนุมัติจริงที่ยังไม่อัปโหลดลายเซ็น_
_ถูกแทนด้วยลายเซ็น+ชื่อ "เจ้าของ" ทุกประเภทเอกสาร (โชว์ผิดคน + แก้ชื่อผู้อนุมัติ_
_ไม่เปลี่ยนตาม). แก้: fallback ทำงานเฉพาะเมื่อ "ไม่มีผู้อนุมัติระบุเลย" (slot 1 ไม่มี_
_ชื่อ) — ถ้ามีผู้อนุมัติจริง (ชื่อ resolve สดจาก Users) คงชื่อไว้ เว้นบรรทัดลายเซ็น_
_ให้เซ็นมือ. custom signatory (opt-in) คงเดิม (ตั้งใจ fix ชื่อ — แก้ที่ Settings)._
_รอบ 68: ใบเสร็จ settlement เก่า — โชว์ชื่อผู้กดที่ถูกต้องด้วย. ชื่อ/ลายเซ็นถูก_
_resolve สดตอน render (ไม่ snapshot — ไม่มี field เก็บ HTML/PDF/ชื่อบนใบ) → ใบเดิม_
_แสดงชื่อถูกอัตโนมัติหลัง deploy. เสริม robustness: ใบ settlement ที่ UpdatedBy ว่าง_
_(ใบเก่า/บาง path) → approver ตกไปใช้ CreatedBy (= ผู้กดคนเดียวกัน) กันเว้นว่าง/เด้ง_
_ไปเจ้าของ. ไม่ต้อง migrate DB._
_รอบ 67: ใบเสร็จ settlement — ยกเว้น custom authorized signatory ด้วย (ต่อ รอบ 64)._
_รอบ 64 ข้าม owner fallback ให้ใบ settlement แล้ว แต่ **ยังไม่ข้าม custom signatory**_
_(opt-in `UseCustomAuthorizedSignatory`) ที่ override slot 1 ก่อนหน้า. `AuthorizedSignatoryName`_
_เป็นค่า "เก็บไว้" ใน CompanySettings → แก้ชื่อ user แล้วไม่เปลี่ยนตาม = อาการ "ชื่อเก่า_
_ไม่อัปเดต" + ลายเซ็นเจ้าของที่ผู้ใช้รายงานซ้ำ. แก้: เพิ่ม `!doc.IsSettlementReceipt` ที่_
_เงื่อนไข custom signatory ด้วย → ใบเสร็จ settlement ใช้ผู้กดบันทึก (อ่านชื่อสดจาก Users)_
_เสมอ ทั้ง custom + owner ข้ามหมด. ยืนยันไม่มี name snapshot ตอนสร้าง (ไม่เซ็ต PreparerName)._
_รอบ 66 (backlog F11): `RecalcVatTotals` นับ JE_INPUT เป็นภาษีซื้อ. เดิมภาษีซื้อ_
_จาก JE ล้วน (tag "JE_INPUT") ถูกเช็ค `!= "INPUT"` → หลุดไปรวมใน OutputVat +_
_หายจาก InputVat = ภาษีขายเกิน + ภาษีซื้อขาด → NetVat ผิด (นำส่งเกิน) ตอนแก้ไข/_
_finalize รายงานที่ recompute. แก้: `IsInputLine` = "INPUT" or "JE_INPUT"_
_(ตรงกับ LineSide ที่ generate ครั้งแรกถูกอยู่แล้ว — ปิด drift ระหว่าง 2 เส้นทาง)._
_รอบ 65 (backlog F5 — ปิดช่องนำส่งภาษีขายขาด): ใบแจ้งหนี้ (Invoice) ที่มี VAT_
_เข้า ภ.พ.30. เหตุ: `AutoPostToJournalAsync` ลง Cr 21911 ให้ทั้ง Invoice และ_
_TaxInvoice เท่ากัน แต่ `TaxService.GenerateVatReport` รายงานเฉพาะ TaxInvoice →_
_ใบแจ้งหนี้ที่มี VAT มีภาระภาษีขายใน GL แต่ไม่เคยถูกนำส่ง = ภ.พ.30 < GL (โดนปรับ)._
_แก้: branch output VAT รับ Invoice (VatAmount>0) ด้วย ยกเว้นใบที่ถูกแปลงเป็น_
_ใบกำกับภาษี (`supersededInvoiceIds` = Invoice ที่มี TaxInvoice child non-void_
_อ้างถึง) กันนับซ้ำ. Invoice→Receipt = settlement (Receipt child ถูก exclude ที่_
_branch เดิมอยู่แล้ว) ไม่กระทบ. **ค้าง (design)**: แปลง Invoice→TaxInvoice ที่_
_ทั้งคู่มี VAT → GL 21911 เบิ้ล (ต้อง reverse JE ใบต้นทางตอน convert) แยกแก้._
_รอบ 64: ใบเสร็จ settlement — ช่องผู้อนุมัติ = ลายเซ็นผู้กดบันทึก ไม่ใช่เจ้าของ._
_ปัญหา: กดรับเงินจากใบกำกับ/ใบแจ้งหนี้ → ใบเสร็จโชว์ลายเซ็น+ชื่อ "เจ้าของ" (Owner)_
_ไม่ใช่ผู้กด และผู้กดแก้ชื่อตัวเองแล้วไม่เปลี่ยนตาม. เหตุ: `ResolveSignersAsync`_
_มี Owner-signature fallback เมื่อ approver ยังไม่มีรูปลายเซ็น → ผู้กดที่ยังไม่ตั้ง_
_ลายเซ็นเด้งไปลายเซ็น+ชื่อเจ้าของ. แก้: fallback นี้ **ข้ามใบ IsSettlementReceipt**_
_→ ช่องผู้อนุมัติ = ผู้กด (UpdatedBy) เท่านั้น (ผ่านเช็คสิทธิ์อนุมัติแล้ว; ไม่มีสิทธิ์_
_= ใบเป็น Draft). ชื่ออ่านสดจาก Users ทุกครั้ง แก้ชื่อแล้วเปลี่ยนตามทันที._
_รอบ 63: ชื่ออาคาร (BuildingName) ขึ้นบนที่อยู่เอกสารครบ —_
_`PdfGenerationService.FormatThaiAddress` รับพารามิเตอร์ buildingName เพิ่ม_
_(เดิม structured street ประกอบจาก เลขที่+หมู่+ถนน เท่านั้น → contact ที่บันทึก_
_ชื่ออาคารไว้หายจากเอกสารพิมพ์ทุกใบ); อัปเดต call site ทั้ง 6 จุด (HTML + native_
_renderer, company + contact + 50ทวิ) + กันซ้ำเมื่อ street head จาก free-text_
_มีชื่ออาคารอยู่แล้ว + คงพฤติกรรมเดิมเมื่อมีแค่ชื่ออาคารโดด ๆ (ตกไปใช้ free-text)._
_`WithholdingTaxCertService.ComposeFullAddress` (API response) เติม buildingName_
_ใน structured fallback ด้วย. e-Tax XML มี BuildingName element อยู่แล้วทั้ง 2 ฝั่ง._
_รอบ 62: **Deposit Center** — redesign หน้าเงินมัดจำทั้งหน้า (`/pages/_
_deposit-center.html` + endpoint ใหม่ `GET document/deposit-center`): payload_
_เดียวจบ (rows + KPI + **GL tie-out** + sources + GeneratedAtUtc + build marker)_
_→ หน้ากับ GL ไม่ตรง = ฟ้องบน banner ทันที ไม่มีวันโชว์ 0 เงียบ. URL ใหม่ทั้ง_
_หน้า+API = ทะลุ cache เก่าทุกชั้น (SW/browser/CDN) ที่ทำ "แก้แล้วยังขึ้น 0"._
_เมนูชี้หน้าใหม่, หน้าเก่า redirect. mobile-first cards / desktop table, tabs+_
_ค้นหา+เรียง, VAT chip (พักรอ 21913/รายงานแล้ว), booking chip, progress bar_
_รับรู้/คืน, refresh + เวลาข้อมูลจากเซิร์ฟเวอร์, modal รับรู้/คืนเงิน (payload_
_เดิม), accordion ที่มาของตัวเลขรายบัญชี._
_รอบ 61 (ปิด backlog สูงจาก audit รอบ 60): กัน**รายได้ซ้ำ** QT/BN → Invoice_
_และ → Receipt (นับ Receipt/RV เป็น revenue-child ของ QT/BN ใน conversion guard_
_F3); **ภ.พ.30 นับ Receipt ที่แปลงจาก QT/BN** (ขายเงินสด — VAT ลง GL แต่เดิม_
_ถูก exclude เพราะมี RelatedDocumentId = นำส่งขาด F4-sales; settlement ของ_
_Invoice/TaxInvoice ยัง exclude ตามเดิม); PI อ้าง GRN + VAT ต้องห้าม → Dr VAT_
_เข้าเป็นต้นทุนตามบรรทัด (เดิม JE ไม่สมดุล F1-purchase); 50 ทวิ ลง**เดือนที่จ่าย_
_จริง** (paymentDate param F13) + auto-สร้างตอน approve เอกสารจ่ายที่จบทันที_
_(PV เงินสด/settle — เดิมไม่มี cert เลย F12); recurring ส่งต่อส่วนลดบาท/_
_IsVatClaimable/ProductCode/BillDiscount/PricesIncludeVat (D1/D2) + RunNow lock_
_+ เลื่อน NextRunDate กันออกใบซ้ำกับ cron (D3)._
_ยัง backlog (ต้อง design/เสี่ยงสูง): F5 Invoice ไม่เข้า ภ.พ.30 (VAT ใน GL ตั้งแต่_
_approve — ต้องเลือก post ตอน settle หรือรายงาน Invoice), F15 ภ.พ.36 ไม่มี JE,_
_F10 Receipt ขายสดไม่ตัด COGS/สต๊อก (เสี่ยงชน POS ที่เขียน movement เอง), A8_
_multi-warehouse, A4/A10 FIFO relayer ตอน void, A7 negative-stock enforcement,_
_F16 หัวใบเสร็จ settlement ขึ้น "ใบกำกับภาษี", F6 คอลัมน์ exempt/0%, §65ตรี ครบ_
_ทุกวงเล็บ, ใบกำกับอย่างย่อ (§86/6), F11 RecalcVatTotals JE_INPUT, F8 §82/3_
_anchor ตามงวดรายงาน, D5 convert race._
_รอบ 60 (audit ทุกประเภทเอกสาร — แก้ criticals ชุดแรก 15 จุด): void ใบเสร็จ_
_settlement ถูก block (ให้ยกเลิก payment แทน — กัน AR ติดลบ/เก็บซ้ำ B3/F8);_
_FindAccountAsync exact-match ข้าม header Level<4 (CN ซื้อเคย Cr "116"/"212"_
_header F2); CN/DN เคารพ WHT basis Cash (gross AR/AP ไม่แตะ 11910 F2-sales);_
_ห้าม CN/DN อ้างใบ Voided (§86/9-10 F9); void CN ซื้อคืน PaidAmount (Dr 212 probe_
_F3); ภ.พ.30 ไม่เคลม VAT undue 11640 (excluded line F4) + CIL ออกจาก input_
_whitelist (§82/4 F7); Overdue เฉพาะใบอนุมัติแล้ว + จ่ายใบ Overdue ได้ (F7-sales);_
_convert ส่งต่อ BillDiscount/PricesIncludeVat/ส่วนลดบาท/IsVatClaimable (F1, เฉลี่ย_
_ตาม partial); FIFO sign-agnostic + marginal-slice costing (A1/A2/A6); WAC rebuild_
_หลัง void (A5); void payment จัดการ 50 ทวิ Draft (B5); apply มัดจำ stamp_
_DepositAppliedAmount ลงใบ (E2); sensitivity ไม่โผล่ search/CSV (PDPA E3); HTML_
_scale-back ยกเว้นมัดจำ defer (F14). backlog ที่เหลือดูรายงาน audit._
_รอบ 60 (TakeTime cash-sale spec — B2B ขายเงินสด ใบเดียว จบ = e-Tax T03):
เพิ่ม `Document.IssuedAsCashReceipt` (bool, persist, migration ALTER ADD COLUMN).
IsCashSale settle สำเร็จ (BalanceDue→0) → ตั้ง flag → e-Tax **T03**
"ใบเสร็จรับเงิน/ใบกำกับภาษี" (EtaxInvoiceService docTypeCode/Name switch เพิ่ม
`TaxInvoice when IssuedAsCashReceipt`) + หัว PDF "ใบเสร็จรับเงิน/ใบกำกับภาษี"
(PdfGenerationService.ComputeDocumentTitle). ต่าง ServedAsReceipt (NotMapped,
คิดตอน render) ตรงที่ persist → คุม e-Tax type ได้ (ServedAsReceipt คุมแค่หัว).
+ InboundInvoiceRequest รับ deposit fields (DepositAppliedAmount/Ref/
OutputVatDeferred/DrivesJournal) → persist ลง Document ตอนสร้าง (รองรับ resync
+ deposit/checkout). **เคสมีมัดจำ (DrivesJournal=true):** ก่อน settle เรียก
`ApplyDepositToInvoiceAsync` (เส้น verified — Dr 217xx + Dr [21913|21911] /
Cr ลูกหนี้, กลับ deferred ของใบมัดจำ REC-xxx ที่อ้าง, ไม่รับรู้รายได้ซ้ำ) →
BalanceDue เหลือสุทธิ → settle รับแค่ส่วนต่าง → GL: Dr เงินสด(สุทธิ) +
Dr 217xx/VAT-reversal / Cr รายได้+VAT+ล้าง AR. drives ต้องมี "ใบมัดจำจริง"
(IsDeposit) — resolve จาก depositAppliedRef; ไม่พบ → fail-soft (ใบกำกับค้างชำระ
ไม่ล้ม sync). display-only mode (DrivesJournal=false): stamp DepositAppliedAmount
ที่ create เพื่อ render "หักมัดจำ/รับสุทธิ" เท่านั้น ไม่แตะ GL, settle จ่ายเต็ม.
⚠ ยัง gate ด้วย toggle ฝั่ง TakeTime (`Nexaacc_CashSale_Deposit`) จนกว่า
test GL บน Windows ผ่าน. **สมมาตร void (step 2b ใหม่ใน VoidDocumentAsync):**
JV ตัดชำระด้วยมัดจำ (ApplyDepositToInvoiceAsync) มี SourceDocumentId=ใบมัดจำ
จึงหลุด step 2 (กลับเฉพาะ JE ของใบที่ void) → เพิ่ม 2b: หา deposit ที่
DepositAppliedToDocumentId ชี้มาใบนี้ → reverse JV (คัดเฉพาะ JE ที่มีขา Cr 113
กัน realize-JE) + คืน subledger (RealizedAmount/RecognizedAt เฉพาะผู้ stamp
Dr 21913/AppliedToDocumentId) — ปิดช่อง AR ติดลบ + มัดจำถูกกลืนถาวร (ครอบ
ApplyDeposit ฝั่ง UI ที่มีช่องเดิมนี้ด้วย); เคส drives (ขา reversal ฝังใน JE
ใบเช็คเอาท์ ไม่มี JV) ข้าม 2b โดยธรรมชาติ → 7c ทำงานตามเดิม. **Self-heal
(TrySettleCashSaleAsync ใช้ร่วม 3 จุด: create / retry "Already synced" /
resyncUpdate):** create รอบแรก fail-soft → partner ยิงซ้ำหรือ resync → settle
ต่อจากขั้นที่ค้าง (มัดจำ apply แล้วข้าม — ดูจาก DepositAppliedAmount ที่ drives
ไม่ pre-stamp, ยอดปิดแล้ว → heal flag T03 อย่างเดียว); resync re-stamp deposit
fields จาก request (source of truth — guard PaidAmount==0 ผ่านแล้วจึงปลอดภัย)._
_รอบ 59 (audit จำลอง scenario — ชุดใหญ่ 15 แก้): **สมมาตร apply↔void สมบูรณ์** —_
_void/purge un-realize คิดจาก "บรรทัด JE จริงของใบเช็คเอาท์" (helper Unrealize_
_DrivesDepositAsync: depBase = ΣDr 215/217, เคลียร์ RecognizedAt เฉพาะเมื่อใบมี_
_Dr 21913 จริง) แทน field-ratio — ปิด F1 (gross drift +VAT/รอบ), F2 (void แล้ว_
_VAT ผี ค้าง ภ.พ.30), F3 (ล้าง stamp ของ RealizeDeposit ผิดใบ → 21913 ติดลบ/_
_21911 เบิ้ล). เคส A เพิ่ม guard ครบ (F4): one-shot+self-heal เหมือนเคส B +_
_over-apply เทียบ GL net/subledger + row-lock FOR UPDATE กัน concurrent (F9B,_
_ทั้งใบมัดจำและ JV+reload). purge un-realize subledger ก่อนลบ JE (F7). Apply_
_classic: over-apply guard + one-shot + คุม status PartiallyPaid + stamp_
_RecognizedAt เมื่อครบเท่านั้น (กัน 21913 ghost จาก partial). Refund: guard_
_เทียบคงเหลือจริง (หัก realized) + **ออกใบลดหนี้จริง** (§86/10) เมื่อ VAT เคย_
_ถูกรายงาน → ภ.พ.30 ลดยอดถูกต้อง. หน้า deposits: หัก refunded, clamp ≥0,_
_fallback GL net เมื่อ SubTotal=0, DTO เพิ่ม RefundedAmount. create block_
_IsDeposit+RelatedDocumentId (มัดจำต้อง standalone)._
_รอบ 69: **ปิดช่องว่าง OCR D1-D3** — (D1) **สาขาผู้ซื้อไม่เคยถูกอ่าน**: regex
"สาขาที่ …" เดิมยิงทับทั้งหน้าแล้วยัดผลเป็นสาขา **ผู้ขาย** ตัวเดียว ⇒ ลูกค้าที่
เป็นสาขาตกเป็นสำนักงานใหญ่เสมอ (รายงานภาษีขายผิดสาขา ประกาศฯ 199/§86/4) และถ้า
บล็อกผู้ซื้ออยู่บนสุดก็สลับกันอ่าน. แยกกฎออกเป็น `BranchCodeExtractor` (pure):
ตัดข้อความที่ "จุดเริ่มบล็อกผู้ซื้อ" → ก่อนหน้า = ผู้ขาย, หลัง = ผู้ซื้อ (จำกัด
หน้าต่าง 500 อักษร + ตัดที่หัวตารางรายการ กันเลขในตารางปน) · กฎอ่านค่าใช้
ฟังก์ชันเดียวทั้งสองฝั่ง · ไม่พบ = คืน null ไม่เดา 00000 · เติม `Contact.BranchCode`
ฝั่งลูกค้าทั้งตอนสร้างใหม่และ enrich ของเดิมที่ยังว่าง ·
(D2) **ใบมัดจำสแกนตรงได้แล้ว**: เพิ่ม pseudo-target `Deposit` ในหน้าสแกน (ฟอร์ม
แปลงเป็น Receipt + IsDeposit ให้เอง) + `DepositKeywordRegex` เดาให้เมื่อเราเป็น
ผู้ขายและกระดาษระบุ "มัดจำ/รับล่วงหน้า" + เส้น auto-create ฝั่ง backend ตัดสิน
`wantDeposit` ก่อน `Enum.TryParse` (ไม่งั้นตกไป fallback เป็น Expense) แล้วตั้ง
`IsDeposit=true` — เดิมต้องเลือก "ใบเสร็จ" แล้วไปติ๊กเอง ลืมติ๊ก = รับรู้รายได้
แทนหนี้สินมัดจำ 217xx ·
(D3) **กันสแกนใบเดิมซ้ำ**: `CheckDuplicateAsync` + `GET /document/duplicate-check`
สองระดับ — เลขใบกำกับผู้ขายตรงกัน+คู่ค้าเดียวกัน = **แน่นอน** (เลขนี้ไม่ซ้ำใน
ระบบผู้ขาย) · คู่ค้า+ยอด±0.5%+ช่วง ±60 วัน = น่าสงสัย · ตัด Voided/Rejected/
ลบแล้ว/ตัวเอง · หน้าสแกนถามยืนยันก่อนพาไปฟอร์ม (เตือนอย่างเดียว **ห้ามบล็อก** —
ผู้ขายขายของชุดเดิมซ้ำได้จริง false positive ที่บล็อกแรงกว่าปัญหาที่กัน).
D4 (แยกหลายใบในไฟล์เดียว) + D5 (AI second-opinion ผัง JE) ยังอยู่ใน TODO §D_
_รอบ 68: **เจตนา "รับเงินครบแล้ว" (tax_paid) ต้อง persist + 500 ต้องตามรอยได้**_
_เคสจริง: แปลง INV → เลือก "ใบกำกับภาษี/ใบเสร็จรับเงิน — รับเงินครบแล้ว" แต่_
_(ก) ใบร่างพิมพ์หัว "ใบกำกับภาษี" เฉย ๆ (ข) อนุมัติล้ม "เกิดข้อผิดพลาดภายในระบบ"._
_วิเคราะห์: โหมด tax_paid เดิมอยู่แค่ในฟอร์ม + chain ฝั่ง client (approve→_
_createPayment) **ไม่เคยบันทึกลงเอกสาร** ⇒ ปิดฟอร์ม/chain ล้ม = เจตนาหายเงียบ_
_(defect class "เก็บแล้วต้อง echo กลับ") และ resolver หัวรวมตัดสินจาก "ชำระจริง"_
_เท่านั้น จึงไม่มีทางรวมบนใบร่าง. แก้: (1) field ใหม่ `Document.PaidOnIssue`_
_(ครบ checklist B: entity + migration + Create/Update/Response + payload +_
_hydrate openEdit + reset + mapper) — เก็บเฉพาะ TaxInvoice/Invoice ·_
_(2) `ComputeServedAsReceipt` + `ResolveServedAsReceiptAsync` (mirror คู่):_
_**Draft → ใช้เจตนา** (เลข DRAFT ไม่ใช่เอกสารตามกฎหมาย — หลัก Draft PDF =_
_Approved PDF) / **หลังอนุมัติ → ใช้การชำระจริงเท่านั้น** (อนุมัติแล้ว chain_
_จ่ายล้ม ห้ามพิมพ์ "ใบเสร็จรับเงิน" = หลักฐานรับเงินเท็จ) · (3) การอนุมัติที่ล้ม:_
_toast แบบไม่มีคำนำหน้า = 500 ชนิด exception ไม่คาดคิด (middleware ปิดบังข้อความ)_
_— เพิ่ม **รหัสอ้างอิง 8 หลัก** ใน response 500 + prefix `[REF:xxxx]` ในแถว_
_ErrorLogs → เกิดซ้ำครั้งหน้าแจ้งรหัสแล้วเปิดดู exception จริงได้ทันที (แก้ blind_
_ไม่ได้เพราะ log อยู่ฝั่ง production)._
_รอบ 67: **ด่านตรวจโครงสร้าง JE ก่อนบันทึก (JournalPostingGuard) + สแกนย้อนหลัง**_
_ที่มา (เคสจริง UV-202607-0037 จาก EXP integration): Dr ค่าใช้จ่าย 17,890 +_
_Dr ภาษีซื้อ 1,252.30 / **Cr 21917 ทั้งใบ 19,142.30** — ไม่มีขาเจ้าหนี้เลย และ_
_WHT = 107% ของฐาน. JE สมดุลเป๊ะจึงผ่าน guard "Dr=Cr" เดิมทุกตัว ⇒ สมดุลไม่พอ_
_ต้องตรวจ **โครงสร้าง**. เพิ่ม `JournalPostingGuard` (pure class, ไม่มี DB/AI_
_dependency — kill-switch safe ตามกฎเหล็ก #1): JE-BAL · **JE-WHT-RATIO** (WHT_
_Cr > 15%+ε ของฐานค่าใช้จ่าย = เครดิตผิดบัญชีแน่ จับได้แม้ไม่รู้เอกสาร) ·_
_JE-WHT-DOC (≠ ยอดบนเอกสาร) · JE-VAT-OVER (VAT ใน GL เกินเอกสาร — น้อยกว่าได้_
_เพราะไม่เคลม/พักรอใบกำกับ) · **JE-NO-COUNTERPART** (ฝั่งซื้อ: Cr ที่ไม่ใช่บัญชี_
_ภาษีต้องรองรับ TotalAmount — ไม่ fix รหัสบัญชีเพื่อไม่ block แหล่งเงินถูกกฎหมาย_
_อื่น เช่น เจ้าหนี้กรรมการ/หักมัดจำจ่าย 11810) · JE-WHT-MISSING (warning). ตัวกลับ_
_ตรวจแบบ doc=null (ขาสลับโดยเจตนา) มัดจำข้าม doc-rules. **Wire 4 จุด**: (1)_
_`AutoPostToJournalAsync` ก่อน save — Error = throw (approve ล้มดังๆ) · (2)(3)(4)_
_integration `ValidateAndAutofixJournalAsync` (PV path เดิม + เพิ่มใน_
_`CreateJournalFromMappingsAsync` และ `UpdateJournalInPlaceAsync` ที่เดิม**ไม่_
_ผ่านการตรวจเลย** — ต้นทางของ JE เสียใบนี้). **Scanner**: `JournalAnomalyService`_
_รันกฎชุดเดียวกัน (canonical เดียว) กวาด JE posted ทั้งงวด + หา **DOC-NO-JE**_
_(เอกสารอนุมัติแล้วแต่ไม่มี JE — เส้น integration ที่ refuse แล้วเงียบ) →_
_`GET /accountant/journal-anomalies` + การ์ด "🩺 ตรวจโครงสร้างรายการบัญชี" ใน_
_accountant.html พร้อมทางแก้ (เปิดเอกสาร → 📒 แก้ผังบัญชี / ยกเลิกออกใหม่)._
_AI: ตำแหน่งที่ออกแบบไว้ = second-opinion ความสมเหตุสมผลของผังผ่าน_
_`IAiOrchestrator` + distillation (ยังไม่เปิด — กฎ rule-based จับ defect class_
_ที่เกิดจริงได้ 100% โดยไม่พึ่ง AI)._
_รอบ 66: **ตัวเลือกผังบัญชีบนมือถือไม่เด้งอะไรเลย** — โมดัล "แก้ผังบัญชี" และ_
_"เปลี่ยนผัง" ใช้ `<input list=...>` + `<datalist>` ซึ่งบนเดสก์ท็อปทำงานปกติ แต่บน_
_Android Chrome มัก **ไม่แสดงรายการเลย** (และเมื่อช่องมีค่าเต็มอยู่แล้ว เบราว์เซอร์_
_ยังกรองจนเหลือรายการเดียว) ⇒ ผู้ใช้กด/พิมพ์แล้ว "ไม่มีอะไรให้เลือก". เปลี่ยนเป็น_
_`<select>` + `<optgroup>` ตามหมวดบัญชี ทำงานเหมือนกันทุกเบราว์เซอร์ ·_
_`_ensureAllAccounts()` โหลดผังครบ **5 หมวดรวมส่วนของเจ้าของ** (coaByCode เดิม_
_มีแค่ Expense/Liability/Asset/Revenue → JE แตะหมวดทุนไม่ได้) แล้ว cache +_
_merge เข้า `coaByCode` · เลิกใช้การ parse โค้ดจากข้อความ (`"53120 ค่าโฆษณา"` →_
_split) เปลี่ยนไปถือ `accountId` ตรง ๆ จาก option value — ตัดชั้นที่พังเงียบทิ้ง ·_
_แถวใน "แก้ผังบัญชี" เปลี่ยนจากตาราง 4 คอลัมน์เป็น **การ์ดต่อบรรทัด** (บนมือถือ_
_ตารางต้องเลื่อนแนวนอนจนช่องเดบิต/เครดิตหลุดจอ) · เพิ่ม guard "ต้องมีอย่างน้อย_
_2 บรรทัด" และ "แต่ละบรรทัดใส่ได้ด้านเดียว" ในหน้าจอให้ตรงกับ server._
_รอบ 65: **"เลขที่/วันที่ใบลดหนี้จากผู้ขาย" ต้องเป็นของใบลดหนี้ ไม่ใช่ใบกำกับที่อ้าง**_
_ช่องคู่นี้ใช้ field เดียวกับใบกำกับซื้อ (`SupplierInvoiceNumber` /_
_`SupplierTaxInvoiceDate`) แต่ความหมายเปลี่ยนตามชนิดเอกสาร: PI/Expense/PV =_
_ใบกำกับของผู้ขาย · CN/DN ฝั่งซื้อ = **ใบลดหนี้/เพิ่มหนี้ที่ผู้ขายออกให้**._
_ปัญหา: (ก) ตอนเลือกใบต้นทางในฟอร์ม `_applyCnSourceDefaults` ยกเลขที่+วันที่ของ_
_**ใบกำกับที่กำลังลดหนี้ให้** มาเติม → ผู้ใช้เห็นเลขที่ไม่ใช่ของใบนี้ และถ้าบันทึกต่อ_
_รายงานภาษีซื้อจะโชว์เลขใบกำกับแทนเลขใบลดหนี้จริง (คำเตือน §86/10 ก่อนอนุมัติ_
_ก็ถูก "ทำให้ผ่าน" ด้วยเลขที่ผิด) · (ข) ตรงกันข้าม เวลา OCR สแกนใบลดหนี้ของผู้ขาย_
_มาจริง ช่องนี้กลับ **ว่างเสมอ** เพราะ `bookSupplierInvoice` จำกัดไว้ที่_
_PV/PI/Expense เท่านั้น. แก้: (1) เลิกยกเลขที่/วันที่จากใบต้นทางสำหรับ CN/DN_
_(ยกเฉพาะรหัสสาขาผู้ขายที่เป็นค่าเดียวกันเสมอ) — ใบที่อ้างถึงยังผูกอยู่ที่_
_`RelatedDocumentId` ไม่หายไปไหน · (2) เพิ่ม `bookSupplierCreditNote` ใน_
_`OcrService` (+ `hasSupplierCreditNote` ใน `document-scan.html` handoff):_
_CN/DN ฝั่งซื้อที่อ่านเลขได้ → เติม `SupplierInvoiceNumber`/`SupplierTaxInvoiceDate`/_
_`SupplierBranchCode` จากกระดาษ **โดยไม่ผูกกับเงื่อนไข VAT/claimable** (เลขที่และ_
_วันที่ใบลดหนี้เป็นข้อมูลอ้างอิงตาม §86/10 ไม่ใช่เงื่อนไขการเคลม) ·_
_`HasTaxInvoiceReference` ยังเป็นของ PV เท่านั้นเหมือนเดิม._
_รอบ 64: **แผง "📒 รายการบัญชี" บนหน้าเอกสาร — ตรวจสอบ + แก้ผังบัญชีของ JE ตรงนั้น**_
_ผู้ใช้ยังหาปุ่มแก้ผังไม่เจอ เพราะแผงเดิมโชว์แค่ยอดรวมต่อใบ (ไม่เห็นด้วยซ้ำว่าลง_
_ผังอะไรไป) และปุ่ม "เปลี่ยนผัง" ซ่อนอยู่ท้ายบรรทัดสินค้าคนละที่. เพิ่ม:_
_(1) `GET /document/{id}/journal-entries` คืน JE ทุกใบของเอกสารพร้อม **บรรทัด_
_Dr/Cr จริงจาก GL** + `CanAdjust`/`BlockReason` ต่อใบ · แผงกางบรรทัดให้เห็นทั้งหมด_
_พร้อมป้าย 🔒 บนบัญชีคุม · (2) `POST /document/{id}/journal-entries/{jeId}/adjust`_
_รับ **"สถานะปลายทาง"** ของใบสำคัญ (แก้ผัง / เพิ่ม / ลดบรรทัด + เลือกวันที่) แล้ว_
_`AdjustDocumentJournalEntryAsync` คำนวณผลต่างต่อบัญชีแล้วลง **ใบปรับปรุงใหม่**_
_— ใบเดิมไม่ถูกแตะ (audit trail ครบ อ่านคู่กันได้) · (3) ค่าคงที่ 2 ข้อที่ทำให้_
_เปิดให้แก้อิสระได้อย่างปลอดภัย: **ยอดรวมห้ามเปลี่ยน** (ยอดผิด = แก้ที่เอกสาร_
_ไม่ใช่แอบแก้ผ่าน GL) และ **บัญชีคุมห้ามขยับ** (`ReclassifyProtectedCodes` —_
_ภาษีซื้อ-ขาย/ลูกหนี้-เจ้าหนี้/มัดจำ/WHT เพราะ ภ.พ.30 · ภ.ง.ด. · อายุหนี้ อ่านอยู่)_
_UI ล็อกช่องของแถวบัญชีคุมไว้เลย · (4) gate ระดับเอกสารร่วมชุดเดียวกับ reclassify_
_(`ResolveJournalAdjustBlockAsync`: ยกเลิกแล้ว / อยู่ในแบบที่ยื่นแล้ว / ส่ง e-Tax แล้ว)_
_+ งวดของวันที่ใบปรับปรุงต้องเปิด · สิทธิ์ = สิทธิ์ Approve · เขียน AuditLog ผลต่าง_
_ทุกบัญชี · (5) แบนเนอร์/แผงช่วยในหน้าสมุดรายวันชี้มาที่แผงนี้ (ใช้ได้ทุกชนิดเอกสาร_
_ต่างจากปุ่ม "เปลี่ยนผัง" รายบรรทัดที่จำกัดชนิด)._
_รอบ 63: **แปลงเอกสารแล้วยอดขยับ 1 สตางค์** — ใบแจ้งหนี้ VAT 44,942.29 /_
_สุทธิ 667,713.95 แปลงเป็นใบกำกับได้ 44,942.28 / 667,713.94. สาเหตุ: ใบต้นทาง_
_เก็บ VAT ที่ปัด **รายบรรทัด** (Σ round(net×7%)) แต่ `ConvertCoreAsync` สร้าง_
_`DocumentLineRequest` ใหม่จาก qty/ราคา/อัตราแล้วให้ `CreateDocumentAsync` คิดใหม่_
_ผ่าน `ReconcileTaxRounding` ที่กระทบยอด **รายกลุ่มอัตรา** (round(Σฐาน×7%)) —_
_ต่างกันได้ ±สตางค์เสมอเมื่อหลายบรรทัดปัดขึ้นพร้อมกัน. ลูกค้าถือใบแจ้งหนี้อยู่แล้ว_
_ใบกำกับยอดไม่ตรง = เอกสารสองใบของรายการเดียวกันขัดกันเอง (กระทบยอดกับลูกค้า/_
_ตรวจสอบภาษี). แก้: (1) `ConvertCoreAsync` ยก `VatAmountOverride = line.VatAmount`_
_เมื่อยก **ทั้งบรรทัด** (`ComputeLineAmounts` honor ตรง ๆ + `ReconcileTaxRounding`_
_ข้ามบรรทัดที่มี override) ⇒ ใบลูก = ใบแม่เป๊ะ · ยกบางส่วนยังคิดใหม่ (การเฉลี่ย VAT_
_ตามสัดส่วนสร้างเศษของตัวเอง) · ยกเว้นโหมด "ราคารวม VAT + ส่วนลดท้ายบิล" ที่การ_
_back-out ทำให้ net ไม่ตรงอยู่ดี · (2) **ฟอร์มก็ drift แบบเดียวกัน** — เดิมไม่เคย_
_ส่ง/อ่าน `vatAmountOverride` เลย ⇒ เปิดใบเก่าแล้วกดบันทึกเฉย ๆ ยอดขยับเงียบ ๆ_
_(defect class "เก็บแล้วต้อง echo กลับ"). เพิ่ม `_vatBasisKey` (qty|ราคา|ส่วนลด|_
_โหมดส่วนลด|อัตรา|ราคารวมVAT) + `_keptVat`: ผู้ใช้ไม่แตะฐาน → ใช้ VAT ที่บันทึกไว้_
_ทั้งตอนแสดงผลและตอนส่งบันทึก, แตะเมื่อไรกลับไปคิดใหม่ + กระทบยอดตามปกติ ·_
_`calcSum` ข้ามบรรทัด kept ทั้งใน reconcile และการคิด VAT ใหม่หลังส่วนลดท้ายบิล_
_(mirror backend) · (3) ป้ายใต้ยอด VAT บอกตรง ๆ ว่า "ใช้ค่าที่บันทึกไว้เดิม ต่างจาก_
_การคิดจากฐานรวม X บาท" + วิธีให้คิดใหม่ — ไม่แก้ตัวเลขเงียบ ๆ และไม่ปล่อยให้ผู้ใช้_
_ดีดเครื่องคิดเลขแล้วงง._
_รอบ 62: **แก้วันที่กลับบัญชีรายใบจากตารางโดยตรง** — ต่อจากรอบ 60. คำถามผู้ใช้:_
_"1 ใบ ยกเลิกทีเดียวทำไมมี 4 รายการ" → ระบบลงบัญชีตาม **เหตุการณ์** ไม่ใช่ตามใบ_
_(อนุมัติเอกสาร 1 รายการ + รับ/จ่ายเงินอีก 1 รายการ) พอยกเลิกจึงได้ตัวกลับครบทุกตัว_
_= ต้นฉบับ 2 + ตัวกลับ 2. เพิ่มกล่องอธิบายเรื่องนี้ในโมดัล. ฟังก์ชัน: (1) ทุกแถวใน_
_ตารางมี `<input type="date">` ของตัวเอง แก้ทีละใบได้ทันที ไม่ต้องเลือกโหมดก่อน_
_(เลิกใช้ radio perEntry/fixed) + hint ต่อแถวว่า "จะย้าย / วันที่ตรงอยู่แล้ว /_
_ติดงวดปิด" · (2) ปุ่มตั้งทั้งชุด 4 แบบ: **📌 วันที่ใบแรก** (ใบสำคัญต้นฉบับที่_
_เก่าที่สุด ตกกลับวันที่เอกสาร) · วันที่เอกสาร · ตามใบต้นฉบับของแต่ละใบ · วันที่_
_ที่เลือกเอง · (3) API รับ body `RedateVoidReversalRequest{ Entries[] }` —_
_`RedateVoidReversalAsync(..., IReadOnlyList<RedateEntryDate>? entryDates)`_
_ลำดับตัดสิน: ระบุรายใบ → ระบุวันเดียวทั้งชุด → วันที่ใบต้นฉบับ → วันที่เอกสาร ·_
_id ที่ไม่ใช่ตัวกลับของเอกสารนี้ถูกปฏิเสธ (กันใช้เป็นช่องแก้วันที่ JE ใบไหนก็ได้) ·_
_(4) ปุ่มยืนยันบอกจำนวนจริง "ย้ายวันที่ N ใบ" และปิดตัวเองเมื่อไม่มีใบต้องย้าย._
_รอบ 61: **เปิดเปลี่ยนผังบัญชีรายบรรทัดให้ฝั่งขาย** — เดิม `ReclassifyLineAccountAsync`_
_รับเฉพาะ Expense/PurchaseInvoice/PaymentVoucher โดยอ้าง §86/4 ว่าใบกำกับแก้ไม่ได้_
_ซึ่งอ้างผิดมาตรา: §86/4 บังคับ **สิ่งที่พิมพ์บนใบกำกับ** และรหัสผังบัญชีไม่ได้อยู่_
_บนใบกำกับเลย — ย้ายรายได้ 41100 → 41200 ไม่แตะยอดบนใบ ไม่แตะ VAT ไม่แตะ ภ.พ.30_
_ผลของการห้ามคือใบขายที่ลงผังผิด "ไม่มีทางแก้" ต้องยกเลิกใบกำกับทั้งใบ ซึ่งเสี่ยงกว่า._
_แก้: (1) allowedTypes เพิ่ม Invoice/TaxInvoice/Receipt/CreditNote/DebitNote ·_
_(2) **ทิศ JE ต้องกลับด้านที่ลงจริง** — อ่านขา Dr/Cr ของผังเก่าจาก JE ของเอกสาร_
_(GL-first เหมือน drives รอบ 57) แล้วสร้างคู่ตรงข้าม: บรรทัดค่าใช้จ่าย = Dr ใหม่/_
_Cr เก่า · บรรทัดรายได้ = Dr เก่า/Cr ใหม่ (ถ้าใช้สูตรเดียวกันทั้งคู่ รายได้เดิมจะ_
_เพิ่มเป็นสองเท่าและผังใหม่ติดลบ) fallback = ธรรมชาติของ AccountType เมื่อหา JE ไม่เจอ ·_
_(3) เลิกบล็อกด้วย "มีการรับ/จ่ายชำระแล้ว" — JE ชำระเงินแตะ เงินสด ↔ ลูกหนี้/เจ้าหนี้_
_ไม่ใช่ผังของบรรทัดสินค้า (การเปลี่ยนแหล่งเงินยังอยู่ที่ `ReclassifyPaymentSourceAsync`) ·_
_(4) ใบเสร็จหลักฐาน `IsSettlementReceipt` ไม่นับเป็น "เอกสารปลายทาง" (evidence-only_
_ไม่ลง JE) ไม่งั้นใบขายที่เก็บเงินแล้วถูกล็อกทุกใบ · (5) เพิ่ม `ReclassifyProtectedCodes`_
_ห้ามย้ายเข้า/ออกบัญชีคุม (11310 ลูกหนี้, 21210 เจ้าหนี้, 11610/11640 ภาษีซื้อ,_
_21911/21912/21913 ภาษีขาย, 21916-18 WHT, 21510/21520/21610/21711-13 มัดจำ) —_
_**เทียบตรงรหัส ไม่ใช่ prefix** (prefix "215"/"217" จะเผลอล็อก 21511 ค่าไฟฟ้าค้างจ่าย_
_และ 21714 ดอกเบี้ยค้างจ่าย) · (6) ใบมัดจำ (`IsDeposit`) ยังห้าม (มีวงจรของตัวเอง) ·_
_gate เดิมคงอยู่ครบ: งวดปิด / อยู่ในรายงานภาษีที่ยื่นแล้ว / ส่ง e-Tax แล้ว / เอกสาร_
_ปลายทางจริง. UI: `canReclassify` + datalist mirror รายการเดียวกัน._
_รอบ 60: **"แก้วันที่กลับบัญชี" กับเอกสารที่มีตัวกลับหลายใบ** — เอกสารใบเดียว_
_มักมีตัวกลับหลายใบและคนละวัน (ใบซื้อ 1 ก.ค. + ใบจ่ายชำระ 17 ก.ค. → ยกเลิกทีเดียว_
_ได้ตัวกลับ 2 ใบ). เดิม `RedateVoidReversalAsync` ยัดทุกใบไป **วันเดียวกัน**_
_(วันที่เอกสาร) และ UI เป็น `prompt()` ที่ไม่บอกเลยว่าจะไปแตะใบไหนบ้าง._
_แก้: (1) ค่าเริ่มต้นเปลี่ยนเป็น **วันที่ของใบต้นฉบับที่ตัวเองกลับ** ต่อใบ_
_(`ResolveRedateTarget` + `LoadReversalOriginalsAsync` — resolver กลางใช้ร่วมกับ_
_preview, ตกกลับวันที่เอกสารเมื่อหาต้นฉบับไม่เจอ) · ระบุวันที่มาเอง = บังคับทุกใบ_
_ไปวันนั้น · (2) `PreviewVoidReversalRedateAsync` +_
_`GET /document/{id}/redate-void-reversal/preview` คืนตาราง เลขที่ตัวกลับ /_
_ประเภท / ยอด / วันที่ตอนนี้ → วันที่ใหม่ / ใบต้นฉบับที่อ้าง / เหตุผลที่ย้ายไม่ได้_
_(งวดต้นทางหรือปลายทางปิด, วันที่ตรงอยู่แล้ว) — ตรวจงวดครั้งเดียวจากลิสต์_
_FiscalPeriod ไม่ยิงรายบรรทัด · (3) UI เปลี่ยนจาก `prompt()` เป็นโมดัลที่โชว์_
_ตารางก่อนยืนยัน + เลือกโหมด "ตามใบต้นฉบับของแต่ละใบ (แนะนำ)" หรือ "วันเดียวกัน_
_ทุกใบ" · เลือกเฉพาะ `OriginalEntryId != null && Status == Posted` เหมือนเดิม_
_(ใบต้นฉบับไม่ถูกแตะ ⇒ ยอดรวมเท่าเดิม เปลี่ยนแค่งวด)._
_รอบ 59: **ทางแก้ผังบัญชีของ JE ที่ระบบลงจากเอกสาร** + ปุ่มหน้าสมุดรายวันตรงกับ_
_สิทธิ์จริง. ปัญหา: ใบสำคัญที่ผูก `SourceDocumentId` ถูก server บล็อกทั้ง Update /_
_Reverse / Correct / Void / Delete แต่ UI โชว์ปุ่มครบ ⇒ กด 4 ใน 5 ปุ่มแล้ว error_
_และไม่มีปุ่มไหนบอกว่าจริง ๆ ต้องไปแก้ที่ไหน. แก้: (1) แถว/โมดัลของใบที่ผูกเอกสาร_
_เหลือ "ดู" + "📄 เปิดเอกสารต้นทาง" เท่านั้น + ป้าย `จากเอกสาร` + checkbox batch_
_ถูก disable (BatchDelete ฝั่ง server ปฏิเสธทั้งชุด) · (2) banner ในโมดัลบอกทางแก้_
_ตามชนิดเอกสาร: Expense/PI/PV → ปุ่ม **"✏️ เปลี่ยนผัง"** ท้ายบรรทัด_
_(`ReclassifyLineAccountAsync` — ลง JE ย้ายบัญชีในงวดเดิม เอกสารต้นฉบับไม่ถูกแก้)_
_ชนิดอื่น → "ยกเลิกเอกสารแล้วออกใหม่" · (3) `CorrectJournalEntryAsync` เพิ่ม guard_
_`SourceDocumentId` พร้อมข้อความชี้ทางแก้ (เดิมตกไปโดน guard ของ Reverse ที่ตอบ_
_คนละคำถาม) · (4) ลิงก์ "เอกสารต้นทาง" เดิมชี้ `documents.html?search=<เลขที่>`_
_แต่หน้านั้น **ไม่เคยอ่าน `?search=`** ⇒ ตกมาที่ลิสต์เปล่า — เพิ่มการอ่าน `?search=`_
_(ซ่อมลิงก์ที่ตายพร้อมกัน 4 หน้า: journals / general-ledger / bank / wht) และเพิ่ม_
_`?openDoc=<id>` ที่เปิด **หน้ารายละเอียด** เอกสารตรง ๆ (ไม่ใช่ `?editDoc=` ที่เปิด_
_ฟอร์มแก้ไขของใบอนุมัติแล้ว = แก้อะไรไม่ได้) · (5) ปุ่ม reclassify เดิมเป็น "✏️"_
_เปล่าขนาด 10px ไม่มีใครเห็น → ใส่ข้อความ "เปลี่ยนผัง" + สีน้ำเงิน · (6) แผงช่วย_
_"❓ ปุ่มไหนใช้ตอนไหน" บนหน้าสมุดรายวัน (เปิดค้างครั้งแรก) อธิบายความต่าง_
_แก้ไข / กลับ-แก้ / กลับรายการ / ลบ เป็นภาษาคนอ่าน._
_รอบ 58 (audit จำลอง scenario): แก้ **ภ.พ.30 นับ VAT มัดจำซ้ำ** — มัดจำ defer ที่_
_ถูกหักผ่าน drives/apply (DepositAppliedToDocumentId ตั้ง) เคยถูกดึงเข้า ภ.พ.30_
_งวด RecognizedAt ทั้งที่ใบเช็คเอาท์/ใบกำกับปลายทางรายงาน VAT เต็มใบแล้ว → ยอด_
_ขาย/ภาษีขายเกินจริง. แก้: exclude applied deposits จาก deferredRecognized query_
_+ Receipt branch (standalone RealizeDeposit ยังรายงานปกติ). + deferred ตัดสิน_
_**GL-first** (flag หรือขา Cr 21913 จริงใน JE ใบมัดจำ) เหมือน drives d7ee4d3 —_
_มัดจำ integration ที่ flag ไม่ตั้งเคยถูกรายงานเดือนรับเงินทั้งที่ GL พัก 21913._
_รอบ 57: drives — **หลักเดียวทุกเคส** (TakeTime §5): resolve เจอแล้ว → อ่าน "ขา Cr_
_จริง" ของใบมัดจำ/JV แล้วกลับตามนั้น ไม่ assume โหมดจาก field/flag/setting._
_เคส A ยกเครื่องเหมือนเคส B: อ่าน JE ของใบมัดจำ (SourceDocumentId, Posted||Reversed)_
_→ ratio ฐาน/VAT จากขา Cr จริง + Dr กลับ "บัญชีเดิมที่ถูกเครดิต" (ไม่เดาผัง):_
_gross (ไม่มีขา VAT) → Dr 217xx เต็ม / net+21913 → Dr ทั้งคู่ + Cr 21911 เต็ม /_
_net+21911 → Dr 21911 (net). fallback field+flag เฉพาะเมื่อ JE ไม่ผูกใบมัดจำ._
_รอบ 56: drives เคส A (document REC-) + deferred VAT — ขา VAT อ่านจาก **GL จริง**_
_ของใบมัดจำ (GL-first, flag-fallback) เหมือนเคส B: เดิมพึ่ง flag DepositOutputVat_
_Deferred อย่างเดียว → มัดจำที่ Cr 21913 จริงแต่ flag ไม่ตั้ง ถูกเลือก 21911 →_
_Dr net กับ Cr 21911 ของใบเช็คเอาท์ = 21913 ค้างถาวร + ภาษีขายงวดขาด + JE ≠_
_ยอดเอกสาร (เคส REC-20260707-0002). ใหม่: sum(Cr−Dr) บน 21913 ของ JE ที่_
_SourceDocumentId=ใบมัดจำ (Posted||Reversed) ≥ depVat → Dr 21913 + Cr 21911 เต็ม._
_รอบ 55: drives-resolve (เคส B journal) เปลี่ยนจาก link-based เป็น **net-balance**_
_ครอบคลุมทุกกลไก un-reverse — เดิมกรอง `ReversedByEntryId == null` (partner_
_reverse→un-reverse → link ค้างที่ NextAcc → หาไม่เจอ). เปลี่ยนเป็นคำนวณ **net GL_
_จริง**: Σ(Cr−Dr) บนบัญชี deferred ของ **ทั้ง reverse-family** (transitive closure_
_ตาม OriginalEntryId ทุกชั้น) นับ **Status = Posted||Reversed** (ตรงกับ GetGeneral_
_Ledger/TrialBalance — Reversed ยังอยู่ใน ledger; Voided/Draft/ลบ หลุด) →_
_telescope เป็น net เสมอ: reversal-of-reversal(+Cr)/void/delete reversal → net live;_
_reversal ยัง active → net 0 (ตัด). สำคัญ: reverse ตั้ง original.Status=Reversed_
_ถ้ากรองแค่ Posted จะหา original ไม่เจอหลัง reverse. verify: TakeTime reverse ผ่าน_
_integration ProcessJournalReverse → ตั้ง OriginalEntryId + Status=Reversed →_
_closure เห็นครบ. ไม่พึ่ง flag ReversedByEntryId → un-reverse วิธีใดก็ได้._
_รอบ 54: กวาดบั๊ก Include(Contact) INNER JOIN ทั้งระบบ (~40 จุด) + integration_
_invoice รับ `bookingNumber` — helper กลาง `ContactHydration` (Hydrate*ContactsAsync_
_ผูก Contact ที่ soft-delete กลับเข้า nav ด้วย IgnoreQueryFilters). ครอบคลุม ภาษี_
_(ภ.พ.30/36/54, ภ.ง.ด.3, aging, bad-debt, 50 ทวิ), รายงาน (executive/dashboard/_
_cashforecast/reportbuilder), bank reconciliation, PDF/email/etax, portal,_
_revenue-recognition. `InboundInvoiceRequest.BookingNumber` (JSON `bookingNumber`,_
_string) → `Document.BookingNumber` (company endpoint มีอยู่แล้ว)._
_รอบ 53: **ต้นเหตุจริง** หน้าเงินมัดจำโชว์ 0 (ไม่ใช่ cache/deploy) —_
_`GetDepositsAsync` ทำ `.Include(d => d.Contact)` แต่ `Document.Contact` เป็น_
_required (ContactId non-nullable) + `Contact` มี `HasQueryFilter(!IsDeleted)` →_
_EF Core แปลงเป็น **INNER JOIN + filter** → เอกสารมัดจำที่ contact ถูกลบ/ปิด_
_(IsDeleted=true เช่น vendor โรงแรมที่ deactivate) ถูก "ตัดทิ้งเงียบทั้งใบ" →_
_native 16 ใบหายหมด. Diagnostic ไม่มี Include เลยนับครบ (= 2 ตัวเลขขัดกัน). แก้:_
_เลิก .Include, โหลดชื่อ/เลขภาษี contact แยกด้วย IgnoreQueryFilters ลง dictionary_
_แล้ว map ตอน build DepositSummary (contact ที่ถูกลบยังโชว์ชื่อ ไม่ทำใบหาย)._
_รอบ 52: deposit-applied drives-journal รับ journal ref (TakeTime point 2) —_
_เดิม `DepositAppliedRef` resolve ได้แค่ใบมัดจำ (Document) ตาม DocumentNumber →_
_มัดจำที่เป็นสมุดรายวันภายนอก (JV-INT) หาไม่เจอ → throw → integration ต้อง_
_fallback ส่ง JV reverse แยก. เพิ่มเคส B: ไม่พบ Document → resolve เป็น_
_JournalEntry.EntryNumber → กลับ deferred (217xx/21913) จากบรรทัด Cr จริงของ_
_journal → net JE ใบเดียว. guard double-reverse ด้วยคอลัมน์ใหม่_
_JournalEntry.DepositAppliedToDocumentId (void → un-mark). เคส A ไม่แตะ._
_รอบ 51: ที่อยู่ต่างประเทศของ Contact — ฟอร์มผู้ติดต่อเดิมเป็นโครงไทยล้วน_
_(จังหวัด/รหัสไปรษณีย์ required) → vendor/ลูกค้าต่างชาติ (เช่น Booking.com B.V.)_
_กรอกไม่ได้. เพิ่ม checkbox "🌐 ที่อยู่ต่างประเทศ" → สลับเป็น dropdown ประเทศ_
_(ISO alpha-2) + textarea ที่อยู่เต็ม; save เซ็ต `CountryCode`≠TH + `Address`_
_free-text + null โครงไทย. e-Tax `BuildBuyerParty`: guard `isThai` — CountryID≠TH_
_บังคับไปทาง unstructured (LineOne + CountryID ต่างชาติ) ไม่ยัด TISI geo-code_
_ไทยให้ที่อยู่ต่างชาติ. (backend DTO/entity/WHT ม.70 รองรับ CountryCode อยู่แล้ว)_
_รอบ 50: dashboard เงินมัดจำ (`GetDepositsAsync`) — ยังโชว์ 0. ขยายการตรวจจับ_
_เป็น 3 ชั้น: (1) native IsDeposit, (2) GL-detected **ทุก doc type** (เลิกจำกัด_
_แค่ Receipt/RV) ใช้ยอด **Cr สุทธิใน GL** (ΣCr−ΣDr) เป็นฐาน/คงค้าง แทน SubTotal_
_ที่ integration doc อาจไม่ตั้ง (เดิม outstanding=0), (3) doc-less: มัดจำที่เป็น_
_JE ล้วน `SourceDocumentId=null` → รวมเป็น 1 แถวสรุป (Id=Guid.Empty ไม่มีปุ่ม)_
_เพื่อ KPI ไม่ขึ้น 0 ทั้งที่งบดุลมีหนี้สินมัดจำ._
_รอบ 49: dashboard เงินมัดจำ (`GetDepositsAsync`) — เดิมกรอง `IsDeposit=true`_
_อย่างเดียว → พลาดมัดจำที่สร้างผ่าน integration (ลง JE เอง Cr 215xx/217xx ผ่าน_
_mapping DEPOSIT_RECEIVED โดยไม่ set IsDeposit) → หน้าเงินมัดจำโชว์ 0. เพิ่มการ_
_ตรวจจาก GL จริง (เอกสาร Receipt/RV ที่มี JE posted Cr 215xx/217xx ไม่ reverse)_
_union กับ native → สะท้อนความจริงทางบัญชี ไม่พึ่งแค่ธง._
_รอบ 48: e-Tax PDF/A-3 — เปลี่ยนวิธีฝัง XML จาก hand-rolled injector (2 xref,_
_XMP ซ้อน → strict parser/สรรพากรหา XML ไม่เจอ = "XML หาย") → **QuestPDF native_
_`DocumentOperation.AddAttachment()` + `ExtendMetadata()`** (qpdf single-pass,_
_xref เดียว, XMP เดียว, /AF ถูก — เหมือน iTextSharp ที่ TakeTime ใช้)._
_`AttachEtaxXmlNative` (temp file + fallback injector ถ้า native ล้ม),_
_`BuildEtdaXmpExtension` (rsm schema สำหรับ ExtendMetadata). ใช้ทั้ง_
_BuildEtaxPdfA3WithEmbeddedXml + GenerateDocumentPdfAsync._
_รอบ 47: e-Tax PDF/A-3 — แก้บั๊ก /Size ผิด (trailer /Size = maxObj+1 แต่ add_
_object เลข maxObj+1..+4 → embedded XML objects นอกช่วง → สรรพากร "ประมวลผล_
_เอกสารแนบไม่ได้"). แก้เป็น newOffsets.Keys.Max()+1. นี่คือสาเหตุหลักที่ RD reject._
_รอบ 46: e-Tax PDF/A-3 — แก้ compliance ให้ผ่าน validator: (1) trailer เพิ่ม /ID_
_(incremental update คง file id เดิม — PDF/A บังคับ), (2) XMP เพิ่ม field มาตรฐาน_
_ครบ (dc:title/creator/description, pdf:Producer/Keywords, xmp:CreatorTool/Create_
_Date/ModifyDate) ตรงกับ Info dict + วันที่ capture ครั้งเดียว, (3) FindMaxObj_
_fallback สแกน object header กัน XML ไม่ถูกฝังเงียบ. + DocumentEmailService:_
_ส่ง e-Tax by Email ถ้าสร้าง PDF/A-3 ไม่ได้ → **fail loud** (เดิม swallow ส่ง_
_อีเมลเปล่าไม่มีเอกสารตามกฎหมาย). หมายเหตุ: วิธี robust สุดคือใช้ PDF/A library_
_(ETDA reference ใช้ iTextSharp) — ปัจจุบัน QuestPDF(A-2b)+injector ยังเปราะ._
_รอบ 45: บันทึกชำระเงิน → ออก "ใบเสร็จรับเงิน" หลักฐานอัตโนมัติ (default เปิด_
_ฝั่งขาย Invoice/TaxInvoice/DebitNote). `Document.IsSettlementReceipt=true` +_
_`SettlementPaymentId`, `Payment.ReceiptDocumentId`. ใบนี้ **evidence-only**:_
_Payment ลง Dr เงินสด/Cr ลูกหนี้ + ตัด AR แล้ว → ใบเสร็จ **ไม่ลง JE ซ้ำ ไม่ตัด_
_หนี้ซ้ำ ไม่คิด VAT ซ้ำ** (VAT อยู่ที่ใบกำกับ, VatAmount=0) สร้างตรงเป็น Status=_
_Paid ไม่ผ่าน ApproveDocumentAsync. void payment → void ใบเสร็จตาม. เลิกใช้_
_convert Invoice→Receipt เป็นทางตัดหนี้ (กันเบิ้ล). `CreateSettlementReceiptAsync`._
_รอบ 44: หัวเอกสาร downgrade ตาม `Buyer864Incomplete` จริง (ไม่ใช่แค่ flag) —_
_ข้อมูล §86/4 ผู้ซื้อไม่ครบ = ห้ามขึ้น "ใบกำกับภาษี". + ส่วนลดท้ายบิล (จากยอด_
_รวม): `Document.BillDiscountPercent/Amount` — `ComputeLineAmounts(extraDiscount)`_
_เฉลี่ย pro-rata (ex-VAT) ลงบรรทัด → VAT/WHT รายบรรทัดถูกต้องแม้ mixed-rate._
_SubTotal = หลังหักท้ายบิล (คง invariant Σ line.Amount); PDF แสดง "ยอดรวมก่อน_
_VAT" = SubTotal+BillDiscount + บรรทัด "ส่วนลดท้ายบิล". `AllocateBillDiscount`._
_รอบ 16 (audit ยอดเบิ้ล/double-count + concurrency): supersede block,_
_deposit-apply settlement JE, settlement receipt กันนับซ้ำในรายงานรายได้,_
_POS tip fix, POS refund discountFactor, Integration idempotency (expense/PV_
_+ ExternalId fallback), payment/JE FOR UPDATE ใน tx (create+void), payroll_
_void row-lock, recurring FOR UPDATE SKIP LOCKED, Employee.LineId migration._
_รอบ 16: DBD XBRL annual export (TFRS-NPAEs taxonomy) + ผู้ทำบัญชี CPD gate_
_(พ.ร.บ.การบัญชี ม.7), PDPA Wave 3 UI tabs (DSR/RoPA/Consent/Breach with 72h timer)._

## รายการที่ผ่านมาเรียงตามรอบ

| รอบ | Theme | Key items |
| --- | --- | --- |
| 6 | e-Tax XML ครบสุด | line ChargeAmount ถอด VAT, TaxBasis แก้, PDF format |
| 7 | Multi-currency มัดจำ + audit | FX guard, hash chain weekly verifier, recurring template validate, §65 ตรี(4) YTD, §82/5(6) override |
| 8 | Option-1 reclassify + CMS sync | line GL reclassify-JE, PV Cash auto-approve all channels, CMS ConfirmPaymentAsync 6-step, PrePayment booking IsDeposit 217xx |
| 9 | CMS gap close + perf | payment-gateway webhook, POS Z/X-Report, Stock unify, OverdueDunningJob, Dashboard alerts, e-Tax retry, §82/3 LateReason, dup-doc detect, 11 indexes |
| 10 | Notification consolidate | NotificationContext.RecipientUserId, ApprovalService migrate, PiiMask helper, FX bank scope note |
| 11 | PDPA + DSR + builder ครบสุด | EncryptedColumnConverter (AES-256-GCM Employee CitizenId/TaxId/Passport), PiiMask + permission Pii.View ใน PayrollController, SubscriptionService migrate 4/5 → NotificationEngine, DSR endpoints /access /portability /rectify /erase (legal_hold), Multi-warehouse StockAdjustmentRequest WarehouseId/LotNumber, ProductLot verified, JournalEntryBuilder fluent abstraction |
| 12 | JE migrate + business gaps ปิด | JE Builder phase 2 (ReclassifyLine + FxRevaluation refactor), UnifiedPaymentQueryService cross-domain (AR+AP+POS+CMS), POS deposit IsDeposit+DepositRealizedAt, TipPayoutService §50 ทวิ (3% WHT >1000), RecurringLateFeeAccrualJob (rate/grace/cap config), DocumentLineDeliveryService LINE flex, Budget scenarios best/base/worst |
_Files referenced are accurate; if behavior diverges, this doc is wrong —_
_update it in the same PR (CLAUDE.md §"DOCUMENT_FLOW.md" hard requirement)._
