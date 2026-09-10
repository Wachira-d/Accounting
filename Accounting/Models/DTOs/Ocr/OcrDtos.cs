namespace Accounting.Models.DTOs.Ocr;

public record OcrResultResponse(
    Guid Id, string OriginalFileName, string ScanStatus, string? DocumentType, decimal Confidence,
    string? ExtractedVendorName, string? ExtractedVendorTaxId, string? ExtractedDocumentNumber,
    DateTime? ExtractedDate, decimal? ExtractedSubTotal, decimal? ExtractedVatAmount, decimal? ExtractedTotalAmount,
    Guid? MatchedContactId, Guid? CreatedDocumentId, DateTime? ProcessedAt,
    bool IsDuplicate = false, Guid? DuplicateOfScanId = null, string? FileHash = null, string? ProcessingNotes = null,
    string? ExpenseCategory = null,
    OcrSuggestedAccountsDto? SuggestedAccounts = null,
    bool HasWht = false, decimal? WhtRate = null,
    int? PaymentTermsDays = null,
    List<OcrLineItemDto>? ExtractedItems = null,
    string? RawTextContent = null,
    Dictionary<string, double>? FieldConfidence = null,
    string? BuyerName = null,
    string? BuyerTaxId = null,
    OcrDbdInfo? DbdInfo = null,
    // ─── Role inference (Phase 1) ───
    // ScannedDocumentType is the paper that the user actually scanned.
    // TargetDocumentType is what we should create in our books — these differ
    // for the common case of a supplier receipt (scanned=Receipt,
    // target=PaymentVoucher). OurRole is "Buyer" or "Seller". The legacy
    // DocumentType field mirrors ScannedDocumentType for back-compat.
    string? ScannedDocumentType = null,
    string? OurRole = null,
    string? TargetDocumentType = null,
    /// <summary>Which OCR engine produced this result: "AzureDI",
    /// "LocalPython", "EmbeddedTesseract", or "Cached" (duplicate-detection
    /// short-circuit). Useful in the debug panel for accuracy
    /// troubleshooting.</summary>
    string? OcrEngine = null,
    /// <summary>True when at least one line item looks like a Fixed
    /// Asset. UI shows a "Needs Review — Potential Asset" alert; auto-
    /// create is suppressed until the user registers the asset(s) or
    /// dismisses the flag.</summary>
    bool HasPotentialFixedAsset = false,
    /// <summary>JSON array of asset candidate line decisions — schema:
    /// [{lineIndex, description, unitPrice, amount, suggestedCategory,
    /// suggestedUsefulLifeMonths, confidenceScore, reasons}].</summary>
    string? PotentialAssetLinesJson = null,
    /// <summary>Letter grade A/B/C/D plus 0–100 score + color hex —
    /// computed by ScanQualityGrader. Lets the UI render a single
    /// at-a-glance badge instead of forcing the user to interpret six
    /// separate confidence numbers.</summary>
    OcrQualityGradeDto? Quality = null,
    /// <summary>True when Azure DI's styleFont feature flagged
    /// hand-written content on the document. Auto-create is suppressed
    /// when this is true; UI shows a "✋ ตรวจสอบยอดเงิน" alert.</summary>
    bool HasHandwriting = false,
    decimal? HandwritingConfidence = null,
    /// <summary>Suggested entry mode for the review UI: "Stock" when the
    /// vendor has product-alias history + the scan has line items, else
    /// "Expense". Hint only — the user picks the final mode.</summary>
    string? SuggestedEntryMode = null,
    /// <summary>Open Purchase Order numbers of the matched vendor (JSON
    /// array, last 6 months, max 5). Non-null ⇒ UI warns the operator to
    /// book via the PO/receiving function instead of creating fresh.</summary>
    string? OpenPoNumbersJson = null,
    /// <summary>When the operator chose a PO to receive against, this is
    /// the PO document id; the review UI shows a "ผูกกับ PO ..." chip.</summary>
    Guid? LinkedPurchaseOrderId = null,
    string? LinkedPurchaseOrderNumber = null,
    /// <summary>Header discount (ส่วนลด) read off the paper.</summary>
    decimal? ExtractedDiscountAmount = null,
    /// <summary>True เมื่อ AI's GL answer ถูก "นำมาใช้จริง" บน suggestedAccounts
    /// (ผ่าน confidence guard ≥0.70 + อยู่ใน CoA) — ขับป้ายซื่อสัตย์
    /// "🤖 AI แนะนำ". False = ค่าที่แสดงมาจาก local/rule (แม้ AI ถูกเรียกแต่ถูก
    /// ปฏิเสธเพราะ confidence ต่ำ/ไม่อยู่ในผัง).</summary>
    bool GlAccountUsedAi = false,
    /// <summary>ผัง GL ที่ AI เสนอ (primary) — เก็บไว้แม้ถูกปฏิเสธ เพื่อให้ UI
    /// โชว์ "AI เสนอ X (มั่นใจ Y%) แต่ระบบใช้ Z แทน" + ให้ผู้ใช้กดเลือกของ AI
    /// ได้เอง. null = AI ไม่ถูกเรียก หรือไม่เสนอผัง.</summary>
    string? GlAccountAiSuggestedCode = null,
    string? GlAccountAiSuggestedName = null,
    decimal? GlAccountAiConfidence = null,
    /// <summary>§86/4 รหัสสาขา + ที่อยู่ที่อ่านได้จากใบ (กฎเหล็ก #3 — ต้องส่งออกมา
    /// ให้หน้า review แสดง/แก้ได้ ไม่ใช่ปล่อยว่างแล้วไปหยิบจาก Contact ซึ่งอาจเป็น
    /// สาขาอื่น). "00000" = สำนักงานใหญ่</summary>
    string? VendorBranchCode = null,
    string? VendorAddress = null,
    string? BuyerBranchCode = null,
    string? BuyerAddress = null,
    /// <summary>คำเตือน "ข้อมูลตามสรรพากรยังไม่ครบ" บนการ์ดผลสแกน — คำนวณที่
    /// เซิร์ฟเวอร์โดย <c>OcrScanComplianceEvaluator</c> หน้าเว็บมีหน้าที่ "แสดง"
    /// อย่างเดียว ห้ามคำนวณเอง (เดิม document-scan.html คัดลอกกฎไปเขียนใน JS
    /// แล้วไม่เคยแก้ตาม ⇒ เตือนผิดบนใบที่กระดาษมีเลขผู้ซื้อครบ). null =
    /// เส้นทางที่ยังไม่ได้ประเมิน (ไม่ใช่ "ไม่มีปัญหา") — UI อย่าเพิ่งวาดอะไร</summary>
    List<OcrScanIssueDto>? ComplianceIssues = null,
    /// <summary>แผนที่ "ชนิดเอกสาร → ฝั่ง" ที่สร้างจาก <c>Helpers.DocumentSide</c>
    /// ค่าเป็น <c>"Sales"</c> | <c>"Purchase"</c> | <c>"Both"</c> (Both = ต้องดู
    /// <c>OurRole</c> ประกอบ เช่น CN/DN/ใบส่งของ)
    ///
    /// <para>มีไว้เพื่อให้หน้าเว็บ **เลิกถือลิสต์ของตัวเอง** — เดิม
    /// <c>document-scan.html</c> มี <c>salesTypes</c> เป็นสำเนามือที่ตัด CN/DN
    /// ออกจากฝั่งขายเสมอและนับใบส่งของเป็นฝั่งขายเสมอ ⇒ ใบลดหนี้<b>ขาย</b>
    /// เปิดฟอร์มรายจ่าย และใบส่งของจากผู้ขายเปิดฟอร์มรายได้
    /// (<c>DocumentSide.cs</c> ระบุชื่อสำเนานี้ไว้ในหมายเหตุตั้งแต่แรก)</para>
    ///
    /// <para>null = เซิร์ฟเวอร์รุ่นเก่า — หน้าเว็บใช้ fallback ของตัวเอง</para></summary>
    Dictionary<string, string>? DocumentSideMap = null,
    /// <summary>รายการในใบนี้มาจากการ "แตกบรรทัดด้วย AI" หรือไม่
    /// (<c>AiFeatureKey.OcrLineItemSplit</c> — ทำงานเมื่อ engine ไม่คืนตาราง
    /// รายการมาเลย) ⇒ UI ติดป้าย "🤖 AI แตกรายการให้ กรุณาตรวจ" ให้ซื่อสัตย์
    /// ตามกฎเหล็ก #1 (ป้าย "🤖 AI แนะนำ" เฉพาะตอนเรียก AI จริง)</summary>
    bool LineSplitUsedAi = false,
    /// <summary>ชนิดเอกสารที่จะสร้างมาจากการ **เรียก AI จริง** หรือไม่ (กฎเหล็ก #1 ข้อ
    /// "ป้ายซื่อสัตย์") — <c>false</c> = กติกา/บทบาทบนกระดาษ/นักเรียนเป็นคนตอบ
    ///
    /// <para>⚠️ ธงนี้มีบนแถวสแกนและถูกเซ็ตมาตลอด แต่<b>ไม่เคยอยู่ใน DTO</b> ⇒ หน้า review
    /// ไม่มีทางติดป้ายให้ช่องที่ผู้ใช้เห็นบ่อยที่สุดช่องหนึ่ง (ผลตรวจ 2026-09-06 · T3-12)</para></summary>
    bool TargetDocTypeUsedAi = false,
    /// <summary>สกุลเงินที่อ่านได้จากกระดาษ (null = ไม่พบสัญลักษณ์/รหัสสกุล → THB)
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ OCR 2026-09-06 · T4-08): เส้น "สร้างทันที" อ่านสกุลเงิน
    /// จาก <c>InferCurrency(RawTextContent)</c> แต่ DTO ไม่มีช่องนี้ ⇒ เส้น
    /// "📝 แก้ในฟอร์มก่อน" (handoff) ได้ THB เสมอ — <b>ใบเดียวกัน สองคำตอบ</b>
    /// (invoice USD ถูกบันทึกเป็นบาท ตัวเลขเท่าเดิมแต่ความหมายผิดหลายสิบเท่า)</para></summary>
    string? Currency = null,
    /// <summary>FK ของแถว feedback ผัง GL — หน้าเว็บส่งต่อไปกับฟอร์มเอกสาร เพื่อให้
    /// ตอนผู้ใช้ยืนยัน/แก้ผัง ระบบสอน local model กลับได้ (กฎเหล็ก #1 ขั้น CAPTURE)
    ///
    /// <para>⚠️ ที่มา (T4-11): ทั้งฝั่งผลิต (<c>document-scan.html</c>) และฝั่งบริโภค
    /// (<c>documents.html</c>) อ้างชื่อนี้อยู่แล้ว แต่ <b>DTO ตรงกลางไม่มีช่อง</b>
    /// ⇒ การแก้ผังบัญชีในฟอร์มไม่เคยถูกบันทึกเป็นตัวอย่างสอนเลย</para></summary>
    Guid? GlAccountAiFeedbackId = null,
    /// <summary>อัตราหัก ณ ที่จ่ายที่ **กฎหมายกำหนด** สำหรับหมวดรายจ่ายนี้ (ท.ป.4/2528)
    /// — ข้อเสนอ ไม่ใช่ค่าที่ระบบตั้งให้ (<c>WhtRate</c> = ยอดที่พิมพ์บนกระดาษ)</summary>
    decimal? SuggestedWhtRate = null,
    /// <summary>รหัสประเภทเงินได้ ม.40 — บังคับก่อนออก 50 ทวิ/ภ.ง.ด.3/53 (T4-06)</summary>
    string? WhtIncomeTypeCode = null,
    /// <summary>หมายเหตุ/เหตุผลทางธุรกิจที่ผู้ใช้เขียน (§65 ตรี(3)/(14)) — ต้อง echo
    /// กลับมาให้ฟอร์ม hydrate ได้ ไม่งั้น "เปิดแก้แล้วบันทึก ค่าหายเงียบ ๆ" (T4-10)</summary>
    string? UserNotes = null,
    /// <summary>สมุดที่มาของค่ารายช่อง (JSON จาก <c>Helpers/OcrFieldArbiter</c>) —
    /// หน้า review ใช้ตอบคำถาม "ค่านี้มาจากไหน" ให้ผู้ใช้ (สถาปัตยกรรมเป้าหมาย D1)
    ///
    /// <para><c>null</c> = สแกนรุ่นก่อนมีระบบนี้ ⇒ หน้าเว็บต้อง<b>ไม่วาดอะไร</b>
    /// (ไม่ใช่วาดว่า "ไม่มีที่มา" ซึ่งเป็นคนละความหมาย)</para></summary>
    string? FieldDecisionsJson = null,
    // ─── ใบต้นทางทุกชนิด (Helpers/OcrPredecessorMatcher) ───
    Guid? LinkedPredecessorDocumentId = null,
    string? LinkedPredecessorNumber = null,
    string? LinkedPredecessorType = null,
    string? PredecessorLinkReason = null,
    /// <summary>JSON ของ <c>List&lt;PredecessorCandidateDto&gt;</c> เรียงตามคะแนน — <c>null</c> = ยังไม่ได้วิเคราะห์
    /// (สแกนรุ่นก่อน) · <c>[]</c> = วิเคราะห์แล้วไม่พบ · หน้าเว็บวาดปุ่ม "เลือกใบต้นทาง" เฉพาะเมื่อมีรายการ</summary>
    string? PredecessorCandidatesJson = null);

