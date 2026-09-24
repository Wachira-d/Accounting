# รอบ 193 — ฝ่ายค้านรอบสาม (รอบสุดท้าย): ตรวจงานแก้หลังฝ่ายค้านรอบสอง

> ขอบเขต: O1 `0eb8492` · C3 `69ccf37` · main `208f44d` (API v1 confirm) · V `c641f6c` · L2 `53256f8`+`979eefe` · S2 `0a82911`
> (HEAD `642c201` บน `claude/erp-system-review-team-660mev`) · **อ่านอย่างเดียว** · ยังไม่ได้คอมไพล์ (env ไม่มี .NET SDK)
> วิธี: เปิดไฟล์ตรงจุดทุกข้อ · เดินตัวเลขด้วยสูตรในโค้ดจริง · `callers.py` + grep ทั้งเรพ · รัน checker ทีละตัว (ไม่รัน `check_all.sh`)

## สรุปสั้น

| ระดับ | จำนวน | หัวข้อ |
|---|---|---|
| P1 CONFIRMED | 3 | L2 ส่วนลดท้ายบิลถูกนับเป็นฐานมัดจำ · C3 เติมเลขภาษีให้แถวที่จับด้วยชื่อแบบ fuzzy/substring · O1 ลายเซ็นลูกค้าเดิมถูกใช้กับใบที่แก้เนื้อหาแล้ว |
| P2 CONFIRMED | 3 | V คำค่ารับรองหลุดจากการปิดเคลม · L2 ข้อความด่านสั่งให้รับรู้มัดจำเองซ้ำ · Integration idempotency ข้ามใบ Voided (ถี่ขึ้นเพราะ N2) |
| PLAUSIBLE | 9 | ดูหมวด B |
| ความเสี่ยงคอมไพล์ | ไม่พบจุดที่ต้องแก้ | checker ที่เกี่ยวข้อง 20 ตัวผ่าน + ตรวจมือ 12 จุด (หมวด D) |

---

## A. CONFIRMED

### ✅ 04ce362 R3-1 (P1 · L2) ส่วนลดท้ายบิลที่ผู้ใช้กรอกเอง ถูกรวมเข้ากับฐานมัดจำ แล้วตอนอนุมัติถูกรับรู้เป็น "มัดจำ" ทั้งก้อน

**ที่**
- `DocumentService.cs:15747` `ConvertTaxedDrivesAsync` คืน `((billDiscountAmount ?? 0m) + baseSum, false, 0m)` ⇒ `BillDiscountAmount` = ส่วนลดการค้า + ฐานมัดจำ ในช่องเดียวกัน
- `DocumentService.cs:15765-15766` `RealizeTaxedDepositDeductionsAsync` ใช้ `doc.BillDiscountAmount - already` **ทั้งก้อน** เป็นฐานที่ต้องรับรู้จากมัดจำ
- ป้ายบนกระดาษ `DepositPolicyResolver.BillDeductionIsTaxedDeposit` (`:246`) → `PdfGenerationService.cs:2179` / `DocumentRenderer.cs:921` พิมพ์ทั้งก้อนเป็น "หักมูลค่ามัดจำตามใบกำกับภาษี {เลข}"
- ฟอร์มส่ง `billDiscountAmount` (โหมด ฿) พร้อมกับ drives ของขายเงินสดได้ตามปกติ (`documents.html:8774-8777` + `:8940-8952`) · ส่วนลดแบบ % ถูกปฏิเสธ แต่ส่วนลดแบบบาท**ไม่ถูกปฏิเสธ**

**เดินตัวเลข** (ขายเงินสด 7,450 รวม VAT ⇒ ฐาน 6,962.62 · ส่วนลดท้ายบิล 100 บาท)
- (ก) มัดจำ 2,000 (ฐาน 1,869.16) ใช้เต็ม ⇒ `BillDiscountAmount` = 1,969.16 ⇒ อนุมัติ: `AllocateBaseDeduction(1,969.16, [1,869.16])` ขาด **100.00** ⇒ throw "มัดจำเหลือฐานไม่พอ (ขาด 100.00)" — การขายที่ถูกต้องอนุมัติไม่ได้ และข้อความชี้ผิดที่ (บอกให้ตรวจมัดจำ)
- (ข) มัดจำ 10,700 (ฐาน 10,000) ใช้บางส่วน 2,140 (ฐาน 2,000) ⇒ `BillDiscountAmount` = 2,100 ⇒ ตัวใบถูก (ฐาน 4,862.62 · VAT 340.38 · รวม 5,203.00) แต่การอนุมัติรับรู้มัดจำ **2,100 แทน 2,000** **โดยไม่มีอะไรฟ้อง**:
  - มัดจำคงเหลือ 7,900 แทน 8,000 ⇒ ยอดคืนลูกค้าได้ 8,453.00 แทน **8,560.00 (ลูกค้าขาด 107.00)**
  - รายได้รวม 4,862.62 + 2,100 = 6,962.62 (= ราคาไม่หักส่วนลด) แทน 6,862.62 ⇒ รายได้เกิน 100
  - VAT สุทธิของรายการ (เมื่อคืนมัดจำที่เหลือ) = 147 + 340.38 = 487.38 แทน 480.38 ⇒ ภ.พ.30 เกิน 7.00
  - ใบกำกับพิมพ์ "หักมูลค่ามัดจำตามใบกำกับ … 2,100.00" — ส่วนลดการค้า 100 หายจากกระดาษ และยอดมัดจำที่อ้างไม่ตรงใบมัดจำ

