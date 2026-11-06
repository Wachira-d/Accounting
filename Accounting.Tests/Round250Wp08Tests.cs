using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 WP-08 (erp-review/2026-11-06/AUDIT_249_FOR_OPUS.md §4 WP-08 · คำตัดสิน 250-26/27/29) — ตัวตัดสิน pure ทุกตัวมีทิศตรงข้าม:
/// (1) L1 ต่อสาย: เรื่องของ trigger · อีเมลที่ส่งได้/เหตุไม่ส่ง (<see cref="ContactRecipientRule.ForEmail"/>) · ใบเสร็จหลังรับชำระบอกเหตุยกเลิกรับ
/// (2) ชั้นผู้ติดต่อ "ไม่ส่งอัตโนมัติ" (<see cref="AutopilotSettingsResolver.AutoSendBlock"/>) · ผลรอบรายการประจำบอกเหตุ
/// (3) สินทรัพย์: จุดตั้งต้นค่าเสื่อมตัวเดียว · ด่านวันพร้อมใช้ของสินทรัพย์ใบเดียว · ราคาตามสัญญา
/// </summary>
public class Round250Wp08Tests
{
    private static readonly Guid Co = Guid.Parse("25000000-0000-0000-0000-000000000008");
    private static readonly Guid Ct = Guid.Parse("25000000-0000-0000-0000-0000000000c1");

    private static ContactRecipientRule.ContactFacts Contact(string? email) => new(Ct, Co, email, false);

