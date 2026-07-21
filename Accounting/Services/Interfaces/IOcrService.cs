using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;

namespace Accounting.Services.Interfaces;

public interface IOcrService
{
    /// <summary>
    /// Run the OCR cascade on an uploaded file.
    /// <paramref name="preferredEngine"/>: "auto" | "azure" | "local" (case-
    /// insensitive). Null/empty/auto = full cascade (Tier 0 e-Tax XML →
    /// Tier 1 Azure DI → Tier 2 Local Python → Tier 3 Embedded Tesseract).
    /// "azure" skips Tier 2 / Tier 3 (no silent local fallback when the user
    /// explicitly asked for Azure-grade accuracy). "local" skips Tier 1 so
    /// no Azure cost is incurred. Tier 0 always runs regardless — it's free,
    /// 100% accurate, and consumes no engine quota.
    ///
    /// <paramref name="externalMetadataJson"/>: optional structured payload an
    /// external system uploads alongside the file (order/project line info).
    /// When present, OcrMetadataProjectMatcher links each extracted line back
    /// to its originating project so created DocumentLines get ProjectId
    /// pre-selected for automatic cost allocation.
    ///
    /// <paramref name="autoCreate"/>: when true, ScanAsync also creates the
    /// inferred target document automatically (the historical integration
    /// behavior). Web / human-driven uploads pass false — the scan only
    /// suggests the target type and the user explicitly creates via
    /// CreateDocumentFromScanAsync. Integration partner syncs opt in to true
    /// to keep their existing zero-touch behavior.
    /// </summary>
    Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId, string? preferredEngine = null, string? externalMetadataJson = null, bool autoCreate = false);
    Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId);
    Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request);
    Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride = null);
    /// <summary>Rebuild a document's lines from its source OCR scan when it
    /// was created empty (pre line-building fix). Looked up by documentId.</summary>
    Task<OcrResultResponse> RepopulateDocumentLinesFromScanAsync(Guid companyId, Guid documentId, string performedBy);

    /// <summary>ผูกไฟล์ scan ของ OCR เข้ากับเอกสารที่สร้างผ่าน path อื่น
    /// (UI handoff: OCR review → ฟอร์มเอกสาร → POST /documents). กัน file
    /// ค้างที่ EntityType="OcrScan" จนผู้ใช้เปิดเอกสารแล้วไม่เห็นไฟล์ต้นฉบับ.
    /// Idempotent: เรียกซ้ำเป็น no-op. คืน false ถ้าไม่พบ scan/document.</summary>
    Task<bool> LinkScanToExistingDocumentAsync(Guid companyId, Guid scanId, Guid documentId);

    /// <summary>Record a balanced Journal Entry directly from a scan (the
    /// "JE only" path — no business document). Returns the created JE id.</summary>
    Task<Guid> CreateJournalEntryFromScanAsync(Guid companyId, Guid scanResultId,
        Models.DTOs.Ocr.CreateJeFromScanRequest request, string performedBy);
    Task<OcrResultResponse> MatchContactAsync(Guid companyId, Guid scanResultId, Guid contactId);

    /// <summary>Persist a per-line project assignment into the scan's
    /// ExtractedItemsJson so CreateDocumentFromScanAsync can flow it
    /// to DocumentLine.ProjectId.</summary>
    Task SetExtractedLineProjectAsync(Guid companyId, Guid scanResultId,
        int lineIndex, Guid? projectId, string? projectName);

    /// <summary>Bulk assign — set the same project on EVERY extracted
    /// line. Used by the OCR review UI's "main project" picker:
    /// user picks one project, every row inherits it, then user only
    /// has to touch the rows that should override. When onlyEmpty=true,
    /// only lines that don't already have a project get updated
    /// (preserves the user's prior overrides).</summary>
    Task SetAllExtractedLineProjectsAsync(Guid companyId, Guid scanResultId,
        Guid? projectId, string? projectName, bool onlyEmpty);

    /// <summary>แก้ description/จำนวน/ราคาต่อหน่วยของบรรทัด OCR ในหน้า review
    /// (กฎเหล็ก #3 — แก้ inline ก่อนสร้างเอกสาร ไม่ต้องสร้างแล้วเข้าไปแก้ทีหลัง).
    /// recompute Amount = qty×unitPrice, persist ลง ExtractedItemsJson, คืน amount
    /// ใหม่. field ที่ส่ง null = คงค่าเดิม.</summary>
    Task<decimal> SetExtractedLineFieldsAsync(Guid companyId, Guid scanResultId,
        int lineIndex, string? description, decimal? quantity, decimal? unitPrice);

    /// <summary>List the matched vendor's open Purchase Orders together with
    /// their line items so the review UI can render the "เลือก PO" picker.
    /// Returns empty when no contact is matched or no open POs exist.</summary>
    Task<List<OpenPurchaseOrderDto>> GetOpenPosForScanAsync(Guid companyId, Guid scanResultId);

    /// <summary>Link this scan to one of the vendor's open POs and record the
    /// per-line OCR↔PO mappings. Persists the link + mappings on the scan,
    /// learns each mapped OCR description as a ProductAlias (when the PO line
    /// has a ProductCode that resolves), and returns the updated scan.</summary>
    Task<OcrResultResponse> LinkPurchaseOrderAsync(Guid companyId, Guid scanResultId,
        LinkPurchaseOrderRequest request, string performedBy);

    /// <summary>Clear the scan's PO linkage. The created document, if any, is
    /// untouched — only the scan-level link is removed so the operator can
    /// re-pick or fall back to a plain expense.</summary>
    Task<OcrResultResponse> UnlinkPurchaseOrderAsync(Guid companyId, Guid scanResultId);

    Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction);
    Task DeleteScanAsync(Guid companyId, Guid scanResultId, bool cascadeCreatedDocument = false, string? reason = null, Guid? performedByUserId = null);
    Task<object> RegisterAssetFromScanAsync(Guid companyId, Guid scanResultId,
        Controllers.OcrController.RegisterAssetFromScanRequest req,
        IFixedAssetService assetService, string createdBy);
}
