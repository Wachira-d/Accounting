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
    ///
    /// <paramref name="forceRescan"/>: ข้ามด่าน "ไฟล์นี้เคยสแกนแล้ว (hash ตรง)"
    /// แล้วเดินเส้น engine จริง — ใช้กับปุ่ม "สแกนใหม่" เท่านั้น · ถ้าไม่มีธงนี้
    /// การกดสแกนใหม่จะได้สำเนาของผลเดิมกลับมาแล้วตอบว่า "สำเร็จ" (silent no-op)
    /// </summary>
    /// <param name="actingUserId">ผู้ใช้ที่เป็นเจ้าของการกระทำ — ใช้ตรวจสิทธิ์
    /// "สร้างเอกสารชนิดนี้ได้ไหม" ก่อน auto-create · <c>null</c> = เส้นที่ไม่มี
    /// ผู้ใช้เป็นเจ้าของ (partner API ที่คุมด้วย scope ของ key อยู่แล้ว · งาน
    /// เบื้องหลัง) ⇒ ไม่ตรวจ. **ทางเข้าที่เป็นคนต้องส่งเสมอ** ไม่งั้นด่านสิทธิ์
    /// ที่อยู่ใน controller อย่างเดียวจะถูกลัดผ่าน (ผลตรวจทีม E · E-02)</param>
    Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId, string? preferredEngine = null, string? externalMetadataJson = null, bool autoCreate = false, bool forceRescan = false, Guid? actingUserId = null);
    Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId);
    Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request);
    Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride = null);

    /// <summary>เหมือนตัวบน แต่ <paramref name="allowDuplicate"/> = ผู้ใช้ยืนยันแล้วว่า
    /// รู้ว่าเป็นใบซ้ำและยังต้องการสร้าง — ด่านกันซ้ำอยู่ที่เซิร์ฟเวอร์ ไม่ใช่ที่ปุ่ม
    /// บนหน้าเว็บ (เดิมเตือนเฉพาะปุ่มเดียวจากสามปุ่ม อีกสองปุ่มลัดผ่านไปเลย)</summary>
    Task<OcrResultResponse> CreateDocumentFromScanAsync(
        Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride, bool allowDuplicate);
    /// <summary>Rebuild a document's lines from its source OCR scan when it
    /// was created empty (pre line-building fix). Looked up by documentId.</summary>
    Task<OcrResultResponse> RepopulateDocumentLinesFromScanAsync(Guid companyId, Guid documentId, string performedBy);

    /// <summary>ตรวจความครบถ้วนตามกรมสรรพากร (RD compliance) ซ้ำ จากผลสแกน
    /// ที่เก็บไว้ — ผลตรวจถูก persist ตอนสแกนครั้งเดียว validator ที่ฉลาดขึ้น
    /// ภายหลังไม่ช่วยใบเก่า จึงต้องมีทางประเมินใหม่โดยไม่ต้องสแกนซ้ำ</summary>
    Task<(string Status, string IssuesJson)> RecheckRdComplianceAsync(Guid companyId, Guid documentId);

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
        int lineIndex, string? description, decimal? quantity, decimal? unitPrice,
        string? accountCode = null);

    /// <summary>เพิ่ม/ลบบรรทัดรายการของผลสแกน — <c>action</c> = "add" | "delete"
    /// (เดิมตาราง review เพิ่ม/ลบแถวไม่ได้เลย ⇒ OCR รวมหรือแตกแถวผิดแล้วผู้ใช้
    /// มีทางออกแค่ "แกะใหม่" ซึ่งจำกัดจำนวนครั้ง) คืนจำนวนบรรทัดหลังแก้</summary>
    Task<int> ModifyExtractedLineAsync(Guid companyId, Guid scanResultId, string action, int lineIndex);

    /// <summary>พรีวิวบรรทัดที่จะได้เมื่อสร้างเอกสารจากสแกนนี้ — ใช้ตัวสร้างบรรทัดตัวเดียวกับ
    /// <see cref="CreateDocumentFromScanAsync(Guid, Guid, string, string?)"/> แต่ไม่บันทึกอะไร
    /// (หน้าเว็บ “แก้ในฟอร์มก่อน” ต้องไม่คำนวณเอง)</summary>
    Task<OcrLinePreviewResponse> PreviewDocumentLinesAsync(Guid companyId, Guid scanResultId, string? targetTypeOverride);

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

    /// <summary>ใบต้นทางที่อาจตรงกับสแกน (ทุกชนิดเอกสาร) — คำนวณสดจาก <c>Helpers/OcrPredecessorMatcher</c>
    /// เรียงตามคะแนน; ว่าง = ไม่มีอะไรเกี่ยว</summary>
    Task<List<PredecessorCandidateDto>> GetPredecessorCandidatesAsync(Guid companyId, Guid scanResultId);

    /// <summary>ผูกสแกนกับใบต้นทางที่ผู้ใช้เลือก — ต้องเป็นชนิดที่แปลงมาเป็นเอกสารเป้าหมายได้
    /// และเป็นคู่ค้ารายเดียวกัน; ต้นทางเป็น PO จะเดินเส้น <see cref="LinkPurchaseOrderAsync"/> ด้วย</summary>
    Task<OcrResultResponse> LinkPredecessorAsync(Guid companyId, Guid scanResultId, LinkPredecessorRequest request, string performedBy);

    Task<OcrResultResponse> UnlinkPredecessorAsync(Guid companyId, Guid scanResultId);

    Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction);
    Task DeleteScanAsync(Guid companyId, Guid scanResultId, bool cascadeCreatedDocument = false, string? reason = null, Guid? performedByUserId = null);
    Task<object> RegisterAssetFromScanAsync(Guid companyId, Guid scanResultId,
        Controllers.OcrController.RegisterAssetFromScanRequest req,
        IFixedAssetService assetService, string createdBy);

    /// <summary>รอบ 193: ข้อเสนอบรรทัดปรับส่วนต่างยอดชำระของเอกสารที่สร้างจากสแกน (null = ไม่มี) — อ่านอย่างเดียว</summary>
    Task<OcrSettlementProposalResponse?> GetSettlementProposalAsync(Guid companyId, Guid documentId);

    /// <summary>รอบ 193 (เจ้าของข้อ 14): รายงานสแกน/เอกสารเก่าที่ตัวเลขที่เก็บไว้ผิดเพราะตรรกะส่วนลด/ยอดรวมแบบเดิม — อ่านอย่างเดียว</summary>
    Task<List<OcrStoredAmountAuditRow>> GetStoredAmountAuditAsync(Guid companyId, DateTime? from, DateTime? to, int take);
}
