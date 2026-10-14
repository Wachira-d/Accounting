using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// จำนวนเงินเป็นตัวอักษร (เลข → ตัวอักษร) — ตัวตั้งเดียว <see cref="ThaiBahtText"/>
///
/// ═══ ที่มา (2026-10-14 · ทีม PDF) ═══
/// สูตรเดิมทั้ง 4 ชุด (เอกสาร · 50 ทวิ · หน้าพิมพ์ wht.html · พรีวิว JS) ฝั่งพิมพ์ใช้ <c>units[pos % 7]</c> ไม่วนกลุ่ม "ล้าน"
/// ⇒ 10,000,000 พิมพ์ว่า "หนึ่งบาทถ้วน" · 12,345,678 พิมพ์ว่า "หนึ่งสองล้าน…" บนใบกำกับ/50 ทวิจริง
///
/// ⚠️ ตาราง <c>[InlineData("ยอด", "ตัวอักษร")]</c> ของ <see cref="ตารางยอดต่อตัวอักษร"/> ถูกอ่านโดย
/// <c>tools/thai_baht_text_sim.js</c> เพื่อเทียบสำเนา JS พรีวิว (<c>ux-helpers.js</c>) กับเคสชุดเดียวกัน —
/// คงรูปแบบ <c>InlineData("ตัวเลข", "ข้อความ")</c> ไว้ (เพิ่มเคสได้ · ห้ามเปลี่ยนรูป)
/// </summary>
public class ThaiBahtTextTests
{
    [Theory]
    // ── ศูนย์ / สตางค์ล้วน ──
    [InlineData("0", "ศูนย์บาทถ้วน")]
    [InlineData("0.5", "ศูนย์บาทห้าสิบสตางค์")]         // เดิมสูตรเอกสารพิมพ์ "ศูนย์บาทถ้วน"
    [InlineData("0.01", "ศูนย์บาทหนึ่งสตางค์")]
    [InlineData("0.21", "ศูนย์บาทยี่สิบเอ็ดสตางค์")]
    // ── หลักสิบ / เอ็ด / ยี่ ──
    [InlineData("1", "หนึ่งบาทถ้วน")]
    [InlineData("10", "สิบบาทถ้วน")]
    [InlineData("11", "สิบเอ็ดบาทถ้วน")]
    [InlineData("20", "ยี่สิบบาทถ้วน")]
    [InlineData("21", "ยี่สิบเอ็ดบาทถ้วน")]
    [InlineData("72.64", "เจ็ดสิบสองบาทหกสิบสี่สตางค์")]
    [InlineData("101", "หนึ่งร้อยเอ็ดบาทถ้วน")]          // เดิมพรีวิว JS = "หนึ่งร้อยหนึ่ง"
    [InlineData("1001", "หนึ่งพันเอ็ดบาทถ้วน")]
    [InlineData("1.01", "หนึ่งบาทหนึ่งสตางค์")]          // สตางค์นับแยก — ไม่ใช่ "เอ็ดสตางค์"
    [InlineData("6420", "หกพันสี่ร้อยยี่สิบบาทถ้วน")]
    [InlineData("1053.95", "หนึ่งพันห้าสิบสามบาทเก้าสิบห้าสตางค์")]
    // ── ใต้ 10 ล้าน (ต้องเหมือนสูตรเดิมทุกตัวอักษร) ──
    [InlineData("999999.99", "เก้าแสนเก้าหมื่นเก้าพันเก้าร้อยเก้าสิบเก้าบาทเก้าสิบเก้าสตางค์")]
    [InlineData("1000000", "หนึ่งล้านบาทถ้วน")]
    [InlineData("1000001", "หนึ่งล้านเอ็ดบาทถ้วน")]
    [InlineData("2530000", "สองล้านห้าแสนสามหมื่นบาทถ้วน")]
    [InlineData("9999999.99", "เก้าล้านเก้าแสนเก้าหมื่นเก้าพันเก้าร้อยเก้าสิบเก้าบาทเก้าสิบเก้าสตางค์")]
    // ── ≥ 10 ล้าน (บั๊กเดิม: units[pos % 7]) ──
    [InlineData("10000000", "สิบล้านบาทถ้วน")]
    [InlineData("10000001", "สิบล้านเอ็ดบาทถ้วน")]
    [InlineData("11000000", "สิบเอ็ดล้านบาทถ้วน")]
    [InlineData("12345678", "สิบสองล้านสามแสนสี่หมื่นห้าพันหกร้อยเจ็ดสิบแปดบาทถ้วน")]
    [InlineData("12345678.90", "สิบสองล้านสามแสนสี่หมื่นห้าพันหกร้อยเจ็ดสิบแปดบาทเก้าสิบสตางค์")]
    [InlineData("21000021.21", "ยี่สิบเอ็ดล้านยี่สิบเอ็ดบาทยี่สิบเอ็ดสตางค์")]
    [InlineData("100000000", "หนึ่งร้อยล้านบาทถ้วน")]
    [InlineData("101000000", "หนึ่งร้อยเอ็ดล้านบาทถ้วน")]
    [InlineData("1000000000", "หนึ่งพันล้านบาทถ้วน")]
    [InlineData("999999999999.99", "เก้าแสนเก้าหมื่นเก้าพันเก้าร้อยเก้าสิบเก้าล้านเก้าแสนเก้าหมื่นเก้าพันเก้าร้อยเก้าสิบเก้าบาทเก้าสิบเก้าสตางค์")]
    [InlineData("1000000000000", "หนึ่งล้านล้านบาทถ้วน")]
    [InlineData("2000000000001", "สองล้านล้านเอ็ดบาทถ้วน")]
    [InlineData("1000001000000", "หนึ่งล้านเอ็ดล้านบาทถ้วน")]          // กลุ่มล้าน 1,000,001 = หนึ่งล้านเอ็ด (กติกาเดียวกับกลุ่มล่าง)
    // ── ปัดสตางค์ AwayFromZero (ปัดก่อนแยกบาท/สตางค์) ──
    [InlineData("0.005", "ศูนย์บาทหนึ่งสตางค์")]
    [InlineData("1.005", "หนึ่งบาทหนึ่งสตางค์")]
    [InlineData("1.995", "สองบาทถ้วน")]                  // เดิม 50 ทวิ = "หนึ่งบาทหนึ่งร้อยสตางค์"
    [InlineData("0.004", "ศูนย์บาทถ้วน")]
    // ── ยอดติดลบ = อ่านขนาด (พฤติกรรมเดิมของ 50 ทวิ · เดิมสูตรเอกสารโยน exception) ──
    [InlineData("-5", "ห้าบาทถ้วน")]
    [InlineData("-0.5", "ศูนย์บาทห้าสิบสตางค์")]
    [InlineData("-12345678.90", "สิบสองล้านสามแสนสี่หมื่นห้าพันหกร้อยเจ็ดสิบแปดบาทเก้าสิบสตางค์")]
    public void ตารางยอดต่อตัวอักษร(string amount, string expected)
        => Assert.Equal(expected, ThaiBahtText.Format(decimal.Parse(amount, CultureInfo.InvariantCulture)));

