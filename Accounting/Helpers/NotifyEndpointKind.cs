using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>รอบ 245 L1 — เรื่องที่ปลายทางของผู้ติดต่อเลือกรับ (LINE_MULTICHANNEL_DESIGN §3.g) · เก็บเป็น<b>ชื่อ</b>ใน <c>ContactNotifyEndpoint.TopicsJson</c>
/// (ห้ามเก็บตัวเลข — F2 ข้อ 5) · สลิปเงินเดือนไม่ใช่เรื่องของผู้ติดต่อ (ส่งเฉพาะ 1:1 ถึงพนักงานตามเส้นเดิม)</summary>
public enum NotifyTopic
{
    /// <summary>ส่งเอกสารตอนอนุมัติ/เอกสารประจำ</summary>
    Document = 1,
    /// <summary>ทวงชำระ (ใกล้/เกินกำหนด) + ใบแจ้งยอด</summary>
    Dunning = 2,
    /// <summary>ใบเสร็จหลังรับชำระ</summary>
    Receipt = 3,
    /// <summary>ส่ง/ทวงหนังสือรับรอง 50 ทวิ</summary>
    Wht = 4,
    /// <summary>ลิงก์ขอเซ็นเอกสาร</summary>
    Sign = 5,
    /// <summary>แจ้งเตือนอื่นถึงผู้ติดต่อ</summary>
    Other = 6,
}

/// <summary>รอบ 245 L1 — ตัวตั้งตัวเดียวของชนิดปลายทาง · ขอบเขต OA · ที่มา · เหตุยกเลิก ของ <c>ContactNotifyEndpoint</c> (F2 ข้อ 4)</summary>
public static class NotifyEndpointKind
{
    public const string Email = "Email";
    public const string LineUser = "LineUser";
    public const string LineGroup = "LineGroup";

    /// <summary>OA กลางของ NextAcc</summary>
    public const string ScopeNextAcc = "nextacc";
    /// <summary>OA ของบริษัทเอง</summary>
    public const string ScopeCompany = "company";
    /// <summary>ไม่รู้ว่าผูกกับ OA ใด (แถว legacy จาก <c>Contact.LineUserId</c>) — <b>ไม่ใช้ส่ง</b> (userId ใช้ได้เฉพาะ OA ที่ออกค่านั้น)</summary>
    public const string ScopeUnknown = "unknown";

    /// <summary>ผู้ดูแลบริษัทเพิ่มอีเมลเอง</summary>
    public const string SourceManual = "manual";
    /// <summary>แถวค่าตั้งของอีเมลหลัก (<c>Contact.Email</c>)</summary>
    public const string SourceContactEmail = "contact-email";
    /// <summary>ย้ายจาก <c>Contact.LineUserId</c> (one-shot รอบ 245)</summary>
    public const string SourceLegacyLineUserId = "legacy-line-userid";

    /// <summary>ผู้ดูแลบริษัทกดยกเลิก</summary>
    public const string UnlinkCompany = "company";
    /// <summary>ลบตามคำขอเจ้าของข้อมูล (DSR)</summary>
    public const string UnlinkErase = "erase";
    /// <summary>รวมผู้ติดต่อแล้วซ้ำ (แถวหลักของผู้ถูกรวม · อีเมล/LINE ซ้ำ) — ฝ่ายค้าน S4</summary>
    public const string UnlinkMerge = "merge";

    public static bool IsLine(string? kind) => kind == LineUser || kind == LineGroup;

    public static string Label(string? kind) => kind switch
    {
        Email => "อีเมล",
        LineUser => "ผู้ใช้ LINE",
        LineGroup => "กลุ่ม LINE",
        _ => "ไม่ทราบชนิด",
    };

    public static string SourceLabel(string? source) => source switch
    {
        SourceManual => "เพิ่มโดยผู้ดูแลบริษัท",
        SourceContactEmail => "อีเมลหลักของผู้ติดต่อ",
        SourceLegacyLineUserId => "LINE เดิมของผู้ติดต่อ (ก่อนรอบ 245)",
        _ => string.IsNullOrWhiteSpace(source) ? "ไม่ทราบที่มา" : source!,
    };

    public static string UnlinkReasonLabel(string? reason) => reason switch
    {
        UnlinkCompany => "ผู้ดูแลบริษัทยกเลิก",
        UnlinkErase => "ลบตามคำขอเจ้าของข้อมูล (PDPA)",
        UnlinkMerge => "ซ้ำหลังรวมผู้ติดต่อ",
        "user" => "ผู้รับยกเลิกเอง",
        "bot-removed" => "บอทถูกนำออกจากกลุ่ม",
        "relinked" => "ผูกใหม่แทน",
        "consent-withdrawn" => "ถอนความยินยอม",
        _ => string.IsNullOrWhiteSpace(reason) ? "ไม่ระบุเหตุ" : reason!,
    };

