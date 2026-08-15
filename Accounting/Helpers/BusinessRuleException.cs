namespace Accounting.Helpers;

/// <summary>
/// ข้อผิดพลาดเชิงกฎธุรกิจที่ **ตั้งใจให้ผู้ใช้เห็นข้อความตรง ๆ**
/// (เช่น "ยอดลดหนี้เกินยอดใบเดิม", "ต้องอนุมัติเอกสารก่อนส่งอีเมล")
///
/// ทำไมต้องมีชนิดของตัวเอง: เดิมโค้ดโยน <see cref="InvalidOperationException"/>
/// สำหรับกฎธุรกิจ แล้ว ExceptionMiddleware ส่ง <c>exception.Message</c> ออกไป
/// เป็น HTTP 400 ตรง ๆ — แต่ EF/LINQ/framework ก็โยน
/// <c>InvalidOperationException</c> เหมือนกัน ("Sequence contains no elements",
/// "Nullable object must have a value") ⇒ ข้อความภายในระบบรั่วถึง client และ
/// ผู้ใช้เห็น error ที่ไม่มีความหมาย
///
/// โค้ดใหม่ทุกที่ที่ต้องการสื่อสารกฎธุรกิจกับผู้ใช้ **ให้โยนชนิดนี้**
/// (ดู CLAUDE.md กฎเหล็ก #4 E — "business error โยน exception ชนิดเฉพาะ")
/// </summary>
public class BusinessRuleException : Exception
{
    /// <summary>รหัสกฎ (ถ้ามี) — เช่น "RD-86/10-CAP" ใช้ผูกกับ audit/เอกสารอ้างอิง</summary>
    public string? RuleCode { get; }

    /// <summary>HTTP status ที่ต้องการ (default 400 Bad Request)</summary>
    public int StatusCode { get; }

    public BusinessRuleException(string message, string? ruleCode = null, int statusCode = 400)
        : base(message)
    {
        RuleCode = ruleCode;
        StatusCode = statusCode;
    }

    public BusinessRuleException(string message, Exception inner, string? ruleCode = null, int statusCode = 400)
        : base(message, inner)
    {
        RuleCode = ruleCode;
        StatusCode = statusCode;
    }
}
