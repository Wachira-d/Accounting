using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

// ============================================================================
// ตัวคำนวณราคาที่พัก — pure (ไม่แตะ DB) ตามกฎเหล็ก #4 G: logic เงินต้อง extract
// เป็น pure class + เทสต์. Service โหลดข้อมูล (ราคาฐาน/ฤดูกาล/override/แผนราคา/
// นโยบาย) แล้วส่งเข้ามาที่นี่ตัวเดียว — ทั้งหน้าเว็บ (quote) และตอนสร้างการจอง
// ต้องได้ตัวเลขเดียวกันเป๊ะเพราะเดินสูตรเดียวกัน (ห้ามให้ JS คำนวณราคาเอง)
//
// ลำดับการคิดราคาต่อคืน (ตรงกับ TakeTime DynamicPricing + PricingSeasons แต่กำหนด
// ลำดับชัดเจน ไม่ให้ "ใครมาก่อนชนะ"):
//   1. ราคาฐานของประเภทห้อง (BaseRate)
//   2. แผนราคา (rate plan): Absolute แทนที่ / Multiplier คูณ / Delta บวก
//   3. ฤดูกาล: ช่วงที่ "แคบกว่า" ชนะ (Songkran 5 วัน ชนะ High season 4 เดือน)
//   4. สุดสัปดาห์: คูณ WeekendMultiplier เมื่อวันนั้นอยู่ใน WeekendDaysMask
//   5. override รายวัน: ถ้ามี Rate → แทนที่ผลข้อ 1-4 ทั้งหมด (พนักงานตั้งใจตั้งราคาวันนั้น)
//   6. แขกเกิน/เตียงเสริม: + ExtraGuestPrice × คนเกิน + ExtraBedPrice × เตียง (ต่อคืน)
// ปัดเศษ: AwayFromZero ทุกจุด (กฎ E)
// ============================================================================

public sealed record LodgingSeasonInput(
    string Name, LodgingSeasonType Type, DateTime StartDate, DateTime EndDate,
    decimal Multiplier, bool IsRecurringYearly, int? MinNights);

public sealed record LodgingOverrideInput(DateTime Date, decimal? Rate, bool StopSell, int? Allotment, int? MinNights);

public sealed record LodgingRatePlanInput(
    string Name, LodgingRateAdjustMode AdjustMode, decimal AdjustValue, bool IncludesBreakfast);

/// <summary>ข้อมูลที่ engine ต้องใช้คิดราคาห้อง 1 ประเภท</summary>
public sealed record LodgingRoomPricingInput(
    decimal BaseRate,
    LodgingPricingMode PricingMode,
    int StandardOccupancy,
    decimal ExtraGuestPrice,
    decimal ExtraBedPrice,
    decimal WeekendMultiplier,
    int WeekendDaysMask,
    IReadOnlyList<LodgingSeasonInput> Seasons,
    IReadOnlyList<LodgingOverrideInput> Overrides,
    LodgingRatePlanInput? RatePlan);

public sealed record LodgingNightlyRate(DateTime Date, decimal Rate, string? SeasonName, bool IsWeekend, bool IsOverride);

public sealed record LodgingRoomQuote(
    IReadOnlyList<LodgingNightlyRate> Nights,
    decimal RoomRate,          // รวมค่าห้องทุกคืน (ไม่รวมแขกเกิน/เตียงเสริม)
    decimal ExtraGuestCharge,  // แขกเกิน × คืน
    decimal ExtraBedCharge,
    decimal Subtotal);         // RoomRate + ExtraGuestCharge + ExtraBedCharge

public sealed record LodgingExtraInput(string Name, LodgingExtraPriceMode PriceMode, decimal UnitPrice, int Quantity);

public sealed record LodgingTotals(
    decimal RoomSubtotal,
    decimal ExtrasTotal,
    decimal DiscountAmount,
    decimal ServiceChargeAmount,
    decimal VatAmount,
    decimal TotalAmount,
    decimal DepositRequired);

public sealed record LodgingCancellationRule(int DaysBefore, decimal PenaltyPercent);

