using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 250 (WP-02 · erp-review/2026-11-06/AUDIT_249_FOR_OPUS.md §4) — ตัวตัดสิน pure ของบั๊กเงิน/ความถูกต้อง P0–P1 · ทุกด่านมีทิศตรงข้าม
/// (§2.1 ทางเข้ารับชำระ · B-13 ชั้นค่าตั้ง · §2.2 GL สูตรเดียว · §2.3 ลิงก์ชำระของใบแจ้งหนี้ที่มีใบกำกับ · §2.4 ล้มดัง/ซ้ำ/ลำดับ · Y7/Y8/N11 การ์ด go-live)
/// </summary>
public class Round250Wp02Tests
{
    private static readonly PostPaymentAutomationPolicy.Settings On = new(EmailReceipt: true, IncludePartial: false);
    private static readonly PostPaymentAutomationPolicy.Settings Off = new(EmailReceipt: false, IncludePartial: false);

    private static PostPaymentAutomationPolicy.Facts F(PaymentEntrySource source = PaymentEntrySource.Manual, string? taxInvoice = null)
        => new(true, DocumentStatus.Paid, false, true, 0, false, false, false, false, false, "ap@buyer.co.th", null, 0m, true,
            source, taxInvoice);

    // ═══ §2.1 งานหลังรับชำระทุกทางเข้า ═══

