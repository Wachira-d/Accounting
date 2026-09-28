# ฝ่ายค้าน (F3 #11) — รอบ 199 ภ.พ.06 ต่อช่องทาง · รอบ 197 ทีม K2 (สาขาผู้ขาย) · รอบ 195 ทีม I3 (VAT ถอดเอง รอบสาม)

ตรวจแบบอ่านอย่างเดียวบน HEAD `a628d01a` (branch `claude/erp-system-review-team-660mev`) · ไม่ได้แก้โค้ด · 2026-09-28

**สถานะคอมไพล์:** CI run 36404482765 (HEAD `a628d01a`) งาน `dotnet build Accounting.sln` (Release) = **ผ่าน**
(build ระดับ solution จึงรวมโปรเจกต์เทสต์) · run 36403312604 (`4c975c30`, รอบ 199) = ผ่าน ⇒ **ไม่พบ compile error**
ในทั้งสามชุด · แต่งาน `dotnet test` ถูก **skip** บน `claude/**` ⇒ assertion ของเทสต์ใหม่ (PosSlipHeaderTests ·
OcrVendorBranchReview197Tests · OcrApprovalGapWarningTests ฯลฯ) **ยังไม่เคยถูกรันจริง**

ป้าย: CONFIRMED = เปิดไฟล์ยืนยันแล้ว · PLAUSIBLE = กลไกมีจริงแต่ผลขึ้นกับข้อมูล · NOT-A-BUG = ตรวจแล้วไม่ใช่

---

## C) รอบ 199 — ภ.พ.06 คุมเฉพาะสลิปจากเครื่องบันทึกการเก็บเงิน (`61dd6408`)

### ✅ 347f2517 C-1 · CONFIRMED · **P1** — ด่านฝั่งเซิร์ฟเวอร์ยังบังคับวันที่ ภ.พ.06 เมื่อติ๊ก "กิจการขายปลีก" ⇒ คำตัดสินเจ้าของใช้ไม่ได้กับบริษัทใหม่
- `Accounting/Services/Implementations/CompanyService.cs:269-275`
  ```csharp
  if (request.IsRetailApproved == true
      && request.PhoR06ApprovedDate == null && company.PhoR06ApprovedDate == null)
      throw new InvalidOperationException(
          "กรุณาระบุวันที่กรมสรรพากรอนุมัติ ภ.พ.06 ก่อนเปิดสิทธิ์ออกใบกำกับภาษีอย่างย่อ");
  ```
- รอบ 199 เปลี่ยนความหมายของ `IsRetailApproved` เป็น "ประกอบกิจการขายปลีก (§86/6)" และหน้า `settings.html:201-206` บอกผู้ใช้ว่า
  "ติ๊กแล้ว เอกสารจากหน้าเอกสารเป็น 'ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ' ได้ทันที **ไม่ต้องใช้ ภ.พ.06**" — แต่
  `settings.html:2348-2349` ส่ง `phoR06ApprovedDate: null` เมื่อช่องวันที่ว่าง ⇒ เซิร์ฟเวอร์โยน "กรุณาระบุวันที่… ภ.พ.06"
- สถานการณ์: ร้านค้าปลีกที่ไม่ใช้เครื่อง POS (กลุ่มเป้าหมายของคำตัดสินนี้พอดี) ติ๊กช่องขายปลีก กดบันทึก ⇒ ได้ error ให้กรอก ภ.พ.06
  ⇒ ทางเดียวที่จะผ่านคือกรอกวันที่ ภ.พ.06 ปลอม (= ข้อมูลเท็จที่ต่อมาเปิดสิทธิ์สลิป POS ด้วย) หรือไม่ใช้ฟีเจอร์เลย · ผู้ได้ประโยชน์
  จากรอบ 199 มีแค่บริษัทที่เคยกรอกวันที่ไว้แล้ว
