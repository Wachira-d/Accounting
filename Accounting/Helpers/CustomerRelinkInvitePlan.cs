using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ลิงก์ LINE ของลูกค้า 1 แถว + ข้อเท็จจริงที่ใช้ตัดสินคำเชิญ (โหลดโดยบริการ · ทุกแถวของบริษัทเดียว) ·
/// <c>TargetRelevant</c> จาก <see cref="CustomerRelinkInvitePlan.IsRelevant"/> · <c>Email</c> = อีเมลของเป้าหมาย (สมาชิก/แขกของการจอง/ผู้รับของออเดอร์)</summary>
public sealed record RelinkLinkFact(
    Guid LinkId, Guid SiteId, string TargetKind, Guid TargetId, string LineUserId, string OaScope, string? OaDestination,
    DateTime LinkedAt, DateTime? BlockedAt, bool ConsentWithdrawn, bool TargetRelevant, string? Email, string? PublicToken);

/// <summary>การสลับ OA 1 ครั้ง (แถว <c>LineOaSwitchEvents</c>) — <c>From*</c> = OA ที่ลูกค้าผูกไว้เดิม</summary>
public sealed record RelinkSwitch(Guid Id, string FromScope, string? FromDestination);

/// <summary>ผู้รับคำเชิญ 1 คน (1 LINE userId ต่อการสลับ) · <c>Link</c> = ลิงก์ตัวแทน (สมาชิกก่อน — หน้า "บัญชีของฉัน" ผูกครั้งเดียวใช้ได้ทุกการจอง/ออเดอร์) ·
/// <c>Channel</c> = line (ผ่าน OA เดิม) / email / none (ไม่มีทางติดต่อ ⇒ ข้าม)</summary>
public sealed record RelinkCandidate(string MessageKey, RelinkLinkFact Link, string Channel, string? Email);

/// <summary>ผลวางแผนคำเชิญของการสลับ 1 ครั้ง · <c>SkippedConsent</c> = ถอนความยินยอม/บล็อก OA (PDPA — ไม่เชิญ) · <c>SkippedInactive</c> = ไม่มีรายการที่ยังใช้งาน ·
/// <c>NoChannel</c> = ไม่มีช่องติดต่อ (OA เดิมส่งไม่ได้ + ไม่มีอีเมล)</summary>
public sealed record RelinkPlanResult(IReadOnlyList<RelinkCandidate> Invite, int SkippedConsent, int SkippedInactive, int NoChannel)
{
    public int Skipped => SkippedConsent + SkippedInactive + NoChannel;
}

