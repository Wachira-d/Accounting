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

    [Theory]
    [InlineData(true, null, false)]    // ปิดอัตโนมัติ ⇒ Off แม้ไม่ได้ตั้ง
    [InlineData(true, true, false)]    // ปิดอัตโนมัติชนะค่า On ของผู้ติดต่อเอง
    [InlineData(false, true, true)]    // ทิศตรงข้าม: ไม่ปิด ⇒ ค่าเดิม
    [InlineData(false, null, null)]    // ไม่ปิด + ไม่ตั้ง ⇒ ตามบริษัท
    public void ชั้นผู้ติดต่อของใบเสร็จหลังรับชำระ(bool disabled, bool? setting, bool? expected)
        => Assert.Equal(expected, AutopilotSettingsResolver.ContactPostPayEmailReceipt(setting, disabled));

    [Fact]
    public void ตารางส่งที่ตั้งเอง_ยังชนะชั้นผู้ติดต่อที่ปิดอัตโนมัติ()
    {
        var company = new PostPaymentAutomationPolicy.Settings(EmailReceipt: true, IncludePartial: false);
        var contact = new PostPaymentAutomationPolicy.Overrides(AutopilotSettingsResolver.ContactPostPayEmailReceipt(null, true), null);
        Assert.False(PostPaymentAutomationPolicy.EffectiveLayers(company, null, null, contact).EmailReceipt);
        var schedule = new PostPaymentAutomationPolicy.Overrides(true, null);
        Assert.True(PostPaymentAutomationPolicy.EffectiveLayers(company, schedule, null, contact).EmailReceipt);
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
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2026, 2, 1), today));
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, null, today));   // ล้าง = วันที่เอกสาร
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, true, bought, new DateTime(2026, 2, 1), today));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, true, bought, null, today));
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(true, true, false, bought, new DateTime(2026, 2, 1), today));   // ระหว่างจัดหา
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, true, false, bought, new DateTime(2026, 2, 1), today));  // หลายใบ
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2026, 1, 14), today)); // ก่อนวันซื้อ
        Assert.Null(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2026, 1, 15), today));   // วันเดียวกันได้
        Assert.NotNull(FixedAssetValuationEdit.AvailableDateProblem(false, false, false, bought, new DateTime(2028, 1, 1), today));  // ปีผิด
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
}