/// <summary>คำเตือน 1 ข้อบนการ์ดผลสแกน — <c>Severity</c> = "error" | "warn"</summary>
public record OcrScanIssueDto(string Severity, string Message);

/// <summary>One open PO of the matched vendor — what the picker modal
/// renders. Lines come back inline so the operator can map OCR ↔ PO line
/// without a second roundtrip.</summary>
public record OpenPurchaseOrderDto(
    Guid Id, string DocumentNumber, DateTime DocumentDate, string Status,
    decimal TotalAmount, IReadOnlyList<OpenPurchaseOrderLineDto> Lines);

public record OpenPurchaseOrderLineDto(
    Guid Id, int LineOrder, string Description, decimal Quantity,
    decimal UnitPrice, decimal Amount, Guid? AccountId, string? AccountCode);

/// <summary>ผลพรีวิวบรรทัดที่**เซิร์ฟเวอร์**สร้างจากผลสแกน (ตัวสร้างเดียวกับปุ่ม “สร้างเอกสาร” —
/// <c>BuildScanLinesAsync</c>) ให้ปุ่ม “แก้ในฟอร์มก่อน” นำไปเติมฟอร์ม **โดยไม่คำนวณเอง**
///
/// <para>ที่มา (2026-09-10): หน้า document-scan.html มีสำเนา JS ของตรรกะกระทบยอดที่ให้คำตอบ
/// คนละแบบกับเซิร์ฟเวอร์บนกระดาษใบเดียวกัน (ใบลักกี้เวย์: JS แต่งส่วนลดท้ายบิล 87.28 ที่ไม่มี
/// บนกระดาษ) — กลไกเดียวกับ <c>complianceIssues</c>/<c>documentTitle</c>: เซิร์ฟเวอร์คำนวณ
/// หน้าเว็บแสดงอย่างเดียว</para>
///
/// <param name="Notes">ข้อความ [Σ]/[Σ-GAP]/[Σ-SWAP] ที่ตัวสร้างเขียนระหว่างพรีวิว (ไม่ persist)</param></summary>
public record OcrLinePreviewResponse(
    string TargetDocumentType,
    bool IsSalesSide,
    bool PricesIncludeVat,
    decimal HeaderSubTotal,
    decimal HeaderVat,
    decimal HeaderTotal,
    decimal HeaderWht,
    IReadOnlyList<OcrLinePreviewLineDto> Lines,
    string? Notes);

