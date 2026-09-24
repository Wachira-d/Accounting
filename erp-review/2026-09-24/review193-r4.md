# รอบ 193 — ฝ่ายค้านรอบสี่ (รอบสุดท้าย): ตรวจงานแก้หลังฝ่ายค้านรอบสาม

> ขอบเขต: W `b371d4c` · V `41bb2c8` · C3 `85dfda8` · S2 `3da2760` · O1 `132c2b5` · L2 `27ed475`+`04ce362` (merge `af6f89b`) บน
> `claude/erp-system-review-team-660mev` HEAD `66e3dc2` · อ่านอย่างเดียว · ไม่มี .NET SDK ⇒ **ยังไม่ได้คอมไพล์** · ไม่ได้รัน
> `check_all.sh` (รัน checker ทีละตัว — ผลในหมวด D) · ทุกข้อใน A เปิดไฟล์ตรงจุดแล้ว

## สรุปสั้น

- **CONFIRMED 4 ข้อ** (P1 1 · P2 3) — ไม่มี P0
  - **R4-1 (P1 · O1)** ทางเข้าอื่นของ R3-3: ข้อความ 422 แนะนำให้ "กดอนุมัติที่หน้าเอกสาร" แต่เส้นนั้นไม่ตรวจ hash เลย ⇒ แก้เนื้อหาแล้วกดอนุมัติ
    ลายเซ็นลูกค้าเดิม**ผ่านด่านอนุมัติหลายขั้น**และ**พิมพ์บน PDF** ของเนื้อหาใหม่
  - **R4-2 (P2 · O1)** hash ไม่ครอบข้อความที่ลูกค้าเห็นบนใบเสนอราคา (`CustomTermsAndConditions` · `CustomAppendix` · `CustomFooterNotes` ·
    `DocumentDate` · `BankAccountId` · `DocumentLanguage`) ⇒ แก้เงื่อนไขพิเศษแล้วเรียกซ้ำ = ใช้ลายเซ็นเดิมได้
  - **R4-3 (P2 · L2)** migration ย้ายค่า `BillDiscountAmount → DepositBaseDeducted` **รันทุกครั้งที่บูต** และจับแถวที่โค้ดใหม่สร้างได้ ⇒
    ส่วนลดการค้าของใบที่อ้างเลขใบมัดจำแต่ไม่หักฐานมัดจำ ถูกย้ายไปเป็น "ฐานมัดจำ" แล้วถูกรับรู้เป็นมัดจำตอนอนุมัติ (R3-1 กลับมา) ·
    และ UPDATE ไม่กันตัวเองเมื่อสองเครื่องบูตพร้อมกัน ⇒ ส่วนลดถูกหักสองครั้ง
  - **R4-4 (P2 · C3)** `NameMatchKind` ตัดคำบอกรูปนิติบุคคลทิ้งทั้งหมด ⇒ "บจก. เอ" = "บริษัท เอ จำกัด (มหาชน)" = "หจก. เอ" = "เอ"
    (บุคคล) เป็น `ExactName` ⇒ เลขภาษีของนิติบุคคลหนึ่งถูกเติมลงแถวของอีกนิติบุคคลที่ชื่อแกนเดียวกัน
- **ความเสี่ยงคอมไพล์: ไม่พบจุดที่ต้องแก้** (checker 20 ตัวเขียว + ตรวจมือชนิดที่ checker มองไม่เห็น — หมวด D)
- คำถามโจทย์ 1–6 ตอบครบในหมวด A/C

---

## A. CONFIRMED

### ✅ de5dc4cd R4-1 (P1 · O1) ลายเซ็นลูกค้าเดิมยังอนุมัติเนื้อหาที่แก้แล้วได้ ผ่านเส้น "อนุมัติที่หน้าเอกสาร" ที่ข้อความ 422 แนะนำเอง

- `SignatureApprovalService.cs:550-553` (ข้อความ 422): *"แก้ตามข้อความแล้วเรียกซ้ำ **หรือกด "อนุมัติ" ที่หน้าเอกสาร**"*
- เส้นหน้าเอกสาร `DocumentService.ApproveDocumentAsync` — ด่านอนุมัติหลายขั้น `DocumentService.cs:5171-5185` นับ `DocumentApproval`
  ทุกแถวที่ `!IsDeleted` และ `Status == Approved` ว่า "เซ็นครบ" **โดยไม่ดู `SignedContentHash`** · ลายเซ็นเดิมถูก "แทนที่" (soft-delete)
  **เฉพาะ**ใน `ExternalApproveQuotationAsync` (`SignatureApprovalService.cs:449-461`) — `UpdateDocumentAsync` ไม่แตะ `DocumentApprovals` เลย (grep 0 จุด)
- PDF ช่องลายเซ็นลูกค้า `PdfGenerationService.cs:1055-1061` อ่าน `DocumentApprovals` ที่มี `SignatureData` (ตัวกรอง `!IsDeleted` จาก
  `AccountingDbContext.cs:2354`) ⇒ แถวเดิมที่ไม่ถูกแทนที่ = พิมพ์บนใบ