public static class LodgingPricingEngine
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>bitmask ของวันในสัปดาห์: อา=1 จ=2 อ=4 พ=8 พฤ=16 ศ=32 ส=64</summary>
    public static int DayMask(DayOfWeek d) => 1 << (int)d;

    public static bool IsWeekend(DateTime date, int weekendDaysMask)
        => (weekendDaysMask & DayMask(date.DayOfWeek)) != 0;

    /// <summary>ฤดูกาลที่ครอบวันนี้ — "ช่วงแคบกว่าชนะ" (กติกาเดียวกับ SsoRateSchedule.SpanWidth)
    /// ช่วงที่ IsRecurringYearly เทียบเฉพาะเดือน-วัน และรองรับช่วงข้ามปี (28 ธ.ค.–2 ม.ค.)</summary>
    public static LodgingSeasonInput? SeasonFor(DateTime date, IReadOnlyList<LodgingSeasonInput> seasons)
    {
        LodgingSeasonInput? best = null;
        var bestSpan = int.MaxValue;
        foreach (var s in seasons)
        {
            if (!Covers(s, date)) continue;
            var span = (int)(s.EndDate.Date - s.StartDate.Date).TotalDays;
            if (span < 0) span += 366;
            if (span < bestSpan) { best = s; bestSpan = span; }
        }
        return best;
    }

    private static bool Covers(LodgingSeasonInput s, DateTime date)
    {
        var d = date.Date;
        if (!s.IsRecurringYearly)
            return d >= s.StartDate.Date && d <= s.EndDate.Date;

        // เทียบ (เดือน,วัน) — ช่วงข้ามปี: start > end (เช่น 12/28 → 01/02)
        int key(DateTime x) => x.Month * 100 + x.Day;
        var k = key(d); var ks = key(s.StartDate); var ke = key(s.EndDate);
        return ks <= ke ? (k >= ks && k <= ke) : (k >= ks || k <= ke);
    }

    /// <summary>ราคาต่อคืนของวันหนึ่ง (ยังไม่รวมแขกเกิน/เตียงเสริม)</summary>
    public static LodgingNightlyRate NightlyRate(DateTime date, LodgingRoomPricingInput input)
    {
        var d = date.Date;
        var ov = input.Overrides.FirstOrDefault(o => o.Date.Date == d);
        var season = SeasonFor(d, input.Seasons);
        var weekend = IsWeekend(d, input.WeekendDaysMask);

        if (ov?.Rate is decimal fixedRate)
            return new LodgingNightlyRate(d, R2(fixedRate), season?.Name, weekend, true);

        var rate = input.BaseRate;
        if (input.RatePlan != null)
        {
            rate = input.RatePlan.AdjustMode switch
            {
                LodgingRateAdjustMode.Absolute => input.RatePlan.AdjustValue,
                LodgingRateAdjustMode.Multiplier => rate * input.RatePlan.AdjustValue,
                LodgingRateAdjustMode.Delta => rate + input.RatePlan.AdjustValue,
                _ => rate,
            };
        }
        if (season != null) rate *= season.Multiplier;
        if (weekend && input.WeekendMultiplier > 0) rate *= input.WeekendMultiplier;
        if (rate < 0) rate = 0;
        return new LodgingNightlyRate(d, R2(rate), season?.Name, weekend, false);
    }

    /// <summary>ราคาห้อง 1 ห้องตลอดการเข้าพัก [checkIn, checkOut) — checkOut ไม่นับเป็นคืน</summary>
    public static LodgingRoomQuote QuoteRoom(
        DateTime checkIn, DateTime checkOut, int adults, int children, int extraBeds,
        LodgingRoomPricingInput input)
    {
        if (checkOut.Date <= checkIn.Date)
            throw new ArgumentException("วันเช็คเอาต์ต้องหลังวันเช็คอิน");

        var nights = new List<LodgingNightlyRate>();
        for (var d = checkIn.Date; d < checkOut.Date; d = d.AddDays(1))
            nights.Add(NightlyRate(d, input));

        decimal roomRate;
        var guests = adults + children;
        if (input.PricingMode == LodgingPricingMode.PerPerson)
        {
            // ต่อคนต่อคืน — ไม่มีแนวคิด "แขกเกิน"
            roomRate = R2(nights.Sum(n => n.Rate) * Math.Max(1, guests));
        }
        else
        {
            roomRate = R2(nights.Sum(n => n.Rate));
        }

        var extraGuests = input.PricingMode == LodgingPricingMode.PerUnit
            ? Math.Max(0, guests - input.StandardOccupancy) : 0;
        var extraGuestCharge = R2(extraGuests * input.ExtraGuestPrice * nights.Count);
        var extraBedCharge = R2(Math.Max(0, extraBeds) * input.ExtraBedPrice * nights.Count);
        return new LodgingRoomQuote(nights, roomRate, extraGuestCharge, extraBedCharge,
            R2(roomRate + extraGuestCharge + extraBedCharge));
    }

    /// <summary>ราคาบริการเสริมตามโหมด
    ///
    /// <para>⚠️ รอบ 193 (#36 · F-01): วิธีคิดราคาที่ไม่มีในระบบ (เช่น 0 จาก dropdown ว่างของรอบ 159–188)
    /// <b>ต้องปฏิเสธ</b> — เดิมตกที่ <c>_ => 1</c> = คิดครั้งเดียวทั้งทริป ("อาหารเช้า ฿250/คน/คืน"
    /// 2 คน 3 คืน ได้ 250 แทน 1,500) โดยไม่มีอะไรฟ้อง · ผู้เรียกตรวจก่อนด้วย <see cref="ExtraConfigProblem"/>
    /// แล้วแสดงข้อความไทย — ถึงตรงนี้ได้แปลว่ามีทางเข้าที่ลืมตรวจ จึงโยนแทนการเดาค่า</para></summary>
    public static decimal ExtraTotal(LodgingExtraInput extra, int nights, int guests)
    {
        var qty = Math.Max(0, extra.Quantity);
        var mult = extra.PriceMode switch
        {
            LodgingExtraPriceMode.PerStay => 1,
            LodgingExtraPriceMode.PerNight => nights,
            LodgingExtraPriceMode.PerPerson => Math.Max(1, guests),
            LodgingExtraPriceMode.PerPersonPerNight => nights * Math.Max(1, guests),
            _ => throw new ArgumentOutOfRangeException(nameof(extra),
                ExtraConfigProblem(extra.PriceMode, LodgingExtraCategory.Other) + $" (บริการ: {extra.Name})"),
        };
        return R2(extra.UnitPrice * qty * mult);
    }

    /// <summary>ปัญหาการตั้งค่าบริการเสริมที่ทำให้คิดราคา/บันทึกไม่ได้ (ข้อความไทยบอกทางไปต่อ) — null = ใช้ได้
    /// · ตัวตัดสินตัวเดียวของ: ด่านบันทึก (SaveExtraAsync) · ตัวคิดราคา (BuildQuote) · ป้ายเตือนในหน้าตั้งค่า
    /// · รายงานแถวที่ต้องเลือกใหม่ — ห้ามเขียนเงื่อนไข "0 = ผิด" ซ้ำที่อื่น</summary>
    public static string? ExtraConfigProblem(LodgingExtraPriceMode? priceMode, LodgingExtraCategory? category)
    {
        var badMode = priceMode is not LodgingExtraPriceMode m || !Enum.IsDefined(m);
        var badCat = category is not LodgingExtraCategory c || !Enum.IsDefined(c);
        if (!badMode && !badCat) return null;
        var parts = new List<string>();
        if (badMode) parts.Add("วิธีคิดราคา (ต่อการเข้าพัก / ต่อคืน / ต่อคน / ต่อคนต่อคืน)");
        if (badCat) parts.Add("หมวด");
        return $"ยังไม่ได้เลือก{string.Join(" และ ", parts)} — ค่าที่บันทึกไว้ไม่มีในระบบ ระบบจึงไม่คิดราคาบริการนี้ "
            + "กรุณาเปิด “แก้ไข” แล้วเลือกให้ถูกต้องก่อนเปิดขาย";
    }

    /// <summary>รวมยอดทั้งการจอง + คำนวณ service charge / VAT / มัดจำ
    ///
    /// <para>ราคารวม VAT (pricesIncludeVat=true — ที่พักไทยส่วนใหญ่ตั้งราคา "รวมภาษี"):
    /// ค่าห้อง+เสริม−ส่วนลด = ยอดรวม VAT; service charge คิดจากยอดนั้นและถือว่ารวม VAT
    /// ด้วย; VAT = ยอดรวม × 7/107 (informational — ตัวจริงคำนวณอีกครั้งตอนออกเอกสาร
    /// ด้วย PricesIncludeVat=true ซึ่งใช้สูตรเดียวกันจึงตรงกัน)</para>
    /// <para>ราคาไม่รวม VAT: SC คิดจาก net แล้ว VAT คิดจาก (net + SC)</para></summary>
    public static LodgingTotals Totals(
        decimal roomSubtotal, decimal extrasTotal, decimal discountAmount,
        decimal serviceChargePercent, decimal vatRate, bool pricesIncludeVat,
        decimal depositPercent, decimal? depositFixed, decimal depositMin, decimal? depositMax)
    {
        var baseAmt = Math.Max(0, roomSubtotal + extrasTotal - discountAmount);
        var sc = R2(baseAmt * serviceChargePercent / 100m);
        decimal vat, total;
        if (pricesIncludeVat)
        {
            total = R2(baseAmt + sc);
            vat = vatRate > 0 ? R2(total * vatRate / (100m + vatRate)) : 0m;
        }
        else
        {
            vat = vatRate > 0 ? R2((baseAmt + sc) * vatRate / 100m) : 0m;
            total = R2(baseAmt + sc + vat);
        }

        decimal deposit;
        if (depositFixed is decimal fixedDep) deposit = fixedDep;
        else deposit = R2(total * depositPercent / 100m);
        if (deposit < depositMin) deposit = depositMin;
        if (depositMax is decimal mx && deposit > mx) deposit = mx;
        if (deposit > total) deposit = total;
        if (deposit < 0) deposit = 0;

        return new LodgingTotals(R2(roomSubtotal), R2(extrasTotal), R2(discountAmount), sc, vat, total, R2(deposit));
    }

    /// <summary>ค่าปรับยกเลิกตามนโยบายขั้นบันได — คืน (ค่าปรับ, กฎที่ใช้)
    /// กฎเรียงจาก daysBefore มาก→น้อย ใช้ข้อแรกที่ daysBefore ≤ วันที่เหลือ;
    /// ไม่มีข้อไหนตรง = 0 (ยกเลิกล่วงหน้ามากกว่าทุกขั้น) · NonRefundable = 100%</summary>
    public static (decimal Fee, LodgingCancellationRule? Rule) CancellationFee(
        decimal totalAmount, DateTime checkIn, DateTime cancelAt,
        IReadOnlyList<LodgingCancellationRule> rules, bool nonRefundable)
    {
        if (nonRefundable) return (R2(totalAmount), new LodgingCancellationRule(int.MaxValue, 100));
        var daysLeft = (int)Math.Floor((checkIn.Date - cancelAt.Date).TotalDays);
        if (daysLeft < 0) daysLeft = 0;
        foreach (var r in rules.OrderByDescending(r => r.DaysBefore))
        {
            if (daysLeft <= r.DaysBefore)
                continue;   // ยังไม่ถึงขั้นนี้ — ต้องหาขั้นที่ daysBefore น้อยกว่าวันที่เหลือ
            return (R2(totalAmount * r.PenaltyPercent / 100m), r);
        }
        // วันที่เหลือ ≤ ทุก daysBefore ⇒ ใช้ขั้นที่ daysBefore น้อยที่สุด (ใกล้วันเข้าพักที่สุด)
        var last = rules.OrderBy(r => r.DaysBefore).FirstOrDefault();
        return last == null ? (0m, null) : (R2(totalAmount * last.PenaltyPercent / 100m), last);
    }

    public static IReadOnlyList<LodgingCancellationRule> ParseRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<LodgingCancellationRule>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<LodgingCancellationRule>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var days = el.TryGetProperty("daysBefore", out var d) ? d.GetInt32() : 0;
                var pct = el.TryGetProperty("penaltyPercent", out var p) ? p.GetDecimal() : 0m;
                list.Add(new LodgingCancellationRule(days, pct));
            }
            return list;
        }
        catch { return Array.Empty<LodgingCancellationRule>(); }
    }

    /// <summary>ขั้นต่ำกี่คืนสำหรับช่วงที่เลือก = max(ที่พัก, ประเภทห้อง, ฤดูกาลที่ครอบคืนใด ๆ, override)</summary>
    public static int EffectiveMinNights(DateTime checkIn, DateTime checkOut, int propertyMin, int? roomTypeMin,
        IReadOnlyList<LodgingSeasonInput> seasons, IReadOnlyList<LodgingOverrideInput> overrides)
    {
        var min = Math.Max(1, propertyMin);
        if (roomTypeMin is int rt) min = Math.Max(min, rt);
        for (var d = checkIn.Date; d < checkOut.Date; d = d.AddDays(1))
        {
            var s = SeasonFor(d, seasons);
            if (s?.MinNights is int sm) min = Math.Max(min, sm);
            var ov = overrides.FirstOrDefault(o => o.Date.Date == d);
            if (ov?.MinNights is int om) min = Math.Max(min, om);
        }
        return min;
    }
}

