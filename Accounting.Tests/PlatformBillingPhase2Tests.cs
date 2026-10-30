using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §5 · คำตัดสินข้อ 180) — ตัวตัดสิน pure ของ "เก็บเงินได้จริง": ค่าตั้งการเรียกเก็บ · เงื่อนไขการซื้อ ·
/// กติกาใบแจ้งหนี้ · ศูนย์บิล · ทุกด่านล็อกทั้งทิศ "ปฏิเสธ" และ "ของที่ถูกต้องผ่าน" (F2 ข้อ 8)
/// </summary>
public class PlatformBillingPhase2Tests
{
    private static readonly DateTime Now = new(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);   // 10:00 เวลาไทย

    // ═══ ค่าตั้งการเรียกเก็บ ═══

    [Fact]
    public void Settings_default_when_no_row_and_match_previous_hardcoded_behaviour()
    {
        var p = PlatformBillingSettingsRules.FromRow(null);
        Assert.Equal(15, p.RenewalInvoiceLeadDays);   // เดิมค่าคงที่ 15 ใน SubscriptionService — ไม่มีแถว = พฤติกรรมเดิม
        Assert.Equal(new[] { 7, 3, 1 }, p.ReminderDaysBeforeDue);
        Assert.Equal(new[] { 1, 3, 7 }, p.ReminderDaysAfterDue);
        Assert.Equal(14, p.SuspendGraceDays);
        Assert.True(p.AutoSuspendEnabled);
        Assert.Equal(14, p.PaymentLinkExpiryDays);
        Assert.True(p.ReminderEmailEnabled && p.ReminderLineEnabled);
    }

    [Fact]
    public void Settings_row_values_out_of_range_are_clamped_not_trusted()
    {
        var p = PlatformBillingSettingsRules.FromRow(new PlatformBillingSettings
        {
            RenewalInvoiceLeadDays = 0, SuspendGraceDays = -5, PaymentLinkExpiryDays = 999,
            ReminderDaysBeforeDue = "3, x, 7, 7, 0, 99", ReminderDaysAfterDue = "",
        });
        Assert.Equal(PlatformBillingSettingsRules.MinRenewalInvoiceLeadDays, p.RenewalInvoiceLeadDays);
        Assert.Equal(0, p.SuspendGraceDays);   // ผ่อนผันติดลบ ≠ ระงับก่อนครบกำหนด
        Assert.Equal(PlatformBillingSettingsRules.MaxPaymentLinkExpiryDays, p.PaymentLinkExpiryDays);
        Assert.Equal(new[] { 7, 3 }, p.ReminderDaysBeforeDue);
        Assert.Empty(p.ReminderDaysAfterDue);   // ว่าง = ตั้งใจไม่ทวง
    }

    [Theory]
    [InlineData(15, "7,3,1", "1,3,7", 14, 14, null)]
    [InlineData(0, "7", "1", 14, 14, "ออกใบแจ้งหนี้ต่ออายุ")]
    [InlineData(15, "7", "1", 91, 14, "ระยะผ่อนผัน")]
    [InlineData(15, "7", "1", 14, 0, "อายุลิงก์")]
    [InlineData(15, "7,abc", "1", 14, 14, "abc")]
    [InlineData(15, "7", "1,2,3,4,5,6,8", 14, 14, "ไม่เกิน")]
    [InlineData(15, "", "", 0, 1, null)]
    public void Settings_validation_rejects_bad_values_with_thai_message(int lead, string before, string after, int grace, int link, string? expectContains)
    {
        var err = PlatformBillingSettingsRules.Validate(lead, before, after, grace, link, out var nb, out var na);
        if (expectContains == null) Assert.Null(err);
        else Assert.Contains(expectContains, err);
    }

    [Fact]
    public void Settings_validation_normalizes_day_lists()
    {
        Assert.Null(PlatformBillingSettingsRules.Validate(15, " 1, 7 ,3,7", "7 1 3", 14, 14, out var b, out var a));
        Assert.Equal("7,3,1", b);
        Assert.Equal("1,3,7", a);
    }