public record OcrLinePreviewLineDto(
    string Description, decimal Quantity, string? Unit, decimal UnitPrice,
    decimal DiscountPercent, decimal DiscountAmount, decimal Amount,
    decimal VatRate, decimal VatAmount, decimal WithholdingTaxRate,
    string? AccountCode, string? ProductCode, Guid? ProjectId, Guid? SourceLineId);

/// <summary>Body of POST /ocr/{scanId}/link-po — the chosen PO plus the
/// per-OCR-line mapping (line index → PO line id). Unmapped indices are
/// omitted; nulls explicitly clear a mapping.</summary>
public record LinkPurchaseOrderRequest(
    Guid PurchaseOrderId,
    Dictionary<int, Guid?>? LineMappings);

/// <summary>เอกสารในระบบที่อาจเป็นใบต้นทางของสแกน — ผลจาก <c>Helpers/OcrPredecessorMatcher</c>
/// (เซิร์ฟเวอร์ตัดสิน Strength/Reason · หน้าเว็บแสดงอย่างเดียว)</summary>
public record PredecessorCandidateDto(
    Guid DocumentId, string DocumentType, string DocumentNumber, DateTime DocumentDate,
    decimal TotalAmount, decimal BalanceDue, string Strength, int Score, string Reason);

public record LinkPredecessorRequest(Guid DocumentId);

