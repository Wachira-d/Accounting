namespace Accounting.Models.Entities;

// ===== AI-Powered Features =====

/// <summary>
/// กฎ Auto-categorization สำหรับ AI
/// </summary>
public class AutoCategorizationRule : TenantEntity
{
    public string RuleName { get; set; } = "";
    public string MatchType { get; set; } = "Contains";   // Contains, StartsWith, Regex, AI
    public string MatchField { get; set; } = "Description"; // Description, Reference, Payee, Amount
    public string? MatchPattern { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public Guid? TargetAccountId { get; set; }            // auto-assign GL account
    public Guid? TargetDimensionId { get; set; }          // auto-assign cost center
    public string? TargetCategory { get; set; }
    public int Priority { get; set; } = 0;
    public int TimesApplied { get; set; } = 0;
    public decimal ConfidenceThreshold { get; set; } = 0.8m;
    public bool IsActive { get; set; } = true;
    public bool IsAiGenerated { get; set; } = false;      // AI สร้างจาก pattern
}

/// <summary>
/// ผลการจัดหมวดหมู่อัตโนมัติ
/// </summary>
public class CategorizationResult : TenantEntity
{
    public string EntityType { get; set; } = "";          // BankTransaction, ExpenseClaim, Document
    public Guid EntityId { get; set; }
    public Guid? SuggestedAccountId { get; set; }
    public Guid? SuggestedDimensionId { get; set; }
    public string? SuggestedCategory { get; set; }
    public decimal Confidence { get; set; }
    public string? ReasoningJson { get; set; }            // AI reasoning
    public bool IsAccepted { get; set; } = false;
    public bool IsRejected { get; set; } = false;
    public Guid? AcceptedByUserId { get; set; }
    public Guid? AppliedRuleId { get; set; }
}

/// <summary>
/// Anomaly Detection Log
/// </summary>
public class AnomalyDetection : TenantEntity
{
    public string AnomalyType { get; set; } = "";         // UnusualAmount, DuplicateEntry, OutOfPattern, MissingEntry, BalanceDiscrepancy
    public string Severity { get; set; } = "Medium";       // Low, Medium, High, Critical
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Description { get; set; } = "";
    public string? DetailJson { get; set; }
    public decimal? ExpectedValue { get; set; }
    public decimal? ActualValue { get; set; }
    public decimal? DeviationPercent { get; set; }
    public string Status { get; set; } = "Open";           // Open, Acknowledged, Resolved, FalsePositive
    public string? ResolvedBy { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    // AI-generated explanation — populated lazily by ExplainAnomalyAsync
    // on first /explain call. Cached so re-views don't pay for the
    // same explanation; user-confirm-or-override updates the feedback
    // row, not this column.
    public string? AiVerdict { get; set; }
    public decimal? AiConfidence { get; set; }
    public string? AiReasoning { get; set; }
    public string? AiSuggestedActionsJson { get; set; }
    public string? AiRisksJson { get; set; }
    public Guid? AiFeedbackId { get; set; }
    public DateTime? AiExplainedAt { get; set; }
}

/// <summary>
/// Cash Flow Forecast
/// </summary>
public class CashFlowForecast : TenantEntity
{
    public string Name { get; set; } = "";
    public DateTime ForecastDate { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string ForecastMethod { get; set; } = "Historical"; // Historical, AI, Manual, Hybrid
    public decimal OpeningBalance { get; set; }
    public decimal ProjectedInflows { get; set; }
    public decimal ProjectedOutflows { get; set; }
    public decimal ProjectedClosingBalance { get; set; }
    public decimal? ActualClosingBalance { get; set; }
    public decimal AccuracyPercent { get; set; }
    public string? DetailJson { get; set; }               // weekly/daily breakdown

    public ICollection<CashFlowForecastLine> Lines { get; set; } = new List<CashFlowForecastLine>();
}

public class CashFlowForecastLine : TenantEntity
{
    public Guid CashFlowForecastId { get; set; }
    public CashFlowForecast Forecast { get; set; } = null!;
    public DateTime PeriodDate { get; set; }
    public string Category { get; set; } = "";            // Sales, Purchases, Payroll, Tax, Loan, Other
    public string FlowType { get; set; } = "Inflow";      // Inflow, Outflow
    public decimal ProjectedAmount { get; set; }
    public decimal? ActualAmount { get; set; }
    public decimal Confidence { get; set; }
    public string? Source { get; set; }                   // InvoiceDue, RecurringPayment, Payroll, Historical
}

// ===== Document OCR =====

/// <summary>
/// ผลการสแกน OCR เอกสาร
/// </summary>
public class OcrScanResult : TenantEntity
{
    public Guid? FileAttachmentId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string ScanStatus { get; set; } = "Pending";   // Pending, Processing, Completed, Failed
    public string? DocumentType { get; set; }              // Invoice, Receipt, TaxInvoice, WHT
    public decimal Confidence { get; set; }

    // Number of times this scan has been retried by the user/admin.
    // Increments on retry; bounded by SiteSettings.OcrMaxRetriesPerScan.
    // Retries do NOT consume additional quota (quota was charged on initial scan).
    public int RetryCount { get; set; } = 0;

