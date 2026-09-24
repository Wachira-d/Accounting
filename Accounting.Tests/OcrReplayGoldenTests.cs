using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// D-7 — golden ของ replay harness: **คำตอบของกระดาษชุดเดิมต้องไม่เปลี่ยนเงียบ ๆ**
///
/// <para>ทุกแถวคือคำตอบที่ระบบให้<b>วันนี้</b> · ถ้าแถวไหนล้ม แปลว่าการแก้ครั้งนี้
/// เปลี่ยนคำตอบของใบนั้น — ซึ่ง<b>อาจถูกหรือผิดก็ได้</b> กติกาคือ
/// <b>ต้องอธิบายได้ทุกใบ</b> ก่อนจะแก้ตัวเลขในเทสต์ (กฎเหล็ก #4 H)</para>
///
/// <para>เทสต์มีสองครึ่ง: ครึ่ง "ใบที่เคยพังต้องถูก" (ลักกี้เวย์สลับป้าย · แถวฟอร์ม
/// มัดจำยอด 0 · ชื่อผู้ซื้อที่ถูกตัด) และครึ่ง "ใบที่ถูกอยู่แล้วห้ามถูกแตะ"
/// (Makro · ใบส่งออก 0% · ใบบริการที่ไม่พิมพ์ส่วนหัก)</para>
/// </summary>
public class OcrReplayGoldenTests
{
    private static string? Val(string paper, string field)
        => OcrReplayHarness.Run().Single(a => a.Paper == paper && a.Field == field).Value;

    // ── ครึ่งที่ 1: ใบที่เคยพัง ต้องได้คำตอบที่ถูก ───────────────────────────

    [Fact]
    public void ลักกี้เวย์_ป้ายสลับต้องถูกสลับกลับ()
    {
        Assert.Equal("true", Val("luckyway-swapped", "HeaderSwapped"));
        Assert.Equal("1000.00", Val("luckyway-swapped", "HeaderSubTotal"));
        Assert.Equal("1070.00", Val("luckyway-swapped", "HeaderTotal"));
    }

    [Fact]
    public void แถวฟอร์ม_หักเงินมัดจำ_ยอดศูนย์_ต้องไม่ติดธงมัดจำ()
        => Assert.Equal("false", Val("deposit-form-row-zero", "IsDeposit"));

    [Fact]
    public void ใบมัดจำจริง_ต้องยังติดธง()
        => Assert.Equal("true", Val("deposit-real", "IsDeposit"));

    [Fact]
    public void ชื่อผู้ซื้อที่ถูกตัด_ต้องถูกขยายเป็นชื่อเต็มบนกระดาษ()
        => Assert.Equal("หจก. แอม แฮปปี้เนส", Val("buyer-name-truncated", "ExpandedName"));

    [Fact]
    public void ใบที่กระดาษพิมพ์ส่วนหัก_ต้องอ่านยอดและอัตราได้()
    {
        Assert.Equal("300.00", Val("service-wht-printed", "PaperWhtAmount"));
        Assert.Equal("3", Val("service-wht-printed", "PaperWhtRate"));
    }

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้ว ต้องไม่ถูกแตะ ───────────────────────────

    [Fact]
    public void Makro_สามยอดถูกอยู่แล้ว_ห้ามสลับ()
    {
        Assert.Equal("false", Val("makro-correct", "HeaderSwapped"));
        Assert.Equal("951.00", Val("makro-correct", "HeaderSubTotal"));
        Assert.Equal("1000.00", Val("makro-correct", "HeaderTotal"));
    }

    [Fact]
    public void ใบส่งออกศูนย์เปอร์เซ็นต์_VAT_เป็นศูนย์โดยชอบ_ห้ามถูกตีว่าป้ายสลับ()
    {
        Assert.Equal("false", Val("export-zero-rated", "HeaderSwapped"));
        Assert.Equal("100000.00", Val("export-zero-rated", "HeaderTotal"));
    }

    [Fact]
    public void ใบบริการที่ไม่พิมพ์ส่วนหัก_ต้องไม่มียอดหักจากกระดาษ_ห้ามแต่งขึ้น()
    {
        Assert.Null(Val("service-wht-absent", "PaperWhtAmount"));
        Assert.Null(Val("service-wht-absent", "PaperWhtRate"));
    }

