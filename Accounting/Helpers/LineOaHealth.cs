namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 แพ็ก B (A-M5 · A-M6 · A-L3 · คำตัดสิน 259-2-3) — <b>สุขภาพ LINE OA ของบริษัท: ตัวตัดสินเดียว</b> (pure · server computes, page displays)
/// <list type="bullet">
/// <item>"พร้อม" เดิม (<see cref="CompanyLineReadiness"/>) ดูแค่ เปิด + token + secret ⇒ ร้านที่ไม่เคยวาง Webhook URL ก็ขึ้นพร้อม แต่ผูก/หยุด/บล็อกไม่เคยทำงาน ·
/// รอบนี้: webhook ต้องเคยเข้าจริง (<c>CompanySettings.LineLastWebhookAt</c> ประทับโดย <c>CustomerLineWebhookController</c> หลังลายเซ็นผ่าน + บอทตรง) ⇒ ไม่เคย = ⚠️ พร้อมทางไปต่อ</item>
/// <item>Basic ID ว่าง ⇒ ⚠️ (ปุ่มเพิ่มเพื่อน/QR ผูก LINE ใช้ไม่ได้) — ไม่ใช่ข้อห้ามส่ง</item>
/// <item>ผลทดสอบล่าสุดอ่านจาก <c>LineLastTestStatus</c> (ไม่ใช่ <c>LineConfigured</c> ที่ถูกรีเซ็ตทุกครั้งที่บันทึก — A-L3 สีแดงทั้งที่ผ่าน)</item>
/// <item>ทดสอบส่งล้ม ⇒ ข้อความจริงของ LINE ผ่านตัวจำแนกเดียว <see cref="LinePushOutcome.Classify"/> (เดิม "ดู log" ที่ผู้ใช้เปิดไม่ได้ — A-M6)</item>
/// </list>
/// ผู้อ่าน: <c>LineConfigController</c> (แท็บ LINE ของหน้าตั้งค่า)
/// </summary>
public static class LineOaHealth
{
    public const string RuleCode = "LINE-OA-HEALTH";

    /// <summary>ประทับเวลา webhook ไม่ถี่กว่านี้ (กันเขียนฐานทุก event ของแชตที่คุยกันถี่)</summary>
    public static readonly TimeSpan StampEvery = TimeSpan.FromMinutes(5);

    /// <summary>หนึ่งแถวความพร้อม — <paramref name="Level"/> = ok · warn · error (ชื่อ ไม่ใช่สี) · <paramref name="Label"/>/<paramref name="Detail"/> = ข้อความถึงผู้ใช้</summary>
    public sealed record Row(string Code, string Level, string Label, string? Detail);

    public const string LevelOk = "ok", LevelWarn = "warn", LevelError = "error";

