using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 248 (AUTOPILOT_BILLING_DESIGN §4.3–§4.5 · §5.1) — ตัวตัดสิน pure ของรายการประจำแบบ Autopilot:
/// นโยบายอนุมัติข้ามคำเตือน (<see cref="AutoApprovePolicy"/> + <see cref="ApprovalAckSource.UnattendedWithPolicy"/>) · ป้าย <c>[AR-OVERDUE]</c> ·
/// ช่องทางรับเงินเป็นชั้น (<see cref="AutopilotSettingsResolver"/>) · แผงความพร้อม (<see cref="AutopilotReadiness"/>) ·
/// ทุกข้อล็อกทั้งทิศที่เปิดและทิศตรงข้าม (ไม่ตั้งนโยบาย ⇒ พฤติกรรมรอบ 247 · ชุดห้ามปล่อยหยุดเสมอ)
/// </summary>
public class AutopilotRound248Tests
{
    private static readonly string Ar = ArOverdueApprovalWarning.Message(2, 9750m);
    private static readonly AutoApprovePolicy ReleaseAr = AutoApprovePolicy.Create(new[] { "AR-OVERDUE" }, "u1", new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc));

    // ═══ [AR-OVERDUE] ═══

    [Fact]
    public void R248_ArOverdue_มีป้ายรหัส_และข้อความเดิม()
    {
        Assert.StartsWith("[AR-OVERDUE] ", Ar);
        Assert.Contains("ผู้ติดต่อนี้ยังค้างชำระ 2 ใบ รวม 9,750.00 THB", Ar);
        Assert.True(ArOverdueApprovalWarning.IsApprovalWarning(Ar));
        Assert.False(ArOverdueApprovalWarning.IsApprovalWarning("ผู้ติดต่อนี้ยังค้างชำระ 2 ใบ"));   // ข้อความล้วน = ไม่ใช่ป้าย (ห้าม match ข้อความไทย)
        Assert.Equal("AR-OVERDUE", AutoApprovePolicy.CodeOf(Ar));
    }

    // ═══ ApprovalAcknowledgement + นโยบาย ═══

    [Fact]
    public void R248_ไม่ตั้งนโยบาย_ใบประจำยังหยุดเพราะค้างชำระ_เหมือนรอบ247()
    {
        var w = new[] { Ar };
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, w));
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, w));
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, w, null));
        // นโยบายที่ตั้งไว้แต่ไม่ปล่อยอะไร (หยุดรอคน) — ยังหยุด
        var stop = AutoApprovePolicy.Create(Array.Empty<string>(), "u1", DateTime.UtcNow);
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, w, stop));
    }

    [Fact]
    public void R248_นโยบายปล่อยARค้างชำระ_อนุมัติต่อได้_รหัสกฎAPPROVE_UNATTENDED_POLICY()
    {
        var w = new[] { Ar };
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, w, ReleaseAr));
        Assert.Equal(new[] { Ar }, AutoApprovePolicy.Released(ReleaseAr, w));
        Assert.Equal("APPROVE-UNATTENDED-POLICY", ApprovalAcknowledgement.RuleCode(ApprovalAckSource.UnattendedWithPolicy));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.UnattendedWithPolicy));   // ไม่ใช่การรับทราบของคน
        Assert.Equal(ArOverdueApprovalWarning.LegalReference, ApprovalAcknowledgement.LegalReference(w));   // ไม่อ้าง §86 ที่ไม่เกี่ยว
        Assert.Contains("นโยบาย", ApprovalAcknowledgement.Note(ApprovalAckSource.UnattendedWithPolicy, w, "system", DateTime.UtcNow));
    }

    [Fact]
    public void R248_ทิศตรงข้าม_นโยบายไม่ปล่อยคำเตือนที่ไม่มีป้าย_ผู้ซื้อ86_4_ยังหยุด()
    {
        const string buyer = "ใบกำกับภาษีต้องระบุที่อยู่ผู้ซื้อ ('บจก.ทดสอบ' ยังไม่มีที่อยู่) — ตามมาตรา 86/4";
        var w = new[] { Ar, buyer };
        Assert.Equal(new[] { buyer }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, w, ReleaseAr));
    }

    [Fact]
    public void R248_ทิศตรงข้าม_SigmaGap_และVATไม่อยู่บนกระดาษ_ไม่มีวันถูกปล่อย()
    {
        var gap = OcrApprovalGapWarning.Prefix + " [AR-OVERDUE] ยอดรวม ต่าง 10.00";
        var vat = OcrApprovalGapWarning.VatDerivedPrefix + " — ระบบถอด VAT เอง";
        Assert.True(AutoApprovePolicy.NeverPassable(gap));
        Assert.True(AutoApprovePolicy.NeverPassable(vat));
        Assert.Null(AutoApprovePolicy.CodeOf(gap));
        var left = ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, new[] { gap, vat }, ReleaseAr);
        Assert.Equal(2, left.Count);
    }

    [Fact]
    public void R248_ทิศตรงข้าม_ทางเข้าอื่นไม่ได้สิทธิ์จากนโยบายของใบประจำ()
    {
        var w = new[] { Ar };
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, w, ReleaseAr));
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, w, ReleaseAr));
        Assert.Equal(w, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, w, ReleaseAr));
    }

    // ═══ AutoApprovePolicy: ตรวจ · ชั้น · JSON ═══

    [Theory]
    [InlineData("BUYER-86/4")]
    [InlineData("Σ-GAP")]
    [InlineData("VAT-NOT-ON-PAPER")]
    [InlineData("APPROVAL-LIMIT")]
    [InlineData("STOCK-NEG")]
    public void R248_ตั้งนโยบายปล่อยรหัสห้ามปล่อย_หรือไม่รู้จัก_ถูกปฏิเสธพร้อมเหตุ(string code)
    {
        var why = AutoApprovePolicy.Validate(new[] { "AR-OVERDUE", code });
        Assert.NotNull(why);
        Assert.Contains(code == "STOCK-NEG" ? "ไม่รู้จักรหัส" : "ไม่ได้", why);
    }

    [Fact]
    public void R248_ตั้งนโยบายARได้_ชั้นรายการชนะผู้ติดต่อ_รายการตั้งว่างทับผู้ติดต่อ()
    {
        Assert.Null(AutoApprovePolicy.Validate(new[] { "ar-overdue" }));
        var contactJson = AutoApprovePolicy.Serialize(ReleaseAr);
        var stopJson = AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(Array.Empty<string>(), "u2", DateTime.UtcNow));
        // ฝ่ายค้าน HIGH/MED-2: นโยบายผู้ติดต่อไม่มีผลกับรายการที่ไม่มีใบรับรอง · รับรองแล้ว = ใช้ของผู้ติดต่อ
        Assert.Null(AutoApprovePolicy.Effective(null, contactJson));
        var certJson = AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(Array.Empty<string>(), "u3", DateTime.UtcNow, "H", inheritContact: true));
        Assert.Equal(AutoApprovePolicy.ScopeContact, AutoApprovePolicy.Effective(certJson, contactJson)!.Scope);
        Assert.Null(AutoApprovePolicy.Effective(certJson, null));
        var eff = AutoApprovePolicy.Effective(stopJson, contactJson)!;
        Assert.Equal(AutoApprovePolicy.ScopeRecurring, eff.Scope);
        Assert.Empty(eff.PassCodes);
        Assert.Null(AutoApprovePolicy.Effective(null, null));
        Assert.Null(AutoApprovePolicy.Parse("{ไม่ใช่ json"));
        // รหัสที่ไม่รู้จักใน JSON เก่า/แก้มือ ⇒ ตัดทิ้ง (ไม่ปล่อย)
        Assert.Empty(AutoApprovePolicy.Parse("{\"passCodes\":[\"BUYER-86/4\"],\"setBy\":\"x\"}")!.PassCodes);
    }

    private const string Tpl = "{\"lines\":[{\"description\":\"ค่าบริการ <<month>>\",\"quantity\":1,\"unitPrice\":5000,\"vatRate\":7}]}";

    [Fact]
    public void R248_ฝ่ายค้าน_ลายนิ้วมือผูกยอด_ผู้ติดต่อ_ชนิด_ธงอัตโนมัติ_ไม่ผูกคำอธิบาย()
    {
        var c = Guid.NewGuid();
        var h = AutoApprovePolicy.BindingHash(c, "Invoice", Tpl, true, false);
        Assert.Equal(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("ค่าบริการ <<month>>", "ค่าบริการรายเดือน"), true, false));   // คำอธิบายไม่นับ
        Assert.Equal(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("5000", "5000.00"), true, false));   // รูปตัวเลขเดียวกัน
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("5000", "900000"), true, false));   // แก้ยอด
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(Guid.NewGuid(), "Invoice", Tpl, true, false));            // เปลี่ยนลูกค้า
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(c, "TaxInvoice", Tpl, true, false));                       // เปลี่ยนชนิด
        Assert.NotEqual(h, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl, true, true));                           // เปิดส่งอีเมล
        var bound = AutoApprovePolicy.Create(new[] { ArOverdueApprovalWarning.Code }, "u1", DateTime.UtcNow, h);
        Assert.True(AutoApprovePolicy.MatchesBinding(bound, h));
        Assert.False(AutoApprovePolicy.MatchesBinding(bound, AutoApprovePolicy.BindingHash(c, "Invoice", Tpl.Replace("5000", "900000"), true, false)));
        Assert.False(AutoApprovePolicy.MatchesBinding(ReleaseAr, h));   // ไม่มีลายนิ้วมือ = ไม่ตรง (ระวังไว้ก่อน)
        Assert.Equal(h, AutoApprovePolicy.Parse(AutoApprovePolicy.Serialize(bound))!.BindingHashValue);
    }

    [Fact]
    public void R248_ฝ่ายค้าน_MED3_นโยบายไม่ได้ปล่อยอะไร_ร่องรอยเป็นUnattendedเดิม()
    {
        var src = ApprovalAcknowledgement.EffectiveSource(ApprovalAckSource.UnattendedWithPolicy, 0);
        Assert.Equal(ApprovalAckSource.Unattended, src);
        Assert.Equal("APPROVE-UNATTENDED-PASSED-S65", ApprovalAcknowledgement.RuleCode(src));
        Assert.Equal(ApprovalAckSource.UnattendedWithPolicy, ApprovalAcknowledgement.EffectiveSource(ApprovalAckSource.UnattendedWithPolicy, 1));
        Assert.Equal(ApprovalAckSource.User, ApprovalAcknowledgement.EffectiveSource(ApprovalAckSource.User, 0));
    }

    [Fact]
    public void R248_ฝ่ายค้าน_LOW7_ชื่อผู้ติดต่อมีป้ายAR_อยู่กลางคำเตือน_ไม่ถูกปล่อย()
    {
        const string spoof = "ใบกำกับภาษีต้องระบุที่อยู่ผู้ซื้อ ('[AR-OVERDUE] บจก.ปลอม' ยังไม่มีที่อยู่) — ตามมาตรา 86/4";
        Assert.Null(AutoApprovePolicy.CodeOf(spoof));
        Assert.Equal(new[] { spoof }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, new[] { spoof }, ReleaseAr));
    }

    [Fact]
    public void R248_ฝ่ายค้าน_MED5_รหัสรายการจากกุญแจคิว_และชั้นรายการทับเฉพาะช่องว่าง()
    {
        var rid = Guid.NewGuid();
        Assert.Equal(rid, ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey($"doc:{Guid.NewGuid()}:RecurringInvoiceCreated:recurring-{rid}"));
        Assert.Equal(rid, ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey($"doc:{Guid.NewGuid()}:RecurringInvoiceCreated:autosend-{rid}"));
        Assert.Null(ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey($"doc:{Guid.NewGuid()}:DocumentApproved:approved"));
        Assert.Null(ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey(null));
        var transfer = AutopilotSettingsResolver.FromMode("TransferOnly", null, Available);
        var rule = new Accounting.Models.Entities.EmailScheduleRule { Trigger = "RecurringInvoiceCreated" };
        Assert.False(Accounting.Services.Implementations.EmailScheduleService.WithRecurringPayLayer(rule, transfer).AttachPayLink);
        var ruleSet = new Accounting.Models.Entities.EmailScheduleRule { Trigger = "RecurringInvoiceCreated", AttachPayLink = true };
        Assert.True(Accounting.Services.Implementations.EmailScheduleService.WithRecurringPayLayer(ruleSet, transfer).AttachPayLink);   // ตารางตั้งเอง = ชนะ
    }

    // ═══ AutopilotSettingsResolver ═══

    private static readonly IReadOnlyList<string> Available = new[] { "PromptPay", "Card", DocumentPayLinkPolicy.BankTransferCode };

    [Fact]
    public void R248_ไม่ตั้งรายการและผู้ติดต่อ_ผลเท่าค่าบริษัท_ทิศตรงข้ามInherit()
    {
        var inherit = AutopilotSettingsResolver.FromMode("Inherit", null, Available);
        var c = AutopilotSettingsResolver.Resolve(null, inherit, null, companyAttach: true, new[] { "Card" });
        Assert.True(c.Attach);
        Assert.Equal(new[] { "Card" }, c.Channels);
        Assert.Equal("บริษัท", c.Source);
        var none = AutopilotSettingsResolver.Resolve(null, null, null, companyAttach: false, null);
        Assert.False(none.Attach);
        Assert.Null(none.Channels);
    }

    [Fact]
    public void R248_TransferOnly_ไม่แนบลิงก์บัตร_Online_ตัดโอนออก()
    {
        var transfer = AutopilotSettingsResolver.FromMode("TransferOnly", null, Available);
        var c = AutopilotSettingsResolver.Resolve(null, transfer, null, companyAttach: true, null);
        Assert.False(c.Attach);
        Assert.Equal("รายการประจำ", c.Source);
        var online = AutopilotSettingsResolver.FromMode("online", null, Available);
        Assert.Equal(new[] { "PromptPay", "Card" }, online.Channels);
        Assert.True(online.Attach);
    }

    [Fact]
    public void R248_ชั้น_ตารางส่ง_รายการ_ผู้ติดต่อ_บริษัท()
    {
        var contactNone = AutopilotSettingsResolver.FromMode("None", null, Available);
        var recurringBoth = AutopilotSettingsResolver.FromMode("Both", new[] { "PromptPay" }, Available);
        var r = AutopilotSettingsResolver.Resolve(null, recurringBoth, contactNone, companyAttach: false, null);
        Assert.True(r.Attach);
        Assert.Equal(new[] { "PromptPay" }, r.Channels);
        // ผู้ติดต่อปิด + รายการไม่ตั้ง ⇒ ผู้ติดต่อชนะบริษัท
        Assert.False(AutopilotSettingsResolver.Resolve(null, null, contactNone, companyAttach: true, null).Attach);
        // ตารางส่งตั้งเอง ⇒ ชนะทุกชั้น
        var sched = new AutopilotSettingsResolver.Layer(true, new[] { "Card" });
        var s = AutopilotSettingsResolver.Resolve(sched, AutopilotSettingsResolver.FromMode("None", null, Available), contactNone, false, null);
        Assert.True(s.Attach);
        Assert.Equal(new[] { "Card" }, s.Channels);
    }

    [Fact]
    public void R248_โหมดและช่องทางที่ไม่รู้จัก_ถูกปฏิเสธ()
    {
        Assert.NotNull(AutopilotSettingsResolver.ValidateMode("Cheque"));
        Assert.Null(AutopilotSettingsResolver.ValidateMode(null));
        Assert.Null(AutopilotSettingsResolver.ValidateMode("transferonly"));
        Assert.NotNull(AutopilotSettingsResolver.ValidateChannels(new[] { "Bitcoin" }));
        Assert.Null(AutopilotSettingsResolver.ValidateChannels(new[] { "PromptPay", "ManualSlip" }));
        Assert.Null(AutopilotSettingsResolver.Normalize("Inherit"));
    }

    // ═══ AutopilotReadiness ═══

    private static AutopilotReadiness.Facts Ready() => new()
    {
        DocumentType = DocumentType.Invoice,
        AutoApprove = true,
        AutoSendEmail = true,
        ContactId = Guid.NewGuid(),
        ContactUsable = true,
        ContactEmail = "ap@customer.co.th",
        CompanySenderVerified = true,
        SaleSide = true,
        PayLinkAttach = true,
        PayLinkAllowed = new[] { "PromptPay" },
        ApprovalWarnings = Array.Empty<string>(),
    };

    [Fact]
    public void R248_ครบทุกข้อ_ระดับAutopilotเต็ม()
    {
        var r = AutopilotReadiness.Evaluate(Ready());
        Assert.Equal(AutopilotReadiness.LevelFull, r.Level);
        Assert.Equal(0, r.FailCount);
    }

    [Fact]
    public void R248_ผู้ซื้อขาดสาขา_BUYERTAX_Fail_ทิศตรงข้ามบุคคลธรรมดาไม่ฟ้อง()
    {
        var f = Ready() with { BuyerBlockingFields = new[] { "รหัสสาขาผู้ซื้อ (5 หลัก)" } };
        var item = Assert.Single(AutopilotReadiness.Evaluate(f).Items, i => i.Code == "BUYER-TAX");
        Assert.Equal(AutopilotReadiness.Fail, item.Verdict);
        Assert.StartsWith("/pages/contacts.html?id=", item.FixUrl);
        // บุคคลธรรมดา ⇒ BuyerBlockingFields = null (ตัวตัดสิน BuyerBlockingFields ไม่บล็อก) ⇒ ไม่ฟ้อง
        var ok = Assert.Single(AutopilotReadiness.Evaluate(Ready()).Items, i => i.Code == "BUYER-TAX");
        Assert.Equal(AutopilotReadiness.Ok, ok.Verdict);
    }

    [Fact]
    public void R248_ติดค้างชำระ_APPROVE_Fail_ชี้ไปตั้งนโยบาย_ระดับสร้างร่างเท่านั้น()
    {
        var f = Ready() with { ApprovalWarnings = new[] { Ar }, ApprovalBlocking = new[] { Ar } };
        var r = AutopilotReadiness.Evaluate(f);
        Assert.Equal(AutopilotReadiness.LevelDraftOnly, r.Level);
        var item = Assert.Single(r.Items, i => i.Code == "APPROVE");
        Assert.Equal(AutopilotReadiness.Fail, item.Verdict);
        Assert.Equal(AutopilotReadiness.PolicyStepUrl, item.FixUrl);
        // นโยบายปล่อยแล้ว ⇒ ผ่าน (Ok) พร้อมบอกว่าปล่อย
        var released = AutopilotReadiness.Evaluate(Ready() with { ApprovalWarnings = new[] { Ar }, ApprovalReleased = new[] { Ar }, Policy = ReleaseAr, PolicySetterActive = true });
        Assert.Equal(AutopilotReadiness.Ok, Assert.Single(released.Items, i => i.Code == "APPROVE").Verdict);
        Assert.Equal(AutopilotReadiness.LevelFull, released.Level);
    }

    [Fact]
    public void R248_ผู้ตั้งนโยบายออกจากบริษัท_OWNERPOLICY_Fail()
    {
        var f = Ready() with { ApprovalWarnings = new[] { Ar }, ApprovalBlocking = new[] { Ar }, Policy = ReleaseAr, PolicySetterActive = false };
        var r = AutopilotReadiness.Evaluate(f);
        Assert.Contains(r.Items, i => i.Code == "OWNER-POLICY" && i.Verdict == AutopilotReadiness.Fail);
        Assert.Equal(AutopilotReadiness.LevelDraftOnly, r.Level);
    }

    [Fact]
    public void R248_ไม่มีอีเมล_RECIPIENT_Fail_TransferOnly_บอกว่าไม่แนบลิงก์()
    {
        var f = Ready() with { ContactEmail = null, PayLinkAttach = false, PaymentMode = AutopilotSettingsResolver.TransferOnly };
        var r = AutopilotReadiness.Evaluate(f);
        Assert.Contains(r.Items, i => i.Code == "RECIPIENT" && i.Verdict == AutopilotReadiness.Fail);
        Assert.Contains(r.Items, i => i.Code == "GATEWAY" && i.Verdict == AutopilotReadiness.Info && i.Title.Contains("โอนเข้าบัญชี"));
        Assert.Equal(AutopilotReadiness.LevelPartial, r.Level);
    }

    [Fact]
    public void R248_ไม่เปิดอนุมัติอัตโนมัติ_ระดับสร้างร่างเท่านั้น()
    {
        var r = AutopilotReadiness.Evaluate(Ready() with { AutoApprove = false, AutoSendEmail = false });
        Assert.Equal(AutopilotReadiness.LevelDraftOnly, r.Level);
    }

    // ═══ RecurringRunOutcome: ป้ายรหัสไม่โผล่ในหัวเรื่อง + ชี้ทางตั้งนโยบาย ═══

    [Fact]
    public void R248_ผลรอบ_ค้างชำระมีป้าย_หัวเรื่องไม่มีป้าย_บอกทางตั้งนโยบาย()
    {
        var facts = new RecurringRunFacts { ApproveBlockedReason = "x", ApproveWarnings = new[] { Ar } };
        var doc = new RecurringRunDoc(Guid.NewGuid(), "DRAFT-1", DocumentStatus.Draft, DocumentType.Invoice, DateTime.UtcNow);
        var v = RecurringRunOutcome.Build(facts, null, doc, Guid.NewGuid(), "a@b.co", false, true)!;
        Assert.DoesNotContain("[AR-OVERDUE]", v.Headline);
        Assert.Contains("ผู้ติดต่อนี้ยังค้างชำระ 2 ใบ", v.Headline);
        Assert.Contains("นโยบายอนุมัติ", v.AutoApproveNote);
    }

    // ═══ server computes · page displays ═══

    [Fact]
    public void R248_หน้ารายการประจำไม่มีรหัสกติกาของตัวเอง()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "wwwroot", "pages", "recurring.html"));
        foreach (var code in new[] { "AR-OVERDUE", "BUYER-86/4", "VAT-NOT-ON-PAPER", "APPROVAL-LIMIT", "TransferOnly" })
            Assert.DoesNotContain(code, html);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
