# ทีม C — OCR → เอกสาร (ตั้งแต่อัปโหลดจนได้เอกสาร + JE)

รอบ 189 (2026-09-21) · ขอบเขต: `Services/Implementations/OcrService.cs` ·
`Services/Implementations/Ocr/SmartFieldExtractor.cs` · `Helpers/Ocr*.cs` ·
`Services/Ai/` ส่วน OCR · `Controllers/OcrController.cs` · `Controllers/V1/OcrV1Controller.cs` ·
`wwwroot/pages/document-scan.html` + ผู้บริโภค handoff `wwwroot/pages/documents.html`

ทุกข้อเปิดไฟล์ยืนยันเองตรงบรรทัด · `bash tools/check_all.sh` = เขียวทั้งหมด (checker 43 · sim 3 ·
`ocr_helper_test_check` = 32 คลาส ไม่มีเทสต์ 0 · `dead_helper_check` = ไม่มีตัวใหม่)

## สรุป 5 บรรทัด

1. **§86/4 กลับมาครบแล้วในหน้า review** (ช่องครบ 24 ช่อง · payload builder ส่งครบ · server persist ครบ)
   — ช่องโหว่ที่เหลือไม่ได้อยู่ที่ "ช่องว่าง" แต่อยู่ที่ **ฝั่งขาย** ซึ่งทุกเส้นยัง treat คู่ค้าเป็น "ผู้ขาย" เสมอ
2. **เส้น 1-click ที่ไม่เปิด review modal (ปุ่มบนการ์ด · LINE/มือถือ · `/api/v1` autoCreate) ยังไม่เท่าเส้น modal**
   ทั้งเรื่องการหาคู่ค้า · ข้อความล้มเหลว · การปิดลูปเรียนรู้ — เป็นรากร่วมของ 4 ข้อในตาราง
3. **ลูปเรียนรู้ปิดไม่ครบ 2 จุด**: `OurRoleAiFeedbackId` ปิดเฉพาะเส้น `SubmitCorrection` · `/api/v1 confirm`
   บันทึก `acceptedAi: false` **ทุกครั้ง** พร้อมคอมเมนต์ที่อ้างว่า recorder ตัดสินให้ (ไม่จริง — recorder เก็บค่าที่ส่งมาตรง ๆ)
