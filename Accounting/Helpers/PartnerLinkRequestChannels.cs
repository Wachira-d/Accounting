using System.Net;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินของ "ช่องทางแจ้งคำขอเชื่อมคู่ค้า" รอบ 280** (คำตัดสิน 280-1…280-9 · <c>erp-review/2026-09-29/DECISIONS.md</c>) · pure
///
/// <para>═══ ที่มา ═══ เจ้าของ (2026-11-20): "คำขอส่งไปทางไหน ไลน์ที่ผูกกันไว้ไม่เด้ง เมล์ไม่มี ปรับให้เลือกช่องทางการส่งแจ้งได้ด้วย" —
/// รอบ 264 แจ้งเฉพาะ<b>ในระบบของคู่ค้า</b> (เครื่องแจ้งเตือนของบริษัทปลายทาง · เฉพาะเมื่อเลขนี้ใช้ NextAcc) · คู่ค้าที่ยังไม่ใช้ NextAcc ไม่ได้รับอะไรเลย ·
/// กลุ่ม LINE/อีเมลที่<b>เรา</b>ผูกไว้กับผู้ติดต่อรายนั้นไม่ถูกใช้</para>
///
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item><see cref="InApp"/> (แจ้งในระบบของคู่ค้า) ทำเสมอ — ผลไม่ถูกแสดง (ไม่บอกว่าเลขนี้ใช้ NextAcc · คำตัดสิน 264-1-2 คงเดิม)</item>
/// <item><see cref="LineGroup"/> / <see cref="Email"/> = ช่องของ<b>ผู้ติดต่อของเราเอง</b> (ข้อมูลที่เรามีอยู่แล้ว) ⇒ ผลส่งแสดงได้ตามจริง (ส่งสำเร็จ/ล้มพร้อมเหตุ) โดยไม่รั่วสมาชิกภาพ ·
/// ผู้ติดต่อต้องเป็นของบริษัทเรา และเลขภาษีของผู้ติดต่อต้องตรงกับคำขอ (หรือว่าง) — ไม่ส่งเรื่องของเลข X ถึงผู้ติดต่อเลข Y</item>
/// <item>เรื่องที่ปลายทางรับ = <see cref="NotifyTopic.PartnerLink"/> (เรื่องใหม่) — <b>ไม่เปิดให้ปลายทางเดิมเอง</b> (ค่าที่เก็บไว้ของผู้รับชนะ · เหมือนส่งเอกสารเอง)
/// ปลายทางใหม่ได้เป็นค่าตั้งต้น · ปลายทางเดิมที่ยังไม่ติ๊ก ⇒ ช่องปิดพร้อมเหตุ + ลิงก์ไปติ๊กที่หน้าผู้ติดต่อ (คำตัดสิน 280-2)</item>
/// <item>ข้อความนอกแอปไม่มีข้อความอิสระของผู้ส่ง · เลขภาษีของคู่ค้าแบบปิดบัง · ลิงก์เดียวทุกกรณี (หน้าคู่ค้า · ยังไม่ใช้ NextAcc = สมัครด้วยเลขนี้แล้วคำขอรออยู่)</item>
/// <item>กันส่งถี่ต่อคำขอต่อช่อง: ไม่เกิน <see cref="MaxAttemptsPerChannel"/> ครั้ง และห่างกัน ≥ <see cref="MinResendHours"/> ชม. (นับเฉพาะครั้งที่ลองส่งจริง) ·
/// คำขอที่เชื่อมแล้ว/หมดอายุ ⇒ ไม่ส่ง</item>
/// <item>"ส่งสำเร็จ" ประทับเมื่อผู้ให้บริการ (LINE/อีเมล) ตอบรับจริงเท่านั้น</item>
/// </list></para>
/// </summary>
public static class PartnerLinkRequestChannels
{
    public const string RuleCode = "PARTNER-LINK-NOTIFY";

    // ── ชื่อช่อง (เก็บเป็นชื่อ · หน้าเว็บส่งชื่อเหล่านี้) ──
    public const string InApp = "InApp";
    public const string LineGroup = "LineGroup";
    public const string Email = "Email";

