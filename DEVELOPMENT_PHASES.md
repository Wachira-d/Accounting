# DEVELOPMENT_PHASES.md — แผนพัฒนาต่อ (handoff สำหรับ AI agent / ทีมถัดไป)

> สร้างจาก **full-system audit** (2026-07-31): ทีมตรวจ 8 โดเมนอ่านโค้ดจริงทั้งระบบ
> พบ ~46 ประเด็น → **แก้ครบแล้ว 45 ข้อ** บน branch `claude/fix-errors-638kW`
> (เฟส 1-3 ปิดหมดในรอบเดียวกัน) → **ค้างงานจริง 1 ข้อ + งานโครงสร้าง (เฟส 4-5)**
>
> **ค้างข้อเดียวที่เป็นบั๊ก**: XAdES-BES ของ e-Tax — ต้องทดสอบกับ ETDA validator
> จริงก่อน จึงไม่แก้ในรอบนี้ (ดู Phase 2)
> ใช้คู่กับ `TEST_PLAN.md` (รหัสเคสทดสอบ) และ `DOCUMENT_FLOW.md` (behavior)
>
> **กติกาสำหรับผู้ทำต่อ**: ทุกข้อมี file:line + root cause + แนวแก้ ที่ตรวจสอบ
> กับโค้ดจริงแล้ว ณ วันที่เขียน — แต่ **ต้องเปิดอ่านโค้ดปัจจุบันก่อนแก้เสมอ**
> (บรรทัดอาจเลื่อนจาก commit ใหม่) และห้ามข้าม kill-switch/compliance checklist
> ใน CLAUDE.md. env agent ไม่มี dotnet SDK — เช็ค brace balance + ให้ผู้ใช้ build

---

## สิ่งที่แก้ไปแล้ว (อย่าแก้ซ้ำ — regression test คือหน้าที่เฟส 4)

> เพิ่มเติมรอบนี้: **ฟีเจอร์ภาษาเอกสาร (ไทย/อังกฤษ)** — `Pdf/DocumentLabels.cs`
> + `ResolveDocumentLanguage` + `CompanySettings.DocumentLanguage` +
> `Document.DocumentLanguage` + UI ตั้งค่า. มีเทสต์ครอบ 10 เคส
> (`DocumentLabelsTests`) และ import heuristics 18 เคส (`ImportReviewHeuristicsTests`)

| โดเมน | สรุป | commit |
| --- | --- | --- |
| Contact identity | OCR name-match guard, merge เลขภาษีต่างกัน block, ContactIdentity audit | `af6abb7`, `f6a7583` |
| e-Tax | BasisAmount/PDF หักส่วนลดซ้ำ, buyer TaxId pad ปลอม→throw, +07:00, ชื่อเอกสาร PDF=XML | `f19010d` |
| WHT | ภงด.3/53 แยกผู้ถูกหัก (DetectJuristic) + กรองฝั่งซื้อ + ภงด.1 ไม่ดึงจากเอกสาร, ไฟล์ยื่นกรอง SUMMARY/IsExcluded | `3480d38` |
| ภ.พ.30 | claimedElsewhere ยกเว้นงวดเดียวกัน (CSV ยื่นว่าง), Sale.csv กรอง IsExcluded, ปี พ.ศ. ต่อแถว | `3480d38` |
| Security | CMS cart tenant scope ([AllowAnonymous]), POS ProductId ข้าม tenant, payroll ตก includeSalary, AI Disabled ชนะ override | `3480d38` |
| XSS | bank/documents/tax/contacts.html → Layout.esc | `3480d38` |
| JE/Asset | restore→re-approve ไม่ post JE (guard นับ reversal), FX reval เฉพาะ monetary types, ค่าเสื่อมสินทรัพย์ไม่ผูก GL หยุด mutate, rounding squeeze เฉพาะ FX | `9a5cab0` |
| Import | พ.ศ.→ค.ศ. ทั้ง 14 จุด, JE จับคู่ใน ChangeTracker + dedup, bank txn dedup + ไม่เดินยอดซ้ำ | `9a5cab0` |

---