**ทางแก้ที่เสนอ**: เก็บฐานมัดจำที่หักไว้เป็นฟิลด์แยก (เช่น `DepositBaseDeducted`) แล้วให้ทั้ง realize และ renderer อ่านฟิลด์นั้น · หรือปฏิเสธ "ส่วนลดท้ายบิลแบบบาท + หักมัดจำที่ออกใบกำกับแล้ว" พร้อมบอกทางไปต่อ (ใส่ส่วนลดเป็นรายบรรทัด) เหมือนกรณี %. เทสต์ที่ต้องเพิ่ม: สองทิศ (มีส่วนลด → รับรู้เท่าฐานมัดจำเท่านั้น · ไม่มีส่วนลด → ตัวเลขเดิม 1,869.16)

### ✅ 85dfda8 R3-2 (P1 · C3) `AdoptTaxId` เขียนเลขภาษีถาวรลงแถวที่จับได้ด้วย **fuzzy/substring**

**ที่**
- `Controllers/V1/DocumentsV1Controller.cs:346-361` — ผู้สมัครจาก SoftScope → `CounterpartyNameMatcher.Best` (เทียบชื่อ**ข้ามภาษาแบบคล้าย** ≥ `ConfidentThreshold`) → `AdoptTaxId(matched, taxId, …)` + `SaveChangesAsync`
- `ImportExportService.cs:1597-1601` (`ImportDocumentAsync`) — `softScope.FirstOrDefaultAsync(c => c.Name.Contains(contactName))` (**substring · ไม่มี OrderBy**) → `AdoptTaxId`

**ผล**: ก่อนรอบนี้การจับผิดตัวผูกผิด "เอกสารใบนั้น" (แก้ได้รายใบ) · ตอนนี้เลขภาษีของผู้ซื้อ A ถูกเขียนลงแถวผู้ติดต่อ B **ถาวร** ⇒ ครั้งถัดไป `FindAsync(taxId A)` ได้แถว B ⇒ ใบกำกับของ A ทุกใบพิมพ์ชื่อ/ที่อยู่ของ B กับเลขของ A (§86/4 ผู้ซื้อผิดตัว) และแถวที่เคยจับผิดไม่มีทางกลับ (ไม่มีร่องรอยว่าเลขมาจาก payload ไหน). ตัวอย่าง: ไฟล์นำเข้า ContactName "ABC" ⇒ `Contains` ได้ "ABC Trading" หรือ "ABC Holdings" แล้วแต่ลำดับจากฐาน ⇒ แถวนั้นได้เลขของรายการในไฟล์
ขัด CLAUDE.md กฎ #4 H ("ต้องเป็น superstring ไม่ใช่ fuzzy" · "ห้ามขยายเป็นชื่อคนละบริษัท") และ F2 ข้อ 3

**ทางแก้ที่เสนอ**: ให้ `AdoptTaxId` ทำงานเฉพาะเมื่อจับได้ด้วย**ชื่อเท่ากันหลัง normalize** หรือด้วยอีเมล/รหัสภายนอก · fuzzy/substring = คืน id ได้ (พฤติกรรมเดิม) แต่ห้ามเติมเลข · เพิ่มกติกา `required_call_site_check` ห้าม `AdoptTaxId` ในเมธอดเดียวกับ `CounterpartyNameMatcher` / `.Contains(` บนชื่อ

### ✅ 132c2b5 R3-3 (P1 · O1 N6) ลายเซ็นลูกค้าเดิมถูกใช้อนุมัติใบเสนอราคาที่ถูกแก้เนื้อหาไปแล้ว

**ที่**: `SignatureApprovalService.cs:409-467` (`alreadySigned` = มีขั้น Customer ที่ Approved อยู่แล้ว ⇒ ข้ามการบันทึกลายเซ็นใหม่) · ข้อความ `:489-493` บอกผู้ใช้ "แก้ตามข้อความแล้วเรียกซ้ำ (**ระบบใช้ลายเซ็นเดิม** ไม่บันทึกซ้ำ)" · `UpdateDocumentAsync` ไม่ล้าง/ทำให้ `DocumentApproval` เป็นโมฆะเมื่อแก้เนื้อหา (grep `DocumentApproval` ใน `DocumentService.cs` = เฉพาะ purge `:8474` กับการอ่าน `:5131`)

