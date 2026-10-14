namespace Accounting.Helpers;

/// <summary>ผลการส่งรายงานตามกำหนดเวลา 1 รอบ ⇒ ข้อความ <c>ScheduledReport.LastResult</c> + ควรประทับ <c>LastSentAt</c> ไหม — ตัวตัดสินเดียว (pure)
/// ของ <c>ScheduledReportDispatcher</c> และปุ่ม "ส่งทันที" · รอบ 2026-10-14 ทีม F4 (ID#25)
///
/// <para>═══ ที่มา ═══ ตัวส่งกลืน error ของผู้รับแต่ละราย (LogWarning) แล้วตั้ง <c>LastResult = "OK"</c> เสมอ ⇒ ผู้รับครึ่งหนึ่ง/ทุกรายไม่ได้รายงาน
/// แต่หน้าจอบอกสำเร็จ · ปุ่ม "ส่งทันที" ประทับ "OK" + LastSentAt โดย<b>ไม่ได้ส่งอะไรเลย</b> (และทำให้รอบจริงถูกข้าม 23 ชม.)</para>
///
/// <para>ขึ้นต้นด้วยรหัสคงที่ (OK · PARTIAL · ERROR · SKIPPED) ให้เครื่องอ่านได้ ตามด้วยคำอธิบายไทยพร้อมทางไปต่อ</para></summary>
public static class ScheduledReportOutcome
{
    /// <summary>ข้อความผล + มีผู้รับได้รับอย่างน้อย 1 ราย (ประทับ LastSentAt เฉพาะกรณีนี้ — ไม่ส่งถึงใครเลย ≠ "ส่งแล้ว")</summary>
    public sealed record Result(string LastResult, bool AnyDelivered);

    private const int MaxLength = 480;

    /// <param name="notSentReason">ไม่ได้เริ่มส่งเลย (ไม่มีผู้รับ · รายชื่ออ่านไม่ได้ · ไม่มีช่องส่งอีเมล) — null = พยายามส่งแล้ว</param>
    public static Result Summarize(int recipientCount, IReadOnlyList<(string To, string? Error)> failures, string? notSentReason)
    {
        if (notSentReason != null) return new Result(Clip("SKIPPED · ไม่ได้ส่ง: " + notSentReason), false);
        var delivered = recipientCount - failures.Count;
        if (failures.Count == 0) return new Result($"OK · ส่งครบ {recipientCount} ราย", recipientCount > 0);
        var detail = string.Join(" · ", failures.Select(f =>
            $"{f.To} ({(string.IsNullOrWhiteSpace(f.Error) ? "ไม่ทราบสาเหตุ" : f.Error!.Trim())})"));
        // ทางไปต่อมาก่อนรายละเอียด — ข้อความยาวเกินถูกตัดที่รายละเอียด ไม่ใช่ที่คำแนะนำ
        const string wayForward = "ตรวจอีเมลผู้รับในรายงานนี้และการตั้งค่าอีเมลของบริษัท แล้วกด “ส่งทันที” เพื่อลองใหม่";
        return delivered > 0
            ? new Result(Clip($"PARTIAL · ส่งได้ {delivered}/{recipientCount} ราย — {wayForward} · ไม่สำเร็จ: {detail}"), true)
            : new Result(Clip($"ERROR · ส่งไม่สำเร็จทุกราย (0/{recipientCount}) — {wayForward} · {detail}"), false);
    }

    private static string Clip(string s) => s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
}
