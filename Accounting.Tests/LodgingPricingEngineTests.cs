using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>ตัวคำนวณราคาที่พัก — logic เงินต้องมีเทสต์ล็อกตัวเลขก่อนต่อ UI (กฎเหล็ก #4 G)
/// ตัวเลขในนี้จำลองจากรีสอร์ทจริง (TakeTime: High season ×1.25 · Songkran ×1.50 ·
/// Weekend ×1.20 · มัดจำ 50%)</summary>
public class LodgingPricingEngineTests
{
    private static readonly DateTime Mon = new(2026, 9, 7);   // จันทร์

    private static LodgingRoomPricingInput Input(
        decimal baseRate = 1500m, decimal weekendMult = 1.20m,
        IReadOnlyList<LodgingSeasonInput>? seasons = null,
        IReadOnlyList<LodgingOverrideInput>? overrides = null,
        LodgingRatePlanInput? plan = null,
        LodgingPricingMode mode = LodgingPricingMode.PerUnit,
        int stdOcc = 2, decimal extraGuest = 500m, decimal extraBed = 800m)
        => new(baseRate, mode, stdOcc, extraGuest, extraBed, weekendMult, 32 | 64,
            seasons ?? Array.Empty<LodgingSeasonInput>(),
            overrides ?? Array.Empty<LodgingOverrideInput>(), plan);

    [Fact]
    public void วันธรรมดา_ราคาฐานตรงตัว_และวันเช็คเอาต์ไม่นับเป็นคืน()
    {
        var q = LodgingPricingEngine.QuoteRoom(Mon, Mon.AddDays(2), 2, 0, 0, Input());
        Assert.Equal(2, q.Nights.Count);
        Assert.Equal(3000m, q.RoomRate);
        Assert.Equal(3000m, q.Subtotal);
    }

    [Fact]
    public void สุดสัปดาห์_คูณตัวคูณเฉพาะศุกร์เสาร์()
    {
        // พฤ 10 → อา 13: คืน พฤ(1500) ศ(1800) ส(1800)
        var q = LodgingPricingEngine.QuoteRoom(new DateTime(2026, 9, 10), new DateTime(2026, 9, 13), 2, 0, 0, Input());
        Assert.Equal(new[] { 1500m, 1800m, 1800m }, q.Nights.Select(n => n.Rate).ToArray());
        Assert.Equal(5100m, q.RoomRate);
    }

    [Fact]
    public void ฤดูกาลช่วงแคบกว่าชนะ_สงกรานต์ชนะHighSeason()
    {
        var seasons = new List<LodgingSeasonInput>
        {
            new("High", LodgingSeasonType.High, new DateTime(2026, 11, 1), new DateTime(2027, 2, 28), 1.25m, false, null),
            new("Songkran", LodgingSeasonType.Peak, new DateTime(2026, 4, 12), new DateTime(2026, 4, 16), 1.50m, true, 2),
        };
        // 13 เม.ย. 2027 (recurring) → ต้องได้ Songkran แม้ไม่อยู่ในปี 2026
        var n = LodgingPricingEngine.NightlyRate(new DateTime(2027, 4, 13), Input(seasons: seasons, weekendMult: 1m));
        Assert.Equal("Songkran", n.SeasonName);
        Assert.Equal(2250m, n.Rate);
        // 15 ธ.ค. 2026 → High
        var h = LodgingPricingEngine.NightlyRate(new DateTime(2026, 12, 15), Input(seasons: seasons, weekendMult: 1m));
        Assert.Equal("High", h.SeasonName);
        Assert.Equal(1875m, h.Rate);
    }

    [Fact]
    public void ฤดูกาลข้ามปีแบบrecurring_ปีใหม่ครอบ31ธค_และ1มค()
    {
        var seasons = new List<LodgingSeasonInput>
        {
            new("New Year", LodgingSeasonType.Peak, new DateTime(2025, 12, 28), new DateTime(2026, 1, 2), 1.60m, true, null),
        };
        Assert.NotNull(LodgingPricingEngine.SeasonFor(new DateTime(2027, 12, 31), seasons));
        Assert.NotNull(LodgingPricingEngine.SeasonFor(new DateTime(2028, 1, 1), seasons));
        Assert.Null(LodgingPricingEngine.SeasonFor(new DateTime(2028, 1, 3), seasons));
    }

    [Fact]
    public void overrideรายวัน_แทนที่ทุกอย่าง_รวมฤดูกาลและสุดสัปดาห์()
    {
        var sat = new DateTime(2026, 9, 12);
        var seasons = new List<LodgingSeasonInput> { new("High", LodgingSeasonType.High, sat, sat, 2m, false, null) };
        var ov = new List<LodgingOverrideInput> { new(sat, 999m, false, null, null) };
        var n = LodgingPricingEngine.NightlyRate(sat, Input(seasons: seasons, overrides: ov));
        Assert.True(n.IsOverride);
        Assert.Equal(999m, n.Rate);
    }

    [Fact]
    public void แผนราคา_สามโหมด()
    {
        Assert.Equal(1350m, LodgingPricingEngine.NightlyRate(Mon, Input(plan: new("NR", LodgingRateAdjustMode.Multiplier, 0.90m, false))).Rate);
        Assert.Equal(1800m, LodgingPricingEngine.NightlyRate(Mon, Input(plan: new("ABF", LodgingRateAdjustMode.Delta, 300m, true))).Rate);
        Assert.Equal(2000m, LodgingPricingEngine.NightlyRate(Mon, Input(plan: new("Fix", LodgingRateAdjustMode.Absolute, 2000m, false))).Rate);
    }

    [Fact]
    public void แขกเกินมาตรฐาน_และเตียงเสริม_คิดต่อคืน()
    {
        // 3 ผู้ใหญ่ (มาตรฐาน 2) + เตียงเสริม 1 × 2 คืน
        var q = LodgingPricingEngine.QuoteRoom(Mon, Mon.AddDays(2), 3, 0, 1, Input());
        Assert.Equal(1000m, q.ExtraGuestCharge);   // 1 × 500 × 2
        Assert.Equal(1600m, q.ExtraBedCharge);     // 1 × 800 × 2
        Assert.Equal(5600m, q.Subtotal);
    }

    [Fact]
    public void โฮสเทลราคาต่อคน_ไม่มีแนวคิดแขกเกิน()
    {
        var q = LodgingPricingEngine.QuoteRoom(Mon, Mon.AddDays(1), 3, 0, 0,
            Input(baseRate: 350m, mode: LodgingPricingMode.PerPerson, weekendMult: 1m));
        Assert.Equal(1050m, q.RoomRate);
        Assert.Equal(0m, q.ExtraGuestCharge);
    }

    [Fact]
    public void บริการเสริม_สี่โหมด()
    {
        Assert.Equal(800m, LodgingPricingEngine.ExtraTotal(new("รถรับส่ง", LodgingExtraPriceMode.PerStay, 800m, 1), 3, 2));
        Assert.Equal(2400m, LodgingPricingEngine.ExtraTotal(new("เตียงเสริม", LodgingExtraPriceMode.PerNight, 800m, 1), 3, 2));
        Assert.Equal(1000m, LodgingPricingEngine.ExtraTotal(new("ทัวร์", LodgingExtraPriceMode.PerPerson, 500m, 1), 3, 2));
        Assert.Equal(1500m, LodgingPricingEngine.ExtraTotal(new("อาหารเช้า", LodgingExtraPriceMode.PerPersonPerNight, 250m, 1), 3, 2));
    }

    /// <summary>รอบ 193 #36 (F-01): วิธีคิดราคา 0 (ค่าที่บันทึกไว้ตอน dropdown ว่าง) ต้องปฏิเสธ —
    /// เดิมตก `_ => 1` ⇒ "อาหารเช้า ฿250/คน/คืน" 2 คน 3 คืน ได้ 250 แทน 1,500 โดยไม่มีอะไรฟ้อง</summary>
    [Fact]
    public void บริการเสริม_วิธีคิดราคาไม่มีในระบบ_ปฏิเสธไม่คิดครั้งเดียวเงียบๆ()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LodgingPricingEngine.ExtraTotal(new("อาหารเช้า", (LodgingExtraPriceMode)0, 250m, 1), 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LodgingPricingEngine.ExtraTotal(new("อาหารเช้า", (LodgingExtraPriceMode)9, 250m, 1), 3, 2));
    }

    [Fact]
    public void บริการเสริม_ตัวตรวจการตั้งค่า_สองทิศ()
    {
        // แถวที่ต้องเลือกใหม่
        Assert.Contains("วิธีคิดราคา", LodgingPricingEngine.ExtraConfigProblem((LodgingExtraPriceMode)0, LodgingExtraCategory.Breakfast));
        Assert.Contains("หมวด", LodgingPricingEngine.ExtraConfigProblem(LodgingExtraPriceMode.PerNight, (LodgingExtraCategory)0));
        Assert.NotNull(LodgingPricingEngine.ExtraConfigProblem(null, null));
        // แถวที่ถูกอยู่แล้ว — ไม่ถูกแตะ (ทุกโหมด/ทุกหมวดที่มีจริง)
        foreach (var m in Enum.GetValues<LodgingExtraPriceMode>())
            foreach (var c in Enum.GetValues<LodgingExtraCategory>())
                Assert.Null(LodgingPricingEngine.ExtraConfigProblem(m, c));
    }

    [Fact]
    public void ยอดรวมราคารวมVAT_serviceCharge10_มัดจำ50()
    {
        // ค่าห้อง 3,000 + เสริม 500 = 3,500 → SC 350 → รวม 3,850 → VAT ใน = 3,850×7/107 = 251.87
        var t = LodgingPricingEngine.Totals(3000m, 500m, 0m, 10m, 7m, true, 50m, null, 0m, null);
        Assert.Equal(350m, t.ServiceChargeAmount);
        Assert.Equal(3850m, t.TotalAmount);
        Assert.Equal(251.87m, t.VatAmount);
        Assert.Equal(1925m, t.DepositRequired);
    }

    [Fact]
    public void ยอดรวมราคาไม่รวมVAT_VATคิดจากnetบวกSC()
    {
        var t = LodgingPricingEngine.Totals(3000m, 0m, 0m, 10m, 7m, false, 0m, null, 0m, null);
        Assert.Equal(300m, t.ServiceChargeAmount);
        Assert.Equal(231m, t.VatAmount);          // (3000+300)×7%
        Assert.Equal(3531m, t.TotalAmount);
        Assert.Equal(0m, t.DepositRequired);
    }

    [Fact]
    public void มัดจำคงที่_และเพดานขั้นต่ำสูงสุด()
    {
        Assert.Equal(1000m, LodgingPricingEngine.Totals(3000m, 0, 0, 0, 0, true, 50m, 1000m, 0, null).DepositRequired);
        Assert.Equal(2000m, LodgingPricingEngine.Totals(3000m, 0, 0, 0, 0, true, 50m, null, 2000m, null).DepositRequired); // ขั้นต่ำ
        Assert.Equal(500m, LodgingPricingEngine.Totals(3000m, 0, 0, 0, 0, true, 50m, null, 0, 500m).DepositRequired);     // เพดาน
        Assert.Equal(300m, LodgingPricingEngine.Totals(300m, 0, 0, 0, 0, true, 50m, 1000m, 0, null).DepositRequired);     // มัดจำห้ามเกินยอด
    }

    [Fact]
    public void ค่าปรับยกเลิกขั้นบันได()
    {
        var rules = LodgingPricingEngine.ParseRules(
            "[{\"daysBefore\":7,\"penaltyPercent\":0},{\"daysBefore\":3,\"penaltyPercent\":50},{\"daysBefore\":0,\"penaltyPercent\":100}]");
        var checkIn = new DateTime(2026, 9, 20);
        // ยกเลิก 10 วันก่อน → มากกว่าทุกขั้น → 0 (ขั้น 7 วัน = 0%)
        Assert.Equal(0m, LodgingPricingEngine.CancellationFee(4000m, checkIn, checkIn.AddDays(-10), rules, false).Fee);
        // 5 วันก่อน → ขั้น 3-7 วัน = 50%
        Assert.Equal(2000m, LodgingPricingEngine.CancellationFee(4000m, checkIn, checkIn.AddDays(-5), rules, false).Fee);
        // 1 วันก่อน → 100%
        Assert.Equal(4000m, LodgingPricingEngine.CancellationFee(4000m, checkIn, checkIn.AddDays(-1), rules, false).Fee);
        // วันเดียวกัน/หลังวันเข้าพัก → 100%
        Assert.Equal(4000m, LodgingPricingEngine.CancellationFee(4000m, checkIn, checkIn, rules, false).Fee);
        // non-refundable → 100% เสมอ
        Assert.Equal(4000m, LodgingPricingEngine.CancellationFee(4000m, checkIn, checkIn.AddDays(-60), rules, true).Fee);
    }

    [Fact]
    public void ขั้นต่ำกี่คืน_เอาค่ามากสุดจากทุกชั้น()
    {
        var seasons = new List<LodgingSeasonInput>
        {
            new("Songkran", LodgingSeasonType.Peak, new DateTime(2026, 4, 12), new DateTime(2026, 4, 16), 1.5m, true, 3),
        };
        Assert.Equal(3, LodgingPricingEngine.EffectiveMinNights(new DateTime(2027, 4, 13), new DateTime(2027, 4, 14), 1, 2, seasons, Array.Empty<LodgingOverrideInput>()));
        Assert.Equal(2, LodgingPricingEngine.EffectiveMinNights(new DateTime(2027, 6, 1), new DateTime(2027, 6, 2), 1, 2, seasons, Array.Empty<LodgingOverrideInput>()));
    }
}