    /// <summary>ช่องที่ผู้ใช้เลือกได้ (InApp ทำเสมอ ไม่ต้องเลือก)</summary>
    public static readonly IReadOnlyList<string> Selectable = new[] { LineGroup, Email };

    public static string ChannelLabel(string? channel) => channel switch
    {
        InApp => "แจ้งในระบบ NextAcc ของคู่ค้า",
        LineGroup => "กลุ่ม LINE ของผู้ติดต่อ",
        Email => "อีเมลของผู้ติดต่อ",
        _ => "ไม่ทราบช่องทาง",
    };

    /// <summary>ชื่อช่องที่ผู้ใช้ส่งมา ⇒ ชุดที่รู้จัก (ไม่ซ้ำ · ตัดชื่อแปลก/InApp ทิ้ง — InApp ไม่ใช่ตัวเลือก)</summary>
    public static IReadOnlyList<string> NormalizeSelection(IEnumerable<string?>? raw)
        => (raw ?? Enumerable.Empty<string?>()).Select(c => (c ?? "").Trim())
            .Where(c => Selectable.Contains(c)).Distinct().OrderBy(c => c == LineGroup ? 0 : 1).ToList();

    // ── สถานะแถวการแจ้ง (ชื่อ enum เก็บเป็นข้อความ) ──
    public const string StatusQueued = "Queued";
    public const string StatusSending = "Sending";
    public const string StatusSent = "Sent";
    /// <summary>ฝ่ายค้าน LOW-1: ส่งได้บางปลายทาง (หลายกลุ่ม) — ไม่ใช่ "ส่งสำเร็จ" · เหตุ = รายการที่ล้ม</summary>
    public const string StatusPartial = "Partial";
    public const string StatusFailed = "Failed";
    public const string StatusRefused = "Refused";

    public static string StatusLabel(string? status) => status switch
    {
        StatusQueued => "เข้าคิวแจ้งแล้ว — ถ้าเลขนี้ใช้ NextAcc ผู้ดูแลของเขาได้รับแจ้งในระบบ (ระบบไม่แสดงผลเพื่อคุ้มครองข้อมูลคู่ค้า)",
        StatusSending => "กำลังส่ง / ไม่ทราบผล (เครื่องหยุดกลางทาง)",
        StatusSent => "ส่งสำเร็จ",
        StatusPartial => "ส่งได้บางส่วน",
        StatusFailed => "ส่งไม่สำเร็จ",
        StatusRefused => "ไม่ได้ส่ง",
        _ => string.IsNullOrWhiteSpace(status) ? "-" : status!,
    };

    /// <summary>ครั้งที่ "ลองส่งจริง" (นับเพดาน) — ไม่ได้ส่งเพราะช่องปิด/ถูกจำกัดไม่นับ</summary>
    public static bool CountsAsAttempt(string? status) => status is StatusQueued or StatusSending or StatusSent or StatusPartial or StatusFailed;

    // ── กันส่งถี่ (ต่อคำขอต่อช่อง) ──
    public const int MaxAttemptsPerChannel = 3;
    public const int MinResendHours = 12;

    /// <summary>ส่งช่องนี้ของคำขอนี้ได้ไหม · null = ได้ · <paramref name="previousAttemptsUtc"/> = เวลาของครั้งที่ลองส่งจริงก่อนหน้า (ช่องเดียวกัน)</summary>
    public static string? ThrottleProblem(IEnumerable<DateTime> previousAttemptsUtc, DateTime nowUtc)
    {
        var list = previousAttemptsUtc.ToList();
        if (list.Count >= MaxAttemptsPerChannel)
            return $"แจ้งทางช่องนี้ครบ {MaxAttemptsPerChannel} ครั้งของคำขอนี้แล้ว — ติดต่อคู่ค้าโดยตรง หรือรอคำขอหมดอายุแล้วส่งใหม่";
        if (list.Count > 0)
        {
            var next = list.Max().AddHours(MinResendHours);
            if (nowUtc < next)
                return $"แจ้งทางช่องนี้ไปแล้วเมื่อไม่นาน — ส่งซ้ำได้อีกครั้งหลัง {ThaiTime(next)} (เว้นอย่างน้อย {MinResendHours} ชั่วโมง กันรบกวนคู่ค้า)";
        }
        return null;
    }