    // Content-based fingerprint for cross-format duplicate detection.
    // Computed AFTER extraction as SHA256 of:
    //   "{VendorTaxId}|{DocumentNumber}|{DocumentDate:yyyy-MM-dd}|{TotalAmount:0.00}"
    // Catches the case where same invoice is uploaded as JPG one time and PDF another —
    // file hash differs but the content fingerprint matches.
    public string? ContentFingerprint { get; set; }

    // Extracted data
    public string? ExtractedVendorName { get; set; }
    public string? ExtractedVendorTaxId { get; set; }
    public string? ExtractedDocumentNumber { get; set; }
    public DateTime? ExtractedDate { get; set; }
    public decimal? ExtractedSubTotal { get; set; }
    public decimal? ExtractedVatAmount { get; set; }
    public decimal? ExtractedTotalAmount { get; set; }
    /// <summary>Header discount (ส่วนลด) read off the paper.</summary>
    public decimal? ExtractedDiscountAmount { get; set; }
    public string? ExtractedItemsJson { get; set; }       // JSON of line items

    /// <summary>Business-flow hint: which entry mode the operator should pick
    /// for this scan — "Stock" (vendor has product-alias history + line items)
    /// or "Expense" (everything else). Suggestion only; user decides.</summary>
    public string? SuggestedEntryMode { get; set; }

    /// <summary>JSON array of open Purchase Order numbers found for the matched
    /// vendor at scan time (last 6 months, max 5). Non-null ⇒ the review UI
    /// warns "this vendor has open POs — book via the PO function instead".</summary>
    public string? OpenPoNumbersJson { get; set; }

    /// <summary>Operator's chosen Purchase Order to receive this scan against
    /// (the "ฟังก์ชันชื่อแทน / รับตาม PO" function in the business flow). When
    /// set, CreateDocumentFromScanAsync inherits the PO's GL accounts on
    /// matched lines and stamps the new Purchase Invoice as RelatedDocumentId
    /// = PO id, so the receiving ties back to the order.</summary>
    public Guid? LinkedPurchaseOrderId { get; set; }

    /// <summary>Denormalised PO DocumentNumber so list endpoints can render
    /// the "ผูกกับ PO {n}" chip without an extra join per row. Stays in sync
    /// because PO numbers don't change post-creation.</summary>
    public string? LinkedPurchaseOrderNumber { get; set; }

    /// <summary>JSON map of {ocrLineIndex → poLineId} the operator confirmed
    /// when linking to a PO. Drives per-line GL inheritance + ProductAlias
    /// learning on document creation.</summary>
    public string? PoLineMappingsJson { get; set; }

    // ─── ใบต้นทางทุกชนิด (2026-09-10 — ทั่วไปกว่า PO) ───
    // ผลของ Helpers/OcrPredecessorMatcher: เอกสารในระบบที่สแกนใบนี้ "ต่อเนื่อง" มา (PO/GRN →
    // ใบซื้อ · ใบเสนอราคา/ใบวางบิล/ใบส่งของ → ใบแจ้งหนี้ · ใบแจ้งหนี้/ใบกำกับ → ใบเสร็จ ฯลฯ)
    // ผูกอัตโนมัติเมื่อชัด (เลขที่บนกระดาษ / ยอดตรงใบเดียว) ไม่งั้นเก็บผู้สมัครไว้ให้คนเลือก.
    // เมื่อต้นทางเป็น PO จะตั้ง LinkedPurchaseOrderId ด้วย (เส้นสืบทอด GL รายบรรทัดเดิม)
    public Guid? LinkedPredecessorDocumentId { get; set; }
    public string? LinkedPredecessorNumber { get; set; }
    public string? LinkedPredecessorType { get; set; }        // enum-string DocumentType
    public string? PredecessorLinkReason { get; set; }        // "auto: …" / "user"
    public string? PredecessorCandidatesJson { get; set; }    // List<PredecessorCandidateDto>

    /// <summary>Structured metadata an external system (e.g. a project /
    /// materials-ordering system) sends ALONGSIDE the uploaded document.
    /// Lists each ordered item with its originating project/order so we can
    /// auto-match the OCR'd invoice lines back to the source project and
    /// pre-select DocumentLine.ProjectId — no manual project picking.
    /// Stored verbatim as received (JSON).</summary>
    public string? ExternalMetadataJson { get; set; }

    // ─── Buyer side of the document (the customer on a sales doc, or
    // "us" on a supplier doc). Persisted so the RD-compliance warning
    // "ใบกำกับ ≥ ฿1,000 ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" stops false-
    // firing when the page reloads — previously these only lived on
    // the in-memory OcrExtractedData and were lost after the initial
    // scan response. ───
    public string? BuyerName { get; set; }
    public string? BuyerTaxId { get; set; }

    // ─── §86/4 ที่อยู่ + รหัสสาขา (กฎเหล็ก #3: pre-fill ครบทุก field) ───
    // OCR แกะค่าเหล่านี้ได้อยู่แล้วแต่เดิมไม่มีที่เก็บ → ตอนสร้างเอกสารต้อง
    // ไปอ่าน Contact.BranchCode แทน ทำให้ใบของผู้ขายหลายสาขาได้สาขาผิด
    // (ใบสาขา 00003 แต่ Contact เก็บ 00000 จาก scan ก่อนหน้า) และหน้า review
    // ไม่มีช่องให้ผู้ใช้แก้เพราะ DTO ไม่ได้ส่งค่าออกมา
    public string? VendorBranchCode { get; set; }
    public string? VendorAddress { get; set; }
    public string? BuyerBranchCode { get; set; }
    public string? BuyerAddress { get; set; }

