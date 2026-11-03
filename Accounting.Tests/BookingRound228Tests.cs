using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Cms;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 228 (TEST_PLAN BKPAY-70…79 · <c>erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md</c> §9) — ตัวตัดสินล้วน:
/// <list type="bullet">
///   <item>228-1 มัดจำไม่บังคับ — เลือกชำระภายหลัง (ยอดเดียวกับติ๊กตอนจอง · ทิศตรงข้าม: บังคับ/เลือกแล้ว/รับเงินแล้ว/บัตรคอร์ส/เลยเวลา)</item>
///   <item>228-3 เปลี่ยนวิธีรับ/ที่อยู่: หน้าต่างจากค่าตั้ง · ส่วนต่างค่าส่ง (ยังไม่จ่าย ⇒ มัดจำเต็มจำนวนขยับ · จ่ายแล้ว ⇒ ค้างเพิ่ม/หัก · ต้องคืนเงิน ⇒ ปฏิเสธ)</item>
///   <item>บัญชีรายได้ค่าจัดส่ง · โซนชนะโซนถัดไป (วงระยะทางด้วยรหัสไปรษณีย์) · ข้อความจากร้านในอีเมล/LINE · แม่แบบสั่งทำ/พรีออเดอร์</item>
/// </list>
/// </summary>
public class BookingRound228Tests
{
    private static readonly DateTime Now = new(2026, 11, 2, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = Now.AddDays(3);

    private static BookingPaymentEffective Eff(BookingPaymentMode mode, decimal? pct = 50m)
        => BookingPaymentPolicy.Resolve(BookingType.PrePayment, new BookingPaymentRuleInput(mode, null, pct, null, null, null), BookingPaymentRuleInput.None);

    // ═════════ 228-1 ชำระมัดจำภายหลัง ═════════

    [Fact]
    public void มัดจำไม่บังคับที่ไม่ได้ติ๊ก_เลือกชำระภายหลังได้_ยอดเท่าติ๊กตอนจอง()
    {
        var e = Eff(BookingPaymentMode.DepositOptional);
        var d = BookingDepositLater.Decide(e, BookingType.PrePayment, BookingStatus.Confirmed, 0m, 0m, 650m, 50m, Start, Now, false);
        Assert.True(d.Show); Assert.True(d.Allowed);
        Assert.Equal(325m, d.Amount);   // 50% ของค่าบริการ — ค่าส่งไม่อยู่ในฐานมัดจำ (226-4)
        Assert.Equal(BookingPaymentPolicy.AmountDue(e, 650m, customerOptsIn: true, 50m), d.Amount);
        Assert.Contains("฿325.00", BookingDepositLater.Offer(d.Amount));
        Assert.Contains("ลิงก์จัดการการจอง", BookingDepositLater.NotifyLine(d.Amount));
    }

    [Fact]
    public void ทิศตรงข้าม_ชำระภายหลัง_ไม่แสดง_หรือปฏิเสธพร้อมเหตุ()
    {
        var opt = Eff(BookingPaymentMode.DepositOptional);
        // โหมดบังคับ/ปิด — ไม่ใช่เรื่องของปุ่มนี้
        Assert.False(BookingDepositLater.Decide(Eff(BookingPaymentMode.DepositRequired), BookingType.PrePayment, BookingStatus.Pending, 325m, 0m, 650m, 0m, Start, Now, false).Show);
        // ชนิดที่ตรึงบนการจองไม่ใช่ชำระล่วงหน้า (§78/1)
        Assert.False(BookingDepositLater.Decide(opt, BookingType.Appointment, BookingStatus.Confirmed, 0m, 0m, 650m, 0m, Start, Now, false).Show);
        // เลือกไว้แล้ว ⇒ กล่องชำระปกติ
        Assert.False(BookingDepositLater.Decide(opt, BookingType.PrePayment, BookingStatus.Confirmed, 325m, 0m, 650m, 0m, Start, Now, false).Show);
        // ยกเลิก/คิวสำรอง/บัตรคอร์ส
        Assert.False(BookingDepositLater.Decide(opt, BookingType.PrePayment, BookingStatus.Cancelled, 0m, 0m, 650m, 0m, Start, Now, false).Show);
        Assert.False(BookingDepositLater.Decide(opt, BookingType.PrePayment, BookingStatus.Waitlisted, 0m, 0m, 650m, 0m, Start, Now, false).Show);
        Assert.False(BookingDepositLater.Decide(opt, BookingType.PrePayment, BookingStatus.Confirmed, 0m, 0m, 650m, 0m, Start, Now, paidWithCredit: true).Show);
        // รับเงินแล้ว / เลยเวลานัด ⇒ บอกเหตุ (ไม่เงียบ)
        var paid = BookingDepositLater.Decide(opt, BookingType.PrePayment, BookingStatus.Confirmed, 0m, 100m, 650m, 0m, Start, Now, false);
        Assert.True(paid.Show); Assert.False(paid.Allowed); Assert.NotNull(paid.Problem);
        var late = BookingDepositLater.Decide(opt, BookingType.PrePayment, BookingStatus.Confirmed, 0m, 0m, 650m, 0m, Now.AddMinutes(-1), Now, false);
        Assert.False(late.Allowed); Assert.Contains("เลยเวลานัด", late.Problem);
        // ร้านไม่ได้ตั้งยอด ⇒ ไม่มีปุ่ม
        Assert.False(BookingDepositLater.Decide(Eff(BookingPaymentMode.DepositOptional, pct: null), BookingType.PrePayment, BookingStatus.Confirmed, 0m, 0m, 650m, 0m, Start, Now, false).Show);
    }

    // ═════════ 228-3 เปลี่ยนวิธีรับ/ที่อยู่ ═════════

    private static BookingFulfillmentChangeFacts Facts(decimal oldFee, decimal newFee, decimal oldDep = 0m, decimal newDep = 0m, decimal paid = 0m,
        int? cutoff = 24, BookingLinkedDocKind doc = BookingLinkedDocKind.None, bool slip = false, bool gateway = false, bool credit = false,
        BookingStatus status = BookingStatus.Confirmed, DateTime? start = null, bool pos = false)
        => new(status, start ?? Start, Now, cutoff, 650m, oldFee, newFee, oldDep, newDep, paid, slip, gateway, doc, credit, pos);

    [Fact]
    public void หน้าต่างเปลี่ยนวิธีรับ_ไม่ตั้ง_ไม่เปิด_ตั้งแล้วเปิดถึงกำหนด_เลยกำหนดปิดพร้อมเหตุ()
    {
        Assert.False(BookingFulfillmentChange.Window(null, BookingStatus.Confirmed, Start, Now).Enabled);   // ค่าเดิม = ติดต่อร้าน
        var open = BookingFulfillmentChange.Window(24, BookingStatus.Confirmed, Start, Now);
        Assert.True(open.Enabled && open.Open); Assert.Equal(Start.AddHours(-24), open.DeadlineUtc);
        var closed = BookingFulfillmentChange.Window(24, BookingStatus.Confirmed, Now.AddHours(10), Now);
        Assert.True(closed.Enabled); Assert.False(closed.Open); Assert.Contains("ติดต่อร้าน", closed.Note);
        Assert.False(BookingFulfillmentChange.Window(24, BookingStatus.Completed, Start, Now).Enabled);
        Assert.Null(BookingFulfillmentChange.ValidateCutoff(0));
        Assert.NotNull(BookingFulfillmentChange.ValidateCutoff(-1));
        Assert.NotNull(BookingFulfillmentChange.ValidateCutoff(BookingFulfillmentChange.MaxCutoffHours + 1));
    }

    [Fact]
    public void ค่าส่งเท่าเดิม_เปลี่ยนได้เสมอ_เงินเอกสารไม่ขยับ_แม้ออกใบแล้วรับเงินแล้ว()
    {
        var d = BookingFulfillmentChange.Decide(Facts(50m, 50m, oldDep: 700m, newDep: 700m, paid: 700m, doc: BookingLinkedDocKind.Other));
        Assert.True(d.Allowed); Assert.Equal(700m, d.DepositAmount); Assert.False(d.DiscardDraftDeposit);
        Assert.Contains("เท่าเดิม", d.Effect);
    }

    [Fact]
    public void ยังไม่จ่าย_ชำระเต็มจำนวน_ค่าส่งเปลี่ยน_มัดจำขยับ_ใบร่างที่ยอดไม่ตรงถูกลบ()
    {
        // รับเอง (0) → จัดส่ง 80 · มัดจำเต็ม 650 → 730 · ใบรับมัดจำร่างยอดเดิม ⇒ ลบ (ออกใหม่ตอนเงินเข้า)
        var d = BookingFulfillmentChange.Decide(Facts(0m, 80m, oldDep: 650m, newDep: 730m, doc: BookingLinkedDocKind.DepositDraft));
        Assert.True(d.Allowed); Assert.Equal(730m, d.DepositAmount); Assert.True(d.DiscardDraftDeposit);
        Assert.Contains("฿650.00 → ฿730.00", d.Effect);
        // ทิศตรงข้าม: มัดจำ % (ฐานค่าบริการ) — ใบร่างไม่ครอบค่าส่ง ⇒ ไม่ลบ
        var pct = BookingFulfillmentChange.Decide(Facts(0m, 80m, oldDep: 325m, newDep: 325m, doc: BookingLinkedDocKind.DepositDraft));
        Assert.True(pct.Allowed); Assert.Equal(325m, pct.DepositAmount); Assert.False(pct.DiscardDraftDeposit);
    }

    [Fact]
    public void จ่ายแล้ว_ค่าส่งเพิ่ม_ค้างเพิ่มชำระตอนรับของ_ค่าส่งลดจนต้องคืนเงิน_ปฏิเสธ()
    {
        var up = BookingFulfillmentChange.Decide(Facts(0m, 80m, oldDep: 325m, newDep: 325m, paid: 325m, doc: BookingLinkedDocKind.DepositIssued));
        Assert.True(up.Allowed); Assert.Equal(325m, up.DepositAmount);   // ไม่แก้มัดจำ/ใบที่ออกแล้ว
        Assert.Contains("ยอดคงเหลือ ฿405.00", up.Effect);
        // ชำระเต็ม 730 (รวมส่ง 80) → รับเอง ⇒ เกินยอดใหม่ 650 ต้องคืน 80 ⇒ ติดต่อร้าน (ระบบไม่ประทับ "ค้างคืน" เอง)
        var refund = BookingFulfillmentChange.Decide(Facts(80m, 0m, oldDep: 730m, newDep: 650m, paid: 730m, doc: BookingLinkedDocKind.DepositIssued));
        Assert.False(refund.Allowed); Assert.Contains("คืนเงินส่วนต่าง ฿80.00", refund.Problem);
    }

    [Fact]
    public void ค่าส่งเปลี่ยน_ปฏิเสธเมื่อ_สลิปรอตรวจ_ชำระออนไลน์ค้าง_ใบเสนอราคาหรือใบกำกับรวมค่าส่งแล้ว_บัตรคอร์ส_เลยกำหนด()
    {
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 80m, slip: true)).Allowed);
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 80m, gateway: true)).Allowed);
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 80m, doc: BookingLinkedDocKind.Other)).Allowed);
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 80m, credit: true)).Allowed);
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 80m, cutoff: null)).Allowed);
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 80m, start: Now.AddHours(5))).Allowed);
        Assert.False(BookingFulfillmentChange.Decide(Facts(0m, 0m, pos: true)).Allowed);   // บิล POS ตรึงค่าส่งแล้ว — แม้ค่าส่งเท่าเดิม (ที่อยู่ในบิลไม่ตาม)
        // ทิศตรงข้าม: ไม่มีเอกสาร ไม่มีเงิน ⇒ ผ่าน
        Assert.True(BookingFulfillmentChange.Decide(Facts(0m, 80m)).Allowed);
    }

    [Fact]
    public void ฝ่ายค้าน_S1_มัดจำใหม่จากข้อเท็จจริงที่ตรึง_ชำระเต็มเดิมตามยอดรวมใหม่_มัดจำเปอร์เซ็นต์คงเดิม_ไม่เกินยอดรวมใหม่()
    {
        // จองตอนโหมดชำระเต็ม (650 + ส่ง 80 = 730) แล้วเจ้าของเปลี่ยนโหมดเว็บภายหลัง — ค่าส่งลดเป็น 30 ⇒ ยอดรวมใหม่ 680 (ไม่ดูโหมดปัจจุบัน)
        Assert.Equal(680m, BookingFulfillmentChange.NewDeposit(730m, 650m, 80m, 30m));
        Assert.Equal(325m, BookingFulfillmentChange.NewDeposit(325m, 650m, 80m, 30m));     // มัดจำ 50% ฐานค่าบริการ — ไม่ขยับ
        Assert.Equal(0m, BookingFulfillmentChange.NewDeposit(0m, 650m, 0m, 80m));          // มัดจำไม่บังคับที่ไม่เลือก
        // Decide เพดานยอดรวมใหม่ทั้งสองทาง (ยังไม่จ่าย / จ่ายแล้ว)
        Assert.Equal(680m, BookingFulfillmentChange.Decide(Facts(80m, 30m, oldDep: 730m, newDep: 900m)).DepositAmount);
        var paid = BookingFulfillmentChange.Decide(Facts(80m, 30m, oldDep: 730m, newDep: 730m, paid: 600m, doc: BookingLinkedDocKind.DepositIssued));
        Assert.True(paid.Allowed); Assert.Equal(680m, paid.DepositAmount);
    }

    [Fact]
    public void ฝ่ายค้าน_S2_S8_ใบรับมัดจำรออนุมัติหรือมีเลขแล้ว_ถือว่าออกแล้ว_ต้องให้ร้านปรับ_ลบได้เฉพาะร่างDRAFT()
    {
        Assert.Equal(BookingLinkedDocKind.DepositDraft, BookingFulfillmentChange.DepositDocKind(DocumentStatus.Draft, "DRAFT-abc"));
        Assert.Equal(BookingLinkedDocKind.DepositIssued, BookingFulfillmentChange.DepositDocKind(DocumentStatus.WaitingApproval, "DRAFT-abc"));
        Assert.Equal(BookingLinkedDocKind.DepositIssued, BookingFulfillmentChange.DepositDocKind(DocumentStatus.Draft, "RD-2611-0001"));
        var d = BookingFulfillmentChange.Decide(Facts(0m, 80m, oldDep: 650m, newDep: 730m, doc: BookingLinkedDocKind.DepositIssued));
        Assert.False(d.Allowed); Assert.Contains("ร้านต้องปรับเอกสาร", d.Problem);
    }

    // ═════════ บัญชีรายได้ค่าจัดส่ง ═════════

    [Fact]
    public void บัญชีรายได้ค่าจัดส่ง_ต้องเป็นหมวดรายได้ของบริษัทนี้ที่ใช้งาน()
    {
        var cid = Guid.NewGuid();
        var ok = new ChartOfAccount { CompanyId = cid, AccountCode = "41300", AccountName = "รายได้ค่าขนส่ง", AccountType = AccountType.Revenue, IsActive = true };
        Assert.Null(ShippingRevenueAccount.Problem(ok, cid));
        Assert.NotNull(ShippingRevenueAccount.Problem(null, cid));
        Assert.NotNull(ShippingRevenueAccount.Problem(ok, Guid.NewGuid()));   // ข้ามบริษัท
        Assert.NotNull(ShippingRevenueAccount.Problem(new ChartOfAccount { CompanyId = cid, AccountCode = "52100", AccountName = "ค่าขนส่ง", AccountType = AccountType.Expense, IsActive = true }, cid));
        Assert.NotNull(ShippingRevenueAccount.Problem(new ChartOfAccount { CompanyId = cid, AccountCode = "41300", AccountName = "x", AccountType = AccountType.Revenue, IsActive = false }, cid));
        Assert.Null(ShippingRevenueAccount.StaleWarning(null, null, cid));     // ไม่ตั้ง = ไม่เตือน (เดิม)
        Assert.NotNull(ShippingRevenueAccount.StaleWarning(Guid.NewGuid(), null, cid));
    }

    // ═════════ โซนชนะโซนถัดไป (วงระยะทาง/อำเภอด้วยรหัสไปรษณีย์) ═════════

    private static SiteShippingZone Zone(string name, string postal, decimal fee, bool stop, int sort, decimal? maxKg = null)
    {
        var z = new SiteShippingZone { Name = name, PostalCodePatterns = postal, IsActive = true, StopWhenMatched = stop, SortOrder = sort };
        z.Rates.Add(new SiteShippingRate { Name = name, RateType = ShippingRateType.Flat, BaseRate = fee, IsActive = true, MaxWeightKg = maxKg });
        return z;
    }

    [Fact]
    public void ฝ่ายค้าน_S5_โซนชนะที่ไม่มีอัตราใช้ได้_ตกไปโซนถัดไป_S4_คำเตือนลำดับซ้ำ()
    {
        var near = Zone("ใกล้ร้าน", "10110", 30m, stop: true, 1, maxKg: 1m);   // วงในส่งได้ไม่เกิน 1 กก.
        var city = Zone("กรุงเทพฯ", "10*", 60m, stop: false, 2);
        var addr = new CalculateShippingRequest { Province = "กรุงเทพมหานคร", PostalCode = "10110" };
        var heavy = ShopDeliveryFee.Compute(new[] { near, city }, addr, 500m, 5m, null, null, false, false, null, true);
        Assert.Null(heavy.Problem); Assert.Equal(60m, heavy.Fee);   // ไม่ใช่ "ไม่มีวิธีจัดส่ง"
        var light = ShopDeliveryFee.Compute(new[] { near, city }, addr, 500m, 0.5m, null, null, false, false, null, true);
        Assert.Single(light.Options); Assert.Equal(30m, light.Fee);
        Assert.NotNull(ShopDeliveryFee.StopZoneOrderWarning(true, 1, new[] { 1, 2 }));
        Assert.Null(ShopDeliveryFee.StopZoneOrderWarning(true, 1, new[] { 2 }));
        Assert.Null(ShopDeliveryFee.StopZoneOrderWarning(false, 1, new[] { 1 }));
    }

    [Fact]
    public void โซนใกล้ร้านชนะโซนถัดไป_ลูกค้าวงในไม่เห็นอัตราวงนอก_ทิศตรงข้ามไม่ติ๊กได้ทุกโซนเหมือนเดิม()
    {
        var near = Zone("ใกล้ร้าน", "10110,10260", 30m, stop: true, 1);
        var city = Zone("กรุงเทพฯ", "10*", 60m, stop: false, 2);
        var addr = new CalculateShippingRequest { Province = "กรุงเทพมหานคร", PostalCode = "10110" };
        var r = ShopDeliveryFee.Compute(new[] { near, city }, addr, 500m, 0m, null, null, false, false, null, true);
        Assert.Single(r.Options); Assert.Equal(30m, r.Fee);
        // วงนอก (ไม่ตรงโซนใกล้) ⇒ โซนถัดไป
        var far = ShopDeliveryFee.Compute(new[] { near, city }, new CalculateShippingRequest { Province = "กรุงเทพมหานคร", PostalCode = "10500" }, 500m, 0m, null, null, false, false, null, true);
        Assert.Equal(60m, far.Fee);
        // ไม่ติ๊ก ⇒ เดิม: สองโซนตรง ⇒ ให้ลูกค้าเลือก
        near.StopWhenMatched = false;
        var both = ShopDeliveryFee.Compute(new[] { near, city }, addr, 500m, 0m, null, null, false, false, null, true);
        Assert.Equal(2, both.Options.Count); Assert.Equal("กรุณาเลือกวิธีจัดส่ง", both.Problem);
    }

    // ═════════ ข้อความจากร้านในอีเมล/LINE ═════════

    [Fact]
    public void ข้อความจากร้าน_ต่อท้ายบรรทัดชำระ_ไม่มีบรรทัดชำระไม่เพิ่ม_ยาวเกินตัด()
    {
        var line = BookingPaymentPolicy.NotifyLine(325m, null, true, false);
        Assert.Contains("📝 ข้อความจากร้าน: โอนแล้วแจ้งชื่อในแชต", BookingPaymentPolicy.WithShopNote(line, " โอนแล้วแจ้งชื่อในแชต "));
        Assert.Equal(line, BookingPaymentPolicy.WithShopNote(line, "  "));
        Assert.Null(BookingPaymentPolicy.WithShopNote(null, "โอนแล้วแจ้ง"));   // ไม่มียอด ⇒ ไม่มีบรรทัดชำระ ⇒ ไม่ส่งข้อความชำระ
        var longNote = new string('ก', BookingPaymentPolicy.NotifyNoteMaxLength + 50);
        Assert.EndsWith("…", BookingPaymentPolicy.WithShopNote(line, longNote));
        // ฝ่ายค้าน N3: ไม่ตัดกลางอีโมจิ (คู่ surrogate)
        var emoji = new string('ก', BookingPaymentPolicy.NotifyNoteMaxLength - 1) + "🎂🎂";
        var cut = BookingPaymentPolicy.WithShopNote(line, emoji)!;
        Assert.False(char.IsHighSurrogate(cut[^2]));
    }

    // ═════════ แม่แบบสั่งทำ / พรีออเดอร์ ═════════

    [Fact]
    public void แม่แบบสั่งทำพรีออเดอร์_บริการชำระมัดจำล่วงหน้า_จัดส่งรับเอง_ช่องฟอร์มถูกต้อง()
    {
        var mto = CmsBookingServiceSeeder.Presets.Single(p => p.Key == "mto");
        Assert.NotNull(mto.Setup);
        Assert.Null(BookingFulfillmentChange.ValidateCutoff(mto.Setup!.ChangeCutoffHours));
        Assert.Null(BookingFormFields.ValidateConfig(new BookingFormFieldsConfig(CustomFields: mto.Setup.CustomFields)));
        foreach (var s in mto.Samples)
        {
            var req = CmsBookingServiceSeeder.ToRequest(s);
            Assert.Equal(BookingType.PrePayment, req.BookingType);
            Assert.True(req.DepositPercent > 0m);
            Assert.True(req.MinAdvanceBookingHours >= 24);   // lead time
            Assert.Equal("Custom", req.FulfillmentMode);
            Assert.True(req.FulfillmentDeliveryEnabled && req.FulfillmentPickupEnabled);
            var svc = new SiteBookingService();
            CmsBookingService.ApplySettings(svc, req);
            Assert.True(svc.FulfillmentDeliveryEnabled == true && svc.FulfillmentPickupEnabled == true);
        }
        // ทิศตรงข้าม: แม่แบบเดิมไม่ตั้งวิธีรับ (ตามค่าเว็บ) และไม่มีปุ่มตั้งค่าเว็บ
        var spa = CmsBookingServiceSeeder.PresetResponses().Single(p => p.Key == "spa");
        Assert.Null(spa.SiteSetupSummary);
        Assert.All(spa.Services, r => Assert.Null(r.FulfillmentMode));
        Assert.NotNull(CmsBookingServiceSeeder.PresetResponses().Single(p => p.Key == "mto").SiteSetupSummary);
    }

    // ═════════ CMS_SETTINGS_UX_AUDIT แพ็ก A / C (รอบ 228) ═════════

    [Fact]
    public void แพ็กA_ทางเข้าเดิมไม่ล้างรูปQRและชื่อธนาคารที่ไม่ได้ส่ง_ส่งว่างคือล้าง()
    {
        Assert.Equal("https://x/qr.png", SiteTransferAccount.KeepUnlessSent("https://x/qr.png", null));   // modal เดิมไม่ส่งรูป QR ⇒ ยังอยู่
        Assert.Null(SiteTransferAccount.KeepUnlessSent("https://x/qr.png", ""));                         // ตั้งใจล้าง
        Assert.Equal("กสิกรไทย", SiteTransferAccount.KeepUnlessSent(null, " กสิกรไทย "));
    }

    [Fact]
    public void แพ็กA_ชื่อธนาคารนอกรายการคงไว้ในตัวเลือก_ไม่หายตอนบันทึก()
    {
        var opts = SiteTransferAccount.BankNameOptions("กสิกรไทย");
        Assert.Equal("กสิกรไทย", opts[0]);
        Assert.Equal(SiteTransferAccount.BankNames.Count + 1, opts.Count);
        Assert.Equal(SiteTransferAccount.BankNames.Count, SiteTransferAccount.BankNameOptions("ธนาคารกสิกรไทย (KBANK)").Count);   // อยู่ในรายการแล้ว ไม่ซ้ำ
        Assert.Equal(SiteTransferAccount.BankNames.Count, SiteTransferAccount.BankNameOptions(null).Count);
    }

    [Fact]
    public void แพ็กA_ที่พักให้แขกโอนแต่เว็บไม่มีบัญชี_เตือน_ทิศตรงข้ามไม่เตือน()
    {
        Assert.NotNull(SiteTransferAccount.LodgingWarning(siteLinked: true, guestMustPay: true, transferPayable: false, onlineReady: false));
        Assert.Null(SiteTransferAccount.LodgingWarning(true, true, transferPayable: true, onlineReady: false));
        Assert.Null(SiteTransferAccount.LodgingWarning(true, true, false, onlineReady: true));
        Assert.Null(SiteTransferAccount.LodgingWarning(true, guestMustPay: false, false, false));   // จองสำเร็จทันที ไม่ต้องโอน
        Assert.Null(SiteTransferAccount.LodgingWarning(siteLinked: false, true, false, false));
        Assert.True(SiteTransferAccount.Payable(new BookingTransferInfo(null, null, null, null, null, "https://x/qr.png"), false));
        Assert.False(SiteTransferAccount.Payable(new BookingTransferInfo("กสิกรไทย", null, null, null, "0812345678", null), promptPayValid: false));
    }

    [Fact]
    public void แพ็กC_Q3_เปิดจัดส่งไม่ตั้งค่าส่ง_เตือนค้างจนยืนยันส่งฟรี()
    {
        Assert.NotNull(ShopDeliveryFee.FreeByOmissionWarning(deliveryOn: true, zoneCount: 0, flatRate: null, freeConfirmed: false));
        Assert.Null(ShopDeliveryFee.FreeByOmissionWarning(true, 0, null, freeConfirmed: true));   // ตั้งใจส่งฟรี
        Assert.Null(ShopDeliveryFee.FreeByOmissionWarning(true, 0, 40m, false));
        Assert.Null(ShopDeliveryFee.FreeByOmissionWarning(true, 2, null, false));
        Assert.Null(ShopDeliveryFee.FreeByOmissionWarning(deliveryOn: false, 0, null, false));
    }
}

