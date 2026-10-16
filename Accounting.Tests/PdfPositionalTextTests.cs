using System.Text;
using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using UglyToad.PdfPig;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 10 (2026-10-16) — ประกอบข้อความหน้า PDF ตามพิกัด (<see cref="PdfPositionalText"/>) แทน PdfPig <c>page.Text</c> (ลำดับ content stream)
///
/// <para>ที่มา: บิลค่าไฟจริง กฟภ. XH0712609004157 — "1,356.7120,738.21" (VAT กับยอดรวมติดกัน ⇒ ยอด 689.92/48.29/738.21) · "เลขที่ (No.)516004632612"
/// (ป้ายกับค่าสลับคู่) · "1090000000" (สาขา 00000 ต่อรหัสไปรษณีย์) · ใบ RE-20261000111 (พรามี่) "1.00ชิ้น1,330.00293.007%1,037.00" + วรรณยุกต์ PUA ·
/// เกณฑ์ทุกตัววัดจากพิกัดจริงของสองใบนั้น (ดูคอมเมนต์ค่าคงที่ใน helper) · ข้อความที่ได้จากสองใบจริงล็อกไว้ใน
/// <see cref="OcrPaperSamples.PeaElectricityBill0869Positional"/> / <see cref="OcrPaperSamples.PramieRe111Positional"/> + golden ของ replay</para>
/// </summary>
public class PdfPositionalTextTests
{
    /// <summary>glyph ทีละอักขระ เริ่มที่ x · ความกว้าง 0.5 × ขนาด (วรรณยุกต์/สระบน-ล่างกว้าง 0 ที่ตำแหน่งท้ายตัวก่อนหน้า — อย่างที่ PdfPig ให้กับฟอนต์ไทย)</summary>
    private static IEnumerable<PdfGlyph> Run(string text, double x, double y, double size = 10)
    {
        foreach (var ch in text)
        {
            var s = ch.ToString();
            var mark = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark
                       || (ch >= '' && ch <= '');
            var w = mark ? 0 : 0.5 * size;
            yield return new PdfGlyph(s, x, x + w, y, size);
            x += w;
        }
    }

    [Fact]
    public void ยอดที่วาดก่อนป้าย_เรียงตามแถวบนกระดาษ_ป้ายกับค่าแยกช่อง_ไม่ติดกัน()
    {
        // ลำดับ stream ของ กฟภ.: ตัวเลขทั้งคอลัมน์ก่อน แล้วป้าย ⇒ page.Text = "19,381.501,356.7120,738.21รวม …"
        var glyphs = Run("19,381.50", 500, 270).Concat(Run("1,356.71", 505, 240)).Concat(Run("20,738.21", 500, 213))
            .Concat(Run("Sub Total", 300, 270)).Concat(Run("VAT", 300, 240)).Concat(Run("Total", 300, 213)).ToList();
        var text = PdfPositionalText.Build(glyphs);
        Assert.Equal("Sub Total  19,381.50\nVAT  1,356.71\nTotal  20,738.21", text);
    }

    [Fact]
    public void ช่องว่างระหว่างคำ_หนึ่งช่อง_ช่องตาราง_สองช่อง_ตัวติดกัน_ไม่มีช่อง()
    {
        // "1.00" | "ชิ้น" | "1,330.00" | "293.00" — ช่องตารางห่าง 2 × ขนาด · เว้นวรรคในคำอธิบายห่าง 0.25 × ขนาด
        var glyphs = Run("แผ่น", 100, 500).Concat(Run("M", 122.5, 500)).Concat(Run("1.00", 200, 500)).Concat(Run("ชิ้น", 240, 500))
            .Concat(Run("1,330.00", 280, 500)).Concat(Run("293.00", 340, 500)).ToList();
        Assert.Equal("แผ่น M  1.00  ชิ้น  1,330.00  293.00", PdfPositionalText.Build(glyphs));
    }