/// <summary>ตรวจห้องว่าง — pure: รับรายการการจองที่ทับช่วง แล้วนับว่าเหลือกี่ห้อง
/// การจองที่ทับ = CheckIn &lt; ต้องการ CheckOut && CheckOut &gt; ต้องการ CheckIn
/// (คืนวันเช็คเอาต์ไม่นับ — ห้องที่เช็คเอาต์ 12:00 ขายคืนนั้นได้)</summary>
public static class LodgingAvailability
{
    public sealed record BookedRange(DateTime CheckIn, DateTime CheckOut, int Rooms, LodgingReservationStatus Status, DateTime? HoldExpiresAt);

    public static bool Overlaps(DateTime aIn, DateTime aOut, DateTime bIn, DateTime bOut)
        => aIn.Date < bOut.Date && aOut.Date > bIn.Date;

    /// <summary>การจองนี้ยัง "กันห้อง" อยู่ไหม — Pending ที่หมดเวลาถือมัดจำแล้ว = ไม่กัน</summary>
    public static bool Blocks(BookedRange r, DateTime now)
        => r.Status switch
        {
            LodgingReservationStatus.Confirmed or LodgingReservationStatus.CheckedIn => true,
            LodgingReservationStatus.Pending => r.HoldExpiresAt == null || r.HoldExpiresAt > now,
            _ => false,
        };

    /// <summary>จำนวนห้องว่างขั้นต่ำตลอดช่วง (ต้องว่างทุกคืน) — คำนวณต่อคืนแล้วเอาค่าต่ำสุด</summary>
    public static int AvailableRooms(
        DateTime checkIn, DateTime checkOut, int totalUnits, int overbookingAllowance,
        IReadOnlyList<BookedRange> booked, IReadOnlyList<LodgingOverrideInput> overrides, DateTime now)
    {
        var min = int.MaxValue;
        for (var d = checkIn.Date; d < checkOut.Date; d = d.AddDays(1))
        {
            var ov = overrides.FirstOrDefault(o => o.Date.Date == d);
            if (ov?.StopSell == true) return 0;
            var capacity = ov?.Allotment is int a ? Math.Min(a, totalUnits) : totalUnits;
            var used = booked.Where(b => Blocks(b, now) && Overlaps(b.CheckIn, b.CheckOut, d, d.AddDays(1)))
                              .Sum(b => b.Rooms);
            var free = capacity + overbookingAllowance - used;
            if (free < min) min = free;
        }
        return min == int.MaxValue ? 0 : Math.Max(0, min);
    }
}