/// <param name="Reasons">เหตุผลว่าทำไมได้เกรดนี้ — <c>ScanQualityGrader</c>
/// สร้างรายการนี้ให้ครบทุกครั้งอยู่แล้ว แต่ DTO เดิม<b>ทิ้งทั้งก้อน</b> ⇒
/// ผู้ใช้เห็นตัวอักษร "D" โดยไม่รู้ว่าเพราะอะไรและต้องทำอะไรต่อ (เอกสารของ
/// grader เขียนว่า D = "probably re-scan" แต่การ์ดไม่เคยบอกว่าให้ถ่ายใหม่)</param>
/// <param name="Advice">คำแนะนำสั้น ๆ ตามเกรด — ผู้ใช้ต้องรู้ "ต้องทำอะไรต่อ"
/// ไม่ใช่แค่ "คะแนนเท่าไร" (กติกาเดียวกับ Action ของกฎ RD compliance)</param>
public record OcrQualityGradeDto(
    string Letter, int Score, string Color,
    IReadOnlyList<string>? Reasons = null,
    string? Advice = null);

public record OcrDbdInfo(
    bool LookupAttempted,
    bool Matched,
    string? CanonicalName = null,
    string? Address = null,
    string? JuristicType = null,
    string? Status = null);

public record OcrSuggestedAccountsDto(
    string? DebitAccountCode, string? DebitAccountName,
    string? CreditAccountCode, string? CreditAccountName,
    string? VatAccountCode = null, string? VatAccountName = null);

