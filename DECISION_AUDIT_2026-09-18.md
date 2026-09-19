# DECISION_AUDIT_2026-09-18.md — ตรวจ "กระบวนการตัดสินใจ" ทั้งระบบ (8 ทีม) + แผนรอบพัฒนาถัดไป

> โจทย์จากเจ้าของโปรเจกต์ (รอบ 181): "ตั้งทีมตรวจสอบการตัดสินใจในลักษณะนี้ ทุกส่วน ทุกระบบ ทั้งระบบ …
> ไล่วิเคราะห์ขั้นตอน ลำดับการคิด … ถูกขั้นตอน ถูกหลักการของเรื่องนั้น ๆ ไหม การปรับ สลับ เพิ่ม ลด
> แต่ละขั้นแบบไหนจะได้ผลลัพธ์ดีและถูกต้องที่สุด … มีการเรียกใช้ AI ให้ช่วยตัดสินใจ และเทรนข้อมูล
> เพื่อพัฒนาให้ระบบดีขึ้นเรื่อย ๆ เองทั้งหมดรึยัง ไล่ทุกส่วนและวางแผนให้ Opus พัฒนาอีกครั้ง"

**กติกาของไฟล์นี้** (ชุดเดียวกับ SYSTEM_REVIEW / ERP_REVIEW / OCR_PIPELINE_REVIEW / DECISION_DOCTRINE):
ทุกข้อใน §3 ที่ติด ✅ คือ main agent **เปิดไฟล์ตรงบรรทัดนั้นแล้ว** ก่อนเขียนลงที่นี่ · ข้อที่ทีมรายงานมา
แล้ว**ไม่จริง / แก้ไปแล้ว** อยู่ §5 ห้ามรายงานซ้ำ · §7 คือที่ตรวจแล้ว**ไม่ใช่ปัญหา** ห้ามแก้ · เมื่อแก้ข้อใด
ให้เติม `✅ <sha>` หน้า ID ไม่ลบแถว · เลขบรรทัดอิงคอมมิต `087b0c1` (ต้นรอบ 181) — refactor แล้วต้องขยับ

**ทีม** (แต่ละทีมได้ brief เดียวกัน: ตาราง A จุดตัดสินใจ · B ลำดับขั้น · C ความครอบคลุม · D AI+เรียนรู้ ·
E ข้อเสนอ · F ไม่ใช่ปัญหา · ห้ามรายงานซ้ำสิ่งที่ SYSTEM_REVIEW/ERP_REVIEW/OCR_REVIEW/DOCTRINE ปิดหรือปฏิเสธแล้ว):

| ทีม | ขอบเขต | ไฟล์หลัก |
| --- | --- | --- |
| D1 | วงจรเอกสาร + ด่านภาษี/บัญชีตอนอนุมัติ | `DocumentService.cs` (DS) |
| D2 | ยื่นภาษี / compliance / PDPA / ปิดปี | `TaxService.cs` · `TaxFilingExportService.cs` · `WithholdingTaxCertService.cs` · `StatutoryRemittanceService.cs` |
| D3 | ไปป์ไลน์ OCR อัปโหลด → เอกสาร | `OcrService.cs` + `Helpers/Ocr*` |
| D4 | ธนาคาร · รับ/จ่ายเงิน · เช็ค · เงินสดย่อย | `BankService*.cs` · `BankFeedService.cs` · `Bulk/*` · `Cheque/*` · `PettyCash/*` |
| D5 | สต็อก · COGS · สินทรัพย์ · ผลิต · ฝากขาย | `Inventory/*` · `FixedAssetService.cs` · `ProductionOrderService.cs` |
| D6 | เงินเดือน · ปกส. · PIT · HR | `PayrollService.cs` · `Helpers/ThaiPitCalculator` · `Helpers/Sso*` |
| D7 | สถาปัตยกรรม AI / การเรียนรู้ ทั้งระบบ (57 key) | `Services/Ai/*` · `Distillation/*` · `AiFeedbackTrainingJob` |
| D8 | POS · CMS/ร้านค้า · ที่พัก · บิลลิ่ง · ทางเข้าภายนอก (Integration/V1/LINE/Mobile) | `PosService*.cs` · `Cms*.cs` · `Lodging*.cs` · `IntegrationService.cs` · `*V1Controller.cs` |

---

## §1 คำตอบ 5 ข้อของเจ้าของ (สรุปสั้น — รายละเอียดใน §2–§3)

1. **"ตัดสินถูกหลักการไหม ทุกส่วน?"** — **แกนกลางถูก** (ตารางกฎหมายที่เป็น `Helpers/*` ตัวเดียว + เทสต์:
   `ThaiPitCalculator` · `SsoWageBase`/`SsoRateSchedule` · `TaxPointResolver` · `ThaiWhtRateTable` ·
   `TaxFilingDeadline` · `WhtApplicabilityEvidence` · `PaperTaxInvoiceCompleteness` · `PaymentIntentPolicy` ·
   `OcrLineReconciler` ฯลฯ) **แต่รอบนอกยังผิดหลักการ 3 แบบ** ที่พบซ้ำทุกโดเมน: (ก) **ประทับสถานะปลายทาง
   เอง**โดยไม่มีของจริงยืนยัน (ยื่นแล้ว · จับคู่แล้ว · ตัดสต็อกแล้ว · NoShow · Approved ตรง) (ข) **"ไม่รู้" กลาย
   เป็นค่าที่แต่งขึ้น** (รหัสเงินได้ "8" · 3% · VAT 7 · สาขา "00000" · อายุ 60 เดือน · "Acknowledge"@0.50 ·
   COGS 0 · บริการ→21913) (ค) **ตารางกฎหมาย/สูตรมีสำเนาที่สอง** (CIT · PIT · ลดหย่อน · อัตรา WHT · อายุใช้งาน ·
   สูตรจับคู่ธนาคาร 5 ชุด · สถานะจ่าย 7 ชุด · mod-11 3 ชุด · +543 44 จุด)
2. **"ลำดับขั้นถูกไหม ควรสลับอะไร?"** — 3 จุดที่ลำดับผิดแล้ว**ผลผิดจริง**: (ก) **ด่าน WHT เก่ายังรันหลังด่าน
   ใหม่** ในเมธอดเดียว → audit บอกว่า "เงียบ" แต่จอ "เตือน" (D1-B1) (ข) **OCR serialize บรรทัดก่อน Product
   master/AI ทำงาน** → ผังบัญชีรายบรรทัดที่ดีที่สุดไม่เคยถึงเอกสาร (D3-1) (ค) **AI ใน OCR รันก่อน DBD** → ครู
   เห็นชื่อโลโก้ ไม่เห็นชื่อทะเบียน + `DbdJuristicType` เป็น null เสมอ (D3-B) · และ **soft-warning รวบก่อน
   hard-block** ทำให้ผู้ใช้กดรับทราบแล้วชนบล็อกอีกรอบ (D1-B5)
3. **"เงื่อนไขครอบคลุมทุกด้านไหม?"** — เคสที่ยังไม่มีทางเดินสรุปใน §3 คอลัมน์ C ของแต่ละทีม · ที่หนักสุด:
   เช็คเด้งไม่ถอย Payment/JE (D4) · ขายก่อนซื้อไม่ true-up (D5) · CN คืนของใช้ WAC ปัจจุบันไม่ใช่ต้นทุนตอนขาย
   (D5) · ม.70/DTA ไม่มีตาราง (D2) · ค่าปรับ no-show > มัดจำไม่มีเอกสารเรียกเก็บ (D8) · ลูกจ้างต่างด้าว/หลาย
   นายจ้างไม่มี field (D6) · §65 ตรี (8)(14)(15)(19) ไม่มีใครป้อน context (D1)
4. **"เรียก AI + เทรนให้ดีขึ้นเองครบไหม?"** — **ไม่ครบ และที่มีบางเส้น "สอนตัวเอง"**: (ก) **self-confirm
   loop** — VendorIntel เขียน `HasWht` สวมรอยกระดาษ → ด่านอนุมัติเชื่อว่ากระดาษพูด → VendorIntel เรียนกลับจาก
   ผลนั้น (D3-2) (ข) **label ปลอม** — `DocumentAiAugmenter` แต่ง "Acknowledge"@0.50 เมื่อไม่มีใครตอบ แล้ว UI บันทึก
   `acceptedAi=true` (D1-11) · ชนิดเอกสารเทียบค่าสุดท้ายไม่ใช่คำตอบ AI (D3-4) (ค) **write-only** — `AssetCategory
   Suggestion` ไม่มีนักเรียน/ไม่มี RecordUserChoice (D5) · `PayrollIncomeType` ไม่มีผู้เรียก/ไม่ capture (D6) ·
   `TaxFilingPreCheck` key ผี (D2) · bank 1:1/Batch ไม่สอน · unmatch ไม่เป็น negative label (D4) ·
   `BankMatchDistillationModel` prepass คืนว่างเสมอ (D4) (จ) **กุญแจ/สายป้ายของรอบ 178 มีรู 3 จุด** (D7-1 sentinel `__USER_KEPT_EXISTING__` ไหลเข้าตัวแนะนำ GL · D7-2 กุญแจเขียน≠อ่านเมื่อนักเรียนตอบ · D7-3 หน้าเว็บ 6 จุดไม่ส่ง `source`) ⇒ ต้องซ่อมก่อน governor ใด ๆ · (ง) **ควรถามนักเรียนแต่เป็นกฎแข็ง** — ชนิด `PayrollItem`
   จาก prefix โค้ด (D6) · `InferCreditNoteReason` regex ทั้งที่มี student (D3) · จับคู่ SKU ภายนอก exact เท่านั้น
   (D8) · LINE "บันทึก เซเว่น 250" ไม่เรียก `GlAccountDistillationModel` (D8) · **ที่ถูกอยู่แล้ว**: ไม่มีทีมไหนพบ
   การถาม AI ในสิ่งที่กฎหมายตอบไว้ (ขั้นภาษี · เพดาน ปกส. · tax point) ✅