    // ── ฝ่ายค้าน MED-2 (คำตัดสิน 280-11): เพดานต่อ (บริษัทผู้ส่ง × ผู้ติดต่อ) ข้ามทุกคำขอ — กันสร้างคำขอใหม่เพื่อเลี่ยงเพดานต่อคำขอ/ใช้เป็นช่องส่งข้อความซ้ำ ──
    public const int MaxContactSendsPerWindow = 3;
    public const int ContactWindowDays = 7;

    /// <summary>ส่งทาง LINE/อีเมลถึงผู้ติดต่อนี้ได้อีกไหม · <paramref name="attemptsInWindowUtc"/> = ครั้งที่ลองส่งจริงทาง LINE/อีเมลถึงผู้ติดต่อนี้ (ทุกคำขอ · Invite + Renotify)
    /// ภายใน <see cref="ContactWindowDays"/> วัน รวมที่วางแผนในการกดเดียวกัน · null = ได้</summary>
    public static string? ContactWindowProblem(IEnumerable<DateTime> attemptsInWindowUtc, DateTime nowUtc)
    {
        var list = attemptsInWindowUtc.Where(t => t > nowUtc.AddDays(-ContactWindowDays)).ToList();
        if (list.Count < MaxContactSendsPerWindow) return null;
        var next = list.OrderByDescending(t => t).Skip(MaxContactSendsPerWindow - 1).First().AddDays(ContactWindowDays);
        return $"แจ้งผู้ติดต่อนี้ทาง LINE/อีเมลครบ {MaxContactSendsPerWindow} ครั้งใน {ContactWindowDays} วันแล้ว (นับทุกคำขอ) — ส่งได้อีกหลัง {ThaiTime(next)} หรือติดต่อคู่ค้าโดยตรง";
    }

    /// <summary>คำขอยังแจ้งได้ไหม (ตามสถานะที่ผู้ขอเห็นอยู่แล้ว — ไม่รั่ว) · null = ได้</summary>
    public static string? RequestProblem(DateTime? acceptedAt, DateTime expiresAt, DateTime nowUtc)
        => PartnerLinkRequestRules.OutgoingStateOf(acceptedAt, expiresAt, nowUtc) switch
        {
            PartnerLinkRequestRules.OutgoingState.Accepted => "คำขอนี้เชื่อมแล้ว — ไม่ต้องแจ้งอีก",
            PartnerLinkRequestRules.OutgoingState.Expired => "คำขอนี้หมดอายุแล้ว — ส่งคำขอใหม่จากช่อง \"ส่งคำขอเชื่อม\"",
            _ => null,
        };

    // ── ผู้ติดต่อ ──
    public const string NoContactReason = "ยังไม่ได้เลือกผู้ติดต่อ — เลือกผู้ติดต่อของคู่ค้ารายนี้ (หรือเพิ่มผู้ติดต่อด้วยเลขผู้เสียภาษีนี้) เพื่อใช้กลุ่ม LINE/อีเมลที่ผูกไว้";
    public const string ContactNotFoundReason = "ไม่พบผู้ติดต่อนี้ในบริษัทของคุณ (อาจถูกลบแล้ว) — เลือกผู้ติดต่อใหม่";
    /// <summary>ฝ่ายค้าน MED-2: ผู้ติดต่อไม่มีเลขภาษี ⇒ ไม่ส่ง (เดิมยอม — ผู้ใช้เลือกผู้ติดต่อใดก็ได้ ⇒ ใช้เป็นช่องส่งข้อความถึงใครก็ได้)</summary>
    public const string ContactNoTaxIdReason = "ผู้ติดต่อนี้ยังไม่มีเลขผู้เสียภาษี — กรอกเลขให้ตรงกับเลขในคำขอที่หน้าผู้ติดต่อก่อน (ส่งทาง LINE/อีเมลได้เฉพาะผู้ติดต่อเลขเดียวกับคำขอ)";
    public const string ContactTaxMismatchReason = "เลขผู้เสียภาษีของผู้ติดต่อนี้ไม่ตรงกับเลขในคำขอ — ไม่ส่งเรื่องของเลขหนึ่งถึงผู้ติดต่ออีกเลขหนึ่ง (แก้เลขที่หน้าผู้ติดต่อ หรือเลือกผู้ติดต่อที่ถูก)";

