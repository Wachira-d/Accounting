using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 267 แพ็ก P3 — <b>หน้าต่างเดียว "📤 ส่งให้ลูกค้า" + ไทม์ไลน์ลูกค้า</b> (pure · คำตัดสิน 267-1-61…67 · DOCUMENT_FLOW §2.12n-P3)
/// <list type="bullet">
/// <item>ช่องทาง (คัดลอกลิงก์ · อีเมล · LINE · QR) + ป้าย + ผลต่อช่องทาง — <b>ผลแยกกันทุกช่องทาง</b> (อีเมลล้มไม่ลาก LINE · ทุกช่องทางบอกผลของตัวเองดัง — F2 ข้อ 7) ·
/// "เข้าคิวแล้ว" ไม่ใช่ "ส่งแล้ว" (LINE = ผู้ส่งคิวได้คำตอบจาก LINE จริงเท่านั้น)</item>
/// <item>ตัวระบุแหล่งที่มา <c>s</c> ของลิงก์บนการ์ด LINE — <b>ไม่ใช่สิทธิ์</b> (สิทธิ์ = token ของใบ) · ใช้ส่งฉบับเซ็นกลับไป LINE เดิม ·
/// ผิด/หาย = ไม่มีผลต่อการใช้งานหน้า (267-1-63)</item>
/// <item>บล็อกลิงก์หน้าเอกสารในอีเมล · ป้ายปุ่ม · ตัวเลือก "เปิดให้ลูกค้าตอบรับ" · ชนิด/ป้ายของแถวไทม์ไลน์ — หน้าเว็บไม่มีตาราง/ป้ายของตัวเอง (F2 ข้อ 5)</item>
/// </list>
/// ผู้เรียก: <c>CustomerShareService</c> (orchestrate บริการเดิม — ไม่ประกอบเส้นส่งเอง) · <c>CustomerTimelineService</c> · <c>DocumentEmailService</c> (บล็อกลิงก์) ·
/// <c>CustomerDocumentResponseService</c> (อ่าน <c>s</c>)
/// </summary>
public static class CustomerShareRule
{
    public const string RuleCode = "DOC-SHARE-267";

    // ═══════════════ ช่องทาง ═══════════════

    public const string ChannelCopy = "copy";
    public const string ChannelEmail = "email";
    public const string ChannelLine = "line";
    public const string ChannelQr = "qr";

    public sealed record Channel(string Code, string Label, string Hint);

    /// <summary>ช่องทางทั้งหมดตามลำดับที่หน้าต่างแสดง</summary>
    public static readonly IReadOnlyList<Channel> Channels = new[]
    {
        new Channel(ChannelCopy, "คัดลอกลิงก์", "ลิงก์หน้าเอกสารของใบนี้ (ลิงก์เดียวต่อใบ) — วางในแชตหรือช่องทางอื่นได้"),
        new Channel(ChannelEmail, "อีเมล", "ส่งอีเมลพร้อมปุ่มเปิดหน้าเอกสาร (ใบกำกับของบริษัทโหมด e-Tax by Email ส่งตามรูปแบบสรรพากร)"),
        new Channel(ChannelLine, "LINE", "ส่งการ์ดถึง LINE ผู้ใช้/กลุ่ม LINE ของผู้ติดต่อ (ผ่าน LINE OA ของบริษัท)"),
        new Channel(ChannelQr, "QR พิมพ์", "QR ของลิงก์หน้าเอกสาร — พิมพ์ติดเอกสารหรือให้ลูกค้าสแกน"),
    };

    public static string ChannelLabel(string code) => Channels.FirstOrDefault(c => c.Code == code)?.Label ?? code;

