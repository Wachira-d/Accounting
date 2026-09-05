# ทีม T3 — สถาปัตยกรรมการเรียนรู้/AI (ML systems · LLM app · data-flywheel)

**สถานะ:** เสร็จ (15 findings + 5 design) — 2026-09-05 · HEAD e7e3e5c
**ขอบเขต:** loop การเรียนรู้ของไปป์ไลน์ OCR→เอกสาร ตั้งแต่ call site ของ AI · student model ·
seed · feedback capture · ปิด loop ตอนแก้ · kill-switch · คุณภาพสัญญาณ (final posted document)
· และแบบสถาปัตยกรรมเป้าหมาย "self-improving accountant"
**กติกา:** ทุกข้อเปิดไฟล์จริง (file:line) · ห้ามแก้โค้ด · ไม่รายงานซ้ำสิ่งที่ปิดแล้ว (OCR-BRIEF ข้อ 6)

## สรุป 5 บรรทัด
1. **student บนเส้น OCR ทั้ง 5 จุดถูก "อ่านแล้วทิ้ง"** — gate `if (result.UsedAi && …)` ที่ `OcrService.cs:871/1416/1893/6850` +
   `OcrMetadataProjectMatcher.cs:413` ทำให้คำตอบของ local model ที่ short-circuit (UsedAi=false) ไม่เคยเติมค่าให้ผู้ใช้ —
   ระบบ "จ่ายน้อยลง" แต่ไม่ "ฉลาดขึ้น" (T3-01, P0)
2. **เส้น 1-click approve ไม่ CAPTURE เลย** — bulk/การ์ด `create-document?approve=true` ไม่ผ่าน `SubmitCorrectionAsync`, และ
   `CreateDocumentFromScanAsync :4446–5276` ไม่มี recorder สักบรรทัด ⇒ student เรียนแต่เคสที่ผู้ใช้ต้องแก้ (selection bias) ยกเว้น GL
   ที่ปิดจากเอกสารได้ (T3-03, P0)
3. **student state อยู่ใน process + โหลดโดย job ที่ล็อกให้รัน 1 instance** ⇒ หลัง deploy 7 นาทีแรก และ instance ที่แพ้ล็อกตลอดไป
   ไม่มี student (T3-04) · `OcrLineItemSplit` ไม่มี student/trainer เลย ปิด AI = ไม่มีรายการสินค้า (T3-02)
4. **learning from noise 2 ชั้น**: Azure output ก่อนยืนยันถูกเขียนเป็น "known good" แล้วทับใบถัดไป (T3-06) · full-review student
   เรียนค่าบนสแกน ไม่ใช่เอกสารที่อนุมัติ (T3-11) · ปุ่ม AI ตรวจทานเติมยอดเงินลงฟอร์มโดยไม่มี guard (T3-07)
5. **แบบเป้าหมาย**: Decision Record ต่อช่อง + Arbiter (D1) · student ตอบได้จริง + lazy-load (D2) · ปิด loop ที่เอกสารอนุมัติ (D3) ·
   golden set/backtest/KPI `StudentServeRate` (D4) · LLM เป็น full-review 1 call ที่เสนอ candidate เข้า arbiter (D5) —
   เฟส 0 มี S-fix 7 ตัวทำได้ทันที

## §1 Matrix A — AI call ทุกจุดบนเส้น OCR (เปิดไฟล์จริงแล้ว)

เส้นทาง: `OcrService.ScanAsync` → `OcrAiAugmenter.*` → `AiOrchestrator.AskAsync`
(`Services/Ai/AiOrchestrator.cs:112` outer catch → `:126` AskInternalAsync → routing `:142` →
`TryPredictLocalAsync` `:57–70` → Hybrid short-circuit `ReturnLocalAsync` `:75` UsedAi=false →
provider ล้ม/ปิด/เกินงบ → `FallbackToLocal` `:557` UsedAi=false, PrimaryAnswer=`req.LocalPrimaryAnswer`)

