using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 245 L1 (LINE_MULTICHANNEL_DESIGN §8 L1 · คำตอบเจ้าของ §10) — ตัวตัดสินเดียว <see cref="ContactRecipientRule"/> + <see cref="NotifyTopics"/>:
/// (ก) ไม่มีแถว ⇒ <c>Contact.Email</c> ทุกเรื่องเหมือนเดิม (ทิศตรงข้าม: ไม่ถดถอย) · (ข) เรื่องที่ไม่ได้เลือกไม่ได้รับ · (ค) ยกเลิก/ถอนความยินยอม ⇒ ไม่ได้ ·
/// (ง) legacy OA ไม่รู้ ⇒ ไม่ได้ · (จ) บริษัทอื่น ⇒ ไม่ได้ · กลุ่มกลางของระบบ (Q1(b)) · เพดาน 200 คน (Q2) · ความยินยอมบังคับ (PDPA) · L1 LINE ยังไม่เปิดส่ง
/// </summary>
public class ContactRecipientRuleTests
{
    private static readonly Guid Co = Guid.NewGuid();
    private static readonly Guid OtherCo = Guid.NewGuid();
    private static readonly Guid ContactId = Guid.NewGuid();
    private const string PlatformGroup = "Cplatform00000000000000000000000001";
    /// <summary>ฝ่ายค้าน S2: กลุ่มกลางจากหน้าแอดมิน (PlatformGroup) + config Line:GroupId (ConfigGroup) แยกกัน</summary>
    private const string ConfigGroup = "Cconfig0000000000000000000000000002";
    private static readonly string[] Platform = NextAccLineConfigRule.PlatformGroupIds(PlatformGroup, ConfigGroup).ToArray();

    private static ContactRecipientRule.ContactFacts Contact(string? email = "ap@abc.co.th", Guid? company = null, bool deleted = false)
        => new(ContactId, company ?? Co, email, deleted);

