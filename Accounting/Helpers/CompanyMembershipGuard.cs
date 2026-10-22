namespace Accounting.Helpers;

/// <summary>ข้อความ/รหัสของ "ไม่ได้เป็นสมาชิกของบริษัทนี้" — ตัวเดียวของด่านอ่านข้อมูลทีม/บทบาท (รอบ 205 ทีม I) ·
/// สถานะ <b>403</b> เสมอ (ล็อกอินแล้วแต่ไม่มีสิทธิ์ ≠ ยังไม่ล็อกอิน — 401 ทำให้หน้าเว็บลบ token แล้วเด้งออกจากระบบ)</summary>
public static class CompanyMembershipGuard
{
    public const string RuleCode = "COMPANY-NOT-MEMBER";

    public const string NotMemberMessage =
        "คุณไม่ได้เป็นสมาชิกของบริษัทนี้ — สลับไปบริษัทที่คุณเป็นสมาชิก หรือขอให้เจ้าของบริษัทเชิญคุณเข้าทีม";

    public const string PlatformReadAction = "PlatformAdminRead";

    /// <summary>แถว audit "แอดมินแพลตฟอร์มอ่านข้อมูลทีมของบริษัทที่ตนไม่ได้เป็นสมาชิก" (ฝ่ายค้านรอบ 205 · แบบเดียวกับ impersonate) — ผู้เรียกเพิ่มผ่าน
    /// <c>AddChainedAuditLog</c> แล้ว SaveChanges เอง</summary>
    public static Accounting.Models.Entities.AuditLog PlatformReadAudit(Guid companyId, Guid adminUserId, string entityType, string? ip) => new()
    {
        CompanyId = companyId,
        UserId = adminUserId,
        Action = Accounting.Models.Enums.AuditAction.View,
        EntityType = entityType,
        EntityId = companyId.ToString(),
        NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = PlatformReadAction, ruleCode = RuleCode }),
        IpAddress = ip,
        Timestamp = DateTime.UtcNow,
    };
}
