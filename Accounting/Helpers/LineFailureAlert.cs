namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-4 (team-A L6 + L8 · คำตัดสิน 260-4-4/260-4-6 · ฝ่ายค้าน 260-4 M1/M2/M4/L6/L7/L8 · 260-4-7…) — <b>กระดิ่ง "ส่งถึงลูกค้าอัตโนมัติล้ม": ตัวตัดสินเดียว</b> (pure)
/// <list type="bullet">
/// <item><b>LINE ล้มซ้ำ</b>: แถวล้มถาวรของบริษัทใน <see cref="Window"/> ≥ <see cref="Threshold"/> ⇒ ชนิด <see cref="Kind.Repeated"/> ·
/// Token/ช่องของ OA บริษัทใช้ไม่ได้ ⇒ <see cref="Kind.ChannelBroken"/> · ถอด LINE id ของผู้รับไม่ได้ (กุญแจของเครื่องอาจไม่ตรงกัน) ⇒ <see cref="Kind.Decrypt"/> ·
/// <b>กันซ้ำแยกต่อชนิด</b> ครั้งเดียวต่อ <see cref="Dedupe"/> (ฝ่ายค้าน M4 — Token เสียหลังกระดิ่ง "ล้มซ้ำ" ยังต้องดัง) · EntityId = Guid คงที่ต่อชนิด
/// (<see cref="KindId"/>) ⇒ Autopilot แยกเรื่องตามชนิดด้วย</item>
/// <item>ผู้รับ = เจ้าของที่ยังใช้งาน · ไม่มีเจ้าของที่ใช้งาน ⇒ ผู้มีสิทธิ์แก้ค่าตั้งบริษัท (ฝ่ายค้าน L8 · <c>LineAlertRecipients</c>)</item>
/// <item>OA กลางของ NextAcc ล้มถาวร ⇒ กระดิ่งผู้ดูแลระบบครั้งเดียวต่อวัน (<see cref="CentralEntityType"/> · ฝ่ายค้าน L7) — ไม่ใช่กระดิ่งบริษัท (บริษัทแก้ไม่ได้)</item>
/// <item><b>เข้าคิวส่งอัตโนมัติล้ม</b> (A-L6 · M2): อนุมัติเอกสาร · จ่ายเงินเดือน · ออก 50 ทวิ ⇒ กระดิ่งต่อรายการ (<see cref="ApproveEnqueueEntityType"/> · EntityId = รายการ)</item>
/// <item>Autopilot อ่านแถวแจ้งเตือนชนิดเหล่านี้ (ช่วง 30 วัน) และ<b>ซ่อนเมื่อแก้แล้ว</b> (L6): LINE ส่งสำเร็จหลังกระดิ่ง · เอกสารมีแถวคิว/การส่งหลังกระดิ่ง</item>
/// </list>
/// </summary>
public static class LineFailureAlert
{
    public const string RuleCode = "LINE-REPEATED-FAILURE";
    /// <summary>RuleCode บนแถวคิวเมื่อถอด LINE id ไม่ได้</summary>
    public const string DecryptRuleCode = "line-decrypt";
    public const string EntityType = "LineQueueRepeatedFailure";
    public const string CentralEntityType = "LineCentralOaFailure";
    public const string ApproveEnqueueEntityType = "AutoSendEnqueueFailed";
    public const string LockScope = "line-failure-alert";

    public const int Threshold = 5;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public static readonly TimeSpan Dedupe = TimeSpan.FromHours(24);

    public enum Kind { Repeated, ChannelBroken, Decrypt }

    /// <summary>Guid คงที่ต่อชนิด (EntityId ของแถวแจ้งเตือน) — กันซ้ำ/รวมเรื่องใน Autopilot แยกต่อชนิด</summary>
    public static Guid KindId(Kind kind) => kind switch
    {
        Kind.ChannelBroken => new Guid("2604c0de-0000-4000-8000-000000000002"),
        Kind.Decrypt => new Guid("2604c0de-0000-4000-8000-000000000003"),
        _ => new Guid("2604c0de-0000-4000-8000-000000000001"),
    };

