namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 (C-M5 ส่วน UX · คำตัดสิน 259-4-8) — บรรทัดที่ผู้ใช้กด "➕ เพิ่มบรรทัด" แล้วยังไม่ได้กรอก · pure
/// <para>เดิม: ปุ่มเพิ่มบรรทัดสร้างแถว 1 × 0.00 (คำอธิบายว่าง) ⇒ ลืมกรอกแล้วกดสร้าง = เอกสารมีรายการว่างยอด 0 ที่ไม่มีบนกระดาษ ·
/// ตอนนี้: แถวใหม่ไม่มีจำนวน/ราคา/ยอด (ช่องว่างจริง ให้ผู้ใช้กรอก) และตอนสร้างเอกสาร แถวว่าง = หยุดพร้อมบอกว่าต้องกรอกหรือลบ
/// (ไม่ทิ้งเงียบ — ผู้ใช้ตั้งใจเพิ่ม · ไม่แต่งค่า)</para>
/// <para>แถวว่าง = ไม่มีคำอธิบาย <b>และ</b> ไม่มียอด (null/0) — แถวที่มียอดแต่ไม่มีคำอธิบาย (engine อ่านชื่อไม่ได้) ไม่ใช่แถวว่าง (ทางเดิม)</para>
/// </summary>
public static class OcrBlankLine
{
    public const string RuleCode = "OCR-BLANK-LINE";

    private static bool IsBlank(string? description, decimal? amount)
        => string.IsNullOrWhiteSpace(description) && (amount is null || amount == 0m);

    public static int Count(IEnumerable<(string? Description, decimal? Amount)> lines)
        => lines.Count(l => IsBlank(l.Description, l.Amount));

    public static string Message(int count)
        => $"มีบรรทัดรายการที่เพิ่มไว้แต่ยังว่าง {count} บรรทัด — กรอกรายละเอียด/จำนวน/ราคา หรือกด ✕ ลบบรรทัดนั้นที่หน้าตรวจสอบ แล้วกดสร้างเอกสารอีกครั้ง";
}