    /// <summary>ผู้ติดต่อใช้กับคำขอเลขนี้ได้ไหม · null = ได้ · ต้องมีเลขภาษีและตรงกับคำขอ (ฝ่ายค้าน MED-2 — เลขว่างไม่ผ่าน)</summary>
    public static string? ContactProblem(bool found, string? contactTaxId, string requestTaxId)
    {
        if (!found) return ContactNotFoundReason;
        var t = ThaiTaxId.Normalize(contactTaxId);
        if (t.Length == 0) return ContactNoTaxIdReason;
        return t == ThaiTaxId.Normalize(requestTaxId) ? null : ContactTaxMismatchReason;
    }

    /// <summary>ลิงก์ไปแท็บ "ช่องทางแจ้งเตือน" ของผู้ติดต่อ (เพิ่มกลุ่ม/อีเมล/ติ๊กเรื่อง)</summary>
    public static string ContactNotifyUrl(Guid contactId) => $"/pages/contacts.html?id={contactId:D}&tab=notify";

    /// <summary>ลิงก์เพิ่มผู้ติดต่อใหม่ด้วยเลขภาษีนี้</summary>
    public static string NewContactUrl(string taxId) => "/pages/contacts.html?autoopen=1&taxId=" + Uri.EscapeDataString(ThaiTaxId.Normalize(taxId));

    public const string NoLineGroupReason = "ผู้ติดต่อนี้ยังไม่มีกลุ่ม LINE ที่ผูกไว้ — เพิ่มกลุ่มที่แท็บ \"ช่องทางแจ้งเตือน\" ของผู้ติดต่อ";
    public const string NoEmailReason = "ผู้ติดต่อนี้ยังไม่มีอีเมล — กรอกอีเมลที่หน้าผู้ติดต่อ";

    /// <summary>ช่องหนึ่งของผู้ติดต่อ (หน้าแสดงตรงตัว) — <paramref name="Targets"/> = ป้ายผู้รับที่ส่งได้ (ไม่มี LINE id)</summary>
    public sealed record ChannelOption(string Channel, string Label, bool Available, string? Reason, string? FixUrl, IReadOnlyList<string> Targets);

    /// <summary>ผลลัพธ์ของปลายทางหนึ่งตัวจากตัวหาผู้รับ (ผู้เรียกแปลงจาก <c>ContactRecipientRule.RecipientOption</c>)</summary>
    public sealed record EndpointView(string Kind, string Display, bool Available, string? Reason);

    /// <summary>สรุปช่อง LINE กลุ่ม/อีเมล ของผู้ติดต่อ จากผลตัวหาผู้รับเรื่อง <see cref="NotifyTopic.PartnerLink"/> (ตัวเดียวของหน้าเลือกช่อง + ด่านตอนส่ง)</summary>
    public static ChannelOption Summarize(string channel, Guid contactId, IEnumerable<EndpointView> endpoints)
    {
        var kind = channel == LineGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.Email;
        var rows = endpoints.Where(e => e.Kind == kind).ToList();
        var ok = rows.Where(e => e.Available).Select(e => e.Display).Distinct().ToList();
        var fix = ContactNotifyUrl(contactId);
        if (ok.Count > 0) return new ChannelOption(channel, ChannelLabel(channel), true, null, null, ok);
        if (rows.Count == 0)
            return new ChannelOption(channel, ChannelLabel(channel), false, channel == LineGroup ? NoLineGroupReason : NoEmailReason, fix, Array.Empty<string>());
        var reasons = rows.Where(e => !string.IsNullOrWhiteSpace(e.Reason)).Select(e => $"{e.Display}: {e.Reason}").Distinct().ToList();
        return new ChannelOption(channel, ChannelLabel(channel), false,
            (reasons.Count == 0 ? "ปลายทางของผู้ติดต่อใช้ไม่ได้" : string.Join(" · ", reasons)) + " (แก้ที่แท็บ \"ช่องทางแจ้งเตือน\" ของผู้ติดต่อ)", fix, Array.Empty<string>());
    }