    /// <summary>ชนิดที่ต้องแจ้ง — null = ยังไม่ถึงเกณฑ์ · ช่องเสีย/ถอดรหัสไม่ได้ ดังทันที · ที่เหลือต้องถึงเกณฑ์</summary>
    public static Kind? AlertKind(int failuresInWindow, bool channelBroken, bool decryptFailed = false)
        => decryptFailed ? Kind.Decrypt : channelBroken ? Kind.ChannelBroken : failuresInWindow >= Threshold ? Kind.Repeated : null;

    public static string Title(bool channelBroken) => Title(channelBroken ? Kind.ChannelBroken : Kind.Repeated);

    public static string Title(Kind kind) => kind switch
    {
        Kind.ChannelBroken => "LINE OA ของบริษัทส่งข้อความถึงลูกค้าไม่ได้ (Token/ช่องทางใช้ไม่ได้)",
        Kind.Decrypt => "ถอดรหัส LINE ของผู้รับไม่ได้ — ตรวจกุญแจเข้ารหัสของเซิร์ฟเวอร์",
        _ => "การ์ด LINE ถึงลูกค้าส่งไม่สำเร็จหลายครั้ง",
    };

    public static string Message(bool channelBroken, int failuresInWindow, string? lastReason)
        => Message(channelBroken ? Kind.ChannelBroken : Kind.Repeated, failuresInWindow, lastReason);

    public static string Message(Kind kind, int failuresInWindow, string? lastReason)
    {
        var reason = string.IsNullOrWhiteSpace(lastReason) ? "" : " · เหตุล่าสุด: " + LineMessageLimits.Truncate(lastReason!.Trim(), 300);
        var head = kind switch
        {
            Kind.ChannelBroken => "LINE ปฏิเสธ Token ของ LINE OA ของบริษัท — การ์ดเอกสาร/ทวงชำระ/ใบเสร็จถึงผู้ติดต่อทาง LINE จะล้มทั้งหมด (ถอยไปอีเมลตามกฎที่ตั้งไว้) จนกว่าจะตั้ง Token ใหม่ที่ ตั้งค่า → LINE",
            Kind.Decrypt => DecryptAdvice + " — การ์ดของปลายทางนั้นไม่ได้ส่ง",
            _ => $"การ์ด LINE ถึงผู้ติดต่อล้ม {failuresInWindow} ครั้งในชั่วโมงที่ผ่านมา — ดูเหตุและกดส่งใหม่ที่คิว LINE (ตารางส่งอีเมล/LINE)",
        };
        return head + reason + " · แจ้งครั้งเดียวต่อ 24 ชั่วโมง (ต่อชนิดปัญหา)";
    }

    /// <summary>ฝ่ายค้าน M1: เหตุบนแถว/กระดิ่งเมื่อถอด LINE id ไม่ได้ — ไม่สั่ง "ผูกใหม่" ก่อนตัดเรื่องกุญแจของเครื่องออก</summary>
    public const string DecryptAdvice =
        "ถอดรหัส LINE id ของผู้รับไม่ได้ — ให้ผู้ดูแลระบบตรวจก่อนว่าทุกเครื่องใช้ ENCRYPTION_KEY เดียวกัน (กุญแจไม่ตรงทำให้ข้อมูลเดิมอ่านไม่ได้) · ถ้ากุญแจถูกแล้ว ค่าที่เก็บเสียหาย ⇒ ให้ผู้รับผูกใหม่";

    /// <summary>หมายเหตุบนแถวเมื่อถอดไม่ได้ครั้งแรก (ลองใหม่อีกครั้ง — เครื่องอื่นอาจถอดได้)</summary>
    public const string DecryptRetryNote = "ถอดรหัส LINE id ของผู้รับไม่ได้บนเครื่องนี้ — ลองใหม่อีกครั้ง (เครื่องอื่นอาจใช้กุญแจถูก)";

