using Accounting.Helpers;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 251 (AUDIT_249 §WP-07 · AUTOPILOT_BILLING_DESIGN §4.4–§4.6) — รหัสความพร้อมใหม่ของ <see cref="AutopilotReadiness"/>:
/// <c>ETAX</c>/<c>ETAX-SENDER</c>/<c>ETAX-RECIPIENT</c> · <c>PAY-QR</c> · <c>QUOTA</c> · <c>PERIOD</c> · <c>POSTPAY</c> — ทุกรหัสล็อกทั้ง "ฟ้อง" และทิศตรงข้าม "ผ่าน/ไม่ออกข้อ" ·
/// ข้อเท็จจริงที่ไม่ได้ตรวจ (null) ⇒ ไม่ออกข้อ (ผู้เรียกเดิม/ระดับเดิมไม่ถดถอย) · ตัวช่วย <see cref="ClosedPeriodRanges.Covers"/> · <see cref="RecurringTestEmail"/>
/// </summary>
public class AutopilotRound251Wp07Tests
{
    private static AutopilotReadiness.Facts Ready() => new()
    {
        DocumentType = DocumentType.TaxInvoice,
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

    private static AutopilotReadiness.Item One(AutopilotReadiness.Facts f, string code)
        => Assert.Single(AutopilotReadiness.Evaluate(f).Items, i => i.Code == code);

    private static void None(AutopilotReadiness.Facts f, string code)
        => Assert.DoesNotContain(AutopilotReadiness.Evaluate(f).Items, i => i.Code == code);

    // ═══ ไม่ถดถอย: ข้อเท็จจริงใหม่ไม่ได้ตรวจ ⇒ ไม่มีข้อใหม่ · ระดับเต็มเหมือนเดิม ═══

    [Fact]
    public void R251_ไม่ได้ตรวจข้อใหม่_ระดับเต็มเหมือนเดิม_ไม่มีรหัสใหม่()
    {
        var r = AutopilotReadiness.Evaluate(Ready());
        Assert.Equal(AutopilotReadiness.LevelFull, r.Level);
        foreach (var code in new[] { "ETAX", "ETAX-SENDER", "ETAX-RECIPIENT", "PAY-QR", "QUOTA", "PERIOD", "POSTPAY" })
            Assert.DoesNotContain(r.Items, i => i.Code == code);
    }

    // ═══ ETAX-SENDER ═══

    [Fact]
    public void R251_ETAX_SENDER_ด่านผู้ส่งปฏิเสธ_Fail_ไม่ใช่ระดับเต็ม_ไม่มีข้อSMTPที่บอกว่าส่งผ่านNextAcc()
    {
        var f = Ready() with
        {
            CompanySenderVerified = false,
            PlatformDailyCap = 20,
            EtaxChannel = EtaxEmailChannel.EtaxByEmail,
            EtaxSenderRefusal = "ยังไม่ได้ตั้งค่าการส่งอีเมลของบริษัท",
        };
        var r = AutopilotReadiness.Evaluate(f);
        var item = Assert.Single(r.Items, i => i.Code == "ETAX-SENDER");
        Assert.Equal(AutopilotReadiness.Fail, item.Verdict);
        Assert.Equal(AutopilotReadiness.EtaxSenderFailTitle, item.Title);
        Assert.Equal(AutopilotReadiness.EtaxSettingsUrl, item.FixUrl);
        Assert.NotEqual(AutopilotReadiness.LevelFull, r.Level);
        Assert.DoesNotContain(r.Items, i => i.Code == "SMTP");     // ใบ e-Tax by Email ไม่มีทางถอยไปอีเมลของ NextAcc
        Assert.DoesNotContain(r.Items, i => i.Code == "QUOTA");    // ไม่ส่งผ่านเพดานของ NextAcc
    }

    [Fact]
    public void R251_ETAX_SENDER_ทิศตรงข้าม_ผู้ส่งพร้อม_Ok_ระดับเต็ม()
    {
        var f = Ready() with { EtaxChannel = EtaxEmailChannel.EtaxByEmail, EtaxSenderRefusal = null };
        Assert.Equal(AutopilotReadiness.Ok, One(f, "ETAX-SENDER").Verdict);
        Assert.Equal(AutopilotReadiness.Ok, One(f, "ETAX").Verdict);
        Assert.Equal(AutopilotReadiness.LevelFull, AutopilotReadiness.Evaluate(f).Level);
    }

    [Fact]
    public void R251_ETAX_ใบนอกขอบเขต_อีเมลธรรมดา_Ok_และไม่ออกข้อผู้ส่งผู้รับ()
    {
        var f = Ready() with { DocumentType = DocumentType.Invoice, EtaxChannel = EtaxEmailChannel.PlainEmail, EtaxRouteReason = "ไม่ใช่ใบกำกับ" };
        Assert.Equal(AutopilotReadiness.Ok, One(f, "ETAX").Verdict);
        None(f, "ETAX-SENDER");
        None(f, "ETAX-RECIPIENT");
        Assert.Equal(AutopilotReadiness.Ok, One(f, "SMTP").Verdict);   // เส้นเดิม
    }

    // ═══ ฝ่ายค้านข้อ 3: ส่งตามกฎในตารางส่งก็ต้องตรวจ e-Tax ═══

    [Fact]
    public void R251_ETAX_ไม่ติ๊กส่งแต่มีกฎเมื่ออนุมัติ_ยังตรวจ_ทิศตรงข้ามไม่มีกฎไม่ออกข้อ()
    {
        var byRule = Ready() with
        {
            AutoApprove = false, AutoSendEmail = false, ApprovedEmailRuleCovers = true,
            EtaxChannel = EtaxEmailChannel.EtaxByEmail, EtaxSenderRefusal = "ยังไม่ทดสอบ",
        };
        Assert.True(AutopilotReadiness.WillEmail(byRule));
        Assert.Equal(AutopilotReadiness.Fail, One(byRule, "ETAX-SENDER").Verdict);
        var recurringRule = byRule with { ApprovedEmailRuleCovers = false, AutoApprove = true, RecurringEmailRuleExists = true };
        Assert.Equal(AutopilotReadiness.Fail, One(recurringRule, "ETAX-SENDER").Verdict);
        var none = byRule with { ApprovedEmailRuleCovers = false };
        Assert.False(AutopilotReadiness.WillEmail(none));
        None(none, "ETAX-SENDER");
        // กฎ "เมื่อรายการประจำสร้างเอกสาร" ไม่ส่งใบร่าง ⇒ ไม่อนุมัติอัตโนมัติ = ไม่ส่ง
        Assert.False(AutopilotReadiness.WillEmail(none with { RecurringEmailRuleExists = true, AutoApprove = false }));
    }

    // ═══ ฝ่ายค้านข้อ 5: รูปแบบใบเสร็จตัดสินตอนอนุมัติ ⇒ ไม่ฟ้องแดงหลอก ═══

    [Fact]
    public void R251_ETAX_ใบเสร็จทำนายไม่ได้_ตรวจตอนอนุมัติเป็นInfo_ทิศตรงข้ามใบกำกับแน่นอนFail()
    {
        var f = Ready() with { DocumentType = DocumentType.Receipt, EtaxChannel = EtaxEmailChannel.EtaxByEmail, EtaxSenderRefusal = "x", EtaxScopeUncertain = true };
        Assert.Equal(AutopilotReadiness.Info, One(f, "ETAX-SENDER").Verdict);
        Assert.Equal(AutopilotReadiness.Info, One(f, "ETAX").Verdict);
        Assert.Contains("ตรวจตอนอนุมัติ", One(f, "ETAX").Title);
        Assert.Equal(AutopilotReadiness.Fail, One(f with { EtaxScopeUncertain = false }, "ETAX-SENDER").Verdict);
    }

    [Fact]
    public void R251_ทำนายใบกำกับของใบที่ยังไม่สร้าง_ใบกำกับมีVATแน่นอน_ใบเสร็จไม่แน่นอน_ชนิดอื่นไม่เกี่ยว()
    {
        Assert.Equal(((bool?)true, false), EtaxAutoIssueScope.PredictForUnissued(DocumentType.TaxInvoice, 70m));
        Assert.Equal(((bool?)null, true), EtaxAutoIssueScope.PredictForUnissued(DocumentType.Receipt, 70m));
        Assert.Equal(((bool?)null, true), EtaxAutoIssueScope.PredictForUnissued(DocumentType.TaxInvoice, 0m));
        Assert.Equal(((bool?)null, true), EtaxAutoIssueScope.PredictForUnissued(DocumentType.TaxInvoice, 70m, paysOnIssue: true));
        Assert.Equal(((bool?)null, false), EtaxAutoIssueScope.PredictForUnissued(DocumentType.Invoice, 70m));
    }

    // ═══ ETAX-RECIPIENT ═══

    [Fact]
    public void R251_ETAX_RECIPIENT_ผู้ติดต่อหลายอีเมล_Fail_ชี้หน้าผู้ติดต่อ()
    {
        var f = Ready() with { EtaxChannel = EtaxEmailChannel.EtaxByEmail, EtaxRecipientProblem = "ส่งถึงผู้ซื้อได้ 1 อีเมลต่อฉบับ" };
        var item = One(f, "ETAX-RECIPIENT");
        Assert.Equal(AutopilotReadiness.Fail, item.Verdict);
        Assert.StartsWith("/pages/contacts.html?id=", item.FixUrl);
        var sched = One(f with { EtaxRecipientFromSchedule = true }, "ETAX-RECIPIENT");
        Assert.Equal(AutopilotReadiness.EmailScheduleUrl, sched.FixUrl);
    }

    [Fact]
    public void R251_ETAX_RECIPIENT_ทิศตรงข้าม_อีเมลเดียว_Ok()
    {
        var f = Ready() with { EtaxChannel = EtaxEmailChannel.EtaxByEmail };
        Assert.Equal(AutopilotReadiness.Ok, One(f, "ETAX-RECIPIENT").Verdict);
    }

    [Fact]
    public void R251_ตัวตัดสินผู้รับeTax_หลายอีเมลหรือBCC_ปฏิเสธ_อีเมลเดียวผ่าน()
    {
        Assert.NotNull(EtaxByEmailFormat.Recipients("a@x.co, b@x.co", null, null, true, true, false, false).Problem);
        Assert.NotNull(EtaxByEmailFormat.Recipients("a@x.co", null, "boss@x.co", true, true, false, false).Problem);
        Assert.Null(EtaxByEmailFormat.Recipients("a@x.co", null, null, true, true, false, false).Problem);
    }

    [Fact]
    public void R251_ช่องทางใบประจำ_ใบกำกับอนุมัติแล้ว_ByEmail_ทิศตรงข้ามใบแจ้งหนี้อีเมลธรรมดา()
    {
        var taxScope = EtaxAutoIssueScope.Judge(DocumentType.TaxInvoice, DocumentStatus.Approved, false, false, null, null, null, false);
        Assert.Equal(EtaxEmailChannel.EtaxByEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.ByEmail, taxScope, false).Channel);
        var invScope = EtaxAutoIssueScope.Judge(DocumentType.Invoice, DocumentStatus.Approved, false, false, null, null, null, false);
        Assert.Equal(EtaxEmailChannel.PlainEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.ByEmail, invScope, false).Channel);
        Assert.Equal(EtaxEmailChannel.PlainEmail, EtaxDeliveryRouter.Decide(false, EtaxMode.ByEmail, taxScope, false).Channel);
    }

    [Fact]
    public void R251_คำเตือนแท็บผู้ติดต่อ_เฉพาะโหมดByEmail()
    {
        Assert.NotNull(EtaxByEmailFormat.ContactRecipientsNote(true, EtaxMode.ByEmail));
        Assert.Null(EtaxByEmailFormat.ContactRecipientsNote(true, EtaxMode.Direct));
        Assert.Null(EtaxByEmailFormat.ContactRecipientsNote(false, EtaxMode.ByEmail));
    }

    // ═══ PAY-QR ═══

    [Fact]
    public void R251_PAYQR_บัญชีมีQR_Ok_ทิศตรงข้ามเลือกบัญชีที่ไม่มีQR_Warn()
    {
        var ok = One(Ready() with { BankQrApplies = true, BankQrAccount = "กสิกรไทย 123-4-56789-0 (บริษัท)" }, "PAY-QR");
        Assert.Equal(AutopilotReadiness.Ok, ok.Verdict);
        var warn = One(Ready() with { BankQrApplies = true, BankQrProblem = PaymentQrPolicy.ProblemChosenNoQr }, "PAY-QR");
        Assert.Equal(AutopilotReadiness.Warn, warn.Verdict);
        Assert.Equal(AutopilotReadiness.BankAccountsUrl, warn.FixUrl);
        Assert.Equal(AutopilotReadiness.LevelPartial, AutopilotReadiness.Evaluate(Ready() with { BankQrApplies = true, BankQrProblem = "x" }).Level);
    }

    [Fact]
    public void R251_PAYQR_ไม่มีQR_โอนอย่างเดียวWarn_มีลิงก์ชำระOk_เทมเพลตไม่พิมพ์QRไม่ออกข้อ()
    {
        Assert.Equal(AutopilotReadiness.Warn, One(Ready() with { BankQrApplies = true, PayLinkAttach = false }, "PAY-QR").Verdict);
        Assert.Equal(AutopilotReadiness.Ok, One(Ready() with { BankQrApplies = true }, "PAY-QR").Verdict);
        None(Ready() with { BankQrApplies = false, BankQrProblem = "x" }, "PAY-QR");
        None(Ready() with { BankQrApplies = true, Currency = "USD" }, "PAY-QR");
    }

    // ═══ QUOTA ═══

    [Fact]
    public void R251_QUOTA_ครบเพดานวันนี้_Warn_ยังไม่ยืนยันตัวตน_Warn()
    {
        var f = Ready() with { CompanySenderVerified = false, PlatformDailyCap = 20, PlatformSentToday = 20, PlatformIdentityVerified = false };
        Assert.Equal(AutopilotReadiness.Warn, One(f, "QUOTA").Verdict);
        Assert.Contains("ครบเพดาน", One(f, "QUOTA").Title);
        var unverified = One(f with { PlatformSentToday = 3 }, "QUOTA");
        Assert.Equal(AutopilotReadiness.Warn, unverified.Verdict);
        Assert.Contains("ยังไม่ยืนยันตัวตน", unverified.Title);
    }

    [Fact]
    public void R251_QUOTA_ทิศตรงข้าม_ยังเหลือ_Ok_ส่งจากอีเมลบริษัทไม่ออกข้อ()
    {
        var f = Ready() with { CompanySenderVerified = false, PlatformDailyCap = 200, PlatformSentToday = 5, PlatformIdentityVerified = true };
        Assert.Equal(AutopilotReadiness.Ok, One(f, "QUOTA").Verdict);
        None(Ready() with { PlatformDailyCap = 200 }, "QUOTA");                  // อีเมลบริษัททดสอบผ่าน = ไม่มีเพดานนี้
        None(Ready() with { CompanySenderVerified = false, AutoSendEmail = false, PlatformDailyCap = 200 }, "QUOTA");
    }

    // ═══ PERIOD ═══

    [Fact]
    public void R251_PERIOD_รอบหน้าในงวดปิด_Fail_ระดับต่ำสุด()
    {
        var f = Ready() with { NextDocumentDate = new DateTime(2026, 10, 31), NextPeriodClosed = true };
        var r = AutopilotReadiness.Evaluate(f);
        var item = Assert.Single(r.Items, i => i.Code == "PERIOD");
        Assert.Equal(AutopilotReadiness.Fail, item.Verdict);
        Assert.Contains("31/10/2569", item.Title);
        Assert.Equal(AutopilotReadiness.FiscalPeriodUrl, item.FixUrl);
        Assert.Equal(AutopilotReadiness.LevelDraftOnly, r.Level);
    }

    [Fact]
    public void R251_PERIOD_ทิศตรงข้าม_งวดเปิดOk_รอบถัดไปปิดWarn_ไม่ได้ตรวจไม่ออกข้อ()
    {
        Assert.Equal(AutopilotReadiness.Ok, One(Ready() with { NextDocumentDate = new DateTime(2026, 11, 6), NextPeriodClosed = false }, "PERIOD").Verdict);
        var later = One(Ready() with { NextPeriodClosed = false, LaterClosedRunDates = new[] { new DateTime(2026, 12, 6) } }, "PERIOD");
        Assert.Equal(AutopilotReadiness.Warn, later.Verdict);
        None(Ready(), "PERIOD");
    }

    [Fact]
    public void R251_ClosedPeriodRanges_Covers_วันแรกรวม_วันหลังวันสุดท้ายไม่รวม_งวดเปิดไม่นับ()
    {
        var ranges = ClosedPeriodRanges.Build(new[]
        {
            (new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), FiscalPeriodStatus.Closed),
            (new DateTime(2026, 11, 1), new DateTime(2026, 11, 30), FiscalPeriodStatus.Open),
        });
        Assert.True(ClosedPeriodRanges.Covers(ranges, new DateTime(2026, 10, 1)));
        Assert.True(ClosedPeriodRanges.Covers(ranges, new DateTime(2026, 10, 31, 15, 0, 0)));
        Assert.False(ClosedPeriodRanges.Covers(ranges, new DateTime(2026, 11, 1)));
        Assert.False(ClosedPeriodRanges.Covers(ranges, new DateTime(2026, 9, 30)));
        Assert.False(ClosedPeriodRanges.Covers(Array.Empty<ClosedDateRange>(), new DateTime(2026, 10, 15)));
    }

    // ═══ POSTPAY ═══

    [Fact]
    public void R251_POSTPAY_ปิด_Info_ระดับบางส่วน_ชี้ขั้นส่งรับเงิน()
    {
        var f = Ready() with { PostPayEmailReceipt = false };
        var item = One(f, "POSTPAY");
        Assert.Equal(AutopilotReadiness.Info, item.Verdict);
        Assert.Equal(AutopilotReadiness.PaymentStepUrl, item.FixUrl);
        Assert.Equal(AutopilotReadiness.LevelPartial, AutopilotReadiness.Evaluate(f).Level);
    }

    [Fact]
    public void R251_POSTPAY_ทิศตรงข้าม_เปิด_Ok_ระดับเต็ม_ไม่ใช่เอกสารขายไม่ออกข้อ()
    {
        var f = Ready() with { PostPayEmailReceipt = true, PostPayIncludePartial = true };
        Assert.Equal(AutopilotReadiness.Ok, One(f, "POSTPAY").Verdict);
        Assert.Contains("ทุกงวด", One(f, "POSTPAY").Title);
        Assert.Equal(AutopilotReadiness.LevelFull, AutopilotReadiness.Evaluate(f).Level);
        None(Ready() with { SaleSide = false, PostPayEmailReceipt = false }, "POSTPAY");
    }

    // ═══ ฝ่ายค้านข้อ 4: ใบเสร็จหลังรับชำระไปทาง e-Tax by Email ═══

    [Fact]
    public void R251_POSTPAY_ใบเสร็จต้องไปeTaxแต่ด่านผู้ส่งไม่ผ่าน_Warn_ทิศตรงข้ามผ่านOk()
    {
        var f = Ready() with { PostPayEmailReceipt = true, PostPayReceiptEtaxRefusal = "ยังไม่ได้ตั้งค่าการส่งอีเมลของบริษัท" };
        var item = One(f, "POSTPAY");
        Assert.Equal(AutopilotReadiness.Warn, item.Verdict);
        Assert.Equal(AutopilotReadiness.EtaxSettingsUrl, item.FixUrl);
        Assert.Equal(AutopilotReadiness.Ok, One(f with { PostPayReceiptEtaxRefusal = null }, "POSTPAY").Verdict);
        Assert.Equal(AutopilotReadiness.Info, One(f with { PostPayEmailReceipt = false }, "POSTPAY").Verdict);   // ปิดอยู่ = ไม่ส่ง ไม่มีด่านให้ล้ม
    }

    [Fact]
    public void R251_ขอบเขตใบเสร็จหลังรับชำระ_อ้างใบแจ้งหนี้ไปeTax_อ้างใบกำกับไม่ไป()
    {
        var ofInvoice = EtaxAutoIssueScope.Judge(DocumentType.Receipt, DocumentStatus.Approved, false, false, null, DocumentType.Invoice, null, false);
        Assert.Equal(EtaxEmailChannel.EtaxByEmail, EtaxDeliveryRouter.Decide(true, EtaxMode.ByEmail, ofInvoice, false).Channel);
        var ofTaxInvoice = EtaxAutoIssueScope.Judge(DocumentType.Receipt, DocumentStatus.Approved, false, false, null, DocumentType.TaxInvoice, null, false);
        Assert.Equal(EtaxAutoSkip.ReceiptSettlesTaxInvoice, ofTaxInvoice);
    }

    // ═══ ฝ่ายค้านข้อ 8: PAY-QR ตัวตัดสินเดียวกับกระดาษ ═══

    [Fact]
    public void R251_ShowBankQr_ใบอนุมัติมียอด_พิมพ์_ทิศตรงข้ามยอดศูนย์หรือสกุลอื่นไม่พิมพ์()
    {
        Assert.True(PaymentQrPolicy.ShowBankQr(true, DocumentType.Invoice, null, null, false, DocumentStatus.Approved, 1070m, "THB"));
        Assert.False(PaymentQrPolicy.ShowBankQr(true, DocumentType.Invoice, null, null, false, DocumentStatus.Approved, 0m, "THB"));
        Assert.False(PaymentQrPolicy.ShowBankQr(true, DocumentType.Invoice, null, null, false, DocumentStatus.Approved, 1070m, "USD"));
        Assert.False(PaymentQrPolicy.ShowBankQr(false, DocumentType.Invoice, null, null, false, DocumentStatus.Approved, 1070m, "THB"));
    }

    // ═══ ฝ่ายค้านข้อ 7: ด่านงวดตอนสร้างเทียบวันตามปฏิทินไทย ═══

    [Fact]
    public void R251_วันตามปฏิทินไทย_บ่ายวันสุดท้ายยังเป็นวันสุดท้าย_ดึกเป็นวันถัดไป()
    {
        var ranges = ClosedPeriodRanges.Build(new[] { (new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), FiscalPeriodStatus.Closed) });
        var afternoon = ThaiDate.CalendarDateUtc(new DateTime(2026, 10, 31, 8, 0, 0, DateTimeKind.Utc));   // 15:00 ไทย
        Assert.Equal(new DateTime(2026, 10, 31), afternoon.Date);
        Assert.True(ClosedPeriodRanges.Covers(ranges, afternoon));          // เดิมเทียบเวลาดิบ > EndDate เที่ยงคืน ⇒ หลุดด่าน
        var lateNight = ThaiDate.CalendarDateUtc(new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc));  // 01:00 ไทยของ 1 พ.ย.
        Assert.Equal(new DateTime(2026, 11, 1), lateNight.Date);
        Assert.False(ClosedPeriodRanges.Covers(ranges, lateNight));         // ทิศตรงข้าม: วันถัดไป (งวดเปิด) ไม่ถูกกัน
    }

    // ═══ ฝ่ายค้านข้อ 2: ส่งทดสอบ ═══

    [Fact]
    public void R251_ส่งทดสอบ_อีเมลยังไม่ยืนยันปฏิเสธ_ยืนยันแล้วผ่าน_ปิดบังอีเมลผู้รับจริง()
    {
        Assert.NotNull(RecurringTestEmail.UnverifiedRefusal(false));
        Assert.Null(RecurringTestEmail.UnverifiedRefusal(true));
    }

    [Fact]
    public void R251_ชั้นหลังรับชำระ_รายการชนะผู้ติดต่อ_ว่างตามบริษัท()
    {
        var company = new PostPaymentAutomationPolicy.Settings(false, false);
        var on = PostPaymentAutomationPolicy.EffectiveLayers(company, null, new PostPaymentAutomationPolicy.Overrides(true, null),
            new PostPaymentAutomationPolicy.Overrides(false, true));
        Assert.True(on.EmailReceipt);
        Assert.True(on.IncludePartial);
        Assert.False(PostPaymentAutomationPolicy.EffectiveLayers(company, null, new PostPaymentAutomationPolicy.Overrides(null, null), null).EmailReceipt);
    }

    // ═══ ส่งทดสอบ: เพดาน + เนื้อหา ═══

    [Fact]
    public void R251_ส่งทดสอบ_เพดานต่อวัน_ทิศตรงข้ามยังไม่ครบส่งได้()
    {
        Assert.Null(RecurringTestEmail.LimitRefusal(RecurringTestEmail.MaxPerUserPerDay - 1));
        Assert.NotNull(RecurringTestEmail.LimitRefusal(RecurringTestEmail.MaxPerUserPerDay));
    }

    [Fact]
    public void R251_อีเมลทดสอบ_หัวทดสอบ_หนีHTML_ไม่มีลิงก์ชำระ()
    {
        var readiness = new AutopilotReadinessResponse(AutopilotReadiness.LevelPartial, "บางส่วน",
            new[] { new AutopilotReadiness.Item("ETAX-SENDER", "ส่ง", AutopilotReadiness.Fail, "<b>ล้ม</b>", null, null, null) }, 0, 1);
        var dry = new RecurringDryRunResponse(readiness, new[] { new DateTime(2026, 12, 1) }, "ใบกำกับภาษี", "เลขที่จริงออกตอนอนุมัติ",
            new[] { new RecurringDryRunLine("<script>alert(1)</script>", 1m, 100m, 7m, 100m, 7m, 0m, 0m) }, 100m, 7m, 107m, 0m, 107m,
            "จะอนุมัติ", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "ap@customer.co.th", "ส่งถึง ap@customer.co.th",
            "แนบลิงก์ชำระ", Array.Empty<string>(), EtaxPlan: "e-Tax by Email");
        var m = RecurringTestEmail.Compose("ค่าบริการ <x>", "บริษัท & ลูก", dry);
        Assert.StartsWith(RecurringTestEmail.SubjectPrefix, m.Subject);
        Assert.DoesNotContain("<script>", m.Html);
        Assert.Contains("&lt;script&gt;", m.Html);
        Assert.DoesNotContain("<b>ล้ม</b>", m.Html);
        Assert.Contains("บริษัท &amp; ลูก", m.Html);
        Assert.DoesNotContain("pay-document", m.Html);
        Assert.Contains("ไม่มีลิงก์ชำระ", m.Html);
        Assert.DoesNotContain("ap@customer.co.th", m.Html);   // ฝ่ายค้านข้อ 2: ปิดบังอีเมลลูกค้า
        Assert.DoesNotContain("ap@customer.co.th", m.Text);
        Assert.Contains("•••@customer.co.th", m.Html);
    }

    // ═══ server computes · page displays ═══

    [Fact]
    public void R251_หน้ารายการประจำ_มีคอลัมน์ระดับและปุ่มส่งทดสอบ_ไม่มีรหัสกติกาใหม่ในJS()
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting", "wwwroot", "pages", "recurring.html"));
        Assert.Contains("ระดับ Autopilot", html);
        Assert.Contains("ส่งทดสอบ", html);
        Assert.Contains("getRecurringAutopilotLevels", html);
        foreach (var code in new[] { "ETAX-SENDER", "ETAX-RECIPIENT", "PAY-QR", "POSTPAY", "PlatformDailyCap" })
            Assert.DoesNotContain(code, html);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
