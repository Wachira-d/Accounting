using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ITaxService
{
    Task<TaxReportResponse> GenerateTaxReportAsync(Guid companyId, CreateTaxReportRequest request);
    Task<TaxReportResponse> GetTaxReportAsync(Guid companyId, Guid reportId);
    Task<List<TaxReportResponse>> GetTaxReportsAsync(Guid companyId, TaxType? taxType = null, int? year = null);
    Task<TaxReportResponse> FileTaxReportAsync(Guid companyId, Guid reportId);
    Task<TaxReportResponse> UpdateTaxReportAsync(Guid companyId, Guid reportId, UpdateTaxReportRequest request);
    Task<TaxReportResponse> RegenerateTaxReportAsync(Guid companyId, Guid reportId, Guid? actorId = null, string? actorEmail = null);
    /// <summary>ทบทวนรายงาน ภ.ง.ด.50 ที่บันทึกไว้ทุกปีเทียบยอดสด (อ่านอย่างเดียว · ทีม O 2026-10-22) — เฉพาะปีที่ยอดต่าง เมื่อ <paramref name="onlyDiffering"/></summary>
    Task<List<CitRecomputeReviewRow>> GetCitRecomputeReviewAsync(Guid companyId, bool onlyDiffering = true);
    /// <summary>รอบ 269 — ทบทวนช่อง 7/8 ของ ภ.พ.30 ที่บันทึกไว้ (อ่านอย่างเดียว)</summary>
    Task<List<Pp30SalesBoxReviewRow>> GetPp30SalesBoxReviewAsync(Guid companyId);
    Task DeleteTaxReportAsync(Guid companyId, Guid reportId);
    Task<int> AutoRefreshReportsAsync(Guid companyId, int months = 2);
    Task<object> GetVatDebugAsync(Guid companyId, int year, int month);

    /// <summary>เอกสารงวดอื่นที่มี VAT และยังไม่ถูกใช้ในรายงานใด — สำหรับดึงเข้างวดนี้ ·
    /// รอบ 225: แบ่งหน้า (offset/limit) + Total ของทั้งชุด ⇒ "เลือกทั้งหมด" บอก N จาก M ได้ตรง</summary>
    Task<PullableDocumentListResponse> GetPullableDocumentsAsync(Guid companyId, Guid reportId, string? search,
        DateTime? fromDate, DateTime? toDate, string? side = null, int offset = 0, int limit = 200);
    /// <summary>ดึงเอกสารเก่าเข้ารายงานภาษีงวดนี้เป็นบรรทัดใหม่ (ไม่ผ่าน = throw ข้อความไทย)</summary>
    Task<TaxReportResponse> PullDocumentIntoReportAsync(Guid companyId, Guid reportId, Guid documentId,
        Guid? actorId = null, string? actorEmail = null);
    /// <summary>ดึงหลายใบในครั้งเดียว (รอบ 225) — partial: ใบที่ผ่านด่านเดียวกับดึงทีละใบเข้าในธุรกรรมเดียว ·
    /// ใบที่ไม่ผ่านคืนเหตุผล + RuleCode · ส่งซ้ำไม่เกิดบรรทัดซ้ำ</summary>
    Task<PullDocumentsResult> PullDocumentsIntoReportAsync(Guid companyId, Guid reportId, IReadOnlyList<Guid> documentIds,
        Guid? actorId = null, string? actorEmail = null);
    /// <summary>รายงานร่างที่ถือบรรทัด "ดึงเข้างวด" ซึ่งตอนนี้ไม่ผ่านด่านดึง (รอบ 225 S3 — อ่านอย่างเดียว ไม่แก้)</summary>
    Task<List<IneligiblePulledLineDto>> FindIneligiblePulledLinesAsync(Guid companyId);

    /// <summary>ดึงใบเดียวเข้ารายงานร่างของงวดเคลม (ไม่ regenerate — ค่าที่ติ๊ก
    /// ไว้ในรายงานไม่หาย) — best-effort คืนข้อความผลลัพธ์ ไม่ throw</summary>
    Task<string> TryPullIntoDraftReportAsync(Guid companyId, Guid documentId, Guid? actorId = null, string? actorEmail = null);
    /// <summary>ส่งออกรายงานภาษีเป็นไฟล์ Excel (.xlsx) — VAT แยกชีตภาษีขาย/ภาษีซื้อ</summary>
    Task<(byte[] Content, string FileName)> ExportTaxReportXlsxAsync(Guid companyId, Guid reportId);

    /// <summary>คำนวณ ภ.พ.30 เป็น TaxReport ชั่วคราว (ไม่ persist) — ใช้ตรรกะ
    /// เดียวกับหน้าจอ/Excel เพื่อให้ไฟล์ยื่น (CSV) ตรงกับที่ผู้ใช้เห็นทุกบรรทัด.</summary>
    Task<Models.Entities.TaxReport> ComputeVatReportAsync(Guid companyId, int year, int month);
    /// <summary>ภ.พ.36 แบบ transient (ไม่บันทึก) — single source ให้ e-Filing
    /// ใช้ชุดเดียวกับหน้าจอ (กัน "ไฟล์ที่ยื่น ≠ ที่ผู้ใช้เห็น")</summary>
    Task<Models.Entities.TaxReport> ComputePp36ReportAsync(Guid companyId, int year, int month);
    /// <summary>ยอดบวกกลับ §65 ตรี ของช่วงเวลา — ใช้ร่วมกันระหว่าง ภ.ง.ด.50
    /// (รายปี) และ ภ.ง.ด.51 (ประมาณการครึ่งปี) เพื่อให้สองแบบใช้ฐานเดียวกัน</summary>
    Task<decimal> ComputeSection65TerAddBackAsync(Guid companyId, DateTime fromDate, DateTime toDate);

    // Task 4 of ERP upgrade
    Task<Models.Entities.VatDeferral> DeferInputVatAsync(Guid companyId, Guid documentId, int deferredToPeriod, string? reason, string userId);
    Task UnlockTaxFilingAsync(Guid companyId, Guid reportId, string userId, string reason);
    Task<TaxReportResponse> RejectAndReverseTaxReportAsync(Guid companyId, Guid reportId, string reason, Guid? nonClaimableVatAccountId, string userId);
    Task<bool> IsDocumentFilingLockedAsync(Guid companyId, Guid documentId);

    // RD pipe-delimited e-Filing export (Phase E)
    Task<Models.Entities.EFilingExport> GenerateEFilingAsync(Guid companyId, string formType, int year, int month, string userId);
}
