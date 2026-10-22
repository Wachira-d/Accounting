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

    /// <summary>ป้ายกติกาห้องบนหน้าห้องพักสาธารณะ — บอกทั้งสองทิศเสมอ (ห้ามสูบ/สูบได้ · ไม่รับ/รับสัตว์เลี้ยง) ให้แขกไม่ต้องเดา</summary>
    public static List<string> HouseRuleLabels(bool smokingAllowed, bool petsAllowed) => new()
    {
        smokingAllowed ? "สูบบุหรี่ได้" : "ห้องปลอดบุหรี่ (ห้ามสูบบุหรี่)",
        petsAllowed ? "รับสัตว์เลี้ยง" : "ไม่รับสัตว์เลี้ยง",
    };

    /// <summary>"เด็กพักได้สูงสุด N คน/ห้อง" — 0 หรือติดลบ ⇒ null (ไม่แต่งป้าย · ความจุนับผู้ใหญ่อย่างเดียวตามข้อ 124)</summary>
    public static string? ChildrenLabel(int maxChildren)
        => maxChildren > 0 ? $"เด็กพักได้สูงสุด {maxChildren} คน/ห้อง" : null;

    /// <summary>สรุปเงื่อนไขยกเลิกของนโยบายเดียวกับที่ใบเสนอราคาใช้ — ความหมายตรงกับ <see cref="LodgingPricingEngine.CancellationFee"/> ทุกขั้น:
    /// กฎ {d,p} (เรียง d มาก→น้อย) ใช้เมื่อยกเลิกล่วงหน้า<b>มากกว่า</b> d วัน · เหลือวันไม่เกิน d ของขั้นสุดท้าย ⇒ ใช้ขั้นสุดท้าย ⇒
    /// ขั้นสุดท้ายเขียนเป็น "ภายใน {d ของขั้นก่อนหน้า} วัน" · มีขั้นเดียว = ทุกกรณี · ไม่คืนเงิน ⇒ "ไม่คืนเงินทุกกรณี" ·
    /// ไม่มีกฎ = null (ไม่แต่งเงื่อนไขที่ไม่มีจริง)</summary>
    public static string? CancellationSummary(bool nonRefundable, IEnumerable<LodgingCancellationRule> rules)
    {
        if (nonRefundable) return "ไม่คืนเงินทุกกรณี";
        var ordered = rules.OrderByDescending(r => r.DaysBefore).ToList();
        if (ordered.Count == 0) return null;
        static string Fee(decimal pct) => pct <= 0 ? "ไม่มีค่าธรรมเนียม"
            : $"คิด {pct.ToString("0.##", CultureInfo.InvariantCulture)}% ของยอดการจอง";
        var parts = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            if (i < ordered.Count - 1)
                parts.Add($"ยกเลิกล่วงหน้ามากกว่า {Math.Max(0, r.DaysBefore)} วัน {Fee(r.PenaltyPercent)}");
            else if (i == 0)
                parts.Add($"ยกเลิกเมื่อใดก็ตาม {Fee(r.PenaltyPercent)}");
            else
                parts.Add($"ยกเลิกภายใน {Math.Max(0, ordered[i - 1].DaysBefore)} วันก่อนเข้าพัก {Fee(r.PenaltyPercent)}");
        }
        return string.Join(" · ", parts);
    }
}