public record OcrLineItemDto(
    string? Description, decimal? Quantity, decimal? UnitPrice, decimal? Amount,
    string? SuggestedAccountCode = null,
    /// <summary>Per-line project charge — populated by the user in
    /// the OCR review UI before document creation. When set, flows
    /// to DocumentLine.ProjectId so each line books cost against the
    /// right project. Null = use document-level project (default).</summary>
    Guid? ProjectId = null,
    string? ProjectName = null,
    /// <summary>Unit detected from the description (ถุง/เส้น/กล่อง…).</summary>
    string? Unit = null,
    /// <summary>อัตรา VAT ของบรรทัดนี้ — <c>7</c> เสียภาษี · <c>0</c> อัตราศูนย์ ·
    /// <c>-1</c> ยกเว้น §81 (convention เดียวกับ <c>DocumentLine.VatRate</c>)
    ///
    /// <para>E-OCR-01: ต้องส่งถึงหน้า review ให้ผู้ใช้เห็น/แก้ได้ — ไม่งั้นบรรทัด
    /// ที่ระบบเดาผิดจะไหลเข้าเอกสารโดยไม่มีใครทัดทาน แล้วไปโผล่ผิดคอลัมน์ใน
    /// รายงานภาษีซื้อ §87</para></summary>
    decimal? VatRate = null,
    /// <summary>ภาษีของบรรทัดนี้หลังเฉลี่ยยอดจากหัวใบ (เฉพาะบรรทัดที่อัตรา &gt; 0)</summary>
    decimal? VatAmount = null);

public record OcrCreditPurchaseRequest(int Pages);

