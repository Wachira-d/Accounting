# รอบ 195 · ฝ่ายค้านรอบสอง — diff `c69a0b62` (ทีม I2)

อ่านอย่างเดียว · ไม่มี .NET SDK ⇒ ตัวเลขทั้งหมดมาจากการอ่านโค้ด/เทสต์ ไม่ได้รัน

## CONFIRMED

**R2-1 (P0 · build พัง)** `Accounting.Tests/OcrReplayHarness.cs:196-199` เรียก `OcrLineVatPlanner.PlanWholeInvoice(...)` **7 อาร์กิวเมนต์**
แต่ลายเซ็นใหม่ (`Helpers/OcrLineVatPlanner.cs:78-81`) บังคับพารามิเตอร์ตัวที่ 8 `bool vatPrintedOnPaper` แบบไม่มีค่า default ⇒ CS7036
⇒ `dotnet build Accounting.sln` ล้ม (ci.yml:67 build ทั้ง solution รวมโปรเจกต์เทสต์) ⇒ เทสต์ทุกตัวล้มตาม. ในคอมมิตเขียนว่า "PlanWholeInvoice 1 จุดเรียก"
— จริง ๆ มี 2 จุดนอกเทสต์ของ planner (OcrService:6773 + harness). ยังไม่ได้ push (`origin` = aa04d91c) CI จึงยังไม่เห็น.
แก้: ส่ง `vatPrintedOnPaper: OcrHeaderVatEvidence.Classify(p.RawText, Normalize(p.RawText), n.Vat ?? 0m, null) == Labelled`
(ห้ามส่ง `true` ตรง ๆ — harness เป็นชุดกระดาษจริงตาม §H ถ้าส่ง true จะบังผลของด่านที่เพิ่งเพิ่ม)

**R2-2 (P1 · "ถอดที่เดียว" ไม่จริง)** ตัวถอด 7/107 ชุดที่สามยังอยู่: `CrossValidator.FillMissingAmounts` (`Ocr/CrossValidator.cs:89-94`)
แยก VAT **ทุกครั้งที่มีแต่ยอดรวม** ไม่ถามคำ VAT · เลขผู้ขาย · ม.81 และใช้ `Math.Round` แบบ banker's (ขัด §4 E) → `DocumentZoneAnalyzer.cs:328`
→ `OcrService.cs:9443` เติม `data.VatAmount` ในเส้น ZoneFallback (ตอนที่เส้นหลักอ่านไม่ได้ทั้งชื่อผู้ขายและยอดรวม) โดยไม่ติด `[VAT back-calc]`.
grep ตามคำสั่งเจอ 4 จุด: `OcrService:5564` + `OcrVatBackCalc:83` ผ่าน guard · `CrossValidator:92` **ไม่ผ่าน guard** · `OcrService:516` (ยอด − ฐาน ไม่ใช่ 7/107)
ตาข่าย `[VAT-DERIVED]` ยังจับได้ถ้าเลขไม่บังเอิญพิมพ์อยู่บนใบ แต่ข้อความในคอมมิต/DOCUMENT_FLOW ที่บอกว่าเหลือ 2 จุดยังผิด

**R2-3 (P1 · R5 ทางเข้าอื่น)** แท็ก `[VAT-DERIVED]` มีผู้อ่านแค่ `OcrPostingReadiness` (เว็บ "สร้าง+อนุมัติ" `OcrController:420` · LINE `:666/:813`)
และหน้าสแกน. **ไม่ได้ออกเป็นคำเตือนตอนอนุมัติ** เพราะ `OcrApprovalGapWarning.Build` อ่านแค่ `[Σ-GAP]` (`OcrApprovalGapWarning.cs:48-53`)
⇒ อนุมัติบนหน้าเอกสาร · Quick-approve มือถือ (`MobileApiService.cs:375` ใช้ `PreviewApprovalWarningsAsync` อย่างเดียว) · `DocumentsV1Controller:256`
ลงภาษีซื้อที่ระบบคำนวณเองได้โดยไม่มีคำเตือนสักข้อ. บนมือถือนี่คือ "แตะครั้งเดียวไม่เห็นกระดาษ" แบบเดียวกับปุ่มอนุมัติเลยบน LINE

**R2-4 (P2 · ชนกันเพราะเลขตรงโดยบังเอิญ)** ตัวตัดสินติดแท็กเฉพาะ `NotOnPaper` ถ้า VAT ที่ถอดเองบังเอิญตรงกับเลขใดก็ได้บนใบ
(ค่าส่ง/เงินทอน/ยอดบรรทัด) จะได้ `PrintedUnlabelled` ⇒ ไม่ติดแท็ก ⇒ อนุมัติอัตโนมัติได้. เทสต์ `OcrHeaderVatEvidenceTests.cs:80`
ล็อกผลนี้ไว้เอง ("ค่าส่ง 70 · รวม 1,070") ทั้งที่เส้นจริงของใบนั้น (มีเลขผู้ขาย + คำว่าใบกำกับภาษี) VAT 70 **คือค่าที่ถอดเอง**. `[VAT back-calc]`
ถูกเก็บลง `[Reasoning]` ของ ProcessingNotes อยู่แล้ว (`OcrService:3213`) ⇒ ถ้ามีแท็กนี้และผลไม่ใช่ `Labelled` ให้ถือเป็น `NotOnPaper`

