namespace Accounting.Helpers;

/// <summary>เพดานของ LINE Messaging API ต่อ<b>คำขอ push หนึ่งครั้ง</b> — ตัวตั้งตัวเดียว (รอบ 212 C · <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §3.1)
///
/// <para>LINE รับ message object ได้ไม่เกิน 5 ก้อนต่อคำขอ และข้อความ text ก้อนละไม่เกิน 5,000 ตัวอักษร — เกินแล้ว API ตอบ 400 ทั้งคำขอ
/// (ผู้รับไม่ได้อะไรเลย) ⇒ ทุกเส้นที่ประกอบข้อความยาว (สรุปประจำวัน · แจ้งเตือนพนักงาน) ต้องผ่าน <see cref="Normalize"/> ก่อนส่ง ·
/// ผู้ที่ต้องการ "ตัดอย่างมีความหมาย" (บอกว่าเหลืออีกกี่รายการ + ลิงก์) ทำเองก่อนหน้านี้ (<see cref="StaffDailySummaryBuilder"/>) —
/// ตัวนี้เป็นตาข่ายสุดท้ายที่รับประกันว่าคำขอไม่ถูกปฏิเสธทั้งก้อน</para></summary>
public static class LineMessageLimits
{
    /// <summary>message object สูงสุดต่อคำขอ push</summary>
    public const int MaxMessagesPerPush = 5;

    /// <summary>ตัวอักษรสูงสุดของ text message หนึ่งก้อน</summary>
    public const int MaxTextChars = 5000;

    /// <summary>เครื่องหมายต่อท้ายเมื่อข้อความถูกตัด</summary>
    public const string Ellipsis = "…";

    /// <summary>ทิ้งก้อนว่าง · ตัดก้อนที่ยาวเกิน (ต่อ "…") · เก็บแค่ <see cref="MaxMessagesPerPush"/> ก้อนแรก · ลำดับเดิม</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? messages)
    {
        var list = new List<string>();
        if (messages == null) return list;
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            list.Add(Truncate(m));
            if (list.Count == MaxMessagesPerPush) break;
        }
        return list;
    }

    /// <summary>ตัดข้อความให้ไม่เกิน <paramref name="max"/> ตัวอักษร (รวม "…") · ไม่ตัดกลาง surrogate pair</summary>
    public static string Truncate(string text, int max = MaxTextChars)
    {
        if (text.Length <= max) return text;
        var cut = Math.Max(0, max - Ellipsis.Length);
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + Ellipsis;
    }
}