public record OcrCorrectionRequest(
    string? DocumentType = null,
    string? VendorName = null,
    string? VendorTaxId = null,
    string? DocumentNumber = null,
    DateTime? DocumentDate = null,
    decimal? SubTotal = null,
    decimal? VatAmount = null,
    decimal? TotalAmount = null,
    string? ExpenseCategory = null,
    string? DebitAccountCode = null,
    string? CreditAccountCode = null,
    bool? HasWht = null,
    decimal? WhtRate = null,
    /// <summary>ประเภทเงินได้ ม.40 ที่ผู้ใช้เลือก — ต้องส่งกลับมาได้ ไม่งั้น 50 ทวิ
    /// และ ภ.ง.ด.3/53 ไม่มีข้อมูลนี้ (T4-06)</summary>
    string? WhtIncomeTypeCode = null,
    // Phase-1 role-inference correction: when user changes the inferred
    // "เอกสารที่จะสร้าง" dropdown, this string carries the new value so the
    // backend can both update the scan record AND train VendorIntelligence
    // to suggest the same target for this vendor next time.
    string? TargetDocumentType = null,
    /// <summary>
    /// บทบาทของเราบนกระดาษ — "Buyer" | "Seller"
    ///
    /// <para>⚠️ เดิม<b>ไม่มีช่องนี้เลย</b> และหน้า review ก็แสดงบทบาทเป็นข้อความ
    /// อ่านอย่างเดียว ⇒ เมื่อระบบอนุมานผิด (ซึ่งเกิดได้จริง — 50 ทวิ, ใบที่ OCR
    /// อ่านเลขภาษีไม่ออก, ใบขายของเราเองที่สแกนกลับเข้ามา) <b>ผู้ใช้แก้ไม่ได้
    /// และระบบไม่มีทางเรียนรู้</b> เพราะไม่มีทั้ง field และ learner ใด ๆ ที่เก็บ
    /// ความจริงข้อนี้</para>
    /// </summary>
    string? OurRole = null,
    string? VendorBranchCode = null,
    string? BuyerTaxId = null,
    string? BuyerBranchCode = null,
    /// <summary>ที่อยู่ผู้ขาย/ผู้ซื้อ + ชื่อผู้ซื้อ + เครดิตเทอม — รายการบังคับ
    /// ตาม §86/4 ที่ <b>ไม่เคยมีทางแก้จากหน้า review เลย</b> (ผลตรวจ E-OCR-05)
    ///
    /// <para>OCR อ่านที่อยู่เพี้ยนเป็นเรื่องปกติ (ตัวเล็ก หลายบรรทัด มีตราประทับทับ)
    /// แต่ทางเดียวที่ผู้ใช้แก้ได้คือไปแก้ที่ <c>Contact</c> ซึ่ง<b>เปลี่ยนใบเก่า
    /// ทุกใบของผู้ขายรายนั้นตามไปด้วย</b> — ที่อยู่บนใบกำกับต้องเป็นที่อยู่
    /// ณ วันที่ออกใบ ไม่ใช่ที่อยู่ล่าสุด</para></summary>
    string? VendorAddress = null,
    string? BuyerName = null,
    string? BuyerAddress = null,
    int? PaymentTermsDays = null,
    /// <summary>หมายเหตุ/เหตุผลทางธุรกิจที่ผู้ใช้พิมพ์เอง (เช่น "เดินทางไปพบ
    /// ลูกค้า") — ไหลต่อไปเป็น <c>Document.Notes</c> ของใบที่สร้างจากสแกนนี้
    ///
    /// <para>⚠️ หน้าเบิกค่าใช้จ่ายบนมือถือมีช่องนี้มาตลอด **แต่ไม่เคยส่งค่าไปไหน**
    /// (อ่านใส่ตัวแปรแล้วทิ้ง) ⇒ ผู้ใช้พิมพ์เหตุผลแล้วหายเงียบ. ไม่ใช่แค่เรื่อง
    /// ความสะดวก — §65 ตรี(3)/(14) ให้รายจ่ายที่พิสูจน์ไม่ได้ว่าเกี่ยวกับกิจการ
    /// เป็น**รายจ่ายต้องห้าม** เหตุผลที่ผู้เบิกเขียนคือหลักฐานชิ้นแรกของเรื่องนี้</para>
    /// </summary>
    string? Notes = null);

/// <summary>
/// Request to record a Journal Entry directly from a scan — the "บันทึก JE
/// เท่านั้น" path, used when the company already issued the real document in
/// an external system and only needs the GL effect recorded here (no
/// business document created). DebitAccountCode / CreditAccountCode are the
/// two primary account codes the user picks in the review modal; the service
/// auto-adds balanced VAT / WHT lines from the extracted amounts when those
/// accounts resolve. EntryDate defaults to the scan's document date.
/// </summary>
public record CreateJeFromScanRequest(
    string DebitAccountCode,
    string CreditAccountCode,
    string? Description = null,
    DateTime? EntryDate = null,
    bool PostVat = true,
    bool PostWht = true,
    // ผู้ใช้ยืนยันแล้วว่าเป็นคนละใบจริง แม้เลขที่จะซ้ำ — ทรงเดียวกับเส้นสร้าง
    // เอกสาร (ผลตรวจ T1-14: เส้น JE ตรงเดิมไม่มีด่านกันซ้ำเลย)
    bool AllowDuplicate = false);