## ✅ Phase 1 — เสร็จแล้ว (commit ชุด "เฟส 1")

ทั้ง 5 ข้อแก้แล้ว: multi-doc payment void (เพิ่ม `ReverseMultiDocPaymentInternalAsync`
+ block การยกเลิกใบเดียวในกลุ่ม), RealizeDeposit หักส่วนที่คืนแล้ว, bank
reconciliation ตรวจยอดจริงจาก DB (`ResolveItemAmountAsync`), UpdateDocument
บล็อกการแก้ใบที่ restore แล้วถือเลขจริง, ApplyDepositToInvoice ห่อ transaction +
`FOR UPDATE` เรียงตาม Id กัน deadlock.
**ยังไม่มี regression test** (ต้องใช้ Testcontainers — ดู Phase 4)

<details><summary>รายละเอียดเดิม (เก็บไว้อ้างอิง)</summary>

### Phase 1 — เงินหาย/บัญชีผิด (แก้แล้ว)

### 1.1 Void เอกสารที่ชำระด้วย multi-doc payment ไม่คืนเงินธนาคาร ⚠️ CRITICAL
- **ที่**: `DocumentService.cs` `VoidDocumentAsync` (~4201) + `ReversePaymentInternalAsync` (~5468)
- **Root cause**: `CreateMultiDocPaymentAsync` ตั้ง `Payment.DocumentId = ใบแรก` ใบอื่นผูกผ่าน
  `PaymentAllocation`; void ใบที่ไม่ใช่ใบแรก → หา payment ไม่เจอ (query ด้วย `p.DocumentId ==`)
  → ข้าม reverse → `BankAccount.CurrentBalance` ค้างสูงถาวร. ซ้ำ: JE ของ multi-doc ใช้
  `Reference = "{PaymentNumber}/{index}"` แต่ reverse หาแบบ exact → throw/หักยอดผิด
- **แนวแก้**: (1) หา payment เพิ่มผ่าน `PaymentAllocations.Any(a => a.DocumentId == documentId)`
  (2) reverse เฉพาะ **ยอด allocation ของใบนั้น** ไม่ใช่ทั้งเช็ค (3) JE matching:
  `Reference == pn || Reference.StartsWith(pn + "/")` (4) ตัดสินใจ: void ใบเดียวใน
  กลุ่ม = ปลด allocation นั้น + ปรับ `Payment.Amount` หรือบังคับ void payment ทั้งใบก่อน
  (แนะนำอย่างหลัง — ง่ายและตรงบัญชีกว่า: throw บอกให้ยกเลิกใบรับ/จ่ายเงินก่อน)
- **Acceptance**: DOC-I-04 + เคสใหม่ "void ใบที่ 2 ของ multi-doc payment" — ยอดธนาคาร,
  PaidAmount, JE กลับครบ; ไม่มี allocation ชี้ใบ Voided

### 1.2 RealizeDeposit ไม่หักส่วนที่คืนแล้ว ⚠️ CRITICAL
- **ที่**: `DocumentService.cs` `RealizeDepositAsync` (~1920)
- **Root cause**: outstanding = `SubTotal - DepositRealizedAmount` ไม่หัก
  `DepositRefundedAmount` (Refund/Apply หักแล้ว แต่ Realize ลืม) → มัดจำที่คืนครบแล้ว
  ยัง realize รายได้+ภาษีขายได้ → 217xx ติดลบ, ภ.พ.30 เกิน
- **แนวแก้**: ใช้สูตรเดียวกับ `ApplyDepositToInvoiceAsync` (~2587) ที่หัก refundedBase
  แล้ว — refactor เป็น helper เดียว `ComputeDepositOutstanding(doc)` ใช้ทั้ง 3 ที่
- **Acceptance**: DOC-I-09 + เคส "รับมัดจำ→คืนครบ→พยายาม realize" ต้อง block

### 1.3 Bank reconciliation group เชื่อยอดจาก client ⚠️ CRITICAL
- **ที่**: `BankService.Reconciliation.cs` `CreateReconciliationGroupAsync` (88-127)
- **Root cause**: `AllocatedAmount` ทั้งฝั่ง bank และฝั่งเอกสารมาจาก request ตรง ๆ
  แล้วเช็ค balanced กับตัวเอง = ปลอมได้; `TotalMatchedAmount` ลง DB เป็นค่าปลอมถาวร