    [Fact]
    public void ใบที่ไม่เกี่ยวมัดจำ_ต้องไม่ติดธง()
    {
        Assert.Equal("false", Val("makro-correct", "IsDeposit"));
        Assert.Equal("false", Val("service-wht-printed", "IsDeposit"));
        Assert.Equal("false", Val("export-zero-rated", "IsDeposit"));
    }

    // ── รอบ 190 ข้อ 9: ส่วนลดท้ายบิล · ใบผสม VAT ─────────────────────────────

    [Fact]
    public void ใบร้านวัสดุ_ส่วนลดท้ายบิล6975_ฐานภาษีหลังลด132525()
    {
        Assert.Equal("69.75", Val("hardware-bill-discount", "BillDiscount"));
        Assert.Equal("1325.25", Val("hardware-bill-discount", "NetSubTotal"));
    }

    [Fact]
    public void ใบซูเปอร์มาร์เก็ต_ส่วนลดสมาชิก3870_ฐานภาษี68720()
    {
        Assert.Equal("38.70", Val("supermarket-member-discount", "BillDiscount"));
        Assert.Equal("687.20", Val("supermarket-member-discount", "NetSubTotal"));
    }

    [Fact]
    public void ใบค้าส่งผสม_อัตรารายบรรทัดตามสัญลักษณ์บนกระดาษ()
        => Assert.Equal("-1,-1,-1,7,7,7", Val("wholesale-mixed-vat", "LineVatRates"));

    [Fact]
    public void ใบA_WinePro_ต้องไม่ถูกแตะ_ไม่มีส่วนลด_ฐานเดิม_ไม่ใช้สัญลักษณ์()
    {
        Assert.Null(Val("winepro-vat-included", "BillDiscount"));
        Assert.Equal("3357.94", Val("winepro-vat-included", "NetSubTotal"));
        Assert.Equal("(ไม่ใช้)", Val("winepro-vat-included", "LineVatRates"));
        Assert.Equal("false", Val("winepro-vat-included", "HeaderSwapped"));
    }

    [Fact]
    public void ใบเดิมในชุด_ไม่มีส่วนลด_ฐานภาษีเท่าเดิม()
    {
        Assert.Null(Val("makro-correct", "BillDiscount"));
        Assert.Equal("951.00", Val("makro-correct", "NetSubTotal"));
        Assert.Equal("1000.00", Val("luckyway-swapped", "NetSubTotal"));
        Assert.Equal("100000.00", Val("export-zero-rated", "NetSubTotal"));
    }

    // ── รอบ 192 Total-first: ใบจริง M/U/S ต้องถูก · ใบเดิมทุกใบต้องได้ยอด/ฐานเดิม ──────

    [Fact]
    public void ใบM_Makro_ยอดรวมทั้งสิ้น23812_25_ฐาน22663_97_ไม่ใช่ป้ายTOTAL24110()
    {
        Assert.Equal("24110.00", Val("makro-page3-total-first", "HeaderTotal"));      // ค่าที่ engine หยิบ (บั๊ก)
        Assert.Equal("22961.72", Val("makro-page3-total-first", "NetSubTotal"));      // สูตรเดิมบนยอดผิด = 24,110 − VAT
        Assert.Equal("Proven", Val("makro-page3-total-first", "AnchorVerdict"));
        Assert.Equal("23812.25", Val("makro-page3-total-first", "AnchorTotal"));
        Assert.Equal("22663.97", Val("makro-page3-total-first", "AnchoredNetSubTotal"));
    }

    [Fact]
    public void ใบU_Shopee_ยอดใบกำกับ536_ส่วนลดพิเศษหลังVATไม่ถูกกระจาย()
    {
        Assert.Equal("Confirmed", Val("uptoyou-shopee-pay-not-total", "AnchorVerdict"));
        Assert.Equal("536.00", Val("uptoyou-shopee-pay-not-total", "AnchorTotal"));
        Assert.Equal("98.00", Val("uptoyou-shopee-pay-not-total", "BillDiscount"));   // ตัวอ่านเห็นบนกระดาษ…
        Assert.Equal("PostInvoice", Val("uptoyou-shopee-pay-not-total", "DiscountPlacement"));
        Assert.Equal("0.00", Val("uptoyou-shopee-pay-not-total", "DiscountToSpread")); // …แต่ไม่ใช่ส่วนลดในใบกำกับ
        Assert.Equal("500.93", Val("uptoyou-shopee-pay-not-total", "AnchoredNetSubTotal"));
    }