**ลำดับที่เกิดได้จริง**: ลูกค้าเซ็น → อนุมัติไม่ผ่าน (422 "SIGN-APPROVE-PENDING") → ใบยังเป็นร่างแก้ได้ → ผู้ใช้แก้ราคา/บรรทัด → เรียกซ้ำ ⇒ อนุมัติด้วยลายเซ็นที่ลูกค้าให้กับ**ราคาเดิม** · ค่า `SignatureData`/`ApproverName` ของคำขอใหม่ถูกทิ้งเงียบ ขณะที่ `ApproveDocumentAsync` ประทับผู้อนุมัติเป็น `external:{request.ApproverName}` ของคำขอใหม่ ⇒ ชื่อผู้อนุมัติกับลายเซ็นที่เก็บไว้อาจเป็นคนละคน
ก่อนรอบนี้การเรียกซ้ำสร้างลายเซ็นใหม่ทุกครั้ง (ซ้ำแต่สด) — การกันซ้ำจึงสร้างคลาสบั๊กใหม่

**ทางแก้ที่เสนอ**: ใช้ลายเซ็นเดิมได้เฉพาะเมื่อเอกสารไม่ถูกแก้หลังเวลาเซ็น (`doc.UpdatedAt <= signature.SignedAt` หรือ hash เนื้อหาเอกสาร) · มิฉะนั้นทำให้ขั้น Customer เดิมเป็นโมฆะแล้วบันทึกใหม่ · หรือให้ `UpdateDocumentAsync` ล้างขั้นลูกค้าที่ Approved ของใบที่ยังไม่อนุมัติ

### ✅ 41bb2c8 R3-4 (P2 · V C-4) ย้าย regex ค่ารับรองออกจาก JS แล้วคำที่เป็นค่ารับรองจริงหลุด (ทิศอันตราย = เคลมภาษีซื้อเกินสิทธิ์)

`ManualInputVatLineRule.cs:27-30` + `ProhibitedInputVatScreener.cs:36-37` เทียบกับ regex เดิม `/รับรอง|เลี้ยง(?:ลูกค้า|รับรอง)?|กระเช้า|ของขวัญลูกค้า|กอล์ฟ|พาลูกค้า/` (จำลองด้วยรายการคำเดียวกับโค้ด):

| คำอธิบายบรรทัด | เดิม | ตอนนี้ |
|---|---|---|
| ค่าอาหารรับรองแขก | ปิดเคลม | **ไม่ปิด** |
| รับรองแขกต่างประเทศ | ปิดเคลม | **ไม่ปิด** |
| เลี้ยงอาหารลูกค้า | ปิดเคลม | **ไม่ปิด** |
| เลี้ยงสังสรรค์ลูกค้า | ปิดเคลม | **ไม่ปิด** |
| ค่าอาหารเลี้ยงลูกค้า · ค่ารับรอง · ของขวัญลูกค้า | ปิดเคลม | ปิดเคลม ✓ |
| หนังสือรับรองบริษัท · อาหารเลี้ยงสัตว์ · ค่าเลี้ยงพนักงาน | ปิดผิด | ไม่ปิด ✓ (ตั้งใจ) |

คอมเมนต์ของคลาสเองระบุว่า "เปิดเคลมผิด = ยื่น ภ.พ.30 เกินสิทธิ์" คือทิศที่แพงกว่า · ตาข่ายที่เหลือคือธงผังบัญชี (`InputVatAccountPolicy`) ถ้าผู้ใช้เลือกผังค่ารับรอง. เสนอเพิ่มรูปคำ "รับรอง" ที่มีคำนามคนนำหน้า/ตามหลัง (แขก · ลูกค้า · ผู้บริหาร) และ "เลี้ยง…ลูกค้า" แบบมีคำคั่น พร้อมเทสต์ทิศตรงข้ามชุดเดิม

### ✅ 04ce362 R3-5 (P2 · L2) ข้อความด่านสั่ง "รับรู้มัดจำที่หน้าเงินมัดจำ" ทั้งที่เส้นที่แนะนำรับรู้ให้อัตโนมัติแล้ว ⇒ รับรู้ซ้ำได้

`DocumentService.cs:15685` (`GuardDrivesGrossApplyAsync` — ใบเก่าที่ยังเป็น drives) ต่อข้อความ "แก้ใบ (บันทึกใหม่จากฟอร์ม ระบบแปลงเป็นหักฐานให้) แล้วอนุมัติอีกครั้ง" เข้ากับ `GrossApplyBlockedMessage` ข้อ ① ซึ่งสั่ง "…แล้วรับรู้มัดจำเป็นรายได้ที่หน้า "เงินมัดจำ"" (`DepositPolicyResolver.cs` `GrossApplyBlockedMessage`). ผู้ใช้ที่ทำตามทั้งสองประโยค: บันทึกใหม่ → อนุมัติ (รับรู้อัตโนมัติ) → กดรับรู้เองอีกครั้ง ⇒ ถ้ามัดจำใช้บางส่วน ปุ่มรับรู้ผ่าน (ยังมีฐานคงเหลือ) ⇒ รายได้ซ้ำ + มัดจำลูกค้าหาย. ข้อความเดียวกันที่ปุ่ม "หักมัดจำ" (`:4122`) และ Integration (`IntegrationService.cs:3190`) ถูกต้อง (สองเส้นนั้นไม่รับรู้อัตโนมัติ) — ต้องแยกข้อความของ Guard