- **แนวแก้**: ดึงยอดจริงจาก DB ต่อ item (Payment.Amount / JE line / Document.BalanceDue /
  BankTransaction.Amount แบบเดียวกับ `ValidateMatchAmountAsync` ใน BankService.cs:401)
  — `AllocatedAmount` จาก client ใช้ได้เฉพาะ ≤ ยอดจริง (partial) ห้ามเกิน
- **Acceptance**: BNK-U-01/BNK-I-01 + เคส "ส่ง AllocatedAmount ปลอม" → 400

### 1.4 Restore แล้วแก้เอกสารที่ถือเลขจริงได้ (แก้ย้อนหลัง §86/4)
- **ที่**: `DocumentService.cs` `RestoreVoidedDocumentAsync` (~4647) + `UpdateDocumentAsync` (~1373)
- **Root cause**: restore → Draft (คงเลขจริง) → `UpdateDocumentAsync` guard เดียวคือ
  `!= Draft` → แก้ยอด/วันที่/บรรทัดแล้ว re-approve เลขเดิม = แก้ใบที่ออกเลขแล้ว
- **แนวแก้**: ใน `UpdateDocumentAsync` ถ้า `!DocumentNumber.StartsWith("DRAFT-")`
  (= restored draft) อนุญาตแก้เฉพาะ field ไม่กระทบเงิน/ภาษี/คู่ค้า/วันที่
  (Notes/Reference/footer) — field อื่น throw บอกให้ "ยกเลิกถาวรแล้วออกใบใหม่"
- **Acceptance**: DOC-I-05/06 + เคสใหม่ "restore แล้วแก้ยอด" → block

### 1.5 ApplyDepositToInvoice ไม่มี transaction/lock (กดซ้ำ = ตัดมัดจำซ้ำ)
- **ที่**: `DocumentService.cs` `ApplyDepositToInvoiceAsync` (~2541)
- **แนวแก้**: ห่อ execution strategy + transaction + `SELECT ... FOR UPDATE` แถวมัดจำ
  และ invoice ก่อนอ่านยอด (copy pattern จาก `RefundDepositAsync` ~2415 ที่แก้แล้ว)
- **Acceptance**: เคส concurrency 2 requests พร้อมกัน — สำเร็จ 1 ล้มเหลว 1

</details>

## ✅ Phase 2 — เสร็จแล้ว ยกเว้น XAdES-BES

แก้แล้ว: void ติ๊กบรรทัดรายงานทุกแบบ + `RecalcWhtTotals`, 50 ทวิ ตามงวดจ่าย
(`SourcePaymentId` + pro-rate + idempotency ต่องวด), ภ.พ.30 โครงยอด 3 จุด
(InputVat ไม่รวม CF, PDF แยก 0%/ยกเว้นด้วย IncomeTypeCode, ฐาน = `VatableBase`),
ค่าเสื่อม/ตีราคาเช็คงวดปิด + `FiscalPeriodId`

> ⚠️ **ค้างข้อเดียว: XAdES-BES (2.1 เดิม)** — ลายเซ็น e-Tax ยังเป็น XMLDSig เปล่า
> ต้อง implement `QualifyingProperties`/`SignedProperties` + Reference ชี้
> SignedProperties **และทดสอบกับ ETDA validator จริงก่อน production**
> (env นี้ทดสอบไม่ได้ จึงไม่แตะ — เดารูปแบบแล้วปล่อยขึ้น production อันตรายกว่า
> การคงสถานะเดิมที่รู้ตัวว่ายังไม่ผ่าน)

<details><summary>รายละเอียดเดิม (เก็บไว้อ้างอิง)</summary>

### Phase 2 — compliance (แก้แล้ว ยกเว้น 2.1)

