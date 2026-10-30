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
public sealed record RelinkPlanResult(IReadOnlyList<RelinkCandidate> Invite, int SkippedConsent, int SkippedInactive, int NoChannel, int SkippedCooldown = 0)
{
    public int Skipped => SkippedConsent + SkippedInactive + NoChannel + SkippedCooldown;
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

    /// <summary>ฝ่ายค้าน (B+D) ข้อ 1: รอให้ค่าตั้ง "นิ่ง" ก่อนเชิญ — ปิดแล้วเปิด LINE / ลองค่าตั้งไปมาภายในช่วงนี้ ⇒ ตอนประมวลไม่ใช่การสลับแล้ว ⇒ ยกเลิก (ไม่ส่ง)</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(45);
    /// <summary>ฝ่ายค้าน (B+D) ข้อ 1: 1 คนได้คำเชิญไม่เกิน 1 ฉบับต่อช่วงนี้ (ข้ามทุกการสลับของบริษัท) — สลับไปมาหลายรอบไม่เชิญซ้ำ</summary>
    public const int CooldownDays = 30;
    /// <summary>ป้ายบนแถวที่ยกเลิกเพราะตอนประมวล OA ที่ใช้คือ OA เดิมแล้ว</summary>
    public const string CancelledFlipBackNote = "ยกเลิก: สลับกลับแล้ว (OA ที่ใช้ตอนนี้คือ OA เดิม — ลิงก์ของลูกค้ายังใช้ได้ ไม่ต้องเชิญ)";
    /// <summary>ฝ่ายค้าน (B+D) ข้อ 2: webhook ที่ลายเซ็นผ่านมาจากบอทอื่นที่ไม่ใช่บอทของ token (secret ยังเป็นของ OA เดิม)</summary>
    public const string SecretOfOtherOaProblem = "Channel Secret ที่บันทึกไว้เป็นของ LINE OA คนละตัวกับ Channel Access Token (webhook ที่เข้ามาเป็นของบอทอื่น) — ใส่ Channel Secret ของ OA เดียวกับ token แล้วกดบันทึก · ระหว่างนี้ระบบไม่เปลี่ยนบอทที่จำไว้และไม่เชิญลูกค้าผูกใหม่";

    /// <summary>ฝ่ายค้าน (B+D) ข้อ 2: webhook (ลายเซ็นผ่าน) มาจากบอทอื่นที่ไม่ใช่บอทที่จำไว้ — ทั้งสองค่าต้องรู้ · ยังไม่จำ ⇒ false (ให้จำ)</summary>
    public static bool IsForeignDestination(string? stampedDestination, string? receivedDestination)
        => !string.IsNullOrWhiteSpace(stampedDestination) && !string.IsNullOrWhiteSpace(receivedDestination)
           && !string.Equals(stampedDestination.Trim(), receivedDestination.Trim(), StringComparison.Ordinal);

