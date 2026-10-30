using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 213 (คำตัดสินข้อ 172 · เจ้าของ: "มีหน้าให้จัดการ ถ้าเติมโควต้าแล้ว สามารถส่งซ้ำทีหลังได้" + "แอดมินตั้งโควตา ราคา แพ็กเกจ จำนวนข้อความได้") —
/// ตัวตัดสินส่งซ้ำ <see cref="CustomerNotifyResend"/> · รหัสเหตุระงับ <see cref="NotifySuppressReason"/> · คีย์เลื่อนนัดอ่านกลับได้ ·
/// แพ็ก <c>notify.topup</c> ที่แอดมินตั้ง (<see cref="NotifyQuotaPolicy.TopUpPack"/>) · เพดานเฉพาะบริษัท · ซ่อมแถวเก่า (migration) ·
/// ทุกกลุ่มล็อกทั้ง "ส่งซ้ำได้" และทิศตรงข้าม
/// </summary>
public class CmsRound213NotifyResendTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowLocal = BookingSlotEngine.BangkokNow(Now);
    private static readonly Guid Site = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static CustomerNotifyResendTarget FutureBooking(BookingStatus s = BookingStatus.Confirmed)
        => new(true, Site, "การจอง BK-1", BookingStatus: s, BookingStartLocal: NowLocal.AddDays(2));

    private static CustomerNotifyResendInput In(string eventKey, CustomerNotifyResendTarget target, string status = NotifyLogStatus.Suppressed,
        string? reason = NotifySuppressReason.QuotaFull, int ageDays = 1, bool enabled = true, bool superseded = false)
        => new(eventKey, status, reason, Now.AddDays(-ageDays), enabled, superseded, target);

    private static CustomerNotifyResendVerdict Eval(CustomerNotifyResendInput i) => CustomerNotifyResend.Evaluate(i, Now, NowLocal);

    // ═════════ 1. ส่งซ้ำได้ ═════════

    [Fact]
    public void Quota_suppressed_confirmation_for_upcoming_booking_is_resendable()
    {
        var v = Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking()));
        Assert.True(v.Resendable);
        Assert.Equal(CustomerNotifyResend.CodeOk, v.Code);
        Assert.True(Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(BookingStatus.Waitlisted))).Resendable);
        Assert.True(Eval(In(CustomerNotifyEvents.BookingCancelled, FutureBooking(BookingStatus.Cancelled))).Resendable);
        Assert.True(Eval(In(CustomerNotifyEvents.OrderShipped, new(true, Site, "คำสั่งซื้อ SO-1", OrderStatus: SiteOrderStatus.Shipped))).Resendable);
        Assert.True(Eval(In(CustomerNotifyEvents.OrderConfirmation, new(true, Site, "คำสั่งซื้อ SO-1", OrderStatus: SiteOrderStatus.Processing))).Resendable);
    }

    // ═════════ 2. ทิศตรงข้าม — ทุกเหตุที่ห้ามส่งซ้ำ ═════════

    [Fact]
    public void Only_quota_full_rows_are_resendable_shop_switched_off_rows_are_not()
    {
        var off = Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(), reason: NotifySuppressReason.EventOff));
        Assert.False(off.Resendable);
        Assert.Equal(CustomerNotifyResend.CodeNotQuota, off.Code);
        Assert.False(Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(), reason: null)).Resendable);
        var sent = Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(), status: NotifyLogStatus.Sent, reason: null));
        Assert.Equal(CustomerNotifyResend.CodeNotPending, sent.Code);
    }

    [Fact]
    public void Older_than_30_days_event_now_off_or_superseded_is_not_resendable()
    {
        Assert.True(Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(), ageDays: 29)).Resendable);
        Assert.Equal(CustomerNotifyResend.CodeTooOld, Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(), ageDays: 31)).Code);
        Assert.Equal(CustomerNotifyResend.CodeEventOff, Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(), enabled: false)).Code);
        Assert.Equal(CustomerNotifyResend.CodeSuperseded, Eval(In(CustomerNotifyEvents.BookingRescheduled, FutureBooking(), superseded: true)).Code);
    }

    [Fact]
    public void Message_must_still_match_current_state_of_the_target()
    {
        // การจองถูกยกเลิกแล้ว ⇒ ห้ามส่ง "ยืนยันการจอง"
        var cancelled = Eval(In(CustomerNotifyEvents.BookingConfirmed, FutureBooking(BookingStatus.Cancelled)));
        Assert.Equal(CustomerNotifyResend.CodeTargetStale, cancelled.Code);
        // ยืนยันแล้ว ⇒ ห้ามส่ง "ร้านยกเลิก"
        Assert.Equal(CustomerNotifyResend.CodeTargetStale, Eval(In(CustomerNotifyEvents.BookingCancelled, FutureBooking())).Code);
        // เลยเวลานัด ⇒ ไม่ส่ง
        var past = new CustomerNotifyResendTarget(true, Site, "การจอง BK-1", BookingStatus: BookingStatus.Confirmed, BookingStartLocal: NowLocal.AddHours(-1));
        Assert.Equal(CustomerNotifyResend.CodeTargetStale, Eval(In(CustomerNotifyEvents.BookingConfirmed, past)).Code);
        // คำสั่งซื้อส่งมอบแล้ว ⇒ "จัดส่งแล้ว" เก่าไม่ส่ง · ยกเลิก ⇒ "ยืนยันคำสั่งซื้อ" ไม่ส่ง
        Assert.Equal(CustomerNotifyResend.CodeTargetStale,
            Eval(In(CustomerNotifyEvents.OrderShipped, new(true, Site, "คำสั่งซื้อ SO-1", OrderStatus: SiteOrderStatus.Delivered))).Code);
        Assert.Equal(CustomerNotifyResend.CodeTargetStale,
            Eval(In(CustomerNotifyEvents.OrderConfirmation, new(true, Site, "คำสั่งซื้อ SO-1", OrderStatus: SiteOrderStatus.Cancelled))).Code);
        // รายการถูกลบ
        Assert.Equal(CustomerNotifyResend.CodeTargetMissing, Eval(In(CustomerNotifyEvents.BookingConfirmed, CustomerNotifyResendTarget.Missing)).Code);
    }

    [Theory]
    [InlineData(CustomerNotifyEvents.BookingReminder)]
    [InlineData(CustomerNotifyEvents.BookingWaitlistPromoted)]
    [InlineData(CustomerNotifyEvents.CartAbandoned)]
    [InlineData(CustomerNotifyEvents.LodgingConfirmed)]
    [InlineData(CustomerNotifyEvents.CreditExpiring)]
    [InlineData(CustomerNotifyEvents.FormAutoReply)]
    [InlineData(CustomerNotifyEvents.ClassCancelled)]
    public void Events_without_a_rebuild_path_are_listed_with_a_reason_not_hidden(string key)
    {
        Assert.Null(CustomerNotifyResend.TargetKindOf(key));
        var v = Eval(In(key, FutureBooking()));
        Assert.False(v.Resendable);
        Assert.Equal(CustomerNotifyResend.CodeUnsupported, v.Code);
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
    }

    [Fact]
    public void Every_supported_event_has_a_target_kind_and_every_catalog_event_has_an_answer()
    {
        foreach (var e in CustomerNotifyEvents.Catalog)
            Assert.True(CustomerNotifyResend.IsSupported(e.Key) ^ CustomerNotifyResend.UnsupportedReason(e.Key) != null, e.Key);
        Assert.Equal(CustomerLineLinkRule.KindBooking, CustomerNotifyResend.TargetKindOf(CustomerNotifyEvents.BookingRescheduled));
        Assert.Equal(CustomerLineLinkRule.KindOrder, CustomerNotifyResend.TargetKindOf(CustomerNotifyEvents.OrderDelivered));
    }

    // ═════════ 3. คีย์เลื่อนนัด: ส่งซ้ำต้องได้คีย์เดิม (claim แถวเดิม) ═════════

    [Fact]
    public void Rescheduled_key_round_trips_to_the_same_key()
    {
        var id = Guid.NewGuid();
        var oldDate = new DateTime(2026, 11, 5);
        var oldStart = new TimeOnly(9, 30);
        var key = CustomerNotifyKeys.BookingRescheduled(id, oldDate, oldStart);
        Assert.True(CustomerNotifyKeys.TryParseRescheduled(key, out var d, out var t));
        Assert.Equal(key, CustomerNotifyKeys.BookingRescheduled(id, d, t));
        // ทิศตรงข้าม: คีย์ชนิดอื่นอ่านไม่ได้
        Assert.False(CustomerNotifyKeys.TryParseRescheduled(CustomerNotifyKeys.Booking(id, CustomerNotifyEvents.BookingConfirmed), out _, out _));
        Assert.False(CustomerNotifyKeys.TryParseRescheduled(null, out _, out _));
        Assert.False(CustomerNotifyKeys.TryParseRescheduled($"order:{id:N}:booking.rescheduled:202611050930", out _, out _));
    }

    [Fact]
    public void Result_message_tells_the_way_forward_when_quota_is_still_full()
    {
        var stopped = CustomerNotifyResend.ResultMessage(2, 0, 1, 5, stoppedForQuota: true);
        Assert.Contains("ส่งซ้ำสำเร็จ 2", stopped);
        Assert.Contains("ซื้อแพ็กข้อความเพิ่ม", stopped);
        Assert.Contains("5", stopped);
        Assert.DoesNotContain("ซื้อแพ็ก", CustomerNotifyResend.ResultMessage(3, 0, 0, 0, stoppedForQuota: false));
    }

    // ═════════ 4. รหัสเหตุ + ซ่อมแถวเก่า ═════════

    [Fact]
    public void Legacy_backfill_matches_the_exact_texts_the_notifier_wrote()
    {
        Assert.StartsWith(CustomerNotifyResendMigration.QuotaFullNotePrefix, NotifyQuotaPolicy.FullReason(300));
        Assert.StartsWith(CustomerNotifyResendMigration.QuotaFullNotePrefix, CustomerNotifyLogRule.Redact(NotifyQuotaPolicy.FullReason(1000))!);
        Assert.DoesNotContain("%", CustomerNotifyResendMigration.QuotaFullNotePrefix);
        var sql = string.Join("\n", CustomerNotifyResendMigration.Statements());
        Assert.Contains("\"SuppressReason\" IS NULL", sql);   // รันซ้ำไม่ทับแถวที่มีรหัสแล้ว
        Assert.Contains(NotifySuppressReason.QuotaFull, sql);
        Assert.Contains(CustomerNotifyPolicy.SuppressedByPolicyReason.Replace("'", "''"), sql);
        Assert.DoesNotContain("UPDATE \"ApiFeatures\"", sql);   // ไม่ตั้งแพ็กให้เอง (ค่าตั้งต้น = เดิม)
    }

    // ═════════ 5. แพ็ก notify.topup ที่แอดมินตั้ง + เพดานเฉพาะบริษัท ═════════

    [Fact]
    public void Topup_pack_defaults_to_500_messages_60_days_and_admin_values_win()
    {
        var def = NotifyQuotaPolicy.TopUpPack(null, null);
        Assert.Equal(AddOnCodes.MessagesPerNotifyTopUpPack, def.Size);
        Assert.Equal(AddOnCodes.TopUpValidDays, def.ValidDays);
        Assert.Equal(new NotifyTopUpPack(1000, 90), NotifyQuotaPolicy.TopUpPack(1000, 90));
        // ค่านอกช่วงที่หลุดเข้าฐาน ⇒ ไม่ให้ 0 ข้อความ/หมดอายุทันที (ถอยไปค่าตั้งต้น)
        Assert.Equal(def, NotifyQuotaPolicy.TopUpPack(0, -3));
        // documents.topup ไม่แตะ — ค่าคงที่เดิม
        Assert.Equal(100, AddOnCodes.DocumentsPerTopUpPack);
        Assert.Equal(60, AddOnCodes.TopUpValidDays);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(500, 60, true)]
    [InlineData(1, 1, true)]
    [InlineData(0, null, false)]
    [InlineData(100_001, null, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, 731, false)]
    public void Admin_pack_validation(int? size, int? days, bool ok)
        => Assert.Equal(ok, NotifyQuotaPolicy.ValidatePack(size, days) == null);

    [Fact]
    public void Unit_label_is_built_from_pack_size()
    {
        Assert.Equal("แพ็ก 500 ข้อความ", NotifyQuotaPolicy.PackUnitLabel(500));
        Assert.Equal("แพ็ก 1,000 ข้อความ", NotifyQuotaPolicy.PackUnitLabel(1000));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(300, true)]
    [InlineData(-2, false)]
    public void Company_override_validation(int? value, bool ok)
        => Assert.Equal(ok, NotifyQuotaPolicy.ValidateOverride(value) == null);

    [Fact]
    public void Admin_summary_says_where_the_limit_comes_from()
    {
        Assert.Contains("ตามแพ็กเกจ", NotifyQuotaPolicy.AdminSummary(null, 10, 300, 0, null));
        Assert.Contains("ค่าเฉพาะบริษัท 50", NotifyQuotaPolicy.AdminSummary(50, 10, 50, 0, null));
        Assert.Contains("ไม่จำกัด", NotifyQuotaPolicy.AdminSummary(-1, 10, -1, 0, null));
        var withBonus = NotifyQuotaPolicy.AdminSummary(null, 10, 300, 500, new DateTime(2026, 12, 29, 17, 30, 0, DateTimeKind.Utc));
        Assert.Contains("โบนัสคงเหลือ 500", withBonus);
        Assert.Contains("2026-12-30", withBonus);   // วันหมดอายุตามเวลาไทย
        // ค่าเฉพาะบริษัทชนะแพ็กเกจ (ผู้อ่านเชิงธุรกิจตัวเดียว PlanLimit)
        Assert.Equal(50, NotifyQuotaPolicy.PlanLimit(50, 300, 100, isTrial: false));
        Assert.Equal(NotifyQuotaPolicy.Unlimited, NotifyQuotaPolicy.PlanLimit(-1, 300, 100, isTrial: false));
    }
}