/// <summary>
/// **ตัวตัดสินคำเชิญผูก LINE ใหม่เมื่อร้านสลับ OA — ตัวเดียว** (รอบ 212 แพ็ก B · คำตัดสินข้อ 171 ข้อ 4 · เจ้าของ: "ส่ง" ·
/// <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §2.4/§3.1/§5/§6)
/// <list type="bullet">
/// <item><see cref="IsSwitch"/>: OA ที่ "ส่งจริง" เปลี่ยน = scope เปลี่ยน (ร้าน↔NextAcc) หรือ scope เดิมแต่ bot (<c>destination</c>) ที่รู้ทั้งสองข้างไม่ตรงกัน ·
///   ไม่รู้ข้างใดข้างหนึ่ง ⇒ ไม่ใช่การสลับ (เช่นออก token ใหม่ของ OA เดิม — ตรงกับ <see cref="CustomerNotifyChannel.LinkUsable"/>)</item>
/// <item><see cref="Plan"/>: ลิงก์ที่ยังเปิดบน OA เดิม และใช้กับ OA ปัจจุบันไม่ได้ · ข้ามที่ถอนยินยอม/บล็อก OA · ต้องมีรายการที่ยังใช้งาน (สมาชิก active ·
///   การจองที่ยังไม่ถึง/ไม่ยกเลิก · ออเดอร์ยังไม่จบ) · <b>1 คน 1 คำเชิญ</b>ต่อการสลับ (รวมตาม LINE userId)</item>
/// <item><see cref="ChooseChannel"/>: OA เดิม = NextAcc และตอนนี้ไม่ได้ใช้ NextAcc ⇒ ยัง push ได้ (token กลางยังอยู่) ⇒ LINE (ถอยไปอีเมลเมื่อ push ล้ม) ·
///   OA เดิม = ของร้าน (token ถูกแทน/ปิด) หรือ bot ของ NextAcc เปลี่ยน ⇒ อีเมล · ไม่มีอีเมล ⇒ ข้าม</item>
/// <item>ฐาน PDPA: ความต่อเนื่องของบริการที่ลูกค้ายินยอมไว้ (<c>PdpaConsentRecord</c> ของลิงก์เดิม · ข้อความบริการ ไม่ใช่การตลาด) · ส่ง<b>ครั้งเดียว</b>ต่อการสลับ ·
///   มีประโยค "ไม่ต้องการรับ — ไม่ต้องทำอะไร" · ข้อความไม่มีอีเมล/เบอร์ของลูกค้า</item>
/// </list>
/// ส่งผ่าน <c>IBookingNotifier.SendAsync</c> (ประเภท <see cref="CustomerNotifyEvents.LineRelink"/> — ร้านปิดได้ที่ตารางค่าตั้ง · คีย์ <see cref="CustomerNotifyKeys.Relink"/>)
/// </summary>
public static class CustomerRelinkInvitePlan
{
    public const string RuleCode = "CMS-212-LINE-RELINK";
    public const string SourceConfigPut = "config-put";
    public const string SourceWebhook = "webhook-destination";
    public const string ChannelNone = "none";

    /// <summary>ส่งต่อบริษัทต่อรอบงาน (5 นาที) — ทยอยส่ง ไม่ยิงรัวถึง LINE/SMTP</summary>
    public const int BatchPerCompanyPerRun = 100;
    /// <summary>ส่งรวมทุกบริษัทต่อรอบงาน</summary>
    public const int MaxPerRun = 500;
    /// <summary>ส่งล้มกี่ครั้งแล้วเลิกลองคนนั้น (ครั้งละรอบงาน)</summary>
    public const int MaxAttempts = 3;
    /// <summary>หน่วงระหว่างข้อความ (ต่อบริษัท)</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(100);

    /// <summary>OA ที่ส่งจริงเปลี่ยนไหม — <paramref name="fromScope"/> ว่าง (ไม่รู้ค่าเดิม) ⇒ ไม่ใช่</summary>
    public static bool IsSwitch(string? fromScope, string? fromDestination, string? toScope, string? toDestination)
    {
        if (string.IsNullOrWhiteSpace(fromScope) || string.IsNullOrWhiteSpace(toScope)) return false;
        if (!string.Equals(fromScope, toScope, StringComparison.Ordinal)) return true;
        return !string.IsNullOrWhiteSpace(fromDestination) && !string.IsNullOrWhiteSpace(toDestination)
               && !string.Equals(fromDestination.Trim(), toDestination.Trim(), StringComparison.Ordinal);
    }

    /// <summary>คีย์กันบันทึกการสลับซ้ำ (คำขอซ้อน/webhook หลายครั้ง) · ≤ 200 ตัว</summary>
    public static string DedupeKey(string fromScope, string? fromDestination, string toScope, string? toDestination)
    {
        var k = $"{fromScope}:{(string.IsNullOrWhiteSpace(fromDestination) ? "-" : fromDestination.Trim())}>{toScope}:{(string.IsNullOrWhiteSpace(toDestination) ? "-" : toDestination.Trim())}";
        return k.Length > 200 ? k[..200] : k;
    }