- ไม่มีเทสต์ใดครอบ `CompanyService` (เทสต์ของรอบนี้ทดสอบแค่ `AbbreviatedTaxInvoiceRule`/`PosSlipHeader`) — ตรง "เทสต์เรียกแค่ helper ≠ ด่านถูกต่อสาย"
- **แก้:** ถอดเงื่อนไขนี้ (ธงขายปลีกไม่ผูกกับวันที่อีกแล้ว) · ถ้าจะคงอะไรไว้ ให้กันทิศกลับคือ "กรอกวันที่ ภ.พ.06 แต่ไม่ติ๊กขายปลีก" (แจ้งเตือน ไม่ throw)
  · ใช้ `BusinessRuleException` แทน `InvalidOperationException` (กฎ #4 E) · เพิ่มเทสต์ service-level ทิศ "ติ๊กขายปลีก ไม่มีวันที่ = บันทึกได้"
  · ล็อกด้วย `required_call_site_check` แบบ `forbid` สูตรเดิม

### ✅ <pending> C-2 · CONFIRMED · P2 — หัวเอกสารคำนวณสดจากธงบริษัทปัจจุบัน ไม่ใช่ `IsTaxInvoiceByLaw` ที่ตรึงตอนออกเลข ⇒ พิมพ์ซ้ำใบเก่าได้หัวคนละแบบกับเลข
- `PdfGenerationService.cs:1546-1548, 1624-1626, 1845-1847` · `DocumentRenderer.cs:65-67` คำนวณ `companyMayIssueAbbreviated` จาก
  `Company` ณ วันพิมพ์ · ไม่มีบรรทัดใดใน `PdfGenerationService*.cs` อ่าน `Document.IsTaxInvoiceByLaw`
- ใบที่อนุมัติก่อนรอบ 199 โดยบริษัทขายปลีกที่ลงวันที่ **ก่อน** `PhoR06ApprovedDate` เคยได้หัว "ใบเสร็จรับเงิน" + เลข REC + `IsTaxInvoiceByLaw=false`
  (ตรึงที่ `DocumentService.cs:6134-6175`) · หลังรอบ 199 ช่องทาง Document ไม่ดูวันที่อีก ⇒ พิมพ์ซ้ำได้หัว "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ"
  แต่เลขยัง REC · `EtaxAutoIssueScope.DeclaredNotTaxInvoice` ยังถือว่าไม่ใช่ใบกำกับ ⇒ กระดาษประกาศว่าเป็นใบกำกับย้อนหลังโดยที่ระบบ/รายงานไม่ได้นับ
  (§86/4 ห้ามแก้ย้อนหลัง) · คอมมิตรู้เรื่องนี้แล้วแต่โยนไปให้ "นักบัญชีตรวจการพิมพ์ซ้ำ" (TEST_PLAN ABB-14) = ตรวจด้วยมือ ไม่ใช่ด่าน
- ทิศกลับก็เกิดได้ (เจ้าของเอาธงขายปลีกออกทีหลัง ⇒ ใบ TIV เดิมพิมพ์ซ้ำเป็น "ใบเสร็จรับเงิน")
- **แก้:** ใบที่ออกเลขแล้ว (`IsTaxInvoiceByLaw != null`) ให้ใช้ค่าที่ตรึงไว้เป็นตัวตัดสิน `mayAbbrev` ในทั้งสอง renderer (HTML + QuestPDF
  ผ่าน resolver ตัวเดียว) · ธงบริษัทสดใช้เฉพาะ Draft · เทสต์: ใบ REC ที่ตรึง false แล้วบริษัทได้สิทธิ์ภายหลัง พิมพ์ซ้ำต้องยังเป็น "ใบเสร็จรับเงิน"

### 📋 backlog (ต้องให้เจ้าของเลือก) C-3 · CONFIRMED · P3 — สวิตช์แพลตฟอร์มชื่อ "RequirePhoR06" ยังปิดด่าน §86/6 ขายปลีกด้วย
- `AbbreviatedTaxInvoiceRule.cs:98` `if (!requirePhoR06) return None;` อยู่**ก่อน**ด่าน `NotRetailBusiness`
- หลังรอบ 199 ขายปลีก = ข้อกฎหมาย §86/6 (ทุกช่องทาง) ไม่ใช่ ภ.พ.06 · แอดมินที่ปิดสวิตช์ "บังคับ ภ.พ.06" (ตามป้าย `admin/site-settings.html:349`)
  จะเปิดให้บริษัทขายส่ง/ไม่ใช่ขายปลีกทุกแห่งออกใบกำกับอย่างย่อจากหน้าเอกสารด้วย · คอมมิตตั้งใจคงไว้ ("คงความหมายเดิม") แต่ป้ายบนหน้าแอดมินไม่บอก
- **แก้:** ย้ายเช็ค `!isRetailApproved` ขึ้นก่อนสวิตช์ (สวิตช์คุมเฉพาะส่วน ภ.พ.06) หรืออย่างน้อยแก้ป้ายแอดมินให้บอกว่าปิดทั้งสองด่าน — ต้องให้เจ้าของเลือก

### ✅ 347f2517 (ข้อความหน้าเอกสาร) · ชื่อเทสต์คงเดิม C-4 · CONFIRMED · P3 — ข้อความค้างความหมายเดิม (doc drift)
- `wwwroot/pages/documents.html:3950` คำแนะนำ "(§86/6 — ควรได้รับอนุมัติ ภ.พ.06)" สำหรับตัวเลือกใบเสร็จอย่างย่อ**จากหน้าเอกสาร** — ขัดคำตัดสิน
- `HelpContentSeeder.cs:400` ถูกต้อง (พูดถึงสลิปหน้าร้าน) · เทสต์ `AbbreviatedTaxInvoiceTitleTests.cs:130-160` ชื่อ `ไม่มีสิทธิ์_ภพ06_…` ความหมายเปลี่ยนเป็น "ไม่มีสิทธิ์ขายปลีก" (ชื่ออย่างเดียว)

### C-5 · NOT-A-BUG — ช่องทางอื่นที่พิมพ์ใบกำกับอย่างย่อ
- สลิป POS: `PosService.Orders.cs:1577` → `PosSlipHeader.Resolve` → `CashRegisterSlip` ✅ · หัวสลิปที่ส่งให้หน้าเว็บตรึงจาก `AbbreviatedInvoiceNumber`
  (`PosService.Orders.cs:2183-2186`) · `pos.html` ไม่มี literal หัวใบแล้ว
- ใบกำกับเต็มรูปจาก POS (`PosService.Orders.cs:710-781`) เป็นเอกสาร → ช่องทาง Document ถูกต้อง (มีผู้ซื้อครบ) · `IsTaxInvoiceByLaw` ผ่าน `TaxInvoiceSeriesPolicy.CarriesTaxInvoiceRole`
- ที่พัก/CMS/LINE/มือถือ/API/integration ออกเป็น `Document` แล้วพิมพ์ผ่าน `PdfGenerationService` ⇒ Document (ตรงคำตัดสิน: ภ.พ.06 คุมเฉพาะเครื่อง) · ไม่พบ
  kiosk/self-order ในโค้ด · ไม่พบ literal "ใบกำกับภาษีอย่างย่อ" ที่ข้ามตัวตัดสิน
- เลขชุด: ตอนอนุมัติ `ResolveDocumentTitleAsync` (`PdfGenerationService.cs:1546`, ช่องทาง Document) → `CarriesTaxInvoiceRole` → `SeriesTypeOverride` ใช้ตัวตัดสิน
  เดียวกับหัวกระดาษ ⇒ หัว/เลขตรงกัน **ณ ตอนออก** (ปัญหาเฉพาะตอนพิมพ์ซ้ำ = C-2) · สอง renderer เรียก `CanIssue(..., Document)` เหมือนกัน
- ฝั่งซื้อ §82/5(2): `PaperTaxInvoiceCompleteness.cs:90` · `OcrDocumentRoleInferrer.cs:357` · `EtaxPdfXmlExtractor.cs:586` ไม่ถูกแตะ ✅

---

## A) รอบ 197 ทีม K2 — สาขาผู้ขาย (`b3b4a9f3` + `ac011f6c`)

### ✅ <pending> A-1 · CONFIRMED · **P1** — K-7 ทำให้เส้นสร้างเอกสาร**สร้างผู้ติดต่อซ้ำ/ผู้ติดต่อที่เป็นตัวเราเอง**แทนที่จะกัน
- `OcrService.cs:6231-6240` `vendorIsUs = IsOurContact(ExtractedVendorTaxId, ExtractedVendorName, …)` ⇒ ถ้าจริง **ข้ามทั้งบล็อกเลือกสาขา**
  แล้วไหลไป `OcrService.cs:6291-6309`: `if (!contactId.HasValue && ExtractedVendorName)` ⇒ `new Contact { Name = ExtractedVendorName, TaxId = ExtractedVendorTaxId … }`
  โดย**ไม่ตรวจว่ามีแถวของเลขนี้อยู่แล้ว** · `contactId` = `result.MatchedContactId` ซึ่งเส้นสแกนกรองด้วยตัวตัดสินเดียวกันไว้แล้ว (`OcrService.cs:2605/2643/2667`) ⇒ มักเป็น null
- `OcrSelfPartyGuard.IsOurContact` (`OcrSelfPartyGuard.cs:56`) = `Same(taxId) || IsSelf(name)` และ `IsSelf` ถือว่า "ชื่อบนกระดาษ**สั้นกว่า**ชื่อเรา" เป็นเราเสมอ
  (ส่วนต่างติดลบ ≤ `MaxSurplus`) — ไม่ดูว่าเลขภาษีสองฝั่ง**มีและต่างกัน**
- สถานการณ์ 1 (บริษัทในเครือ): tenant "สยามพารากอน ดีเวลลอปเม้นท์" ซื้อจาก "สยามพารากอน" (เลขภาษีต่าง, valid) ⇒ `vendorIsUs = true` ⇒ ก่อน K2 เส้นสร้าง
  เอกสารยังกู้ได้ด้วย `siblingIds` ของเลขภาษี ⇒ หลัง K2 **สร้างผู้ติดต่อใหม่ทุกครั้งที่สร้างเอกสารจากสแกน** (ผู้ติดต่อซ้ำ · AP/ใบต้นทาง/PO กระจายหลายแถว)
- สถานการณ์ 2 (ตัวเราเองจริง — `[OWN-DOC]` `OcrService.cs:1274`): ผู้ใช้สร้างเอกสารฝั่งซื้อจากสแกนสำเนาใบขายของเรา ⇒ ข้ามบล็อกสาขาแล้ว**สร้าง Contact ชื่อเรา+เลขเรา**
  ซึ่งคือสิ่งที่ K-7 ประกาศว่ากัน (คอมมิต: "เส้นสร้างเอกสารสร้าง 'ผู้ขาย' … ที่เป็นตัวเราเองได้")
- เทสต์ K-7 มีแค่ระดับ helper (`OcrVendorBranchReview197Tests.cs:272-283`) ไม่มีเทสต์ว่าเส้นสร้างเอกสารทำอะไรเมื่อ `vendorIsUs` — ไม่มีเทสต์ทิศตรงข้ามเรื่องบริษัทในเครือ
- **แก้:** (1) `IsOurContact`: เลขภาษีตัดสินก่อน — ทั้งสองฝั่งมีเลข 13 หลักที่ต่างกัน ⇒ **ไม่ใช่เรา** ไม่ว่าชื่อจะซ้อนกันแค่ไหน · ชื่อใช้เฉพาะเมื่อฝั่งใดไม่มีเลข
  (2) เส้นสร้างเอกสาร เมื่อ `vendorIsUs` ห้ามตกไป fallback "สร้างผู้ติดต่อใหม่" — โยน `BusinessRuleException` ให้ผู้ใช้เลือกผู้ติดต่อ/เปลี่ยนชนิดเอกสาร
  (3) เทสต์สองทิศ: บริษัทในเครือชื่อซ้อน+เลขต่าง ⇒ ผูกแถวเดิม · เลขเรา ⇒ ไม่สร้างผู้ติดต่อ

### ✅ <pending> A-2 · CONFIRMED · P2 — `SameEntityIdsAsync` ถือเลขศูนย์ล้วน/placeholder เป็น "นิติบุคคลเดียวกัน"
- `ContactTaxBranchKey.cs:254-273` ใช้ `HasTaxId` (= มีตัวเลขสักตัว, `:308`) ไม่ใช่ `IsUsableTaxId` · ไฟล์เดียวกันบรรทัด `:351, :386-392` ระบุเองว่า
  `"0000000000000"` = ค่ามาตรฐาน "ลูกค้าทั่วไป" ของ POS หลายเจ้า ไม่ใช่เลข
- ผลของ K-3: ผู้ติดต่อทุกแถวที่เก็บ `0000000000000` (ลูกค้าทั่วไปที่นำเข้าจากระบบอื่น · ผู้ขายต่างประเทศที่กรอกศูนย์) กลายเป็น "ทุกสาขาของนิติบุคคลเดียว":
  แบนเนอร์ PO/ตัวเสนอ PO (`OcrService.cs:3024-3040, 10080-10091`) เห็น PO ของผู้ขายคนละราย · `LinkPurchaseOrderAsync` (`~:10119`) **ยอมผูก PO ของผู้ขายอื่น**
  · ตัวหาใบต้นทาง/ด่านผูกใบต้นทาง (`~:10209-10223, :10341-10352`) ฝั่งขาย: ใบเสร็จของลูกค้าทั่วไปราย A ผูกใบแจ้งหนี้ของราย B ⇒ `source.PaidAmount += …`
  (`DocumentService.cs:10055-10106` ไม่ตรวจคู่ค้า) = ตัดลูกหนี้ผิดราย
- **แก้:** `SameEntityIdsAsync` ต้องใช้เงื่อนไขเดียวกับ `IsUsableTaxId` (ไม่ใช่ศูนย์ล้วน · 13 หลักผ่าน mod-11) · แถวผู้ติดต่อ `IsWalkInCustomer` ไม่ขยาย · เทสต์ศูนย์ล้วน = ตัวเองอย่างเดียว

### ✅ <pending> A-3 · PLAUSIBLE · P2 — คะแนนสาขาตามที่มา (K-2) ไม่ย้ายตามเมื่อ `OcrPartyResolver` สลับฝั่ง
- `OcrService.cs:970-973` (และ `:1123-1126`) ย้าย `VendorBranchCode`↔`BuyerBranchCode` ตามผล `Swapped` แต่ `FieldConfidence[SellerBranchCode]`
  ที่ `EnrichFromRawText` ใส่ไว้ก่อน (`:778` → `:9783-9790`) **ไม่ถูกสลับ** ⇒ สาขาที่ตอนนี้อยู่ช่องผู้ขาย (มาจากบล็อกผู้ซื้อของกระดาษ) ถือคะแนน SellerBlock 0.85
  = ผ่านเกณฑ์สร้างผู้ติดต่อสาขาถาวร — คลาสเดียวกับ K-2 ที่ตั้งใจปิด · มีอยู่ก่อน K2 (ตอนนั้น 0.85 ทุกทาง) แต่ K2 อ้างว่า "คะแนนตามที่มา" ซึ่งไม่จริงหลังสลับ
- **แก้:** ตอน `Decision == Swapped` สลับ `FieldConfidence`/`FieldEvidence` ของคู่ช่องสาขา (และชื่อ/เลข/ที่อยู่) ไปพร้อมค่า หรือลดคะแนนสาขาทั้งสองฝั่งเป็น < 0.85 · เทสต์: ใบที่ engine ใส่เราช่องผู้ขาย

### 📋 backlog (ถามเจ้าของ) A-4 · PLAUSIBLE · P3 — ใบเสร็จร้านค้า/สลิป (ไม่มีบล็อกผู้ซื้อ) ไม่สร้างแถวสาขาอีกเลย
- `BranchCodeExtractor.cs:131` ไม่มีป้ายผู้ซื้อ ⇒ `WholePageNoBuyerBlock` 0.70 < 0.85 ⇒ ใบร้านสะดวกซื้อ/ปั๊มน้ำมันที่มีแต่ "สาขาที่ 01234" ของผู้ขาย (ไม่มีผู้ซื้อบนกระดาษ ⇒ ไม่มีสาขาอื่นให้สับสน)
  ตก `OtherBranchRow` + ข้อความให้พิมพ์ยืนยันทุกใบ · เป็นใบปริมาณสูงสุดของผู้ใช้ ⇒ UX "1-click approve" (กฎเหล็ก #3) ถอยลง · ตั้งใจตาม K-2 แต่ไม่มีเทสต์ทิศนี้
  ในชุดกระดาษจริง (คอมมิตรันเฉพาะ Makro/Radisson)
- **แก้/ถามเจ้าของ:** ใบที่ไม่มีบล็อกผู้ซื้อ**และ**มีรหัสสาขาเพียงค่าเดียวทั้งหน้า ⇒ 0.85 ได้ · เพิ่มเทสต์สลิป 7-Eleven/ปั๊ม

### 📋 backlog A-5 · PLAUSIBLE · P3 — ขยาย PO เป็นทั้งนิติบุคคลแต่ยัง `Take(5)` ⇒ auto-link ด้วยเลขบนกระดาษพลาดมากขึ้น
- `OcrService.cs:3024-3066` ลิสต์ PO ของทุกสาขาแล้วตัดเหลือ 5 ใบล่าสุดก่อนเทียบเลข PO บนกระดาษ ⇒ ผู้ขายเครือใหญ่ที่มี PO ค้าง > 5 ใบ (ทุกสาขารวม) ใบที่อ้างจริงหลุดจาก 5 ใบ = ไม่ auto-link
  (ก่อน K-3 กรองแถวเดียว โอกาสอยู่ใน 5 ใบสูงกว่า) · ข้อความแบนเนอร์ "ค้าง N ใบ" ก็ถูกเพดาน 5
- **แก้:** เทียบเลข PO บนกระดาษกับ PO เปิด**ทั้งหมด** (query แยกไม่ Take) แล้วค่อยตัดเฉพาะรายการที่แสดง

### 📋 backlog (ต้องตัดสิน) A-6 · PLAUSIBLE · P3 — ใบรับเงิน/จ่ายเงินที่ผูกต้นทางข้ามแถวสาขา ⇒ รายงานรายผู้ติดต่อเพี้ยน
- K-3 ยอมให้ Receipt/PaymentVoucher (สร้างจากสแกน) ผูก Invoice/PI ของแถว สนญ. · settlement ลด `BalanceDue` ของต้นทางถูก (JE ไม่มี ContactId) แต่ statement/ประวัติ
  รายผู้ติดต่อแสดงเงินเข้า/ออกที่แถวสาขา ขณะที่หนี้อยู่แถว สนญ. · เฉพาะ CN/DN ที่ถูกกันตาม §86/9-10 (`DocumentService.cs:14956`) ✅
- **แก้:** ตัดสินใจว่า settlement ต้องใช้ `ContactId` ของต้นทาง (สืบทอดเหมือน convert) หรือยอมรับ + บอกใน DOCUMENT_FLOW

### ✅ <pending> A-7 · CONFIRMED · P3 — doc-comment ของ `IsSelf` ถูกดึงไปติด `IsOurContact`
- `OcrSelfPartyGuard.cs:48-58` บล็อก `<summary>` ของ `IsSelf` อยู่เหนือ `IsOurContact` ⇒ `IsOurContact` มีสอง `<summary>` · `IsSelf` ไม่มีเอกสาร (build ผ่าน — ไม่มี warnings-as-errors)

### A-8 · NOT-A-BUG / ตรวจแล้ว
- K-1: `OcrCorrectedFieldList.From(correction, baseline)` มีผู้เรียกจริงจุดเดียว (`OcrService.cs:4918`) · `SubmitCorrectionAsync` มีผู้เรียกเดียว (`OcrController.cs:755`)
  · Draft-sync (`DocumentService.cs:13497-13523`) นับจากส่วนต่างอยู่แล้ว · `userTouched` ติดเฉพาะ `oninput` (hydrate ผ่าน template ไม่ยิง input event) ✅
- K-6: `IsReliableBranch(null)=false` ทิศปลอดภัย · e-Tax XML ใส่ 1.0 · `VendorKnownGoodCorrector` ไม่เติมสาขา ✅
- `OcrIssuerBranch` regex สองภาษา: ต้องมี "สาขา…ออก" ก่อน "/ Branch" ⇒ ป้ายสาขาผู้ซื้อ "สาขา/ Branch" ไม่ติด ✅
- `InternalsVisibleTo` มีแล้ว (`Accounting.csproj:82`) — เทสต์ที่เรียก `internal` คอมไพล์ผ่าน (CI build เขียว)

---

## B) รอบ 195 ทีม I3 — ฝ่ายค้านรอบสาม (merge `54fdae45`)

### ✅ <pending> (คำตัดสินเจ้าของรอบ 198 ข้อ 6: API ปฏิเสธ 422 `APPROVE-SCAN-VAT-NOT-ON-PAPER` · ทีม H) B-1 · PLAUSIBLE · **P2 (ต้องให้เจ้าของตัดสิน)** — API v1 อนุมัติใบที่ VAT ไม่ได้พิมพ์บนกระดาษได้โดยไม่มีคนรับทราบ
- `ApprovalAcknowledgement.cs:53` `ApiClient => warnings.Where(w => !IsGapWarning(w))` + `OcrApprovalGapWarning.IsGapWarning` ขยายให้รวม `VatDerivedPrefix`
  (`OcrApprovalGapWarning.cs:140-142`) ⇒ `/api/v1/documents/{id}/approve` ผ่านคำเตือน "VAT ไม่ได้พิมพ์บนกระดาษ" เงียบ ๆ แล้วคืนแค่ `scanVatNotOnPaper`
  (`DocumentsV1Controller.cs:265-268`)
- คำตัดสินเจ้าของข้อ 12 ครอบ **[Σ-GAP] (ยอดไม่ตรงกระดาษ)** · ทีม I3 ขยายไปครอบคำเตือนที่ข้อความของระบบเองบอกว่า "ภาษีซื้อต้องห้าม ม.82/5(1)" โดยอ้างข้อ 12
  ⇒ ภาษีซื้อที่ระบบแต่งเข้า ภ.พ.30 ผ่าน API ได้โดยไม่มีมนุษย์ (R1/R5 · DOCTRINE §2 write-gate) — คลาส "ขยายคำตัดสินเจ้าของเอง"
- **แก้:** ให้ `ApiClient` หยุดที่ `IsVatDerivedWarning` (ผ่านเฉพาะ `Prefix` เดิม) จนกว่าเจ้าของจะตัดสิน · บันทึกข้อนี้ใน review195-r2 §ต้องตัดสิน

### ✅ <pending> B-2 · CONFIRMED · P3 — คำเตือน VAT-derived ตัดสินจาก VAT ของ**สแกน** ไม่ใช่ของเอกสารตอนนี้
- `DocumentService.cs:18871-18874` `Classify(..., gapScan.ExtractedVatAmount, …)` · สแกนถูก sync จากเอกสาร**หลังอนุมัติ**เท่านั้น (`DocumentService.cs:13470-13525`)
  ⇒ ผู้ใช้ทำตามทางเลือก (1) "แก้ยอด VAT ตามกระดาษ" ในฟอร์มเอกสาร ⇒ คำเตือนยังขึ้นพร้อมตัวเลข VAT เก่า (`VatDerivedWarning` ปิดได้เฉพาะ `linesVat == 0`)
  · ทิศปลอดภัย (ยังต้องกดรับทราบ) แต่ข้อความผิดความจริงหลังผู้ใช้แก้ถูกแล้ว ⇒ ฝึกให้กดรับทราบโดยไม่อ่าน
- **แก้:** ส่ง VAT บรรทัดปัจจุบันเข้า `Classify` เป็น `headerVat` (หรือ Classify ทั้งสองค่า — ปิดคำเตือนเมื่อ Σ VAT บรรทัด Labelled/PrintedUnlabelled บนกระดาษ) + เทสต์ทิศ "แก้เป็นเลขบนกระดาษ ⇒ ไม่เตือน"

### ✅ <pending> B-3 · PLAUSIBLE · P3 — `RawTextContent` ว่าง ⇒ `NotOnPaper` ทุกใบที่มี VAT
- `OcrHeaderVatEvidence.Classify` ไม่มีข้อความให้ค้น ⇒ `NotOnPaper` ⇒ คำเตือนขึ้นทุกครั้งแม้ engine อ่าน VAT จากช่องโครงสร้าง · ทิศปลอดภัย ("ไม่รู้" ≠ ผ่าน) แต่ข้อความควรบอกว่า
  "ไม่มีข้อความสแกนให้ตรวจ" แทน "ไม่ได้พิมพ์บนกระดาษ" (ข้อความที่ระบุสาเหตุต้องตรวจสาเหตุนั้นจริง — F2 ข้อ 7)

### ✅ <pending> (แก้คนละทางกับที่เสนอ) B-4 · PLAUSIBLE · P3 — "ยกเลิก…แทน" ข้ามบรรทัด: `SentenceContinues` ไม่ได้ยึดท้ายบรรทัด
- `OcrDocumentRoleInferrer.cs:756` `และ|พร้อม|and|&` ติดที่ไหนก็ได้ในบรรทัด (คอมเมนต์บอก "ท้ายบรรทัด") · บรรทัดถัดไปมี "แทน" (ไม่ใช่ "ตัวแทน" เช่น "ผู้แทน" · "ใช้แทนใบเสร็จ") ⇒ นับเป็นประกาศแทนใบ
  ⇒ การพบ "ใบกำกับภาษีอย่างย่อ" จุดนั้นถูกปฏิเสธ · ผลจริงต้องเป็นสลิปที่คำว่า "อย่างย่อ" มีแค่ในประโยคท้ายใบ (หัวใบปกติยังจับได้) จึงให้ P3
- **แก้:** ยึด `(?:และ|พร้อม|and|&|,)[ \t]*$` · ตัด "ผู้แทน/ใช้แทน" เหมือน "ตัวแทน" · เทสต์ทิศตรงข้าม

### B-5 · NOT-A-BUG / ตรวจแล้ว
- สูตรถอด VAT: `OcrVatBackCalc.SplitInclusive` เป็นผู้**ผลิต**ค่าตัวเดียวในเส้น OCR · ที่เหลือ (`OcrTotalAnchor:280` · `OcrTotalDecomposer:113` · `OcrLineVatMarks:246` ·
  `VatBackCalcGuard:130`) เป็นตัว**ตรวจ** 7×/107 ที่ต่างจาก SplitInclusive ≤ 0.01 อยู่ในค่าเผื่อ · `CrossValidator.FillMissingAmounts` ไม่ถอดแล้ว · ZoneFallback ผ่าน `Plan` ✅
- `[VAT back-calc]` ≠ `[VAT skip]` (substring ไม่ชน) ✅ · ร่องรอยติดไปกับสำเนาอัปซ้ำ ✅
- ทางเข้าอนุมัติ: เว็บ/มือถือ (`MobileApiService.cs:375`) · workflow (`ApprovalService.cs:339`) · ลายเซ็น (`SignatureApprovalService.cs:285,537`) · LINE (legacy overload = `None` ⇒ หยุด+บอกผู้ใช้)
  · สร้าง+อนุมัติ OCR (`OcrController.cs:441` ⇒ `[APPROVE-FAIL]`) เดิน `CollectApprovalWarningsAsync` ตัวเดียว ✅ (ข้อยกเว้นคือ API = B-1)
- `VatBackCalcGuard.PaperDeclaresNoVat` ปฏิเสธใบห้างที่มียอดยกเว้นจริง > 0 — ถูกทิศ (ถอด 7/107 ของยอดรวมที่มีสินค้ายกเว้น = ผิดอยู่แล้ว)
- `IsVatLabelled` ใหม่: false-positive ต้องอาศัยจำนวนป้าย = จำนวนตัวเลขพอดี (`ColumnBlockMatches`) — ความเสี่ยงต่ำ ไม่พบเคสในชุดกระดาษ

---

## สรุปตามความรุนแรง

| ID | ป้าย | P | เรื่อง |
|---|---|---|---|
| C-1 | CONFIRMED | **P1** | `CompanyService.cs:271` ยังบังคับวันที่ ภ.พ.06 เมื่อติ๊กขายปลีก ⇒ คำตัดสินรอบ 199 ใช้ไม่ได้กับร้านที่ไม่มี POS |
| A-1 | CONFIRMED | **P1** | K-7: `vendorIsUs` ข้ามการเลือกสาขาแล้วตกไป "สร้างผู้ติดต่อใหม่" ⇒ ผู้ติดต่อซ้ำ (บริษัทในเครือชื่อซ้อน เลขต่าง) / ผู้ติดต่อเป็นตัวเราเอง |
| A-2 | CONFIRMED | P2 | `SameEntityIdsAsync` ถือ `0000000000000` เป็นนิติบุคคลเดียว ⇒ ผูก PO/ใบต้นทางข้ามคู่ค้า |
| C-2 | CONFIRMED | P2 | หัวเอกสารพิมพ์ซ้ำคำนวณสด ไม่ใช้ `IsTaxInvoiceByLaw` ที่ตรึง ⇒ ใบ REC เก่าพิมพ์ซ้ำเป็น "ใบกำกับภาษีอย่างย่อ" |
| ✅ B-1 | PLAUSIBLE (owner) | P2 | API v1 ผ่านคำเตือน VAT-derived โดยอ้างคำตัดสินข้อ 12 ซึ่งครอบแค่ [Σ-GAP] |
| A-3 | PLAUSIBLE | P2 | คะแนนสาขาไม่สลับตาม party swap |
| C-3, C-4, A-4..A-7, B-2..B-4 | — | P3 | ดูรายละเอียดข้างบน |

ไม่พบ compile error (CI build เขียวที่ HEAD) · เทสต์ยังไม่ถูกรันบน branch นี้ (job `dotnet test` skip)

---

## สถานะการแก้ (รอบ 199 ทีม K3 · 2026-09-28)

| ID | สถานะ | แก้ที่ / เหตุผล |
|---|---|---|
| C-1 | ✅ 347f2517 | (ทีมก่อนหน้า) ถอดด่านวันที่ ภ.พ.06 |
| A-1 | ✅ <pending> | `OcrSelfPartyGuard.IsOurContact` เลขภาษีตัดสินก่อนชื่อ (เลขสองฝั่ง valid + ต่าง ⇒ ไม่ใช่เรา) · `DecideVendorContactFallback` + `VendorContactBlockMessage` ⇒ ผู้ขายเป็นเรา/เลขมีแถวแต่ทุกแถวเป็นเรา = `BusinessRuleException` `OCR-VENDOR-IS-US` (ไม่สร้างผู้ติดต่อ) · ผู้ใช้เลือกเอง = ใช้ตามนั้นแต่ไม่เติมเลขเรา · **ไม่ได้ทำ** "ผูกแถวเดิมใน fallback" แยก: หลังแก้ `IsOurContact` บริษัทในเครือเข้าบล็อกเลือกแถวของเลขนั้นอยู่แล้ว (fallback ถึงได้เฉพาะเมื่อเลขยังไม่มีแถว) และเลขที่มีแถวแต่ทุกแถวถูกกรองว่าเป็นเรา ⇒ บล็อก ไม่สร้างซ้ำ |
| A-2 | ✅ <pending> | `SameEntityIds(Async)` ใช้ `IsUsableTaxId` + แถว walk-in ไม่ขยาย · ⚠️ `AllBranchIdsAsync` (ตัวรวมสาขาของเส้นสร้างเอกสาร/WHT) ยังใช้ `HasTaxId` — เลขศูนย์ล้วนของผู้ขายยังจับแถวศูนย์ล้วนรายอื่นในเส้นสร้างเอกสารได้ (คลาสเดียวกัน · ไม่แก้รอบนี้เพราะเปลี่ยนเป็น checksum จะทำให้แถวที่เก็บเลขพิมพ์ผิดถูกสร้างซ้ำ — ต้องตัดสินกติกา "placeholder" แยกจาก "checksum") |
| A-3 | ✅ <pending> | `OcrPartyResolver.FollowFieldConfidence` ที่ทั้งสองจุดสลับฝั่งใน `ScanAsync` (ตัดสินจากป้าย + เส้น AI) · ข้อสังเกต: `MoveSelf` ย้ายรหัสสาขาเฉพาะเมื่อช่องปลายทางว่าง — สถานการณ์ตามตัวอักษรของฝ่ายค้านเกิดเฉพาะเส้นนั้น (ล็อกด้วยเทสต์ ApplySide) |
| A-4 | 📋 backlog | ต้องถามเจ้าของ (ใบร้านค้าไม่มีบล็อกผู้ซื้อ + สาขาเดียวทั้งหน้า ⇒ 0.85?) |
| A-5 | 📋 backlog | เทียบเลข PO บนกระดาษกับ PO เปิดทั้งหมดก่อนตัด `Take(5)` |
| A-6 | 📋 backlog | ต้องตัดสินว่า settlement สืบทอด `ContactId` ของต้นทางไหม |
| A-7 | ✅ <pending> | doc-comment ของ `IsSelf` กลับที่ |
| B-1 | 📋 backlog | รอเจ้าของ (API v1 ผ่านคำเตือน VAT-derived) — รอบนี้ไม่แตะ `ApprovalAcknowledgement` · คำเตือนใหม่ `VatUncheckedPrefix` อยู่ชุดเดียวกัน (พฤติกรรม API เท่าเดิม) |
| B-2 | ✅ <pending> | `OcrHeaderVatEvidence.ClassifyPosted` (Σ VAT บรรทัดตอนนี้) ใน `CollectApprovalWarningsAsync` |
| B-3 | ✅ <pending> | `OcrHeaderVatSource.NoTextToCheck` — ตัดสินแล้วว่า **ยังเตือน/หยุดอนุมัติเอง** (DOCTRINE §1: ไม่รู้ ≠ ผ่าน) แต่ข้อความบอกสาเหตุจริง (`VatUncheckedPrefix`) |
| B-4 | ✅ <pending> | **ไม่ยึดท้ายบรรทัด** ตามที่เสนอ — หมายเหตุ Scommerce จริง ("…และออกใบกำกับภาษี\nอิเล็กทรอนิกส์ฉบับใหม่แทน") ตัดบรรทัด**หลัง**คำเชื่อม ยึด `$` จะทำให้เทสต์กระดาษจริงล้ม · ปิดความเสี่ยงที่ชี้ด้วยการตัด "ผู้แทน/ใช้แทน" เหมือน "ตัวแทน" + แก้คอมเมนต์ให้ตรงพฤติกรรม |
| C-2 | ✅ <pending> | `AbbreviatedTaxInvoiceRule.HeadingMayUseAbbreviated` ใน HTML + QuestPDF + `ResolveDocumentTitlesAsync` + `ResolveDocumentHeadingAsync` + การ์ดหัวถูกลด · ข้อจำกัด: ใบชนิด TaxInvoice มี VAT ที่ออกตอนไม่มีสิทธิ์ (ตรึง true ตามชนิด) พิมพ์ซ้ำได้หัวอย่างย่อ (TEST_PLAN ABB-14) |
| C-3 | 📋 backlog | ต้องให้เจ้าของเลือก (สวิตช์ RequirePhoR06 ปิดด่านขายปลีกด้วย) |
| C-4 | ✅ 347f2517 | ข้อความหน้าเอกสารแก้แล้ว · ชื่อเทสต์ `ไม่มีสิทธิ์_ภพ06_…` คงไว้ (ชื่ออย่างเดียว) |

เทสต์: `Accounting.Tests/OcrReview199Tests.cs` · ยังไม่ได้รัน (`dotnet test` ถูก skip บน `claude/**`)