| # | FeatureKey | call site (OcrService.cs) | FeedbackId บน entity | ปิด loop ตอนแก้ | student (Program.cs) | seed cold-start | student ถูก **ใช้** จริง? | UsedAi ใน DTO | kill-switch |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `DocumentTypeClassification` (#3) | `:832` `ClassifyDocumentTypeAsync` (เฉพาะ role conf<0.7 หรือไม่มี marker `:829`) | `TargetDocTypeAiFeedbackId` `:863` (เก็บเสมอ) | `CloseAiFeedbackLoop(TargetDocTypeAiFeedbackId…)` `:3765` ✅ | `GenericFeedbackDistillationModel` `Program.cs:566` | ❌ ไม่มี seed (เรียนจาก feedback เท่านั้น → `IsReady` false จนมีแถวแรก `GenericFeedbackDistillationModel.cs:44–46`) | **❌ อ่านแล้วทิ้ง** — apply gate `:871` `if (cls.UsedAi && …)` ⇒ คำตอบ student (UsedAi=false) ไม่เคยถูกใช้ | `TargetDocTypeUsedAi` `:876` ✅ | ✅ ผ่านเพราะ **rule** (`OcrDocumentRoleInferrer`) ตอบไว้ก่อน ไม่ใช่เพราะ student |
| 2 | `GlAccountSuggestion` (#2) | `:1314` `if (_aiAugmenter != null)` → `SuggestGlAccountAsync` `:1355` — **เรียกทุกใบ** ไม่มีเงื่อนไข "local ไม่มั่นใจ" ที่ call site | `GlAccountAiFeedbackId` `:1404` (เก็บเสมอ ✅) · ต่อบรรทัด `:5119–5161` | `SubmitCorrectionAsync` `:3721–3728` ✅ | `GlAccountDistillationModel` `Program.cs:534` | ✅ industry baseline (`RefreshIndustryBaselineAsync` `GlAccountDistillationModel.cs:251`) + `SystemOcrCategoryMapping` ผ่าน resolver | **❌ อ่านแล้วทิ้ง** — apply gate `:1416` `if (glResult.UsedAi && …)`; ป้าย `:1438` เขียนว่า "ใช้ผลของ local model แทน" แต่ local model ที่ว่าคือ heuristic ก่อนหน้า ไม่ใช่ student | `GlAccountUsedAi` `:1428` ✅ ("applied" semantics) | ✅ ผ่านเพราะ `_categoryLearner`/NB/rules ก่อนหน้า |
| 3 | `VendorCanonicalization` (#1) | `:1869` เฉพาะ `!MatchedContactId` → `CanonicaliseVendorAsync` `:1876` ส่ง `localBestContactId: null, localConfidence: 0m` | `AiSuggestionFeedbackId` **เก็บเฉพาะ** เมื่อ AI ถูก apply `:1906` หรือ `!UsedAi` `:1919` — **AI ตอบแต่ conf<0.70 / `__NEW__` / contact ไม่มีจริง ⇒ FeedbackId หาย** | `MatchContactAsync` `:5805–5811` ✅ (แต่ต้องมี FeedbackId) | `VendorCanonDistillationModel` `Program.cs:532` | ❌ (feedback-only) | **❌ อ่านแล้วทิ้ง** `:1893` `if (aiResult.UsedAi …)` | ❌ **ไม่มี** `VendorCanonUsedAi` ใน `ToDto` (`:6687–6700` มีแค่ GlAccount/LineSplit) | ✅ ผ่านเพราะ contact match by tax id/name ก่อนหน้า |
| 4 | `OcrLineItemSplit` (#56) | `:677` → `TrySplitLineItemsWithAiAsync` `:6830` เมื่อ `Items.Count==0` | `LineSplitAiFeedbackId` `:6878` **เฉพาะเมื่อ guard รับ** — guard ปฏิเสธ `:6853–6859` `return` โดยไม่เก็บ | `:5775–5781` (ครั้งเดียวต่อสแกน) ✅ แต่ต้องมี FeedbackId | **❌ ไม่มี student เลย** (`grep OcrLineItemSplit Program.cs` = 0) · ไม่อยู่ใน `AiFeedbackTrainingJob.KnownTrainerFeatures` (`:247–308` มีแต่ `LineItemStructuredParse` #6 ซึ่งไม่มีใครเรียก) | ❌ | — | `LineSplitUsedAi` `:6700` ✅ | **❌ ไม่ผ่าน** — `:6850` `if (!res.UsedAi …) return;` ⇒ ปิด provider = ไม่มีรายการสินค้าเลย (ขัดกฎเหล็ก #3 ข้อ 6) |
| 5 | `OcrProjectMatch` (#29) | `MatchLineProjectAsync` `OcrAiAugmenter.cs:569` (เรียกจาก project allocation `:690`) | `ProjectAiFeedbackId` ต่อบรรทัด `:7342` | `:5574–5580`, `:5631–5637` ✅ | Generic `Program.cs:572` | ❌ | ยังไม่ได้ตรวจ apply gate (ดู §ยังไม่ได้ตรวจ) | ❌ ไม่มี `ProjectUsedAi` ใน DTO | น่าจะผ่าน (allocation rule ก่อน) |
| 6 | `OcrFullReview` (#22) | **ไม่ได้อยู่ใน ScanAsync** — เรียกจากปุ่มใน UI ผ่าน `AiSuggestionController.cs:3034` → `AdvancedAiAugmenter.ReviewOcrAsync:85` | (ตรวจต่อ §1b) | (ตรวจต่อ) | `OcrFullReviewDistillationModel` `Program.cs:558` (bespoke) | ❌ | ตรวจต่อ | ตรวจต่อ | ตรวจต่อ |
| 7 | `StockMovementValidation` (#19) · `DocumentConversionSuggestion` (#23) | `AiSuggestionController.cs:3047/3070` (ปุ่ม UI, on-demand) | — | — | Generic `:569/:571` | ❌ | on-demand | — | — |

**ข้อสังเกตเชิงระบบจาก matrix (นำไป finding T3-01/02/03):**
- ทั้ง 4 จุดบนเส้นหลัก (#1–#4) ใช้ gate รูปเดียวกัน `if (result.UsedAi && …) apply` ⇒ student ถูก **อ่าน**
  (orchestrator `TryPredictLocalAsync :57`) และเมื่อมั่นใจ ≥0.85 orchestrator คืน `UsedAi=false` พร้อม
  `PrimaryAnswer = student` (`ReturnLocalAsync :75–104`) → `OcrAiAugmenter` map เป็น `Answer` ตรง ๆ
  (`OcrAiAugmenter.cs:468–476`, `:546–554`) → **OcrService ทิ้งทันทีเพราะ UsedAi=false**
- ผลคือ "Hybrid short-circuit" ประหยัด token ได้จริง แต่ **ไม่เคยเติมค่าให้ผู้ใช้** — kill-switch ที่ผ่าน
  วันนี้ผ่านเพราะ heuristic/rule ชั้นก่อนหน้า ไม่ใช่เพราะ student ⇒ student บนเส้น OCR เป็น
  "ตัวชี้วัด" (LocalModelAnswer ในแถว feedback) ไม่ใช่ "ผู้ตอบ" — ขัดกฎเหล็ก #1 ข้อ "local ต้องทดแทน AI ได้ 100%"

## §1b เส้นทาง retrain (nightly) — ข้อเท็จจริงที่เปิดไฟล์แล้ว

- `Services/Implementations/Jobs/AiFeedbackTrainingJob.cs:34–48` — หน่วง 7 นาทีหลัง boot แล้วรันทุก
  `Ai:FeedbackTraining:IntervalHours` (default **6 ชม.**) · `:57–60` ครอบทั้ง pass ด้วย
  `JobLock.RunExclusiveAsync` (`Helpers/JobLock.cs:59` = `pg_try_advisory_lock` → **try ไม่ใช่ wait**,
  instance ที่แพ้ล็อกข้ามรอบ `:66`)
- `:62–68` ลำดับ: `RefreshAllLocalModelHealthsAsync` → `TrainLocalModelsAsync` (เขียนตาราง
  `OcrCategoryMappings` ฯลฯ) → **`ReloadDistillationModelsAsync` `:504–528`** = โหลด state ของ
  student ทุกตัวเข้าหน่วยความจำ **ของ process ที่ชนะล็อกเท่านั้น** และเฉพาะบริษัทที่มีแถว
  `AiSuggestionFeedbacks.UserChosenAt != null` (`:511–513`)
- student ทุกตัวเป็น singleton in-memory (`Program.cs:527–531` คอมเมนต์ยืนยัน) · `IsReady` ขึ้นกับ dict
  ว่างไหม (`GenericFeedbackDistillationModel.cs:44–46`, `GlAccountDistillationModel.cs:29`) · ไม่มี
  lazy-load ต่อบริษัทใน `PredictAsync` (GL `:173`, Generic `:184`)

## §2 Matrix B — learner ที่ไม่ใช่ LLM บนเส้น OCR

| learner | writer (ใครสอน) | reader (ใครอ่าน) | เงื่อนไข reader ทำงาน | key/shape ตรงกัน? | เรียนจาก "ค่าที่ยืนยันแล้ว" หรือ "เสียง"? |
|---|---|---|---|---|---|
| `VendorIntelligenceService` (นิสัยผู้ขาย: ชนิดเอกสาร/WHT/เทอม) | `TryTrainAsync` จาก `DocumentService.cs:5306` (approve) · Signature/Integration/E-commerce · admin `OcrController.cs:1338` · `OcrService.cs:3680` (SubmitCorrection) | `OcrService.cs:1117 _vendorIntel.PredictAsync` | `vendorPred.HasHistory` `:1119` | ✅ key = tax id/ชื่อ (ไม่ได้ไล่ทุกช่อง) | ✅ เรียนจากเอกสารที่ **อนุมัติแล้ว** — ดีที่สุดในกลุ่ม |
| `ExpenseCategoryLearner` (`_categoryLearner`) + `TfIdfNaiveBayesClassifier` | `RecordAsync` `OcrService.cs:3706` (SubmitCorrection เมื่อมี DebitAccountCode) · `AiFeedbackTrainingJob.TrainGlAccount` → `OcrCategoryMappings` `:470–486` | NB `:1056` (อ่าน `OcrCategoryMappings` `TfIdfNaiveBayesClassifier.cs:58`) · learner `:1089`, ต่อบรรทัด `:1102` · federated `ExpenseCategoryLearner.cs:185` | NB: conf>0.7 **และ** > FieldConfidence เดิม `:1052–1054` · learner: conf≥0.55 **ไม่เทียบของเดิม** `:1091–1093` | ✅ ตารางเดียวกัน | ⚠️ เรียนจาก review-screen + document approve (ผ่าน TrainGlAccount) — ok |
| `GlobalDocWorkflowLearner` (`_docWorkflowLearner`) | `RecordConfirmAsync` `:3799` key `tax:{13 หลัก}` ไม่ครบ 13 → `name:` `:3783–3791` | `:1184 PredictAsync(fedVKey)` key `tax:{digits ทั้งหมด}` `:1177` **ไม่เช็ค 13 หลัก** | `ocrRoleConfidence < 0.9 || !ocrTargetFromPaper` `:1173` (แก้จาก write-only แล้ว ✅) | ⚠️ **key drift เมื่อเลขไม่ครบ 13 หลัก**: writer เขียน `name:`, reader ถาม `tax:12digits` → ไม่เจอ (เงียบ) | ✅ ยืนยันแล้ว |
| `OcrLearnedPatterns` (regex ต่อผู้ขาย) | `SubmitCorrection` `:3860–3894` + negative `:4047–4058` · **`AzureDiPatternLearner.LearnAsync` `OcrService.cs:369` ทุกสแกน Azure ก่อนผู้ใช้เห็น** | `ApplyLearnedPatternsAsync :6789` → `DocumentZoneAnalyzer.ApplyLearnedPatternsTo :6809` | มี pattern ของ vendor/ทั่วไป ≥1 · เรียง `TimesConfirmed` | (FieldName mapping ยังไม่ได้เทียบทีละชื่อ — ดู §ยังไม่ได้ตรวจ) | ❌ **ครึ่งหนึ่งเรียนจากเสียง** (Azure output ที่ยังไม่ยืนยัน) |
| `VendorKnownGoodValue` + `VendorKnownGoodCorrector` | `AzureDiPatternLearner.UpsertKnownGoodAsync` `:82–90` (SellerName/BuyerName/Address/Phone/Email…) **ไม่มี confidence gate** `:125–160`; `ConfirmedCount += 1` ทั้งที่ไม่มีใคร confirm `:141`; Source=`AzureDI` | `_knownGoodCorrector.ApplyAsync` `:429/:525` — ทับค่าที่ sim≥0.80 `VendorKnownGoodCorrector.cs:30,106` เรียง UserCorrection→sim→ConfirmedCount `:107–109` | ทุกสแกนที่มี VendorTaxId | ✅ ผ่าน `VendorKnownGoodFields` (per-document ถูกกันแล้ว ✅) | ❌ **"known good" = "first seen"** — ค่าอ่านผิดใบแรกกลายเป็น canonical แล้วไปทับค่าอ่านถูกใบถัดไป (sim≥0.80 & `Value != noisyValue`) |
| `GlobalVendorIntelLearner` (k-anonymity) | ผ่าน `VendorIntelligenceService.TrainFromDocumentAsync` | ใน `VendorIntelligenceService.PredictAsync` | — | (ปิดแล้วใน CLAUDE.md — ไม่ตรวจซ้ำ) | — |
| `ProductMatcher` | `RecordAliasAsync :7232` (LinkPurchaseOrder) | product xref `:1074` | — | ยังไม่ได้ตรวจ | — |
| `OcrFullReviewDistillationModel` (student ของ #22) | **ไม่ใช้ feedback row** — อ่าน `OcrScanResult` ที่ `CreatedDocumentId != null && ScanStatus=="Completed"` `OcrFullReviewDistillationModel.cs:85–95` เอา `ExtractedVendorName/TaxId/BranchCode/TargetDocumentType/ExpenseCategory` **ของสแกน** | orchestrator เมื่อ UI กด ai-review | `IsReady` หลัง reload · แต่ reload วนเฉพาะบริษัทที่มี **feedback row ปิดแล้ว** (`AiFeedbackTrainingJob.cs:511`) ⇒ บริษัทที่สแกน→สร้างเอกสารเยอะแต่ไม่เคยกด ai-review = **ไม่ถูกโหลดเลย** | ⚠️ | ⚠️ เรียน "ค่าบนสแกน ณ ตอนสร้าง" ไม่ใช่ "ค่าบนเอกสารที่อนุมัติ" — ถ้าผู้ใช้แก้ชื่อผู้ขาย/ชนิดใน documents.html ทีหลัง student เรียนค่าผิด |
| `GlAccountDistillationModel` | feedback rows GL+PV `:80–84` (UserChosenAt != null) + industry baseline `:251` | orchestrator | Hybrid ≥0.85 | ✅ | ✅ |
| `VendorCanonDistillationModel` | feedback rows | orchestrator | Hybrid | (ยังไม่ได้เปิด) | ✅ แต่ขาด row เมื่อ AI ไม่มั่นใจ (T3-05) |

## §3 Findings (เรียง P0→P3)

### T3-01 [🔴 P0][M] student บนเส้น OCR ทั้ง 4 จุด "ถูกอ่านแต่ไม่เคยถูกใช้" — Local-First Sovereignty เป็นศูนย์โดยโครงสร้าง
- ไฟล์: `Services/Implementations/OcrService.cs:871` (doc type), `:1416` (GL), `:1893` (vendor), `:6850` (line split) ·
  `Services/Ai/AiOrchestrator.cs:75–104` (`ReturnLocalAsync` คืน `UsedAi=false, PrimaryAnswer=student`) ·
  `Services/Ai/OcrAiAugmenter.cs:468–476, :546–554` (map `Answer: resp.PrimaryAnswer` ไม่ดู UsedAi)
- โค้ด: `if (cls.UsedAi && (cls.Confidence ?? 0m) >= 0.70m && …) { extractedData.TargetDocumentType = aiType…}` ·
  `if (glResult.UsedAi && !string.IsNullOrEmpty(glResult.Answer) && …)` · `if (aiResult.UsedAi && …)` ·
  `if (!res.UsedAi || string.IsNullOrWhiteSpace(res.Answer)) return;`
- ทำไมพัง: (1) student มั่นใจ ≥0.85 → orchestrator short-circuit คืน `UsedAi=false` พร้อมคำตอบ student →
  (2) augmenter ส่งต่อเป็น `Answer` → (3) call site ทุกจุดมี `UsedAi &&` เป็นด่านแรก ⇒ คำตอบ student ถูกทิ้ง
  ทุกครั้ง → (4) ค่าที่ผู้ใช้เห็นมาจาก heuristic ชั้นก่อนหน้าเสมอ ⇒ student ไม่มีวันเติมค่าที่ heuristic เติมไม่ได้
- ผลกระทบ: (ก) กฎเหล็ก #1 ข้อ 6 (`UsedAi` ลดลง = local โต) **อ่านไม่ได้** — UsedAi ลดลงจริงแต่คุณภาพคำตอบ
  ไม่เพิ่ม เพราะคำตอบ student ไม่ถูกใช้; (ข) ยิ่ง Hybrid short-circuit ทำงานดี ผู้ใช้ยิ่ง **เสียคำตอบของ AI
  ที่เคยได้** (AI ถูกข้าม + student ถูกทิ้ง = ได้ heuristic เปล่า ๆ) — ระบบ "เรียนแล้วโง่ลง";
  (ค) kill-switch วันนี้ผ่านเพราะ rule ไม่ใช่เพราะ student ⇒ ทุก feature ที่ rule ไม่ครอบ (line split) ตายเมื่อปิด AI
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" (ทรง 4: ฝั่งเขียนทำงาน ฝั่งอ่านไม่ถูกใช้) + "ด่านที่เขียนไว้ครึ่งเดียว"
- ทางแก้ (ข้อความ): เปลี่ยน contract ของ `OcrAiAugmentationResult` ให้มี `Source ∈ {Provider, Student, Heuristic}` +
  `Confidence`; call site ใช้ `Answer` เมื่อ `Source != Heuristic && Confidence ≥ threshold` และผ่าน anti-hallucination
  guard เดิม (enum/CoA/contact) — ป้าย UI: Provider→"🤖 AI", Student→"⚙️ ระบบเรียนรู้แล้ว (จาก N ครั้ง)";
  `<Feature>UsedAi` คงความหมาย "provider ถูกใช้" แต่เพิ่ม `<Feature>Source`
- ความมั่นใจ: **สูง** (อ่าน gate ทั้ง 4 จุด + orchestrator + augmenter mapping ครบ)
- ฝ่ายค้าน: "student ไม่ผ่าน guard เท่า AI — เชื่อไม่ได้" → ตอบ: guard เดิม (Enum/CoA/Contact exists) ใช้กับ
  student ได้เหมือนกัน และ student ที่ short-circuit ได้ต้องมั่นใจ ≥0.85 จาก feedback ที่ผู้ใช้ยืนยันเอง —
  น่าเชื่อกว่า AI ที่ตอบ 0.70 ซึ่งวันนี้รับอยู่แล้ว

### T3-02 [🔴 P1][S] `OcrLineItemSplit` ไม่มี student · ไม่อยู่ใน trainer · ปิด provider = ไม่มีรายการสินค้าเลย
- ไฟล์: `Program.cs:531–590` (ไม่มี OcrLineItemSplit) · `AiFeedbackTrainingJob.cs:247–308` (`KnownTrainerFeatures`
  มีแต่ `LineItemStructuredParse` #6 ซึ่ง `grep` ไม่พบ call site) · `OcrService.cs:6850`
- ทำไมพัง: engine ไม่คืนตาราง → `Items.Count==0` → เรียก AI → provider ปิด/เกินงบ → `!res.UsedAi` → `return`
  ⇒ เอกสารสร้างด้วยบรรทัดสรุปเดียว (ขัดกฎเหล็ก #3 ข้อ 6) · feedback row ของ feature นี้ถูก job ตีเป็น
  "unknown trainer" (warning) และไม่มี student รับ
- ผลกระทบ: kill-switch test ไม่ผ่านสำหรับใบที่ engine ไม่ให้ตาราง (Tesseract/PDF text layer เป็นเส้นหลักของ
  tenant ที่ไม่มี Azure) · ทุก token ที่จ่ายให้ feature นี้ไม่มีใครเรียน = จ่ายฟรี
- defect class: "feature parity ขาด" (กฎเหล็ก #1 ข้อ 2) · enum ซ้อน (#6 vs #56) = corpus สองกอง
- ทางแก้: (1) เขียน `LineSplitDistillationModel` แบบ bespoke ที่จำ "template ของผู้ขาย" (ตำแหน่งคอลัมน์/regex
  บรรทัดที่ผ่าน guard + ที่ผู้ใช้ยืนยัน) → ใช้ regex ซ้ำกับผู้ขายเดิมโดยไม่ต้องเรียก AI; (2) heuristic fallback
  ขั้นต่ำเมื่อไม่มี AI: แตกบรรทัดจาก raw text ด้วย `FieldPatternLibrary` (บรรทัดที่ลงท้ายด้วยจำนวนเงิน) ผ่าน
  `OcrLineSplitGuard` เดิม; (3) ตี `LineItemStructuredParse` #6 เป็น `[Obsolete(error:true)]` แบบเดียวกับ #13–16
- ความมั่นใจ: สูง

### T3-03 [🔴 P0][M] เส้น "1-click approve" (bulk + การ์ด) **ไม่ CAPTURE อะไรเลย** — ยิ่ง UX ดี ระบบยิ่งไม่เรียน
- ไฟล์: `wwwroot/pages/document-scan.html:1361` (bulk `create-document?approve=true` ไม่เรียก `ocrCorrect`) ·
  `:1594–1602` (การ์ด `createDocument` เรียก `ocrCreateDocument` อย่างเดียว) ·
  `OcrService.cs:4446–5276` (`CreateDocumentFromScanAsync`) — `awk` ช่วงนี้ **ไม่มี** `_feedbackRecorder`/
  `RecordUserChoiceAsync`/`_categoryLearner.RecordAsync`/`RecordConfirmAsync` เลยสักบรรทัด ·
  `SubmitCorrectionAsync :3700` บันทึก GL feedback เฉพาะ `if (!string.IsNullOrEmpty(correction.DebitAccountCode))`
- ทำไมพัง: ผู้ใช้กด "สร้าง+อนุมัติ" จากการ์ด/bulk (เส้นที่กฎเหล็ก #3 ตั้งเป็นเป้าหมาย) → ไม่ผ่าน
  `SubmitCorrectionAsync` → `TargetDocTypeAiFeedbackId`/`AiSuggestionFeedbackId`/`LineSplitAiFeedbackId`
  ไม่มีวันได้ `UserChosenAnswer` → student ของ doc-type/vendor/line-split **ไม่ได้ตัวอย่างบวก** (accepted-as-is)
  · GL รอดเพราะ `DocumentService.RecordLineAccountFeedbackAsync :11714` ถูกเรียกตอน approve `:5399`
  (ปิด loop ผ่านเอกสาร — ดีมาก แต่มีแค่ GL) · `_docWorkflowLearner.RecordConfirmAsync :3799` ก็อยู่ใน
  SubmitCorrection เท่านั้น
- ผลกระทบ: student เรียนจาก "เคสที่ผู้ใช้ต้องแก้" เท่านั้น = **selection bias** (เรียนแต่ตัวอย่างที่ AI ผิด/
  ยาก) ⇒ Wilson score/majority ของ Generic model เพี้ยนไปทางลบ ⇒ IsReady ช้า ⇒ ยิง provider นานกว่าที่ควร
- defect class: "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้" · "ด่านครอบทางเดียว" (review modal ครอบ, bulk ไม่ครอบ)
- ทางแก้: ย้ายการปิด loop ไปที่ **จุดที่เอกสารกลายเป็นจริง** — ใน `ApproveDocumentAsync` (หรือ hook หลัง approve
  ใน `OcrController :289`) ค้น `OcrScanResult` ที่ `CreatedDocumentId == doc.Id` แล้วปิดทุก FeedbackId บน scan
  ด้วยค่าจากเอกสาร: `TargetDocTypeAiFeedbackId`←`doc.DocumentType`, `AiSuggestionFeedbackId`←`doc.ContactId`,
  `LineSplitAiFeedbackId`←lines JSON, + `RecordConfirmAsync`/`_categoryLearner.RecordAsync` — helper ตัวเดียว
  `OcrFeedbackCloser.CloseFromDocumentAsync(scan, doc)` เรียกจากทั้ง SubmitCorrection และ Approve (idempotent
  แบบเดียวกับ `RecordLineAccountFeedbackAsync` ที่ skip เมื่อ `UserChosenAnswer` มีแล้ว)
- ความมั่นใจ: สูง

### T3-04 [🔴 P1][S] student ทุกตัวว่างเปล่าหลัง restart ≥7 นาที และบน instance ที่แพ้ล็อก **ตลอดไป**
- ไฟล์: `AiFeedbackTrainingJob.cs:39` (`Task.Delay(7 min)`), `:57–60` (`JobLock.RunExclusiveAsync` ครอบ
  `ReloadDistillationModelsAsync`), `Helpers/JobLock.cs:59,66` (try-lock, แพ้ = ข้าม) ·
  `GenericFeedbackDistillationModel.cs:44–46`, `GlAccountDistillationModel.cs:29` (IsReady จาก dict ใน process)
- ทำไมพัง: state อยู่ใน process → โหลดได้ทางเดียวคือ job → job ถูก lock ให้รัน **1 process** → process B
  ไม่เคยโหลด → `TryPredictLocalAsync :59` เห็น `IsReady=false` → ยิง provider ทุก request (เสียเงิน) หรือเมื่อ
  provider ปิดก็ตกไป heuristic · deploy ใหม่ทุกครั้ง = 7 นาทีแรกทุก instance เหมือนไม่มี student
- ผลกระทบ: ค่า `UsedAi` และบิล provider ขึ้นกับว่า load balancer ส่ง request ไปเครื่องไหน — ขัด CLAUDE.md
  กฎเหล็ก #4 D (multi-instance readiness) · ตัวชี้วัด LocalAccuracy ไม่มีความหมายข้ามเครื่อง
- defect class: "state ที่ผูกกับเครื่อง จะพังตอนมีเครื่องที่สอง โดยไม่มี error ให้เห็น" (บทเรียน DataProtection)
- ทางแก้: (1) `PredictAsync` lazy-load ต่อบริษัทเมื่อยังไม่มี state (แบบ `RefreshIndustryBaselineAsync` ที่มี
  `IndustryRefreshInterval` อยู่แล้ว `GlAccountDistillationModel.cs:255`) พร้อม TTL; (2) แยก "train" (เขียนตาราง —
  ต้อง lock) ออกจาก "reload" (อ่านตาราง — ทุก instance ทำเองได้) ให้ reload รันนอก lock; (3) ระยะยาว: เก็บ
  ผลลัพธ์ที่ train แล้วเป็นตาราง (`LocalModelSnapshot` per feature/company, version) แล้ว student อ่านจาก DB
  ตาม version — ไม่มี state ใน process
- ความมั่นใจ: สูง (ต้องเช็คต่อ: มี hosted service อื่นเรียก `LoadFromFeedbackAsync` ตอน boot ไหม —
  `grep LoadFromFeedbackAsync(` พบแค่ job `:519` และ `BulkBankAiMatchService.cs:1760`)

### T3-05 [🟠 P1][S] VendorCanonicalization ทิ้ง FeedbackId ทันทีที่ AI "ไม่มั่นใจ" — สัญญาณที่มีค่าที่สุดหาย
- ไฟล์: `OcrService.cs:1893–1920`
- โค้ด: `if (aiResult.UsedAi && … >= 0.70m && Guid.TryParse…) { … scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId; }
  else if (!aiResult.UsedAi && aiResult.FeedbackId.HasValue) { scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId; }`
- ทำไมพัง: AI ตอบจริง (UsedAi=true) แต่ conf<0.70 / `__NEW__` / contact ไม่มีจริง → **ไม่เข้าทั้งสอง branch**
  → FeedbackId ไม่ถูกเก็บ → `MatchContactAsync :5805` ไม่มีอะไรให้ปิด → แถว feedback ค้าง `UserChosenAt=null`
  ตลอดกาล → student ไม่ได้ตัวอย่าง "AI ผิดตรงนี้ ที่ถูกคือ X" ซึ่งเป็นตัวอย่างที่แก้ bias ได้ดีที่สุด
  (เทียบกับ GL ที่ `:1404` เก็บ FeedbackId เสมอ — ถูกต้อง)
- ผลกระทบ: student VendorCanon โตช้า · LocalModelHealth นับ AI accuracy เกินจริง (แถวที่ AI ผิดถูกตัดออกจาก
  ตัวหาร เพราะไม่มี UserChosenAt)
- defect class: "capture ครึ่งเดียว" · "แก้ตัวเดียว เหลือที่เหลือ" (GL ทำถูกแล้ว, vendor ไม่ได้ตาม)
- ทางแก้: ย้าย `scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId;` ออกมาก่อน `if` (เก็บเสมอ) และเก็บ
  `AiSuggestedContactId` แม้ไม่ apply เพื่อให้ UI โชว์ "AI เสนอ X" แบบเดียวกับ GL `:1406–1409`
- ความมั่นใจ: สูง

### T3-06 [🟠 P1][S] `AzureDiPatternLearner` สอน "known good" จากผลอ่านที่ยังไม่มีใครยืนยัน — เรียนจากเสียง
- ไฟล์: `OcrService.cs:369` (เรียกทันทีหลัง Azure ตอบ ก่อน corrector/ผู้ใช้) ·
  `Services/Implementations/Ocr/AzureDiPatternLearner.cs:82–90` (Seller/Buyer/Address/Phone/Email) ·
  `:125–160` (ไม่มี threshold ของ `sourceConfidence`; `ConfirmedCount += 1` `:141` ทั้งที่ไม่มีการ confirm;
  `Source="AzureDI"`) · `VendorKnownGoodCorrector.cs:106–110` (ทับเมื่อ sim≥0.80 และ `Value != noisyValue`)
- ทำไมพัง: ใบแรกของผู้ขาย Azure อ่าน "บริษัท เอบีซี จำกัค" → เก็บเป็น known good (Confidence = ของ Azure) →
  ใบที่สอง Azure อ่านถูก "บริษัท เอบีซี จำกัด" → sim 0.93 ≥ 0.80 และไม่เท่ากัน → **ถูกทับด้วยค่าผิดของใบแรก**
  → ใบที่สองก็ Learn อีก (ค่าถูก ConfirmedCount=1) แต่ค่าผิดมี ConfirmedCount สูงกว่าจากการ "เห็น" ซ้ำ
  (`:141`) → ranking `ThenByDescending(ConfirmedCount)` ยิ่งตรึงค่าผิด · ทางแก้เดียวคือผู้ใช้แก้ในหน้า review
  (`Source=UserCorrection` ชนะ `:107`) ซึ่งเป็นเส้นที่ T3-03 บอกว่าผู้ใช้ส่วนใหญ่ไม่เดิน
- ผลกระทบ: ชื่อผู้ขายบนใบกำกับ §86/4 ผิดซ้ำทุกใบของผู้ขายรายนั้น · Contact auto-create (`:1931–2126`) สร้าง
  ผู้ติดต่อชื่อผิด · เมื่อ key คือ `VendorTaxId` ที่ OCR อ่านผิด (บทเรียน EAN-13) ค่าถูกยัดใต้ผู้ขายผิดราย
- defect class: ญาติของ `VendorKnownGoodCorrector` (ปิดแล้วเรื่อง per-document) แต่ **มิติใหม่: แหล่งสอน**
- ทางแก้: (1) `LearnAsync` เขียนด้วย `Source="AzureDI-unconfirmed"`, `ConfirmedCount=0` และ corrector
  **ไม่ใช้** แถวที่ `ConfirmedCount==0` ทับค่าอื่น (ใช้ได้เฉพาะเติมช่องว่าง); (2) ย้ายการ "promote เป็น known good"
  ไปที่จุดอนุมัติเอกสาร (T3-03 helper เดียวกัน) โดยเอาค่าจาก `Document`/`Contact` ที่อนุมัติแล้ว; (3) migration
  ลด `ConfirmedCount` ของแถว `Source='AzureDI'` เป็น 0 (ซ้ำรอย migration `VendorKnownGoodValues` รอบก่อน)
- ความมั่นใจ: สูง (ต้องเช็คต่อ: `UpsertKnownGoodAsync` มี branch ที่ `Source=="UserCorrection"` กัน Azure ทับ `:145` ✅
  แต่ไม่กัน Azure-vs-Azure)

### T3-07 [🔴 P1][S] ปุ่ม "AI ตรวจทาน" เขียนคำตอบ AI ลงฟอร์ม **ตรง ๆ** ไม่มี anti-hallucination guard และไม่เคารพค่าที่ผู้ใช้พิมพ์
- ไฟล์: `wwwroot/pages/document-scan.html:2978–3000` (`_applyAiCorrections`) · `Services/Ai/AdvancedAiAugmenter.cs:749–759`
  (`ToResult` ส่ง `RawResponseJson` ผ่านโดยตรวจแค่ "มี key ไหม" `ValidateSchema`) · `Controllers/AiSuggestionController.cs:3034–3040`
- โค้ด: `set('revVendorTaxId', c.vendor_tax_id); set('revSubTotal', c.sub_total); set('revVatAmount', c.vat_amount);
  set('revTotalAmount', c.total_amount);` — `set` เช็คแค่ `looksMasked` และไม่ว่าง แล้ว `el.value = val`
- ทำไมพัง: (1) เซิร์ฟเวอร์ไม่ validate อะไรเลยนอกจาก schema key → (2) UI ทับค่าทุกช่องรวมยอดเงิน/เลขผู้เสียภาษี →
  (3) ไม่มี `userTouched`/`dataset.autoSrc` ⇒ ค่าที่ผู้ใช้เพิ่งแก้ถูก AI ทับ (ขัดกฎเหล็ก #4 A "ห้ามใครมาก่อนชนะ") →
  (4) ไม่มีด่าน Σ(บรรทัด)=Subtotal, Subtotal+VAT=Total, mod-11 ของ tax id, VAT = 7/107 — ต่างจากเส้น ScanAsync ที่
  ทุก AI call มี guard (`:871`, `:1416–1425`, `:1885–1890`, `OcrLineSplitGuard`)
- ผลกระทบ: ตัวเลขที่ AI แต่งขึ้นกลายเป็นยอดบนใบกำกับ §86/4 และ ภ.พ.30 ด้วยการกดปุ่มเดียว ทั้งที่ CLAUDE.md
  anti-pattern เขียนไว้ตรง ๆ ว่า "ห้ามใช้คำตอบ AI โดยไม่ validate"
- defect class: "anti-hallucination guard ขาด" (กฎเหล็ก #1 checklist) · "สำเนามือฝั่ง JS" (guard ฝั่ง server มี แต่เส้นนี้ไม่ผ่าน)
- ทางแก้: ให้เซิร์ฟเวอร์เป็นคนตัดสิน — `ReviewOcrAsync` ส่ง `corrections` ผ่าน `OcrReviewGuard.Evaluate(scan, corrections)`
  (ใช้ `ThaiTaxId`, amount triangle จาก `OcrAmountTriangleTests` logic, enum check) แล้วส่งเฉพาะช่องที่ผ่านพร้อม
  `accepted[]/rejected[]{field, reason}`; UI แสดง rejected เป็นคำแนะนำ ไม่เติม; และ `set()` ต้องข้ามช่องที่ `userTouched`
- ความมั่นใจ: สูง

### T3-08 [🟠 P2][S] `_categoryLearner` (0.55) ทับคำตอบ NaiveBayes (≥0.70) โดยไม่เทียบความมั่นใจ — "ใครมาหลังชนะ"
- ไฟล์: `OcrService.cs:1052–1054` (NB เทียบ `FieldConfidence["DebitAccount"]` ก่อนทับ ✅) vs `:1091–1094`
  (`if (learnedCode != null && learnedConf >= 0.55m) { extractedData.DebitAccountCode = learnedCode; …}` ไม่เทียบ
  และ **ไม่อัปเดต `FieldConfidence["DebitAccount"]`**)
- ทำไมพัง: ลำดับ global rules → NB (0.9, ตั้ง FieldConfidence=0.9) → learner (0.55) ทับ → ค่าที่แสดงคือ learner แต่
  FieldConfidence ยังเป็น 0.9 ของ NB ⇒ ไฮไลต์เหลือง (กฎเหล็ก #3 ข้อ 3) ไม่ขึ้น และ `localConf` ที่ส่งให้ AI `:1341`
  บอกครูว่า "local มั่นใจ 0.9" ทั้งที่ค่าเป็นของตัว 0.55
- ผลกระทบ: GL ผิดเงียบในเคสที่สองตัวเห็นต่าง + ครูตรวจคำตอบผิดตัว (บทเรียนเดียวกับ `ReplaceLocalModelBlock`
  ใน orchestrator `:160–169` ที่แก้ไปแล้วสำหรับ student)
- defect class: "ห้ามใครมาก่อนชนะ — ผลลัพธ์ห้ามขึ้นกับลำดับ" (กฎเหล็ก #4 A) · ญาติ "FieldConfidence 3 ชุดชื่อ"
- ทางแก้: เป็นเคสตัวอย่างของ 🔵 T3-D1 (arbiter) — ระยะสั้น: ทับเฉพาะเมื่อ `learnedConf > FieldConfidence["DebitAccount"]`
  และตั้ง FieldConfidence ให้ตรงค่าที่ใช้
- ความมั่นใจ: สูง

### T3-09 [🟠 P2][S] key ของ federated doc-workflow learner ต่างกันระหว่างฝั่งเขียนกับฝั่งอ่านเมื่อเลขภาษีไม่ครบ 13 หลัก
- ไฟล์: writer `OcrService.cs:3783–3791` (`if (digits.Length == 13) vKey = $"tax:{digits}"` ไม่ครบ → `name:`) ·
  reader `:1177–1181` (`$"tax:{digits}"` ไม่เช็คความยาว)
- ทำไมพัง: OCR อ่านเลขได้ 12 หลัก → reader ถาม `tax:123…(12)` → writer ไม่เคยเขียนคีย์นี้ (เขียน `name:`) → ไม่เจอ
  เงียบ ๆ → ตกไปใช้ default · เคสนี้พบบ่อยใน Tesseract (หลักหาย) ซึ่งเป็นเคสที่ต้องการ learner มากที่สุด
- defect class: "ชื่อ key คนละชุดระหว่างฝั่งเขียนกับฝั่งอ่าน" (CLAUDE.md ทรง 4)
- ทางแก้: helper เดียว `VendorKeys.For(taxId, name)` ใช้ทั้งสองฝั่ง (แบบเดียวกับ `OcrFullReviewDistillationModel.VendorKey`)
- ความมั่นใจ: สูง (ต้องเช็คต่อ: `GlobalDocWorkflowLearner.PredictAsync` normalize key เองไหม — ยังไม่ได้เปิด)

### T3-10 [🟠 P2][S] `OcrFullReviewDistillationModel` ไม่ถูกโหลดสำหรับบริษัทที่ไม่เคยกด ai-review — เงื่อนไข reload อิงตารางผิด
- ไฟล์: `AiFeedbackTrainingJob.cs:511–513` (`companyIds` = บริษัทที่มี `AiSuggestionFeedbacks.UserChosenAt != null`) vs
  `OcrFullReviewDistillationModel.cs:85–95` (เรียนจาก `OcrScanResult.CreatedDocumentId != null` ไม่ใช้ feedback เลย)
- ทำไมพัง: บริษัทสแกน 500 ใบ สร้างเอกสารครบ แต่ไม่เคยแก้อะไรใน review และไม่เคยกดปุ่ม AI → ไม่มีแถว feedback ปิด →
  ไม่อยู่ใน `companyIds` → student นี้ `IsReady=false` สำหรับบริษัทนั้นตลอด → ทุกครั้งที่กด ai-review ยิง provider
  ทั้งที่มีข้อมูลสอน 500 ใบอยู่ในมือ
- ทางแก้: ให้แต่ละ model บอกเองว่าต้องโหลดบริษัทไหน (`Task<IReadOnlyList<Guid>> CompaniesWithTrainingDataAsync()`
  บน `ILocalDistillationModel`) หรือ lazy-load ใน `PredictAsync` (T3-04)
- ความมั่นใจ: สูง

### T3-11 [🟠 P2][S] student "เรียนจากสแกน" ไม่ใช่ "จากเอกสารที่อนุมัติ" — สัญญาณที่ดีที่สุดถูกทิ้ง
- ไฟล์: `OcrFullReviewDistillationModel.cs:96–100` เอา `r.ExtractedVendorName, r.TargetDocumentType, r.ExpenseCategory`
  จาก **scan** · `Models/Entities` ไม่มี `Document.SourceScanId` (grep = 0; มีแค่ `FixedAsset.SourceScanResultId`) ·
  ลิงก์ทางเดียว `OcrScanResult.CreatedDocumentId` (ถูกล้างเมื่อลบเอกสาร `DocumentService.cs:7988`)
- ทำไมพัง: ผู้ใช้กด "สร้าง (Draft)" → เปิด documents.html แก้ผู้ขาย/ชนิด/บรรทัด → อนุมัติ ⇒ เอกสารถูก แต่ scan
  ยังถือค่าเดิม ⇒ student เรียนค่าก่อนแก้ (learning from noise ชั้นที่สอง) · มีเฉพาะ GL ที่ปิดจากเอกสาร
  (`RecordLineAccountFeedbackAsync :11714` ← `:2481/:5399`) และ `RecordLineAccountFeedbackAsync :11733`
  **skip ถ้า `UserChosenAnswer` มีแล้ว** ⇒ ถ้าผู้ใช้เลือก A ใน review แล้วเปลี่ยนเป็น B ตอนอนุมัติ ระบบจำ A
- ผลกระทบ: ตอบคำถามข้อ (4) ของ mission: **loop ปิดที่ review screen + GL ตอนอนุมัติเท่านั้น**; ชนิดเอกสาร/
  ผู้ขาย/วันที่/ยอด/บรรทัด ที่นักบัญชี "ลงจริง" ไม่ไหลกลับ
- ทางแก้: (1) join ผ่าน `CreatedDocumentId` → อ่านค่าจาก `Document` + `Contact` แทนช่องบนสแกน; (2) เปลี่ยน
  `RecordLineAccountFeedbackAsync` ให้ "final wins": อัปเดต `UserChosenAnswer` เมื่ออนุมัติ (แถว feedback มี
  `UserChosenAt` — เก็บ `FinalizedAt` แยกเพื่อไม่ flap) ; (3) หลังอนุมัติ snapshot ค่า final ลง scan
  (`FinalVendorName/FinalDocumentType/…` หรือตาราง `OcrScanOutcome`) ให้ทุก learner อ่านจากที่เดียว
- ความมั่นใจ: สูง

### T3-12 [🟠 P2][S] `UsedAi` ใน DTO ไม่ครบ — VendorCanon/DocType/Project ไม่มีป้าย ⇒ UI ติดป้ายซื่อสัตย์ไม่ได้
- ไฟล์: `OcrService.cs:6680–6702` (`ToDto` มีแค่ `GlAccountUsedAi`, `LineSplitUsedAi`) · `TargetDocTypeUsedAi` ถูกเซ็ต `:876`
  แต่ `grep` ใน `Models/DTOs` = 0 · ไม่มี `VendorCanonUsedAi`/`ProjectUsedAi` เลย
- ผลกระทบ: กฎเหล็ก #1 checklist ข้อ "เพิ่ม `bool <Feature>UsedAi` ใน DTO" ไม่ผ่าน 3 ใน 5 feature; หน้า review
  โชว์ชนิดเอกสารที่ AI เลือกเป็น "ระบบแนะนำ"
- ทางแก้: เพิ่ม 3 ธง + `Source` ตาม T3-01 ใน `OcrScanResultDto` และ `ToDto` (S)
- ความมั่นใจ: สูง

### T3-13 [🟠 P2][M] ไม่มี evaluation harness — วัด "student แม่นไหม" ได้แค่จาก feedback ที่มี selection bias
- ไฟล์: `Accounting.Tests/` (ls) มี `OcrLineSplitGuardTests`, `LearnedPatternReadPathTests`, `RdComplianceOcrNoiseTests`…
  แต่ **ไม่มี golden set** (grep `golden` = 0) · ตัวชี้วัดเดียวคือ `LocalModelHealth.AccuracyPercent`
  (`AiFeedbackTrainingJob.cs:85–104`) ซึ่งนับเฉพาะแถวที่มี `UserChosenAt` — คือแถวจาก review screen (T3-03)
- ผลกระทบ: ตัดสินใจ promote/demote student (`Recommendation`) จากตัวเลขที่เอนไปทางเคสยาก · เปลี่ยน regex/threshold
  ครั้งใดก็ไม่มีใครรู้ว่าดีขึ้นหรือแย่ลงบนกระดาษจริง (CLAUDE.md บทเรียน "negative test ที่ไม่ผ่าน = จำลองผิด")
- ทางแก้: 🔵 T3-D4 ข้างล่าง
- ความมั่นใจ: สูง

### T3-14 [🟠 P3][S] GL AI ถูกเรียก **ทุกใบ** ที่ call site — student-first อยู่แค่ใน orchestrator
- ไฟล์: `OcrService.cs:1314` `if (_aiAugmenter != null)` (ไม่มีเงื่อนไข confidence) เทียบกับ doc-type `:829`
  ที่มี `aiWorthAsking`
- ทำไมเป็นปัญหา: เมื่อ student ยังไม่ IsReady (T3-04: 7 นาทีแรก/instance ที่แพ้ล็อก) → ยิง provider ทุกใบแม้
  `_categoryLearner` ตอบ 0.95 ไปแล้ว → เสียเงิน + latency 15s timeout ต่อใบ · และคำตอบ AI ที่ conf≥0.70 **ทับ**
  learner ที่ 0.95 (`:1416–1425` ไม่เทียบ localConf)
- ทางแก้: gate เดียวกับ doc-type: เรียก AI เฉพาะ `FieldConfidence["DebitAccount"] < 0.85` หรือไม่มีค่า; และเมื่อ
  เรียก ให้ทับเฉพาะ `aiConf > localConf`
- ความมั่นใจ: สูง

### T3-15 [🟠 P3][S] enum ซ้อน `LineItemStructuredParse` (#6) กับ `OcrLineItemSplit` (#56) — job รู้จักตัวที่ไม่มีใครเรียก
- ไฟล์: `AllEnums.cs:1558` (#6) `:1908` (#56) · `AiFeedbackTrainingJob.cs:268,347` (จอง #6) · `AdvancedPrompts.cs:538` (ใช้ #56)
- ทำไมเป็นปัญหา: ซ้ำรอย E-AI-10 ที่เพิ่งปิด (#13–16) — คนอ่าน job เห็น "LineItemStructuredParse consume-only" แล้วคิดว่า
  line-split มี trainer แล้ว
- ทางแก้: ย้าย #56 เข้า `KnownTrainerFeatures`, ตี #6 `[Obsolete(error:true)]`
- ความมั่นใจ: สูง

## §4 Kill-switch trace — `AiProviderConfig.IsActive=false` ทุกตัว แล้ว `ScanAsync` ให้อะไร

เส้นทางใน orchestrator: `AiOrchestrator.cs:142` routing → `:143` student predict (ถ้า IsReady) →
Hybrid ≥0.85 คืน student (`ReturnLocalAsync`) **แต่ call site ทิ้ง (T3-01)** → ไม่ถึงเกณฑ์ → `:226–230`
`AiProviderConfigs.FirstOrDefaultAsync(p => p.IsActive && p.IsEnabled)` = null → `RecordSkip(NoProvider)` →
`FallbackToLocal :557` คืน `UsedAi=false, PrimaryAnswer=req.LocalPrimaryAnswer` (= ค่าที่ call site ส่งเข้ามาเอง)

| ช่อง | เส้นที่ยังเติมได้เมื่อไม่มี AI | ผล | หมายเหตุ |
|---|---|---|---|
| `TargetDocumentType` | `OcrDocumentRoleInferrer` `:746–800` + `_vendorIntel` `:1117` + federated `:1184` | ✅ มีค่าเสมอ | student (Generic) ไม่มีส่วนร่วม (T3-01) — ค่า default `Expense/PaymentVoucher` เมื่อ role ไม่รู้ (คอมเมนต์ `:805` ยอมรับว่า "เป็นการเดา") |
| `DebitAccountCode` | global rules → NB `:1056` → `_categoryLearner` `:1089` → `VendorDefaultGlAccount`/industry | ✅ ส่วนใหญ่ · ❌ **ว่าง** เมื่อผู้ขายใหม่ + คำอธิบายไม่เคยเห็น + ไม่มี rule (GL student ที่มี industry baseline **มีคำตอบ** แต่ถูกทิ้ง) | กฎเหล็ก #3 ข้อ 1 ตกในเคส cold vendor |
| `MatchedContactId` | tax id/name match `:1775–1868` → auto-create `:1931–2126` | ✅ (สร้างใหม่ถ้าไม่เจอ) | VendorCanon student ถูกทิ้ง ⇒ ผู้ขายเดิมที่สะกดต่าง → **สร้าง Contact ซ้ำ** ทั้งที่ student รู้จัก |
| `Items[]` เมื่อ engine ไม่คืนตาราง | **ไม่มี** (`TrySplitLineItemsWithAiAsync :6850 return`) | ❌ ว่าง — บรรทัดสรุปเดียว | T3-02 |
| `ProjectId` ต่อบรรทัด | metadata rules ใน `OcrMetadataProjectMatcher` ก่อน AI | ✅ ส่วนใหญ่ | gate `res.UsedAi` `:413` ทิ้ง student เหมือนกัน |
| ปุ่ม "AI ตรวจทาน" (OcrFullReview) | student bespoke ถ้า IsReady + โหลดแล้ว (T3-10) | ⚠️ ได้ `structured` จาก student เฉพาะบริษัทที่เคยปิด feedback | เส้นนี้แก้ E-AI-01 แล้ว (RawResponseJson = StructuredJson ✅) |
| `AiSuggestionController` 27 endpoint | `MemoryLookupAsync` + heuristic + `_localModels` (`:140–145` PaymentType ใช้ student จริง ✅) | ✅ | เป็นตัวอย่างที่ **ใช้ student ถูกต้อง** — ต่างจากเส้น OCR |

**สรุปคำตอบข้อ (3):** ปิด AI แล้วช่องที่ว่างจริงคือ **รายการสินค้า (เมื่อไม่มีตาราง)** และ **GL ของผู้ขายใหม่ที่ไม่มี rule**;
ที่เหลือมีค่าจาก rule/heuristic แต่ **คุณภาพเท่ากับวันที่ยังไม่มี AI** เพราะสิ่งที่เรียนมาทั้งหมด (student) ไม่ถูกใช้
บนเส้นนี้ — ระบบไม่ได้ "ฉลาดขึ้นตามการใช้งาน" ในความหมายของกฎเหล็ก #1 มันแค่ "จ่ายน้อยลง"

## §5 🔵 สถาปัตยกรรมเป้าหมาย — "self-improving accountant"

### T3-D1 [🔵][L] Decision Record ต่อช่อง + Arbiter แทน "ใครมาหลังชนะ" ข้าม 6+ แหล่ง
**ปัญหาที่แก้:** วันนี้ค่าแต่ละช่องใน `OcrExtractedData` ถูก**ทับ**ตามลำดับโค้ดใน ScanAsync 2,260 บรรทัด
(engine → knownGood → EnrichFromRawText → learned patterns → zone → NB → learner → vendorIntel → AI → DBD…)
ร่องรอยมีแค่ `ReasoningTrace` (ข้อความ) และ `FieldConfidence` (ตัวเลขเดียว ไม่บอกแหล่ง) ⇒ T3-08, T3-01, และ
บทเรียน "FieldConfidence 3 ชุดชื่อ" เป็นอาการของโครงสร้างเดียวกัน

**แบบ:**
```
FieldDecision {
  Field: "DebitAccountCode",
  Candidates: [ {Value, Source: Engine|KnownGood|Pattern|Zone|Rule|NB|Learner|VendorIntel|Student|Provider|Dbd|History,
                 Confidence, Evidence: {RawSpan?, PatternId?, FeedbackId?, TrainingN?}, Reason} ... ],
  Chosen: index, Policy: "max-confidence with source priors + veto rules",
  Vetoes: [ "CoA-not-found", "side-mismatch", "EAN13-barcode", "amount-triangle" ]
}
OcrScanResult.DecisionsJson  (persist ทั้งก้อน — ไม่ใช่แค่ค่าสุดท้าย)
```
- ทุกแหล่งเดิม**ไม่ต้องเขียนใหม่** — เปลี่ยนจาก `extractedData.X = value` เป็น `decisions.Propose("X", value, source, conf, evidence)`;
  `Arbiter.Resolve()` ครั้งเดียวก่อน persist `:733` แล้วเขียน `extractedData` + `FieldConfidence` จาก `Chosen`
- Arbiter policy เริ่มง่าย: `score = conf × prior(source)` โดย prior จาก `LocalModelHealth`/backtest (D4) — UserCorrection/KnownGood-confirmed
  = 1.0, Student(ready) = 0.95, Provider = 0.9, Learner = 0.85, NB = 0.8, Pattern/Zone = 0.7, Engine = conf ตรง ๆ, default = 0.3
  ("ค่าที่แต่งขึ้น" ต้องแพ้ทุกแหล่งที่มีหลักฐาน — บทเรียน `?? "00000"`)
- veto rules คือ guard ที่มีอยู่แล้ว (CoA exists, `DocumentSide.MatchesRole`, `ThaiTaxId`, `OcrLineSplitGuard`, EAN-13) ย้ายมาเป็น
  ชั้นเดียว ใช้กับ**ทุก**แหล่ง ไม่ใช่เฉพาะ AI (T3-07 คือรูที่เกิดจาก guard ผูกกับแหล่ง)
- UI ได้ "ทำไมช่องนี้เป็นค่านี้ / ทางเลือกอื่น" ต่อช่อง — แทน `ReasoningTrace` ยาว ๆ · ป้าย 🤖/⚙️ มาจาก `Chosen.Source` ตรง ๆ (ปิด T3-12)
- reuse: `OcrExtractedData`, `FieldConfidence`, guards ทั้งหมด, `ReasoningTrace` (สร้างจาก decisions) · replace: การเขียน `extractedData.X =` กระจาย ~40 จุด
- ขนาด: L (2–3 วัน core + 2 วันไล่ 40 จุด) — ทำเป็น 2 คอมมิต: (ก) record อย่างเดียว ไม่เปลี่ยนคำตอบ (shadow) (ข) เปิด arbiter หลัง backtest D4 ผ่าน
- **ฝ่ายค้าน:** "เพิ่มชั้น abstraction ใน ScanAsync ที่ยาวอยู่แล้ว = ยากขึ้น" → **ตอบ:** วันนี้ยาวเพราะทุกแหล่งต้องเขียน if-else ว่าจะทับหรือไม่ทับ
  (`:1052–1054` เทียบ, `:1091` ไม่เทียบ) — decision record ลบ if-else เหล่านั้นออก · "prior เป็นตัวเลขแต่งขึ้น" → ตอบ: ใช่ในวันแรก
  จึงต้อง shadow mode + D4 วัดก่อนเปิด และ prior ต้องมาจาก backtest ไม่ใช่หยิบเลขสวย (บทเรียน threshold 0.80)

### T3-D2 [🔵][M] Student ต้อง "ตอบได้" ไม่ใช่ "ถูกวัด" — เปลี่ยน contract ผลลัพธ์ + lazy-load
- `OcrAiAugmentationResult` เพิ่ม `Source` (Provider/Student/Heuristic) และ `TrainingSamples` — call site ตัดสินจาก `Source != Heuristic && conf ≥ τ`
  ผ่าน veto เดิม (ปิด T3-01/T3-14/T3-12 พร้อมกัน) · student ที่ short-circuit ต้องโชว์ "⚙️ เรียนจาก N ครั้งของคุณ" ไม่ใช่ "🤖 AI"
- student state: (ก) lazy-load ต่อบริษัทใน `PredictAsync` เมื่อไม่มี state + TTL 6 ชม. (ข) `ReloadDistillationModelsAsync` ออกนอก `JobLock`
  (อ่านอย่างเดียว ทุก instance ทำได้) (ค) `ILocalDistillationModel.CompaniesWithTrainingDataAsync()` (ปิด T3-04/T3-10)
- ระยะยาว: `LocalModelSnapshot(feature, company, version, blob)` เขียนโดย job, อ่านโดยทุก instance — ไม่มี state ใน process (บทเรียน DataProtection)
- **ฝ่ายค้าน:** "lazy-load ใน request path = latency" → ตอบ: โหลดครั้งแรกต่อบริษัทต่อ instance (ms-ระดับ query เดียว) ถูกกว่าการยิง provider 1–5 วิ
  ที่เกิดแทนทุกครั้งวันนี้ · "singleton in-memory ตั้งใจแล้ว (คอมเมนต์ Program.cs:527)" → ตอบ: ตั้งใจถูกสำหรับ 1 instance; CLAUDE.md #4 D
  ห้ามสร้าง state ข้าม request โดยไม่มีแผน multi-node — แผนคือ snapshot table

### T3-D3 [🔵][M] ปิด loop ที่ "เอกสารที่ลงจริง" — `OcrFeedbackCloser.CloseFromDocumentAsync`
- จุดเรียก: `ApproveDocumentAsync` (หลัง `RecordLineAccountFeedbackAsync :5399`) + `UpdateDocumentAsync :2481` (final-wins) + `SubmitCorrectionAsync :3517` (เส้นเดิม)
- งาน: ค้น `OcrScanResult` ที่ `CreatedDocumentId == doc.Id` → ปิด `TargetDocTypeAiFeedbackId`←`doc.DocumentType`, `AiSuggestionFeedbackId`←`doc.ContactId`,
  `LineSplitAiFeedbackId`←lines, `ProjectAiFeedbackId`←line.ProjectId · เรียก `_vendorIntel.TryTrainAsync` (มีแล้ว `:5306` ✅), `_categoryLearner.RecordAsync`,
  `_docWorkflowLearner.RecordConfirmAsync`, promote `VendorKnownGoodValue` เป็น `Source=DocumentApproved, ConfirmedCount+1` (ปิด T3-06 ทิศบวก)
  · เขียน `OcrScanOutcome` snapshot (ค่า final ต่อช่อง + `DecisionsJson` จาก D1) = ตาราง training ตัวเดียวให้ทุก learner (ปิด T3-11)
- **ตัวอย่างบวกต้องเข้าด้วย**: accepted-as-is = `acceptedAi=true`/`UserChosenAnswer=ค่าเดิม` — วันนี้หายทั้งก้อน (T3-03) ⇒ Wilson score ของ Generic model เอนลบ
- idempotent: มี `UserChosenAt` แล้ว → เขียน `FinalizedAt`/`FinalAnswer` แยก (ไม่ทับ first-choice เพื่อวัด "review vs final" ได้)
- **ฝ่ายค้าน:** "DocumentService จะรู้จัก OCR มากไป (คอมเมนต์ OcrController:22 กันวงกลม DI)" → ตอบ: ใช้ domain event `DocumentApproved(docId)` +
  handler ใน OCR layer (pattern มีแล้วใน `SignatureApprovalService :435` ที่เรียก `TryTrainAsync`) — ไม่ต้องให้ DocumentService อ้าง OcrService

### T3-D4 [🔵][M] Evaluation harness — golden set กระดาษจริง + backtest + KPI ตามกฎเหล็ก #1 ข้อ 6
- **Golden set:** ตาราง `OcrGoldenSample(scanId, finalDecisionsJson, frozenAt)` เติมอัตโนมัติจาก D3 (ทุกใบที่อนุมัติ = label) + ชุดคัดมือ 100 ใบ
  (ทุก engine tier × ชนิดเอกสาร × ผู้ขายซ้ำ/ใหม่) ที่ **ไม่ใช้ train** · เก็บ raw text + engine output ต้นทาง (มีแล้วใน `RawTextContent`/`ExtractedItemsJson`)
- **Backtest job (nightly, หลัง retrain):** รัน arbiter/student/heuristic แบบ offline บน golden → per-field accuracy ต่อ source ต่อ engine tier ⇒
  เขียน `LocalModelHealth` (มีอยู่แล้ว) เพิ่มมิติ `Field` + `Source` · prior ของ D1 อัปเดตจากตรงนี้
- **KPI:** (1) `ProviderCallRate` = provider calls / scans (ต้องลด) (2) `StudentServeRate` = ช่องที่ `Chosen.Source=Student` (ต้องเพิ่ม —
  วันนี้ = 0 โดยโครงสร้าง) (3) `FirstPassAcceptRate` = ใบที่อนุมัติโดยไม่แก้ช่องใด (ตัวชี้วัด "แค่อัพแล้วจบ") (4) `EditsPerDoc` ต่อช่อง
  (5) `ColdVendorAccuracy` แยกจาก known vendor · dashboard ใน admin ที่มีอยู่ (`LocalModelHealth.Recommendation`)
- เทสต์ xUnit: `ArbiterBacktestTests` โหลด 20 ใบจาก `Accounting.Tests/Fixtures/ocr-golden/*.json` (raw text + expected) — negative test:
  ใส่ heuristic เดิม (learner 0.55 ทับ NB) กลับเข้าไปแล้ว accuracy ต้องตก
- **ฝ่ายค้าน:** "label จากผู้ใช้ก็ผิดได้" → ตอบ: ใช่ แต่มันคือสิ่งที่**ลงบัญชีจริง** — ถ้าผิดก็ผิดในงบอยู่แล้ว; harness วัด "ตรงกับที่นักบัญชีลง"
  ไม่ใช่ "ตรงกับความจริงสัมบูรณ์" และชุดคัดมือ 100 ใบมี label ตรวจสองคน

### T3-D5 [🔵][M] LLM อยู่ตรงไหนถึงคุ้มสุดและพึ่งน้อยสุด — "full-review pass แบบ structured + guard" 1 ครั้ง แทน per-field 4 ครั้ง
- วันนี้ยิง 4–5 ครั้งต่อใบ (doc-type, GL, vendor, split, project) ต่างบริบท ⇒ ครูเห็นแค่เศษกระดาษทีละชิ้น + 4 feedback row ที่ปิดคนละที่ (T3-03/05)
- เสนอ: **หนึ่ง call** `OcrFullReview` (prompt/student/entity มีครบแล้ว `AdvancedPrompts.cs:113`, `OcrFullReviewDistillationModel`) ทำงานเป็น
  **ผู้เสนอ candidate ต่อช่อง** เข้า D1 (source=Provider) — ไม่ใช่ผู้ตัดสิน — เรียกเฉพาะเมื่อ arbiter มีช่อง §86/4 ที่ `Chosen.Confidence < 0.85`
  หรือ veto ขัดกัน (เช่น Σบรรทัด ≠ total) · ผลผ่าน `OcrReviewGuard` (T3-07) ก่อนเข้า arbiter
- ต่อ call เก็บ feedback **หนึ่งแถว** ที่ `UserChosenAnswer` = JSON ค่า final ต่อช่อง (D3) ⇒ student full-review เรียน "รายช่อง" ได้จริง
  (โครงสร้างของ `OcrFullReviewDistillationModel` รองรับอยู่แล้ว `:167–174`)
- per-field call เดิมเก็บไว้เฉพาะ GL (ต้องการ CoA ของบริษัทใน prompt — บริบทเฉพาะ) และ line-split (ต้องการ raw text ยาว) — ยุบ doc-type/vendor/project เข้า full-review
- ประโยชน์: token ลด ~50% (system prompt/company context ส่งครั้งเดียว — `EnrichSystemPromptWithCompanyAsync :263` ทำต่อ call วันนี้), latency 1 รอบ,
  ครูเห็นทั้งใบ (ตัดสิน doc-type จาก VAT/บทบาท/บรรทัดพร้อมกัน), feedback หนึ่งแถว = ปิด loop จุดเดียว
- **ฝ่ายค้าน:** "call ใหญ่ล้มทีเดียวหายหมด / JSON ยาว parse ยาก" → ตอบ: `ValidateSchema` + guard ต่อช่องมีแล้ว; ช่องที่ parse ไม่ผ่านแค่ไม่เข้า arbiter
  ส่วนที่เหลือใช้ได้ (partial acceptance) — ต่างจากวันนี้ที่ doc-type ล้ม = ทั้งฟีเจอร์ตก · "GL ต่อบรรทัดต้องการ CoA ทั้งผัง" → ตอบ: ส่ง top-30 ผัง
  ที่ใช้บ่อย + ผังที่ student/learner เสนอ (บริบทที่ `CompanyBusinessContextLoader :445` โหลดอยู่แล้ว)

### Roadmap (ขนาด · reuse/replace)
| เฟส | งาน | ขนาด | reuse | replace | ปิด finding |
|---|---|---|---|---|---|
| 0 (S-fix, ทำได้ทันที) | T3-02 ทางแก้ (2)(3) · T3-05 · T3-09 · T3-12 · T3-15 · T3-08 ระยะสั้น · T3-14 gate | S×7 | ทั้งหมด | — | 02,05,08,09,12,14,15 |
| 1 | D2: `Source` ใน result + call site ใช้ student + lazy-load/reload นอก lock | M | orchestrator, students, guards | gate `UsedAi &&` 5 จุด | 01,04,10 |
| 2 | D3: `OcrFeedbackCloser` + `OcrScanOutcome` + domain event หลัง approve | M | `RecordLineAccountFeedbackAsync`, `TryTrainAsync`, learners | `LearnAsync` ของ Azure เป็น unconfirmed | 03,06,11 |
| 3 | D1 shadow → D4 harness → เปิด arbiter | L | ทุก source เดิม | การเขียน `extractedData.X=` ~40 จุด | 07,08,13 |
| 4 | D5 full-review เป็นแหล่งเดียวของ LLM (คง GL/split) · `LineSplitDistillationModel` | M+M | prompts/students ที่มี | per-field call doc-type/vendor/project | 02 (เต็ม), token/latency |

## §6 ตรวจแล้วไม่ใช่บั๊ก (ทีมอื่นไม่ต้องเสียเวลาซ้ำ)
- `RecordLineAccountFeedbackAsync` (`DocumentService.cs:11714`) **มี call site จริง** `:2481` (Update) + `:5399` (Approve) — GL loop ปิดจากเอกสาร ✅
- `_vendorIntel.TryTrainAsync` ถูกเรียกจากเส้นอนุมัติหลัก `DocumentService.cs:5306` ✅ (+ Signature/Integration/E-commerce)
- pattern writer/reader key ตรงกัน: writer เขียน `SellerTaxId`/`DocumentNumber` (+negative `SellerName`) `DocumentZoneAnalyzer.cs:760–775`,
  reader รองรับ 6 ชื่อรวมทั้งหมดนั้น `:671–725` ✅ (`LearnedPatternReadPathTests` ครอบ)
- `DocumentWorkflowPredictor`/federated reader condition แก้จาก write-only แล้ว (`OcrService.cs:1173` ทำงานเมื่อ role<0.9 หรือไม่มาจากกระดาษ) ✅
- `AiCallBilling.BillableRow` (`Helpers/AiCallBilling.cs:62–67`) ยังคัด `ProviderUsed != None && CacheHitOfFeedbackId == null` ✅ (E-AI fix ยังอยู่)
- orchestrator `ReplaceLocalModelBlock :160–169` + `FallbackToLocal.RawResponseJson :575` (E-AI-01) ยังอยู่ ✅
- `AiSuggestionController` PaymentType `:140–145` ใช้ `model.PredictAsync` ตรง ๆ และใช้คำตอบจริง — ตัวอย่างที่ถูกของ student-first
- `AzureDiPatternLearner` กัน per-document field (`IsPerDocument :127`) และไม่ทับ `UserCorrection` (`:145`) ✅ — ปัญหาที่รายงาน (T3-06) คือ Azure-vs-Azure
- `OcrFullReviewDistillationModel` เลือกแหล่งสอน "สแกนที่กลายเป็นเอกสาร" — ทิศถูก (แค่อ่านช่องผิดตาราง T3-11)
- ที่รายงานเองแล้วต้องแก้: ตอนแรกผมสงสัยว่า `RecordLineAccountFeedbackAsync` ไม่มีใครเรียก (grep ครั้งแรกไม่เห็น) — เปิดไฟล์แล้ว**มี** จึงไม่ใช่ finding

## §7 ซ้ำกับผลตรวจเดิม (ไม่นับใหม่)
- DocumentTypeClassification ไม่มี call site — **ปิดแล้ว** (`OcrService.cs:832` เรียกจริง) ✅ แต่คำตอบ student ยังถูกทิ้ง (T3-01 = มิติใหม่)
- FieldConfidence 3 ชุดชื่อ — ไม่ตรวจซ้ำ; T3-08 เป็นเคสที่ FieldConfidence ไม่ถูกอัปเดตเมื่อค่าถูกทับ (คนละอาการ)
- VendorKnownGoodCorrector per-document — ปิดแล้ว ✅; T3-06 เป็นเรื่อง "แหล่งสอน" ไม่ใช่ "ช่องที่แก้"
- AiBudgetGuard นับ local rows — ปิดแล้ว ✅ (verify §6)

## §8 ยังไม่ได้ตรวจ
- `OcrMetadataProjectMatcher.cs` เต็มไฟล์ (ดูแค่ gate `:405–435`) · `ProductMatcher.RecordAliasAsync/Match` writer/reader shape
- `GenericFeedbackDistillationModel.PredictAsync` fingerprint (`:184+`) ตรงกับ `UserPromptJson` ของ `DocumentTypeClassifyPrompt` ไหม
  (ถ้า fingerprint รวม raw text ทั้งก้อน exact-match จะไม่มีวัน hit — ต้องเปิด `:184–230` + `WorkflowPrompts.cs:312`)
- `VendorCanonDistillationModel.ExtractVendorKey :202–215` normalize ชื่ออย่างไร (ตัวพิมพ์/ช่องว่าง/คำนำหน้า "บจก.") — ถ้าไม่ normalize จะเรียนช้า
- `SystemOcrKnowledgeSeeder`/`DistillationCorpusSeeder` เนื้อหา seed (ตาราง `SystemOcrCategoryMappings`, `SystemOcrVendorIntelligence`,
  `SystemOcrAssociationRules`) — ครอบอุตสาหกรรมไหนบ้าง / ใช้จริงใน resolver ตัวไหน
- `ExpenseCategoryLearner.RecordAsync/PredictAsync` สูตร confidence (0.55 มาจากไหน) และ federated `_global.PredictAsync :185` k-gate
- `AiFeatureRoutingResolver` — admin ตั้ง per-feature ได้ไหมสำหรับ OCR features 5 ตัว (มี row ใน `AiFeatureRoutingConfigs` ไหม)
- `review-queue.html` — เส้นปิด loop อีกเส้นที่ยังไม่เปิด
- OcrController `/correct-line`, `/match-contact` endpoints → ตรวจว่า UI เรียกจริง (call site ฝั่ง JS)
- `TfIdfNaiveBayesClassifier` train ต่อ request จาก `OcrCategoryMappings` ทุกครั้ง? (`:58` ใน `PredictAsync`) — ถ้าใช่ เป็น N-query ต่อสแกน