### 2.1 XAdES-BES สำหรับ e-Tax (ตอนนี้เป็น XMLDSig เปล่า — RD reject ทุกใบ)
- **ที่**: `EtaxInvoiceService.cs` `SignXmlWithCertificate` (~363-402)
- **Spec**: เพิ่ม `ds:Object/xades:QualifyingProperties@Target=#SigId` →
  `SignedProperties` (Id, `SigningTime`, `SigningCertificate` = SHA-256 digest +
  IssuerSerial ของ cert) + `Reference` ที่ `Type="http://uri.etsi.org/01903#SignedProperties"`
  ชี้ `#<SignedPropsId>` เพิ่มใน SignedInfo. namespace `http://uri.etsi.org/01903/v1.3.2#`
- **สำคัญ**: ต้องทดสอบกับ ETDA validator / ตัวอย่าง reference จริงก่อน production —
  ห้าม merge แบบยังไม่เคยรัน. เก็บ `CertificateSerialNumber`, `SignedAt` ลง entity ด้วย
- **Acceptance**: ETX-U-01 + validate ผ่านเครื่องมือ ETDA

### 2.2 VoidDocument ไม่ติ๊ก IsExcluded ให้บรรทัดรายงาน WHT (เฉพาะ VAT)
- **ที่**: `DocumentService.cs` ~4524-4551 (`l.TaxReport.TaxType == TaxType.VAT`)
- **แนวแก้**: ขยายเงื่อนไขครอบ WithholdingTax1/3/53/54 ในงวดที่ยังไม่ Filed +
  recalc `TotalIncome`/`TotalTaxWithheld` ของ report นั้น (ดู pattern recalc ฝั่ง VAT)
- **Acceptance**: WHT-I-02/03

### 2.3 50ทวิ ตอนชำระบางส่วนออกเต็มใบ (cash basis ผิด)
- **ที่**: `DocumentService.cs` ~7286-7341 (pro-rata ถูกแล้ว) vs
  `WithholdingTaxCertService.AutoGenerateFromDocumentAsync` (386-477) ที่ออกเต็ม + guard กันออกซ้ำ
- **แนวแก้**: ส่ง `paymentWht`/`paymentId` เข้า AutoGenerate → cert ต่อ "งวดจ่าย"
  (ยอดตามงวด, วันที่ = วันจ่าย) + guard เปลี่ยนจาก "มี cert แล้ว" เป็น "มี cert ของ
  payment นี้แล้ว"; แบบยื่นเดือนอิงยอด cert ไม่ใช่ยอดเอกสาร
- **Acceptance**: WHT-I-01 + เคสจ่าย 40/60 สองเดือน — cert 2 ใบ ยอดตามจ่ายจริง

### 2.4 ภ.พ.30: InputVat รวมเครดิตยกมา + แบบสรุปนับ CF เป็นยอดขาย + ฐานปนบรรทัดยกเว้น
- **ที่**: `TaxService.cs:1156` (InputVat + carryforward), `PdfGenerationService.TaxReport.cs:175`
  (VAT_CREDIT_CF ตกใน exempt + 0% ใช้ TaxRate แยกไม่ได้), `TaxService.cs:418,493,781`
  (`IncomeAmount = doc.SubTotal` ทั้งใบรวมบรรทัดยกเว้น)
- **แนวแก้**: (1) InputVat = ภาษีซื้อของงวดล้วน (สูตรเดียวกับ `RecalcVatTotals` ~2041),
  CF แสดงช่องของตัวเอง (2) กรอง `VAT_CREDIT_CF` ออกจาก salesLines และแยก 0%/exempt
  ด้วย IncomeTypeCode ไม่ใช่ TaxRate (3) ฐานบรรทัดรายงาน = `Σ line.Amount where VatRate > 0`
- **Acceptance**: TAX-I-04, TAX-S-01 — reconcile ทุกช่องกับคำนวณมือ

### 2.5 ค่าเสื่อมไม่เช็คงวดปิด + ไม่ตั้ง FiscalPeriodId
- **ที่**: `FixedAssetService.cs` ~702-727 (+ `RevalueAsync` ~766)
- **แนวแก้**: ก่อน post — resolve FiscalPeriod ของ (Year,Month): ถ้า Closed/Locked
  → throw; ตั้ง `FiscalPeriodId` ลง JE (copy pattern `AutoPostToJournalAsync` ~10711)