    /// <summary>ผู้ใช้แก้ค่าที่ระบบเติมให้ไปแล้วอย่างน้อยหนึ่งช่อง (ครั้งแรกเมื่อไร)
    ///
    /// <para>⚠️ ตัวชี้วัดคุณภาพ OCR เดิมนับ "ใบที่ถูกแก้" จาก <c>UpdatedAt != null</c>
    /// ซึ่งขยับทุกครั้งที่<b>ระบบเอง</b>บันทึกแถว (จบการสแกน · ผูกเอกสารที่สร้าง ·
    /// sync ตอนอนุมัติ) ⇒ อัตราการแก้ = ~100% ทุก tenant = ตัวเลขที่อ่านไม่ได้เลย
    /// (ผลตรวจ 2026-09-06 · T5). ช่องนี้ถูกตั้งเฉพาะตอน<b>คนแก้จริง</b>เท่านั้น</para></summary>
    public DateTime? UserCorrectedAt { get; set; }

    /// <summary>ชื่อช่องที่ผู้ใช้แก้ (คั่นด้วย <c>,</c>) — ใช้ดูว่าไปป์ไลน์พลาดตรงไหนบ่อย
    /// เพื่อจัดลำดับงานปรับปรุง ไม่ใช่แค่รู้ว่า "ถูกแก้"</summary>
    public string? UserCorrectedFields { get; set; }

    /// <summary>**สมุดที่มาของค่ารายช่อง** — JSON ของ
    /// <c>Helpers/OcrFieldArbiter.ToJson()</c> (สถาปัตยกรรมเป้าหมาย D1)
    ///
    /// <para>ไปป์ไลน์มี 6+ แหล่งเขียนทับช่องเดียวกันตามลำดับบรรทัดในเมธอด ⇒ เดิม
    /// ไล่ย้อนไม่ได้เลยว่าค่าที่ผู้ใช้เห็นมาจาก engine · ป้ายบนกระดาษ · ประวัติผู้ขาย ·
    /// นักเรียน หรือ AI. ช่องนี้เก็บผู้ชนะ + ตัวเลือกที่แพ้ของแต่ละช่อง</para>
    ///
    /// <para>⚠️ เฟสนี้ <b>บันทึกที่มาอย่างเดียว ยังไม่ย้ายตัวตัดสิน</b> — ค่าที่ใช้จริง
    /// ยังมาจากลำดับเดิมทุกประการ (แผนของ §4 D1: ทำทีละขั้น ไม่ให้การรื้อใหญ่
    /// กลายเป็นความเสี่ยงที่มากกว่าปัญหาเดิม)</para></summary>
    public string? FieldDecisionsJson { get; set; }

    /// <summary>สกุลเงินของเอกสาร — จาก e-Tax XML (ประกาศไว้ + มีลายเซ็น) ก่อน
    /// แล้วจึงเดาจากข้อความ · <c>null</c> = ยังไม่รู้ (ผู้เรียกตกไปใช้ "THB")
    ///
    /// <para>⚠️ เดิมไม่มีที่เก็บ ⇒ ทั้งเส้นสร้างเอกสารและ DTO ต่างคนต่าง
    /// <c>InferCurrency(RawTextContent)</c> ⇒ เดาสองที่ที่อาจไม่ตรงกัน และค่าที่
    /// e-Tax XML ประกาศไว้ชัด ๆ ถูกทิ้งทุกครั้ง (ผลตรวจ 2026-09-06 · T2-08)</para></summary>
    public string? Currency { get; set; }

    // ─── Document role inference ──────────────────────────────────────
    // Thai-accounting workflow separates THREE distinct concepts:
    //   • ScannedDocumentType — the physical paper we OCR'd (e.g. "Receipt")
    //   • OurRole              — "Buyer" or "Seller" depending on whose tax-id
    //                            matches the company doing the scanning
    //   • TargetDocumentType   — what to CREATE in our books (e.g.
    //                            "PaymentVoucher" when we scanned a supplier
    //                            receipt — we paid them, so we book a payment
    //                            voucher, NOT a "Receipt" document)
    //
    // The legacy DocumentType field (above) is kept for backwards-compat and
    // mirrors ScannedDocumentType for now; downstream AutoCreate logic and
    // VendorIntel learning consume TargetDocumentType instead.
    public string? ScannedDocumentType { get; set; }
    public string? OurRole { get; set; }                  // "Buyer" | "Seller"
    public string? TargetDocumentType { get; set; }       // enum-string from DocumentType

    // Matching
    public Guid? MatchedContactId { get; set; }
    public Guid? CreatedDocumentId { get; set; }           // Document created from OCR
    public Guid? CreatedJournalEntryId { get; set; }       // JE recorded from OCR (JE-only path)