- ลำดับจริง: ลูกค้าเซ็นใบเสนอราคา 100,000 → อนุมัติไม่ผ่าน 422 → ผู้ใช้แก้ราคาเป็น 120,000 → ทำตามทางเลือกที่สองของข้อความ (กดอนุมัติที่หน้า)
  ⇒ ด่าน `RequireApprovalForDocuments` ผ่านด้วยขั้นลูกค้าเดิม · ใบ 120,000 ถูกอนุมัติ และ PDF มีลายเซ็นลูกค้าที่ให้กับ 100,000 — **ผลเดียวกับ R3-3**
  ผ่านทางเข้าที่สอง (R5 "ทางเข้าอื่นไม่เดินด่าน")
- แนวแก้: ตัวตรวจ "ลายเซ็นลูกค้าที่ยังมีผล" ตัวเดียว (hash เท่า `DocumentSignedContent.Hash` ปัจจุบัน) ใช้ทั้ง (ก) ด่านเซ็นครบใน
  `ApproveDocumentAsync` (ข) PDF slot 2 (ค) `GetDocumentSignaturesAsync` — หรือแทนที่ลายเซ็นลูกค้าตอน `UpdateDocumentAsync` เมื่อ hash เปลี่ยน ·
  และแก้ข้อความ 422 ให้บอกว่าถ้าแก้เนื้อหาแล้วต้องเซ็นใหม่ **ทั้งสองทาง**

### ✅ de5dc4cd R4-2 (P2 · O1) hash เนื้อหาที่ลูกค้าเซ็นไม่ครอบข้อความ/ช่องที่ลูกค้าเห็นหลายช่อง

- `Helpers/DocumentSignedContent.cs:27-52` ครอบ ผู้ซื้อ (ContactId) · สกุลเงิน · ส่วนลด · ยอดหัวทุกช่อง · DueDate · CreditDays · PaymentTerms · Notes · บรรทัด
- **ไม่ครอบ** แต่พิมพ์บนใบเสนอราคาและแก้ได้บนร่าง (`DocumentService.cs:2342` ฯลฯ):
  - `CustomTermsAndConditions` — พิมพ์ `PdfGenerationService.cs:2307-2308` / `.DocumentRenderer.cs:1105-1109` (เงื่อนไขรับประกัน/ค่าปรับ = เนื้อหาสัญญา)
  - `CustomAppendix` — `PdfGenerationService.cs:2257` / `.DocumentRenderer.cs:1035` (ขอบเขตงาน)
  - `CustomFooterNotes` · `DocumentDate` · `BankAccountId` (บัญชีที่ให้ลูกค้าโอน) · `DocumentLanguage` · `DepositAppliedRef` (ป้าย "หักมูลค่ามัดจำตามใบกำกับ {เลข}")
- ผล: 422 → แก้ "รับประกัน 1 ปี" เป็น "ไม่รับประกัน" (ราคา/บรรทัดเดิม) → เรียกซ้ำด้วยลายเซ็นเดิม ⇒ `CanReuseSignature` = true ⇒ อนุมัติด้วยลายเซ็นเดิม
- หมายเหตุ: `DepositBaseDeducted` ของ L2 (เพิ่มหลัง O1 เขียน hash) **ครอบโดยอ้อม** — เปลี่ยนค่านี้ต้องคิดบรรทัดใหม่ ⇒ `SubTotal`/`VatAmount` เปลี่ยน ·
  สลับ "ส่วนลด 100 ↔ ฐานมัดจำ 100" ก็ติด `BillDiscountAmount` ⇒ ไม่รั่ว (หมวด C)
- แนวแก้: เพิ่มช่องข้อความข้างบนใน `Hash` + เปลี่ยน `Version` เป็น `v2` (hash เก่าไม่เท่า ⇒ ขอเซ็นใหม่ = ทิศปลอดภัย ตามที่คลาสออกแบบไว้แล้ว)
  + เทสต์ทิศตรงข้าม (แก้ผังบัญชี/โปรเจกต์ยังใช้ซ้ำได้)

### ✅ c3820dce R4-3 (P2 · L2) migration ย้ายค่าเดิมไม่ใช่ครั้งเดียว — จับแถวที่โค้ดใหม่สร้าง และหักซ้ำเมื่อบูตพร้อมกัน

`DatabaseMigrationHelper.cs:6651-6682` อยู่ในรายการ `ApplyMissingColumns` ซึ่ง `Program.cs:1998` รัน**ทุกครั้งที่บูต** (ไม่มี marker "รันแล้ว")

(ก) **ส่วนลดการค้าของแถวใหม่ถูกย้ายเป็นฐานมัดจำ** — เงื่อนไขแถวที่ย้าย: `DepositBaseDeducted = 0` · `BillDiscountAmount > 0` · `DepositAppliedRef`
ชี้ใบมัดจำออกใบกำกับแล้ว · ยังไม่อนุมัติ + `DepositAppliedAmount <= 0` ⇒ **ย้ายทั้งก้อน** · โค้ดใหม่สร้างรูปนี้ได้:
- `UpdateDocumentRequest.DepositBaseDeducted = 0` (DTO เขียนว่า "0 = ล้าง" — `DocumentDtos.cs:315-317`) ขณะคงเลขใบมัดจำไว้ ⇒
  `TaxedDepositDeductionProblem(0, …)` คืน null ทันที (`DepositPolicyResolver.cs:260`) ⇒ บันทึกได้