    /// <summary>ฝ่ายค้าน (B+D) ข้อ 1: แถวการสลับ 2 แถวที่ "กลับทิศกัน" (X→Y แล้ว Y→X) ห่างกันไม่เกิน <see cref="SettleDelay"/> = ลอง/ปิดเปิดค่าตั้งไปมา ⇒ ยกเลิกทั้งคู่ ·
    /// <paramref name="otherDedupeKey"/> ต้องเป็นคีย์กลับทิศของแถวนี้พอดี</summary>
    public static bool IsFlipPair(string? fromScope, string? fromDestination, string? toScope, string? toDestination, DateTime detectedAtUtc,
        string? otherDedupeKey, DateTime otherDetectedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(fromScope) || string.IsNullOrWhiteSpace(toScope) || otherDedupeKey == null) return false;
        var reverse = DedupeKey(toScope, toDestination, fromScope, fromDestination);
        return string.Equals(reverse, otherDedupeKey, StringComparison.Ordinal)
               && (detectedAtUtc - otherDetectedAtUtc).Duration() <= SettleDelay;
    }

    /// <summary>ถึงเวลาประมวลแถวการสลับหรือยัง (ผ่าน <see cref="SettleDelay"/> นับจากที่ตรวจพบ)</summary>
    public static bool IsSettled(DateTime detectedAtUtc, DateTime nowUtc) => detectedAtUtc <= nowUtc - SettleDelay;

    /// <summary>bot ของ OA ปัจจุบันที่ใช้เทียบ: รู้ ⇒ ใช้ค่านั้น · ไม่รู้แต่ scope ตรงกับปลายทางของแถว ⇒ ใช้ปลายทางที่แถวบันทึกไว้ (webhook เห็นแล้ว · เช่น OA กลางที่ยังไม่ได้ตั้ง Bot User ID)</summary>
    public static string? EffectiveCurrentDestination(string currentScope, string? currentDestination, string? eventToScope, string? eventToDestination)
        => !string.IsNullOrWhiteSpace(currentDestination) ? currentDestination
            : string.Equals(currentScope, eventToScope, StringComparison.Ordinal) ? eventToDestination : null;

    /// <summary>คนที่ได้คำเชิญ (ส่งถึง) จากการสลับ<b>อื่น</b>ภายใน <see cref="CooldownDays"/> วัน ⇒ ไม่เชิญซ้ำ (นับเป็นข้าม) · คีย์ของการสลับนี้เองไม่นับ (ทยอยส่งข้ามรอบได้)</summary>
    public static RelinkPlanResult WithCooldown(RelinkPlanResult plan, IEnumerable<string> recentlySentRelinkKeys)
    {
        var recent = recentlySentRelinkKeys.ToHashSet(StringComparer.Ordinal);
        var people = recent.Select(PersonOf).ToHashSet(StringComparer.Ordinal);
        var kept = new List<RelinkCandidate>(plan.Invite.Count);
        var cooled = 0;
        foreach (var c in plan.Invite)
        {
            if (!recent.Contains(c.MessageKey) && people.Contains(PersonOf(c.MessageKey))) { cooled++; continue; }
            kept.Add(c);
        }
        return plan with { Invite = kept, SkippedCooldown = plan.SkippedCooldown + cooled };
    }

    /// <summary>ส่วน "คน" ของคีย์ <see cref="CustomerNotifyKeys.Relink"/> (hash ของ userId — ท้ายสุดหลัง ':')</summary>
    private static string PersonOf(string messageKey)
    {
        var i = messageKey.LastIndexOf(':');
        return i < 0 ? messageKey : messageKey[(i + 1)..];
    }

    /// <summary>ฝ่ายค้าน (B+D) ข้อ 2: เปลี่ยน token โดยไม่ใส่ secret ใหม่ทั้งที่มี secret เดิม ⇒ ต้องยืนยันว่า token ใหม่เป็นบอทเดิม (ถาม LINE ได้ + ตรงกับบอทที่จำไว้) ·
    /// ไม่งั้นปฏิเสธ (secret เดิมของ OA ก่อนหน้าจะทำให้ webhook ของ OA เก่าผ่านลายเซ็นแล้วพลิกบอทกลับ) · null = ผ่าน</summary>
    public static string? TokenWithoutSecretProblem(bool tokenChanged, bool secretChanged, bool hasExistingSecret, string? fetchedBotUserId, string? stampedBotUserId)
    {
        if (!tokenChanged || secretChanged || !hasExistingSecret) return null;
        if (!string.IsNullOrWhiteSpace(fetchedBotUserId) && string.Equals(fetchedBotUserId, stampedBotUserId, StringComparison.Ordinal)) return null;
        return "เปลี่ยน Channel Access Token แล้ว ต้องใส่ Channel Secret ของ OA เดียวกันด้วย — ยืนยันไม่ได้ว่า token ใหม่เป็นบอทเดิม (secret เดิมอาจเป็นของ OA อื่น ⇒ webhook ใช้ไม่ได้/ลูกค้าถูกเชิญผูกใหม่ผิด ๆ)";
    }

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
