using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 9) — <see cref="OcrBuyerOnPaper"/>: ใบหัว "ใบกำกับภาษี" ที่กระดาษไม่มีชื่อ/เลขผู้ซื้อ ⇒ ภาษีซื้อต้องห้าม §82/5(1)
///
/// <para><b>ครึ่งที่ 1</b> (ใบที่เคยผิด): ใบค้าส่งไม่มีบล็อกผู้ซื้อเลย · ใบที่มีป้าย "ลูกค้า" แต่ไม่มีเลข · ใบที่ระบบเติมชื่อเราเอง
/// (<c>FillOurName</c>) แต่กระดาษไม่มี ⇒ ต้องบล็อก พร้อม RuleCode + LegalReference + ข้อความบอกทางไปต่อ</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามแตะ): ใบจริงของเจ้าของที่มีผู้ซื้อครบ (Shopee · Makro · Lazada · Wine Pro) · เลขเราที่ OCR อ่านเพี้ยนหลักเดียว ·
/// e-Tax XML ที่มี BuyerTradeParty · ตัดสินไม่ได้ (ไม่มีข้อความ/ไม่รู้ชื่อเรา) · ไม่ใช่ใบเต็มรูป/ไม่มี VAT ⇒ ไม่บล็อก</para>
/// </summary>
public class OcrBuyerOnPaperTests
{
    private const string OurName = "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส";
    private const string OurTaxId = "0203562005871";

    private static OcrBuyerClaimVerdict Judge(string text, string vendorTaxId, string? buyerTaxId = null, string? buyerName = null,
        string? ourName = OurName, string? ourTaxId = OurTaxId)
        => OcrBuyerOnPaper.JudgeClaim(true, 28.00m,
            OcrBuyerOnPaper.Read(text, vendorTaxId, buyerTaxId, buyerName, ourTaxId, ourName));

    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void ใบค้าส่งไม่มีบล็อกผู้ซื้อ_บล็อกเคลม_มีรหัสกฎและมาตรา()
    {
        var ev = OcrBuyerOnPaper.Read(OcrPaperSamples.WholesaleMixedVat, "0105536092641", null, null, OurTaxId, OurName);
        Assert.Equal(OcrPaperPresence.Missing, ev.Name);
        Assert.Equal(OcrPaperPresence.Missing, ev.TaxId);

        var v = OcrBuyerOnPaper.JudgeClaim(true, 28.00m, ev);
        Assert.True(v.BlockClaim);
        Assert.Equal(OcrBuyerOnPaper.RuleCode, v.RuleCode);
        Assert.Contains("82/5(1)", v.LegalReference);
        Assert.Contains("ชื่อผู้ซื้อและเลขประจำตัวผู้เสียภาษีของผู้ซื้อ", v.Message);
        Assert.Contains("เปิดการเคลมคืน", v.Message);   // ทางไปต่อเมื่อกระดาษมีจริง
    }

    [Fact]
    public void ชื่อเราที่ระบบเติมเอง_ไม่ใช่หลักฐานว่ากระดาษมี()
    {
        // FillOurName ใส่ "หจก.แอม แฮปปี้เนส" ในช่องผู้ซื้อ + เลขเรา ⇒ ค่าที่ถืออยู่ไม่ว่าง แต่กระดาษไม่มี
        var ev = OcrBuyerOnPaper.Read(OcrPaperSamples.WholesaleMixedVat, "0105536092641",
            buyerTaxId: OurTaxId, buyerName: "หจก.แอม แฮปปี้เนส", ourTaxId: OurTaxId, ourName: OurName);
        Assert.Equal(OcrPaperPresence.Missing, ev.Name);
        Assert.Equal(OcrPaperPresence.Missing, ev.TaxId);
        Assert.True(OcrBuyerOnPaper.JudgeClaim(true, 28.00m, ev).BlockClaim);
    }

    [Fact]
    public void มีป้ายลูกค้าพร้อมชื่อแต่ไม่มีเลข_บล็อกเพราะขาดเลข()
    {
        var text = OcrPaperSamples.WholesaleMixedVat.Replace(
            "ใบเสร็จรับเงิน/ใบกำกับภาษี\n", "ใบเสร็จรับเงิน/ใบกำกับภาษี\nลูกค้า: ร้านสมชายการค้า\n");
        var ev = OcrBuyerOnPaper.Read(text, "0105536092641", null, null, OurTaxId, OurName);
        Assert.Equal(OcrPaperPresence.Printed, ev.Name);
        Assert.Equal(OcrPaperPresence.Missing, ev.TaxId);

        var v = OcrBuyerOnPaper.JudgeClaim(true, 28.00m, ev);
        Assert.True(v.BlockClaim);
        Assert.Contains("ไม่มีเลขประจำตัวผู้เสียภาษีของผู้ซื้อ", v.Message);
    }