    /// <summary>ช่องของคำเชิญ (ดูหัวคลาส)</summary>
    private static string ChooseChannel(string fromScope, string currentScope, bool nextAccCanPush, bool hasEmail)
    {
        var oldOaCanPush = fromScope == CustomerNotifyChannel.ScopeNextAcc && currentScope != CustomerNotifyChannel.ScopeNextAcc && nextAccCanPush;
        if (oldOaCanPush) return CustomerNotifyChannel.ChannelLine;
        return hasEmail ? CustomerNotifyChannel.ChannelEmail : ChannelNone;
    }

    /// <summary>เป้าหมายของลิงก์ยังมีเรื่องให้แจ้งไหม: สมาชิก active · การจองที่ยังไม่ผ่าน (วันนี้ขึ้นไป) และไม่ยกเลิก/ไม่มา/เสร็จ · ออเดอร์ที่ยังไม่จบ</summary>
    public static bool IsRelevant(string targetKind, bool? memberActive, BookingStatus? bookingStatus, DateTime? bookingDate, SiteOrderStatus? orderStatus, DateOnly todayBkk)
        => targetKind switch
        {
            CustomerLineLinkRule.KindMember => memberActive == true,
            CustomerLineLinkRule.KindBooking => bookingStatus is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.InProgress or BookingStatus.Waitlisted
                                                && bookingDate is DateTime d && DateOnly.FromDateTime(d) >= todayBkk,
            CustomerLineLinkRule.KindOrder => orderStatus is SiteOrderStatus.Pending or SiteOrderStatus.Confirmed or SiteOrderStatus.Processing
                                              or SiteOrderStatus.Shipped or SiteOrderStatus.ReadyForPickup,
            _ => false,
        };

