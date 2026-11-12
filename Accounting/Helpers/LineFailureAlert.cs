namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-4 (team-A L6 + L8 · คำตัดสิน 260-4-4/260-4-6) — <b>กระดิ่ง "ส่งถึงลูกค้าอัตโนมัติล้ม": ตัวตัดสินเดียว</b> (pure)
/// <list type="bullet">
/// <item><b>LINE ล้มซ้ำ</b>: แถวคิว LINE ถึงผู้ติดต่อที่ล้มถาวรของบริษัทใน <see cref="Window"/> ล่าสุด ≥ <see cref="Threshold"/> แถว
/// <b>หรือ</b> token/OA ของบริษัทใช้ไม่ได้ (<c>LinePushClass.ChannelBroken</c> — 401/403/ไม่มีช่อง) ⇒ กระดิ่งถึงเจ้าของที่ยังใช้งาน <b>ครั้งเดียวต่อ <see cref="Dedupe"/></b> ·
/// แถวที่ล้มเพราะโควตาเดือนหมดไม่นับ (มีกระดิ่งของตัวเอง <c>LinePushOutcome.MonthlyLimitAlertEntity</c> ครั้งเดียวต่อเดือน) ·
/// ปลายทางผ่าน OA กลางของ NextAcc ไม่นับ (บริษัทแก้ไม่ได้ — ผู้ดูแลระบบเห็นใน log)</item>
/// <item><b>เข้าคิวส่งอัตโนมัติตอนอนุมัติล้ม</b> (A-L6 — เดิม LogWarning อย่างเดียว ไม่มีร่องรอยที่ผู้ใช้เห็น) ⇒ กระดิ่งต่อเอกสาร (EntityId = เอกสาร) กันซ้ำต่อใบ</item>
/// <item>Autopilot อ่านแถวแจ้งเตือนสองชนิดนี้ (ช่วง 30 วัน) เป็นเรื่องที่ระบบทำต่อไม่ได้ · กันซ้ำข้ามเครื่องด้วย <c>pg_advisory_xact_lock</c> ต่อบริษัท (<see cref="LockScope"/>)</item>
/// </list>
/// </summary>
public static class LineFailureAlert
{
    public const string RuleCode = "LINE-REPEATED-FAILURE";
    public const string EntityType = "LineQueueRepeatedFailure";
    public const string ApproveEnqueueEntityType = "AutoSendEnqueueFailed";
    public const string LockScope = "line-failure-alert";

    public const int Threshold = 5;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public static readonly TimeSpan Dedupe = TimeSpan.FromHours(24);

    /// <summary>ควรตั้งกระดิ่งไหม — <paramref name="failuresInWindow"/> นับแถวล้มถาวรของบริษัท (รวมแถวนี้) ที่ไม่ใช่โควตาเดือนหมด</summary>
    public static bool ShouldAlert(int failuresInWindow, bool channelBroken) => channelBroken || failuresInWindow >= Threshold;

    public static string Title(bool channelBroken) => channelBroken
        ? "LINE OA ของบริษัทส่งข้อความถึงลูกค้าไม่ได้ (Token/ช่องทางใช้ไม่ได้)"
        : "การ์ด LINE ถึงลูกค้าส่งไม่สำเร็จหลายครั้ง";

    public static string Message(bool channelBroken, int failuresInWindow, string? lastReason)
    {
        var reason = string.IsNullOrWhiteSpace(lastReason) ? "" : " · เหตุล่าสุด: " + LineMessageLimits.Truncate(lastReason!.Trim(), 300);
        var head = channelBroken
            ? "LINE ปฏิเสธ Token ของ LINE OA ของบริษัท — การ์ดเอกสาร/ทวงชำระ/ใบเสร็จถึงผู้ติดต่อทาง LINE จะล้มทั้งหมด (ถอยไปอีเมลตามกฎที่ตั้งไว้) จนกว่าจะตั้ง Token ใหม่ที่ ตั้งค่า → LINE"
            : $"การ์ด LINE ถึงผู้ติดต่อล้ม {failuresInWindow} ครั้งในชั่วโมงที่ผ่านมา — ดูเหตุและกดส่งใหม่ที่คิว LINE (ตารางส่งอีเมล/LINE)";
        return head + reason + " · แจ้งครั้งเดียวต่อ 24 ชั่วโมง";
    }

    public const string ApproveEnqueueTitle = "อนุมัติเอกสารแล้ว แต่เข้าคิวส่งอัตโนมัติไม่สำเร็จ";

    public static string ApproveEnqueueMessage(string? documentNumber, string? reason)
        => $"{(string.IsNullOrWhiteSpace(documentNumber) ? "เอกสาร" : documentNumber)}: กฎ \"เมื่ออนุมัติเอกสาร\" เข้าคิวอีเมล/LINE ไม่สำเร็จ"
           + (string.IsNullOrWhiteSpace(reason) ? "" : " (" + LineMessageLimits.Truncate(reason!.Trim(), 200) + ")")
           + " — ลูกค้ายังไม่ได้รับเอกสาร · ส่งเองจากหน้าเอกสาร (📧 ส่งอีเมล / 💬 ส่งทาง LINE)";
}