    // AI augmentation trail — populated when IOcrAiAugmenter ran post-
    // extraction. AiSuggestedContactId is what AI proposed (may equal
    // MatchedContactId when AI's pick was accepted, or differ when the
    // user later overrides via the review modal). AiSuggestionFeedbackId
    // is the FK back to AiSuggestionFeedback so the UI can post a
    // user-accept back to that row.
    public Guid? AiSuggestedContactId { get; set; }
    public Guid? AiSuggestionFeedbackId { get; set; }

    // GL-account classification trail. Distinct from the vendor-match trail
    // above. GlAccountUsedAi = true when DeepSeek (the teacher) actually
    // classified the expense account on this scan — drives the honest
    // "🤖 AI แนะนำ" vs "ระบบแนะนำ (rule-based)" badge in the review UI.
    // GlAccountAiFeedbackId is the FK back to AiSuggestionFeedback so the
    // user's confirm/override in the review modal is recorded as a training
    // signal that the nightly job distils into the local model.
    public bool GlAccountUsedAi { get; set; }
    public Guid? GlAccountAiFeedbackId { get; set; }
    /// <summary>ผัง GL ที่ AI เสนอ (primary) — เก็บแม้ถูกปฏิเสธโดย confidence
    /// guard เพื่อความโปร่งใส: review UI โชว์ให้ผู้ใช้เห็นว่า AI เสนออะไร
    /// แม้ระบบใช้ของ local model แทน. ให้ผู้ใช้กดเลือกของ AI ได้เอง.</summary>
    public string? GlAccountAiSuggestedCode { get; set; }
    public decimal? GlAccountAiConfidence { get; set; }

    public string? RawTextContent { get; set; }
    public string? ProcessingNotes { get; set; }

    /// <summary>หมายเหตุที่ **ผู้ใช้** พิมพ์เอง (คนละเรื่องกับ ProcessingNotes ซึ่ง
    /// เป็น log ของไปป์ไลน์) — เหตุผลทางธุรกิจของรายจ่าย เช่น "เดินทางไปพบลูกค้า"
    /// ไหลต่อเป็น <c>Document.Notes</c> ตอนสร้างเอกสาร (§65 ตรี(3)/(14): รายจ่าย
    /// ที่พิสูจน์ความเกี่ยวข้องกับกิจการไม่ได้ = รายจ่ายต้องห้าม)</summary>
    public string? UserNotes { get; set; }
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// ความมั่นใจรายช่อง — JSON <c>{"SellerTaxId":0.95,"TotalAmount":0.7,…}</c>
    /// ใช้ชื่อช่องกลางจาก <c>Helpers/OcrFieldKeys.cs</c> เท่านั้น
    ///
    /// <para>เดิม<b>ไม่ได้เก็บลงฐานเลย</b> — ค่าอยู่ในหน่วยความจำเฉพาะตอนสแกน
    /// สด พอ reload หน้า/เปิดจากรายการ ค่าหายหมด แล้วป้าย % ข้างทุกช่องตกไปใช้
    /// confidence ของทั้งใบ ดูเหมือนเป็นข้อมูลรายช่องจริงทั้งที่เป็นเลขเดียวกัน
    /// ⇒ ไฮไลต์ "ตรวจสอบอีกครั้ง" ตามกฎเหล็ก #3 ข้อ 3 ใช้งานไม่ได้จริง</para>
    /// </summary>
    public string? FieldConfidenceJson { get; set; }

    /// <summary>feedback row ของการจำแนก "เอกสารที่จะสร้าง" ด้วย AI —
    /// เก็บไว้เพื่อปิด loop ตอนผู้ใช้ยืนยัน/แก้ (กฎเหล็ก #1 ขั้น CAPTURE)</summary>
    public Guid? TargetDocTypeAiFeedbackId { get; set; }

    /// <summary>คำตอบที่ AI เสนอ — เก็บไว้เทียบว่าผู้ใช้ "รับ" หรือ "แก้"
    /// (acceptedAi) และให้ UI ติดป้ายซื่อสัตย์ได้</summary>
    public string? TargetDocTypeAiSuggested { get; set; }

    /// <summary>true เมื่อ AI ถูกเรียกจริงและคำตอบถูกนำมาใช้ — ขับป้าย
    /// "🤖 AI แนะนำ" vs "⚙️ ระบบแนะนำ" ตามกฎเหล็ก #1</summary>
    public bool TargetDocTypeUsedAi { get; set; }

    /// <summary>feedback row ของการถาม AI ว่า "เราเป็นผู้ซื้อหรือผู้ขาย"
    /// (<c>AiFeatureKey.DocumentRoleInference</c>) — ปิด loop ตอนผู้ใช้แก้ <c>OurRole</c>
    /// ในหน้า review (กฎเหล็ก #1 ขั้น CAPTURE). null = กติกามั่นใจพอ ไม่ได้ถาม</summary>
    public Guid? OurRoleAiFeedbackId { get; set; }

    /// <summary>คำตอบที่ AI/นักเรียนเสนอ (Buyer/Seller) — เทียบ accepted/แก้</summary>
    public string? OurRoleAiSuggested { get; set; }

    /// <summary>true เมื่อ provider จริงถูกเรียกและคำตอบถูกใช้ — ป้าย "🤖 AI" vs "⚙️ ระบบ"</summary>
    public bool OurRoleUsedAi { get; set; }