    /// <summary>
    /// ทิศตรงข้าม: ยอดที่ไม่มีหลักล้านซ้อน ต้องได้ผลเหมือนสูตรเดิม (<c>PdfGenerationService.ThaiNumberToText</c> ที่ b3f9855f)
    /// ทุกตัวอักษร — ไล่ทุกยอด 0..1,200 บาท + ตัวอย่างกระจายถึง 9,999,999.99 · สำเนาสูตรเดิมด้านล่างคัดจาก git ไม่ใช่เขียนจากความจำ
    /// </summary>
    [Fact]
    public void ใต้สิบล้าน_ผลเหมือนสูตรเดิมทุกตัวอักษร()
    {
        var samples = new List<decimal>();
        for (var i = 0; i <= 1200; i++) samples.Add(i);
        for (var i = 0; i < 100; i++) samples.Add(i / 100m);
        for (long v = 1; v < 10_000_000; v = v * 7 + 3) samples.AddRange(new[] { v, v + 0.01m, v + 0.21m, v + 0.99m });
        samples.AddRange(new[] { 1_000_001m, 1_000_011m, 1_000_101m, 2_100_021m, 9_999_999.99m, 5_000_000m });
        foreach (var v in samples)
            Assert.Equal(LegacyWhtThaiNumberToText(v), ThaiBahtText.Format(v));
    }

    [Fact]
    public void พิมพ์แล้วอ่านกลับด้วยตัวอ่านOCRได้ยอดเดิม_รวมยอดหลายล้านล้าน()
    {
        decimal[] samples =
        {
            0.01m, 0.5m, 1m, 11m, 21m, 101m, 1_000_001m, 9_999_999.99m, 10_000_000m, 12_345_678.90m,
            21_000_021.21m, 100_000_000m, 1_000_000_000_000m, 2_000_000_000_001m, 999_999_999_999.99m,
        };
        foreach (var v in samples)
        {
            var text = ThaiBahtText.Format(v);
            Assert.Equal(v, ThaiAmountInWords.Parse(text));
        }
    }

