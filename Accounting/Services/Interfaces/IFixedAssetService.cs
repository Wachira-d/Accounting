using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

public interface IFixedAssetService
{
    Task<FixedAssetResponse> CreateAsync(Guid companyId, CreateFixedAssetRequest request, string createdBy);
    Task<FixedAssetResponse> GetByIdAsync(Guid companyId, Guid assetId);
    /// <summary>รอบ 250 WP-03 (D §2.5): กรองชนิด/สถานะ/รอตรวจที่เซิร์ฟเวอร์ + แบ่งหน้าจริง — เดิมหน้าเว็บดึง 200 แถวแล้วกรองเอง ⇒ บริษัทที่มี
    /// สินทรัพย์ &gt; 200 รายการเห็นไม่ครบเงียบ ๆ · null = ไม่กรอง</summary>
    Task<PagedResponse<FixedAssetResponse>> GetAllAsync(Guid companyId, PagedRequest request,
        global::Accounting.Models.Enums.AssetType? assetType = null,
        global::Accounting.Models.Enums.AssetStatus? status = null,
        bool? needsReview = null);
    /// <summary>รอบ 250 WP-03: ตัวนับบนแท็บชนิด (นับทั้งฐานตามคำค้น/สถานะ ไม่ใช่เฉพาะหน้าที่โหลด) + จำนวนรอตรวจ</summary>
    Task<FixedAssetTypeCounts> GetTypeCountsAsync(Guid companyId, string? search,
        global::Accounting.Models.Enums.AssetStatus? status = null);
    /// <summary>สินทรัพย์ที่ระบบสร้างอัตโนมัติจาก PV/PI ที่ผู้ใช้ยังไม่ "ยืนยัน"
    /// (NeedsReview=true) — บังคับให้ผู้ใช้เติมรายละเอียดก่อนใช้งานจริง.</summary>
    Task<List<FixedAssetResponse>> GetNeedsReviewAsync(Guid companyId);

    /// <summary>สินทรัพย์ที่ลงทะเบียนจากเอกสารใบนี้ (`SourceDocumentId`)
    ///
    /// ใช้ตอบคำถามที่หน้ารายละเอียดเอกสารตอบไม่ได้มาก่อน: "ใบนี้ลงเป็นสินทรัพย์
    /// แล้วหรือยัง" — เดิมระบบสร้าง DRAFT ให้อัตโนมัติตอนอนุมัติ แต่แจ้งด้วย toast
    /// ที่หายไปใน 12 วินาที ⇒ ผู้ใช้ที่พลาดหรือเปิดใบย้อนหลังไม่มีทางรู้เลยว่า
    /// ของไปโผล่ที่ไหน (และไม่รู้ว่าต้องไปยืนยัน)</summary>
    Task<List<FixedAssetResponse>> GetByDocumentAsync(Guid companyId, Guid documentId);
    /// <summary>บรรทัดของเอกสารเทียบกับทะเบียน: บรรทัดไหนเข้าข่ายสินทรัพย์ · มีทะเบียนแล้วไหม —
    /// ให้แผงสินทรัพย์บนหน้าเอกสารวาดปุ่ม “ขึ้นทะเบียนจากบรรทัดนี้” เฉพาะที่ยังขาด</summary>
    Task<DocumentAssetLinesResponse> GetDocumentAssetLinesAsync(Guid companyId, Guid documentId);
    /// <summary>ขึ้นทะเบียนจากเอกสารที่โพสต์แล้ว (ทั้งใบ หรือเฉพาะบรรทัด) — ตรรกะเดียวกับที่รันตอนอนุมัติ</summary>
    Task<RegisterFromDocumentResult> RegisterFromDocumentAsync(Guid companyId, Guid documentId, Guid? onlyLineId, string actor);
    /// <summary>เวอร์ชันรับ entity — DocumentService เรียกตอนอนุมัติ (มี doc โหลดอยู่แล้ว)</summary>
    Task<RegisterFromDocumentResult> RegisterFromDocumentAsync(Guid companyId, Document doc, Guid? onlyLineId, string actor, bool skipIfScanRegistered);
    /// <summary>ลบสินทรัพย์ที่ลงทะเบียนผิด — อนุญาตเฉพาะ asset ที่ยังไม่คิดค่า
    /// เสื่อมจริง (NeedsReview=true / ไม่มี posted depreciation). ต้นทุนมาจาก
    /// เอกสารต้นทาง (ไม่ใช่ acquisition JE ของ asset) จึงไม่กระทบ GL. asset ที่
    /// คิดค่าเสื่อมแล้วต้องใช้ Dispose/WriteOff แทน (ลบไม่ได้).</summary>
    Task DeleteAsync(Guid companyId, Guid assetId);
    Task<FixedAssetResponse> UpdateAsync(Guid companyId, Guid assetId, UpdateFixedAssetRequest request);
    Task<FixedAssetResponse> DisposeAsync(Guid companyId, Guid assetId, DisposeAssetRequest request, string performedBy);
    Task<FixedAssetResponse> WriteOffAsync(Guid companyId, Guid assetId, WriteOffAssetRequest request, string performedBy);
    Task<FixedAssetResponse> AdjustUsefulLifeAsync(Guid companyId, Guid assetId, AdjustUsefulLifeRequest request, string performedBy);
    Task<List<DepreciationResponse>> GetDepreciationsAsync(Guid companyId, Guid assetId);
    Task<List<DepreciationResponse>> CalculateDepreciationAsync(Guid companyId, CalculateDepreciationRequest request, string performedBy);
    Task<RevaluationResponse> RevalueAsync(Guid companyId, Guid assetId, RevalueAssetRequest request, string performedBy);
    Task<List<AssetCategoryResponse>> GetCategoriesAsync(Guid companyId);
    Task<AssetRegisterReport> GetAssetRegisterReportAsync(Guid companyId);
    Task<DepreciationScheduleReport> GetDepreciationScheduleAsync(Guid companyId, Guid assetId);
    Task<ImportFixedAssetsResult> ImportAsync(Guid companyId, List<ImportFixedAssetRow> rows, string createdBy);