    /// <summary>feedback row ของการ "แตกบรรทัดจากข้อความด้วย AI"
    /// (<c>AiFeatureKey.OcrLineItemSplit</c>) — เก็บไว้ปิด loop ตอนผู้ใช้แก้/
    /// ยืนยันรายการในหน้า review (กฎเหล็ก #1 ขั้น CAPTURE). null = ไม่ได้เรียก
    /// AI ในรอบนี้ (engine คืนรายการมาแล้ว หรือผลถูกด่านตรวจยอดปฏิเสธ)</summary>
    public Guid? LineSplitAiFeedbackId { get; set; }

    /// <summary>true เมื่อรายการในใบนี้มาจากการแตกบรรทัดด้วย AI — UI ติดป้าย
    /// "🤖 AI แตกรายการให้ กรุณาตรวจ" ให้ซื่อสัตย์ตามกฎเหล็ก #1</summary>
    public bool LineSplitUsedAi { get; set; }

    /// <summary>เวลาที่สแกนนี้ถูก "นำเข้าสต็อก" สำเร็จแล้ว (UTC) — กันกดซ้ำ
    ///
    /// <para>⚠️ <c>POST /ocr/{id}/import-stock</c> เดิม<b>ไม่มี guard ใด ๆ เลย</b>
    /// (ไม่เช็ค <c>CreatedDocumentId</c> ไม่เขียน marker กลับ) ⇒ double-click /
    /// กด retry / refresh หน้า = <b>สต็อกเข้าซ้ำทุกรอบ</b> · และยังซ้อนกับ
    /// <c>ApplyStockMovementsAsync(+1)</c> ตอน approve ใบซื้อที่สร้างจากสแกน
    /// ใบเดียวกัน (เมื่อบรรทัดมี <c>ProductCode</c> จากการผูก PO)
    /// ⇒ <c>CurrentStock</c> เกินจริงเท่าตัว · WAC เพี้ยน · COGS รอบถัดไปผิด</para></summary>
    public DateTime? StockImportedAt { get; set; }

    // GL & expense suggestions
    public string? ExpenseCategory { get; set; }
    public string? SuggestedAccountsJson { get; set; }
    public bool HasWht { get; set; }
    public decimal? WhtRate { get; set; }

    /// <summary>อัตราหัก ณ ที่จ่ายที่ **กฎหมายกำหนด** สำหรับหมวดนี้ (ท.ป.4/2528) —
    /// ข้อเสนอ ไม่ใช่ค่าที่ระบบตัดสินให้ (ดู <c>OcrExtractedData.SuggestedWhtRate</c>)</summary>
    public decimal? SuggestedWhtRate { get; set; }

    /// <summary>**ใครเป็นคนเสนออัตราใน <see cref="SuggestedWhtRate"/>**
    /// (<c>Helpers/OcrWhtSuggestion</c> · D-3) — ต้องเก็บลงแถวเพราะเส้นสร้างเอกสาร
    /// อ่านจากที่นี่เพื่อเขียนโน้ต <c>[WHT-SUGGEST]</c>
    ///
    /// <para>⚠️ ก่อนรอบ 184 เส้นนั้น<b>เดา</b>ที่มาจาก "มีรหัส ม.40 ไหม" ⇒ ประโยคที่
    /// อ้างว่า "กฎหมายให้หัก" โผล่บนใบที่ข้อเสนอมาจากนิสัยผู้ขายได้</para></summary>
    public Accounting.Helpers.WhtEvidenceSource SuggestedWhtSource { get; set; }
        = Accounting.Helpers.WhtEvidenceSource.None;

    /// <summary>รหัสประเภทเงินได้ ม.40 — ต้องมีก่อนออก 50 ทวิ/ภ.ง.ด.3/53</summary>
    public string? WhtIncomeTypeCode { get; set; }
    public int? PaymentTermsDays { get; set; }

    // Duplicate detection
    public string? FileHash { get; set; }
    public bool IsDuplicate { get; set; }
    public Guid? DuplicateOfScanId { get; set; }

    // ─── Which OCR engine actually produced the text ───
    // Records which tier of the cascade returned the result that was used:
    //   "AzureDI"           — Azure Document Intelligence (cloud, prebuilt models)
    //   "LocalPython"        — PaddleOCR + EasyOCR microservice
    //   "EmbeddedTesseract"  — in-process Tesseract via NuGet (always-on fallback)
    //   "Cached"             — duplicate-detection short-circuit; copied an earlier scan
    // Surfaced in the debug panel so users can see at a glance which engine
    // handled their document — useful when troubleshooting accuracy regressions
    // ("the embedded fallback ran because the Python service was down").
    public string? OcrEngine { get; set; }

    // ─── Potential Fixed Asset detection (Phase 4) ───
    // True when at least one line item crossed the asset detection
    // threshold (unit price + keyword). The UI uses this flag to surface
    // a "Needs Review — Potential Asset" alert in the review modal so
    // the user can register the asset(s) before the scan auto-creates
    // an expense document.
    public bool HasPotentialFixedAsset { get; set; }