    /// <summary>ป้ายผู้รับบนแถวการแจ้ง — อีเมลปิดบัง · กลุ่ม = ชื่อที่ตั้ง (ไม่มี id)</summary>
    public static string RecipientLabel(string channel, string display)
        => channel == Email ? (PiiMask.Email(display) ?? "-") : display;

    // ── ข้อความ ──
    /// <summary>หน้าที่ลิงก์พาไป — หน้าคู่ค้า (เข้าสู่ระบบก่อน · ยังไม่มีบัญชี ⇒ หน้าเข้าสู่ระบบมีทางสมัคร) · ลิงก์เดียวทุกกรณี (ไม่บอกว่าเลขใช้ NextAcc)</summary>
    public const string LandingPath = "/pages/partners.html?incoming=1";
    public const string RegisterPath = "/register.html";

    public sealed record Message(string Subject, string Text, string Html);

    /// <summary>ข้อความถึงผู้ติดต่อของเรา (LINE กลุ่ม · อีเมล) — ชื่อบริษัทผู้ส่ง (เราเอง) + เลขภาษีของคู่ค้าแบบปิดบัง + ลิงก์ · ไม่มีข้อความอิสระ/ยอดเงิน/เลขเต็ม ·
    /// <paramref name="origin"/> null ⇒ ผู้เรียกต้องไม่ส่ง (<see cref="NoBaseUrlReason"/>)</summary>
    public static Message Compose(string? senderCompanyName, string partnerTaxId, string? partnerBranchCode, DateTime expiresAtUtc, string origin)
    {
        var sender = string.IsNullOrWhiteSpace(senderCompanyName) ? "คู่ค้าของคุณ" : senderCompanyName!.Trim();
        var masked = PartnerTaxMasked(partnerTaxId);
        var branch = partnerBranchCode == null ? "" : " · " + TaxBranchCode.Label(partnerBranchCode);
        var until = QuotaAddOnPricing.ThaiDateText(expiresAtUtc.AddHours(7).Date);
        var o = origin.TrimEnd('/');
        var link = o + LandingPath;
        var register = o + RegisterPath;
        // ฝ่ายค้าน MED-2: หัวเรื่องคงที่ (ชื่อบริษัท = ข้อความที่ผู้ส่งตั้งเอง ⇒ ไม่อยู่ในหัวเรื่อง) · บรรทัดแรกคงที่จาก NextAcc บอกผู้ส่งจริง
        var subject = Subject;
        var text = SenderLine(sender) + "\n"
                 + $"{sender} ส่งคำขอเชื่อมระบบบัญชีผ่าน NextAcc ถึงบริษัทของคุณ (เลขผู้เสียภาษี {masked}{branch})\n"
                 + "เมื่อเชื่อมแล้ว เอกสารซื้อ-ขายระหว่างกันส่งเข้าระบบของแต่ละฝ่ายได้โดยไม่ต้องพิมพ์ซ้ำ — ยังไม่มีข้อมูลใดของคุณถูกส่งจนกว่าคุณจะกดยอมรับ\n"
                 + $"• ใช้ NextAcc อยู่แล้ว: เข้าสู่ระบบด้วยบริษัทเลขนี้ แล้วเปิด {link} ดูที่ \"คำขอที่รอเรายอมรับ\"\n"
                 + $"• ยังไม่ใช้ NextAcc: สมัครที่ {register} และตั้งบริษัทด้วยเลขผู้เสียภาษีนี้ แล้วแจ้ง {sender} ให้กด \"ส่งแจ้งอีกครั้ง\" — คำขอจะขึ้นที่หน้า \"คู่ค้าที่ใช้ NextAcc\"\n"
                 + $"คำขอหมดอายุ {until}";
        var e = (Func<string, string>)WebUtility.HtmlEncode;
        var html = $@"<div style='font-family:sans-serif;font-size:14px;color:#1f2937'>
<p style='color:#64748b;font-size:12px'>{e(SenderLine(sender))}</p>
<p><strong>{e(sender)}</strong> ส่งคำขอเชื่อมระบบบัญชีผ่าน NextAcc ถึงบริษัทของคุณ (เลขผู้เสียภาษี {e(masked)}{e(branch)})</p>
<p>เมื่อเชื่อมแล้ว เอกสารซื้อ-ขายระหว่างกันส่งเข้าระบบของแต่ละฝ่ายได้โดยไม่ต้องพิมพ์ซ้ำ — ยังไม่มีข้อมูลใดของคุณถูกส่งจนกว่าคุณจะกดยอมรับ</p>
<ul>
<li>ใช้ NextAcc อยู่แล้ว: เข้าสู่ระบบด้วยบริษัทเลขนี้ แล้วเปิด <a href='{e(link)}'>หน้าคู่ค้าที่ใช้ NextAcc</a> ดูที่ ""คำขอที่รอเรายอมรับ""</li>
<li>ยังไม่ใช้ NextAcc: <a href='{e(register)}'>สมัคร</a> และตั้งบริษัทด้วยเลขผู้เสียภาษีนี้ แล้วแจ้ง {e(sender)} ให้กด ""ส่งแจ้งอีกครั้ง"" — คำขอจะขึ้นที่หน้า ""คู่ค้าที่ใช้ NextAcc""</li>
</ul>
<p style='color:#64748b'>คำขอหมดอายุ {e(until)}</p>
</div>";
        return new Message(subject, text, html);
    }

