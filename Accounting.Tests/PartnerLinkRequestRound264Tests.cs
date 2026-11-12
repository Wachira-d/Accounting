using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 — คำขอเชื่อมคู่ค้า NextAcc ถึงปลายทางจริง (คำตัดสิน 264-1-x) · pure (<see cref="PartnerLinkRequestRules"/> · <see cref="PartnerCompanyMatch.InviteTargets"/> ·
/// ค่าตั้งต้นการแจ้งของคีย์ใหม่) · คู่ Db: <c>Db/PartnerLinkRequestRound264DbTests</c>
/// </summary>
public class PartnerLinkRequestRound264Tests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);

    // ═══════════ ผู้รับคำขอ (ไม่ทิ้งเมื่อกำกวม) ═══════════

    [Fact]
    public void ผู้รับคำขอ_ตรงหนึ่งเดียว_ได้บริษัทนั้น_ไม่มี_ว่าง()
    {
        var a = Guid.NewGuid();
        Assert.Equal(new[] { a }, PartnerCompanyMatch.InviteTargets(new[] { new PartnerCompanyCandidate(a, "00000") }, null));
        Assert.Empty(PartnerCompanyMatch.InviteTargets(Array.Empty<PartnerCompanyCandidate>(), null));
        Assert.Empty(PartnerCompanyMatch.InviteTargets(null, "00000"));
        // ระบุสาขาที่ไม่มี ⇒ ไม่ถอยไปสำนักงานใหญ่ (สาขาผิด = คนละบริษัท)
        Assert.Empty(PartnerCompanyMatch.InviteTargets(new[] { new PartnerCompanyCandidate(a, "00000") }, "00002"));
    }

    [Fact]
    public void ผู้รับคำขอ_เลขซ้ำสาขาเดียวกัน_ส่งถึงทุกบริษัทที่ตรง_ไม่ทิ้ง_ทิศตรงข้ามสาขาอื่นไม่ได้()
    {
        var real = Guid.NewGuid(); var fake = Guid.NewGuid(); var br = Guid.NewGuid();
        var list = new[] { new PartnerCompanyCandidate(real, "00000"), new PartnerCompanyCandidate(fake, "0"), new PartnerCompanyCandidate(br, "00001") };
        // Pick (เส้นส่งเอกสาร) = กำกวม ⇒ ไม่ส่ง · InviteTargets (คำขอ) = ทั้งสองสำนักงานใหญ่ · สาขา 00001 ไม่ได้
        Assert.Equal(PartnerMatchKind.Ambiguous, PartnerCompanyMatch.Pick(list.Take(2), "00000").Kind);
        var t = PartnerCompanyMatch.InviteTargets(list, "00000");
        Assert.Equal(new[] { real, fake }.OrderBy(x => x), t);
        var noBranch = PartnerCompanyMatch.InviteTargets(list, null);
        Assert.Equal(new[] { real, fake }.OrderBy(x => x), noBranch);
        // ไม่ระบุสาขา + สำนักงานใหญ่หนึ่งเดียว ⇒ สำนักงานใหญ่เท่านั้น (พฤติกรรมเดิมของ Pick)
        Assert.Equal(new[] { real }, PartnerCompanyMatch.InviteTargets(new[] { list[0], list[2] }, null));
        // ไม่มีสำนักงานใหญ่ + หลายสาขา ⇒ ทุกสาขา
        var b2 = Guid.NewGuid();
        Assert.Equal(new[] { br, b2 }.OrderBy(x => x), PartnerCompanyMatch.InviteTargets(new[] { list[2], new PartnerCompanyCandidate(b2, "00002") }, null));
    }

    // ═══════════ หมดอายุ · ยอมรับ ═══════════

    [Fact]
    public void หมดอายุ30วัน_ยอมรับไม่ได้_ทิศตรงข้ามยังไม่ครบยอมรับได้_แถวเก่าใช้CreatedAt()
    {
        Assert.False(PartnerLinkRequestRules.IsExpired(TradingPartnershipStatus.Pending, T0, T0, T0.AddDays(30)));
        Assert.True(PartnerLinkRequestRules.IsExpired(TradingPartnershipStatus.Pending, T0, T0, T0.AddDays(30).AddMinutes(1)));
        Assert.True(PartnerLinkRequestRules.IsExpired(TradingPartnershipStatus.Pending, null, T0, T0.AddDays(31)));   // แถวก่อนรอบ 264
        Assert.False(PartnerLinkRequestRules.IsExpired(TradingPartnershipStatus.Pending, T0.AddDays(20), T0, T0.AddDays(31)));   // เปิดใหม่ = นับใหม่
        Assert.False(PartnerLinkRequestRules.IsExpired(TradingPartnershipStatus.Accepted, T0, T0, T0.AddDays(400)));   // คู่ที่เชื่อมแล้วไม่หมดอายุ

        Assert.Null(PartnerLinkRequestRules.AcceptProblem(TradingPartnershipStatus.Pending, T0, T0, T0.AddDays(1)));
        Assert.Equal(PartnerLinkRequestRules.ExpiredMessage, PartnerLinkRequestRules.AcceptProblem(TradingPartnershipStatus.Pending, T0, T0, T0.AddDays(31)));
        Assert.NotNull(PartnerLinkRequestRules.AcceptProblem(TradingPartnershipStatus.Rejected, T0, T0, T0.AddDays(1)));
        Assert.Contains("ส่งคำขอใหม่", PartnerLinkRequestRules.ExpiredMessage);   // ทางไปต่อ
    }

    [Fact]
    public void ป้ายและปุ่ม_รอเราและยังไม่หมดอายุเท่านั้นที่กดตอบได้()
    {
        Assert.True(PartnerLinkRequestRules.CanRespond(TradingPartnershipStatus.Pending, invitedByUs: false, expired: false));
        Assert.False(PartnerLinkRequestRules.CanRespond(TradingPartnershipStatus.Pending, invitedByUs: true, expired: false));
        Assert.False(PartnerLinkRequestRules.CanRespond(TradingPartnershipStatus.Pending, invitedByUs: false, expired: true));
        Assert.False(PartnerLinkRequestRules.CanRespond(TradingPartnershipStatus.Accepted, invitedByUs: false, expired: false));
        Assert.Equal("รอเรายอมรับ", PartnerLinkRequestRules.PartnershipLabel(TradingPartnershipStatus.Pending, false, false));
        Assert.Equal("คำขอหมดอายุ", PartnerLinkRequestRules.PartnershipLabel(TradingPartnershipStatus.Pending, false, true));
        Assert.Equal("เชื่อมแล้ว", PartnerLinkRequestRules.PartnershipLabel(TradingPartnershipStatus.Accepted, true, false));
    }

    // ═══════════ ฝั่งผู้ขอ — ไม่รั่วสมาชิกภาพ ═══════════

    [Fact]
    public void ผู้ขอเห็นแค่รอตอบ_เชื่อมแล้ว_หมดอายุ_ไม่มีสถานะปฏิเสธหรือไม่พบ()
    {
        var exp = T0.AddDays(30);
        Assert.Equal(PartnerLinkRequestRules.OutgoingState.Awaiting, PartnerLinkRequestRules.OutgoingStateOf(null, exp, T0.AddDays(1)));
        Assert.Equal(PartnerLinkRequestRules.OutgoingState.Accepted, PartnerLinkRequestRules.OutgoingStateOf(T0.AddDays(2), exp, T0.AddDays(40)));
        Assert.Equal(PartnerLinkRequestRules.OutgoingState.Expired, PartnerLinkRequestRules.OutgoingStateOf(null, exp, T0.AddDays(31)));
        Assert.Equal(new[] { "Awaiting", "Accepted", "Expired" }, Enum.GetNames<PartnerLinkRequestRules.OutgoingState>());
        foreach (var s in Enum.GetValues<PartnerLinkRequestRules.OutgoingState>())
        {
            var label = PartnerLinkRequestRules.OutgoingLabel(s);
            Assert.DoesNotContain("ปฏิเสธ", label);
            Assert.DoesNotContain("ไม่พบ", label);
        }
    }

    // ═══════════ ข้อความแจ้ง ═══════════

    [Fact]
    public void ข้อความแจ้งปลายทาง_เลขปิดบังและสาขา_ไม่มีชื่อหรือข้อความอิสระ_ลิงก์การ์ดยอมรับ()
    {
        // ฝ่ายค้าน M2/L6: ชื่อบริษัทผู้ขอ = ข้อความอิสระ ⇒ ไม่อยู่ในข้อความ (อีเมล/LINE) · เลขภาษีแบบปิดบัง
        var msg = PartnerLinkRequestRules.NoticeMessage("0105555555553", "00001", T0.AddDays(30));
        Assert.Contains("0-XXXX-XXXXX-XX-3", msg);
        Assert.DoesNotContain("0105555555553", msg);
        Assert.Contains("สาขาที่ 00001", msg);
        Assert.Contains("จนกว่าคุณจะยอมรับ", msg);
        Assert.Contains("เลขผู้เสียภาษี -", PartnerLinkRequestRules.NoticeMessage(null, null, T0));
        Assert.Contains("เลขผู้เสียภาษี - ", PartnerLinkRequestRules.NoticeMessage("12", null, T0));
        var cid = Guid.NewGuid(); var pid = Guid.NewGuid();
        var url = PartnerLinkRequestRules.AcceptUrl(cid, pid);
        Assert.Equal("/pages/partners.html?company=" + cid + "&request=" + pid, url);
        Assert.Equal(url, NotificationLink.InternalPath(url));   // ลิงก์ผ่านตัวตัดสินของกระดิ่ง
        // ข้อความตอบรับถึงผู้ขอ: เลขปิดบัง · บอกว่าไม่ได้ยืนยันเจ้าของเลข (L5) · ทางไปต่อ "ระงับการเชื่อม"
        var acc = PartnerLinkRequestRules.AcceptedNoticeMessage("0105555555553", "00001");
        Assert.Contains("0-XXXX-XXXXX-XX-3", acc);
        Assert.Contains("ไม่ได้ยืนยัน", acc);
        Assert.Contains("ระงับการเชื่อม", acc);
        Assert.Contains("ไม่ได้ยืนยัน", PartnerLinkRequestRules.NameUnverifiedLabel);
    }

    [Fact]
    public void กระดิ่ง_pathภายในเท่านั้น_กันตัวควบคุมและแบ็กสแลช()
    {
        Assert.Equal("/pages/a.html?x=1", NotificationLink.InternalPath(" /pages/a.html?x=1 "));
        Assert.Null(NotificationLink.InternalPath("//evil.example"));
        Assert.Null(NotificationLink.InternalPath("/\t/evil.example"));
        Assert.Null(NotificationLink.InternalPath("/\n/evil.example"));
        Assert.Null(NotificationLink.InternalPath("/a\\b"));
        Assert.Null(NotificationLink.InternalPath("https://evil.example"));
        Assert.Null(NotificationLink.InternalPath("javascript:alert(1)"));
        Assert.Null(NotificationLink.InternalPath(null));
    }

    [Fact]
    public void ปฏิเสธ_เฉพาะคำขอที่รอเรา_คู่ที่เชื่อมแล้วต้องระงับ_บล็อกผู้ขอ()
    {
        Assert.Null(PartnerLinkRequestRules.RejectProblem(TradingPartnershipStatus.Pending, invitedByUs: false));
        Assert.Equal(PartnerLinkRequestRules.RejectNotAllowedMessage, PartnerLinkRequestRules.RejectProblem(TradingPartnershipStatus.Pending, invitedByUs: true));
        Assert.NotNull(PartnerLinkRequestRules.RejectProblem(TradingPartnershipStatus.Accepted, invitedByUs: false));
        Assert.NotNull(PartnerLinkRequestRules.RejectProblem(TradingPartnershipStatus.Suspended, invitedByUs: false));
        Assert.Contains("ระงับการเชื่อม", PartnerLinkRequestRules.RejectNotAllowedMessage);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(PartnerLinkRequestRules.BlockedForRequester(b, a));
        Assert.False(PartnerLinkRequestRules.BlockedForRequester(a, a));   // ผู้บล็อกส่งคำขอเอง = ปลดบล็อก
        Assert.False(PartnerLinkRequestRules.BlockedForRequester(null, a));
    }

    [Fact]
    public void เกินเพดาน_เลื่อนไปเมื่อหน้าต่างว่าง_ไม่เงียบถาวร()
    {
        Assert.Equal(T0.AddHours(23).AddMinutes(1), PartnerLinkRequestRules.CapRetryAt(T0.AddHours(-1), T0));
        Assert.Equal(T0.AddHours(1), PartnerLinkRequestRules.CapRetryAt(null, T0));
        Assert.Equal(T0.AddMinutes(1), PartnerLinkRequestRules.CapRetryAt(T0.AddHours(-30), T0));   // ไม่ย้อนอดีต
    }

    // ═══════════ งานเบื้องหลัง ═══════════

    [Fact]
    public void งานแจ้ง_ส่ง_ข้ามเมื่อหมดอายุหรือครบเพดาน_เลิกเฉพาะเมื่อเคยล้มนาน()
    {
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.Send, PartnerLinkRequestRules.Decide(false, T0, T0.AddMinutes(5), 0, false));
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.SkipExpired, PartnerLinkRequestRules.Decide(true, T0, T0, 0, false));
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.SkipCapped,
            PartnerLinkRequestRules.Decide(false, T0, T0, PartnerLinkRequestRules.NoticesPerTargetPerDay, false));
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.Send,
            PartnerLinkRequestRules.Decide(false, T0, T0, PartnerLinkRequestRules.NoticesPerTargetPerDay - 1, false));
        // งานหยุดไปหลายวันโดยไม่เคยลอง ⇒ ยังส่ง (ทิศตรงข้ามของ GiveUp)
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.Send, PartnerLinkRequestRules.Decide(false, T0, T0.AddDays(5), 0, previouslyFailed: false));
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.GiveUp, PartnerLinkRequestRules.Decide(false, T0, T0.AddDays(3), 0, previouslyFailed: true));
        Assert.Equal(PartnerLinkRequestRules.NoticeAction.Send, PartnerLinkRequestRules.Decide(false, T0, T0.AddHours(2), 0, previouslyFailed: true));
    }

    [Fact]
    public void คีย์แจ้งคำขอเชื่อม_ค่าตั้งต้น_กระดิ่งอีเมลLINE_ถึงเจ้าของและผู้ดูแล_ไม่เข้ากลุ่ม()
    {
        var rows = NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.PartnerLinkRequested, companyLineGroupConfigured: true);
        Assert.Equal(new[] { "Owner", NotificationRecipientRoles.CompanyAdmin }, rows.Select(r => r.RecipientRole));
        Assert.All(rows, r => { Assert.True(r.EnableSystem); Assert.True(r.EnableEmail); Assert.True(r.EnableLine); Assert.False(r.EnableLineGroup); });
        Assert.Equal(NotificationAudience.TenantBusiness, NotificationAudiencePolicy.Classify(NotificationEvents.PartnerLinkRequested));
        Assert.Contains(NotificationRecipientRoles.CompanyAdmin, NotificationRecipientRoles.All);
        Assert.True(NotificationRecipientRoles.Labels.ContainsKey(NotificationRecipientRoles.CompanyAdmin));
        // ฝ่ายค้าน L4: คีย์ตอบรับของตัวเอง — ค่าตั้งต้นเดียวกัน ไม่เข้ากลุ่ม LINE
        var acc = NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.PartnerLinkAccepted, companyLineGroupConfigured: true);
        Assert.Equal(rows.Select(r => r.RecipientRole), acc.Select(r => r.RecipientRole));
        Assert.All(acc, r => Assert.False(r.EnableLineGroup));
        Assert.Contains(NotificationEvents.All, c => c.Key == NotificationEvents.PartnerLinkAccepted);
        // ทิศตรงข้าม: คีย์อื่นไม่ถูกแตะ (ค่าตั้งต้นของคีย์ที่ไม่รู้จัก = ว่าง)
        Assert.Empty(NotificationAudiencePolicy.DefaultMatrix("partner.unknown_key", true));
    }
}