    public const string CentralTitle = "LINE OA กลางของ NextAcc ส่งเข้ากลุ่มของลูกค้าไม่ได้";

    public static string CentralMessage(string? code, string? reason)
        => $"การส่งการ์ดเข้ากลุ่มของผู้ติดต่อผ่าน LINE OA กลางล้ม ({code ?? "?"})"
           + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + LineMessageLimits.Truncate(reason!.Trim(), 300))
           + " — ตรวจ Token ของ OA กลางที่หน้าแอดมิน · แจ้งครั้งเดียวต่อวัน";

    public const string ApproveEnqueueTitle = "อนุมัติเอกสารแล้ว แต่เข้าคิวส่งอัตโนมัติไม่สำเร็จ";

    private static string ApproveEnqueueMessage(string? documentNumber, string? reason)
        => $"{(string.IsNullOrWhiteSpace(documentNumber) ? "เอกสาร" : documentNumber)}: กฎ \"เมื่ออนุมัติเอกสาร\" เข้าคิวอีเมล/LINE ไม่สำเร็จ"
           + (string.IsNullOrWhiteSpace(reason) ? "" : " (" + LineMessageLimits.Truncate(reason!.Trim(), 200) + ")")
           + " — ลูกค้ายังไม่ได้รับเอกสาร · ส่งเองจากหน้าเอกสาร (📧 ส่งอีเมล / 💬 ส่งทาง LINE)";

    /// <summary>ฝ่ายค้าน M2: ชนิดรายการที่เข้าคิวส่งอัตโนมัติ — หัว/ข้อความ/ลิงก์ของกระดิ่ง</summary>
    public enum EnqueueSource { DocumentApproved, PayrollPaid, WhtCertIssued }

    public static string EnqueueTitle(EnqueueSource s) => s switch
    {
        EnqueueSource.PayrollPaid => "จ่ายเงินเดือนแล้ว แต่เข้าคิวส่งสลิปอัตโนมัติไม่สำเร็จ",
        EnqueueSource.WhtCertIssued => "ออกหนังสือรับรอง 50 ทวิ แล้ว แต่เข้าคิวส่งอัตโนมัติไม่สำเร็จ",
        _ => ApproveEnqueueTitle,
    };

    public static string EnqueueMessage(EnqueueSource s, string? number, string? reason) => s switch
    {
        EnqueueSource.PayrollPaid => $"รอบเงินเดือน {(string.IsNullOrWhiteSpace(number) ? "" : number)}: กฎ \"เมื่อจ่ายเงินเดือน\" เข้าคิวส่งสลิปไม่สำเร็จ"
            + Why(reason) + " — พนักงานยังไม่ได้รับสลิปทางอีเมล · ส่งเองจากหน้าเงินเดือน",
        EnqueueSource.WhtCertIssued => $"{(string.IsNullOrWhiteSpace(number) ? "หนังสือรับรอง" : number)}: กฎ \"เมื่อออก 50 ทวิ\" เข้าคิวส่งไม่สำเร็จ"
            + Why(reason) + " — ผู้ถูกหักยังไม่ได้รับหนังสือรับรอง · ส่งเองจากหน้า 50 ทวิ",
        _ => ApproveEnqueueMessage(number, reason),
    };

    public static string EnqueueActionUrl(EnqueueSource s, Guid entityId) => s switch
    {
        EnqueueSource.PayrollPaid => "/pages/payroll.html",
        EnqueueSource.WhtCertIssued => "/pages/wht.html",
        _ => "/pages/documents.html?openDoc=" + entityId,
    };

    private static string Why(string? reason) => string.IsNullOrWhiteSpace(reason) ? "" : " (" + LineMessageLimits.Truncate(reason!.Trim(), 200) + ")";
}
