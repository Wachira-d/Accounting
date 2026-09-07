using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;

namespace Accounting.Services.Interfaces;

public interface IFixedAssetService
{
    Task<FixedAssetResponse> CreateAsync(Guid companyId, CreateFixedAssetRequest request, string createdBy);
    Task<FixedAssetResponse> GetByIdAsync(Guid companyId, Guid assetId);
    Task<PagedResponse<FixedAssetResponse>> GetAllAsync(Guid companyId, PagedRequest request);
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
}