    [Fact]
    public void Suspension_description_tells_truth_about_shadow_mode()
    {
        var p = PlatformBillingSettingsRules.Defaults;
        Assert.Contains("ระงับการใช้งาน", PlatformBillingSettingsRules.DescribeSuspension(p, enforced: true));
        Assert.Contains("ยังบันทึกอย่างเดียว", PlatformBillingSettingsRules.DescribeSuspension(p, enforced: false));
        Assert.Contains("ไม่มีการระงับอัตโนมัติ", PlatformBillingSettingsRules.DescribeSuspension(p with { AutoSuspendEnabled = false }, true));
    }

    // ═══ เงื่อนไขการซื้อ (คำตัดสินเจ้าของ ⑧) ═══

    [Fact]
    public void Purchase_terms_require_tick_and_current_version()
    {
        var none = PlatformPurchaseTerms.Refusal(false, PlatformPurchaseTerms.Version);
        Assert.NotNull(none);
        Assert.Equal(400, none!.StatusCode);
        Assert.Equal(PlatformPurchaseTerms.NotAcceptedRuleCode, none.RuleCode);

        var stale = PlatformPurchaseTerms.Refusal(true, "2020-01-01.1");
        Assert.Equal(409, stale!.StatusCode);
        Assert.NotNull(PlatformPurchaseTerms.Refusal(true, null));   // ไม่รู้ว่าเห็นฉบับไหน = ไม่ถือว่ายอมรับ

        Assert.Null(PlatformPurchaseTerms.Refusal(true, " " + PlatformPurchaseTerms.Version + " "));
    }

    [Fact]
    public void Purchase_terms_text_matches_owner_decisions()
    {
        var all = string.Join("\n", PlatformPurchaseTerms.Lines);
        Assert.Contains("ไม่สามารถขอคืนเงิน", all);           // ⑦ ไม่คืนเงิน
        Assert.Contains("จนสิ้นรอบ", all);                    // ⑥ ยกเลิก = ใช้ได้ถึงสิ้นรอบ
        Assert.Contains("ไม่มีการคิดหรือคืนเงินตามสัดส่วน", all); // ⑥ ไม่ prorate
        Assert.Contains("รวมในใบแจ้งหนี้รายเดือน", all);        // ⑤ ส่วนเสริมเลือกได้ต่อการซื้อ
    }

    // ═══ กติกาใบแจ้งหนี้ ═══

    [Theory]
    [InlineData("RENEW-ABCD1234", PlatformInvoiceKind.Renewal)]
    [InlineData("USAGE-2026-09-ABCD1234", PlatformInvoiceKind.Usage)]
    [InlineData("ค่าติดตั้ง", PlatformInvoiceKind.Other)]
    [InlineData(null, PlatformInvoiceKind.Other)]
    public void Invoice_kind_from_reference(string? reference, PlatformInvoiceKind expected)
        => Assert.Equal(expected, PlatformInvoiceRules.KindOf(reference));

    [Theory]
    [InlineData(DocumentStatus.Approved, 100.0, true)]
    [InlineData(DocumentStatus.PartiallyPaid, 50.0, true)]
    [InlineData(DocumentStatus.Overdue, 1.0, true)]
    [InlineData(DocumentStatus.Paid, 0.0, false)]
    [InlineData(DocumentStatus.Approved, 0.004, false)]
    [InlineData(DocumentStatus.Voided, 100.0, false)]
    [InlineData(DocumentStatus.Draft, 100.0, false)]
    public void Invoice_open_only_when_issued_not_voided_and_balance_left(DocumentStatus st, double balance, bool open)
        => Assert.Equal(open, PlatformInvoiceRules.IsOpen(st, (decimal)balance));

    [Fact]
    public void Days_past_due_counts_thai_calendar_days()
    {
        // ครบกำหนด 30 ต.ค. 23:30 ไทย (16:30 UTC) · ตอนนี้ 31 ต.ค. 00:30 ไทย ⇒ เกิน 1 วัน (ไม่ใช่ 1 ชั่วโมง = 0)
        var due = new DateTime(2026, 10, 30, 16, 30, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 30, 17, 30, 0, DateTimeKind.Utc);
        Assert.Equal(1, PlatformInvoiceRules.DaysPastDue(due, now));
        Assert.Equal(0, PlatformInvoiceRules.DaysPastDue(due, due));
        Assert.Equal(-3, PlatformInvoiceRules.DaysPastDue(Now.AddDays(3), Now));
        Assert.Null(PlatformInvoiceRules.DaysPastDue(null, Now));
    }

