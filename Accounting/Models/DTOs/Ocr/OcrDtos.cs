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
    decimal? GlAccountAiConfidence = null);

/// <summary>One open PO of the matched vendor — what the picker modal
/// renders. Lines come back inline so the operator can map OCR ↔ PO line
/// without a second roundtrip.</summary>
public record OpenPurchaseOrderDto(
    Guid Id, string DocumentNumber, DateTime DocumentDate, string Status,
    decimal TotalAmount, IReadOnlyList<OpenPurchaseOrderLineDto> Lines);

public record OpenPurchaseOrderLineDto(
    Guid Id, int LineOrder, string Description, decimal Quantity,
    decimal UnitPrice, decimal Amount, Guid? AccountId, string? AccountCode);

/// <summary>Body of POST /ocr/{scanId}/link-po — the chosen PO plus the
/// per-OCR-line mapping (line index → PO line id). Unmapped indices are
/// omitted; nulls explicitly clear a mapping.</summary>
public record LinkPurchaseOrderRequest(
    Guid PurchaseOrderId,
    Dictionary<int, Guid?>? LineMappings);

public record OcrQualityGradeDto(string Letter, int Score, string Color);

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
    string? Unit = null);

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
    // Phase-1 role-inference correction: when user changes the inferred
    // "เอกสารที่จะสร้าง" dropdown, this string carries the new value so the
    // backend can both update the scan record AND train VendorIntelligence
    // to suggest the same target for this vendor next time.
    string? TargetDocumentType = null);

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
    bool PostWht = true);