    /// <param name="lastWebhookUtc">webhook ล่าสุดที่ลายเซ็นผ่าน (null = ไม่เคย)</param>
    /// <param name="destinationMismatch">webhook มาจากบอทอื่น (secret ของ OA อื่น) — มีค่า ⇒ ผิด</param>
    /// <param name="lastTestStatus">"OK" = ทดสอบผ่าน · ข้อความ = เหตุที่ล้ม · null = ยังไม่เคยทดสอบ</param>
    public static IReadOnlyList<Row> Rows(bool enabled, bool tokenUsable, bool secretUsable, DateTime? lastWebhookUtc, bool destinationMismatch,
        string? basicId, DateTime? lastTestedUtc, string? lastTestStatus, bool isPlatformCompany)
    {
        if (isPlatformCompany) return Array.Empty<Row>();   // บริษัทผู้ให้บริการใช้ OA กลาง (257 C-H1) — ไม่มี OA ของร้านให้ตรวจ
        var rows = new List<Row>();
        var ready = CompanyLineReadiness.Evaluate(enabled, tokenUsable, secretUsable);
        rows.Add(ready.Ready
            ? new Row("oa", LevelOk, "เปิดใช้ + Channel Access Token + Channel Secret ครบ", null)
            : new Row("oa", enabled ? LevelError : LevelWarn, "LINE OA ของบริษัทยังไม่พร้อม", ready.Problem));
        if (enabled && secretUsable)
        {
            if (destinationMismatch)
                rows.Add(new Row("webhook", LevelError, "webhook ที่เข้ามาเป็นของ LINE OA อื่น", CustomerRelinkInvitePlan.SecretOfOtherOaProblem));
            else if (lastWebhookUtc is DateTime at)
                rows.Add(new Row("webhook", LevelOk, "LINE เรียก webhook ของบริษัทแล้ว", "ล่าสุด " + ThaiDate.ToThaiDisplayString(at) + " "
                    + at.AddHours(7).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " น."));
            else
                rows.Add(new Row("webhook", LevelWarn, "ยังไม่เคยได้รับ event จาก LINE",
                    "วาง Webhook URL ใน LINE Developers → Messaging API → Webhook settings · เปิด Use webhook · กด Verify — ก่อนนั้นการผูก LINE ของผู้ติดต่อ/กลุ่ม การพิมพ์ \"หยุด\" และการรู้ว่าถูกบล็อกจะไม่ทำงาน"));
        }
        if (enabled && string.IsNullOrWhiteSpace(basicId))
            rows.Add(new Row("basic-id", LevelWarn, "ยังไม่ได้ใส่ LINE OA Basic ID",
                "ใส่ Basic ID (เช่น @myshop) เพื่อให้ปุ่มเพิ่มเพื่อน/QR ผูก LINE ของลูกค้าและพนักงานใช้ได้"));
        if (lastTestedUtc != null)
            rows.Add(string.Equals(lastTestStatus, "OK", StringComparison.Ordinal)
                ? new Row("test", LevelOk, "ทดสอบส่งล่าสุดผ่าน", ThaiDate.ToThaiDisplayString(lastTestedUtc.Value))
                : new Row("test", LevelError, "ทดสอบส่งล่าสุดไม่ผ่าน", string.IsNullOrWhiteSpace(lastTestStatus) ? null : lastTestStatus));
        return rows;
    }

    /// <summary>รอบ 259 (A-M8 ส่วนแรก · 259-2-4): ลูกค้าส่งรูป/ไฟล์ (เช่นสลิป) ในแชต OA ของบริษัท — เดิมเงียบ ⇒ ตอบรับพร้อมทางไปต่อ ·
    /// รอบ 260: ใช้เฉพาะเมื่อเส้นสลิปจากแชต (<c>LineChatSlipService</c>) ไม่รับ — ปิดสวิตช์ <c>LineChatSlipIntakeEnabled</c> · ไฟล์ที่ไม่ใช่รูป · บริการไม่พร้อม ·
    /// ข้อความนี้เองไม่เก็บไฟล์ ไม่อ่านเนื้อหา</summary>
    public const string ChatMediaReply =
        "ได้รับรูป/ไฟล์แล้วค่ะ แต่แชตนี้ยังบันทึกสลิปเข้าระบบอัตโนมัติไม่ได้ — ถ้าเป็นสลิปโอนเงิน กรุณากดปุ่ม \"ชำระเงิน\" บนการ์ดเอกสารแล้วแนบสลิปที่หน้านั้น "
        + "(ระบบจะผูกกับใบให้) หรือแจ้งผู้ขายโดยตรง";

    /// <summary>ชนิดข้อความในแชตที่ตอบด้วย <see cref="ChatMediaReply"/></summary>
    public static bool IsChatMedia(string? messageType) => messageType is "image" or "file";

    /// <summary>เพดานคำตอบต่อผู้ใช้ LINE (ทางเข้าสาธารณะ — กันยิงรูปถี่ให้บอทตอบไม่หยุด) · คีย์ = hash ของ (บอท + userId) ไม่ใช่ userId ดิบ</summary>
    public const int ChatMediaPerMinute = 2, ChatMediaPerDay = 10;

    public static string ChatMediaRateKey(string? oaDestination, string lineUserId)
        => "co-line-media:" + NotifyEndpointKind.LineIdHash(oaDestination, lineUserId)[..32];