### ✅ 04ce362 R3-6 (P2 · L2 N2 + เดิม) idempotency ของ Integration ข้ามใบ Voided แบบไม่มีลำดับ ⇒ สร้างใบกำกับซ้ำ

`IntegrationService.cs:706-712`: `FirstOrDefaultAsync(Reference == ExternalRef && TaxInvoice)` **ไม่มี OrderBy** แล้ว `existing.Status != Voided` ⇒ ถ้ามีทั้งใบ Voided (A) และใบที่ใช้งาน (B) ของ ExternalRef เดียวกัน การยิงซ้ำที่ได้ A กลับมาจะตกไปสร้างใบ C ใหม่ (ใบกำกับซ้ำ · รายได้/VAT ซ้ำ). รูปแบบนี้มีอยู่ก่อน แต่ N2 ทำให้ "ใบ Voided ที่ Reference เดียวกับใบที่ส่งซ้ำ" เป็นเส้นทางปกติของตาข่าย (`:1078` void แล้วคู่ค้าแก้แล้วส่ง ExternalRef เดิม). เสนอ: เลือกใบที่ไม่ Voided ก่อน (`OrderBy(Status == Voided)`) หรือกรอง Voided ออกใน query

---

## B. PLAUSIBLE (เงื่อนไขเกิดชัด แต่ต้องมีข้อมูล/จังหวะเฉพาะ)

