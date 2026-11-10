using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 257 ชุด 4 (erp-review/2026-11-10 team-A/C/E · READINESS_257 ชุด 4) — ตัวตัดสิน pure ทั้งสองทิศ:
/// รายการประจำ (สมุดรายวันห้ามสร้างล่วงหน้า · วันที่ตามปฏิทินไทย · เดือนภาษีย้าย · ลายนิ้วมือค่าตั้งต้น · ผู้ติดต่อหยุดรอคน · แผงบอกเหตุค่าตั้งต้นไม่มีผล) ·
/// โควตา/แพ็กไม่จำกัด · ถอย LINE→อีเมลเมื่อ e-Tax บังคับอีเมล · ข้อความ OA กลาง · การ์ด go-live ข้อ LINE · กระดิ่ง 429 ต่อเดือน · สลิปที่ถูกปฏิเสธ
/// </summary>
public class Round257Pack4Tests
{
    // ═══ A-H3: สมุดรายวันห้ามสร้างล่วงหน้า (คำตัดสิน 257-4-1) ═══

    [Fact]
    public void H3_สมุดรายวัน_สร้างล่วงหน้า_ปฏิเสธพร้อมเหตุ_ทิศตรงข้าม_เอกสารและ0ผ่าน()
    {
        Assert.NotNull(RecurringSchedule.JournalLeadDaysError("journal", 3));
        Assert.NotNull(RecurringSchedule.JournalLeadDaysError("Journal", 1));
        Assert.Null(RecurringSchedule.JournalLeadDaysError("journal", 0));
        Assert.Null(RecurringSchedule.JournalLeadDaysError("Document", 5));
        Assert.Null(RecurringSchedule.JournalLeadDaysError(null, 5));
    }

    // ═══ A-M9: วันที่เอกสาร/ใบสำคัญ = วันตามปฏิทินไทย (ขอบ 00:00–07:00 น.) ═══

    [Theory]
    [InlineData("2026-11-30T17:00:00Z", "2026-12-01")]   // 00:00 น. วันที่ 1 ธ.ค. (ไทย) — เดิมได้ 30 พ.ย.
    [InlineData("2026-11-30T23:59:00Z", "2026-12-01")]   // 06:59 น. วันที่ 1
    [InlineData("2026-12-01T00:00:00Z", "2026-12-01")]   // 07:00 น. วันที่ 1
    [InlineData("2026-11-30T16:59:00Z", "2026-11-30")]   // 23:59 น. วันที่ 30 พ.ย. — ไม่ถูกเลื่อนไปวันถัดไป
    public void M9_วันที่เอกสาร_ตามปฏิทินไทย_ขอบเที่ยงคืนถึงเจ็ดโมง(string nowUtc, string expected)
    {
        var now = DateTime.Parse(nowUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var d = RecurringSchedule.DocumentDateUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        Assert.Equal(DateTime.Parse(expected), d.Date);
        Assert.Equal(DateTimeKind.Utc, d.Kind);
        Assert.Equal(TimeSpan.Zero, d.TimeOfDay);
    }

    // ═══ A L13: สร้างล่วงหน้าข้ามเดือน ⇒ เดือนภาษีย้าย ═══

    [Fact]
    public void L13_สร้างล่วงหน้าข้ามเดือน_เตือนเดือนภาษี_ทิศตรงข้ามเดือนเดียวกันไม่เตือน()
    {
        var dec3 = new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc);
        var dec20 = new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(RecurringSchedule.LeadDaysMovesVatMonth(dec3, 5));
        Assert.False(RecurringSchedule.LeadDaysMovesVatMonth(dec20, 5));
        Assert.False(RecurringSchedule.LeadDaysMovesVatMonth(dec3, 0));
        Assert.True(RecurringSchedule.LeadDaysMovesVatMonth(new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc), 3));   // ข้ามปี