    [Fact]
    public void Invoice_state_and_label()
    {
        Assert.Equal(PlatformInvoiceState.Overdue, PlatformInvoiceRules.StateOf(DocumentStatus.Approved, 10m, Now.AddDays(-2), Now));
        Assert.Equal(PlatformInvoiceState.DueToday, PlatformInvoiceRules.StateOf(DocumentStatus.Sent, 10m, Now, Now));
        Assert.Equal(PlatformInvoiceState.Open, PlatformInvoiceRules.StateOf(DocumentStatus.Approved, 10m, Now.AddDays(5), Now));
        Assert.Equal(PlatformInvoiceState.Paid, PlatformInvoiceRules.StateOf(DocumentStatus.Paid, 0m, Now.AddDays(-30), Now));
        Assert.Equal(PlatformInvoiceState.Voided, PlatformInvoiceRules.StateOf(DocumentStatus.Voided, 10m, Now.AddDays(-30), Now));
        Assert.Equal("เกินกำหนด 2 วัน", PlatformInvoiceRules.StateLabel(PlatformInvoiceState.Overdue, 2));
    }

    [Theory]
    [InlineData(true, 0, true)]     // ใบค่าใช้งาน ⇒ ตัดหนี้อย่างเดียว
    [InlineData(true, 1, false)]    // ใบต่ออายุ ⇒ ต่ออายุตามเส้นเดิม
    [InlineData(false, 0, false)]   // รายการแบบเดิม (ไม่ผูกใบ) ไม่ถูกแตะ
    [InlineData(false, 12, false)]
    public void Invoice_only_payment_never_extends_subscription(bool linked, int months, bool invoiceOnly)
        => Assert.Equal(invoiceOnly, PlatformInvoiceRules.IsInvoiceOnlyPayment(linked ? Guid.NewGuid() : null, months));

    // ═══ ศูนย์บิล ═══

    [Fact]
    public void Billing_center_pay_refusal_reasons()
    {
        Assert.Null(PlatformBillingCenterService.PayRefusal(open: true, canPay: true, pendingWithSlip: false, null));
        Assert.Equal("ไม่มียอดค้าง", PlatformBillingCenterService.PayRefusal(false, true, false, null));
        Assert.Contains("จัดการค่าใช้จ่าย", PlatformBillingCenterService.PayRefusal(true, false, false, null));
        Assert.Contains("SP-1", PlatformBillingCenterService.PayRefusal(true, true, true, "SP-1"));
        // แจ้งไว้แต่ยังไม่แนบสลิป ⇒ จ่ายต่อได้ (คืนรายการเดิม — ไม่เกิดรายการซ้อน)
        Assert.Null(PlatformBillingCenterService.PayRefusal(true, true, false, "SP-1"));
    }

    [Fact]
    public void Billing_center_payment_purpose_labels()
    {
        Assert.Equal("ชำระใบแจ้งหนี้ INV-1", PlatformBillingCenterService.PaymentPurpose(true, "INV-1", SubscriptionPaymentKind.Normal, SubscriptionPlan.Pro, 0));
        Assert.Equal("ยกเว้นค่าบริการ", PlatformBillingCenterService.PaymentPurpose(false, null, SubscriptionPaymentKind.Waived, SubscriptionPlan.Pro, 1));
        Assert.Equal("แพ็กเกจ Pro (12 เดือน)", PlatformBillingCenterService.PaymentPurpose(false, null, SubscriptionPaymentKind.Normal, SubscriptionPlan.Pro, 12));
        Assert.Equal("รอแนบสลิป", PlatformBillingCenterService.PaymentStatusLabel(SubscriptionPaymentStatus.Pending, false));
        Assert.Equal("รอตรวจสอบ", PlatformBillingCenterService.PaymentStatusLabel(SubscriptionPaymentStatus.Pending, true));
    }