    /// <summary>key ของปลายทางทดสอบ "กลุ่ม LINE ของทีมที่ตั้งไว้" (<c>CompanySettings.LineDefaultGroupId</c>)</summary>
    public const string TeamGroupTargetKey = "team-group";

    /// <summary>รอบ 260-4 (260-4-3): key ของปลายทาง LINE ของผู้ติดต่อ (<c>ContactNotifyEndpoint.Id</c>) — หน้าเว็บไม่เห็น LINE id</summary>
    public const string EndpointTargetPrefix = "ep:";

    public static string EndpointTargetKey(Guid endpointId) => EndpointTargetPrefix + endpointId.ToString("N");

    /// <summary>key ปลายทางของผู้ติดต่อ ⇒ id · อย่างอื่น ⇒ null (ไปเส้น <see cref="TestTarget"/>)</summary>
    public static Guid? ParseEndpointKey(string? targetKey)
        => targetKey != null && targetKey.StartsWith(EndpointTargetPrefix, StringComparison.Ordinal)
           && Guid.TryParseExact(targetKey[EndpointTargetPrefix.Length..], "N", out var id) ? id : null;

    /// <summary>ป้ายของปลายทางผู้ติดต่อในรายการทดสอบ</summary>
    public static string EndpointTargetLabel(string? contactName, string kind, string? groupName, string? label)
    {
        var who = string.IsNullOrWhiteSpace(contactName) ? "ผู้ติดต่อ" : contactName!.Trim();
        var what = kind == NotifyEndpointKind.LineGroup
            ? "กลุ่ม LINE" + (string.IsNullOrWhiteSpace(groupName) ? "" : " \"" + groupName!.Trim() + "\"")
            : "LINE ผู้ใช้" + (string.IsNullOrWhiteSpace(label) ? "" : " (" + label!.Trim() + ")");
        return LineMessageLimits.Truncate(who + " · " + what, 160);
    }

    /// <summary>ปลายทางของคำขอทดสอบ — key ที่รู้จัก ⇒ id จากค่าตั้ง · ไม่มี key ⇒ id ที่พิมพ์ · key ไม่รู้จัก/ค่าตั้งว่าง ⇒ (null, เหตุ)</summary>
    public static (string? To, string? Problem) TestTarget(string? targetKey, string? typedId, string? teamGroupId)
    {
        if (!string.IsNullOrWhiteSpace(targetKey))
        {
            if (targetKey != TeamGroupTargetKey) return (null, "ไม่รู้จักปลายทางทดสอบที่เลือก — โหลดหน้านี้ใหม่");
            return string.IsNullOrWhiteSpace(teamGroupId) ? (null, "ยังไม่ได้ตั้งกลุ่ม LINE ของทีม") : (teamGroupId.Trim(), null);
        }
        return string.IsNullOrWhiteSpace(typedId) ? (null, "เลือกปลายทาง หรือระบุ LINE User ID (U…) / Group ID (C…) ของผู้รับ") : (typedId.Trim(), null);
    }

    /// <summary>ข้อความเมื่อทดสอบส่งล้ม — เหตุจริงจาก LINE (จำแนกตัวเดียวกับคิว LINE) + ทางไปต่อ</summary>
    public static string TestFailureMessage(int? httpStatus, string? error, bool transportError)
    {
        var cls = LinePushOutcome.Classify(false, httpStatus, error, null, transportError);
        var reason = string.IsNullOrWhiteSpace(cls.Reason) ? "LINE ไม่รับข้อความ" : cls.Reason!;
        var hint = httpStatus == 400
            ? " · ตรวจว่า ID ขึ้นต้นด้วย U (ผู้ใช้) หรือ C (กลุ่ม) และผู้รับเพิ่ม LINE OA นี้เป็นเพื่อน/เชิญบอทเข้ากลุ่มแล้ว"
            : "";
        return LineMessageLimits.Truncate("ส่งไม่สำเร็จ — " + reason + hint, 500);
    }
}
