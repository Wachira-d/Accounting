namespace Accounting.Helpers;

/// <summary>
/// **"ผู้ทำ" ของบรรทัดรอบโอนสำหรับด่าน SoD ของการลงบัญชี** (รอบ 201 ทีม ST · A-ST7 · review198-S3 S3-11(3)) — ผู้สร้างบรรทัด (ผู้นำเข้า/ผู้เติมไฟล์) <b>และ</b>
/// ผู้ตัดสินการจับคู่/จัดประเภทของบรรทัด (<c>SettlementLine.DecidedBy</c>) — ทั้งคู่กำหนดเนื้อหาของเอกสารที่ระบบออกให้ ⇒ คนกดลงบัญชีต้องไม่ใช่คนเดียวกัน
/// (บริษัทที่เปิดแยกหน้าที่) · ค่าว่าง (ระบบตัดสิน · บรรทัดเก่า) ไม่อยู่ในผล · ส่งเข้า <c>SettlementPostingGate.SodSelfApproval</c>
/// ตามพารามิเตอร์เดิมของเจ้าของเมธอด (ทีม TX) — ไม่มีสูตร SoD ชุดที่สอง · pure
/// </summary>
public static class SettlementLineMakers
{
    public static IReadOnlyList<string> Of(IEnumerable<(string? CreatedBy, string? DecidedBy)> lines)
        => lines.SelectMany(l => new[] { l.CreatedBy, l.DecidedBy })
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// **บทบาทที่ผู้กดลงบัญชีชน** (ฝ่ายค้าน ST-X3) — ผู้สร้างรอบโอน · ผู้เติมไฟล์เข้ารอบ (ผู้สร้างบรรทัดที่ไม่ใช่ผู้สร้างรอบ) · ผู้ตัดสินจับคู่/จัดประเภทบรรทัด ·
    /// ผู้สร้างรอบไม่รู้ (ค่าว่าง) = บทบาท "ยืนยันไม่ได้" (ด่านเดิมบล็อกกรณีนี้) · ข้อความเท่านั้น — การตัดสินบล็อก/ไม่บล็อกยังเป็นของ <c>SodSelfApproval</c> · pure
    /// </summary>
    public static IReadOnlyList<string> RolesOf(string? batchCreatedBy, IEnumerable<(string? CreatedBy, string? DecidedBy)> lines, Guid postingUserId)
    {
        var me = postingUserId.ToString();
        bool Is(string? x) => !string.IsNullOrWhiteSpace(x) && string.Equals(x.Trim(), me, StringComparison.OrdinalIgnoreCase);
        var list = lines.ToList();
        var roles = new List<string>();
        if (string.IsNullOrWhiteSpace(batchCreatedBy)) roles.Add("ผู้ที่ระบบยืนยันไม่ได้ว่าไม่ใช่ผู้สร้าง (รอบโอนไม่มีชื่อผู้สร้าง)");
        else if (Is(batchCreatedBy)) roles.Add("ผู้สร้างรอบโอน");
        if (!Is(batchCreatedBy) && list.Any(l => Is(l.CreatedBy))) roles.Add("ผู้เติมไฟล์เข้ารอบ");
        if (list.Any(l => Is(l.DecidedBy))) roles.Add("ผู้ตัดสินการจับคู่/จัดประเภทบรรทัด");
        return roles;
    }
}