**R2-5 (P3 · คำเตือนเก่าไม่ถูกล้าง)** ล้างด้วย `StripRecomputed` แค่ตอนดึงรายการซ้ำ (`OcrService:7571`). เส้นสร้างใหม่หลังลบเอกสาร
(`DocumentService.cs:8752` ล้าง `CreatedDocumentId`) และสำเนาจากอัปไฟล์ซ้ำ (`DecisionNoteTags` ⊇ BlockingTags) ยังต่อท้ายซ้ำ ⇒ `[Σ-GAP]`/`[VAT-DERIVED]`
ของรอบเก่าค้างอยู่ (บั๊กประเภทเดียวกับ P3). ควรย้ายไปล้างที่ต้น `BuildScanLinesAsync`

## PLAUSIBLE

- **ภาษีซื้อ §86/4(6)**: ใบเต็มรูปที่พิมพ์แค่ "ราคารวมภาษีมูลค่าเพิ่มแล้ว" ⇒ guard ยอมให้ถอด (0.75) ⇒ `NotOnPaper` ⇒ หยุดแค่การอนุมัติอัตโนมัติ
  แต่อนุมัติเองแล้ว **เคลม 7/107 ได้** และไม่มีการปิดสิทธิ์เคลมแบบ `[VAT-CLAIM]`. ข้อความ `DerivedNote` ให้ทางไปต่อทางเดียวคือ "ม.81 ตั้ง 0"
  ไม่ได้บอกว่าใบที่ไม่แยก VAT ไม่ครบ §86/4 ⇒ เคลมไม่ได้ตาม §82/5(1) (ใบย่อ §82/5(2) และใบเสร็จ `[TAX-INV-PENDING]` สอดคล้องแล้ว) — **ต้องให้เจ้าของตัดสิน**
- **ใบยกเว้นทั้งใบที่ items ยังว่างตอน Enrich**: ใบที่พิมพ์ "ใบกำกับภาษี + ยกเว้นภาษีมูลค่าเพิ่ม" ยังถูกถอด (ด่าน ม.81 ใน `VatBackCalcGuard:76` ต้องมีบรรทัดก่อน ·
  ประโยคยกเว้นบนกระดาษถูกตัดออกจาก `MentionsVat` เท่านั้น ไม่ได้ใช้เป็นเหตุปฏิเสธ) มีแต่ตาข่ายที่จับได้
- **ทางตรงข้าม**: `IsVatLabelled` รับแค่ "ตัวเลขบรรทัดถัดไป" ⇒ ใบที่คอลัมน์ป้ายกับคอลัมน์ตัวเลขแยกกัน · "Value Added Tax 70.00" (regex ไม่รู้จัก) ·
  Tesseract "ภาษีมูลค่าเพิม" (normalizer ไม่มีรูปนี้) ⇒ `PrintedUnlabelled` ⇒ ตัวพิสูจน์ทั้งใบหยุดทำงาน ⇒ ถดถอยแบบ Scommerce อาจกลับมาในใบที่ยังไม่มีเทสต์
- **C2**: บังคับว่า "แทน" ต้องอยู่**บรรทัดเดียวกัน** — ถ้าหมายเหตุ Scommerce (~130 ตัวอักษร) ขึ้นบรรทัดใหม่ ก็กลับไปนับเป็นหลักฐานใบย่อ (ยังรอดเพราะ mod-11 ของผู้ซื้อ)

## NOT-A-BUG

- ชื่อ named arg `vatPrintedOnPaper:`/`linesVat:`/`paperVat:` ตรงกับลายเซ็นทุกจุด · เทสต์ส่งค่าตามลำดับ (8/5 อาร์กิวเมนต์) ถูก ·
  `internal` (`PrintedVatContradicts` · `MentionsVat`) ใช้ได้เพราะมี `InternalsVisibleTo` (`Accounting.csproj:82`)
- `SameRootTolerance` 0.05: สูตร `remaining = linesInclVat + (paperVat − linesVat) − paperTotal` ถูก (ตัวอย่างค่าธรรมเนียม 50 ⇒ −50 แยกเป็นอีกข้อ) · ไม่รู้ค่า ⇒ ไม่รวมข้อ
- P2 ถอด "นม UHT" ไปในทางที่ปลอดภัย (นมโคล้วนมักยกเว้น) · "infant formula" แคบลงถูกแล้ว
- ใบที่ไม่มีเลขภาษีผู้ขาย ⇒ VAT ว่าง/0 ตรงตามกฎหมาย (ผู้ไม่จด VAT ออกใบกำกับไม่ได้) และ §86/4 compliance แจ้งเรื่องเลขที่หายอยู่แล้ว
- ตัวอย่าง 7 ใบจริง (Wine Pro · Makro · Shopee · Scommerce ฯลฯ) = `Labelled` ⇒ ไม่แตะผลเดิม (อ่านจากเทสต์ ยังไม่ได้รัน)
