# OCR-BRIEF — โจทย์ "อัพเอกสาร = มีนักบัญชีที่เก่งที่สุดในโลกทำเอกสารให้"

โจทย์จากเจ้าของโปรเจกต์ (2026-09-05):
> วิเคราะห์กระบวนการ OCR จนสร้างเอกสาร ตั้งทีมที่เก่งทุกด้านในโลกมาช่วยกันคิด โต้เถียง ออกแบบ
> พัฒนา ใช้ AI ร่วม เพื่อให้ระบบทำงานได้ดีที่สุด มีประสิทธิภาพที่สุด **แค่อัพเอกสารไป ก็เหมือนมี
> นักบัญชีที่เก่งที่สุดในโลกมาทำเอกสารให้ ถูกต้องครบถ้วนที่สุด**

## กติกาทีม (เหมือนรอบ ERP — ดู BRIEF.md ข้อ 1–6 ใช้ทั้งหมด)
1. **เปิดไฟล์ก่อนเชื่อ** — ทุกข้อต้องมี file:line ที่เปิดดูจริง ห้ามอ้างจากชื่อเมธอด/คอมเมนต์
   ("doc-comment ที่บอกว่าป้องกันแล้ว ≠ มีด่านจริง" — grep call site เสมอ)
2. **แยก 3 ชั้น**: 🔴 บั๊ก (ผลลัพธ์ผิด/ผิดกฎหมาย) · 🟠 ช่องว่าง (นักบัญชีเก่งจะทำ แต่ระบบไม่ทำ)
   · 🔵 ข้อเสนอออกแบบ (ต้องมีเหตุผล + ทางเลือกที่ปฏิเสธพร้อมเหตุผล)
3. **โต้เถียงในรายงาน**: ทุกข้อเสนอต้องมี "ฝ่ายค้านจะว่าอย่างไร" และคำตอบ
4. **เขียนรายงานทีละส่วนทันที** (append ลง `erp-review/2026-09-05/ocr-report-T<n>.md`
   ทุก ~10 นาที) — rate limit ตัดได้ทุกเมื่อ ผลที่อยู่ในหัวจะหาย
5. ห้ามแก้โค้ด — เสนอ patch เป็นข้อความ (main agent จะ verify แล้วแก้เฉพาะ S-size ที่ยืนยันแล้ว)
6. ห้ามรายงานซ้ำสิ่งที่ปิดแล้ว: `SYSTEM_REVIEW_2026-09.md` ทีม E (E-OCR-01/02, E-AI-01/06 ✅),
   `ERP_REVIEW_2026-09-05.md` E-09, CLAUDE.md บทเรียน OCR ทั้งหมด (EAN-13 vs เลขภาษี ·
   VendorKnownGoodCorrector · DBD key guard · OcrScanSnapshot · BranchCodeExtractor เส้น Tesseract ·
   FieldConfidence 3 ชุดชื่อ · learner write-only · DocumentTypeClassification ไม่มี call site)
   — ให้**ตรวจว่าที่บอกว่าแก้แล้ว ยังแก้อยู่จริงไหม** ได้ แต่ห้ามรายงานเป็นของใหม่

## แผนที่ไปป์ไลน์ (ground truth ที่ main agent อ่านแล้ว — เริ่มจากตรงนี้)
`Services/Implementations/OcrService.cs` (7,356 บรรทัด):
- `ScanAsync` :124–2383 (**2,260 บรรทัดในเมธอดเดียว**) ลำดับ:
  cache/duplicate (:150–209 ผ่าน `Helpers/OcrScanSnapshot`) → Tier 0 e-Tax XML (:290) →
  Tier 1 Azure DI (:324–400, `ExtractWithAzureDiAsync` :2620, `MapAzureDiToExtractedDataAsync` :2666)
  → Tier 2 Python local (:404–440) → Tier 3 Embedded Tesseract + PDF text layer (:450–540,
  `ParseThaiDocument` :4078) → `_knownGoodCorrector` (:429/:525) → tier ledger (:554) →
  `OcrConfidenceGateway` (:562) → เลขที่เอกสารซ้ำ/ต่อกัน (:602) → `EnrichFromRawText` (:630/:6962)
  → `ApplyLearnedPatternsAsync` (:644/:6789) → `ApplyZoneAnalysisFallbackAsync` (:651/:6903) →
  `TrySplitLineItemsWithAiAsync` (:677/:6830, ผ่าน `Helpers/OcrLineSplitGuard`) → external amount
  overrides (:679) → project allocation (:690) → `SanitizeVatSplitArtifacts` (:712/:3110) →
  persist items (:733) → `OcrDocumentRoleInferrer` (:746–800) → AI doc-type classification เฉพาะเคส
  คลุมเครือ (:806–890) → duplicate warning (:897) → `ProhibitedInputVatScreener` §82/5 (:910–940)
  → persist branch/buyer (:955–985) → global rules → NaiveBayes (:1046) → product xref (:1074) →
  `_categoryLearner` (:1089) → `_vendorIntel.PredictAsync` (:1110–1230) → credit terms (:1235) →
  pattern confidence boost (:1262) → DeepSeek GL classification teacher→student (:1314) →
  ใบรับรองแทนใบเสร็จ (:1453) → มัดจำ (:1477) → re-sync (:1495) → credit account 3-tier (:1516) →
  content fingerprint dedup (:1671) → buyer tax id from paper (:1733) → DBD (:1763/:3911) →
  contact match by tax id/name (:1775–1868, ⚠️ name match :1815) → AI vendor canonicalisation
  (:1869) → contact auto-create 4 branches (:1931–2126) → PO check (:2127) → stock vs expense
  (:2185) → fixed asset detect (:2208) → `AutoCreateDocumentAsync` (:2338/:6043) → `AttachComplianceAsync`