public class LodgingAvailabilityTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D10 = new(2026, 9, 10);

    private static LodgingAvailability.BookedRange B(int inDay, int outDay, int rooms,
        LodgingReservationStatus st = LodgingReservationStatus.Confirmed, DateTime? hold = null)
        => new(new DateTime(2026, 9, inDay), new DateTime(2026, 9, outDay), rooms, st, hold);

    [Fact]
    public void วันเช็คเอาต์ของคนเดิม_ขายให้คนใหม่ได้()
    {
        var booked = new[] { B(8, 10, 1) };   // ออก 10 → คืน 10 ว่าง
        Assert.Equal(1, LodgingAvailability.AvailableRooms(D10, D10.AddDays(2), 1, 0, booked, Array.Empty<LodgingOverrideInput>(), Now));
    }

    [Fact]
    public void ทับช่วงบางคืน_ต้องว่างทุกคืนถึงจะจองได้()
    {
        var booked = new[] { B(11, 12, 2) };  // 2 ห้องถูกจองคืน 11
        // มี 2 ห้อง ขอ 10→12: คืน 10 ว่าง 2, คืน 11 ว่าง 0 → ต่ำสุด 0
        Assert.Equal(0, LodgingAvailability.AvailableRooms(D10, D10.AddDays(2), 2, 0, booked, Array.Empty<LodgingOverrideInput>(), Now));
    }

    [Fact]
    public void Pendingที่หมดเวลาถือมัดจำ_ไม่กันห้อง()
    {
        var expired = B(10, 11, 1, LodgingReservationStatus.Pending, Now.AddHours(-1));
        var alive = B(10, 11, 1, LodgingReservationStatus.Pending, Now.AddHours(1));
        Assert.Equal(1, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 1, 0, new[] { expired }, Array.Empty<LodgingOverrideInput>(), Now));
        Assert.Equal(0, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 1, 0, new[] { alive }, Array.Empty<LodgingOverrideInput>(), Now));
    }

    [Fact]
    public void ยกเลิกและNoShow_ไม่กันห้อง()
    {
        var booked = new[] { B(10, 11, 1, LodgingReservationStatus.Cancelled), B(10, 11, 1, LodgingReservationStatus.NoShow) };
        Assert.Equal(1, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 1, 0, booked, Array.Empty<LodgingOverrideInput>(), Now));
    }

    [Fact]
    public void StopSell_และAllotment_และOverbooking()
    {
        var stop = new[] { new LodgingOverrideInput(D10, null, true, null, null) };
        Assert.Equal(0, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 5, 0, Array.Empty<LodgingAvailability.BookedRange>(), stop, Now));

        var allot = new[] { new LodgingOverrideInput(D10, null, false, 2, null) };
        Assert.Equal(2, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 5, 0, Array.Empty<LodgingAvailability.BookedRange>(), allot, Now));

        // เต็ม 5/5 แต่อนุญาต overbooking 1
        var full = new[] { B(10, 11, 5) };
        Assert.Equal(1, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 5, 1, full, Array.Empty<LodgingOverrideInput>(), Now));
        Assert.Equal(0, LodgingAvailability.AvailableRooms(D10, D10.AddDays(1), 5, 0, full, Array.Empty<LodgingOverrideInput>(), Now));
    }

    // ═══ S-10: อัตรา VAT ของที่พักมาจากบริษัท ไม่ใช่ 7 ตายตัว ═══

    [Fact]
    public void อัตราVATที่พัก_ตามอัตราบริษัท_เมื่อจดVAT()
    {
        Assert.Equal(7m, LodgingPricingEngine.PropertyVatRate(null, companyVatRegistered: true, companyVatRate: 7m));
        Assert.Equal(10m, LodgingPricingEngine.PropertyVatRate(true, companyVatRegistered: true, companyVatRate: 10m));
    }

    [Fact]
    public void อัตราVATที่พัก_ไม่จดVATหรือตั้งไม่คิด_เป็นศูนย์()
    {
        Assert.Equal(0m, LodgingPricingEngine.PropertyVatRate(false, companyVatRegistered: true, companyVatRate: 7m));
        Assert.Equal(0m, LodgingPricingEngine.PropertyVatRate(null, companyVatRegistered: false, companyVatRate: 7m));
        // §90/2 — ตั้ง "คิด VAT เสมอ" บนบริษัทที่ไม่จดทะเบียน ต้องไม่เก็บภาษี (เดิมคืน 7)
        Assert.Equal(0m, LodgingPricingEngine.PropertyVatRate(true, companyVatRegistered: false, companyVatRate: 7m));
    }

    // ═══ C8 รอบ 193 หลังฝ่ายค้าน — อัตรา VAT รายการ folio ผ่านด่าน §90/2 ═══

    [Fact]
    public void รายการfolio_บริษัทไม่จดVAT_เป็นศูนย์แม้พิมพ์7หรือแถวเดิมเก็บ7()
    {
        Assert.Equal(0m, LodgingPricingEngine.ChargeVatRate(7m, companyVatRegistered: false, propertyRate: 0m));
        Assert.Equal(0m, LodgingPricingEngine.ChargeVatRate(null, companyVatRegistered: false, propertyRate: 0m));
    }

    [Fact]
    public void รายการfolio_บริษัทจดVAT_ใช้อัตราที่ระบุหรืออัตราที่พัก()
    {
        Assert.Equal(7m, LodgingPricingEngine.ChargeVatRate(null, companyVatRegistered: true, propertyRate: 7m));
        Assert.Equal(0m, LodgingPricingEngine.ChargeVatRate(0m, companyVatRegistered: true, propertyRate: 7m));
        Assert.Equal(7m, LodgingPricingEngine.ChargeVatRate(7m, companyVatRegistered: true, propertyRate: 0m));
    }
}
