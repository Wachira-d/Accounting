using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ป้ายของการ์ดประเภทห้องบนเว็บสาธารณะ (บล็อก <c>LodgingRooms</c> · <c>GET …/lodging/info</c>) — <b>เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดงอย่างเดียว</b>
/// (รอบ 202 ทีม LW · ฝ่ายค้าน P2-1/P2-2)
///
/// <para>ที่มา: รุ่นแรกของบล็อกพิมพ์ "เริ่มต้น ฿{baseRate}/คืน" จากราคาฐานดิบ = สูตรที่สองนอก <see cref="LodgingPricingEngine"/> ⇒ ที่พักที่ตั้งแผนราคา
/// ตั้งต้นลด 10% / ฤดูกาล Low ×0.85 / ราคาต่อคน โชว์ราคาที่ไม่มีวันเกิดขึ้นจริง · และ "พักได้ {MaxOccupancy} คน" ขัดคำตัดสินข้อ 124
/// (ความจุนับเฉพาะผู้ใหญ่ · เด็ก/ทารกไม่นับ · คนเสริมเป็นช่องแยก)</para></summary>
public static class LodgingPublicRoomLabels
{
    /// <summary>ช่วงวันที่หา "ราคาต่อคืนที่ถูกที่สุด" นับจากวันนี้ (เวลาไทย)</summary>
    public const int FromRateWindowDays = 30;

    /// <summary>ราคาต่อคืนที่ถูกที่สุดของประเภทห้องในช่วง [<paramref name="firstNight"/>, +<paramref name="days"/>) ผ่าน
    /// <see cref="LodgingPricingEngine.NightlyRate"/> ตัวเดียวกับใบเสนอราคา (แผนราคา · ฤดูกาล · สุดสัปดาห์ · override) · ข้ามวันที่ปิดขาย (stop-sell) ·
    /// ปิดขายทุกวัน/ช่วงว่าง = null (หน้าเว็บไม่แสดงราคา — ไม่แต่งตัวเลข)</summary>
    public static decimal? FromRate(LodgingRoomPricingInput input, DateTime firstNight, int days)
    {
        decimal? best = null;
        for (var i = 0; i < Math.Max(0, days); i++)
        {
            var d = firstNight.Date.AddDays(i);
            if (input.Overrides.Any(o => o.Date.Date == d && o.StopSell)) continue;
            var rate = LodgingPricingEngine.NightlyRate(d, input).Rate;
            if (best == null || rate < best) best = rate;
        }
        return best;
    }

    /// <summary>"เริ่มต้น ฿1,350.00/ห้อง/คืน · รวม VAT แล้ว" — หน่วยตามวิธีคิดราคา (ต่อคน = /คน/คืน) · ป้าย VAT ตามที่พัก (ไม่มี VAT = ไม่มีป้าย)</summary>
    public static string? FromRateLabel(decimal? rate, LodgingPricingMode mode, bool pricesIncludeVat, decimal vatRate)
    {
        if (rate is not decimal r) return null;
        var unit = mode == LodgingPricingMode.PerPerson ? "/คน/คืน" : "/ห้อง/คืน";
        var vat = vatRate <= 0 ? ""
            : pricesIncludeVat ? " · รวม VAT แล้ว"
            : $" · ยังไม่รวม VAT {vatRate.ToString("0.##", CultureInfo.InvariantCulture)}%";
        return $"เริ่มต้น ฿{r.ToString("N2", CultureInfo.InvariantCulture)}{unit}{vat}";
    }

    /// <summary>ความจุบนการ์ดห้อง (ข้อ 124): ผู้ใหญ่สูงสุด/ห้อง · คนเสริม (ป้ายเดียวกับหน้าตั้งค่า <see cref="LodgingSettingsRules.ExtraBedSummary"/>
    /// เฉพาะเมื่อตั้งครบ — ป้ายเตือนเจ้าของ "ตั้งค่าไม่ครบ" ไม่ออกหน้าสาธารณะ) · เด็ก/ทารกไม่นับความจุ</summary>
    public static string CapacityLabel(int maxAdults, bool allowExtraBed, int maxExtraBeds, decimal? extraBedPrice)
    {
        var extra = LodgingOccupancy.SellsExtraBeds(allowExtraBed, maxExtraBeds, extraBedPrice)   // ตัวตัดสินเดียวกับเครื่องจอง (ราคาว่าง = ไม่ขาย)
            ? " · " + LodgingSettingsRules.ExtraBedSummary(true, maxExtraBeds, extraBedPrice)
            : "";
        return $"ผู้ใหญ่สูงสุด {Math.Max(1, maxAdults)} คน/ห้อง{extra} · เด็ก/ทารกไม่นับความจุ";
    }
}
