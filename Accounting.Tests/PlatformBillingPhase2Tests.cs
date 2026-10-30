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

    // ═══ ขั้น 3: ใบต่ออายุของรอบที่ยกเลิก (ยกเลิกเมื่อสิ้นรอบเป็นของ part B) · วิธีชำระส่วนเสริม · ใบต่ออายุที่ล้าสมัย (S9) ═══

    [Fact]
    public void Renewal_of_cancelled_subscription_is_not_payable()
    {
        Assert.NotNull(PlatformInvoiceRules.RenewalNotPayableReason(true));
        Assert.Null(PlatformInvoiceRules.RenewalNotPayableReason(false));
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

    // ═══ ขั้น 4: ลิงก์ชำระ ═══

    [Fact]
    public void Payment_link_token_is_128_bit_random_and_stored_only_as_hash()
    {
        var t1 = PlatformPaymentLinkRules.NewToken();
        var t2 = PlatformPaymentLinkRules.NewToken();
        Assert.NotEqual(t1, t2);
        Assert.Equal(22, t1.Length);   // 16 ไบต์ base64url ไม่มี padding
        Assert.True(PlatformPaymentLinkRules.LooksValid(t1));
        var h = PlatformPaymentLinkRules.Hash(t1);
        Assert.Equal(64, h.Length);
        Assert.Equal(h, PlatformPaymentLinkRules.Hash(t1));   // canonical เดียว — เขียนและค้นได้ค่าเดียวกัน
        Assert.DoesNotContain(t1, h);
        Assert.False(PlatformPaymentLinkRules.LooksValid("short"));
        Assert.False(PlatformPaymentLinkRules.LooksValid("<script>alert(1)</script>xxxxxxxx"));
        Assert.False(PlatformPaymentLinkRules.LooksValid(null));
    }

    [Theory]
    [InlineData(5, false, true, false, PaymentLinkState.Valid)]
    [InlineData(-1, false, true, false, PaymentLinkState.Expired)]
    [InlineData(5, true, true, false, PaymentLinkState.Revoked)]
    [InlineData(5, false, false, true, PaymentLinkState.Paid)]
    [InlineData(-1, false, false, true, PaymentLinkState.Paid)]           // ชำระครบแล้ว ⇒ บอกว่าชำระแล้ว แม้หมดอายุ
    [InlineData(5, false, false, false, PaymentLinkState.InvoiceClosed)]  // ใบถูกยกเลิก
    public void Payment_link_state(int expiresInDays, bool revoked, bool open, bool paid, PaymentLinkState expected)
    {
        var st = PlatformPaymentLinkRules.StateOf(Now.AddDays(expiresInDays), revoked ? Now : (DateTime?)null, open, paid, Now);
        Assert.Equal(expected, st);
        Assert.Equal(expected == PaymentLinkState.Valid, PlatformPaymentLinkRules.Message(st) == null);
    }

    [Fact]
    public void Payment_link_expiry_defaults_from_settings_and_is_clamped()
    {
        var p = PlatformBillingSettingsRules.Defaults;
        Assert.Equal(14, PlatformPaymentLinkRules.ExpiryDays(null, p));
        Assert.Equal(3, PlatformPaymentLinkRules.ExpiryDays(3, p));
        Assert.Equal(PlatformBillingSettingsRules.MaxPaymentLinkExpiryDays, PlatformPaymentLinkRules.ExpiryDays(1000, p));
        Assert.Equal(PlatformBillingSettingsRules.MinPaymentLinkExpiryDays, PlatformPaymentLinkRules.ExpiryDays(0, p));
        Assert.Equal(30, PlatformPaymentLinkRules.ExpiryDays(null, p with { PaymentLinkExpiryDays = 30 }));
    }
    // ═══ ฝ่ายค้านรอบ 219 เฟส 2 (B2/N6/S2/S3/S4/S6) — ตัวตัดสิน pure · ทั้งทิศ "ปฏิเสธ/พัก" และ "ของถูกผ่าน" ═══

    [Fact]
    public void ReviewLinked_holds_system_refuses_admin_when_invoice_closed_and_never_double_extends_superseded()
    {
        // ใบไม่มียอดค้าง (ยกเลิก/จ่ายครบโดยรายการอื่น): เงินเข้าออนไลน์ = พักให้แอดมิน · แอดมินกด = ปฏิเสธพร้อมทางไปต่อ
        Assert.Equal(PlatformInvoiceRules.LinkedReviewAction.Hold, PlatformInvoiceRules.ReviewLinked(true, false, false, "INV-1").Action);
        var refuse = PlatformInvoiceRules.ReviewLinked(false, false, false, "INV-1");
        Assert.Equal(PlatformInvoiceRules.LinkedReviewAction.Refuse, refuse.Action);
        Assert.Contains("บันทึกรับเงิน", refuse.Message);
        // ใบต่ออายุที่ถูกแทนที่: ระบบ = พัก · แอดมิน = ตัดหนี้อย่างเดียว (ไม่ต่ออายุซ้ำ)
        Assert.Equal(PlatformInvoiceRules.LinkedReviewAction.Hold, PlatformInvoiceRules.ReviewLinked(true, true, true, "INV-2").Action);
        Assert.Equal(PlatformInvoiceRules.LinkedReviewAction.InvoiceOnly, PlatformInvoiceRules.ReviewLinked(false, true, true, "INV-2").Action);
        // ทิศตรงข้าม: ใบเปิด + ใบปัจจุบัน ⇒ ผ่านทั้งสองทาง
        Assert.Equal(PlatformInvoiceRules.LinkedReviewAction.Proceed, PlatformInvoiceRules.ReviewLinked(true, true, false, "INV-3").Action);
        Assert.Equal(PlatformInvoiceRules.LinkedReviewAction.Proceed, PlatformInvoiceRules.ReviewLinked(false, true, false, "INV-3").Action);
    }

    [Fact]
    public void SupersededRenewal_only_for_renewal_reference_that_is_not_current_pointer()
    {
        var id = Guid.NewGuid();
        Assert.NotNull(PlatformInvoiceRules.SupersededRenewalReason("RENEW-ABC", id, null));
        Assert.NotNull(PlatformInvoiceRules.SupersededRenewalReason("RENEW-ABC", id, Guid.NewGuid()));
        Assert.Null(PlatformInvoiceRules.SupersededRenewalReason("RENEW-ABC", id, id));          // ใบปัจจุบัน
        Assert.Null(PlatformInvoiceRules.SupersededRenewalReason("USAGE-2026-09", id, null));     // ใบค่าใช้งานไม่เกี่ยว
        Assert.Null(PlatformInvoiceRules.SupersededRenewalReason(null, id, null));
    }

    [Fact]
    public void CapToBalance_never_settles_more_than_balance_and_reports_excess()
    {
        Assert.Equal((1040m, 30m, 0m), PlatformInvoiceRules.CapToBalance(1040m, 30m, 1070m));   // โอน + ภาษีหัก = ยอดค้างพอดี
        Assert.Equal((500m, 0m, 100m), PlatformInvoiceRules.CapToBalance(600m, 0m, 500m));     // รับเกิน 100 ⇒ ไม่ตัดเกิน
        Assert.Equal((0m, 0m, 990m), PlatformInvoiceRules.CapToBalance(990m, 0m, 0m));         // ใบไม่มียอดค้างแล้ว ⇒ เกินทั้งก้อน
        Assert.Equal((300m, 0m, 0m), PlatformInvoiceRules.CapToBalance(300m, 0m, 990m));       // จ่ายบางส่วน (ไม่ใช่ส่วนเกิน)
    }

    [Fact]
    public void WithholdingProblem_juristic_only_capped_at_statutory_service_rate()
    {
        // ใบ 1,070 (ก่อน VAT 1,000) ค้างเต็ม
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(null, 1070m, 1070m, 1000m, false));
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(0m, 1070m, 1070m, 1000m, false));
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(30m, 1070m, 1070m, 1000m, true));     // 3% ของฐานก่อน VAT 1,000
        // ฝ่ายค้านรอบสุดท้าย 4b: เดิมยอมถึง 32.10 (3% ของยอดรวม VAT) — ตอนนี้ฐานก่อน VAT ⇒ เกิน
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(32.10m, 1070m, 1070m, 1000m, true));
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(30m, 1070m, 1070m, 1000m, false)); // บุคคลธรรมดาหักไม่ได้
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(50m, 1070m, 1070m, 1000m, true));  // เกินอัตรา
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(-1m, 1070m, 1070m, 1000m, true));
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(1070m, 1070m, 1070m, 1000m, true));
        // ค้างครึ่งใบ ⇒ ฐานก่อน VAT ตามสัดส่วน 500 ⇒ ไม่เกิน 15
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(15m, 535m, 1070m, 1000m, true));
        Assert.NotNull(PlatformInvoiceRules.WithholdingProblem(16m, 535m, 1070m, 1000m, true));
        Assert.Null(PlatformInvoiceRules.WithholdingProblem(32.10m, 1070m, 1070m, 0m, true));   // ไม่รู้ยอดก่อน VAT ⇒ ฐาน = ยอดค้าง
    }

    [Fact]
    public void StatusAfterPayment_keeps_admin_and_dunning_suspension_lifts_expiry_suspension()
    {
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.PastDue, false, true, null));
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.Trial, false, true, null));
        // ระงับโดยผู้ดูแลระบบ/ไม่รู้เหตุ ⇒ การจ่ายไม่ปลด
        Assert.Equal(SubscriptionStatus.Suspended, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.Suspended, false, true, "AdminStatusChanged"));
        Assert.Equal(SubscriptionStatus.Suspended, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.Suspended, false, true, null));
        // ระงับโดยระบบทวงหนี้ ⇒ คงไว้ให้ dunning คืน (ทางคืนอัตโนมัติทางเดียว) · ไม่มีบริการทวงหนี้ ⇒ เปิด (เดิม)
        Assert.Equal(SubscriptionStatus.Suspended, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.Suspended, true, true, "PlatformDunningSuspended"));
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.Suspended, true, false, "PlatformDunningSuspended"));
        // ระงับเพราะหมดอายุเกินกำหนด ⇒ จ่ายแล้วเปิด
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.StatusAfterPayment(SubscriptionStatus.Suspended, false, true, PlatformDunningPolicy.ExpirySuspendAction));
    }

    [Fact]
    public void RestoreStatus_follows_current_end_date_not_stale_status_before_suspend()
    {
        // เดิม PastDue ก่อนถูกระงับ แล้วจ่ายต่ออายุ (EndDate อนาคต) ⇒ Active (เดิมคืน PastDue)
        Assert.Equal(SubscriptionStatus.Active, PlatformDunningPolicy.RestoreStatus(SubscriptionStatus.PastDue, Now.AddDays(30), Now, false));
        Assert.Equal(SubscriptionStatus.PastDue, PlatformDunningPolicy.RestoreStatus(SubscriptionStatus.PastDue, Now.AddDays(-1), Now, false));
    }

    [Fact]
    public void Dunning_excludes_paid_awaiting_us_superseded_and_pending_unlinked_renewal_slip()
    {
        Assert.True(PlatformDunningPolicy.Excluded(true, false, false, false, false));    // อนุมัติแล้วแต่ตัดชำระไม่สำเร็จ/สลิปรอตรวจ
        Assert.True(PlatformDunningPolicy.Excluded(false, false, false, true, false));    // ใบต่ออายุที่ถูกแทนที่
        Assert.True(PlatformDunningPolicy.Excluded(false, true, true, false, false));     // ใบต่ออายุของรอบที่ยกเลิก
        Assert.True(PlatformDunningPolicy.Excluded(false, false, true, false, true));     // สลิปต่ออายุทางหน้าแพ็กเกจรอตรวจ
        // ทิศตรงข้าม: ใบปกติที่ค้างจริงยังทวง · สลิปต่ออายุไม่ช่วยใบค่าใช้งาน
        Assert.False(PlatformDunningPolicy.Excluded(false, false, true, false, false));
        Assert.False(PlatformDunningPolicy.Excluded(false, false, false, false, true));
        Assert.False(PlatformDunningPolicy.Excluded(false, true, false, false, false));
    }
    [Fact]
    public void Addon_catch_up_only_after_unpaid_upfront_that_was_already_billed_this_period()
    {
        Assert.True(AddOnPaymentPolicy.NeedsCatchUpAfterSwitch(AddOnPaymentStatus.AwaitingPayment, "2026-10", "2026-10", "2026-10"));
        Assert.True(AddOnPaymentPolicy.NeedsCatchUpAfterSwitch(AddOnPaymentStatus.Rejected, "2026-10", "2026-10", "2026-10"));
        // ทิศตรงข้าม: ยังไม่ออกบิลงวดนี้ (งานรายเดือนจะเก็บเอง) · จ่ายแล้ว/สลิปรอตรวจ · ไม่เคยเลือกจ่ายล่วงหน้า
        Assert.False(AddOnPaymentPolicy.NeedsCatchUpAfterSwitch(AddOnPaymentStatus.AwaitingPayment, "2026-10", "2026-09", "2026-10"));
        Assert.False(AddOnPaymentPolicy.NeedsCatchUpAfterSwitch(AddOnPaymentStatus.Paid, "2026-10", "2026-10", "2026-10"));
        Assert.False(AddOnPaymentPolicy.NeedsCatchUpAfterSwitch(AddOnPaymentStatus.PendingReview, "2026-10", "2026-10", "2026-10"));
        Assert.False(AddOnPaymentPolicy.NeedsCatchUpAfterSwitch(AddOnPaymentStatus.AwaitingPayment, null, "2026-10", "2026-10"));
    }
    [Fact]
    public void Billing_payment_path_exempt_only_for_subscription_payment_intents()
    {
        var c = Guid.NewGuid(); var p = Guid.NewGuid(); var i = Guid.NewGuid();
        Assert.True(SubscriptionGatePolicy.IsBillingPaymentPath($"/api/companies/{c}/platform-pay/subscription-payments/{p}/intents"));
        Assert.True(SubscriptionGatePolicy.IsBillingPaymentPath($"/api/companies/{c}/platform-pay/subscription-payments/{p}/intents/{i}"));
        // ทิศตรงข้าม: เส้นอื่นของบริษัทที่ถูกระงับยังถูกด่านแพ็กเกจตามเดิม
        Assert.False(SubscriptionGatePolicy.IsBillingPaymentPath($"/api/companies/{c}/platform-pay/addons/x/intents"));
        Assert.False(SubscriptionGatePolicy.IsBillingPaymentPath($"/api/companies/{c}/platform-pay/policy-acceptance"));
        Assert.False(SubscriptionGatePolicy.IsBillingPaymentPath($"/api/companies/{c}/documents"));
        Assert.False(SubscriptionGatePolicy.IsBillingPaymentPath($"/api/companies/{c}/platform-pay/subscription-payments/{p}/intents/{i}/refund"));
        Assert.False(SubscriptionGatePolicy.IsBillingPaymentPath(null));
    }

    [Fact]
    public void Renewal_void_on_cancel_only_for_invoice_of_the_cancelled_cycle()
    {
        var id = Guid.NewGuid(); var end = Now.AddDays(-1);
        Assert.Equal(id, PlatformInvoiceRules.RenewalVoidOnCancel(id, end, end));
        Assert.Null(PlatformInvoiceRules.RenewalVoidOnCancel(id, end.AddMonths(-1), end));   // ใบของรอบอื่น
        Assert.Null(PlatformInvoiceRules.RenewalVoidOnCancel(null, end, end));
    }

    [Fact]
    public void Paylink_view_audit_window_and_intent_status_scope()
    {
        // ลิงก์ใช้ได้ ⇒ audit ครั้งแรกต่อวันไทย (เที่ยงคืนไทย = 17:00 UTC ของวันก่อน)
        Assert.Equal(new DateTime(2026, 10, 29, 17, 0, 0, DateTimeKind.Utc), PlatformPaymentLinkRules.ViewAuditSince(true, Now));
        Assert.Equal(DateTime.MinValue, PlatformPaymentLinkRules.ViewAuditSince(false, Now));      // ใช้ไม่ได้ ⇒ ครั้งเดียวตลอด
        Assert.Equal("\"state\":\"Valid\"", PlatformPaymentLinkRules.ViewAuditMarker("Valid"));
        Assert.True(PlatformPaymentLinkRules.IntentStatusAllowed(null, Now.AddDays(1), Now));
        Assert.False(PlatformPaymentLinkRules.IntentStatusAllowed(Now.AddHours(-1), Now.AddDays(1), Now));   // เพิกถอน
        Assert.False(PlatformPaymentLinkRules.IntentStatusAllowed(null, Now.AddMinutes(-1), Now));          // หมดอายุ
    }

    [Fact]
    public void Admin_linked_amounts_never_exceed_balance_and_cash_must_be_positive()
    {
        Assert.Null(PlatformInvoiceRules.AdminLinkedAmountsProblem(1040m, 30m, 1070m));
        Assert.Null(PlatformInvoiceRules.AdminLinkedAmountsProblem(535m, 0m, 1070m));     // จ่ายบางส่วน (แอดมินยืดหยุ่น)
        Assert.NotNull(PlatformInvoiceRules.AdminLinkedAmountsProblem(1070m, 30m, 1070m)); // เกินยอดค้าง
        Assert.NotNull(PlatformInvoiceRules.AdminLinkedAmountsProblem(0m, 30m, 1070m));
        Assert.NotNull(PlatformInvoiceRules.AdminLinkedAmountsProblem(100m, -1m, 1070m));
    }

    [Fact]
    public void Invoice_claim_paid_awaiting_platform_covers_slip_held_and_unsettled_approval()
    {
        Assert.True(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.Pending, true, true, false, false));
        Assert.True(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.UnderReview, true, false, true, false));
        Assert.True(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.Approved, true, false, false, false));   // อนุมัติแล้วรอตัดชำระ
        // ทิศตรงข้าม: ตัดชำระแล้ว · รายการไม่มีหลักฐาน · ปฏิเสธ · ไม่ผูกใบ
        Assert.False(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.Approved, true, false, false, true));
        Assert.False(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.Pending, true, false, false, false));
        Assert.False(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.Rejected, true, true, false, false));
        Assert.False(PlatformInvoiceClaims.IsAwaitingPlatform(SubscriptionPaymentStatus.Approved, false, false, false, false));
    }

    [Fact]
    public void Wht_certificate_outstanding_only_for_online_payment_with_withholding()
    {
        Assert.True(PlatformPaymentAttention.WhtCertificateOutstanding(true, 30m, null));
        Assert.False(PlatformPaymentAttention.WhtCertificateOutstanding(false, 30m, null));   // แอดมินเห็น 50 ทวิ กับสลิปแล้ว
        Assert.False(PlatformPaymentAttention.WhtCertificateOutstanding(true, 0m, null));
        Assert.False(PlatformPaymentAttention.WhtCertificateOutstanding(true, 30m, "cert.pdf"));
    }
}