4. **feature parity (กฎเหล็ก #1 ข้อ 2) ผ่านครบแล้ว** — ทุก `AiFeatureKey` ของสาย OCR มี student register
   (รวม `OcrLineItemSplit` → `LineSplitDistillationModel` ⇒ backlog **T3-02 ปิดแล้ว**) · kill-switch ไม่พบจุดตาย
5. **ตัวตัดสินตัวเลข/ธงที่ยังฝังใน `OcrService.cs` ยังไม่มีเทสต์เลย 25 เมธอด** และ
   `tools/ocr_helper_test_check.py` สแกนเฉพาะ `Helpers/Ocr*.cs` ⇒ **checker เขียวโดยไม่ได้วัดไฟล์ที่การถดถอยเกิดจริง**

---

## ตาราง finding

| ID | P | หัวข้อ | file:line | ผู้ใช้เห็นอะไร | ทำไมถึงผิด | ทางแก้ที่เสนอ |
|----|---|--------|-----------|----------------|-------------|----------------|
| C-01 | P1 | ฝั่งขาย: หาลูกค้าด้วย `Name.Contains()` ดิบ ไม่มีความยาวขั้นต่ำ ไม่ดู `IsCustomer` ไม่มีลำดับ | `Accounting/Services/Implementations/OcrService.cs:5786-5790` | สแกนใบขาย/ใบที่เราเป็นผู้ขาย แล้ว OCR อ่านชื่อผู้ซื้อ**ไม่ครบ** (ทรงเดียวกับ RG-03 "แอม แฮปปี้" ที่ตัดจาก "หจก. แอม แฮปปี้เนส") → เอกสารถูกสร้างให้ **ลูกค้าคนอื่น** เงียบ ๆ · ชื่อผู้ซื้อบนใบกำกับ §86/4 ผิด · AR ไปลงคู่ค้าผิดราย | `.Where(c => c.Name.Contains(buyerNm)).FirstOrDefaultAsync()` — ชิ้นส่วนสั้น ("บจก" · "จำกัด" · "บริษัท") แมตช์ได้หมด · ไม่กรอง `IsCustomer` ⇒ **ผู้ขาย**ถูกเลือกเป็นลูกค้าของใบขายได้ · ไม่มี `OrderBy` ⇒ ผลไม่คงที่ · ฝั่งซื้อในเมธอดเดียวกัน (`:5827-5838`) ใช้เลขภาษี normalize + สร้างใหม่ ไม่เคย fuzzy — **สองครึ่งของเมธอดเดียวกันคนละมาตรฐาน** (หลักการ 1 "คู่สมมาตรต้องอ่านอีกฝั่งทันที") | ยุบเป็น pure helper เดียว `Helpers/OcrCounterpartyMatch` ที่ทั้งสองฝั่งเรียก: เลขภาษี (normalize) → ชื่อ **เท่ากันหลัง normalize** หรือ superstring (กติกาเดียวกับ `OcrPartyName.ExpandTruncated`) → ไม่เจอ = สร้างใหม่ · บังคับ `IsCustomer`/`IsSupplier` ตามฝั่ง · เทสต์สองทิศ (ใบที่เคยจับคู่ถูกต้องไม่ถูกแตะ + ชิ้นส่วนสั้นต้องไม่แมตช์) |
| C-02 | P1 | "แก้ในฟอร์มก่อน" ส่ง **ผู้ขาย** เป็นคู่ค้าเสมอ แม้ใบเป็นฝั่งขาย | `Accounting/wwwroot/pages/document-scan.html:4525-4527` (ผู้บริโภค `Accounting/wwwroot/pages/documents.html:2035-2045`) | ใบที่ `ourRole = Seller` → กดปุ่มที่ระบบเองติดป้ายว่าแนะนำ → ฟอร์มรายได้เปิดขึ้นโดยช่อง "ผู้ติดต่อ" ถูกเติมด้วย **ชื่อบริษัทเราเอง** (หรือว่างพร้อม placeholder "กรุณาคลิกเลือกผู้ติดต่อ") · ชื่อ/เลขภาษีผู้ซื้อที่ผู้ใช้เพิ่งตรวจในหน้า review **ไม่ถูกส่งไปเลย** ⇒ ต้องพิมพ์ใหม่ทั้งชุด (ขัดกฎเหล็ก #3 ข้อ 4) | handoff hardcode `contactName: scan.extractedVendorName` · `contactTaxId: scan.extractedVendorTaxId` · `contactId: scan.matchedContactId` ทั้งที่ `OcrService.cs:5771-5773` เขียนคอมเมนต์ไว้ตรง ๆ ว่า "MatchedContactId points at the vendor side, which on a sales doc is ourselves — **never use it as the primary pick here**" · ในฟังก์ชัน `openInDocumentForm` ทั้งฟังก์ชัน (`:4374-4640`) ไม่มีคำว่า `buyer` อยู่เลยสักจุด ⇒ เส้นสร้างเอกสารสองเส้นตัดสินคู่ค้าคนละกติกาบนใบเดียวกัน | ให้ handoff อ่านฝั่งจาก `side` ที่คำนวณไว้แล้วบรรทัด `:4419`: `side === 'revenue'` ⇒ ใช้ `scan.buyerName` / `scan.buyerTaxId` / `scan.buyerAddress` / `scan.buyerBranchCode` · ระยะยาวย้ายการตัดสิน "คู่ค้าคือใคร" ไปเซิร์ฟเวอร์ (คืนมากับ `line-preview` หรือ DTO) ตามกติกา "server computes · page displays" |
| C-03 | P1 | สร้างเอกสารจากการ์ดแล้วล้มด้วยข้อความ generic ที่ไม่บอกทางไปต่อ | `Accounting/Services/Implementations/OcrService.cs:5873` (และ `:5621` · `:5630`) · ตัวปิดบัง `Accounting/Middleware/ExceptionMiddleware.cs:113` | สแกนที่ OCR อ่านชื่อผู้ขายไม่ออก (ภาพเบลอ · เส้น Tesseract · ใบต่างประเทศ) → กด "สร้างเอกสาร" บนการ์ด → toast แดง **"สร้างเอกสารไม่สำเร็จ: ไม่สามารถดำเนินการนี้ได้ในสถานะปัจจุบัน"** — ไม่บอกว่าขาดคู่ค้า ทั้งที่หน้า review มีช่องค้นหา/ผูกคู่ค้าอยู่แล้ว (`match-contact`) ⇒ ผู้ใช้ตันสนิท | โยน `InvalidOperationException` พร้อมข้อความ **ภาษาอังกฤษ** ⇒ `LooksUserFacing()` (ตรวจว่ามีอักษรไทยไหม) คืน false ⇒ middleware ปิดบังเป็นประโยคกลาง · ขัดกฎเหล็ก #4 E ตรง ๆ ("Business error โยน exception ชนิดเฉพาะ ข้อความไทยถึงผู้ใช้ได้ — อย่าใช้ `InvalidOperationException`") · เมธอดเดียวกันใช้ `BusinessRuleException` ถูกต้องแล้ว 3 จุด (`:5633` · `:5650` · `:5765`) — เหลือ 3 จุดนี้ที่ไม่ได้ตาม | เปลี่ยน 3 จุดเป็น `BusinessRuleException` ข้อความไทยที่บอก**ทางไปต่อ**: "ยังไม่รู้ว่าคู่ค้าคือใคร (OCR อ่านชื่อผู้ขายไม่ได้) — เปิดการ์ดนี้ แล้วเลือก/สร้างคู่ค้าในช่อง 'ผู้ติดต่อ' ก่อนกดสร้างอีกครั้ง" · ทิศเดียวกับ `validation_field_label_sim.js` (ข้อความต้องชี้ป้ายไทยที่ผู้ใช้เห็น) |
| C-04 | P1 | `/api/v1 confirm` บันทึก `acceptedAi: false` **ทุกครั้ง** — คอมเมนต์อ้างว่า recorder ตัดสินให้ (ไม่จริง) | `Accounting/Controllers/V1/OcrV1Controller.cs:161-166` เทียบกับ `Accounting/Services/Ai/AiFeedbackRecorder.cs:268` (`row.UserAcceptedAi = acceptedAi;`) | ลูกค้าที่ใช้ Connected API อย่างเดียว: หน้า "รายงานการใช้ AI" / แดชบอร์ดแอดมิน แสดง **"AI ถูกต้อง 0%" ถาวร** ไม่ว่าพาร์ตเนอร์จะยืนยันคำตอบเดิมของ AI กี่ครั้ง — ตัวชี้วัด "UsedAi ลดลงเรื่อย ๆ" (กฎเหล็ก #1 ข้อ 6) อ่านไม่ได้สำหรับช่องทางนี้ | `RecordUserChoiceAsync` **ไม่เคย**เทียบกับ `row.AiPrimaryAnswer` — เก็บค่าที่ผู้เรียกส่งมาตรง ๆ (override เฉพาะ sentinel) ⇒ คอมเมนต์ที่ `:163-164` เป็นเท็จ (หลักการ 7: ข้อความที่ระบุ "สาเหตุ" ต้องตรวจสาเหตุนั้นจริง) · เส้นเว็บทำถูกอยู่แล้วโดยเรียก `Helpers/OcrAiLabelScope.AcceptedAi(aiAnswer, chosen)` (`OcrService.cs:6318-6321` · `:4753`) ⇒ **สองทางเข้าให้ label คนละแบบกับเหตุการณ์เดียวกัน** · ผู้อ่านผลกระทบ: `AiAdminController.cs:346,456` · `AiUsageReportService.cs:220` · `VendorCanonDistillationModel.cs:95` | ให้ `AiFeedbackRecorder.RecordUserChoiceAsync` มี overload ที่**คำนวณเอง**จาก `row.AiPrimaryAnswer` ผ่าน `OcrAiLabelScope.AcceptedAi` เมื่อผู้เรียกไม่รู้ (หรือให้ V1 เรียก helper ตัวนั้นก่อนส่ง) · แก้คอมเมนต์ให้ตรงความจริงในคอมมิตเดียวกัน · เทสต์: ยืนยันค่าเดิมของ AI ⇒ `UserAcceptedAi == true` |
| C-05 | P2 | คำตอบ AI เรื่อง **ผู้ซื้อ** ที่ผ่านด่านเซิร์ฟเวอร์แล้ว ถูกทิ้งเงียบที่หน้าเว็บ | `Accounting/Helpers/OcrReviewGuard.cs:93` + `:121` (accept `buyer_tax_id` / `buyer_name`) เทียบกับ `Accounting/wwwroot/pages/document-scan.html:3427-3467` (`_applyAiCorrections` เขียนแค่ 7 ช่อง) | กด "🤖 ตรวจสอบกับ AI" บนใบที่ระบบอ่านชื่อ/เลขผู้ซื้อผิด — AI เสนอค่าที่ถูก · เซิร์ฟเวอร์ตรวจผ่าน (mod-11 + ต้องปรากฏบนกระดาษ) · แต่ **ไม่มีอะไรเกิดขึ้นบนจอเลย** ไม่เขียนลงช่อง ไม่โชว์เป็นคำแนะนำ ⇒ ผู้ใช้สรุปว่า "AI ไม่ช่วยเรื่องนี้" แล้วพิมพ์เอง | คอมเมนต์ที่ `:3429-3431` ("Buyer-name/tax-id fields aren't in the current review form — when AI returns them we surface as a note in the verdict panel instead") **ล้าสมัยทั้งสองท่อน**: ฟอร์มมี `revBuyerName` (`:2569`) · `revBuyerTaxId` (`:2547`) มาตั้งแต่ E-OCR-05 แล้ว · และ `_renderAiVerdict` (`:3469-3495`) ไม่เคยแสดงช่องพวกนี้ ⇒ "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" ฝั่งเซิร์ฟเวอร์ (หลักการ 2) | เพิ่ม `set('revBuyerName', c.buyer_name)` · `set('revBuyerTaxId', c.buyer_tax_id)` ใน `_applyAiCorrections` (ด่าน `userTouched` เดิมครอบให้อยู่แล้ว) · ลบคอมเมนต์ที่ไม่จริง · ระยะยาว: ให้ชื่อช่องฝั่งเซิร์ฟเวอร์ (snake_case) แมปกับ id ฟอร์มจาก**ตารางเดียว** แทนการไล่เขียน `set(...)` ทีละบรรทัด (ตารางแบบนี้มีอยู่แล้วที่ `:2866-2880`) |
| C-06 | P2 | เส้น 1-click ไม่ปิดลูป `DocumentRoleInference` — นักเรียนเรียนแต่จากใบที่เปิด modal | `Accounting/Services/Implementations/OcrService.cs:6303-6325` (ปิดเฉพาะ `TargetDocTypeAiFeedbackId` + `AiSuggestionFeedbackId`) เทียบกับ `:4766-4771` (`SubmitCorrectionAsync` ปิด `OurRoleAiFeedbackId`) | ผู้ใช้ที่ทำงานแบบ bulk (กด "สร้างเอกสาร" บนการ์ดโดยไม่เปิด review — `document-scan.html:1760-1768`) หรือใช้ LINE/มือถือ/`/api/v1 autoCreate`: ระบบเดาบทบาทผู้ซื้อ/ผู้ขายถูกทุกใบ แต่ **ไม่เคยได้ตัวอย่าง "ยอมรับ" สักแถว** ⇒ นักเรียน `DocumentRoleInference` เรียนจากใบที่ถูกแก้อย่างเดียว = selection bias (ปัญหาเดิมที่ T3-03 แก้ให้ชนิดเอกสาร/คู่ค้าไปแล้ว) ⇒ ยิ่งใช้ ยิ่งเดาบทบาทแย่ลง | บล็อก CAPTURE ฝั่ง "ยอมรับ" ถูกเขียนตอนที่ระบบยังไม่มี `OurRoleAiFeedbackId` (เพิ่มทีหลังใน `7bac95b`) และไม่มีใครกลับมาเติม — "แก้ที่หนึ่ง เหลือที่เหลือ" · `AutoCreateDocumentAsync` (`:7711-7742`) delegate มาที่เมธอดนี้ ⇒ ทุกช่องทางที่ไม่ผ่าน modal ตกหมด | เพิ่มอีก 1 บล็อกในชุดเดียวกัน: `if (result.OurRoleAiFeedbackId is Guid rFid && result.OurRole is string role) RecordUserChoiceAsync(rFid, role, OcrAiLabelScope.AcceptedAi(result.OurRoleAiSuggested, role), …, Implicit)` · ตรวจพร้อมกันว่ายังมี `*AiFeedbackId` ตัวไหนบน `OcrScanResult` ที่ไม่มีคนปิดลูปอีก (`LineSplitAiFeedbackId` ปิดที่ `:7440` แล้ว) |
| C-07 | P2 | สองปุ่มในหน้าสแกนยังเขียน `fetch()` เองแล้วเรียก `r.json()` ดิบ — ข้าม `API.request` ทั้งตัว | `Accounting/wwwroot/pages/document-scan.html:2131-2151` (register-asset) · `:3747-3761` (create-journal-entry) | เซสชันหมดอายุระหว่างเปิดหน้า review ค้างไว้นาน (เรื่องปกติของงานตรวจเอกสาร) → กด "ลงทะเบียนสินทรัพย์" หรือ "🧾 บันทึก JE เท่านั้น" → ได้ toast แดงสั้น ๆ แล้ว**ค้างอยู่หน้าเดิม** ไม่ถูกพาไป login (ทุกปุ่มอื่นในระบบพาไป) กดซ้ำกี่ครั้งก็เหมือนเดิม · ฟีเจอร์ที่ไม่อยู่ในแพ็กเกจ (403 `FEATURE_NOT_AVAILABLE`) ก็ไม่ถูกพาไปหน้าอัปเกรด | §2l (รอบ 175) แก้ปัญหานี้ให้ `ocrUploadAndScan` ใน `js/api.js` แต่ **ไม่ได้ `grep "fetch("` ในหน้าเว็บที่เรียก endpoint เดียวกัน** ⇒ เหลือสองเส้นที่ข้าม 401-redirect · 403-subscription · non-JSON · network-error ที่ `API.request` (`js/api.js:103-200`) จัดการไว้หมดแล้ว — "ตัวกลางมีอยู่ ไม่พอ ต้องทุกเส้นเดินผ่านจริง" (หลักการ 2) | เปลี่ยนทั้งสองเป็น `API.post(...)` · เพิ่ม checker เล็ก ๆ (scope แคบ = `wwwroot/pages/*.html`) ที่ฟ้อง `fetch('/api/` ที่ไม่ได้อยู่ใน `js/api.js` — กันไม่ให้เกิดตัวที่สาม |
| C-08 | P2 | โหลดตารางประเภทเงินได้ ม.40 ล้ม → ทุกการบันทึก **ล้างค่า** ที่เคยเลือกไว้เงียบ ๆ | `Accounting/wwwroot/pages/document-scan.html:1428-1440` (`catch { this._whtIncomeTypes = []; }` + cache ตลอด session) → `:3524` (`whtIncomeTypeCode: v('revWhtIncomeType') ?? ''`) → `Accounting/Services/Implementations/OcrService.cs:4488-4490` (`"" = ล้างค่า`) | ถ้า `GET /api/reference/income-types` ล้มครั้งเดียว (เน็ตกระตุก · 401 ชั่วคราว) dropdown "ประเภทเงินได้ (ม.40)" เหลือแต่ "— ไม่ระบุ —" ทุกใบไปจนกว่าจะรีเฟรชหน้า · ผู้ใช้กด "สร้างเอกสาร" (ซึ่งเรียก `_persistReviewEdits` ให้เองอัตโนมัติ) ⇒ ประเภทเงินได้ที่ระบบ/ผู้ใช้เคยตั้งไว้ **หายไป** → `DocumentLine.IncomeTypeCode` เป็น null → 50 ทวิ / ภ.ง.ด.3/53 ตกไปใช้ประเภท "8" ผิดช่อง | `[]` เป็น truthy ⇒ `if (this._whtIncomeTypes) return;` แคชความล้มเหลวถาวร · payload ส่ง `''` แทน `null` ซึ่งตามกติกาของเรพ = "ผู้ใช้สั่งล้างค่า" ทั้งที่ผู้ใช้ไม่ได้แตะอะไรเลย (กฎเหล็ก #4 B "สร้าง: ว่าง=null · แก้ไข: ""=ล้างค่า" ถูกใช้ผิดบริบท) — `openReview` `await` ตารางไว้ถูกแล้ว แต่กัน**เฉพาะกรณีช้า** ไม่ได้กันกรณี**ล้ม** | อย่าแคชผลล้มเหลว (เก็บ `null` เมื่อ catch) · และเมื่อตารางว่าง ให้ส่ง `null` (ไม่แตะ) แทน `''` — กติกาเดียวกับช่องอื่น: ส่ง `''` ได้เฉพาะตอน element มีอยู่จริงและผู้ใช้เลือก "— ไม่ระบุ —" เอง · เทสต์ sim ฝั่ง JS ทิศตรงข้าม ("ตารางโหลดไม่ได้ ⇒ ค่าเดิมต้องไม่ถูกแตะ") |
| C-09 | P2 | ตัวตัดสินที่ยังฝังใน `OcrService.cs` — 25 เมธอดไม่มีเทสต์ และ checker ไม่ได้มองไฟล์นี้ | ตัวอย่างที่หนักสุด `Accounting/Services/Implementations/OcrService.cs:3761-3781` (`InferCurrency` — `callers.py` = โค้ดจริงเรียก 3 · **เทสต์ 0**) · ผู้เรียก `:2019` · `:6089` · `:8422` | ใบสกุลต่างประเทศที่มีคำว่า "บาท" โผล่ที่ไหนก็ได้บนหน้า (บรรทัดโอนเงิน · อัตราแลกเปลี่ยนอ้างอิง "1 USD = 36.50 THB" · ตราประทับธนาคาร) → `InferCurrency` คืน `null` = บาท → เอกสารถูกบันทึกเป็นบาทด้วยตัวเลขของ USD · **ตัวเลขเท่าเดิมแต่ความหมายต่างกัน 36 เท่า** และ doc-comment ของเมธอดเองเขียนว่าอาการนี้คือ "ความผิดพลาดแบบเงียบ" ที่มันตั้งใจกัน | `if (t.Contains("THB") || rawText.Contains("บาท")) return null;` ทำงานกับ **ข้อความทั้งหน้า** = defect class เดียวกับ RG-02 เป๊ะ ("แถวฟอร์ม 'หักเงินมัดจำ 0.00' ทำให้ใบซื้อธรรมดาติด `[DEPOSIT-BUY]`") ซึ่ง §2f ข้อ 3 สั่งไว้ว่าเจอในตัวสแกนคำตัวหนึ่ง ต้อง `grep` ตัวสแกนคำทุกตัวในไฟล์เดียวกันวันนั้นเลย · และ `tools/ocr_helper_test_check.py` สแกนเฉพาะ `Helpers/Ocr*.cs` ⇒ รายงาน "ไม่มีเทสต์ 0 คลาส" ทั้งที่ไฟล์ที่การถดถอยเกิดจริงไม่เคยถูกวัด | ย้าย `InferCurrency` (+ `InferCreditNoteReason` `:3788-3805` · `ResolveHeaderSubTotal` `:6821` · `MapAzureDocType`) ออกเป็น `Helpers/Ocr*.cs` พร้อมเทสต์ในคอมมิตเดียวกัน — ทิศเดียวกับ `OcrHeaderAmounts`/`OcrDepositMarker`/`OcrPartyName` · ตัวสกุลเงินต้องดู **บริเวณยอดรวม** ไม่ใช่ทั้งหน้า และ "เจอทั้ง THB และ USD" = ไม่รู้ ไม่ใช่ THB · ขยาย `ocr_helper_test_check.py` ให้ฟ้อง `internal static` ใน `OcrService.cs`/`SmartFieldExtractor.cs` ที่ไม่มีเทสต์ด้วย (ratchet baseline แบบ `dead_helper_check`) |
| C-10 | P3 | `Math.Round` ไม่ระบุ `MidpointRounding.AwayFromZero` ในสายสร้างบรรทัดจากสแกน | `Accounting/Services/Implementations/OcrService.cs:6609-6610` (กระจาย WHT รายบรรทัด) · `:6637` (ส่วนลดรายบรรทัด) · `:7368` · `Accounting/Services/Implementations/Ocr/SmartFieldExtractor.cs:506` (back-calc SubTotal 7/107) · `:769` (ยอด WHT) | ยอดหัก ณ ที่จ่ายรายบรรทัดบน 50 ทวิ ต่างจากที่คำนวณมือ 0.01 บาทในเคส midpoint (ยอดรวมยังตรงเพราะบรรทัดสุดท้ายดูดเศษ แต่การแบ่งตามประเภทเงินได้เพี้ยน) | กฎเหล็ก #4 E บังคับ `AwayFromZero` เสมอ · และในเมธอดเดียวกัน `:5958-5959` มีคอมเมนต์ว่าเคสนี้เคยเป็นบั๊กจริงแล้ว (T5-N6) พร้อมใส่ `AwayFromZero` ให้ยอด**หัวใบ** — แต่ตัวกระจาย**รายบรรทัด** ห่างไป 650 บรรทัดในไฟล์เดียวกันยังไม่ได้แก้ ("แก้ตัวเดียว เหลือที่เหลือ") | เติม `MidpointRounding.AwayFromZero` 5 จุด · repo-wide มี 266 จุดที่ไม่ระบุ — ไม่ควรกวาดทั้งหมดในรอบนี้ แต่ควรมี checker scope แคบสำหรับไฟล์ที่ผลิต `DocumentLine`/`JournalEntryLine` |
| C-11 | P3 | `OcrPartyLabels.FindBuyer` / `FindSeller` ไม่มีผู้เรียกเลย (มีแต่เทสต์) | `Accounting/Helpers/OcrPartyLabels.cs:106-107` (`dead_helper_check --all` = `[ORPHAN]` ทั้งคู่ · อยู่ใน `tools/dead_helper_baseline.txt`) | ไม่มีอาการกับผู้ใช้โดยตรง — แต่เทสต์ที่ยืนยันสองเมธอดนี้ **ผ่านทุกวันบนโค้ดที่ระบบไม่เคยเดิน** ⇒ คนอ่านเข้าใจว่าเส้นทางนี้ถูกคุ้มครองแล้ว | ผู้เรียกจริงใช้ `Find()` / `FindAll()` · สองตัวนี้เป็น wrapper ที่เหลือจากรอบก่อน — "มี ≠ ถูกเรียก" (หลักการ 2): ต้องเลือกอย่างตั้งใจ ต่อสาย หรือ ลบ | ลบสองเมธอด + ให้เทสต์เรียก `Find()` แทน แล้วตัด 2 แถวออกจาก baseline (ratchet เดินทางเดียว) — **เป็นคำถามที่เจ้าของตัดสิน 1 บรรทัด ไม่ใช่ให้ agent เดา** |

---

## ผลตรวจตามหัวข้อที่โจทย์ระบุ (สรุปสั้น)

**1. กฎเหล็ก #3 (1-click approve) — DTO ครบไหม / มี required error ตอน approve ไหม**
- ช่อง §86/4 ครบทั้งสาย: ฟอร์ม (`document-scan.html:2512-2580`) → payload (`:3499-3541`) →
  `OcrCorrectionRequest` → persist (`OcrService.cs:4471-4506`) → `OcrResultResponse` → hydrate
  — ไล่ครบทุกข้อของ checklist กฎเหล็ก #4 B แล้ว **ไม่พบช่องที่ขาด**
- `VendorBranchCode`/`BuyerBranchCode` **จงใจ**ไม่เติม `"00000"` (`OcrService.cs:8408-8418` อธิบายไว้ชัด) —
  ถูกต้องตาม "ไม่รู้ = บอกว่าไม่รู้" และไม่ถือเป็น finding
- **ไม่มี required-validation error ตอนกด approve** — `create-document` ล้มได้เฉพาะ 3 ทาง (ซ้ำ · สร้างไปแล้ว ·
  หาคู่ค้าไม่ได้ = C-03) และ approve ที่ล้มไม่ทำให้ทั้งก้อนล้ม (คืน 200 + `[APPROVE-SKIP]`/`[APPROVE-FAIL]`
  ที่ `OcrController.cs:327-378`) — ออกแบบถูกแล้ว
- ที่ยัง**ไม่ถึง 1-click จริง** คือฝั่งขาย (C-01/C-02) และคอลัมน์อัตรา VAT/หน่วยรายบรรทัดที่ยังแก้ไม่ได้
  ในหน้า review (tooltip บอกตรงว่า "แก้ได้ที่ฟอร์มเอกสาร") — **ข้อหลังคือ backlog T4-04 เดิม ไม่รายงานซ้ำ**

**2. `Helpers/Ocr*.cs`** — 32 คลาส มีเทสต์อ้างถึงครบ 32 (`ocr_helper_test_check` เขียว) ·
`dead_helper_check --all` ชี้ OCR 11 แถว: ORPHAN 3 (`OcrPartyLabels.FindBuyer` · `FindSeller` = C-11 ·
`OcrScanPii.UndecidedTextFields` = **ตั้งใจให้เทสต์ใช้ ไม่ใช่บั๊ก**) · INTERNAL 8 (ควรเป็น `private` — cosmetic)

**3. ตรรกะที่ยังฝังในไฟล์ใหญ่** — `OcrService.cs` มี `static` decider 30 ตัว **ไม่มีเทสต์ 25 ตัว**
(`InferCurrency` · `InferCreditNoteReason` · `ResolveHeaderSubTotal` · `MapAzureDocType` · `ApplyWhtSuggestion` ·
`ComputeContentFingerprint` · `SetTargetDocumentType` · …) → C-09 ·
`SmartFieldExtractor.cs` มีตัวตัดสิน 20+ ตัวเป็น `private static` โดยมีเทสต์แตะแค่ `Enrich` +
`ExtractValidThaiTaxIds` (`RdComplianceOcrNoiseTests` · `OcrScanComplianceEvaluatorTests`) ⇒
`ApplyAmountMath` (`:480`) · `LooksLikeVatDoc` (`:524`) · `NormalizeWhtRate` (`:548`) ·
`ValidateAmountOrdering` (`:789`) ตัดสินเงิน/ภาษีโดยไม่มีอะไรล็อกพฤติกรรม

**4. การถดถอยจาก `git log`** — ไล่ 45 คอมมิตล่าสุดของ `OcrService.cs` แล้ว **ไม่พบคอมมิตที่ถอดตัวซ่อม
ของบั๊กอื่นแบบ RG-01..04** · รอบหลัง ๆ (`c79dd90` · `bab2864` · `723f4ea` · `e48ea3c`) เพิ่ม/ย้ายด่านพร้อม
pure helper + เทสต์สองทิศทุกครั้ง ซึ่งเป็นทิศที่ §2f สั่งไว้ · สิ่งที่ยังเสี่ยงคือ **ไฟล์ที่ไม่มีเทสต์เลย**
(C-09/C-10) ซึ่งเป็นที่ที่การถดถอยครั้งหน้าจะเกิดโดยไม่มีอะไรฟ้อง

**5. ลูปเรียนรู้** — ทุกจุดที่เรียก AI บนสาย OCR เก็บ `FeedbackId` ลงแถวสแกนครบ
(`GlAccountAiFeedbackId` · `TargetDocTypeAiFeedbackId` · `OurRoleAiFeedbackId` · `LineSplitAiFeedbackId` ·
`AiSuggestionFeedbackId`) · ปิดลูปครบยกเว้น C-06 (OurRole บนเส้น 1-click) และ C-04 (ช่องทาง API)
**feature parity ผ่าน**: `OcrFullReview`→bespoke · `OcrLineItemSplit`→`LineSplitDistillationModel`
(⇒ **backlog T3-02 ปิดแล้ว**) · `DocumentRoleInference` · `DocumentTypeClassification` ·
`WhtCategoryInference` · `DocumentConversionSuggestion` · `StockMovementValidation` · `OcrProjectMatch`
→ `GenericFeedbackDistillationModel` (`Program.cs:566-622`) · `VendorCanonicalization` · `GlAccountSuggestion` → bespoke

**6. kill-switch** — ไม่พบ feature ของสาย OCR ที่ตายเมื่อปิด provider ทุกตัว:
`AiOrchestrator.cs:259-267` คืน `FallbackToLocal(NoProvider)` · `OcrAiAugmenter` ทุกเมธอดคืน `Empty(...)`
แทนการ throw · `AdvancedAiAugmenter` มี `Fallback(null)` ทุกทาง · หน้าเว็บมีทางเลี่ยงที่ถูกต้อง
(`document-scan.html:3375-3377` "AI ไม่ตอบ — ใช้ข้อมูลเดิมต่อ") · `VatTypeInference` ไม่เคยยิง provider
(`AiSuggestionController.cs:501-565` เป็น `LocalServed` ล้วน) จึงไม่ต้องมี student

---

## ที่ตรวจแล้วไม่ใช่บั๊ก (กันรอบหน้าเสียเวลาซ้ำ)

| เรื่อง | ทำไมถึงไม่ใช่ |
| --- | --- |
| `OcrScanPii.UndecidedTextFields` ไม่มีผู้เรียกในโค้ดจริง | ออกแบบมาให้ **เทสต์ reflection** ใช้ฟ้อง "ช่องข้อความใหม่ที่ยังไม่ถูกตัดสินว่าเป็น PII ไหม" — doc-comment ระบุไว้ตรง ๆ (`OcrScanPii.cs:93-95`) การ "ต่อสาย" เข้าโค้ดจริงจะผิดเจตนา |
| `MapToResponse` ไม่เซ็ต `ComplianceIssues` / `DocumentSideMap` | ถูกเติมใน `AttachComplianceAsync` (`OcrService.cs:8240-8258`) ซึ่งครอบทุก endpoint ที่หน้าเว็บใช้ **เปิด** การ์ด/review · endpoint ที่คืนดิบ (link-po / link-predecessor / match-contact) หน้าเว็บทิ้งผลลัพธ์หรือโหลดใหม่ และ DTO ระบุไว้ว่า `null` = "ยังไม่ประเมิน" พร้อม fallback ที่ถูกต้องใน `_resolveDocSide` (`document-scan.html:3554-3567`) |
| `OcrLineItemSplit` ไม่มี student (backlog T3-02) | **ปิดไปแล้ว** — `LineSplitDistillationModel` register ที่ `Program.cs:601-602` พร้อม fallback `RawTextLineSplitter` สำหรับ tenant ใหม่ |
| `_buildReviewCorrection` ส่ง `hasWht: false` เสมอเมื่อไม่มี checkbox | checkbox `revHasWht` ถูก render ทุกครั้งใน review form (`:2648`) และ `_persistReviewEdits` ถูกเรียกจาก modal เท่านั้น ⇒ ไม่มีเส้นที่ element หาย |
| `SetLineFieldsRequest` / `ModifyLineRequest` / `LinkPurchaseOrderRequest` เป็นช่อง non-nullable | ตรวจแล้วฝั่ง JS ส่งค่าเสมอ (number/string ไม่เคยเป็น `null`) ⇒ ไม่เข้าข่ายบั๊กรอบ 187/188 |
| `OcrController.CreateDocument` คืน 200 เมื่อ approve ล้ม | ตั้งใจ — ใบ Draft ถูกสร้างสำเร็จแล้ว เหตุผลถูกเขียนลง `ProcessingNotes` **ของแถวจริง** ด้วย (`:363-377`) และหน้าเว็บอ่าน `[APPROVE-SKIP]`/`[APPROVE-FAIL]` ครบทั้งสองแบบ (`document-scan.html:3702-3708`) |
| `VendorBranchCode` ไม่ถูก default เป็น `"00000"` ใน DTO | ตั้งใจตามผลตรวจ T1-10/T4-05 — เป็นทิศที่ถูก ("ไม่รู้ ≠ สำนักงานใหญ่") |

---

## ที่ยังไม่ได้ตรวจ (ขอบเขตที่เหลือ)

- `Services/Implementations/Ocr/` อีก 25 ไฟล์ที่ไม่ได้เปิด: `OcrConfidenceGateway` · `CrossValidator` ·
  `AzureDiPatternLearner` · `FieldPatternLibrary` · `OcrSelfCorrectionService` · `ProductMatcher` ·
  `BenfordsLawAnalyzer` · `FpGrowth` · `OcrPreprocessor`
- เส้น python `ocr-service/` (คนละภาษา ต้องมีภาพทดสอบจริง)
- `SlipOcrAssistService` (สลิปโอนเงิน) และเส้น LINE/มือถือฝั่ง client
- `wwwroot/pages/admin-ocr.html` + `review-queue.html` (ตัวหลังยังไม่มีทางเข้าจากเมนู = backlog T4-14 เดิม)
- เทสต์: ไม่ได้รัน (ไม่มี .NET SDK ในเครื่องนี้) — **ยังไม่ได้คอมไพล์**
