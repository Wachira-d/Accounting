namespace Accounting.Helpers;

/// <summary>รอบ 219 ส่วน B (คำตัดสินข้อ 179): หน้าทดลองจ่ายของแพลตฟอร์ม <c>/platform-pay-test.html</c> — ใครเปิดได้ · pure</summary>
public static class PlatformPayTestPage
{
    public const string Off = "Off";
    public const string AdminsOnly = "AdminsOnly";
    public const string Public = "Public";
    public static readonly IReadOnlyList<string> Modes = new[] { Off, AdminsOnly, Public };

    /// <summary>ค่าที่เก็บ → โหมดจริง (null/ไม่รู้จัก = AdminsOnly — ค่าเริ่มต้นตามคำตัดสินเจ้าของ)</summary>
    public static string Normalize(string? stored)
        => Modes.FirstOrDefault(m => string.Equals(m, stored?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? AdminsOnly;

    /// <summary>ผู้เรียกเปิดหน้า/เริ่มทดลองจ่ายได้ไหม — null = ได้ · ข้อความ = ปฏิเสธ (404/403 ตามที่ผู้เรียกเลือก)</summary>
    public static string? AccessRefusal(string mode, bool isSystemAdmin, bool gatewayInTestMode)
    {
        if (mode == Off) return "หน้าทดลองจ่ายปิดอยู่";
        if (mode == AdminsOnly && !isSystemAdmin) return "หน้าทดลองจ่ายเปิดเฉพาะผู้ดูแลระบบ";
        if (!gatewayInTestMode) return "ช่องทางรับชำระของ NextAcc ไม่ได้อยู่โหมดทดสอบ (หรือยังไม่ได้ตั้งค่า/ปิดอยู่) — หน้าทดลองจ่ายใช้ได้เฉพาะโหมดทดสอบ";
        return null;
    }
}