    // JSON-serialized list of FixedAssetDetector.LineDecision rows for
    // each line flagged as a potential asset. Schema:
    //   [{ "lineIndex":0, "suggestedCategory":"คอมพิวเตอร์...",
    //      "suggestedUsefulLifeMonths":36, "confidenceScore":0.85,
    //      "description":"...", "unitPrice":29900, "amount":29900,
    //      "reasons":["..."] }]
    public string? PotentialAssetLinesJson { get; set; }

    // ─── Handwriting flag (from Azure DI styleFont feature) ───
    // True when at least one field on the document appears to be
    // hand-written on a printed form. Triggers a "✋ ตรวจสอบยอดเงิน
    // ด้วยตา" alert in the UI, suppresses auto-create, and docks the
    // scan quality grade so the review queue surfaces it first.
    public bool HasHandwriting { get; set; }
    public decimal? HandwritingConfidence { get; set; }
}

/// <summary>
/// Learned extraction patterns from user corrections.
/// The zone analyzer uses these to improve accuracy over time.
/// </summary>
public class OcrLearnedPattern : TenantEntity
{
    public string? VendorTaxId { get; set; }
    public string FieldName { get; set; } = "";          // SellerName, SellerTaxId, DocumentNumber, etc.
    public string ContextKeyword { get; set; } = "";      // The keyword found near the field value
    public string? ExtractionRegex { get; set; }           // Regex to extract the value near the keyword
    public int SearchRadius { get; set; } = 300;           // How far from keyword to search
    public int TimesConfirmed { get; set; } = 1;           // Increases each time this pattern is confirmed
    public DateTime LastConfirmedAt { get; set; } = DateTime.UtcNow;

    // Negative learning: a value that was previously extracted but corrected away from
    // — should be down-weighted when seen again for this vendor.
    public bool IsNegativeExample { get; set; } = false;
    public string? NegativeValue { get; set; }              // The wrong value that was rejected
    public int FailureCount { get; set; } = 0;              // How many times this pattern was wrong
}

/// <summary>
/// Learned mapping: vendor + line-item description keyword → chart-of-account code.
/// Built incrementally from user corrections and from approved auto-created documents.
/// On a new scan, OcrService queries this table to suggest expense categories that
/// match the company's actual booking habits — far more accurate than generic rules.
/// </summary>
public class OcrCategoryMapping : TenantEntity
{
    /// <summary>Vendor TaxId (preferred) or normalized vendor name when TaxId missing.</summary>
    public string VendorKey { get; set; } = "";
    /// <summary>Lower-cased substring match key from line description / expense category text.</summary>
    public string DescriptionKeyword { get; set; } = "";
    /// <summary>Suggested debit account code from CoA (e.g. "5402" for fuel).</summary>
    public string AccountCode { get; set; } = "";
    public string? AccountName { get; set; }
    /// <summary>How many times user confirmed/booked with this account for this vendor+keyword.</summary>
    public int TimesUsed { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Optional: which user originally trained this mapping (for audit).</summary>
    public Guid? TrainedByUserId { get; set; }
}

/// <summary>
/// Per-vendor aggregated intelligence cache. Built from approved Documents history
/// and refreshed on each new document approval. Drives auto-suggestion of:
///   • DocumentType (most common type used with this vendor)
///   • Debit account (most common booking)
///   • WHT habits (does this vendor usually have WHT? what rate?)
///   • Amount sanity range (flag scans with anomalous totals)
///   • Payment terms
/// One row per (CompanyId, VendorKey). Denormalized for sub-millisecond lookup
/// during ScanAsync — full per-document scans on every OCR would be too slow.
/// </summary>
public class OcrVendorIntelligence : TenantEntity
{
    public string VendorKey { get; set; } = "";              // tax:1234567890123 or name:lower
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }

    // ─── DocumentType prediction ───
    public string? MostCommonDocumentType { get; set; }      // e.g. "PurchaseInvoice"
    public int MostCommonDocumentTypeCount { get; set; }
    public int TotalDocuments { get; set; }
    public string? DocumentTypeBreakdownJson { get; set; }   // {"PurchaseInvoice":12,"Expense":3}

    // ─── Debit account prediction ───
    public string? MostCommonDebitAccountCode { get; set; }
    public string? MostCommonDebitAccountName { get; set; }
    public int MostCommonDebitAccountCount { get; set; }
    public string? DebitAccountBreakdownJson { get; set; }   // {"5300":8,"5402":4}

    // ─── WHT habits ───
    public bool TypicallyHasWht { get; set; }                // >50% of past docs had WHT
    public decimal? TypicalWhtRate { get; set; }             // mode of past WHT rates
    public int WhtUsageCount { get; set; }

    // ─── Amount sanity range ───
    public decimal? AvgTotalAmount { get; set; }
    public decimal? MinTotalAmount { get; set; }
    public decimal? MaxTotalAmount { get; set; }
    public decimal? MedianTotalAmount { get; set; }
    // Running log-amount stats — feeds AmountAnomalyDetector.CheckZScore
    // for robust anomaly detection that handles heavy-tailed amount
    // distributions far better than raw min/max.
    public decimal? LogAmountMean { get; set; }
    public decimal? LogAmountVariance { get; set; }

    // ─── Payment terms ───
    public int? TypicalPaymentTermsDays { get; set; }