    [Fact]
    public void ทิศตรงข้าม_ข้อความที่พิมพ์ติดกัน_ไม่ถูกแตกเป็นช่อง_ตัวเลขช่องเดียวไม่ถูกตัด()
    {
        // ช่องว่างภายในคำ ≤ 0.06 × ขนาด (วัดจริง) ⇒ ไม่มีช่องว่างแทรก · ช่องว่างเว้นวรรค 0.9 × ขนาด (มากสุดที่วัดได้ในข้อความต่อเนื่อง) ⇒ หนึ่งช่อง ไม่ใช่ช่องตาราง
        var glyphs = Run("20,738.21", 100, 300).Concat(Run("บาท", 100 + 45 + 9, 300)).ToList();
        Assert.Equal("20,738.21 บาท", PdfPositionalText.Build(glyphs));
    }

    [Fact]
    public void เส้นฐานห่างเกินเกณฑ์_เป็นคนละแถว_ใกล้กว่าเกณฑ์_แถวเดียวกัน()
    {
        // กฟภ.: "เลขที่ (No.)" ห่างจากที่อยู่ผู้ขาย 0.45 × ขนาด = คนละแถวบนกระดาษ ⇒ ต้องไม่รวม
        var apart = Run("No. XH0712609004157", 400, 723.56, 14).Concat(Run("200 Ngamwongwan", 20, 717.23, 14)).ToList();
        Assert.Equal("No. XH0712609004157\n200 Ngamwongwan", PdfPositionalText.Build(apart));
        // ตัวเลขยอดตัวใหญ่ที่เส้นฐานต่ำกว่าป้ายเล็กน้อย (0.2 × ขนาดตัวเล็ก) = แถวเดียวกัน
        var same = Run("Total", 100, 300, 9).Concat(Run("1,037.00", 300, 298.2, 14)).ToList();
        Assert.Equal("Total  1,037.00", PdfPositionalText.Build(same));
    }

    [Fact]
    public void ตัวหนาแบบวาดทับสองรอบ_เก็บตัวเดียว()
    {
        var glyphs = Run("4,272.00", 400, 503).Concat(Run("4,272.00", 400.3, 503)).ToList();
        Assert.Equal("4,272.00", PdfPositionalText.Build(glyphs));
        // ทิศตรงข้าม: อักขระเดียวกันที่อยู่ห่างกันจริง (ตัวเลขซ้ำในยอด) ไม่ถูกตัด
        Assert.Equal("1111", PdfPositionalText.Build(Run("1111", 10, 10).ToList()));
    }

    [Fact]
    public void วรรณยุกต์PUA_แปลงเป็นอักขระไทย_ไอคอนฟอนต์ทิ้ง_นิคหิตสระอารวมเป็นสระอำ()
    {
        // พรามี่: "แผน" (ไม้เอก PUA) · "จํากัด" (นิคหิต + สระอา) · ไอคอนโทรศัพท์  หน้าเบอร์
        var glyphs = Run("แผน", 10, 100).Concat(Run("จํากัด", 60, 100)).Concat(Run("", 10, 80)).Concat(Run("085-914-8140", 16, 80)).ToList();
        Assert.Equal("แผ่น  จำกัด\n085-914-8140", PdfPositionalText.Build(glyphs));
        Assert.Equal("ส่วนลด", PdfPositionalText.NormalizeGlyphText("สวนลด"));
        Assert.Equal("", PdfPositionalText.NormalizeGlyphText(""));
        Assert.Equal("✉", PdfPositionalText.NormalizeGlyphText("✉"));   // อักขระ Unicode จริง (ไม่ใช่ PUA) คงไว้
    }