5. **"แผนให้รอบถัดไป?"** — §6 · หลัก: **เทสต์ golden ก่อนแตะ** (กฎ #4 H) · **ตัวตัดสินตัวเดียวต่อเรื่อง** ·
   **สถานะปลายทางต้องมาจากของจริง** · **ไม่รู้ = มองเห็น** · แยก "ทำได้เลย" (§6.1–6.3) กับ "เจ้าของต้องตัดสิน" (§6.4)

---

## §2 ต้นเหตุร่วม 7 แบบ (แก้ที่รากปิดได้หลายข้อ — เรียงตามจำนวนข้อที่ปิดได้)

| # | รูปแบบ | ข้อที่อยู่ใต้ราก | กลไกกัน (ไม่ใช่แค่จด) |
| --- | --- | --- | --- |
| R1 | **สถานะปลายทางประทับเอง** (หลักการ F2 ข้อ 3) | D2-B1 Filed จากปุ่ม · D2-B1 Compliance "Simulate" · D4-2 BankFeed `Matched` ไม่มีคู่ · D4 OpenBanking FirstOrDefault · D8 CMS `StockDeducted=true` ไม่ตัด · D8 Night audit NoShow ตรง · D8 POS/Integration `Status=Approved` ตรง · D8 Mobile ExpenseClaim Approved ไม่มีด่าน | checker ใหม่ `tools/terminal_status_writer_check.py`: เขียน `Status = <Terminal>` นอก OWNER method ของชนิดนั้น = ฟ้อง (scope แคบ: `TaxReportStatus.Filed` · `ReconciliationStatus.Matched` · `DocumentStatus.Approved` · `ReservationStatus.NoShow`) |
| R2 | **"ไม่รู้" → ค่าที่แต่งขึ้น** (G3) | D2-B3 `"8"/"40(8)"/3m/7m/"6"` · D8 `"00000"` 3 จุด · D8 `VatRate ?? 7m` 6 จุด · D5 อายุ 60 เดือน + conf 0.80 · D1-11 Acknowledge@0.50 · D1-7 ไม่มี ProductCode = บริการ · D5 COGS 0 เงียบ · D4 บัญชีเงิน fallback 111 · D4 additive default | ทุก default ต้องเป็นค่าใน enum "ไม่รู้" หรือแถว excluded+reviewNote · `Helpers/OutputVatRate.ForCompany` แทน 7m · `PosSlipHeader.BranchLabel` pattern แทน "00000" |
| R3 | **ตารางกฎหมาย/สูตรสำเนาที่สอง** (F2 ข้อ 4) | D2-B2 CIT 2 ชุด (**เงินผิดจริง**) · PIT 2 · Col11 2 ไฟล์ · mod-11 3 · +543 44 · ปฏิทินยื่นรายปี 2 · D6 ลดหย่อน 3 · D1-3 อัตรา WHT ใน DS (มี 1.5% ที่ตารางกลางไม่มี) · D5 อายุใช้งาน 3 · D4 สูตรจับคู่ 5 + สถานะจ่าย 7 · D1-4 §86 คิวรี inline ซ้ำ · D8 ContactType จากเลข 3 สำเนา | `filing_deadline_single_source_check` มีแล้ว → ขยาย pattern เดียวกันเป็น `legal_table_copy_check` (CIT bracket · PIT bracket · mod-11 · `+ 543`) · ratchet baseline |
| R4 | **สองด่านในเมธอดเดียว / สองสูตรของสิ่งเดียว** | D1-B1 WHT เก่า+ใหม่ · D1-B1 ยอดสะสม 2 สูตร · D3-6 dup scan-time vs create · D5-B7 WAC live vs rebuild · D2 งวด ภ.พ.36 2 กติกา · D2 สถานะ JE 21913 3 ชุด | เมื่อเพิ่มด่านใหม่ ต้อง `callers.py` ด่านเก่า + ถอดในคอมมิตเดียว (ข้อ 7 ของ F3) |
| R5 | **ทางเข้าอื่นไม่เดินด่านเดียวกัน** (F3 ข้อ 8) | D8 POS ใบกำกับเต็มรูป · D8 Integration 6 จุด · D4 Integration/Import จ่ายเกินไม่บล็อก · D5 `ImportAsync` ข้ามด่านที่ดิน · D8 V1 อนุมัติไม่มี `CanApproveAsync` · D8 PosController/Cms*/Integration/Mobile ไม่มี permission gate · D6 `ExpenseClaimService` ลง 2191x ไม่ผูก cert | `write_permission_gate_check` มีแล้วแต่ไม่ครอบ 4 controller นี้ → เพิ่ม scope · ทางเข้าสร้างเอกสารทุกเส้นเรียก `CreateDocumentAsync`+`ApproveDocumentAsync` |
| R6 | **ลูปเรียนรู้ไม่ปิด / สอนตัวเอง** (§3 doctrine) | D3-2 VendorIntel WHT self-confirm · D1-11 acceptedAi ปลอม · D3-4 label ชนิดเอกสารเทียบผิดตัว · D5 AssetCategory write-only · D6 ไม่มี capture เลย · D4 unmatch ไม่เรียน · D2 TaxFilingPreCheck ผี · D3 OcrLineItemSplit negative-only | `AiFeedbackTrainingJob` รายงาน per-key: rows · labels · Explicit% · "ไม่มีผู้เรียก" (ต่อยอด T3-P1a) |
| R7 | **ไม่มีเทสต์ที่ด่านเงิน** | `ApproveDocumentAsync`/`CollectApprovalWarningsAsync` 0 ไฟล์ · Bank*/Bulk*/Cheque/PettyCash 0 ไฟล์ · POS refund 0 · FIFO 0 · CIT 0 | §6.0 — เทสต์ก่อนแตะ |

---

## §3 ผลตรวจรายทีม — ยืนยันแล้ว (✅ = เปิดไฟล์ตรงบรรทัด · ⚠ = ยืนยันบางส่วน/บรรทัดคลาด)

ความรุนแรง: **P0** = ตัวเลขภาษี/เงิน/สต็อกผิดหรือสองความจริงที่ผู้ใช้เห็นวันนี้ · **P1** = ทิศ "ไม่รู้" ผิด /
ด่านตาย / ลูปเรียนรู้เสีย · **P2** = สำเนา/ป้าย/โครงสร้าง

### D1 · วงจรเอกสาร + ด่านตอนอนุมัติ (`DocumentService.cs`)

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| D1-B1 | ✅ | **P0** | **ด่าน WHT เก่ายังรันหลังด่านใหม่** — `IsPureGoodsPurchaseAsync` + `CheckWhtThresholdAsync` เตือนทุกใบที่บรรทัดไม่ผูก TrackStock (= เคสที่ด่านใหม่ถูกสร้างมาเงียบ) ⇒ audit `WhtWarningSuppressed` บอก "เงียบ" แต่จอเตือน · ไม่ดู `paperGrade` เลย ⇒ คำตัดสินเจ้าของ "ใบกำกับสมบูรณ์ กระดาษชนะ" ถูกลบล้าง · ยอดสะสมคิด 2 สูตร (`WhtCumulativeScope.SumDistinct` vs รวม PI+Expense+PV ตรง = นับซ้ำ) | DS `:16697-16719` · `:12210` · `:12245` |
| D1-B2 | ✅ | **P0** | `DocumentSide.IsPurchase(doc.DocumentType)` **ไม่ส่ง role** 2 จุด → CN/DN ฝั่ง**ขาย**เข้าด่าน WHT + `ProhibitedInputVatScreener` · PO/PR/GRN (`AlwaysPurchase`) เข้าด่าน WHT ทั้งที่ไม่ใช่การจ่าย → เขียน audit "suppressed" ใส่ PO · `CnDnPurchaseSideOverride` มีแต่ใช้ที่ `:16603` เท่านั้น | DS `:16414` · `:16444` · `Helpers/DocumentSide.cs:40-85` |
| D1-7/B3 | ✅ | **P0** | Invoice ที่บรรทัดไม่มี `ProductCode` = **เดาเป็นบริการ → VAT พัก 21913** ไม่เข้า ภ.พ.30 ทั้งที่ส่งมอบแล้ว (§78) = นำส่งขาดแบบมองไม่เห็น (ทิศผิดตาม G5) · `TaxPointResolver.Resolve(doc)` โหมด Auto ซ้ำอีกชั้นแทนส่ง `SupplyKind` ที่เพิ่งตัดสิน | DS `:13452-13476` · `:5150` |
| D1-11 | ✅ | **P1** | `DocumentAiAugmenter` คืน **"Acknowledge"@0.50 เมื่อไม่มีใครตอบ** (`:736,:796` · `:175` เรียกว่า "ค่าปลอดภัย") · DS `:4567` `r.Answer ?? "Acknowledge"` · UI ป้ายเขียว "🤖 AI: Acknowledge" · กดรับทราบ → `acceptedAi=true` ⇒ คลัง `ApprovalWarningFixSuggestion` เต็มด้วยคำตอบที่ AI ไม่เคยพูด | `Services/Ai/DocumentAiAugmenter.cs:175,736,778,796` · DS `:4567` · `documents.html:9884` |
| 🔨 3b81ca1 D1-B4 | ✅ | **P1** | `doc.BuyerDeclinedTaxInvoice = true` **ประทับเจตนาผู้ซื้อแทนผู้ซื้อ** เมื่อ `IsJuristicBuyer=false` (ContactType default Individual + ไม่มีเลข 0xxxx — ราก 180-A) ⇒ ลูกค้านิติบุคคลที่คีย์มาเปล่าถูก downgrade เป็นอย่างย่อเงียบ · หัว "ใบกำกับภาษีอย่างย่อ" พิมพ์โดย**ไม่ตรวจ `IsRetailApproved`/`PhoR06ApprovedDate`** (ธงนี้มีผู้อ่านแค่ POS) — ขัดกฎเหล็ก #2 §86/6 | DS `:4834` · `PdfGenerationService.cs:1437-1446` |
| D1-9 | ⚠ | **P1** | §65 ตรี: `annualRevenue` ใช้ปีปฏิทินทั้งที่ `FiscalYear` ถูกใช้ 5 บรรทัดถัดมา + รวม Receipt ที่ settle Invoice ⇒ ฐานเพดานค่ารับรองพอง → บวกกลับ**น้อยไป** · context (8)(14)(15)(19) ไม่มีใครป้อน (grep `IsRelatedParty|RelatedToBusiness` นอก validator = 0) · keyword "รับรอง" 2 สำเนา | DS `:12265-12305` · `Section65TerValidator.cs:220` (ยังไม่ได้เปิดยืนยันบรรทัด annualRevenue) |
| D1-C | ⚠ | **P1** | §82/3 ตอน post: `ResolveInputVatAccountAsync` ดูแค่ §86/4 ⇒ ใบครบแต่เก่า 8 เดือนลง 11610 · reclassify กรอง `InputVatPostedAsUndue` เท่านั้น ⇒ GL 11610 ≠ ภ.พ.30 · ข้อความ `:16752` "ระบบจะ reclassify อัตโนมัติ" ไม่ตรงสิ่งที่เกิด | DS `:11758` · `:11946` · `:16741-16761` |
| D1-3 | ✅ | P2 | `knownWhtRates {0,1,1.5,2,3,5,10,15}` ฝังใน DS = สำเนาที่สองของ `ThaiWhtRateTable.StatutoryRates` และมี 1.5% ที่ตารางกลางไม่มี | DS `:16690` |
| D1-4 | ⚠ | P2 | §86 "ใบแจ้งหนี้มีสินค้า" ตรวจ 2 ที่ (`InvoiceHasTrackedGoodsAsync` + คิวรี inline) เตือน ×2 | DS `:16390` · `:16642` |
| D1-B5 | ⚠ | P2 | soft-warning รวบ (`:4529`) ก่อน hard block (`:4632-4796`) ⇒ กดรับทราบแล้วชนบล็อก = สองรอบ · `RecordWhtSilence` ถูก rollback เมื่อ throw | DS `:4529` · `:4632-4796` |
| D1-D | ✅ | P2 | `RecordWhtDecisionFeedbackAsync` บันทึก `Implicit` เสมอเมื่อไม่หัก — แยกไม่ออกระหว่าง "ไม่เคยเห็นคำเตือน" กับ "เห็นคำเตือน WHT แล้วกดรับทราบ" (ควร `BulkApprove`) · ข้อความ `:16573` "ระบบจะจำคำตอบนี้ไว้สอน" จึงเกินจริง | DS `:16033-16060` · `:16573` |
| D1-13 | ⚠ | P2 | `CheckCreditAsync` ล้ม → `catch → LogWarning` → ผ่านเงียบ (เงื่อนไขเท็จเพราะไม่มีข้อมูล = ผ่าน) | DS ~`:4959-4977` (บรรทัดที่ทีมอ้างตรงกับ 3-way match — ยังไม่ได้ยืนยันตำแหน่งจริง) |
| D1-T | ✅ | **P0 (โครง)** | `ApproveDocumentAsync`/`CollectApprovalWarningsAsync` **ไม่มีเทสต์ตรงเลย** (มีแค่ `ConvertedTaxInvoiceApproveTests`/`RestoredDocumentApproveTests` ที่แตะขอบ) — ทุกด่านใน D1 ไม่มี golden | `Accounting.Tests/` |

### D2 · ยื่นภาษี / compliance / PDPA / ปิดปี

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| ✅ 3b81ca1 D2-B2a | ✅ | **P0** | **CIT 2 ตาราง**: `TaxService.CalculateThaiCit(netProfit)` ใช้ขั้น SME กับ**ทุกบริษัท** (ผู้เรียก ภ.ง.ด.50 `:2135`) ขณะ `TaxFilingExportService.ComputeCit(netProfit, isSme)` ตัดสิน `PaidUpCapital ≤ 5ล. && รายได้ ≤ 30ล.` ถูก ⇒ บริษัททั่วไปได้ CIT ต่ำกว่ากฎหมายบน ภ.ง.ด.50 แต่ถูกบน 51 · golden: ทุน 10 ล. กำไร 1 ล. → ต้อง 200,000 (วันนี้ 105,000) | `TaxService.cs:2485-2510,2135` · `TaxFilingExportService.cs:1052-1054,1077-1096` |
| D2-B1a | ✅ | **P0** | **`FileTaxReportAsync` ประทับ `Filed`+`FiledDate`+`FilingLockedAt` จากการกดปุ่ม** ไม่มี `FilingNumber`/การตอบกลับ RD · controller ตอบ "ยื่นรายงานภาษีสำเร็จ" · `TaxReportStatus.Submitted` ไม่มีใครตั้ง · e-Filing แค่ `EFilingExportedAt` ⇒ ล็อกเอกสาร/JE ทั้งงวดด้วยเหตุการณ์ที่ระบบไม่รู้ว่าเกิด · **ไม่บล็อกเมื่อมีแถว ⚠ excluded** (ด่านที่ `RemitAsync` มี) | `TaxService.cs:2725-2729` · `TaxController.cs:51` · `TaxService.EFiling.cs:103,132-135` |
| D2-B1b | ✅ | **P0** | `ComplianceService.SubmitFilingAsync` — คอมเมนต์เอง "Simulate submission" → `Status="Filed"` + **แต่ง** `SubmissionReference`/`ConfirmationNumber` จาก GUID · เงินเพิ่ม 1.5%/เดือน **ไม่มีเพดานเท่าภาษี** (§27) · มี UI เรียกจริง | `ComplianceService.cs:232-247` · `ComplianceController.cs:46` |
| D2-B4a | ✅ | **P1** | PDPA legal hold = `DocumentDate > now−5y` แต่ ม.10 พ.ร.บ.บัญชี = สิ้นรอบ+5 ปี · §87/3 = วันยื่น+5 ปี ⇒ ปลดล็อก**เร็วกว่ากฎหมายได้ถึง ~17 เดือน** (ลบแล้วกู้ไม่ได้) | `Services/Implementations/Pdpa/PdpaService.cs:515-522,541` |
| D2-B4b | ⚠ | **P1** | ภ.พ.36 สองกติกางวด: รายงาน/ไฟล์ = `TaxPointDate ?? DocumentDate` · แดชบอร์ด/นำส่ง/รับรู้ = `PaymentDate ?? DocumentDate` · §83/6 ผูกวันจ่าย ⇒ ใบผู้ขาย 28/6 จ่าย 3/7 อยู่คนละงวดกันระหว่างจอกับ JE | `TaxService.cs:1505-1506` · `StatutoryRemittanceService.cs:275-283,1180-1186` |
| D2-B3 | ✅ | **P1** | ค่าแต่งลงไฟล์ยื่น: `PndIncomeTypeCode.ForFile _ => "6"` (รหัสไม่รู้จัก → ไฟล์ยื่นเป็น "6" เงียบ) · `IncomeTypeCode="40(8)"` default 5 จุด · cert ไม่มีบรรทัด → `"8"` + `3m` · ภ.พ.36 `7m` · JE-fallback `3m` | `Helpers/PndIncomeTypeCode.cs:42` · `TaxService.cs:1541,1683,1705,1795,1810,1925,~1899` · `WithholdingTaxCertService.cs:519-528,539` |
| ✅ 3b81ca1 D2-B3b | ✅ | P2 | `GetWhtRate` `?? RateFor(code, !payeeIsJuristic)` — ขั้น "ชนิดตรงข้าม" วันนี้ dead แต่เป็นกับดักทันทีที่เพิ่มรหัสที่มีอัตราฝั่งเดียว (ขัด 180-3 ที่เพิ่งแก้) | `TaxService.cs:2532-2533` |
| D2-C1 | ⚠ | **P1** | `ThaiWhtRateTable` **ไม่มีแถว ม.70** (15%/10%) · ไม่มีตาราง DTA ทั้งเรพ ⇒ cert ภ.ง.ด.54 รับ `line.WithholdingTaxRate` ตามที่ผู้ใช้พิมพ์ไม่มีด่าน | `Helpers/ThaiWhtRateTable.cs` · `ITaxFilingExportService.cs:59-60` |
| D2-C2 | ⚠ | P1 | `rdReceiptNumber` optional ใน `RecognizePp36InputVatAsync` ⇒ ใบเข้ารายงานภาษีซื้อไม่มีเลขใบกำกับ §86/14 | `StatutoryRemittanceService.cs:1156-1165` |
| D2-C3 | ⚠ | P1 | §82/5(2) ฝั่งซื้อเส้นคีย์มือ: ไม่มีธง "ผู้ขายให้ใบอย่างย่อ" · `TaxInvoiceCompletenessChecker.Evaluate` ไม่ตรวจหัว "ใบกำกับภาษี" (GAP-3 ครอบเฉพาะ OCR) | `TaxInvoiceCompletenessChecker.cs:304-330` |
| D2-C4 | ⚠ | P1 | ปิดปี: ไม่มีด่าน ม.7 (CPD อยู่แค่ XBRL) · `PreCloseChecklistService` ไม่มีใครเรียกก่อน `YearEndCloseAsync` · ไม่ตรวจว่า ภ.พ.30/ภ.ง.ด. ทุกงวด Filed | `AccountingService.YearEndClose.cs:76-215` |
| D2-B2b | ⚠ | P2 | สำเนา: PIT bracket (`TaxService.cs:2447` vs `ThaiPitCalculator:73`) · `RecalcVatTotals` allow-list เอง + `TaxComplianceChecker:203` ตัดสินฝั่งด้วย `Description.Contains("ขาย")` ทั้งที่ `VatReportLineKind` เป็น OWNER · `GeneratePp36Report` array ซ้ำ `WhtRemitScope.PayerSideTypes` · Col11 2 ไฟล์ + `WithholdingTaxCert.Condition` ไม่มีใครอ่าน · mod-11 3 จุดนอก `ThaiTaxId` · `+543` 44 จุดนอก `ThaiDate` · ปฏิทินยื่นรายปี 2 ตาราง (`TaxCalendarService:117-127` สบช.3 ตรึง 31 พ.ค.) · JE 21913 สถานะ 3 ชุด | ตามที่ระบุ |
| D2-D | ⚠ | P2 | `AiFeatureKey.TaxFilingPreCheck` ไม่มีผู้เรียก (key ผี) · `BookTaxDifferenceDetection` คำนวณ §65 ตรี ด้วย keyword+`AddbackPct` ของตัวเอง แทน `Section65TerValidator` + `Math.Round` ไม่มี AwayFromZero · `IncomeTypeCode` default `"8"` ทั้งที่ `WhtCategoryInference` มีนักเรียน | `AiSuggestionController.cs:1967-1985` |

### D3 · ไปป์ไลน์ OCR

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| D3-1 | ✅ | **P0** | **serialize บรรทัดครั้งเดียวที่ `:786`** (projection 8 ช่อง ไม่มี `VatRate/VatAmount/ProjectAiFeedbackId`) **ก่อน** `ApplyProductCrossReferenceAsync` (`:1374` → `SuggestedAccountCode`) และ learner รายบรรทัด ⇒ `BuildScanLinesAsync :6255` อ่าน `SuggestedAccountCode` = null ทุกบรรทัด → ตก `scanDebitAccountId` ⇒ **Product master (ชั้นที่ 4.1e เรียก "แข็งแรงที่สุด") ไม่เคยถึงเอกสาร** · trace `[Product]` โกหก · re-serialize เต็มเกิดเฉพาะเมื่อผู้ใช้แก้บรรทัด (`:6903/6961/7019/7069`) | `OcrService.cs:786,1374,3164,6255-6262` |
| D3-2 | ✅ | **P0** | **VendorIntel สวมรอย "กระดาษ" เรื่อง WHT**: `:1547-1552` เขียน `extractedData.HasWht=true; WhtRate` (DTO บอกว่าช่องนี้ = ยอดบนกระดาษ) ทั้งที่ `PaperWhtReader :595` เพิ่งตอบว่าไม่มี → persist → create คิด `WithholdingTaxAmount` จริง → approve `ScanPaperWhtEvidenceAsync` DS `:16126` `if (scan.HasWht || scan.WhtRate>0) return (true…)` = กระดาษชนะ → VendorIntel เรียนกลับจาก `doc.WithholdingTaxAmount>0` = **self-confirm loop** · ไม่มี FieldConfidence ⇒ ไม่ไฮไลต์ | `OcrService.cs:595,1547-1552,1937,5638-5642` · DS `:16126` · `VendorIntelligenceService.cs:532,781` |
| D3-3 | ✅ | **P1** | `extractedData.Confidence = Max(Confidence, vendorPred.DocumentTypeConfidence)` ยกเลิก penalty ของ Gateway · `GatewayResult.MathConsistent` **ไม่มีผู้บริโภคทั้งเรพ** ⇒ ใบ sub+vat≠total ของผู้ขายประจำผ่าน autoCreate ≥0.85 | `OcrService.cs:1463` · `OcrConfidenceGateway.cs:16` |
| D3-4 | ✅ | **P1** | TargetDocumentType 7 ชั้น "ใครมาหลังชนะ" ไม่มี `Note()` · **label ผิด**: create `:6006-6009` `acceptedAi = (result.TargetDocumentType == docType)` เทียบค่าสุดท้าย ไม่ใช่ `TargetDocTypeAiSuggested` (ที่ SubmitCorrection `:4484` เทียบถูก) ⇒ AI ตอบ X, VendorIntel ทับ Y, กดสร้าง = "AI ถูก" | `OcrService.cs:1460-1463,6006-6009,4484` |
| D3-5 | ⚠ | **P1** | `OcrPostingReadiness.BlockingTags` ลิสต์มือ 5 ตัว vs แท็ก "ไม่แน่ใจ" ที่เขียนจริง 37 ชนิด — ไม่บล็อก `[WHT-SUGGEST]` (§54) · `[PAY-SOURCE-GUESS]` · `[DEPOSIT-BUY]` · `[PP36]/[PND54]` · `[DBD] ⚠` · `[COPY-DOC]/[OWN-DOC]` · `[Duplicate]` — LINE โชว์แท็กแล้วยังขึ้นปุ่มเขียว | `Helpers/OcrPostingReadiness.cs` · `LineBotService.cs:672` |
| D3-B | ⚠ | **P1** | ลำดับผิด doc (กฎเหล็ก #3 ข้อ 2 — doc ผิด 3 จุด): `VendorKnownGoodCorrector` รันในขั้น 1 (ไม่ใช่ 6) · **AI ทั้ง 4 จุด (`:727,:943,:1053,:1713`) รันก่อน DBD (`:2241`)** ⇒ GL AI รับ `DbdJuristicType` null เสมอ + เห็นชื่อโลโก้ · Arbiter `DecideAll :1248` รันก่อน DBD/CertInLieu/Deposit | `OcrService.cs` ตามบรรทัด |
| D3-6 | ⚠ | P2 | ด่านซ้ำ 2 สูตร: scan-time `:2185` ตรงสตางค์·ไม่ดูผู้ขาย·ไม่มีกรอบเวลา (= บั๊ก T1-14 ที่ helper บอกว่าแก้แล้ว) vs `OcrDuplicateScanRule` เฉพาะ create `:7863` | `OcrService.cs:2185,7863` |
| D3-7 | ⚠ | P2 | Σ VAT รายบรรทัด ≠ หัว → `LogWarning` เท่านั้น (`:6188`) · GL AI ส่ง `"THB"` ตายตัว (`:1720`) · `OcrLineItemSplit` ปิดลูปเฉพาะ Modify `acceptedAi:false` = คลัง negative-only · `DocumentRoleInference` ปิดลูปเฉพาะ correction · `InferCreditNoteReason :3651` regex ทั้งที่ `CreditNoteReasonClassification` มี student + enum ปิด | ตามบรรทัด |

### D4 · ธนาคาร · รับ/จ่าย · เช็ค · เงินสดย่อย

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| D4-2 | ✅ | **P0** | `BankFeedService.TryAutoMatchAsync` ประทับ `Matched` **โดยไม่เก็บว่าจับกับอะไร** (ไม่ตั้ง `MatchedPaymentId`/doc id) และไม่แตะเอกสาร ⇒ บรรทัดธนาคาร "จับแล้ว" กับความว่าง ใบยังค้าง · AI fallback ≥0.75 → `Suggested` ไม่เก็บ id เช่นกัน · และถาม AI ก่อนลอง `MatchCandidates`/ชื่อผู้โอน (ขัด §2.1) | `BankFeedService.cs:284-345` |
| D4-1 | ✅ | **P0** | **สูตรจับคู่อัตโนมัติ 5 สำเนา** (ต่อยอด GAP-1): AutoMatch 50/30/20 เฉพาะ `PaymentMethod==BankTransfer` (PromptPay/เช็คหลุด) · MatchCandidates 60/30/10/22 · AiSmartMatch 0.40/0.25/0.20/0.10 (**ไม่เรียก orchestrator แต่ UI ป้าย "🤖 AI (local)"**) · BankFeed exact · OpenBanking amount+3d `FirstOrDefault` โหลด Payments **ทั้งบริษัทไม่กันตัวที่จับแล้ว** + query txn ไม่มี `CompanyId` | `BankService.cs:579-757,595-597` · `MatchCandidates.cs:15-25` · `AiReconciliation.cs:262-330` · `OpenBankingService.cs:315-324` · `bank.html:214` |
| D4-3 | ✅ | **P0** | Paid/PartiallyPaid **สำเนา 7 ชุด 3 เกณฑ์**: DS 0.005 · `IntegrationService:1083-1092` `<=0`+clamp 0 = **จ่ายเกินหายเงียบ ไม่มีด่านเกิน** · `:1215` 0.01 · `ImportExportService:1251` ไม่มีด่านเกิน BalanceDue ติดลบได้ — เว็บบล็อก (`DS:10774`) แต่ Integration/Import ไม่ | ตามบรรทัด |
| D4-4 | ✅ | **P1** | `request.Tolerance` **ไม่มีเพดาน** (`> 0 ? request.Tolerance : 0.01m`) — ส่ง 1,000,000 ก็ "สมดุล" | `BankService.Reconciliation.cs:142` |
| D4-5 | ⚠ | **P1** | `ValidateMatchAmountAsync`: ชนิดที่ไม่รู้ทิศ → "additive (legacy safe default)" · JE ไม่แตะธนาคาร → บวก · **ผ่านถ้า net *หรือ* gross ตรง** (สองโอกาสผ่านต่อใบ) | `BankService.cs:401-511` |
| D4-6 | ✅ | **P1** | **เช็คเด้ง = เปลี่ยนสถานะ+เหตุผลเท่านั้น** — Payment/ใบ Paid/JE ไม่ถอย · เช็คขึ้นเงินบวก/ลบ `CurrentBalance` ตรงไม่มี JE/BankTransaction · เช็ครับไม่มีบัญชีฝาก → LogWarning แล้วผ่าน | `Cheque/ChequeService.cs:160-241` |
| D4-7 | ⚠ | **P1** | เงินสดย่อย: ไม่มี §65 ตรี(9) ใบเสร็จ (UI "optional") · ไม่มีด่านงวด/VAT · ไม่มีบัญชีค่าใช้จ่าย → บันทึกรายการ**ไม่มี JE** · **`ReplenishAsync` ไม่มี JE เลย** = เงินออกธนาคารเข้าถังโดย GL ไม่รู้ | `PettyCash/PettyCashService.cs:63-155` · `petty-cash.html:43` |
| D4-8 | ⚠ | P2 | เลือกบัญชีเงิน fallback "111" เงินสดในมือเงียบ · Bulk `DeduplicateMatches` เรียงด้วย `Confidence` อย่างเดียว (AI แต่ง conf ชนะเซิร์ฟเวอร์ — ขัด G1) · `TryDistillationPrePassAsync` คืนลิสต์ว่างเสมอ (stub) · Batch apply `ReconciledBy="AI-Batch"` ทุกแถวแม้ `WasAiValidated=false` · 1:1/Batch/AutoMatch ไม่บันทึกแพตเทิร์น · `UnmatchTransactionAsync` ไม่เป็น negative label · `Learning.cs:145` บันทึกล้ม = LogWarning · FX: AutoMatch/MatchCandidates/AiSmartMatch เทียบ `Payment.Amount` สกุลเอกสารกับยอดธนาคารตรง · `CashForecastService` = `DueDate` ล้วน · `BankFeeDictionary.cs:9` สัญญา "single-click" JE ค่าธรรมเนียม — ไม่มี | ตามบรรทัด |
| D4-T | ✅ | **P0 (โครง)** | `BankService*`/`Bulk*`/`BankMatchScoring`/`BankMatchVerification`/`BankFlowClassifier`/`BankFeeDictionary`/`Cheque`/`PettyCash`/`BankFeed`/`OpenBanking` = **0 ไฟล์เทสต์** | `Accounting.Tests/` |

### D5 · สต็อก · COGS · สินทรัพย์ · ผลิต · ฝากขาย

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| ✅ 3b81ca1 D5-1 | ✅ | **P0** | **FIFO เศษ**: `Math.Round(takeSum / Math.Max(taken, 1m), 4)` — ขาย 0.5 กก. จาก layer 100/กก. ได้ต้นทุน **50** (วัตถุดิบ กก./ลิตร โดนทุกบิล) · ปัด 4 ตำแหน่ง**ไม่ระบุ AwayFromZero** (สำเนาที่สองของ `WeightedAverageCost` ที่ทำถูก) | `Inventory/InventoryCostingService.cs:182,223` |
| ✅ 3b81ca1 D5-6 | ✅ | **P0** | **ด่านสต็อกติดลบอยู่ผิดชั้น**: guard อยู่ใน `ResolveOutboundCostAsync` แต่ `StockLedger.MoveAsync :175` `r.UnitCostOverride ?? …` ข้ามมันทุกครั้งที่มี override ซึ่งเอกสารส่ง**เสมอ** (DS `:13096`) ⇒ `AllowNegativeStock=false` **ไม่เคยถูกบังคับที่ ledger** (ราก E-03) | `Inventory/StockLedger.cs:175` · `InventoryCostingService.cs:112-126` · DS `:13096` |
| ✅ 3b81ca1 D5-2 | ✅ | **P0** | **วัสดุสิ้นเปลืองสองบัญชีไม่หักล้าง**: `TrackStock = request.TrackStock || ProductType == Supplies` บังคับเป็นสินค้าคงเหลือ · ซื้อ → Dr `InventoryAccountId ?? 11500` (resolver `:13241-13246` ดู `tracked` = TrackStock อย่างเดียว) · เบิกใช้ → Cr `SuppliesAccountId ?? 118xx` ⇒ 11500 บวมถาวร | `ProductService.cs:119,1009-1012` · DS `:13224-13264` |
| D5-3 | ⚠ | **P1** | COGS = 0 เงียบเมื่อ avg=0 & CostPrice=0 (`if (cogsTotal > 0)` ไม่มี else/หมายเหตุ) · ไม่พบผัง 511/115 → log เท่านั้น | DS `:13486-13505` |
| D5-4 | ⚠ | **P1** | CN Return กลับ COGS ที่ **WAC ปัจจุบัน** ไม่ใช่ต้นทุนตอนขาย (`StockMovement.UnitCost` ของใบต้นทางมีอยู่) · สินค้า FIFO คืนของถูกตีเป็น `CostPrice` เพราะ `EffectiveUnitCost` คืน avg เฉพาะ WeightedAverage ⇒ layer ใหม่ราคาผิด | DS `:13874-13890,13176` |
| D5-5 | ⚠ | **P1** | capex ตัดสินด้วยผังที่ผู้ใช้เลือกอย่างเดียว · เกณฑ์เงิน 3 ชุด (50,000 / 5,000 / 50,000→0.90) เป็นแค่เตือน · **เพดานรถยนต์นั่ง 1,000,000 (พ.ร.ฎ.145 ม.5) ไม่มีที่ไหนเลย** ⇒ ค่าเสื่อมทางบัญชี=ทางภาษี ไม่มีบวกกลับ · `ImportAsync` `_db.FixedAssets.Add` ตรงข้ามด่านที่ดิน · job ค่าเสื่อมไม่กรอง `NeedsReview` ⇒ ตัว `DRAFT-` อายุ default โพสต์ JE ก่อนใครยืนยัน · วันเริ่มคิด = `DocumentDate` ไม่ใช่วันพร้อมใช้ | `FixedAssetService.cs:1421-1425,1243,861` · `Section65TerValidator.cs:25,257` · `FixedAssetDetector.cs:41,85` · `DurableGoodsHeuristic.cs:16` · `DepreciationSchedule.cs:42-43` |
| D5-7 | ✅ | **P1** | `ReconcileProductTotalsAsync` **ไม่มีผู้เรียก** (callers.py: นิยาม 1 · โค้ดจริง 0 นอก interface) ขณะ doc-comment `StockLedger.cs:186-188` อ้าง "งานตรวจสอบเรียกเป็นระยะ" · WAC 2 สูตร (live ปรับทุก delta>0 รวม ADJUST/TRANSFER_IN vs `RebuildAverageCostAsync` นับเฉพาะ `"IN"`) ⇒ void แล้ว rebuild ได้คนละค่า · `RegisterReceiptAsync` ไม่มีผู้เรียก (Standard-cost variance ไม่เคยรัน) | `StockLedger.cs:147-157,186-188,209-221` |
| D5-C | ⚠ | P1 | ขายก่อนซื้อไม่ true-up (`WeightedAverageCost.cs:22` ปัดสต็อกลบเป็น 0) · ต้นทุนขาเข้าใช้ `line.Amount` ดิบไม่ผ่าน `Conv()` ⇒ ใบซื้อ USD มูลค่าสต็อก ≠ Dr 11500 · ฝากขาย OUT "no GL" + Invoice ไม่มี `ProductCode` ⇒ ต้นทุนไม่เคยถึง P&L · FG = Σ RM เท่านั้น ไม่มี labor/OH ไม่มี JE (`_costing` inject แล้วไม่ใช้) · POC `UpdateProgressAsync` เขียน `RecognizedRevenue` ไม่มี JE · LCNRV ไม่มีโค้ด · 3-way tolerance 2%/1% hardcode | ตามบรรทัดในรายงานทีม |
| D5-D | ⚠ | P1 | `AssetCategorySuggestion` = keyword ล้วน · **ไม่มี `GenericFeedbackDistillationModel` register** แม้ `AiFeedbackTrainingJob` mark เป็นผู้เรียน · UI เรียกแล้วไม่มี `RecordUserChoiceAsync` ⇒ write-only · default "5 ปี" ประทับ conf 0.80 เท่ากับกรณีจับคีย์เวิร์ดได้ (ขัด G1) · ตารางอายุ 3 สำเนา (software 36 vs classifier 60) | `AiSuggestionController.cs:1271-1319` · `Program.cs:607-616` · `fixed-assets.html:370` |

### D6 · เงินเดือน · ปกส. · PIT · HR

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| D6-3 | ✅ | **P0** | **"ฐานประจำ vs ครั้งคราว" ตัดสินจาก prefix ของ `PayrollItem.Code`** (`OT*` · `COM` · `BONUS` · ที่เหลือทุกตัว = เบี้ยเลี้ยงประจำ) ⇒ โค้ดที่ผู้ใช้ตั้งเอง (`BN01`, `INCENTIVE`) ถูกฉาย ×งวดที่เหลือ = บั๊ก D-T3 กลับมาทางประตูหลัง · `PayrollItem.IsTaxable` เขียน `:958` **อ่าน 0 จุดใน engine** = silent no-op (กฎเหล็ก #4 A) | `PayrollService.cs:1665-1682,958,1833` |
| D6-1 | ✅ | **P0 (กฎหมาย — เจ้าของตัดสิน)** | ฐานค่าจ้าง ปกส. = `proratedBaseSalary` **เท่านั้น** ไม่รวมเบี้ยเลี้ยงประจำ (ม.5 "ค่าจ้าง" รวมค่าครองชีพ/ค่าตำแหน่ง ตามแนวฎีกา) · golden: 12,000 + ค่าครองชีพ 2,000 → ฐาน 14,000 → 700/ฝ่าย (วันนี้ 600) · **เปลี่ยนแล้วค่าจ้างที่ประกาศไปแล้วเปลี่ยน** ⇒ ต้องนักบัญชียืนยัน + migration | `PayrollService.cs:1839-1852` · `Helpers/SsoWageBase.cs:34-40` |
| D6-2 | ✅ | **P1** | หักคืนเงินทดรอง: cap แค่ `NetPay` (หักได้ 100%) · ไม่มีธงยินยอม (พ.ร.บ.คุ้มครองแรงงาน §76 = 1/5 ของค่าจ้างเว้นยินยอม) | `PayrollService.cs:2405-2416` |
| D6-4 | ✅ | **P1** | 50 ทวิ พนักงาน: `TaxId` ว่าง → `continue` เงียบ · ทั้งเมธอด `catch→LogWarning` · ด่านนำส่ง ภ.ง.ด.1 **ไม่มี gap-hint** (มีเฉพาะ 3/53) — ขัดคำตัดสินเจ้าของ (ค) รอบ 170 · รายปีใช้ `GrossIncome` ขณะ 1ก ใช้ `TaxableGross` | `PayrollService.cs:3102,3172,2667,2686` · `StatutoryRemittanceService.cs:722-740` |
| D6-5 | ⚠ | P1 | `UpdatePayrollDetailAsync` แก้รายได้โดยไม่ระบุ WHT → **throw ให้ HR กรอกเลขภาษีเอง** ไม่มี preview จาก `ThaiPitCalculator` · แก้ WHT รายคนไม่ถูก capture | `PayrollService.cs:1456-1465` |
| D6-6 | ⚠ | P2 | ค่าลดหย่อน 3 สำเนา (`TaxRuleConfig` defaults · `Pit*` const · literal `?? 60_000m/30_000m/100_000m`) · กท.20ก cap 20,000 literal + `Math.Round(x,2)` banker's + ฐานไม่ prorate · `annualSso = ssoEmployee×12` ฉายเดือนลดอัตราชั่วคราวเป็นทั้งปี · PVD/% คิดจาก `BaseSalary` เต็มไม่ prorate | `PayrollService.cs:53-55,1864-1867,1871-1876,1914-1927` · `Payroll.cs:521-556` |
| D6-D | ✅ | P1 | `AiSuggestionController.cs:1326-1367` (income-type) rule ล้วน · **ไม่มีผู้เรียก** · ไม่ `RecordCallAsync` · ไม่มี student · `AiFeedbackTrainingJob` mark "consumed" ทั้งที่ไม่มีแถว · ทั้งเส้นเงินเดือน**ไม่มี capture สักจุด** · `Nationality` ไม่ถูกอ่านที่ใดในเส้นเงินเดือน · `ExpenseClaimService:661-667` ลง 21916/21917 ไม่ผูก form/cert · `IncomeTypeCode` ฮาร์ด "1" ทุกใบ | ตามบรรทัด |
| D6-10 | ✅ | P2 | dead helpers ที่ต้องตัดสิน: `PayrollRunFilingScope.CanRemit/CountsTowardFiling` → **ต่อสาย** (ผู้เรียกทำสำเนาเอง: `.FilingStatuses.Contains` 7 จุด · `== Paid` 5 จุด · literal `"Paid"/"Approved"` 2 จุด) · `SsoRateSchedule.RangesOverlap` → **ลบ** (ยุบเป็น private ใน `RangesAmbiguous` — public ชวนให้ใช้เป็นด่านแล้วบล็อกช่วงซ้อนที่ถูกกฎหมาย) · `GetMaxContribution` ลบ | `tools/dead_helper_baseline.txt:39,40,48,49` |

### D7 · สถาปัตยกรรม AI / การเรียนรู้ ทั้งระบบ (`AiFeatureKey` 57 ค่า · นักเรียน 21 ตัว · เทรนทุก 6 ชม.)

**คำตอบสั้นของทีม (ยืนยันแล้ว)**: 4 ขั้นของกฎเหล็ก #1 มี "ครั้งแรก" ครบ (orchestrator ตัวเดียว · ไม่พบเส้นข้าม ·
เทรน/โหลดอัตโนมัติ) แต่ "ดีขึ้นเรื่อย ๆ **เอง**" ยังไม่จริง 3 จุด: ป้ายถูกเขียนด้วยกุญแจที่ฝั่งอ่านสร้างไม่ได้อีก (D7-2) ·
หน้าเว็บ 6 จุดไม่ส่ง `source` ⇒ ด่าน Explicit≥1 ของรอบ 178 ปิดคลังทันทีของ 3 feature เงียบ (D7-3) · สัญญาณสุขภาพคำนวณแล้ว
**ไม่มีใครทำอะไรต่อ** — ไม่มี governor (D7-E)

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| ✅ 3b81ca1 D7-1 | ✅ | **P0** | widget `ai-suggestion.js:123` ส่ง `chosenAnswer:'__USER_KEPT_EXISTING__'` — **server ไม่กรอง** (grep .cs = 0) ⇒ `LearnInlineAsync` เก็บเป็น `LearnedAnswer` · `TrainGlAccountAsync` insert `OcrCategoryMapping.AccountCode = sentinel` · `GlAccountDistillationModel` นับเป็นบัญชีที่ยืนยัน ⇒ **รหัสผังปลอมไหลเข้าตัวแนะนำ GL** | `wwwroot/js/ai-suggestion.js:120-123` · `AiFeedbackRecorder.cs:280-296` · `AiFeedbackTrainingJob.cs:556-566` · `GlAccountDistillationModel.cs:97-110` |
| ✅ 3b81ca1 D7-2 | ✅ | **P0** | **กุญแจเขียน ≠ กุญแจอ่าน ทันทีที่นักเรียนตอบได้** (รูของรอบ 178 เอง): `AiOrchestrator :154` ทำนายจาก JSON เดิม → `:177` `request = request with { UserPromptJson = ReplaceLocalModelBlock(...) }` → `:310` `memoryKey = AiMemoryKey.Of(request.UserPromptJson)` คิดจาก JSON **หลังแก้** · `GenericFeedbackDistillationModel :212` predict จาก JSON ก่อนแก้ · `:134` เรียนจาก `PromptJson` หลังแก้ ⇒ prompt ที่มีบล็อก `local_model` (9 ไฟล์ prompt) ทำให้ generic 5 ตัว (DocType · WhtCategory · CreditNote · PVAccounting · DocConversion) tier-0/1 **หยุดโตหลังใบแรก** | `Services/Ai/AiOrchestrator.cs:154-178,310,540` · `GenericFeedbackDistillationModel.cs:134,212` |
| ✅ 3b81ca1 D7-3 | ✅ | **P0** | 6 จุด JS ผูก `change` แล้ว**ไม่ส่ง `source`** ⇒ Implicit ⇒ `MemoryLookupAsync` ปฏิเสธ (Explicit≥1) ⇒ คลังทันทีของ ManualJeAccount / ProductCategory / GlAccountSlot(recurring) / AssetCategory / BankStatementMatch / OcrFullReview+DocConversion **ตายหลังรอบ 178** (backfill ครอบแค่แถวเก่า) | `journals.html:607` · `products.html:492` · `fixed-assets.html:343` · `bank.html:1311` · `recurring.html:404` · `document-scan.html:3609,3614` |
| ✅ 3b81ca1 D7-4 | ✅ | **P1** | `MinSamplingRate` บังคับแค่ `SetAsync` · ฝั่งอ่าน `:143` `?? DefaultSampling` ไม่ clamp · migration มีแค่ CREATE ไม่มี UPDATE ⇒ แถวที่เคยตั้ง 0 ยัง 0 (kill-teacher ที่รอบ 178 ตั้งใจกัน ยังเกิดกับข้อมูลเก่า) | `AiFeatureRoutingResolver.cs:86-91,143` · `DatabaseMigrationHelper.cs:4229` |
| ✅ 3b81ca1 D7-5 | ✅ | **P1** | orphan detector `_ => false` กับ 6 key ที่**มีนักเรียนเรียนตรงจากแถวอยู่แล้ว** (PaymentType · OcrProjectMatch · OcrLineItemSplit · Chat×2 · FuzzyDuplicate) ⇒ ประทับ `NeedsRedesign "ขาด trainer"` ทุกรอบ = false alarm · query `FirstOrDefaultAsync(h => h.FeatureKey == featureKey)` **ไม่กรอง `CompanyId`** ⇒ หลังรอบ 179 มีแถวรายบริษัท อาจคว้าแถวบริษัทมาประทับ | `AiFeedbackTrainingJob.cs:300-323,470` |
| D7-6 | ⚠ | **P1** | CAPTURE ขาด 7 จุด (ได้ FeedbackId แล้วทิ้ง/ไม่ปิด): AnomalyExplanation (`AiService.cs:571-580` MarkFalsePositive ไม่เรียก recorder) · StockMovementValidation (`document-scan.html` ไม่เก็บ id) · AgingExplanation (ไม่มีนักเรียน + ไม่ปิด) · ImportColumnMatch/ImportDataReview (`ImportExportService` ไม่เก็บ FeedbackId เลย) · FuzzyDuplicate (ไม่มี UI) · **PayrollIncomeType ไม่มี `RecordCallAsync` = CAPTURE ศูนย์** (ตรง D6-D) · `BankMatchDistillationModel` เรียนจาก `BankMatchRules.TimesConfirmed` ที่เขียนเฉพาะ `OcrService:4603` ไม่ใช่จาก feedback · `OcrLineItemSplit` negative-only (ตรง D3-7) | ตามบรรทัด |
| D7-7 | ⚠ | P2 | `ApprovalWarningFixSuggestion` ไม่มี guard (free-text) · `BankStatementMatch` `BankAiAugmenter:113-131` ไม่พบ candidate check · `BulkBankAiMatchService:147` child rows hardcode `DeepSeek` · `/accuracy` `/routing` ไม่ส่ง `LocalCoverage30d`/`ExplicitLabels30d` (โผล่แค่ในสตริง Recommendation) · แถว health รายบริษัทไม่มีผู้อ่าน · enum doc `DocumentMemo :1732` บอกเรียก AI เมื่อ ≥3 หมวด แต่โค้ดไม่เรียก (doc ผิด) · T3-P1a ตัวเลขที่ถูก: consume-only 29 key ในนั้นเรียก AI จริงแค่ 2 · key ตาย: 6 · 18 · 21 (0 caller) | ตามบรรทัด |
| D7-E | ✅ | **P1 (โครง)** | **ไม่มีวงจรปิด**: grep `NeedsRedesign|Degraded` นอก job/controller = 0 ⇒ สุขภาพตกแล้วทำแค่แสดง badge · routing เปลี่ยนได้ทางเดียวคือคน PUT · `Version` bump ทุกรอบไม่มีเทสต์ก่อน promote · **ไม่มีเทสต์ orchestrator/Generic/job/resolver เลย** (มีแค่ `AiMemoryKeyTests` · `LineSplitStudentTests`) | `AiFeedbackTrainingJob.cs` · `admin/ai-models.html:220` |
| D7-D | ✅ | P2 | ควรเรียนแต่เป็นกฎแข็ง (นโยบายบริษัท): `OcrEntryModeAdvisor` (Stock/Expense ต่อ (vendor,keyword)) · `AssetRegistrationPlanner` (รวม/แยกต่อผัง) · AssetCategory ไม่มี memory ทั้งที่ endpoint อื่นมี · **ถูกแล้วที่ไม่มี AI**: `Pp30SalesClassifier` · `InputVatAccountPolicy` · `SettlementReceiptPolicy` · `ReceiptIssuePolicy` · `CashSaleStockRules` · `TipAccountResolver` · `GlDebitAccountPicker` · key 39,40,42,44,47–52 | — |

### D8 · POS · CMS · ที่พัก · บิลลิ่ง · ทางเข้าภายนอก

| ID | สถานะ | P | เรื่อง | ที่ |
| --- | --- | --- | --- | --- |
| D8-1 | ✅ | **P0** | **POS ใบกำกับเต็มรูป** สร้าง `Document` ตรง `Status=Approved` ไม่ผ่าน `ApproveDocumentAsync`/`CanApproveAsync`/quota/§86/4 · บรรทัดจาก `item.TotalAmount/VatAmount` **ไม่หักส่วนลดบิล/คูปอง/ค่าบริการ** (`RecalculateOrder` หัก `DiscountAmount` ที่ระดับออเดอร์) ⇒ ยอดใบ ≠ ยอดที่ลูกค้าจ่าย ≠ JE · ไม่ตั้ง `Document.BranchId`/`IsTaxInvoiceByLaw` · ไม่อ้าง/ยกเลิกใบย่อ (`AbbreviatedInvoiceNumber`) · Contact จับด้วยชื่อเท่านั้น | `PosService.Orders.cs:437-503,460,476-478,1739-1746` |
| D8-2 | ✅ | **P0** | **POS คืนเงินนับคูปองซ้ำ**: `orderLevelDiscount = DiscountAmount + CouponDiscountAmount` แต่ `DiscountAmount` **รวมคูปองอยู่แล้ว** (`:1740`) ⇒ คืนเงินต่ำกว่าจริง · เทสต์ = 0 | `PosService.Orders.cs:236-241,1740` |
| ✅ 3b81ca1 D8-3 | ✅ | **P0** | **V1 ContactType**: `taxId.Length == 13 ⇒ JuristicPerson` — เลขไทยทุกใบ 13 หลัก ⇒ บุคคลธรรมดาที่มีบัตรกลายเป็นนิติบุคคล ⇒ **ภ.ง.ด.53 แทน 3** · `DocumentsV1Controller:215` สำเนาอีกชุด (`StartsWith('0')`) = กติกา 3 สำเนา (ตัวถูกคือ `ThaiTaxId.IsJuristic` ที่ Integration ใช้) · V1 อนุมัติด้วย scope `documents:write` ไม่มี `CanApproveAsync` | `V1/ContactsV1Controller.cs:128` · `V1/DocumentsV1Controller.cs:149-156,215` |
| ✅ 3b81ca1 D8-4 | ✅ | **P0** | **Integration `request.VatRate ?? 7m` 6 จุด โดยไม่มี `IsVatRegistered` เลยทั้งไฟล์** (grep = 0) ⇒ tenant ไม่จด VAT ได้ VAT 7% · + `Status=Approved` ตรง 6 จุด (ERP_REVIEW รู้แล้ว) | `IntegrationService.cs:691,2625,2709,3124,3245,3502` |
| D8-5 | ⚠ | **P1** | CMS: PreOrder/Backorder → `StockDeducted=true` **โดยไม่ตัด** → ยกเลิก `RestoreStock` **บวกคืน** ⇒ สต็อกบวมเงียบ · ฐาน VAT ค่าส่ง/ส่วนลด `VatRate:0` ขัด §79 · Contact ไม่ตั้ง ContactType / TaxId ไม่ตรวจ checksum · booking `FreeCancellationHours/CancellationFeePercent` ไม่มีผู้อ่านนอก CRUD | `CmsCommerceService.cs:864-874,936-958,1244-1251,1315-1325` · `CmsBookingService.cs:38-42,346-353` |
| D8-6 | ⚠ | **P1** | ที่พัก: เช็คเอาต์ `Approve(acknowledgeWarnings:true)` ⇒ ที่อยู่ผู้ซื้อ §86/4 (warning) ถูก ack อัตโนมัติ ใบกำกับแขกไม่มีที่อยู่ออกเงียบ · `BranchCode="00000"` แต่งขึ้น · ContactType จาก "มีชื่อบริษัทไหม" ไม่ใช่ `ThaiTaxId.IsJuristic` · **Night audit ประทับ NoShow ตรง**ไม่ผ่าน `CancelCoreAsync` ⇒ มัดจำค้าง 217xx สถานะ Terminal · ค่าปรับ > มัดจำ แค่ InternalNotes · snapshot นโยบายพัง → `catch → fee 0` เงียบ · VAT `7` ตายตัว (ไม่ใช่ `OutputVatRate.ForCompany`) | `Lodging*/Lifecycle.cs:296-374,603-618,718-764` · `Reservations.cs:413-434` · `LodgingNightAuditJob.cs:122-130` · `LodgingService.cs:69-75` |
| D8-7 | ✅ | **P1** | **ไม่มีด่านสิทธิ์เลย**: `PosController` (0/51 endpoint รวม `issue-tax-invoice`) · `CmsCommerceController` · `CmsBookingController` · `IntegrationController` · `MobileController` (เทียบ `LodgingController` 41 อ้าง) · Mobile ExpenseClaim ตั้ง Approved ตรง | ตาม controller |
| D8-8 | ⚠ | P2 | POS ทิป: `TipAccountResolver` ไม่พบ → **ลงรายได้ขาย** + LogWarning · POS VAT อัตราบริษัทเดียวทุกรายการ (สินค้ายกเว้น §81 ถูกคิด 7%) · เลขชุดอย่างย่อใช้ `"00000"` · แพลตฟอร์ม issuer ล้ม → PDF เดี่ยว + เลขชุดของตัวเอง + HTML renderer ของตัวเอง (สอง renderer) · CMS `RequestTaxInvoice=false` แต่จด VAT → "ใบแจ้งหนี้" ที่มี VAT (ต้องยืนยัน `IsTaxInvoiceByLaw` รับช่วง) | `PosService.Orders.cs:1305-1320,1633-1685,1212` · `PlatformBillingDocumentIssuer.cs:78-100,308,441-560` · `CmsCommerceService.cs:884-886` |
| D8-D | ✅ | P1 | grep `IAiOrchestrator|AskAsync` ทั้ง POS/CMS/Lodging/Integration/LINE/Mobile/V1/บิลลิ่ง = **0** · ควรมีนักเรียน (candidate set ปิด): SKU ภายนอก↔สินค้าเรา (`IntegrationService:1908` exact เท่านั้น) · LINE "บันทึก เซเว่น 250" → บัญชี/หมวดว่างทั้งที่ `GlAccountDistillationModel` มี · dedupe Contact CMS ด้วย Email เท่านั้น (GAP-2 คำตอบที่ 5) | ตามบรรทัด |

---

## §4 สิ่งที่ทีมรายงานมาแล้ว **แก้ไปแล้ว / ต้องอ่านคู่กับคำตัดสินเดิม**

- **D3 อ้าง T3-P1b "`LocalModelHealth` ไม่มี `CompanyId`"** — แก้แล้วรอบ 179 (`f23a89e`/`087b0c1`: `CompanyId` + partial unique index 2 ตัว + แถว platform aggregate) → ติ๊กใน DOCTRINE §4.2 แล้ว
- **D2 อ้าง T2-4 (`wht/infer-category` ไม่ผ่าน `ThaiWhtRateTable`)** — แก้แล้วรอบ 178 (`c79dd90`) → ติ๊กแล้ว
- **D1-1 / D3-2 ทับกัน**: D1 ให้ด่าน WHT ใหม่ ✅ ครบตาม §2 ขณะ D3 พบว่าอินพุตชั้น 0 ของด่านนั้น (`scan.HasWht`) ถูก VendorIntel เขียนสวมรอย ⇒ ด่านถูก แต่ **หลักฐานที่ป้อนปลอม** — ต้องแก้ที่ D3-2 ไม่ใช่ที่ด่าน
- **D8 สถานะเรื่องที่รู้แล้ว** (ยืนยัน): ภ.พ.06 บนสลิป POS แก้แล้ว (`PosSlipHeader`) · POS ผูก Branch/Warehouse บน `PosOrder` แล้วแต่ **ใบกำกับเต็มรูปยังไม่ตั้ง `Document.BranchId`** (D8-1) · `PublicPaymentController` มีแล้ว (LDG-P0-01) · E-commerce มี UI บางส่วน (`api.js:1176-1178` + option ใน integrations) — ยังไม่ยืนยันว่าครบ

## §5 ตรวจแล้ว **ไม่จริง / บรรทัดคลาด** — ห้ามรายงานซ้ำ

- **D1-13 "`CheckCreditAsync` ผ่านเงียบที่ `:4974-4977`"** — บรรทัดนั้นคือ 3-way match GRN (throw เมื่อไม่ ack) ไม่ใช่ด่านเครดิต · ตัวด่านเครดิตยังไม่ได้เปิดยืนยัน (คง ⚠ ใน §3) — บทเรียนเดิม: ระบุบรรทัดต้องเปิดตรงบรรทัด
- **D3-3 "`:1467`"** — ของจริงอยู่ `:1463` (`Math.Max`) — สาระถูก บรรทัดคลาด 4
- **D3-4 "`:5993`"** — ของจริง `:6006-6009` — สาระถูก
- **D7 ทุกข้อ P0/P1 ยืนยันแล้ว** (D7-1..5 เปิดไฟล์ตรงบรรทัด) — 3 ใน 5 เป็นรูใน**งานรอบ 178–180 ของ main agent เอง** (`AiMemoryKey` · ด่าน Explicit≥1 · `MinSamplingRate`) ⇒ บทเรียน: ด่านที่เพิ่มฝั่ง server ต้อง grep ผู้เรียกฝั่ง JS ทุกตัวในคอมมิตเดียว (F2 ข้อ 1) และกุญแจต้องมีเทสต์ "เขียน == อ่าน" ด้วย payload จริง ไม่ใช่ payload สังเคราะห์
- ไม่มีข้อไหนใน 8 ทีมที่**สาระ**ไม่จริงหลังเปิดไฟล์ (ต่างจากรอบ 177/178 ที่พบ 2 ข้อ) — เพราะ brief รอบนี้บังคับ "เปิดไฟล์ + `callers.py` ทุกสัญลักษณ์ + ห้ามรายงานซ้ำ"

---

## §6 แผนรอบพัฒนาถัดไป (สำหรับ Opus) — เรียงตามผลต่อความถูกต้อง × ความเสี่ยงต่ำ

> กติกาทุกข้อ: **เทสต์ golden ก่อนแตะ** (กฎ #4 H — รันชุดกระดาษ/ตัวเลขจริงก่อนและหลัง อธิบายทุกใบที่คำตอบเปลี่ยน) ·
> pure helper ใน `Helpers/` + เทสต์สองทิศในคอมมิตเดียว · `callers.py` ทุกสัญลักษณ์ที่แตะ · ทางเข้าอื่น (POS/Integration/
> Import/V1/LINE/Mobile/job) เดินด่านเดียวกัน · ค่าที่ persist ไว้ผิด → migration · ตอบ 12 ข้อ F3 ในข้อความคอมมิต ·
> **ห้าม amend หลังจด sha** · หลัง push อ่าน CI ผ่าน MCP

### §6.0 ก่อนทุกอย่าง — ตาข่ายเทสต์ที่ยังไม่มี (1–2 คอมมิต · ไม่แตะพฤติกรรม)

| # | เทสต์ | ล็อกอะไร |
| --- | --- | --- |
| T-A | `ApprovalWarningGoldenTests` — ประกอบ `Document` in-memory เรียก `CollectApprovalWarningsAsync` ผ่าน harness (ต้องแยก IO ออกเป็น facts ก่อน — ทำเป็น `Helpers/ApprovalWarningFacts` + pure `ApprovalWarningRules.Evaluate(facts)`) | ใบซื้อสินค้า IKEA → 0 WHT warn · จ้างเหมา 800×3 → warn · CN ขาย ≥1,000 "ค่าบริการ" → 0 · PO → 0 · Invoice ไม่มี ProductCode + VAT → เตือน 21911 (หลังแก้ D1-7) |
| T-B | `BankMatchGoldenTests` (9 เคสจาก D4-E9) | ยอดเท่า 2 ใบ ชื่อต่าง → เลือกตามชื่อ · ชื่อไม่มี → ไม่ประทับ · Makro gross/net → ผ่านทางเดียว · Tolerance 100 → ปฏิเสธ · AI อ้าง id ปลอม → ทิ้ง · เช็คเด้งหลัง Paid → ใบกลับค้าง · จ่ายเกินผ่าน Integration → throw · petty cash ไม่มีใบเสร็จ → block |
| T-C | `FifoFractionTests` · `CitRateTableTests` · `PosRefundDiscountTests` · `PayrollItemNatureTests` | layer 100/กก. ขาย 0.5 → 100 · ทุน 10 ล. กำไร 1 ล. → 200,000 · บิลลด 10% + คูปอง → คืนเงินถูก · `BN01` "โบนัส" 300,000 เดือน 6 → 61,925 |
| T-D | `TerminalStatusWriterCheck` (checker + negative test) · ขยาย `write_permission_gate_check` ครอบ Pos/Cms*/Integration/Mobile controller | R1 · R5 |

### §6.1 P0 — ทำได้เลย ไม่ต้องรอเจ้าของ (ทิศชัด · เป็นบั๊ก ไม่ใช่นโยบาย)

| # | ID | งาน | helper/เทสต์ | เสี่ยง |
| --- | --- | --- | --- | --- |
| 1 | D1-B1 | **ถอดด่าน WHT เก่า** `:16697-16719` + `IsPureGoodsPurchaseAsync` + `CheckWhtThresholdAsync` (ผู้เรียกเดียว) · `knownWhtRates` → `ThaiWhtRateTable.StatutoryRates` (D1-3) · DOCUMENT_FLOW `:2130/2171` | T-A ก่อน/หลัง | กลาง — เคส "บรรทัดอิสระ+ไม่มีคำบ่งชี้" จะเงียบจริงตามคำตัดสินรอบ 179 (ถูกตามคำสั่ง แต่ต้องบอกเจ้าของว่าเกิด) |
| 2 | D1-B2 | `Helpers/WhtGateScope.Applies(type, cnDnPurchaseSide)` = ชุดเดียวกับ `WhtCumulativeScope.Counts` ใช้ที่ `:16414/:16444` (ตัด PO/PR/GRN · CN/DN ตาม override) | เทสต์ทิศตรงข้าม: CN ขาย → 0 · PO → 0 · PI บริการ → เข้า | ต่ำ |
| 3 | D3-1 | ย้าย serialize items ไปท้าย pipeline (ใต้ `:2915`) + `Serialize(items)` เต็มแบบ `:6903` | simulation `BuildScanLines` JSON 2 บรรทัดคนละบัญชี | ต่ำ |
| 4 | D3-2 | VendorIntel เขียน `SuggestedWhtRate` แทน `HasWht/WhtRate` · `ScanPaperWhtEvidenceAsync` ตัด `scan.HasWht` ใช้ `PaperWhtReader` ล้วน · VendorIntel เรียน WHT เฉพาะเอกสารที่ WHT มาจาก Explicit | `WhtApplicabilityEvidenceTests` + ทิศกลับ "กระดาษพิมพ์ 3% ยังหัก" | **ถดถอยที่ตั้งใจ**: ผู้ขายบริการประจำได้แค่ warn (ไม่ auto-fill) — สอดคล้องคำตัดสิน 179 "เตือนเมื่อมีเหตุ" · ต้องบอกเจ้าของ |
| 5 | D2-B2a | `Helpers/CitRateTable.Compute(netProfit, isSme)` ตัวเดียว · `CalculateThaiCit` รับ `isSme` จากเกณฑ์เดียวกับ 51 | T-C | ต่ำ — ตัวเลข ภ.ง.ด.50 ของบริษัททั่วไปจะ**เพิ่ม**เป็นค่าถูก |
| 6 | D5-1 | FIFO `taken > 0 ? takeSum/taken : fallback` + AwayFromZero ทั้ง 2 จุด | T-C | ต่ำ |
| 7 | D5-6 | ย้ายด่านสต็อกติดลบเข้า `StockLedger.MoveAsync` (delta<0 ทุกกรณี ต่อคลัง) — ปิด E-03 ที่ราก | ทิศตรงข้าม: `AllowNegativeStock=true` ยังผ่าน | กลาง — ใบที่เคยอนุมัติได้ทั้งสต็อกไม่พอจะถูกบล็อก (ทางไปต่อ = เปิดตั้งค่า) |
| 8 | D8-2 | POS refund ใช้ `DiscountAmount` ตัวเดียว | T-C สองทิศ | ต่ำ |
| 9 | D8-3 | V1 2 จุด → `ThaiTaxId.IsJuristic` + migration แถวที่ประทับผิด (`ContactType=JuristicPerson && !IsJuristic(TaxId)` ที่ `UpdatedBy='api:v1:*'`) | เทสต์ 3xxxx → Individual | ต่ำ |
| 10 | D8-4 | Integration 6 จุด → `OutputVatRate.ForCompany` | เทสต์ tenant ไม่จด VAT → 0 | ต่ำ |
| 11 | D4-3 | `Helpers/DocumentSettlementState.Apply(total, paid) → {Status, BalanceDue, Overpaid}` ตัวเดียว ใช้ 7 จุด · Integration/Import เดินด่านเกินยอดเดียวกับเว็บ | T-B "จ่ายเกินผ่าน Integration → throw" | ต่ำ |
| 12 | D4-2 | BankFeed ห้ามประทับ `Matched` ไม่มีคู่ → เก็บ id + `ValidateMatchAmountAsync` หรือลดเป็น `Suggested`+`SuggestedItemId` · migration แถว Matched ที่ id ว่าง → Unmatched | T-B | ต่ำ |
| 13 | D1-11 | `DocumentAiAugmenter` คืน `Answer=null` เมื่อไม่มีโมเดลตอบ · DS เลิก `?? "Acknowledge"` · UI ซ่อนป้าย | negative: provider ปิด ⇒ 0 แถว acceptedAi=true | ต่ำ |
| 14 | D3-4 | create เทียบ `TargetDocTypeAiSuggested` (ตาม `:4484`) | เทสต์ label | ต่ำ |
| 15 | D3-3 | ลบ `Math.Max` ยก confidence · `MathConsistent=false` → แท็ก `[MATH]` ที่ `OcrPostingReadiness` บล็อก | `OcrGatewayFalseAlarmTests` | ต่ำ |
| 16 | D2-B1a/b | **แยก "ประกาศว่ายื่น" ออกจาก "ยื่นแล้ว"**: `FileTaxReportAsync` ต้องการ `FilingNumber` + บล็อกเมื่อมีแถว excluded (ด่านเดียวกับ `RemitAsync`) · ป้าย UI "บันทึกการยื่นด้วยมือ" · ลบ simulate ใน `ComplianceService` (หรือเปลี่ยนเป็นบันทึกมือ ไม่แต่งเลข) · เงินเพิ่ม cap = ภาษี | เทสต์: report มี cert ร่าง → File throw | กลาง — UX เปลี่ยน (ต้องกรอกเลขรับ) ⇒ **แจ้งเจ้าของก่อน** แต่ทิศไม่มีทางเลือกอื่นตาม F2 ข้อ 3 |
| 17 | D8-1 | POS ใบกำกับเต็มรูป → `CreateDocumentAsync`+`ApproveDocumentAsync` + บรรทัดส่วนลด/ค่าบริการให้ยอด = `NetAmount` + `BranchId`/`IsTaxInvoiceByLaw` + อ้างใบย่อ | เทสต์ "บิลลด 10% ⇒ ใบกำกับ 900" | กลาง — quota/สิทธิ์จะเริ่มบังคับกับ POS |
| 18 | D6-3 | `IncomeNature` enum บน `PayrollItem` + อ่าน `IsTaxable` จริง · migration: เติม `IncomeNature` จาก prefix เดิม (คงพฤติกรรมเดิมสำหรับข้อมูลเก่า) · register `GenericFeedbackDistillationModel(PayrollIncomeTypeSuggestion)` + `RecordCallAsync`/`RecordUserChoiceAsync` ตอน HR บันทึก item | T-C | ต่ำ (migration คงพฤติกรรม) |
| 19 | D7-1 | recorder กรอง `__USER_KEPT_EXISTING__` (และ sentinel อื่นใน allow-list เดียว) + migration ล้าง `OcrCategoryMapping`/`AiSuggestionMemory` ที่มีค่านี้ | เทสต์ recorder ปฏิเสธ sentinel | ต่ำ |
| 20 | D7-2 | คิด `memoryKey` **ก่อน** `ReplaceLocalModelBlock` (หรือให้ `AiMemoryKey.Canonicalise` ตัดบล็อก `local_model` ทิ้ง — ทางหลังกันทุกผู้เรียก) + เทสต์ predict-key == record-key ด้วย prompt จริงที่มีบล็อก | `AiMemoryKeyTests` เพิ่มเคส | ต่ำ |
| 21 | D7-3 | เติม `source:'Explicit'` 6 จุด + `api.aiFeedbackRecord(..., 'Explicit')` ที่ document-scan 2 จุด + backfill `ExplicitAcceptCount` รอบสอง (เฉพาะ feature ที่ UI ยิงจาก `change` เท่านั้น) | checker: body ของ `ai-feedback/record` ต้องมี `source` | ต่ำ |
| 22 | D7-4/5 | migration `ProviderSamplingRate=0 → 0.01` (mode≠LocalOnly) + clamp ตอนอ่าน `:143` · orphan detector ยกเว้น key ที่มี `ILocalDistillationModel` register + กรอง `CompanyId == null` | เทสต์ resolver อ่าน 0 → 0.01 | ต่ำ |

### §6.2 P1 — ทำได้เลย (เรียงตามโดเมน)

- **เอกสาร**: D1-7 `Helpers/SupplyKindEvidence.Judge(lines, productKinds) → Goods/Service/Unknown` ใช้ที่ `:13458` · `:5150` (ส่ง `SupplyKind`) · `:16390` — `Unknown` บน Invoice มี VAT ⇒ **21911 + เตือน** · D1-C §82/3 ที่ post ผ่าน `EvaluateClaimPeriod` + แก้ข้อความ `:16752` · D1-9 `yearStart` จาก `FiscalYear` + ตัด Receipt ที่ settle + keyword รับรองย้ายเข้า validator · D1-D `BulkApprove` เมื่อคำเตือน WHT ถูกโชว์ · D1-B5 ย้าย hard block ที่ตัดสินจากตัวเอกสารล้วนขึ้นก่อน `CollectApprovalWarningsAsync`
- **ภาษี**: D2-B4a PDPA hold = `MAX(FY end+5y, filing+5y)` helper เดียว · D2-B4b `Helpers/Pp36PeriodOf(doc)` = วันจ่าย ใช้ทั้งรายงาน/ไฟล์/JE · D2-B3 default `"8"/"40(8)"/3m/7m/"6"` → excluded+reviewNote · D2-B3b ลบ `?? RateFor(code, !payeeIsJuristic)` · D2-C1 แถว ม.70 + ตาราง DTA (`Helpers/DtaRateTable` — ข้อมูล treaty ต้องเจ้าของยืนยันแหล่ง) · D2-C2 `rdReceiptNumber` บังคับ · D2-B2b `TaxComplianceChecker:203` → `VatReportLineKind.SideOf` · `RecalcVatTotals` → helper เดียวกัน
- **OCR**: D3-5 Readiness เพิ่มแท็ก + checker ฟ้อง `\n[` ใหม่ที่ไม่อยู่ใน BlockingTags/allow-list · D3-B ย้าย `EnrichFromDbdAsync` ก่อน AI (`:1651`) — วัด latency ก่อน · D3-6 `:2185` เรียก `OcrDuplicateScanRule` · D3-7 Σ VAT ≠ หัว → แท็ก · `InferCreditNoteReason` → student (candidate set = enum)
- **ธนาคาร**: D4-1 `Helpers/BankMatchArbiter` pure (รับ candidates จาก `MatchCandidates` → apply เมื่อ conf ≥ เกณฑ์ **และ** ไม่มีคู่รองห่าง < margin — เสมอ = ไม่ตัดสิน) · BankFeed/OpenBanking/AutoMatch เรียกตัวเดียว · OpenBanking เติม `CompanyId` + กัน double-match · ป้าย `bank.html:214` → "⚙️ ระบบแนะนำ" · D4-4 Tolerance เพดาน (≤1 บาท หรือแนบเหตุผล+ผังส่วนต่าง) · D4-5 ชนิดไม่รู้ทิศ → ปฏิเสธ · net/gross ตาม `WhtRecognitionBasis` · D4-6 เช็คเด้ง → void Payment + ใบกลับ + JE กลับ ในทรานแซกชันเดียว · เช็ครับไม่มีบัญชีฝาก → throw · D4-7 PettyCash เดิน `Section65TerValidator` + ใบเสร็จบังคับ + `EnsureFiscalPeriodOpen` + Replenish มี JE
- **สต็อก/สินทรัพย์**: D5-2 resolver ฝั่งซื้อ `ProductType == Supplies` → `SuppliesAccountId ?? 118xx` + migration ยอดเก่า · D5-3 COGS 0 → `AppendInternalNote` + warning · D5-4 CN Return อ่าน `StockMovement.UnitCost` ใบต้นทาง + `EffectiveUnitCost` FIFO ไม่ตก CostPrice · D5-5 เพดานรถยนต์นั่ง 1,000,000 ใน `Section65TerValidator`/ค่าเสื่อมทางภาษี · `ImportAsync` → `CreateAsync` · job ข้าม `NeedsReview` · ยุบตารางอายุเป็น `FixedAssetAccountClassifier` + register Generic student + `RecordUserChoiceAsync` จากหน้าทะเบียน · default conf ≤0.5 · D5-7 ต่อสาย `ReconcileProductTotalsAsync` เข้างานกลางคืน (`JobLock`) · `RebuildAverageCostAsync` replay ผ่าน `WeightedAverageCost.Next` · ลบ `RegisterReceiptAsync`
- **เงินเดือน**: D6-2 cap 1/5 เว้น `ConsentAt` · D6-4 `:3102` conflict list แทน `continue` · `:3172` โยนต่อ · `whtGapHint` ให้ `WhtPnd1` ผ่าน `WhtCertFilingScope.Filed` · รายปีใช้ `TaxableGross` · D6-5 server preview WHT จาก `ThaiPitCalculator` · D6-6 ยุบลดหย่อน 3 สำเนา · กท.20ก AwayFromZero + prorate · `annualSso` = YTD จริง + ฐาน×งวดที่เหลือ · D6-10 ต่อสาย `PayrollRunFilingScope.CanRemit/CountsTowardFiling` (14 จุด) · ลบ `RangesOverlap`/`GetMaxContribution` (baseline ลด 3 แถว)
- **AI/เรียนรู้**: D7-6 ปิดลูป 7 จุด (Anomaly `MarkFalsePositive`→`RecordUserChoiceAsync` · Stock/Aging/Import เก็บ FeedbackId · Payroll `RecordCallAsync` · `BankMatchDistillationModel` เรียนจาก feedback ไม่ใช่ `TimesConfirmed`) · D7-7 ส่ง Coverage/Explicit ออก endpoint · child rows ใช้ provider จริง · แก้ enum doc `DocumentMemo` · D7-E **governor ใน job**: Healthy+coverage≥0.8+agreement≥0.85 ติดต่อ 3 รอบ → ลด sampling ถึง floor เอง · shadow win-rate ตก → ดัน sampling/AlwaysTeach เอง เขียน `AiFeatureRoutingConfig` `LastModifiedBy="auto"` + AuditLog · **replay harness กลางคืน**: แถว 30 วันที่มี label ผ่าน `PredictAsync` รุ่นใหม่ vs เดิม → ไม่ promote ถ้าแย่ลง · เทสต์ orchestrator/Generic/job/resolver ชุดแรก
- **โมดูลรอง**: D8-5 CMS `StockDeducted` → enum 3 ค่า · `RestoreStock` คืนเฉพาะที่ตัดจริง · ฐาน VAT ค่าส่ง/ส่วนลดตาม §79 · booking อ่านนโยบายหรือลบคอลัมน์ · D8-6 Night audit → `CancelCoreAsync(noShow:true)` · VAT ที่พัก → `OutputVatRate` · เลิก `"00000"` 3 จุด · เช็คเอาต์ไม่ ack §86/4 อัตโนมัติ (ให้เป็นใบเสร็จ/อย่างย่อตามด่านเดิม) · D8-7 `[RequirePermission]` ให้ Pos/Cms*/Integration/Mobile · V1 อนุมัติผ่าน `CanApproveAsync` · D8-8 ทิป fallback → throw/ปักหมุด

### §6.3 P2 — โครงสร้าง (ทำเมื่อ P0/P1 ปิด)

- R3 checker `legal_table_copy_check` (CIT/PIT bracket · mod-11 · `+ 543` นอก `ThaiDate`) ratchet · ยุบ mod-11 3 จุด → `ThaiTaxId` · `+543` 44 จุด → `ThaiDate` ทีละไฟล์
- D2-B2b ปฏิทินยื่นรายปี → `TaxFilingDeadline` · Col11 → `WithholdingTaxCert.Condition` ตัวเดียว · JE 21913 สถานะชุดเดียว
- D4-8 Bulk `DeduplicateMatches` เรียง (Server > AI) ก่อน conf · `ReconciledBy` บนจอ · 1:1/Batch เรียน + unmatch = negative · ต่อสายหรือลบ prepass stub · FX ใน AutoMatch/MatchCandidates · `CashForecastService` ใช้ประวัติจ่ายจริง
- D5-C ต้นทุนแปลงสภาพ (labor/OH) + JE ผลิต · POC JE ตาม % · LCNRV · ฝากขาย GL · ขาเข้าผ่าน `Conv()`
- D8-8 แพลตฟอร์ม fallback renderer/เลขชุด → ใช้ตัวกลาง · POS VAT ต่อบรรทัดจาก `Product.VatRate`

### §6.4 ต้องให้เจ้าของตัดสินก่อน (สองทางให้ผลคนละเรื่อง — ห้ามเดาแทน)

| # | เรื่อง | ทางเลือก | ผล |
| --- | --- | --- | --- |
| Q1 | **D6-1 ฐาน ปกส. รวมเบี้ยเลี้ยงประจำ** (ม.5) | (ก) เพิ่ม `CountsForSsoBase` บน `PayrollItem` ฐาน = prorated + Σ ที่ติ๊ก · migration ต้องนักบัญชียืนยัน (ข) คงเดิม + เตือนบนหน้า item | (ก) ค่าจ้างที่ประกาศไปแล้วเปลี่ยน — ยื่นเพิ่มเติมย้อนหลัง? |
| Q2 | **D1-B4 `BuyerDeclinedTaxInvoice` ประทับเอง** + หัวอย่างย่อไม่ตรวจ ภ.พ.06 | (ก) เลิกประทับ → เป็นคำเตือน + `PdfGenerationService` ตรวจ ภ.พ.06 (ไม่มี = พิมพ์ "ใบเสร็จรับเงิน") (ข) คงไว้จนกว่า 180-A (ContactType `Unknown`) เสร็จ | (ก) ลูกค้าที่ยังไม่ได้ ภ.พ.06 จะพิมพ์ใบย่อไม่ได้ทันที — ถูกกฎหมาย แต่กระทบผู้ใช้จริง |
| Q3 | **D1-B1 ผลข้างเคียงหลังถอดด่านเก่า**: ใบซื้อ "บรรทัดอิสระ + ไม่มีคำบ่งชี้บริการ + ผู้ขายนิติบุคคล" จะ**เงียบสนิท** | ยืนยันว่ายอมรับตามคำตัดสินรอบ 179 หรือต้องการ "เตือนเบา" (info ไม่บล็อก) สำหรับ ≥ X บาท | — |
| Q4 | **D3-2 หลังตัด VendorIntel auto-fill WHT**: ผู้ขายบริการประจำที่กระดาษไม่พิมพ์หัก → เหลือ warn (ไม่เติม 3% อัตโนมัติ) | (ก) ยอมรับ (ข) ให้ VendorIntel เติมได้เฉพาะเมื่อประวัติมาจาก Explicit ≥ N ใบ **และ** ป้ายบอกว่า "จากประวัติ" ไม่ใช่ "จากกระดาษ" | (ข) ต้อง FieldConfidence + ป้ายซื่อสัตย์ |
| Q5 | **D2-B1 ประกาศว่ายื่น** — บังคับ `FilingNumber` (เลขรับ RD) ก่อน Filed หรือยอมให้ "ยื่นด้วยมือ ไม่มีเลข" เป็นสถานะแยก (`DeclaredFiled`) | (ก) บังคับเลข (ข) สถานะแยก + ป้าย + ไม่ล็อกงวด | (ก) ผู้ใช้ที่ยื่นกระดาษต้องพิมพ์เลขรับ |
| Q6 | **D2-C1 ตาราง DTA** — แหล่งข้อมูลอัตราสนธิสัญญา (RD publish) ใช้ชุดไหน · อัปเดตยังไง | — | กระทบ ภ.ง.ด.54 ทุกใบ |
| Q7 | **D5-C ฝากขาย / POC / LCNRV / ต้นทุนแปลงสภาพ** — โมดูลไหนอยู่ในขอบเขตผลิตภัณฑ์จริง | ต่อสาย หรือ ปิดโมดูล (ซ่อนเมนู) | ต่างกันคนละงาน |
| Q8 | **D5-5 วันเริ่มคิดค่าเสื่อม** = `DocumentDate` หรือเพิ่ม `ReadyForUseDate` (ถูกตาม TFRS บทที่ 10) | — | migration สินทรัพย์เดิม |
| Q9 | ค้างจากรอบ 180 §4.1d: ContactType `Unknown` default + 12 `new Contact` sites · GovernmentAgency · JE/report backfill · GL 6 ขั้น (§4.1e) — **D3-1 ทำให้ขั้น 4.1e ต้องเริ่มที่ serialize ก่อน** | — | — |

---

## §7 ตรวจแล้ว **ไม่ใช่ปัญหา** — ห้ามแก้ / ห้ามรายงานซ้ำ (รวมจาก F ทุกทีม)

- **เอกสาร**: `AdviseWhtApplicabilityAsync` ปฏิเสธ Skip · 0.70 เฉพาะทิศเงียบ · ตอบนอกชุดยังเก็บ FeedbackId · `RecordWhtSilence` 2 ร่องรอย + idempotent · Invoice fail-closed บทบาททางกฎหมาย · 11640 เมื่อ §86/4 ไม่ครบ · `InputVatClaimPeriodRules` ตรวจ Filed ก่อน §82/3 · `RecordLineAccountFeedbackAsync` Implicit ตั้งใจ · `TryReclassifyUndueOutputVatAsync` ที่ใบเสร็จ · retention นับจากสิ้นรอบ · 50 ทวิ auto-issue ล้ม LogWarning ยอมรับได้เพราะยอดนำส่งอ่านจาก certs
- **ภาษี**: `EtaxInvoiceService.SubmitToRevenueAsync` ไม่ประทับเมื่อไม่มี API key (ต้นแบบที่ถูกของ R1) · `EtaxDocumentTypeMap._ => "388"` unreachable · `TaxFilingDeadline` ตารางเดียวจริง · `GetWhtRate` ขั้น 3 คืน 0 = ไม่รู้ · `WhtRemitScope.SettledSourceIds` ตรงกับ `GenerateWhtReport` · `RemitAsync` 3 ด่านล้มดังพร้อมทางไปต่อ · `ThaiVatTypeRule` + memory-first
- **OCR**: แถวสแกน `Remove` จริง · `SmartFieldExtractor.Enrich` รันทุก tier · เติมเลขผู้ซื้อมีด่านเชิงบวก 3 ชั้น · AI บทบาท `contradictsIdentity` + `ShouldAskAi` 3 เคส · `OcrPostingReadiness` เรียกครบเว็บ+LINE · PaperWht เข้า Gateway เป็นค่ากระดาษ (T2-02 ปิดจริง)
- **ธนาคาร**: `PaymentIntentService` Expired→Succeeded ตาม policy · `GatewaySettlementService` บล็อกยอดไม่ตรง · ค่าธรรมเนียมหาผังไม่ได้ → throw · AwayFromZero ครบ Loan/Payment · Bulk: ทับยอดจริง + prune + Verification cap + กรองสกุล + ไม่เสนอคู่ที่ปฏิเสธ (ต้นแบบดี) · `ChequeService.cs:75` catch อยู่บน webhook · `ValidateMatchAmountAsync` เรียกทั้ง 1:1 และ Batch · overpay guard เว็บครอบ fee+WHT
- **สต็อก**: ล็อกต่อ (คลัง,สินค้า) · `WeightedAverageCost.Next` คิดก่อนบวก · `AssetRegistrationPlanner` Σ ตรง · `DepreciationSchedule` ถูก · `AdjustUsefulLifeAsync` prospective (C-06 ส่วนนี้แก้แล้วยังไม่ติ๊ก) · void กลับตาม movement idempotent · GRN→PI ไม่เข้าสองรอบ · deposit ไม่ตัดสต็อก · `AdjustStockAsync` ต่อคลัง · POS BOM `Handled` · `InventoryIndustry.IsUnknown` · `DisposeAsync` คิดค่าเสื่อมค้างก่อน · Revalue งวดปิด throw
- **เงินเดือน**: อัตรา ปกส. จาก `run.Month` ถูกตามรูปประกาศ · ช่วงแคบชนะ + ปฏิเสธกว้างเท่ากัน · ลดหย่อน 60,000 = สิทธิ์ทุกคน §47(1)(ก) · `SsoWageBase.Normalize` คืน `Conflict` · Void/Reopen บล็อกหลังนำส่ง + row lock · สปส.1-03 นับวันปฏิทิน · เพดาน 17,500 ปี 2026 อยู่ SYSTEM_REVIEW §S4 แล้ว
- **โมดูลรอง**: `ConfirmPaymentAsync` สะสมล้มเหลวลง InternalNotes · เลขอย่างย่อ POS advisory lock + D5 · `PosSlipHeader.BranchLabel` ปฏิเสธเดา HQ · Integration `ResolveContactType` DBD + `IsJuristic` · ที่พัก Confirm ตรวจห้องว่างซ้ำ + cap คงค้าง + snapshot นโยบาย · แพลตฟอร์ม Create+Approve + WHT ย้อนอัตรา · CMS booking VAT อ่าน `IsVatRegistered` · LINE/Mobile เอกสารผ่าน `CanApproveAsync` · `UsageMeteringService` idempotent

---

## §8 รอบ 182 — ลงมือแล้ว (ทีมลงมือ 4 ชุด + main agent · main agent เปิดไฟล์ตรวจงานทุกทีมก่อนรวม)

> โจทย์เจ้าของ 2026-09-19: (ก) เรื่องใบกำกับอย่างย่อ — ให้มีที่ตั้งค่า ภ.พ.06 ต่อบริษัท และ**สวิตช์ฝั่งแอดมิน**
> ว่าจะบังคับ ภ.พ.06 ไหม เผื่อกฎหมายเปลี่ยน (ข) เรื่องอื่น "ตั้งทีมวิเคราะห์ ทำอันที่ดีที่สุด"

### 8.1 ใบกำกับภาษีอย่างย่อ §86/6 — สวิตช์ระดับแพลตฟอร์ม (คำตอบข้อ ก)

**สิ่งที่พบตอนเปิดโค้ด**: ฟิลด์ `Company.IsRetailApproved` + `PhoR06ApprovedDate` มีอยู่แล้ว **และมีช่องกรอก
ในหน้าตั้งค่าบริษัทแล้ว** (`settings.html:181-194`) · แต่ **(1)** มีผู้อ่านแค่ `PosService` เท่านั้น — เส้น
เอกสาร/PDF พิมพ์ "ใบกำกับภาษีอย่างย่อ" โดย**ไม่เคยตรวจ ภ.พ.06 เลย** (D1-B4) **(2)** ไม่มีสวิตช์ฝั่งแอดมิน

| ทำอะไร | ที่ |
| --- | --- |
| ตัวตัดสิน**ตัวเดียว**ของ "บริษัทนี้ออกอย่างย่อได้ไหม" (ย้าย enum เหตุผลมาด้วย) | `Helpers/AbbreviatedTaxInvoiceRule.cs` (ใหม่) |
| สลิป POS เลิกตัดสินเอง → เรียกตัวตัดสินกลาง (เหลือเฉพาะกติกาของสลิป "ไม่มี VAT ก็ไม่ใช่ใบกำกับ") | `Helpers/PosSlipHeader.Resolve(..., requirePhoR06)` |
| **หัวเอกสาร/PDF ผ่านด่าน §86/6 เป็นครั้งแรก** — ทั้ง HTML renderer และ QuestPDF ใช้ predicate ตัวเดียวกัน (ข้อความ §86/6(6) หายพร้อมกัน ไม่ drift) | `ComputeDocumentTitle(..., companyMayIssueAbbreviated)` · `IsAbbreviatedTaxInvoiceDoc(doc, companyMayIssueAbbreviated)` — **ไม่มีค่าตั้งต้น** ผู้เรียกต้องตอบเสมอ |
| สวิตช์แอดมิน "บังคับ ภ.พ.06" (ค่าตั้งต้น = บังคับ ตามกฎหมายวันนี้) | `SiteSettings.RequirePhoR06ForAbbreviatedTaxInvoice` + migration + `GET/PUT admin/site-settings` + การ์ด "นโยบายตามกฎหมาย" ใน `admin/site-settings.html` |

**กติกาที่ล็อกไว้**: สวิตช์ปิดได้เฉพาะด่าน **ภ.พ.06** — ด่าน "ยังไม่จด VAT" (§77/1) ปิดไม่ได้ ·
ใบกำกับ**เต็มรูป §86/4 ไม่เกี่ยวกับสวิตช์นี้** จด VAT แล้วออกได้เสมอ · ไม่มีสิทธิ์ → หัวตกเป็น
"ใบเสร็จรับเงิน" (VAT ขายยังลง ภ.พ.30 ครบ) · ผู้เรียกที่ลืมส่งนโยบายได้ทิศ**เข้มกว่า**
เทสต์: `AbbreviatedTaxInvoiceRuleTests` (10 เคส สองทิศ) + `AbbreviatedTaxInvoiceTitleTests` (+3 เคส รวม
"ใบเต็มรูปไม่ถูกแตะ")

### 8.2 ของที่เจอเพิ่มระหว่างทำ — `PaidUpCapital` ถูกอ่านแต่ไม่มีใครเขียน

`Company.PaidUpCapital` ถูกใช้โดยสูตรภาษี **4 จุด** (อัตรา CIT ของ SME ทั้ง ภ.ง.ด.50/51 · เพดานค่ารับรอง
§65 ตรี(4) · context ของ AI) แต่ **ไม่มีทั้งช่องกรอก ช่องรับใน DTO และช่อง echo กลับ** ⇒ เป็น `0` ทุกบริษัท
(defect class "หน้าเว็บอ่านฟิลด์ที่ไม่มีใครเขียน" ในทิศกลับ) · แก้: `UpdateCompanyRequest.PaidUpCapital` +
`CompanyResponse.PaidUpCapital` + ด่านค่าติดลบใน `CompanyService` + ช่องกรอกใน `settings.html` พร้อมคำอธิบาย
ว่ามีผลกับอัตราภาษีอย่างไร

⚠️ **ตามมาเป็นคำถามให้เจ้าของ (Q10)**: วันนี้ `PaidUpCapital = 0` (ยังไม่กรอก) + รายได้ ≤ 30 ล.
⇒ ระบบนับเป็น **SME** = "ไม่รู้" ตกเป็น "ผ่าน" ซึ่งขัด G3 และเป็นทิศที่เสียภาษี**ต่ำกว่า**กฎหมาย
(ความเสียหายมองไม่เห็นจนกว่าสรรพากรจะประเมิน) · **ยังไม่เปลี่ยนในรอบนี้** เพราะก่อนหน้านี้ไม่มีช่องให้กรอก —
การพลิกทันทีจะทำให้ทุกบริษัทเสีย 20% โดยไม่มีทางแก้ · ตอนนี้ช่องกรอกมีแล้ว เจ้าของตัดสินได้ว่าจะพลิกเมื่อไร

### 8.3 P0 อื่นที่ปิดในรอบนี้

| ID | ทำอะไร | ผลต่อผู้ใช้จริง |
| --- | --- | --- |
| D2-B2a | `Helpers/CitRateTable` เป็น OWNER ตัวเดียวของอัตรา CIT + เกณฑ์ SME · `CalculateThaiCit` รับ `isSme` จริง · `ComputeCit` เหลือ wrapper | **บริษัทที่ทุน > 5 ล. หรือรายได้ > 30 ล. เสียภาษีเพิ่มบน ภ.ง.ด.50** (เดิมต่ำกว่ากฎหมาย) · กำไร 1 ล. → 200,000 แทน 105,000 · ส่วนต่างสูงสุด +195,000 คงที่เมื่อกำไร > 3 ล. · **SME ไม่เปลี่ยนแม้แต่สตางค์เดียว** (ล็อกด้วยเทสต์ทิศตรงข้าม) · ⚠️ รายงาน ภ.ง.ด.50 ที่ generate ไว้**ก่อน**แก้ ยังถือยอดเก่าในฐาน — ต้อง regenerate |
| D2-B3b | ลบขั้น fallback "ใช้อัตราของชนิดผู้รับ**ตรงข้าม**" ใน `GetWhtRate` | วันนี้ไม่มีผล (ทุกรหัสมีอัตราครบสองข้างหรือว่างทั้งคู่) — เป็นการถอดกับดักก่อนเพิ่มรหัสใหม่ · เทสต์เดินทุกแถวของ `ThaiWhtRateTable` เป็นด่านเชิงรุก |
| D5-1 | FIFO `Math.Max(taken, 1m)` → หารด้วยจำนวนจริง + `AwayFromZero` (ซ่อม `Math.Round` ไม่ระบุ MidpointRounding 5 จุด) | **ต้นทุนขายของที่ขายเป็นเศษส่วนถูกต้อง** — ขาย 0.5 กก. จาก layer 100/กก. ได้ 100 (เดิม 50) · วัตถุดิบที่นับเป็น กก./ลิตร/ชม. โดนทุกบิล |
| D5-6 | ย้ายด่านสต็อกติดลบจาก `InventoryCostingService` (ซึ่งถูกข้ามทุกครั้งที่ผู้เรียกส่ง `UnitCostOverride` = เส้นเอกสาร**เสมอ**) ไปที่ `StockLedger.MoveAsync` + `Helpers/NegativeStockGuard` | **ปิดราก SYSTEM_REVIEW E-03** · `AllowNegativeStock=false` มีผลจริงครั้งแรก · ดู §8.4 |
| D5-2 | `Helpers/InventoryControlAccount` — ฝั่งซื้อ (`DocumentService`) กับฝั่งเบิกใช้ (`ProductService`) ถามผังจากตัวตัดสินเดียวกัน + กรอง `Level >= 4` (บัญชี postable) ทั้งสองฝั่ง | ซื้อวัสดุสิ้นเปลืองเข้า 118xx เหมือนตอนเบิกใช้ — เดิม Dr 11500 / Cr 118xx ⇒ **11500 บวมถาวร** · ⚠️ ยอดเก่าที่ลง 11500 ไปแล้วยังอยู่ ต้องมี migration/ปรับปรุงรายการ (ยังไม่ทำ) |

### 8.4 พฤติกรรมที่เปลี่ยน — ผู้ใช้จะเห็นทันที (ต้องบอกเจ้าของ)

1. **ขายของที่คลังนั้นไม่มี จะถูกบล็อก** (เดิมผ่านเงียบแล้วสต็อกติดลบ) — ข้อความบอกทางไปต่อ 3 ทาง
   (รับเข้าคลังนี้ · โอนจากคลังอื่น · เปิด "อนุญาตสต๊อกติดลบ" ในตั้งค่าบริษัท ซึ่ง**มีสวิตช์จริงอยู่แล้ว**
   ที่ `settings.html:874` — ยืนยันแล้ว ไม่ใช่ข้อความที่ชี้ไปที่ว่าง)
2. **ฐานที่ใช้ตัดสินเปลี่ยนจาก "ยอดรวมบริษัท" → "ยอดของคลังนั้น"** — กระทบเฉพาะบริษัทหลายคลัง
   (POS · ร้านค้าออนไลน์ · เบิกวัตถุดิบเข้าผลิต · ฝากขาย) · บริษัทคลังเดียวได้ผลเท่าเดิม
3. **การกลับรายการ (void/ยกเลิกเอกสาร) ได้รับยกเว้นด่าน** — `StockMoveRequest.AllowNegativeOverride`
   (ค่าตั้งต้น `false` · มีเทสต์ล็อก) ใช้เฉพาะเส้น void: การกลับรายการไม่ใช่การตัดสินใจใหม่ แต่คือการลบสิ่งที่
   เคยลงไว้ — ถ้าบล็อก ใบซื้อที่ของถูกขายออกไปแล้วจะ**ยกเลิกไม่ได้ตลอดกาล** = ด่านที่ไม่มีทางไปต่อ
4. **ภ.ง.ด.50 ของบริษัทที่ไม่ใช่ SME แสดงยอดภาษีสูงขึ้น** (ยอดที่ถูกตามกฎหมาย)
5. **บริษัทที่ยังไม่ได้อนุมัติ ภ.พ.06 จะเห็นหัวเอกสารเป็น "ใบเสร็จรับเงิน"** แทน "ใบกำกับภาษีอย่างย่อ"
   ทั้งบนจอและบนกระดาษ (ถ้าไม่ต้องการ ให้กรอกวันที่อนุมัติ ภ.พ.06 หรือปิดสวิตช์ฝั่งแอดมิน)

### 8.5 ทีม 3 (สายป้าย/คลังเรียนรู้ AI) · ทีม 4 (ทางเข้าภายนอก)

| ID | ทำอะไร | ผลต่อผู้ใช้จริง |
| --- | --- | --- |
| D7-1 | `Helpers/AiSentinelAnswers` — sentinel `__USER_KEPT_EXISTING__` (ธง "ผู้ใช้ไม่รับคำแนะนำ") ถูกกรอง **3 ชั้น** (recorder · `LearnInlineAsync` · trainer) + migration ล้างแถวที่ปนเปื้อน 3 ตาราง | **รหัสผังบัญชีปลอมหยุดไหลเข้าตัวแนะนำ GL** · แถวที่ค้างอยู่ถูกล้าง โดยยัง**คงการปฏิเสธ**ไว้ (ไม่แตะ `UserChosenAt`) ⇒ สถิติไม่เพี้ยน |
| D7-1b | ตามมาจาก D7-1: แถว "ตรวจแล้วแต่ไม่บอกคำตอบ" เคยตกไปสาขา "ครูตอบแล้วไม่มีใครแตะ" ⇒ ได้คะแนน**บวก**ให้คำตอบที่มนุษย์เพิ่งปฏิเสธ = สอนกลับทาง · เพิ่มสาขาที่สาม ให้คะแนน**ลบ** ไม่นับเข้าถังรวมบริษัท | นักเรียนเลิกเรียนผิดทาง (main agent แก้เอง — ทีมชี้รูที่งานตัวเองเปิด) |
| D7-2 | `AiMemoryKey` ตัดบล็อก `local_model` ออกจากกุญแจ (ลิสต์ปิด ไม่ใช้แพตเทิร์น) | **นักเรียน generic กลับมาโตได้** — เดิมกุญแจฝั่งเขียน≠ฝั่งอ่าน**ทันทีที่นักเรียนเริ่มตอบได้** ⇒ หยุดโตตั้งแต่ใบที่สองของทุก feature ที่มีบล็อกนี้ (11 จุด 5 prompt builder) · tier-0 ของ 11 จุดนั้นเริ่มใหม่ · tier-1/2 ฟื้นเองใน 1 รอบงานกลางคืน (~6 ชม.) ไม่ต้อง migration |
| D7-3 | เติม `source` 6 หน้าเว็บ + ป็อปอัพ · **checker ใหม่** `tools/ai_feedback_source_check.py` (+ `--self-test`) · backfill เฉพาะ 3 feature ที่พิสูจน์ได้ | ด่าน "Explicit ≥ 1" ของรอบ 178 เลิกปิดคลังทันทีของ ManualJeAccount / ProductCategory / GlAccountSlot เงียบ ๆ · **ป้ายตรงความจริง**: `document-scan` ยิงตอน "บันทึก" จึงเป็น `Implicit` เว้นแต่ผู้ใช้แตะช่องจริง (เพิ่ม `userTouched`) |
| D7-4 | `ClampSamplingRate` ใช้ทั้งฝั่งเขียนและฝั่งอ่าน + migration ยกแถวที่ค้าง 0 | ครูที่ถูกปิดถาวรโดยไม่มีใครรู้กลับมาถูกสุ่มถาม (นักเรียนได้ตัวอย่างใหม่) · **LocalOnly ยังเป็น 0** (kill-switch ที่ประกาศชัดต้องใช้ได้จริง) |
| D7-5 | orphan detector ยกเว้น feature ที่มี `ILocalDistillationModel` จริง (อ่านจาก DI ไม่ hardcode) + กรอง `CompanyId == null` | เลิกประทับ `NeedsRedesign` ให้ 6 feature ที่มีนักเรียนอยู่แล้วทุกรอบ (ตัวเตือนที่ฟ้องผิด = ตัวเตือนที่พัง) และเลิกเสี่ยงคว้าแถวรายบริษัทมาประทับสถานะแพลตฟอร์ม |
| D8-3 | `Helpers/ContactTypeFromTaxId` ตัวเดียว — V1 ทั้ง 2 จุด + `IntegrationService` เรียกตัวเดียวกัน · ตัดสินไม่ได้ = **คงค่าเดิม ไม่เดา** | คู่ค้า**บุคคลธรรมดา**ที่ sync ผ่าน API เลิกกลายเป็นนิติบุคคล ⇒ เลิกยื่น **ภ.ง.ด.53 แทน ภ.ง.ด.3** และเลิกใช้อัตราดอกเบี้ย 1% แทน 15% · ⚠️ แถวที่ประทับผิดไปแล้วยังอยู่ — ดู Q11 |
| D8-4 | `Helpers/PartnerVatRate` — `?? 7m` เหลือ **0 จุด** (พบเพิ่มเป็น **8 จุด** ไม่ใช่ 6: `BuildDocumentLinesAsync(defaultVatRate = 7)` ที่ใบเพิ่ม/ลดหนี้ใช้อยู่โดยไม่มีใครเห็น) | tenant ที่ไม่จด VAT เลิกออกเอกสารพร้อม VAT 7% · บริษัทที่ตั้งอัตราเอง ≠ 7 ได้อัตราของตัวเอง · **พบบั๊กเพิ่ม**: `BuildDocumentLinesAsync` ไม่เคยตั้ง `IsVatClaimable` ⇒ default `true` ⇒ ภาษีซื้อของบริษัทที่ไม่จด VAT ถูกนับเป็นเคลมได้ทุกใบที่มาทาง integration — ปิดแล้ว |
| D8-3b | `POST /api/v1/documents/{id}/approve` ผ่าน `CanApproveAsync` (ผู้ใช้ = เจ้าของ API key) + 404 เมื่อเอกสารไม่ใช่ของบริษัทผู้เรียก | คีย์ที่ออกโดย Owner/Accountant/SystemAdmin ไม่กระทบ · คีย์ของ Staff ที่ไม่มีสิทธิ์อนุมัติจะได้ 403 พร้อมทางไปต่อ (เอกสารยังค้าง Draft ไม่หาย) |

**brief ของ main agent ผิด 1 ข้อ — จดไว้ตามกติกา**: ผมสั่งทีม 4 ให้เปลี่ยน `?? 7m` ทั้ง 6 จุดเป็น
`OutputVatRate.ForCompany` ทีมทักกลับว่า **6 จุดไม่ใช่กติกาเดียวกัน** — 2 จุดเป็นภาษี**ขาย** (เราออกใบ)
อีก 4 จุดเป็นภาษี**ซื้อ** (ผู้ขายออกให้เรา) · ถ้าบังคับฝั่งซื้อเป็น 0 เมื่อเราไม่จด VAT **ยอดที่ต้องจ่าย
ผู้ขายจะหายไป 7% เงียบ ๆ** (VAT บนใบเป็นของผู้ขาย ไม่ใช่ของเรา) = ทิศที่ความเสียหายมองไม่เห็น ซึ่งขัด
DOCTRINE §1 G5 · ทีมทำตามเส้นที่ระบบมีอยู่แล้ว (`DocumentService.CreateDocumentAsync`): **คงอัตราไว้ แต่
บังคับ `IsVatClaimable=false` + เหตุผล** — ถูกกว่าที่ผมสั่ง

### 8.6 คำถามใหม่ที่ต้องให้เจ้าของตัดสิน (เพิ่มจาก §6.4)

| # | เรื่อง | ทางเลือก / สิ่งที่ต้องรู้ |
| --- | --- | --- |
| Q10 | **`PaidUpCapital = 0` (ยังไม่กรอก) ควรนับเป็น SME ไหม** | วันนี้ = นับเป็น SME (เสียภาษีต่ำกว่ากฎหมาย · ความเสียหายมองไม่เห็น) · ทางที่ตรง DOCTRINE G3/G5 คือ "ไม่รู้ทุน = ไม่ใช่ SME" แต่กระทบ ภ.ง.ด.51 ด้วย และบริษัทที่ยังไม่กรอกจะเสีย 20% ทันที · **ตอนนี้มีช่องให้กรอกแล้ว** (§8.2) เจ้าของเลือกได้ว่าจะพลิกเมื่อไร และจะแจ้งลูกค้าให้กรอกก่อนไหม |
| Q11 | **รัน migration แก้ ContactType ที่ประทับผิดไหม** (D8-3) | คิวรีนับก่อน: `SELECT "CompanyId", COUNT(*) FROM "Contacts" WHERE "ContactType"=2 AND "UpdatedBy"='api:v1:contact-sync' AND "TaxId" ~ '^[1-8][0-9]{12}$' GROUP BY 1;` · ขอบเขตแคบพิสูจน์ได้ (สตริง `UpdatedBy` มีจุดเขียนจุดเดียวทั้งเรพ) · **false positive**: คู่ค้าที่ส่ง `contactType: "JuristicPerson"` มาเองพร้อมเลขบัตรประชาชนก็ตกในชุดนี้ (ฐานไม่บันทึกว่ามาจาก branch ไหน) — ความเห็นทีม: แถวแบบนั้นผิดอยู่แล้ว แต่ไม่เดาแทน |
| Q12 | **regenerate รายงาน ภ.ง.ด.50 ของบริษัทที่ไม่ใช่ SME ไหม** (D2-B2a) | รายงานที่ generate ไว้ก่อนแก้ยังถือยอดเก่าในฐาน (`TaxReport.CitAmount`) · ทางเลือก: regenerate อัตโนมัติ · เตือนบนหน้าจอให้กดสร้างใหม่ · ปล่อยไว้ |
| Q13 | **ยอด 11500 ที่บวมจากวัสดุสิ้นเปลืองย้อนหลัง** (D5-2) | โค้ดถูกแล้วตั้งแต่นี้ไป แต่ยอดเก่าที่ลง 11500 ไม่ได้ย้ายเอง · ต้องมีรายการปรับปรุง (JE) หรือ migration ย้ายยอด — ต้องให้นักบัญชีตัดสินว่าย้ายทั้งก้อนหรือปล่อยให้ล้างตามธรรมชาติ |
| Q14 | **สำเนากติกา ContactType ที่เหลืออีก 7 จุด** (นอกขอบเขตทีม 4) | `OcrService:2517` · `TaxInvoiceCompletenessChecker:83` · `LodgingService.Reservations:428` · `PlatformBillingDocumentIssuer:310` · `PayrollService:2753` · `CmsLeadService:248` · `DocumentService:15937` — ทั้งหมดเป็น `StartsWith("0")` ไม่ตรวจ checksum · เรียก `Helpers.ContactTypeFromTaxId` ได้เลย (งานรอบถัดไป) |

### 8.7 ยังไม่ได้คอมไพล์

env นี้ไม่มี .NET SDK — ตรวจด้วย Python checker 39 ตัว + `node --check` + brace balance + U+FFFD เท่านั้น
**CI บน `claude/**` คือ compiler ตัวแรก** · จุดที่เสี่ยง compile error มากสุด: ลายเซ็นที่เปลี่ยนแบบ
**ไม่มีค่าตั้งต้น** (`ComputeDocumentTitle` · `IsAbbreviatedTaxInvoiceDoc` · `PosSlipHeader.Resolve` ·
`CalculateThaiCit` · `BuildDocumentLinesAsync`) และ constructor ของ `DocumentsV1Controller` ที่ฉีด
`IPermissionService` เพิ่ม

---

_Last verified against codebase: 2026-09-19 — รอบ 182 (ลงมือ P0 ชุดแรก · §8)_
_commit: ddb023b_
