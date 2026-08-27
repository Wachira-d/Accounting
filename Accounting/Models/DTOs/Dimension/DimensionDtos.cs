using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Dimension;

public record CreateDimensionRequest(
    string Code, string Name, string? NameEn,
    DimensionType DimensionType, Guid? ParentId,
    string? Description, string? ManagerName,
    string? ManagerEmail, decimal? AnnualBudget);

public record UpdateDimensionRequest(
    string? Name, string? NameEn, string? Description,
    string? ManagerName, string? ManagerEmail,
    decimal? AnnualBudget, bool? IsActive, int? SortOrder);

public record DimensionResponse(
    Guid Id, string Code, string Name, string? NameEn,
    DimensionType DimensionType, Guid? ParentId, int Level,
    string? Description, string? ManagerName,
    decimal? AnnualBudget, bool IsActive,
    List<DimensionResponse>? Children);

public record DimensionAllocationRequest(
    Guid DimensionId, decimal? Amount, decimal? Percent);

public record DimensionAllocationResponse(
    Guid DimensionId, string DimensionCode, string DimensionName,
    DimensionType DimensionType, decimal? AllocatedAmount,
    decimal? AllocatedPercent);

public record DimensionPnLResponse(
    Guid DimensionId, string DimensionName,
    DateTime FromDate, DateTime ToDate,
    decimal TotalRevenue, decimal TotalExpenses,
    decimal NetIncome, List<DimensionPnLLine> Lines);

public record DimensionPnLLine(
    string AccountCode, string AccountName, decimal Amount);

public record DimensionSummaryResponse(
    Guid DimensionId, string Code, string Name,
    DimensionType DimensionType, decimal TotalRevenue,
    decimal TotalExpenses, decimal NetIncome,
    decimal? BudgetAmount, decimal? Variance);

public record CreateBranchRequest(
    string Code, string Name, string? NameEn,
    string? Address, string? SubDistrict, string? District,
    string? Province, string? PostalCode, string? Phone,
    string? Email, string? TaxBranchCode, bool IsHeadOffice,
    string? ManagerName);

/// <summary>
/// แก้ไขสาขา — ทุกช่องเป็น null ได้ (null = ไม่แตะ)
/// ⚠️ ครอบ **ทุกฟิลด์ที่ฟอร์มรับ** เจตนา: defect class "เก็บแล้วต้อง echo กลับ"
/// เดิมรับแค่ 8 ช่อง ⇒ ที่อยู่แยกตำบล/อำเภอ/จังหวัด/ไปรษณีย์ แก้ไม่ได้เลย
/// (กด "บันทึก" แล้วไม่มีผลเงียบ ๆ — silent no-op)
/// </summary>
public record UpdateBranchRequest(
    string? Code, string? Name, string? NameEn, string? Address,
    string? SubDistrict, string? District, string? Province,
    string? PostalCode, string? Phone, string? Email,
    string? TaxBranchCode, bool? IsHeadOffice,
    string? ManagerName, bool? IsActive);

/// <summary>
/// ข้อมูลสาขาที่ส่งกลับ — ต้อง echo **ทุกฟิลด์ที่รับเข้า** ไม่งั้นฟอร์มแก้ไข
/// prefill ไม่ได้ และผู้ใช้เปิดแก้แล้วบันทึกทับด้วยค่าว่าง
/// </summary>
public record BranchResponse(
    Guid Id, string Code, string Name, string? NameEn,
    string? Address, string? SubDistrict, string? District,
    string? Province, string? PostalCode, string? Phone,
    string? Email, string? TaxBranchCode,
    bool IsHeadOffice, bool IsActive, string? ManagerName);
