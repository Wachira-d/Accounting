namespace Accounting.Helpers;

/// <summary>ผลการส่งหนึ่งครั้งหลังจำแนก — <see cref="Sent"/> ส่งถึงแล้ว · <see cref="Retry"/> ลองใหม่ด้วย retry key เดิม · <see cref="Failed"/> ล้มถาวร</summary>
public enum LinePushVerdict
{
    Sent,
    Retry,
    Failed,
}

/// <summary>ผลจำแนก — <paramref name="BlockEndpoint"/> = ประทับ "ผู้รับบล็อก" บนปลายทาง · <paramref name="ChannelBroken"/> = token/OA ของบริษัทเสีย ·
/// <paramref name="FallbackEligible"/> = ถอยไปอีเมลได้ (คำตอบเจ้าของ Q7: LINE ส่งไม่ได้ ⇒ อีเมลแทน เมื่อกฎเปิดไว้)</summary>
public sealed record LinePushClass(LinePushVerdict Verdict, string Code, string? Reason, bool BlockEndpoint, bool ChannelBroken, bool FallbackEligible);

/// <summary>
/// รอบ 254 WP-11 L2 (LINE_MULTICHANNEL_DESIGN §3.e ตาราง "การจำแนกผลจาก LINE") — <b>ตัวตัดสินเดียว</b> (pure) ว่าผลจาก <c>LineNotifyService</c>
/// แปลว่า "ส่งแล้ว / ลองใหม่ / ล้มถาวร" · ผู้เรียก: <c>ContactLineQueueService.ProcessPendingAsync</c> เท่านั้น
/// <list type="bullet">
/// <item>200 ⇒ Sent · <b>409</b> (ส่งซ้ำด้วย <c>X-Line-Retry-Key</c> เดิม) ⇒ Sent — LINE รับคำขอนั้นไปแล้ว (ไม่ส่งสองข้อความ · ไม่ใช่สถานะที่ระบบประทับเอง: LINE เป็นผู้ตอบ)</item>
/// <item>นโยบายปฏิเสธ (<c>RuleCode</c> มีค่า — เช่น กลุ่มกลางของแอดมิน · OA กลางยังไม่เปิด) ⇒ Failed ถาวร</item>
/// <item>400 "ไม่ได้เป็นเพื่อน/ไม่พบผู้รับ" ⇒ Failed + บล็อกปลายทาง · 400 อื่น ⇒ Failed (payload ของเรา) · 401/403 ⇒ Failed + ช่องเสีย</item>
/// <item>429 "monthly limit" ⇒ Failed (ข้อความฟรีของ OA บริษัทหมด — ไม่ถอยไป OA กลาง เพราะ userId ผูกกับ OA) · 429 อื่น / 5xx / เครือข่ายล้ม ⇒ Retry</item>
/// <item>ไม่มีสถานะ HTTP และไม่ใช่เครือข่ายล้ม (เช่น ไม่มี token) ⇒ Failed + ช่องเสีย (ลองใหม่ไม่ช่วย)</item>
/// </list>
/// ⚠ ข้อความ/รหัสของ LINE ในตารางนี้มาจากความรู้ของทีม (DESIGN §7.4 ⚠) — ต้องยืนยันกับเอกสารทางการ · ค่าที่ไม่รู้จัก ⇒ ล้มพร้อมเหตุ (ไม่ประทับว่าส่งแล้ว)
/// </summary>
public static class LinePushOutcome
{
    public const string CodeSent = "sent";
    public const string CodeAlreadyAccepted = "already-accepted";
    public const string CodeDenied = "policy-denied";
    public const string CodeNotFriend = "not-friend";
    public const string CodeRecipientInvalid = "recipient-invalid";
    public const string CodeBadRequest = "bad-request";
    public const string CodeAuth = "auth";
    public const string CodeMonthlyLimit = "line-oa-monthly-limit";
    public const string CodeRateLimit = "rate-limit";
    public const string CodeServer = "server";
    public const string CodeNoChannel = "no-channel";
    public const string CodeUnknown = "unknown";

    /// <summary>จำนวนครั้งที่ลองใหม่ได้ (ครบแล้ว = Failed)</summary>
    public const int MaxAttempts = 3;

    public static LinePushClass Classify(bool ok, int? httpStatus, string? error, string? ruleCode, bool transportError)
    {
        if (ok) return new(LinePushVerdict.Sent, CodeSent, null, false, false, false);
        var why = string.IsNullOrWhiteSpace(error) ? "LINE ไม่รับข้อความ (ไม่ทราบเหตุ)" : error!.Trim();
        if (!string.IsNullOrWhiteSpace(ruleCode))
            return new(LinePushVerdict.Failed, CodeDenied, why, false, false, true);
        if (httpStatus == 409)
            return new(LinePushVerdict.Sent, CodeAlreadyAccepted, null, false, false, false);
        if (transportError)
            return new(LinePushVerdict.Retry, CodeServer, why, false, false, true);
        var lower = why.ToLowerInvariant();
        switch (httpStatus)
        {
            case null:
                return new(LinePushVerdict.Failed, CodeNoChannel, why, false, true, true);
            case 400:
                // ฝ่ายค้าน L5: "ไม่พบผู้รับ/ปลายทางไม่ถูกต้อง" มักเกิดจาก OA เปลี่ยน (userId ใช้ได้เฉพาะ OA ที่ออก) — ไม่ใช่การบล็อกของผู้รับ ⇒ ไม่ประทับ "ผู้รับบล็อก"
                if (lower.Contains("friend") || lower.Contains("blocked"))
                    return new LinePushClass(LinePushVerdict.Failed, CodeNotFriend, "ผู้รับไม่ได้เป็นเพื่อนกับ LINE OA หรือบล็อกไว้ — " + why, true, false, true);
                if (lower.Contains("not found") || lower.Contains("invalid to") || lower.Contains("invalid user"))
                    return new LinePushClass(LinePushVerdict.Failed, CodeRecipientInvalid,
                        "ปลายทางนี้ใช้กับ LINE OA ปัจจุบันไม่ได้ (บริษัทอาจเปลี่ยน OA) — ให้ผู้รับผูกใหม่ · " + why, false, false, true);
                return new LinePushClass(LinePushVerdict.Failed, CodeBadRequest, why, false, false, true);
            case 401:
            case 403:
                return new(LinePushVerdict.Failed, CodeAuth, "Token ของ LINE OA ใช้ไม่ได้หรือถูกเพิกถอน — ตั้งค่า LINE ของบริษัทใหม่ · " + why, false, true, true);
            case 429:
                return lower.Contains("monthly limit")
                    ? new LinePushClass(LinePushVerdict.Failed, CodeMonthlyLimit, "LINE OA ของบริษัทใช้ข้อความของเดือนนี้หมดแล้ว — " + why, false, false, true)
                    : new LinePushClass(LinePushVerdict.Retry, CodeRateLimit, why, false, false, true);
        }
        if (httpStatus >= 500) return new(LinePushVerdict.Retry, CodeServer, why, false, false, true);
        return new(LinePushVerdict.Failed, CodeUnknown, why, false, false, true);
    }

    /// <summary>หน่วงก่อนลองใหม่ครั้งที่ <paramref name="attempt"/> (1 = ครั้งแรกที่ล้ม): 1 → 5 → 30 นาที</summary>
    public static TimeSpan Backoff(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(30),
    };
}