- `CreateDocumentFromScanAsync` :4446–5276: duplicate gate (:4467) → PO linkage (:4522) →
  type-dependent data (:4555) → WHT/subtotal prefill (:4704) → debit GL (:4745) → credit/payment
  source (:4768) → seller tax invoice §86/4 (:4799) → §82/5 (:4805) → branch order (:4844) →
  line reconciliation 4 cases (:4970) → per-line VAT rate (:5027) → Σ gate (:5052) → 50 ทวิ (:5231)
- `SubmitCorrectionAsync` :3517 (ปิด loop) · `RepopulateDocumentLinesFromScanAsync` :5304 ·
  `CreateJournalEntryFromScanAsync` :5417 · `MatchContactAsync` :5790 · `ModifyExtractedLineAsync` :5721
  · `RegisterAssetFromScanAsync` :6136 · `LinkPurchaseOrderAsync` :7165
- `Controllers/OcrController.cs` (1,621) · `Services/Ai/OcrAiAugmenter.cs` (658) ·
  `Services/Implementations/Ocr/*` ~50 ไฟล์ (SmartFieldExtractor 931 · OcrDocumentRoleInferrer 725 ·
  VendorIntelligenceService 966 · SystemOcrKnowledgeSeeder 1308 · AzureDocumentIntelligenceService 788 ·
  EtaxPdfXmlExtractor 524 · RdComplianceValidator 447 · ProductMatcher 671 · ExpenseCategoryResolver 477 ·
  OcrConfidenceGateway 268 · OcrScanComplianceEvaluator 135 · ProhibitedInputVatScreener 131 ·
  DocumentZoneAnalyzer · BranchCodeExtractor · FieldPatternLibrary · VendorKnownGoodCorrector)
- Distillation: `Services/Ai/Distillation/OcrFullReviewDistillationModel.cs` · `GlAccountDistillationModel.cs`
  · registrations `Program.cs:531–590` · `AiFeatureKey` ใน `Models/Enums/AllEnums.cs`
- UI: `wwwroot/pages/document-scan.html` (4,316) · `wwwroot/pages/review-queue.html`
- Python: `ocr-service/` (PaddleOCR+EasyOCR) · Tests: `Accounting.Tests/*Ocr*`, `*Extractor*`, `*Distill*`

## คำถามที่ทีมต้องตอบ (ทุกทีม)
- นักบัญชีที่เก่งที่สุดในโลกเห็นกระดาษใบนี้แล้ว "ตัดสิน" อะไรบ้าง (ลิสต์การตัดสินใจครบ) —
  ระบบตัดสินข้อไหน / ข้อไหนปล่อยให้ผู้ใช้ / ข้อไหนตัดสินผิดเงียบ ๆ
- กฎเหล็ก #3: field ไหนยัง null/ว่างถึงมือผู้ใช้ · กฎเหล็ก #1: จุดไหนเรียก AI แล้วไม่ CAPTURE/DISTILL
  หรือมี student ที่ไม่มีใครป้อน/ไม่มีใครอ่าน
- ค่า default ที่แต่งขึ้น (`?? "00000"`, `?? 7`, `?? today`, PaymentVoucher default) อยู่ที่ไหนอีก
- "แค่อัพแล้วจบ" ตอนนี้ต้องกดกี่ครั้ง กรอกอะไรเอง — ตัวเลขจริงจาก UI
