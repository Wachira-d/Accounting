# ฝ่ายค้านรอบ 197 — ทีม K `47156575` (merge `53907710`) · OCR ผู้ติดต่อสาขา

ตรวจแบบอ่านอย่างเดียว · บรรทัดอ้างอิง HEAD `8663946f` · ยังไม่ได้คอมไพล์ (ไม่มี SDK)

## CONFIRMED

**K-1 (P1) ด่าน "หลักฐานอ่อน ⇒ ไม่สร้างแถว" ถูกข้ามทุกครั้งบนปุ่มสร้างเอกสารของเว็บ**
`createDocFromReview` → `_persistReviewEdits` (document-scan.html:3714 → 3630) ส่ง `_buildReviewCorrection()` ทุกครั้ง โดยมี
`vendorBranchCode: v('revVendorBranch')` (3557) ซึ่งเป็นค่า non-null เสมอเมื่อช่องถูกเติมไว้แล้ว → `OcrCorrectedFieldList.From`
นับทุกช่องที่ไม่เป็น null (OcrCorrectedFieldList.cs:26–33 · OcrService.cs:4914) → `"VendorBranchCode"` เข้า `UserCorrectedFields`
แม้ผู้ใช้ไม่ได้แตะ → `IsReliableBranch(userCorrected:true)` (OcrService.cs:6240) = true ⇒ สาขาที่ขัดกับประโยคบนกระดาษ (0.50)
กลายเป็น `NewBranchRow` แล้วเกิดผู้ติดต่อใหม่ ตัวสัญญาณนี้หมายถึง "ส่งค่ามา" ไม่ได้หมายถึง "ผู้ใช้เปลี่ยนค่า" ·
แนวแก้: เทียบกับ `scan.VendorBranchCode` ก่อนนับว่าแก้ (ใน From หรือที่ SubmitCorrection) + เทสต์ทิศตรงข้าม

**K-2 (P1) สาขาของผู้ซื้อถูกอ่านเป็นสาขาผู้ขาย แล้วตอนนี้ถูกบันทึกเป็นผู้ติดต่อถาวรตั้งแต่ตอนสแกน**
`BranchCodeExtractor.Extract`: ถ้าบล็อกผู้ซื้ออยู่บน (sellerSegment ว่าง) หรือไม่มีป้ายผู้ซื้อ จะถอยไปอ่านทั้งหน้า
(BranchCodeExtractor.cs:88, 104) → ได้ "สาขาที่ 3" ของผู้ซื้อเป็นสาขาผู้ขาย แล้วถูกตีคะแนนคงที่ 0.85 (OcrService.cs:~9732)
= เท่าเกณฑ์ `ReliableBranchConfidence` พอดี ⇒ เกณฑ์ 0.85 **ไม่ได้แยก "มีป้ายฝั่งผู้ขาย" ออกจาก "ถอยไปอ่านทั้งหน้า"** ·
ก่อน K ความผิดนี้แค่ผูก สนญ. (OtherBranchRow) แต่ตอนนี้ Branch 0 (OcrService.cs:2764–2790) สร้าง Contact ก่อนผู้ใช้ยืนยัน
และลบสแกนแล้ว Contact ก็ไม่หายไปด้วย · ไม่มีเทสต์ที่ต่อ `BranchCodeExtractor` เข้ากับ `Decide` · แนวแก้: ถ้า seller==buyer
และมาจากตำแหน่งเดียวกัน หรือได้มาจากการถอยทั้งหน้า ให้ตีคะแนนต่ำกว่า 0.85

**K-3 (P1 · ทิศตรงข้าม) PO/ใบต้นทางของผู้ขายหลายสาขาหายเงียบ**
`GetOpenPosForScanAsync` กรองด้วย `d.ContactId == scan.MatchedContactId` (OcrService.cs:~10017–10025) ส่วน link PO ใช้
`po.ContactId != scan.MatchedContactId ⇒ throw` (~10063) และตัวหาใบต้นทางฝั่งซื้อก็ใช้ MatchedContactId (~10169) ⇒ PO
ที่ออกให้แถว สนญ. (ซึ่งเป็นกรณีปกติ: สั่ง สนญ. แต่สาขาเป็นผู้ออกใบกำกับ) จะไม่ถูกเสนอเลย และถ้าพยายามผูกจะ throw
`ProductAlias.ContactId` ที่เรียนไว้บนแถว สนญ. ก็ใช้กับแถวสาขาใหม่ไม่ได้ · ไม่มีเทสต์ · คำอธิบายคอมมิตข้อ 9 ไม่ได้พูดถึงเรื่องนี้

**K-4 (P2) "แก้ในฟอร์มก่อน" ยังไม่ครอบ (K ยอมรับเอง)** document-scan.html:4555 ใช้ `scan.matchedContactId` ·
`SubmitCorrectionAsync` ไม่ได้ตัดสินผู้ติดต่อใหม่เมื่อรหัสสาขาเปลี่ยน ⇒ สแกนเก่า และสแกนที่ผู้ใช้แก้สาขาในหน้ารีวิว ยังผูก สนญ.

## PLAUSIBLE