- **Acceptance**: JE-I-02 ครอบ depreciation path

</details>

## ✅ Phase 3 — เสร็จแล้วทั้ง 4 ข้อ

fingerprint ตรงกันเมื่อเปิด PII strip, `ImportReviewHeuristics` เป็น local path
ของ ImportDataReview (kill-switch ผ่าน), OCR เก็บ/ส่งออกสาขา+ที่อยู่ และใช้สาขา
บนใบก่อน Contact, `DailyCallCap` นับเฉพาะ call ที่ยิง provider จริง

<details><summary>รายละเอียดเดิม (เก็บไว้อ้างอิง)</summary>

### Phase 3 — AI mandate (แก้แล้ว)

### 3.1 Fingerprint mismatch: student ไม่มีวันจำได้เมื่อเปิด PII strip
- **ที่**: `GenericFeedbackDistillationModel.cs` 84-108/232-272 vs `AiOrchestrator.cs` 250/261
- **Root cause**: บันทึก feedback ด้วย sanitized JSON แต่ predict ด้วย JSON ดิบ →
  hash ไม่ตรง → Tier-1 exact-memory ตาย 7 features → ไม่เคย short-circuit 0.85
- **แนวแก้**: sanitize input **ก่อน** fingerprint ทั้งสองทาง (เรียก `AiPromptSanitizer`
  ตัวเดียวกันใน `PredictAsync`) หรือเก็บ fingerprint ของ raw ไว้ในคอลัมน์แยกตอนบันทึก
- **Acceptance**: AI-I-02 — user แก้ 1 ครั้ง สแกนใบเดิมซ้ำ student ตอบคำแก้ทันที

### 3.2 ImportDataReview ไม่มี student (ปิด AI = feature ตาย 0%)
- **ที่**: `ImportAiAugmenter.cs:113-133`, `Program.cs:381-397`
- **แนวแก้**: เขียน rule-based local model (duplicate = exact key match, วันที่อนาคต/
  ติดลบ = quality flag, TaxId 13 หลัก checksum) register เป็น `ILocalDistillationModel`
  ของ featureKey 27 — mandate ข้อ 2 + cold-start ข้อ 3
- **Acceptance**: OCR-S-02 pattern: ปิด provider แล้ว import review ยังให้ผล

### 3.3 OCR ไม่เก็บ VendorBranchCode/Address ลง scan result (สาขาบนใบหาย)
- **ที่**: `OcrService.cs` 3400-3413/3953-4001, entity `Intelligence.cs:115-235`, DTO `OcrDtos.cs`
- **แนวแก้**: เพิ่มคอลัมน์ `VendorBranchCode`,`VendorAddress`,`BuyerBranchCode`,`BuyerAddress`
  ลง `OcrScanResult` (ผ่าน `DatabaseMigrationHelper.ApplyMissingColumns` — ห้าม EF migration)
  + DTO + review UI + `CreateDocumentFromScan` ใช้ค่าจากใบก่อน fallback Contact
- **Acceptance**: OCR-I-02 + เคสผู้ขายหลายสาขา

### 3.4 DailyCallCap นับ call ที่ไม่ได้ยิง provider
- **ที่**: `AiBudgetGuard.cs:73-90`
- **แนวแก้**: กรอง `Status` เฉพาะที่ยิง provider จริง (Success/ProviderError ฯลฯ —
  ไม่นับ Skipped/Cached/local) — ยิ่ง local เก่ง cap ต้องยิ่งเหลือ ไม่ใช่ยิ่งหมด
- **Acceptance**: AI-U-03

</details>

## Phase 4 — โครงสร้างพื้นฐานคุณภาพ (งานหลักที่เหลือ)

1. **Testcontainers PostgreSQL harness** + เขียนเทสต์ P0 ทั้งหมดใน `TEST_PLAN.md`
   (TAX-I-04/05, JE-U-01, WHT-U-02/03, DOC-I-01/02, SEC-I-01, SEC-U-01) —
   ทุกบั๊กที่แก้ในรอบนี้ต้องมี regression test ประกบ
