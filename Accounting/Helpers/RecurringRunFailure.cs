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
        var msg = UserFacingDetail(ex)
            ?? "เกิดข้อผิดพลาดของระบบระหว่างสร้างเอกสาร (บันทึกรายละเอียดให้ผู้ดูแลระบบแล้ว) — กด \"สร้างทันที\" เพื่อลองใหม่ ถ้ายังไม่สำเร็จให้ติดต่อผู้ดูแลระบบ";
        return Cap(msg);
    }

    /// <summary>
    /// ฝ่ายค้านรอบ 1 P2 (3b) — ขั้น<b>ตามหลัง</b>การสร้างเอกสารล้ม (เอกสารถูกบันทึก + งวดถูกนับแล้ว): ข้อความ "เรื่องที่ต้องทำต่อ" บนแถว
    /// ไม่ใช่ "รอบล้ม" · <b>ห้ามชวนกด "สร้างทันที"</b> (เดิมแถวขึ้นล้ม ⇒ ผู้ใช้กดซ้ำ = ใบซ้ำ) · ข้อผิดพลาดทางเทคนิคไม่หลุดถึงผู้ใช้
    /// </summary>
    public static string AfterCreateNotice(string? documentNumber, Exception ex)
    {
        var detail = UserFacingDetail(ex) ?? "ข้อผิดพลาดของระบบ (บันทึกรายละเอียดให้ผู้ดูแลระบบแล้ว)";
        var no = string.IsNullOrWhiteSpace(documentNumber) ? "" : " " + documentNumber.Trim();
        return Cap($"สร้างเอกสาร{no} แล้ว (รอบนี้นับแล้ว — ไม่ต้องกด \"สร้างทันที\" ซ้ำ) แต่ขั้นตามหลังไม่สำเร็จ: {detail} — "
                   + "เปิดเอกสารนี้ที่หน้าเอกสารเพื่อตรวจ/อนุมัติ/ส่งอีเมลเอง");
    }

    /// <summary>ข้อความที่ผู้ใช้อ่านได้ (กฎธุรกิจ/ข้อความไทย) · null = ข้อผิดพลาดทางเทคนิค (EF/Npgsql/JSON — ห้ามหลุดถึงผู้ใช้)</summary>
    private static string? UserFacingDetail(Exception ex)
    {
        var e = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return e is BusinessRuleException || (e is InvalidOperationException or KeyNotFoundException or ArgumentException
                && ContainsThai(e.Message))
            ? e.Message.Trim()
            : null;
    }

    private static string Cap(string msg)
    {
        msg = msg.Trim();
        return msg.Length <= MaxLength ? msg : msg[..MaxLength] + "…";
    }

    private static bool ContainsThai(string? s) => s != null && s.Any(c => c >= '\u0E00' && c <= '\u0E7F');
}