    /// <summary>
    /// ทุกผู้พิมพ์เรียกตัวตั้งเดียว — renderer 4 จุด (เอกสาร HTML/QuestPDF · 50 ทวิ HTML/QuestPDF) + DTO ของหน้าพิมพ์ wht.html
    /// (จุดเรียกใน renderer ล็อกซ้ำใน <c>tools/required_call_site_check.py</c> · ส่วนนี้ล็อก DTO ที่ checker อ่านไม่ได้เพราะ
    /// <c>MapToResponse</c> เป็น expression-bodied) · สูตรเดิมห้ามกลับมา
    /// </summary>
    [Fact]
    public void ทุกผู้พิมพ์เรียกตัวตั้งเดียว_และหน้าพิมพ์50ทวิแสดงข้อความจากเซิร์ฟเวอร์()
    {
        var root = RepoRoot();
        string Code(string rel) => Regex.Replace(Regex.Replace(File.ReadAllText(Path.Combine(root, rel)),
            @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

        var pdf = Code("Accounting/Services/Implementations/PdfGenerationService.cs");
        Assert.Contains("ThaiBahtText.Format(printTotals.Grand)", pdf);
        Assert.Contains("ThaiBahtText.Format(cert.TotalTaxAmount)", pdf);
        Assert.Contains("ThaiBahtText.Format(totalTax)", Code("Accounting/Services/Implementations/PdfGenerationService.WhtCert.cs"));
        Assert.Contains("ThaiBahtText.Format(wordsAmount)", Code("Accounting/Services/Implementations/PdfGenerationService.DocumentRenderer.cs"));
        foreach (var old in new[] { "ThaiNumberToText(", "ConvertToThaiWords(", "ConvertThaiNumber(" })
            Assert.DoesNotContain(old, pdf);

        Assert.Contains("TotalTaxAmountInWords: Accounting.Helpers.ThaiBahtText.Format(w.TotalTaxAmount)",
            Code("Accounting/Services/Implementations/WithholdingTaxCertService.cs"));
        Assert.NotNull(typeof(WithholdingTaxCertResponse).GetProperty("TotalTaxAmountInWords"));

        var page = File.ReadAllText(Path.Combine(root, "Accounting/wwwroot/pages/wht.html"));
        Assert.Contains("Layout.esc(d.totalTaxAmountInWords || '')", page);
        Assert.DoesNotContain("thaiBahtText", page);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }

    /// <summary>สูตรเดิม <c>PdfGenerationService.ThaiNumberToText</c> ที่ commit b3f9855f (คัดจาก <c>git show</c>) —
    /// ใช้เป็น baseline ของเทสต์ทิศตรงข้ามเท่านั้น (ห้ามเรียกจากโค้ดจริง)</summary>
    private static string LegacyWhtThaiNumberToText(decimal amount)
    {
        if (amount == 0) return "ศูนย์บาทถ้วน";
        string[] units = { "", "สิบ", "ร้อย", "พัน", "หมื่น", "แสน", "ล้าน" };
        string[] digits = { "", "หนึ่ง", "สอง", "สาม", "สี่", "ห้า", "หก", "เจ็ด", "แปด", "เก้า" };

        static string ConvertGroup(long val, string[] digits, string[] units)
        {
            if (val == 0) return "ศูนย์";
            var s = val.ToString();
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[i] - '0';
                int pos = s.Length - i - 1;
                if (d == 0) continue;
                if (pos == 1 && d == 1) { result.Append("สิบ"); continue; }
                if (pos == 1 && d == 2) { result.Append("ยี่สิบ"); continue; }
                if (pos == 0 && d == 1 && s.Length > 1) { result.Append("เอ็ด"); continue; }
                result.Append(digits[d]);
                result.Append(units[pos % 7]);
            }
            return result.ToString();
        }

        long baht = (long)Math.Floor(Math.Abs(amount));
        int satang = (int)Math.Round((Math.Abs(amount) - baht) * 100, MidpointRounding.AwayFromZero);
        var text = ConvertGroup(baht, digits, units) + "บาท";
        text += satang > 0 ? ConvertGroup(satang, digits, units) + "สตางค์" : "ถ้วน";
        return text;
    }
}
