using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ค่าบริการ/ปัดเศษที่<b>พิมพ์อยู่บนบิล</b> — null = กระดาษไม่บอก</summary>
/// <param name="ServiceChargeAmount">ยอดค่าบริการเป็นบาท (null = ไม่พบ)</param>
/// <param name="ServiceChargePercent">อัตราที่พิมพ์กำกับไว้ เช่น 10 (null = กระดาษบอกแต่ยอด)</param>
/// <param name="RoundingAdjustment">ค่าปัดเศษ — บวก/ลบตามที่พิมพ์ (null = ไม่พบ)</param>
public sealed record BillSurcharge(
    decimal? ServiceChargeAmount,
    decimal? ServiceChargePercent,
    decimal? RoundingAdjustment);

/// <summary>
/// อ่าน "ค่าบริการ (Service Charge)" และ "ปัดเศษ/เศษสตางค์" จากข้อความบนบิล
///
/// ═══ ทำไมต้องมี (ผลตรวจ OCR 2026-09-06 · T2-18) ═══
/// บิลร้านอาหาร/โรงแรมเป็นงานประจำวันของ SME: อาหาร 1,000 · SC 10% = 100 ·
/// ยอดก่อน VAT 1,100 · VAT 77 · รวม 1,177 — ทั้งเรพ **ไม่มีจุดไหนอ่าน SC เลย**
/// (grep `service charge|ค่าบริการ|ปัดเศษ` = 0 จุดในเส้น OCR) ⇒ Σ บรรทัดที่
/// OCR อ่านได้ = 1,000 แต่หัวใบ 1,100 ⇒ ตัวจำแนกตกเป็น <c>LinesShort</c>
/// ทุกใบ (เอกสารมีบรรทัดรวม 1,000 แต่หัวใบ 1,100 — ขัดกันเองในใบเดียว) และ
/// <c>OcrLineSplitGuard</c> ทิ้งผลแตกบรรทัดของ AI ทั้งชุดเพราะยอดไม่ลงตัว
///
/// ═══ กติกาของตัวอ่านนี้ ═══
/// • อ่านทีละ<b>บรรทัด</b> ไม่ใช่ regex ข้ามบรรทัด — ตัวคั่นระหว่างป้ายกับตัวเลข
///   ใช้ <c>[ \t]</c> เท่านั้น (บทเรียน "regex ที่ใช้ <c>\s</c> เป็นตัวคั่นระหว่าง
///   กลุ่มตัวเลข = กลืนขึ้นบรรทัดใหม่เงียบ ๆ")
/// • คืน null เมื่อไม่พบ — <b>ห้ามคำนวณ SC ให้เองจากส่วนต่าง</b> เพราะส่วนต่าง
///   เกิดจาก OCR อ่านบรรทัดขาดได้เท่า ๆ กับเกิดจาก SC ("ค่าที่แต่งขึ้นเพื่อให้
///   โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ") — ผู้เรียกต้องพิสูจน์ด้วยยอดหัวใบเอง
/// </summary>
public static class ThaiBillSurcharge
{
    // ป้ายค่าบริการ — "ค่าบริการ" เป็นคำกว้าง (ใบแจ้งหนี้บริการก็ใช้) จึงต้องให้
    // ผู้เรียกยืนยันด้วยยอดหัวใบก่อนนำไปใช้เสมอ ตัวอ่านนี้แค่ "เห็นอะไรบนกระดาษ"
    private static readonly Regex ServiceLabel = new(
        @"(?:service[ \t]*charge|เซอร์วิส[ \t]*ชาร์จ|ค่าบริการ(?:เสิร์ฟ)?|ค่าเซอร์วิส|\bS\.?C\.?\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoundingLabel = new(
        @"(?:ปัดเศษ|เศษสตางค์|round(?:ing)?(?:[ \t]*(?:adj(?:ustment)?|off))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>อัตรา % ที่พิมพ์กำกับ เช่น "10%" หรือ "(10 %)"</summary>
    private static readonly Regex PercentToken = new(
        @"(\d{1,2}(?:\.\d{1,2})?)[ \t]*%", RegexOptions.Compiled);

    /// <summary>จำนวนเงิน — ต้องมีทศนิยม 2 ตำแหน่งหรือมีคอมมาคั่นหลักพัน
    /// (กันการหยิบเลข "10" ของ "10%" มาเป็นยอด)</summary>
    private static readonly Regex MoneyToken = new(
        @"(?<![\d.%])(-|−)?[ \t]*(\d{1,3}(?:,\d{3})+(?:\.\d{2})?|\d+\.\d{2})(?![\d%])",
        RegexOptions.Compiled);

    public static BillSurcharge Read(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new BillSurcharge(null, null, null);

        decimal? scAmount = null, scPercent = null, rounding = null;

        foreach (var raw in rawText.Split('\n'))
        {
            var line = raw.Replace('\r', ' ');
            if (line.Length == 0) continue;

            if (scAmount == null && ServiceLabel.IsMatch(line))
            {
                // ตัดข้อความก่อนป้ายทิ้ง — เลขที่อยู่ทางซ้ายของป้ายเป็นของช่องอื่น
                var after = line[ServiceLabel.Match(line).Index..];
                var pct = PercentToken.Match(after);
                var amt = LastMoney(after);
                if (amt is > 0m)
                {
                    scAmount = amt;
                    if (pct.Success && decimal.TryParse(pct.Groups[1].Value,
                            NumberStyles.Number, CultureInfo.InvariantCulture, out var p)
                        && p is > 0m and <= 30m)
                        scPercent = p;
                }
            }

            if (rounding == null && RoundingLabel.IsMatch(line))
            {
                var after = line[RoundingLabel.Match(line).Index..];
                var m = MoneyToken.Match(after);
                if (m.Success && decimal.TryParse(m.Groups[2].Value.Replace(",", ""),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var r)
                    && r > 0m && r < 1m)
                    rounding = m.Groups[1].Success ? -r : r;
            }
        }

        return new BillSurcharge(scAmount, scPercent, rounding);
    }

    /// <summary>ยอดเงินตัวสุดท้ายในข้อความ (บิลพิมพ์ยอดไว้ขวาสุดของบรรทัด)</summary>
    private static decimal? LastMoney(string text)
    {
        decimal? last = null;
        foreach (Match m in MoneyToken.Matches(text))
        {
            if (decimal.TryParse(m.Groups[2].Value.Replace(",", ""),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0m)
                last = m.Groups[1].Success ? -v : v;
        }
        return last;
    }
}