    private static ContactRecipientRule.EndpointFacts Row(string? email, bool primary, params NotifyTopic[] topics)
        => new(Guid.NewGuid(), Co, Ct, NotifyEndpointKind.Email, null, email, null, NotifyEndpointKind.ScopeCompany, null, null,
            NotifyTopics.Serialize(topics), true, primary, true,
            primary ? NotifyEndpointKind.SourceContactEmail : NotifyEndpointKind.SourceManual,
            null, null, null, null, null, false, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

    private static ContactRecipientRule.EmailDelivery Deliver(string? contactEmail, NotifyTopic topic, params ContactRecipientRule.EndpointFacts[] rows)
        => ContactRecipientRule.ForEmail(ContactRecipientRule.Resolve(Co, Contact(contactEmail), rows, topic, Array.Empty<string>(), false));

    // ═══ (1) L1 ต่อสาย ═══

    [Theory]
    [InlineData("DocumentApproved", NotifyTopic.Document)]
    [InlineData("RecurringInvoiceCreated", NotifyTopic.Document)]
    [InlineData("DocumentDueSoon", NotifyTopic.Dunning)]
    [InlineData("DocumentOverdue", NotifyTopic.Dunning)]
    [InlineData("MonthlyStatement", NotifyTopic.Dunning)]
    [InlineData("WhtCertIssued", NotifyTopic.Wht)]
    [InlineData("PayrollPaid", NotifyTopic.Other)]
    [InlineData("ไม่รู้จัก", NotifyTopic.Other)]
    [InlineData(null, NotifyTopic.Other)]
    public void เรื่องของtrigger_ตัวเดียว_ไม่รู้จักเป็นอื่นๆ(string? trigger, NotifyTopic expected)
        => Assert.Equal(expected, NotifyTopics.ForTrigger(trigger));

    [Fact]
    public void ไม่ตั้งปลายทาง_ได้ContactEmailเดิมทุกเรื่อง_ไม่ถดถอย()
    {
        foreach (var t in NotifyTopics.All)
        {
            var d = Deliver("AP@Buyer.co.th", t);
            Assert.True(d.Deliverable);
            Assert.Equal("ap@buyer.co.th", d.To);
            Assert.Null(d.OptOutReason);
        }
    }

    [Fact]
    public void ทวงชำระ_ส่งถึงอีเมลที่สอง_ที่เลือกรับเรื่องนี้()
    {
        var d = Deliver("ap@buyer.co.th", NotifyTopic.Dunning, Row("finance@buyer.co.th", false, NotifyTopic.Dunning));
        Assert.Equal(new[] { "ap@buyer.co.th", "finance@buyer.co.th" }, d.Emails);
        Assert.Equal("ap@buyer.co.th, finance@buyer.co.th", d.To);
        // ทิศตรงข้าม: อีเมลที่สองไม่ได้เลือกรับ "เอกสาร" ⇒ เอกสารถึงอีเมลหลักเท่านั้น
        var doc = Deliver("ap@buyer.co.th", NotifyTopic.Document, Row("finance@buyer.co.th", false, NotifyTopic.Dunning));
        Assert.Equal(new[] { "ap@buyer.co.th" }, doc.Emails);
    }

    [Fact]
    public void ยกเลิกรับทุกปลายทาง_ไม่ส่ง_พร้อมเหตุ_ไม่ใช่ไม่มีอีเมล()
    {
        // อีเมลหลักตั้งรับเฉพาะใบเสร็จ ⇒ เรื่องทวงชำระไม่มีผู้รับ
        var d = Deliver("ap@buyer.co.th", NotifyTopic.Dunning, Row(null, true, NotifyTopic.Receipt));
        Assert.False(d.Deliverable);
        Assert.Null(d.To);
        Assert.NotNull(d.OptOutReason);
        Assert.Contains("ทวงชำระ", d.OptOutReason);
        Assert.Contains("ap@buyer.co.th", d.OptOutReason);
    }

    [Fact]
    public void ไม่มีอีเมลเลย_ว่างทั้งคู่_พฤติกรรมเดิมคือไม่เข้าคิว()
    {
        var d = Deliver(null, NotifyTopic.Document);
        Assert.False(d.Deliverable);
        Assert.Null(d.OptOutReason);
    }

    [Fact]
    public void ใบเสร็จหลังรับชำระ_ยกเลิกรับ_บอกเหตุ_ทิศตรงข้ามไม่มีอีเมลข้อความเดิม()
    {
        var on = new PostPaymentAutomationPolicy.Settings(EmailReceipt: true, IncludePartial: false);
        PostPaymentAutomationPolicy.Facts F(string? email, string? optOut)
            => new(true, DocumentStatus.Paid, false, true, 0, false, false, false, false, false, email, null, 0m, true,
                PaymentEntrySource.Manual, null, EmailOptOutReason: optOut);
        var optOut = PostPaymentAutomationPolicy.Decide(on, F(null, "ไม่ส่งอีเมลเรื่อง \"ใบเสร็จ\" — x"));
        Assert.Contains(optOut.Blocked, b => b.Contains("ไม่ส่งอีเมลเรื่อง \"ใบเสร็จ\""));
        Assert.DoesNotContain(optOut.Blocked, b => b.StartsWith("ลูกค้าไม่มีอีเมล"));
        var none = PostPaymentAutomationPolicy.Decide(on, F(null, null));
        Assert.Contains(none.Blocked, b => b.StartsWith("ลูกค้าไม่มีอีเมลที่ใช้ได้"));
        // ทิศตรงข้าม: มีอีเมล ⇒ ไม่ติดเรื่องอีเมล
        var ok = PostPaymentAutomationPolicy.Decide(on, F("ap@buyer.co.th", null));
        Assert.DoesNotContain(ok.Blocked, b => b.StartsWith("ลูกค้าไม่มีอีเมล") || b.Contains("ไม่ส่งอีเมลเรื่อง"));
    }

    // ═══ (2) ชั้นผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ═══

    [Fact]
    public void ไม่ส่งอัตโนมัติ_ชนะ_ทิศตรงข้ามไม่ตั้งส่งได้()
    {
        Assert.Equal(AutopilotSettingsResolver.AutoSendDisabledReason, AutopilotSettingsResolver.AutoSendBlock(true));
        Assert.Null(AutopilotSettingsResolver.AutoSendBlock(false));
    }

    private static PostPaymentAutomationPolicy.Facts PayFacts(bool hasReceipt, string? email, string? autoOff)
        => new(true, DocumentStatus.Paid, false, true, 0, hasReceipt, false, false, false, false, email, null, 0m, true,
            PaymentEntrySource.Manual, null, AutoSendBlockedReason: autoOff);

    [Fact]
    public void ไม่ส่งอัตโนมัติ_ยังออกใบเสร็จ_แต่หยุดขั้นส่งอีเมลพร้อมเหตุ_ทิศตรงข้ามส่งตามเดิม()
    {
        // ฝ่ายค้าน HIGH-2 (Option 1): ห้ามแค่ "ส่งอีเมล" ไม่ห้าม "ออกเอกสาร" · แม้ไม่มีอีเมลก็ยังออกใบเสร็จ (เหตุคือค่าตั้ง ไม่ใช่ "ไม่มีอีเมล")
        var on = new PostPaymentAutomationPolicy.Settings(EmailReceipt: true, IncludePartial: false);
        var off = PostPaymentAutomationPolicy.Decide(on, PayFacts(false, null, AutopilotSettingsResolver.AutoSendDisabledReason));
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.IssueReceipt }, off.Steps);
        Assert.Contains(off.Blocked, b => b.StartsWith(PostPaymentAutomationPolicy.AutoSendBlockedPrefix) && b.Contains("ไม่ส่งเอกสารอัตโนมัติ"));
        Assert.DoesNotContain(off.Blocked, b => b.StartsWith("ลูกค้าไม่มีอีเมล"));
        // มีใบเสร็จแล้ว ⇒ ไม่มีขั้นใดเหลือ แต่ยังบอกเหตุ (ไม่เงียบ)
        var hasRec = PostPaymentAutomationPolicy.Decide(on, PayFacts(true, "ap@buyer.co.th", AutopilotSettingsResolver.AutoSendDisabledReason));
        Assert.Empty(hasRec.Steps);
        Assert.Single(hasRec.Blocked);
        // ทิศตรงข้าม: ไม่ปิด ⇒ ออก + ส่ง
        var normal = PostPaymentAutomationPolicy.Decide(on, PayFacts(false, "ap@buyer.co.th", null));
        Assert.Equal(new[] { PostPaymentAutomationPolicy.Step.IssueReceipt, PostPaymentAutomationPolicy.Step.EmailReceipt }, normal.Steps);
        Assert.Empty(normal.Blocked);
    }

    [Fact]
    public void ไม่ส่งอัตโนมัติ_ชนะแม้ตารางส่งตั้งส่งใบเสร็จOn()
    {
        // ฝ่ายค้าน MED-4: ค่าตั้งที่มีผล (ตาราง On) ไม่ช่วยให้ส่ง — ด่านอยู่ที่ขั้นส่งอีเมล ไม่ใช่ในชั้นค่าตั้ง
        var company = new PostPaymentAutomationPolicy.Settings(EmailReceipt: false, IncludePartial: false);
        var schedule = new PostPaymentAutomationPolicy.Overrides(true, null);
        var effective = PostPaymentAutomationPolicy.EffectiveLayers(company, schedule, null, null);
        Assert.True(effective.EmailReceipt);
        var plan = PostPaymentAutomationPolicy.Decide(effective, PayFacts(true, "ap@buyer.co.th", AutopilotSettingsResolver.AutoSendDisabledReason));
        Assert.DoesNotContain(PostPaymentAutomationPolicy.Step.EmailReceipt, plan.Steps);
        Assert.Contains(plan.Blocked, b => b.StartsWith(PostPaymentAutomationPolicy.AutoSendBlockedPrefix));
    }

    [Fact]
    public void ผลรอบรายการประจำ_ไม่ส่งเพราะค่าตั้งผู้ติดต่อ_บอกเหตุ_ทิศตรงข้ามSkippedทั่วไปเงียบ()
    {
        var (facts, notice) = EmailEnqueueOutcome.Apply(new RecurringRunFacts(), EmailEnqueueOutcome.NotSent(AutopilotSettingsResolver.AutoSendDisabledReason), "INV-0008");
        Assert.Equal(AutopilotSettingsResolver.AutoSendDisabledReason, facts.EmailNotSentReason);
        Assert.False(facts.IsEmpty);
        Assert.Contains("INV-0008", notice);
        var doc = new RecurringRunDoc(Guid.NewGuid(), "INV-0008", DocumentStatus.Approved, DocumentType.Invoice, new DateTime(2026, 11, 6));
        var view = RecurringRunOutcome.Build(facts, null, doc, Ct, "ap@buyer.co.th", false, true);
        Assert.NotNull(view);
        Assert.Contains(view!.Steps, s => s.Contains("ไม่ส่งเอกสารอัตโนมัติ"));
        Assert.Contains(view.Actions, a => a.Kind == "openContact");

        var (quiet, n2) = EmailEnqueueOutcome.Apply(new RecurringRunFacts(), EmailEnqueueOutcome.Skipped(EmailEnqueueOutcome.CoveredByApprovedRule), "INV-1");
        Assert.Null(quiet.EmailNotSentReason);
        Assert.Null(n2);
        Assert.True(quiet.IsEmpty);
    }

    // ═══ (3) สินทรัพย์ ═══

    [Fact]
    public void จุดตั้งต้นค่าเสื่อม_วันพร้อมใช้ก่อน_ว่างใช้วันที่ซื้อ()
    {
        var bought = new DateTime(2026, 1, 15);
        Assert.Equal(bought, DepreciationSchedule.StartDate(bought, null));                                         // ไม่ถดถอย
        Assert.Equal(new DateTime(2026, 3, 1), DepreciationSchedule.StartDate(bought, new DateTime(2026, 3, 1, 13, 0, 0)));
        // งวดแรก = เดือนถัดจากพร้อมใช้ (ธรรมเนียมเดิม · คำตัดสิน 250-24)
        var start = DepreciationSchedule.StartDate(bought, new DateTime(2026, 3, 1));
        Assert.Equal(-1, DepreciationSchedule.MonthIndexFor(start, 2026, 3));
        Assert.Equal(0, DepreciationSchedule.MonthIndexFor(start, 2026, 4));
        Assert.Equal(new DateTime(2026, 4, 1).Month, DepreciationSchedule.Build(DepreciationMethod.StraightLine, 1200m, 0m, 12, start)[0].Month);
    }

    [Fact]
    public void วันพร้อมใช้ใบเดียว_แก้ได้ก่อนลงค่าเสื่อม_ทิศตรงข้ามล็อกหลังลง()
    {
        var bought = new DateTime(2026, 1, 15);
        var today = new DateTime(2026, 11, 6);
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2026, 2, 1), today, AssetStatus.Active, 0m, false));
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, null, today, AssetStatus.Active, 0m, false));   // ล้าง = วันที่เอกสาร
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, true, bought, new DateTime(2026, 2, 1), today, AssetStatus.Active, 0m, false));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, true, bought, null, today, AssetStatus.Active, 0m, false));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(true, true, false, bought, new DateTime(2026, 2, 1), today, AssetStatus.Active, 0m, false));   // ระหว่างจัดหา
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, true, false, bought, new DateTime(2026, 2, 1), today, AssetStatus.Active, 0m, false));  // หลายใบ
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2026, 1, 14), today, AssetStatus.Active, 0m, false)); // ก่อนวันซื้อ
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2026, 1, 15), today, AssetStatus.Active, 0m, false));   // วันเดียวกันได้
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2028, 1, 1), today, AssetStatus.Active, 0m, false));  // ปีผิด
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("0", null, false)]
    [InlineData("1000000", "1000000", false)]
    [InlineData("1000.005", "1000.01", false)]
    [InlineData("-1", null, true)]
    [InlineData("1000000000000", null, true)]
    public void ราคาตามสัญญา_ล้างได้_ติดลบปฏิเสธ(string? raw, string? expected, bool problem)
    {
        var (value, p) = AssetCostSourceRules.NormalizeContractPrice(raw == null ? null : decimal.Parse(raw));
        Assert.Equal(expected == null ? null : decimal.Parse(expected), value);
        Assert.Equal(problem, p != null);
    }

    [Fact]
    public void ราคาตามสัญญาที่แก้แล้ว_ยังเป็นคำเตือนไม่บล็อก()
    {
        Assert.NotNull(AssetCostSourceRules.ContractPriceWarning(1_000_000m, 1_300_000m));
        Assert.Null(AssetCostSourceRules.ContractPriceWarning(1_300_000m, 1_300_000m));   // แก้ราคาแล้วเงียบ
        Assert.Null(AssetCostSourceRules.ContractPriceWarning(null, 1_300_000m));          // ล้างแล้วเงียบ
    }

    // ═══ ฝ่ายค้าน WP-08 (HIGH-1 · MED-1/2/3/5) ═══

    private static ContactRecipientRule.RecipientOption Opt(string email, bool primary, bool available = true)
        => new(Guid.NewGuid(), NotifyEndpointKind.Email, email, email, available, available ? null : "ไม่ได้เลือกรับ", null, true, primary);

    [Fact]
    public void อีเมลหลักมาก่อนเสมอ_ไม่พึ่งลำดับOptions()
    {
        var set = new ContactRecipientRule.RecipientSet(NotifyTopic.Document, new[] { Opt("extra@b.co.th", false), Opt("main@b.co.th", true) });
        var d = ContactRecipientRule.ForEmail(set);
        Assert.Equal(new[] { "main@b.co.th", "extra@b.co.th" }, d.Emails);
        Assert.Equal(new[] { "main@b.co.th" }, d.PrimaryEmails);
        Assert.Equal("main@b.co.th", d.First);
    }

    [Fact]
    public void eTax_ส่งถึงอีเมลหลักเท่านั้น_อีเมลเสริมบันทึกว่าไม่ได้ส่ง_ทิศตรงข้ามอีเมลธรรมดาได้ครบ()
    {
        var d = ContactRecipientRule.ForEmail(new ContactRecipientRule.RecipientSet(NotifyTopic.Document,
            new[] { Opt("main@b.co.th", true), Opt("extra@b.co.th", false) }));
        var etax = ContactRecipientRule.ForChannel(d, etaxByEmail: true);
        Assert.Equal("main@b.co.th", etax.To);
        Assert.Equal(new[] { "extra@b.co.th" }, etax.NotSent);
        Assert.StartsWith(ContactRecipientRule.EtaxPrimaryOnlyPrefix, etax.Note);
        Assert.Contains("extra@b.co.th", etax.Note);
        Assert.Null(EtaxByEmailFormat.Recipients(etax.To, null, null, true, true, false, false).Problem);   // ผ่านตัวตรวจผู้รับ e-Tax
        var plain = ContactRecipientRule.ForChannel(d, etaxByEmail: false);
        Assert.Equal("main@b.co.th, extra@b.co.th", plain.To);
        Assert.Null(plain.Note);
        // อีเมลหลักคนเดียว ⇒ ไม่มีหมายเหตุ
        var only = ContactRecipientRule.ForChannel(ContactRecipientRule.ForEmail(new ContactRecipientRule.RecipientSet(NotifyTopic.Document,
            new[] { Opt("main@b.co.th", true) })), etaxByEmail: true);
        Assert.Equal("main@b.co.th", only.To);
        Assert.Null(only.Note);
    }

    [Fact]
    public void eTax_อีเมลหลักปิดรับแต่อีเมลเสริมรับ_ไม่ส่งพร้อมเหตุตรงกับคำเตือนในแท็บ()
    {
        var d = ContactRecipientRule.ForEmail(new ContactRecipientRule.RecipientSet(NotifyTopic.Document,
            new[] { Opt("main@b.co.th", true, available: false), Opt("extra@b.co.th", false) }));
        Assert.True(d.Deliverable);
        Assert.Empty(d.PrimaryEmails);
        var etax = ContactRecipientRule.ForChannel(d, etaxByEmail: true);
        Assert.Null(etax.To);
        Assert.Contains("อีเมลหลัก", etax.Problem);
        Assert.Contains("extra@b.co.th", etax.Problem);
        // ทิศตรงข้าม: เส้นอีเมลธรรมดาส่งถึงอีเมลเสริมได้
        Assert.Equal("extra@b.co.th", ContactRecipientRule.ForChannel(d, etaxByEmail: false).To);
    }

    [Fact]
    public void ผู้ติดต่อถูกลบ_มีเหตุ_ทิศตรงข้ามบริษัทอื่นเงียบ()
    {
        var deleted = new ContactRecipientRule.ContactFacts(Ct, Co, "ap@buyer.co.th", true);
        var d = ContactRecipientRule.ForEmail(ContactRecipientRule.Resolve(Co, deleted, Array.Empty<ContactRecipientRule.EndpointFacts>(),
            NotifyTopic.Dunning, Array.Empty<string>(), false));
        Assert.False(d.Deliverable);
        Assert.Equal(ContactRecipientRule.ContactDeletedReason, d.OptOutReason);
        var other = ContactRecipientRule.ForEmail(ContactRecipientRule.Resolve(Guid.NewGuid(), deleted,
            Array.Empty<ContactRecipientRule.EndpointFacts>(), NotifyTopic.Dunning, Array.Empty<string>(), false));
        Assert.Null(other.OptOutReason);   // ไม่เปิดเผยผู้ติดต่อของบริษัทอื่น
    }

    [Fact]
    public void เพดานผู้รับNextAcc_ตัดตอนเข้าคิวพร้อมหมายเหตุ_ทิศตรงข้ามSMTPบริษัทส่งครบ()
    {
        var to = string.Join(", ", Enumerable.Range(1, 7).Select(i => $"u{i}@b.co.th"));
        var (capped, note) = ContactRecipientRule.CapForSender(to, platformSender: true, otherRecipients: 1, maxRecipients: 5, maxLength: 500);
        Assert.Equal(4, capped.Split(", ").Length);
        Assert.StartsWith("u1@b.co.th", capped);
        Assert.StartsWith(ContactRecipientRule.CappedPrefix, note);
        Assert.Contains("u7@b.co.th", note);
        var (all, none) = ContactRecipientRule.CapForSender(to, platformSender: false, otherRecipients: 1, maxRecipients: 5, maxLength: 500);
        Assert.Equal(7, all.Split(", ").Length);
        Assert.Null(none);
        // ความยาวช่องเก็บ ⇒ ตัดพร้อมหมายเหตุ (SMTP บริษัทก็ตัด)
        var (shortTo, lenNote) = ContactRecipientRule.CapForSender(to, platformSender: false, otherRecipients: 0, maxRecipients: 5, maxLength: 30);
        Assert.True(shortTo.Length <= 30);
        Assert.NotNull(lenNote);
    }

    [Fact]
    public void หมายเหตุส่งไม่ครบ_คงไว้หลังส่ง_ข้อความผิดพลาดอื่นถูกล้าง()
    {
        Assert.NotNull(Accounting.Services.Implementations.EmailScheduleService.AfterSentNote(ContactRecipientRule.EtaxPrimaryOnlyPrefix + "x"));
        Assert.NotNull(Accounting.Services.Implementations.EmailScheduleService.AfterSentNote(ContactRecipientRule.CappedPrefix + "x"));
        Assert.Null(Accounting.Services.Implementations.EmailScheduleService.AfterSentNote("SMTP 451"));
        Assert.Null(Accounting.Services.Implementations.EmailScheduleService.JoinNotes(null, " "));
        Assert.Equal("a · b", Accounting.Services.Implementations.EmailScheduleService.JoinNotes("a", null, "b"));
    }

    [Fact]
    public void ลิงก์เซ็นอัตโนมัติ_ส่งเฉพาะผู้ที่ยังรับเรื่องเซ็น_ทิศตรงข้ามไม่มีผู้รับเดิมใช้ช่องทางทั้งหมด()
    {
        var sign = ContactRecipientRule.ForEmail(new ContactRecipientRule.RecipientSet(NotifyTopic.Sign,
            new[] { Opt("main@b.co.th", true, available: false), Opt("buy@b.co.th", false) }));
        Assert.Equal(new[] { "buy@b.co.th" },
            Accounting.Services.Implementations.SignRequestService.SignAutomationRecipients(new[] { "main@b.co.th", "buy@b.co.th" }, sign));
        Assert.Empty(Accounting.Services.Implementations.SignRequestService.SignAutomationRecipients(new[] { "main@b.co.th" }, sign));
        Assert.Equal(new[] { "buy@b.co.th" },
            Accounting.Services.Implementations.SignRequestService.SignAutomationRecipients(Array.Empty<string>(), sign));
    }

    [Fact]
    public void ลิงก์เซ็นอัตโนมัติ_ผู้รับที่พิมพ์เองไม่อยู่ในช่องทางผู้ติดต่อ_คงไว้_ผู้ที่ปฏิเสธตัดออก_ผู้ติดต่อถูกลบไม่ส่ง()
    {
        // CI 42232967230: อีเมลเตือนรอบ 240 ส่งถึงผู้รับลิงก์เดิมที่ผู้ใช้พิมพ์เอง — ไม่มีค่าตั้งของผู้ติดต่อที่บอกว่าเขาไม่รับ ⇒ ไม่ตัด
        var sign = ContactRecipientRule.ForEmail(new ContactRecipientRule.RecipientSet(NotifyTopic.Sign,
            new[] { Opt("main@b.co.th", true, available: false), Opt("buy@b.co.th", false) }));
        Assert.Equal(new[] { "dn@other.co.th" },
            Accounting.Services.Implementations.SignRequestService.SignAutomationRecipients(new[] { "dn@other.co.th" }, sign));
        Assert.Equal(new[] { "dn@other.co.th", "buy@b.co.th" },
            Accounting.Services.Implementations.SignRequestService.SignAutomationRecipients(new[] { "dn@other.co.th", "MAIN@b.co.th", "buy@b.co.th" }, sign));
        // ทิศตรงข้าม: ผู้ติดต่อถูกลบ ⇒ ไม่ส่งถึงใคร แม้ผู้รับเดิมพิมพ์เอง
        var deleted = ContactRecipientRule.ForEmail(new ContactRecipientRule.RecipientSet(NotifyTopic.Sign,
            Array.Empty<ContactRecipientRule.RecipientOption>()) { ContactDeleted = true });
        Assert.Empty(Accounting.Services.Implementations.SignRequestService.SignAutomationRecipients(new[] { "dn@other.co.th" }, deleted));
    }

    [Fact]
    public void วันพร้อมใช้_ล็อกเมื่อไม่ใช่สถานะใช้งาน_มียอดยกมา_หรือทบทวนอายุแล้ว()
    {
        var bought = new DateTime(2026, 1, 15);
        var today = new DateTime(2026, 11, 6);
        var d = new DateTime(2026, 2, 1);
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, d, today, AssetStatus.Disposed, 0m, false));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, d, today, AssetStatus.FullyDepreciated, 0m, false));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, d, today, AssetStatus.Active, 5_000m, false));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, d, today, AssetStatus.Active, 0m, true));
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, d, today, AssetStatus.Active, 0m, false));   // ทิศตรงข้าม
    }
}
