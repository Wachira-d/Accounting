using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Company;

public record CreateCompanyRequest(
    [property: Required, StringLength(100)] string Name,
    string? NameEn,
    [property: Required, RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    string? BranchName,
    BusinessType BusinessType,
    IndustryType IndustryType = IndustryType.General,
    string? JuristicId = null,
    bool IsVatRegistered = false,
    [property: Range(0, 100)] decimal VatRate = 7m,
    bool IsWhtRegistered = true,
    bool IsSocialSecurityRegistered = false,
    string? SocialSecurityAccountNo = null,
    string? Address = null,
    string? BuildingNumber = null,
    string? BuildingName = null,
    string? Moo = null,
    string? StreetName = null,
    string? SubDistrict = null,
    string? District = null,
    string? Province = null,
    string? PostalCode = null,
    string? Phone = null,
    string? Fax = null,
    string? Email = null,
    string? Website = null,
    int FiscalYearStartMonth = 1);

public record UpdateCompanyRequest(
    [property: StringLength(100)] string? Name,
    string? NameEn,
    [property: RegularExpression(@"^\d{13}$", ErrorMessage = "เลขประจำตัวผู้เสียภาษีต้องเป็นตัวเลข 13 หลัก")] string? TaxId,
    [property: RegularExpression(@"^\d{5}$", ErrorMessage = "รหัสสาขาต้องเป็นตัวเลข 5 หลัก")] string? BranchCode,
    string? BranchName,
    BusinessType? BusinessType,
    IndustryType? IndustryType,
    string? JuristicId,
    bool? IsVatRegistered,
    [property: Range(0, 100)] decimal? VatRate,
    bool? IsWhtRegistered,
    bool? IsSocialSecurityRegistered,
    string? SocialSecurityAccountNo,
    string? Address,
    string? BuildingNumber,
    string? BuildingName,
    string? Moo,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Phone,
    string? Fax,
    string? Email,
    string? Website,
    int? FiscalYearStartMonth,
    bool? IsSetupComplete,
    // ที่อยู่ภาษาอังกฤษ (โหมดเอกสาร en) — null = ไม่เปลี่ยน, "" = ล้าง (กลับไป
    // ใช้ตัวถอดอักษรอัตโนมัติ), อื่น ๆ = ตั้งค่า
    string? AddressEn = null,
    // ภ.พ.06 — สิทธิ์ออกใบกำกับภาษีอย่างย่อ (§86/6) · ธง = เจตนา · วันที่ = หลักฐาน
    // ต้องมีทั้งคู่ถึงจะออกได้ (ดู Helpers/PosSlipHeader) · null = ไม่แตะ
    bool? IsRetailApproved = null,
    DateTime? PhoR06ApprovedDate = null,
    // ทุนจดทะเบียนที่ชำระแล้ว — ใช้ตัดสินอัตรา CIT (SME ≤ 5 ล.) และเพดาน
    // ค่ารับรอง §65 ตรี(4) · null = ไม่แตะ · 0 = "ยังไม่ได้กรอก" (ดู CitRateTable)
    decimal? PaidUpCapital = null);

public record CompanyResponse(
    Guid Id,
    string Name,
    string? NameEn,
    string TaxId,
    string? BranchCode,
    string? BranchName,
    BusinessType BusinessType,
    IndustryType IndustryType,
    CompanyStatus Status,
    string? JuristicId,
    bool IsVatRegistered,
    decimal VatRate,
    bool IsWhtRegistered,
    bool IsSocialSecurityRegistered,
    string? SocialSecurityAccountNo,
    string? Address,
    string? BuildingNumber,
    string? BuildingName,
    string? Moo,
    string? StreetName,
    string? SubDistrict,
    string? District,
    string? Province,
    string? PostalCode,
    string? Phone,
    string? Fax,
    string? Email,
    string? Website,
    int FiscalYearStartMonth,
    bool IsSetupComplete,
    SubscriptionSummary? Subscription,
    string? MyRole = null,   // current requesting user's role in this company
    string? AddressEn = null,
    // เก็บแล้วต้อง echo กลับ — ไม่งั้นเปิดหน้าตั้งค่าแล้วติ๊กหาย
    bool IsRetailApproved = false,
    DateTime? PhoR06ApprovedDate = null,
    // เก็บแล้วต้อง echo กลับ — ฟิลด์นี้ถูก "อ่าน" โดยสูตรภาษี 4 จุดมาตลอด
    // แต่ไม่เคยมีทั้งช่องกรอกและช่อง echo ⇒ เป็น 0 ทุกบริษัท (รอบ 182)
    decimal PaidUpCapital = 0m);

public record SubscriptionSummary(
    SubscriptionPlan Plan,
    SubscriptionStatus Status,
    DateTime EndDate,
    TrialSummary? Trial);

public record TrialSummary(
    TrialStatus Status,
    DateTime TrialEndDate,
    int DaysRemaining,
    int ExtensionsUsed,
    int MaxExtensions);

public record AddCompanyUserRequest(
    string Email,
    UserRole Role);

public record UpdateUserRoleRequest(UserRole Role);
public record UpdateMemberNameRequest(string FullName);

public record CompanyMemberResponse(
    Guid UserId,
    string FullName,
    string Email,
    string? Phone,
    UserRole Role,
    Guid? CompanyRoleId,
    string? CompanyRoleName,
    DateTime JoinedAt,
    DateTime? LastLoginAt,
    UserStatus Status);
