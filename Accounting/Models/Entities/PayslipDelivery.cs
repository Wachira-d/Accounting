namespace Accounting.Models.Entities;

// ===== ส่งสลิปเงินเดือนทาง LINE (self-service binding + secure token link) =====

/// <summary>
/// รหัสผูก LINE ระดับ "พนักงาน" (ไม่ใช่ผู้ใช้ระบบ) — HR สร้างรหัส 6 หลักต่อ
/// พนักงานหนึ่งคน, พนักงานเพิ่มเพื่อน LINE OA บริษัทแล้วส่งข้อความ
/// "สลิป {รหัส}" → bot จับคู่แล้วเขียน <see cref="Employee.LineUserId"/>.
/// ต่างจาก <see cref="LineBindCode"/> ตรงที่อันนั้นผูก User (บัญชีล็อกอิน)
/// ส่วนอันนี้ผูก Employee ที่ส่วนใหญ่ไม่มีบัญชีในระบบ. หมดอายุ 24 ชม.
/// consume เมื่อ match ครั้งแรก.
/// </summary>
public class EmployeeLineBindCode : TenantEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string Code { get; set; } = "";          // 6 หลัก zero-padded
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? UsedByLineUserId { get; set; }
}

/// <summary>
/// Token เข้าถึงสลิปเงินเดือนแบบสาธารณะ (ไม่ต้องล็อกอิน) — ใช้กับปุ่ม
/// "ดาวน์โหลดสลิป" ที่ส่งทาง LINE. สลิป = PII (เงินเดือน+เลขบัตร) จึงต้องเป็น
/// token สุ่มเดาไม่ได้ + หมดอายุ + เพิกถอนได้ + นับ/บันทึกการเข้าถึง
/// (PdpaPiiAccessLog ม.37(4)). Endpoint สาธารณะ resolve token → stream PDF.
/// </summary>
public class PayslipShareToken : TenantEntity
{
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }

    /// <summary>URL-safe random token (≥ 32 bytes base64url) — เดาไม่ได้.</summary>
    public string Token { get; set; } = "";

    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>ช่องทางที่สร้าง token (เช่น "LINE") — ไว้ audit.</summary>
    public string Channel { get; set; } = "LINE";

    public int AccessCount { get; set; }
    public DateTime? LastAccessedAt { get; set; }
}
