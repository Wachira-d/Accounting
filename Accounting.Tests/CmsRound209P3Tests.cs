using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 P3 — ลำดับสถานะออเดอร์ (A6) · สถานะพร้อมให้รับ (A5) · รับเงินหน้าร้าน (E7) · คืนเงิน ⇒ ใบลดหนี้ร่าง (E8 · ข้อ 168 ฉ) · ข้อความแจ้งเตือน (ข้อ 168 ซ) ·
/// ทุกกลุ่มล็อกทั้ง "เคสที่ถูกกัน" และ "เคสเดิมที่ยังผ่าน" (ปุ่มเดิมบนหน้าออเดอร์ทุกปุ่ม)
/// </summary>
public class CmsRound209P3Tests
{
    private static PaymentDue Paid(decimal total = 1000m) => MtoPaymentTerms.FromSnapshot(PaymentTermMode.Full, total, total, total);
    private static PaymentDue Unpaid(decimal total = 1000m) => MtoPaymentTerms.FromSnapshot(PaymentTermMode.Full, total, total, 0m);

    // ═════════ enum — ต่อท้าย ไม่เรียงเลขใหม่ (ค่าเก็บเป็นตัวเลขในฐาน) ═════════

    [Fact]
    public void ReadyForPickup_appended_without_renumbering()
    {
        Assert.Equal(8, (int)SiteOrderStatus.ReadyForPickup);
        Assert.Equal(4, (int)SiteOrderStatus.Delivered);
        Assert.Equal(7, (int)SiteOrderStatus.PartiallyRefunded);
    }

    [Fact]
    public void ReadyForPickup_is_not_handover_so_no_final_invoice_or_stock()
    {
        Assert.False(SiteOrderDepositInvoicing.IsHandoverStatus(SiteOrderStatus.ReadyForPickup));
        Assert.True(SiteOrderDepositInvoicing.IsHandoverStatus(SiteOrderStatus.Delivered));
    }

    // ═════════ A6 — ปุ่มเดิมทุกปุ่มยังผ่าน (ทิศตรงข้ามของการเข้มขึ้น) ═════════