    // ─── Learned patterns for OCR boosting ───
    // TypicalDocNumberPrefix: when this vendor's document numbers always
    // start with the same prefix (e.g. HomePro "612XXX", PTT "TAX-"), the
    // OCR can use that as a high-confidence anchor to disambiguate
    // candidate numbers. Empty when no consistent pattern detected.
    public string? TypicalDocNumberPrefix { get; set; }

    // TopLineKeywordsJson: frequency map of words seen in line-item
    // descriptions from prior approved docs, e.g. {"น้ำมัน":12, "Diesel":5}.
    // Lets the category resolver boost confidence on a new scan whose
    // descriptions match the vendor's historical pattern, even when the
    // global rule library would only score weakly.
    public string? TopLineKeywordsJson { get; set; }

    // Audit
    public DateTime LastTrainedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDocumentDate { get; set; }
}

/// <summary>
/// System-wide vendor → expense-account mappings. Trained by SystemAdmin from
/// the /admin/ocr-config page and shared across every tenant. Acts as a
/// fallback knowledge base when the tenant's own OcrCategoryMappings has no
/// match for a vendor/keyword combination — so newly-onboarded companies
/// get useful OCR predictions on day one.
///
/// Mirrors OcrCategoryMapping but without CompanyId. Tenant-specific
/// mappings always win at predict time; this is consulted only when no
/// tenant row matches.
/// </summary>
public class SystemOcrCategoryMapping : BaseEntity
{
    public string VendorKey { get; set; } = "";
    public string DescriptionKeyword { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string? AccountName { get; set; }
    public int TimesUsed { get; set; } = 1;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public Guid? TrainedByUserId { get; set; }

    // Industry breakdown of contributing tenants: e.g.
    //   {"Manufacturing": 5, "Trading": 3, "Service": 2}
    // Used at predict time to weight this row higher when the consuming
    // tenant's IndustryType matches the dominant industry of contributors.
    // Null / empty = universal (seeded data or industry-mixed sources).
    public string? IndustryBreakdownJson { get; set; }
}

/// <summary>
/// System-wide per-vendor intelligence. Trained by SystemAdmin from the
/// /admin/ocr-config page; shared across every tenant. Acts as a fallback
/// when the tenant has no OcrVendorIntelligence row for a given vendor yet.
///
/// Mirrors OcrVendorIntelligence but without CompanyId; one row per VendorKey
/// for the entire system.
/// </summary>
public class SystemOcrVendorIntelligence : BaseEntity
{
    public string VendorKey { get; set; } = "";
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }

    public string? MostCommonDocumentType { get; set; }
    public int MostCommonDocumentTypeCount { get; set; }
    public int TotalDocuments { get; set; }
    public string? DocumentTypeBreakdownJson { get; set; }

    public string? MostCommonDebitAccountCode { get; set; }
    public string? MostCommonDebitAccountName { get; set; }
    public int MostCommonDebitAccountCount { get; set; }
    public string? DebitAccountBreakdownJson { get; set; }

    public bool TypicallyHasWht { get; set; }
    public decimal? TypicalWhtRate { get; set; }
    public int WhtUsageCount { get; set; }

    public decimal? AvgTotalAmount { get; set; }
    public decimal? MinTotalAmount { get; set; }
    public decimal? MaxTotalAmount { get; set; }
    public decimal? MedianTotalAmount { get; set; }

    public int? TypicalPaymentTermsDays { get; set; }

    public DateTime LastTrainedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastDocumentDate { get; set; }

    // Industry breakdown of contributing tenants — same semantics as
    // SystemOcrCategoryMapping.IndustryBreakdownJson. Lets the query-
    // time consumer weight this row toward same-industry similarity.
    public string? IndustryBreakdownJson { get; set; }

    /// <summary>Number of DISTINCT tenants who have contributed at least
    /// one training event to this row. Used by VendorIntelligenceService
    /// to decide whether the row crossed the k-anonymity floor (k=3)
    /// before any sensitive AVG fields are surfaced cross-tenant.
    /// Maintained by GlobalVendorIntelLearner.</summary>
    public int TenantContributionCount { get; set; }
}

/// <summary>
/// Discovered association rule from system-wide basket analysis. Each row
/// represents "when antecedent tokens are present in a document, the
/// consequent account is likely the right debit". Refreshed by the
/// AssociationRuleMiner background job. No CompanyId — these are
/// system-wide patterns shared across every tenant.
/// </summary>
public class SystemOcrAssociationRule : BaseEntity
{
    /// <summary>JSON array of antecedent tokens, e.g. ["brand:ptt","kw:น้ำมัน"].</summary>
    public string AntecedentJson { get; set; } = "[]";

    /// <summary>The consequent token: typically "acct:5402" (a debit account
    /// code) but the format is intentionally generic so we can mine other
    /// consequents (doc type, WHT rate) in the future.</summary>
    public string Consequent { get; set; } = "";

    /// <summary>Fraction of all transactions that contain the antecedent AND
    /// consequent — measures how OFTEN the pattern occurs.</summary>
    public decimal Support { get; set; }

    /// <summary>P(consequent | antecedent) — measures how RELIABLE the rule
    /// is. ≥ 0.5 typically required for usable rules.</summary>
    public decimal Confidence { get; set; }