    /// <summary>SHA-256 hex ของ (OaDestination + "|" + id) — ค้นปลายทางตอน webhook (unfollow/leave) โดยไม่ถอดรหัสทุกแถว · ไม่ใช่ความลับ แต่ห้ามแสดง</summary>
    public static string LineIdHash(string? oaDestination, string lineId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(oaDestination ?? "").Trim()}|{(lineId ?? "").Trim()}"))).ToLowerInvariant();
}

/// <summary>รอบ 245 L1 — ตัวอ่าน/เขียน/ป้าย/ค่าตั้งต้นของเรื่องที่รับ (ตัวเดียว · หน้าเว็บแสดงชื่อ+ป้ายที่เซิร์ฟเวอร์ส่ง)</summary>
public static class NotifyTopics
{
    public static readonly IReadOnlyList<NotifyTopic> All = Enum.GetValues<NotifyTopic>();

    public static string Label(NotifyTopic t) => t switch
    {
        NotifyTopic.Document => "เอกสาร",
        NotifyTopic.Dunning => "ทวงชำระ/ใบแจ้งยอด",
        NotifyTopic.Receipt => "ใบเสร็จ",
        NotifyTopic.Wht => "50 ทวิ",
        NotifyTopic.Sign => "ลิงก์เซ็น",
        NotifyTopic.Other => "อื่น ๆ",
        _ => t.ToString(),
    };

    /// <summary>ค่าตั้งต้นต่อชนิด (DESIGN §4.1): อีเมล = ทุกเรื่อง · ผู้ใช้ LINE = เอกสาร/ทวงชำระ/ใบเสร็จ/ลิงก์เซ็น · กลุ่ม LINE = เอกสาร/ทวงชำระ</summary>
    public static IReadOnlyList<NotifyTopic> DefaultsFor(string? kind) => kind switch
    {
        NotifyEndpointKind.LineUser => new[] { NotifyTopic.Document, NotifyTopic.Dunning, NotifyTopic.Receipt, NotifyTopic.Sign },
        NotifyEndpointKind.LineGroup => new[] { NotifyTopic.Document, NotifyTopic.Dunning },
        _ => All,
    };

    /// <summary>JSON array ของชื่อ (เรียงตามค่า enum · ไม่ซ้ำ)</summary>
    public static string Serialize(IEnumerable<NotifyTopic> topics)
        => JsonSerializer.Serialize(topics.Distinct().OrderBy(t => (int)t).Select(t => t.ToString()).ToList());

    /// <summary>อ่านค่าที่เก็บ · เสียหาย/ชื่อที่ไม่รู้จัก ⇒ <c>null</c> (ผู้เรียกต้องถือว่า "ไม่รู้" แล้วไม่ส่ง — ห้ามเดาว่ารับทุกเรื่อง)</summary>
    public static IReadOnlyList<NotifyTopic>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(json);
            if (names == null) return null;
            var list = new List<NotifyTopic>();
            foreach (var n in names)
            {
                if (!Enum.TryParse<NotifyTopic>(n, ignoreCase: false, out var t) || !Enum.IsDefined(t) || int.TryParse(n, out _)) return null;
                if (!list.Contains(t)) list.Add(t);
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>รับชื่อเรื่องจากคำขอ — ชื่อที่ไม่รู้จัก/ตัวเลข ⇒ ปฏิเสธ (ไม่ตัดทิ้งเงียบ) · <paramref name="allowEmpty"/> = อีเมลหลักปิดทุกเรื่องได้</summary>
    public static (IReadOnlyList<NotifyTopic>? Topics, string? Problem) FromRequest(IEnumerable<string>? names, bool allowEmpty)
    {
        var list = new List<NotifyTopic>();
        foreach (var raw in names ?? Array.Empty<string>())
        {
            var n = (raw ?? "").Trim();
            if (int.TryParse(n, out _) || !Enum.TryParse<NotifyTopic>(n, ignoreCase: true, out var t) || !Enum.IsDefined(t))
                return (null, $"ไม่รู้จักเรื่อง \"{n}\" — เลือกจาก: {string.Join(" · ", All.Select(Label))}");
            if (!list.Contains(t)) list.Add(t);
        }
        if (list.Count == 0 && !allowEmpty) return (null, "เลือกเรื่องที่รับอย่างน้อย 1 เรื่อง (ไม่ต้องการรับแล้ว ให้กดยกเลิกปลายทางนี้)");
        return (list, null);
    }
}
