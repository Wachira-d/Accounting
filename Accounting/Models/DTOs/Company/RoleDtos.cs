using System.ComponentModel.DataAnnotations;

namespace Accounting.Models.DTOs.Company;

public record CreateCompanyRoleRequest(
    [property: Required, StringLength(100)] string Name,
    string? Description,
    string? Color,
    string? Icon,
    List<string>? AllowedMenuIds,
    /// <summary>รอบ 276 — ผู้ถือบทบาทนี้เห็นเฉพาะเอกสารที่ตัวเองสร้าง / สร้าง-แก้ได้เฉพาะร่าง</summary>
    bool OwnDocumentsOnly = false,
    bool DraftDocumentsOnly = false);

public record UpdateCompanyRoleRequest(
    [property: StringLength(100)] string? Name,
    string? Description,
    string? Color,
    string? Icon,
    int? SortOrder,
    List<string>? AllowedMenuIds,
    /// <summary>รอบ 276 — null = ไม่เปลี่ยน</summary>
    bool? OwnDocumentsOnly = null,
    bool? DraftDocumentsOnly = null);

public record CompanyRoleResponse(
    Guid Id,
    string Name,
    string? Description,
    string Color,
    string Icon,
    bool IsSystemRole,
    int SortOrder,
    int MemberCount,
    List<string> AllowedMenuIds,
    /// <summary>รอบ 276 — เก็บแล้วต้อง echo</summary>
    bool OwnDocumentsOnly = false,
    bool DraftDocumentsOnly = false);

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
    bool IsSystemAdmin = false,
    /// <summary>รอบ 201 ทีม GW (A-GW10): เมนูที่ผู้ใช้<b>ไม่มีสิทธิ์</b>ตามคีย์ perm:* ของหน้านั้น (ตาราง <c>MenuPermissionMap.Keys</c> · รอบ 277 รวมแดชบอร์ด/รายงานผู้บริหาร) —
    /// layout.js ซ่อนเมนูเหล่านี้ (เดิมเห็นเมนู กดแล้ว 403) · null/ว่าง = ไม่มีเมนูที่ต้องซ่อนด้วยเหตุนี้ · เจ้าของ/แอดมินไม่ถูกตัด</summary>
    List<string>? PermissionDeniedMenuIds = null,
    /// <summary>รอบ 276 — ค่าจำกัดเอกสารที่มีผล (ผู้ใช้ ∪ บทบาท · <c>DocumentRestrictionPolicy</c>) · null = ไม่ถูกจำกัด ·
    /// layout.js: เห็นเฉพาะของตัวเอง ⇒ แสดงเฉพาะเมนูใน <see cref="MyDocumentRestriction.AllowedMenuIds"/> + แถบป้าย</summary>
    MyDocumentRestriction? DocumentRestriction = null,
    /// <summary>รอบ 277 — เข้าร่วมบริษัทแล้วแต่เจ้าของยังไม่ตั้งบทบาท (<c>CompanyUser.AccessPending</c>) ⇒ ทุก API ของบริษัท 403 ·
    /// layout.js แสดงหน้าต้อนรับ "ยังไม่ได้รับสิทธิ์" (<c>/pages/no-access.html</c>) แทนเมนู · ข้อความจากเซิร์ฟเวอร์</summary>
    bool AccessPending = false,
    string? AccessPendingNotice = null);

/// <summary>รอบ 276 — ค่าจำกัดเอกสารของผู้ใช้ที่ล็อกอิน (เซิร์ฟเวอร์ตัดสิน · หน้าเว็บแสดงอย่างเดียว)</summary>
public sealed record MyDocumentRestriction(bool OwnDocumentsOnly, bool DraftOnly, string Notice, List<string>? AllowedMenuIds);