    /// <summary>Confidence / P(consequent). Lift > 1 means the antecedent
    /// actually moves the needle (vs. choosing the consequent at random).
    /// Used as the primary ranking metric.</summary>
    public decimal Lift { get; set; }

    /// <summary>Raw count of training transactions supporting this rule —
    /// used to weight lift (a lift of 10 from 3 docs is weaker than 5 from 300).</summary>
    public int TransactionCount { get; set; }

    public DateTime MinedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// การซื้อเครดิต OCR เพิ่มเติม (add-on pages)
/// </summary>
public class OcrCreditPurchase : TenantEntity
{
    public Guid SubscriptionId { get; set; }
    public int PagesPurchased { get; set; }
    public int PagesRemaining { get; set; }
    public decimal AmountPaid { get; set; }
    public string Currency { get; set; } = "THB";
    public string Status { get; set; } = "Pending";
    public string? PaymentReference { get; set; }
    public string? SlipFileName { get; set; }
    public string? SlipStoragePath { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

// ===== Custom Report Builder =====

/// <summary>
/// รายงานที่ผู้ใช้สร้างเอง
/// </summary>
public class CustomReport : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ReportType { get; set; } = "Table";      // Table, Chart, Pivot, Dashboard
    public string Category { get; set; } = "Financial";    // Financial, Tax, Operational, Custom

    // Data source
    public string DataSourceType { get; set; } = "JournalEntry"; // JournalEntry, Document, BankTransaction, Product, Contact, Payroll
    public string? FilterJson { get; set; }                // Dynamic filters
    public string? ColumnsJson { get; set; }               // Column definitions
    public string? SortingJson { get; set; }
    public string? GroupingJson { get; set; }
    public string? AggregationJson { get; set; }           // Sum, Avg, Count, Min, Max

    // Display
    public string? ChartType { get; set; }                 // Bar, Line, Pie, Scatter, Area
    public string? ChartConfigJson { get; set; }
    public bool ShowTotals { get; set; } = true;
    public bool IsPublic { get; set; } = false;            // Shared with all users
    public string? CreatedByUserId { get; set; }

    // Schedule
    public bool IsScheduled { get; set; } = false;
    public string? ScheduleFrequency { get; set; }         // Daily, Weekly, Monthly
    public string? SendToEmails { get; set; }
    public string? ExportFormat { get; set; }              // PDF, Excel, CSV
}

// ===== Customer/Supplier Portal =====

/// <summary>
/// Token เข้าถึง Portal ของลูกค้า/Supplier
/// </summary>
public class PortalAccess : TenantEntity
{
    public Guid ContactId { get; set; }
    public Contact Contact { get; set; } = null!;
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }

    // Permissions
    public bool CanViewInvoices { get; set; } = true;
    public bool CanViewStatements { get; set; } = true;
    public bool CanDownloadPdf { get; set; } = true;
    public bool CanMakePayment { get; set; } = false;      // Online payment
    public bool CanViewOrders { get; set; } = false;
    public bool CanCreateOrders { get; set; } = false;
    public bool CanViewDeliveries { get; set; } = false;
}

/// <summary>
/// Activity ใน Portal
/// </summary>
public class PortalActivity : TenantEntity
{
    public Guid PortalAccessId { get; set; }
    public PortalAccess PortalAccess { get; set; } = null!;
    public string ActivityType { get; set; } = "";        // Login, ViewInvoice, DownloadPdf, MakePayment
    public Guid? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string? IpAddress { get; set; }
    public DateTime ActivityAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-vendor "known good" field values, populated whenever Azure DI
/// extracts a high-confidence value for a vendor we recognize. The
/// local-OCR cascade (PaddleOCR / Tesseract) then fuzzy-matches its own
/// noisy output against these values and substitutes the canonical
/// version when similarity is high enough. Drastically reduces the
/// "หจก . แอมแฮปปี๊เนส" (with stray spaces) → "หจก. แอมแฮปปี๊เนส"
/// kind of noise that plagues Tesseract-only scans.
///
/// Keyed by (CompanyId, VendorTaxId, FieldName, Value). Repeated
/// confirmations bump ConfirmedCount — values that Azure has seen 5+
/// times beat one-off noise. Cleaned by background job after 12 months
/// of inactivity.
/// </summary>
public class VendorKnownGoodValue : TenantEntity
{
    /// <summary>Vendor's TaxId. NULL allowed for vendor-name-only matches.</summary>
    public string? VendorTaxId { get; set; }
    /// <summary>SellerName / SellerTaxId / DocumentNumber / BuyerName / Address / Phone / Email / BranchCode.</summary>
    public string FieldName { get; set; } = "";
    /// <summary>The canonical value extracted by Azure DI (or user correction).</summary>
    public string Value { get; set; } = "";
    /// <summary>Confidence score from Azure DI (0–1). Used to weight when multiple variants exist.</summary>
    public decimal Confidence { get; set; }
    /// <summary>How many distinct scans confirmed this exact value.</summary>
    public int ConfirmedCount { get; set; } = 1;
    /// <summary>"AzureDI" | "UserCorrection" — source lets us trust user corrections over Azure on conflict.</summary>
    public string Source { get; set; } = "AzureDI";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