    [Fact]
    public void Acceptance_evidence_hash_round_trips_with_canonical_function()
    {
        var at = PdpaConsentEvidence.NormalizeTimestamp(Now);
        var hash = PdpaConsentEvidence.ComputeHash("owner@example.com", PlatformPurchaseTerms.Purpose, PlatformPurchaseTerms.Version,
            at, "billing-center", "1.2.3.4", "UA");
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PdpaConsentEvidence.Check(hash, "owner@example.com", PlatformPurchaseTerms.Purpose,
            PlatformPurchaseTerms.Version, at, "billing-center", "1.2.3.4", "UA"));
        // ฉบับต่าง = หลักฐานไม่ตรง (ไม่ใช่ "ยอมรับแล้ว" ของฉบับอื่น)
        Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Invalid, PdpaConsentEvidence.Check(hash, "owner@example.com", PlatformPurchaseTerms.Purpose,
            "2020-01-01.1", at, "billing-center", "1.2.3.4", "UA"));
    }

    // ═══ ขั้น 2: dunning (pure) ═══

    private static DunningInvoice Inv(int dueInDays, int issuedDaysAgo = 10, decimal bal = 500m, bool excluded = false) =>
        new(Guid.NewGuid(), "INV-1", Now.AddDays(dueInDays), Now.AddDays(-issuedDaysAgo), bal, excluded);

    [Fact]
    public void Dunning_reminders_send_latest_crossed_threshold_only()
    {
        var p = PlatformBillingSettingsRules.Defaults;   // before 7,3,1 · after 1,3,7
        Assert.Contains(PlatformDunningPolicy.RemindersFor(Inv(5), Now, p), r => r.Kind == "before" && r.Days == 7);
        Assert.Contains(PlatformDunningPolicy.RemindersFor(Inv(2), Now, p), r => r.Kind == "before" && r.Days == 3);
        Assert.DoesNotContain(PlatformDunningPolicy.RemindersFor(Inv(10), Now, p), r => r.Kind == "before");
        Assert.Contains(PlatformDunningPolicy.RemindersFor(Inv(0), Now, p), r => r.Kind == "due");
        // job หยุด 5 วัน: เกิน 5 วัน ⇒ ส่งเกณฑ์ล่าสุด (3) ฉบับเดียว ไม่ส่งย้อน 1
        var late = PlatformDunningPolicy.RemindersFor(Inv(-5), Now, p).Where(r => r.Kind == "after").ToList();
        Assert.Single(late);
        Assert.Equal(3, late[0].Days);
        // ทิศตรงข้าม: ใบที่มีสลิปรอตรวจ/ยอดศูนย์ ไม่ถูกทวง
        Assert.Empty(PlatformDunningPolicy.RemindersFor(Inv(-5, excluded: true), Now, p));
        Assert.Empty(PlatformDunningPolicy.RemindersFor(Inv(-5, bal: 0m), Now, p));
    }

    [Fact]
    public void Dunning_issued_notice_once_for_recent_invoices_and_keys_are_stable()
    {
        var p = PlatformBillingSettingsRules.Defaults;
        var fresh = Inv(5, issuedDaysAgo: 0);
        var r = PlatformDunningPolicy.RemindersFor(fresh, Now, p);
        Assert.Contains(r, x => x.Kind == "issued");
        Assert.DoesNotContain(r, x => x.Kind == "before");   // วันออกใบไม่ส่งเตือนซ้อน
        Assert.DoesNotContain(PlatformDunningPolicy.RemindersFor(Inv(5, issuedDaysAgo: 40), Now, p), x => x.Kind == "issued");
        Assert.Equal(PlatformDunningPolicy.Key(fresh.DocumentId, "after", 3), PlatformDunningPolicy.Key(fresh.DocumentId, "after", 3));
        Assert.NotEqual(PlatformDunningPolicy.Key(fresh.DocumentId, "after", 3), PlatformDunningPolicy.Key(fresh.DocumentId, "after", 7));
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Enforce, true, DunningAction.Suspend)]
    [InlineData(SubscriptionEnforcementMode.Shadow, true, DunningAction.WouldSuspend)]
    [InlineData(SubscriptionEnforcementMode.Off, true, DunningAction.WouldSuspend)]
    [InlineData(SubscriptionEnforcementMode.Enforce, false, DunningAction.None)]
    public void Dunning_suspends_only_in_enforce_mode_with_auto_suspend(SubscriptionEnforcementMode mode, bool auto, DunningAction expected)
    {
        var p = PlatformBillingSettingsRules.Defaults with { AutoSuspendEnabled = auto };
        var d = PlatformDunningPolicy.Decide(Now, new[] { Inv(-20) }, p, mode, SubscriptionStatus.Active, false, null);
        Assert.Equal(expected, d.Action);
    }

    [Fact]
    public void Dunning_grace_exemption_status_and_restore_directions()
    {
        var p = PlatformBillingSettingsRules.Defaults;   // ผ่อนผัน 14 วัน
        var E = SubscriptionEnforcementMode.Enforce;
        Assert.Equal(DunningAction.None, PlatformDunningPolicy.Decide(Now, new[] { Inv(-14) }, p, E, SubscriptionStatus.Active, false, null).Action);
        Assert.Equal(DunningAction.Suspend, PlatformDunningPolicy.Decide(Now, new[] { Inv(-15) }, p, E, SubscriptionStatus.Active, false, null).Action);
        // ใบที่มีสลิปรอตรวจไม่นับ · แอดมินพักการระงับ · ทดลองใช้/ยกเลิก/ระงับด้วยเหตุอื่น ไม่ถูกแตะ
        Assert.Equal(DunningAction.None, PlatformDunningPolicy.Decide(Now, new[] { Inv(-30, excluded: true) }, p, E, SubscriptionStatus.Active, false, null).Action);
        Assert.Equal(DunningAction.None, PlatformDunningPolicy.Decide(Now, new[] { Inv(-30) }, p, E, SubscriptionStatus.Active, false, Now.AddDays(3)).Action);
        Assert.Equal(DunningAction.None, PlatformDunningPolicy.Decide(Now, new[] { Inv(-30) }, p, E, SubscriptionStatus.Trial, false, null).Action);
        Assert.Equal(DunningAction.None, PlatformDunningPolicy.Decide(Now, new[] { Inv(-30) }, p, E, SubscriptionStatus.Suspended, false, null).Action);
        // จ่ายแล้ว (ไม่มีใบเกินผ่อนผัน) + ระบบระงับไว้ ⇒ คืน · ระงับอยู่แล้วยังค้าง ⇒ คงเดิม
        Assert.Equal(DunningAction.Restore, PlatformDunningPolicy.Decide(Now, Array.Empty<DunningInvoice>(), p, E, SubscriptionStatus.Suspended, true, null).Action);
        Assert.Equal(DunningAction.None, PlatformDunningPolicy.Decide(Now, new[] { Inv(-30) }, p, E, SubscriptionStatus.Suspended, true, null).Action);
        Assert.Equal(SubscriptionStatus.PastDue, PlatformDunningPolicy.RestoreStatus(SubscriptionStatus.Active, Now.AddDays(-1), Now, false));
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.RestoreStatus(SubscriptionStatus.Active, Now.AddDays(10), Now, false));
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.RestoreStatus(SubscriptionStatus.Active, Now.AddDays(-1), Now, true));
    }

    // ═══ ขั้น 3: ยกเลิกเมื่อสิ้นรอบ · วิธีชำระส่วนเสริม · ใบต่ออายุที่ล้าสมัย (S9) ═══

    [Theory]
    [InlineData(SubscriptionStatus.Active, false, 10, SubscriptionCancelMode.AtPeriodEnd)]
    [InlineData(SubscriptionStatus.Active, true, 10, SubscriptionCancelMode.Immediate)]      // ฟรีถาวร — ไม่มีรอบที่จ่าย
    [InlineData(SubscriptionStatus.Trial, false, 10, SubscriptionCancelMode.Immediate)]
    [InlineData(SubscriptionStatus.PastDue, false, -3, SubscriptionCancelMode.Immediate)]
    [InlineData(SubscriptionStatus.Active, false, -1, SubscriptionCancelMode.Immediate)]
    public void Cancel_is_at_period_end_only_with_paid_cycle_left(SubscriptionStatus st, bool free, int endInDays, SubscriptionCancelMode expected)
        => Assert.Equal(expected, SubscriptionCancelPolicy.Decide(st, free, Now.AddDays(endInDays), Now));

    [Fact]
    public void Reactivate_and_renewal_payability_follow_cancellation()
    {
        Assert.Null(SubscriptionCancelPolicy.ReactivateRefusal(true, SubscriptionStatus.Active, Now.AddDays(5), Now));
        Assert.NotNull(SubscriptionCancelPolicy.ReactivateRefusal(false, SubscriptionStatus.Active, Now.AddDays(5), Now));
        Assert.NotNull(SubscriptionCancelPolicy.ReactivateRefusal(true, SubscriptionStatus.Cancelled, Now.AddDays(-1), Now));
        Assert.NotNull(SubscriptionCancelPolicy.RenewalNotPayableReason(true));
        Assert.Null(SubscriptionCancelPolicy.RenewalNotPayableReason(false));
        Assert.Contains("ไม่คืนเงินตามสัดส่วน", SubscriptionCancelPolicy.Note(true, Now.AddDays(5)));
    }

    [Fact]
    public void Addon_choice_never_charges_same_period_twice()
    {
        const string P = "2026-10";
        var monthly = AddOnPaymentPolicy.ApplyChoice(true, AddOnBillingChoice.MonthlyInvoice, AddOnPaymentStatus.AwaitingPayment, null, P);
        Assert.Equal(AddOnPaymentPolicy.ChoiceOutcome.MonthlyInvoice, monthly.Outcome);
        // จ่ายล่วงหน้างวดนี้แล้ว ⇒ คงเดิมทั้งสองทาง (งวดนี้ ฿0 · ไม่ออกใบซ้ำ)
        Assert.Equal(AddOnPaymentPolicy.ChoiceOutcome.Keep, AddOnPaymentPolicy.ApplyChoice(true, AddOnBillingChoice.MonthlyInvoice, AddOnPaymentStatus.Paid, P, P).Outcome);
        // งวดนี้ถูกคิดในใบรายเดือนแล้ว ⇒ จ่ายล่วงหน้าอีก = เก็บสองครั้ง ⇒ ปฏิเสธ
        Assert.Equal(AddOnPaymentPolicy.ChoiceOutcome.Refuse, AddOnPaymentPolicy.ApplyChoice(true, AddOnBillingChoice.Upfront, AddOnPaymentStatus.NotRequired, P, P).Outcome);
        Assert.Equal(AddOnPaymentPolicy.ChoiceOutcome.Keep, AddOnPaymentPolicy.ApplyChoice(true, AddOnBillingChoice.Upfront, AddOnPaymentStatus.NotRequired, "2026-09", P).Outcome);
        // สลิปรอตรวจ ⇒ เปลี่ยนเป็นรายเดือนไม่ได้จนรู้ผล
        Assert.Equal(AddOnPaymentPolicy.ChoiceOutcome.Refuse, AddOnPaymentPolicy.ApplyChoice(true, AddOnBillingChoice.MonthlyInvoice, AddOnPaymentStatus.PendingReview, null, P).Outcome);
        // ไม่ต้องจ่าย ⇒ ไม่แตะ
        Assert.Equal(AddOnPaymentPolicy.ChoiceOutcome.Keep, AddOnPaymentPolicy.ApplyChoice(false, AddOnBillingChoice.Upfront, AddOnPaymentStatus.NotRequired, P, P).Outcome);
    }

    [Fact]
    public void Stale_renewal_invoice_only_when_issued_for_current_cycle_and_not_the_one_paid()
    {
        var doc = Guid.NewGuid();
        var end = Now.AddDays(5);
        Assert.Equal(doc, PlatformInvoiceRules.StaleRenewalInvoice(doc, end, end, null));            // อัปเกรด/ต่ออายุแบบเดิม
        Assert.Null(PlatformInvoiceRules.StaleRenewalInvoice(doc, end, end, doc));                    // จ่ายใบนั้นเอง
        Assert.Null(PlatformInvoiceRules.StaleRenewalInvoice(doc, end.AddMonths(-1), end, null));     // ใบของรอบอื่น
        Assert.Null(PlatformInvoiceRules.StaleRenewalInvoice(null, end, end, null));
    }
}
