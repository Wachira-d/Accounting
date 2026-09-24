namespace Accounting.Models.DTOs.Commission;

/// <summary>รอบ 193 · A04: <c>CalculationBasis</c>/<c>CalculationMethod</c> เป็น <c>string?</c> โดยตั้งใจ —
/// เดิม non-nullable ⇒ [Required] โดยปริยาย และหน้าเว็บไม่เคยส่ง ⇒ สร้างแผนไม่ได้เลย.
/// ตอนนี้ฟอร์มส่งจาก dropdown · null = ค่าเริ่มต้นจาก <see cref="Accounting.Helpers.CommissionPlanRules"/>
/// ซึ่งเป็นตัวตรวจอัตรา/ขั้นตัวเดียวของทั้งสร้างและแก้ไข</summary>
public record CreateCommissionPlanRequest(
    string Name, string? Description, string? CalculationBasis,
    string? CalculationMethod, decimal? FlatRate = null,
    bool IsActive = true, List<CommissionTierRequest>? Tiers = null);

public record CommissionTierRequest(
    decimal FromAmount, decimal? ToAmount, decimal Rate);

/// <summary>null = ไม่แก้ช่องนั้น · <c>Tiers</c> ที่ส่งมา = แทนที่ขั้นเดิมทั้งชุด · ผลรวมหลังผสานกับค่าเดิม
/// ต้องผ่าน <see cref="Accounting.Helpers.CommissionPlanRules.Validate"/> เหมือนตอนสร้าง
/// (เดิมแก้ได้แค่ชื่อ/อัตรา ขณะที่ฟอร์มเปิดให้เปลี่ยนประเภท ⇒ silent no-op)</summary>
public record UpdateCommissionPlanRequest(
    string? Name = null, string? Description = null,
    decimal? FlatRate = null, bool? IsActive = null,
    string? CalculationBasis = null, string? CalculationMethod = null,
    List<CommissionTierRequest>? Tiers = null);

public record CommissionPlanResponse(
    Guid Id, string Name, string? Description, string CalculationBasis,
    string CalculationMethod, decimal? FlatRate, bool IsActive,
    List<CommissionTierResponse> Tiers,
    // จำนวนการกำหนดแผนที่ยังมีผล — หน้าเว็บเคยอ่าน `assignedCount` ที่ไม่มีใครส่ง ⇒ "0 คน" ทุกแผน
    int AssignedCount = 0);

public record CommissionTierResponse(
    decimal FromAmount, decimal? ToAmount, decimal Rate);

public record CreateCommissionAssignmentRequest(
    Guid CommissionPlanId, Guid EmployeeId, DateTime EffectiveFrom, DateTime? EffectiveTo);

public record CommissionAssignmentResponse(
    Guid Id, Guid CommissionPlanId, string PlanName,
    Guid EmployeeId, string EmployeeName,
    DateTime EffectiveFrom, DateTime? EffectiveTo, bool IsActive);

public record CommissionCalculationResponse(
    Guid Id, Guid EmployeeId, string EmployeeName, Guid CommissionPlanId,
    string PlanName, int Year, int Month,
    decimal BaseAmount, decimal CommissionAmount, string Status, DateTime CreatedAt);

public record AssignCommissionRequest(Guid? EmployeeId, Guid? UserId, DateTime StartDate, DateTime? EndDate);

public record CommissionCalcResponse(Guid? EmployeeId, string? EmployeeName, int Year, int Month, decimal BasisAmount, decimal CommissionAmount, string Status);