    // ═══ รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md) ═══
    /// <summary>บรรทัดเอกสารซื้อที่ผูกเป็นต้นทุนได้ (มัดจำ/งานระหว่างก่อสร้าง/ผังสินทรัพย์ · ยังเหลือยอด) — ค้นด้วยเลขเอกสาร/ผู้ขาย/รายการ หรือเฉพาะใบ</summary>
    Task<List<LinkableCostLine>> GetLinkableCostLinesAsync(Guid companyId, string? search, Guid? documentId);
    /// <summary>สร้างสินทรัพย์ระหว่างจัดหาจากหลายบรรทัด (ยังไม่คิดค่าเสื่อม · ไม่ลง JE)</summary>
    Task<AttachCostSourcesResult> CreateFromSourcesAsync(Guid companyId, CreateAssetFromSourcesRequest request, string actor);
    /// <summary>ใบที่เป็นต้นทุนของสินทรัพย์นี้ + สถานะ + ปัญหาที่ตรวจพบ</summary>
    Task<AssetCostSourcesResponse> GetCostSourcesAsync(Guid companyId, Guid assetId);
    /// <summary>ผูกบรรทัดเพิ่ม — ระหว่างจัดหา: สะสมต้นทุน · โอนแล้ว: ลง JE ย้ายผัง + ปรับต้นทุนไปข้างหน้า</summary>
    Task<AttachCostSourcesResult> AttachCostSourcesAsync(Guid companyId, Guid assetId, AttachCostSourcesRequest request, string actor);
    /// <summary>ปลดแถวที่ยังไม่โอน (ต้องมีเหตุผล · แถวยังอยู่เป็นประวัติ)</summary>
    Task<AssetCostSourcesResponse> ReleaseCostSourceAsync(Guid companyId, Guid assetId, Guid sourceId, ReleaseCostSourceRequest request, string actor);
    /// <summary>โอนเป็นสินทรัพย์พร้อมใช้: JE Dr ผังสินทรัพย์ / Cr ผังต้นทาง · ค่าเสื่อมเริ่มจากวันพร้อมใช้</summary>
    Task<FixedAssetResponse> PlaceInServiceAsync(Guid companyId, Guid assetId, PlaceInServiceRequest request, string actor);
}