    private static ContactRecipientRule.EndpointFacts Row(string kind, string? email = null, IEnumerable<NotifyTopic>? topics = null,
        string oa = "", bool primary = false, bool verified = false, Guid? consent = null, DateTime? withdrawn = null,
        DateTime? unlinked = null, DateTime? blocked = null, string? groupId = null, int? members = null, Guid? company = null, Guid? contact = null,
        string? topicsJson = null, bool showAmounts = true, DateTime? linked = null, Guid? id = null)
        => new(id ?? Guid.NewGuid(), company ?? Co, contact ?? ContactId, kind, null, email, groupId, oa, null, members,
            topicsJson ?? NotifyTopics.Serialize(topics ?? NotifyTopics.DefaultsFor(kind)), showAmounts, primary, verified,
            NotifyEndpointKind.SourceManual, consent, withdrawn, blocked, unlinked, unlinked == null ? null : NotifyEndpointKind.UnlinkCompany, false,
            linked ?? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

    private static ContactRecipientRule.EndpointFacts UsableGroup(string groupId = "Cgroup000000000000000000000000000002", int members = 12)
        => Row(NotifyEndpointKind.LineGroup, oa: NotifyEndpointKind.ScopeNextAcc, verified: true, consent: Guid.NewGuid(), groupId: groupId, members: members);

    private static ContactRecipientRule.RecipientSet Resolve(ContactRecipientRule.ContactFacts? c, IEnumerable<ContactRecipientRule.EndpointFacts> rows,
        NotifyTopic topic, bool lineOpen = false)
        => ContactRecipientRule.Resolve(Co, c, rows, topic, Platform, lineOpen);

    // ───── (ก) ไม่ถดถอย ─────

    [Theory]
    [InlineData(NotifyTopic.Document)]
    [InlineData(NotifyTopic.Dunning)]
    [InlineData(NotifyTopic.Receipt)]
    [InlineData(NotifyTopic.Wht)]
    [InlineData(NotifyTopic.Sign)]
    [InlineData(NotifyTopic.Other)]
    public void ก_ไม่มีแถวปลายทาง_ได้อีเมลหลักทุกเรื่องเหมือนเดิม(NotifyTopic topic)
    {
        var set = Resolve(Contact("AP@abc.co.th"), Array.Empty<ContactRecipientRule.EndpointFacts>(), topic);
        Assert.Equal(new[] { "ap@abc.co.th" }, set.Emails);
        Assert.True(Assert.Single(set.Options).IsPrimary);
    }

    [Fact]
    public void ก_อีเมลหลักหลายที่อยู่ในช่องเดียว_ได้ครบเหมือนตัวแยกฝั่งส่ง()
    {
        var set = Resolve(Contact("a@x.co.th; b@x.co.th"), Array.Empty<ContactRecipientRule.EndpointFacts>(), NotifyTopic.Document);
        Assert.Equal(new[] { "a@x.co.th", "b@x.co.th" }, set.Emails);
    }

    [Fact]
    public void ก_ไม่มีอีเมลหลักและไม่มีแถว_ชุดว่างไม่แต่งผู้รับขึ้นมา()
    {
        var set = Resolve(Contact(null), Array.Empty<ContactRecipientRule.EndpointFacts>(), NotifyTopic.Document);
        Assert.Empty(set.Emails);
        Assert.Empty(set.Options);
    }

    // ───── (ข) เรื่องที่เลือก ─────

    [Fact]
    public void ข_อีเมลที่สองรับเฉพาะทวงชำระ_ไม่ได้ใบเสร็จ_แต่ได้ทวงชำระ()
    {
        var rows = new[] { Row(NotifyEndpointKind.Email, "bill@abc.co.th", new[] { NotifyTopic.Dunning }) };
        var receipt = Resolve(Contact(), rows, NotifyTopic.Receipt);
        Assert.Equal(new[] { "ap@abc.co.th" }, receipt.Emails);
        Assert.Contains(receipt.Options, o => o.Display == "bill@abc.co.th" && !o.Available && o.Reason!.Contains("ไม่ได้เลือกรับเรื่อง"));
        var dunning = Resolve(Contact(), rows, NotifyTopic.Dunning);
        Assert.Equal(new[] { "ap@abc.co.th", "bill@abc.co.th" }, dunning.Emails);
    }

    [Fact]
    public void ข_อีเมลหลักปิดทุกเรื่อง_อีเมลอื่นยังได้_และอีเมลหลักบอกเหตุ()
    {
        var rows = new[]
        {
            Row(NotifyEndpointKind.Email, primary: true, topics: Array.Empty<NotifyTopic>()),
            Row(NotifyEndpointKind.Email, "acc@abc.co.th"),
        };
        var set = Resolve(Contact(), rows, NotifyTopic.Document);
        Assert.Equal(new[] { "acc@abc.co.th" }, set.Emails);
        Assert.Contains(set.Options, o => o.IsPrimary && !o.Available);
    }

    [Fact]
    public void ข_ค่าตั้งเรื่องเสียหาย_ไม่เดาว่ารับทุกเรื่อง()
    {
        var rows = new[] { Row(NotifyEndpointKind.Email, "acc@abc.co.th", topicsJson: "{not json") };
        var set = Resolve(Contact(null), rows, NotifyTopic.Document);
        Assert.Empty(set.Emails);
        Assert.Contains("เสียหาย", Assert.Single(set.Options).Reason);
        Assert.Null(NotifyTopics.Parse("[\"Document\",\"Nope\"]"));
        Assert.Null(NotifyTopics.Parse("[\"1\"]"));
    }

    // ───── (ค) ยกเลิก / ถอนความยินยอม ─────

    [Fact]
    public void ค_แถวที่ยกเลิกแล้ว_ไม่ได้รับ()
    {
        var rows = new[] { Row(NotifyEndpointKind.Email, "old@abc.co.th", unlinked: DateTime.UtcNow) };
        var set = Resolve(Contact(), rows, NotifyTopic.Document);
        Assert.DoesNotContain("old@abc.co.th", set.Emails);
        Assert.Equal("unlinked", ContactRecipientRule.Status(rows[0], Platform, true).Code);
    }

    [Fact]
    public void ค_LINE_ถอนความยินยอม_ไม่ได้แม้เปิดส่งLINEแล้ว()
    {
        var withdrawn = Row(NotifyEndpointKind.LineUser, oa: NotifyEndpointKind.ScopeNextAcc, consent: Guid.NewGuid(), withdrawn: DateTime.UtcNow);
        var st = ContactRecipientRule.Status(withdrawn, Platform, lineDeliveryOpen: true);
        Assert.False(st.Usable);
        Assert.Equal("consent-withdrawn", st.Code);
        Assert.Equal(ContactRecipientRule.RuleCodeConsent, st.RuleCode);
        Assert.Empty(Resolve(Contact(), new[] { withdrawn }, NotifyTopic.Document, lineOpen: true).LineUsers);
    }

    [Fact]
    public void ความยินยอมบังคับ_LINEไม่มีหลักฐาน_ไม่ได้_ทิศตรงข้ามมีหลักฐาน_ได้()
    {
        var noConsent = Row(NotifyEndpointKind.LineUser, oa: NotifyEndpointKind.ScopeNextAcc);
        Assert.Equal("no-consent", ContactRecipientRule.Status(noConsent, Platform, true).Code);
        var withConsent = Row(NotifyEndpointKind.LineUser, oa: NotifyEndpointKind.ScopeNextAcc, consent: Guid.NewGuid());
        var st = ContactRecipientRule.Status(withConsent, Platform, true);
        Assert.True(st.Usable);
        Assert.Single(Resolve(Contact(), new[] { withConsent }, NotifyTopic.Document, lineOpen: true).LineUsers);
        Assert.Contains("ม.24(3)", ContactRecipientRule.ConsentLabel(Row(NotifyEndpointKind.Email, "a@b.co")));
    }

    [Fact]
    public void ผู้รับบล็อกOA_ไม่ได้()
    {
        var blocked = Row(NotifyEndpointKind.LineUser, oa: NotifyEndpointKind.ScopeCompany, consent: Guid.NewGuid(), blocked: DateTime.UtcNow);
        Assert.Equal("blocked", ContactRecipientRule.Status(blocked, Platform, true).Code);
    }

    // ───── (ง) legacy ─────

    [Fact]
    public void ง_LINEเดิมที่ไม่รู้OA_ไม่ใช้ส่งแม้มีความยินยอมและเปิดส่งแล้ว()
    {
        var legacy = Row(NotifyEndpointKind.LineUser, oa: NotifyEndpointKind.ScopeUnknown, consent: Guid.NewGuid());
        var st = ContactRecipientRule.Status(legacy, Platform, lineDeliveryOpen: true);
        Assert.False(st.Usable);
        Assert.Equal("legacy", st.Code);
        var set = Resolve(Contact(), new[] { legacy }, NotifyTopic.Document, lineOpen: true);
        Assert.Empty(set.LineUsers);
        Assert.Equal(new[] { "ap@abc.co.th" }, set.Emails);
    }

    // ───── (จ) tenant ─────

    [Fact]
    public void จ_ผู้ติดต่อของบริษัทอื่น_ชุดว่าง()
    {
        var rows = new[] { Row(NotifyEndpointKind.Email, "x@abc.co.th", company: OtherCo) };
        var set = Resolve(Contact(company: OtherCo), rows, NotifyTopic.Document);
        Assert.Empty(set.Options);
    }

    [Fact]
    public void จ_แถวของบริษัทอื่นหรือผู้ติดต่ออื่น_ไม่ถูกนับ_ผู้ติดต่อที่ลบ_ชุดว่าง()
    {
        var rows = new[]
        {
            Row(NotifyEndpointKind.Email, "foreign@other.co.th", company: OtherCo),
            Row(NotifyEndpointKind.Email, "someone@else.co.th", contact: Guid.NewGuid()),
        };
        var set = Resolve(Contact(), rows, NotifyTopic.Document);
        Assert.Equal(new[] { "ap@abc.co.th" }, set.Emails);
        Assert.Single(set.Options);
        Assert.Empty(Resolve(Contact(deleted: true), rows, NotifyTopic.Document).Options);
        Assert.Empty(Resolve(null, rows, NotifyTopic.Document).Options);
    }

    // ───── คำตัดสินเจ้าของ Q1(b): กลุ่มกลางของระบบ ─────

    [Fact]
    public void Q1b_กลุ่มกลางของระบบ_ห้ามเป็นปลายทาง_แม้ยืนยันแล้วมีความยินยอมและเปิดส่งLINE()
    {
        var central = UsableGroup(groupId: PlatformGroup);
        var st = ContactRecipientRule.Status(central, Platform, lineDeliveryOpen: true);
        Assert.False(st.Usable);
        Assert.Equal("platform-group", st.Code);
        Assert.Equal(ContactRecipientRule.RuleCodePlatformGroup, st.RuleCode);
        Assert.Empty(Resolve(Contact(), new[] { central }, NotifyTopic.Document, lineOpen: true).LineGroups);
        // ช่องว่างรอบค่าไม่ช่วยหลบด่าน
        Assert.True(ContactRecipientRule.IsPlatformCentralGroup("  " + PlatformGroup + " ", Platform));
        Assert.NotNull(ContactRecipientRule.GroupProblem(PlatformGroup, 3, Platform));
    }

    [Fact]
    public void S2_กลุ่มกลางจากconfigอย่างเดียว_ก็ถูกกันด้วย_ไม่ใช่เฉพาะค่าหน้าแอดมิน()
    {
        // admin ไม่ตั้ง · config ตั้ง ⇒ ยังกัน
        var onlyConfig = NextAccLineConfigRule.PlatformGroupIds(null, ConfigGroup);
        Assert.Equal(new[] { ConfigGroup }, onlyConfig);
        Assert.Equal("platform-group", ContactRecipientRule.Status(UsableGroup(groupId: ConfigGroup), onlyConfig.ToArray(), true).Code);
        // admin ตั้ง · config ตั้งคนละค่า ⇒ กันทั้งคู่ (Merge เลือกแหล่งเดียว — ห้ามใช้)
        Assert.Equal("platform-group", ContactRecipientRule.Status(UsableGroup(groupId: ConfigGroup), Platform, true).Code);
        Assert.Equal("platform-group", ContactRecipientRule.Status(UsableGroup(groupId: PlatformGroup), Platform, true).Code);
        // ว่าง/ช่องว่าง/ซ้ำ ⇒ ตัดออก
        Assert.Equal(new[] { PlatformGroup }, NextAccLineConfigRule.PlatformGroupIds(" " + PlatformGroup + " ", PlatformGroup));
        Assert.Empty(NextAccLineConfigRule.PlatformGroupIds("  ", null));
    }

    [Fact]
    public void S2_groupIdถอดรหัสไม่ออกหรือว่าง_ใช้ไม่ได้_ไม่ผ่านด่าน()
    {
        foreach (var unreadable in new[] { "", "  ", null })
        {
            var st = ContactRecipientRule.Status(UsableGroup(groupId: unreadable!), Platform, lineDeliveryOpen: true);
            Assert.False(st.Usable);
            Assert.Equal("group-unreadable", st.Code);
            Assert.Equal(ContactRecipientRule.RuleCodeGroupUnreadable, st.RuleCode);
        }
    }

    [Fact]
    public void Q1b_ทิศตรงข้าม_กลุ่มของผู้ติดต่อที่ไม่ใช่กลุ่มกลาง_ใช้ได้เมื่อเปิดส่ง()
    {
        var g = UsableGroup();
        Assert.True(ContactRecipientRule.Status(g, Platform, lineDeliveryOpen: true).Usable);
        Assert.Single(Resolve(Contact(), new[] { g }, NotifyTopic.Document, lineOpen: true).LineGroups);
        // แอดมินยังไม่ตั้งกลุ่มกลาง ⇒ ไม่ฟ้องผิดว่าทุกกลุ่มเป็นกลุ่มกลาง
        Assert.False(ContactRecipientRule.IsPlatformCentralGroup(g.LineGroupIdPlain, Array.Empty<string?>()));
        Assert.False(ContactRecipientRule.IsPlatformCentralGroup(null, Platform));
    }

    // ───── คำตัดสินเจ้าของ Q2: ≤ 200 คน ─────

    [Fact]
    public void Q2_กลุ่มเกิน200คน_ใช้ไม่ได้_200คนพอดี_ใช้ได้()
    {
        var over = UsableGroup(members: ContactRecipientRule.MaxGroupMembers + 1);
        var st = ContactRecipientRule.Status(over, Platform, true);
        Assert.Equal("group-cap", st.Code);
        Assert.Equal(ContactRecipientRule.RuleCodeGroupCap, st.RuleCode);
        Assert.True(ContactRecipientRule.Status(UsableGroup(members: 200), Platform, true).Usable);
        Assert.Equal(200, ContactRecipientRule.MaxGroupMembers);
    }

    [Fact]
    public void Q5_กลุ่มปิดแสดงยอด_ส่งค่าต่อถึงผู้ส่ง_ชนิดอื่นแสดงยอดเสมอ()
    {
        var hidden = UsableGroup() with { ShowAmounts = false };
        var set = Resolve(Contact(), new[] { hidden }, NotifyTopic.Document, lineOpen: true);
        Assert.False(Assert.Single(set.LineGroups).ShowAmounts);
        var email = Row(NotifyEndpointKind.Email, "a@abc.co.th", showAmounts: false);
        Assert.True(Resolve(Contact(null), new[] { email }, NotifyTopic.Document).Options.Single().ShowAmounts);
    }

    [Fact]
    public void L1_ยังไม่เปิดส่งLINE_ปลายทางLINEที่พร้อมทุกอย่าง_แสดงพร้อมเหตุแต่ไม่อยู่ในชุดผู้รับ()
    {
        var g = UsableGroup();
        var st = ContactRecipientRule.Status(g, Platform, lineDeliveryOpen: false);
        Assert.Equal("line-not-open", st.Code);
        var set = Resolve(Contact(), new[] { g }, NotifyTopic.Document, lineOpen: false);
        Assert.Empty(set.LineGroups);
        Assert.Contains(set.Options, o => o.Kind == NotifyEndpointKind.LineGroup && !o.Available && o.Reason != null);
    }

    [Fact]
    public void กลุ่มยังไม่ยืนยันด้วยรหัส_ใช้ไม่ได้()
    {
        var g = UsableGroup() with { Verified = false };
        Assert.Equal("unverified", ContactRecipientRule.Status(g, Platform, true).Code);
    }

    // ───── ตรวจค่าที่เขียน ─────

    [Fact]
    public void ValidateNewEmail_ซ้ำอีเมลหลัก_ซ้ำแถวเดิม_ผิดรูป_เกินเพดาน_และทิศตรงข้ามผ่าน()
    {
        Assert.Equal(409, ContactRecipientRule.ValidateNewEmail("AP@abc.co.th", "ap@abc.co.th", Array.Empty<string?>()).Status);
        Assert.Equal(409, ContactRecipientRule.ValidateNewEmail("b@abc.co.th", null, new string?[] { "B@abc.co.th" }).Status);
        Assert.Equal(400, ContactRecipientRule.ValidateNewEmail("not-an-email", null, Array.Empty<string?>()).Status);
        Assert.Equal(400, ContactRecipientRule.ValidateNewEmail("a@x.co.th, b@x.co.th", null, Array.Empty<string?>()).Status);
        var ten = Enumerable.Range(0, ContactRecipientRule.MaxEmailsPerContact).Select(i => (string?)$"u{i}@x.co.th").ToList();
        Assert.Equal(400, ContactRecipientRule.ValidateNewEmail("new@x.co.th", null, ten).Status);
        var ok = ContactRecipientRule.ValidateNewEmail(" New@X.co.th ", "ap@abc.co.th", ten.Take(3));
        Assert.Null(ok.Problem);
        Assert.Equal("new@x.co.th", ok.Email);
    }

    [Fact]
    public void FromRequest_ชื่อไม่รู้จักหรือตัวเลข_ปฏิเสธ_ว่างได้เฉพาะเมื่ออนุญาต()
    {
        Assert.NotNull(NotifyTopics.FromRequest(new[] { "Document", "Payslip" }, allowEmpty: false).Problem);
        Assert.NotNull(NotifyTopics.FromRequest(new[] { "2" }, allowEmpty: false).Problem);
        Assert.NotNull(NotifyTopics.FromRequest(Array.Empty<string>(), allowEmpty: false).Problem);
        Assert.Empty(NotifyTopics.FromRequest(Array.Empty<string>(), allowEmpty: true).Topics!);
        var (topics, problem) = NotifyTopics.FromRequest(new[] { "dunning", "Receipt", "Dunning" }, allowEmpty: false);
        Assert.Null(problem);
        Assert.Equal(new[] { NotifyTopic.Dunning, NotifyTopic.Receipt }, topics);
        Assert.Equal("[\"Dunning\",\"Receipt\"]", NotifyTopics.Serialize(topics!.Reverse()));
    }

    [Fact]
    public void ค่าตั้งต้นต่อชนิด_และป้ายยาวเกินถูกปฏิเสธ_ไม่ตัดเงียบ()
    {
        Assert.Equal(NotifyTopics.All, NotifyTopics.DefaultsFor(NotifyEndpointKind.Email));
        Assert.Equal(new[] { NotifyTopic.Document, NotifyTopic.Dunning }, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineGroup));
        Assert.DoesNotContain(NotifyTopic.Wht, NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser));
        Assert.NotNull(ContactRecipientRule.NormalizeLabel(new string('ก', ContactRecipientRule.MaxLabelLength + 1)).Problem);
        var blank = ContactRecipientRule.NormalizeLabel("   ");
        Assert.Null(blank.Label);
        Assert.Null(blank.Problem);
        Assert.Equal("ฝ่ายบัญชี", ContactRecipientRule.NormalizeLabel(" ฝ่ายบัญชี\n").Label);
        Assert.Equal(NotifyEndpointKind.LineIdHash("Ubot", "U1"), NotifyEndpointKind.LineIdHash(" Ubot", "U1 "));
        Assert.NotEqual(NotifyEndpointKind.LineIdHash("Ubot", "U1"), NotifyEndpointKind.LineIdHash(null, "U1"));
    }

    // ───── ฝ่ายค้าน S4: แถวหลักหลายแถว + รวมผู้ติดต่อ ─────

    [Fact]
    public void S4_แถวหลักสองแถว_เลือกแถวที่ผูกก่อน_ไม่ใช่ตามGuid()
    {
        // แถวที่ผูกก่อนมี Guid "ใหญ่กว่า" — การเลือกตาม Guid จะได้คำตอบผิด
        var older = Row(NotifyEndpointKind.Email, primary: true, topics: new[] { NotifyTopic.Dunning }, linked: new DateTime(2026, 1, 1),
            id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var newer = Row(NotifyEndpointKind.Email, primary: true, topics: NotifyTopics.All, linked: new DateTime(2026, 6, 1),
            id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var set = Resolve(Contact(), new[] { newer, older }, NotifyTopic.Document);
        Assert.Empty(set.Emails);   // แถวที่ผูกก่อนรับเฉพาะทวงชำระ
        Assert.Equal(older.Id, Assert.Single(set.Options).EndpointId);
        Assert.Same(older, ContactRecipientRule.PickPrimary(new[] { newer, older }, e => e.LinkedAt, e => e.LinkedAt));
    }

    [Fact]
    public void S4_MergeUnlinks_ยกเลิกแถวหลักของผู้ถูกรวม_อีเมลซ้ำ_ซ้ำอีเมลหลัก_LINEซ้ำ_คงแถวของผู้ที่เก็บไว้()
    {
        var t0 = new DateTime(2026, 1, 1);
        var keepPrimary = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, true, null, null, false, t0.AddDays(5));
        var loserPrimary = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, true, null, null, true, t0);
        var keepBill = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, false, "bill@x.co.th", null, false, t0.AddDays(9));
        var loserBillDup = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, false, "BILL@x.co.th", null, true, t0);
        var loserSameAsPrimary = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, false, "ap@x.co.th", null, true, t0);
        var loserOther = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, false, "other@x.co.th", null, true, t0);
        var keepLine = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.LineUser, false, null, "h1", false, t0.AddDays(3));
        var loserLineDup = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.LineUser, false, null, "h1", true, t0);
        var unlink = ContactRecipientRule.MergeUnlinks(new[] { keepPrimary, loserPrimary, keepBill, loserBillDup, loserSameAsPrimary, loserOther, keepLine, loserLineDup },
            "ap@x.co.th");
        Assert.Equal(new[] { loserPrimary.Id, loserBillDup.Id, loserSameAsPrimary.Id, loserLineDup.Id }.OrderBy(x => x), unlink.OrderBy(x => x));
        Assert.DoesNotContain(loserOther.Id, unlink);   // ทิศตรงข้าม: อีเมลที่ไม่ซ้ำย้ายตามมาใช้ต่อ
    }

    [Fact]
    public void S4_MergeUnlinks_ผู้ที่เก็บไว้ไม่มีแถวหลัก_คงแถวหลักของผู้ถูกรวม_เรื่องที่ตั้งไว้ไม่หาย()
    {
        var a = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, true, null, null, true, new DateTime(2026, 2, 1));
        var b = new ContactRecipientRule.MergeRow(Guid.NewGuid(), NotifyEndpointKind.Email, true, null, null, true, new DateTime(2026, 1, 1));
        Assert.Equal(new[] { a.Id }, ContactRecipientRule.MergeUnlinks(new[] { a, b }, null));
        Assert.Empty(ContactRecipientRule.MergeUnlinks(Array.Empty<ContactRecipientRule.MergeRow>(), "ap@x.co.th"));
    }
}