- **K-5 (P2) race ตอนอัปโหลดพร้อมกัน**: หน้าเว็บอัปโหลด 3 ไฟล์พร้อมกัน (`_maxParallelUploads: 3`, document-scan.html:874)
  และไม่มี unique index บน (CompanyId, TaxId, BranchCode) ⇒ ใบ Makro 00005 สองใบที่อัปโหลดพร้อมกันได้แถวสาขาซ้ำกัน
  (ต้นเหตุเดียวกับ Branch 1/2 เดิม · K ขยายให้กว้างขึ้น)
- **K-6 (P2)** `IsReliableBranch(null)` = true (OcrVendorBranchContact.cs:74) ขัดกับ DOCTRINE §1 ("ไม่มีข้อมูล ห้ามตกเป็นผ่าน")
  กรณีที่ไม่มีคะแนน: รหัสสาขาที่ `ParseThaiDocument` ใส่ใน Tesseract (บรรทัด ~5630) · สแกนเก่าตอนสร้างเอกสาร
- **K-7 (P3)** เส้นสร้างเอกสารไม่มี `IsOurOwnContact` (ต่างจากเส้นสแกน 2775): เมื่อ vendor tax = เลขเรา และ tenant มีแถวของตัวเอง
  จะสร้าง "ผู้ขาย" สาขาหนึ่งที่เป็นตัวเราเอง
- **K-8 (P3) อีเมลท้ายใบถูกทิ้ง**: `RecipientLabels` จับ "ผู้รับสินค้า"/"Receiver" ได้ทุกตำแหน่ง (OcrPartyLabels.cs:114,118) ซึ่งเป็นช่องลายเซ็น
  ท้ายบิลด้วย ⇒ อีเมล/เบอร์ของผู้ขายที่พิมพ์ใต้ช่องลายเซ็นภายใน 10 บรรทัดถูกนับเป็นของผู้ซื้อ ผลคือได้ null ไม่ใช่ค่าผิด
  (ทิศปลอดภัย แต่หายเงียบ) · เทสต์ footer ครอบเฉพาะกรณีที่ห่างเกิน 10 บรรทัด
- **K-9 (P3)** ที่อยู่ของแถวสาขาในเส้นสร้างเอกสาร (6242–6245) ส่ง `paperIsIssuerBranchAddress:false` แต่ `ContactAddress` ยังคืน
  ที่อยู่จากกระดาษเมื่อสาขาตรง ⇒ อาจได้ที่อยู่ สนญ. จากหัวกระดาษ ต่างจากเส้นสแกน · ขัดกับคอมเมนต์ "ไม่รู้ = ปล่อยว่าง"

## NOT-A-BUG
- tenant: ทุก query ใหม่กรอง CompanyId (taxMatches · AllBranchIdsAsync · template · knownGood · boundContact)
- `LooseGroupingPattern`: รหัสสมาชิก 14 หลัก `00596437340722` ไม่ถูกจับเพราะ lookbehind/lookahead กัน · ต้องมีป้ายกำกับ + mod-11 + ผ่านด่านบาร์โค้ด
- `FindAboveTaxId`: ถ้าเลขอยู่ในบล็อกผู้ซื้อ คืน null · ตัดชื่อเรา · หยุดที่บรรทัดนิติบุคคลที่ใกล้ที่สุด · `PickKnownLegalName` ใช้เฉพาะข้อมูลใน tenant
  (ความเสี่ยงที่เหลือเล็กน้อย: บรรทัดผู้รับ/ผู้จัดส่งที่เป็นบุคคลที่สามและอยู่เหนือเลข เพราะ RecipientLabels ไม่ถูกส่งเข้า InBuyerBlock)
- ใบ สนญ./สาขาตรง และลูกค้าที่มีแต่แถวไม่ระบุสาขา: `PickBranch` ยังผูกแถวเดิม (ContactTaxBranchKey.cs `wanted==HeadOffice → unspecified` และ fallback)
- สถานะปลายทาง: K ไม่ได้ประทับสถานะเอง · `MatchContactAsync` ถูกเรียกจากปุ่มของผู้ใช้เท่านั้น (OcrController.cs:747)
- ทางเข้าอื่น: LINE และ API v1 ผ่าน `ScanAsync` → `CreateDocumentFromScanAsync` ตัวเดียวกัน (OcrService.cs:8631) · MobileController ไม่แตะ OCR
- I2/I3: merge ไม่มี conflict ในโค้ด (conflict มีเฉพาะ md) · hunk ของ I2 (VatBackCalcGuard) อยู่คนละช่วงกับของ K · file:line ใน DOCUMENT_FLOW ที่ K เขียนเลื่อนไปราว 8 บรรทัด
- ความเสี่ยงคอมไพล์: สัญลักษณ์ที่อ้างถึงมีอยู่จริงทุกตัว (`NameMatchKind` · `SoftScope` · `AdoptTaxId` · `ContactAdoptOutcome.Reject` ·
  `OcrFieldSource.VendorHistory/PaperLabel` · `ContactType.Unknown` · `TaxIdCandidate.Position` · `ParseFieldConfidenceJson`)
  ไม่พบจุดที่น่าจะพัง — ต้องรอผล CI
