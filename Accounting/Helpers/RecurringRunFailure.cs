namespace Accounting.Helpers;

/// <summary>
/// ข้อความ "ทำไมรอบนี้สร้างเอกสารไม่สำเร็จ" ที่เก็บบนแถวรายการประจำ (<c>RecurringTransaction.LastRunError</c>) และแจ้งผู้ตั้งรายการ —
/// ตัวเดียวทั้ง cron และ "สร้างทันที"
///
/// <para><b>ที่มา (ทีมตรวจรายการประจำ 2026-10-14):</b> cron ล้ม ⇒ เดิม <c>LogError</c> + ErrorLog อย่างเดียว ทุก 15 นาทีตลอดไป
/// (log spam) ขณะที่ผู้ใช้ในหน้ารายการประจำเห็นแค่ "ครั้งถัดไป" ค้างวันเดิมโดยไม่รู้ว่าทำไมเอกสารไม่ออก (F2-7 ล้มดัง 3 ที่) ·
/// ข้อความต้องเป็นภาษาที่ผู้ใช้ทำตามได้ — exception ของกรอบงาน (EF/Npgsql/JSON) ห้ามหลุดถึงผู้ใช้ (ไปอยู่ที่ ErrorLog แทน)</para>
/// </summary>
public static class RecurringRunFailure
{
    public const string RuleCode = "RECURRING-RUN-FAILED";
    private const int MaxLength = 1000;

    /// <summary>ข้อความถึงผู้ใช้: กฎธุรกิจของเรา (<see cref="BusinessRuleException"/> หรือ exception กฎเดิมที่ข้อความเป็นภาษาไทย) ⇒ ข้อความนั้น ·
    /// ข้อผิดพลาดทางเทคนิค (ข้อความอังกฤษของ EF/Npgsql/JSON เช่น "The connection is already in a transaction") ⇒ ข้อความกลางพร้อมทางไปต่อ</summary>
    public static string UserMessage(Exception ex)
    {
        var e = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        string msg;
        if (e is BusinessRuleException || (e is InvalidOperationException or KeyNotFoundException or ArgumentException
                && ContainsThai(e.Message)))
            msg = e.Message;
        else
            msg = "เกิดข้อผิดพลาดของระบบระหว่างสร้างเอกสาร (บันทึกรายละเอียดให้ผู้ดูแลระบบแล้ว) — กด \"สร้างทันที\" เพื่อลองใหม่ ถ้ายังไม่สำเร็จให้ติดต่อผู้ดูแลระบบ";
        msg = msg.Trim();
        return msg.Length <= MaxLength ? msg : msg[..MaxLength] + "…";
    }

    private static bool ContainsThai(string? s) => s != null && s.Any(c => c >= '\u0E00' && c <= '\u0E7F');
}