    /// <summary>วางแผนผู้รับคำเชิญของการสลับ <paramref name="sw"/> เทียบกับ OA ปัจจุบัน (<paramref name="currentScope"/>/<paramref name="currentDestination"/>) ·
    /// ลำดับผลคงที่ (เรียงตามคีย์) ⇒ ทยอยส่งหลายรอบได้</summary>
    public static RelinkPlanResult Plan(RelinkSwitch sw, string currentScope, string? currentDestination, IEnumerable<RelinkLinkFact> links, bool nextAccCanPush)
    {
        // ลิงก์ที่ "เคยใช้ได้กับ OA เดิม" และ "ใช้กับ OA ปัจจุบันไม่ได้"
        var onOld = links
            .Where(l => !string.IsNullOrWhiteSpace(l.LineUserId)
                        && CustomerNotifyChannel.LinkUsable(l.OaScope, l.OaDestination, sw.FromScope, sw.FromDestination)
                        && !CustomerNotifyChannel.LinkUsable(l.OaScope, l.OaDestination, currentScope, currentDestination))
            .GroupBy(l => l.LineUserId.Trim(), StringComparer.Ordinal)
            .ToList();

        var invite = new List<RelinkCandidate>();
        int consent = 0, inactive = 0, noChannel = 0;
        foreach (var person in onOld)
        {
            // บล็อก OA = ไม่อยากรับข้อความ · ถอนความยินยอมแถวใดแถวหนึ่ง = ไม่เชิญคนนั้น (ด้านปลอดภัยของ PDPA)
            if (person.Any(l => l.BlockedAt != null || l.ConsentWithdrawn)) { consent++; continue; }
            var relevant = person.Where(l => l.TargetRelevant).ToList();
            if (relevant.Count == 0) { inactive++; continue; }
            var rep = relevant
                .OrderBy(l => l.TargetKind == CustomerLineLinkRule.KindMember ? 0 : 1)
                .ThenByDescending(l => l.LinkedAt)
                .ThenBy(l => l.LinkId)
                .First();
            var email = !string.IsNullOrWhiteSpace(rep.Email) ? rep.Email.Trim()
                : relevant.Concat(person).Select(l => l.Email).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))?.Trim();
            var channel = ChooseChannel(sw.FromScope, currentScope, nextAccCanPush, email != null);
            if (channel == ChannelNone) { noChannel++; continue; }
            invite.Add(new RelinkCandidate(CustomerNotifyKeys.Relink(sw.Id, person.Key), rep, channel, email));
        }
        return new RelinkPlanResult(invite.OrderBy(c => c.MessageKey, StringComparer.Ordinal).ToList(), consent, inactive, noChannel);
    }

    /// <summary>แถวบันทึกการส่งของคำเชิญจบแล้วไหม (ไม่ต้องลองอีก): ส่งถึง · ระงับโดยตั้งใจ (ร้านปิดประเภทนี้/โควตาเต็ม) · ล้มครบ <see cref="MaxAttempts"/> · พยายามเกินเพดาน</summary>
    public static bool IsTerminal(string? logStatus, int attempts)
        => logStatus == NotifyLogStatus.Sent || logStatus == NotifyLogStatus.Suppressed
           || (logStatus == NotifyLogStatus.Failed && attempts >= MaxAttempts)
           || attempts > MaxAttempts;   // ค้างสถานะ Claimed (เครื่องดับกลางคัน) ซ้ำ ๆ — มีเพดานเสมอ ไม่วนไม่จบ

    /// <summary>หัวเรื่องอีเมลของคำเชิญ</summary>
    public static string Subject(string? siteName)
        => $"เชิญเชื่อมต่อ LINE ใหม่เพื่อรับแจ้งเตือน — {(string.IsNullOrWhiteSpace(siteName) ? "ร้าน" : siteName.Trim())}";

    /// <summary>ข้อความคำเชิญ (text ล้วน — ช่องอีเมล HtmlEncode ทั้งก้อนที่ตัวส่ง) · <paramref name="relinkLine"/> จาก <see cref="CmsStorefrontLink.RelinkLine"/></summary>
    public static string Text(string? siteName, string relinkLine)
    {
        var site = string.IsNullOrWhiteSpace(siteName) ? "ร้าน" : siteName.Trim();
        return $"{site} เปลี่ยนบัญชี LINE ที่ใช้ส่งแจ้งเตือนการจอง/คำสั่งซื้อ\n"
             + "การเชื่อมต่อ LINE ที่คุณเคยตั้งไว้จึงใช้รับแจ้งเตือนต่อไม่ได้\n\n"
             + "ถ้าต้องการรับแจ้งเตือนทาง LINE ต่อ: กดปุ่ม \"รับแจ้งเตือนทาง LINE\" อีกครั้งที่\n"
             + relinkLine + "\n\n"
             + "ไม่ต้องการรับทาง LINE — ไม่ต้องทำอะไร (ร้านยังแจ้งทางอีเมลตามปกติ)\n"
             + "ข้อความนี้ส่งครั้งเดียวเพื่อแจ้งการเปลี่ยนแปลงบริการที่คุณยินยอมไว้";
    }

    /// <summary>ป้ายความคืบหน้าบนหน้าตั้งค่า LINE (เซิร์ฟเวอร์ประกอบ · F2 #5)</summary>
    public static string ProgressLabel(bool started, int total, int invited, int failed, int skipped, bool done)
    {
        if (!started) return "เชิญผูกใหม่: รอเริ่มส่ง (ระบบทยอยส่งรอบละ 5 นาที)";
        var s = $"เชิญผูกใหม่: ส่งแล้ว {invited}/{total}";
        if (failed > 0) s += $" · ส่งไม่สำเร็จ {failed}";
        if (skipped > 0) s += $" · ข้าม {skipped} (ถอนความยินยอม/บล็อก/ไม่มีรายการค้าง/ไม่มีช่องติดต่อ)";
        s += done ? " · เสร็จแล้ว" : " · กำลังทยอยส่ง (รอบละ 5 นาที)";
        return s;
    }
}