    /// <summary>ช่องทางที่ผู้ใช้เลือก ⇒ รายการไม่ซ้ำตามลำดับมาตรฐาน · ว่าง/ไม่รู้จัก ⇒ เหตุ (ทั้งคำขอถูกปฏิเสธ — ไม่ส่งบางส่วนเงียบ ๆ)</summary>
    public static (IReadOnlyList<string> Channels, string? Problem) ParseChannels(IEnumerable<string>? requested)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested ?? Array.Empty<string>())
        {
            var c = (raw ?? "").Trim().ToLowerInvariant();
            if (c.Length == 0) continue;
            if (Channels.All(x => x.Code != c)) return (Array.Empty<string>(), $"ไม่รู้จักช่องทาง \"{c}\" — เลือกจาก: {string.Join(" · ", Channels.Select(x => x.Label))}");
            wanted.Add(c);
        }
        if (wanted.Count == 0) return (Array.Empty<string>(), "เลือกช่องทางอย่างน้อย 1 ช่องทาง");
        return (Channels.Where(x => wanted.Contains(x.Code)).Select(x => x.Code).ToList(), null);
    }

    /// <summary>ปุ่ม "📤 ส่งให้ลูกค้า" บนรายการ/หน้ารายละเอียดเอกสาร แสดงไหม — ด่านชนิด/สถานะเดียวกับตัวออกลิงก์หน้าเอกสาร
    /// (<see cref="CustomerDocPage.IssueRefusal"/> · ฝั่งของใบลดหนี้/เพิ่มหนี้และใบค่าบริการผู้เช่าตัดสินในหน้าต่างพร้อมเหตุ)</summary>
    public static bool ButtonOffered(DocumentType type, DocumentStatus status)
        => CustomerDocPage.IssueRefusal(type, status, customerFacing: true, platformTenantDocument: false) == null;

    // ═══════════════ ผลต่อช่องทาง ═══════════════

    public const string StatusOk = "ok";
    public const string StatusQueued = "queued";
    public const string StatusFailed = "failed";
    public const string StatusSkipped = "skipped";

    /// <summary>ผลของช่องทางหนึ่ง — <paramref name="Status"/>: ok (ทำแล้ว — คัดลอก/QR/อีเมลที่ผู้ให้บริการรับ) · queued (เข้าคิว LINE แล้ว — ผลจริงดูที่คิว) ·
    /// failed (ล้ม — ข้อความบอกเหตุ + ทางไปต่อ) · skipped (ไม่ได้ทำเพราะช่องทางนั้นใช้กับใบนี้ไม่ได้)</summary>
    public sealed record ChannelOutcome(string Channel, string Label, string Status, string Message, string? RuleCode = null);

    public static ChannelOutcome Ok(string channel, string message) => new(channel, ChannelLabel(channel), StatusOk, message);
    public static ChannelOutcome Queued(string channel, string message) => new(channel, ChannelLabel(channel), StatusQueued, message);
    public static ChannelOutcome Failed(string channel, string message, string? ruleCode = null) => new(channel, ChannelLabel(channel), StatusFailed, message, ruleCode);
    public static ChannelOutcome Skipped(string channel, string message) => new(channel, ChannelLabel(channel), StatusSkipped, message);

    /// <summary>สรุปผลทั้งคำขอ — สำเร็จเมื่อ<b>ไม่มีช่องทางใดล้ม/ถูกข้าม</b> (ช่องทางหนึ่งล้ม ⇒ คำตอบเป็น "ไม่สำเร็จ" พร้อมผลของทุกช่องทาง ·
    /// ช่องทางที่สำเร็จยังสำเร็จ — ไม่ย้อน · ไม่ซ่อนช่องทางที่ล้มหลังช่องทางที่ผ่าน)</summary>
    public static (bool AllOk, string Message) Summary(IReadOnlyList<ChannelOutcome> outcomes)
    {
        if (outcomes.Count == 0) return (false, "ไม่มีช่องทางที่ส่ง");
        var bad = outcomes.Where(o => o.Status is StatusFailed or StatusSkipped).ToList();
        var parts = outcomes.Select(o => $"{o.Label}: {o.Message}");
        var text = string.Join(" · ", parts);
        return bad.Count == 0
            ? (true, text)
            : (false, $"ส่งไม่ครบ ({bad.Count} จาก {outcomes.Count} ช่องทาง) — " + text);
    }

    // ═══════════════ ตัวเลือก "เปิดให้ลูกค้าตอบรับ" ═══════════════

    /// <summary>ตัวเลือกบนหน้าต่าง — ใบที่มีปุ่มตอบรับ (ใบเสนอราคา · ใบส่งของ) เท่านั้น · ลิงก์ดู ≠ สิทธิ์ตอบรับ (267-1-25) ⇒ ผู้ขายต้องติ๊กเพื่อเปิดช่วงตอบรับ ·
    /// <paramref name="Open"/> = ช่วงตอบรับยังเปิดอยู่แล้ว (ติ๊กซ้ำ = ใช้ช่วงเดิม ไม่หมุน token) · <paramref name="Note"/> = เหตุเมื่อเปิดไม่ได้</summary>
    public sealed record RespondOption(bool Applies, bool Offered, bool Open, string? Label, string? Note);

    public static RespondOption Respond(DocumentType type, DocumentStatus status, bool windowOpen, DateTime? signedAt)
    {
        var k = SignRequestKinds.For(type);
        if (k == null) return new(false, false, false, null, null);
        if (signedAt != null) return new(true, false, false, "เปิดให้ลูกค้า" + k.ActionTh, $"ลูกค้า{k.ActionTh}แล้ว — ไม่ต้องเปิดอีก");
        if (SignRequestKinds.IssueRefusal(type, status) is { } refusal) return new(true, false, false, "เปิดให้ลูกค้า" + k.ActionTh, refusal);
        return new(true, true, windowOpen, "เปิดให้ลูกค้า" + k.ActionTh + " บนหน้าเอกสาร",
            windowOpen ? "ช่วงตอบรับเปิดอยู่แล้ว (ใช้ช่วงเดิม)"
                : type == DocumentType.Quotation ? "ลูกค้ายอมรับได้ถึงวันยืนราคา (ไม่มีวันที่ = 30 วัน) · ไม่ติ๊ก = ลูกค้าเปิดดูได้อย่างเดียว"
                : $"ลูกค้าเซ็นรับได้ {k.TtlDays} วัน · ไม่ติ๊ก = ลูกค้าเปิดดูได้อย่างเดียว");
    }

    // ═══════════════ อีเมล ═══════════════

    /// <summary>อีเมลทางนี้ใส่บล็อกลิงก์หน้าเอกสารได้ไหม — ทาง e-Tax by Email เนื้ออีเมลตามรูปแบบสรรพากร ⇒ ไม่ได้ (เหตุบนหน้าต่าง · ไม่ทิ้งค่าเงียบ)</summary>
    public static bool EmailCarriesPageLink(EtaxEmailChannel channel) => channel != EtaxEmailChannel.EtaxByEmail;

    public const string EtaxNoPageLinkNote =
        "ใบนี้ส่งแบบ e-Tax by Email (เนื้ออีเมลตามรูปแบบสรรพากร) — อีเมลไม่มีปุ่มลิงก์หน้าเอกสาร · ส่งลิงก์ทาง LINE หรือคัดลอกลิงก์แยก";

    /// <summary>ป้ายปุ่มในอีเมล — ช่วงตอบรับเปิด ⇒ ป้ายของการตอบรับ (<see cref="SignRequestKinds.Kind.ButtonTh"/>) · ใบที่จ่ายได้ + มียอดค้าง ⇒ ดู/ชำระ · อื่น ๆ ⇒ เปิดดู</summary>
    public static string EmailPageButton(bool en, DocumentType type, bool respondOpen, bool outstanding)
    {
        if (respondOpen && SignRequestKinds.For(type) is { } k) return en ? k.ButtonEn : k.ButtonTh;
        if (outstanding && CustomerDocPage.IsPayableType(type)) return en ? "View and pay the document" : "เปิดดูเอกสารและชำระเงิน";
        return en ? "View the document" : "เปิดดูเอกสาร";
    }

    /// <summary>เครื่องหมายครอบบล็อกลิงก์หน้าเอกสาร (หาเจอ/ถอดได้ — แบบเดียวกับบล็อกลิงก์ชำระ)</summary>
    public const string PageBlockStart = "<!--nextacc-page-link-->";
    public const string PageBlockEnd = "<!--/nextacc-page-link-->";

    /// <summary>บล็อก HTML ในอีเมล (ค่าทุกตัวหนีแล้ว · ไม่มี tracking) — ปุ่ม + URL ข้อความล้วน (ลูกค้าคัดลอกได้เมื่อปุ่มไม่ทำงาน)</summary>
    public static string EmailPageBlock(string url, string buttonLabel, bool en)
    {
        var u = System.Net.WebUtility.HtmlEncode(url);
        var b = System.Net.WebUtility.HtmlEncode(buttonLabel);
        var note = en ? "Open this link to view, download and respond to the document." : "เปิดลิงก์นี้เพื่อดู ดาวน์โหลด และทำรายการของเอกสาร";
        return PageBlockStart
             + $"<div style='margin-top:14px;padding:12px;border:1px solid #bbf7d0;border-radius:8px;background:#f0fdf4'>{System.Net.WebUtility.HtmlEncode(note)}"
             + $"<br><a href='{u}' style='display:inline-block;margin-top:8px;background:#15803d;color:#fff;padding:9px 16px;border-radius:6px;text-decoration:none'>{b}</a>"
             + $"<br><span style='font-size:12px;color:#64748b'>{u}</span></div>"
             + PageBlockEnd;
    }

    // ═══════════════ ตัวระบุแหล่งที่มา (s) ═══════════════

    private const string LineSourcePrefix = "l";

    /// <summary>ค่า <c>s</c> ของลิงก์บนการ์ด LINE แถวหนึ่ง — ไม่ใช่ความลับ/สิทธิ์ (ผู้รับตรวจกับแถวคิวจริงก่อนใช้ — <see cref="LineSourceValid"/>)</summary>
    public static string LineSource(Guid lineQueueId) => LineSourcePrefix + lineQueueId.ToString("N");

    /// <summary>อ่าน <c>s</c> — รูปแบบผิด/ไม่ใช่ของ LINE ⇒ null (ไม่มีผลต่อการใช้งาน)</summary>
    public static Guid? ParseLineSource(string? s)
    {
        var v = (s ?? "").Trim();
        return v.Length == 33 && v.StartsWith(LineSourcePrefix, StringComparison.Ordinal)
               && Guid.TryParseExact(v[1..], "N", out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>ต่อ <c>s</c> ท้าย URL หน้าเอกสาร (URL เดิมมี query <c>?c=…&amp;t=…</c> เสมอ)</summary>
    public static string WithSource(string pageUrl, string source)
        => pageUrl + (pageUrl.Contains('?') ? "&" : "?") + "s=" + Uri.EscapeDataString(source);

    /// <summary>แถวคิวที่ <c>s</c> ชี้ใช้ส่งฉบับเซ็นกลับได้ไหม — ต้องเป็นการ์ดของ<b>ใบนี้</b> · ปลายทาง LINE <b>1:1</b> · การ์ดที่มีลิงก์หน้าเอกสาร · LINE รับแล้ว ·
    /// ผู้รับแถวนั้นได้ลิงก์ของใบนี้จากผู้ขายอยู่แล้ว ⇒ ใครปลอม <c>s</c> ได้อย่างมากแค่ส่งฉบับเซ็นให้ผู้รับที่ผู้ขายส่งใบนี้ให้เอง (267-1-63)</summary>
    public static bool LineSourceValid(bool found, string entityType, Guid entityId, Guid documentId, string endpointKind, bool? includePageLink, string status)
        => found && entityType == "Document" && entityId == documentId && endpointKind == NotifyEndpointKind.LineUser
           && includePageLink == true && status == ContactLineChannel.StatusSent;

    // ═══════════════ ไทม์ไลน์ ═══════════════

    public const string ToneOk = "ok";
    public const string ToneInfo = "info";
    public const string ToneWarn = "warn";
    public const string ToneBad = "bad";

    public const string KindLinkIssued = "link-issued";
    public const string KindLinkRevoked = "link-revoked";
    public const string KindRespondOpened = "respond-opened";
    public const string KindCustomerOpened = "customer-opened";
    public const string KindEmail = "email";
    public const string KindLine = "line";
    public const string KindCustomerAction = "customer-action";
    public const string KindLinkRequested = "link-requested";
    public const string KindPayment = "payment";
    public const string KindSlip = "slip";
    public const string KindReceipt = "post-pay";
    public const string KindEtax = "etax";

    /// <summary>ทางไปต่อของแถว (หน้าเว็บเลือกตัวทำงานจากรหัสนี้ — ป้ายมาจากเซิร์ฟเวอร์)</summary>
    public const string ActionReshare = "reshare";
    public const string ActionReviewSlips = "review-slips";

    /// <summary>หนึ่งแถวของไทม์ไลน์ — ข้อเท็จจริงจากแถวจริงเท่านั้น (ไม่ประทับสถานะเอง · ไม่มี tracking pixel) ·
    /// <paramref name="ActionId"/>/<paramref name="ActionLabel"/> = ทางไปต่อ (null = ไม่มี)</summary>
    public sealed record TimelineItem(DateTime At, string Kind, string Label, string? Detail, string Tone, string? ActionId = null, string? ActionLabel = null);

    public static string KindLabel(string kind) => kind switch
    {
        KindLinkIssued => "ออกลิงก์หน้าเอกสาร",
        KindLinkRevoked => "ยกเลิกลิงก์หน้าเอกสาร",
        KindRespondOpened => "เปิดให้ลูกค้าตอบรับ",
        KindCustomerOpened => "ลูกค้าเปิดลิงก์",
        KindEmail => "อีเมล",
        KindLine => "LINE",
        KindCustomerAction => "ลูกค้าตอบรับ",
        KindLinkRequested => "ลูกค้าขอลิงก์ใหม่",
        KindPayment => "รับชำระ",
        KindSlip => "แจ้งโอน/สลิป",
        KindReceipt => "ใบเสร็จอัตโนมัติ",
        KindEtax => "e-Tax by Email",
        _ => kind,
    };

    /// <summary>ผลส่งอีเมลหนึ่งแถว (บันทึกอีเมลของเอกสาร) — ล้ม ⇒ ทางไปต่อ "ส่งใหม่"</summary>
    public static TimelineItem EmailItem(DateTime at, bool sent, string? to, string? error, bool etax)
        => new(at, KindEmail, (etax ? "ส่ง e-Tax by Email " : "ส่งอีเมล ") + (sent ? "สำเร็จ" : "ไม่สำเร็จ"),
            (string.IsNullOrWhiteSpace(to) ? "" : "ถึง " + to) + (sent || string.IsNullOrWhiteSpace(error) ? "" : " — " + error),
            sent ? ToneOk : ToneBad, sent ? null : ActionReshare, sent ? null : "ส่งใหม่");

    /// <summary>แถวคิว LINE หนึ่งแถว — สถานะตามคิวจริง (รอส่ง/กำลังส่ง ≠ ส่งแล้ว) · ล้ม/ข้าม ⇒ ทางไปต่อ "ส่งใหม่"</summary>
    public static TimelineItem LineItem(DateTime at, string status, string endpointKind, string? error, bool signedCopy)
    {
        var who = NotifyEndpointKind.Label(endpointKind);
        var what = signedCopy ? "การ์ดฉบับเซ็น" : "การ์ดเอกสาร";
        return status switch
        {
            ContactLineChannel.StatusSent => new(at, KindLine, $"{what} ถึง {who} — LINE รับแล้ว", null, ToneOk),
            ContactLineChannel.StatusPending or ContactLineChannel.StatusSending => new(at, KindLine, $"{what} ถึง {who} — รอส่ง", null, ToneInfo),
            ContactLineChannel.StatusCancelled => new(at, KindLine, $"{what} ถึง {who} — ยกเลิก", error, ToneWarn),
            _ => new(at, KindLine, $"{what} ถึง {who} — " + (status == ContactLineChannel.StatusSkipped ? "ไม่ได้ส่ง" : "ส่งไม่สำเร็จ"), error, ToneBad,
                signedCopy ? null : ActionReshare, signedCopy ? null : "ส่งใหม่"),
        };
    }

    /// <summary>คำตอบของลูกค้าหนึ่งแถว (บัญชีหลักฐาน) — ชื่อ/ตำแหน่งตามสิทธิ์อ่านเอกสาร (267-1-59 · ไม่มี IP/อีเมล)</summary>
    public static TimelineItem CustomerActionItem(DateTime at, string action, string? signer, string? title, int revision)
    {
        var (label, tone) = action switch
        {
            nameof(CustomerSignRule.Kind.Accepted) => ("ลูกค้ายอมรับใบเสนอราคา", ToneOk),
            nameof(CustomerSignRule.Kind.DeliverySigned) => ("ลูกค้าเซ็นรับสินค้า", ToneOk),
            nameof(CustomerSignRule.Kind.Declined) => ("ลูกค้าไม่รับใบเสนอราคา", ToneBad),
            nameof(CustomerSignRule.Kind.ChangeRequested) => ("ลูกค้าขอแก้ไข", ToneWarn),
            _ => ("ลูกค้าตอบรับ", ToneInfo),
        };
        var who = string.IsNullOrWhiteSpace(signer) ? null : signer + (string.IsNullOrWhiteSpace(title) ? "" : " (" + title + ")");
        return new(at, KindCustomerAction, label, (who == null ? "" : who + " · ") + "Rev " + revision, tone);
    }
}
