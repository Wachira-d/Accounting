namespace Accounting.Helpers;

/// <summary>เล่มเลขเอกสารต่อสาขา (§86/4 ยอมรับเลขรันแยกต่อสาขา) — ตัวกำหนด "ตัวนำหน้าของเล่ม" ตัวเดียวของ
/// <see cref="DocumentNumberGenerator"/> (รอบ 201 ทีม IN · A-IN4 · คำตัดสินข้อ 35)
///
/// ═══ สถานะ ═══
/// เครื่องออกเลขรับรหัสสาขาเป็นพารามิเตอร์เสริมท้ายแล้ว — <b>ไม่ส่ง = เล่มเดิมของบริษัททุกประการ</b> (เลขเดิม · ล็อกเดิม) ·
/// สวิตช์ "แยกเล่มต่อสาขา" ระดับบริษัท<b>ยังไม่เปิด</b>: ผู้เรียก 19 จุดอยู่ในไฟล์ของทีมอื่น (เอกสาร/integration/POS/ออกแทน) —
/// เปิดสวิตช์ก่อนทุกทางเข้าส่งสาขา = ใบของสาขาเดียวกันกระจายสองเล่ม (เลขไม่ต่อเนื่องต่อเล่ม) จึงเป็น 📋 รอบถัดไป
///
/// ═══ กติกา ═══
/// • สาขาว่าง / <c>00000</c> (สำนักงานใหญ่) = เล่มของบริษัท (ตัวนำหน้าเดิม) — เปิดแยกเล่มแล้วสำนักงานใหญ่ไม่ต้องเริ่มเลขใหม่
/// • สาขาอื่น = <c>{PREFIX}-{รหัสสาขา 5 หลัก}</c> ⇒ เลขเต็ม <c>TIV-00002-20260615-0001</c> · ไม่ทับเล่มบริษัท
///   (<c>TIV-20260615-</c> ไม่ใช่คำนำหน้าของ <c>TIV-00002-…</c>) และได้ล็อก advisory คนละตัว
/// • รหัสสาขาผิดรูป = ปฏิเสธดัง — ห้ามตกไปเล่มสำนักงานใหญ่เงียบ ๆ (ใบสาขาจะไปอยู่ในเล่มผิด)
/// </summary>
public static class DocumentNumberBook
{
    public const string HeadOfficeBranchCode = "00000";

    /// <summary>ตัวนำหน้าของเล่มสำหรับ <paramref name="typePrefix"/> และสาขา <paramref name="branchCode"/></summary>
    public static string BookPrefix(string typePrefix, string? branchCode)
    {
        var code = branchCode?.Trim();
        if (string.IsNullOrEmpty(code) || code == HeadOfficeBranchCode) return typePrefix;
        if (code.Length != 5 || !code.All(char.IsAsciiDigit))
            throw new BusinessRuleException(
                $"รหัสสาขา “{code}” ไม่ถูกต้อง — ต้องเป็นตัวเลข 5 หลัก (ประกาศอธิบดีฯ ฉบับที่ 199) จึงจะออกเลขเล่มของสาขาได้",
                "DOC-NUMBER-BRANCH");
        return $"{typePrefix}-{code}";
    }
}
