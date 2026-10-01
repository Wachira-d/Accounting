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
}