| # | ทีม | เรื่อง | ที่ | ระดับถ้าเกิด |
|---|---|---|---|---|
| ✅ 04ce362 B1 | L2 | ตาข่าย void ของ Integration ไม่มีตาข่ายของตัวเอง: `VoidDocumentAsync` throw ได้ (งวดยื่นแล้ว `IsDocumentFilingLockedAsync` · งวดบัญชีปิด — ใบย้อนวันที่) ⇒ ตก `HandleSyncError` ⇒ **ใบ Approved ไม่มี JE** ค้างอยู่ (สภาพที่ N2 ตั้งใจปิด) และหมายเหตุบนใบถูกบันทึกไปแล้วว่า "ยกเลิกอัตโนมัติ" ทั้งที่ไม่ได้ยกเลิก | `IntegrationService.cs:1075-1078` · `DocumentService.cs:7194-7240` | P2 |
| ✅ 04ce362 B2 | L2 | `RealizeTaxedDepositDeductionsAsync` ไม่ล็อกแถวใบมัดจำ (`FOR UPDATE`) ขณะที่เส้น drives เดิมล็อก ⇒ อนุมัติใบสุดท้ายสองใบที่อ้างมัดจำเดียวกันพร้อมกัน = lost update บน `DepositRealizedAmount` + JE รับรู้สองใบ ⇒ 217xx ถูก Dr เกิน | `DocumentService.cs:15693-15785` เทียบ `:14848` | P2 |
| ✅ 04ce362 B3 | L2 | ตัวแปลงนับ "จำนวนเลขอ้างอิง ≠ จำนวนมัดจำออกใบกำกับ" ⇒ ข้อความ "ปนกันระหว่างออกใบกำกับแล้ว/VAT พัก" ขึ้นผิดเหตุเมื่อ: อ้างเลข JV (มัดจำแบบ journal จากฟอร์ม `ap.kind === 'journal'`) · เลขซ้ำในสตริง · เลขเดียวชนทั้ง DocumentNumber ของใบหนึ่งและ Reference ของอีกใบ · มัดจำร่าง/ยกเลิก | `DocumentService.cs:15724-15728` | P3 (ดัง แต่ชี้ผิด) |
| ✅ 04ce362 B4 | L2 | `UpdateDocumentAsync` แปลงก่อนด่านสถานะ (`:2151` ก่อน `:2181`) ⇒ ใบที่ไม่ใช่ร่างที่ยังถือ drives + ref + มัดจำเหลือฐาน จะได้ข้อความของตัวแปลง ("ต้องส่งรายการบรรทัด" / "ปนกัน" / ส่วนลด ถูกบล็อกเป็น "ห้ามแก้ส่วนลดท้ายบิล" ในเส้น revision) แทนข้อความสถานะที่ถูก · ใบ drives เก่าที่ผ่าน AutoPost ตั้ง `DepositRealizedAmount = SubTotal` แล้ว (`:14901`) จึงเกิดเฉพาะข้อมูลผิดปกติ | `DocumentService.cs:2151-2168` | P3 |
| ✅ 04ce362 B5 | L2 | ที่พักมัดจำสองแบบ (ออกใบกำกับแล้ว + VAT พัก): `ApplyDepositToInvoiceAsync` ตั้ง `DepositAppliedAmount > 0` บนใบสุดท้าย (`:4333`) ⇒ `BillDeductionIsTaxedDeposit` กลายเป็น false ⇒ กระดาษพิมพ์ฐานมัดจำที่หักเป็นส่วนลดธรรมดา ไม่อ้างเลขใบกำกับมัดจำ (มีมาก่อนรอบนี้ — ไม่ใช่ถดถอยของ 979eefe) | `DepositPolicyResolver.cs:246` · `LodgingService.Lifecycle.cs:814-820` | P2 |
| ✅ 41bb2c8 B6 | V | `IsZeroRatedFullTaxInvoice` นับบรรทัด `VatRate == 0` ทุกบรรทัดเป็น §80/1 — สัญญา API (`IntegrationDtos.cs:175`) ไม่ได้บอกว่า "ยกเว้น §81 ต้องส่ง −1" และ `PartnerVatRate` ส่ง rate ≤ 0 ผ่านตรง ⇒ คู่ค้า/POS ที่ใช้ 0 แทน "ไม่มี VAT/ยกเว้น" (ธรรมเนียมทั่วไป) จะได้เลขชุด TIV + `IsTaxInvoiceByLaw = true` + e-Tax อัตโนมัติ สำหรับการขายยกเว้น (กฎ #2 D: Exempt ห้ามออกใบกำกับ) — เดิมได้ REC | `TaxInvoiceSeriesPolicy.cs:76-81` · `IntegrationService.cs:976-984` | P2 |
| ✅ 3da2760 B7 | S2 | งานกวาดไฟล์สแกนที่ถูกถอด: `.Take(500)` ไม่มีลำดับ และแถวที่ยัง "มีสแกนชี้" ถูกข้ามแต่ไม่เคยถูกเอาออก ⇒ ถ้าแถวแบบนี้เกิน 500 แถว งานจะวนเจอชุดเดิมทุกคืน (head-of-line ถาวร — คลาสเดียวกับที่ทีมเพิ่งแก้ใน query แรก) | `OcrSelfCorrectionService.cs:239-248` | P3 |
| ✅ 85dfda8 B8 | C3 | `AdoptTaxId` ไม่ตรวจ checksum mod-11 และไม่กันแถว `IsWalkInCustomer` ⇒ คู่ค้า/POS ที่ส่ง `0000000000000` (ค่ามาตรฐานของ POS หลายเจ้าสำหรับลูกค้าทั่วไป — checksum ไม่ผ่าน) + ชื่อ "ลูกค้าทั่วไป" จะเติมเลขปลอมลงแถวลูกค้าทั่วไปที่มีอยู่ (จับด้วยชื่อตรงตัว `IntegrationService.cs:1643`) ⇒ แถวกลางกลายเป็น "มีเลขผู้ซื้อ" | `ContactTaxBranchKey.cs:256-283` | P2 |
| ✅ 132c2b5 B9 | O1 | กันลายเซ็นลูกค้าซ้ำด้วย `AnyAsync` อย่างเดียว (ไม่มีล็อก/unique) ⇒ เรียกพร้อมกันสองครั้ง = ขั้น Customer + ลายเซ็นซ้ำ และครั้งที่สองได้ 422 "บันทึกลายเซ็นลูกค้าแล้ว แต่อนุมัติไม่ได้: ใบนี้อนุมัติแล้ว" (ข้อความหลอก — จริง ๆ สำเร็จแล้ว) | `SignatureApprovalService.cs:409-412, :483-494` | P3 |

หมายเหตุข้อมูลเก่า (ไม่ใช่ถดถอย): `RefundBaselineGross` เพิ่มด้วย `DEFAULT 0` ⇒ การจองที่ยกเลิกก่อนรอบนี้และมีการคืนจากหน้าเงินมัดจำก่อนยกเลิก ยังคำนวณแบบเดิม (N3 ยังเกิดกับแถวเก่า) — ย้อนเติมไม่ได้เพราะไม่มีประวัติยอดคืน ณ จุดยกเลิก · ควรจดใน DOCUMENT_FLOW ว่าแถวเก่าอาจต้องซ่อมด้วยมือ

---

## C. ตรวจแล้วไม่มีปัญหา (ห้ามรายงานซ้ำ)

**L2**
- เดินตัวเลข 7,450/2,000 (VAT ทันที) เส้นฟอร์มขายเงินสดใบเดียว: ตัวแปลงได้ฐาน 1,869.16 (`remGross` 2,000.00 = ใช้เต็ม ⇒ ฐานคงเหลือไม่ปัด) · ใบสุดท้ายฐาน 5,093.46 VAT **356.54** รวม 5,450.00 · VAT สองใบ 130.84 + 356.54 = **487.38** ✓ · รายได้ 5,093.46 + รับรู้ 1,869.16 = 6,962.62 ✓ · เงินสด Dr 5,450 = TotalAmount ✓ · ส่วนหักท้ายบิลเป็นฐานก่อน VAT ทั้งโหมดราคารวม/ไม่รวม VAT (`AllocateBillDiscount` ใช้ `NetAmount`) ✓
- เส้นเช็คเอาต์ที่พัก: ใบสุดท้ายถูกอนุมัติ ⇒ รับรู้ครบใน AutoPost · `SettleCheckOutAsync` รับรู้เฉพาะ `d.Base − realizedFor[d.Id]` (= 0) · ลำดับใบมัดจำสองฝั่งเหมือนกัน (`DocumentDate, CreatedAt`) และสูตรฐานคงเหลือสามจุดเหมือนกัน ⇒ ไม่รับรู้ซ้ำ/ขาด
- ฐาน ภ.พ.30: JE รับรู้ไม่แตะ 21911 (ใบมัดจำ taxed ⇒ `recognizeDeferredVat = false`) · VAT มัดจำคงอยู่เดือนรับเงิน · VAT ใบสุดท้ายอยู่เดือน tax point ของใบสุดท้าย ✓
- `RealizeTaxedDepositDeductionsAsync` อยู่ในธุรกรรมอนุมัติ (`CommitAsync :5608` / `RollbackAsync :5612`) ⇒ มัดจำไม่พอ = rollback ทั้งการออกเลข ✓ · void → `ReverseDepositRealizationsForAsync` (`:7466`) กลับ JE ที่ผูก `DepositRealizedForDocumentId` · กู้ใบ (`RestoreVoidedDocumentAsync`) → อนุมัติใหม่รับรู้อีกครั้ง (`already` ไม่นับ JE ที่ถูกกลับ) ✓ · เส้น reclassify/สลับฝั่ง CN (`:6518/:6640`) กลับเฉพาะ JE ที่ `SourceDocumentId == ใบนี้` — JE รับรู้มี `SourceDocumentId = ใบมัดจำ` จึงไม่ถูกกลับและ `already` กันซ้ำ ✓
- Integration: ด่านปฏิเสธอยู่ก่อน `GetNextNumberAsync` และก่อน `ResyncUpdateInvoiceAsync` ⇒ ไม่มีช่องว่างเลข §86/4 · ตาข่าย void คงเลขไว้ · ใบ Voided รับชำระไม่ได้ · e-Tax ไม่ถูกออก (void ก่อน hook) ✓ (ยกเว้นกรณี void ล้ม B1)
- migration `RefundBaselineGross`: `ADD COLUMN IF NOT EXISTS … NOT NULL DEFAULT 0` idempotent · แถวเก่าได้ 0 = สูตรเดิมทุกประการ (`RefundPaidCatchUp` กับ baseline 0 ≡ `min(refundedOnDocs, due) − paid`) ✓
- `DepositReversalMath.RefundSplit` คืนครั้งเดียวจากศูนย์ = สูตรเดิม · ใช้ตัวเดียวทั้ง `RefundDepositAsync` และตัววางแผนที่พัก ✓
- ความเสี่ยงที่ทีมรู้ (เลือกมัดจำซ้ำบนร่างที่แปลงแล้ว): ถ้ามัดจำใช้เต็ม ⇒ อนุมัติล้มดัง · ถ้ามัดจำเหลือพอ ⇒ ใบหักเพิ่มและมัดจำถูกรับรู้เพิ่ม**เท่ากัน** (สมดุลทางบัญชี ผิดแค่เจตนาผู้ใช้) — ต่างจาก R3-1 ที่ตัวใบกับการรับรู้ไม่ตรงกัน

**S2**
- ทิศตรงข้าม: ผู้ถือ `Expense.Approve`/`HR.Admin` ที่ไม่ใช่ผู้ยื่นอนุมัติ/ปฏิเสธได้ · ผู้ถือ `Expense.Pay` + สิทธิ์อนุมัติ PV จ่ายได้ (`Decide` ไม่เข้า branch เจ้าของใบ) ✓ · บริษัทคนเดียว: role `Owner` + `SodBlockSelfApproval = false` ⇒ `SelfDecisionAllowed` ✓ · SoD ของ PV (`CreatedBy = "system:expense-claim"` ≠ ผู้จ่าย) ไม่ตีกลับ ✓
- ลายเซ็น interface ที่เปลี่ยน (`UpdateAsync/SubmitAsync/VoidAsync/MarkAsPaidAsync`): `IExpenseClaimService` ถูกใช้แค่ `ExpenseClaimController` + `MobileApiService` (มือถือเรียก `ApproveAsync/RejectAsync` ซึ่งลายเซ็นไม่เปลี่ยน) · ไม่มี `new ExpenseClaimService(` ในเทสต์ · ไม่มีผู้ตั้ง `ExpenseClaimStatus.Approved/Paid/Rejected` นอก service (grep) · `UpdateAsync` แก้ได้เฉพาะ Draft ⇒ ผู้ยื่นแก้ยอดหลังอนุมัติไม่ได้ ✓
- ลบสแกนที่ยังไม่ผูก = ลบไฟล์จริง: ไฟล์เป็นแถวเจ้าของเดียว (`EntityType/EntityId`) · สแกนอื่นชี้ไฟล์ ⇒ `Leave` · ไฟล์รายการอื่น (ใบนำส่ง `StatutoryRemittanceController` อัปโหลดไฟล์ของตัวเอง · 50 ทวิ ถูกกันด้วย `WhtCreditFileLink`) ไม่ใช่ชนิด `OcrScan` ⇒ `Leave` · เอกสารที่ OCR cascade soft-delete มีได้แค่ Draft/WaitingApproval/Rejected (ไม่ต้องเก็บตามกฎหมาย) ✓
- `OcrScanPostingKeys` ครอบทั้งสองผลของ import-stock · ไม่มีค่า enum ปลายทางอื่นนอก Stock/Supplies/FixedAsset ✓

**V**
- ใบ mixed 7% + 0%: พื้นแรก `VatAmount > 0` จับอยู่แล้ว ✓ · ยกเว้นล้วน (−1) ไม่เข้า ✓ · POS ไม่ส่ง `companyVatRegistered` ⇒ ค่าเดิม ✓ · walk-in/ไม่ประสงค์รับ ⇒ ไม่ใช่ใบกำกับ (probe ของ Integration ใช้ผู้ติดต่อ walk-in เมื่อผู้ซื้อไม่ประสงค์รับ) ✓
- hook e-Tax: `NotFullTaxInvoiceByDesign` ถูกประเมินเฉพาะ VAT > 0 ⇒ ใบ 0%/ยกเว้นตัดสินด้วย `IsTaxInvoiceByLaw` ตามเดิม · `IsJuristicBuyer` ดูทั้งชนิดและเลขขึ้นต้น 0 ✓
- `confirmVatStatus`: หน้าแท็บบริษัทส่งเมื่อ `_companyVatLoaded && _vatTouched` · แท็บตั้งค่าส่งเมื่อ `_vatLoaded && _vatTouched` · ปุ่ม "ยืนยันตามที่เลือกอยู่" ตั้ง touched แล้วรอกดบันทึก · wizard สร้างบริษัท (`layout.js:2103` `createCompany`) ไม่ผ่านเส้นนี้ ✓
- รายงาน `zero-rated-tax-invoices`: กรอง `CompanyId` · `Include(Lines, Contact)` ก่อนตัดสินด้วยตัวเดียวกับตัวตรึงธง ✓ (ไม่มี Take ก่อนตัดสินในหน่วยความจำ — ข้อสังเกตประสิทธิภาพเท่านั้น)

**O1**
- คำเตือนภายในไม่รั่วถึงลูกค้าภายนอก: `ExternalApprovalController` เป็น `[Authorize]` + `RequireApproveAsync` (สมาชิกที่มีสิทธิ์อนุมัติเท่านั้น) · หน้าลูกค้าไม่ล็อกอิน `PublicQuotationController` (`/accept`) แค่ประทับ `QuotationAcceptedAt` ไม่เรียกการอนุมัติ/คำเตือน ✓
- P-c/P-d/P-e: `closesByAdjustmentOnly` ประกาศก่อนใช้ (`:11182`) · ผู้เรียก `ActualPaidNotApplicableReason` 4 จุดส่งอาร์กิวเมนต์ครบ · ร่างเก่าที่ถือยอดชำระจริงบนใบตั้งหนี้จ่ายทันทีถูกหยุดพร้อมทางไปต่อ และฟอร์มส่ง `actualPaidAmount: 0` เมื่อล้างช่อง ✓

**C3**
- tenant เปลี่ยนเลขภาษี (ย้ายนิติบุคคล): กุญแจ (1) เจอแถวเลขเก่า → `Pick` ไม่ตรง → ถอยไป (2)/(3) → สร้างแถวใหม่ที่ประทับ ExternalId · รอบถัดไป `FirstOrDefault` ได้แถวใดก็ตาม — ถ้าแถวเก่า จะถูกข้ามแล้ว (2) เจอแถวใหม่ ⇒ ผลถูกทั้งสองลำดับ ✓ · เปลี่ยนสาขาเหมือนกัน ✓
- ที่พัก: soft match ด้วยอีเมล/เบอร์ + `LodgingGuestContact.SoftCandidateAcceptable` ก่อน `AdoptTaxId` — แขกชื่อซ้ำไม่ถูกเติมเลขด้วยชื่ออย่างเดียว ✓
- main `208f44d` (API v1 confirm กรอง feedbackId ตาม CompanyId) — อ่านแล้วตรงตามที่ตั้งใจ ✓

---

## D. ความเสี่ยงคอมไพล์

**checker (รันทีละตัวบน HEAD `642c201`) — เขียวทั้งหมด**: `required_call_site_check` (90 กติกา + negative test 12) · `attachment_gate_check` (12 target · 28 action · negative test ผ่าน) · `contact_taxid_only_match_check` (5 = baseline 5) · `approved_status_writer_check` (5 = baseline 5 · กติกา commit ผ่าน) · `service_interface_check` 0 · `undeclared_local_check` 0 · `record_arg_check` 0 · `write_permission_gate_check` ✅ · `nullable_arg_check` ✅ · `using_check` 0 · `tuple_name_merge_check` 0 · `dead_helper_check` ✅ (แจ้ง `OcrPartyResolver.OurTaxIdOnPaper` / `PiiMask.BankAccountNo` ตัดออกจาก baseline ได้) · `arg_type_check` 0 · `identifier_space_check` 0 · `string_quote_close_check` 0 · `verbatim_string_check` 0 · `namespace_shadow_check` 0 · `accessibility_check` 0 · `dto_nullable_contract_check` 0 · `di_cycle_check` ✅ · `test_inventory --check` ✅ · `node --check` ทุก `<script>` ของ documents/settings/lodging + `signatures-logic.js` ✅ · U+FFFD 0 · brace สมดุลทุกไฟล์ .cs ที่แตะตั้งแต่ `f9f1556`

**ตรวจมือ (ชนิดที่ checker มองไม่เห็น) — ไม่พบจุดที่ต้องแก้**
1. `is { } convCreate` บน `(decimal BillDiscount, bool Drives, decimal Applied)?` — pattern ได้ชนิด tuple ที่คงชื่อ ⇒ `convCreate.BillDiscount` คอมไพล์ได้
2. `request = request with { … }` บนพารามิเตอร์ของ `CreateDocumentAsync`/`UpdateDocumentAsync` — record positional · ชนิด `bool?`/`decimal?` รับค่า `bool`/`decimal` ได้ (`DocumentDtos.cs:26-41, 289-295`) · `Lines = request.Lines ?? throw …` บน `List<…>?` ✓
3. `request.DepositAppliedDrivesJournal ?? false` — `CreateDocumentRequest` เป็น `bool?` ✓ · `InboundInvoiceRequest.DepositAppliedDrivesJournal` เป็น `bool` (ไม่ใช้ `??`) ✓
4. `new RealizeDepositRequest(amount, doc.DocumentDate, revenueCode, doc.Id)` — ลำดับเดียวกับผู้เรียกเดิมในที่พัก ✓
5. `foreach (var (depId, amount) in lines)` บน `IReadOnlyList<(Guid Id, decimal Base)>` · ชื่อ `lines` ไม่ชนตัวแปรอื่นในเมธอด ✓
6. `SignatureApprovalService`: ถอด `var approved =` แล้วไม่มีการอ้างถึงภายหลัง · `customerApproval` ถูกย้ายเข้า block `if (!alreadySigned)` และไม่ถูกใช้หลัง block ✓ · `BusinessRuleException(string, string, int)` เลือก overload แรกไม่กำกวม ✓ · `ApproveDocumentAsync(…, ApprovalAckSource, withAiHints:)` มีใน interface (`IDocumentService.cs:119`) ✓
7. `OcrController`: `var postDeny` สองจุดอยู่คนละ action (`RegisterAsset :794` · `ImportStock :1023`) ไม่ชน CS0136 · `return postDeny` (ObjectResult) → `ActionResult<T>` ผ่าน implicit ✓
8. `MobileApiService`: `_services` มีอยู่ · `ApproveExpenseClaimRequest(string?)` / `RejectExpenseClaimRequest(string)` ตรงลำดับ ✓
9. `ExpenseClaimService` ctor เพิ่ม `IPermissionService? permissions = null` ท้ายสุด — DI มี registration · ไม่มีผู้ `new` ในเทสต์ ✓ · `CompanyUser.Role` เป็น `UserRole` · `UserRole.Owner` มีจริง ✓
10. `IntegrationService`: probe `new Document { Lines = lines }` — `lines` เป็น `List<DocumentLine>` เข้า `ICollection<DocumentLine>` ✓ · `RejectTaxedDrivesAsync(IntegrationSyncLog, Guid, Stopwatch, …)` ใช้ชนิดเดียวกับ `CreateSyncLog`/`SaveSyncLog` ✓ · `VoidDocumentAsync(companyId, id)` ตรงลายเซ็น (`reversalDate` optional) ✓
11. `ContactsV1Controller`: projection `new ResolveCandidateRow(…)` ใน `Select` สุดท้าย (EF แปลได้) · `taxKey` ย้ายขึ้นมาประกาศนอก `if` ก่อนใช้ ✓
12. `LodgingService.Operations`: `docNos`/`includeInternal` อยู่ใน scope ของ `MapAsync` · `FinalDocumentNote` มีใน DTO (`LodgingDtos.cs:536`) ✓

---

## E. คำถามถึง main agent / เจ้าของ

1. R3-1: เลือกทาง "ฟิลด์ฐานมัดจำแยก" (ต้อง migration + renderer ×2 + DOCUMENT_FLOW) หรือ "ปฏิเสธส่วนลดบาทร่วมกับมัดจำที่ออกใบกำกับแล้ว" (แก้เร็ว · ผู้ใช้ย้ายส่วนลดไปรายบรรทัด)
2. R3-2: ยอมให้ fuzzy/substring **ผูกเอกสาร**ต่อไปแต่ไม่เติมเลข หรือยกเลิกการจับ fuzzy เมื่อ payload มีเลขภาษีเลย (สร้างผู้ติดต่อใหม่แทน)
3. R3-3: ลายเซ็นลูกค้าที่เก็บไว้แล้วใช้ซ้ำได้ภายใต้เงื่อนไขอะไร (เวลาแก้เอกสาร · hash เนื้อหา · หรือไม่ใช้ซ้ำเลย)
4. B6: สัญญา API ของคู่ค้าจะบังคับ "ยกเว้น = −1" (และออกประกาศถึงคู่ค้า) หรือให้ใบ 0% ต้องมีธงส่งออก/ต่างประเทศเพิ่มก่อนนับเป็นใบกำกับ
