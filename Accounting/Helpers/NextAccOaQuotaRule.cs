using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L4 (คำตอบเจ้าของ N8 · LINE_MULTICHANNEL_DESIGN §5.4 · NOTIFY_PRICING §1.3/§5 ข้อ 9) — <b>ตัวตัดสินของงานเฝ้าต้นทุน OA กลาง</b>
/// (<c>Services/Background/NextAccOaQuotaWatch</c>) ตัวเดียว:
/// <list type="bullet">
/// <item>ตัวนับ <b>reply</b> ของ OA กลางต่อเดือน (<see cref="KindReply"/>) — LINE ประกาศว่า reply จะไม่ฟรีทั้งหมดตั้งแต่ 1 ก.พ. 2570 (ฟรี 25,000/เดือน/OA ·
///   แหล่งรองแหล่งเดียว ⇒ <see cref="AssumedFreeRepliesPerMonth"/> เป็นเกณฑ์เตือนเท่านั้น ไม่ใช่ตัวตัดสินการส่ง) · <b>ไม่ตัดโควตาผู้เช่า</b></item>
/// <item>ข้อความ push ของ OA กลางเดือนนี้เทียบโควตาที่ LINE ให้ (<c>GET /v2/bot/message/quota</c> + <c>/quota/consumption</c>) — ถึง <see cref="WarnPercent"/>% ⇒ เตือนผู้ดูแล</item>
/// <item>เตือนครั้งเดียวต่อเรื่องต่องวด (ประทับ <c>WarnedAt</c> แบบมีเงื่อนไข) · ช่องทาง = กลุ่มผู้ดูแลแพลตฟอร์ม (<c>TryPushPlatformOpsAsync</c> · ไม่ใช่กลุ่มของผู้เช่า)</item>
/// </list>
/// ตัวเลขเป็นของแพลตฟอร์ม (ไม่มี CompanyId — ข้อมูลต้นทุนรวมของ OA กลาง ไม่ใช่ข้อมูลผู้เช่า)
/// </summary>
public static class NextAccOaQuotaRule
{
    public const string KindReply = "reply";
    public const string KindPush = "push";

    /// <summary>เกณฑ์เตือน reply ฟรีต่อเดือน (ยังไม่ยืนยันกับ LINE โดยตรง — NOTIFY §1.3 ⚠) · เปลี่ยนเมื่อได้ตัวเลขทางการ</summary>
    public const int AssumedFreeRepliesPerMonth = 25_000;
    public const int WarnPercent = 90;

    /// <summary>งวด "yyyy-MM" ตามเวลาไทย (ตัวเดียวกับตัวนับโควตาผู้เช่า)</summary>
    public static string PeriodOf(DateTime utc) => AddOnBilling.PeriodOf(utc);

    /// <summary>อ่านโควตาจาก <c>GET /v2/bot/message/quota</c>: <c>{"type":"limited","value":N}</c> ⇒ N · <c>{"type":"none"}</c> (ไม่จำกัด) / อ่านไม่ได้ ⇒ null</summary>
    public static long? ParseQuota(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "limited") return null;
            return root.TryGetProperty("value", out var v) && v.TryGetInt64(out var n) && n >= 0 ? n : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>อ่านยอดใช้จาก <c>GET /v2/bot/message/quota/consumption</c>: <c>{"totalUsage":N}</c> ⇒ N · อ่านไม่ได้ ⇒ null</summary>
    public static long? ParseConsumption(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("totalUsage", out var v) && v.TryGetInt64(out var n) && n >= 0 ? n : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>ถึงเกณฑ์เตือนไหม (≥ <see cref="WarnPercent"/>% ของเพดาน) · เพดานไม่รู้/ไม่จำกัด ⇒ ไม่เตือน</summary>
    public static bool Crossed(long used, long? limit) => limit is long l && l > 0 && used * 100 >= l * WarnPercent;

    /// <summary>ข้อความเตือนผู้ดูแลเรื่อง push ของ OA กลาง</summary>
    public static string PushAlert(string period, long used, long limit)
        => $"[NextAcc OA] งวด {period}: ข้อความ push ของ OA กลางใช้ไป {N(used)}/{N(limit)} (≥ {WarnPercent}%) — เกินโควตา LINE จะตอบ 429 · "
           + "ตรวจแผน/เพดานส่วนเกินใน LINE OA Manager (NOTIFY §5 ข้อ 1) · ข้อความถึงผู้เช่าไม่ถูกตัดโควตาเพราะเรื่องนี้";

    /// <summary>ข้อความเตือนผู้ดูแลเรื่อง reply ของ OA กลาง</summary>
    public static string ReplyAlert(string period, long replies, long free)
        => $"[NextAcc OA] งวด {period}: reply ของ OA กลาง {N(replies)} ครั้ง (≥ {WarnPercent}% ของ {N(free)} ที่ฟรีตามประกาศ — ยังไม่ยืนยันกับ LINE) · "
           + "ตรวจประกาศ reply ใน OA Manager (NOTIFY §5 ข้อ 9) · ไม่ตัดโควตาผู้เช่า";

    private static string N(long v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