    [Theory]
    [InlineData(PaymentEntrySource.Manual)]
    [InlineData(PaymentEntrySource.Gateway)]
    [InlineData(PaymentEntrySource.Settlement)]
    [InlineData(PaymentEntrySource.Import)]
    [InlineData(PaymentEntrySource.Integration)]
    public void ทางเข้าที่เดินเต็ม_เข้าคิวแถวรอประมวลผล(PaymentEntrySource source)
    {
        var d = PostPaymentAutomationPolicy.ForEnqueue(On, source);
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.Pending, d.Kind);
        Assert.Null(d.Reason);
    }

    [Theory]
    [InlineData(PaymentEntrySource.Cms)]
    [InlineData(PaymentEntrySource.Lodging)]
    [InlineData(PaymentEntrySource.PlatformBilling)]
    public void ทางเข้าที่มีเส้นใบเสร็จของตัวเอง_แถวSkippedพร้อมเหตุ_ไม่ออกใบเสร็จซ้ำ(PaymentEntrySource source)
    {
        var d = PostPaymentAutomationPolicy.ForEnqueue(On, source);
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.Skipped, d.Kind);
        Assert.Contains("มีเส้นออกใบเสร็จของตัวเอง", d.Reason);
        // ตอนประมวลผลแถวเก่าที่เข้าคิวก่อนแก้ — ตัวตัดสินเดียวกันข้าม (ไม่ออก/ไม่ส่ง)
        var p = PostPaymentAutomationPolicy.Decide(On, F(source));
        Assert.Empty(p.Steps);
        Assert.Contains("มีเส้นออกใบเสร็จของตัวเอง", p.Skipped);
    }

    [Fact]
    public void MultiDoc_ไม่สร้างแถว_และปิดค่าตั้ง_ไม่สร้างแถว()
    {
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.None, PostPaymentAutomationPolicy.ForEnqueue(On, PaymentEntrySource.MultiDoc).Kind);
        Assert.Equal(PostPaymentAutomationPolicy.MultiDocReason, PostPaymentAutomationPolicy.ForEnqueue(On, PaymentEntrySource.MultiDoc).Reason);
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.None, PostPaymentAutomationPolicy.ForEnqueue(Off, PaymentEntrySource.Import).Kind);
        // ทิศตรงข้าม: ปิดค่าตั้ง + ทางเข้ามีเส้นของตัวเอง ⇒ ไม่สร้างแถวเลย (ไม่ใช่ Skipped)
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.None, PostPaymentAutomationPolicy.ForEnqueue(Off, PaymentEntrySource.Cms).Kind);
    }

    [Fact]
    public void ชื่อทางเข้าในแถวเก่า_ตัวเล็ก_อ่านได้_ไม่รู้จัก_เป็นมือ()
    {
        Assert.Equal(PaymentEntrySource.Manual, PostPaymentAutomationPolicy.ParseSource("manual"));
        Assert.Equal(PaymentEntrySource.Gateway, PostPaymentAutomationPolicy.ParseSource("gateway"));
        Assert.Equal(PaymentEntrySource.Import, PostPaymentAutomationPolicy.ParseSource("Import"));
        Assert.Equal(PaymentEntrySource.Manual, PostPaymentAutomationPolicy.ParseSource("???"));
        Assert.Equal(PaymentEntrySource.Manual, PostPaymentAutomationPolicy.ParseSource(null));
    }

    // ═══ B-13 ชั้นค่าตั้ง ตาราง › รายการประจำ › ผู้ติดต่อ › บริษัท ═══

    [Fact]
    public void R248_ค่าตั้งต่อรายการปิด_ไม่ออกใบเสร็จแม้บริษัทเปิด()
    {
        var s = PostPaymentAutomationPolicy.EffectiveLayers(On, null, new PostPaymentAutomationPolicy.Overrides(false, null), null);
        Assert.False(s.EmailReceipt);
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.None, PostPaymentAutomationPolicy.ForEnqueue(s, PaymentEntrySource.Manual).Kind);
    }

    [Fact]
    public void R248_ค่าตั้งต่อรายการInherit_ตามบริษัทเหมือนเดิม()
    {
        var s = PostPaymentAutomationPolicy.EffectiveLayers(On, null, new PostPaymentAutomationPolicy.Overrides(null, null), null);
        Assert.Equal(On, s);
        Assert.Equal(On, PostPaymentAutomationPolicy.EffectiveLayers(On, null, null, null));
    }

    [Fact]
    public void ชั้นค่าตั้ง_ตารางชนะรายการ_รายการชนะผู้ติดต่อ_ทีละช่อง()
    {
        var schedule = new PostPaymentAutomationPolicy.Overrides(true, null);
        var recurring = new PostPaymentAutomationPolicy.Overrides(false, true);
        var contact = new PostPaymentAutomationPolicy.Overrides(false, false);
        var s = PostPaymentAutomationPolicy.EffectiveLayers(Off, schedule, recurring, contact);
        Assert.True(s.EmailReceipt);       // ตาราง
        Assert.True(s.IncludePartial);     // รายการ (ตารางไม่ตอบช่องนี้)
        var c = PostPaymentAutomationPolicy.EffectiveLayers(On, null, null, contact);
        Assert.False(c.EmailReceipt);      // ผู้ติดต่อทับบริษัท
    }

    [Fact]
    public void สามสถานะ_ชื่อเข้าออก_และปฏิเสธชื่อแปลก()
    {
        Assert.Equal(AutopilotSettingsResolver.Inherit, AutopilotSettingsResolver.TriStateName(null));
        Assert.Equal(AutopilotSettingsResolver.On, AutopilotSettingsResolver.TriStateName(true));
        Assert.Equal(AutopilotSettingsResolver.Off, AutopilotSettingsResolver.TriStateName(false));
        Assert.True(AutopilotSettingsResolver.ParseTriState("on"));
        Assert.False(AutopilotSettingsResolver.ParseTriState("Off"));
        Assert.Null(AutopilotSettingsResolver.ParseTriState("Inherit"));
        Assert.Null(AutopilotSettingsResolver.ParseTriState(""));
        Assert.NotNull(AutopilotSettingsResolver.ValidateTriState("maybe"));
        Assert.Null(AutopilotSettingsResolver.ValidateTriState("Inherit"));
        Assert.Null(AutopilotSettingsResolver.ValidateTriState(null));
    }

    // ═══ §2.2 GL สูตรเดียวต่อบรรทัด ═══

    [Fact]
    public void ใบรับรองแทน_Standalone_กับ_Posted_ต่างกันเมื่อCIL_เท่ากันเมื่อใบธรรมดาและไม่มีส่วนออกแทน()
    {
        // ใบธรรมดา share=0 ⇒ ตัวเลขเดียวกันทั้งสองเส้น
        Assert.Equal(PurchaseLineGlAmount.Standalone(10_000m, 700m, true), PurchaseLineGlAmount.Posted(false, 10_000m, 700m, true, 0m));
        Assert.Equal(PurchaseLineGlAmount.Standalone(10_000m, 700m, false), PurchaseLineGlAmount.Posted(false, 10_000m, 700m, false, 0m));
        // ใบรับรองแทน (§82/4) — VAT รวมต้นทุนเสมอแม้ธงบรรทัดบอกเคลมได้ ⇒ ย้ายผังต้องย้าย 10,700 ไม่ใช่ 10,000
        Assert.Equal(10_000m, PurchaseLineGlAmount.Standalone(10_000m, 700m, true));
        Assert.Equal(10_700m, PurchaseLineGlAmount.Posted(true, 10_000m, 700m, true, 0m));
    }

    [Fact]
    public void ส่วนออกแทนต่อบรรทัด_ภาษีออกให้ตลอดไป3เปอร์เซ็นต์_ย้าย10309จุด28()
    {
        var line = Guid.NewGuid();
        var shares = WhtGrossUp.PayerBorneShareByLine(new List<(Guid, decimal, decimal)> { (line, 10_000m, 3m) }, WithholdingTaxCertType.PayAlways);
        Assert.Equal(309.28m, shares[line]);
        Assert.Equal(10_309.28m, PurchaseLineGlAmount.Posted(false, 10_000m, 0m, true, shares[line]));
        // ทิศตรงข้าม: หัก ณ ที่จ่าย (ไม่ออกแทน) ⇒ ส่วนออกแทน 0 ยอดย้าย = ฐานเดิม
        var plain = WhtGrossUp.PayerBorneShareByLine(new List<(Guid, decimal, decimal)> { (line, 10_000m, 3m) }, WithholdingTaxCertType.Withhold);
        Assert.Equal(0m, plain[line]);
    }

    [Fact]
    public void ส่วนออกแทนคิดจากทั้งใบ_เกลี่ยเศษเหมือนตัวคิดเดิม()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var lines = new List<(Guid, decimal, decimal)> { (a, 333.33m, 3m), (b, 333.34m, 3m) };
        var shares = WhtGrossUp.PayerBorneShareByLine(lines, WithholdingTaxCertType.PayAlways);
        var direct = WhtGrossUp.ForLines(new List<(decimal, decimal)> { (333.33m, 3m), (333.34m, 3m) }, WithholdingTaxCertType.PayAlways);
        Assert.Equal(direct[0].PayerBorne, shares[a]);
        Assert.Equal(direct[1].PayerBorne, shares[b]);
    }

    // ═══ §2.3 ลิงก์ชำระของใบแจ้งหนี้ที่มีใบกำกับแล้ว ═══

    [Fact]
    public void INVที่มีใบกำกับแล้ว_ไม่ออกลิงก์_บอกเลขใบกำกับ()
    {
        var why = DocumentPayLinkPolicy.IssueRefusal(DocumentType.Invoice, DocumentStatus.Approved, 1_070m, true, "THB", "TIV-2026-0042");
        Assert.NotNull(why);
        Assert.Contains("TIV-2026-0042", why);
        Assert.Contains(DocumentPayLinkPolicy.TaxInvoiceRuleCode, why);
    }

    [Fact]
    public void ลิงก์เดิมของINV_ใช้ไม่ได้หลังออกใบกำกับ()
    {
        var now = DateTime.UtcNow;
        Assert.True(DocumentPayLinkPolicy.LinkUsable("h", now.AddDays(5), null, now, null));
        Assert.False(DocumentPayLinkPolicy.LinkUsable("h", now.AddDays(5), null, now, "TIV-1"));
        Assert.Contains("TIV-1", DocumentPayLinkPolicy.UnusableNote("TIV-1", "ลิงก์ใช้ไม่ได้"));
        Assert.Equal("ลิงก์ใช้ไม่ได้", DocumentPayLinkPolicy.UnusableNote(null, "ลิงก์ใช้ไม่ได้"));
    }

    [Fact]
    public void INVที่ใบกำกับถูกยกเลิก_ออกลิงก์ได้_และINVไม่มีใบกำกับ_เดิม()
    {
        // ใบกำกับถูกยกเลิก ⇒ ตัวโหลดคืน null ⇒ ไม่มีเหตุปฏิเสธเรื่องนี้
        Assert.Null(DocumentPayLinkPolicy.IssueRefusal(DocumentType.Invoice, DocumentStatus.Approved, 1_070m, true, "THB", null));
        // ใบกำกับภาษีเองไม่ถูกปฏิเสธด้วยเงื่อนไขนี้ (ชนิดอื่นไม่ดูค่า)
        Assert.Null(DocumentPayLinkPolicy.IssueRefusal(DocumentType.TaxInvoice, DocumentStatus.Approved, 1_070m, true, "THB", "TIV-1"));
    }

    [Fact]
    public void งานหลังรับชำระ_INVที่มีใบกำกับแล้ว_หยุด_ไม่ออกใบเสร็จซ้อน_ทิศตรงข้ามไม่มีใบกำกับออกตามปกติ()
    {
        var blocked = PostPaymentAutomationPolicy.Decide(On, F(taxInvoice: "TIV-9"));
        Assert.Empty(blocked.Steps);
        var why = Assert.Single(blocked.Blocked);
        Assert.Contains("TIV-9", why);
        Assert.Contains("ออกใบเสร็จจากใบกำกับ", why);
        var normal = PostPaymentAutomationPolicy.Decide(On, F());
        Assert.Contains(PostPaymentAutomationPolicy.Step.IssueReceipt, normal.Steps);
    }

    // ═══ §2.4 ข้อ 1 EmailEnqueueFailed ═══

    [Fact]
    public void เข้าคิวอีเมลล้ม_ตั้งธงEmailEnqueueFailed_พร้อมข้อความ()
    {
        var (facts, notice) = EmailEnqueueOutcome.Apply(new RecurringRunFacts(), EmailEnqueueOutcome.Failed("DB timeout"), "INV-001");
        Assert.True(facts.EmailEnqueueFailed);
        Assert.Contains("INV-001", notice);
        Assert.Contains("DB timeout", notice);
        Assert.False(facts.IsEmpty);
    }

    [Fact]
    public void ข้ามโดยตั้งใจ_และเข้าคิวสำเร็จ_ไม่ตั้งธง()
    {
        var (skipped, n1) = EmailEnqueueOutcome.Apply(new RecurringRunFacts(), EmailEnqueueOutcome.Skipped(EmailEnqueueOutcome.CoveredByApprovedRule), "INV-1");
        Assert.False(skipped.EmailEnqueueFailed);
        Assert.Null(n1);
        var (ok, n2) = EmailEnqueueOutcome.Apply(new RecurringRunFacts(), EmailEnqueueOutcome.Enqueued(), "INV-1");
        Assert.False(ok.EmailEnqueueFailed);
        Assert.Null(n2);
    }

    // ═══ §2.4 ข้อ 3 อีเมลซ้ำ 2 กฎ ═══

    [Fact]
    public void ทั้งสองกฎ_ข้ามฝั่งรายการประจำ_กฎเดียว_เดิม()
    {
        Assert.True(EmailEnqueueOutcome.SkipForApprovedRule(approvedRuleCoversType: true, documentApproved: true));
        Assert.False(EmailEnqueueOutcome.SkipForApprovedRule(approvedRuleCoversType: false, documentApproved: true));
        Assert.False(EmailEnqueueOutcome.SkipForApprovedRule(approvedRuleCoversType: true, documentApproved: false));
    }

    // ═══ §2.4 ข้อ 2/3 ความพร้อม Autopilot ═══

    private static AutopilotReadiness.Facts Ready() => new()
    {
        DocumentType = DocumentType.Invoice, AutoApprove = true, AutoSendEmail = true, ContactId = Guid.NewGuid(), ContactUsable = true,
        ContactEmail = "ap@customer.co.th", CompanySenderVerified = true, SaleSide = true, PayLinkAttach = true,
        PayLinkAllowed = new[] { "PromptPay" }, ApprovalWarnings = Array.Empty<string>(), SystemBaseUrlConfigured = true,
    };

    [Fact]
    public void ไม่ตั้งBaseUrl_readinessไม่ใช่Full_GATEWAYเป็นWarn()
    {
        var r = AutopilotReadiness.Evaluate(Ready() with { SystemBaseUrlConfigured = false });
        Assert.NotEqual(AutopilotReadiness.LevelFull, r.Level);
        var g = Assert.Single(r.Items, i => i.Code == "GATEWAY");
        Assert.Equal(AutopilotReadiness.Warn, g.Verdict);
        Assert.Equal(AutopilotReadiness.SystemSettingsUrl, g.FixUrl);
    }

    [Fact]
    public void ตั้งBaseUrlแล้ว_ระดับเต็ม_ไม่ได้ตรวจ_ไม่ตัดสิน()
    {
        Assert.Equal(AutopilotReadiness.LevelFull, AutopilotReadiness.Evaluate(Ready()).Level);
        Assert.Equal(AutopilotReadiness.LevelFull, AutopilotReadiness.Evaluate(Ready() with { SystemBaseUrlConfigured = null }).Level);
    }

    [Fact]
    public void มีกฎทั้งสองแบบ_เตือน_EMAILRULES_ทิศตรงข้ามกฎเดียวไม่เตือน()
    {
        var both = AutopilotReadiness.Evaluate(Ready() with { ApprovedEmailRuleCovers = true });
        Assert.Equal(AutopilotReadiness.Warn, Assert.Single(both.Items, i => i.Code == "EMAIL-RULES").Verdict);
        Assert.DoesNotContain(AutopilotReadiness.Evaluate(Ready()).Items, i => i.Code == "EMAIL-RULES");
        // ไม่ได้ตั้งส่งอีเมลและไม่มีกฎรายการประจำ ⇒ ไม่มีอะไรซ้ำ
        Assert.DoesNotContain(AutopilotReadiness.Evaluate(Ready() with { AutoSendEmail = false, ApprovedEmailRuleCovers = true }).Items,
            i => i.Code == "EMAIL-RULES");
    }

    // ═══ §2.4 ข้อ 4 แจ้งเตือนที่ระบุผู้รับ ═══

    [Fact]
    public void ปิดทุกช่อง_ระบุผู้รับ_ไม่ส่ง()
        => Assert.Equal(NotificationDispatchRoute.Route.SuppressedByCompany, NotificationDispatchRoute.Decide(2, 0, hasRecipientUser: true));

    [Fact]
    public void ยังไม่ตั้ง_ระบุผู้รับ_ส่งค่าตั้งต้น_และเปิดบางช่อง_ตามค่าตั้ง()
    {
        Assert.Equal(NotificationDispatchRoute.Route.DirectToRecipient, NotificationDispatchRoute.Decide(0, 0, hasRecipientUser: true));
        Assert.Equal(NotificationDispatchRoute.Route.SystemDefaults, NotificationDispatchRoute.Decide(0, 0, hasRecipientUser: false));
        Assert.Equal(NotificationDispatchRoute.Route.CompanySettings, NotificationDispatchRoute.Decide(1, 1, hasRecipientUser: true));
        Assert.Equal(NotificationDispatchRoute.Route.SuppressedByCompany, NotificationDispatchRoute.Decide(1, 0, hasRecipientUser: false));
    }

    // ═══ §2.4 ข้อ 5 ทิศเงิน ═══

    [Fact]
    public void ทิศเงิน_ตารางครบทุกชนิดเอกสาร_ทุกฝั่ง()
    {
        var inflow = new HashSet<DocumentType> { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.BillingNote };
        foreach (var t in Enum.GetValues<DocumentType>())
        {
            if (t is DocumentType.CreditNote or DocumentType.DebitNote) continue;
            Assert.Equal(inflow.Contains(t), CashFlowDirection.IsInflow(t, null, null));
            // ชนิดอื่นไม่ดูฝั่ง/ใบต้นทาง
            Assert.Equal(inflow.Contains(t), CashFlowDirection.IsInflow(t, DocumentType.PurchaseInvoice, true));
        }
        // ใบเพิ่มหนี้: ขาย = เข้า · ซื้อ (ต้นทาง/ผู้ใช้เลือก) = ออก · ไม่รู้ = เข้า (เดิม)
        Assert.True(CashFlowDirection.IsInflow(DocumentType.DebitNote, DocumentType.Invoice, null));
        Assert.False(CashFlowDirection.IsInflow(DocumentType.DebitNote, DocumentType.PurchaseInvoice, null));
        Assert.False(CashFlowDirection.IsInflow(DocumentType.DebitNote, DocumentType.CertificateInLieu, false));   // ต้นทางชนะที่ผู้ใช้เลือก
        Assert.False(CashFlowDirection.IsInflow(DocumentType.DebitNote, null, true));
        Assert.True(CashFlowDirection.IsInflow(DocumentType.DebitNote, null, null));
        // ใบลดหนี้: ขาย = ออก (คืนลูกค้า) · ซื้อ = เข้า (ผู้ขายคืนเรา — เดิมตอบ "ออก" ทุกใบ) · ไม่รู้ = ออก (เดิม)
        Assert.False(CashFlowDirection.IsInflow(DocumentType.CreditNote, DocumentType.TaxInvoice, null));
        Assert.True(CashFlowDirection.IsInflow(DocumentType.CreditNote, DocumentType.Expense, null));
        Assert.True(CashFlowDirection.IsInflow(DocumentType.CreditNote, null, true));
        Assert.False(CashFlowDirection.IsInflow(DocumentType.CreditNote, null, null));
    }

    [Fact]
    public void ทิศเงิน_โหลดใบต้นทางเฉพาะใบลดเพิ่มหนี้ที่อ้างใบเดิม()
    {
        Assert.True(CashFlowDirection.NeedsRelatedType(DocumentType.CreditNote, Guid.NewGuid()));
        Assert.False(CashFlowDirection.NeedsRelatedType(DocumentType.CreditNote, null));
        Assert.False(CashFlowDirection.NeedsRelatedType(DocumentType.Invoice, Guid.NewGuid()));
    }

    // ═══ §2.4 ข้อ 7 รหัสประทับเวลา ETDA ═══

    [Fact]
    public void รหัสประทับเวลายาวเกิน_ปฏิเสธพร้อมเหตุ_ไม่ตัดเงียบ()
    {
        Assert.NotNull(EtaxByEmailFormat.TimestampTokenRefusal(new string('A', EtaxByEmailFormat.TimestampTokenMaxLength + 1)));
        Assert.Null(EtaxByEmailFormat.TimestampTokenRefusal(new string('A', EtaxByEmailFormat.TimestampTokenMaxLength)));
        Assert.Null(EtaxByEmailFormat.TimestampTokenRefusal(null));
    }

    // ═══ §2.4 ข้อ 8/10 สินทรัพย์ ═══

    [Fact]
    public void ชนิดที่อาจผูกต้นทุน_ถือคีย์เสมอ_ใบขายไม่ถือ()
    {
        foreach (var t in new[] { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher, DocumentType.CertificateInLieu,
                     DocumentType.CreditNote, DocumentType.DebitNote })
            Assert.True(AssetCostSourceRules.MayBeCostSource(t));
        foreach (var t in new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.Quotation })
            Assert.False(AssetCostSourceRules.MayBeCostSource(t));
    }

    [Fact]
    public void ใบที่มีทะเบียนเกินGLอยู่ก่อน_ติดปัญหา_ไม่เกิน_ไม่ติด()
    {
        Assert.Contains("PI-7", AssetCostSourceRules.PreExistingOverRegistration("PI-7", 500m, 1));
        Assert.Null(AssetCostSourceRules.PreExistingOverRegistration("PI-7", 0m, 1));
        Assert.Null(AssetCostSourceRules.PreExistingOverRegistration("CN-1", 500m, -1));   // ใบลดหนี้ — ทิศกลับ
        Assert.NotNull(AssetCostSourceRules.PurchaseDateNote(true));
        Assert.Null(AssetCostSourceRules.PurchaseDateNote(false));
    }

    // ═══ Y7/Y8/N11 การ์ด go-live ═══

    private static PlatformGoLiveChecklist.Facts AllGood() => new()
    {
        SystemBaseUrlConfigured = true, SystemBaseUrl = "https://acc.example.th", PlatformGatewayConfigured = true, TestKeyPassed = true,
        PublicCheckoutEnabled = true, SellablePlanCount = 2, PrivacyPublished = true, TermsPublished = true, RefundPublished = true,
        EnglishForAllPublished = true, ContactInfoSet = true, SystemEmailConfigured = true, SelfTestPaid = true, LiveConfirmed = true,
        OutboundCheckOk = true, OutboundCheckMessage = "HTTP 404", OutboundCheckAt = DateTime.UtcNow,
    };

    [Fact]
    public void goLive_ครบ_ไม่มีข้อแดง_ข้อส่งURLเป็นทำเอง()
    {
        var r = PlatformGoLiveChecklist.Evaluate(AllGood());
        Assert.Equal(0, r.FailCount);
        Assert.Equal(PlatformGoLiveChecklist.Manual, Assert.Single(r.Items, i => i.Code == "SEND-URLS").Status);
        Assert.Contains("https://acc.example.th/pricing.html", Assert.Single(r.Items, i => i.Code == "SEND-URLS").Why);
        Assert.Equal(11, r.Items.Count);
    }

    [Fact]
    public void goLive_ไม่ตั้งBaseUrl_แดงพร้อมปุ่มไปตั้ง_ไม่เดาโดเมน()
    {
        var r = PlatformGoLiveChecklist.Evaluate(AllGood() with { SystemBaseUrlConfigured = false, SystemBaseUrl = null });
        var b = Assert.Single(r.Items, i => i.Code == "BASEURL");
        Assert.Equal(PlatformGoLiveChecklist.Fail, b.Status);
        Assert.Equal("/admin/site-settings.html", b.FixUrl);
        Assert.DoesNotContain("http", Assert.Single(r.Items, i => i.Code == "SEND-URLS").Why);
    }

    [Fact]
    public void goLive_ขาออกล้ม_แดง_ยังไม่ทดสอบ_ไม่ใช่เขียว()
    {
        Assert.Equal(PlatformGoLiveChecklist.Fail,
            Assert.Single(PlatformGoLiveChecklist.Evaluate(AllGood() with { OutboundCheckOk = false }).Items, i => i.Code == "OUTBOUND").Status);
        Assert.Equal(PlatformGoLiveChecklist.Unknown,
            Assert.Single(PlatformGoLiveChecklist.Evaluate(AllGood() with { OutboundCheckOk = null }).Items, i => i.Code == "OUTBOUND").Status);
    }

    [Fact]
    public void ผลทดสอบขาออก_ได้คำตอบใดๆ_ผ่าน_ด่านปฏิเสธหรือต่อไม่ได้_ล้มพร้อมเหตุ()
    {
        Assert.True(PlatformGoLiveChecklist.OutboundResult(404, null, null, 120).Ok);
        var guard = PlatformGoLiveChecklist.OutboundResult(null, "ชี้ไปที่อยู่ภายใน", null, 5);
        Assert.False(guard.Ok);
        Assert.Contains("ชี้ไปที่อยู่ภายใน", guard.Message);
        var down = PlatformGoLiveChecklist.OutboundResult(null, null, "Connection refused", 5000);
        Assert.False(down.Ok);
        Assert.Contains("proxy", down.Message);
    }
}