- `POST documents` (API ตรง/คีย์) ส่ง `depositAppliedRef` + `billDiscountAmount` โดยไม่มี `depositAppliedAmount`/`depositBaseDeducted`
- ตัวเลข: ร่างขาย 7,450 ส่วนลดการค้า 100 อ้าง "DEP-001" (ล้างฐานมัดจำแล้ว) → รีสตาร์ต → `BillDiscountAmount 0 · DepositBaseDeducted 100` →
  อนุมัติ `RealizeTaxedDepositDeductionsAsync` รับรู้มัดจำ 100 จาก DEP-001 · กระดาษพิมพ์ "หักมูลค่ามัดจำตามใบกำกับ DEP-001 (100.00)" แทน "ส่วนลด" —
  **R3-1 กลับมาแบบเงียบ** (ลูกค้าเสียมัดจำ 107 · รายได้เกิน 100)

(ข) **สองเครื่องบูตพร้อมกัน = หักสองครั้ง** — `UPDATE … SET "BillDiscountAmount" = t."BillDiscountAmount" - c.mv … WHERE t."Id" = c."Id" AND c.mv > 0`
ไม่มีเงื่อนไขบนแถวเป้าหมาย (`t."DepositBaseDeducted" = 0`) · ไม่มี advisory lock รอบ `ApplyMissingColumns` (grep `pg_advisory` ใน `Program.cs`/
`DatabaseMigrationHelper.cs` = 0 · กฎ #4 D) ⇒ ธุรกรรมที่สองรอล็อกแถว แล้ว PostgreSQL ประเมินแถวใหม่ด้วยเงื่อนไข `t.Id = c.Id` ซึ่งยังจริง ⇒
`BillDiscountAmount = (BDA − mv) − mv` (ติดลบได้) · `DepositBaseDeducted = mv` (ไม่ซ้ำ) ⇒ ยอดก่อนหักท้ายบิลบนกระดาษผิด

แนวแก้: (1) ย้ายค่าเดิมแบบครั้งเดียว — จำกัด `d."CreatedAt" <` เวลาคอมมิตนี้ หรือ marker ใน `__DataMigrations` · (2) เติม `AND t."DepositBaseDeducted" = 0`
ใน WHERE ของ UPDATE (EvalPlanQual จะข้ามแถวที่เครื่องแรกย้ายแล้ว) · (3) ถ้าตั้งใจให้ "อ้างเลขมัดจำ + ส่วนลดอย่างเดียว" เป็นรูปที่ไม่ถูกต้อง ให้
`TaxedDepositDeductionProblem` ปฏิเสธตอนบันทึกแทนการให้ migration ตีความ

### ✅ cb552889 R4-4 (P2 · C3) `NameMatchKind` ถือ "บจก." · "บมจ." · "หจก." · ไม่มีคำนำหน้า เป็นชื่อเดียวกัน

- `ContactTaxBranchKey.cs:340-355` — `NameMarkersTh` ลบ "ห้างหุ้นส่วนจำกัด/บริษัท/จำกัด/มหาชน/บมจ/บจก/หจก…" และ regex อังกฤษลบ
  `co|ltd|company|limited|plc|public|inc|corp|partnership` ⇒ รูปนิติบุคคลหายจากการเทียบทั้งหมด
- ผล: `NameMatchKind("บจก. เอ", "บริษัท เอ จำกัด (มหาชน)")` = `ExactName` · ("หจก. เอ", "บริษัท เอ จำกัด") = `ExactName` · ("สมชาย ใจดี", "บริษัท สมชาย ใจดี จำกัด")
  = `ExactName` · ("ABC Co., Ltd.", "ABC Public Company Limited") = `ExactName` — คนละนิติบุคคลตามกฎหมาย (บจก./บมจ./หจก./บุคคล คือคนละผู้เสียภาษี)
- เส้นที่ใช้ผลนี้เติมเลข: API v1 `DocumentsV1Controller.cs:354-367` (จับด้วย `CounterpartyNameMatcher` แบบคล้าย แล้วยกระดับเป็น Exact ผ่าน `NameMatchKind`)
  · นำเข้าเอกสาร `ImportExportService.cs:1618-1624` (substring แล้ว `NameMatchKind`) ⇒ `AdoptTaxId` = `Adopted` ⇒ เลขภาษีของ "บจก. เอ" ติดแถว
  "บริษัท เอ จำกัด (มหาชน)" ถาวร · ใบกำกับของ บจก. ทุกใบหลังจากนั้นพิมพ์ชื่อ บมจ. (§86/4 ผู้ซื้อผิดตัว — คลาสเดียวกับ R3-2)
- ขอบเขต: ต้องมีแถวชื่อแกนเดียวกันที่**ยังไม่มีเลข** (ชุด `SoftScope` = `RowsWithoutTaxId`) — เกิดได้ในกลุ่มบริษัท/บริษัทแม่-ลูก/ผู้ประกอบการที่มีทั้ง
  ร้านบุคคลและนิติบุคคลชื่อเดียวกัน
- เทสต์ที่ล็อกพฤติกรรมนี้ไว้เป็น "ถูก": `ContactTaxBranchKeyTests.cs:445-452` (`"บริษัท เรดิสัน จำกัด"` = `"เรดิสัน"`)
- แนวแก้: normalize ช่องว่าง/ตัวพิมพ์/จุด/รูปย่อ ↔ รูปเต็ม **ของรูปนิติบุคคลเดียวกัน** (บจก. ↔ บริษัท … จำกัด · บมจ. ↔ บริษัท … จำกัด (มหาชน) · หจก. ↔
  ห้างหุ้นส่วนจำกัด) แต่คง "ชนิด" ไว้ในคีย์ (`CanonicalName` คืน `(ชนิด, แกน)` แล้วเทียบทั้งคู่) · ฝั่งไม่มีคำนำหน้าเลย = ไม่รู้ชนิด ⇒ Fuzzy ·
  เพิ่มเทสต์ทิศตรงข้าม "บจก. เอ" ≠ "บมจ. เอ"

---

## B. PLAUSIBLE

| # | ทีม | เรื่อง | ที่ | ระดับ |
|---|---|---|---|---|
| ✅ c3820dce P4-1 | L2 | ร่าง/รออนุมัติที่สร้าง**ก่อนแยกช่อง**และมีทั้งส่วนลดการค้า + ฐานมัดจำใน `BillDiscountAmount` ถูกย้าย "ทั้งก้อน" ⇒ อนุมัติแล้วรับรู้ส่วนลดเป็นมัดจำ (R3-1 เดิม) หรือล้ม "ขาด X" · migration แยกไม่ได้ก็จริง แต่ควร**ประทับธงบนใบ** (InternalNotes) ให้ผู้ใช้ตรวจก่อนอนุมัติ ไม่ใช่ย้ายเงียบ · ใบที่อนุมัติไปแล้วด้วยโค้ดรุ่น R3-1 (รับรู้ทั้งก้อน) ถูกย้ายทั้งก้อนตาม JE ⇒ กระดาษตรงบัญชีแต่ความเสียหาย (มัดจำลูกค้าหาย 107) ไม่มีรายการให้ตาม (F2 ข้อ 9) | `DatabaseMigrationHelper.cs:6662-6668` | P2 ถ้าโค้ดรอบ 193 เคย deploy · ไม่งั้น P3 |
| P4-2 | L2 | ใบที่**อนุมัติแล้ว**รูป "`BillDiscountAmount` = ฐานมัดจำ + เลขใบมัดจำ + `DepositAppliedAmount = 0`" ที่รับรู้มัดจำจากหน้าเงินมัดจำ (JE ไม่มี `DepositRealizedForDocumentId`) ⇒ `mv = LEAST(BDA, 0) = 0` ไม่ย้าย ⇒ **พิมพ์ซ้ำแล้วป้ายเปลี่ยน** จาก "หักมูลค่ามัดจำตามใบกำกับ X" เป็น "ส่วนลดท้ายบิล" บนใบกำกับที่ออกไปแล้ว + โคลนพ่วงเป็นส่วนลด (`DocumentCloneController.cs:69,174`) · หน้าต่างเวลาเกิด: `d788c2a` → `198fb5c` (วันเดียวกัน) | `DatabaseMigrationHelper.cs:6662` · renderer ×2 | P3 (เฉพาะถ้าเคย deploy ช่วงนั้น) |
| ✅ c3820dce P4-3 | L2 | ด่าน "ส่วนลด + ฐานมัดจำเกินยอดขาย" บอกทางไปต่อ "หักมัดจำให้น้อยลง" (`DocumentService.cs:660-664`) แต่ฟอร์มไม่มีช่อง/ปุ่มแก้ `DepositBaseDeducted` (มีแค่แสดงที่ `documents.html:9682`) — ทีมรู้ว่าไม่มีปุ่มล้าง (r193-L2 §11) แต่ข้อความยังสั่งสิ่งที่หน้าเว็บทำไม่ได้ (F2 ข้อ 7 "ข้อความที่ระบุทางไปต่อต้องทำได้จริง") | `DocumentService.cs:662` | P3 |
| ✅ c3820dce P4-4 | L2 | `TaxedDepositDeductionProblem`/`RealizeTaxedDepositDeductionsAsync` ไม่จำกัดชนิดเอกสาร ⇒ API ตรงตั้ง `depositBaseDeducted` + เลขใบมัดจำ**ขาย**บนใบซื้อ (`PurchaseInvoice` อยู่ใน `PostingTypes`) ⇒ อนุมัติใบซื้อแล้วรับรู้มัดจำขายเป็นรายได้ · ควรจำกัดเป็นชนิดขาย (ชุดเดียวกับที่ P0-1 ใช้กับธงขับ JE) | `DepositPolicyResolver.cs:256-267` · `DocumentService.cs:13856` | P3 |
| ✅ cb552889 P4-5 | C3 | เลข 13 หลักที่ checksum ไม่ผ่าน: แถวที่จับได้ด้วยอีเมล/ชื่อ ⇒ `Keep` (ใช้แถว**โดยไม่เติมเลข** เงียบ) แต่ถ้าจับไม่ได้ ⇒ สร้างแถวใหม่**พร้อมเลขที่ checksum ผิด** (ทางสร้างใหม่ไม่ผ่าน `IsUsableTaxId`) — สองผลของ input เดียวกัน และทางแรกออกใบกำกับให้ผู้ซื้อไม่มีเลขโดยไม่มีธง (R2-C5 ในทรงใหม่) | `ContactTaxBranchKey.cs:303-308,322-327` · ที่พัก `LodgingService.Reservations.cs:457-472` · Integration `:564` | P3 |
| ✅ 960e98cd P4-6 | W | tamper ที่**ประทับ RowHash ใหม่ด้วย** (สูตร v2 อยู่ในโค้ด ไม่มีกุญแจ) ⇒ แถวที่แก้ตรวจเนื้อผ่าน แต่แถวลูกกลายเป็น Dangling ⇒ ข้อความ `AlertMessage` บอก "แถวก่อนหน้าหายไป (ถูกลบ)" ทั้งที่ถูก**แก้** — ยังแจ้ง (ไม่เงียบ) แต่ระบุสาเหตุผิด (F2 ข้อ 7) · ทีมเขียนข้อจำกัด "ไม่มีกุญแจ" ไว้แล้ว (W2-P3) — ขอแค่ปรับถ้อยคำ "ถูกลบ **หรือถูกแก้แล้วประทับใหม่**" | `AuditHashChain.cs:165` | P3 |
| P4-7 | W | `generate-pdf`/`generate-html` ของเอกสารลับยังไม่เดินด่านชั้นความลับ (ทีมจดไว้เป็น backlog "W2-P6 ส่วนที่เหลือ" ใน r193-W.md:234 — ไม่ใช่ข้อใหม่ ใส่ไว้เพื่อให้ main เห็นว่าอีเมลปิดแล้วแต่ PDF ตรงยังเปิด: `DocumentTemplateController.cs:88-103` · `PdfGenerationService.cs:42-47` ไม่มี `CanViewAsync`) | — | backlog เดิม |

---

## C. ตรวจแล้วไม่มีปัญหา (ห้ามรายงานซ้ำ)

**L2 (โจทย์ 1)**
- `AllocateBillDeductions` เมื่อไม่มีฐานมัดจำ: `SplitBillDeduction(depositBase<=0)` คืน `(alloc.Sum(), 0, true)` = `BillDiscountAmount = billAlloc.Sum()` เดิมทุกสตางค์ ·
  โหมด % อย่างเดียวผ่านตามเดิม · เทียบ `AllocateBillDiscount` (`DocumentService.cs:673-729`): Σ alloc = `round(trade + base)` เสมอเว้นแต่ถูก cap ที่ `totalBase`
  (⇒ `Ok=false` ล้มดัง) · เดินเลขเทสต์ `TaxedDepositDeductionTests` ด้วยมือ: 4,993.46/349.54/5,343.00 · 4,862.62/340.38/5,203.00 · 696.26 ✓
- ผู้อ่าน `BillDiscountAmount` ทั้งเรพ (ไม่รวม worktrees): PDF ×2 ใช้ `BillDeductionTotal` ตัวเดียวทั้งยอดก่อนหักและ scale บรรทัด + สองแถวแยก ✓ · หน้ารายละเอียด ✓ ·
  clone (ส่วนลดตาม · มัดจำไม่ตาม) ✓ · convert (สัดส่วนเดียวกัน + เลขใบมัดจำ) ✓ · recurring อ่านแค่ `billDiscountAmount` (มัดจำไม่พ่วง = ถูก) ✓ ·
  POS (`PosService.Orders.cs:775`) ความหมายส่วนลดอยู่แล้ว ✓ · **e-Tax XML / ภ.พ.30 / รายงานภาษีขาย / API v1 / export / OCR ไม่อ่านช่องนี้เลย** (ใช้ `SubTotal`/`VatAmount`
  ซึ่งหลังหักทั้งสองอย่างแล้ว) ✓ · `document-scan.html:4483` ส่ง 0 ✓
- migration ใบที่**อนุมัติแล้ว**: ย้ายเท่า JE ที่ผูก `DepositRealizedForDocumentId = ใบนี้` (ผู้เขียนช่องนี้มีที่เดียว `DocumentService.cs:3516` ผ่าน
  `FinalInvoiceId` — `ApplyDepositToInvoiceAsync` ไม่ประทับ) ⇒ ใบอนุมัติที่มีส่วนลดการค้าจริง + หักมัดจำ VAT พักแบบยอดรวมไม่ถูกย้าย ✓ · `TotalDebit` ของ JE รับรู้ =
  ฐาน (มัดจำ taxed ⇒ `vatMove = 0`) ✓ · คอลัมน์ที่ UPDATE อ้างถูกเพิ่มก่อนหน้าในรายการ (`:955-964`, `:1435-1438`, `:4844-4845`) ✓ · `Status` เก็บเป็น int ✓
- `RealizeTaxedDepositDeductionsAsync`: ใบลูกจาก convert นับ JE ของใบแม่ (`RelatedDocumentId`) ⇒ ไม่รับรู้ซ้ำ · convert บางส่วนหลายใบ ⇒ `DBD − already` ติดลบ ⇒
  `AllocateBaseDeduction` ใช้ `Max(0, …)` ⇒ ไม่มีบรรทัด ✓ · ใบแทน (`ReplacesDocumentId` ผ่าน `ConvertCoreAsync`) ไม่ AutoPost และถ้า post ก็นับใบแม่ ✓ ·
  ไม่มีมัดจำ ⇒ ล้มดังพร้อมทางไปต่อ (เดิม return เงียบ) ✓ · ล็อก `FOR UPDATE` อยู่ในธุรกรรมอนุมัติ ✓
- ที่พัก: ใบสุดท้ายส่ง `DepositBaseDeducted` · ทำเช็คเอาต์ต่ออ่านช่องใหม่ · `docType` เป็น `Invoice` เฉพาะ VAT 0 ซึ่งไม่มีมัดจำ taxed ✓ · B5 ของรอบสามปิดไปด้วย ✓
- Integration 6 เมธอดกรอง Voided ในคิวรี ✓ · ตาข่าย void: void ก่อนเขียนหมายเหตุ · ล้มแล้ว `ChangeTracker.Clear()` — `IntegrationSyncLog` ยังไม่ถูก track จนถึง
  `SaveSyncLog` (`:3218-3238` Add ตอนบันทึก) ⇒ log ไม่หาย ✓ · ยิงซ้ำเจอป้าย ⇒ ล้มดัง ✓

**C3 (โจทย์ 2)**
- `ImportDocumentAsync` โยน `KeyNotFoundException` **ก่อน** `_db.Documents.Add` (`ImportExportService.cs:1629-1647`) ⇒ ไม่มีหัวเอกสารครึ่งใบค้างใน context ·
  `ImportAsync` จับ exception **ต่อแถว** (`:44-107`) แล้วบันทึก `ImportError(i + 1, …, ex.Message)` ⇒ **ไฟล์ทั้งชุดไม่ล้ม** · ข้อความมีชื่อในไฟล์ ชื่อที่จับได้ และเลขภาษี ✓
  (ใบหลายแถว: ทุกแถวของเลขเอกสารนั้นล้มด้วยข้อความเดียวกัน — สอดคล้อง)
- ผู้เรียก `AdoptTaxId` 10 จุดส่ง `ContactMatchKind` ครบ · 3-อาร์กิวเมนต์เหลือ 0 (grep ทั้ง `Accounting` + `Accounting.Tests`) · named `branchCode: null` ตามด้วย positional
  อยู่ตำแหน่งที่ถูก (C# 7.2 non-trailing named) ✓ · ที่พักตัดแถว walk-in จากชุดผู้สมัคร ✓

**O1 (โจทย์ 3)**
- `DepositBaseDeducted` ถูกครอบโดยอ้อมผ่าน `SubTotal`/`VatAmount`/`BillDiscountAmount` (ดู R4-2) ✓ · ภาษา/ส่วนลดรายบรรทัด/หน่วย/VAT rate รายบรรทัด **อยู่ใน** `LineKey` ✓
  (ภาษาเอกสารไม่อยู่ — รวมใน R4-2)
- B9: ล็อก `FOR UPDATE` + `ReloadAsync` บน `doc` ที่ track อยู่ (โหลดด้วย `FirstOrDefaultAsync` ไม่ใช่ `AsNoTracking`) ✓ · `strategy.ExecuteAsync(Func<Task>)` ✓ ·
  PDF อ่านเฉพาะแถว `!IsDeleted` (query filter `AccountingDbContext.cs:2354`) ⇒ ลายเซ็นที่ถูกแทนที่ไม่ขึ้นกระดาษ ✓

**W (โจทย์ 4)**
- `Analyze` แถว v1 + v2 ปนกัน: แถว v2 แรกชี้ `PrevHash` = RowHash ของ v1 (ไม่มีป้าย) ซึ่งอยู่ใน `known` ⇒ ไม่ Dangling · `VerifyRow` แยกตามป้าย `v2:` ✓ · ผู้เขียน chain
  ต่อบริษัทมาตั้งแต่รุ่นแรก (`git show 0750e6a` — `Where(a => a.CompanyId == grp.Key)`) ⇒ แถวเก่าไม่ชี้ข้ามบริษัท ✓ · `AuditLog.Id` เป็น `long` ⇒ tip ตาม Id ถูก ✓ ·
  trigger `audit_log_no_delete` กันการลบ ⇒ ไม่มี purge ที่ทำให้ Dangling หลอก ✓ · endpoint ตัดแถวด้วย `OrderBy(Id).Take(maxRows)` (แถวเก่าสุด) ⇒ การตัดไม่ทำให้ Dangling ✓
- แตกกิ่งไม่แจ้งลูกค้า (`HasIntegrityFindings` ไม่นับ fork · job แค่ `LogWarning`) · ถูกแก้/ขาดตอนยังแจ้งพร้อมรายการทุกแถว ✓
- `[RequireOwner]` เป็น `IAsyncAuthorizationFilter` ตั้ง `ctx.Result` ทุกทางปฏิเสธ (ไม่มี companyId ⇒ 500 · ไม่มี DB ⇒ 503 · ไม่ใช่เจ้าของ/คีย์ ⇒ 403) ✓ ·
  route ของ PaymentSettings/Sensitivity มี `{companyId:guid}` ✓ · Owner/SystemAdmin (บริษัท) และ platform SystemAdmin ผ่าน ⇒ หน้า `payment-settings.html` ของเจ้าของใช้ได้ ·
  GET list/providers ไม่ถูกด่าน ⇒ หน้าเปิดได้ทุกบทบาท · ผู้ไม่ใช่เจ้าของกดบันทึก ⇒ toast ข้อความ 403 (`payment-settings.html:211,237`) ไม่เงียบ ✓
  (ข้อเสนอ P3: ล็อกปุ่มสำหรับผู้ไม่ใช่เจ้าของก่อนกด)
- `SubscriptionController.RequireOwnerAsync` → `Task<ActionResult?>` รับ `ObjectResult?` จาก `DenyAsync` ✓ · อีเมลเอกสารลับ ✓ · JS ลำดับ `_seSeq` ถูก (await อยู่ระหว่างสองบรรทัด) ✓

**V (โจทย์ 5)**
- `SplitLine`: `explicitVat` ถูกตรวจ**ก่อน**ด่านอัตรา ≤ 0 ⇒ ใบที่ส่งยอด VAT มาเองยังชนะ · 7% ไม่ผ่านเส้นใหม่ · ราคารวม VAT + อัตรา 0 ได้ `net = amount` เท่าสูตรเดิม ·
  ฟังก์ชันเดียวใช้ทั้งฝั่งขายและซื้อ ✓ · อัตรา −1 ไม่ได้ VAT ติดลบอีก ✓
- `ManualInputVatLineRule`: ทุกข้อความที่ regex เดิม (`7601891`) ปิดเคลมยังปิด ยกเว้นบริบทในรายการตัดออก · ไม่พบคำตัดออกที่กลืนค่ารับรองจริงในรูปที่พบบ่อย
  (ข้อสังเกต P3: สำนวน "เลี้ยงดูปูเสื่อลูกค้า" ถูกตัดด้วย "เลี้ยงดู")

**S2**
- query แรกตัดสแกนพี่น้องที่ผูกเอกสาร/JE ที่ query + เรียง `CreatedAt, Id` · query ไฟล์ตัดแถวที่ยังมีสแกนชี้ + เรียงวันที่ถอด · ลบไม่สำเร็จ ⇒ ประทับ `UpdatedAt` ใหม่ =
  เลื่อนหนึ่งระยะผ่อน (ไม่ค้างหัวคิว) · `UpdateTimestamps()` ก็ประทับค่าเดียวกันอยู่แล้ว ✓

---

## D. ความเสี่ยงคอมไพล์ (โจทย์ 6)

**checker (รันทีละตัวบน `66e3dc2`) — เขียวทั้งหมด**: `service_interface_check` 0 · `undeclared_local_check` 0 · `record_arg_check` 0 · `using_check` 0 ·
`di_cycle_check` ✅ · `nullable_arg_check` ✅ · `accessibility_check` 0 · `arg_type_check` 0 · `tuple_name_merge_check` 0 · `namespace_shadow_check` 0 ·
`identifier_space_check` 0 · `verbatim_string_check` 0 · `string_quote_close_check` 0 · `dto_nullable_contract_check` 0 · `required_call_site_check` 115 กติกา + negative 12 ✅ ·
`owner_action_wiring_check` 64 จุด ✅ · `write_permission_gate_check` ✅ · `contact_taxid_only_match_check` 5 = baseline · `approved_status_writer_check` 5 = baseline ·
`dead_helper_check` ✅ (แจ้งซ้ำ: `OcrPartyResolver.OurTaxIdOnPaper` / `PiiMask.BankAccountNo` ตัดออกจาก baseline ได้) · `test_inventory --check` ✅ ·
`node --check` ทุก `<script>` ของ `documents.html` 0 ผิด · brace สมดุล 8 ไฟล์ .cs หลักที่แตะ

**ตรวจมือ (ชนิดที่ checker มองไม่เห็น) — ไม่พบจุดที่ต้องแก้**
1. **CS0103 ตัวแปรที่ถูกลบ**: `ConvertTaxedDrivesAsync` ถอดพารามิเตอร์ `billDiscountAmount` — ผู้เรียก 2 จุด (`:1076`, `:2194`) ส่ง 5 อาร์กิวเมนต์ตรงลายเซ็นใหม่ · ตัวเมธอดไม่อ้างชื่อเดิม ·
   `billAlloc.Sum()`/`updBillAlloc.Sum()` เดิมถูกแทนด้วยตัวแปรจาก deconstruction · `alreadySigned`/`customerApproval` นอก block = 0 จุด
2. **CS1061 สมาชิก tuple/anonymous**: `PreviewTotals` คืน 5-tuple — ผู้เรียกทุกตัว (ที่พัก 2 · เทสต์ 9) ใช้ `var t`/`.Net`/`.Total`/`.TradeDiscount`/`.DepositBase` ไม่มีใคร
   deconstruct 3 ตัว · `convCreate.DepositBase`/`convUpdate.DepositBase` ตรงชื่อ tuple ใหม่ · projection `new { d.DocumentNumber, d.Reference }` ใช้แค่สองสมาชิกนั้น ·
   ที่พัก `candidates` เป็น `List<(Contact Row, ContactMatchKind Kind)>` — `AddRange` ของ `(x, …Email)` แปลงชื่อ tuple ได้ (identity conversion) · `pick.Row`/`pick.Kind` ✓ ·
   `case Reject: default:` สองป้ายในส่วนเดียว ✓
3. **enum/record ข้ามไฟล์**: `ContactMatchKind` (TaxKey/ExternalKey/ExactName/Email/Phone/FuzzyName) และ `ContactAdoptOutcome` อยู่ namespace `Accounting.Helpers` —
   ผู้ใช้ `Helpers.X` ใน V1 controller ผูกถูก (namespace_shadow 0) · `AuditChainVerifyResult` เพิ่มพารามิเตอร์ท้ายพร้อมค่าเริ่มต้น — ผู้สร้างมีที่เดียว (`AuditTrailService.cs:116`) ส่ง 11 ค่า
   ตรงลำดับ · `FirstBrokenIndex`/`ComputeRowHash` ที่ถูกลบ: ผู้เรียกเหลือ 0 (โค้ด + เทสต์) · `AuditHashChain.ChainAnalysis` เป็น nested record ใน static class (อนุญาต)
4. **`[Obsolete] AdoptTaxId` 3 อาร์กิวเมนต์ถูกลบ**: ผู้เรียกเหลือ 0 · **`BillDeductionIsTaxedDeposit` ถูกลบ**: ผู้เรียกเหลือ 0 (เหลือแค่ในคอมเมนต์ doc)
5. `RequireOwnerAttribute.DenyAsync` switch expression (`null` / `ObjectResult?` / `ObjectResult`) มีชนิดร่วม `ObjectResult?` ✓ · `ApiResponse<object>` (`Models/DTOs/Common.cs:3`) ·
   `JwtHelper`/`OwnerActionGuard` อยู่ `Accounting.Helpers` ที่ `using` แล้ว ✓ · attribute args เป็นค่าคงที่ ✓
6. `DocumentSignedContent`: `Document.ContactId` เป็น `Guid` (ไม่ nullable) ⇒ `.ToString("D")` คอมไพล์ได้ · `DocumentLine.IsDeleted` จาก `BaseEntity` ✓
7. เทสต์ใหม่อ้างสมาชิกที่มีจริงทุกตัว (`DepositPolicyResolver` 23 ชื่อ · `AuditHashChain` 6 · `OwnerGateDecision` 5 · `DocumentSignedContent` 3) · `AllocateBillDeductions`/`VerifyRow` เป็น
   `internal` + `InternalsVisibleTo Accounting.Tests` (`Accounting.csproj:82`) ✓
8. `IntegrationService` ชื่อ local ใหม่ (`voidedId`, `voidedNo`, `stuck`, `stuckMsg`, `exVoid`) ไม่ชนชื่อในสโคปครอบ (CS0136) ใน `ProcessInvoiceAsync` ✓

---

## E. คำถามถึง main agent / เจ้าของ

1. R4-1: ลายเซ็นลูกค้าที่เนื้อหาเปลี่ยนแล้ว — แทนที่ทันทีตอนแก้เอกสาร (ง่าย ทุกเส้นเห็นผลเดียวกัน) หรือให้ทุกผู้อ่านเช็ค hash เอง (ต้องมีตัวกลางตัวเดียว)
2. R4-3: ข้อมูลรอบ 193 เคย deploy ขึ้นเครื่องจริงหรือยัง — ถ้ายัง ลบคำสั่งย้ายค่าออกได้ทั้งก้อน (ไม่มีแถวให้ย้าย) แทนการทำให้เป็น one-shot
3. R4-4: ต้องการให้ "ชื่อไม่มีคำนำหน้า" (เช่นป้ายร้าน) จับกับนิติบุคคลเพื่อเติมเลขได้ไหม — ถ้าไม่ แนวแก้คือ Fuzzy เมื่อฝั่งหนึ่งไม่มีคำบอกรูปนิติบุคคล
