using System.ComponentModel.DataAnnotations;

namespace Accounting.Models.DTOs.Company;

public record CreateCompanyRoleRequest(
    [property: Required, StringLength(100)] string Name,
    string? Description,
    string? Color,
    string? Icon,
    List<string>? AllowedMenuIds);

public record UpdateCompanyRoleRequest(
    [property: StringLength(100)] string? Name,
    string? Description,
    string? Color,
    string? Icon,
    int? SortOrder,
    List<string>? AllowedMenuIds);

public record CompanyRoleResponse(
    Guid Id,
    string Name,
    string? Description,
    string Color,
    string Icon,
    bool IsSystemRole,
    int SortOrder,
    int MemberCount,
    List<string> AllowedMenuIds);

public record AssignCustomRoleRequest(
    Guid RoleId);

public record MyPermissionsResponse(
    string RoleName,
    bool IsOwnerOrAdmin,
    List<string> AllowedMenuIds,
    // Owner-level menu hide list — applies to EVERYONE in this company
    // regardless of their role. Subtracted from the rendered sidebar
    // by layout.js. Owner controls via /pages/settings-features.html.
    List<string>? OwnerHiddenMenuIds = null,
    /// <summary>
    /// **แอดมินของแพลตฟอร์ม** (เจ้าของระบบ NextAcc) — คนละเรื่องกับ
    /// <see cref="IsOwnerOrAdmin"/> ซึ่งแปลว่า "เจ้าของ/แอดมิน **ของบริษัทนี้**"
    /// คือลูกค้าทุกรายที่เปิดบริษัทเอง
    ///
    /// ⚠️ เมนูที่แสดงข้อมูล **ข้ามบริษัท** (รายงานการใช้งาน AI · การใช้งานรายบริษัท)
    /// ต้อง gate ด้วยธงตัวนี้เท่านั้น — เดิม gate ด้วย IsOwnerOrAdmin ⇒ ลูกค้าทุกราย
    /// เห็นเมนูของแพลตฟอร์มโผล่ในแถบซ้ายของตัวเอง (API ยัง 403 อยู่ ข้อมูลไม่รั่ว
    /// แต่เป็นการเปิดเผยหน้าจอภายในและอยู่ห่างจากการรั่วจริงแค่ก้าวเดียว)
    /// </summary>
    bool IsSystemAdmin = false);
