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

## สถานะหลังทีม K2 (2026-09-28 · คอมมิต WIP `b3b4a9f3` + คอมมิตตามหลัง)

| ID | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|
| K-1 | ✅ แก้ | `OcrCorrectedFieldList.From(req, OcrCorrectionBaseline)` — รหัสสาขาสองฝั่งนับว่าแก้เฉพาะเมื่อค่าเปลี่ยนจากที่สแกนเก็บไว้ (normalize 5 หลัก · ว่าง≠00000) หรือ `VendorBranchConfirmed` (ช่อง `revVendorBranch` ถูกพิมพ์ — ทางยืนยันสาขาที่ระบบไม่แน่ใจ) · `SubmitCorrectionAsync` จับ baseline ก่อนรับคำแก้ (ล็อกด้วย `required_call_site_check`) |
| K-2 | ✅ แก้ | `BranchCodeExtractor.Result.SellerEvidence/SellerConfidence` (ประโยคประกาศ 0.90 · บล็อกผู้ขาย 0.85 · ไม่มีป้ายผู้ซื้อ 0.70 · ถอยอ่านทั้งหน้า 0.60 · เท่ารหัสผู้ซื้อ 0.40) ใช้ทั้ง `EnrichFromRawText` และ `ParseThaiDocument` · **ทิศตรงข้าม**: ใบ Makro จริง (`MakroPhoto`) เดิมได้ 00005 จาก**การถอยอ่านทั้งหน้า** (ตัวอ่านประโยคประกาศไม่รู้จัก “/ Branch”) ⇒ ถ้าลดคะแนนอย่างเดียวจะหยุดสร้างแถวสาขาของผู้ใช้ ⇒ `OcrIssuerBranch` รับป้ายสองภาษา (“/ Branch” · “(Branch No.)”) · เทสต์ต่อ extractor → `IsReliableBranch` → `Decide` |
| K-3 | ✅ แก้ (ยกเว้น alias) | `ContactTaxBranchKey.SameEntityIdsAsync` (ตัวเอง + เลขภาษีเดียวกัน · ไม่มีเลข = ตัวเอง · CompanyId) ใน 5 จุด: แบนเนอร์ PO ตอนสแกน (จุดที่ 5 ที่ฝ่ายค้านไม่ได้ระบุ) · `GetOpenPosForScanAsync` · ด่าน `LinkPurchaseOrderAsync` · `ComputePredecessorDecisionAsync` · ด่าน `LinkPredecessorAsync` · ใบลด/เพิ่มหนี้ไม่ขยาย (§86/9-10 + `DocumentService` โยนเมื่อ ContactId ต่าง) |
| K-3b | 📋 backlog | `ProductAlias.ContactId` รายแถว — alias ที่เรียนบนแถว สนญ. ไม่ช่วยแถวสาขา · ต้องเปลี่ยนลายเซ็น `ProductMatcher` (vendorContactId → ชุด id) + raw SQL `GetVendorHistoryProductIdsAsync` + `GetVendorAdaptiveThresholdAsync` + `vendorHasProductHistory` — ไม่ใช่การแก้เล็ก |
| K-4 | 📋 backlog | "แก้ในฟอร์มก่อน" (document-scan.html `contactId: scan.matchedContactId`) + `SubmitCorrectionAsync` ไม่ตัดสินผู้ติดต่อใหม่เมื่อรหัสสาขาเปลี่ยน — เส้นกดสร้างเอกสารตัดสินซ้ำแล้ว; เส้นฟอร์มยังไม่ |
| K-5 | 📋 backlog | race อัปโหลดพร้อมกัน — ไม่มี unique index (CompanyId, TaxId, BranchCode) · ต้องตัดสินกับข้อมูลซ้ำที่มีอยู่ก่อน (migration) |
| K-6 | ✅ แก้ | `IsReliableBranch(null)` = false ⇒ `OtherBranchRow` ผูกแถวเดิม + ข้อความ (เส้นสร้างเอกสารใส่ `ProcessingNotes`) · ผู้ใช้ยืนยัน ⇒ สร้าง · เส้น Tesseract/python ได้คะแนนแล้ว (ไม่ตกเป็น null) · e-Tax 1.0 ไม่กระทบ |
| K-7 | ✅ แก้ | `OcrSelfPartyGuard.IsOurContact` ตัวเดียว (เส้นสแกนเปลี่ยน local function ให้เรียกตัวนี้) · เส้นสร้างเอกสาร: ผู้ขายคือเรา ⇒ ไม่ตัดสินสาขา/ไม่สร้างแถว · แถวของเราไม่เป็นผู้สมัคร |
| K-8 | 📋 backlog | อีเมลใต้ช่องลายเซ็น "ผู้รับสินค้า" ท้ายบิลถูกทิ้ง (ทิศปลอดภัย: null ไม่ใช่ค่าผิด) |
| K-9 | 📋 backlog | ที่อยู่แถวสาขาในเส้นสร้างเอกสาร (`paperIsIssuerBranchAddress:false`) อาจได้ที่อยู่ สนญ. จากหัวกระดาษ — ต้องเก็บ `VendorAddressFromIssuerBranch` ลงสแกนก่อน (ยังไม่มีคอลัมน์) |
| K-10 | 📋 ใหม่ · ให้เจ้าของตัดสิน | ช่อง WHT (`hasWht`/`whtIncomeTypeCode`) หน้าเว็บส่งทุกครั้ง ⇒ `OcrWhtLearningScope.UserEdited` จริงเสมอบนเว็บ — แต่นิยามเดิมนับ "ยืนยัน" เป็นหลักฐานโดยตั้งใจ ⇒ ไม่เปลี่ยนเอง |
| K-11 | 📋 ใหม่ | ใบ Makro (`MakroPhoto`): บล็อกผู้ซื้อของ `BranchCodeExtractor` เริ่มที่ "ต้นฉบับลูกค้า" (รายการคำของตัวเองยังไม่กลบป้ายฉบับ) + สองคอลัมน์สลับ ⇒ `BuyerBranchCode` = 00005 (ควร 00000) |