    [Fact]
    public void XMLที่ไม่มีเลขผู้ซื้อ_บล็อก()
    {
        var v = OcrBuyerOnPaper.JudgeClaim(true, 35.07m, OcrBuyerOnPaper.FromStructured("หจก. แอม แฮปปี้เนส", null));
        Assert.True(v.BlockClaim);
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(OcrPaperSamples.UptoyouShopee), "0123565003005")]
    [InlineData(nameof(OcrPaperSamples.MakroPage3of3), "0107567000414")]
    [InlineData(nameof(OcrPaperSamples.ScommerceLazada), "0105560113122")]
    [InlineData(nameof(OcrPaperSamples.WinePro), "0105555175590")]
    public void ใบจริงที่มีผู้ซื้อครบ_ไม่บล็อก(string sample, string vendorTaxId)
    {
        var text = (string)typeof(OcrPaperSamples).GetField(sample)!.GetValue(null)!;
        var ev = OcrBuyerOnPaper.Read(text, vendorTaxId, OurTaxId, OurName, OurTaxId, OurName);
        Assert.Equal(OcrPaperPresence.Printed, ev.Name);
        Assert.Equal(OcrPaperPresence.Printed, ev.TaxId);
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 35.07m, ev).BlockClaim);
    }

    [Fact]
    public void ใบจริงที่มีผู้ซื้อครบ_แม้ไม่รู้ชื่อเรา_ยังไม่บล็อก()
    {
        // ชื่อจากป้าย "ลูกค้า" + เลขที่ผ่าน checksum ที่ไม่ใช่ของผู้ขาย
        var v = Judge(OcrPaperSamples.UptoyouShopee, "0123565003005", ourName: null, ourTaxId: null);
        Assert.False(v.BlockClaim);
    }

    [Fact]
    public void เลขเราที่OCRอ่านเพี้ยนหนึ่งหลัก_นับว่ากระดาษพิมพ์ไว้()
    {
        var text = OcrPaperSamples.UptoyouShopee.Replace("0203562005871", "0203562005872");
        var ev = OcrBuyerOnPaper.Read(text, "0123565003005", null, null, OurTaxId, OurName);
        Assert.Equal(OcrPaperPresence.Printed, ev.TaxId);
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 35.07m, ev).BlockClaim);
    }

    [Fact]
    public void XMLที่มีBuyerTradeParty_ไม่บล็อก()
    {
        var ev = OcrBuyerOnPaper.FromStructured("หจก. แอม แฮปปี้เนส", OurTaxId);
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 35.07m, ev).BlockClaim);
    }

    [Fact]
    public void ตัดสินไม่ได้_ไม่แตะพฤติกรรมเดิม()
    {
        var empty = OcrBuyerOnPaper.Read(null, "0105536092641", null, null, OurTaxId, OurName);
        Assert.Equal(OcrPaperPresence.Unknown, empty.Name);
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 28.00m, empty).BlockClaim);

        // ไม่รู้ชื่อเรา ไม่มีชื่อผู้ซื้อ ไม่มีป้าย ⇒ ชื่อ Unknown — แต่เลขยังตัดสินได้ (ไม่มีเลขผู้ซื้อ) จึงบล็อกด้วยเหตุ "ขาดเลข" เท่านั้น
        var noName = OcrBuyerOnPaper.Read(OcrPaperSamples.WholesaleMixedVat, "0105536092641", null, null, null, null);
        Assert.Equal(OcrPaperPresence.Unknown, noName.Name);
        var v = OcrBuyerOnPaper.JudgeClaim(true, 28.00m, noName);
        Assert.True(v.BlockClaim);
        Assert.Contains("ไม่มีเลขประจำตัวผู้เสียภาษีของผู้ซื้อ", v.Message);
    }

    [Fact]
    public void ไม่ใช่ใบเต็มรูป_หรือไม่มีVAT_ไม่บล็อก()
    {
        var ev = OcrBuyerOnPaper.Read(OcrPaperSamples.WholesaleMixedVat, "0105536092641", null, null, OurTaxId, OurName);
        Assert.False(OcrBuyerOnPaper.JudgeClaim(false, 28.00m, ev).BlockClaim);   // ใบอย่างย่อ — ตัวอนุมานบทบาทตัดสินแล้ว (§82/5(2))
        Assert.False(OcrBuyerOnPaper.JudgeClaim(true, 0m, ev).BlockClaim);        // ไม่มีภาษีซื้อให้เคลม
    }
}