    [Theory]
    [InlineData(SiteOrderStatus.Pending, SiteOrderStatus.Confirmed)]
    [InlineData(SiteOrderStatus.Pending, SiteOrderStatus.Cancelled)]
    [InlineData(SiteOrderStatus.Confirmed, SiteOrderStatus.Processing)]
    [InlineData(SiteOrderStatus.Confirmed, SiteOrderStatus.Delivered)]
    [InlineData(SiteOrderStatus.Confirmed, SiteOrderStatus.Cancelled)]
    [InlineData(SiteOrderStatus.Processing, SiteOrderStatus.Shipped)]
    [InlineData(SiteOrderStatus.Processing, SiteOrderStatus.Delivered)]
    [InlineData(SiteOrderStatus.Processing, SiteOrderStatus.Cancelled)]
    [InlineData(SiteOrderStatus.Shipped, SiteOrderStatus.Delivered)]
    [InlineData(SiteOrderStatus.Delivered, SiteOrderStatus.Refunded)]
    [InlineData(SiteOrderStatus.PartiallyRefunded, SiteOrderStatus.Refunded)]
    [InlineData(SiteOrderStatus.Processing, SiteOrderStatus.ReadyForPickup)]
    [InlineData(SiteOrderStatus.ReadyForPickup, SiteOrderStatus.Delivered)]
    [InlineData(SiteOrderStatus.ReadyForPickup, SiteOrderStatus.Shipped)]
    [InlineData(SiteOrderStatus.Delivered, SiteOrderStatus.PartiallyRefunded)]
    public void Existing_and_new_forward_buttons_allowed_when_paid(SiteOrderStatus from, SiteOrderStatus to)
        => Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(from, to, Paid()).Result);

    [Fact]
    public void Shipping_unpaid_is_still_allowed_only_delivered_is_guarded()
        => Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Processing, SiteOrderStatus.Shipped, Unpaid()).Result);

    [Theory]
    [InlineData(SiteOrderStatus.Pending)]
    [InlineData(SiteOrderStatus.Confirmed)]
    [InlineData(SiteOrderStatus.Processing)]
    [InlineData(SiteOrderStatus.ReadyForPickup)]
    [InlineData(SiteOrderStatus.Shipped)]
    public void Delivered_unpaid_full_mode_is_rejected_with_way_forward(SiteOrderStatus from)
    {
        var (r, msg) = SiteOrderStatusTransition.Check(from, SiteOrderStatus.Delivered, Unpaid());
        Assert.Equal(BookingTransitionResult.Rejected, r);
        Assert.Contains("บันทึกรับเงินหน้าร้าน/ปลายทาง", msg);
        Assert.Contains("1,000.00", msg);
    }

    [Fact]
    public void Delivered_pay_on_pickup_unpaid_is_allowed()
    {
        var due = MtoPaymentTerms.FromSnapshot(PaymentTermMode.PayOnPickup, 1000m, 0m, 0m);
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Pending, SiteOrderStatus.Delivered, due).Result);
    }

    [Fact]
    public void Delivered_cod_unpaid_is_allowed()
        => Assert.Equal(BookingTransitionResult.Allowed,
            SiteOrderStatusTransition.Check(SiteOrderStatus.Shipped, SiteOrderStatus.Delivered, Unpaid(), isCod: true).Result);

    [Fact]
    public void Delivered_deposit_mode_needs_deposit_only_balance_goes_to_final_invoice()
    {
        var depositPaid = MtoPaymentTerms.FromSnapshot(PaymentTermMode.DepositPercent, 1000m, 300m, 300m);
        var depositMissing = MtoPaymentTerms.FromSnapshot(PaymentTermMode.DepositPercent, 1000m, 300m, 100m);
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.ReadyForPickup, SiteOrderStatus.Delivered, depositPaid).Result);
        Assert.Equal(BookingTransitionResult.Rejected, SiteOrderStatusTransition.Check(SiteOrderStatus.ReadyForPickup, SiteOrderStatus.Delivered, depositMissing).Result);
    }

    [Theory]
    [InlineData(SiteOrderStatus.Shipped, SiteOrderStatus.Processing)]
    [InlineData(SiteOrderStatus.Shipped, SiteOrderStatus.ReadyForPickup)]
    [InlineData(SiteOrderStatus.Delivered, SiteOrderStatus.Shipped)]
    [InlineData(SiteOrderStatus.Delivered, SiteOrderStatus.Confirmed)]
    [InlineData(SiteOrderStatus.Delivered, SiteOrderStatus.Cancelled)]
    [InlineData(SiteOrderStatus.PartiallyRefunded, SiteOrderStatus.Cancelled)]
    [InlineData(SiteOrderStatus.Confirmed, SiteOrderStatus.Pending)]
    [InlineData(SiteOrderStatus.Cancelled, SiteOrderStatus.Confirmed)]
    [InlineData(SiteOrderStatus.Refunded, SiteOrderStatus.PartiallyRefunded)]
    public void Backward_past_handover_or_out_of_closed_is_rejected(SiteOrderStatus from, SiteOrderStatus to)
    {
        var (r, msg) = SiteOrderStatusTransition.Check(from, to, Paid());
        Assert.Equal(BookingTransitionResult.Rejected, r);
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }

    [Theory]
    [InlineData(SiteOrderStatus.Processing, SiteOrderStatus.Confirmed)]
    [InlineData(SiteOrderStatus.ReadyForPickup, SiteOrderStatus.Processing)]
    public void Backward_within_preparation_is_allowed_to_fix_misclick(SiteOrderStatus from, SiteOrderStatus to)
        => Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(from, to, Paid()).Result);

    [Fact]
    public void Refund_needs_money_received()
    {
        Assert.Equal(BookingTransitionResult.Rejected, SiteOrderStatusTransition.Check(SiteOrderStatus.Confirmed, SiteOrderStatus.Refunded, Unpaid()).Result);
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Confirmed, SiteOrderStatus.Refunded, Paid()).Result);
    }

    [Fact]
    public void Graph_only_check_without_payment_keeps_round208_behaviour()
    {
        Assert.Equal(BookingTransitionResult.NoOp, SiteOrderStatusTransition.Check(SiteOrderStatus.Cancelled, SiteOrderStatus.Cancelled).Result);
        Assert.Equal(BookingTransitionResult.Allowed, SiteOrderStatusTransition.Check(SiteOrderStatus.Pending, SiteOrderStatus.Delivered).Result);
    }

    // ═════════ NextActions — ปุ่มจากเซิร์ฟเวอร์ ═════════

    [Fact]
    public void NextActions_pickup_processing_offers_ready_for_pickup_first()
    {
        var a = SiteOrderStatusTransition.NextActions(SiteOrderStatus.Processing, FulfillmentMethod.Pickup, Paid());
        Assert.Equal("ReadyForPickup", a[0].Status);
        Assert.True(a[0].Primary);
        Assert.DoesNotContain(a, x => x.Status == "Shipped");
    }

    [Fact]
    public void NextActions_delivery_processing_ship_asks_tracking_and_keeps_old_buttons()
    {
        var a = SiteOrderStatusTransition.NextActions(SiteOrderStatus.Processing, FulfillmentMethod.Delivery, Paid());
        Assert.Equal(new[] { "Shipped", "Delivered", "Cancelled" }, a.Select(x => x.Status).ToArray());
        Assert.True(a[0].AsksTracking);
        Assert.All(a, x => Assert.Null(x.DisabledReason));
    }

    [Fact]
    public void NextActions_unpaid_delivered_button_is_disabled_with_server_reason()
    {
        var a = SiteOrderStatusTransition.NextActions(SiteOrderStatus.ReadyForPickup, FulfillmentMethod.Pickup, Unpaid());
        var deliver = a.Single(x => x.Status == "Delivered");
        Assert.Contains("บันทึกรับเงิน", deliver.DisabledReason);
        Assert.Null(a.Single(x => x.Status == "Cancelled").DisabledReason);
    }

    [Fact]
    public void NextActions_delivered_offers_full_and_partial_refund_asking_amount()
    {
        var a = SiteOrderStatusTransition.NextActions(SiteOrderStatus.Delivered, null, Paid());
        Assert.Equal(new[] { "Refunded", "PartiallyRefunded" }, a.Select(x => x.Status).ToArray());
        Assert.All(a, x => Assert.True(x.AsksRefundAmount));
        Assert.Empty(SiteOrderStatusTransition.NextActions(SiteOrderStatus.Cancelled, null, Paid()));
    }

    [Theory]
    [InlineData(SiteOrderStatus.Confirmed, 500, true)]
    [InlineData(SiteOrderStatus.ReadyForPickup, 0.004, false)]
    [InlineData(SiteOrderStatus.Cancelled, 500, false)]
    [InlineData(SiteOrderStatus.PartiallyRefunded, 500, false)]
    [InlineData(SiteOrderStatus.Delivered, 200, true)]
    public void Counter_payment_only_on_open_order_with_balance(SiteOrderStatus s, double balance, bool expected)
        => Assert.Equal(expected, SiteOrderStatusTransition.AcceptsCounterPayment(s, (decimal)balance));

    // ═════════ E8 — แผนใบลดหนี้ของการคืนเงิน (ข้อ 168 ฉ) ═════════

    private static readonly RefundSourceLine Cake = new("เค้กวันเกิด", 1m, "ชิ้น", 1000m, 0m, 0m, 7m, null, null);
    private static readonly RefundSourceLine Ship = new("ค่าจัดส่ง", 1m, "ครั้ง", 70m, 0m, 0m, 7m, null, null);
    private static readonly (string Description, decimal Fee)[] ShipParts = { ("ค่าจัดส่ง", 70m) };

    [Fact]
    public void Refund_before_shipping_copies_whole_invoice_including_shipping()
    {
        var p = SiteOrderRefundPlan.Plan(new[] { Cake, Ship }, 1070m, 0m, ShipParts, excludeShipping: false, 0m, 1070m, null, fullRefund: true);
        Assert.Null(p.Problem);
        Assert.Equal(1070m, p.Amount);
        Assert.Equal(1m, p.Ratio);
        Assert.Equal(2, p.Lines.Count);
        Assert.Equal(70m, p.Lines[1].UnitPrice);
    }

    [Fact]
    public void Refund_after_shipping_excludes_shipping_line()
    {
        Assert.True(SiteOrderRefundPlan.ExcludesShipping(DateTime.UtcNow, null));
        var p = SiteOrderRefundPlan.Plan(new[] { Cake, Ship }, 1070m, 0m, ShipParts, excludeShipping: true, 0m, 1070m, null, fullRefund: true);
        Assert.Null(p.Problem);
        Assert.Equal(1000m, p.Amount);
        Assert.Equal(70m, p.ShippingExcluded);
        Assert.Single(p.Lines);
        Assert.Equal("เค้กวันเกิด", p.Lines[0].Description);
    }

    [Fact]
    public void Product_named_like_shipping_but_different_price_is_not_excluded()
    {
        var oddly = new RefundSourceLine("ค่าจัดส่ง", 1m, "ชิ้น", 999m, 0m, 0m, 7m, null, null);
        var p = SiteOrderRefundPlan.Plan(new[] { oddly, Ship }, 1069m, 0m, ShipParts, excludeShipping: true, 0m, 1069m, null, fullRefund: true);
        Assert.Single(p.Lines);
        Assert.Equal(999m, p.Lines[0].UnitPrice);
    }

    [Fact]
    public void Partial_refund_scales_prices_and_discounts_by_ratio()
    {
        var discounted = Cake with { DiscountAmount = 100m };
        var p = SiteOrderRefundPlan.Plan(new[] { discounted }, 900m, 20m, Array.Empty<(string, decimal)>(), false, 0m, 900m, 450m, fullRefund: false);
        Assert.Null(p.Problem);
        Assert.Equal(0.5m, p.Ratio);
        Assert.Equal(500m, p.Lines[0].UnitPrice);
        Assert.Equal(50m, p.Lines[0].DiscountAmount);
        Assert.Equal(10m, p.BillDiscount);
        Assert.Equal(1m, p.Lines[0].Quantity);
    }

    [Fact]
    public void Partial_refund_without_amount_or_over_refundable_is_a_problem()
    {
        Assert.NotNull(SiteOrderRefundPlan.Plan(new[] { Cake }, 1000m, 0m, ShipParts, false, 0m, 1000m, null, fullRefund: false).Problem);
        var over = SiteOrderRefundPlan.Plan(new[] { Cake, Ship }, 1070m, 0m, ShipParts, true, 0m, 1070m, 1050m, fullRefund: false);
        Assert.Contains("เกินยอดที่คืนได้", over.Problem);
        Assert.Equal(1000m, over.Refundable);
    }

    [Fact]
    public void Already_credited_reduces_refundable_and_full_credit_blocks_second_draft()
    {
        var p = SiteOrderRefundPlan.Plan(new[] { Cake }, 1000m, 0m, ShipParts, false, 400m, 1000m, null, fullRefund: true);
        Assert.Equal(600m, p.Amount);
        var none = SiteOrderRefundPlan.Plan(new[] { Cake }, 1000m, 0m, ShipParts, false, 1000m, 1000m, null, fullRefund: true);
        Assert.Contains("ไม่ร่างซ้ำ", none.Problem);
    }

    [Fact]
    public void Refund_capped_by_money_received()
    {
        var p = SiteOrderRefundPlan.Plan(new[] { Cake }, 1000m, 0m, ShipParts, false, 0m, 300m, null, fullRefund: true);
        Assert.Equal(300m, p.Amount);
        Assert.Equal(0.3m, p.Ratio);
        Assert.Equal(300m, p.Lines[0].UnitPrice);
    }

    [Fact]
    public void Only_shipping_left_after_exclusion_is_a_problem()
        => Assert.NotNull(SiteOrderRefundPlan.Plan(new[] { Ship }, 70m, 0m, ShipParts, true, 0m, 70m, null, true).Problem);

    [Fact]
    public void Refund_draft_reason_does_not_return_stock()
        => Assert.Equal(CreditNoteReason.Adjustment, SiteOrderRefundPlan.DraftReason);

    [Fact]
    public void Shipping_parts_descriptions_match_invoice_line_builder()
    {
        var parts = ShippingVatAllocation.Split(ShippingVatAllocation.Proportional, true, 100m, new[] { (500m, 7m), (500m, 0m) });
        var d = SiteOrderRefundPlan.ShippingPartsOf(parts);
        Assert.Equal(2, d.Count);
        Assert.All(d, x => Assert.StartsWith("ค่าจัดส่ง (", x.Description));
        Assert.Equal(100m, d.Sum(x => x.Fee));
    }

    // ═════════ ข้อความแจ้งเตือน (ข้อ 168 ซ) ═════════

    [Fact]
    public void ParseEmails_dedupes_and_drops_garbage()
    {
        var e = CmsNotificationText.ShopRecipients("a@shop.co; A@SHOP.CO, not-an-email\nb@x.th  @bad.com c@nodot", null, null);
        Assert.Equal(new[] { "a@shop.co", "b@x.th" }, e.ToArray());
        Assert.Equal(new[] { "x@y.co" }, CmsNotificationText.ShopRecipients("not-an-email", "x@y.co", null).ToArray());
    }

    [Fact]
    public void ShopRecipients_fallback_chain()
    {
        Assert.Equal(new[] { "o@s.com" }, CmsNotificationText.ShopRecipients("o@s.com", "site@s.com", "co@s.com").ToArray());
        Assert.Equal(new[] { "site@s.com" }, CmsNotificationText.ShopRecipients("  ", "site@s.com", "co@s.com").ToArray());
        Assert.Equal(new[] { "co@s.com" }, CmsNotificationText.ShopRecipients(null, null, "co@s.com").ToArray());
        Assert.Empty(CmsNotificationText.ShopRecipients(null, null, null));
    }

    [Fact]
    public void Shop_order_notify_switch_defaults_on_and_respects_off()
    {
        Assert.True(CmsNotificationText.ShopOrderNotifyEnabled(null));
        Assert.True(CmsNotificationText.ShopOrderNotifyEnabled(true));
        Assert.False(CmsNotificationText.ShopOrderNotifyEnabled(false));
    }

    [Fact]
    public void Customer_status_message_only_for_shipped_ready_delivered()
    {
        var shipped = CmsNotificationText.CustomerOrderStatus(SiteOrderStatus.Shipped, "ร้านเค้ก", "WEB-1", "สมใจ", "Kerry", "TH123", 0m);
        Assert.NotNull(shipped);
        Assert.Contains("TH123", shipped!.Text);
        Assert.Contains("Kerry", shipped.Text);
        var ready = CmsNotificationText.CustomerOrderStatus(SiteOrderStatus.ReadyForPickup, "ร้านเค้ก", "WEB-1", null, null, null, 250m);
        Assert.Contains("250.00", ready!.Text);
        Assert.Contains("ลูกค้า", ready.Text);
        Assert.NotNull(CmsNotificationText.CustomerOrderStatus(SiteOrderStatus.Delivered, "ร้าน", "WEB-1", "x", null, null, 0m));
        Assert.Null(CmsNotificationText.CustomerOrderStatus(SiteOrderStatus.Processing, "ร้าน", "WEB-1", "x", null, null, 0m));
        Assert.Null(CmsNotificationText.CustomerOrderStatus(SiteOrderStatus.Cancelled, "ร้าน", "WEB-1", "x", null, null, 0m));
    }

    [Fact]
    public void Order_confirmation_carries_shop_message_and_amount_due()
    {
        var m = CmsNotificationText.CustomerOrderConfirmation("ร้านเค้ก", "WEB-9", "สมชาย", 1070m, 321m, "มารับเองที่ร้าน", new DateOnly(2026, 11, 6), "ขอบคุณค่ะ รับของ 10:00–18:00");
        Assert.Contains("321.00", m.Text);
        Assert.Contains("ขอบคุณค่ะ รับของ", m.Text);
        Assert.Contains("2569", m.Text);   // พ.ศ.
        Assert.DoesNotContain("ยอดที่ต้องชำระ", CmsNotificationText.CustomerOrderConfirmation("ร้าน", "W", null, 100m, 0m, "จัดส่ง", null, null).Text);
    }

    [Fact]
    public void Booking_messages_cover_waitlist_and_shop_cancel_reason()
    {
        var w = CmsNotificationText.CustomerBookingConfirmation("สตูดิโอ", "B-1", "พิลาทิส", "แอน", new DateOnly(2026, 11, 2), new TimeOnly(18, 0), null, BookingStatus.Waitlisted);
        Assert.Contains("คิวสำรอง", w.Subject);
        var c = CmsNotificationText.CustomerBookingCancelledByShop("สตูดิโอ", "B-1", "พิลาทิส", "แอน", new DateOnly(2026, 11, 2), new TimeOnly(18, 0), "ครูป่วย");
        Assert.Contains("ครูป่วย", c.Text);
        Assert.Contains("18:00", c.Text);
        Assert.Contains("คิวสำรอง", CmsNotificationText.ShopNewBooking("สตูดิโอ", "B-2", "โยคะ", null, null, new DateOnly(2026, 11, 2), new TimeOnly(9, 0), 2, true).Subject);
    }

    [Fact]
    public void Lead_and_quotation_messages()
    {
        var l = CmsNotificationText.ShopNewLead("โรงงาน", "L-2610-0001", LeadType.Rfq, "บริษัท ก", "a@b.co", "081", new string('x', 800));
        Assert.Contains("คำขอใบเสนอราคา", l.Subject);
        Assert.Contains("…", l.Text);   // ข้อความยาวถูกตัด
        Assert.Contains("ได้งาน", CmsNotificationText.ShopQuotationAccepted("QT-1", "คุณเอ", 5000m, "L-1").Text);
        Assert.DoesNotContain("lead", CmsNotificationText.ShopQuotationAccepted("QT-1", "คุณเอ", 5000m, null).Text);
    }

    [Fact]
    public void User_data_is_html_encoded_by_the_email_channel()
    {
        var m = CmsNotificationText.CustomerOrderConfirmation("<b>ร้าน</b>", "W-1", "<script>alert(1)</script>", 1m, 0m, "จัดส่ง", null, null);
        var html = BookingReminderPlan.ToHtml(m.Text);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Failure_note_is_tagged_and_carries_real_reason()
    {
        var n = CmsNotificationText.FailureNote("ลูกค้า", "จัดส่งแล้ว", "อีเมล: ส่งไม่สำเร็จ", new DateTime(2026, 10, 29, 3, 0, 0, DateTimeKind.Utc));
        Assert.StartsWith(CmsNotificationText.FailureTag, n);
        Assert.Contains("อีเมล: ส่งไม่สำเร็จ", n);
    }
}
