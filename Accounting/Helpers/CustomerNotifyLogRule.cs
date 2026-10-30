using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้านรอบ 212 S3 — กติกาของบันทึกการส่ง <c>CustomerNotifyLogs</c> (ตัวเดียว):
/// <list type="bullet">
/// <item><see cref="Redact"/>: หมายเหตุ (ข้อความผิดพลาดจากผู้ให้บริการอีเมล/LINE) อาจมีอีเมล/เบอร์ของลูกค้า ⇒ ปิดบังก่อนเก็บ (ตารางนี้ต้องไม่เป็นสำเนา PII ที่สาม · PDPA ม.37)</item>
/// <item><see cref="PurgeCutoff"/>: เก็บ <see cref="RetentionDays"/> วันแล้วลบจริง (ไม่ใช่เอกสารบัญชี · ไม่มี legal hold) — งานรายคืน <c>PdpaRetentionPurgeJob</c> (JobLock เดิม)</item>
/// <item>DSR ลบ (<c>PdpaService.ApplyCmsErasureAsync</c>) ลบแถวของเป้าหมาย (สมาชิก/การจอง/ออเดอร์/ฟอร์ม) ทันที</item>
/// </list>
/// </summary>
public static class CustomerNotifyLogRule
{
    public const int RetentionDays = 90;
    public const int MaxNoteLength = 500;

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    // เบอร์โทร: ตัวเลข 9–12 หลัก มีขีด/เว้นวรรค/+ คั่นได้ (ไม่แตะรหัสข้อผิดพลาดสั้นอย่าง "535")
    private static readonly Regex Phone = new(@"(?<!\d)\+?\d(?:[- \t]?\d){8,11}(?!\d)", RegexOptions.Compiled);
    // LINE userId (U/C/R + 32 hex)
    private static readonly Regex LineId = new(@"\b[UCR][0-9a-f]{32}\b", RegexOptions.Compiled);

    /// <summary>ปิดบังอีเมล/เบอร์/LINE userId + ตัดความยาว · null = null</summary>
    public static string? Redact(string? note)
    {
        if (string.IsNullOrEmpty(note)) return note;
        var s = Email.Replace(note, "[อีเมล]");
        s = LineId.Replace(s, "[LINE]");
        s = Phone.Replace(s, "[เบอร์]");
        return s.Length > MaxNoteLength ? s[..MaxNoteLength] : s;
    }

    /// <summary>แถวที่สร้างก่อนเวลานี้ลบได้</summary>
    public static DateTime PurgeCutoff(DateTime nowUtc) => nowUtc.AddDays(-RetentionDays);
}