    /// <summary>หัวเรื่องอีเมลคงที่ (ไม่มีข้อความที่ผู้ส่งตั้งเอง)</summary>
    public const string Subject = "คำขอเชื่อมระบบบัญชีผ่าน NextAcc";

    /// <summary>บรรทัดคงที่จาก NextAcc ว่าใครเป็นผู้ส่ง (อีเมล + LINE)</summary>
    public static string SenderLine(string sender) => $"ข้อความนี้ส่งโดยบริษัท {sender} ผ่าน NextAcc — NextAcc ไม่ได้ตรวจสอบเนื้อหาธุรกิจของผู้ส่ง";

    public const string NoBaseUrlReason = "ยังไม่ได้ตั้ง URL ของระบบ (" + SystemBaseUrl.AdminPageHint + " / App:BaseUrl) — ข้อความต้องมีลิงก์ให้คู่ค้ากด จึงยังไม่ส่ง";

    /// <summary>เลขภาษีของคู่ค้าแบบปิดบัง (ข้อความในกลุ่ม — สมาชิกกลุ่มหลายคน) · ไม่ใช่ 13 หลัก ⇒ "-"</summary>
    private static string PartnerTaxMasked(string? taxId)
    {
        var d = ThaiTaxId.Normalize(taxId);
        return d.Length == 13 ? PiiMask.CitizenId(d)! : "-";
    }

    /// <summary>หมายเหตุของช่องในระบบ (หน้าแสดงตรงตัว — ข้อความเดียวทุกกรณี)</summary>
    public const string InAppNote = "ถ้าเลขนี้ใช้ NextAcc ผู้ดูแลของเขาได้รับแจ้งในระบบเสมอ (กระดิ่ง · อีเมล · LINE ตามที่เขาตั้ง) — ระบบไม่แสดงผลว่าเลขนี้ใช้ NextAcc หรือไม่";

    /// <summary>วันเวลาไทย (พ.ศ. ไม่ขึ้นกับ culture ของเครื่อง)</summary>
    private static string ThaiTime(DateTime utc)
    {
        var t = utc.AddHours(7);
        return QuotaAddOnPricing.ThaiDateText(t.Date) + " " + t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " น.";
    }
}