2. **CI**: GitHub Actions build + test ทุก PR (ตอนนี้ agent เช็คได้แค่ brace balance)
3. **Negative stock policy**: ตัดสินใจ business rule (block หรือ allow+alert) แล้ว implement
   ให้ `CompanySettings.AllowNegativeStock` มีผลจริงใน approve path
   (`DocumentService.cs` ~8952-9081 — ตอนนี้ COGS=0 เงียบ ๆ เมื่อขายก่อนซื้อ)
4. **Audit hash chain ordering**: insert ใช้ `OrderByDescending(Id)` แต่ verify ใช้
   `OrderBy(Timestamp)` → false alarm — เปลี่ยน verify ให้เดินตาม Id (`AuditTrailService.cs:108`
   vs `AccountingDbContext.cs:3187`)
5. **ภงด.1 จาก payroll**: ต่อ `GenerateEFilingAsync("PND.1")` เข้า `ExportPnd1Async`
   (PayrollDetail) แทน report ว่างที่ generator คืนตอนนี้ (หลังแก้ `3480d38`)
6. **JE fallback ในรายงาน WHT**: บรรทัดจาก manual JE (ไม่มีเอกสาร/contact) ยังใส่ทุกแบบ
   3/53 — ต้องให้ผู้ใช้ระบุประเภทผู้ถูกหักตอนบันทึก JE หรือตัดออกจากแบบอัตโนมัติ

7. **แยกบรรทัดรายงานภาษีต่ออัตรา** — ใบที่ผสม 7% กับ 0% (§80/1) ยังรวมเป็น
   บรรทัดเดียวที่อัตราสูงสุด (`TaxService.VatableBase` มีหมายเหตุไว้). ควรแยก
   บรรทัดต่ออัตราเพื่อให้คอลัมน์ 7%/0%/ยกเว้น ตรงเป๊ะทุกเคส
8. **ภาษาเอกสาร — ส่วนที่ยังไม่ครอบ**: `PdfGenerationService.WhtCert.cs` และ
   รายงานภาษี (`PdfGenerationService.TaxReport.cs`) ตั้งใจคงไทย (ฟอร์มราชการ);
   ถ้าต้องการ "ใบแนบภาษาอังกฤษ" สำหรับผู้บริหารต่างชาติ ให้ทำเป็นเอกสารแยก
   ไม่ใช่แปลฟอร์มยื่น. อีเมล/ชื่อไฟล์แนบยังเป็นไทยล้วน — แปลได้ถ้าต้องการ

## Phase 5 — ตามแผนเดิม (ROADMAP.md/DEVELOPMENT_PLAN.md)

หลัง Phase 1-4 เสถียร: caching/performance, mobile, BI dashboard, XBRL DBD,
e-Withholding, Peak/TakeTime integration hardening ฯลฯ — ดู `ROADMAP.md`

---

## วิธีทำงานที่พิสูจน์แล้วในรอบนี้ (สำหรับ agent ถัดไป)

- **Workflow tool ใช้ไม่ได้ใน env นี้** — permission handler ตัด parameter ของทุก tool
  ใน workflow subagent (ล้มเหลว 2 รอบ เผา ~870k tokens) → ใช้ **Agent tool ธรรมดา**
  fan-out แทน แล้วผู้ประสานงานตรวจทาน finding เองก่อนแก้ทุกข้อ
- ทุก finding จาก subagent ต้อง **เปิดโค้ดจุดจริงยืนยันเอง** ก่อนแก้ — รอบนี้ทีม
  รายงานแม่น แต่หน้าที่ verify คือของผู้แก้
- แก้เป็น batch เล็ก commit บ่อย (e-Tax → critical → ชุดสอง) — ถ้า build พังผู้ใช้
  ชี้ commit ได้ทันที
- อย่าลืม: แก้ flow เอกสาร = อัปเดต `DOCUMENT_FLOW.md` คอมมิตเดียวกัน (กฎ CLAUDE.md)

---
Last updated: 2026-07-31 — commit จะระบุใน git log (Fable 5 audit session)