หมายเหตุ verify: "file:line ใน DOCUMENT_FLOW ที่ K เขียนเลื่อนไปราว 8 บรรทัด" — K ไม่ได้เขียน file:line ตัวเลขใน DOCUMENT_FLOW เลย (ตรวจ `git show 47156575`) · K2 เติม file:line ปัจจุบันให้ส่วน OCR ผู้ติดต่อสาขาแทน

## สถานะหลังรอบ 200 ทีม K (2026-09-29 · คำตัดสินเจ้าของข้อ 19 · รายงาน `erp-review/2026-09-29/team-K.md`)

| ID | สถานะ | ที่แก้ / เหตุผล |
|---|---|---|
| K-3b | ✅ 53dc5921 | ฝั่ง**อ่าน** alias/คำปฏิเสธ/ประวัติซื้อ/เกณฑ์ยอมรับอัตโนมัติ/คำแนะนำนำเข้าสต๊อก = ทุกแถวของนิติบุคคลเดียวกัน (`ProductMatcher.VendorEntityIdsAsync` → `ContactTaxBranchKey.SameEntityIdsAsync` + `Helpers/OcrVendorAliasScope`) · ฝั่ง**เขียน**ยังผูกแถวของใบนั้น · ไม่ต้องเปลี่ยนลายเซ็น `MatchAsync` (ขยายภายใน + แคชต่อคำขอ) |
| K-4 | ✅ 53dc5921 | `DecideScanVendorBranchContactAsync` ตัวเดียวของเส้นสร้างเอกสาร + `SubmitCorrectionAsync` · ตัดสินใหม่เมื่อ `OcrVendorBranchContact.ShouldRedecideOnCorrection` (กุญแจผู้ขายเปลี่ยนจริง · ไม่ได้เลือกผู้ติดต่อเอง · ยังไม่สร้างเอกสาร · ฝั่งซื้อ) ⇒ "แก้ในฟอร์มก่อน" อ่าน `MatchedContactId` ใหม่หลังบันทึกคำแก้ (document-scan.html ไม่ต้องแก้ — โหลดผลสแกนใหม่อยู่แล้ว) |
| K-5 | ✅ 53dc5921 | คำตัดสินข้อ 19: `pg_advisory_xact_lock` ต่อ (CompanyId, เลขผู้เสียภาษีตัวเลขล้วน) (`AdvisoryLockKey.OcrContactCreate` · `Helpers/OcrContactCreateLock`) ในธุรกรรมสั้นของ 3 เส้น (สแกน · สร้างเอกสาร · แก้ผลสแกน) + ถามคีย์กลางซ้ำใต้ล็อก · ไม่เพิ่ม unique index · แถวซ้ำเดิมรายงานในหน้า contact-hygiene (`ContactDataHygiene.DuplicateKeyGroups` — คีย์เดียวกับแถบเตือนหน้าผู้ติดต่อ) ไม่รวมอัตโนมัติ |
| K-8 | ✅ 53dc5921 | `Helpers/OcrSignatureSlot` — บรรทัดของป้ายผู้รับที่มีคำลงนาม/เส้นให้เซ็น/บทบาทผู้ลงนาม ≥ 2/อยู่ใต้เส้นเซ็น = ช่องลายเซ็น ⇒ `OcrPartyLabels.FindRecipientAll` ไม่นับ · Makro "ชื่อผู้รับสินค้า/ Receiver" ยังเป็นบล็อกผู้ซื้อ |
| K-9 | ✅ 53dc5921 | เส้นสร้างเอกสาร: ที่อยู่แถวสาขาใหม่ = ที่พิสูจน์ได้เท่านั้น (`OcrIssuerBranch.StoredAddressIsIssuerBranch` — คำนวณจากข้อความกระดาษที่เก็บไว้ด้วยตัวอ่านเดียวกับเส้นสแกน ⇒ ไม่ต้องเพิ่มคอลัมน์) หรือผู้ใช้**เปลี่ยน**ที่อยู่เอง (`OcrTextBaseline`) · ไม่รู้ = ว่าง + ข้อความให้เติม |
| K-10 | ✅ 53dc5921 | คำตัดสินข้อ 19: `OcrWhtBaseline` — HasWht/WhtRate/WhtIncomeTypeCode นับว่าแก้เฉพาะเมื่อเปลี่ยนจากที่สแกน · แถวเดิมที่มี "HasWht" จากการส่งซ้ำของเว็บแยกไม่ได้ว่าอันไหนแก้จริง ⇒ ไม่ migrate (ดูคำถามค้างในรายงานทีม) |
| K-11 | ✅ 53dc5921 | `BranchCodeExtractor` อ่านสาขาผู้ซื้อบนข้อความที่กลบป้ายฉบับ (`OcrPartyLabels.MaskCopyNoise`) + ประโยคประกาศสาขาผู้ออกใบ (`OcrIssuerBranch.MaskStatements`) · จุดแบ่งฝั่งผู้ขายไม่ขยับ · จำลองชุดกระดาษในเทสต์ 77 ใบ: เปลี่ยน 1 ใบ (MakroPhoto 00005 → 00000) |