    [Fact]
    public void วรรณยุกต์กว้างศูนย์_ติดตัวก่อนหน้าตามลำดับstream_ไม่ถูกเรียงไปหลังตัวถัดไป()
    {
        // PdfPig ให้ x ของไม้เอกเท่ากับจุดเริ่มของตัวถัดไป ⇒ ถ้าเรียงตาม x ล้วน ๆ "ส่วน" อาจกลายเป็น "สว่น"
        var glyphs = new List<PdfGlyph>
        {
            new("ส", 10, 15, 50, 10), new("่", 15, 15, 50, 10), new("ว", 15, 20, 50, 10), new("น", 20, 25, 50, 10),
        };
        Assert.Equal("ส่วน", PdfPositionalText.Build(glyphs));
    }

    [Fact]
    public void ไม่มีglyph_หรือหน้าส่วนใหญ่หมุน_คืนnull_ให้ผู้เรียกใช้ข้อความเดิม()
    {
        Assert.Null(PdfPositionalText.Build(null));
        Assert.Null(PdfPositionalText.Build(new List<PdfGlyph>()));
        Assert.Null(PdfPositionalText.Build(new List<PdfGlyph> { new(" ", 1, 2, 3, 0) }));   // ขนาด 0 = ใช้ไม่ได้
        var rotated = Run("ROTATED", 10, 10).Select(g => g with { Horizontal = false }).Concat(Run("ok", 10, 50)).ToList();
        Assert.Null(PdfPositionalText.Build(rotated));
        // หมุนส่วนน้อย (ตราประทับข้าง) ⇒ ต่อท้ายตามลำดับ stream ไม่ทิ้ง
        var fewRotated = Run("Total 100.00", 10, 50).Concat(Run("PAID", 300, 10).Select(g => g with { Horizontal = false })).ToList();
        Assert.Equal("Total 100.00\nPAID", PdfPositionalText.Build(fewRotated));
    }

    // ── ต่อสายจริง: PdfTextLayerExtractor → PdfPig letters → helper (PDF ที่สร้างในเทสต์ · ลำดับ stream แบบ กฟภ.) ──────────────

    [Fact]
    public void PdfTextLayerExtractor_PDFที่วาดตัวเลขก่อนป้าย_ได้ข้อความตามแถวบนกระดาษ_pageTextเดิมติดกัน()
    {
        var pdf = BuildPdf(
            // ลำดับ stream: คอลัมน์ตัวเลขทั้งหมดก่อน (แบบ กฟภ.) แล้วคอลัมน์ป้าย
            (430, 270, "19,381.50"), (436, 240, "1,356.71"), (430, 213, "20,738.21"),
            (60, 300, "Tax Invoice No. XH0712609004157 Invoice No. 516004632612"),
            (60, 270, "Sub Total"), (60, 240, "VAT 7%"), (60, 213, "Total"));
        using (var doc = PdfDocument.Open(pdf))
            Assert.Contains("1,356.7120,738.21", doc.GetPage(1).Text);   // ทำซ้ำอาการเดิม (page.Text = ลำดับ stream)
        var r = PdfTextLayerExtractor.TryExtract(pdf);
        Assert.True(r.HasUsableText, r.Reason);
        Assert.Equal("Tax Invoice No. XH0712609004157 Invoice No. 516004632612\nSub Total  19,381.50\nVAT 7%  1,356.71\nTotal  20,738.21\n",
            r.Text.Replace("\r\n", "\n"));
    }

    /// <summary>PDF หนึ่งหน้า ฟอนต์ Helvetica (มาตรฐาน 14 ตัว — PdfPig มีเมตริกในตัว) · แต่ละข้อความวาดที่ (x, y) ตามลำดับที่ส่ง</summary>
    private static byte[] BuildPdf(params (double X, double Y, string Text)[] runs)
    {
        var content = new StringBuilder();
        foreach (var (x, y, t) in runs)
            content.Append(System.FormattableString.Invariant($"BT /F1 12 Tf {x} {y} Td ({t.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET\n"));
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content.ToString())} >>\nstream\n{content}endstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