    [Fact]
    public void ใบS_Lazada_ส่วนลดก่อนVAT216_82_ฐาน4695_33()
    {
        Assert.Equal("Confirmed", Val("scommerce-lazada-prevat-discount", "AnchorVerdict"));
        Assert.Equal("216.82", Val("scommerce-lazada-prevat-discount", "BillDiscount"));
        Assert.Equal("PreVat", Val("scommerce-lazada-prevat-discount", "DiscountPlacement"));
        Assert.Equal("216.82", Val("scommerce-lazada-prevat-discount", "DiscountToSpread"));
        Assert.Equal("4695.33", Val("scommerce-lazada-prevat-discount", "AnchoredNetSubTotal"));
    }

    [Fact]
    public void ใบเดิมทุกใบในชุด_ยอดรวมและฐานหลังขั้นยึดยอด_เท่าเดิมทุกใบ()
    {
        var newPapers = new[] { "makro-page3-total-first", "uptoyou-shopee-pay-not-total", "scommerce-lazada-prevat-discount" };
        foreach (var p in OcrReplayHarness.Corpus.Where(x => !newPapers.Contains(x.Name)))
        {
            // ขั้นยึดยอดต้องไม่เขียนทับ/ขัดใบเดิมใบไหนเลย
            Assert.Contains(Val(p.Name, "AnchorVerdict"), new[] { "Confirmed", "Unknown" });
            Assert.Equal(Val(p.Name, "HeaderTotal"), Val(p.Name, "AnchorTotal"));
            Assert.Equal(Val(p.Name, "NetSubTotal"), Val(p.Name, "AnchoredNetSubTotal"));
            // ส่วนลดที่ส่งต่อให้ตัวสร้างบรรทัด = ส่วนลดที่อ่านได้เดิม
            Assert.Equal(Val(p.Name, "BillDiscount") ?? "0.00", Val(p.Name, "DiscountToSpread"));
        }
    }

    // ── ตัวเครื่องมือเอง: ต้อง deterministic และต้อง "จับได้" เมื่อคำตอบเปลี่ยน ──

    [Fact]
    public void รันสองครั้งต้องได้ผลเท่ากัน_ไม่งั้นตารางผลต่างเชื่อไม่ได้()
        => Assert.Empty(OcrReplayHarness.Diff(OcrReplayHarness.Run(), OcrReplayHarness.Run()));

    /// <summary>negative test ของตัวเครื่องมือ (F2 ข้อ 6): ใส่ "บั๊ก" กลับเข้าไป
    /// (กระดาษที่ป้ายไม่สลับ) แล้วตารางผลต่างต้องฟ้อง — เครื่องมือที่ฟ้องไม่ได้
    /// = เครื่องมือที่ไม่มีอยู่จริง</summary>
    [Fact]
    public void ตารางผลต่างต้องจับได้เมื่อคำตอบของใบหนึ่งเปลี่ยน()
    {
        var before = OcrReplayHarness.Run();
        var mutated = OcrReplayHarness.Corpus
            .Select(p => p.Name == "luckyway-swapped"
                ? p with { EngineSubTotal = 1000m, EngineTotal = 1070m }   // ป้ายถูกอยู่แล้ว
                : p)
            .ToList();
        var after = OcrReplayHarness.Run(mutated);

        var diff = OcrReplayHarness.Diff(before, after);
        Assert.NotEmpty(diff);
        Assert.All(diff, d => Assert.Equal("luckyway-swapped", d.Paper));
        Assert.Contains(diff, d => d.Field == "HeaderSwapped" && d.Before == "true" && d.After == "false");

        var table = OcrReplayHarness.DiffTable(before, after);
        Assert.Contains("luckyway-swapped", table);
        Assert.Contains("| ใบ | ช่อง | เดิม | ตอนนี้ |", table);
    }

    [Fact]
    public void ไม่มีอะไรเปลี่ยน_ตารางต้องบอกว่าศูนย์แถว_ไม่ใช่ตารางเปล่า()
        => Assert.Contains("0 แถว",
            OcrReplayHarness.DiffTable(OcrReplayHarness.Run(), OcrReplayHarness.Run()));

    [Fact]
    public void สแนปช็อตต้องอ่านออกและมีทุกใบ()
    {
        var snap = OcrReplayHarness.Snapshot(OcrReplayHarness.Run());
        foreach (var p in OcrReplayHarness.Corpus)
            Assert.Contains(p.Name, snap);
    }
}
