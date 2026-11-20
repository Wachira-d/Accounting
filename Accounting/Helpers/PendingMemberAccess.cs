namespace Accounting.Helpers;

/// <summary>
/// รอบ 277 (งาน B · คำตัดสิน 277-4) — สมาชิกที่ "เข้าร่วมแล้วแต่ยังไม่ได้รับสิทธิ์" (<c>CompanyUser.AccessPending = true</c>):
/// ตอบรับคำเชิญที่ผู้เชิญไม่ได้เลือกบทบาท ⇒ เห็นได้แค่หน้าต้อนรับจนกว่าเจ้าของตั้งบทบาท (ค่าเริ่มต้นที่ปลอดภัย = ไม่มีสิทธิ์)
///
/// <para>ตัวตัดสินเดียวของด่าน <c>TenantAccessMiddleware</c> (ทุก API ที่มี companyId ใน route/header) — ผ่านได้เฉพาะ endpoint ในรายการ
/// (สิทธิ์ของฉัน — หน้าเว็บต้องรู้ว่าตัวเองอยู่สถานะนี้) · ที่เหลือ 403 พร้อมข้อความเดียวกัน · เจ้าของ "ตั้งบทบาท" ที่หน้าจัดการทีม
/// = ปลดสถานะนี้ (<c>CompanyService.UpdateUserRoleAsync</c> / <c>RolePermissionService.AssignRoleAsync</c>)</para>
/// </summary>
public static class PendingMemberAccess
{
    public const string RuleCode = "TEAM-ACCESS-PENDING";

    /// <summary>path ที่สมาชิกสถานะนี้เรียกได้ (เทียบท้าย path · ไม่สนตัวพิมพ์) — GET เท่านั้น</summary>
    private static readonly string[] AllowedSuffixes = { "/roles/my-permissions" };

    public static bool IsAllowed(string? method, string? path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(path)) return false;
        var p = path.TrimEnd('/');
        return AllowedSuffixes.Any(s => p.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public const string DeniedMessage =
        "คุณเข้าร่วมบริษัทนี้แล้ว แต่เจ้าของบริษัทยังไม่ได้กำหนดบทบาท/สิทธิ์ให้คุณ — เมื่อเจ้าของตั้งสิทธิ์ที่หน้า “จัดการทีม” แล้ว "
        + "รีเฟรชหน้านี้เพื่อเริ่มใช้งาน (ระบบแจ้งเจ้าของแล้วว่าคุณตอบรับคำเชิญ)";

    /// <summary>แถว audit "เจ้าของตั้งสิทธิ์ = ปลดสถานะยังไม่มีสิทธิ์" (ตัวเดียวของทั้งเปลี่ยนบทบาทมาตรฐานและผูกบทบาทกำหนดเอง)</summary>
    public static Accounting.Models.Entities.AuditLog GrantAudit(Guid companyId, Guid actorId, Guid targetUserId, string grantedRole) => new()
    {
        CompanyId = companyId,
        UserId = actorId,
        Action = Accounting.Models.Enums.AuditAction.Update,
        EntityType = "CompanyUser",
        EntityId = targetUserId.ToString(),
        OldValues = System.Text.Json.JsonSerializer.Serialize(new { accessPending = true }),
        NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "grant-access", accessPending = false, role = grantedRole, ruleCode = RuleCode }),
        Timestamp = DateTime.UtcNow,
    };

    /// <summary>ข้อความบนหน้าต้อนรับ (หน้าเว็บแสดงอย่างเดียว)</summary>
    public static string Notice(string? companyName)
        => $"ยินดีต้อนรับสู่ {(string.IsNullOrWhiteSpace(companyName) ? "บริษัท" : companyName)} — คุณเข้าร่วมแล้ว รอเจ้าของบริษัทกำหนดบทบาทและสิทธิ์ให้ · "
           + "ระหว่างนี้ยังเปิดดูข้อมูลของบริษัทไม่ได้ · เมื่อได้รับสิทธิ์แล้วรีเฟรชหน้านี้ หรือสลับไปบริษัทอื่นของคุณได้จากตัวเลือกบริษัทด้านบน";
}