        var f = Ready() with { DocumentType = DocumentType.TaxInvoice, LeadDays = 5, NextPeriodDate = dec3 };
        var item = Assert.Single(AutopilotReadiness.Evaluate(f).Items, i => i.Code == "LEAD-VAT-MONTH");
        Assert.Equal(AutopilotReadiness.Warn, item.Verdict);
        Assert.DoesNotContain(AutopilotReadiness.Evaluate(f with { NextPeriodDate = dec20 }).Items, i => i.Code == "LEAD-VAT-MONTH");
        Assert.DoesNotContain(AutopilotReadiness.Evaluate(f with { DocumentType = DocumentType.Invoice }).Items, i => i.Code == "LEAD-VAT-MONTH");
    }

    // ═══ A-M4 / A-M5: ค่าตั้งต้นรอบ 256 ผูกลายนิ้วมือ · ผู้ติดต่อตั้งหยุดรอคนทับ ═══

    [Fact]
    public void M4_ลายนิ้วมือค่าตั้งต้น_ตรงปล่อย_ไม่ตรงไม่ปล่อย_ไม่เคยประทับถือว่าตรง()
    {
        var cid = Guid.NewGuid();
        var h1 = AutoApprovePolicy.BindingHash(cid, "Invoice", "{\"lines\":[{\"quantity\":1,\"unitPrice\":5000}]}", true, false);
        var h2 = AutoApprovePolicy.BindingHash(cid, "Invoice", "{\"lines\":[{\"quantity\":1,\"unitPrice\":900000}]}", true, false);
        Assert.True(AutoApprovePolicy.MatchesDefaultBinding(h1, h1));
        Assert.False(AutoApprovePolicy.MatchesDefaultBinding(h1, h2));
        Assert.True(AutoApprovePolicy.MatchesDefaultBinding(null, h2));   // รายการก่อนรอบ 257 — คงคำตัดสินรอบ 256
    }

    [Fact]
    public void M5_ผู้ติดต่อตั้งหยุดรอคน_ทับค่าตั้งต้น_ทิศตรงข้าม_ไม่ตั้งหรือปล่อย_ไม่ทับ()
    {
        Assert.True(AutoApprovePolicy.StopsRecurringDefault(AutoApprovePolicy.Create(Array.Empty<string>(), "u", DateTime.UtcNow)));
        Assert.False(AutoApprovePolicy.StopsRecurringDefault(null));
        Assert.False(AutoApprovePolicy.StopsRecurringDefault(AutoApprovePolicy.Create(new[] { ArOverdueApprovalWarning.Code }, "u", DateTime.UtcNow)));
        // JSON ที่หน้าเขียนจริง (SetContactPolicyAsync · passCodes ว่าง) อ่านกลับแล้วยังเป็น "หยุดรอคน"
        var json = AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(Array.Empty<string>(), "u", DateTime.UtcNow));
        Assert.True(AutoApprovePolicy.StopsRecurringDefault(AutoApprovePolicy.Parse(json, AutoApprovePolicy.ScopeContact)));
    }

    // ═══ A-M6: แผงบอกเหตุเมื่อค่าตั้งต้นไม่มีผล ═══

    [Fact]
    public void M6_ค่าตั้งต้นไม่มีผล_แผงบอกเหตุ_ทิศตรงข้าม_มีผลไม่ขึ้นข้อ()
    {
        var gone = Ready() with { Policy = null, PolicySetterActive = false, PolicyInactiveReason = RecurringApprovePolicyLoader.DefaultSetterGoneReason };
        var item = Assert.Single(AutopilotReadiness.Evaluate(gone).Items, i => i.Code == "OWNER-POLICY");
        Assert.Equal(RecurringApprovePolicyLoader.DefaultSetterGoneReason, item.Why);
        Assert.Equal(AutopilotReadiness.PolicyStepUrl, item.FixUrl);
        var changed = gone with { PolicyInactiveReason = RecurringApprovePolicyLoader.BindingChangedReason };
        Assert.Equal(RecurringApprovePolicyLoader.BindingChangedReason, Assert.Single(AutopilotReadiness.Evaluate(changed).Items, i => i.Code == "OWNER-POLICY").Why);
        Assert.DoesNotContain(AutopilotReadiness.Evaluate(gone with { PolicySetterActive = true, PolicyInactiveReason = null }).Items, i => i.Code == "OWNER-POLICY");
        Assert.DoesNotContain(AutopilotReadiness.Evaluate(gone with { AutoApprove = false }).Items, i => i.Code == "OWNER-POLICY");
    }

    [Fact]
    public void H2_ข้อความค่าเริ่มต้น_ไม่บอกว่าไม่ปล่อย()
    {
        Assert.DoesNotContain("ไม่ปล่อยคำเตือนใด", RecurringApprovePolicyLoader.DefaultConfirmedMessage);
        Assert.Contains("ลูกค้าค้างชำระ", RecurringApprovePolicyLoader.DefaultConfirmedMessage);
        Assert.Contains(ArOverdueApprovalWarning.Code, AutoApprovePolicy.RecurringDefault(null).PassCodes);
        Assert.Contains("หยุดรอคน", RecurringApprovePolicyLoader.DefaultChangeNotice);
    }

    // ═══ C-M2: เพดานไม่จำกัด ⇒ แพ็กขายไม่ได้ (ก้อนรวม + ช่อง) ═══

    [Fact]
    public void CM2_ไม่จำกัด_ไม่ขายแพ็ก_ทิศตรงข้าม_เพดานจริงขายได้()
    {
        Assert.False(NotifyQuotaPolicy.CombinedPackSellable(NotifyQuotaPolicy.Unlimited));
        Assert.False(NotifyQuotaPolicy.CombinedPackSellable(-5));
        Assert.True(NotifyQuotaPolicy.CombinedPackSellable(0));
        Assert.True(NotifyQuotaPolicy.CombinedPackSellable(500));
        Assert.False(NotifyQuotaPolicy.ChannelPackSellable(NotifyQuotaPolicy.Unlimited));
        Assert.False(NotifyQuotaPolicy.ChannelPackSellable(null));
        Assert.True(NotifyQuotaPolicy.ChannelPackSellable(300));
        Assert.Contains("ยังไม่ได้คิดเงิน", NotifyQuotaPolicy.UnlimitedPackNotSellableMessage(NotifyQuotaPolicy.ChannelAll));
        Assert.Contains("LINE", NotifyQuotaPolicy.UnlimitedPackNotSellableMessage(NotifyQuotaPolicy.ChannelLine));
    }

    // ═══ C-M3: e-Tax บังคับอีเมล ⇒ แถว LINE ไม่ถอยไปอีเมล ═══

    [Fact]
    public void CM3_eTaxบังคับอีเมล_แถวLINEไม่ถอย_ทิศตรงข้าม_ไม่บังคับยังถอย()
    {
        Assert.False(ContactLineChannel.LineFallbackAtSend(channelEmail: false, lineFallbackToEmail: true, etaxForcesEmail: true));
        Assert.True(ContactLineChannel.LineFallbackAtSend(channelEmail: false, lineFallbackToEmail: true, etaxForcesEmail: false));
        Assert.False(ContactLineChannel.LineFallbackAtSend(channelEmail: true, lineFallbackToEmail: true, etaxForcesEmail: false));
        Assert.False(ContactLineChannel.LineFallbackAtSend(channelEmail: false, lineFallbackToEmail: false, etaxForcesEmail: false));
        Assert.True(ContactLineChannel.LineFallbackMayDuplicate(false, true));
        Assert.False(ContactLineChannel.LineFallbackMayDuplicate(true, true));
    }

    // ═══ C-M4 / C-M5: ข้อความ OA กลางตามบริษัทผู้ให้บริการ ═══

    [Fact]
    public void CM4_รหัสจากOAกลาง_บอกชื่อบริษัทผู้ให้บริการ_และทางของบริษัทอื่น()
    {
        var r = LineGroupBindRule.Replies.CentralCode("ABCDEFGH", "กลุ่มบัญชี", DateTime.UtcNow, "บริษัท ผู้ให้บริการ จำกัด");
        Assert.Contains("GB-ABCDEFGH", r);
        Assert.Contains("บริษัท ผู้ให้บริการ จำกัด", r);
        Assert.Contains("บริษัทอื่น", r);
        Assert.Contains("LINE OA ของบริษัทตัวเอง", r);
        // ทิศตรงข้าม: รหัสของ OA บริษัทไม่มีประโยคเรื่อง OA กลาง
        Assert.DoesNotContain("บริษัทอื่น", LineGroupBindRule.Replies.Code("ABCDEFGH", "กลุ่ม", DateTime.UtcNow));
    }

    [Fact]
    public void CM5_ประโยคOAกลาง_ตามNextAccGroupOpen()
    {
        Assert.Contains("เปิดแล้ว", ContactLineChannel.CentralOaSentence(true));
        Assert.Contains("ยังไม่เปิด", ContactLineChannel.CentralOaSentence(false));
        Assert.Contains("ยังไม่เปิด", ContactRecipientResolver.DeliveryNoteFor(false));
        Assert.Contains("เปิดแล้วสำหรับบริษัทนี้", ContactRecipientResolver.DeliveryNoteFor(true));
        Assert.Contains("LINE OA กลางของ NextAcc", ContactLineChannel.ChannelLabels(true).LineGroup);
        Assert.DoesNotContain("LINE OA กลาง", ContactLineChannel.ChannelLabels(false).LineGroup);
    }

    // ═══ C LOW: กระดิ่ง 429 ครั้งเดียวต่อเดือน (ขอบเดือนตามปฏิทินไทย) ═══

    [Fact]
    public void C_LOW_ขอบเดือนกันซ้ำ_ตามปฏิทินไทย()
    {
        Assert.Equal(new DateTime(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc),
            LinePushOutcome.AlertMonthStartUtc(new DateTime(2026, 11, 30, 18, 0, 0, DateTimeKind.Utc)));   // 01:00 น. 1 ธ.ค. ไทย
        Assert.Equal(new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc),
            LinePushOutcome.AlertMonthStartUtc(new DateTime(2026, 11, 30, 16, 0, 0, DateTimeKind.Utc)));   // 23:00 น. 30 พ.ย. ไทย
    }

    // ═══ A-M8: สลิปที่ถูกปฏิเสธ/หมดอายุไม่นับเป็นสลิปซ้ำ ═══

    [Fact]
    public void AM8_สถานะที่นับซ้ำ_รอตรวจและรับเงินแล้ว_ไม่นับปฏิเสธหมดอายุ()
    {
        Assert.Contains(PaymentIntentStatus.Pending, SlipEvidenceRule.DuplicateStatuses);
        Assert.Contains(PaymentIntentStatus.Created, SlipEvidenceRule.DuplicateStatuses);
        Assert.Contains(PaymentIntentStatus.Succeeded, SlipEvidenceRule.DuplicateStatuses);
        Assert.DoesNotContain(PaymentIntentStatus.Failed, SlipEvidenceRule.DuplicateStatuses);
        Assert.DoesNotContain(PaymentIntentStatus.Expired, SlipEvidenceRule.DuplicateStatuses);
        Assert.Contains("1,070.00", SlipEvidenceRule.PayLinkSlipAmountNote(1070m));
        Assert.Contains("14", BankReceiptAutoSettle.EnableNote(14));
    }

    // ═══ E-H1: การ์ด go-live มีข้อ LINE OA กลาง ═══

    private static PlatformGoLiveChecklist.Facts GoLiveAllGood() => new()
    {
        SystemBaseUrlConfigured = true, SystemBaseUrl = "https://acc.example.th", PlatformGatewayConfigured = true, TestKeyPassed = true,
        PublicCheckoutEnabled = true, SellablePlanCount = 1, PrivacyPublished = true, TermsPublished = true, RefundPublished = true,
        EnglishForAllPublished = true, ContactInfoSet = true, SystemEmailConfigured = true, SelfTestPaid = true, LiveConfirmed = true,
        OutboundCheckOk = true, PlatformCompanySet = true, PlatformCompanyName = "บริษัทผู้ให้บริการ",
        CentralOaHasToken = true, CentralOaHasSecret = true, CentralOaHasBasicId = true,
    };

    [Fact]
    public void EH1_goLive_ข้อLINE_ครบผ่าน_ขาดแดง_webhookและเข้ากลุ่มเป็นทำเอง()
    {
        var ok = PlatformGoLiveChecklist.Evaluate(GoLiveAllGood());
        Assert.Equal(0, ok.FailCount);
        Assert.Equal(PlatformGoLiveChecklist.CardTitle, ok.Title);
        Assert.Equal(PlatformGoLiveChecklist.Ok, Assert.Single(ok.Items, i => i.Code == "LINE-PLATFORM-COMPANY").Status);
        Assert.Equal(PlatformGoLiveChecklist.Ok, Assert.Single(ok.Items, i => i.Code == "LINE-CENTRAL-OA").Status);
        var hook = Assert.Single(ok.Items, i => i.Code == "LINE-WEBHOOK");
        Assert.Equal(PlatformGoLiveChecklist.Manual, hook.Status);
        Assert.Contains("https://acc.example.th/api/line-webhook", hook.Why);
        Assert.Equal(PlatformGoLiveChecklist.Manual, Assert.Single(ok.Items, i => i.Code == "LINE-JOIN-GROUPS").Status);

        var bad = PlatformGoLiveChecklist.Evaluate(GoLiveAllGood() with { PlatformCompanySet = false, PlatformCompanyName = null, CentralOaHasBasicId = false });
        Assert.Equal(PlatformGoLiveChecklist.Fail, Assert.Single(bad.Items, i => i.Code == "LINE-PLATFORM-COMPANY").Status);
        var oa = Assert.Single(bad.Items, i => i.Code == "LINE-CENTRAL-OA");
        Assert.Equal(PlatformGoLiveChecklist.Fail, oa.Status);
        Assert.Contains("Basic ID", oa.Title);
        Assert.DoesNotContain("Channel Secret", oa.Title);
        Assert.Equal(2, bad.FailCount);
    }

    private static AutopilotReadiness.Facts Ready() => new()
    {
        DocumentType = DocumentType.Invoice,
        AutoApprove = true,
        ContactId = Guid.NewGuid(),
        ContactUsable = true,
        ContactEmail = "ap@customer.co.th",
        CompanySenderVerified = true,
        SaleSide = true,
        ApprovalWarnings = Array.Empty<string>(),
    };
}
